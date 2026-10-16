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
/// Defect-hunt cycle 2, lot c2d, end to end on SQL Server with the services as the desktop composes
/// them: the till with a deposit, a payment, a withdrawal and a closing; a partial credit note with
/// a long reason; a merge with an anonymised file; the patient file's coverage, consent and alert
/// at the lengths their dialogs allow; and what a receptionist and an accountant may each do.
/// </summary>
public sealed class Cycle2ReceptionBillingDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId, IReadOnlySet<string>? permissions = null) : ICurrentUser
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
        public IReadOnlySet<string> Permissions { get; } = permissions ?? new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => permissions is null || permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
    }

    // The grants of the shipped roles, as the UI database holds them.
    private static readonly HashSet<string> Receptionist =
    [
        Permissions.Patients.View, Permissions.Patients.Create, Permissions.Patients.Edit,
        Permissions.Orders.View, Permissions.Orders.Create, Permissions.Orders.Edit,
        Permissions.Orders.Cancel, Permissions.Orders.AddUrgent,
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.RecordPayment,
        Permissions.Billing.CloseCashSession
    ];

    private static readonly HashSet<string> Accountant =
    [
        Permissions.Patients.View, Permissions.Orders.View,
        Permissions.Billing.View, Permissions.Billing.CreateInvoice, Permissions.Billing.CreditNote,
        Permissions.Billing.RecordPayment, Permissions.Billing.ApplyDiscount, Permissions.Billing.ManageTariffs,
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
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

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

        var amo = new Payer(Unique("AMO"), new MultilingualText("AMO — CNSS"), PayerKind.AmoCnss)
        {
            DefaultReimbursementRate = 0.80m,
            ThirdPartyPaymentAgreement = true,
            RequiresPrescription = false,
            RequiresInpeOnClaim = false
        };

        var insured = new Patient(Unique("P"), "ALAOUI", "Mohammed", new DateOnly(1959, 9, 2), Gender.Male, site.Id);
        insured.AddCoverage(new PatientCoverage(insured.Id, amo.Id, "123456", validFrom));
        var selfPaying = new Patient(Unique("P"), "BENNANI", "Omar", new DateOnly(1980, 1, 15), Gender.Male, site.Id);
        var duplicate = new Patient(Unique("P"), "BENNANI", "Omar", new DateOnly(1980, 1, 15), Gender.Male, site.Id);

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

    private static Task<Result<RegisteredOrder>> RegisterAsync(ServiceProvider provider, Guid patientId, Guid[] analyses) =>
        On<OrderRegistrationService, Result<RegisteredOrder>>(provider, s => s.RegisterAsync(new RegisterOrderRequest
        {
            PatientId = patientId,
            AnalysisTestIds = analyses,
            TestPanelIds = [],
            PrescriberFreeText = "Dr BENNIS Karim"
        }));

    [SqlServerFact]
    public async Task TheTill_TakesADepositAndAPayment_RefusesAWithdrawalBeyondTheDrawer_AndClosesOnItsVariance()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        // Self-paying: 50 DH at the counter, 20 of them as a deposit before the invoice.
        var registered = await RegisterAsync(provider, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        registered.IsSuccess.Should().BeTrue(string.Join(", ", registered.Errors.Select(e => e.ResourceKey)));

        (await On<BillingService, Result<Guid>>(provider, s => s.OpenCashSessionAsync(100m))).IsSuccess.Should().BeTrue();

        (await On<BillingService, Result<string>>(provider, s => s.TakeDepositAsync(
            new TakeDepositRequest(registered.Value.OrderId, 20m, PaymentMethod.Cash, null, null, null))))
            .IsSuccess.Should().BeTrue();

        var issued = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([registered.Value.OrderId]));
        issued.IsSuccess.Should().BeTrue(string.Join(", ", issued.Errors.Select(e => e.ResourceKey)));

        (await On<BillingService, Result>(provider, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(issued.Value, 30m, PaymentMethod.Cash, null, null, null, null)))).IsSuccess.Should().BeTrue();

        // The deposit applied to the invoice is not counted a second time: 100 + 20 + 30.
        var till = await On<BillingService, Result<CashSessionSummary>>(provider, s => s.GetOpenCashSessionAsync());
        till.Value.ExpectedClosingCash.Should().Be(150m);

        // 1 500 typed for 150: refused with what the drawer should hold, nothing recorded.
        var tooMuch = await On<BillingService, Result>(provider, s => s.RecordCashWithdrawalAsync(1500m, "Dépôt en banque"));
        tooMuch.FirstError.ResourceKey.Should().Be("CashSession_WithdrawalExceedsCash");
        tooMuch.FirstError.Args.Should().ContainSingle().Which.Should().Be(150m);

        (await On<BillingService, Result>(provider, s => s.RecordCashWithdrawalAsync(150m, "Dépôt en banque")))
            .IsSuccess.Should().BeTrue();

        till = await On<BillingService, Result<CashSessionSummary>>(provider, s => s.GetOpenCashSessionAsync());
        till.Value.CashWithdrawals.Should().Be(150m);
        till.Value.ExpectedClosingCash.Should().Be(0m);

        // Nothing left to take out.
        (await On<BillingService, Result>(provider, s => s.RecordCashWithdrawalAsync(0.01m, "Dépôt en banque")))
            .FirstError.ResourceKey.Should().Be("CashSession_WithdrawalExceedsCash");

        // 15 DH found in the drawer: explained, then closed on a surplus.
        (await On<BillingService, Result<decimal>>(provider, s => s.CloseCashSessionAsync(15m, null)))
            .FirstError.ResourceKey.Should().Be("CashSession_VarianceExplanationRequired");
        var closed = await On<BillingService, Result<decimal>>(provider,
            s => s.CloseCashSessionAsync(15m, new string('x', 1000)));
        closed.IsSuccess.Should().BeTrue();
        closed.Value.Should().Be(15m);

        await using var context = database.Context();
        var session = await context.CashSessions.Include(s => s.Withdrawals).AsNoTracking()
            .SingleAsync(s => s.SiteId == lab.SiteId);
        session.Status.Should().Be(CashSessionStatus.Closed);
        session.Withdrawals.Should().ContainSingle().Which.Amount.Should().Be(150m);
        session.Variance.Should().Be(15m);
    }

    [SqlServerFact]
    public async Task APartialCreditNote_WithTheLongestReasonItsPromptAllows_IsIssued()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        var registered = await RegisterAsync(provider, lab.SelfPaying.Id, [lab.Nfs.Id, lab.Creat.Id]);
        var issued = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([registered.Value.OrderId]));
        issued.IsSuccess.Should().BeTrue();

        // The prompt takes up to 500 characters; the line quoting it holds 300. It used to fail
        // the credit note with an unexpected error.
        var reason = "Analyse facturée deux fois. " + new string('r', 472);
        reason.Length.Should().Be(500);

        var credited = await On<BillingService, Result<Guid>>(provider, s => s.IssueCreditNoteAsync(issued.Value, reason, 10m));
        credited.IsSuccess.Should().BeTrue(string.Join(", ", credited.Errors.Select(e => e.ResourceKey)));

        // And the balance, credited as one line quoting the same reason.
        var rest = await On<BillingService, Result<Guid>>(provider, s => s.IssueCreditNoteAsync(issued.Value, reason));
        rest.IsSuccess.Should().BeTrue(string.Join(", ", rest.Errors.Select(e => e.ResourceKey)));

        await using var context = database.Context();
        var notes = await context.Invoices.Include(i => i.Lines).AsNoTracking()
            .Where(i => i.CorrectsInvoiceId == issued.Value).ToListAsync();
        notes.Should().HaveCount(2);
        notes.Should().OnlyContain(n => n.CorrectionReason == reason);
        notes.SelectMany(n => n.Lines).Should().OnlyContain(l => l.Description.Length <= 300 && l.Description.EndsWith("…"));
        notes.Sum(n => n.TotalAmount).Should().Be(50m);

        var original = await context.Invoices.AsNoTracking().SingleAsync(i => i.Id == issued.Value);
        original.IsFullyCredited.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task AnAnonymisedFile_IsNeitherMergedNorMergedInto()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        (await On<PatientRegistrationService, Result>(provider, s => s.AnonymizeAsync(
            lab.Duplicate.Id, "REF-1", "Demande d'effacement du patient", overrideRetention: true)))
            .IsSuccess.Should().BeTrue();

        (await On<PatientRegistrationService, Result>(provider,
                s => s.MergeAsync(lab.SelfPaying.Id, lab.Duplicate.Id, "Même personne")))
            .FirstError.ResourceKey.Should().Be("Patient_AnonymizedIsReadOnly");
        (await On<PatientRegistrationService, Result>(provider,
                s => s.MergeAsync(lab.Duplicate.Id, lab.SelfPaying.Id, "Même personne")))
            .FirstError.ResourceKey.Should().Be("Patient_AnonymizedIsReadOnly");

        await using var context = database.Context();
        (await context.Patients.AsNoTracking().Where(p => p.Id == lab.SelfPaying.Id || p.Id == lab.Duplicate.Id)
                .Select(p => p.MergedIntoPatientId).ToListAsync())
            .Should().OnlyContain(id => id == null);

        // Two live files still merge.
        (await On<PatientRegistrationService, Result>(provider,
                s => s.MergeAsync(lab.SelfPaying.Id, lab.Insured.Id, "Doublon créé à l'accueil")))
            .IsSuccess.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task TheFilesCoverageConsentAndAlert_SaveAtTheLengthsTheirDialogsAllow()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        (await On<PatientRegistrationService, Result>(provider, s => s.AddCoverageAsync(lab.SelfPaying.Id, new CoverageRequest
        {
            PayerId = lab.PayerId,
            MembershipNumber = new string('1', 40),
            Relationship = BeneficiaryRelationship.Spouse,
            InsuredPersonName = new string('N', 200),
            InsuredPersonCin = new string('C', 20),
            EmployerName = new string('E', 200),
            IsChronicDiseaseScheme = true,
            PriorAgreementNumber = new string('A', 60),
            PriorAgreementValidUntil = DateOnly.FromDateTime(DateTime.Today).AddYears(1)
        }))).IsSuccess.Should().BeTrue();

        (await On<PatientRegistrationService, Result>(provider, s => s.RecordConsentAsync(lab.SelfPaying.Id, new ConsentRequest
        {
            Purpose = ConsentPurpose.GeneticTesting,
            SignedByName = new string('S', 200),
            SignedByCin = new string('C', 20),
            SignedByRelationship = new string('R', 60),
            FormVersion = new string('V', 40),
            Notes = new string('n', 1000)
        }))).IsSuccess.Should().BeTrue();

        (await On<PatientRegistrationService, Result>(provider, s => s.AddAlertAsync(lab.SelfPaying.Id,
            PatientAlertSeverity.Warning, new MultilingualText(new string('f', 500), new string('ع', 500)))))
            .IsSuccess.Should().BeTrue();

        // One more character than the membership column holds: what the dialog no longer lets through.
        var tooLong = async () => await On<PatientRegistrationService, Result>(provider, s => s.AddCoverageAsync(
            lab.Insured.Id, new CoverageRequest { PayerId = lab.PayerId, MembershipNumber = new string('1', 41) }));
        await tooLong.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    public async Task AWeightOrHeightWithDigitsTooMany_IsRefused_InsteadOfFailingTheSave()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        PatientIdentityRequest Identity(decimal? weight, decimal? height) => new()
        {
            LastName = Unique("TAZI"), FirstName = "Imane", Gender = Gender.Female,
            DateOfBirth = new DateOnly(1990, 3, 1), WeightKg = weight, HeightCm = height
        };

        // 12 345 kg used to overflow its column: an unexpected error, and the new file was lost.
        (await On<PatientRegistrationService, Result<PatientRegistrationOutcome>>(provider,
                s => s.RegisterAsync(Identity(12345m, 165m))))
            .FirstError.ResourceKey.Should().Be("Patient_BodyMeasureOutOfRange");

        var created = await On<PatientRegistrationService, Result<PatientRegistrationOutcome>>(provider,
            s => s.RegisterAsync(Identity(PatientRegistrationService.MaxWeightKg, PatientRegistrationService.MaxHeightCm)));
        created.IsSuccess.Should().BeTrue(string.Join(", ", created.Errors.Select(e => e.ResourceKey)));
        var id = created.Value.PatientId!.Value;

        (await On<PatientRegistrationService, Result>(provider, s => s.UpdateAsync(id, Identity(64.5m, 1650m))))
            .FirstError.ResourceKey.Should().Be("Patient_BodyMeasureOutOfRange");

        await using var context = database.Context();
        var saved = await context.Patients.AsNoTracking().SingleAsync(p => p.Id == id);
        saved.WeightKg.Should().Be(500m);
        saved.HeightCm.Should().Be(300m);
    }

    [SqlServerFact]
    public async Task AReceptionist_RegistersAndCashes_ButNeitherRefundsNorClaims_AndAnAccountantRegistersNothing()
    {
        var lab = await LabAsync();
        using var reception = Services(lab.SiteId, Receptionist);
        using var accounts = Services(lab.SiteId, Accountant);

        var registered = await RegisterAsync(reception, lab.Insured.Id, [lab.Nfs.Id, lab.Creat.Id]);
        registered.IsSuccess.Should().BeTrue(string.Join(", ", registered.Errors.Select(e => e.ResourceKey)));

        (await On<BillingService, Result<Guid>>(reception, s => s.OpenCashSessionAsync(50m))).IsSuccess.Should().BeTrue();
        var issued = await On<BillingService, Result<Guid>>(reception, s => s.IssueInvoiceAsync([registered.Value.OrderId]));
        issued.IsSuccess.Should().BeTrue(string.Join(", ", issued.Errors.Select(e => e.ResourceKey)));
        (await On<BillingService, Result>(reception, s => s.RecordPaymentAsync(
            new RecordPaymentRequest(issued.Value, 10m, PaymentMethod.Cash, null, null, null, null)))).IsSuccess.Should().BeTrue();

        var payment = (await On<BillingService, Result<IReadOnlyList<PaymentRow>>>(reception, s => s.GetPaymentsAsync(issued.Value)))
            .Value.Single();

        // Money back, a credit note and the claim are the accounts office's.
        (await On<BillingService, Result>(reception, s => s.RefundPaymentAsync(issued.Value, payment.PaymentId, "Erreur")))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await On<BillingService, Result<Guid>>(reception, s => s.IssueCreditNoteAsync(issued.Value, "Erreur de saisie")))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await On<BillingService, Result<ClaimSubmission>>(reception, s => s.SubmitClaimBatchAsync([issued.Value])))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        // The accounts office sends the claim, records its refusal, but registers no patient and no request.
        (await On<BillingService, Result<ClaimSubmission>>(accounts, s => s.SubmitClaimBatchAsync([issued.Value])))
            .IsSuccess.Should().BeTrue();
        (await On<BillingService, Result<ClaimOutcomeRecorded>>(accounts, s => s.RecordClaimOutcomeAsync(
            [issued.Value], ClaimStatus.Rejected, "Ordonnance illisible"))).IsSuccess.Should().BeTrue();
        (await RegisterAsync(accounts, lab.Insured.Id, [lab.Nfs.Id]))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        (await On<PatientRegistrationService, Result>(accounts, s => s.MergeAsync(lab.Duplicate.Id, lab.SelfPaying.Id, "Doublon")))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        // The receptionist sees the refusal; the payment stands and the till still holds it.
        await using var context = database.Context();
        var invoice = await context.Invoices.Include(i => i.Payments).AsNoTracking().SingleAsync(i => i.Id == issued.Value);
        invoice.ClaimStatus.Should().Be(ClaimStatus.Rejected);
        invoice.Payments.Should().ContainSingle(p => p.Status == PaymentStatus.Settled);
        (await On<BillingService, Result<CashSessionSummary>>(reception, s => s.GetOpenCashSessionAsync()))
            .Value.ExpectedClosingCash.Should().Be(60m);
    }
}
