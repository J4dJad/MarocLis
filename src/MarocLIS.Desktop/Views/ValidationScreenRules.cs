using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;

namespace MarocLIS.Desktop.Views;

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

/// <summary>
/// The rules of the « Validation biologique » screen that need no window, kept free of WinForms so
/// the test project can compile them in.
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
    /// Only a technically validated value can be released. An entered one is listed so the biologist
    /// sees the whole file, but it is neither pre-ticked nor tickable: the service refuses it until
    /// the technician has checked it.
    /// </summary>
    public static bool IsReleasable(ResultEntryRow row) =>
        !row.IsHeading && row.Status == TestResultStatus.TechnicallyValidated;

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
            ? new(ValidationTone.Warning, abnormal.ToString(), "Validation.ContextAbnormal", abnormal)
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
            ? new(ValidationTone.Critical, toCall.ToString(), "Validation.ContextCriticalToCall", toCall)
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
}
