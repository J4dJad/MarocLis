using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database for the stock-planning queries, created from the model and dropped.</summary>
public sealed class AchatsBScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_achatsB_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public AchatsBScratchDatabase()
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
/// Purchasing lot B against SQL Server: opening and closing stock worked back from the lots and the
/// ledger (B2), the expiring lots (B5), the count sheet and its round trip (B6), the readings of a
/// storage unit (B7).
/// </summary>
public sealed class AchatsStockDatabaseTests(AchatsBScratchDatabase database) : IClassFixture<AchatsBScratchDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static DateTimeOffset At(DateOnly day, int hour)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [SqlServerFact]
    public async Task Consumption_data_works_the_opening_and_closing_back_from_the_lots_and_the_ledger()
    {
        var siteId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId)
        {
            UsedByAnalysisCodesCsv = Unique("NOPE"),
            TestsPerPack = 100
        };
        item.ApplyConsumptionPolicy(new StockConsumptionPolicy { DeadVolumePercent = 2m, SafetyStockDays = 5 })
            .IsSuccess.Should().BeTrue();
        var lot = item.AddLot(new ReagentLot("L1", Today.AddDays(200), 7m, Today.AddDays(-5)));
        item.AddLot(new ReagentLot("Q1", Today.AddDays(200), 0.5m, Today.AddDays(-5)));   // still in quarantine
        lot.Release(Today.AddDays(-5), user, "CQ conforme").IsSuccess.Should().BeTrue();

        // In the period (J−10 to J−1): 10 received, 3 issued. After it: 2 issued today. Before it: ignored.
        var moves = new[]
        {
            new StockMovement(item.Id, lot.Id, StockMovementKind.Receipt, 10m, At(Today.AddDays(-5), 9), user, siteId),
            new StockMovement(item.Id, lot.Id, StockMovementKind.Issue, 3m, At(Today.AddDays(-3), 9), user, siteId),
            new StockMovement(item.Id, lot.Id, StockMovementKind.Issue, 2m, At(Today, 9), user, siteId),
            new StockMovement(item.Id, lot.Id, StockMovementKind.Receipt, 50m, At(Today.AddDays(-40), 9), user, siteId)
        };

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.StockMovements.AddRange(moves);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var data = (await new StockPlanningQueries(read).GetConsumptionDataAsync(
            siteId, Today.AddDays(-10), Today.AddDays(-1), Today, null)).Single();

        // 7.5 there now; +10 −3 −2 since the start → 2.5 at the start; −2 after the end → 9.5 at the end.
        data.Opening.Should().Be(2.5m);
        data.Closing.Should().Be(9.5m);
        data.Receipts.Should().Be(10m);
        data.Issued.Should().Be(3m);
        data.Usable.Should().Be(7m);
        data.Policy.DeadVolumePercent.Should().Be(2m);
        data.Policy.SafetyStockDays.Should().Be(5);
        data.Activity.Should().ContainSingle().Which.PatientTests.Should().Be(0);
    }

    [SqlServerFact]
    public async Task Expiring_lots_stop_at_the_horizon_and_skip_empty_or_rejected_lots()
    {
        var siteId = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("CRP"), StockItemKind.Reagent, "coffret", siteId);
        item.AddLot(new ReagentLot("SOON", Today.AddDays(5), 2m, Today.AddDays(-30)));
        item.AddLot(new ReagentLot("LATER", Today.AddDays(60), 2m, Today.AddDays(-30)));
        var empty = item.AddLot(new ReagentLot("EMPTY", Today.AddDays(3), 1m, Today.AddDays(-30)));
        empty.ApplyCountVariance(-1m);

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var lots = await new StockPlanningQueries(read).GetExpiringLotsAsync(siteId, Today, 30);

        lots.Select(l => l.LotNumber).Should().Equal("SOON");
    }

    [SqlServerFact]
    public async Task Count_sheet_takes_the_lots_location_or_the_items_and_round_trips()
    {
        var siteId = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("Urée"), StockItemKind.Reagent, "coffret", siteId)
        {
            DefaultStorageLocation = "Frigo 1"
        };
        item.AddLot(new ReagentLot("A", Today.AddDays(90), 4m, Today.AddDays(-10)));
        item.AddLot(new ReagentLot("B", Today.AddDays(120), 2m, Today.AddDays(-10)) { StorageLocation = "Frigo 2" });

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            await context.SaveChangesAsync();
        }

        InventoryCount count;
        await using (var context = database.Context())
        {
            var queries = new InventoryCountQueries(context);
            var lots = await queries.GetCountableLotsAsync(siteId);
            lots.Select(l => (l.Lot.LotNumber, l.Lot.StorageLocation))
                .Should().BeEquivalentTo([("A", "Frigo 1"), ("B", "Frigo 2")]);
            (await queries.GetStorageLocationsAsync(siteId)).Should().Equal("Frigo 1", "Frigo 2");

            count = new InventoryCount(Unique("INV/"), siteId, Today, InventoryCountScope.All, Guid.NewGuid());
            count.SetScope(InventoryCountScope.StorageLocation, "frigo 2", null).IsSuccess.Should().BeTrue();
            count.StartCounting(lots.Where(l => count.Covers(l.Lot.StorageLocation, l.Kind)).Select(l => l.Lot),
                                At(Today, 8)).IsSuccess.Should().BeTrue();
            context.InventoryCounts.Add(count);
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
        {
            var tracked = await context.InventoryCounts.SingleAsync(c => c.Id == count.Id);
            tracked.RecordCount(tracked.Lines.Single().Id, 1m, Guid.NewGuid(), At(Today, 9), "cassé")
                .IsSuccess.Should().BeTrue();
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var detail = await new InventoryCountQueries(read).GetDetailAsync(count.Id);

        detail!.Lines.Should().ContainSingle();
        var line = detail.Lines.Single();
        line.LotNumber.Should().Be("B");
        line.ExpectedQuantity.Should().Be(2m);
        line.CountedQuantity.Should().Be(1m);
        line.Variance.Should().Be(-1m);
        detail.Count.WithVariance.Should().Be(1);

        var listed = (await new InventoryCountQueries(read).GetCountsAsync(siteId, false)).Single();
        listed.Lines.Should().Be(1);
        listed.Counted.Should().Be(1);
        listed.Status.Should().Be(InventoryCountStatus.Counting);
    }

    [SqlServerFact]
    public async Task Storage_units_list_todays_readings_and_the_last_one()
    {
        var siteId = Guid.NewGuid();
        var unit = new StorageUnit("Frigo " + Unique("U"), StorageUnitKind.Refrigerator, siteId);
        var user = Guid.NewGuid();
        var yesterday = new TemperatureReading(unit.Id, siteId, At(Today.AddDays(-1), 16), 4m, user);
        var morning = new TemperatureReading(unit.Id, siteId, At(Today, 8), 5m, user);
        var warm = new TemperatureReading(unit.Id, siteId, At(Today, 9), 6m, user)
        {
            PeriodMaximumCelsius = 10m, Comment = "Porte ouverte"
        };
        foreach (var r in new[] { yesterday, morning, warm }) r.Evaluate(unit);

        await using (var context = database.Context())
        {
            context.StorageUnits.Add(unit);
            context.TemperatureReadings.AddRange(yesterday, morning, warm);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new TemperatureQueries(read);
        var row = (await queries.GetUnitsAsync(siteId, Today, false)).Single();

        row.ReadingsToday.Should().Be(2);
        row.LastCelsius.Should().Be(6m);
        row.LastOutOfRange.Should().BeTrue();

        var month = await queries.GetReadingsAsync(unit.Id, Today.AddDays(-1), Today);
        month.Should().HaveCount(3);
        month.Count(r => r.IsOutOfRange).Should().Be(1);

        (await queries.NameExistsAsync(siteId, unit.Name, null)).Should().BeTrue();
        (await queries.NameExistsAsync(siteId, unit.Name, unit.Id)).Should().BeFalse();
    }
}
