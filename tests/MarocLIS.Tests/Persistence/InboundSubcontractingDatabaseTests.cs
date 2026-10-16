using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Patients;
using MarocLIS.Application.Relations;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Relations;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Inbound subcontracting end to end, against SQL Server and the services as the desktop composes
/// them: another laboratory sends us its patients' tubes, we analyse them, the report goes back to
/// that laboratory and the bill is that laboratory's, not the patient's. The referring laboratory is
/// set up as the screens allow (a payer of kind "Laboratoire adresseur", a prescriber of kind
/// laboratory, a subcontracting convention with a discount), the order is registered with the
/// origin "Sous-traitance reçue", and the circuit runs through reception, results, release, the
/// invoice, the claim batch, the payer's settlement and the accounting export.
/// </summary>
public sealed class InboundSubcontractingDatabaseTests(SubcontractingScratchDatabase database)
    : IClassFixture<SubcontractingScratchDatabase>
{
    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; set; }
        public string UserName => "biologiste";
        public string FullName => "Dr Adresse";
        public Guid SiteId { get; set; }
        public string SiteCode { get; set; } = "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds { get; set; } = [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-ST";
    }

    private sealed class CapturingRenderer : IReportRenderer
    {
        public List<ReportModel> Models { get; } = [];

        public byte[] Render(ReportModel model)
        {
            Models.Add(model);
            return [0x25, 0x50, 0x44, 0x46, (byte)Models.Count];
        }

        public int CountPages(byte[] pdf) => 1;
    }

    private sealed class AllFonts : IFontAvailability
    {
        public string ArabicFamily => "Arabic";
        public string LatinFamily => "Latin";
        public bool IsAvailable(string family) => true;
        public IReadOnlyList<string> MissingFrom(IEnumerable<string> families) => [];
    }

    private sealed record Lab(LabSite Site, AppUser Biologist, AnalysisTest Glucose, AnalysisTest Ferritin,
                              Patient Patient);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private ServiceProvider Services(Operator user, CapturingRenderer renderer)
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
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton<IReportRenderer>(renderer);
        services.AddSingleton<IFontAvailability, AllFonts>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        services.AddIsolatedQueries<IReportQueries, ReportQueries>();
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ISampleQueries, SampleQueries>();
        services.AddIsolatedQueries<ISubcontractQueries, SubcontractQueries>();
        services.AddIsolatedQueries<IBillingQueries, BillingQueries>();
        services.AddIsolatedQueries<IMasterDataQueries, MasterDataQueries>();
        services.AddIsolatedQueries<IRelationsQueries, RelationsQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static string Keys(IReadOnlyList<Error> errors) => string.Join(", ", errors.Select(e => e.ResourceKey));

    private static AnalysisTest Analysis(string code, string name, Department department, SpecimenType specimen,
                                         decimal price, DateOnly validFrom)
    {
        var analysis = new AnalysisTest(Unique(code), new MultilingualText(name), department.Id, specimen.Id)
        {
            RoutineTurnaroundHours = 4
        };

        var parameter = new TestParameter(Unique(code), new MultilingualText(name), ResultValueType.Numeric)
        {
            Decimals = 1
        };
        parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 500m));
        analysis.AddParameter(parameter);

        // The laboratory's own price list (no payer-specific tariff): what a referring laboratory
        // is billed today, whatever its convention says.
        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
        {
            LetterKey = "B",
            Coefficient = price,
            LetterKeyValue = 1m,
            Price = price,
            IsVatExempt = true
        });

        analysis.Validate().IsSuccess.Should().BeTrue();
        return analysis;
    }

    private async Task<Lab> LabAsync()
    {
        var biologist = new AppUser(Unique("bio"), "Dr Adresse", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-TEST-2"
        };

        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire receveur"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE RECEVEUR SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("2, rue du Test"),
            City = "Casablanca",
            HealthMinistryAuthorizationNumber = "AUT-TEST-2",
            ResponsibleBiologistUserId = biologist.Id
        };

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var glucose = Analysis("GLY", "Glycémie", department, specimen, 40m, validFrom);
        var ferritin = Analysis("FER", "Ferritine", department, specimen, 160m, validFrom);

        var patient = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1988, 6, 14), Gender.Female, site.Id);

        await using var context = database.Context();
        context.NumberSequences.Add(new NumberSequence(SequencePurpose.Accession, site.Id, DateTime.Today.Year,
                                                       "{SITE}-{YY}{MM}-{SEQ:6}"));
        context.Users.Add(biologist);
        context.Sites.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.AddRange(glucose, ferritin);
        context.Patients.Add(patient);
        await context.SaveChangesAsync();

        return new Lab(site, biologist, glucose, ferritin, patient);
    }

    /// <summary>
    /// The referring laboratory as the screens set it up today: a payer ("Organismes payeurs"), a
    /// prescriber ("Prescripteurs", kind laboratory) and a convention ("Relations › Conventions").
    /// Three records with nothing linking the payer to the other two.
    /// </summary>
    private static async Task<(Guid PayerId, Guid PrescriberId, Guid PartnershipId)> ReferringLabAsync(
        ServiceProvider provider, bool requiresPrescription)
    {
        var payer = await On<MasterDataService, Result<Guid>>(provider, s => s.SavePayerAsync(new PayerRequest
        {
            Code = Unique("BIOLAB"),
            Name = new MultilingualText("Laboratoire Biolab Rabat"),
            Kind = PayerKind.ReferringInstitution,
            DefaultReimbursementRate = 1.00m,
            UsesNationalReferenceTariff = false,
            ThirdPartyPaymentAgreement = true,
            PaymentTermsDays = 30,
            RequiresPrescription = requiresPrescription,
            RequiresInpeOnClaim = false,
            City = "Rabat",
            AddressLine = "12, avenue Fal Ould Oumeir"
        }));
        payer.IsSuccess.Should().BeTrue(Keys(payer.Errors));

        var prescriber = await On<MasterDataService, Result<Guid>>(provider, s => s.SavePrescriberAsync(new PrescriberRequest
        {
            // The editor insists on a last name, whatever the kind.
            LastName = "BIOLAB",
            FirstName = string.Empty,
            Kind = PrescriberKind.Laboratory,
            InstitutionName = "Laboratoire Biolab Rabat",
            Email = "resultats@biolab.example",
            City = "Rabat",
            DeliveryPreference = ReportDeliveryPreference.Email
        }));
        prescriber.IsSuccess.Should().BeTrue(Keys(prescriber.Errors));

        var partnership = await On<RelationsService, Result<Guid>>(provider, s => s.SavePartnershipAsync(new SavePartnershipRequest
        {
            PartnerName = "Laboratoire Biolab Rabat",
            PartnerKind = PartnerKind.Laboratory,
            PrescriberId = prescriber.Value,
            Kind = PartnershipKind.Subcontracting,
            Reference = "CONV-ST-2026-01",
            StartDate = DateOnly.FromDateTime(DateTime.Today).AddMonths(-1),
            DiscountPercent = 15m,
            PaymentTermsDays = 30
        }));
        partnership.IsSuccess.Should().BeTrue(Keys(partnership.Errors));

        return (payer.Value, prescriber.Value, partnership.Value);
    }

    private static async Task GiveCoverageAsync(ServiceProvider provider, Patient patient, Guid payerId)
    {
        // The only place a payer reaches an order: the patient's primary coverage. The referring
        // laboratory's own file number is typed as the "membership number", for want of a field.
        var covered = await On<PatientRegistrationService, Result>(provider, s => s.AddCoverageAsync(patient.Id,
            new CoverageRequest
            {
                PayerId = payerId,
                MembershipNumber = "BIOLAB-24-00871",
                ValidFrom = DateOnly.FromDateTime(DateTime.Today),
                IsPrimary = true,
                DirectBillingEnabled = true
            }));
        covered.IsSuccess.Should().BeTrue(Keys(covered.Errors));
    }

    /// <summary>What the registration screen sends: no discount, no coverage chosen, no ward.</summary>
    private static async Task<RegisteredOrder> RegisterAsync(ServiceProvider provider, Lab lab, Guid prescriberId)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider,
            s => s.RegisterAsync(new RegisterOrderRequest
            {
                PatientId = lab.Patient.Id,
                AnalysisTestIds = [lab.Glucose.Id, lab.Ferritin.Id],
                Origin = OrderOrigin.Subcontracting,
                PrescriberId = prescriberId,
                PatientFasted = true,
                BillDirectlyToPatient = false
            }));

        registered.IsSuccess.Should().BeTrue(Keys(registered.Errors));
        return registered.Value;
    }

    private async Task ReceiveEnterValidateAsync(ServiceProvider provider, RegisteredOrder order)
    {
        var tubes = order.Labels.Select(l => l.SampleId).ToList();

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
                [.. tubes.Select(id => new RecordCollectionRequest { SampleId = id })])))
            .IsSuccess.Should().BeTrue();

        var received = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider,
            s => s.ReceiveAsync([.. tubes.Select(id => new ReceiveSampleRequest { SampleId = id })]));
        received.IsSuccess.Should().BeTrue();
        received.Value.Received.Should().Be(tubes.Count);

        List<Guid> rows;
        await using (var context = database.Context())
            rows = await context.TestResults.AsNoTracking()
                .Where(r => r.LabOrderId == order.OrderId).Select(r => r.Id).ToListAsync();

        var entered = await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [.. rows.Select(id => new ResultEntry(id, "95"))]));
        entered.IsSuccess.Should().BeTrue();

        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        var validated = await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(order.OrderId, [.. rows.Select(id => new ValidationDecision(id, true))]));
        validated.IsSuccess.Should().BeTrue();
        validated.Value.Validated.Should().Be(rows.Count);
    }

    // ---- The whole circuit ------------------------------------------------------------------------

    [SqlServerFact]
    public async Task ReferringLaboratory_IsReportedTo_Invoiced_Claimed_AndPaid_WhileThePatientOwesNothing()
    {
        var lab = await LabAsync();
        var renderer = new CapturingRenderer();
        var user = new Operator
        {
            UserId = lab.Biologist.Id, SiteId = lab.Site.Id, SiteCode = lab.Site.Code,
            AccessibleSiteIds = [lab.Site.Id]
        };
        using var provider = Services(user, renderer);

        var (payerId, prescriberId, _) = await ReferringLabAsync(provider, requiresPrescription: false);
        await GiveCoverageAsync(provider, lab.Patient, payerId);

        // ---- Registration: the referring laboratory pays everything, nothing is due at the counter.
        var order = await RegisterAsync(provider, lab, prescriberId);

        order.Pricing.Gross.Amount.Should().Be(200m);
        // Gap: the convention's 15 % is not applied. Nothing reads Partnership.DiscountPercent (nor
        // Prescriber.DiscountPercent) at registration, and the screen has no discount field.
        order.Pricing.Discount.Amount.Should().Be(0m);
        order.Pricing.PayerShare.Amount.Should().Be(200m);
        order.Pricing.PatientShare.Amount.Should().Be(0m);
        order.Pricing.DueAtCounter.Amount.Should().Be(0m);

        await using (var context = database.Context())
        {
            var stored = await context.LabOrders.AsNoTracking().SingleAsync(o => o.Id == order.OrderId);
            stored.Origin.Should().Be(OrderOrigin.Subcontracting);
            stored.PayerId.Should().Be(payerId);
            stored.PrescriberId.Should().Be(prescriberId);
            stored.DirectBillingAtRegistration.Should().BeTrue();
            stored.ReimbursementRateAtRegistration.Should().Be(1.00m);
        }

        // The order file shows the origin and names the laboratory, not "BIOLAB" as a doctor.
        {
            var detail = await On<IOrderQueries, OrderDetail?>(provider, q => q.GetDetailAsync(order.OrderId, lab.Site.Id));
            detail.Should().NotBeNull();
            detail!.Origin.Should().Be(OrderOrigin.Subcontracting);
            detail.PrescriberName.Should().Be("Laboratoire Biolab Rabat");
        }

        // ---- Reception, results, validation, release.
        await ReceiveEnterValidateAsync(provider, order);

        var released = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        released.IsSuccess.Should().BeTrue(released.IsFailure ? released.FirstError.ResourceKey : null);

        // The report names the referring laboratory as prescriber — it printed "Dr BIOLAB".
        renderer.Models.Should().ContainSingle().Which.PrescriberName.Should().Be("Laboratoire Biolab Rabat");

        // The register offers it by e-mail to the laboratory's address, under the laboratory's name.
        var register = await On<ReportReleaseService, Result<IReadOnlyList<ReportRegisterRow>>>(provider,
            s => s.GetRegisterAsync(order.AccessionNumber));
        register.IsSuccess.Should().BeTrue();
        var row = register.Value.Should().ContainSingle().Subject;
        row.PrescriberName.Should().Be("Laboratoire Biolab Rabat");
        row.PrescriberPreference.Should().Be(ReportDeliveryPreference.Email);
        row.PrescriberEmail.Should().Be("resultats@biolab.example");
        row.AwaitingDelivery.Should().BeTrue();

        var delivered = await On<ReportReleaseService, Result<DeliveryOutcome>>(provider,
            s => s.RecordDeliveryAsync(released.Value.ReportId, DeliveryChannel.Email, ReportRecipientKind.Prescriber,
                                       row.PrescriberName, row.PrescriberEmail));
        delivered.IsSuccess.Should().BeTrue(delivered.IsFailure ? delivered.FirstError.ResourceKey : null);

        // ---- Billing: in the uninvoiced list with nothing on the patient, invoiced to the laboratory.
        var pending = await On<BillingService, Result<IReadOnlyList<BillableOrderRow>>>(provider,
            s => s.GetUninvoicedOrdersAsync());
        pending.IsSuccess.Should().BeTrue();
        var billable = pending.Value.Should().ContainSingle(o => o.LabOrderId == order.OrderId).Subject;
        billable.PayerId.Should().Be(payerId);
        billable.PatientShare.Should().Be(0m);

        var invoiceId = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([order.OrderId]));
        invoiceId.IsSuccess.Should().BeTrue(Keys(invoiceId.Errors));

        await using (var context = database.Context())
        {
            var invoice = await context.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId.Value);
            invoice.PayerId.Should().Be(payerId);
            invoice.BilledToName.Should().Be("Laboratoire Biolab Rabat");
            invoice.BilledToCity.Should().Be("Rabat");
            invoice.PrescriberName.Should().Be("Laboratoire Biolab Rabat");
            invoice.TotalAmount.Should().Be(200m);
            invoice.PayerShareAmount.Should().Be(200m);
            invoice.PatientShareAmount.Should().Be(0m);
            invoice.ClaimStatus.Should().Be(ClaimStatus.Prepared);
            // The payer's 30 days, the convention's own terms are not read.
            invoice.DueDate.Should().Be(DateOnly.FromDateTime(DateTime.Today).AddDays(30));
        }

        // ---- Claim batch (bordereau) to the laboratory, then its transfer.
        var candidates = await On<BillingService, Result<IReadOnlyList<ClaimCandidateRow>>>(provider,
            s => s.GetClaimCandidatesAsync());
        var candidate = candidates.Value.Should().ContainSingle(c => c.InvoiceId == invoiceId.Value).Subject;
        candidate.PayerId.Should().Be(payerId);
        candidate.Blockers.Should().BeEmpty();

        var batch = await On<BillingService, Result<ClaimSubmission>>(provider,
            s => s.SubmitClaimBatchAsync([invoiceId.Value]));
        batch.IsSuccess.Should().BeTrue(Keys(batch.Errors));
        batch.Value.Submitted.Should().Be(1);

        var settled = await On<BillingService, Result>(provider, s => s.RecordPayerSettlementAsync(
            invoiceId.Value, 200m, PayerShortfallHandling.WriteOff, null, "VIR-BIOLAB-0042"));
        settled.IsSuccess.Should().BeTrue(Keys(settled.Errors));

        await using (var context = database.Context())
        {
            var invoice = await context.Invoices.AsNoTracking().Include(i => i.Payments)
                .SingleAsync(i => i.Id == invoiceId.Value);
            invoice.ClaimStatus.Should().Be(ClaimStatus.Settled);
            invoice.ClaimReference.Should().Be(batch.Value.Reference);
            invoice.OutstandingAmount.Should().Be(0m, "the laboratory paid it all and the patient owes nothing");
            invoice.Payments.Should().ContainSingle()
                .Which.Method.Should().Be(PaymentMethod.ThirdPartyPayer);
        }

        // ---- Accounting: the sale and the transfer sit on the laboratory's receivable, not a patient's.
        await using (var context = database.Context())
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var sources = await new AccountingQueries(context).GetSalesSourcesAsync([lab.Site.Id], today, today);
            var entries = SalesAccountingJournal.Build(sources, AccountingSettings.Default, today, today,
                AccountingJournals.Sales | AccountingJournals.Receipts | AccountingJournals.Adjustments);

            entries.Should().OnlyContain(e => e.IsBalanced);

            var payerCode = (await context.Payers.AsNoTracking().SingleAsync(p => p.Id == payerId)).Code;
            var lines = entries.SelectMany(e => e.Lines).ToList();

            lines.Where(l => l.ThirdParty == payerCode).Sum(l => l.Debit).Should().Be(200m);
            lines.Where(l => l.ThirdParty == payerCode).Sum(l => l.Credit).Should().Be(200m);
            lines.Where(l => l.Account == AccountingSettings.Default.PatientReceivable && l.ThirdParty != payerCode)
                .Sum(l => l.Debit - l.Credit).Should().Be(0m);
        }
    }

    [SqlServerFact]
    public async Task AsSeeded_AReferringLaboratoryPayerAsksForThePrescribersInpe_AndBlocksTheClaim()
    {
        // The seeded "Laboratoire adresseur" payer requires a prescription (RequiresPrescription is
        // true for every kind but self-pay), so the claim waits on a prescriber INPE that a
        // laboratory referring its patients does not have. The administrator has to untick
        // "Ordonnance obligatoire" on the payer first.
        var lab = await LabAsync();
        var user = new Operator
        {
            UserId = lab.Biologist.Id, SiteId = lab.Site.Id, SiteCode = lab.Site.Code,
            AccessibleSiteIds = [lab.Site.Id]
        };
        using var provider = Services(user, new CapturingRenderer());

        var (payerId, prescriberId, _) = await ReferringLabAsync(provider, requiresPrescription: true);
        await GiveCoverageAsync(provider, lab.Patient, payerId);
        var order = await RegisterAsync(provider, lab, prescriberId);

        var invoiceId = await On<BillingService, Result<Guid>>(provider, s => s.IssueInvoiceAsync([order.OrderId]));
        invoiceId.IsSuccess.Should().BeTrue(Keys(invoiceId.Errors));

        var candidates = await On<BillingService, Result<IReadOnlyList<ClaimCandidateRow>>>(provider,
            s => s.GetClaimCandidatesAsync());
        candidates.Value.Single(c => c.InvoiceId == invoiceId.Value).Blockers
            .Should().Equal("Claim.RequiresPrescriberInpe");
    }

    [Fact]
    public void ALaboratoryPrescriber_IsNamedByItsEstablishment_NotAsADoctor()
    {
        var laboratory = new Prescriber("BIOLAB", string.Empty, PrescriberKind.Laboratory)
        {
            InstitutionName = "Laboratoire Biolab Rabat"
        };
        laboratory.FullName.Should().Be("Laboratoire Biolab Rabat");

        // Without an establishment name, its last name alone, still without "Dr".
        new Prescriber("BIOLAB", string.Empty, PrescriberKind.Laboratory).FullName.Should().Be("BIOLAB");

        new Prescriber("TAZI", "Omar", PrescriberKind.Specialist).FullName.Should().Be("Dr TAZI Omar");
        Prescriber.IsEstablishment(PrescriberKind.Institution).Should().BeTrue();
        Prescriber.IsEstablishment(PrescriberKind.GeneralPractitioner).Should().BeFalse();
    }
}
