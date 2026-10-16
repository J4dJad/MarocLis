using FluentAssertions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own, <c>MarocLIS_ResultEntryContext</c>, created from the model and
/// dropped at the end, foreign keys off — the arrangement of <see cref="AnterioriteScratchDatabase"/>.
/// </summary>
public sealed class ResultEntryContextScratchDatabase : IDisposable
{
    public const string Name = "MarocLIS_ResultEntryContext";

    public string ConnectionString { get; }

    private readonly bool _created;

    public ResultEntryContextScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        if (context.Database.CanConnect())
            throw new InvalidOperationException(
                $"Database {Name} already exists; the tests create and drop their own and will not touch an existing one.");

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
/// The entry screen's context queries: progress per dossier, sector names, and the sector and
/// origin of each line of a dossier.
/// </summary>
public sealed class ResultEntryContextDatabaseTests(ResultEntryContextScratchDatabase database)
    : IClassFixture<ResultEntryContextScratchDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.FromHours(1));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>
    /// One dossier with a haematology panel (two measured parameters, one calculated, one heading),
    /// a cancelled analysis, and a CRP keyed by hand; one value from an analyser that has since been
    /// deleted; and another site's dossier with the same parameters.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var siteId = Guid.NewGuid();
        var otherSite = Guid.NewGuid();

        var haematology = new Department(Unique("H"), new MultilingualText("Hématologie", "أمراض الدم"));
        var biochemistry = new Department(Unique("B"), new MultilingualText("Biochimie"));

        var analyser = new Instrument(Unique("I"), new MultilingualText("Hémato 01"), EquipmentKind.Analyser, siteId);

        var hb = new TestParameter(Unique("HB"), new MultilingualText("Hémoglobine"), ResultValueType.Numeric);
        var wbc = new TestParameter(Unique("GB"), new MultilingualText("Leucocytes"), ResultValueType.Numeric);
        var ccmh = new TestParameter(Unique("CC"), new MultilingualText("CCMH"), ResultValueType.Numeric)
        {
            CalculationFormula = "HB / HT * 100"
        };
        var heading = new TestParameter(Unique("T"), new MultilingualText("Formule"), ResultValueType.Heading);
        var crp = new TestParameter(Unique("CRP"), new MultilingualText("CRP"), ResultValueType.Numeric);
        var cancelled = new TestParameter(Unique("X"), new MultilingualText("Ferritine"), ResultValueType.Numeric);

        var order = new LabOrder(Unique("A"), Guid.NewGuid(), siteId, Now, Guid.NewGuid());
        var panel = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { DepartmentId = haematology.Id };
        var crpItem = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { DepartmentId = biochemistry.Id };
        var dropped = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { DepartmentId = biochemistry.Id };
        order.AddItem(panel);
        order.AddItem(crpItem);
        order.AddItem(dropped);
        order.CancelItem(dropped.Id, Now, Guid.NewGuid(), "Doublon").IsSuccess.Should().BeTrue();

        TestResult Result(LabOrder o, LabOrderItem item, TestParameter parameter, Guid department)
            => new(o.Id, item.Id, parameter.Id, o.SiteId) { DepartmentId = department };

        var hbResult = Result(order, panel, hb, haematology.Id);
        hbResult.EnterNumeric(hb, 14.6m, Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        hbResult.InstrumentId = analyser.Id;

        var wbcResult = Result(order, panel, wbc, haematology.Id);
        var ccmhResult = Result(order, panel, ccmh, haematology.Id);
        var headingResult = Result(order, panel, heading, haematology.Id);

        var crpResult = Result(order, crpItem, crp, biochemistry.Id);
        crpResult.MarkNotDone("Tube hémolysé", Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();

        var droppedResult = Result(order, dropped, cancelled, biochemistry.Id);

        // Same accession number shape, other site: never counted for this one.
        var theirs = new LabOrder(Unique("A"), Guid.NewGuid(), otherSite, Now, Guid.NewGuid());
        var theirItem = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { DepartmentId = haematology.Id };
        theirs.AddItem(theirItem);

        await using (var context = database.Context())
        {
            context.Departments.AddRange(haematology, biochemistry);
            context.Instruments.Add(analyser);
            context.TestParameters.AddRange(hb, wbc, ccmh, heading, crp, cancelled);
            context.LabOrders.AddRange(order, theirs);
            context.TestResults.AddRange(hbResult, wbcResult, ccmhResult, headingResult, crpResult,
                                         droppedResult, Result(theirs, theirItem, hb, haematology.Id));
            await context.SaveChangesAsync();

            // Deleted from the inventory since (the application deletes softly): still where the
            // haemoglobin came from.
            analyser.IsDeleted = true;
            analyser.DeletedAt = Now;
            await context.SaveChangesAsync();
        }

        return new Seeded(siteId, order, theirs, haematology, biochemistry, hbResult, wbcResult, crpResult);
    }

    private sealed record Seeded(Guid SiteId, LabOrder Order, LabOrder Theirs, Department Haematology,
                                 Department Biochemistry, TestResult Hb, TestResult Wbc, TestResult Crp);

    [SqlServerFact]
    public async Task Progress_CountsKeyedValues_WithoutCalculatedHeadingsOrCancelledAnalyses()
    {
        var seeded = await SeedAsync();

        await using var read = database.Context();
        var progress = await new ResultEntryContextQueries(read).GetProgressAsync(
            seeded.SiteId, [seeded.Order.AccessionNumber, seeded.Theirs.AccessionNumber, "INCONNU"]);

        // Hb (entered) and CRP (not done) against Hb, leucocytes and CRP: the CCMH is calculated,
        // the heading holds nothing, the ferritin was cancelled, and the other site's file is absent.
        progress.Should().ContainSingle()
            .Which.Should().Be(new Application.Results.ResultEntryProgress(seeded.Order.AccessionNumber, 2, 3));
    }

    [SqlServerFact]
    public async Task Sectors_AreNamed_InDisplayOrder_UnknownCodesAbsent()
    {
        var seeded = await SeedAsync();

        await using var read = database.Context();
        var sectors = await new ResultEntryContextQueries(read).GetSectorsAsync(
            [seeded.Biochemistry.Code, seeded.Haematology.Code, "ZZZ"]);

        sectors.Select(s => s.Code).Should().BeEquivalentTo([seeded.Biochemistry.Code, seeded.Haematology.Code]);
        sectors.Single(s => s.Code == seeded.Haematology.Code).Name.Fr.Should().Be("Hématologie");
    }

    [SqlServerFact]
    public async Task Origins_GiveEachLinesSector_AndTheAnalyserEvenOnceDeleted()
    {
        var seeded = await SeedAsync();

        await using var read = database.Context();
        var origins = (await new ResultEntryContextQueries(read).GetOriginsAsync(seeded.Order.Id))
            .ToDictionary(o => o.TestResultId);

        origins.Should().HaveCount(6);

        origins[seeded.Hb.Id].DepartmentCode.Should().Be(seeded.Haematology.Code);
        origins[seeded.Hb.Id].InstrumentName!.Fr.Should().Be("Hémato 01");

        // Keyed by hand: no analyser, the screen says « Manuel ».
        origins[seeded.Crp.Id].DepartmentName!.Fr.Should().Be("Biochimie");
        origins[seeded.Crp.Id].InstrumentCode.Should().BeNull();
        origins[seeded.Wbc.Id].InstrumentCode.Should().BeNull();
    }

    [SqlServerFact]
    public async Task Origins_OfAnOrderWithoutResults_AreEmpty()
    {
        await using var read = database.Context();
        var origins = await new ResultEntryContextQueries(read).GetOriginsAsync(Guid.NewGuid());

        origins.Should().BeEmpty();
    }
}
