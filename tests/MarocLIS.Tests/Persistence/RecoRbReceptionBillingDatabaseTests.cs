using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Patients;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
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
/// The owner's decisions on the reception and billing items of cycles 1 to 4, replayed on SQL
/// Server: mobile payments and bills of exchange on the shift report; a cash refund the drawer
/// cannot pay refused; only the patient's own payments refunded; the payer's settlement after a
/// credit note checked against what it still owes; a merged duplicate's request invoiced with the
/// surviving file's; an analysis repeated only once it has a value.
/// </summary>
public sealed class RecoRbReceptionBillingDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId, IReadOnlySet<string>? permissions = null) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "caisse";
        public string FullName => "Caissière";
        public Guid SiteId => siteId;
        public string SiteCode { get; } = "R" + ((uint)siteId.GetHashCode()).ToString("X8");
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
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.RecordPayment,
        Permissions.Billing.CloseCashSession
    ];

    private static readonly HashSet<string> Accountant =
    [
        Permissions.Patients.View, Permissions.Orders.View,
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.CreditNote,
        Permissions.Billing.RecordPayment, Permissions.Billing.ApplyDiscount,
        Permissions.Billing.CloseCashSession, Permissions.Billing.ExportAccounting
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
        services.AddIsolatedQueries<IResultQueries, MarocLIS.Infrastructure.Queries.ResultQueries>();
        services.AddIsolatedQueries<IBillingQueries, MarocLIS.Infrastructure.Queries.BillingQueries>();
        services.AddIsolatedQueries<IOrderQueries, MarocLIS.Infrastructure.Queries.OrderQueries>();
        services.AddIsolatedQueries<ISampleQueries, MarocLIS.Infrastructure.Queries.SampleQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static string Keys(IEnumerable<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private sealed record Lab(Guid SiteId, Guid PayerId, Patient Insured, Patient SelfPaying, Patient Duplicate,
                              AnalysisTest Nfs, AnalysisTest Creat);

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
        var duplicate = new Patient(Unique("P"), "TAZI", "Youssef", new DateOnly(1988, 7, 3), Gender.Male, site.Id);

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(edta);
        context.AnalysisTests.AddRange(nfs, creat);
        context.Payers.Add(amo);
        context.Patients.AddRange(insured, selfPaying, duplicate);
        await context.SaveChangesAsync();

        return new Lab(site.Id, amo.Id, insured, selfPaying, duplicate, nfs, creat);
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

    private static async Task<Guid> InvoiceAsync(ServiceProvider provider, params Guid[] orderIds)
    {
        var issued = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync(orderIds));
        issued.IsSuccess.Should().BeTrue(Keys(issued.Errors));
        return issued.Value;
    }

    private static Task<Result> PayAsync(ServiceProvider provider, Guid invoiceId, decimal amount,
                                         PaymentMethod method = PaymentMethod.Cash, string? reference = null) =>
        On<BillingService, Result>(provider, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(invoiceId, amount, method, reference, null, null, null)));

    private static Task<Result<string>> DepositAsync(ServiceProvider provider, Guid orderId, decimal amount,
                                                     PaymentMethod method, string? reference = null) =>
        On<BillingService, Result<string>>(provider, s => s.TakeDepositAsync(
            new TakeDepositRequest(orderId, amount, method, reference, null,
                                   DateOnly.FromDateTime(DateTime.Today).AddDays(30))));

    private async Task<Invoice> ReadInvoiceAsync(Guid invoiceId)
    {
        await using var context = database.Context();
        return await context.Invoices.Include(i => i.Payments).AsNoTracking().SingleAsync(i => i.Id == invoiceId);
    }

    private static Task<CashSessionSummary> TillAsync(ServiceProvider provider) =>
        On<BillingService, CashSessionSummary>(provider, async s => (await s.GetOpenCashSessionAsync()).Value);

    // ---- 1. Mobile payments and bills of exchange on the shift report --------------------------------

    [SqlServerFact]
    public async Task MobilePayments_AndBillsOfExchange_HaveTheirOwnLineOnTheShiftReport()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(100m))).IsSuccess.Should().BeTrue();

        var first = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await DepositAsync(reception, first.OrderId, 10m, PaymentMethod.MobilePayment, "MOB-0001")).IsSuccess.Should().BeTrue();
        (await DepositAsync(reception, first.OrderId, 15m, PaymentMethod.BillOfExchange, "LC-0001")).IsSuccess.Should().BeTrue();

        var second = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id]);
        var invoiceId = await InvoiceAsync(reception, second.OrderId);
        (await PayAsync(reception, invoiceId, 20m, PaymentMethod.MobilePayment, "MOB-0002")).IsSuccess.Should().BeTrue();

        var till = await TillAsync(reception);
        till.MobileReceipts.Should().Be(30m);
        till.BillOfExchangeReceipts.Should().Be(15m);
        // Neither goes into the drawer.
        till.CashReceipts.Should().Be(0m);
        till.ExpectedClosingCash.Should().Be(100m);

        // A mobile payment refunded while its shift is open comes off its own line.
        var mobile = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(accounts, s => s.GetPaymentsAsync(invoiceId)))
            .Value.Single();
        mobile.CanRefund.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(invoiceId, mobile.PaymentId, "Double paiement")))
            .IsSuccess.Should().BeTrue(); // not cash: no till of the refunder needed

        (await TillAsync(reception)).MobileReceipts.Should().Be(10m);

        await using var context = database.Context();
        var session = await context.CashSessions.AsNoTracking().SingleAsync(s => s.Id == till.CashSessionId);
        session.MobileReceipts.Should().Be(10m);
        session.BillOfExchangeReceipts.Should().Be(15m);
        session.TotalReceipts.Should().Be(25m);
    }

    // ---- 5. A cash refund the drawer cannot pay ------------------------------------------------------

    [SqlServerFact]
    public async Task ACashRefund_LargerThanTheDrawer_IsRefusedLikeAWithdrawal_AndNothingChanges()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        (await On<BillingService, Result<Guid>>(accounts, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();

        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        var invoiceId = await InvoiceAsync(accounts, order.OrderId);
        (await PayAsync(accounts, invoiceId, 50m)).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RecordCashWithdrawalAsync(45m, "Remise en banque")))
            .IsSuccess.Should().BeTrue();
        (await TillAsync(accounts)).ExpectedClosingCash.Should().Be(5m);

        // 50 to give back from a drawer holding 5: refused, the payment stays settled.
        var payment = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(accounts, s => s.GetPaymentsAsync(invoiceId)))
            .Value.Single();
        var refused = await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(invoiceId, payment.PaymentId, "Erreur de caisse"));
        refused.FirstError.ResourceKey.Should().Be("CashSession_RefundExceedsCash");
        refused.FirstError.Args.Should().ContainSingle().Which.Should().Be(5m);
        (await TillAsync(accounts)).ExpectedClosingCash.Should().Be(5m);
        (await ReadInvoiceAsync(invoiceId)).Payments.Single().Status.Should().Be(PaymentStatus.Settled);

        // The same for a cash deposit.
        var other = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id]);
        (await DepositAsync(accounts, other.OrderId, 20m, PaymentMethod.Cash)).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RecordCashWithdrawalAsync(20m, "Remise en banque")))
            .IsSuccess.Should().BeTrue();
        var deposit = (await On<BillingService, Result<IReadOnlyList<DepositRow>>>(accounts, s => s.GetDepositsAsync(other.OrderId)))
            .Value.Single();
        (await On<BillingService, Result>(accounts, s => s.RefundDepositAsync(deposit.DepositId, "Dossier annulé")))
            .FirstError.ResourceKey.Should().Be("CashSession_RefundExceedsCash");
        (await On<BillingService, Result<IReadOnlyList<DepositRow>>>(accounts, s => s.GetDepositsAsync(other.OrderId)))
            .Value.Single().Status.Should().Be(DepositStatus.Held);
        (await TillAsync(accounts)).ExpectedClosingCash.Should().Be(5m);
    }

    [SqlServerFact]
    public async Task ACashRefund_TheDrawerCanPay_GoesThrough_DownToZero()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        (await On<BillingService, Result<Guid>>(accounts, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();
        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        var invoiceId = await InvoiceAsync(accounts, order.OrderId);
        (await PayAsync(accounts, invoiceId, 50m)).IsSuccess.Should().BeTrue();

        var payment = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(accounts, s => s.GetPaymentsAsync(invoiceId)))
            .Value.Single();
        (await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(invoiceId, payment.PaymentId, "Erreur de caisse")))
            .IsSuccess.Should().BeTrue();
        (await TillAsync(accounts)).ExpectedClosingCash.Should().Be(0m);
    }

    // ---- 8 and 11. The payer's settlement after a credit note; only the patient's payments refunded --

    [SqlServerFact]
    public async Task AfterACreditNote_ThePayersSettlement_IsCheckedAgainstWhatItStillOwes_AndAWriteOffIsNotRefundable()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        // 50 DH, 40 on the payer. A credit note of 30 takes 10 off the patient and 20 off the payer.
        var order = await RegisterAsync(reception, lab.Insured.Id, [lab.Nfs.Id, lab.Creat.Id]);
        var invoiceId = await InvoiceAsync(reception, order.OrderId);
        (await On<BillingService, Result<Guid>>(accounts, s => s.IssueCreditNoteAsync(invoiceId, "Analyse non réalisée", 30m)))
            .IsSuccess.Should().BeTrue();

        var row = (await On<BillingService, Result<PagedResult<InvoiceRow>>>(accounts, s => s.GetInvoicesAsync()))
            .Value.Items.Single(i => i.InvoiceId == invoiceId);
        row.PayerShare.Should().Be(40m);
        row.PayerDue.Should().Be(20m);
        (await ReadInvoiceAsync(invoiceId)).PayerDueAmount.Should().Be(20m);

        var sent = await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([invoiceId]));
        sent.IsSuccess.Should().BeTrue(Keys(sent.Errors));

        // The original share is no longer owed: refused, naming what is.
        var refused = await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 40m, PayerShortfallHandling.PatientPays, null, "VIR-1"));
        refused.FirstError.ResourceKey.Should().Be("Claim_AcceptedAmountInvalid");
        refused.FirstError.Args.Should().ContainSingle().Which.Should().Be(20m);

        // 15 of the 20 accepted, 5 written off: no shortfall computed on the original 40.
        (await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 15m, PayerShortfallHandling.WriteOff, "Acte hors nomenclature", "VIR-1"))).IsSuccess.Should().BeTrue();

        var invoice = await ReadInvoiceAsync(invoiceId);
        invoice.ClaimStatus.Should().Be(ClaimStatus.PartiallyAccepted);
        invoice.Payments.Should().ContainSingle(p => p.Method == PaymentMethod.WriteOff).Which.Amount.Should().Be(5m);
        invoice.ShortfallTransfers.Should().BeEmpty();

        // The refund picker offers neither the payer's settlement nor the write-off, and the service refuses them.
        var payments = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(accounts, s => s.GetPaymentsAsync(invoiceId))).Value;
        payments.Should().HaveCount(2).And.OnlyContain(p => !p.CanRefund);
        foreach (var payment in payments)
            (await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(invoiceId, payment.PaymentId, "Erreur")))
                .FirstError.ResourceKey.Should().Be("Payment_OnlyPatientPaymentsRefundable");
        (await ReadInvoiceAsync(invoiceId)).Payments.Should().OnlyContain(p => p.Status == PaymentStatus.Settled);
    }

    // ---- 9. A merged duplicate's request ----------------------------------------------------------

    [SqlServerFact]
    public async Task AMergedDuplicatesRequest_IsListedUnderTheSurvivor_AndInvoicedWithTheSurvivorsOwn()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);

        var old = await RegisterAsync(admin, lab.Duplicate.Id, [lab.Creat.Id]);
        var own = await RegisterAsync(admin, lab.SelfPaying.Id, [lab.Nfs.Id]);

        // Before the merge, two patients: refused.
        (await On<BillingService, Result<Guid>>(admin, s => s.IssueInvoiceAsync([old.OrderId, own.OrderId])))
            .FirstError.ResourceKey.Should().Be("Invoice_MultiplePatients");

        (await On<PatientRegistrationService, Result>(admin, s => s.MergeAsync(lab.Duplicate.Id, lab.SelfPaying.Id, "Doublon créé à l'accueil")))
            .IsSuccess.Should().BeTrue();

        var rows = (await On<BillingService, Result<IReadOnlyList<BillableOrderRow>>>(admin, s => s.GetUninvoicedOrdersAsync()))
            .Value.Where(r => r.LabOrderId == old.OrderId || r.LabOrderId == own.OrderId).ToList();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.PatientId == lab.SelfPaying.Id && r.PatientName == "TAZI Youssef");

        var invoiceId = await InvoiceAsync(admin, old.OrderId, own.OrderId);
        var invoice = await ReadInvoiceAsync(invoiceId);
        invoice.PatientId.Should().Be(lab.SelfPaying.Id);
        invoice.TotalAmount.Should().Be(50m);
    }

    // ---- 3. Repeating an analysis with no value ----------------------------------------------------

    [SqlServerFact]
    public async Task AnAnalysisReceivedWithoutAValue_IsNotRepeated_UntilAValueIsEntered()
    {
        var lab = await LabAsync();
        using var admin = Services(lab.SiteId);

        var order = await RegisterAsync(admin, lab.SelfPaying.Id, [lab.Nfs.Id]);
        var tube = order.Labels.Single();
        (await On<SampleWorkflowService, Result>(admin, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(admin, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]))).Value.Received.Should().Be(1);

        var detail = (await On<OrderAmendmentService, Result<OrderDetail>>(admin, s => s.GetDetailAsync(order.OrderId))).Value;
        var item = detail.Items.Single();
        item.EnteredResultCount.Should().Be(0);
        item.CanRepeat.Should().BeFalse();

        (await On<OrderAmendmentService, Result>(admin, s => s.RepeatItemAsync(order.OrderId, item.ItemId, "Valeur douteuse")))
            .FirstError.ResourceKey.Should().Be("Item_RepeatRequiresValue");

        List<Guid> ids;
        await using (var context = database.Context())
        {
            (await context.LabOrderItems.AsNoTracking().SingleAsync(i => i.Id == item.ItemId)).RunCount.Should().Be(item.RunCount);
            ids = await context.TestResults.AsNoTracking().Where(r => r.LabOrderId == order.OrderId).Select(r => r.Id).ToListAsync();
        }

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(admin,
            s => s.SubmitAsync(order.OrderId, [.. ids.Select(id => new ResultEntry(id, "9.9"))]))).Value.Accepted.Should().Be(1);

        item = (await On<OrderAmendmentService, Result<OrderDetail>>(admin, s => s.GetDetailAsync(order.OrderId))).Value.Items.Single();
        item.CanRepeat.Should().BeTrue();
        (await On<OrderAmendmentService, Result>(admin, s => s.RepeatItemAsync(order.OrderId, item.ItemId, "Valeur douteuse")))
            .IsSuccess.Should().BeTrue();
    }
}
