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

/// <summary>A scratch database of its own for the defect-hunt cycle 5 (lot a), created from the model.</summary>
public sealed class Cycle5aScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle5a_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle5aScratchDatabase()
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
/// Cycle 5, lot a: the clinical circuit replayed against SQL Server — a file checked by two
/// technicians in turn, a refused tube followed up and abandoned, a released report reprinted.
/// </summary>
public sealed class Cycle5aWorkflowDatabaseTests(Cycle5aScratchDatabase database)
    : IClassFixture<Cycle5aScratchDatabase>
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
        public string Workstation => "PAILLASSE-C5A";
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
        // The analyser side (replay, preview) lives in MarocLIS.Instruments; saving a
        // mapping table never reaches it.
        services.AddSingleton(ReviewFixesBilling2Tests.Stub<IInstrumentResultReplayer>.Create());
        services.AddSingleton(ReviewFixesBilling2Tests.Stub<IInstrumentMessagePreviewer>.Create());
        services.AddSingleton(new InstrumentHostOptions());
        services.AddSingleton(ReviewFixesBilling2Tests.Stub<IInstrumentInterfaceQueries>.Create());
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
            ProfessionalOrderNumber = "CNOM-C5A-1"
        };

        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire cycle"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE CYCLE SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-C5A-1",
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

    // ---- 1. Technical validation by two technicians ----------------------------------------------

    [SqlServerFact]
    public async Task A_AFileCheckedAgainAfterALateValue_KeepsTheFirstTechniciansCheckOnTheValuesSheChecked()
    {
        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));

        var first = OperatorOf(lab, ShippedRole(SystemRoles.Technician));
        first.Qualification = ProfessionalQualification.LaboratoryTechnician;
        first.UserId = Guid.NewGuid();
        var second = OperatorOf(lab, ShippedRole(SystemRoles.Technician));
        second.Qualification = ProfessionalQualification.LaboratoryTechnician;
        second.UserId = Guid.NewGuid();
        using var morning = Services(first);
        using var evening = Services(second);

        var order = await RegisterAsync(admin, lab.Patient, lab.Glucose, lab.Comment);
        foreach (var label in order.Labels.DistinctBy(l => l.SampleId))
            await DrawAndReceiveAsync(admin, label.SampleId);

        var results = await ResultsAsync(order.OrderId);
        var glucose = results[lab.Glucose.Parameters.Single().Id];
        var aspect = results[lab.Comment.Parameters.Single().Id];

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(morning,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(glucose.Id, "5,4")])))
            .Value.Accepted.Should().Be(1);
        (await On<ResultEntryService, Result>(morning, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        var checkedInTheMorning = (await ResultsAsync(order.OrderId))[glucose.TestParameterId];
        checkedInTheMorning.TechnicallyValidatedByUserId.Should().Be(first.UserId);

        // The late value comes in and the whole file is checked again by somebody else.
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(evening,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(aspect.Id, "Clair")])))
            .Value.Accepted.Should().Be(1);
        (await On<ResultEntryService, Result>(evening, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        var after = await ResultsAsync(order.OrderId);
        after[glucose.TestParameterId].TechnicallyValidatedByUserId.Should().Be(first.UserId);
        after[glucose.TestParameterId].TechnicallyValidatedAt.Should().Be(checkedInTheMorning.TechnicallyValidatedAt);
        after[aspect.TestParameterId].Status.Should().Be(TestResultStatus.TechnicallyValidated);
        after[aspect.TestParameterId].TechnicallyValidatedByUserId.Should().Be(second.UserId);

        // Nothing is left to check: validating the file once more changes nobody's check.
        (await On<ResultEntryService, Result>(evening, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();
        (await ResultsAsync(order.OrderId))[glucose.TestParameterId].TechnicallyValidatedByUserId
            .Should().Be(first.UserId);
    }

    // ---- 2. A refused tube followed up, then abandoned -------------------------------------------

    [SqlServerFact]
    public async Task B_ARefusedTube_IsCalledBack_ThenAbandoned_ItsAnalysisCancelled_AndLeavesTheFollowUp()
    {
        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));

        var order = await RegisterAsync(admin, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single().SampleId;
        (await On<SampleWorkflowService, Result>(admin, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube, VolumeMl = 5m }]))).IsSuccess.Should().BeTrue();

        var refused = await On<SampleWorkflowService, Result<ReceptionOutcome>>(admin, s => s.RejectAsync(
            [new RejectSampleRequest(tube, SampleRejectionReason.Haemolysed, "Hémolyse franche", true)]));
        refused.Value.Rejected.Should().Be(1);
        var replacement = refused.Value.ReplacementLabels.Single().SampleId;

        async Task<RecollectionRow?> FollowUpAsync() =>
            (await On<RecollectionService, Result<IReadOnlyList<RecollectionRow>>>(admin, s => s.GetPendingAsync()))
                .Value.SingleOrDefault(r => r.ReplacementSampleId == replacement);

        (await FollowUpAsync()).Should().NotBeNull();
        (await FollowUpAsync())!.ContactCount.Should().Be(0);

        // A phlebotomist sees the list but neither records the call nor closes the case.
        using var phlebotomist = Services(OperatorOf(lab, ShippedRole(SystemRoles.Phlebotomist)));
        (await On<RecollectionService, Result>(phlebotomist,
                s => s.AbandonAsync(replacement, "Patient injoignable")))
            .FirstError.Code.Should().Be("forbidden");

        (await On<RecollectionService, Result>(admin,
                s => s.RecordContactAsync(replacement, RecallChannel.Phone, "  ")))
            .FirstError.ResourceKey.Should().Be("Recollection_ContactNoteRequired");
        (await On<RecollectionService, Result>(admin,
                s => s.RecordContactAsync(replacement, RecallChannel.Phone, "Pas de réponse, message laissé")))
            .IsSuccess.Should().BeTrue();

        var called = await FollowUpAsync();
        called!.ContactCount.Should().Be(1);
        called.LastContactDetail.Should().Contain("Pas de réponse");

        (await On<RecollectionService, Result>(admin, s => s.AbandonAsync(replacement, " ")))
            .FirstError.ResourceKey.Should().Be("Order_CancellationReasonRequired");
        (await On<RecollectionService, Result>(admin, s => s.AbandonAsync(replacement, "Patient injoignable")))
            .IsSuccess.Should().BeTrue();

        (await FollowUpAsync()).Should().BeNull();
        (await SampleAsync(replacement)).Status.Should().Be(SampleStatus.Discarded);

        await using (var context = database.Context())
        {
            var items = await context.LabOrderItems.AsNoTracking()
                                     .Where(i => i.LabOrderId == order.OrderId).ToListAsync();
            items.Should().OnlyContain(i => i.IsCancelled);
        }

        // Closed once: a second closing, or a call recorded afterwards, is refused.
        (await On<RecollectionService, Result>(admin, s => s.AbandonAsync(replacement, "Encore")))
            .FirstError.ResourceKey.Should().Be("Recollection_AlreadyDrawn");
        (await On<RecollectionService, Result>(admin,
                s => s.RecordContactAsync(replacement, RecallChannel.Sms, "Trop tard")))
            .FirstError.ResourceKey.Should().Be("Recollection_AlreadyDrawn");
    }

    // ---- 3. A released report reprinted ---------------------------------------------------------

    [SqlServerFact]
    public async Task C_AReleasedReport_IsReprintedAsIssued_ByTheBiologist_NotByTheBench_AndATamperedArchiveIsRefused()
    {
        var lab = await LabAsync();
        using var biologist = Services(OperatorOf(lab));

        var order = await RegisterAsync(biologist, lab.Patient, lab.Glucose);
        await DrawAndReceiveAsync(biologist, order.Labels.Single().SampleId);
        var result = (await ResultsAsync(order.OrderId)).Values.Single();
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(biologist,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(result.Id, "5,4")])))
            .Value.Accepted.Should().Be(1);
        await ReleaseValuesAsync(biologist, order.OrderId, [result.Id]);

        var released = await On<ReportReleaseService, Result<ReleasedReport>>(biologist, s => s.ReleaseAsync(order.OrderId));
        released.IsSuccess.Should().BeTrue(released.IsFailure ? released.FirstError.ResourceKey : null);

        var reprint = await On<ReportReleaseService, Result<byte[]>>(biologist,
            s => s.GetStoredPdfAsync(released.Value.ReportId));
        reprint.IsSuccess.Should().BeTrue();
        reprint.Value.Should().Equal(released.Value.Pdf);

        var bench = OperatorOf(lab, ShippedRole(SystemRoles.Technician));
        using var technician = Services(bench);
        (await On<ReportReleaseService, Result<byte[]>>(technician, s => s.GetStoredPdfAsync(released.Value.ReportId)))
            .FirstError.Code.Should().Be("forbidden");

        // An archive whose bytes no longer match the hash recorded at release is not handed out.
        await using (var context = database.Context())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE rpt.LabReports SET RenderedPdf = 0x25504446FF WHERE Id = {released.Value.ReportId}");
        }

        (await On<ReportReleaseService, Result<byte[]>>(biologist, s => s.GetStoredPdfAsync(released.Value.ReportId)))
            .FirstError.ResourceKey.Should().Be("Report_ArchiveIntegrityFailure");
    }

    // ---- 4. A COBAS INTEGRA table holds test numbers -----------------------------------------------

    [SqlServerFact]
    public async Task D_AnIntegraTable_TakesTestNumbersOnly_InEitherWriting_AndOneNumberOnce()
    {
        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));

        var instrument = new Instrument(Unique("I400"), new MultilingualText("cobas INTEGRA 400 plus"),
                                        EquipmentKind.Analyser, lab.Site.Id);
        var link = new InstrumentInterface(instrument.Id, lab.Site.Id, InstrumentProtocol.RocheIntegra,
                                           InstrumentTransportKind.Serial) { SerialPortName = "COM3" };

        await using (var context = database.Context())
        {
            context.Instruments.Add(instrument);
            context.InstrumentInterfaces.Add(link);
            await context.SaveChangesAsync();
        }

        var glucose = lab.Glucose.Parameters.Single().Id;
        var vitamin = lab.Vitamin.Parameters.Single().Id;

        Task<Result> SaveAsync(params MappingRequest[] rows) =>
            On<InstrumentInterfaceService, Result>(admin, s => s.SaveMappingsAsync(link.Id, rows));

        // Roche's test abbreviation is not what the analyser sends: refused, with the code named.
        var refused = await SaveAsync(new MappingRequest("GLUC3", glucose, null, null, null, null, true));
        refused.FirstError.ResourceKey.Should().Be("Interface_IntegraTestNumberInvalid");
        refused.FirstError.Args.Should().Contain("GLUC3");

        // « 031 » and « 0-031 » are one test: the second would overwrite the first.
        (await SaveAsync(new MappingRequest("031", glucose, null, null, null, null, true),
                         new MappingRequest("0-031", vitamin, null, null, null, null, true)))
            .FirstError.ResourceKey.Should().Be("Interface_DuplicateMappingCode");

        (await SaveAsync(new MappingRequest("0-031", glucose, null, null, null, null, true))).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            (await context.InstrumentTestMappings.AsNoTracking().Where(m => m.InstrumentInterfaceId == link.Id)
                          .Select(m => m.InstrumentTestCode).ToListAsync())
                .Should().Equal("0-031");
        }
    }
}
