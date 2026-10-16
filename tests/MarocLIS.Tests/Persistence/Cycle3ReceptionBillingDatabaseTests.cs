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
/// Defect-hunt cycle 3, lot c3c, the reception and billing workflows end to end on SQL Server with
/// the services as the desktop composes them: an insured request paid at the till in two parts,
/// claimed, answered in part and paid off by the patient; a rejection written off then sent again;
/// a file amended after it was paid; a till closed on a shortfall after another cashier's refund;
/// two cashiers settling the same invoice at once; and who may do what among a receptionist, a
/// cashier and the accounts office.
/// </summary>
public sealed class Cycle3ReceptionBillingDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId, IReadOnlySet<string>? permissions = null) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "caisse";
        public string FullName => "Caissière";
        public Guid SiteId => siteId;
        public string SiteCode => "PRINCIPAL";
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

    [SqlServerFact]
    public async Task AnInsuredRequest_PaidInTwoParts_ClaimedAnsweredInPart_IsPaidOffByThePatient()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var till = Services(lab.SiteId, Cashier);
        using var accounts = Services(lab.SiteId, Accountant);

        // 50 DH of acts, 80 % on the payer: 10 DH at the counter. The cashier registers nothing.
        (await On<OrderRegistrationService, Result<RegisteredOrder>>(till, s => s.RegisterAsync(
                new RegisterOrderRequest { PatientId = lab.Insured.Id, AnalysisTestIds = [lab.Nfs.Id] })))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        var order = await RegisterAsync(reception, lab.Insured.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(till, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();
        var invoiceId = await InvoiceAsync(till, order.OrderId);

        var invoice = await ReadInvoiceAsync(invoiceId);
        invoice.TotalAmount.Should().Be(50m);
        invoice.PayerShareAmount.Should().Be(40m);
        invoice.PatientPayableAmount.Should().Be(10m);

        // 4 now, then 7 is more than the 6 left to the patient, then the 6.
        (await PayAsync(till, invoiceId, 4m)).IsSuccess.Should().BeTrue();
        (await PayAsync(till, invoiceId, 7m)).FirstError.ResourceKey.Should().Be("Payment_ExceedsPatientShare");
        (await PayAsync(till, invoiceId, 6m, PaymentMethod.Card, "AUTH-1")).IsSuccess.Should().BeTrue();
        (await ReadInvoiceAsync(invoiceId)).PatientPayableAmount.Should().Be(0m);

        // The cashier sends no claim; the accounts office does.
        (await On<BillingService, Result<ClaimSubmission>>(till, s => s.SubmitClaimBatchAsync([invoiceId])))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        var batch = await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([invoiceId]));
        batch.IsSuccess.Should().BeTrue(Keys(batch.Errors));

        // Sent twice: refused, and the batch reference is not spent.
        (await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([invoiceId])))
            .FirstError.ResourceKey.Should().Be("Claim_AlreadySubmitted");

        // The payer accepts 30 of its 40; the 10 refused are left to the patient.
        (await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 30m, PayerShortfallHandling.PatientPays, null, "VIR-2026-77")))
            .FirstError.ResourceKey.Should().Be("Claim_RejectionReasonRequired");
        (await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 30m, PayerShortfallHandling.PatientPays, "Acte hors nomenclature", "VIR-2026-77")))
            .IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 10m, PayerShortfallHandling.PatientPays, "x", null)))
            .FirstError.ResourceKey.Should().Be("Claim_SettlementAlreadyRecorded");

        invoice = await ReadInvoiceAsync(invoiceId);
        invoice.ClaimStatus.Should().Be(ClaimStatus.PartiallyAccepted);
        invoice.OutstandingAmount.Should().Be(10m);
        invoice.PatientPayableAmount.Should().Be(10m);

        // The patient comes back and pays the rest: the invoice is paid.
        (await PayAsync(till, invoiceId, 10m)).IsSuccess.Should().BeTrue();
        invoice = await ReadInvoiceAsync(invoiceId);
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.OutstandingAmount.Should().Be(0m);

        // A batch answer recorded later does not overwrite the invoice's own settlement.
        var outcome = await On<BillingService, Result<ClaimOutcomeRecorded>>(accounts, s => s.RecordClaimOutcomeAsync(
            [invoiceId], ClaimStatus.Accepted));
        outcome.IsFailure.Should().BeTrue();
        outcome.FirstError.ResourceKey.Should().Be("Claim_OutcomeSkippedSettled");

        // The till holds the cash; the card is on its own line.
        var summary = await On<BillingService, Result<CashSessionSummary>>(till, s => s.GetOpenCashSessionAsync());
        summary.Value.CashReceipts.Should().Be(14m);
        summary.Value.CardReceipts.Should().Be(6m);
        summary.Value.ExpectedClosingCash.Should().Be(14m);
    }

    [SqlServerFact]
    public async Task ARejectedClaim_WrittenOff_IsSentAgain_AndThePayersPaymentClosesIt()
    {
        var lab = await LabAsync();
        using var till = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        var order = await RegisterAsync(till, lab.Insured.Id, [lab.Nfs.Id, lab.Creat.Id]);
        var invoiceId = await InvoiceAsync(till, order.OrderId);
        (await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([invoiceId])))
            .IsSuccess.Should().BeTrue();

        // Refused in full and written off: the patient still owes their own 10.
        (await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 0m, PayerShortfallHandling.WriteOff, "Ordonnance non datée", null))).IsSuccess.Should().BeTrue();

        var invoice = await ReadInvoiceAsync(invoiceId);
        invoice.ClaimStatus.Should().Be(ClaimStatus.Rejected);
        invoice.OutstandingAmount.Should().Be(10m);
        invoice.Payments.Should().ContainSingle(p => p.Method == PaymentMethod.WriteOff && p.Amount == 40m);

        // Corrected and sent again: the write-off is cancelled, kept on the record, and the payer's
        // 40 can be entered.
        var again = await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([invoiceId]));
        again.IsSuccess.Should().BeTrue(Keys(again.Errors));
        again.Value.Submitted.Should().Be(1);

        invoice = await ReadInvoiceAsync(invoiceId);
        invoice.ClaimStatus.Should().Be(ClaimStatus.Submitted);
        invoice.ClaimReference.Should().Be(again.Value.Reference);
        invoice.Payments.Should().ContainSingle(p => p.Method == PaymentMethod.WriteOff)
            .Which.Status.Should().Be(PaymentStatus.Cancelled);
        invoice.OutstandingAmount.Should().Be(50m);

        (await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 40m, PayerShortfallHandling.PatientPays, null, "VIR-9"))).IsSuccess.Should().BeTrue();

        invoice = await ReadInvoiceAsync(invoiceId);
        invoice.ClaimStatus.Should().Be(ClaimStatus.Settled);
        invoice.OutstandingAmount.Should().Be(10m);
        invoice.PatientPayableAmount.Should().Be(10m);
    }

    [SqlServerFact]
    public async Task AFilePaidAtTheTill_TakesNoNewAnalysisNorALaterPrescription_ButAnAnalysisIsCancelledAndCredited()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(100m))).IsSuccess.Should().BeTrue();
        var invoiceId = await InvoiceAsync(reception, order.OrderId);
        (await PayAsync(reception, invoiceId, 50m)).IsSuccess.Should().BeTrue();

        // Invoiced: a new analysis goes on a new request.
        (await On<OrderAmendmentService, Result<OrderAmendment>>(reception, s => s.AddAnalysesAsync(
                order.OrderId, [lab.Glu.Id], [], "Oubli du médecin")))
            .FirstError.ResourceKey.Should().Be("Order_InvoicedCannotAddItem");

        var detail = (await On<OrderAmendmentService, Result<OrderDetail>>(reception, s => s.GetDetailAsync(order.OrderId))).Value;
        detail.IsInvoiced.Should().BeTrue();

        // The prescription cannot be dated after the file was registered.
        var registeredOn = DateOnly.FromDateTime(detail.RegisteredAt.Date);
        AmendOrderHeaderRequest Header(DateOnly? prescribed) => new()
        {
            OrderId = order.OrderId,
            PrescriberFreeText = "Dr BENNIS Karim",
            PrescriptionDate = prescribed,
            Priority = detail.Priority,
            ReportLanguage = detail.ReportLanguage,
            ClinicalInformation = new string('c', 2000),
            WardOrUnit = new string('w', 120),
            RoomOrBed = new string('r', 60),
            InternalNote = new string('n', 1000),
            DiagnosisCode = new string('d', 20)
        };

        (await On<OrderAmendmentService, Result>(reception, s => s.AmendHeaderAsync(Header(registeredOn.AddDays(1)), "Date corrigée")))
            .FirstError.ResourceKey.Should().Be("Order_PrescriptionDateInFuture");
        var amended = await On<OrderAmendmentService, Result>(reception, s => s.AmendHeaderAsync(Header(registeredOn.AddDays(-3)), "Date corrigée"));
        amended.IsSuccess.Should().BeTrue(Keys(amended.Errors));

        // An analysis cancelled after payment stays on the invoice until a credit note takes it off;
        // the receptionist cannot issue one, the accounts office can, and the patient is refunded.
        var creat = detail.Items.Single(i => i.AnalysisTestId == lab.Creat.Id);
        (await On<OrderAmendmentService, Result>(reception, s => s.CancelItemAsync(order.OrderId, creat.ItemId, "Non demandé")))
            .IsSuccess.Should().BeTrue();
        (await ReadInvoiceAsync(invoiceId)).TotalAmount.Should().Be(50m);

        (await On<BillingService, Result<Guid>>(reception, s => s.IssueCreditNoteAsync(invoiceId, "Analyse annulée", 10m)))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        var credit = await On<BillingService, Result<Guid>>(accounts, s => s.IssueCreditNoteAsync(invoiceId, "Analyse annulée après paiement", 10m));
        credit.IsSuccess.Should().BeTrue(Keys(credit.Errors));

        var invoice = await ReadInvoiceAsync(invoiceId);
        invoice.CreditedAmount.Should().Be(10m);
    }

    [SqlServerFact]
    public async Task ARefundFromAnotherCashiersTill_AndAShortfallAtClosing_AreAccountedOnTheRightDrawer()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(200m))).IsSuccess.Should().BeTrue();
        var invoiceId = await InvoiceAsync(reception, order.OrderId);
        (await PayAsync(reception, invoiceId, 50m)).IsSuccess.Should().BeTrue();

        var payment = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(accounts, s => s.GetPaymentsAsync(invoiceId)))
            .Value.Single();

        // Cash goes back from a drawer: the accounts office has none open yet.
        (await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(invoiceId, payment.PaymentId, "Erreur de caisse")))
            .FirstError.ResourceKey.Should().Be("Payment_CashRefundRequiresOpenSession");
        (await ReadInvoiceAsync(invoiceId)).Payments.Single().Status.Should().Be(PaymentStatus.Settled);

        (await On<BillingService, Result<Guid>>(accounts, s => s.OpenCashSessionAsync(300m))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(invoiceId, payment.PaymentId, "Erreur de caisse")))
            .IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RefundPaymentAsync(invoiceId, payment.PaymentId, "Encore")))
            .FirstError.ResourceKey.Should().Be("Payment_NotSettled");

        // The refund came out of the accounts office's drawer, not the receptionist's.
        (await On<BillingService, Result<CashSessionSummary>>(accounts, s => s.GetOpenCashSessionAsync()))
            .Value.ExpectedClosingCash.Should().Be(250m);
        (await On<BillingService, Result<CashSessionSummary>>(reception, s => s.GetOpenCashSessionAsync()))
            .Value.ExpectedClosingCash.Should().Be(250m);
        (await ReadInvoiceAsync(invoiceId)).OutstandingAmount.Should().Be(50m);

        // 230 counted for 250: refused without a word, closed with one; a second close finds nothing open.
        (await On<BillingService, Result<decimal>>(reception, s => s.CloseCashSessionAsync(230m, "  ")))
            .FirstError.ResourceKey.Should().Be("CashSession_VarianceExplanationRequired");
        var closed = await On<BillingService, Result<decimal>>(reception, s => s.CloseCashSessionAsync(230m, "Monnaie rendue en trop"));
        closed.IsSuccess.Should().BeTrue();
        closed.Value.Should().Be(-20m);
        (await On<BillingService, Result<decimal>>(reception, s => s.CloseCashSessionAsync(230m, "x")))
            .FirstError.ResourceKey.Should().Be("CashSession_NotOpen");

        // The receptionist's closed till takes no more cash.
        (await PayAsync(reception, invoiceId, 10m)).FirstError.ResourceKey.Should().Be("Payment_CashRequiresOpenSession");

        await using var context = database.Context();
        var sessions = await context.CashSessions.AsNoTracking().Where(s => s.SiteId == lab.SiteId).ToListAsync();
        sessions.Should().HaveCount(2);
        sessions.Should().ContainSingle(s => s.Status == CashSessionStatus.Closed)
            .Which.VarianceExplanation.Should().Be("Monnaie rendue en trop");
        sessions.Should().ContainSingle(s => s.Status == CashSessionStatus.Open)
            .Which.CountedCash.Should().BeNull();
    }

    [SqlServerFact]
    public async Task TwoCashiersSettlingTheSameInvoiceAtOnce_DoNotBothGetThrough()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        var order = await RegisterAsync(provider, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        var invoiceId = await InvoiceAsync(provider, order.OrderId);

        // Both read the invoice with 50 outstanding, both take the 50 by card.
        await using var first = database.Context();
        await using var second = database.Context();
        var one = await first.Invoices.Include(i => i.Payments).SingleAsync(i => i.Id == invoiceId);
        var two = await second.Invoices.Include(i => i.Payments).SingleAsync(i => i.Id == invoiceId);

        Payment Card() => new(50m, PaymentMethod.Card, DateOnly.FromDateTime(DateTime.Today), Guid.NewGuid(), lab.SiteId)
        {
            Reference = "AUTH"
        };

        one.RecordPayment(Card()).IsSuccess.Should().BeTrue();
        two.RecordPayment(Card()).IsSuccess.Should().BeTrue();

        await first.SaveChangesAsync();
        var late = async () => await second.SaveChangesAsync();
        await late.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var invoice = await ReadInvoiceAsync(invoiceId);
        invoice.Payments.Should().ContainSingle();
        invoice.PaidAmount.Should().Be(50m);
    }
    [SqlServerFact]
    public async Task ACancelledRequestHoldingADeposit_IsListedForTheRefund_ButNotInvoiced()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        var order = await RegisterAsync(reception, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result<string>>(reception, s => s.TakeDepositAsync(
            new TakeDepositRequest(order.OrderId, 20m, PaymentMethod.Cash, null, null, null)))).IsSuccess.Should().BeTrue();

        (await On<OrderAmendmentService, Result>(reception, s => s.CancelOrderAsync(order.OrderId, "Patient parti")))
            .IsSuccess.Should().BeTrue();

        var pending = (await On<BillingService, Result<IReadOnlyList<BillableOrderRow>>>(reception, s => s.GetUninvoicedOrdersAsync())).Value;
        pending.Should().ContainSingle(o => o.LabOrderId == order.OrderId);

        var issued = await On<BillingService, Result<Guid>>(reception, s => s.IssueInvoiceAsync([order.OrderId]));
        issued.IsFailure.Should().BeTrue();
        issued.FirstError.ResourceKey.Should().Be("Invoice_OrderCancelled");

        // No deposit on a cancelled request; the one held goes back from the accounts office's till.
        (await On<BillingService, Result<string>>(reception, s => s.TakeDepositAsync(
            new TakeDepositRequest(order.OrderId, 5m, PaymentMethod.Cash, null, null, null))))
            .FirstError.ResourceKey.Should().Be("Deposit_OrderCancelled");
        var deposit = (await On<BillingService, Result<IReadOnlyList<DepositRow>>>(accounts, s => s.GetDepositsAsync(order.OrderId))).Value.Single();
        (await On<BillingService, Result<Guid>>(accounts, s => s.OpenCashSessionAsync(50m))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RefundDepositAsync(deposit.DepositId, "Dossier annulé")))
            .IsSuccess.Should().BeTrue();

        (await On<BillingService, Result<CashSessionSummary>>(accounts, s => s.GetOpenCashSessionAsync()))
            .Value.ExpectedClosingCash.Should().Be(30m);
        (await On<BillingService, Result<IReadOnlyList<BillableOrderRow>>>(reception, s => s.GetUninvoicedOrdersAsync()))
            .Value.Should().NotContain(o => o.LabOrderId == order.OrderId);
    }

    [SqlServerFact]
    public async Task ARefusalLeftToThePatientAndPaidByThem_IsNotSentToThePayerAgain()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        var order = await RegisterAsync(reception, lab.Insured.Id, [lab.Nfs.Id, lab.Creat.Id]);
        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(0m))).IsSuccess.Should().BeTrue();
        var invoiceId = await InvoiceAsync(reception, order.OrderId);
        (await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([invoiceId]))).IsSuccess.Should().BeTrue();
        (await On<BillingService, Result>(accounts, s => s.RecordPayerSettlementAsync(
            invoiceId, 0m, PayerShortfallHandling.PatientPays, "Assuré non à jour", null))).IsSuccess.Should().BeTrue();

        // The whole 50 is now the patient's, and they pay it.
        (await ReadInvoiceAsync(invoiceId)).PatientPayableAmount.Should().Be(50m);
        (await PayAsync(reception, invoiceId, 50m)).IsSuccess.Should().BeTrue();

        var again = await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([invoiceId]));
        again.IsFailure.Should().BeTrue();
        (await ReadInvoiceAsync(invoiceId)).ClaimStatus.Should().Be(ClaimStatus.Rejected);
    }
}
