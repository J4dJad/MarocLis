using FluentAssertions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the dashboard tests, created from the model.</summary>
public sealed class DashboardScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Dashboard_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public DashboardScratchDatabase()
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
/// The figures of the dashboard's « flux du jour » that the screens behind them list: tubes to
/// receive, files with values to type, files with reports to issue — and the catalogue checklist
/// of the pre-go-live banner, split by kind.
/// </summary>
public sealed class DashboardFlowDatabaseTests(DashboardScratchDatabase database)
    : IClassFixture<DashboardScratchDatabase>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(AnalysisTest Analysis, TestParameter Parameter, Guid SpecimenId)> CatalogueAsync(
        string? rangeSource = null)
    {
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("GLY"), new MultilingualText("Glycémie"), department.Id, specimen.Id);

        var parameter = new TestParameter(Unique("GLU"), new MultilingualText("Glucose"), ResultValueType.Numeric);
        parameter.AddReferenceRange(ReferenceRange.Numeric(0.70m, 1.10m, source: rangeSource));
        analysis.AddParameter(parameter);

        await using var context = database.Context();
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.Add(analysis);
        await context.SaveChangesAsync();

        return (analysis, parameter, specimen.Id);
    }

    [SqlServerFact]
    public async Task Flow_CountsTubesToReceive_FilesToEnter_AndReportsToIssue_ForTheSiteOnly()
    {
        var site = Guid.NewGuid();
        var otherSite = Guid.NewGuid();
        var user = Guid.NewGuid();
        var (analysis, glucose, specimen) = await CatalogueAsync();

        // A file at the bench: its tube received, its glucose still awaited.
        var atBench = new LabOrder(Unique("A"), Guid.NewGuid(), site, Now.AddHours(-2), user);
        var benchItem = new LabOrderItem(analysis.Id, OrderPriority.Routine);
        atBench.AddItem(benchItem);
        var benchTube = new Sample(Unique("T"), atBench.Id, specimen, site);
        benchTube.Collect(Now.AddHours(-2), user, null, null).IsSuccess.Should().BeTrue();
        benchTube.Receive(Now.AddHours(-1), user, SampleDefect.None, false, null, null).IsSuccess.Should().BeTrue();
        benchItem.SampleId = benchTube.Id;
        benchItem.MarkCollected(Now.AddHours(-2)).IsSuccess.Should().BeTrue();
        benchItem.MarkReceived(Now.AddHours(-1), null).IsSuccess.Should().BeTrue();
        var awaited = new TestResult(atBench.Id, benchItem.Id, glucose.Id, site);

        // A file whose tubes are on their way: one drawn, one in the transport box, one not drawn.
        var onTheWay = new LabOrder(Unique("A"), Guid.NewGuid(), site, Now.AddHours(-1), user);
        var drawn = new Sample(Unique("T"), onTheWay.Id, specimen, site);
        drawn.Collect(Now.AddMinutes(-50), user, null, null).IsSuccess.Should().BeTrue();
        var travelling = new Sample(Unique("T"), onTheWay.Id, specimen, site);
        travelling.Collect(Now.AddMinutes(-50), user, null, null).IsSuccess.Should().BeTrue();
        travelling.Dispatch(Now.AddMinutes(-40), user, null).IsSuccess.Should().BeTrue();
        var notDrawn = new Sample(Unique("T"), onTheWay.Id, specimen, site);

        // A file with a validated value, its report not issued yet.
        var validated = new LabOrder(Unique("A"), Guid.NewGuid(), site, Now.AddHours(-5), user);
        var validatedItem = new LabOrderItem(analysis.Id, OrderPriority.Routine);
        validated.AddItem(validatedItem);
        var final = new TestResult(validated.Id, validatedItem.Id, glucose.Id, site);
        final.EnterNumeric(glucose, 0.95m, Now.AddHours(-3), user).IsSuccess.Should().BeTrue();
        final.TechnicallyValidate(Now.AddHours(-3), user).IsSuccess.Should().BeTrue();
        final.BiologicallyValidate(Now.AddHours(-2), user, false, true).IsSuccess.Should().BeTrue();

        // Another site's drawn tube is not this site's work.
        var elsewhere = new LabOrder(Unique("A"), Guid.NewGuid(), otherSite, Now.AddHours(-1), user);
        var elsewhereTube = new Sample(Unique("T"), elsewhere.Id, specimen, otherSite);
        elsewhereTube.Collect(Now.AddMinutes(-30), user, null, null).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.LabOrders.AddRange(atBench, onTheWay, validated, elsewhere);
            context.Samples.AddRange(benchTube, drawn, travelling, notDrawn, elsewhereTube);
            context.TestResults.AddRange(awaited, final);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var snapshot = await new DashboardQueries(read).GetSnapshotAsync(site, Now);

        int Figure(string key) => snapshot.Activity.Single(m => m.LabelKey == key).Value;

        Figure("Dashboard.AwaitingCollection").Should().Be(1);
        Figure("Dashboard.AwaitingReception").Should().Be(2, "the drawn tube and the one in transit");
        Figure("Dashboard.AwaitingEntry").Should().Be(1, "one file holds a value to type");
        Figure("Dashboard.ReportsToRelease").Should().Be(1);

        // Each step leads to the screen that lists it.
        snapshot.Activity.Single(m => m.LabelKey == "Dashboard.AwaitingCollection").NavigationKey.Should().Be("collection");
        snapshot.Activity.Single(m => m.LabelKey == "Dashboard.AwaitingReception").NavigationKey.Should().Be("reception");
        snapshot.Activity.Single(m => m.LabelKey == "Dashboard.AwaitingEntry").NavigationKey.Should().Be("entry");
        snapshot.Activity.Single(m => m.LabelKey == "Dashboard.AwaitingValidation").NavigationKey.Should().Be("validation");
        snapshot.Activity.Single(m => m.LabelKey == "Dashboard.ReportsToRelease").NavigationKey.Should().Be("reports");

        // Every alert names the screen that deals with it.
        snapshot.Alerts.Should().OnlyContain(a => a.NavigationKey != null);
    }

    [SqlServerFact]
    public async Task SetupStatus_NamesTheParametersWhoseRangesAwaitTheLaboratorysCheck()
    {
        var (_, pending, _) = await CatalogueAsync("À vérifier par le laboratoire");
        var (_, checkedOne, _) = await CatalogueAsync("Notice du fournisseur, lot 2026");

        await using var context = database.Context();
        var queries = new CatalogQueries(context);

        var status = await queries.GetCatalogueSetupStatusAsync();
        status.UnverifiedRangeParameterCodes.Should().Contain(pending.Code).And.NotContain(checkedOne.Code);
        status.IsComplete.Should().BeFalse();

        // The older checklist reads the same codes.
        (await queries.GetUnverifiedCatalogueEntriesAsync())
            .Should().Contain($"Valeurs usuelles à valider : {pending.Code}");
    }
}
