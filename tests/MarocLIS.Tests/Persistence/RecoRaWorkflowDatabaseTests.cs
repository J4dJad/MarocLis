using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Instruments;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MarocLIS.Tests.Domain;
using System.Reflection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the owner's decisions (lot ra), created from the model.</summary>
public sealed class RecoRaScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_RecoRa_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public RecoRaScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// The owner's decisions on the clinical screens (« appliquer les recommandations »), replayed
/// against SQL Server: the closed list of storage places held by the service, a past retention date
/// refused, a superseded report no longer handed over, « Compte rendu » offered only when the
/// service would issue one, and sending out to a partner laboratory as a right of its own.
/// </summary>
public sealed class RecoRaWorkflowDatabaseTests(RecoRaScratchDatabase database)
    : IClassFixture<RecoRaScratchDatabase>
{
    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; set; }
        public string UserName => "operateur";
        public string FullName => "Opérateur cycle";
        public Guid SiteId { get; set; }
        public string SiteCode { get; set; } = "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification { get; set; } = ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds { get; set; } = [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();

        /// <summary>Null grants everything; otherwise only these.</summary>
        public HashSet<string>? Granted { get; set; }

        public bool Has(string permission) => Granted is null || Granted.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "PAILLASSE-RA";
    }

    private sealed class Renderer : IReportRenderer
    {
        public byte[] Render(ReportModel model) => [0x25, 0x50, 0x44, 0x46];
        public int CountPages(byte[] pdf) => 1;
    }

    private sealed class AllFonts : IFontAvailability
    {
        public string ArabicFamily => "Arabic";
        public string LatinFamily => "Latin";
        public bool IsAvailable(string family) => true;
        public IReadOnlyList<string> MissingFrom(IEnumerable<string> families) => [];
    }

    private sealed record Lab(LabSite Site, LabSite Partner, AppUser Biologist, AnalysisTest Glucose,
                              AnalysisTest Vitamin, AnalysisTest Comment, Patient Patient);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static Operator OperatorOf(Lab lab, HashSet<string>? granted = null) => new()
    {
        UserId = lab.Biologist.Id,
        SiteId = lab.Site.Id,
        SiteCode = lab.Site.Code,
        AccessibleSiteIds = [lab.Site.Id],
        Granted = granted
    };

    /// <summary>The permissions a role the laboratory ships carries, as the first run seeds them.</summary>
    private static HashSet<string> ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return [.. (string[])tuple.GetField("Item3")!.GetValue(definition)!];
        }

        throw new InvalidOperationException(roleCode);
    }

    private ServiceProvider Services(Operator user)
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
        services.AddSingleton<IReportRenderer>(new Renderer());
        services.AddSingleton<IFontAvailability, AllFonts>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        services.AddIsolatedQueries<IReportQueries, ReportQueries>();
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ISampleQueries, SampleQueries>();
        services.AddIsolatedQueries<ISubcontractQueries, SubcontractQueries>();
        services.AddIsolatedQueries<IRecollectionQueries, RecollectionQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static AnalysisTest Analysis(string code, string name, Department department, SpecimenType specimen,
                                         DateOnly validFrom, ResultValueType type = ResultValueType.Numeric,
                                         decimal? deltaPercent = null)
    {
        var analysis = new AnalysisTest(Unique(code), new MultilingualText(name), department.Id, specimen.Id)
        {
            RoutineTurnaroundHours = 4
        };

        var parameter = new TestParameter(Unique(code), new MultilingualText(name), type)
        {
            Decimals = 1,
            DeltaCheckPercent = deltaPercent
        };
        if (type == ResultValueType.Numeric) parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
        analysis.AddParameter(parameter);

        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
        {
            LetterKey = "B",
            Coefficient = 20m,
            LetterKeyValue = 1m,
            Price = 20m,
            IsVatExempt = true
        });

        analysis.Validate().IsSuccess.Should().BeTrue();
        return analysis;
    }

    private async Task<Lab> LabAsync()
    {
        var biologist = new AppUser(Unique("bio"), "Dr Cycle", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-RA-1"
        };

        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire cycle"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE CYCLE SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-RA-1",
            ResponsibleBiologistUserId = biologist.Id
        };

        var partner = new LabSite(Unique("P"), new MultilingualText("Laboratoire partenaire"), LabSiteKind.ReferralPartner)
        {
            City = "Casablanca"
        };

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var glucose = Analysis("GLY", "Glycémie", department, specimen, validFrom, deltaPercent: 50m);
        var vitamin = Analysis("VTD", "Vitamine D", department, specimen, validFrom);
        vitamin.IsSubcontracted = true;
        vitamin.SubcontractorSiteId = partner.Id;
        var comment = Analysis("COM", "Aspect", department, specimen, validFrom, ResultValueType.Text);

        var patient = new Patient(Unique("P"), "TAZI", "Imane", new DateOnly(1981, 6, 12), Gender.Female, site.Id)
        {
            MobilePhone = "0612345678",
            SearchKey = PatientSearchKey.Build("TAZI", "Imane")
        };

        await using var context = database.Context();

        context.Users.Add(biologist);
        context.Sites.AddRange(site, partner);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.AddRange(glucose, vitamin, comment);
        context.Patients.Add(patient);
        // The site's storage list: since the closed list is enforced by the service too, a tube
        // can only be shelved in one of its places.
        context.SampleStorageLocations.Add(SampleStorageLocation.Create(site.Id, "Réfrigérateur R1").Value);
        await context.SaveChangesAsync();

        return new Lab(site, partner, biologist, glucose, vitamin, comment, patient);
    }

    private static async Task<RegisteredOrder> RegisterAsync(ServiceProvider provider, Patient patient,
                                                             params AnalysisTest[] analyses)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider,
            s => s.RegisterAsync(new RegisterOrderRequest
            {
                PatientId = patient.Id,
                AnalysisTestIds = [.. analyses.Select(a => a.Id)],
                PrescriberFreeText = "Dr Prescripteur"
            }));

        registered.IsSuccess.Should().BeTrue(string.Join(", ", registered.Errors.Select(e => e.ResourceKey)));
        return registered.Value;
    }

    private static async Task DrawAndReceiveAsync(ServiceProvider provider, Guid sampleId)
    {
        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = sampleId, VolumeMl = 5m }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = sampleId }]))).Value.Received.Should().Be(1);
    }

    private async Task<Dictionary<Guid, TestResult>> ResultsAsync(Guid orderId)
    {
        await using var context = database.Context();
        return await context.TestResults.AsNoTracking().Where(r => r.LabOrderId == orderId)
                            .ToDictionaryAsync(r => r.TestParameterId);
    }

    private static async Task ReleaseValuesAsync(ServiceProvider provider, Guid orderId, IEnumerable<Guid> resultIds)
    {
        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(orderId, [])))
            .IsSuccess.Should().BeTrue();
        var validated = await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(orderId, [.. resultIds.Select(id => new ValidationDecision(id, true))]));
        validated.Value.Blocked.Should().BeEmpty();
    }

    private async Task<Sample> SampleAsync(Guid sampleId)
    {
        await using var context = database.Context();
        return await context.Samples.AsNoTracking().SingleAsync(s => s.Id == sampleId);
    }

    private async Task SetStorageAsync(Guid sampleId, string? place, DateOnly? keepUntil)
    {
        await using var context = database.Context();
        await context.Samples.Where(s => s.Id == sampleId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.StorageLocation, place).SetProperty(s => s.KeepUntil, keepUntil));
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    // ---- 1. The closed list of storage places, held by the service ------------------------------

    [SqlServerFact]
    public async Task A_ATubeIsStoredOnlyInAnActivePlaceOfTheList_AtReceptionInTheSerumBankAndForAnAliquot_AFormerPlaceStaysOnIt()
    {
        var lab = await LabAsync();

        var freezer = SampleStorageLocation.Create(lab.Site.Id, "Congélateur C2").Value;
        freezer.Deactivate().IsSuccess.Should().BeTrue();
        await using (var context = database.Context())
        {
            context.SampleStorageLocations.Add(freezer);
            await context.SaveChangesAsync();
        }

        using var provider = Services(OperatorOf(lab));
        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId, VolumeMl = 5m }]))).IsSuccess.Should().BeTrue();

        // Reception: a place typed outside the list is refused, and the tube is not received.
        var typed = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId, StorageLocation = "Frigo de la paillasse" }]));
        typed.Value.Received.Should().Be(0);
        typed.Value.Problems.Should().ContainSingle(e => e.ResourceKey == "Sample_StorageLocationNotInList");
        (await SampleAsync(tube.SampleId)).ReceivedAt.Should().BeNull();

        // A place of the list, whatever its case, is written as the list writes it.
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId, StorageLocation = " réfrigérateur r1 " }])))
            .Value.Received.Should().Be(1);
        (await SampleAsync(tube.SampleId)).StorageLocation.Should().Be("Réfrigérateur R1");

        // The serum bank: neither a place out of the list nor one taken out of use.
        (await On<SampleWorkflowService, Result>(provider,
                s => s.MoveToStorageAsync(tube.SampleId, "Frigo de la paillasse", null, null)))
            .FirstError.ResourceKey.Should().Be("Sample_StorageLocationNotInList");
        (await On<SampleWorkflowService, Result>(provider,
                s => s.MoveToStorageAsync(tube.SampleId, "Congélateur C2", null, null)))
            .FirstError.ResourceKey.Should().Be("Sample_StorageLocationNotInList");
        (await SampleAsync(tube.SampleId)).StorageLocation.Should().Be("Réfrigérateur R1");

        // A tube recorded in free text before the list keeps its place, readable in the serum bank,
        // and may be confirmed there when re-shelved; it cannot be moved to another unlisted place.
        await SetStorageAsync(tube.SampleId, "Frigo ancien", null);

        (await On<SampleWorkflowService, Result<IReadOnlyList<StoredSampleRow>>>(provider,
                s => s.GetStoredSamplesAsync(tube.Barcode)))
            .Value.Should().ContainSingle().Which.StorageLocation.Should().Be("Frigo ancien");

        (await On<SampleWorkflowService, Result>(provider,
            s => s.MoveToStorageAsync(tube.SampleId, "Frigo ancien", "B3", null))).IsSuccess.Should().BeTrue();
        var kept = await SampleAsync(tube.SampleId);
        kept.StorageLocation.Should().Be("Frigo ancien");
        kept.StoragePosition.Should().Be("B3");

        (await On<SampleWorkflowService, Result>(provider,
                s => s.MoveToStorageAsync(tube.SampleId, "Autre frigo", null, null)))
            .FirstError.ResourceKey.Should().Be("Sample_StorageLocationNotInList");

        // An aliquot: a place out of the list is refused, none keeps the parent tube's, a listed one is taken.
        (await On<SampleWorkflowService, Result<string>>(provider,
                s => s.CreateAliquotAsync(tube.SampleId, 1m, "Sérothèque", "Frigo de la paillasse")))
            .FirstError.ResourceKey.Should().Be("Sample_StorageLocationNotInList");

        var inherited = await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, 1m, "Sérothèque", null));
        var listed = await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, 1m, "Sous-traitance", "RÉFRIGÉRATEUR R1"));
        inherited.IsSuccess.Should().BeTrue();
        listed.IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            var aliquots = await context.Set<SampleAliquot>().AsNoTracking()
                .Where(a => a.ParentSampleId == tube.SampleId).ToDictionaryAsync(a => a.Barcode, a => a.StorageLocation);
            aliquots.Should().HaveCount(2);
            aliquots[inherited.Value].Should().Be("Frigo ancien");
            aliquots[listed.Value].Should().Be("Réfrigérateur R1");
        }
    }

    // ---- 2. « Conserver jusqu'au » in the past -----------------------------------------------------

    [SqlServerFact]
    public async Task B_APastKeepUntilDate_IsRefused_TheDateTheTubeAlreadyCarriesMayStay()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId);

        (await On<SampleWorkflowService, Result>(provider,
                s => s.MoveToStorageAsync(tube.SampleId, "Réfrigérateur R1", null, Today.AddDays(-1))))
            .FirstError.ResourceKey.Should().Be("Sample_KeepUntilInPast");
        (await SampleAsync(tube.SampleId)).StorageLocation.Should().BeNull();

        (await On<SampleWorkflowService, Result>(provider,
            s => s.MoveToStorageAsync(tube.SampleId, "Réfrigérateur R1", null, Today))).IsSuccess.Should().BeTrue();
        (await SampleAsync(tube.SampleId)).KeepUntil.Should().Be(Today);

        // A tube whose retention ran out keeps its date when it is put back on the shelf…
        await SetStorageAsync(tube.SampleId, "Réfrigérateur R1", Today.AddDays(-3));
        (await On<SampleWorkflowService, Result>(provider,
            s => s.MoveToStorageAsync(tube.SampleId, "Réfrigérateur R1", "A1", Today.AddDays(-3)))).IsSuccess.Should().BeTrue();

        // … but no other past date can be chosen for it.
        (await On<SampleWorkflowService, Result>(provider,
                s => s.MoveToStorageAsync(tube.SampleId, "Réfrigérateur R1", "A1", Today.AddDays(-2))))
            .FirstError.ResourceKey.Should().Be("Sample_KeepUntilInPast");
        (await SampleAsync(tube.SampleId)).KeepUntil.Should().Be(Today.AddDays(-3));
    }

    // ---- 3. A superseded report ----------------------------------------------------------------------

    [SqlServerFact]
    public async Task C_AReportReplacedByANewerVersion_IsNotHandedOver_TheCurrentVersionIs()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        await DrawAndReceiveAsync(provider, order.Labels.Single().SampleId);

        var result = (await ResultsAsync(order.OrderId)).Values.Single();
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(result.Id, "5,2")]))).Value.Accepted.Should().Be(1);
        await ReleaseValuesAsync(provider, order.OrderId, [result.Id]);

        var v1 = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        v1.IsSuccess.Should().BeTrue(v1.IsFailure ? v1.FirstError.ResourceKey : null);

        var v2 = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(
            order.OrderId, amendmentKind: ReportAmendmentKind.IdentityCorrection,
            amendmentReason: new MultilingualText("Prénom du patient corrigé")));
        v2.IsSuccess.Should().BeTrue(v2.IsFailure ? v2.FirstError.ResourceKey : null);

        var old = await On<ReportReleaseService, Result<DeliveryOutcome>>(provider, s => s.RecordDeliveryAsync(
            v1.Value.ReportId, DeliveryChannel.PrintedAtCounter, ReportRecipientKind.Patient, "TAZI Imane", null));
        old.FirstError.ResourceKey.Should().Be("Report_DeliverySuperseded");
        old.FirstError.Args.Should().Equal(1);

        var current = await On<ReportReleaseService, Result<DeliveryOutcome>>(provider, s => s.RecordDeliveryAsync(
            v2.Value.ReportId, DeliveryChannel.PrintedAtCounter, ReportRecipientKind.Patient, "TAZI Imane", null));
        current.IsSuccess.Should().BeTrue(current.IsFailure ? current.FirstError.ResourceKey : null);

        await using var context = database.Context();
        (await context.ReportDeliveries.AsNoTracking().CountAsync(d => d.LabReportId == v1.Value.ReportId)).Should().Be(0);
        (await context.ReportDeliveries.AsNoTracking().CountAsync(d => d.LabReportId == v2.Value.ReportId)).Should().Be(1);
    }

    // ---- 4. « Compte rendu » on the validation screen ------------------------------------------------

    [SqlServerFact]
    public async Task D_CompteRendu_IsOfferedOnceAValueIsValidated_AsTheServiceThenIssuesIt()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose, lab.Comment);
        foreach (var label in order.Labels) await DrawAndReceiveAsync(provider, label.SampleId);

        var results = await ResultsAsync(order.OrderId);
        var glucose = results[lab.Glucose.Parameters.Single().Id];
        var comment = results.Values.Single(r => r.Id != glucose.Id);

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider, s => s.SubmitAsync(order.OrderId,
            [new ResultEntry(glucose.Id, "5,2"), new ResultEntry(comment.Id, "Sérum limpide")]))).Value.Accepted.Should().Be(2);
        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        async Task<bool> OfferedAsync() =>
            ValidationScreenRules.CanIssueReport(
                (await On<ResultEntryService, Result<ResultEntrySheet>>(provider, s => s.OpenAsync(order.OrderId))).Value.Rows);

        // Entered and checked, nothing validated: greyed, and the service would refuse it.
        (await OfferedAsync()).Should().BeFalse();
        (await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId)))
            .FirstError.ResourceKey.Should().Be("Report_NoValidatedResults");

        // One analysis validated: offered, and the service issues the partial report.
        (await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(order.OrderId, [new ValidationDecision(glucose.Id, true)]))).Value.Validated.Should().Be(1);

        (await OfferedAsync()).Should().BeTrue();
        var partial = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        partial.IsSuccess.Should().BeTrue(partial.IsFailure ? partial.FirstError.ResourceKey : null);

        // Everything validated is on that report: greyed until the next validation.
        (await OfferedAsync()).Should().BeFalse();
    }

    // ---- 5. Sending out to a partner laboratory ------------------------------------------------------

    [SqlServerFact]
    public async Task E_SendingOutToAPartner_IsARightOfItsOwn_ThePhlebotomistStillSendsHerTubesToTheBench()
    {
        // The shipped roles: the bench and the biologists send out, the phlebotomist and the desk do not.
        foreach (var role in new[] { SystemRoles.Technician, SystemRoles.Biologist, SystemRoles.ResponsibleBiologist,
                                     SystemRoles.Administrator })
            ShippedRole(role).Should().Contain(Permissions.Samples.Subcontract, role);
        foreach (var role in new[] { SystemRoles.Phlebotomist, SystemRoles.Receptionist })
            ShippedRole(role).Should().NotContain(Permissions.Samples.Subcontract, role);
        ShippedRole(SystemRoles.Phlebotomist).Should().Contain(Permissions.Samples.Dispatch);

        // An installation already running: at the next start the seeder grants the new right to the
        // shipped roles that carry it, and to them only (the « migration » of the permission).
        foreach (var role in new[] { SystemRoles.Technician, SystemRoles.Phlebotomist })
        {
            var shipped = ShippedRole(role);
            var installed = new Role(role, new MultilingualText(role), isBuiltIn: true);
            installed.SeedShippedPermissions(shipped.Where(p => p != Permissions.Samples.Subcontract));

            installed.ApplyShippedPermissions(shipped);
            installed.Has(Permissions.Samples.Subcontract).Should().Be(role == SystemRoles.Technician, role);
            installed.Has(Permissions.Samples.Dispatch).Should().BeTrue(role);
        }

        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));

        var order = await RegisterAsync(admin, lab.Patient, lab.Vitamin);
        var tube = order.Labels.Single();

        using var nurse = Services(OperatorOf(lab, ShippedRole(SystemRoles.Phlebotomist)));

        // The phlebotomist draws and sends the tube to the laboratory, as before.
        (await On<SampleWorkflowService, Result>(nurse, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId, VolumeMl = 4m }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result>(nurse, s => s.DispatchAsync([tube.SampleId]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(admin, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]))).Value.Received.Should().Be(1);

        Guid itemId;
        await using (var context = database.Context())
            itemId = await context.LabOrderItems.AsNoTracking()
                .Where(i => i.LabOrderId == order.OrderId).Select(i => i.Id).SingleAsync();

        // … but neither sends it out to the partner nor keeps the partner's reference.
        (await On<SubcontractingService, Result<IReadOnlyList<SubcontractDispatch>>>(nurse,
                s => s.DispatchAsync([itemId], null)))
            .FirstError.Code.Should().Be("forbidden");

        var technicianUser = OperatorOf(lab, ShippedRole(SystemRoles.Technician));
        technicianUser.Qualification = ProfessionalQualification.LaboratoryTechnician;
        using var bench = Services(technicianUser);

        var sent = await On<SubcontractingService, Result<IReadOnlyList<SubcontractDispatch>>>(bench,
            s => s.DispatchAsync([itemId], null));
        sent.IsSuccess.Should().BeTrue(sent.IsFailure ? sent.FirstError.ResourceKey : null);

        (await On<SubcontractingService, Result>(nurse, s => s.SetPartnerReferenceAsync(itemId, "PART-1234")))
            .FirstError.Code.Should().Be("forbidden");
        (await On<SubcontractingService, Result>(bench, s => s.SetPartnerReferenceAsync(itemId, "PART-1234")))
            .IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
            (await context.LabOrderItems.AsNoTracking().SingleAsync(i => i.Id == itemId))
                .SubcontractorReference.Should().Be("PART-1234");
    }
}
