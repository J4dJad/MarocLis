using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Contrôle qualité » and of its Levey-Jennings chart, moved out of the
/// workstation's QualityControlView and LeveyJenningsChart, now shared with the web.
/// </summary>
public sealed class QualityControlScreenRulesTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static readonly Guid Au480 = Guid.NewGuid();

    internal static QcEntryRow Row(string code = "GLY", QcLevel level = QcLevel.Normal, decimal? mean = 1.00m, decimal? sd = 0.03m,
                                   bool usable = true, bool assigned = false, WestgardRule rules = WestgardRule.OneTwoS | WestgardRule.OneThreeS | WestgardRule.R4S,
                                   decimal? last = 1.02m, QcOutcome? lastOutcome = QcOutcome.Accepted, Guid? instrument = null,
                                   string instrumentCode = "AU480", string lot = "QCB-2611-N1", string? name = null) =>
        new()
        {
            QcTargetId = Guid.NewGuid(),
            TestParameterId = Guid.NewGuid(),
            QcMaterialId = Guid.NewGuid(),
            InstrumentId = instrument ?? Au480,
            ParameterCode = code,
            ParameterName = new MultilingualText(name ?? (code == "GLY" ? "Glycémie" : "Créatinine"), "سكر الدم", "Glucose"),
            UnitSymbol = "g/L",
            Decimals = 2,
            MaterialLotNumber = lot,
            Level = level,
            MaterialName = new MultilingualText("Contrôle biochimie"),
            MaterialExpiry = new DateOnly(2026, 11, 30),
            MaterialUsable = usable,
            InstrumentCode = instrumentCode,
            TargetMean = mean,
            TargetSd = sd,
            CvPercent = 3.1m,
            MaxAcceptableCvPercent = 5m,
            PointCount = 19,
            UsingAssignedStatistics = assigned,
            ActiveRules = rules,
            LastValue = last,
            LastMeasuredAt = new DateTimeOffset(2026, 10, 17, 8, 15, 0, TimeSpan.FromHours(1)),
            LastOutcome = lastOutcome
        };

    internal static QcReviewRow Review(QcEntryRow row, decimal value = 1.10m, decimal z = 3.4m, int affected = 0,
                                       WestgardRule violated = WestgardRule.OneThreeS) =>
        new(Guid.NewGuid(), row.QcTargetId, row.ParameterCode, row.ParameterName, row.InstrumentCode, row.MaterialLotNumber,
            row.Level, value, z, new DateTimeOffset(2026, 10, 18, 8, 5, 0, TimeSpan.FromHours(1)), violated, false, affected);

    internal static LeveyJenningsSeries Series(params LeveyJenningsPoint[] points) =>
        new(Guid.NewGuid(), "GLY", new MultilingualText("Glycémie"), "g/L", 2, "QCB-2611-N1", QcLevel.Normal, "AU480",
            1.00m, 0.03m, 3.0m, true, points);

    internal static LeveyJenningsPoint Point(decimal z, QcOutcome outcome = QcOutcome.Accepted, WestgardRule rules = WestgardRule.None,
                                             int day = 1, bool overridden = false, string? comment = null) =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 10, day, 8, 0, 0, TimeSpan.FromHours(1)), 1.00m + z * 0.03m, z, outcome, rules,
            overridden, false, comment);

    // ---- Rights ----------------------------------------------------------------------------------

    [Fact]
    public void Each_action_is_kept_by_the_right_the_workstation_asks()
    {
        var lot = Guid.NewGuid();

        var technician = QualityControlScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Quality.EnterQc), true, 2, null, null);
        technician.Submit.Should().BeTrue();
        technician.Review.Should().BeFalse("reviewing a rejection is another right");
        technician.EstablishLimits.Should().BeFalse();
        technician.EditRules.Should().BeFalse();
        technician.ChartMore.Should().BeFalse();
        technician.ChooseReagentLot.Should().BeTrue();
        technician.ChooseLotAnalyser.Should().BeFalse("no lot is named");
        technician.StopEvaluation.Should().BeFalse();

        var reviewer = QualityControlScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Quality.ReviewQc), true, 2, lot, Au480);
        reviewer.Submit.Should().BeFalse();
        reviewer.Review.Should().BeTrue();
        reviewer.ChooseReagentLot.Should().BeFalse();
        reviewer.StartEvaluation.Should().BeFalse();

        QualityControlScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Quality.ReviewQc), true, 0, null, null)
            .Review.Should().BeFalse("nothing awaits review");

        var manager = QualityControlScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Quality.ManageQcRules), true, 0, null, null);
        manager.Should().Be(new QcScreenActions(false, false, true, true, true, false, false, false, false));
        QualityControlScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Quality.ManageQcRules), false, 0, null, null)
            .ChartMore.Should().BeFalse("nothing is charted");

        var named = QualityControlScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Quality.EnterQc), true, 0, lot, null);
        named.ChooseLotAnalyser.Should().BeTrue();
        named.StartEvaluation.Should().BeFalse("the lot's analyser is not chosen");
        named.StopEvaluation.Should().BeTrue();
        QualityControlScreenRules.Actions(new LicenceTestKit.User(true, Permissions.Quality.EnterQc), true, 0, lot, Au480)
            .StartEvaluation.Should().BeTrue();
    }

    // ---- The entry sheet ---------------------------------------------------------------------------

    [Fact]
    public void A_control_reads_its_target_last_value_rules_and_colours()
    {
        var row = Row();

        QualityControlScreenRules.Target(row, Text, French).Should().Be("1,00 ± 0,03");
        QualityControlScreenRules.Target(Row(mean: null), Text, French).Should().Be("Qc_NoTargetEstablished");
        QualityControlScreenRules.Last(row, French).Should().Be("1,02");
        QualityControlScreenRules.Last(Row(last: null), French).Should().BeEmpty();
        QualityControlScreenRules.RulesCell(row).Should().Be("3");
        QualityControlScreenRules.RulesCell(Row(rules: WestgardRule.None)).Should().Be("—");

        QualityControlScreenRules.RulesTip(row, Text, French).Should()
            .Equal("1-2s · 1-3s · R-4s", "CV 3,10 % / 5,00 %", "19 quality.pointcount");
        QualityControlScreenRules.RulesTip(Row(usable: false), Text, French).Should().Contain("Qc_MaterialExpired(QCB-2611-N1)");

        QualityControlScreenRules.Tone(row).Should().Be(QcTone.Plain);
        QualityControlScreenRules.Tone(Row(usable: false)).Should().Be(QcTone.CriticalFill, "an expired control is said on its line");
        QualityControlScreenRules.TargetTone(Row(assigned: true)).Should().Be(QcTone.Abnormal, "the limits are still the manufacturer's");
        QualityControlScreenRules.TargetTone(row).Should().Be(QcTone.Plain);
        QualityControlScreenRules.LastTone(Row(lastOutcome: QcOutcome.Rejected)).Should().Be(QcTone.Critical);
        QualityControlScreenRules.LastTone(Row(lastOutcome: QcOutcome.Warning)).Should().Be(QcTone.Abnormal);
        QualityControlScreenRules.LastTone(row).Should().Be(QcTone.Muted);
        QualityControlScreenRules.LastTone(Row(lastOutcome: null)).Should().Be(QcTone.Plain);
    }

    [Fact]
    public void The_rules_in_force_are_named_and_none_is_said_in_red()
    {
        QualityControlScreenRules.ActiveRules(Row(), Text).Should().Be(("QualityHr.Qc.ActiveRules(1-2s · 1-3s · R-4s)", QcTone.Muted));
        QualityControlScreenRules.ActiveRules(Row(rules: WestgardRule.None), Text).Should().Be(("QualityHr.Qc.ActiveRules(—)", QcTone.Critical));
        QualityControlScreenRules.ActiveRules(null, Text).Text.Should().BeEmpty();
        QualityControlScreenRules.Count(4, 1, Text).Should().Be("QualityHr.Qc.Count(4,1)");
    }

    // ---- Review ----------------------------------------------------------------------------------

    [Fact]
    public void A_rejection_reads_its_figures_and_asks_its_corrective_action()
    {
        var row = Review(Row(), affected: 3);

        QualityControlScreenRules.Measured(row).Should().Be("18/10 08:05");
        QualityControlScreenRules.Value(row, French).Should().Be("1,100");
        QualityControlScreenRules.ZScore(row.ZScore, French).Should().Be("3,40");
        QualityControlScreenRules.ZScore(null).Should().BeEmpty();
        QualityControlScreenRules.Affected(row).Should().Be("3");
        QualityControlScreenRules.Affected(Review(Row())).Should().BeEmpty();

        QualityControlScreenRules.Blocked([row, Review(Row(), affected: 2)], Text).Should().Be("QualityHr.Qc.Blocked(5)");
        QualityControlScreenRules.Blocked([Review(Row())], Text).Should().BeNull();

        QualityControlScreenRules.ReviewPrompt(row, AppLanguage.French, Text).Should().Be("Glycémie · 1-3s\n\nQuality.CorrectiveActionPrompt");
        QualityControlScreenRules.OpensNonConformityWithoutAsking(row).Should().BeTrue("patient results were held back");
        QualityControlScreenRules.OpensNonConformityWithoutAsking(Review(Row())).Should().BeFalse();
        QualityControlScreenRules.CorrectiveActionMinimumLength.Should().Be(15);
    }

    // ---- The run --------------------------------------------------------------------------------

    [Fact]
    public void A_run_is_read_value_by_value_and_an_unreadable_one_is_refused_on_its_box()
    {
        var glyN = Row();
        var glyH = Row(level: QcLevel.High, mean: 2.6m);
        var typed = new Dictionary<Guid, string?> { [glyN.QcTargetId] = " 1,07 ", [glyH.QcTargetId] = "" };

        var reading = QualityControlScreenRules.ReadRun([glyN, glyH], id => typed.GetValueOrDefault(id), null, null);
        reading.Refusal.Should().BeNull();
        reading.Entries.Should().ContainSingle().Which.Should().Be(new QcEntry(glyN.QcTargetId, 1.07m));

        typed[glyH.QcTargetId] = "2,6a";
        var refused = QualityControlScreenRules.ReadRun([glyN, glyH], id => typed.GetValueOrDefault(id), null, null);
        refused.Refusal.Should().Be(new FieldRefusal(QualityControlScreenRules.ValueField(glyH.QcTargetId), "Result_NotANumber", "2,6a"));

        typed[glyH.QcTargetId] = "1e5";
        QualityControlScreenRules.ReadRun([glyN, glyH], id => typed.GetValueOrDefault(id), null, null)
            .Refusal.Should().NotBeNull("an exponent is a typing slip, not a control value");

        QualityControlScreenRules.ReadRun([glyN], _ => null, null, null).IsEmpty.Should().BeTrue();
        QualityControlScreenRules.HasTyped(["", " ", null]).Should().BeFalse();
        QualityControlScreenRules.HasTyped(["", "1,2"]).Should().BeTrue();
    }

    [Fact]
    public void A_named_lot_applies_to_its_analysers_rows_only_and_asks_its_analyser_first()
    {
        var lot = Guid.NewGuid();
        var other = Guid.NewGuid();
        var au = Row();
        var cobas = Row(instrument: other, instrumentCode: "C311");

        QualityControlScreenRules.ReadRun([au, cobas], _ => "1,0", lot, null).Refusal!.Field
            .Should().Be(QualityControlScreenRules.LotAnalyserField);

        var reading = QualityControlScreenRules.ReadRun([au, cobas], _ => "1,0", lot, Au480);
        reading.Entries.Single(e => e.QcTargetId == au.QcTargetId).ReagentLotId.Should().Be(lot);
        reading.Entries.Single(e => e.QcTargetId == cobas.QcTargetId).ReagentLotId.Should().BeNull("another analyser's rows are routine QC");

        var analysers = QualityControlScreenRules.LotAnalysers([cobas, au, Row(code: "CREAT")], French);
        analysers.Select(a => a.Code).Should().Equal("AU480", "C311");
        QualityControlScreenRules.PickAnalyser(analysers, other).Should().Be(other);
        QualityControlScreenRules.PickAnalyser(analysers, null).Should().BeNull("two analysers: one is chosen");
        QualityControlScreenRules.PickAnalyser([analysers[0]], null).Should().Be(Au480, "the only one is chosen already");

        QualityControlScreenRules.LotLabel(null, Text).Should().Be("Qc.ReagentLotInUse");
        QualityControlScreenRules.LotLabel(new ReagentLotOption(lot, "GLU-R1", "L2611", new DateOnly(2027, 1, 1), true), Text)
            .Should().Be("GLU-R1 · L2611 (inventory.quarantined)");
    }

    [Fact]
    public void A_recorded_run_announces_its_rejections_and_its_warnings()
    {
        var outcome = new QcSubmissionOutcome(
            [
                new QcEntryResult(Guid.NewGuid(), Guid.NewGuid(), "GLY", 1.102m, 3.4m, QcOutcome.Rejected, WestgardRule.OneThreeS, ["Qc_Rule_1_3s"]),
                new QcEntryResult(Guid.NewGuid(), Guid.NewGuid(), "GLY", 2.62m, 0.3m, QcOutcome.Accepted, WestgardRule.None, [])
            ],
            [Error.Regulatory("Qc_PatientResultsFlagged", 4)]);

        var summary = QualityControlScreenRules.Summarise(outcome, Text, errors => string.Join(" ; ", errors.Select(e => e.ResourceKey)), French);

        summary.Rejection.Should().Be("QcOutcome.Rejected\n\n• GLY : 1,102 (Z 3,40) — Qc_Rule_1_3s\n\nQc_RejectionRequiresCorrectiveAction");
        summary.Warnings.Should().Be("Qc_PatientResultsFlagged");
        summary.Status.Should().Be("1/2 qcoutcome.accepted");
        summary.StatusTone.Should().Be(QcTone.Critical);

        var clean = QualityControlScreenRules.Summarise(new QcSubmissionOutcome([outcome.Results[1]], []), Text, _ => "", French);
        clean.Rejection.Should().BeNull();
        clean.Warnings.Should().BeNull();
        clean.StatusTone.Should().Be(QcTone.Normal);
    }

    [Fact]
    public void A_value_the_service_finds_impossible_is_said_on_the_box_it_was_typed_in()
    {
        var entry = new QcEntry(Guid.NewGuid(), 99999999999m);

        QualityControlScreenRules.FieldOf(Error.Validation("Result_TechnicallyImpossible", 99999999999m), [new QcEntry(Guid.NewGuid(), 1m), entry])!
            .Field.Should().Be(QualityControlScreenRules.ValueField(entry.QcTargetId));
        QualityControlScreenRules.FieldOf(Error.Validation("Qc_NamedLotOneAnalyser"), [entry]).Should().BeNull();
    }

    // ---- The laboratory's limits ----------------------------------------------------------------------

    [Fact]
    public void The_window_of_the_limits_is_a_number_of_days_within_the_calendar()
    {
        var row = Row();
        QualityControlScreenRules.LimitsPrompt(row, AppLanguage.French, Text)
            .Should().Be("Glycémie · QcLevel.Normal · QCB-2611-N1 · AU480\n\nQuality.EstablishLimitsPrompt(20)");

        QualityControlScreenRules.ReadLimitDays(" 60 ").Should().Be(((int?)60, (FieldRefusal?)null));
        QualityControlScreenRules.ReadLimitDays("soixante").Refusal!.Key.Should().Be("Result_NotANumber");
        QualityControlScreenRules.ReadLimitDays("soixante").Refusal!.Typed.Should().Be("soixante");
        QualityControlScreenRules.ReadLimitDays("0").Refusal!.Key.Should().Be("Result_NotANumber");
        QualityControlScreenRules.ReadLimitDays("-5").Refusal.Should().NotBeNull();
        QualityControlScreenRules.ReadLimitDays("3651").Refusal!.Key.Should().Be("QcTarget_PeriodOutOfRange");
        QualityControlScreenRules.ReadLimitDays("3650").Days.Should().Be(3650);

        QualityControlScreenRules.LimitsWindow(60, new DateOnly(2026, 10, 18)).Should().Be((new DateOnly(2026, 8, 19), new DateOnly(2026, 10, 18)));
        QualityControlScreenRules.LimitsSummary(1.0123m, 0.0287m, 24, Text, French)
            .Should().Be("Quality.LaboratoryMean : 1,0123\nQuality.LaboratorySd : 0,0287\nQuality.PointCount : 24");
    }

    // ---- Filters ----------------------------------------------------------------------------------

    [Fact]
    public void The_filters_pick_a_control_by_analyte_analyser_and_level()
    {
        var other = Guid.NewGuid();
        var gly = Row();
        var creat = Row(code: "CREAT", level: QcLevel.High, instrument: other, instrumentCode: "C311");

        QualityControlScreenRules.Matches(gly, "glyc", AppLanguage.French).Should().BeTrue();
        QualityControlScreenRules.Matches(gly, "سكر", AppLanguage.Arabic).Should().BeTrue();
        QualityControlScreenRules.Matches(creat, "c311", AppLanguage.French).Should().BeTrue();
        QualityControlScreenRules.Matches(creat, "2611", AppLanguage.French).Should().BeTrue("the control lot is searched too");
        QualityControlScreenRules.Matches(gly, "creat", AppLanguage.French).Should().BeFalse();
        QualityControlScreenRules.Matches(gly, null, AppLanguage.French, other).Should().BeFalse();
        QualityControlScreenRules.Matches(creat, "", AppLanguage.French, other, QcLevel.High).Should().BeTrue();
        QualityControlScreenRules.Matches(creat, "", AppLanguage.French, null, QcLevel.Normal).Should().BeFalse();
        QualityControlScreenRules.Levels([creat, gly, gly]).Should().Equal(QcLevel.Normal, QcLevel.High);

        var review = Review(creat);
        QualityControlScreenRules.Matches(review, "créat", AppLanguage.French, other, QcLevel.High, [gly, creat]).Should().BeTrue();
        QualityControlScreenRules.Matches(review, null, AppLanguage.French, Au480, null, [gly, creat]).Should().BeFalse();
    }

    // ---- The chart --------------------------------------------------------------------------------

    [Fact]
    public void The_chart_places_its_points_on_the_Z_axis_held_at_four_SD()
    {
        LeveyJenningsLayout.Y(0m, 0, 200).Should().Be(100);
        LeveyJenningsLayout.Y(4m, 0, 200).Should().Be(8);
        LeveyJenningsLayout.Y(6m, 0, 200).Should().Be(8, "a 6 SD outlier is held at the edge, not off the chart");
        LeveyJenningsLayout.Y(-9m, 0, 200).Should().Be(192);
        LeveyJenningsLayout.X(0, 1, 50, 300).Should().Be(200);
        LeveyJenningsLayout.X(0, 3, 50, 300).Should().Be(62);
        LeveyJenningsLayout.X(2, 3, 50, 300).Should().Be(338);
        LeveyJenningsLayout.DateTicks(20).Should().Equal(0, 3, 6, 9, 12, 15, 18);
        LeveyJenningsLayout.DateTicks(4).Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public void The_limits_are_weighted_and_labelled_in_measurement_units()
    {
        var series = Series(Point(0.4m));

        LeveyJenningsLayout.KindOf(3m).Should().Be(QcLimitKind.Rejection);
        LeveyJenningsLayout.KindOf(-2m).Should().Be(QcLimitKind.Warning);
        LeveyJenningsLayout.KindOf(1m).Should().Be(QcLimitKind.Context);
        LeveyJenningsLayout.KindOf(0m).Should().Be(QcLimitKind.Mean);
        LeveyJenningsLayout.LimitLabel(series, 0m, French).Should().Be("x̄  1,00");
        LeveyJenningsLayout.LimitLabel(series, 3m, French).Should().Be("+3s  1,09");
        LeveyJenningsLayout.LimitLabel(series, -2m, French).Should().Be("-2s  0,94");

        LeveyJenningsLayout.Header(series, Text, French).Should().Be("GLY   ·   QCB-2611-N1   ·   AU480   ·   CV 3,00 %   ·   quality.supplierlimits");
        LeveyJenningsLayout.Header(series with { UsingAssignedStatistics = false }, Text, French).Should().EndWith("quality.laboratorylimits");
    }

    [Fact]
    public void Without_a_target_the_chart_says_so_rather_than_drawing_limits()
    {
        LeveyJenningsLayout.EmptyText(null, Text).Should().Be("—");
        LeveyJenningsLayout.EmptyText(Series() with { Sd = 0m }, Text).Should().Be("Qc.ChartNoTarget");
        LeveyJenningsLayout.EmptyText(Series() with { Mean = null }, Text).Should().Be("Qc.ChartNoTarget");
        LeveyJenningsLayout.EmptyText(Series(), Text).Should().Be("—");
        LeveyJenningsLayout.EmptyText(Series(Point(0m)), Text).Should().BeNull();
        LeveyJenningsLayout.CanPlot(Series()).Should().BeTrue();
    }

    [Fact]
    public void A_point_reads_its_outcome_by_shape_and_says_its_figures()
    {
        LeveyJenningsLayout.Shape(QcOutcome.Rejected).Should().Be(QcPointShape.Square);
        LeveyJenningsLayout.Shape(QcOutcome.Warning).Should().Be(QcPointShape.Triangle);
        LeveyJenningsLayout.Shape(QcOutcome.Accepted).Should().Be(QcPointShape.Circle);
        LeveyJenningsLayout.Tone(QcOutcome.Pending).Should().Be(QcTone.Muted);

        var rejected = Point(3.4m, QcOutcome.Rejected, WestgardRule.OneThreeS, day: 18, overridden: true, comment: "Recherche de cause");
        LeveyJenningsLayout.PointTip(rejected, Series(rejected), Text, French).Should()
            .Equal("1,10 g/L", "Z = 3,40", "18/10/2026 08:00", "1-3s", "Qc.Overridden", "Recherche de cause");
        LeveyJenningsLayout.PointLine(rejected, French).Should().Be(("18/10/2026 08:00 · 1,102 · Z 3,40 · 1-3s", QcTone.Critical));
        LeveyJenningsLayout.PointLine(Point(0.2m), French).Tone.Should().Be(QcTone.Muted);
    }
}
