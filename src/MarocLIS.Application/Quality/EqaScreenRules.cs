using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Quality;

/// <summary>
/// What « Évaluation externe de la qualité » offers, by right and by the participation selected —
/// the workstation's PermissionGate calls and ShowDetail. EqaService re-checks each right.
/// </summary>
/// <param name="Register">« Enregistrer une campagne » (reviewing QC).</param>
/// <param name="Submission">The value sent to the organiser, while the verdict is pending (entering QC).</param>
/// <param name="Evaluation">The organiser's report for the analyte (reviewing QC).</param>
public sealed record EqaScreenActions(bool Register, bool Submission, bool Evaluation);

/// <summary>A round as typed: the workstation's EqaRoundDialog.</summary>
public sealed class EqaRoundForm
{
    public string? Scheme { get; set; }
    public string? Round { get; set; }
    public DateOnly RoundDate { get; set; }
    public bool HasDeadline { get; set; } = true;
    public DateOnly Deadline { get; set; }
    public Guid? InstrumentId { get; set; }

    /// <summary>The analytes the round's samples cover, in the order they were ticked.</summary>
    public List<Guid> ParameterIds { get; } = [];
}

/// <summary>The value sent to the organiser, as typed: the workstation's EqaSubmissionDialog.</summary>
public sealed class EqaSubmissionForm
{
    public string? Value { get; set; }
    public DateOnly SubmittedOn { get; set; }
}

/// <summary>The organiser's report as typed: the workstation's EqaEvaluationDialog.</summary>
public sealed class EqaEvaluationForm
{
    public string? Assigned { get; set; }
    public string? PeerMean { get; set; }
    public string? PeerSd { get; set; }
    public string? ZScore { get; set; }

    /// <summary>Null: the verdict follows from the score; else one of the two a score cannot express.</summary>
    public EqaOutcome? Outcome { get; set; }

    public string? NotesFr { get; set; }
    public string? NotesAr { get; set; }
}

/// <summary>A form read: what to send, or what refuses it with the field it names.</summary>
public sealed record EqaReading<T>(T? Value, FieldRefusal? Refusal) where T : class;

/// <summary>
/// The rules of « Évaluation externe de la qualité » that need no window: what is offered by which
/// right, how a participation reads (its deadline, its score, its verdict, the year's rate), how a
/// round, a submission and an evaluation are typed and refused field by field, and what the screen
/// says after each. The workstation's EqaView and the web's screen apply the same ones; every
/// decision on scoring and on opening a non-conformity stays EqaService's.
/// </summary>
public static class EqaScreenRules
{
    public const int YearMin = 2000;
    public const int YearMax = 2100;

    /// <summary>The longest scheme name and round reference: the columns.</summary>
    public const int SchemeMaxLength = 150;

    public const int RoundMaxLength = 60;

    /// <summary>The longest investigation note, in each language.</summary>
    public const int NotesMaxLength = 2000;

    /// <summary>A figure: twelve characters at most, as the workstation's number boxes.</summary>
    public const int ValueMaxLength = TypedNumber.MeasureMaxLength;

    /// <summary>The deadline offered for a new round: in two weeks.</summary>
    public const int DefaultDeadlineDays = 14;

    public const string SchemeField = "eqa-scheme";
    public const string RoundField = "eqa-round";
    public const string DeadlineField = "eqa-deadline";
    public const string ParametersField = "eqa-parameters";
    public const string ValueField = "eqa-value";
    public const string SubmittedOnField = "eqa-submitted";
    public const string AssignedField = "eqa-assigned";
    public const string PeerMeanField = "eqa-peer-mean";
    public const string PeerSdField = "eqa-peer-sd";
    public const string ZScoreField = "eqa-z";

    /// <summary>The two verdicts the organiser gives that a score cannot express.</summary>
    public static readonly IReadOnlyList<EqaOutcome> OverridableOutcomes = [EqaOutcome.NotSubmitted, EqaOutcome.Excluded];

    // ---- Rights ------------------------------------------------------------------------------------

    public static EqaScreenActions Actions(ICurrentUser user, EqaRow? row)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, row);
    }

    /// <summary>
    /// Registering a round and recording the organiser's report take the right to review QC;
    /// sending the value measured is entry work, possible while the verdict is pending.
    /// </summary>
    public static EqaScreenActions Actions(Func<string, bool> has, EqaRow? row)
    {
        ArgumentNullException.ThrowIfNull(has);
        var review = has(Permissions.Quality.ReviewQc);
        return new EqaScreenActions(
            Register: review,
            Submission: has(Permissions.Quality.EnterQc) && row is { Outcome: EqaOutcome.Pending },
            Evaluation: review && row is not null);
    }

    // ---- The list ------------------------------------------------------------------------------------

    /// <summary>A figure in the operator's culture, as on the QC screen.</summary>
    public static string Number(decimal? value, IFormatProvider? culture = null, string format = "0.###") =>
        value?.ToString(format, culture ?? CultureInfo.CurrentCulture) ?? string.Empty;

    public static string ZScore(decimal? value, IFormatProvider? culture = null) => Number(value, culture, "0.00");

    public static string Bias(decimal? value, IFormatProvider? culture = null) => Number(value, culture, "0.0");

    /// <summary>The target the value is scored against: the assigned value, else the peer group's mean.</summary>
    public static string Assigned(EqaRow row, IFormatProvider? culture = null) =>
        Number(row.AssignedValue ?? row.PeerGroupMean, culture);

    /// <summary>« GLU — Glucose »: the analyte as scored.</summary>
    public static string Parameter(EqaRow row, AppLanguage language) =>
        $"{row.ParameterCode} — {row.ParameterName.For(language)}";

    /// <summary>A choice of the round's pickers: « GLU — Glucose », « AU480 — Beckman AU480 ».</summary>
    public static string Option(EqaOption option, AppLanguage language) =>
        $"{option.Code} — {option.Name.For(language)}";

    /// <summary>The deadline, « ! » before it once passed without a submission.</summary>
    public static string Deadline(EqaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.SubmissionDeadline is { } deadline
            ? row.IsSubmissionOverdue ? $"! {Date(deadline)}" : Date(deadline)
            : string.Empty;
    }

    /// <summary>A submission past its deadline keeps its date in the warning ink.</summary>
    public static QualityInk DeadlineInk(EqaRow row) =>
        row is { Outcome: EqaOutcome.Pending, IsSubmissionOverdue: true } ? QualityInk.Abnormal : QualityInk.Plain;

    /// <summary>An unsatisfactory score in the alarm's ink, a questionable one in the warning's.</summary>
    public static QualityInk ZInk(EqaRow row) => row.Outcome switch
    {
        EqaOutcome.Unsatisfactory => QualityInk.Critical,
        EqaOutcome.Questionable => QualityInk.Abnormal,
        _ => QualityInk.Plain
    };

    public static QualityTone OutcomeTone(EqaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Outcome switch
        {
            EqaOutcome.Satisfactory => QualityTone.Ok,
            EqaOutcome.Questionable => QualityTone.Warning,
            EqaOutcome.Unsatisfactory or EqaOutcome.NotSubmitted => QualityTone.Critical,
            EqaOutcome.Excluded => QualityTone.Muted,
            _ => row.IsSubmissionOverdue ? QualityTone.Warning : QualityTone.Waiting
        };
    }

    /// <summary>« 14 participations · Taux satisfaisant 86% · En retard 2 ».</summary>
    public static string Count(IReadOnlyCollection<EqaRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);

        var evaluated = rows.Count(r => r.Outcome is EqaOutcome.Satisfactory or EqaOutcome.Questionable or EqaOutcome.Unsatisfactory);
        var satisfactory = rows.Count(r => r.Outcome == EqaOutcome.Satisfactory);
        var overdue = rows.Count(r => r.IsSubmissionOverdue);

        var line = text("QualityHr.Eqa.Count", rows.Count);
        if (evaluated > 0)
            line += $" · {text("Eqa.SatisfactoryRate")} {(100.0 * satisfactory / evaluated).ToString("0", CultureInfo.InvariantCulture)}%";
        if (overdue > 0) line += $" · {text("Eqa.Overdue")} {overdue.ToString(CultureInfo.InvariantCulture)}";
        return line;
    }

    /// <summary>When it was sent, the peer group, the investigation: what the selected line says beyond its cells.</summary>
    public static IReadOnlyList<string> Detail(EqaRow? row, AppLanguage language, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (row is null) return [];

        var parts = new List<string>(3);
        if (row.SubmittedOn is { } submitted) parts.Add($"{text("Eqa.SubmittedOn")} {Date(submitted)}");
        if (row.PeerGroupMean is not null)
            parts.Add($"{text("Quality.PeerGroup")} {Number(row.PeerGroupMean, culture)} ± {Number(row.PeerGroupSd, culture)}");
        if (row.InvestigationNotes is { } notes) parts.Add(notes.For(language));
        return parts;
    }

    public static string Date(DateOnly date) => date.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    /// <summary>The participation kept selected after a reload: the one worked on, else the first.</summary>
    public static EqaRow? Keep(IReadOnlyList<EqaRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.Id == kept) ?? rows.FirstOrDefault();
    }

    /// <summary>A year typed into the filter, kept within the years a round can carry.</summary>
    public static int Year(int typed) => Math.Clamp(typed, YearMin, YearMax);

    // ---- Registering a round -------------------------------------------------------------------------

    public static EqaRoundForm NewRound(DateOnly today) => new()
    {
        RoundDate = today,
        HasDeadline = true,
        Deadline = today.AddDays(DefaultDeadlineDays)
    };

    /// <summary>The scheme, the round and at least one analyte; a deadline not before the round.</summary>
    public static FieldRefusal? Check(EqaRoundForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.IsNullOrWhiteSpace(form.Scheme)) return new FieldRefusal(SchemeField, "Eqa_SchemeRequired");
        if (string.IsNullOrWhiteSpace(form.Round)) return new FieldRefusal(RoundField, "Eqa_RoundRequired");
        if (form.ParameterIds.Count == 0) return new FieldRefusal(ParametersField, "Eqa_ParameterRequired");
        if (form.HasDeadline && form.Deadline < form.RoundDate) return new FieldRefusal(DeadlineField, "Eqa_DeadlineBeforeRound");
        return null;
    }

    public static RegisterEqaRoundRequest ToRequest(EqaRoundForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new RegisterEqaRoundRequest
        {
            SchemeName = form.Scheme!.Trim(),
            RoundReference = form.Round!.Trim(),
            RoundDate = form.RoundDate,
            SubmissionDeadline = form.HasDeadline ? form.Deadline : null,
            InstrumentId = form.InstrumentId,
            TestParameterIds = [.. form.ParameterIds.Distinct()]
        };
    }

    /// <summary>Ticks or unticks an analyte of the round.</summary>
    public static void Toggle(EqaRoundForm form, Guid parameterId, bool ticked)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.ParameterIds.Remove(parameterId);
        if (ticked) form.ParameterIds.Add(parameterId);
    }

    /// <summary>The year the list moves to after a round is registered: the round's, so it is on screen.</summary>
    public static int YearAfterRegistering(RegisterEqaRoundRequest request) => request.RoundDate.Year;

    // ---- The submission -------------------------------------------------------------------------------

    public static EqaSubmissionForm SubmissionOf(EqaRow row, DateOnly today) => new()
    {
        Value = row.ReportedValue?.ToString(CultureInfo.InvariantCulture),
        SubmittedOn = today
    };

    /// <summary>« 12,5 g/l » is no figure and is refused as such; an empty box is no value sent.</summary>
    public static (decimal? Value, FieldRefusal? Refusal) Read(EqaSubmissionForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (TypedNumber.IsUnreadable(form.Value))
            return (null, new FieldRefusal(ValueField, "Result_NotANumber", form.Value!.Trim()));
        if (TypedNumber.Parse(form.Value) is not { } value)
            return (null, new FieldRefusal(ValueField, "Eqa_NoReportedValue"));
        if (form.SubmittedOn > today)
            return (null, new FieldRefusal(SubmittedOnField, "Eqa_SubmissionInFuture"));
        return (value, null);
    }

    // ---- The evaluation ---------------------------------------------------------------------------------

    /// <summary>
    /// The report as recorded so far. A note written only in Arabic is stored with itself as the
    /// French fallback: the French box opens empty for it rather than showing the Arabic twice.
    /// </summary>
    public static EqaEvaluationForm EvaluationOf(EqaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        static string? Raw(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
        var notes = row.InvestigationNotes;
        return new EqaEvaluationForm
        {
            Assigned = Raw(row.AssignedValue),
            PeerMean = Raw(row.PeerGroupMean),
            PeerSd = Raw(row.PeerGroupSd),
            ZScore = Raw(row.ZScore),
            Outcome = OverridableOutcomes.Contains(row.Outcome) ? row.Outcome : null,
            NotesFr = notes?.Fr == notes?.Ar ? null : notes?.Fr,
            NotesAr = notes?.Ar
        };
    }

    /// <summary>
    /// A figure typed with its unit (« 12,5 g/l ») is refused on its box: it was saved as no figure,
    /// and the score worked out without it. A peer group SD is positive.
    /// </summary>
    public static EqaReading<EqaEvaluation> Read(EqaEvaluationForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        foreach (var (field, typed) in new[]
                 {
                     (AssignedField, form.Assigned), (PeerMeanField, form.PeerMean),
                     (PeerSdField, form.PeerSd), (ZScoreField, form.ZScore)
                 })
        {
            if (TypedNumber.IsUnreadable(typed))
                return new EqaReading<EqaEvaluation>(null, new FieldRefusal(field, "Result_NotANumber", typed!.Trim()));
        }

        var sd = TypedNumber.Parse(form.PeerSd);
        if (sd is <= 0m) return new EqaReading<EqaEvaluation>(null, new FieldRefusal(PeerSdField, "Eqa_SdMustBePositive"));

        return new EqaReading<EqaEvaluation>(new EqaEvaluation
        {
            AssignedValue = TypedNumber.Parse(form.Assigned),
            PeerGroupMean = TypedNumber.Parse(form.PeerMean),
            PeerGroupSd = sd,
            ZScore = TypedNumber.Parse(form.ZScore),
            OutcomeOverride = form.Outcome,
            InvestigationNotes = MultilingualText.FromOptional(form.NotesFr, form.NotesAr)
        }, null);
    }

    /// <summary>The box a refusal of EqaService names, so it is said next to it rather than only in the bar.</summary>
    public static string? FieldOf(Error error) => error?.ResourceKey switch
    {
        "Eqa_SchemeRequired" => SchemeField,
        "Eqa_RoundRequired" or "Eqa_RoundAlreadyRegistered" => RoundField,
        "Eqa_ParameterRequired" => ParametersField,
        "Eqa_DeadlineBeforeRound" => DeadlineField,
        "Eqa_SubmissionInFuture" => SubmittedOnField,
        "Eqa_SdMustBePositive" => PeerSdField,
        "Eqa_ScoreRequired" => ZScoreField,
        _ => null
    };

    /// <summary>The non-conformity the service opened on an unsatisfactory verdict, said so the investigation is written now.</summary>
    public static string? NonConformityOpened(string? reference, ScreenText text) =>
        reference is null ? null : text("Eqa.NonConformityOpened", reference);
}
