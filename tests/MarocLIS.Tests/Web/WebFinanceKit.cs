using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Licensing;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Samples;
using Microsoft.Extensions.DependencyInjection;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The report, billing, cash and claim screens rendered in memory: the workstation's services over
/// fakes of their queries and repositories, the renderers answering bytes.
/// </summary>
public abstract class WebFinanceContext : WebScreenContext
{
    protected readonly Fake<IBillingQueries> BillingQueries = Fake<IBillingQueries>.Create();
    protected readonly Fake<IReportQueries> ReportQueries = Fake<IReportQueries>.Create();
    protected readonly Fake<IInvoicePrintSettingsQueries> PrintQueries = Fake<IInvoicePrintSettingsQueries>.Create();
    protected readonly Fake<IInvoiceRenderer> InvoiceRenderer = Fake<IInvoiceRenderer>.Create();
    protected readonly Fake<IRepository<Invoice>> InvoiceStore = Fake<IRepository<Invoice>>.Create();
    protected readonly Fake<IRepository<CashSession>> SessionStore = Fake<IRepository<CashSession>>.Create();
    protected readonly Fake<IRepository<LabReport>> ReportStore = Fake<IRepository<LabReport>>.Create();
    protected readonly Fake<IUnitOfWork> Work = Fake<IUnitOfWork>.Create();

    protected WebFinanceContext()
    {
        Services.AddSingleton<ILicenceState>(new FixedLicenceState(FullStatus()));
        Services.AddSingleton(new ActiveLanguagesCache());

        Services.AddSingleton(BillingQueries.Object);
        Services.AddSingleton(ReportQueries.Object);
        Services.AddSingleton(PrintQueries.Object);
        Services.AddSingleton(InvoiceRenderer.Object);
        Services.AddSingleton(InvoiceStore.Object);
        Services.AddSingleton(SessionStore.Object);
        Services.AddSingleton(ReportStore.Object);
        Services.AddSingleton(Work.Object);
        Services.AddSingleton(Fake<IRepository<LabOrder>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<Patient>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<Payer>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<LabSite>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<AppUser>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<Deposit>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<AnalysisTest>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<Sample>>.Create().Object);
        Services.AddSingleton(Fake<IResultQueries>.Create().Object);
        Services.AddSingleton(Fake<IReportRenderer>.Create().Object);
        Services.AddSingleton(Fake<IFontAvailability>.Create().Object);
        Services.AddSingleton(Fake<IMessageSender>.Create().Object);
        Services.AddSingleton(Fake<ISequenceAllocator>.Create().Object);
        Services.AddSingleton(Fake<IConfigurationWriter>.Create().Object);

        Services.AddScoped<BillingService>();
        Services.AddScoped<ReportReleaseService>();
        Services.AddScoped<InvoicePrintSettingsService>();
    }

    /// <summary>The counter: invoices, payments and deposits, its own till.</summary>
    protected static readonly string[] Cashier =
    [
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.RecordPayment,
        Permissions.Billing.CloseCashSession, Permissions.Reports.View, Permissions.Reports.Print,
        Permissions.Reports.ReprintValidated
    ];

    /// <summary>The accounts office: corrections, unpaid cheques and the claims to the payers.</summary>
    protected static readonly string[] Accounting =
    [
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.RecordPayment,
        Permissions.Billing.CreditNote, Permissions.Billing.DeclareUnpaid, Permissions.Billing.ExportAccounting,
        Permissions.Billing.ApplyDiscount
    ];

    protected static string Mad(decimal amount) => Money.Of(amount).Format(AppLanguage.French, MarocLIS.Localization.CultureManager.Build(AppLanguage.French));

    // ---- Data --------------------------------------------------------------------------------------

    internal static PendingReportRow Waiting(string accession = "2610-000031", string patient = "BENNANI Youssef",
                                             int awaiting = 0, OrderPayment? payment = null) =>
        new(Guid.NewGuid(), accession, patient, "بناني يوسف", OrderPriority.Routine, 6, awaiting,
            new DateTimeOffset(2026, 10, 18, 8, 10, 0, TimeSpan.FromHours(1)), 0, "Dr EL FASSI Omar", payment);

    internal static ReportRegisterRow Version(string accession = "2610-000021", string patient = "EL IDRISSI Khadija",
                                              bool critical = false, bool delivered = false, OrderPayment? payment = null,
                                              ReportDeliveryPreference preference = ReportDeliveryPreference.Email) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), accession, patient, null, "+212661445566", 1,
            ReportCompleteness.Final, ReportAmendmentKind.None, null, AppLanguage.French,
            new DateTimeOffset(2026, 10, 17, 16, 40, 0, TimeSpan.FromHours(1)), "Dr AMRANI Salma", critical, false, false, true,
            true, 2, null, delivered ? 1 : 0, delivered ? new DateTimeOffset(2026, 10, 17, 18, 0, 0, TimeSpan.FromHours(1)) : null,
            "Dr EL FASSI Omar", preference, "k.elidrissi@example.ma", "cabinet.elfassi@example.ma", payment);

    internal static BillableOrderRow Billable(string accession, string patient = "EL IDRISSI Khadija", Guid? patientId = null,
                                              decimal total = 450m, string? payer = "CNOPS", decimal payerShare = 360m,
                                              decimal deposit = 0m, Guid? payerId = null) =>
        new(Guid.NewGuid(), accession, patientId ?? Guid.NewGuid(), patient,
            new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.FromHours(1)), payer, payer is null ? null : payerId ?? Guid.NewGuid(),
            total, payer is null ? 0m : payerShare, payer is null ? total : total - payerShare, 4, LabOrderStatus.Validated,
            deposit, DirectBilling: payer is not null, PayerAgreementValid: payer is not null);

    internal static InvoiceRow Invoice(string number, InvoiceStatus status = InvoiceStatus.Issued, decimal total = 450m,
                                       decimal paid = 0m, ClaimStatus claim = ClaimStatus.NotApplicable, decimal payerShare = 0m,
                                       int overdue = 0, InvoiceKind kind = InvoiceKind.Invoice) =>
        new(Guid.NewGuid(), number, kind, status, new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 11), "EL IDRISSI Khadija",
            total, paid, total - paid, overdue, claim, "2610-000021", payerShare);

    internal static InvoiceDocument Document(InvoiceRow row) =>
        new(row.InvoiceId, row.Number, row.Kind, row.Status, row.IssueDate, row.DueDate, null!, row.BilledToName, null, null,
            null, null, row.BilledToName, null, null, row.CoveredOrders, null, null, null, row.Total, 0m, null, row.Total, 0m,
            row.Total, 0m, row.Total, row.Paid, null, [], []);

    internal static CashSessionSummary Session(decimal opening = 500m, decimal cash = 1450m, decimal withdrawals = 0m) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Nadia", new DateTimeOffset(2026, 10, 18, 7, 55, 0, TimeSpan.FromHours(1)), opening,
            cash, 900m, 350m, 0m, 0m, 0m, 0m, withdrawals, opening + cash - withdrawals, 7);

    internal static ClaimCandidateRow Candidate(string number, Guid payerId, string payer = "CNOPS", bool blocked = false, int days = 12) =>
        new(Guid.NewGuid(), number, new DateOnly(2026, 10, 6), payerId, payer, "EL IDRISSI Khadija", 450m, 360m,
            RequiresInpe: true, HasBiologistInpe: !blocked, RequiresPrescription: false, HasPrescriberInpe: true, days);

    internal static ClaimBatchRow Batch(string reference, int pending, int rejected = 0, int ageDays = 10) =>
        new(reference, Guid.NewGuid(), "CNOPS", new DateOnly(2026, 10, 18).AddDays(-ageDays), 3, 1350m, 1080m,
            pending == 3 ? 0m : 720m, 3 - pending - rejected, rejected, pending, pending == 3 ? null : new DateOnly(2026, 10, 15));
}
