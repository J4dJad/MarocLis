using System.Globalization;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Dashboard;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Statistics;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Tableau de bord », « Statistiques d'activité » and « Pilotage », moved out of the
/// workstation's views, now shared with the web; and what the steering and relations screens share
/// (PeriodReport).
/// </summary>
public sealed class PilotageScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture) + " DH";

    // ---- PeriodReport ------------------------------------------------------------------------------

    [Fact]
    public void The_period_chips_give_their_days_and_the_figures_are_written_as_the_screens_write_them()
    {
        PeriodReport.Range(PeriodPreset.ThisMonth, Today).Should().Be((new DateOnly(2026, 10, 1), Today));
        PeriodReport.Range(PeriodPreset.ThisYear, Today).Should().Be((new DateOnly(2026, 1, 1), Today));
        PeriodReport.Range(PeriodPreset.Custom, Today).Should().BeNull();
        PeriodReport.PresetKey(PeriodPreset.LastMonth).Should().Be("Statistics.PresetLastMonth");

        PeriodReport.Count(12_345, Fr).Should().Be(12_345.ToString("N0", Fr));
        PeriodReport.Share(42.46, Fr).Should().Be("42,5 %");
        PeriodReport.Share(null, Fr).Should().Be("—");
        PeriodReport.Change(4.2, Fr).Should().Be("+4,2 %");
        PeriodReport.Change(-3, Fr).Should().Be("-3,0 %");
        PeriodReport.Hours(12.5, Text, Fr).Should().Be("Statistics.HoursValue(12,5)", "the hour's unit is the language's");
        PeriodReport.Hours(null, Text, Fr).Should().Be("—");
        PeriodReport.Period(new DateOnly(2026, 10, 1), Today).Should().Be("01/10/2026 → 18/10/2026");
        PeriodReport.IsPeriodRefusal(Error.Validation("Stats_PeriodReversed")).Should().BeTrue();
        PeriodReport.IsPeriodRefusal(Error.Validation("Steering_AllocationOutOfRange")).Should().BeFalse();
    }

    [Fact]
    public void A_table_is_written_in_csv_with_its_cells_neutralised_and_an_empty_one_is_not()
    {
        var bytes = PeriodReport.Csv(["Libellé", "Montant"], [["=SOMME(A1)", "12,00 DH"]])!;
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("Libellé;Montant");
        lines[1].Should().NotStartWith("=", "a formula typed in a name is not run by the spreadsheet");
        PeriodReport.Csv(["Libellé"], []).Should().BeNull();
    }

    // ---- Statistiques d'activité -------------------------------------------------------------------

    private static ActivityStatistics Activity() => new(
        new DateOnly(2026, 10, 1), Today,
        new StatisticsSummary(120, 98, 410, 6, 380, 14.25, 87.5, 52_300m, 48_000m, 1_200m, 39_500m),
        [new DepartmentStatistic("BIO", new MultilingualText("Biochimie", "الكيمياء الحيوية", "Biochemistry"), 300, 290, 12.5, 91.2, 38_000m)],
        [new TestStatistic("GLY", new MultilingualText("Glycémie à jeun", "سكر الدم", "Fasting glucose"), 90, null, 2_700m)],
        [
            new PayerStatistic(new MultilingualText("CNOPS"), 40, 150, 22_000m, 17_600m, 4_400m),
            new PayerStatistic(null, 80, 260, 30_300m, 0m, 30_300m)
        ],
        [new DailyVolume(new DateOnly(2026, 10, 5), 14, 52)]);

    [Fact]
    public void The_statistics_are_computed_with_their_right_and_exported_once_computed()
    {
        StatisticsScreenRules.Actions(p => p == Permissions.Statistics.View, null).Should().Be(new StatisticsActions(true, false));
        StatisticsScreenRules.Actions(p => p == Permissions.Statistics.View, Activity()).Export.Should().BeTrue();
        StatisticsScreenRules.Actions(_ => false, Activity()).Should().Be(new StatisticsActions(false, false));
        StatisticsScreenRules.FieldOf(Error.Validation("Stats_PeriodTooLong", 366)).Should().Be(StatisticsScreenRules.PeriodField);
        StatisticsScreenRules.TabKey(StatisticsTab.ByPayer).Should().Be("Statistics.ByPayer");
    }

    [Fact]
    public void The_eight_key_figures_carry_the_cancelled_tests_and_the_share_on_time_under_their_figure()
    {
        // The turnaround's caption names the figure, its second part the line under it.
        string French(string key, params object?[] args) =>
            key == "Statistics.TurnaroundLine" ? "Délai réception → validation · dans les délais" : Text(key, args);

        var tiles = StatisticsScreenRules.Tiles(Activity(), French, Amount, Fr);

        tiles.Select(t => t.Key).Should().Equal("orders", "patients", "tests", "turnaround", "charged", "invoiced", "credited", "collected");
        tiles[2].Sub.Should().Be("Statistics.Cancelled 6");
        tiles[3].Should().Be(new ReportTile("turnaround", "Délai réception → validation", "Statistics.HoursValue(14,3)",
                                            "87,5 % dans les délais"));
        tiles[4].Value.Should().Be("52300.00 DH");
        tiles.Should().NotContain(t => t.Value.Contains("MAD"), "the currency is the screen's, not fixed in the text");
        tiles[3].TileText.Should().Be("Délai réception → validation\nStatistics.HoursValue(14,3) · 87,5 % dans les délais");
    }

    [Fact]
    public void Each_statistics_table_is_written_in_the_operators_language_and_as_csv()
    {
        var activity = Activity();

        var departments = StatisticsScreenRules.Rows(activity, StatisticsTab.ByDepartment, AppLanguage.Arabic, Text, Amount, Fr);
        departments.Single().Cells.Should().Equal("BIO", "الكيمياء الحيوية", "300", "290", "Statistics.HoursValue(12,5)", "91,2 %", "38000.00 DH");
        StatisticsScreenRules.Columns(StatisticsTab.ByDepartment).Should().HaveCount(departments.Single().Cells.Count);

        var tests = StatisticsScreenRules.Rows(activity, StatisticsTab.ByTest, AppLanguage.French, Text, Amount, Fr);
        tests.Single().Cells[3].Should().Be("—", "no test of the analysis was timed");

        var payers = StatisticsScreenRules.Rows(activity, StatisticsTab.ByPayer, AppLanguage.French, Text, Amount, Fr);
        payers[1].Cells[0].Should().Be("Statistics.SelfPaying");

        var days = StatisticsScreenRules.Rows(activity, StatisticsTab.ByDay, AppLanguage.French, Text, Amount, Fr);
        days.Single().Cells[0].Should().Be("lun. 05/10/26");
        StatisticsScreenRules.IsIdentifier("date").Should().BeTrue();

        StatisticsScreenRules.Subtitle(activity).Should().Be("01/10/2026 → 18/10/2026");
        StatisticsScreenRules.Subtitle(null).Should().BeEmpty();
        StatisticsScreenRules.FileName(activity, StatisticsTab.ByTest).Should().Be("statistiques-par-analyse-20261001-20261018.csv");

        var csv = Encoding.UTF8.GetString(StatisticsScreenRules.Csv(activity, StatisticsTab.ByPayer, AppLanguage.French, Text, Amount, Fr)!)
                          .TrimStart('﻿').Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        csv[0].Should().Be("Statistics.Payer;Statistics.Orders;Statistics.Tests;Statistics.Charged;Statistics.PayerShare;Statistics.PatientShare");
        csv.Should().HaveCount(3);
        StatisticsScreenRules.Csv(activity with { Days = [] }, StatisticsTab.ByDay, AppLanguage.French, Text, Amount, Fr).Should().BeNull();
    }

    // ---- Pilotage ----------------------------------------------------------------------------------

    private static SteeringDashboard Steering(PayrollFigures? payroll = null, decimal overdue = 8_400m) => new(
        new DateOnly(2026, 10, 1), Today, 2, 120, 410, 52_300m, 48_000m, 1_200m, 39_500m,
        new MarginTotals(410, 52_300m, 9_800m, 1_025m, 41_475m, 79.3, 3),
        25_000m, overdue,
        [
            new AgeingRow(AgeingBand.NotDue, 4, 16_600m),
            new AgeingRow(AgeingBand.Days31To60, 2, 3_000m),
            new AgeingRow(AgeingBand.Days61To90, 1, 5_400m),
            new AgeingRow(AgeingBand.Over180, 0, 0m)
        ],
        payroll,
        new QualityFigures(140, 6, 4.3, 3, 2, 4, 1, 0, 1),
        14.25, 87.5,
        [new RevenueRow("2026-10-05", "lun. 05/10", 14, 52, 6_400m, 12.2)],
        [new RevenueRow("CAS", "Casablanca", 100, 340, 44_000m, 84.1), new RevenueRow("", "", 20, 70, 8_300m, 15.9)],
        [new RevenueRow("", "", 80, 260, 30_300m, 57.9)],
        [new RevenueRow("", "", 30, 90, 10_000m, 19.1)]);

    [Fact]
    public void The_steering_figures_are_computed_with_their_right_and_exported_once_computed()
    {
        SteeringScreenRules.Actions(p => p == Permissions.Steering.View, null).Should().Be(new SteeringActions(true, false));
        SteeringScreenRules.Actions(p => p == Permissions.Steering.View, Steering()).Export.Should().BeTrue();
        SteeringScreenRules.Actions(p => p == Permissions.Statistics.View, Steering()).Should().Be(new SteeringActions(false, false));
        SteeringScreenRules.FieldOf(Error.Validation("Stats_PeriodReversed")).Should().Be(SteeringScreenRules.PeriodField);
    }

    [Fact]
    public void Without_the_salary_right_the_payroll_tile_is_not_shown_at_all_and_overdue_receivables_read_in_orange()
    {
        var tiles = SteeringScreenRules.Tiles(Steering(), Text, Amount, Fr);
        tiles.Select(t => t.Key).Should().Equal("revenue", "invoiced", "reagentCost", "margin", "overdue", "turnaround", "qc");
        tiles[0].Should().Be(new ReportTile("revenue", "Steering.Revenue", "52300.00 DH", "120 Steering.OrdersUnit"));
        tiles[1].Value.Should().Be("46800.00 DH / 39500.00 DH", "invoiced net of the credit notes, against collected");
        tiles[4].Ink.Should().Be(StockInk.Abnormal);
        tiles[6].Value.Should().Be("6 / 140");
        tiles[6].Sub.Should().Be("4,3 %");
        SteeringScreenRules.Tiles(Steering(overdue: 0m), Text, Amount, Fr)[4].Ink.Should().Be(StockInk.Plain);

        var withPayroll = SteeringScreenRules.Tiles(Steering(new PayrollFigures(2, 18_000m, 21_000m, 52_300m, 40.2)), Text, Amount, Fr);
        withPayroll.Should().HaveCount(8);
        withPayroll.Single(t => t.Key == "payroll").Should().Be(new ReportTile("payroll", "Steering.PayrollRatio", "21000.00 DH", "40,2 %"));

        var noMonth = SteeringScreenRules.Tiles(Steering(new PayrollFigures(0, 0m, 0m, 0m, null)), Text, Amount, Fr);
        noMonth.Single(t => t.Key == "payroll").Value.Should().Be("Steering.NoClosedPayroll");
    }

    [Fact]
    public void The_revenue_receivables_and_quality_tables_are_written_as_the_screen_shows_them()
    {
        var d = Steering();

        SteeringScreenRules.Rows(d, SteeringTab.BySite, Text, Amount, Fr)[1].Cells[0].Should().Be("Steering.NoLabel");
        SteeringScreenRules.Rows(d, SteeringTab.ByPayer, Text, Amount, Fr)[0].Cells[0].Should().Be("Statistics.SelfPaying");
        SteeringScreenRules.Rows(d, SteeringTab.ByPrescriber, Text, Amount, Fr)[0].Cells[0].Should().Be("Relations.NoPrescriber");
        SteeringScreenRules.Rows(d, SteeringTab.ByDay, Text, Amount, Fr)[0].Cells.Should().Equal("lun. 05/10", "14", "52", "6400.00 DH", "12,2 %");

        var ageing = SteeringScreenRules.Rows(d, SteeringTab.Receivables, Text, Amount, Fr);
        ageing.Select(r => r.Ink).Should().Equal(StockInk.Plain, StockInk.Plain, StockInk.Abnormal, StockInk.Plain);
        ageing[2].Cells.Should().Equal("AgeingBand.Days61To90", "1", "5400.00 DH");
        SteeringScreenRules.Columns(SteeringTab.Receivables).Select(c => c.CaptionKey)
            .Should().Equal("Steering.Column.band", "Steering.Column.invoices", "Steering.Column.owed");

        var quality = SteeringScreenRules.Rows(d, SteeringTab.Quality, Text, Amount, Fr);
        quality.Should().HaveCount(10);
        quality[1].Cells.Should().Equal("Steering.QcRejections", "6 (4,3 %)");
        quality[8].Cells[1].Should().Be("Statistics.HoursValue(14,3)");

        SteeringScreenRules.Subtitle(d, Text).Should().Be("01/10/2026 → 18/10/2026 · Steering.Sites(2)");
        SteeringScreenRules.FileName(d, SteeringTab.Receivables).Should().Be("pilotage-creances-20261001-20261018.csv");
        var csv = Encoding.UTF8.GetString(SteeringScreenRules.Csv(d, SteeringTab.Quality, Text, Amount, Fr)!).TrimStart('﻿');
        csv.Should().StartWith("Steering.Column.indicator;Steering.Column.value");
        SteeringScreenRules.Csv(d with { ByPrescriber = [] }, SteeringTab.ByPrescriber, Text, Amount, Fr).Should().BeNull();
    }

    // ---- Tableau de bord ---------------------------------------------------------------------------

    private static DashboardSnapshot Snapshot() => new(
        [
            new("Dashboard.OrdersToday", 42, DashboardSeverity.Neutral, "orders"),
            new("Dashboard.AwaitingCollection", 5, DashboardSeverity.Attention, "collection"),
            new("Dashboard.AwaitingReception", 3, DashboardSeverity.Attention, "reception"),
            new("Dashboard.AwaitingEntry", 7, DashboardSeverity.Attention, "entry"),
            new("Dashboard.AwaitingValidation", 12, DashboardSeverity.Attention, "validation"),
            new("Dashboard.ReportsToRelease", 4, DashboardSeverity.Attention, "reports"),
            new("Report.Plural", 0, DashboardSeverity.Neutral, "reports")
        ],
        [
            new("Dashboard.CriticalPending", 2, DashboardSeverity.Urgent, "validation"),
            new("Dashboard.Receivables", 9, DashboardSeverity.Attention, "billing"),
            new("Equipment.NextCalibration", 1, DashboardSeverity.Urgent, "equipment"),
            new("Inventory.ToReorder", 6, DashboardSeverity.Attention, "inventory"),
            new("SupplierInvoices.DashboardDueSoon", 3, DashboardSeverity.Attention, "supplierinvoices"),
            new("Dashboard.QcAlerts", 0, DashboardSeverity.Neutral, "qc"),
            new("Temperatures.DashboardMissing", 0, DashboardSeverity.Neutral, "temperatures")
        ],
        new DateTimeOffset(2026, 10, 18, 9, 41, 7, TimeSpan.FromHours(1)));

    [Fact]
    public void The_whole_laboratory_sees_the_five_steps_and_the_alerts_urgent_first_then_to_plan_handle_follow()
    {
        var board = DashboardScreenRules.Board(Snapshot(), new User(true, [.. Permissions.All]), Text, Fr);

        board.Steps.Select(s => s.StepKey).Should().Equal("Dashboard.StepCollection", "Dashboard.StepReception", "Dashboard.StepEntry",
                                                          "Dashboard.StepValidation", "Dashboard.StepReports");
        board.Steps.Single(s => s.Highlighted).Metric.LabelKey.Should().Be(DashboardScreenRules.HighlightedStep);
        board.Steps.Should().OnlyContain(s => s.Opens == s.Metric.NavigationKey);
        board.Summary.Should().ContainSingle().Which.Should().Be("Dashboard.OrdersRegisteredToday(42)", "no report issued today is not a line");

        board.Pending.Select(a => a.Metric.LabelKey).Should().Equal(
            "Dashboard.CriticalPending", "Equipment.NextCalibration", "SupplierInvoices.DashboardDueSoon", "Inventory.ToReorder",
            "Dashboard.Receivables");
        board.Pending.Select(a => a.Kind).Should().Equal(DashboardAlertKind.Urgent, DashboardAlertKind.Urgent, DashboardAlertKind.ToPlan,
                                                         DashboardAlertKind.ToHandle, DashboardAlertKind.ToFollow);
        board.Pending[0].PillKey.Should().Be("Dashboard.PillUrgent");
        board.Pending[3].ActionKey.Should().Be("Dashboard.ActionOrder");
        board.Pending[4].TextKey.Should().Be("Dashboard.ReceivablesLong");
        board.QuietKeys.Should().Equal("Dashboard.QcAlertsLong", "Temperatures.DashboardMissing");
        board.QuietHintKey.Should().Be("Dashboard.NothingToReportHint");
        board.ShowsFlow.Should().BeTrue();
    }

    [Fact]
    public void A_cashier_sees_only_the_figures_of_their_rights_and_no_way_to_a_screen_they_may_not_open()
    {
        var cashier = DashboardScreenRules.Board(Snapshot(), new User(true, Permissions.Billing.View), Text, Fr);

        cashier.Steps.Should().BeEmpty();
        cashier.Summary.Should().BeEmpty();
        cashier.ShowsFlow.Should().BeFalse();
        cashier.Pending.Should().ContainSingle().Which.Opens.Should().Be("billing");
        cashier.QuietKeys.Should().BeEmpty();
        cashier.QuietHintKey.Should().Be("Dashboard.NothingAtZero");

        // Who may read the results but not validate them sees the figure, without the way to the screen.
        var technician = DashboardScreenRules.Board(Snapshot(), new User(true, Permissions.Results.View, Permissions.Results.Enter), Text, Fr);
        technician.Steps.Select(s => (s.Metric.LabelKey, s.Opens)).Should().Equal(
            ("Dashboard.AwaitingEntry", "entry"), ("Dashboard.AwaitingValidation", null));
        technician.Pending.Single().Opens.Should().BeNull();
    }

    [Fact]
    public void The_banner_before_go_live_names_three_codes_and_offers_the_catalogue_only_to_whom_may_open_it()
    {
        DashboardScreenRules.Setup([], [], null, Text).Should().BeNull();

        var both = DashboardScreenRules.Setup(["GLY", "UREE", "CREA", "NA"], ["NFS"], new User(true, Permissions.Catalog.View), Text)!;
        both.Title.Should().Be("Dashboard.RangesPending(4)");
        both.Detail.Should().Be("Dashboard.AndOthers(GLY, UREE, CREA,1)  ·  Dashboard.TariffsPending(1)");
        both.ReviewKey.Should().Be("Dashboard.ReviewRanges");
        both.Review.Should().BeTrue();

        var tariffs = DashboardScreenRules.Setup([], ["NFS", "TSH"], new User(true, Permissions.Billing.View), Text)!;
        tariffs.Should().Be(new DashboardSetupBanner("Dashboard.TariffsPending(2)", "NFS, TSH", "Dashboard.OpenCatalog", false));
    }

    [Fact]
    public void The_line_under_the_greeting_gives_the_day_and_when_the_figures_were_read()
    {
        var snapshot = Snapshot();
        DashboardScreenRules.DateLine(snapshot.GeneratedAt, snapshot.GeneratedAt, false, Text, Fr)
            .Should().Be("Dimanche 18 octobre 2026 · Dashboard.RefreshedAt(09:41:07)");
        DashboardScreenRules.DateLine(snapshot.GeneratedAt, null, true, Text, Fr).Should().EndWith(" · Message.UnexpectedError");
        DashboardScreenRules.DateLine(snapshot.GeneratedAt, null, false, Text, Fr).Should().Be("Dimanche 18 octobre 2026");
    }
}
