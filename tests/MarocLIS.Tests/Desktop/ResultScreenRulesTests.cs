using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Saisie des résultats » and « Validation biologique » moved out of the
/// workstation's views, now shared with the web.
/// </summary>
public sealed class ResultScreenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 11, 0, 0, TimeSpan.FromHours(1));

    private static ResultEntryRow Row(TestResultStatus status = TestResultStatus.Awaited,
                                      ResultValueType type = ResultValueType.Numeric, string? value = null,
                                      string? comment = null, bool calculated = false, bool readOnly = false,
                                      bool awaitingSample = false, ResultFlag flags = ResultFlag.None,
                                      decimal? numeric = null, bool communicated = false) => new()
    {
        TestResultId = Guid.NewGuid(),
        LabOrderItemId = Guid.NewGuid(),
        TestParameterId = Guid.NewGuid(),
        AnalysisCode = "GLY",
        AnalysisName = new MultilingualText("Glycémie"),
        ParameterCode = "GLY",
        ParameterName = new MultilingualText("Glycémie à jeun"),
        ValueType = type,
        Status = status,
        CurrentValue = value,
        NumericValue = numeric,
        TechnicalComment = comment,
        IsCalculated = calculated,
        IsReadOnly = readOnly || calculated,
        IsAwaitingSample = awaitingSample,
        Flags = flags,
        IsCriticalCommunicated = communicated
    };

    // ---- Saisie : la liste de travail -------------------------------------------------------------

    private static PendingEntryOrder Pending(string departments = "BIO HEM", DateTimeOffset? due = null) =>
        new("2610-000012", "Ouazzani Hajar", OrderPriority.Routine, Now.AddHours(-1), due, 3, departments);

    [Fact]
    public void The_entry_worklist_keeps_the_sector_chosen_and_the_text_in_the_number_or_the_name()
    {
        var order = Pending();
        var culture = CultureInfo.GetCultureInfo("fr-MA");

        ResultEntryRules.WorklistMatches(order, null, null, culture).Should().BeTrue();
        ResultEntryRules.WorklistMatches(order, "HEM", "", culture).Should().BeTrue();
        ResultEntryRules.WorklistMatches(order, "MIC", "", culture).Should().BeFalse();
        ResultEntryRules.WorklistMatches(order, null, "000012", culture).Should().BeTrue();
        ResultEntryRules.WorklistMatches(order, null, " ouazzani ", culture).Should().BeTrue();
        ResultEntryRules.WorklistMatches(order, null, "Tazi", culture).Should().BeFalse();
    }

    [Fact]
    public void The_worklist_pill_says_ready_once_every_value_is_in_else_late_else_the_progress()
    {
        var late = Pending(due: Now.AddMinutes(-1));

        ResultEntryRules.WorklistPill(late, new ResultEntryProgress("A", 3, 3), Now).Should().Be(EntryWorklistPill.ReadyToValidate);
        ResultEntryRules.WorklistPill(late, new ResultEntryProgress("A", 1, 3), Now).Should().Be(EntryWorklistPill.Late);
        ResultEntryRules.WorklistPill(Pending(), new ResultEntryProgress("A", 1, 3), Now).Should().Be(EntryWorklistPill.Progress);
        ResultEntryRules.WorklistPill(Pending(), new ResultEntryProgress("A", 0, 0), Now).Should().Be(EntryWorklistPill.Progress);
        ResultEntryRules.WorklistPill(Pending(), null, Now).Should().Be(EntryWorklistPill.None);
    }

    // ---- Saisie : la feuille ----------------------------------------------------------------------

    [Fact]
    public void Progress_and_values_left_count_only_what_is_owed_and_fall_as_values_are_typed()
    {
        var typed = Row();
        var empty = Row();
        var entered = Row(TestResultStatus.Entered, value: "1,02");
        ResultEntryRow[] rows = [Row(type: ResultValueType.Heading), Row(calculated: true), typed, empty, entered];

        ResultEntryRules.Progress(rows).Should().Be((1, 3));
        ResultEntryRules.RemainingToEnter(rows, id => id == typed.TestResultId ? "5,4" : null).Should().Be(1);
        ResultEntryRules.CanTechnicallyValidate(rows).Should().BeTrue();
        ResultEntryRules.CanTechnicallyValidate([typed, empty]).Should().BeFalse();
    }

    [Fact]
    public void A_line_refuses_a_value_while_its_tube_is_missing_or_once_released()
    {
        ResultEntryRules.EditRefusal(Row(awaitingSample: true)).Should().Be("Result_SampleNotReceived");
        ResultEntryRules.EditRefusal(Row(TestResultStatus.Validated)).Should().Be("Result_ValidatedRequiresRevision");
        ResultEntryRules.EditRefusal(Row(TestResultStatus.Reported)).Should().Be("Result_ValidatedRequiresRevision");
        ResultEntryRules.EditRefusal(Row(TestResultStatus.Entered)).Should().BeNull();

        ResultEntryRules.TakesValue(Row()).Should().BeTrue();
        ResultEntryRules.TakesValue(Row(calculated: true)).Should().BeFalse();
        ResultEntryRules.TakesValue(Row(type: ResultValueType.Heading)).Should().BeFalse();
        ResultEntryRules.TakesComment(Row(awaitingSample: true)).Should().BeFalse();
        ResultEntryRules.TakesComment(Row(calculated: true)).Should().BeTrue();
    }

    [Fact]
    public void The_line_menu_needs_the_right_to_enter_and_a_correction_both_rights_on_a_released_value()
    {
        var technician = new LicenceTestKit.User(true, Permissions.Results.Enter);
        var reviser = new LicenceTestKit.User(true, Permissions.Results.Unvalidate, Permissions.Results.Modify);

        ResultEntryRules.RowActions(Row(value: "5"), technician).Should().Be(new ResultRowActions(true, true, false, true));
        ResultEntryRules.RowActions(Row(TestResultStatus.NotDone), technician).Should().Be(new ResultRowActions(false, false, false, true));
        ResultEntryRules.RowActions(Row(TestResultStatus.Validated, value: "5"), reviser).Should().Be(new ResultRowActions(false, false, true, true));
        ResultEntryRules.RowActions(Row(TestResultStatus.Validated, value: "5"),
                                    new LicenceTestKit.User(true, Permissions.Results.Unvalidate)).Correct.Should().BeFalse();
        ResultEntryRules.RowActions(null, technician).Should().Be(ResultRowActions.None);
    }

    [Fact]
    public void A_save_sends_only_the_values_or_comments_that_changed_and_never_an_empty_value()
    {
        var same = Row(TestResultStatus.Entered, value: "5,4");
        var changed = Row(TestResultStatus.Entered, value: "5,4");
        var commented = Row(TestResultStatus.Entered, value: "1,1");
        var cleared = Row(TestResultStatus.Entered, value: "2");
        var locked = Row(calculated: true);
        ResultEntryRow[] rows = [same, changed, commented, cleared, locked];

        var values = new Dictionary<Guid, string?>
        {
            [same.TestResultId] = " 5,4 ", [changed.TestResultId] = "6,1", [commented.TestResultId] = "1,1",
            [cleared.TestResultId] = "", [locked.TestResultId] = "9"
        };
        var comments = new Dictionary<Guid, string?> { [commented.TestResultId] = " Contrôlé " };

        var entries = ResultEntryRules.ChangedEntries(rows, id => values[id], id => comments.GetValueOrDefault(id));

        entries.Select(e => e.TestResultId).Should().Equal(changed.TestResultId, commented.TestResultId);
        entries[0].PrintTechnicalComment.Should().BeFalse();
        entries[1].TechnicalComment.Should().Be("Contrôlé");
        entries[1].PrintTechnicalComment.Should().BeTrue();

        ResultEntryRules.ChangedEntries(rows, id => values[id], id => comments.GetValueOrDefault(id), except: changed.TestResultId)
            .Select(e => e.TestResultId).Should().Equal(commented.TestResultId);
    }

    [Fact]
    public void The_indicator_is_the_same_arrow_word_and_tone_on_both_screens()
    {
        ResultEntryRules.Indicator(ResultInterpretation.High).Should().Be(new ResultIndicator("▲", "ResultEntry.FlagHigh", ValidationTone.Warning));
        ResultEntryRules.Indicator(ResultInterpretation.Low)!.Tone.Should().Be(ValidationTone.Info);
        ResultEntryRules.Indicator(ResultInterpretation.CriticalLow).Should().Be(new ResultIndicator("▼▼", "ResultEntry.FlagCritical", ValidationTone.Critical));
        ResultEntryRules.Indicator(ResultInterpretation.Abnormal)!.Key.Should().Be("Interpretation.Abnormal");
        ResultEntryRules.Indicator(ResultInterpretation.Normal).Should().BeNull();
    }

    [Fact]
    public void The_origin_is_the_analyser_by_name_then_code_or_null_for_a_value_keyed_in()
    {
        var measured = Row(TestResultStatus.Entered, value: "5");
        var named = new ResultEntryOrigin(measured.TestResultId, "HEM", null, "XN1", new MultilingualText("Sysmex XN-550"));

        ResultEntryRules.Origin(Row(type: ResultValueType.Heading), null, AppLanguage.French).Should().BeEmpty();
        ResultEntryRules.Origin(Row(), null, AppLanguage.French).Should().Be("—");
        ResultEntryRules.Origin(measured, named, AppLanguage.French).Should().Be("Sysmex XN-550");
        ResultEntryRules.Origin(measured, named with { InstrumentName = null }, AppLanguage.French).Should().Be("XN1");
        ResultEntryRules.Origin(measured, null, AppLanguage.French).Should().BeNull();
    }

    [Fact]
    public void The_curve_ends_on_the_value_typed_else_on_the_stored_one_while_not_released()
    {
        var stored = Row(TestResultStatus.Entered, value: "5,4", numeric: 5.4m);

        ResultEntryRules.HistoryCurrent(stored, "6,2").Should().Be(new ResultHistoryCurrent(6.2m, null));
        ResultEntryRules.HistoryCurrent(stored, "5,4").Should().Be(new ResultHistoryCurrent(5.4m, null));
        ResultEntryRules.HistoryCurrent(Row(TestResultStatus.Validated, value: "5,4", numeric: 5.4m), "5,4").Should().BeNull();

        ResultEntryRules.IsOutsideInterval(7m, 4m, 6m).Should().BeTrue();
        ResultEntryRules.IsOutsideInterval(5m, 4m, null).Should().BeFalse();
        ResultEntryRules.IsOutsideInterval(3m, 4m, null).Should().BeTrue();
    }

    // ---- Validation biologique -------------------------------------------------------------------

    [Fact]
    public void The_selection_counts_the_tickable_rows_the_ticks_and_what_a_validation_releases()
    {
        var awaiting = Row(TestResultStatus.TechnicallyValidated);
        var other = Row(TestResultStatus.TechnicallyValidated);
        var entered = Row(TestResultStatus.Entered);
        var released = Row(TestResultStatus.Validated);
        ResultEntryRow[] listed = [awaiting, other, entered, released];
        var ticked = new HashSet<Guid> { awaiting.TestResultId, released.TestResultId };

        ValidationScreenRules.Selection(listed, ticked.Contains, showValidated: true).Should().Be(new ValidationSelection(2, 3, 1));
        ValidationScreenRules.Selection(listed, ticked.Contains, showValidated: false).Should().Be(new ValidationSelection(1, 2, 1));
        ValidationScreenRules.IsTickable(entered, true).Should().BeFalse();
    }

    [Fact]
    public void Validating_sends_every_value_awaiting_release_with_its_comment_only_when_ticked()
    {
        var kept = Row(TestResultStatus.TechnicallyValidated);
        var held = Row(TestResultStatus.TechnicallyValidated);
        var released = Row(TestResultStatus.Validated);
        ResultEntryRow[] listed = [Row(type: ResultValueType.Heading), kept, held, released];
        var ticked = new HashSet<Guid> { kept.TestResultId, released.TestResultId };
        string? Comment(Guid _) => "Contrôle à 3 mois";

        var decisions = ValidationScreenRules.Decisions(listed, ticked.Contains, Comment);

        decisions.Select(d => (d.TestResultId, d.Validate)).Should().Equal((kept.TestResultId, true), (held.TestResultId, false));
        decisions[0].BiologistComment!.For(AppLanguage.French).Should().Be("Contrôle à 3 mois");
        decisions[1].BiologistComment.Should().BeNull();

        ValidationScreenRules.Withdrawals(listed, ticked.Contains).Should().Equal(released.TestResultId);
    }

    [Fact]
    public void An_out_of_control_qc_asks_its_justification_for_the_ticked_values_it_covers()
    {
        var covered = Row(TestResultStatus.TechnicallyValidated, flags: ResultFlag.QcOutOfControl);
        var unticked = Row(TestResultStatus.TechnicallyValidated, flags: ResultFlag.QcOutOfControl);
        var clean = Row(TestResultStatus.TechnicallyValidated);
        ResultEntryRow[] listed = [covered, unticked, clean];
        var ticked = new HashSet<Guid> { covered.TestResultId, clean.TestResultId };

        ValidationScreenRules.QcAffected(listed, ticked.Contains).Should().Equal(covered);

        var decisions = ValidationScreenRules.WithQcJustification(
            ValidationScreenRules.Decisions(listed, ticked.Contains, _ => null), listed, "Contrôle repassé, conforme");

        decisions.Single(d => d.TestResultId == covered.TestResultId).QcOverrideJustification.Should().Be("Contrôle repassé, conforme");
        decisions.Single(d => d.TestResultId == unticked.TestResultId).QcOverrideJustification.Should().BeNull();
        decisions.Single(d => d.TestResultId == clean.TestResultId).QcOverrideJustification.Should().BeNull();
    }

    [Fact]
    public void Select_all_ticks_what_awaits_release_then_unticks_it_and_never_a_released_row()
    {
        var a = Row(TestResultStatus.TechnicallyValidated);
        var b = Row(TestResultStatus.TechnicallyValidated);
        var released = Row(TestResultStatus.Validated);
        ResultEntryRow[] listed = [a, b, released];

        var some = new HashSet<Guid> { a.TestResultId };
        ValidationScreenRules.ToggleAll(listed, some.Contains, true).Should().BeEquivalentTo(
            (new[] { a.TestResultId, b.TestResultId }, true));

        var all = new HashSet<Guid> { a.TestResultId, b.TestResultId };
        ValidationScreenRules.ToggleAll(listed, all.Contains, true).Tick.Should().BeFalse();
    }

    [Fact]
    public void Only_the_critical_values_not_yet_called_are_called_and_only_a_releasable_line_takes_a_comment()
    {
        var toCall = Row(TestResultStatus.TechnicallyValidated, flags: ResultFlag.CriticalHigh);
        var called = Row(TestResultStatus.TechnicallyValidated, flags: ResultFlag.CriticalHigh, communicated: true);

        ValidationScreenRules.CriticalToCommunicate([toCall, called, Row()]).Should().Equal(toCall);

        var biologist = new LicenceTestKit.User(true, Permissions.Results.BiologicalValidate);
        ValidationScreenRules.TakesComment(toCall, biologist).Should().BeTrue();
        ValidationScreenRules.TakesComment(Row(TestResultStatus.Entered), biologist).Should().BeFalse();
        ValidationScreenRules.TakesComment(toCall, new LicenceTestKit.User(true, Permissions.Results.View)).Should().BeFalse();
        ValidationScreenRules.TakesComment(null, biologist).Should().BeFalse();
    }

    [Fact]
    public void The_validation_bar_follows_the_file_and_each_right()
    {
        var critical = Row(TestResultStatus.TechnicallyValidated, flags: ResultFlag.CriticalHigh);
        var released = Row(TestResultStatus.Validated);
        ResultEntryRow[] rows = [critical, released];

        var biologist = new LicenceTestKit.User(true, Permissions.Results.BiologicalValidate, Permissions.Results.EnterCritical,
                                                Permissions.Reports.Print, Permissions.Results.View, Permissions.Results.Unvalidate,
                                                Permissions.Orders.View);

        ValidationScreenRules.Actions(rows, 2, critical, biologist).Should().Be(new ValidationActions(true, true, true, true, true, true, true));
        ValidationScreenRules.Actions(rows, 0, null, biologist).Should().Be(new ValidationActions(false, false, false, false, false, false, true));
        ValidationScreenRules.Actions([critical], 1, critical, biologist).Release.Should().BeFalse();
        ValidationScreenRules.Actions([critical], 1, critical, biologist).Unvalidate.Should().BeFalse();

        var phlebotomist = new LicenceTestKit.User(true, Permissions.Samples.Collect);
        ValidationScreenRules.Actions(rows, 2, critical, phlebotomist).Should().Be(new ValidationActions(false, false, false, false, true, false, false));
    }

    // ---- Feuille de paillasse --------------------------------------------------------------------

    [Fact]
    public void A_bench_offers_its_analysers_and_the_shared_ones()
    {
        Guid haematology = Guid.NewGuid(), biochemistry = Guid.NewGuid();
        BenchInstrumentOption Instrument(string code, Guid? department) =>
            new(Guid.NewGuid(), code, new MultilingualText(code), department);

        var sysmex = Instrument("XN", haematology);
        var cobas = Instrument("C311", biochemistry);
        var shared = Instrument("PARTAGE", null);
        var options = new BenchWorksheetOptions([], [sysmex, cobas, shared]);

        options.InstrumentsFor(null).Should().Equal(sysmex, cobas, shared);
        options.InstrumentsFor(haematology).Should().Equal(sysmex, shared);
    }
}
