using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Quality;

/// <summary>
/// What « Non-conformités » offers, by right and by the state of the file and of the action
/// selected — the workstation's PermissionGate calls, SetDetailEnabled and RefreshActionButtons.
/// Disabled is a hint, not the control: NonConformityService re-checks each right and the domain
/// each transition.
/// </summary>
/// <param name="OpenNew">« Ouvrir une NC » (reporting or managing non-conformities).</param>
/// <param name="AddAction">An action added to a live file (managing).</param>
/// <param name="StartAction">A planned action started.</param>
/// <param name="CompleteAction">A planned or started action completed, with its evidence.</param>
/// <param name="CancelAction">A planned or started action withdrawn, with its reason.</param>
/// <param name="RecordAnalysis">The root cause, the clinical impact and the effectiveness of a live file.</param>
/// <param name="Close">Closure attempted on a live file; the domain says what is still missing.</param>
/// <param name="Reopen">A closed file resumed.</param>
/// <param name="Dismiss">A live file judged not to be a non-conformity.</param>
public sealed record NonConformityScreenActions(
    bool OpenNew, bool AddAction, bool StartAction, bool CompleteAction, bool CancelAction,
    bool RecordAnalysis, bool Close, bool Reopen, bool Dismiss);

/// <summary>A line of the selected file's facts: its caption, its value, and whether the value is a figure or a date.</summary>
public sealed record NonConformityFact(string Label, string Value, bool Mono, bool Critical = false);

/// <summary>How a line of the register reads: a closed file muted, an overdue one in bold with its due date in the alarm's ink.</summary>
public enum NonConformityEmphasis { Plain, Closed, Overdue }

/// <summary>The opening of a non-conformity as typed: the workstation's NonConformityDialog.</summary>
public sealed class NonConformityForm
{
    public NonConformityCategory Category { get; set; }
    public NonConformitySeverity Severity { get; set; }
    public DetectionSource Source { get; set; }
    public string? Description { get; set; }
    public string? ImmediateAction { get; set; }
    public bool AffectedResults { get; set; }
    public int PatientCount { get; set; }
    public DateOnly DueDate { get; set; }

    /// <summary>The instrument the file is about, when another screen opens it (a failed maintenance check).</summary>
    public Guid? InstrumentId { get; set; }

    /// <summary>What was typed to find the tube: its barcode or its file's number.</summary>
    public string? SampleTerm { get; set; }

    /// <summary>The tubes found for <see cref="SampleTerm"/>, to pick from.</summary>
    public IReadOnlyList<NonConformitySampleChoice> SampleChoices { get; set; } = [];

    /// <summary>The tube the file is about, and its file; none when the file is about no tube.</summary>
    public Guid? SampleId { get; set; }
    public Guid? LabOrderId { get; set; }
}

/// <summary>An action added to a file: the workstation's CorrectiveActionDialog.</summary>
public sealed class CorrectiveActionForm
{
    public ActionKind Kind { get; set; } = ActionKind.CorrectiveAction;
    public string? Description { get; set; }
    public DateOnly TargetDate { get; set; }
}

/// <summary>The analysis of a file: the workstation's NonConformityAnalysisDialog.</summary>
public sealed class NonConformityAnalysisForm
{
    public RootCauseCategory RootCause { get; set; }
    public string? RootCauseAnalysis { get; set; }
    public string? ClinicalImpact { get; set; }
    public string? Effectiveness { get; set; }
    public bool AmendedReportsIssued { get; set; }
}

/// <summary>The analysis read from its form, as NonConformityService.RecordAnalysisAsync takes it.</summary>
public sealed record NonConformityAnalysisInput(
    RootCauseCategory? RootCause, MultilingualText? RootCauseAnalysis, MultilingualText? ClinicalImpact,
    MultilingualText? EffectivenessVerification, bool AmendedReportsIssued);

/// <summary>
/// The rules of « Non-conformités » that need no window: what is offered by which right and in
/// which state, how a file and an action read (their markers, their pills, their inks, their
/// facts), how the opening, an action and the analysis are typed and refused field by field, and
/// what each step asks and says. The workstation's NonConformityView and the web's screen apply the
/// same ones; every decision on opening, acting, closing and reopening stays NonConformityService's
/// and the domain's.
/// </summary>
public static class NonConformityScreenRules
{
    /// <summary>The longest description, immediate action, evidence or analysis text: the column.</summary>
    public const int TextMaxLength = 2000;

    /// <summary>The longest reason an action is withdrawn with.</summary>
    public const int CancelReasonMaxLength = 1000;

    /// <summary>A reopening is explained: ten characters at least.</summary>
    public const int ReopenReasonMinimumLength = 10;

    /// <summary>A dismissal is justified: fifteen characters at least.</summary>
    public const int DismissReasonMinimumLength = 15;

    /// <summary>The largest number of patients a file can say were affected.</summary>
    public const int PatientCountMax = 100000;

    /// <summary>The due date offered for a file and for an action: in two weeks.</summary>
    public const int DefaultDueDays = 14;

    /// <summary>The description shown in the workstation's register cell, beyond which it is cut with « … ».</summary>
    public const int DescriptionCellLength = 120;

    /// <summary>The field of the file's description, where its refusal is said.</summary>
    public const string DescriptionField = "nc-description";

    /// <summary>The field of an action's description.</summary>
    public const string ActionDescriptionField = "action-description";

    /// <summary>The marker of a recurrence, after the reference.</summary>
    public const string RecurrenceMarker = "↻";

    /// <summary>The marker of a file whose released results were affected, after the reference.</summary>
    public const string AffectedMarker = "⚠";

    // ---- Rights and states ------------------------------------------------------------------------

    /// <summary>
    /// Opening a file takes the right to report or to manage; every other step the right to manage.
    /// A closed or dismissed file is not editable and only a closed one reopens; an action moves only
    /// while its file is live.
    /// </summary>
    /// <param name="file">The file selected, null when none is.</param>
    /// <param name="action">The action selected in the file's list, null when none is.</param>
    public static NonConformityScreenActions Actions(ICurrentUser user, NonConformityRow? file, CorrectiveActionRow? action)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, file, action);
    }

    /// <summary>The same, the rights read by <paramref name="has"/> (the workstation's PermissionGate).</summary>
    public static NonConformityScreenActions Actions(Func<string, bool> has, NonConformityRow? file, CorrectiveActionRow? action)
    {
        ArgumentNullException.ThrowIfNull(has);

        var manage = has(Permissions.Quality.ManageNonConformity);
        var report = has(Permissions.Quality.ReportNonConformity) || manage;
        var live = manage && IsEditable(file);
        var movable = live && action is { Status: CorrectiveActionStatus.Planned or CorrectiveActionStatus.InProgress };

        return new NonConformityScreenActions(
            OpenNew: report,
            AddAction: live,
            StartAction: live && action is { Status: CorrectiveActionStatus.Planned },
            CompleteAction: movable,
            // A completed action is evidence and stays; an uncompleted one entered by mistake has to
            // be removable, because closure requires every action finished.
            CancelAction: movable,
            RecordAnalysis: live,
            Close: live,
            Reopen: manage && file is { Status: NonConformityStatus.Closed },
            Dismiss: live);
    }

    /// <summary>A file still worked on: neither closed nor dismissed.</summary>
    public static bool IsEditable(NonConformityRow? file) =>
        file is not null && file.Status is not (NonConformityStatus.Closed or NonConformityStatus.Rejected);

    // ---- The register -------------------------------------------------------------------------------

    /// <summary>The reference with its markers — a recurrence, released results affected — visible in the narrowest column.</summary>
    public static string Reference(NonConformityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Reference
               + (row.IsRecurrence ? " " + RecurrenceMarker : string.Empty)
               + (row.AffectedReleasedResults ? " " + AffectedMarker : string.Empty);
    }

    /// <summary>The description cut for the workstation's cell; the tooltip carries it whole.</summary>
    public static string ShortDescription(string description) =>
        description.Length > DescriptionCellLength ? description[..(DescriptionCellLength - 1)] + "…" : description;

    /// <summary>« 1/3 » actions finished while some remain open, else their number.</summary>
    public static string ActionsCell(NonConformityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.OpenActionCount > 0
            ? $"{row.ActionCount - row.OpenActionCount}/{row.ActionCount}"
            : row.ActionCount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The description, then a recurrence, the patients affected and the days open.</summary>
    public static IReadOnlyList<string> Tooltip(NonConformityRow row, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<string> { row.Description.For(language) };

        if (row.IsRecurrence) lines.Add(text("NonConformity.Recurrence"));

        if (row.AffectedReleasedResults)
            lines.Add($"{text("NonConformity.AffectedResults")} : {PatientCount(row)}");

        if (row.DaysOpen is { } days)
            lines.Add($"{text("NonConformity.DaysOpen")} : {days.ToString(CultureInfo.InvariantCulture)}");

        return lines;
    }

    /// <summary>« 12 non-conformités », with how many are overdue when some are.</summary>
    public static string Count(IReadOnlyCollection<NonConformityRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);

        var overdue = rows.Count(r => r.IsOverdue);
        return overdue > 0
            ? text("QualityHr.Nc.CountOverdue", rows.Count, overdue)
            : text("QualityHr.Nc.Count", rows.Count);
    }

    public static NonConformityEmphasis Emphasis(NonConformityRow row) => row switch
    {
        { Status: NonConformityStatus.Closed } => NonConformityEmphasis.Closed,
        { IsOverdue: true } => NonConformityEmphasis.Overdue,
        _ => NonConformityEmphasis.Plain
    };

    public static QualityTone StatusTone(NonConformityStatus status) => status switch
    {
        NonConformityStatus.Open => QualityTone.Waiting,
        NonConformityStatus.InProgress => QualityTone.Info,
        NonConformityStatus.AwaitingVerification => QualityTone.Warning,
        NonConformityStatus.Closed => QualityTone.Ok,
        _ => QualityTone.Muted
    };

    public static QualityTone SeverityTone(NonConformitySeverity severity) => severity switch
    {
        NonConformitySeverity.Critical => QualityTone.Critical,
        NonConformitySeverity.Major => QualityTone.Warning,
        _ => QualityTone.Waiting
    };

    /// <summary>A planned action past its date is in the alarm's colours.</summary>
    public static QualityTone ActionTone(CorrectiveActionRow action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Status switch
        {
            CorrectiveActionStatus.Completed => QualityTone.Ok,
            CorrectiveActionStatus.InProgress => QualityTone.Info,
            CorrectiveActionStatus.Cancelled => QualityTone.Muted,
            _ => action.IsOverdue ? QualityTone.Critical : QualityTone.Waiting
        };
    }

    /// <summary>A completed action in the normal ink, an overdue one in the alarm's.</summary>
    public static QualityInk ActionInk(CorrectiveActionRow action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Status == CorrectiveActionStatus.Completed ? QualityInk.Normal
            : action.IsOverdue ? QualityInk.Critical
            : QualityInk.Plain;
    }

    // ---- The selected file ----------------------------------------------------------------------------

    /// <summary>« Majeure · Équipement »: how bad, and where it came from.</summary>
    public static string Subtitle(NonConformityRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text($"NcSeverity.{row.Severity}")} · {text($"NcCategory.{row.Category}")}";
    }

    /// <summary>« NC-2026-0012 — En cours », over the file's actions.</summary>
    public static string ActionsHeading(NonConformityRow? row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return row is null ? text("App.NoData") : $"{row.Reference} — {text($"NcStatus.{row.Status}")}";
    }

    /// <summary>
    /// Who found it, how, when; who owns it and by when; how long it has been open; whether it
    /// recurs and whether it reached patients.
    /// </summary>
    public static IReadOnlyList<NonConformityFact> Facts(NonConformityRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var facts = new List<NonConformityFact>
        {
            new(text("NonConformity.DetectionSource"), text($"DetectionSource.{row.DetectionSource}"), false),
            new(text("NonConformity.DetectedBy"), row.DetectedByName ?? string.Empty, false),
            new(text("NonConformity.DetectedAt"), LongDate(row.DetectedAt), true),
            new(text("NonConformity.AssignedTo"), row.AssignedToName ?? string.Empty, false),
            new(text("NonConformity.DueDate"), LongDate(row.DueDate), true, row is { IsOverdue: true } && IsEditable(row)),
            new(text("NonConformity.DaysOpen"), row.DaysOpen?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, true)
        };

        if (row.SampleBarcode is { } tube)
            facts.Add(new(text("NonConformity.Sample"), $"{tube} · {row.AccessionNumber}", true));

        if (row.IsRecurrence) facts.Add(new(text("NonConformity.Recurrence"), RecurrenceMarker, false));

        if (row.AffectedReleasedResults)
            facts.Add(new($"{AffectedMarker} {text("NonConformity.AffectedResults")}", PatientCount(row), true, true));

        return facts;
    }

    private static string PatientCount(NonConformityRow row) =>
        row.AffectedPatientCount?.ToString(CultureInfo.InvariantCulture) ?? "?";

    /// <summary>The register's dates: day, month, year on two digits.</summary>
    public static string ShortDate(DateOnly? date) =>
        date?.ToString("dd/MM/yy", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string ShortDate(DateTimeOffset date) => ShortDate(DateOnly.FromDateTime(date.LocalDateTime));

    /// <summary>The selected file's dates, the year in full.</summary>
    public static string LongDate(DateOnly? date) =>
        date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string LongDate(DateTimeOffset date) => LongDate(DateOnly.FromDateTime(date.LocalDateTime));

    /// <summary>
    /// The file kept selected after a reload: the one worked on, else the first. After each action
    /// the register went back to its first file, and the next action was added to another file.
    /// </summary>
    public static NonConformityRow? Keep(IReadOnlyList<NonConformityRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.Id == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    // ---- Opening a file -------------------------------------------------------------------------------

    /// <summary>A new file: the first category, severity and source, due in two weeks.</summary>
    public static NonConformityForm NewForm(DateOnly today) => new()
    {
        Category = Enum.GetValues<NonConformityCategory>()[0],
        Severity = Enum.GetValues<NonConformitySeverity>()[0],
        Source = Enum.GetValues<DetectionSource>()[0],
        DueDate = today.AddDays(DefaultDueDays)
    };

    /// <summary>A file opened from another screen, on what that screen knows; every field stays editable.</summary>
    public static NonConformityForm Prefill(DateOnly today, NonConformityCategory category, NonConformitySeverity severity,
                                            DetectionSource source, string description, Guid? instrumentId)
    {
        var form = NewForm(today);
        form.Category = category;
        form.Severity = severity;
        form.Source = source;
        form.Description = description;
        form.InstrumentId = instrumentId;
        return form;
    }

    // ---- The tube concerned ------------------------------------------------------------------------

    public const string SampleField = "nc-sample";

    /// <summary>The longest barcode or file number the search box takes.</summary>
    public const int SampleTermMaxLength = 40;

    /// <summary>
    /// The tubes found, offered to pick: a barcode found picks its tube at once; a file's number lists
    /// its tubes, the one refused first picked (the commonest reason to open a file about a tube),
    /// otherwise none until one is chosen. Nothing found leaves the file about no tube.
    /// </summary>
    public static void SamplesFound(NonConformityForm form, IReadOnlyList<NonConformitySampleChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(choices);
        form.SampleChoices = choices;
        var term = form.SampleTerm?.Trim() ?? string.Empty;
        var picked = choices.FirstOrDefault(c => string.Equals(c.Barcode, term, StringComparison.OrdinalIgnoreCase))
                     ?? (choices.Count == 1 ? choices[0] : null)
                     ?? choices.FirstOrDefault(c => c.Status == Domain.Samples.SampleStatus.Rejected);
        PickSample(form, picked?.SampleId);
    }

    /// <summary>The tube chosen in the list, with its file; null for none.</summary>
    public static void PickSample(NonConformityForm form, Guid? sampleId)
    {
        ArgumentNullException.ThrowIfNull(form);
        var choice = form.SampleChoices.FirstOrDefault(c => c.SampleId == sampleId);
        form.SampleId = choice?.SampleId;
        form.LabOrderId = choice?.LabOrderId;
    }

    /// <summary>« 0000000036 · Sang total (EDTA) · 2610-000014 · Refusé »: a tube as the list offers it.</summary>
    public static string SampleText(NonConformitySampleChoice choice, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(text);
        return $"{choice.Barcode} · {choice.Specimen.For(language)} · {choice.AccessionNumber} · {text($"SampleStatus.{choice.Status}")}";
    }

    /// <summary>What is said when nothing matches what was typed: the search is for a barcode or a file's number, whole.</summary>
    public static string? SampleSearchOutcome(NonConformityForm form, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        return string.IsNullOrWhiteSpace(form.SampleTerm) || form.SampleChoices.Count > 0
            ? null
            : text("NonConformity.SampleNotFound", form.SampleTerm.Trim());
    }

    /// <summary>The description is what a file is: refused empty, on its box.</summary>
    public static FieldRefusal? Check(NonConformityForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return string.IsNullOrWhiteSpace(form.Description)
            ? new FieldRefusal(DescriptionField, "Nc_DescriptionRequired")
            : null;
    }

    /// <summary>The patient count matters only when released results were affected.</summary>
    public static bool PatientCountEnabled(NonConformityForm form) => form?.AffectedResults == true;

    public static CreateNonConformityRequest ToRequest(NonConformityForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new CreateNonConformityRequest
        {
            Category = form.Category,
            Severity = form.Severity,
            DetectionSource = form.Source,
            Description = new MultilingualText(form.Description!.Trim()),
            ImmediateAction = Wrap(form.ImmediateAction),
            AffectedReleasedResults = form.AffectedResults,
            AffectedPatientCount = form.AffectedResults ? Math.Clamp(form.PatientCount, 0, PatientCountMax) : null,
            DueDate = form.DueDate,
            InstrumentId = form.InstrumentId,
            SampleId = form.SampleId,
            LabOrderId = form.LabOrderId
        };
    }

    /// <summary>« Référence NC-2026-0012 »: the file just opened, in the bar.</summary>
    public static string Opened(string reference, ScreenText text) =>
        $"{text("NonConformity.Reference")} {reference}";

    // ---- Actions ----------------------------------------------------------------------------------------

    /// <summary>A corrective action — the one ISO 15189 actually asks for — due in two weeks.</summary>
    public static CorrectiveActionForm NewAction(DateOnly today) =>
        new() { Kind = ActionKind.CorrectiveAction, TargetDate = today.AddDays(DefaultDueDays) };

    public static FieldRefusal? Check(CorrectiveActionForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return string.IsNullOrWhiteSpace(form.Description)
            ? new FieldRefusal(ActionDescriptionField, "Nc_ActionDescriptionRequired")
            : null;
    }

    /// <summary>The action owned by whoever records it (the service reads Guid.Empty so).</summary>
    public static AddActionRequest ToRequest(CorrectiveActionForm form, Guid nonConformityId)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new AddActionRequest(nonConformityId, form.Kind, new MultilingualText(form.Description!.Trim()),
                                    Guid.Empty, form.TargetDate);
    }

    /// <summary>The evidence an action is completed with — none is an empty answer, not a refusal.</summary>
    public static MultilingualText? Evidence(string? typed) => Wrap(typed);

    /// <summary>A completed action is evidence already: completing it again is said, not done.</summary>
    public static bool AlreadyCompleted(CorrectiveActionRow? action) =>
        action is { Status: CorrectiveActionStatus.Completed };

    // ---- The analysis -------------------------------------------------------------------------------------

    /// <summary>What was already recorded, so completing the file does not mean typing it all again.</summary>
    public static NonConformityAnalysisForm FormOf(NonConformityAnalysis? recorded) => new()
    {
        RootCause = recorded?.RootCause is { } cause && Enum.IsDefined(cause) ? cause : Enum.GetValues<RootCauseCategory>()[0],
        RootCauseAnalysis = recorded?.RootCauseAnalysis?.Fr ?? string.Empty,
        ClinicalImpact = recorded?.ClinicalImpact?.Fr ?? string.Empty,
        Effectiveness = recorded?.EffectivenessVerification?.Fr ?? string.Empty,
        AmendedReportsIssued = recorded?.AmendedReportsIssued ?? false
    };

    public static NonConformityAnalysisInput Read(NonConformityAnalysisForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new NonConformityAnalysisInput(form.RootCause, Wrap(form.RootCauseAnalysis), Wrap(form.ClinicalImpact),
                                              Wrap(form.Effectiveness), form.AmendedReportsIssued);
    }

    /// <summary>The clinical impact is emphasised when released results were affected: the domain will then require it.</summary>
    public static bool ClinicalImpactRequired(NonConformityRow row) => row?.AffectedReleasedResults == true;

    /// <summary>Amended reports are asked about only when released results were affected.</summary>
    public static bool AmendedReportsAsked(NonConformityRow row) => row?.AffectedReleasedResults == true;

    /// <summary>« Analyse des causes — NC-2026-0012 ».</summary>
    public static string AnalysisTitle(NonConformityRow row, ScreenText text) =>
        $"{text("NonConformity.RecordAnalysis")} — {row.Reference}";

    // ---- Closing, reopening, dismissing --------------------------------------------------------------------

    /// <summary>« NC-2026-0012 — Clôturée »: the bar after a closure, a reopening or a dismissal.</summary>
    public static string StatusLine(NonConformityRow row, NonConformityStatus status, ScreenText text) =>
        $"{row.Reference} — {text($"NcStatus.{status}")}";

    private static MultilingualText? Wrap(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new MultilingualText(text.Trim());
}
