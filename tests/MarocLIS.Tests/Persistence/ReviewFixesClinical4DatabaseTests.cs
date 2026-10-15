using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Patients;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own, created from the model and dropped at the end. The name carries a
/// random suffix, so two runs on the same server never meet. Foreign keys are switched off, as in
/// <see cref="Clinical3ScratchDatabase"/>.
/// </summary>
public sealed class Clinical4ScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_FixClinical4_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Clinical4ScratchDatabase()
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

    /// <summary>A context with no signed-in user: no site filter, as a maintenance routine reads.</summary>
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
/// The third clinical verification, against SQL Server and the services as the desktop composes
/// them — one context per screen, queries behind the isolating proxy: a stale screen (V1, V5), the
/// analyser's history lookup under the service account (V2), a merge and the duplicate's results
/// entered before it (V4), the batched history lookup (V6) and age bands to confirm (V7).
/// </summary>
public sealed class ReviewFixesClinical4DatabaseTests(Clinical4ScratchDatabase database)
    : IClassFixture<Clinical4ScratchDatabase>
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "biologiste";
        public string FullName => "Biologiste du site A";
        public Guid SiteId => SiteA;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteA];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "PAILLASSE-1";
    }

    /// <summary>The desktop's composition: application services, and queries behind the proxy.</summary>
    private ServiceProvider Services()
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
        services.AddSingleton<ICurrentUser, Operator>();
        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        return services.BuildServiceProvider();
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>An analysis with one potassium parameter, delta-checked at 20 % over 30 days.</summary>
    private async Task<(AnalysisTest Analysis, TestParameter Potassium)> CatalogueAsync()
    {
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("IONO"), new MultilingualText("Ionogramme"), department.Id, specimen.Id);

        var k = new TestParameter(Unique("K"), new MultilingualText("Potassium"), ResultValueType.Numeric)
        {
            DeltaCheckPercent = 20m,
            DeltaCheckWindowDays = 30
        };
        k.AddReferenceRange(ReferenceRange.Numeric(3.5m, 5.0m));
        analysis.AddParameter(k);

        await using var context = database.Context();
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.Add(analysis);
        await context.SaveChangesAsync();

        return (analysis, k);
    }

    private static Patient Patient(Guid siteId) =>
        new(Unique("P"), "ALAOUI", "Salma", new DateOnly(1980, 1, 1), Gender.Female, siteId);

    private static (LabOrder Order, TestResult Result) OrderWith(
        Patient patient, AnalysisTest analysis, TestParameter parameter, DateTimeOffset registeredAt)
    {
        var order = new LabOrder(Unique("A"), patient.Id, patient.SiteId, registeredAt, Guid.NewGuid());
        var item = new LabOrderItem(analysis.Id, OrderPriority.Routine);
        order.AddItem(item);
        return (order, new TestResult(order.Id, item.Id, parameter.Id, order.SiteId));
    }

    private static void Validate(TestResult result, TestParameter parameter, decimal value, DateTimeOffset at)
    {
        result.EnterNumeric(parameter, value, at, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        result.TechnicallyValidate(at, Guid.NewGuid());
        result.BiologicallyValidate(at, Guid.NewGuid(), false, true).IsSuccess.Should().BeTrue();
    }

    // ---- V1 and V5: a screen's context and a value re-run since it was loaded --------------------

    [SqlServerFact]
    public async Task V1_ACriticalCall_FromAScreenLoadedBeforeAnAnalyserRerun_IsRefused_AndReopeningShowsTheRerun()
    {
        var (analysis, k) = await CatalogueAsync();
        var patient = Patient(SiteA);
        var (order, result) = OrderWith(patient, analysis, k, Now.AddHours(-1));
        result.EnterNumeric(k, 7.2m, Now.AddMinutes(-30), Guid.NewGuid()).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.Patients.Add(patient);
            context.LabOrders.Add(order);
            context.TestResults.Add(result);
            await context.SaveChangesAsync();
        }

        using var provider = Services();
        using var screen = provider.CreateScope();
        var entry = screen.ServiceProvider.GetRequiredService<ResultEntryService>();
        var validation = screen.ServiceProvider.GetRequiredService<ValidationService>();

        // 10:00 — the biologist opens the file: K = 7.2, tracked by the screen's context.
        var shown = (await entry.OpenAsync(order.Id)).Value.Rows.Single();
        shown.NumericValue.Should().Be(7.2m);

        // 10:03 — the analyser's re-run is stored from the host's own scope.
        await using (var host = database.Context())
        {
            var row = await host.TestResults.SingleAsync(r => r.Id == result.Id);
            row.EnterNumeric(k, 2.1m, Now.AddMinutes(-1), Guid.NewGuid(), fromInstrument: true)
                .IsSuccess.Should().BeTrue();
            await host.SaveChangesAsync();
        }

        // 10:05 — "Communiquer" from the screen still showing 7.2.
        var call = await validation.RaiseCriticalNotificationAsync(
            shown.TestResultId, k.Code, "7,2 mmol/L", NotificationChannel.Telephone, null, null,
            shown.EnteredAt);

        call.IsFailure.Should().BeTrue("the value dictated is no longer the one stored");
        call.FirstError.ResourceKey.Should().Be("Notification_ValueChangedSinceDisplayed");

        // V5: re-opening the file on the same screen shows the re-run, and the call about it goes through.
        var reloaded = (await entry.OpenAsync(order.Id)).Value.Rows.Single();
        reloaded.NumericValue.Should().Be(2.1m);

        var current = await validation.RaiseCriticalNotificationAsync(
            reloaded.TestResultId, k.Code, "2,1 mmol/L", NotificationChannel.Telephone, null, null,
            reloaded.EnteredAt);

        current.IsSuccess.Should().BeTrue();
    }

    // ---- V2: the analyser's history lookup reads every site -------------------------------------

    [SqlServerFact]
    public async Task V2_HistoryLookupFromTheAnalyserScope_ReadsAnotherSitesHistory_WhoeverIsSignedIn()
    {
        var (analysis, k) = await CatalogueAsync();
        var patient = Patient(SiteB);
        var (past, history) = OrderWith(patient, analysis, k, Now.AddDays(-6));
        Validate(history, k, 4.0m, Now.AddDays(-5));

        await using (var context = database.Context())
        {
            context.Patients.Add(patient);
            context.LabOrders.Add(past);
            context.TestResults.Add(history);
            await context.SaveChangesAsync();
        }

        using var provider = Services();

        // A screen of the technician signed in to site A only does not see site B.
        using (var screen = provider.CreateScope())
        {
            var queries = screen.ServiceProvider.GetRequiredService<IResultQueries>();
            (await queries.GetPreviousValuesAsync(patient.Id, [k.Id], Now)).Should().BeEmpty();
        }

        // The analyser host's scope, as ScopedInstrumentMessageSink sets it up.
        using var host = provider.CreateScope();
        host.ServiceProvider.GetRequiredService<ScopeIdentity>().RunAs(
            new ServiceAccountUser(Guid.NewGuid(), "interface", "Interface automates"), ignoreSiteScope: true);

        var hostQueries = host.ServiceProvider.GetRequiredService<IResultQueries>();
        var previous = await hostQueries.GetPreviousValuesAsync(patient.Id, [k.Id], Now);

        previous.Should().ContainKey(k.Id, "the proxy's own scope works under the host's identity");
        previous[k.Id].Value.Should().Be(4.0m);
    }

    // ---- V4: merging re-interprets what was entered on the duplicate before -----------------------

    [SqlServerFact]
    public async Task V4_MergingATrueDuplicate_GivesItsPendingResultsTheSurvivorsHistory()
    {
        var (analysis, k) = await CatalogueAsync();
        var survivor = Patient(SiteA);
        var duplicate = Patient(SiteA);   // Same name, date of birth and sex.

        var (past, history) = OrderWith(survivor, analysis, k, Now.AddDays(-6));
        Validate(history, k, 4.0m, Now.AddDays(-5));

        // The blood count entered on the duplicate before anybody noticed: no history, no delta.
        var (today, pending) = OrderWith(duplicate, analysis, k, Now.AddHours(-2));
        pending.EnterNumeric(k, 6.5m, Now.AddHours(-1), Guid.NewGuid()).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.Patients.AddRange(survivor, duplicate);
            context.LabOrders.AddRange(past, today);
            context.TestResults.AddRange(history, pending);
            await context.SaveChangesAsync();
        }

        using var provider = Services();
        using (var screen = provider.CreateScope())
        {
            var registration = screen.ServiceProvider.GetRequiredService<PatientRegistrationService>();
            (await registration.MergeAsync(duplicate.Id, survivor.Id, "Doublon créé à l'accueil"))
                .IsSuccess.Should().BeTrue();
        }

        await using var read = database.Context();
        var stored = await read.TestResults.AsNoTracking().SingleAsync(r => r.Id == pending.Id);
        var merged = await read.Patients.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == duplicate.Id);

        merged.MergedIntoPatientId.Should().Be(survivor.Id);
        stored.PreviousNumericValue.Should().Be(4.0m);
        stored.Flags.Should().HaveFlag(ResultFlag.DeltaCheckFailed);
    }

    // ---- V6: one history lookup for many parameters --------------------------------------------

    [SqlServerFact]
    public async Task V6_TheBatchedHistoryLookup_ReturnsEachParametersLatestReleasedValue()
    {
        var (analysis, k) = await CatalogueAsync();
        var na = new TestParameter(Unique("NA"), new MultilingualText("Sodium"), ResultValueType.Numeric);
        var crp = new TestParameter(Unique("CRP"), new MultilingualText("CRP"), ResultValueType.Numeric);
        var patient = Patient(SiteA);

        var (older, olderK) = OrderWith(patient, analysis, k, Now.AddDays(-20));
        Validate(olderK, k, 3.9m, Now.AddDays(-20));
        var olderNa = new TestResult(older.Id, older.Items.Single().Id, na.Id, older.SiteId);
        Validate(olderNa, na, 140m, Now.AddDays(-20));

        var (recent, recentK) = OrderWith(patient, analysis, k, Now.AddDays(-3));
        Validate(recentK, k, 4.4m, Now.AddDays(-3));

        await using (var context = database.Context())
        {
            context.Patients.Add(patient);
            context.LabOrders.AddRange(older, recent);
            context.TestResults.AddRange(olderK, olderNa, recentK);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var history = await new ResultQueries(read).GetPreviousValuesAsync(patient.Id, [k.Id, na.Id, crp.Id], Now);

        history.Should().HaveCount(2);
        history[k.Id].Value.Should().Be(4.4m);
        history[k.Id].TestResultId.Should().Be(recentK.Id);
        history[na.Id].Value.Should().Be(140m);
        history.Should().NotContainKey(crp.Id);
    }

    // ---- V7: age bands saved without their unit ---------------------------------------------------

    [SqlServerFact]
    public async Task V7_TheCatalogue_CountsTheAgeBandsWhoseUnitIsToConfirm()
    {
        var department = new Department(Unique("D"), new MultilingualText("Hématologie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sang"));
        var analysis = new AnalysisTest(Unique("NFS"), new MultilingualText("NFS"), department.Id, specimen.Id);
        var hb = new TestParameter(Unique("HB"), new MultilingualText("Hb"), ResultValueType.Numeric);

        // Days or months? Unknown: to confirm.
        hb.AddReferenceRange(ReferenceRange.Numeric(14m, 22m, ageFromDays: 0, ageToDays: 30));
        // Saved with its unit since the upgrade.
        var typed = ReferenceRange.Numeric(10m, 14m, ageFromDays: 30, ageToDays: 180);
        typed.AgeUnitDays = 1;
        hb.AddReferenceRange(typed);
        // Only days fit these bounds; only years these: nothing to confirm.
        hb.AddReferenceRange(ReferenceRange.Numeric(13m, 20m, ageFromDays: 0, ageToDays: 14));
        hb.AddReferenceRange(ReferenceRange.Numeric(12m, 16m, ageFromDays: 0, ageToDays: 18250));
        // Withdrawn: no longer read.
        var withdrawn = ReferenceRange.Numeric(11m, 15m, ageFromDays: 0, ageToDays: 60);
        withdrawn.IsActive = false;
        hb.AddReferenceRange(withdrawn);
        analysis.AddParameter(hb);

        await using (var context = database.Context())
        {
            context.Departments.Add(department);
            context.SpecimenTypes.Add(specimen);
            context.AnalysisTests.Add(analysis);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new CatalogMaintenanceQueries(read);

        var parameter = (await queries.GetParametersAsync(analysis.Id)).Single();
        parameter.UnconfirmedAgeBandCount.Should().Be(1);

        var listed = (await queries.GetAnalysesAsync(analysis.Code, null, true, DateOnly.FromDateTime(Now.Date)))
            .Single(a => a.Id == analysis.Id);
        listed.UnconfirmedAgeBandCount.Should().Be(1);

        var ranges = await queries.GetRangesAsync(hb.Id);
        ranges.Where(r => r.HasUnconfirmedAgeUnit).Should().ContainSingle()
              .Which.AgeToDays.Should().Be(30);
    }
}
