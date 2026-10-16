using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Patients;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Defect-hunt cycle 4, lot c4b, the reception and billing paths the earlier cycles did not replay
/// on SQL Server: a deposit absorbed by an invoice that is then reversed in full and issued again;
/// a cheque returned unpaid while its shift is open and after it closed; banking mid-shift and
/// closing on the count; and what a receptionist, a cashier and an administrator may each do.
/// </summary>
public sealed class Cycle4ReceptionBillingDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId, IReadOnlySet<string>? permissions = null) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "caisse";
        public string FullName => "Caissière";
        public Guid SiteId => siteId;
        // One code per test site: the receipt numbers carry it, and two tests' sites sharing one
        // drew the same deposit number.
        // (the identifiers are sequential, so their first characters are alike: the hash differs).
        public string SiteCode { get; } = "S" + ((uint)siteId.GetHashCode()).ToString("X8");
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = permissions ?? new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => permissions is null || permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "CAISSE-1";
    }

    private static readonly HashSet<string> Receptionist =
    [
        Permissions.Patients.View, Permissions.Patients.Create, Permissions.Patients.Edit,
        Permissions.Orders.View, Permissions.Orders.Create, Permissions.Orders.Edit,
        Permissions.Orders.Cancel, Permissions.Orders.AddUrgent,
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.RecordPayment,
        Permissions.Billing.CloseCashSession
    ];

    private static readonly HashSet<string> Cashier =
    [
        Permissions.Patients.View, Permissions.Orders.View,
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.RecordPayment,
        Permissions.Billing.CloseCashSession
    ];

    private static readonly HashSet<string> Accountant =
    [
        Permissions.Patients.View, Permissions.Orders.View,
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.CreditNote,
        Permissions.Billing.RecordPayment, Permissions.Billing.ApplyDiscount,
        Permissions.Billing.CloseCashSession, Permissions.Billing.ExportAccounting,
        Permissions.Billing.ReconcileCashSession
    ];

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private ServiceProvider Services(Guid siteId, IReadOnlySet<string>? permissions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId, permissions));
        services.AddIsolatedQueries<Application.Results.IResultQueries, MarocLIS.Infrastructure.Queries.ResultQueries>();
        services.AddIsolatedQueries<IBillingQueries, MarocLIS.Infrastructure.Queries.BillingQueries>();
        services.AddIsolatedQueries<IOrderQueries, MarocLIS.Infrastructure.Queries.OrderQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static string Keys(IEnumerable<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private sealed record Lab(Guid SiteId, Guid PayerId, Patient Insured, Patient SelfPaying,
                              AnalysisTest Nfs, AnalysisTest Creat, AnalysisTest Glu);

    private async Task<Lab> LabAsync()
    {
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var site = new LabSite(Unique("S"), new MultilingualText("Principal"), LabSiteKind.MainLaboratory)
        {
            LegalName = "Laboratoire Test SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            FiscalId = "40112233",
            AddressLine = new MultilingualText("12 rue Test"),
            City = "Casablanca"
        };

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sang"));
        var edta = new ContainerType(Unique("EDTA"), new MultilingualText("EDTA"), "#7B3FBF");

        AnalysisTest Analysis(string code, decimal price)
        {
            var analysis = new AnalysisTest(Unique(code), new MultilingualText(code), department.Id, specimen.Id)
            {
                ContainerTypeId = edta.Id,
                RoutineTurnaroundHours = 4
            };
            var parameter = new TestParameter(Unique(code), new MultilingualText(code), ResultValueType.Numeric);
            parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
            analysis.AddParameter(parameter);
            analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
            {
                LetterKey = "B", Coefficient = price, LetterKeyValue = 1m, Price = price, IsVatExempt = true
            });
            return analysis;
        }

        var nfs = Analysis("NFS", 40m);
        var creat = Analysis("CREAT", 10m);
        var glu = Analysis("GLU", 20m);

        var amo = new Payer(Unique("AMO"), new MultilingualText("AMO — CNOPS"), PayerKind.AmoCnops)
        {
            DefaultReimbursementRate = 0.80m,
            ThirdPartyPaymentAgreement = true,
            RequiresPrescription = false,
            RequiresInpeOnClaim = false
        };

        var insured = new Patient(Unique("P"), "IDRISSI", "Fatima", new DateOnly(1965, 4, 12), Gender.Female, site.Id);
        insured.AddCoverage(new PatientCoverage(insured.Id, amo.Id, "778899", validFrom));
        var selfPaying = new Patient(Unique("P"), "TAZI", "Youssef", new DateOnly(1988, 7, 3), Gender.Male, site.Id);

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(edta);
        context.AnalysisTests.AddRange(nfs, creat, glu);
        context.Payers.Add(amo);
        context.Patients.AddRange(insured, selfPaying);
        await context.SaveChangesAsync();

        return new Lab(site.Id, amo.Id, insured, selfPaying, nfs, creat, glu);
    }

    private static async Task<RegisteredOrder> RegisterAsync(ServiceProvider provider, Guid patientId, Guid[] analyses)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider, s => s.RegisterAsync(
            new RegisterOrderRequest
            {
                PatientId = patientId,
                AnalysisTestIds = analyses,
                TestPanelIds = [],
                PrescriberFreeText = "Dr BENNIS Karim"
            }));
        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));
        return registered.Value;
    }

    private static async Task<Guid> InvoiceAsync(ServiceProvider provider, Guid orderId)
    {
        var issued = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([orderId]));
        issued.IsSuccess.Should().BeTrue(Keys(issued.Errors));
        return issued.Value;
    }

    private static Task<Result> PayAsync(ServiceProvider provider, Guid invoiceId, decimal amount,
                                         PaymentMethod method = PaymentMethod.Cash, string? reference = null) =>
        On<BillingService, Result>(provider, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(invoiceId, amount, method, reference, null, null, null)));

    private async Task<Invoice> ReadInvoiceAsync(Guid invoiceId)
    {
        await using var context = database.Context();
        return await context.Invoices.Include(i => i.Payments).AsNoTracking().SingleAsync(i => i.Id == invoiceId);
    }


    private static Task<CashSessionSummary> TillAsync(ServiceProvider provider) =>
        On<BillingService, CashSessionSummary>(provider, async s => (await s.GetOpenCashSessionAsync()).Value);

    [SqlServerFact]
    public async Task ADepositAbsorbedByAnInvoiceReversedInFull_IsNotAppliedTwice_AndGoesBackFromATill()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result<string>>(reception, s => s.TakeDepositAsync(
            new TakeDepositRequest(order.OrderId, 30m, PaymentMethod.Cash, null, null, null)))).IsSuccess.Should().BeTrue();

        // Issued: the 30 held become a settlement on the invoice, 20 remain to pay.
        var first = await InvoiceAsync(reception, order.OrderId);
        var invoice = await ReadInvoiceAsync(first);
        invoice.PaidAmount.Should().Be(30m);
        invoice.OutstandingAmount.Should().Be(20m);
        (await On<BillingService, Result<IReadOnlyList<DepositRow>>>(reception, s => s.GetDepositsAsync(order.OrderId)))
            .Value.Should().ContainSingle().Which.Status.Should().Be(DepositStatus.Applied);

        // The receptionist cannot reverse it; the accounts office reverses it in full, which frees the request.
        (await On<BillingService, Result<Guid>>(reception, s => s.IssueCreditNoteAsync(first, "Facture adressée par erreur")))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await On<BillingService, Result<Guid>>(accounts, s => s.IssueCreditNoteAsync(first, "Facture adressée par erreur")))
            .IsSuccess.Should().BeTrue();
        (await On<BillingService, Result<IReadOnlyList<BillableOrderRow>>>(reception, s => s.GetUninvoicedOrdersAsync()))
            .Value.Should().ContainSingle(o => o.LabOrderId == order.OrderId)
            .Which.DepositHeld.Should().Be(0m);

        // Invoiced again: the deposit, already applied to the reversed invoice, is not applied a second time.
        var second = await InvoiceAsync(reception, order.OrderId);
        second.Should().NotBe(first);
        invoice = await ReadInvoiceAsync(second);
        invoice.TotalAmount.Should().Be(50m);
        invoice.PaidAmount.Should().Be(0m);
        invoice.OutstandingAmount.Should().Be(50m);

        // The 30 sit on the reversed invoice and go back to the patient from the refunder's till.
        var payment = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(accounts, s => s.GetPaymentsAsync(first)))
            .Value.Single();
        payment.IsCorrectable.Should().BeTrue();
        (await On<BillingService, Result<Guid>>(accounts, s => s.OpenCashSessionAsync(100m))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(first, payment.PaymentId, "Avoir total, patient remboursé")))
            .IsSuccess.Should().BeTrue();

        (await TillAsync(accounts)).ExpectedClosingCash.Should().Be(70m);
        (await TillAsync(reception)).ExpectedClosingCash.Should().Be(30m);
        (await ReadInvoiceAsync(first)).PaidAmount.Should().Be(0m);

        // The patient pays the new invoice in full at the counter.
        (await PayAsync(reception, second, 50m)).IsSuccess.Should().BeTrue();
        (await ReadInvoiceAsync(second)).Status.Should().Be(InvoiceStatus.Paid);
        (await TillAsync(reception)).ExpectedClosingCash.Should().Be(80m);
    }

    [SqlServerFact]
    public async Task AChequeReturnedUnpaid_ComesOffItsOpenShift_ButNotOffAClosedOne()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);

        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();
        var invoiceId = await InvoiceAsync(reception, order.OrderId);

        // A cheque needs its number.
        (await PayAsync(reception, invoiceId, 50m, PaymentMethod.Cheque)).IsFailure.Should().BeTrue();
        (await PayAsync(reception, invoiceId, 50m, PaymentMethod.Cheque, "CHQ-0001")).IsSuccess.Should().BeTrue();
        (await TillAsync(reception)).ChequeReceipts.Should().Be(50m);

        // Returned while the shift is open: off its cheque line, and owed again. A cashier may record it.
        var cheque = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(reception, s => s.GetPaymentsAsync(invoiceId))).Value.Single();
        cheque.CanBounce.Should().BeTrue();
        (await On<BillingService, Result>(reception, s => s.MarkPaymentBouncedAsync(invoiceId, cheque.PaymentId, "  ")))
            .FirstError.ResourceKey.Should().Be("Payment_BounceReasonRequired");
        (await On<BillingService, Result>(reception, s => s.MarkPaymentBouncedAsync(invoiceId, cheque.PaymentId, "Sans provision")))
            .IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(reception, s => s.MarkPaymentBouncedAsync(invoiceId, cheque.PaymentId, "Encore")))
            .IsFailure.Should().BeTrue();
        (await TillAsync(reception)).ChequeReceipts.Should().Be(0m);
        var invoice = await ReadInvoiceAsync(invoiceId);
        invoice.OutstandingAmount.Should().Be(50m);
        invoice.Status.Should().NotBe(InvoiceStatus.Paid);

        // A second cheque; the shift closes, then that cheque is returned too: the closed shift was
        // remitted as it stood and keeps its line, the invoice is owed again.
        (await PayAsync(reception, invoiceId, 50m, PaymentMethod.Cheque, "CHQ-0002")).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result<decimal>>(reception, s => s.CloseCashSessionAsync(0m, null))).Value.Should().Be(0m);

        var second = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(reception, s => s.GetPaymentsAsync(invoiceId)))
            .Value.Single(p => p.Status == PaymentStatus.Settled);
        (await On<BillingService, Result>(reception, s => s.MarkPaymentBouncedAsync(invoiceId, second.PaymentId, "Compte clôturé")))
            .IsSuccess.Should().BeTrue();

        await using var context = database.Context();
        var closed = await context.CashSessions.AsNoTracking().SingleAsync(s => s.SiteId == lab.SiteId);
        closed.Status.Should().Be(CashSessionStatus.Closed);
        closed.ChequeReceipts.Should().Be(50m);
        (await ReadInvoiceAsync(invoiceId)).OutstandingAmount.Should().Be(50m);

        // Cash cannot be taken without a till, a card can.
        (await PayAsync(reception, invoiceId, 20m)).FirstError.ResourceKey.Should().Be("Payment_CashRequiresOpenSession");
        (await PayAsync(reception, invoiceId, 20m, PaymentMethod.Card, "AUTH-7")).IsSuccess.Should().BeTrue();
        (await ReadInvoiceAsync(invoiceId)).OutstandingAmount.Should().Be(30m);
    }

    [SqlServerFact]
    public async Task AReceptionist_ACashier_AndAnAdministrator_EachDoOnlyWhatTheirRoleAllows()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var till = Services(lab.SiteId, Cashier);
        using var admin = Services(lab.SiteId);

        string Denied(Error error) => error.ResourceKey;
        const string denied = "Message.PermissionDenied";

        // The cashier neither opens a file nor registers a request.
        (await On<PatientRegistrationService, Result<PatientRegistrationOutcome>>(till, s => s.RegisterAsync(
                new PatientIdentityRequest { LastName = "BERRADA", FirstName = "Nadia", Gender = Gender.Female, DeclaredAgeYears = 40 }, [], [])))
            .FirstError.ResourceKey.Should().Be(denied);
        (await On<OrderRegistrationService, Result<RegisteredOrder>>(till, s => s.RegisterAsync(
                new RegisterOrderRequest { PatientId = lab.SelfPaying.Id, AnalysisTestIds = [lab.Nfs.Id], PrescriberFreeText = "Dr X" })))
            .FirstError.ResourceKey.Should().Be(denied);

        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(till, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result<string>>(till, s => s.TakeDepositAsync(
            new TakeDepositRequest(order.OrderId, 10m, PaymentMethod.Cash, null, null, null)))).IsSuccess.Should().BeTrue();

        // Neither the receptionist nor the cashier grants a discount, hands a deposit back or
        // cancels a charge; the request stays uninvoiced and the deposit held.
        Denied((await On<BillingService, Result<Guid>>(reception, s => s.IssueInvoiceAsync(
            [order.OrderId], discountAmount: 5m, discountReason: "Geste commercial"))).FirstError).Should().Be(denied);
        var deposit = (await On<BillingService, Result<IReadOnlyList<DepositRow>>>(till, s => s.GetDepositsAsync(order.OrderId))).Value.Single();
        Denied((await On<BillingService, Result>(till, s => s.RefundDepositAsync(deposit.DepositId, "Erreur"))).FirstError).Should().Be(denied);
        Denied((await On<OrderAmendmentService, Result>(till, s => s.CancelOrderAsync(order.OrderId, "Erreur"))).FirstError).Should().Be(denied);

        var invoiceId = await InvoiceAsync(till, order.OrderId);
        (await ReadInvoiceAsync(invoiceId)).DiscountAmount.Should().Be(0m);
        (await ReadInvoiceAsync(invoiceId)).OutstandingAmount.Should().Be(40m);

        // The cashier takes the rest, may not refund it; the administrator may.
        (await PayAsync(till, invoiceId, 40m)).IsSuccess.Should().BeTrue();
        var paid = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(till, s => s.GetPaymentsAsync(invoiceId)))
            .Value.Single(p => p.Amount == 40m);
        Denied((await On<BillingService, Result>(till, s => s.RefundPaymentAsync(invoiceId, paid.PaymentId, "Erreur"))).FirstError).Should().Be(denied);
        Denied((await On<BillingService, Result<Guid>>(till, s => s.IssueCreditNoteAsync(invoiceId, "Erreur de saisie"))).FirstError).Should().Be(denied);
        Denied((await On<BillingService, Result<ClaimOutcomeRecorded>>(reception, s => s.RecordClaimOutcomeAsync(
            [invoiceId], ClaimStatus.Rejected, "x"))).FirstError).Should().Be(denied);

        (await On<BillingService, Result<Guid>>(admin, s => s.OpenCashSessionAsync(100m))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result<Guid>>(admin, s => s.OpenCashSessionAsync(100m)))
            .FirstError.ResourceKey.Should().Be("CashSession_AlreadyOpenForUser");
        (await On<BillingService, Result>(admin, s => s.RefundPaymentAsync(invoiceId, paid.PaymentId, "Erreur de caisse")))
            .IsSuccess.Should().BeTrue();
        (await TillAsync(admin)).ExpectedClosingCash.Should().Be(60m);
        (await TillAsync(till)).ExpectedClosingCash.Should().Be(50m);
        (await ReadInvoiceAsync(invoiceId)).OutstandingAmount.Should().Be(40m);
    }
}
