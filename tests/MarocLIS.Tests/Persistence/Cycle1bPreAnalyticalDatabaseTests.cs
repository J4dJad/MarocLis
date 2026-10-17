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

/// <summary>A scratch database of its own for the defect-hunt cycle 1 (lot b), created from the model.</summary>
public sealed class Cycle1bScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle1b_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle1bScratchDatabase()
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
/// The pre-analytical and post-analytical circuit replayed end to end against SQL Server, with the
/// services as the desktop composes them: tube drawn, dispatched, received with a reservation and
/// shelved, aliquoted, its value entered (critical), checked, communicated, released, the report
/// issued, rectified and handed over; the refusal with recollection and the recall loop; the serum
/// bank's search and discard; and the rights of a phlebotomist, a technician and a biologist.
/// </summary>
public sealed class Cycle1bPreAnalyticalDatabaseTests(Cycle1bScratchDatabase database)
    : IClassFixture<Cycle1bScratchDatabase>
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
        public string Workstation => "PAILLASSE-C1B";
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

    private sealed record Lab(LabSite Site, AppUser Biologist, AnalysisTest Glucose, AnalysisTest Crp, Patient Patient);

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
        services.AddIsolatedQueries<IRecollectionQueries, RecollectionQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static LabSite Laboratory(Guid biologistId) => new(Unique("L"), new MultilingualText("Laboratoire cycle"),
                                                               LabSiteKind.MainLaboratory)
    {
        LegalName = "LABORATOIRE CYCLE SARL",
        Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
        FiscalId = "45128796",
        AddressLine = new MultilingualText("1, rue du Test"),
        City = "Rabat",
        HealthMinistryAuthorizationNumber = "AUT-C1B-1",
        ResponsibleBiologistUserId = biologistId
    };

    private static AnalysisTest Analysis(string code, string name, Department department, SpecimenType specimen,
                                         DateOnly validFrom, decimal? criticalHigh = null)
    {
        var analysis = new AnalysisTest(Unique(code), new MultilingualText(name), department.Id, specimen.Id)
        {
            RoutineTurnaroundHours = 4
        };

        var parameter = new TestParameter(Unique(code), new MultilingualText(name), ResultValueType.Numeric)
        {
            Decimals = 1,
            CriticalHigh = criticalHigh
        };
        parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
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
            ProfessionalOrderNumber = "CNOM-C1B-1"
        };

        var site = Laboratory(biologist.Id);
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var glucose = Analysis("GLY", "Glycémie", department, specimen, validFrom, criticalHigh: 500m);
        var crp = Analysis("CRP", "Protéine C réactive", department, specimen, validFrom);
        var patient = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1975, 3, 2), Gender.Male, site.Id)
        {
            MobilePhone = "0612345678",
            SearchKey = PatientSearchKey.Build("BENNANI", "Karim")
        };

        await using var context = database.Context();

        context.Users.Add(biologist);
        context.Sites.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.AddRange(glucose, crp);
        context.Patients.Add(patient);
        context.SampleStorageLocations.Add(SampleStorageLocation.Create(site.Id, "Réfrigérateur R1").Value);
        await context.SaveChangesAsync();

        return new Lab(site, biologist, glucose, crp, patient);
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

    private async Task<List<Guid>> ResultIdsAsync(Guid orderId)
    {
        await using var context = database.Context();
        return await context.TestResults.AsNoTracking().Where(r => r.LabOrderId == orderId)
                            .Select(r => r.Id).ToListAsync();
    }

    // ---- 1. Drawn to delivered ------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_TubeDrawn_ReceivedWithReservation_Shelved_Aliquoted_Entered_Validated_Reported_Amended_Delivered()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Should().ContainSingle().Subject;

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
                [new RecordCollectionRequest { SampleId = tube.SampleId, VolumeMl = 5m, PatientFasted = true }])))
            .IsSuccess.Should().BeTrue();

        (await On<SampleWorkflowService, Result>(provider, s => s.DispatchAsync([tube.SampleId])))
            .IsSuccess.Should().BeTrue();

        // A mistyped temperature is refused, and nothing of the reception is recorded.
        var mistyped = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId, TemperatureCelsius = 4000m }]));
        mistyped.Value.Received.Should().Be(0);
        mistyped.Value.Problems.Should().ContainSingle(e => e.ResourceKey == "Temperature_ValueImplausible");

        var places = await On<SampleWorkflowService, Result<IReadOnlyList<string>>>(provider, s => s.GetStorageLocationsAsync());
        places.Value.Should().Equal("Réfrigérateur R1");

        var received = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest
            {
                SampleId = tube.SampleId,
                ObservedDefects = SampleDefect.Haemolysed,
                AcceptWithReservation = true,
                TemperatureCelsius = 4.5m,
                StorageLocation = places.Value[0],
                StoragePosition = "A3"
            }]));
        received.Value.Received.Should().Be(1);
        received.Value.Problems.Should().BeEmpty();

        await using (var context = database.Context())
        {
            var stored = await context.Samples.AsNoTracking().Include(s => s.Events).SingleAsync(s => s.Id == tube.SampleId);
            stored.Status.Should().Be(SampleStatus.Stored);
            stored.IsAcceptedWithReservation.Should().BeTrue();
            stored.StorageLocation.Should().Be("Réfrigérateur R1");
            stored.ReceptionTemperatureCelsius.Should().Be(4.5m);
            stored.Events.Select(e => e.Kind).Should().Contain(
                [SampleEventKind.Collected, SampleEventKind.Dispatched, SampleEventKind.Received, SampleEventKind.Stored]);
        }

        // The aliquot's barcode is the next of the tubes' own series, never one already on a tube.
        var aliquot = await On<SampleWorkflowService, Result<string>>(provider,
            s => s.CreateAliquotAsync(tube.SampleId, 1m, "Sérothèque", null));
        aliquot.IsSuccess.Should().BeTrue(aliquot.IsFailure ? aliquot.FirstError.ResourceKey : null);

        await using (var context = database.Context())
        {
            (await context.Samples.AsNoTracking().AnyAsync(s => s.Barcode == aliquot.Value))
                .Should().BeFalse("an aliquot must never carry the barcode of a tube");
            long.Parse(aliquot.Value).Should().BeGreaterThan(long.Parse(tube.Barcode));
            (await context.SampleAliquots.AsNoTracking().SingleAsync(a => a.Barcode == aliquot.Value))
                .StorageLocation.Should().Be("Réfrigérateur R1", "the parent's place when none is chosen");
        }

        // A second order's tube comes after the aliquot in the same series.
        var next = await RegisterAsync(provider, lab.Patient, lab.Crp);
        long.Parse(next.Labels[0].Barcode).Should().BeGreaterThan(long.Parse(aliquot.Value));

        // A volume the column cannot hold is refused before anything is numbered.
        (await On<SampleWorkflowService, Result<string>>(provider,
                s => s.CreateAliquotAsync(tube.SampleId, 5_000_000m, null, null)))
            .FirstError.ResourceKey.Should().Be("Sample_AliquotVolumeInvalid");

        // Entry: a critical value.
        var resultId = (await ResultIdsAsync(order.OrderId)).Should().ContainSingle().Subject;
        var entered = await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "520")]));
        entered.Value.Accepted.Should().Be(1);
        entered.Value.CriticalFindings.Should().ContainSingle();

        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        // Not released before the call.
        var blocked = await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(order.OrderId, [new ValidationDecision(resultId, true)]));
        blocked.Value.Validated.Should().Be(0);
        blocked.Value.Blocked.Should().Contain(e => e.ResourceKey == "Result_CriticalMustBeCommunicated");

        DateTimeOffset? enteredAt;
        await using (var context = database.Context())
            enteredAt = (await context.TestResults.AsNoTracking().SingleAsync(r => r.Id == resultId)).EnteredAt;

        var raised = await On<ValidationService, Result<Guid>>(provider, s => s.RaiseCriticalNotificationAsync(
            resultId, "Glycémie", "520", NotificationChannel.Telephone, "0522000000", null, enteredAt));
        raised.IsSuccess.Should().BeTrue();
        (await On<ValidationService, Result>(provider, s => s.AcknowledgeCriticalNotificationAsync(
                raised.Value, "Dr Prescripteur", "Médecin traitant", true, "520")))
            .IsSuccess.Should().BeTrue();

        var validated = await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(order.OrderId, [new ValidationDecision(resultId, true)]));
        validated.Value.Blocked.Should().BeEmpty();
        validated.Value.OrderFullyValidated.Should().BeTrue();

        // Report v1, then a reissue without a reason is refused, then the rectification.
        var v1 = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId));
        v1.IsSuccess.Should().BeTrue(v1.IsFailure ? v1.FirstError.ResourceKey : null);
        v1.Value.VersionNumber.Should().Be(1);

        (await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(order.OrderId)))
            .FirstError.ResourceKey.Should().Be("Report_ReissueRequiresAmendmentReason");

        var v2 = await On<ReportReleaseService, Result<ReleasedReport>>(provider, s => s.ReleaseAsync(
            order.OrderId, amendmentKind: ReportAmendmentKind.IdentityCorrection,
            amendmentReason: new MultilingualText("Prénom du patient corrigé")));
        v2.IsSuccess.Should().BeTrue(v2.IsFailure ? v2.FirstError.ResourceKey : null);
        v2.Value.VersionNumber.Should().Be(2);

        var handed = await On<ReportReleaseService, Result<DeliveryOutcome>>(provider, s => s.RecordDeliveryAsync(
            v2.Value.ReportId, DeliveryChannel.PrintedAtCounter, ReportRecipientKind.Patient, "BENNANI Karim", null));
        handed.IsSuccess.Should().BeTrue(handed.IsFailure ? handed.FirstError.ResourceKey : null);

        await using (var context = database.Context())
        {
            var first = await context.LabReports.AsNoTracking().SingleAsync(r => r.Id == v1.Value.ReportId);
            first.SupersededByReportId.Should().Be(v2.Value.ReportId);
            (await context.ReportDeliveries.AsNoTracking().CountAsync(d => d.LabReportId == v2.Value.ReportId))
                .Should().Be(1);
            (await context.LabOrders.AsNoTracking().SingleAsync(o => o.Id == order.OrderId))
                .Status.Should().Be(LabOrderStatus.Reported);
        }
    }

    // ---- 2. Refusal, recollection and the recall loop -------------------------------------------

    [SqlServerFact]
    public async Task B_RejectedTube_WithRecollection_GoesOnTheRecallList_AndItsReplacementCarriesTheValues()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose, lab.Crp);
        var tube = order.Labels.Should().ContainSingle().Subject;

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId }]))).IsSuccess.Should().BeTrue();

        // A comment as long as the box allows, after the reason: the refusal is recorded whole.
        var comment = new string('c', 500);
        var refused = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.RejectAsync(
            [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.ContainerDamagedOrLeaked, comment, true)]));
        refused.IsSuccess.Should().BeTrue();
        refused.Value.Rejected.Should().Be(1);
        refused.Value.Problems.Should().BeEmpty();
        var replacement = refused.Value.ReplacementLabels.Should().ContainSingle().Subject;
        replacement.Barcode.Should().NotBe(tube.Barcode);

        var pending = await On<RecollectionService, Result<IReadOnlyList<RecollectionRow>>>(provider, s => s.GetPendingAsync());
        var row = pending.Value.Should().ContainSingle(r => r.LabOrderId == order.OrderId).Subject;
        row.ReplacementSampleId.Should().Be(replacement.SampleId);
        row.Phone.Should().Be("0612345678");

        (await On<RecollectionService, Result>(provider, s => s.RecordContactAsync(
            replacement.SampleId, RecallChannel.Phone, new string('n', 495)))).IsSuccess.Should().BeTrue();

        // The patient comes back: the replacement is drawn and received, the values entered on it.
        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = replacement.SampleId }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = replacement.SampleId }]))).Value.Received.Should().Be(1);

        (await On<RecollectionService, Result<IReadOnlyList<RecollectionRow>>>(provider, s => s.GetPendingAsync()))
            .Value.Should().NotContain(r => r.LabOrderId == order.OrderId);

        List<Guid> live;
        await using (var context = database.Context())
        {
            var items = await context.LabOrderItems.AsNoTracking().Where(i => i.LabOrderId == order.OrderId).ToListAsync();
            items.Where(i => i.Status == LabOrderItemStatus.Cancelled).Should().HaveCount(2);
            var reopened = items.Where(i => i.Status != LabOrderItemStatus.Cancelled).ToList();
            reopened.Should().HaveCount(2).And.OnlyContain(i => i.SampleId == replacement.SampleId);

            var ids = reopened.Select(i => i.Id).ToList();
            live = await context.TestResults.AsNoTracking().Where(r => ids.Contains(r.LabOrderItemId))
                                .Select(r => r.Id).ToListAsync();

            var rejected = await context.Samples.AsNoTracking().Include(s => s.Events).SingleAsync(s => s.Id == tube.SampleId);
            rejected.RejectionComment.Should().Be(comment);
            rejected.ReplacementSampleId.Should().Be(replacement.SampleId);
            rejected.Events.Single(e => e.Kind == SampleEventKind.Rejected).Detail!.Length.Should().Be(Sample.EventDetailMaxLength);
        }

        var entered = await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [.. live.Select(id => new ResultEntry(id, "5"))]));
        entered.Value.Accepted.Should().Be(2);
        entered.Value.Rejected.Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task C_AbandonedRecollection_CancelsTheAwaitedAnalyses_AndWithdrawsTheTube()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId }]))).IsSuccess.Should().BeTrue();
        var refused = await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.RejectAsync(
            [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.Clotted, null, true)]));
        var replacement = refused.Value.ReplacementLabels.Single();

        (await On<RecollectionService, Result>(provider, s => s.AbandonAsync(replacement.SampleId, "Patient injoignable")))
            .IsSuccess.Should().BeTrue();

        // Once withdrawn, a second abandon or a call on it is refused.
        (await On<RecollectionService, Result>(provider, s => s.AbandonAsync(replacement.SampleId, "Encore")))
            .FirstError.ResourceKey.Should().Be("Recollection_AlreadyDrawn");

        await using var context = database.Context();
        (await context.Samples.AsNoTracking().SingleAsync(s => s.Id == replacement.SampleId))
            .Status.Should().Be(SampleStatus.Discarded);
        (await context.LabOrderItems.AsNoTracking().Where(i => i.LabOrderId == order.OrderId).ToListAsync())
            .Should().OnlyContain(i => i.Status == LabOrderItemStatus.Cancelled);
    }

    // ---- 3. Serum bank ----------------------------------------------------------------------------

    [SqlServerFact]
    public async Task D_SerumBank_FindsATubeByThePatientsName_AndDiscardsItWithALongNote()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        var order = await RegisterAsync(provider, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();

        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]))).Value.Received.Should().Be(1);

        // The box says « code-barres, dossier, patient ou emplacement »: the name finds it, accents
        // and case aside.
        foreach (var term in new[] { "bennani", "BENNANI KARIM", "karim" })
        {
            var found = await On<SampleWorkflowService, Result<IReadOnlyList<StoredSampleRow>>>(provider,
                s => s.GetStoredSamplesAsync(term));
            found.Value.Should().Contain(r => r.SampleId == tube.SampleId, term);
        }

        (await On<SampleWorkflowService, Result<IReadOnlyList<StoredSampleRow>>>(provider,
                s => s.GetStoredSamplesAsync("zzz-introuvable")))
            .Value.Should().NotContain(r => r.SampleId == tube.SampleId);

        (await On<SampleWorkflowService, Result>(provider, s => s.MoveToStorageAsync(
            tube.SampleId, "Réfrigérateur R1", "B2", null))).IsSuccess.Should().BeTrue();

        // A past retention is refused when chosen (owner's decision): the tube's retention runs out
        // with time instead, written here as a tube kept from before would carry it.
        await using (var context = database.Context())
        {
            await context.Samples.Where(s => s.Id == tube.SampleId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.KeepUntil, DateOnly.FromDateTime(DateTime.Today).AddDays(-1)));
        }

        var expired = await On<SampleWorkflowService, Result<IReadOnlyList<StoredSampleRow>>>(provider,
            s => s.GetStoredSamplesAsync(onlyExpired: true));
        expired.Value.Should().ContainSingle(r => r.SampleId == tube.SampleId).Which.IsPastRetention.Should().BeTrue();

        // Not while its glucose still awaits a value (cycle 2): the file would wait for ever.
        (await On<SampleWorkflowService, Result>(provider, s => s.DiscardAsync(tube.SampleId, "Conservation dépassée")))
            .FirstError.ResourceKey.Should().Be("Sample_DiscardAnalysesPending");

        var resultId = (await ResultIdsAsync(order.OrderId)).Single();
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "5")]))).Value.Accepted.Should().Be(1);

        (await On<SampleWorkflowService, Result>(provider, s => s.DiscardAsync(tube.SampleId, new string('x', 500))))
            .IsSuccess.Should().BeTrue();

        (await On<SampleWorkflowService, Result<IReadOnlyList<StoredSampleRow>>>(provider,
                s => s.GetStoredSamplesAsync()))
            .Value.Should().NotContain(r => r.SampleId == tube.SampleId);
    }

    // ---- 4. Who may do what ------------------------------------------------------------------------

    private static readonly HashSet<string> Phlebotomist =
    [
        Permissions.Patients.View, Permissions.Patients.Create, Permissions.Orders.View,
        Permissions.Samples.View, Permissions.Samples.Collect, Permissions.Samples.Reprint,
        Permissions.Samples.Dispatch, Permissions.Catalog.View
    ];

    private static readonly HashSet<string> Technician =
    [
        Permissions.Patients.View, Permissions.Orders.View,
        Permissions.Samples.View, Permissions.Samples.Collect, Permissions.Samples.Receive,
        Permissions.Samples.Reject, Permissions.Samples.Reprint, Permissions.Samples.Dispatch,
        Permissions.Results.View, Permissions.Results.Enter, Permissions.Results.TechnicalValidate,
        Permissions.Reports.View
    ];

    [SqlServerFact]
    public async Task E_APhlebotomistDrawsButNeitherReceivesNorEnters_ATechnicianEntersButNeverReleases()
    {
        var lab = await LabAsync();
        using var admin = Services(OperatorOf(lab));
        using var nurse = Services(OperatorOf(lab, Phlebotomist));
        using var technician = Services(OperatorOf(lab, Technician));

        var order = await RegisterAsync(admin, lab.Patient, lab.Glucose);
        var tube = order.Labels.Single();

        (await On<SampleWorkflowService, Result>(nurse, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result>(nurse, s => s.DispatchAsync([tube.SampleId])))
            .IsSuccess.Should().BeTrue();

        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(nurse, s => s.ReceiveAsync(
                [new ReceiveSampleRequest { SampleId = tube.SampleId }])))
            .FirstError.Code.Should().Be("forbidden");
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(nurse, s => s.RejectAsync(
                [new RejectSampleRequest(tube.SampleId, SampleRejectionReason.Clotted, null, true)])))
            .FirstError.Code.Should().Be("forbidden");

        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(technician, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]))).Value.Received.Should().Be(1);

        (await On<SampleWorkflowService, Result<string>>(nurse, s => s.CreateAliquotAsync(tube.SampleId, null, null, null)))
            .FirstError.Code.Should().Be("forbidden");
        (await On<SampleWorkflowService, Result>(nurse, s => s.DiscardAsync(tube.SampleId, "Tube cassé")))
            .FirstError.Code.Should().Be("forbidden");

        var resultId = (await ResultIdsAsync(order.OrderId)).Single();

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(nurse,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "5")])))
            .FirstError.Code.Should().Be("forbidden");

        (await On<ResultEntryService, Result<ResultEntryOutcome>>(technician,
                s => s.SubmitAsync(order.OrderId, [new ResultEntry(resultId, "5")])))
            .Value.Accepted.Should().Be(1);
        (await On<ResultEntryService, Result>(technician, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        (await On<ValidationService, Result<ValidationOutcome>>(technician,
                s => s.ValidateAsync(order.OrderId, [new ValidationDecision(resultId, true)])))
            .FirstError.Code.Should().Be("forbidden");
        (await On<ReportReleaseService, Result<ReleasedReport>>(technician, s => s.ReleaseAsync(order.OrderId)))
            .FirstError.Code.Should().Be("forbidden");

        // The biologist releases.
        (await On<ValidationService, Result<ValidationOutcome>>(admin,
                s => s.ValidateAsync(order.OrderId, [new ValidationDecision(resultId, true)])))
            .Value.Validated.Should().Be(1);
    }

    // ---- 5. A repeated analysis -----------------------------------------------------------------

    [SqlServerFact]
    public async Task F_TheFirstRunOfARepeatedAnalysis_IsNotTechnicallyValidatedAgain_NorReleasedOneValueAtATime()
    {
        var lab = await LabAsync();
        using var provider = Services(OperatorOf(lab));

        // A two-parameter analysis: releasing one value does not complete it.
        var lipids = new AnalysisTest(Unique("LIP"), new MultilingualText("Bilan lipidique"),
                                      lab.Glucose.DepartmentId, lab.Glucose.SpecimenTypeId) { RoutineTurnaroundHours = 4 };
        foreach (var name in new[] { "Cholestérol", "Triglycérides" })
        {
            var parameter = new TestParameter(Unique("P"), new MultilingualText(name), ResultValueType.Numeric) { Decimals = 2 };
            parameter.AddReferenceRange(ReferenceRange.Numeric(0.1m, 10m));
            lipids.AddParameter(parameter);
        }
        lipids.AddTariff(new AnalysisTestTariff(lipids.Id, payerId: null, DateOnly.FromDateTime(DateTime.Today).AddYears(-1))
        {
            LetterKey = "B", Coefficient = 60m, LetterKeyValue = 1m, Price = 60m, IsVatExempt = true
        });
        await using (var context = database.Context())
        {
            context.AnalysisTests.Add(lipids);
            await context.SaveChangesAsync();
        }

        var order = await RegisterAsync(provider, lab.Patient, lipids);
        var tube = order.Labels.Single();
        (await On<SampleWorkflowService, Result>(provider, s => s.RecordCollectionAsync(
            [new RecordCollectionRequest { SampleId = tube.SampleId }]))).IsSuccess.Should().BeTrue();
        (await On<SampleWorkflowService, Result<ReceptionOutcome>>(provider, s => s.ReceiveAsync(
            [new ReceiveSampleRequest { SampleId = tube.SampleId }]))).Value.Received.Should().Be(1);

        var ids = await ResultIdsAsync(order.OrderId);
        ids.Should().HaveCount(2);
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [.. ids.Select(id => new ResultEntry(id, "9.9"))]))).Value.Accepted.Should().Be(2);
        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();

        Guid itemId;
        await using (var context = database.Context())
            itemId = (await context.LabOrderItems.AsNoTracking().SingleAsync(i => i.LabOrderId == order.OrderId)).Id;

        (await On<OrderAmendmentService, Result>(provider, s => s.RepeatItemAsync(order.OrderId, itemId, "Valeurs suspectes : nouvelle série")))
            .IsSuccess.Should().BeTrue();

        // The file's technical validation, as the entry screen's button asks for it: the set-aside
        // run is not checked again.
        await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, []));

        await using (var context = database.Context())
            (await context.TestResults.AsNoTracking().Where(r => r.LabOrderId == order.OrderId).ToListAsync())
                .Should().OnlyContain(r => r.Status == TestResultStatus.Entered);

        // And a biologist cannot release one of its values on its own.
        var released = await On<ValidationService, Result<ValidationOutcome>>(provider,
            s => s.ValidateAsync(order.OrderId, [new ValidationDecision(ids[0], true)]));
        released.Value.Validated.Should().Be(0);

        // The new run is entered: it can be checked and released as usual.
        (await On<ResultEntryService, Result<ResultEntryOutcome>>(provider,
            s => s.SubmitAsync(order.OrderId, [.. ids.Select(id => new ResultEntry(id, "5.5"))]))).Value.Accepted.Should().Be(2);
        (await On<ResultEntryService, Result>(provider, s => s.TechnicallyValidateAsync(order.OrderId, [])))
            .IsSuccess.Should().BeTrue();
        (await On<ValidationService, Result<ValidationOutcome>>(provider,
                s => s.ValidateAsync(order.OrderId, [.. ids.Select(id => new ValidationDecision(id, true))])))
            .Value.Validated.Should().Be(2);
    }
}
