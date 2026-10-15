using FluentAssertions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Inventory;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Purchasing lot B: theoretical consumption and its reconciliation (B1, B2), computed reorder
/// point and shelf-life cap (B3, B4), expiry and stock-out alerts (B5), periodic inventory counts
/// (B6) and temperature monitoring (B7).
/// </summary>
public sealed class AchatsStockConsumptionTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(1));

    // ---- B1: theoretical consumption -----------------------------------------------------------

    [Fact]
    public void Theoretical_consumption_counts_patients_qc_calibrations_reruns_and_dead_volume()
    {
        var policy = new StockConsumptionPolicy
        {
            TestsPerCalibration = 3,
            DeadVolumePercent = 5m,
            DeadVolumeTestsPerOpening = 2m,
            TestsPerResultCsv = "GLY=1; HBA1C=2"
        };

        var breakdown = TheoreticalConsumption.Compute(policy, 100,
            [new AnalysisActivity("GLY", 150, 20, 2, 5), new AnalysisActivity("HBA1C", 10, 0, 0, 0)]);

        breakdown.PatientTests.Should().Be(170m);          // 150 + 10 × 2
        breakdown.QcTests.Should().Be(20m);
        breakdown.CalibrationTests.Should().Be(6m);        // 2 × 3
        breakdown.RerunTests.Should().Be(5m);
        // 201 tests used → 3 packs of 100 opened, each losing 2 tests + 5 % of 100.
        breakdown.Openings.Should().Be(3m);
        breakdown.DeadVolumeTests.Should().Be(21m);
        breakdown.TotalTests.Should().Be(222m);
        breakdown.Units.Should().Be(2.22m);
    }

    [Fact]
    public void Theoretical_consumption_uses_the_recorded_openings_when_given()
    {
        var policy = new StockConsumptionPolicy { DeadVolumeTestsPerOpening = 1m };

        var breakdown = TheoreticalConsumption.Compute(policy, 50, [new AnalysisActivity("CRP", 40, 0, 0, 0)], 4m);

        breakdown.Openings.Should().Be(4m);
        breakdown.DeadVolumeTests.Should().Be(4m);
        breakdown.Units.Should().Be(0.88m);
    }

    [Fact]
    public void Consumption_factors_parse_and_reject_invalid_entries()
    {
        ConsumptionFactors.For("gly=2,UREE:1.5", "GLY").Should().Be(2m);
        ConsumptionFactors.For("gly=2,UREE:1.5", "UREE").Should().Be(1.5m);
        ConsumptionFactors.For("gly=2", "CRP").Should().Be(1m);

        ConsumptionFactors.Parse("GLY=abc").IsFailure.Should().BeTrue();
        ConsumptionFactors.Parse("GLY=0").FirstError.ResourceKey.Should().Be("Stock_ConsumptionFactorInvalid");
    }

    [Fact]
    public void Consumption_policy_is_validated_before_being_applied()
    {
        new StockConsumptionPolicy { DeadVolumePercent = 120m }.Validate().FirstError.ResourceKey
            .Should().Be("Stock_DeadVolumeOutOfRange");
        new StockConsumptionPolicy { TypicalShelfLifeDays = 0 }.Validate().FirstError.ResourceKey
            .Should().Be("Stock_PolicyValueOutOfRange");

        var item = new StockItem("GLY-R1", new("Glucose R1", "Glucose R1", "Glucose R1"), StockItemKind.Reagent,
                                 "coffret", Guid.NewGuid());
        item.ApplyConsumptionPolicy(new StockConsumptionPolicy { SafetyStockDays = -1 }).IsFailure.Should().BeTrue();
        item.SafetyStockDays.Should().BeNull();

        var policy = new StockConsumptionPolicy
        {
            DeadVolumePercent = 3m, SafetyStockDays = 10, TypicalShelfLifeDays = 180, ReorderPointComputed = true
        };
        item.ApplyConsumptionPolicy(policy).IsSuccess.Should().BeTrue();
        item.ConsumptionPolicy.Should().BeEquivalentTo(policy);
    }

    // ---- B2: theoretical against real ----------------------------------------------------------

    [Fact]
    public void Reconciliation_flags_a_variance_beyond_the_threshold()
    {
        // 10 there, 20 received, activity explains 15, 3 left: 12 went missing beyond the 15.
        var r = new StockReconciliation(10m, 20m, 15m, 3m, 10m);

        r.TheoreticalClosing.Should().Be(15m);
        r.RealConsumption.Should().Be(27m);
        r.Variance.Should().Be(-12m);
        r.VariancePercent.Should().Be(-80m);
        r.IsFlagged.Should().BeTrue();

        new StockReconciliation(10m, 20m, 15m, 14m, 10m).IsFlagged.Should().BeFalse();   // −1/15 = −6.7 %
        new StockReconciliation(10m, 0m, 0m, 8m, 10m).IsFlagged.Should().BeTrue();       // nothing explains it
        new StockReconciliation(10m, 0m, 0m, 8m, 10m, Computable: false).IsFlagged.Should().BeFalse();
    }

    [Fact]
    public void Calculator_row_and_average_follow_the_item_data()
    {
        var data = new ItemConsumptionData(
            Guid.NewGuid(), "GLY-R1", new("Glucose R1", "Glucose R1", "Glucose R1"), StockItemKind.Reagent,
            "coffret", 100, new StockConsumptionPolicy(), ["GLY"], [new AnalysisActivity("GLY", 900, 0, 0, 0)],
            Issued: 0m, Receipts: 10m, Opening: 5m, Closing: 6m, CountNumber: null, LeadTimeDays: 10,
            MinimumStock: 2m, ReorderPoint: 3m, Usable: 6m);

        var row = StockPlanningCalculator.Row(data, 10m);
        row.Breakdown.Units.Should().Be(9m);
        row.Reconciliation.Variance.Should().Be(0m);

        var average = StockPlanningCalculator.AverageDaily(data, 90);
        average!.Value.Daily.Should().Be(0.1m);
        average.Value.FromTheoretical.Should().BeTrue();
    }

    // ---- B3/B4: computed reorder point and the shelf-life cap ----------------------------------

    [Fact]
    public void Average_daily_consumption_prefers_real_issues()
    {
        ReorderPolicy.AverageDailyConsumption(45m, 90m, 90)!.Value.Should().Be((0.5m, false));
        ReorderPolicy.AverageDailyConsumption(0m, 90m, 90)!.Value.Should().Be((1m, true));
        ReorderPolicy.AverageDailyConsumption(0m, 0m, 90).Should().BeNull();
        ReorderPolicy.AverageDailyConsumption(10m, 0m, 0).Should().BeNull();
    }

    [Fact]
    public void Reorder_point_is_lead_time_consumption_plus_safety_stock_never_below_minimum()
    {
        var s = ReorderPolicy.Suggest(0.5m, false, 14, 10, 2m);
        s.SafetyStock.Should().Be(5m);
        s.ReorderPoint.Should().Be(12m);                    // ceil(0.5 × 14 + 5)

        ReorderPolicy.Suggest(0.5m, false, 14, null, 3m).ReorderPoint.Should().Be(10m);   // 7 + minimum 3
        ReorderPolicy.Suggest(0.01m, true, 5, 0, 4m).ReorderPoint.Should().Be(4m);
    }

    [Fact]
    public void Shelf_life_caps_the_quantity_to_what_is_used_before_expiry()
    {
        // 0.2/day over 60 days = 12, 4 already usable → at most 8.
        ReorderPolicy.CapByShelfLife(20m, 0.2m, 60, 4m).Should().Be((8m, (decimal?)8m));
        ReorderPolicy.CapByShelfLife(5m, 0.2m, 60, 4m).Should().Be((5m, (decimal?)null));
        ReorderPolicy.CapByShelfLife(20m, 0.01m, 30, 10m).Should().Be((1m, (decimal?)1m));
        ReorderPolicy.CapByShelfLife(20m, null, 60, 0m).Should().Be((20m, (decimal?)null));
        ReorderPolicy.CapByShelfLife(20m, 0.2m, null, 0m).Should().Be((20m, (decimal?)null));
    }

    // ---- B5: alerts ----------------------------------------------------------------------------

    [Fact]
    public void Expiry_alert_levels_follow_the_two_thresholds()
    {
        StockAlerts.ExpiryLevel(Today.AddDays(31), Today, 30, 7).Should().Be(ExpiryAlertLevel.None);
        StockAlerts.ExpiryLevel(Today.AddDays(30), Today, 30, 7).Should().Be(ExpiryAlertLevel.Warning);
        StockAlerts.ExpiryLevel(Today.AddDays(7), Today, 30, 7).Should().Be(ExpiryAlertLevel.Critical);
        StockAlerts.ExpiryLevel(Today, Today, 30, 7).Should().Be(ExpiryAlertLevel.Critical);
        StockAlerts.ExpiryLevel(Today.AddDays(-1), Today, 30, 7).Should().Be(ExpiryAlertLevel.Expired);
    }

    [Fact]
    public void Stock_out_is_predicted_from_the_usable_stock_and_the_lead_time()
    {
        var date = StockAlerts.PredictedStockOut(10m, 0.5m, Today);
        date.Should().Be(Today.AddDays(20));
        StockAlerts.RunsOutBeforeDelivery(date, Today, 21).Should().BeTrue();
        StockAlerts.RunsOutBeforeDelivery(date, Today, 20).Should().BeFalse();
        StockAlerts.PredictedStockOut(10m, 0m, Today).Should().BeNull();
        StockAlerts.RunsOutBeforeDelivery(null, Today, 30).Should().BeFalse();
    }

    [Fact]
    public void Stock_settings_read_back_and_refuse_inconsistent_thresholds()
    {
        var settings = StockSettings.FromValues(new Dictionary<string, string?>
        {
            [StockSettings.VarianceThresholdPercentKey] = "12,5",
            [StockSettings.ExpiryWarningDaysKey] = "45"
        });
        settings.VarianceThresholdPercent.Should().Be(12.5m);
        settings.ExpiryWarningDays.Should().Be(45);
        settings.ExpiryCriticalDays.Should().Be(7);
        settings.Validate().IsSuccess.Should().BeTrue();

        (StockSettings.Default with { ExpiryCriticalDays = 30 }).Validate().IsFailure.Should().BeTrue();
        (StockSettings.Default with { ConsumptionWindowDays = 3 }).Validate().IsFailure.Should().BeTrue();
    }

    // ---- B6: inventory counts ------------------------------------------------------------------

    private static InventoryCount StartedCount(out Guid itemId)
    {
        var count = new InventoryCount("INV/2026/0001", Guid.NewGuid(), Today, InventoryCountScope.All, Guid.NewGuid());
        itemId = Guid.NewGuid();
        count.StartCounting(
        [
            new InventoryCountLineInput(itemId, Guid.NewGuid(), "GLY-R1", "L2", Today.AddDays(90), "Frigo 1", 5m),
            new InventoryCountLineInput(itemId, Guid.NewGuid(), "GLY-R1", "L1", Today.AddDays(30), "Frigo 1", 3m)
        ], Now).IsSuccess.Should().BeTrue();
        return count;
    }

    [Fact]
    public void Count_scope_requires_its_location_or_category()
    {
        var count = new InventoryCount("INV/2026/0001", Guid.NewGuid(), Today, InventoryCountScope.All, Guid.NewGuid());

        count.SetScope(InventoryCountScope.StorageLocation, " ", null).IsFailure.Should().BeTrue();
        count.SetScope(InventoryCountScope.Category, null, null).IsFailure.Should().BeTrue();

        count.SetScope(InventoryCountScope.StorageLocation, " Frigo 1 ", null).IsSuccess.Should().BeTrue();
        count.Covers("frigo 1", StockItemKind.Consumable).Should().BeTrue();
        count.Covers("Frigo 2", StockItemKind.Reagent).Should().BeFalse();

        count.SetScope(InventoryCountScope.Category, null, StockItemKind.Reagent).IsSuccess.Should().BeTrue();
        count.Covers("Frigo 2", StockItemKind.Reagent).Should().BeTrue();
        count.Covers("Frigo 2", StockItemKind.Consumable).Should().BeFalse();
    }

    [Fact]
    public void Count_cannot_start_empty_and_orders_its_lines()
    {
        var empty = new InventoryCount("INV/2026/0002", Guid.NewGuid(), Today, InventoryCountScope.All, Guid.NewGuid());
        empty.StartCounting([], Now).FirstError.ResourceKey.Should().Be("InventoryCount_NothingToCount");

        var count = StartedCount(out _);
        count.Status.Should().Be(InventoryCountStatus.Counting);
        count.Lines.OrderBy(l => l.LineNumber).Select(l => l.LotNumber).Should().Equal("L1", "L2");
        count.Lines.Should().OnlyContain(l => l.InventoryCountId == count.Id);
        count.SetScope(InventoryCountScope.All, null, null).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Count_closes_only_when_every_line_is_counted()
    {
        var count = StartedCount(out _);
        var lines = count.Lines.OrderBy(l => l.LineNumber).ToList();
        var user = Guid.NewGuid();

        count.RecordCount(lines[0].Id, -1m, user, Now, null).IsFailure.Should().BeTrue();
        count.RecordCount(Guid.NewGuid(), 1m, user, Now, null).FirstError.Code.Should().Be("not_found");
        count.RecordCount(lines[0].Id, 2m, user, Now, "un coffret entamé").IsSuccess.Should().BeTrue();

        count.Close(user, Now).FirstError.ResourceKey.Should().Be("InventoryCount_LinesNotCounted");

        count.RecordCount(lines[1].Id, 6m, user, Now, null).IsSuccess.Should().BeTrue();
        lines[0].Variance.Should().Be(-1m);
        lines[1].Variance.Should().Be(1m);

        count.Close(user, Now).IsSuccess.Should().BeTrue();
        count.IsClosed.Should().BeTrue();
        count.AdjustmentReason.Should().Be("Inventaire INV/2026/0001");
        count.RecordCount(lines[0].Id, 3m, user, Now, null).IsFailure.Should().BeTrue();
        count.Cancel().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Count_variance_moves_the_lot_without_going_negative()
    {
        var lot = new ReagentLot("L1", Today.AddDays(30), 5m, Today);

        lot.ApplyCountVariance(-2m).Should().Be(-2m);
        lot.QuantityRemaining.Should().Be(3m);
        lot.ApplyCountVariance(-10m).Should().Be(-3m);
        lot.QuantityRemaining.Should().Be(0m);
        lot.ApplyCountVariance(1.5m).Should().Be(1.5m);
        lot.QuantityReceived.Should().Be(5m);
    }

    // ---- B7: temperatures ----------------------------------------------------------------------

    [Fact]
    public void Storage_units_take_their_default_range_and_validate()
    {
        StorageUnit.DefaultRange(StorageUnitKind.Refrigerator).Should().Be((2m, 8m));
        StorageUnit.DefaultRange(StorageUnitKind.Freezer20).Should().Be((-25m, -15m));

        var unit = new StorageUnit("Frigo réactifs", StorageUnitKind.Refrigerator, Guid.NewGuid());
        unit.MinimumCelsius.Should().Be(2m);
        unit.MaximumCelsius.Should().Be(8m);
        unit.Validate().IsSuccess.Should().BeTrue();

        unit.MinimumCelsius = 9m;
        unit.Validate().FirstError.ResourceKey.Should().Be("StorageUnit_RangeInvalid");
        new StorageUnit(" ", StorageUnitKind.Room, Guid.NewGuid()).Validate().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_reading_or_its_period_extremes_outside_the_range_is_an_excursion()
    {
        var unit = new StorageUnit("Frigo 1", StorageUnitKind.Refrigerator, Guid.NewGuid());

        new TemperatureReading(unit.Id, unit.SiteId, Now, 5m, Guid.NewGuid()).Evaluate(unit).Should().BeFalse();
        new TemperatureReading(unit.Id, unit.SiteId, Now, 9.5m, Guid.NewGuid()).Evaluate(unit).Should().BeTrue();
        new TemperatureReading(unit.Id, unit.SiteId, Now, 5m, Guid.NewGuid()) { PeriodMaximumCelsius = 11m }
            .Evaluate(unit).Should().BeTrue();
        new TemperatureReading(unit.Id, unit.SiteId, Now, 5m, Guid.NewGuid())
            { PeriodMinimumCelsius = 2m, PeriodMaximumCelsius = 8m }.Evaluate(unit).Should().BeFalse();
    }

    [Fact]
    public void Reading_input_is_checked_and_an_excursion_needs_a_comment()
    {
        TemperatureReading.ValidateInput(500m, null, null, false, null).FirstError.ResourceKey
            .Should().Be("Temperature_ValueImplausible");
        TemperatureReading.ValidateInput(5m, 8m, 3m, false, null).FirstError.ResourceKey
            .Should().Be("Temperature_MinAboveMax");
        TemperatureReading.ValidateInput(5m, 6m, 9m, false, null).FirstError.ResourceKey
            .Should().Be("Temperature_ReadingOutsideMinMax");
        TemperatureReading.ValidateInput(10m, null, null, true, " ").FirstError.ResourceKey
            .Should().Be("Temperature_CommentRequired");
        TemperatureReading.ValidateInput(10m, null, null, true, "Porte restée ouverte").IsSuccess.Should().BeTrue();
    }
}
