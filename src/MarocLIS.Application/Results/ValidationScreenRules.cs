using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;

namespace MarocLIS.Application.Results;

/// <summary>The filter chips above the validation worklist.</summary>
public enum ValidationFilter
{
    All = 0,
    Urgent = 1,
    Abnormal = 2,
    QcBlocked = 3
}

/// <summary>The colour pair a pill or a context line is drawn in, from the design system.</summary>
public enum ValidationTone
{
    Ok = 0,
    Waiting = 1,
    Info = 2,
    Warning = 3,
    Critical = 4
}

/// <summary>A pill of the « Signalements » column: a tone, and a resource key with its arguments.</summary>
public sealed record ValidationAlert(ValidationTone Tone, string Key, params object[] Args);

/// <summary>One line of the « Contexte » card: a badge in a pill, then a sentence.</summary>
public sealed record ValidationContextLine(ValidationTone Tone, string Badge, string Key, params object[] Args);

/// <summary>« 14 sur 16 sélectionnés » and « Valider la sélection (14) »: the ticks, the tickable rows, and those a validation would release.</summary>
public sealed record ValidationSelection(int Ticked, int Tickable, int ToValidate);

/// <summary>What the bottom bar and its menu offer on the open file, by its rows and the operator's rights.</summary>
public sealed record ValidationActions(
    bool Validate,
    bool Communicate,
    bool Release,
    bool History,
    bool SelectAll,
    bool Unvalidate,
    bool OpenOrder)
{
    public static readonly ValidationActions None = new(false, false, false, false, false, false, false);
}

/// <summary>
/// The notes a critical value's call is closed with when it did not reach anyone: the operator gave
/// up before naming whom they spoke to, or the value was not read back. Recorded on the notification
/// as written, by the workstation and the web alike.
/// </summary>
public static class CriticalCallNotes
{
    public const string Abandoned = "Communication abandonnée par l'opérateur";
    public const string ReadBackNotConfirmed = "Relecture non confirmée";
}

/// <summary>
/// The rules of the « Validation biologique » screen that need no window: which chip a file falls
/// under, the order of its alerts, which rows can be ticked and which come pre-ticked.
/// <para>
/// They live in the application layer rather than beside the desktop view because two screens apply
/// them: the workstation and the biologist's phone (MarocLIS.Api). A file the phone lists under
/// « Urgents », or a row it offers to tick, must be exactly the one the workstation would — two
/// copies of these rules would drift the first time either screen was corrected.
/// </para>
/// </summary>
public static class ValidationScreenRules
{
    /// <summary>
    /// Whether a file belongs under a chip. « Urgents » is the former « Urgent » tick box, which
    /// asked the server for the files whose priority is not routine: the same test, applied to the
    /// list already loaded, so the other chips can show their counts.
    /// </summary>
    public static bool Matches(ValidationWorklistEntry entry, ValidationFilter filter) => filter switch
    {
        ValidationFilter.Urgent => entry.Priority != OrderPriority.Routine,
        ValidationFilter.Abnormal => entry.AbnormalCount > 0 || entry.CriticalCount > 0,
        ValidationFilter.QcBlocked => entry.HasQcFailure,
        _ => true
    };

    public static int Count(IEnumerable<ValidationWorklistEntry> entries, ValidationFilter filter) =>
        entries.Count(e => Matches(e, filter));

    /// <summary>
    /// The pills of a file's « Signalements » cell, most pressing first: an uncommunicated critical
    /// value blocks the release, a failed QC blocks it until justified, a late file is late.
    /// </summary>
    public static IReadOnlyList<ValidationAlert> Alerts(ValidationWorklistEntry entry)
    {
        var alerts = new List<ValidationAlert>(5);

        if (entry.UncommunicatedCriticalCount > 0)
            alerts.Add(new(ValidationTone.Critical, "Validation.AlertCriticalToCall", entry.UncommunicatedCriticalCount));
        else if (entry.CriticalCount > 0)
            alerts.Add(new(ValidationTone.Warning, "Validation.AlertCritical", entry.CriticalCount));

        if (entry.HasQcFailure) alerts.Add(new(ValidationTone.Critical, "Validation.AlertQc"));
        if (entry.IsOverdue) alerts.Add(new(ValidationTone.Critical, "Order.Overdue"));
        if (entry.HasDeltaCheckFailure) alerts.Add(new(ValidationTone.Warning, "Validation.AlertDelta"));
        if (entry.HasSampleDefect) alerts.Add(new(ValidationTone.Warning, "Validation.AlertSample"));

        return alerts;
    }

    /// <summary>A released result: validated, or already on a report.</summary>
    public static bool IsReleased(ResultEntryRow row) =>
        row.Status is TestResultStatus.Validated or TestResultStatus.Reported;

    /// <summary>
    /// Whether « Compte rendu » is offered for the open file: only once a value of it is validated
    /// and not yet on a report, which is what the report service needs to issue a version. It was
    /// offered on a file with nothing validated, and the refusal came only after the confirmation.
    /// </summary>
    public static bool CanIssueReport(IEnumerable<ResultEntryRow> rows) =>
        rows.Any(r => !r.IsHeading && r.Status == TestResultStatus.Validated);

    /// <summary>
    /// Only a technically validated value can be released. An entered one is listed so the biologist
    /// sees the whole file, but it is neither pre-ticked nor tickable: the service refuses it until
    /// the technician has checked it.
    /// </summary>
    public static bool IsReleasable(ResultEntryRow row) =>
        !row.IsHeading && row.Status == TestResultStatus.TechnicallyValidated;

    /// <summary>
    /// Whether a listed row comes ticked when the file opens: the releasable ones. The common case is
    /// releasing the whole file, and the biologist unticks what they want to hold back.
    /// </summary>
    public static bool IsPreTicked(ResultEntryRow row) => IsReleasable(row);

    /// <summary>
    /// The arrow beside a value, read from its interpretation: ↓↓ ↑↑ for a critical value, ↓ ↑ for one
    /// outside its interval, * for an abnormal coded answer, nothing for a normal one.
    /// </summary>
    public static string InterpretationGlyph(ResultInterpretation interpretation) => interpretation switch
    {
        ResultInterpretation.CriticalLow => "↓↓",
        ResultInterpretation.CriticalHigh => "↑↑",
        ResultInterpretation.Low => "↓",
        ResultInterpretation.High => "↑",
        ResultInterpretation.Abnormal => "*",
        _ => string.Empty
    };

    /// <summary>
    /// A short glyph string for the flags carried by a result, so a biologist sees at a glance which
    /// values need attention and why: CIQ (quality control out), Δ (delta check), ! (sample defect),
    /// × (diluted), R (corrected), ST (subcontracted), ≷ (outside the measuring range).
    /// </summary>
    public static string FlagGlyphs(ResultFlag flags)
    {
        var glyphs = new List<string>(4);

        if (flags.HasFlag(ResultFlag.QcOutOfControl)) glyphs.Add("CIQ");
        if (flags.HasFlag(ResultFlag.DeltaCheckFailed)) glyphs.Add("Δ");
        if (flags.HasFlag(ResultFlag.SampleDefect)) glyphs.Add("!");
        if (flags.HasFlag(ResultFlag.Diluted)) glyphs.Add("×");
        if (flags.HasFlag(ResultFlag.Corrected)) glyphs.Add("R");
        if (flags.HasFlag(ResultFlag.Subcontracted)) glyphs.Add("ST");
        if (flags.HasFlag(ResultFlag.OutsideMeasuringRange)) glyphs.Add("≷");

        return string.Join(" ", glyphs);
    }

    /// <summary>
    /// The indicator column of a row: the flags' glyphs when it has any, otherwise the arrow of its
    /// interpretation — what the workstation grid shows in its narrow column.
    /// </summary>
    public static string Indicator(ResultEntryRow row) =>
        FlagGlyphs(row.Flags) is { Length: > 0 } glyphs ? glyphs : InterpretationGlyph(row.Interpretation);

    /// <summary>
    /// A validated result is listed on request, to be ticked for un-validation, but never pre-ticked:
    /// withdrawing a release is a deliberate act.
    /// </summary>
    public static bool IsWithdrawable(ResultEntryRow row, bool showValidated) =>
        showValidated && !row.IsHeading && row.Status == TestResultStatus.Validated;

    /// <summary>
    /// Whether a sheet row is listed: what awaits release, the headings, and — on request — what was
    /// released and can still be withdrawn. A biologist validating a file does not need to scroll
    /// past results they released last week.
    /// </summary>
    public static bool IsListed(ResultEntryRow row, bool showValidated) =>
        row.IsHeading
        || row.Status is TestResultStatus.TechnicallyValidated or TestResultStatus.Entered
        || IsWithdrawable(row, showValidated);

    /// <summary>A value outside its reference interval, or a coded answer read as abnormal.</summary>
    public static bool IsAbnormal(ResultEntryRow row) =>
        !row.IsHeading
        && row.Interpretation is not (ResultInterpretation.Normal or ResultInterpretation.NotInterpretable);

    /// <summary>
    /// The « Contexte » card of the open file, read from its listed rows and its worklist entry:
    /// abnormal values, the delay, critical values and the quality control, then the delta-check and
    /// a sample accepted with a defect when there are any.
    /// </summary>
    public static IReadOnlyList<ValidationContextLine> Context(
        IReadOnlyCollection<ResultEntryRow> listed, OrderPriority priority, DateTimeOffset? dueAt,
        DateTimeOffset now)
    {
        var lines = new List<ValidationContextLine>(6);
        var values = listed.Where(r => !r.IsHeading).ToList();

        var abnormal = values.Count(IsAbnormal);
        lines.Add(abnormal > 0
            ? new(ValidationTone.Warning, abnormal.ToString(CultureInfo.InvariantCulture), "Validation.ContextAbnormal", abnormal)
            : new(ValidationTone.Ok, "✓", "Validation.ContextNoAbnormal"));

        var late = dueAt is { } due && now > due;
        var urgent = priority != OrderPriority.Routine;

        if (late)
            lines.Add(new(ValidationTone.Critical, "!", urgent ? "Validation.ContextUrgentLate" : "Validation.ContextLate"));
        else if (urgent)
            lines.Add(new(ValidationTone.Warning, "!", "Validation.ContextUrgent"));
        else if (dueAt is not null)
            lines.Add(new(ValidationTone.Ok, "✓", "Validation.ContextOnTime"));

        var toCall = values.Count(r => r.NeedsCriticalCommunication);
        var critical = values.Count(r => r.IsCritical);

        lines.Add(toCall > 0
            ? new(ValidationTone.Critical, toCall.ToString(CultureInfo.InvariantCulture), "Validation.ContextCriticalToCall", toCall)
            : critical > 0
                ? new(ValidationTone.Ok, "✓", "Validation.ContextCriticalCalled")
                : new(ValidationTone.Ok, "✓", "Validation.ContextNoCritical"));

        lines.Add(values.Any(r => r.Flags.HasFlag(ResultFlag.QcOutOfControl))
            ? new(ValidationTone.Critical, "CIQ", "Validation.ContextQcFailed")
            : new(ValidationTone.Ok, "✓", "Validation.ContextQcOk"));

        if (values.Any(r => r.Flags.HasFlag(ResultFlag.DeltaCheckFailed)))
            lines.Add(new(ValidationTone.Warning, "Δ", "Flag.DeltaCheckFailed"));

        if (values.Any(r => r.Flags.HasFlag(ResultFlag.SampleDefect) || r.SampleDefects != default))
            lines.Add(new(ValidationTone.Warning, "!", "Flag.SampleDefect"));

        return lines;
    }

    /// <summary>Whether a listed row's tick box can be ticked: awaiting release, or released and listed to be withdrawn.</summary>
    public static bool IsTickable(ResultEntryRow row, bool showValidated) =>
        IsReleasable(row) || IsWithdrawable(row, showValidated);

    /// <summary>The counts under the results and on « Valider la sélection ».</summary>
    public static ValidationSelection Selection(IEnumerable<ResultEntryRow> listed, Func<Guid, bool> isTicked, bool showValidated)
    {
        int tickable = 0, ticked = 0, toValidate = 0;

        foreach (var row in listed.Where(r => IsTickable(r, showValidated)))
        {
            tickable++;
            if (!isTicked(row.TestResultId)) continue;
            ticked++;
            if (!IsReleased(row)) toValidate++;
        }

        return new ValidationSelection(ticked, tickable, toValidate);
    }

    /// <summary>
    /// The decisions a validation sends, one per listed value awaiting release, ticked or not: a
    /// released row is listed for withdrawal only, and validating it again means nothing. The
    /// biologist's comment goes with the line it was typed for, and only if that line is validated.
    /// Each decision carries the entry time of the value shown, so the service releases nothing
    /// that changed since the screen was loaded.
    /// </summary>
    public static List<ValidationDecision> Decisions(IEnumerable<ResultEntryRow> listed, Func<Guid, bool> isTicked,
                                                     Func<Guid, string?> commentOf) =>
    [
        .. listed.Where(r => !r.IsHeading && !IsReleased(r))
                 .Select(r =>
                 {
                     var selected = isTicked(r.TestResultId);
                     var comment = selected && commentOf(r.TestResultId) is { Length: > 0 } text
                         ? new MultilingualText(text)
                         : null;
                     // The value on screen is the one released: a re-run stored while the screen
                     // stood open is refused (« modifié depuis l'affichage »), as on the phone.
                     return new ValidationDecision(r.TestResultId, selected, comment,
                                                   DisplayedEnteredAt: r.EnteredAt);
                 })
    ];

    /// <summary>
    /// The ticked values covered by an out-of-control quality control: their override needs its
    /// justification collected before the attempt, not as a refusal afterwards.
    /// </summary>
    public static IReadOnlyList<ResultEntryRow> QcAffected(IEnumerable<ResultEntryRow> listed, Func<Guid, bool> isTicked) =>
        [.. listed.Where(r => !r.IsHeading && !IsReleased(r) && isTicked(r.TestResultId)
                              && r.Flags.HasFlag(ResultFlag.QcOutOfControl))];

    /// <summary>The decisions with the QC override's justification on each validated value it covers.</summary>
    public static List<ValidationDecision> WithQcJustification(IEnumerable<ValidationDecision> decisions,
                                                               IEnumerable<ResultEntryRow> rows, string justification)
    {
        var covered = rows.Where(r => r.Flags.HasFlag(ResultFlag.QcOutOfControl)).Select(r => r.TestResultId).ToHashSet();
        return [.. decisions.Select(d => d.Validate && covered.Contains(d.TestResultId)
                                        ? d with { QcOverrideJustification = justification }
                                        : d)];
    }

    /// <summary>
    /// The releases a withdrawal takes back: the ticked rows already released. The ticked rows
    /// awaiting release are what « Valider » is for.
    /// </summary>
    public static IReadOnlyList<Guid> Withdrawals(IEnumerable<ResultEntryRow> listed, Func<Guid, bool> isTicked) =>
        [.. listed.Where(r => !r.IsHeading && IsReleased(r) && isTicked(r.TestResultId)).Select(r => r.TestResultId)];

    /// <summary>
    /// « Tout sélectionner »: what awaits release — released rows are ticked one by one, on purpose
    /// — all ticked, or all unticked once they all are.
    /// </summary>
    public static (IReadOnlyList<Guid> Rows, bool Tick) ToggleAll(IEnumerable<ResultEntryRow> listed, Func<Guid, bool> isTicked,
                                                                  bool showValidated)
    {
        var toggleable = listed.Where(r => IsTickable(r, showValidated) && !IsReleased(r)).Select(r => r.TestResultId).ToList();
        return (toggleable, toggleable.Any(id => !isTicked(id)));
    }

    /// <summary>The critical values of the file nobody has been told about: only these are called.</summary>
    public static IReadOnlyList<ResultEntryRow> CriticalToCommunicate(IEnumerable<ResultEntryRow> rows) =>
        [.. rows.Where(r => r.NeedsCriticalCommunication)];

    /// <summary>
    /// Whether the selected line takes the biologist's comment: only a line awaiting release, which
    /// writes it onto the result when validated, and only for someone who may validate.
    /// </summary>
    public static bool TakesComment(ResultEntryRow? row, ICurrentUser user) =>
        row is not null && IsReleasable(row) && user.Has(Permissions.Results.BiologicalValidate);

    /// <summary>
    /// The bar's actions on the open file: « Valider » once something is listed; « Résultats
    /// critiques » while a value awaits its call; « Compte rendu » once a value is validated
    /// (<see cref="CanIssueReport"/>); the history on a measured value; « Dévalider » only when
    /// something has been released — retracting nothing is no action. Each with its right.
    /// </summary>
    public static ValidationActions Actions(IReadOnlyCollection<ResultEntryRow> sheetRows, int listedCount,
                                            ResultEntryRow? selected, ICurrentUser user)
    {
        var loaded = listedCount > 0;

        return new ValidationActions(
            Validate: loaded && user.Has(Permissions.Results.BiologicalValidate),
            Communicate: loaded && sheetRows.Any(r => r.NeedsCriticalCommunication) && user.Has(Permissions.Results.EnterCritical),
            Release: loaded && CanIssueReport(sheetRows) && user.Has(Permissions.Reports.Print),
            History: loaded && ResultEntryRules.IsCharted(selected) && user.Has(Permissions.Results.View),
            SelectAll: loaded,
            Unvalidate: loaded && user.Has(Permissions.Results.Unvalidate) && sheetRows.Any(r => !r.IsHeading && IsReleased(r)),
            OpenOrder: user.Has(Permissions.Orders.View));
    }
}
