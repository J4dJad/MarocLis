using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Consommation », « Inventaires » and « Températures », moved out of the
/// workstation's views and dialogs, now shared with the web.
/// </summary>
public sealed class StockScreensRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    // ---- Consommation ------------------------------------------------------------------------------

    internal static ConsumptionRow Consumption(string code = "BIO-GLU", decimal realClosing = 2m, bool computable = true,
                                               decimal patients = 380m, decimal threshold = 10m) =>
        new(Guid.NewGuid(), code, new MultilingualText("Glucose GOD-PAP 4×100 mL", "غلوكوز"), StockItemKind.Reagent, "coffret",
            new ConsumptionBreakdown(patients, 40m, 10m, 12m, 8m, 3m, 100m),
            new StockReconciliation(4m, 6m, 4.5m, realClosing, threshold, computable), 8m, "INV-2026-0003",
            [new AnalysisActivity("GLU", 380, 40, 10, 12)]);

    [Fact]
    public void A_consumption_is_reviewed_for_last_month_and_a_line_beyond_the_threshold_is_flagged()
    {
        ConsumptionScreenRules.DefaultPeriod(Today).Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
        ConsumptionScreenRules.DefaultPeriod(new DateOnly(2026, 1, 5)).Should().Be((new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)));
        ConsumptionScreenRules.IsPeriodValid(Today, Today.AddDays(-1)).Should().BeFalse();
        ConsumptionScreenRules.Headers(Text).Should().HaveCount(13);

        var flagged = Consumption(realClosing: 0m);
        var sound = Consumption(realClosing: 5.5m);
        ConsumptionScreenRules.Ink(flagged).Should().Be(StockInk.Critical);
        ConsumptionScreenRules.Ink(sound).Should().Be(StockInk.Plain);
        ConsumptionScreenRules.Ink(Consumption(computable: false)).Should().Be(StockInk.Muted);
        ConsumptionScreenRules.Notice([flagged, sound], Text).Should().Be(("Consumption.Flagged(1)", true));
        ConsumptionScreenRules.Notice([sound], Text).Should().Be(("Consumption.Notice", false));
        ConsumptionScreenRules.Period(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)).Should().Be("01/09/2026 – 30/09/2026");
        ConsumptionScreenRules.FileName(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "pdf").Should().Be("consommation-20260901-20260930.pdf");
    }

    [Fact]
    public void The_breakdown_says_where_the_theoretical_consumption_comes_from()
    {
        var lines = ConsumptionScreenRules.DetailLines(Consumption(), Text, CultureInfo.InvariantCulture);
        lines.Should().HaveCount(8);
        lines[0].Should().Be(new ConsumptionDetailLine("Consumption.Patients", "380", "3.8", false));
        lines[4].Label.Should().Be("Consumption.DeadVolume(3,3)");
        lines[5].Should().Match<ConsumptionDetailLine>(l => l.Total && l.Tests == "450");
        lines[7].Units.Should().Contain("%");
        ConsumptionScreenRules.DetailNoticeKey(Consumption(computable: false)).Should().Be("Consumption.NotComputable");
    }

    [Fact]
    public void The_stock_settings_are_refused_on_the_box_out_of_range()
    {
        var form = ConsumptionScreenRules.Form(StockSettings.Default);
        ConsumptionScreenRules.Check(form).Should().BeNull();
        ConsumptionScreenRules.MayEditSettings(p => p == Permissions.Administration.ManageSettings).Should().BeTrue();
        ConsumptionScreenRules.MayEditSettings(_ => false).Should().BeFalse();

        form.ExpiryCriticalDays = 30;
        ConsumptionScreenRules.Check(form)!.Field.Should().Be(ConsumptionScreenRules.CriticalField, "J−7 comes after J−30");
        ConsumptionScreenRules.FieldOf(Error.Validation("Stock_SettingOutOfRange"), form).Should().Be(ConsumptionScreenRules.CriticalField);
        form.ExpiryCriticalDays = 7;
        form.ConsumptionWindowDays = 3;
        ConsumptionScreenRules.Check(form)!.Field.Should().Be(ConsumptionScreenRules.WindowField);
        form.ConsumptionWindowDays = 90;
        form.VarianceThresholdPercent = 120m;
        ConsumptionScreenRules.Check(form)!.Field.Should().Be(ConsumptionScreenRules.ThresholdField);
        form.VarianceThresholdPercent = 15m;
        ConsumptionScreenRules.ToSettings(StockSettings.Default, form).VarianceThresholdPercent.Should().Be(15m);
    }

    // ---- Inventaires -------------------------------------------------------------------------------

    internal static InventoryCountRow Count(string number = "INV-2026-0004", InventoryCountStatus status = InventoryCountStatus.Counting,
                                            InventoryCountScope scope = InventoryCountScope.StorageLocation, int lines = 3, int counted = 1,
                                            int variances = 1) =>
        new(Guid.NewGuid(), number, Today, scope, scope == InventoryCountScope.StorageLocation ? "Réfrigérateur 1" : null,
            scope == InventoryCountScope.Category ? StockItemKind.Reagent : null, status, lines, counted, variances,
            status == InventoryCountStatus.Closed ? new DateTimeOffset(2026, 10, 18, 17, 30, 0, TimeSpan.FromHours(1)) : null);

    internal static InventoryCountLineRow Line(int number = 1, decimal? counted = null, decimal? variance = null, string? comment = null,
                                               string lot = "GLU-2611") =>
        new(Guid.NewGuid(), number, Guid.NewGuid(), "BIO-GLU", "Glucose GOD-PAP 4×100 mL", "coffret", Guid.NewGuid(), lot,
            Today.AddDays(140), "Réfrigérateur 1", 4m, counted, variance, comment, counted is null ? null : 4m,
            counted is null ? null : new DateTimeOffset(2026, 10, 18, 9, 40, 0, TimeSpan.FromHours(1)));

    [Fact]
    public void Opening_and_closing_a_count_is_the_managers_and_counting_takes_the_reception_right()
    {
        var counting = Count();
        var detail = new InventoryCountDetail(counting, [Line()]);

        var manager = InventoryCountScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Inventory.CloseCounts), counting, detail);
        manager.Should().Be(new InventoryCountScreenActions(true, false, true, true, true, true, true));

        var technician = InventoryCountScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Inventory.Receive), counting, detail);
        technician.Should().Be(new InventoryCountScreenActions(false, false, false, false, true, true, true));

        var draft = Count(status: InventoryCountStatus.Draft);
        InventoryCountScreenRules.Actions(_ => true, draft, new InventoryCountDetail(draft, [])).Should()
            .Match<InventoryCountScreenActions>(a => a.Start && !a.Close && a.Cancel && !a.Sheet && !a.Count);
        var closed = Count(status: InventoryCountStatus.Closed);
        InventoryCountScreenRules.Actions(_ => true, closed, new InventoryCountDetail(closed, [Line(counted: 4m, variance: 0m)])).Should()
            .Match<InventoryCountScreenActions>(a => !a.Start && !a.Close && !a.Cancel && a.Variances && !a.Count);

        InventoryCountScreenRules.StatusTone(InventoryCountStatus.Counting).Should().Be(StockTone.Running);
        InventoryCountScreenRules.StatusTone(InventoryCountStatus.Closed).Should().Be(StockTone.Done);
        InventoryCountScreenRules.StatusTone(InventoryCountStatus.Cancelled).Should().Be(StockTone.Waiting);
    }

    [Fact]
    public void A_count_reads_its_scope_and_progress_and_a_line_its_variance()
    {
        InventoryCountScreenRules.Scope(Count(), Text).Should().Be("InventoryCountScope.StorageLocation : Réfrigérateur 1");
        InventoryCountScreenRules.Scope(Count(scope: InventoryCountScope.Category), Text).Should().Be("InventoryCountScope.Category : StockItemKind.Reagent");
        InventoryCountScreenRules.Scope(Count(scope: InventoryCountScope.All), Text).Should().Be("InventoryCountScope.All");
        InventoryCountScreenRules.Note(Count()).Should().Be("INV-2026-0004 · 1/3");
        InventoryCountScreenRules.CountingNotice(new InventoryCountDetail(Count(), []), Text).Should().Be("InventoryCount.CountingNotice InventoryCount.SheetTimeShort");
        InventoryCountScreenRules.CountingNotice(new InventoryCountDetail(Count(status: InventoryCountStatus.Closed), []), Text).Should().BeNull();
        InventoryCountScreenRules.LineInk(Line(counted: 3m, variance: -1m)).Should().Be(StockInk.Critical);
        InventoryCountScreenRules.LineInk(Line(counted: 4m, variance: 0m)).Should().Be(StockInk.Plain);
        InventoryCountScreenRules.Quantity(-1.5m, CultureInfo.InvariantCulture).Should().Be("-1.5");
    }

    [Fact]
    public void A_figure_is_saved_once_and_the_sheets_time_goes_with_a_lines_first_figure_only()
    {
        var line = Line();
        InventoryCountScreenRules.ReadCount(line.LineId, " ").Should().Be(((decimal?)null, (FieldRefusal?)null));
        InventoryCountScreenRules.ReadCount(line.LineId, "3,5").Counted.Should().Be(3.5m);
        InventoryCountScreenRules.ReadCount(line.LineId, "trois").Refusal.Should()
            .Be(new FieldRefusal(InventoryCountScreenRules.CountedField(line.LineId), "Result_NotANumber", "trois"));

        var counted = Line(counted: 3m, comment: "Boîte entamée");
        InventoryCountScreenRules.Changed(counted, 3m, "Boîte entamée").Should().BeFalse();
        InventoryCountScreenRules.Changed(counted, 3m, "Boîte écrasée").Should().BeTrue();

        var sheet = new DateTimeOffset(2026, 10, 18, 8, 15, 0, TimeSpan.FromHours(1));
        InventoryCountScreenRules.ShelfTime(line, sheet).Should().Be(sheet);
        InventoryCountScreenRules.ShelfTime(counted, sheet).Should().BeNull("a line counted earlier is not re-based");
        InventoryCountScreenRules.ShelfTime(line, null).Should().BeNull();
    }

    [Fact]
    public void The_sheet_leaves_the_expected_quantity_out_and_the_report_keeps_only_the_variances()
    {
        var detail = new InventoryCountDetail(Count(), [Line(1), Line(2, counted: 3m, variance: -1m, comment: "Casse"), Line(3, counted: 4m, variance: 0m)]);

        InventoryCountScreenRules.SheetHeaders(false, Text).Should().HaveCount(9).And.NotContain("InventoryCount.ExpectedAtStart");
        InventoryCountScreenRules.SheetHeaders(true, Text).Should().HaveCount(12);
        InventoryCountScreenRules.SheetRows(detail).Should().HaveCount(3).And.OnlyContain(r => r.Count == 9 && r[6] == string.Empty);
        var variances = InventoryCountScreenRules.VarianceRows(detail);
        variances.Should().ContainSingle();
        variances[0][10].Should().Be("-1");
        InventoryCountScreenRules.VarianceCaption(detail, 1, Text).Should().EndWith("InventoryCount.VarianceCaption(1,3)");
        InventoryCountScreenRules.SheetTitle(detail.Count, Text).Should().Be("InventoryCount.SheetTitle(INV-2026-0004,18/10/2026)");
        InventoryCountScreenRules.FileName(detail.Count, "feuille", "pdf").Should().Be("INV-2026-0004-feuille.pdf");
    }

    [Fact]
    public void A_count_is_dated_today_or_earlier_and_a_places_count_names_its_place()
    {
        var form = InventoryCountScreenRules.NewCount(Today);
        InventoryCountScreenRules.Check(form, Today).Should().BeNull();
        InventoryCountScreenRules.ScopeBoxes(InventoryCountScope.Category).Should().Be((false, true));

        form.CountDate = Today.AddDays(1);
        InventoryCountScreenRules.Check(form, Today)!.Field.Should().Be(InventoryCountScreenRules.DateField);
        form.CountDate = Today;
        form.Scope = InventoryCountScope.StorageLocation;
        InventoryCountScreenRules.Check(form, Today)!.Key.Should().Be("InventoryCount_LocationRequired");
        form.StorageLocation = " Chambre froide ";
        form.Notes = " ";
        var request = InventoryCountScreenRules.ToRequest(form);
        request.Should().Match<CreateInventoryCountRequest>(r => r.StorageLocation == "Chambre froide" && r.Category == null && r.Notes == null);
        form.Scope = InventoryCountScope.Category;
        InventoryCountScreenRules.ToRequest(form).Should().Match<CreateInventoryCountRequest>(r => r.StorageLocation == null && r.Category == StockItemKind.Reagent);
        InventoryCountScreenRules.FieldOf(Error.Validation("InventoryCount_DateInFuture")).Should().Be(InventoryCountScreenRules.DateField);
    }

    // ---- Températures ------------------------------------------------------------------------------

    internal static StorageUnitRow Unit(string name = "Réfrigérateur réactifs", bool active = true, int today = 1, int perDay = 2,
                                        bool outOfRange = false, decimal? last = 4.2m) =>
        new(Guid.NewGuid(), name, StorageUnitKind.Refrigerator, "Biochimie", "SN-4471", 2m, 8m, perDay, active, today, last,
            last is null ? null : new DateTimeOffset(2026, 10, 18, 8, 5, 0, TimeSpan.FromHours(1)), outOfRange);

    internal static TemperatureReadingRow Reading(decimal celsius = 4.2m, bool outOfRange = false, string? nc = null, string? comment = null) =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 10, 18, 8, 5, 0, TimeSpan.FromHours(1)), celsius, 3.1m, 5.0m, outOfRange,
            "Karim Tahiri", comment, nc);

    [Fact]
    public void Readings_go_only_to_a_unit_in_service_and_the_units_are_managed_by_right()
    {
        var recorder = TemperatureScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Inventory.RecordTemperatures), Unit());
        recorder.Should().Be(new TemperatureScreenActions(true, false, false, true));
        TemperatureScreenRules.Actions(_ => true, Unit(active: false)).Record.Should().BeFalse();
        TemperatureScreenRules.Actions(p => p == Permissions.Inventory.ManageStorageUnits, null).Should()
            .Be(new TemperatureScreenActions(false, true, false, false));
    }

    [Fact]
    public void A_unit_reads_its_range_todays_readings_and_its_state()
    {
        var unit = Unit(today: 1);
        TemperatureScreenRules.Range(unit, CultureInfo.InvariantCulture).Should().Be("2.0 °C – 8.0 °C");
        TemperatureScreenRules.Today(unit).Should().Be("1 / 2");
        TemperatureScreenRules.TodayInk(unit).Should().Be(StockInk.AbnormalBold, "a reading is still owed today");
        TemperatureScreenRules.TodayInk(Unit(today: 2)).Should().Be(StockInk.Plain);

        TemperatureScreenRules.StateKey(Unit(today: 0)).Should().Be("Temperatures.MissingToday");
        TemperatureScreenRules.StateTone(Unit(today: 0)).Should().Be(StockTone.Warning);
        TemperatureScreenRules.StateTone(Unit(outOfRange: true)).Should().Be(StockTone.Critical);
        TemperatureScreenRules.RowInk(Unit(outOfRange: true)).Should().Be(StockInk.Critical);
        TemperatureScreenRules.StateTone(Unit(active: false)).Should().Be(StockTone.Waiting);
        TemperatureScreenRules.RowInk(Unit(active: false)).Should().Be(StockInk.Muted);
        TemperatureScreenRules.MissingNotice([Unit(today: 0), Unit(), Unit(active: false, today: 0)], Text).Should().Be("Temperatures.MissingCount(1)");
        TemperatureScreenRules.MissingNotice([Unit()], Text).Should().BeNull();

        var cells = TemperatureScreenRules.ReadingCells(Reading(9.6m, true, "NC-2026-0012", "Porte restée ouverte"), Text, CultureInfo.InvariantCulture);
        cells.Should().HaveCount(8);
        cells[1].Should().Be("9.6 °C");
        cells[4].Should().Be("Temperatures.OutOfRange");
        cells[7].Should().Be("NC-2026-0012");
        TemperatureScreenRules.SheetCaption(unit, [Reading(), Reading(9.6m, true)], Text, CultureInfo.InvariantCulture)
            .Should().Be("Temperatures.SheetCaption(StorageUnitKind.Refrigerator,2.0 °C – 8.0 °C,Biochimie,2,1,3.1 °C,5.0 °C)");
        TemperatureScreenRules.SheetTitle(unit, 2026, 10, Text).Should().Be("Temperatures.SheetTitle(Réfrigérateur réactifs,10/2026)");
    }

    [Fact]
    public void An_excursion_asks_for_its_comment_and_a_value_is_refused_on_its_box()
    {
        var unit = Unit();

        TemperatureScreenRules.Check(new TemperatureReadingForm(), unit).Refusal!.Key.Should().Be("Temperature_ValueRequired");
        TemperatureScreenRules.Check(new TemperatureReadingForm { Value = "4,1", Minimum = "-25 C" }, unit).Refusal.Should()
            .Be(new FieldRefusal(TemperatureScreenRules.MinimumField, "Result_NotANumber", "-25 C"));

        var warm = new TemperatureReadingForm { Value = "9,6" };
        TemperatureScreenRules.IsExcursion(warm, unit).Should().BeTrue();
        TemperatureScreenRules.Check(warm, unit).Refusal!.Field.Should().Be(TemperatureScreenRules.CommentField);
        TemperatureScreenRules.IsExcursion(new TemperatureReadingForm { Value = "5", Maximum = "8,4" }, unit).Should().BeTrue("the period's maximum went beyond");

        warm.Comment = "  Porte restée ouverte — réactifs transférés  ";
        var (refusal, request) = TemperatureScreenRules.Check(warm, unit);
        refusal.Should().BeNull();
        request!.Should().Match<TemperatureReadingRequest>(r => r.Celsius == 9.6m && r.Comment == "Porte restée ouverte — réactifs transférés");

        TemperatureScreenRules.Excursion(new TemperatureReadingOutcome(true, "NC-2026-0012"), unit, Text)
            .Should().Be("Temperatures.ExcursionRecorded(Réfrigérateur réactifs,NC-2026-0012)");
        TemperatureScreenRules.Excursion(new TemperatureReadingOutcome(false, null), unit, Text).Should().BeNull();
        TemperatureScreenRules.ReadingFieldOf(Error.Validation("Temperature_ValueImplausible")).Should().Be(TemperatureScreenRules.ValueField);
    }

    [Fact]
    public void A_new_unit_takes_the_usual_range_of_its_kind_and_a_range_is_checked()
    {
        var form = TemperatureScreenRules.Form(null, CultureInfo.InvariantCulture);
        form.Should().Match<StorageUnitForm>(f => f.Minimum == "2" && f.Maximum == "8" && f.ReadingsPerDay == 2 && f.IsActive);
        form.Kind = StorageUnitKind.Freezer20;
        TemperatureScreenRules.KindChanged(form, isNew: true, CultureInfo.InvariantCulture);
        form.Should().Match<StorageUnitForm>(f => f.Minimum == "-25" && f.Maximum == "-15");

        var existing = TemperatureScreenRules.Form(Unit(), CultureInfo.InvariantCulture);
        existing.Kind = StorageUnitKind.Room;
        TemperatureScreenRules.KindChanged(existing, isNew: false, CultureInfo.InvariantCulture);
        existing.Minimum.Should().Be("2", "an existing unit keeps what was set");

        TemperatureScreenRules.Check(new StorageUnitForm { Minimum = "2", Maximum = "8" }, null).Refusal!.Field.Should().Be(TemperatureScreenRules.NameField);
        TemperatureScreenRules.Check(new StorageUnitForm { Name = "Congélateur", Minimum = "-15", Maximum = "-25" }, null).Refusal!.Key
            .Should().Be("StorageUnit_RangeInvalid");
        var (_, request) = TemperatureScreenRules.Check(new StorageUnitForm { Name = " Congélateur −20 ", Kind = StorageUnitKind.Freezer20,
                                                                            Minimum = "-25", Maximum = "-15", ReadingsPerDay = 40 }, null);
        request!.Should().Match<StorageUnitRequest>(r => r.Name == "Congélateur −20" && r.ReadingsPerDay == 24 && r.MinimumCelsius == -25m);
        TemperatureScreenRules.UnitFieldOf(Error.Conflict("StorageUnit_NameTaken")).Should().Be(TemperatureScreenRules.NameField);
    }

    [Fact]
    public void The_search_keeps_a_line_when_any_of_its_cells_contains_what_was_typed()
    {
        StockSearch.Matches(null, "BIO-GLU").Should().BeTrue();
        StockSearch.Matches("  ", "BIO-GLU").Should().BeTrue();
        StockSearch.Matches(" glu ", "BIO-GLU", null, "Glucose").Should().BeTrue();
        StockSearch.Matches("edta", "BIO-GLU", null, "Glucose").Should().BeFalse();
        StockSearch.Matches("réfrigérateur", "Réfrigérateur réactifs").Should().BeTrue();
    }
}
