using System.Globalization;
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
/// The rules of « Évaluation externe de la qualité », moved out of the workstation's EqaView and
/// its dialogs, now shared with the web.
/// </summary>
public sealed class EqaScreenRulesTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    internal static EqaRow Row(EqaOutcome outcome = EqaOutcome.Pending, bool overdue = false, decimal? reported = null,
                               decimal? assigned = null, decimal? peerMean = null, decimal? peerSd = null, decimal? z = null,
                               decimal? bias = null, string? nc = null, MultilingualText? notes = null, DateOnly? submitted = null,
                               string code = "GLU", string name = "Glucose", string round = "2026-03") =>
        new(Guid.NewGuid(), "ProBioQual Biochimie", round, new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 12), submitted,
            code, new MultilingualText(name, "الجلوكوز"), "AU480", reported, assigned, peerMean, peerSd, z, bias, outcome, nc, notes, overdue);

    private static EqaScreenActions Of(EqaRow? row, params string[] rights) =>
        EqaScreenRules.Actions(new LicenceTestKit.User(true, rights), row);

    [Fact]
    public void Registering_and_evaluating_take_the_review_right_and_sending_the_entry_right()
    {
        Of(Row(), Permissions.Quality.View).Should().Be(new EqaScreenActions(false, false, false));
        Of(Row(), Permissions.Quality.EnterQc).Should().Be(new EqaScreenActions(false, true, false));
        Of(Row(), Permissions.Quality.ReviewQc).Should().Be(new EqaScreenActions(true, false, true));
        Of(Row(EqaOutcome.Satisfactory), Permissions.Quality.EnterQc, Permissions.Quality.ReviewQc)
            .Should().Be(new EqaScreenActions(true, false, true), "a value sent is not changed once the verdict is out");
        Of(null, Permissions.Quality.EnterQc, Permissions.Quality.ReviewQc).Should().Be(new EqaScreenActions(true, false, false));
    }

    [Fact]
    public void A_participation_reads_its_deadline_score_and_verdict()
    {
        var late = Row(overdue: true);
        EqaScreenRules.Deadline(late).Should().Be("! 12/10/26");
        EqaScreenRules.Deadline(Row()).Should().Be("12/10/26");
        EqaScreenRules.DeadlineInk(late).Should().Be(QualityInk.Abnormal);
        EqaScreenRules.DeadlineInk(Row(EqaOutcome.NotSubmitted, overdue: true)).Should().Be(QualityInk.Plain);

        var bad = Row(EqaOutcome.Unsatisfactory, reported: 1.31m, peerMean: 1.02m, peerSd: 0.06m, z: 4.83m, bias: 28.4m);
        EqaScreenRules.ZScore(bad.ZScore, French).Should().Be("4,83");
        EqaScreenRules.Bias(bad.BiasPercent, French).Should().Be("28,4");
        EqaScreenRules.Assigned(bad, French).Should().Be("1,02", "the peer group's mean stands in for an assigned value");
        EqaScreenRules.Number(1.3124m, French).Should().Be("1,312");
        EqaScreenRules.ZInk(bad).Should().Be(QualityInk.Critical);
        EqaScreenRules.ZInk(Row(EqaOutcome.Questionable)).Should().Be(QualityInk.Abnormal);
        EqaScreenRules.Parameter(bad, AppLanguage.Arabic).Should().Be("GLU — الجلوكوز");

        EqaScreenRules.OutcomeTone(bad).Should().Be(QualityTone.Critical);
        EqaScreenRules.OutcomeTone(Row(EqaOutcome.NotSubmitted)).Should().Be(QualityTone.Critical);
        EqaScreenRules.OutcomeTone(Row(EqaOutcome.Satisfactory)).Should().Be(QualityTone.Ok);
        EqaScreenRules.OutcomeTone(Row(EqaOutcome.Questionable)).Should().Be(QualityTone.Warning);
        EqaScreenRules.OutcomeTone(Row(EqaOutcome.Excluded)).Should().Be(QualityTone.Muted);
        EqaScreenRules.OutcomeTone(late).Should().Be(QualityTone.Warning);
        EqaScreenRules.OutcomeTone(Row()).Should().Be(QualityTone.Waiting);
    }

    [Fact]
    public void The_count_says_the_satisfactory_rate_and_the_late_submissions()
    {
        EqaScreenRules.Count([Row(EqaOutcome.Satisfactory), Row(EqaOutcome.Satisfactory), Row(EqaOutcome.Unsatisfactory), Row(overdue: true)], Text)
            .Should().Be("QualityHr.Eqa.Count(4) · Eqa.SatisfactoryRate 67% · Eqa.Overdue 1");
        EqaScreenRules.Count([Row()], Text).Should().Be("QualityHr.Eqa.Count(1)");
    }

    [Fact]
    public void The_detail_says_when_it_was_sent_the_peer_group_and_the_investigation()
    {
        var row = Row(EqaOutcome.Unsatisfactory, peerMean: 1.02m, peerSd: 0.06m, submitted: new DateOnly(2026, 10, 3),
                      notes: new MultilingualText("Calibrant périmé", "معايرة منتهية"));

        EqaScreenRules.Detail(row, AppLanguage.French, Text, French).Should()
            .Equal("Eqa.SubmittedOn 03/10/26", "Quality.PeerGroup 1,02 ± 0,06", "Calibrant périmé");
        EqaScreenRules.Detail(row, AppLanguage.Arabic, Text, French)[^1].Should().Be("معايرة منتهية");
        EqaScreenRules.Detail(null, AppLanguage.French, Text).Should().BeEmpty();
        EqaScreenRules.NonConformityOpened("NC-2026-0014", Text).Should().Be("Eqa.NonConformityOpened(NC-2026-0014)");
        EqaScreenRules.NonConformityOpened(null, Text).Should().BeNull();
        EqaScreenRules.Year(1990).Should().Be(EqaScreenRules.YearMin);
    }

    [Fact]
    public void A_round_needs_its_scheme_its_reference_an_analyte_and_a_deadline_not_before_it()
    {
        var form = EqaScreenRules.NewRound(Today);
        form.Deadline.Should().Be(new DateOnly(2026, 11, 1));

        EqaScreenRules.Check(form).Should().Be(new FieldRefusal(EqaScreenRules.SchemeField, "Eqa_SchemeRequired"));
        form.Scheme = "ProBioQual";
        EqaScreenRules.Check(form)!.Field.Should().Be(EqaScreenRules.RoundField);
        form.Round = " 2026-04 ";
        EqaScreenRules.Check(form)!.Field.Should().Be(EqaScreenRules.ParametersField);

        var glucose = Guid.NewGuid();
        EqaScreenRules.Toggle(form, glucose, true);
        EqaScreenRules.Toggle(form, glucose, true);
        form.Deadline = Today.AddDays(-1);
        EqaScreenRules.Check(form).Should().Be(new FieldRefusal(EqaScreenRules.DeadlineField, "Eqa_DeadlineBeforeRound"));
        form.HasDeadline = false;
        EqaScreenRules.Check(form).Should().BeNull();

        var request = EqaScreenRules.ToRequest(form);
        request.RoundReference.Should().Be("2026-04");
        request.SubmissionDeadline.Should().BeNull();
        request.TestParameterIds.Should().Equal(glucose);
        EqaScreenRules.YearAfterRegistering(request).Should().Be(2026);

        EqaScreenRules.Toggle(form, glucose, false);
        form.ParameterIds.Should().BeEmpty();
    }

    [Fact]
    public void A_value_sent_with_its_unit_is_refused_on_its_box()
    {
        var form = EqaScreenRules.SubmissionOf(Row(reported: 1.31m), Today);
        form.Value.Should().Be("1.31");

        form.Value = "12,5 g/l";
        EqaScreenRules.Read(form, Today).Refusal.Should().Be(new FieldRefusal(EqaScreenRules.ValueField, "Result_NotANumber", "12,5 g/l"));
        form.Value = "";
        EqaScreenRules.Read(form, Today).Refusal!.Key.Should().Be("Eqa_NoReportedValue");
        form.Value = "12,5";
        form.SubmittedOn = Today.AddDays(1);
        EqaScreenRules.Read(form, Today).Refusal!.Field.Should().Be(EqaScreenRules.SubmittedOnField);
        form.SubmittedOn = Today;
        EqaScreenRules.Read(form, Today).Should().Be((12.5m, (FieldRefusal?)null));
    }

    [Fact]
    public void The_organisers_report_is_read_figure_by_figure()
    {
        var row = Row(EqaOutcome.Excluded, assigned: 1.00m, peerSd: 0.06m, notes: new MultilingualText("ملاحظة", "ملاحظة"));
        var form = EqaScreenRules.EvaluationOf(row);
        form.Assigned.Should().Be("1.00");
        form.Outcome.Should().Be(EqaOutcome.Excluded);
        form.NotesFr.Should().BeNull("an Arabic-only note is not shown twice");
        form.NotesAr.Should().Be("ملاحظة");
        EqaScreenRules.EvaluationOf(Row(EqaOutcome.Satisfactory)).Outcome.Should().BeNull("the verdict follows from the score");

        form.ZScore = "3,1 z";
        EqaScreenRules.Read(form).Refusal.Should().Be(new FieldRefusal(EqaScreenRules.ZScoreField, "Result_NotANumber", "3,1 z"));
        form.ZScore = "";
        form.PeerSd = "0";
        EqaScreenRules.Read(form).Refusal!.Field.Should().Be(EqaScreenRules.PeerSdField);
        form.PeerSd = "0,06";
        form.PeerMean = "1,02";
        form.ZScore = "3,40";

        var evaluation = EqaScreenRules.Read(form).Value!;
        evaluation.ZScore.Should().Be(3.40m);
        evaluation.PeerGroupMean.Should().Be(1.02m);
        evaluation.OutcomeOverride.Should().Be(EqaOutcome.Excluded);

        EqaScreenRules.FieldOf(new Error("Validation", "Eqa_SdMustBePositive")).Should().Be(EqaScreenRules.PeerSdField);
        EqaScreenRules.FieldOf(new Error("Conflict", "Eqa_RoundAlreadyRegistered")).Should().Be(EqaScreenRules.RoundField);
        EqaScreenRules.FieldOf(new Error("Validation", "Other")).Should().BeNull();
    }
}
