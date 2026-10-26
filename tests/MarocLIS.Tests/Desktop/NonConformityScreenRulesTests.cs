using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Non-conformités », moved out of the workstation's NonConformityView and its
/// dialogs, now shared with the web.
/// </summary>
public sealed class NonConformityScreenRulesTests
{
    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static readonly DateOnly Today = new(2026, 10, 18);

    internal static NonConformityRow File(string reference = "NC-2026-0012", NonConformityStatus status = NonConformityStatus.InProgress,
                                          NonConformitySeverity severity = NonConformitySeverity.Major,
                                          NonConformityCategory category = NonConformityCategory.Equipment,
                                          bool affected = false, int? patients = null, bool overdue = false, bool recurrence = false,
                                          int actions = 2, int open = 1, string? description = null, int? daysOpen = 9) =>
        new(Guid.NewGuid(), reference, category, severity, status,
            new MultilingualText(description ?? "Dérive de la calibration de la glycémie sur l'AU480", "انحراف معايرة الجلوكوز"),
            new DateTimeOffset(2026, 10, 9, 10, 30, 0, TimeSpan.FromHours(1)), "Karim Tahiri", DetectionSource.InternalQualityControl,
            affected, patients, "Nadia Bennani", new DateOnly(2026, 10, 23), overdue, daysOpen, actions, open, recurrence);

    internal static CorrectiveActionRow Action(CorrectiveActionStatus status = CorrectiveActionStatus.Planned, bool overdue = false,
                                               ActionKind kind = ActionKind.CorrectiveAction, string description = "Recalibrer l'AU480 et repasser les contrôles") =>
        new(Guid.NewGuid(), kind, new MultilingualText(description), "Nadia Bennani", new DateOnly(2026, 10, 20), status,
            status == CorrectiveActionStatus.Completed ? new DateOnly(2026, 10, 17) : null, overdue);

    private static NonConformityScreenActions Of(NonConformityRow? file, CorrectiveActionRow? action, params string[] rights) =>
        NonConformityScreenRules.Actions(new LicenceTestKit.User(true, rights), file, action);

    // ---- Rights and states ----------------------------------------------------------------------

    [Fact]
    public void Opening_takes_the_right_to_report_and_every_other_step_the_right_to_manage()
    {
        var file = File();
        var planned = Action();

        var reporter = Of(file, planned, Permissions.Quality.View, Permissions.Quality.ReportNonConformity);
        reporter.Should().Be(new NonConformityScreenActions(true, false, false, false, false, false, false, false, false));

        Of(file, planned, Permissions.Quality.View).OpenNew.Should().BeFalse("seeing the register is not reporting");

        var manager = Of(file, planned, Permissions.Quality.ManageNonConformity);
        manager.Should().Be(new NonConformityScreenActions(true, true, true, true, true, true, true, false, true));
    }

    [Fact]
    public void A_closed_file_only_reopens_and_a_dismissed_one_does_nothing()
    {
        var closed = Of(File(status: NonConformityStatus.Closed), Action(), Permissions.Quality.ManageNonConformity);
        closed.Should().Be(new NonConformityScreenActions(true, false, false, false, false, false, false, true, false));

        var dismissed = Of(File(status: NonConformityStatus.Rejected), Action(), Permissions.Quality.ManageNonConformity);
        dismissed.Should().Be(new NonConformityScreenActions(true, false, false, false, false, false, false, false, false));

        Of(null, null, Permissions.Quality.ManageNonConformity).Should()
            .Be(new NonConformityScreenActions(true, false, false, false, false, false, false, false, false), "nothing is selected");

        NonConformityScreenRules.IsEditable(File(status: NonConformityStatus.AwaitingVerification)).Should().BeTrue();
    }

    [Fact]
    public void An_action_moves_by_its_own_state()
    {
        var file = File();
        const string manage = Permissions.Quality.ManageNonConformity;

        Of(file, Action(CorrectiveActionStatus.Planned), manage).Should().Match<NonConformityScreenActions>(a => a.StartAction && a.CompleteAction && a.CancelAction);
        Of(file, Action(CorrectiveActionStatus.InProgress), manage).Should().Match<NonConformityScreenActions>(a => !a.StartAction && a.CompleteAction && a.CancelAction);
        Of(file, Action(CorrectiveActionStatus.Completed), manage).Should()
            .Match<NonConformityScreenActions>(a => !a.StartAction && !a.CompleteAction && !a.CancelAction, "a completed action is evidence and stays");
        Of(file, Action(CorrectiveActionStatus.Cancelled), manage).Should().Match<NonConformityScreenActions>(a => !a.StartAction && !a.CompleteAction && !a.CancelAction);
        Of(file, null, manage).Should().Match<NonConformityScreenActions>(a => a.AddAction && !a.StartAction);

        NonConformityScreenRules.AlreadyCompleted(Action(CorrectiveActionStatus.Completed)).Should().BeTrue();
        NonConformityScreenRules.AlreadyCompleted(Action()).Should().BeFalse();
    }

    // ---- The register ---------------------------------------------------------------------------

    [Fact]
    public void A_file_reads_its_markers_actions_tooltip_and_count()
    {
        var file = File(affected: true, patients: 14, recurrence: true);

        NonConformityScreenRules.Reference(file).Should().Be("NC-2026-0012 ↻ ⚠");
        NonConformityScreenRules.Reference(File()).Should().Be("NC-2026-0012");
        NonConformityScreenRules.ActionsCell(file).Should().Be("1/2");
        NonConformityScreenRules.ActionsCell(File(actions: 3, open: 0)).Should().Be("3");

        NonConformityScreenRules.Tooltip(file, AppLanguage.French, Text).Should().Equal(
            "Dérive de la calibration de la glycémie sur l'AU480", "NonConformity.Recurrence",
            "NonConformity.AffectedResults : 14", "NonConformity.DaysOpen : 9");
        NonConformityScreenRules.Tooltip(File(affected: true), AppLanguage.Arabic, Text).Should().Contain("NonConformity.AffectedResults : ?")
            .And.Contain("انحراف معايرة الجلوكوز");

        NonConformityScreenRules.ShortDescription(new string('a', 200)).Should().HaveLength(120).And.EndWith("…");
        NonConformityScreenRules.ShortDescription("court").Should().Be("court");

        NonConformityScreenRules.Count([File(), File(overdue: true)], Text).Should().Be("QualityHr.Nc.CountOverdue(2,1)");
        NonConformityScreenRules.Count([File()], Text).Should().Be("QualityHr.Nc.Count(1)");

        NonConformityScreenRules.ShortDate(new DateOnly(2026, 10, 23)).Should().Be("23/10/26");
        NonConformityScreenRules.ShortDate((DateOnly?)null).Should().BeEmpty();
    }

    [Fact]
    public void Pills_and_inks_follow_the_state()
    {
        NonConformityScreenRules.StatusTone(NonConformityStatus.Open).Should().Be(QualityTone.Waiting);
        NonConformityScreenRules.StatusTone(NonConformityStatus.InProgress).Should().Be(QualityTone.Info);
        NonConformityScreenRules.StatusTone(NonConformityStatus.AwaitingVerification).Should().Be(QualityTone.Warning);
        NonConformityScreenRules.StatusTone(NonConformityStatus.Closed).Should().Be(QualityTone.Ok);
        NonConformityScreenRules.StatusTone(NonConformityStatus.Rejected).Should().Be(QualityTone.Muted);

        NonConformityScreenRules.SeverityTone(NonConformitySeverity.Critical).Should().Be(QualityTone.Critical);
        NonConformityScreenRules.SeverityTone(NonConformitySeverity.Major).Should().Be(QualityTone.Warning);
        NonConformityScreenRules.SeverityTone(NonConformitySeverity.Minor).Should().Be(QualityTone.Waiting);

        NonConformityScreenRules.ActionTone(Action(overdue: true)).Should().Be(QualityTone.Critical);
        NonConformityScreenRules.ActionTone(Action()).Should().Be(QualityTone.Waiting);
        NonConformityScreenRules.ActionTone(Action(CorrectiveActionStatus.Completed, overdue: true)).Should().Be(QualityTone.Ok);
        NonConformityScreenRules.ActionInk(Action(CorrectiveActionStatus.Completed)).Should().Be(QualityInk.Normal);
        NonConformityScreenRules.ActionInk(Action(overdue: true)).Should().Be(QualityInk.Critical);

        NonConformityScreenRules.Emphasis(File(status: NonConformityStatus.Closed, overdue: true)).Should().Be(NonConformityEmphasis.Closed);
        NonConformityScreenRules.Emphasis(File(overdue: true)).Should().Be(NonConformityEmphasis.Overdue);
        NonConformityScreenRules.Emphasis(File()).Should().Be(NonConformityEmphasis.Plain);
    }

    [Fact]
    public void The_selected_file_says_who_found_it_how_long_it_has_been_open_and_whether_it_reached_patients()
    {
        var file = File(affected: true, patients: 14, recurrence: true);

        NonConformityScreenRules.Subtitle(file, Text).Should().Be("NcSeverity.Major · NcCategory.Equipment");
        NonConformityScreenRules.ActionsHeading(file, Text).Should().Be("NC-2026-0012 — NcStatus.InProgress");
        NonConformityScreenRules.ActionsHeading(null, Text).Should().Be("App.NoData");

        var facts = NonConformityScreenRules.Facts(file, Text);
        facts.Select(f => f.Value).Should().ContainInOrder(
            "DetectionSource.InternalQualityControl", "Karim Tahiri", "09/10/2026", "Nadia Bennani", "23/10/2026", "9", "↻", "14");
        facts[^1].Should().Match<NonConformityFact>(f => f.Critical && f.Label == "⚠ NonConformity.AffectedResults");
        NonConformityScreenRules.Facts(File(), Text).Should().HaveCount(6);
    }

    /// <summary>
    /// The tube a file is about, workstation and web alike: a barcode picks its tube, a file's number
    /// its refused tube first, nothing found says so; the request carries the tube and its file, and
    /// the register names them.
    /// </summary>
    [Fact]
    public void A_file_is_linked_to_its_tube_by_barcode_or_by_file_number_and_shows_it()
    {
        var order = Guid.NewGuid();
        var serum = new NonConformitySampleChoice(Guid.NewGuid(), order, "0000000038", "2610-000014", new MultilingualText("Sérum"),
                                                  MarocLIS.Domain.Samples.SampleStatus.Received);
        var edta = new NonConformitySampleChoice(Guid.NewGuid(), order, "0000000036", "2610-000014", new MultilingualText("Sang total (EDTA)"),
                                                 MarocLIS.Domain.Samples.SampleStatus.Rejected);

        var form = NonConformityScreenRules.NewForm(new DateOnly(2026, 10, 26));
        form.SampleTerm = "0000000038";
        NonConformityScreenRules.SamplesFound(form, [serum]);
        form.SampleId.Should().Be(serum.SampleId);

        form.SampleTerm = "2610-000014";
        NonConformityScreenRules.SamplesFound(form, [serum, edta]);
        form.SampleId.Should().Be(edta.SampleId, "the refused tube first");
        NonConformityScreenRules.PickSample(form, serum.SampleId);
        form.Description = "Étiquette illisible";
        NonConformityScreenRules.ToRequest(form).Should().Match<CreateNonConformityRequest>(r => r.SampleId == serum.SampleId && r.LabOrderId == order);
        NonConformityScreenRules.PickSample(form, null);
        NonConformityScreenRules.ToRequest(form).SampleId.Should().BeNull();

        NonConformityScreenRules.SampleText(edta, AppLanguage.French, Text).Should().Be("0000000036 · Sang total (EDTA) · 2610-000014 · SampleStatus.Rejected");
        form.SampleTerm = "INCONNU";
        NonConformityScreenRules.SamplesFound(form, []);
        NonConformityScreenRules.SampleSearchOutcome(form, Text).Should().Be("NonConformity.SampleNotFound(INCONNU)");

        var facts = NonConformityScreenRules.Facts(File() with { SampleBarcode = "0000000036", AccessionNumber = "2610-000014" }, Text);
        facts.Should().Contain(f => f.Label == "NonConformity.Sample" && f.Value == "0000000036 · 2610-000014");
    }

    [Fact]
    public void The_file_worked_on_stays_selected_after_a_reload()
    {
        var first = File("NC-1");
        var second = File("NC-2");

        NonConformityScreenRules.Keep([first, second], second.Id).Should().Be(second);
        NonConformityScreenRules.Keep([first, second], Guid.NewGuid()).Should().Be(first);
        NonConformityScreenRules.Keep([], null).Should().BeNull();
    }

    // ---- Forms --------------------------------------------------------------------------------

    [Fact]
    public void Opening_a_file_needs_its_description_and_counts_patients_only_when_results_were_affected()
    {
        var form = NonConformityScreenRules.NewForm(Today);
        form.DueDate.Should().Be(new DateOnly(2026, 11, 1));
        form.Category.Should().Be(NonConformityCategory.PreAnalytical);

        NonConformityScreenRules.Check(form).Should().Be(new FieldRefusal(NonConformityScreenRules.DescriptionField, "Nc_DescriptionRequired"));
        form.Description = "   ";
        NonConformityScreenRules.Check(form)!.Key.Should().Be("Nc_DescriptionRequired");

        form.Description = "  Tube hémolysé non signalé  ";
        form.ImmediateAction = " ";
        form.PatientCount = 7;
        NonConformityScreenRules.Check(form).Should().BeNull();
        NonConformityScreenRules.PatientCountEnabled(form).Should().BeFalse();

        var request = NonConformityScreenRules.ToRequest(form);
        request.Description.Fr.Should().Be("Tube hémolysé non signalé");
        request.ImmediateAction.Should().BeNull();
        request.AffectedPatientCount.Should().BeNull("results were not affected");

        form.AffectedResults = true;
        NonConformityScreenRules.PatientCountEnabled(form).Should().BeTrue();
        NonConformityScreenRules.ToRequest(form).AffectedPatientCount.Should().Be(7);

        var instrument = Guid.NewGuid();
        var prefilled = NonConformityScreenRules.Prefill(Today, NonConformityCategory.Equipment, NonConformitySeverity.Major,
                                                         DetectionSource.InstrumentAlarm, "Maintenance échouée", instrument);
        NonConformityScreenRules.ToRequest(prefilled).InstrumentId.Should().Be(instrument);
        NonConformityScreenRules.Opened("NC-2026-0013", Text).Should().Be("NonConformity.Reference NC-2026-0013");
    }

    [Fact]
    public void An_action_is_corrective_by_default_and_needs_its_description()
    {
        var form = NonConformityScreenRules.NewAction(Today);
        form.Kind.Should().Be(ActionKind.CorrectiveAction);
        form.TargetDate.Should().Be(new DateOnly(2026, 11, 1));
        NonConformityScreenRules.Check(form)!.Field.Should().Be(NonConformityScreenRules.ActionDescriptionField);

        form.Description = " Former l'équipe ";
        var id = Guid.NewGuid();
        NonConformityScreenRules.ToRequest(form, id).Should().Match<AddActionRequest>(r =>
            r.NonConformityId == id && r.Description.Fr == "Former l'équipe" && r.ResponsibleUserId == Guid.Empty);

        NonConformityScreenRules.Evidence("  ").Should().BeNull();
        NonConformityScreenRules.Evidence(" Fiche signée ")!.Fr.Should().Be("Fiche signée");
    }

    [Fact]
    public void The_analysis_opens_on_what_was_recorded()
    {
        var recorded = new NonConformityAnalysis(RootCauseCategory.CalibrationDrift, new MultilingualText("Calibrant périmé"), null,
                                                 new MultilingualText("Contrôles conformes 10 jours"), null, true);

        var form = NonConformityScreenRules.FormOf(recorded);
        form.RootCause.Should().Be(RootCauseCategory.CalibrationDrift);
        form.RootCauseAnalysis.Should().Be("Calibrant périmé");
        form.ClinicalImpact.Should().BeEmpty();
        form.AmendedReportsIssued.Should().BeTrue();

        NonConformityScreenRules.FormOf(null).RootCause.Should().Be(RootCauseCategory.HumanError);

        var input = NonConformityScreenRules.Read(form);
        input.ClinicalImpact.Should().BeNull();
        input.EffectivenessVerification!.Fr.Should().Be("Contrôles conformes 10 jours");

        NonConformityScreenRules.ClinicalImpactRequired(File(affected: true)).Should().BeTrue();
        NonConformityScreenRules.AmendedReportsAsked(File()).Should().BeFalse();
        NonConformityScreenRules.AnalysisTitle(File(), Text).Should().Be("NonConformity.RecordAnalysis — NC-2026-0012");
        NonConformityScreenRules.StatusLine(File(), NonConformityStatus.Closed, Text).Should().Be("NC-2026-0012 — NcStatus.Closed");
    }
}
