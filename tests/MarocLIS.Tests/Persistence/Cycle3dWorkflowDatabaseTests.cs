using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
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
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the defect-hunt cycle 3 (lot d), created from the model.</summary>
public sealed class Cycle3dScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle3d_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle3dScratchDatabase()
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
/// Cycle 3, lot d: the circuit replayed again against SQL Server, deeper — a drawn volume or a value
/// the columns cannot hold, the send-out to a partner laboratory and its return, the delta check
/// against the patient's own history, and a released value corrected and re-issued.
/// </summary>
public sealed class Cycle3dWorkflowDatabaseTests(Cycle3dScratchDatabase database)
    : IClassFixture<Cycle3dScratchDatabase>
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
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds { get; set; } = [];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();

        /// <summary>Null grants everything; otherwise only these.</summary>
        public HashSet<string>? Granted { get; set; }

        public bool Has(string permission) => Granted is null || Granted.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "PAILLASSE-C3D";
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
            ProfessionalOrderNumber = "CNOM-C3D-1"
        };

        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire cycle"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE CYCLE SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-C3D-1",
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

    // ---- 1. Figures the columns cannot hold ------------------------------------------------------

    [SqlServerFact]
    public async Task A_ADrawnVolumeTheColumnCannotHold_IsRefused_AndTheTubeStaysToDraw()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();

        // 5 000 000 mL typed for 5 mL: the save failed on the column, « Erreur inattendue ».
        foreach (var volume in new[] { 5_000_000m, -2m, 0m })
        {
            var refused = await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
                [new RecordCollectionRequest { SampleId = tube.SampleId, VolumeMl = volume }]));
            refused.FirstError.ResourceKey.Should().Be("Sample_CollectedVolumeInvalid", volume.ToString());
        }

        await using (var context = database.Context())
            (await context.Samples.AsNoTracking().SingleAsync(s => s.Id == tube.SampleId))
                .Status.Should().Be(SampleStatus.AwaitingCollection);

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId, VolumeMl = 4.5m }]))).IsSuccess.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task B_AValueTheColumnCannotHold_IsRefusedWithItsReason_AndTheOtherValuesAreSaved()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose, lab.Comment);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId);

        var results = await ResultsAsync(order.OrderId);
        var glucose = results[lab.Glucose.Parameters.Single().Id];
        var aspect = results[lab.Comment.Parameters.Single().Id];

        // Thirteen digits typed (a barcode scanned into the value cell): the whole save failed.
        foreach (var typed in new[] { "1000000000000", "1e13", "-2000000000000" })
        {
            var entered = await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(glucose.Id, typed), new ResultEntry(aspect.Id, "Limpide")]));

            entered.IsSuccess.Should().BeTrue(typed);
            entered.Value.Rejected.Should().ContainSingle(e => e.ResourceKey == "Result_TechnicallyImpossible", typed);
        }

        // A text longer than its column is refused, not cut and not failing the save.
        var long_ = await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(aspect.Id, new string('a', 4001))]));
        long_.Value.Rejected.Should().ContainSingle(e => e.ResourceKey == "Result_TextTooLong");

        results = await ResultsAsync(order.OrderId);
        results[glucose.TestParameterId].NumericValue.Should().BeNull();
        results[aspect.TestParameterId].TextValue.Should().Be("Limpide");

        // The largest figure the column holds is still a value.
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(glucose.Id, "999999999999")])))
            .Value.Accepted.Should().Be(1);
    }

    // ---- 2. Subcontracting -------------------------------------------------------------------------

    [SqlServerFact]
    public async Task C_ASubcontractedAnalysis_LeavesOnASlip_ComesBack_AndIsReportedAsSubcontracted()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose, lab.Vitamin);
        var tube = order.Labels.Single();

        // Not received yet: nothing to send.
        (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider, s => s.GetWorklistAsync()))
            .Value.Should().NotContain(r => r.LabOrderId == order.OrderId);

        await DrawAndReceiveAsync(provider, tube.SampleId);

        var toSend = (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider, s => s.GetWorklistAsync()))
            .Value.Should().ContainSingle(r => r.LabOrderId == order.OrderId).Subject;
        toSend.Stage.Should().Be(SubcontractStage.ToSend);
        toSend.PartnerSiteId.Should().Be(lab.Partner.Id);

        var sent = await On<SubcontractingService, Result<IReadOnlyList<SubcontractDispatch>>>(provider,
            s => s.DispatchAsync([toSend.ItemId], null));
        sent.IsSuccess.Should().BeTrue(sent.IsFailure ? sent.FirstError.ResourceKey : null);
        var slip = sent.Value.Should().ContainSingle().Subject;

        // Sent once only.
        (await On<SubcontractingService, Result<IReadOnlyList<SubcontractDispatch>>>(provider,
                s => s.DispatchAsync([toSend.ItemId], null)))
            .FirstError.ResourceKey.Should().Be("Subcontract_AlreadySent");

        var printed = await On<SubcontractingService, Result<SubcontractSlip>>(provider, s => s.GetSlipAsync(slip.Number));
        printed.Value.Items.Should().ContainSingle(i => i.ItemId == toSend.ItemId);
        printed.Value.Partner.Id.Should().Be(lab.Partner.Id);

        (await On<SubcontractingService, Result>(provider, s => s.SetPartnerReferenceAsync(toSend.ItemId, new string('R', 61))))
            .FirstError.ResourceKey.Should().Be("Subcontract_ReferenceTooLong");
        (await On<SubcontractingService, Result>(provider, s => s.SetPartnerReferenceAsync(toSend.ItemId, " CASA-2026-0042 ")))
            .IsSuccess.Should().BeTrue();

        var awaiting = (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider, s => s.GetWorklistAsync()))
            .Value.Should().ContainSingle(r => r.ItemId == toSend.ItemId).Subject;
        awaiting.Stage.Should().Be(SubcontractStage.AwaitingReturn);
        awaiting.PartnerReference.Should().Be("CASA-2026-0042");

        await using (var context = database.Context())
            (await context.SampleEvents.AsNoTracking().Where(e => e.SampleId == tube.SampleId).Select(e => e.Kind).ToListAsync())
                .Should().Contain(SampleEventKind.SentToSubcontractor);

        // The partner's value is entered: it comes off the list, marked as measured elsewhere.
        var results = await ResultsAsync(order.OrderId);
        var vitamin = results[lab.Vitamin.Parameters.Single().Id];
        var glucose = results[lab.Glucose.Parameters.Single().Id];

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider, s => s.SubmitAsync(order.OrderId,
            [new ResultEntry(vitamin.Id, "32,5"), new ResultEntry(glucose.Id, "5,1")]))).Value.Accepted.Should().Be(2);

        (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider, s => s.GetWorklistAsync()))
            .Value.Should().NotContain(r => r.ItemId == toSend.ItemId);

        results = await ResultsAsync(order.OrderId);
        results[vitamin.TestParameterId].Flags.Should().HaveFlag(ResultFlag.Subcontracted);
        results[glucose.TestParameterId].Flags.Should().NotHaveFlag(ResultFlag.Subcontracted);

        await using (var context = database.Context())
            (await context.LabOrderItems.AsNoTracking().SingleAsync(i => i.Id == toSend.ItemId))
                .SubcontractReturnedAt.Should().NotBeNull();

        await ReleaseValuesAsync(provider, order.OrderId, [vitamin.Id, glucose.Id]);

        var report = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        report.IsSuccess.Should().BeTrue(report.IsFailure ? report.FirstError.ResourceKey : null);

        await using (var context = database.Context())
            (await context.LabReports.AsNoTracking().SingleAsync(r => r.Id == report.Value.ReportId))
                .ContainsSubcontractedResults.Should().BeTrue();
    }

    // ---- 3. Delta check, correction and rectification -------------------------------------------

    [SqlServerFact]
    public async Task D_ADeltaCheck_FiresOnTheJump_ClearsOnTheCorrectedValue_AndACorrectionGoesOutAsARectification()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        // The patient's history: 5,0 released last time.
        var first = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        await DrawAndReceiveAsync(provider, first.Labels.Single().SampleId);
        var before = (await ResultsAsync(first.OrderId)).Values.Single();
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(first.OrderId, [new ResultEntry(before.Id, "5")]))).Value.Accepted.Should().Be(1);
        await ReleaseValuesAsync(provider, first.OrderId, [before.Id]);

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        await DrawAndReceiveAsync(provider, order.Labels.Single().SampleId);
        var resultId = (await ResultsAsync(order.OrderId)).Values.Single().Id;

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "9")]))).Value.Accepted.Should().Be(1);

        var jumped = (await ResultsAsync(order.OrderId)).Values.Single();
        jumped.Flags.Should().HaveFlag(ResultFlag.DeltaCheckFailed);
        jumped.PreviousNumericValue.Should().Be(5m);

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "5,5")]))).Value.Accepted.Should().Be(1);
        (await ResultsAsync(order.OrderId)).Values.Single().Flags.Should().NotHaveFlag(ResultFlag.DeltaCheckFailed);

        await ReleaseValuesAsync(provider, order.OrderId, [resultId]);
        (await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId)))
            .IsSuccess.Should().BeTrue();

        // Printed, then corrected: a technician may not, a biologist may.
        var technician = new HashSet<string>
        {
            Permissions.Results.View, Permissions.Results.Enter, Permissions.Results.TechnicalValidate
        };
        using (var bench = Services(OperatorOf(lab, technician)))
            (await On<ResultEntryService, Result<ResultEntryOutcome>>(bench,
                    s => s.CorrectReleasedAsync(order.OrderId, resultId, "6", "Erreur de transcription constatée")))
                .FirstError.Code.Should().Be("forbidden");

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
                s => s.CorrectReleasedAsync(order.OrderId, resultId, "6", "Erreur de transcription constatée")))
            .IsSuccess.Should().BeTrue();

        await ReleaseValuesAsync(provider, order.OrderId, [resultId]);

        (await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId)))
            .FirstError.ResourceKey.Should().Be("Report_ReissueRequiresAmendmentReason");

        var v2 = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(
            order.OrderId, amendmentKind: ReportAmendmentKind.ResultCorrection,
            amendmentReason: new MultilingualText("Glycémie corrigée : erreur de transcription")));
        v2.IsSuccess.Should().BeTrue(v2.IsFailure ? v2.FirstError.ResourceKey : null);
        v2.Value.VersionNumber.Should().Be(2);

        await using var context = database.Context();
        (await context.LabReports.AsNoTracking().SingleAsync(r => r.Id == v2.Value.ReportId))
            .AmendedParameterCodesCsv.Should().Contain(lab.Glucose.Parameters.Single().Code);
        (await context.TestResults.AsNoTracking().SingleAsync(r => r.Id == resultId))
            .NumericValue.Should().Be(6m);
    }

    // ---- 4. A refused tube after the send-out, and a refused replacement ------------------------

    [SqlServerFact]
    public async Task E_ATubeRefusedAfterItsSendOut_GivesANewActToSend_AndARefusedReplacementIsRecalledInItsTurn()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Vitamin);
        var tube = order.Labels.Single();
        await DrawAndReceiveAsync(provider, tube.SampleId);

        var first = (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider, s => s.GetWorklistAsync()))
            .Value.Single(r => r.LabOrderId == order.OrderId);
        var slip = (await On<SubcontractingService, Result<IReadOnlyList<SubcontractDispatch>>>(provider,
            s => s.DispatchAsync([first.ItemId], null))).Value.Single();

        // The partner calls: the tube arrived broken. Refused, a new draw requested.
        var refused = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.RejectAsync(
            [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.ContainerDamagedOrLeaked, "Cassé chez le partenaire", true)]));
        var replacement = refused.Value.ReplacementLabels.Should().ContainSingle().Subject;

        var worklist = (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider, s => s.GetWorklistAsync()))
            .Value.Where(r => r.LabOrderId == order.OrderId).ToList();
        worklist.Should().NotContain(r => r.ItemId == first.ItemId, "the act sent is cancelled with its tube");

        // The slip as sent can still be printed.
        (await On<SubcontractingService, Result<SubcontractSlip>>(provider, s => s.GetSlipAsync(slip.Number)))
            .IsSuccess.Should().BeTrue();

        // The replacement is drawn, received, and its act is to send again, to the same partner.
        await DrawAndReceiveAsync(provider, replacement.SampleId);
        var again = (await On<SubcontractingService, Result<IReadOnlyList<SubcontractRow>>>(provider, s => s.GetWorklistAsync()))
            .Value.Should().ContainSingle(r => r.LabOrderId == order.OrderId).Subject;
        again.Stage.Should().Be(SubcontractStage.ToSend);
        again.PartnerSiteId.Should().Be(lab.Partner.Id);
        again.Barcode.Should().Be(replacement.Barcode);

        // That one is refused in its turn: the recall list shows the newest tube only.
        var second = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.RejectAsync(
            [new RejectSampleRequest(replacement.SampleId, SampleRejectionReason.Haemolysed, null, true)]));
        var third = second.Value.ReplacementLabels.Should().ContainSingle().Subject;

        var recall = (await On<RecollectionService, Result<IReadOnlyList<RecollectionRow>>>(provider, s => s.GetPendingAsync()))
            .Value.Where(r => r.LabOrderId == order.OrderId).ToList();
        recall.Should().ContainSingle().Which.ReplacementSampleId.Should().Be(third.SampleId);
    }
}
