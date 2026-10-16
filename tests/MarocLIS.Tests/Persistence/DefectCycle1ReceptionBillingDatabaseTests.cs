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
/// Defect-hunt cycle 1, lot c1a, end to end on SQL Server with the services as the desktop composes
/// them: a covered request registered, invoiced, paid at the till in part then in full, a payment
/// refunded, the till closed on a shortfall, the claim sent, refused and sent again, and a request
/// refused on a file merged since the counter picked it.
/// </summary>
public sealed class DefectCycle1ReceptionBillingDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "accueil";
        public string FullName => "Secrétaire d'accueil";
        public Guid SiteId => siteId;
        public string SiteCode => "PRINCIPAL";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private ServiceProvider Services(Guid siteId)
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
        services.AddSingleton<ICurrentUser>(new Operator(siteId));
        // As DesktopServiceCollectionExtensions registers them: the merge re-reads the results.
        services.AddIsolatedQueries<Application.Results.IResultQueries, MarocLIS.Infrastructure.Queries.ResultQueries>();
        services.AddIsolatedQueries<IBillingQueries, MarocLIS.Infrastructure.Queries.BillingQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private sealed record Lab(Guid SiteId, Patient Insured, Patient Duplicate, AnalysisTest Nfs, AnalysisTest Creat);

    private async Task<Lab> LabAsync()
    {
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var site = new LabSite(Unique("S"), new MultilingualText("Principal"), LabSiteKind.MainLaboratory)
        {
            LegalName = "Laboratoire Test SARL",
            // Unique per test: the fixture's database is shared and the ICE is unique.
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

        var amo = new Payer(Unique("AMO"), new MultilingualText("AMO — CNSS"), PayerKind.AmoCnss)
        {
            DefaultReimbursementRate = 0.80m,
            ThirdPartyPaymentAgreement = true,
            // A prescription by a doctor without INPE (free text): not asked for here.
            RequiresPrescription = false,
            RequiresInpeOnClaim = false
        };

        var insured = new Patient(Unique("P"), "ALAOUI", "Mohammed", new DateOnly(1959, 9, 2), Gender.Male, site.Id);
        insured.AddCoverage(new PatientCoverage(insured.Id, amo.Id, "123456", validFrom));
        var duplicate = new Patient(Unique("P"), "ALAOUI", "Mohamed", new DateOnly(1959, 9, 2), Gender.Male, site.Id);

        await using var context = database.Context();
        context.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.Add(edta);
        context.AnalysisTests.AddRange(nfs, creat);
        context.Payers.Add(amo);
        context.Patients.AddRange(insured, duplicate);
        await context.SaveChangesAsync();

        return new Lab(site.Id, insured, duplicate, nfs, creat);
    }

    private static Task<Result<RegisteredOrder>> RegisterAsync(ServiceProvider provider, Guid patientId, Guid[] analyses) =>
        On<OrderRegistrationService, Result<RegisteredOrder>>(provider, s => s.RegisterAsync(new RegisterOrderRequest
        {
            PatientId = patientId,
            AnalysisTestIds = analyses,
            TestPanelIds = [],
            PrescriberFreeText = "Dr BENNIS Karim"
        }));

    [SqlServerFact]
    public async Task ACoveredRequest_IsInvoiced_PaidAtTheTill_Refunded_Claimed_RefusedAndResent()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        // Registered under the AMO: 50 DH, 80 % claimed from the payer, 10 DH due at the counter.
        var registered = await RegisterAsync(provider, lab.Insured.Id, [lab.Nfs.Id, lab.Creat.Id]);
        registered.IsSuccess.Should().BeTrue(string.Join(", ", registered.Errors.Select(e => e.ResourceKey)));
        registered.Value.Pricing.Total.Amount.Should().Be(50m);
        registered.Value.Pricing.DueAtCounter.Amount.Should().Be(10m);

        (await On<BillingService, Result<Guid>>(provider, s => s.OpenCashSessionAsync(100m))).IsSuccess.Should().BeTrue();

        var issued = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([registered.Value.OrderId]));
        issued.IsSuccess.Should().BeTrue(string.Join(", ", issued.Errors.Select(e => e.ResourceKey)));
        var invoiceId = issued.Value;

        Task<Result> Pay(decimal amount) => On<BillingService, Result>(provider, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(invoiceId, amount, PaymentMethod.Cash, null, null, null, null)));

        // Part, then the rest; more than the patient's share is refused.
        (await Pay(6m)).IsSuccess.Should().BeTrue();
        (await Pay(5m)).FirstError.ResourceKey.Should().Be("Payment_ExceedsPatientShare");
        (await Pay(4m)).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            var invoice = await context.Invoices.Include(i => i.Payments).AsNoTracking().SingleAsync(i => i.Id == invoiceId);
            invoice.ClaimStatus.Should().Be(ClaimStatus.Prepared);
            invoice.PaidAmount.Should().Be(10m);
            invoice.OutstandingAmount.Should().Be(40m);
            invoice.Payments.Should().OnlyContain(p => p.CashSessionId != null);
        }

        // The 4 DH are given back from the drawer.
        var payments = await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(provider, s => s.GetPaymentsAsync(invoiceId));
        var four = payments.Value.Single(p => p.Amount == 4m);
        (await On<BillingService, Result>(provider, s => s.RefundPaymentAsync(invoiceId, four.PaymentId, "Analyse non réalisée")))
            .IsSuccess.Should().BeTrue();

        var till = await On<BillingService, Result<CashSessionSummary>>(provider, s => s.GetOpenCashSessionAsync());
        till.Value.CashReceipts.Should().Be(10m);
        till.Value.CashRefunds.Should().Be(4m);
        till.Value.ExpectedClosingCash.Should().Be(106m);

        // Closed 20 DH short: refused without a word, closed with one; the variance is kept.
        (await On<BillingService, Result<decimal>>(provider, s => s.CloseCashSessionAsync(86m, null)))
            .FirstError.ResourceKey.Should().Be("CashSession_VarianceExplanationRequired");
        var closed = await On<BillingService, Result<decimal>>(provider, s => s.CloseCashSessionAsync(86m, "Erreur de rendu de monnaie"));
        closed.IsSuccess.Should().BeTrue();
        closed.Value.Should().Be(-20m);
        (await On<BillingService, Result<CashSessionSummary>>(provider, s => s.GetOpenCashSessionAsync()))
            .FirstError.ResourceKey.Should().Be("CashSession_NotOpen");

        // The claim: sent, refused and written off, sent again, and paid by the payer.
        var sent = await On<BillingService, Result<ClaimSubmission>>(provider, s => s.SubmitClaimBatchAsync([invoiceId]));
        sent.IsSuccess.Should().BeTrue(string.Join(", ", sent.Errors.Select(e => e.ResourceKey)));

        (await On<BillingService, Result>(provider, s => s.RecordPayerSettlementAsync(
            invoiceId, 0m, PayerShortfallHandling.WriteOff, "Ordonnance illisible", null))).IsSuccess.Should().BeTrue();

        var resent = await On<BillingService, Result<ClaimSubmission>>(provider, s => s.SubmitClaimBatchAsync([invoiceId]));
        resent.IsSuccess.Should().BeTrue(string.Join(", ", resent.Errors.Select(e => e.ResourceKey)));
        resent.Value.Reference.Should().NotBe(sent.Value.Reference);

        (await On<BillingService, Result>(provider, s => s.RecordPayerSettlementAsync(
            invoiceId, 40m, PayerShortfallHandling.PatientPays, null, "VIR-77"))).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            var invoice = await context.Invoices.Include(i => i.Payments).AsNoTracking().SingleAsync(i => i.Id == invoiceId);
            invoice.ClaimStatus.Should().Be(ClaimStatus.Settled);
            // The 4 DH refunded at the counter are the patient's to pay again.
            invoice.Status.Should().Be(InvoiceStatus.PartiallyPaid);
            invoice.OutstandingAmount.Should().Be(4m);
            invoice.PatientPayableAmount.Should().Be(4m);
            invoice.Payments.Single(p => p.Method == PaymentMethod.WriteOff).Status.Should().Be(PaymentStatus.Cancelled);
            invoice.Payments.Single(p => p.Amount == 4m).Status.Should().Be(PaymentStatus.Refunded);

            // Every step left its reason in the audit trail.
            var reasons = await context.AuditEntries.AsNoTracking()
                .Where(a => a.Reason != null).Select(a => a.Reason!).ToListAsync();
            reasons.Should().Contain(r => r.StartsWith("Remboursement de 4.00 DH") || r.StartsWith("Remboursement de 4,00 DH"));
            reasons.Should().Contain(r => r.StartsWith("Clôture de caisse"));
            reasons.Should().Contain(r => r.StartsWith("Transmission du dossier tiers payant"));
        }
    }

    [SqlServerFact]
    public async Task ARequest_OnAFileMergedSinceTheCounterPickedIt_IsRefused()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        (await On<PatientRegistrationService, Result>(provider,
                s => s.MergeAsync(lab.Duplicate.Id, lab.Insured.Id, "Doublon créé à l'accueil")))
            .IsSuccess.Should().BeTrue();

        var refused = await RegisterAsync(provider, lab.Duplicate.Id, [lab.Nfs.Id]);

        refused.FirstError.ResourceKey.Should().Be("Patient_AlreadyMerged");

        await using var context = database.Context();
        (await context.LabOrders.AsNoTracking().CountAsync(o => o.PatientId == lab.Duplicate.Id)).Should().Be(0);

        (await RegisterAsync(provider, lab.Insured.Id, [lab.Nfs.Id])).IsSuccess.Should().BeTrue();
    }
}
