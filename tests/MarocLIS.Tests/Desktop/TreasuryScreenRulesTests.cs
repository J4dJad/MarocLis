using System.Globalization;
using System.Text;
using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Steering;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Treasury;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Rapprochement bancaire », « Prévision de trésorerie » and « Coût de revient et
/// marge », moved out of the workstation's views and dialogs, now shared with the web.
/// </summary>
public sealed class TreasuryScreenRulesTests
{
    internal static readonly DateOnly Today = new(2026, 10, 18);

    internal const string ValidRib = "011780000012345678901296";

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture) + " DH";

    internal static BankAccountRow Account(string label = "Compte courant", string? mapping = null) =>
        new(Guid.NewGuid(), "BMCE Bank", label, ValidRib, "51410", true, 150_000m, new DateOnly(2026, 8, 31), 162_480.50m,
            new DateOnly(2026, 10, 15), 2, mapping);

    internal static StatementLineRow Line(decimal amount, string label = "REMISE CB 14/10", int matches = 0, decimal matched = 0m,
                                          bool automatic = false, params TreasuryMovementKind[] kinds) =>
        new(Guid.NewGuid(), new DateOnly(2026, 10, 14), null, label, null, amount, matched, matches, automatic, kinds,
            matches > 0 && matched != amount ? "Commission CMI" : null);

    internal static TreasuryMovement Movement(decimal amount, TreasuryMovementKind kind = TreasuryMovementKind.CardBatch,
                                              string key = "CB-20261014", string label = "12") =>
        new(kind, key, new DateOnly(2026, 10, 14), amount, label, null, false);

    internal static ReconciliationData Data(params StatementLineRow[] lines) =>
        new(Account(), lines, [Movement(4_812.40m), Movement(-12_000m, TreasuryMovementKind.SupplierPayment, "SP-1", "Bio-Distribution")],
            new ReconciliationSummary(150_000m, new DateOnly(2026, 8, 31), 162_480.50m, 158_100m, 4_380.50m, 1, -7_187.60m, 2));

    // ---- Rapprochement bancaire ----------------------------------------------------------------------

    [Fact]
    public void Every_action_needs_the_right_to_manage_and_follows_the_line_selected()
    {
        var open = Line(4_800m);
        var matched = Line(4_812.40m, matches: 1, matched: 4_812.40m, kinds: TreasuryMovementKind.CardBatch);
        var data = Data(open, matched);
        bool Manager(string p) => p == Permissions.Treasury.Manage;
        bool Reader(string p) => p == Permissions.Treasury.View;

        BankReconciliationScreenRules.Actions(Manager, data, open, 1)
            .Should().Be(new BankReconciliationActions(true, true, true, true, true, true, false, true));
        BankReconciliationScreenRules.Actions(Manager, data, open, 0).Match.Should().BeFalse("nothing is ticked to match it with");
        BankReconciliationScreenRules.Actions(Manager, data, matched, 1)
            .Should().Be(new BankReconciliationActions(true, true, true, true, false, false, true, true));
        BankReconciliationScreenRules.Actions(Manager, null, null, 0).AutoMatch.Should().BeFalse();
        BankReconciliationScreenRules.Actions(Reader, data, open, 1)
            .Should().Be(new BankReconciliationActions(false, false, false, false, false, false, false, false));
    }

    [Fact]
    public void The_screen_opens_on_last_month_and_keeps_its_account_and_its_line()
    {
        BankReconciliationScreenRules.DefaultPeriod(Today).Should().Be((new DateOnly(2026, 9, 1), Today));
        BankReconciliationScreenRules.DefaultPeriod(new DateOnly(2026, 1, 5)).From.Should().Be(new DateOnly(2025, 12, 1));

        var (a, b) = (Account("A"), Account("B"));
        BankReconciliationScreenRules.Keep([a, b], b.Id).Should().Be(b);
        BankReconciliationScreenRules.Keep([a, b], Guid.NewGuid()).Should().Be(a);
        BankReconciliationScreenRules.Keep([], null).Should().BeNull();

        var (first, second, third) = (Line(1m), Line(2m), Line(3m));
        BankReconciliationScreenRules.KeepLine([first, second, third], second.Id, 1).Should().Be(second);
        BankReconciliationScreenRules.KeepLine([first, third], second.Id, 1).Should().Be(third, "the line matched left « open only »: the next one takes its place");
        BankReconciliationScreenRules.KeepLine([first], second.Id, 5).Should().Be(first);
        BankReconciliationScreenRules.KeepLine([first], null, 0).Should().BeNull();
    }

    [Fact]
    public void Open_only_keeps_the_lines_to_reconcile_and_counts_them_on_its_chip()
    {
        var open = Line(4_800m);
        var matched = Line(4_812.40m, matches: 1, matched: 4_812.40m, kinds: TreasuryMovementKind.CardBatch);
        var data = Data(open, matched);

        BankReconciliationScreenRules.Visible(data, openOnly: true).Should().Equal(open);
        BankReconciliationScreenRules.Visible(data, openOnly: false).Should().Equal(open, matched);
        BankReconciliationScreenRules.Visible(null, true).Should().BeEmpty();
        BankReconciliationScreenRules.OpenOnlyChip(data, Text).Should().Be("Treasury.OpenOnly · 1");
        BankReconciliationScreenRules.OpenOnlyChip(null, Text).Should().Be("Treasury.OpenOnly");
    }

    [Fact]
    public void A_line_says_what_it_was_matched_with_and_a_difference_in_its_ink()
    {
        BankReconciliationScreenRules.LineStatus(Line(4_800m), Text, Amount).Should().Be("Treasury.Unreconciled");
        BankReconciliationScreenRules.LineInk(Line(4_800m)).Should().Be(TreasuryInk.Plain);

        var net = Line(4_800m, matches: 1, matched: 4_812.40m, automatic: true, kinds: TreasuryMovementKind.CardBatch);
        BankReconciliationScreenRules.LineStatus(net, Text, Amount)
            .Should().Be("TreasuryMovementKind.CardBatch (Treasury.Automatic) — Treasury.Difference -12.40 DH");
        BankReconciliationScreenRules.LineInk(net).Should().Be(TreasuryInk.Abnormal);

        var exact = Line(-12_000m, matches: 1, matched: -12_000m, kinds: TreasuryMovementKind.SupplierPayment);
        BankReconciliationScreenRules.LineStatus(exact, Text, Amount).Should().Be("TreasuryMovementKind.SupplierPayment");
        BankReconciliationScreenRules.LineInk(exact).Should().Be(TreasuryInk.Normal);

        BankReconciliationScreenRules.MovementLabel(Movement(4_812.40m), Text).Should().Be("Treasury.CardBatchLabel(12)");
        BankReconciliationScreenRules.MovementLabel(Movement(-1m, TreasuryMovementKind.SupplierPayment, label: "Bio-Distribution"), Text)
            .Should().Be("Bio-Distribution");
        BankReconciliationScreenRules.MovementId(Movement(1m)).Should().Be("CardBatch:CB-20261014");
    }

    [Fact]
    public void The_balances_are_said_in_the_workstations_order_with_the_opening_and_the_counts()
    {
        var s = Data().Summary;
        BankReconciliationScreenRules.Balances(s).Select(b => b.Key).Should().Equal(
            "Treasury.StatementBalance", "Treasury.ReconciledBalance", "Treasury.UnmatchedLines", "Treasury.UnmatchedMovementsSiteTotal");
        BankReconciliationScreenRules.Balances(s)[0].Amount.Should().Be(162_480.50m);
        BankReconciliationScreenRules.OpeningLine(s, Text, Amount).Should().Be("Treasury.OpeningAsOf(31/08/2026,150000.00 DH)");
        BankReconciliationScreenRules.CountsLine(s, Text).Should().Be("Treasury.UnmatchedCounts(1,2)");
    }

    [Fact]
    public void A_manual_match_is_refused_the_wrong_way_and_asks_a_reason_for_its_difference()
    {
        var line = Line(4_800m);
        BankReconciliationScreenRules.CheckMatch(line, [Movement(-4_800m)]).RefusalKey.Should().Be("BankMatch_DirectionMismatch");
        BankReconciliationScreenRules.CheckMatch(line, []).RefusalKey.Should().Be("BankMatch_NoMovement");

        var exact = BankReconciliationScreenRules.CheckMatch(line, [Movement(3_000m), Movement(1_800m, key: "CB-2")]);
        exact.Should().Be(new MatchCheck(null, 0m));
        exact.NeedsReason.Should().BeFalse();

        var net = BankReconciliationScreenRules.CheckMatch(line, [Movement(4_812.40m)]);
        net.Difference.Should().Be(-12.40m);
        net.NeedsReason.Should().BeTrue();
        BankReconciliationScreenRules.MatchDifferencePrompt(net.Difference, Text, Amount).Should().Be("Treasury.MatchDifferencePrompt(-12.40 DH)");
        BankReconciliationScreenRules.JustifyPrompt(line, Text, Amount).Should().Be("Treasury.JustifyPrompt(REMISE CB 14/10,4800.00 DH)");
        BankReconciliationScreenRules.UnmatchQuestion(line, Text, Amount).Should().Be("Treasury.UnmatchConfirm(REMISE CB 14/10,4800.00 DH)");
        BankReconciliationScreenRules.WithdrawQuestion(line, Text, Amount)
            .Should().Be("Treasury.WithdrawImportConfirm(14/10/2026,REMISE CB 14/10,4800.00 DH)");
    }

    [Fact]
    public void A_statement_file_is_refused_above_ten_megabytes()
    {
        BankReconciliationScreenRules.CheckFile(10 * 1024 * 1024).Should().BeNull();
        BankReconciliationScreenRules.CheckFile(10 * 1024 * 1024 + 1)
            .Should().Be(new FieldRefusal(BankReconciliationScreenRules.FileField, "BankStatement_FileTooLarge"));
        BankReconciliationScreenRules.ImportTitle(Account(), "Relevés/releve-octobre.csv", Text)
            .Should().Be("Treasury.ImportStatement — BMCE Bank — Compte courant — releve-octobre.csv");
    }

    [Fact]
    public void The_mapping_reads_back_from_its_boxes_and_the_import_follows_the_preview_on_screen()
    {
        var kept = new BankStatementCsvMapping { Delimiter = ';', SkipLines = 3, AmountColumn = 5, DecimalSeparator = ',', DateFormat = "dd/MM/yy" };
        var form = BankReconciliationScreenRules.MappingForm(kept);
        form.Delimiter.Should().Be(1);
        form.DecimalSeparator.Should().Be(1);
        BankReconciliationScreenRules.Mapping(form).Should().Be(kept);

        form.DateFormat = "  ";
        form.DateColumn = 99;
        var read = BankReconciliationScreenRules.Mapping(form);
        read.DateFormat.Should().Be("dd/MM/yyyy");
        read.DateColumn.Should().Be(BankReconciliationScreenRules.MaximumColumn);
        BankReconciliationScreenRules.Mapping(new StatementMappingForm { Delimiter = 3 }).Delimiter.Should().Be('\t');

        ParsedStatementLine Parsed(int n, decimal? amount, string? error = null) =>
            new(n, error is null ? new DateOnly(2026, 10, 1) : null, null, "VIR CNSS", null, amount, error, []);
        var preview = new ImportPreview([["Date", "Libellé", "Montant"], ["01/10/2026", "VIR CNSS", "12 400,00"]],
            [new ImportPreviewLine(Parsed(2, 12_400m), ImportLineStatus.New, "fp"), new ImportPreviewLine(Parsed(3, 50m), ImportLineStatus.Duplicate),
             new ImportPreviewLine(Parsed(4, null, "BankStatement_AmountUnreadable"), ImportLineStatus.Invalid)]);

        BankReconciliationScreenRules.RawWidth(preview).Should().Be(3);
        BankReconciliationScreenRules.LineStatusKey(preview.Lines[0]).Should().Be("Treasury.LineNew");
        BankReconciliationScreenRules.LineStatusKey(preview.Lines[2]).Should().Be("BankStatement_AmountUnreadable");
        BankReconciliationScreenRules.LineInk(preview.Lines[1]).Should().Be(TreasuryInk.Muted);
        BankReconciliationScreenRules.LineInk(preview.Lines[2]).Should().Be(TreasuryInk.Critical);

        static string Describe(IReadOnlyList<Error> errors) => string.Join(",", errors.Select(e => e.ResourceKey));
        BankReconciliationScreenRules.ImportSummary(preview, kept, Describe, Text, Amount)
            .Should().Be(new ImportSummaryLine("Treasury.ImportSummary(1,1,1,12400.00 DH)", false));
        BankReconciliationScreenRules.ImportSummary(preview, kept with { LabelColumn = 0 }, Describe, Text, Amount)
            .Should().Be(new ImportSummaryLine("BankStatement_LabelColumnRequired", true));

        BankReconciliationScreenRules.CanImport(preview, kept, kept).Should().BeTrue();
        BankReconciliationScreenRules.CanImport(preview, kept, kept with { SkipLines = 4 }).Should().BeFalse("a box changed since the preview");
        BankReconciliationScreenRules.CanImport(preview, null, kept).Should().BeFalse();
        BankReconciliationScreenRules.CanImport(preview with { Lines = [preview.Lines[1]] }, kept, kept).Should().BeFalse("nothing new to bring in");
        BankReconciliationScreenRules.ImportWithInvalidQuestion(preview, Text).Should().Be("Treasury.ImportWithInvalid(1)");
        BankReconciliationScreenRules.ImportWithInvalidQuestion(preview with { Lines = [preview.Lines[0]] }, Text).Should().BeNull();
        BankReconciliationScreenRules.Imported(new ImportResult(18, 2, 1), Text).Should().Be("Treasury.Imported(18,2,1)");
    }

    [Fact]
    public void A_bank_account_is_refused_box_by_box_before_the_round_trip()
    {
        var form = BankReconciliationScreenRules.AccountForm(null, Today, CultureInfo.GetCultureInfo("fr-FR"));
        form.AccountingAccount.Should().Be("5141");
        form.OpeningDate.Should().Be(Today);
        form.Opening.Should().Be("0,00");
        form.IsActive.Should().BeTrue();

        form.Rib = "0117 8000 0012 3456 7890 1234";
        BankReconciliationScreenRules.Check(form).Refusal!.Field.Should().Be(BankReconciliationScreenRules.RibField);

        form.Rib = "0117 8000 0012 3456 7890 1296";
        form.Opening = "15O 000";
        BankReconciliationScreenRules.Check(form).Refusal.Should().Be(new FieldRefusal(BankReconciliationScreenRules.OpeningField, "Result_NotANumber", "15O 000"));

        form.Opening = "150 000,50";
        form.BankName = " BMCE Bank ";
        var (refusal, input) = BankReconciliationScreenRules.Check(form);
        refusal.Should().BeNull();
        input!.OpeningBalanceMad.Should().Be(150_000.50m);
        input.BankName.Should().Be("BMCE Bank");

        BankReconciliationScreenRules.AccountForm(Account(), Today).Opening.Should().Be("150000.00");
        BankReconciliationScreenRules.FieldOf(Error.Conflict("BankAccount_RibInUse")).Should().Be(BankReconciliationScreenRules.RibField);
        BankReconciliationScreenRules.FieldOf(Error.Validation("BankAccount_AccountingAccountInvalid", "4111"))
            .Should().Be(BankReconciliationScreenRules.AccountingField);
        BankReconciliationScreenRules.FieldOf(Error.Validation("BankAccount_Inactive")).Should().BeNull();
        BankReconciliationScreenRules.LastLine(Account()).Should().Be("15/10/2026");
        BankReconciliationScreenRules.LastLine(Account() with { LastLineDate = null }).Should().Be("—");
    }

    // ---- Prévision de trésorerie ---------------------------------------------------------------------

    internal static CashForecastResult Forecast() => new(
        Today, Today.AddDays(13 * 7 - 1),
        [new ForecastOpeningBalance("BMCE Bank — Compte courant", 48_000m, new DateOnly(2026, 10, 15)),
         new ForecastOpeningBalance("", -3_500m, new DateOnly(2026, 10, 15), IsRecordedNotOnStatement: true)],
        [new ForecastWeek(1, Today, Today.AddDays(6), 44_500m, 12_000m, 0m, -18_000m, 0m, -6_500m),
         new ForecastWeek(2, Today.AddDays(7), Today.AddDays(13), 32_000m, 4_000m, 0m, -9_000m, -41_500m, 0m)],
        [], 7_400m, 2_100m, 41_500m, 0m);

    [Fact]
    public void The_forecast_is_read_by_anyone_who_sees_the_treasury_its_flows_kept_by_whoever_manages_it()
    {
        bool Reader(string p) => p == Permissions.Treasury.View;
        bool Manager(string p) => p == Permissions.Treasury.Manage;
        var item = new ForecastItemRow(Guid.NewGuid(), "Loyer", -8_000m, ForecastRecurrence.Monthly, Today, null, true);

        CashForecastScreenRules.Actions(Reader, Forecast(), item).Should().Be(new CashForecastActions(false, true, true, false, false));
        CashForecastScreenRules.Actions(Manager, Forecast(), item).Should().Be(new CashForecastActions(true, true, true, true, true));
        CashForecastScreenRules.Actions(Manager, null, null).Should().Be(new CashForecastActions(true, true, false, true, false));
        CashForecastScreenRules.Actions(_ => false, null, null).Items.Should().BeFalse();

        CashForecastScreenRules.Options(new CashForecastForm { IncludePatientReceipts = true, PayrollDay = 45 })
            .Should().Be(new CashForecastOptions { IncludePatientReceipts = true, PayrollDay = 31 });
    }

    [Fact]
    public void A_week_below_zero_is_a_shortfall_and_the_lowest_closing_is_said_in_red()
    {
        var forecast = Forecast();
        forecast.Weeks[1].ClosingMad.Should().Be(-14_500m);
        CashForecastScreenRules.IsShortfall(forecast.Weeks[0]).Should().BeFalse();
        CashForecastScreenRules.IsShortfall(forecast.Weeks[1]).Should().BeTrue();
        CashForecastScreenRules.LowestClosing(forecast, Text, Amount).Should().Be(("Treasury.LowestClosing : -14500.00 DH", true));
        CashForecastScreenRules.Amounts(forecast.Weeks[1]).Should().Equal(32_000m, 4_000m, 0m, -9_000m, -41_500m, 0m, -46_500m, -14_500m);

        CashForecastScreenRules.Openings(forecast, Text, Amount).Should().Equal(
            "BMCE Bank — Compte courant : 48000.00 DH (15/10/2026)", "Treasury.OpeningRecordedNotOnStatement(15/10/2026,-3500.00 DH)");
        CashForecastScreenRules.Openings(forecast with { Openings = [] }, Text, Amount).Should().Equal("Treasury.NoBankAccount");
        CashForecastScreenRules.Notes(forecast, Text, Amount)
            .Should().Be("Treasury.ForecastNotes(7400.00 DH,2100.00 DH,41500.00 DH,0.00 DH)");
    }

    [Fact]
    public void The_weekly_table_is_written_in_csv_with_the_screens_headers()
    {
        var forecast = Forecast();
        CashForecastScreenRules.Headers(Text).Should().HaveCount(11).And.StartWith("Treasury.Week").And.EndWith("Treasury.Closing");
        CashForecastScreenRules.FileName(forecast).Should().Be("prevision-tresorerie-20261018.csv");

        var csv = Encoding.UTF8.GetString(CashForecastScreenRules.Csv(forecast, Text)).TrimStart('\uFEFF');
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("Treasury.Week;Field.From;Field.To");
        lines[2].Should().EndWith("-14500,00");
    }

    [Fact]
    public void A_flow_entered_by_hand_needs_an_amount_other_than_zero()
    {
        var form = CashForecastScreenRules.ItemForm(null, Today);
        form.Recurrence.Should().Be(ForecastRecurrence.Monthly);
        form.StartDate.Should().Be(Today);
        form.HasEndDate.Should().BeFalse();

        form.Label = "Loyer du local";
        form.Amount = "0";
        CashForecastScreenRules.Check(form).Refusal.Should().Be(new FieldRefusal(CashForecastScreenRules.AmountField, "TreasuryItem_AmountRequired"));
        form.Amount = "huit mille";
        CashForecastScreenRules.Check(form).Refusal!.Field.Should().Be(CashForecastScreenRules.AmountField);

        form.Amount = "-8 000";
        form.HasEndDate = true;
        form.EndDate = new DateOnly(2027, 6, 30);
        var (refusal, input) = CashForecastScreenRules.Check(form);
        refusal.Should().BeNull();
        input.Should().Be(new ForecastItemInput(null, "Loyer du local", -8_000m, ForecastRecurrence.Monthly, Today, new DateOnly(2027, 6, 30), true));

        var row = new ForecastItemRow(Guid.NewGuid(), "Leasing", -3_250.5m, ForecastRecurrence.Quarterly, Today, Today.AddYears(2), false);
        var edited = CashForecastScreenRules.ItemForm(row, Today);
        edited.Amount.Should().Be("-3250.50");
        edited.HasEndDate.Should().BeTrue();
        edited.IsActive.Should().BeFalse();

        CashForecastScreenRules.FieldOf(Error.Validation("TreasuryItem_EndBeforeStart")).Should().Be(CashForecastScreenRules.EndField);
        CashForecastScreenRules.FieldOf(Error.Validation("TreasuryItem_LabelRequired")).Should().Be(CashForecastScreenRules.LabelField);
        CashForecastScreenRules.RecurrenceKey(ForecastRecurrence.Yearly).Should().Be("ForecastRecurrence.Yearly");
    }

    // ---- Coût de revient et marge --------------------------------------------------------------------

    internal static MarginRow Margin(string code, decimal revenue, decimal reagent, int tests = 10, int withoutCost = 0)
    {
        var margin = revenue - reagent;
        return new MarginRow(code, "Analyse " + code, tests, revenue, reagent, 0m, revenue / tests, reagent / tests, margin / tests, margin,
                             revenue == 0m ? null : Math.Round((double)(margin / revenue * 100m), 1), withoutCost);
    }

    internal static MarginAnalysis Analysis() => new(
        new DateOnly(2026, 10, 1), Today, 2.5m,
        new MarginTotals(30, 4_200m, 3_100m, 75m, 1_025m, 24.4, 3),
        [Margin("GLY", 3_000m, 600m), Margin("HBA1C", 1_200m, 2_500m, withoutCost: 2), Margin("TSH", 0m, 0m, 10, 3)],
        [Margin("CAS", 4_200m, 3_100m, 30)], [Margin("AU480", 4_200m, 3_100m, 30)]);

    [Fact]
    public void The_margins_are_seen_with_the_steering_right_the_allocation_set_with_the_costs_right()
    {
        CostMarginScreenRules.Actions(p => p == Permissions.Steering.View, Analysis()).Should().Be(new CostMarginActions(true, true, false));
        CostMarginScreenRules.Actions(p => p == Permissions.Steering.View, null).Export.Should().BeFalse();
        CostMarginScreenRules.Actions(_ => true, null).Allocation.Should().BeTrue();

        CostMarginScreenRules.Range(PeriodPreset.ThisMonth, Today).Should().Be((new DateOnly(2026, 10, 1), Today));
        CostMarginScreenRules.Range(PeriodPreset.LastMonth, Today).Should().Be((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
        CostMarginScreenRules.Range(PeriodPreset.LastYear, Today).Should().Be((new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)));
        CostMarginScreenRules.Range(PeriodPreset.Custom, Today).Should().BeNull();
        CostMarginScreenRules.PresetKey(PeriodPreset.ThisYear).Should().Be("Statistics.PresetThisYear");
        CostMarginScreenRules.FieldOf(Error.Validation("Stats_PeriodTooLong", 366)).Should().Be(CostMarginScreenRules.PeriodField);
    }

    [Fact]
    public void A_group_sold_at_a_loss_reads_in_red_and_tests_without_a_cost_in_orange()
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var analysis = Analysis();
        var (glucose, haemoglobin, tsh) = (analysis.ByAnalysis[0], analysis.ByAnalysis[1], analysis.ByAnalysis[2]);

        CostMarginScreenRules.IsLoss(haemoglobin).Should().BeTrue();
        CostMarginScreenRules.WarnsWithoutCost(haemoglobin).Should().BeFalse("the loss already colours the whole row");
        CostMarginScreenRules.WarnsWithoutCost(tsh).Should().BeTrue();
        CostMarginScreenRules.IsLoss(glucose).Should().BeFalse();

        var cells = CostMarginScreenRules.Cells(haemoglobin, Amount, fr);
        cells.Should().HaveCount(CostMarginScreenRules.Columns.Count);
        cells[9].Should().Be("-1300.00 DH");
        cells[10].Should().Be("-108,3 %");
        cells[11].Should().Be("2");
        CostMarginScreenRules.Cells(glucose, Amount, fr)[11].Should().BeEmpty();
        CostMarginScreenRules.Cells(tsh, Amount, fr)[10].Should().Be("—");

        var tiles = CostMarginScreenRules.Tiles(analysis, Text, Amount, fr);
        tiles.Select(t => t.CaptionKey).Should().Equal("Steering.Column.revenue", "Steering.Column.reagentCost", "Steering.Column.allocation",
                                                       "Steering.Column.margin");
        tiles[0].Value.Should().Be("4200.00 DH · 30 Steering.TestsUnit");
        tiles[1].Ink.Should().Be(StockInk.Abnormal);
        tiles[2].Value.Should().Be("75.00 DH (2.50 DH Steering.PerTest)");
        tiles[3].Should().Be(new MarginTile("Steering.Column.margin", "1025.00 DH · 24,4 %", StockInk.Plain));
        tiles.Should().NotContain(t => t.Value.Contains("MAD"), "the currency is the screen's, not fixed in the text");
        CostMarginScreenRules.Heading(analysis, Text).Should().Be("Steering.CostMarginTitle — 01/10/2026 → 18/10/2026");
    }

    [Fact]
    public void Each_table_is_written_in_csv_as_shown_and_an_empty_one_is_not()
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var analysis = Analysis();
        CostMarginScreenRules.Rows(analysis, MarginTab.BySite).Should().ContainSingle(r => r.Code == "CAS");
        CostMarginScreenRules.FileName(analysis, MarginTab.ByInstrument).Should().Be("marge-par-automate-20261001-20261018.csv");

        var csv = Encoding.UTF8.GetString(CostMarginScreenRules.Csv(analysis.ByAnalysis, Text, Amount, fr)!).TrimStart('\uFEFF');
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("Steering.Column.code;Steering.Column.name;");
        lines.Should().HaveCount(4);
        lines[2].Should().StartWith("HBA1C;Analyse HBA1C;10;1200.00 DH;");
        CostMarginScreenRules.Csv([], Text, Amount, fr).Should().BeNull();
    }

    [Fact]
    public void The_allocation_is_refused_in_its_box_when_it_is_no_amount_or_beyond_the_ceiling()
    {
        CostMarginScreenRules.AllocationText(new CostSettings { AllocationPerTestMad = 2.5m }, CultureInfo.GetCultureInfo("fr-FR")).Should().Be("2,50");
        CostMarginScreenRules.Check("deux").Refusal.Should().Be(new FieldRefusal(CostMarginScreenRules.AllocationField, "Result_NotANumber", "deux"));
        CostMarginScreenRules.Check("150 000").Refusal!.Key.Should().Be("Steering_AllocationOutOfRange");
        CostMarginScreenRules.Check("-1").Refusal!.Key.Should().Be("Steering_AllocationOutOfRange");
        CostMarginScreenRules.Check("3,754").Settings.Should().Be(new CostSettings { AllocationPerTestMad = 3.75m });
    }
}
