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

/// <summary>A scratch database of its own for the defect-hunt cycle 4 (lot c), created from the model.</summary>
public sealed class Cycle4cScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle4c_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle4cScratchDatabase()
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
/// Cycle 4, lot c: the circuit replayed against SQL Server with the roles the laboratory ships — a
/// tube accepted with a reservation and shelved at reception, a discarded tube that nothing may
/// touch any more, an analyser link switched off and on, and what a phlebotomist, a technician and
/// a biologist may each do along the way.
/// </summary>
public sealed class Cycle4cWorkflowDatabaseTests(Cycle4cScratchDatabase database)
    : IClassFixture<Cycle4cScratchDatabase>
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
        public string Workstation => "PAILLASSE-C4C";
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
        services.AddIsolatedQueries<IBenchWorksheetQueries, BenchWorksheetQueries>();
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
            ProfessionalOrderNumber = "CNOM-C4C-1"
        };

        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire cycle"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE CYCLE SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-C4C-1",
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

    // ---- 1. Reception with a reservation, shelved on the way in ---------------------------------

    [SqlServerFact]
    public async Task A_ATubeAcceptedWithAReservation_AndShelvedAtReception_IsStoredForItsRetention_AndNotReceivedTwice()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId, VolumeMl = 5m }]))).IsSuccess.Should().BeTrue();

        // A defect ticked with the plain « Accepter »: refused, nothing recorded.
        var plain = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId, ObservedDefects = SampleDefect.Haemolysed }]));
        plain.Value.Received.Should().Be(0);
        plain.Value.Problems.Should().ContainSingle(e => e.ResourceKey == "Sample_DefectsRequireExplicitDecision");
        (await SampleAsync(tube.SampleId)).ReceivedAt.Should().BeNull();

        var reserved = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
        [
            new ReceiveSampleRequest
            {
                SampleId = tube.SampleId, ObservedDefects = SampleDefect.Haemolysed, AcceptWithReservation = true,
                TemperatureCelsius = 4m, StorageLocation = "Réfrigérateur R1"
            }
        ]));
        reserved.Value.Received.Should().Be(1);

        var stored = await SampleAsync(tube.SampleId);
        stored.Status.Should().Be(SampleStatus.Stored);
        stored.IsAcceptedWithReservation.Should().BeTrue();
        stored.Defects.Should().HaveFlag(SampleDefect.Haemolysed);
        stored.StorageLocation.Should().Be("Réfrigérateur R1");
        stored.ReceptionTemperatureCelsius.Should().Be(4m);
        stored.KeepUntil.Should().Be(DateOnly.FromDateTime(DateTime.Today).AddDays(7));

        var bank = (await On<SampleWorkflowService, Result<IReadOnlyList<StoredSampleRow>>>(provider,
            s => s.GetStoredSamplesAsync(tube.Barcode))).Value.Should().ContainSingle().Subject;
        bank.IsAcceptedWithReservation.Should().BeTrue();
        bank.HasDefects.Should().BeTrue();
        bank.IsUnshelved.Should().BeFalse();

        // Scanned again: shown where it stands, and not received a second time.
        (await On<SampleWorkflowService, Result<SampleReceptionCandidate>>(provider, s => s.ScanAsync(tube.Barcode)))
            .Value.Status.Should().Be(SampleStatus.Stored);
        var again = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]));
        again.Value.Received.Should().Be(0);
        again.Value.Problems.Should().ContainSingle(e => e.ResourceKey == "Sample_CannotReceiveFromStatus");
        (await SampleAsync(tube.SampleId)).ReceivedAt.Should().Be(stored.ReceivedAt);
    }

    // ---- 2. A discarded tube --------------------------------------------------------------------

    [SqlServerFact]
    public async Task B_ADiscardedTube_IsNeitherShelvedNorAliquotedNorReceived_AndLeavesTheSerumBank()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId);

        var result = (await ResultsAsync(order.OrderId)).Values.Single();
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(result.Id, "5,2")]))).Value.Accepted.Should().Be(1);

        (await On<SampleWorkflowService, Result>(provider, s => s.DiscardAsync(tube.SampleId, " ")))
            .FirstError.ResourceKey.Should().Be("Sample_DiscardNoteRequired");
        (await On<SampleWorkflowService, Result>(provider, s => s.DiscardAsync(tube.SampleId, "Fin de série")))
            .IsSuccess.Should().BeTrue();

        (await On<SampleWorkflowService, Result>(provider,
                s => s.MoveToStorageAsync(tube.SampleId, "Réfrigérateur R1", null, null)))
            .FirstError.ResourceKey.Should().Be("Sample_AlreadyDiscarded");
        (await On<SampleWorkflowService, Result<string>>(provider,
                s => s.CreateAliquotAsync(tube.SampleId, 1m, "Sérothèque", null)))
            .FirstError.ResourceKey.Should().Be("Sample_CannotAliquotFromStatus");
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
                [new ReceiveSampleRequest { SampleId = tube.SampleId }])))
            .Value.Problems.Should().ContainSingle(e => e.ResourceKey == "Sample_CannotReceiveFromStatus");

        (await On<SampleWorkflowService, Result<IReadOnlyList<StoredSampleRow>>>(provider,
            s => s.GetStoredSamplesAsync(tube.Barcode))).Value.Should().BeEmpty();

        var discarded = await SampleAsync(tube.SampleId);
        discarded.Status.Should().Be(SampleStatus.Discarded);
        await using var context = database.Context();
        (await context.SampleEvents.AsNoTracking().Where(e => e.SampleId == tube.SampleId).Select(e => e.Kind).ToListAsync())
            .Should().ContainSingle(k => k == SampleEventKind.Discarded);
    }

    // ---- 3. An analyser link switched off and on -------------------------------------------------

    private static InstrumentInterfaceService Interfaces(IServiceProvider scope) => new(
        scope.GetRequiredService<IRepository<InstrumentInterface>>(),
        scope.GetRequiredService<IRepository<Instrument>>(),
        ReviewFixesBilling2Tests.Stub<IInstrumentMessageLogStore>.Create(),
        ReviewFixesBilling2Tests.Stub<IInstrumentResultReplayer>.Create(),
        ReviewFixesBilling2Tests.Stub<IInstrumentMessagePreviewer>.Create(),
        new InstrumentHostOptions(),
        ReviewFixesBilling2Tests.Stub<ISecurityAuditor>.Create(),
        ReviewFixesBilling2Tests.Stub<IInstrumentInterfaceQueries>.Create(),
        scope.GetRequiredService<IUnitOfWork>(),
        scope.GetRequiredService<IPermissionGuard>(),
        scope.GetRequiredService<ICurrentUser>(),
        scope.GetRequiredService<IClock>());

    private static async Task<Result> ToggleAsync(ServiceProvider provider, Guid linkId, bool enabled, string reason)
    {
        using var scope = provider.CreateScope();
        return await Interfaces(scope.ServiceProvider).SetEnabledAsync(linkId, enabled, reason);
    }

    [SqlServerFact]
    public async Task C_AnAnalyserLink_IsSwitchedOffAndOnWithAReason_ByTheTechnician_NotByThePhlebotomist()
    {
        var lab = await LabAsync();

        var instrument = new Instrument(Unique("AU"), new MultilingualText("AU480"), EquipmentKind.Analyser, lab.Site.Id);
        var link = new InstrumentInterface(instrument.Id, lab.Site.Id, InstrumentProtocol.AstmE1394, InstrumentTransportKind.Tcp)
        {
            Host = "192.168.10.48", TcpPort = 5100, IsEnabled = true
        };
        // A link whose host was never entered: it may be switched off, never on.
        var second = new Instrument(Unique("BC"), new MultilingualText("BC-6800"), EquipmentKind.Analyser, lab.Site.Id);
        var unfinished = new InstrumentInterface(second.Id, lab.Site.Id, InstrumentProtocol.AstmE1394, InstrumentTransportKind.Tcp);

        await using (var context = database.Context())
        {
            context.Instruments.AddRange(instrument, second);
            context.InstrumentInterfaces.AddRange(link, unfinished);
            await context.SaveChangesAsync();
        }

        using (var nurse = Services(OperatorOf(lab, ShippedRole(SystemRoles.Phlebotomist))))
            (await ToggleAsync(nurse, link.Id, false, "Automate en maintenance"))
                .FirstError.Code.Should().Be("forbidden");

        using var bench = Services(OperatorOf(lab, ShippedRole(SystemRoles.Technician)));

        (await ToggleAsync(bench, link.Id, false, "  "))
            .FirstError.ResourceKey.Should().Be("Interface_StateChangeReasonRequired");
        (await ToggleAsync(bench, link.Id, false, "Automate en maintenance préventive")).IsSuccess.Should().BeTrue();
        (await ToggleAsync(bench, unfinished.Id, true, "Mise en service"))
            .FirstError.ResourceKey.Should().Be("Interface_HostRequired");

        await using (var context = database.Context())
        {
            (await context.InstrumentInterfaces.AsNoTracking().SingleAsync(i => i.Id == link.Id)).IsEnabled.Should().BeFalse();
            (await context.InstrumentInterfaces.AsNoTracking().SingleAsync(i => i.Id == unfinished.Id)).IsEnabled.Should().BeFalse();

            // The next shift reads why the results stopped arriving.
            (await context.AuditEntries.AsNoTracking()
                    .Where(a => a.EntityName == nameof(InstrumentInterface) && a.EntityId == link.Id)
                    .Select(a => a.Reason).ToListAsync())
                .Should().Contain(r => r != null && r.Contains("désactivée") && r.Contains("maintenance préventive"));
        }

        (await ToggleAsync(bench, link.Id, true, "Maintenance terminée, CIQ conforme")).IsSuccess.Should().BeTrue();
        await using (var context = database.Context())
            (await context.InstrumentInterfaces.AsNoTracking().SingleAsync(i => i.Id == link.Id)).IsEnabled.Should().BeTrue();
    }

    // ---- 4. Who may do what, with the roles as shipped ------------------------------------------

    [SqlServerFact]
    public async Task D_ThePhlebotomistDrawsAndSends_TheTechnicianReceivesEntersAndChecks_OnlyTheBiologistReleases()
    {
        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));

        var order = await RegisterAsync(admin, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();

        using var nurse = Services(OperatorOf(lab, ShippedRole(SystemRoles.Phlebotomist)));
        var technicianUser = OperatorOf(lab, ShippedRole(SystemRoles.Technician));
        technicianUser.Qualification = ProfessionalQualification.LaboratoryTechnician;
        using var bench = Services(technicianUser);
        using var biologist = Services(OperatorOf(lab, ShippedRole(SystemRoles.Biologist)));

        // The phlebotomist: draws and sends; neither receives, refuses, shelves nor enters.
        (await On<SampleWorkflowService, Result>(nurse, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId, VolumeMl = 4m }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result>(nurse, s => s.DispatchAsync([tube.SampleId]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(nurse, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]))).FirstError.Code.Should().Be("forbidden");
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(nurse, s => s.RejectAsync(
                [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.Haemolysed, null, true)])))
            .FirstError.Code.Should().Be("forbidden");
        (await On<SampleWorkflowService, Result>(nurse, s => s.MoveToStorageAsync(tube.SampleId, "R1", null, null)))
            .FirstError.Code.Should().Be("forbidden");

        // The technician: receives, enters, checks; never releases nor issues.
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(bench, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]))).Value.Received.Should().Be(1);

        var result = (await ResultsAsync(order.OrderId)).Values.Single();

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(nurse,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(result.Id, "5,4")])))
            .FirstError.Code.Should().Be("forbidden");
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(bench,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(result.Id, "5,4")])))
            .Value.Accepted.Should().Be(1);
        (await On<ResultEntryService, Result>(bench, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();
        (await On<ValidationService, Result<ValidationOutcome>>(bench,
                s => s.ValidateAsync(order.OrderId, [new ValidationDecision(result.Id, true)])))
            .FirstError.Code.Should().Be("forbidden");
        (await On<ReportReleaseService, Result<ReleasedReport>>(bench, s => s.ReleaseAsync(order.OrderId)))
            .FirstError.Code.Should().Be("forbidden");

        // The biologist releases and issues; the report can then be handed over.
        (await On<ValidationService, Result<ValidationOutcome>>(biologist,
                s => s.ValidateAsync(order.OrderId, [new ValidationDecision(result.Id, true)])))
            .Value.Blocked.Should().BeEmpty();
        var report = await On<ReportReleaseService, Result<ReleasedReport>>(biologist, s => s.ReleaseAsync(order.OrderId));
        report.IsSuccess.Should().BeTrue(report.IsFailure ? report.FirstError.ResourceKey : null);

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(bench,
                s => s.CorrectReleasedAsync(order.OrderId, result.Id, "5,9", "Erreur de transcription constatée")))
            .FirstError.Code.Should().Be("forbidden");

        await using var context = database.Context();
        (await context.TestResults.AsNoTracking().SingleAsync(r => r.Id == result.Id))
            .Status.Should().Be(TestResultStatus.Reported);
    }

    [SqlServerFact]
    public async Task E_AFileWhoseValuesAreAllEntered_StaysOnTheEntryList_UntilItIsTechnicallyValidated()
    {
        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));
        var technicianUser = OperatorOf(lab, ShippedRole(SystemRoles.Technician));
        technicianUser.Qualification = ProfessionalQualification.LaboratoryTechnician;
        using var bench = Services(technicianUser);

        var order = await RegisterAsync(admin, lab.Patient, lab.Glucose);
        await DrawAndReceiveAsync(admin, order.Labels.Single().SampleId);
        var result = (await ResultsAsync(order.OrderId)).Values.Single();

        async Task<bool> ListedAsync() =>
            (await On<BenchWorksheetService, Result<IReadOnlyList<PendingEntryOrder>>>(bench,
                s => s.GetPendingOrdersAsync())).Value.Any(o => o.AccessionNumber == order.AccessionNumber);

        (await ListedAsync()).Should().BeTrue();

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(bench,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(result.Id, "5,4")])))
            .Value.Accepted.Should().Be(1);

        // Entered, not yet checked: still on the entry list, where the check is made.
        (await ListedAsync()).Should().BeTrue();

        (await On<ResultEntryService, Result>(bench, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        (await ListedAsync()).Should().BeFalse();
    }
}
