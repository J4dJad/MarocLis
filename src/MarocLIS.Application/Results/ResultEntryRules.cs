using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Results;

namespace MarocLIS.Application.Results;

/// <summary>The pill of a dossier in the entry worklist: nothing known yet, its progress, late, or every value in.</summary>
public enum EntryWorklistPill
{
    None = 0,
    Progress = 1,
    Late = 2,
    ReadyToValidate = 3
}

/// <summary>The indicator pill of a value: an arrow, the word's text key and the pill's tone.</summary>
public sealed record ResultIndicator(string Arrow, string Key, ValidationTone Tone);

/// <summary>What « Plus d'actions » offers on the selected line of the entry grid.</summary>
public sealed record ResultRowActions(bool NotDone, bool Clear, bool Correct, bool Comment)
{
    public static readonly ResultRowActions None = new(false, false, false, false);
}

/// <summary>The point of a result's curve that is not released yet: the value on screen, typed or stored.</summary>
public sealed record ResultHistoryCurrent(decimal Value, string? Operator);

/// <summary>
/// The rules of the « Saisie des résultats » screen that need no window, and those it shares with
/// « Validation biologique » to open a result's history.
/// </summary>
public static class ResultEntryRules
{
    /// <summary>The reason printed in place of a value marked « not done ».</summary>
    public const int NotDoneReasonMinimumLength = 3;
    public const int NotDoneReasonMaxLength = 500;

    /// <summary>Correcting a released value: the reason goes on the amended report.</summary>
    public const int CorrectionReasonMinimumLength = 15;
    public const int CorrectionReasonMaxLength = 500;

    /// <summary>A line a value can be keyed on: not derived, not a heading, its tube received, not released.</summary>
    public static bool IsKeyable(ResultEntryRow? row) =>
        row is { IsEditable: true, IsAwaitingSample: false }
        && row.Status is not (TestResultStatus.Validated or TestResultStatus.Reported);

    /// <summary>A due time passed: the dossier in the worklist, the file on the patient card, said late.</summary>
    public static bool IsLate(DateTimeOffset? dueAt, DateTimeOffset now) => dueAt is { } due && due < now;

    /// <summary>
    /// The analyses whose tube is not received yet, said once on opening rather than discovered row
    /// by row as the cells refuse the cursor.
    /// </summary>
    public static IReadOnlyList<string> AwaitingSample(IEnumerable<ResultEntryRow> rows) =>
        [.. rows.Where(r => r.IsAwaitingSample).Select(r => r.AnalysisCode).Distinct()];

    /// <summary>
    /// What the bar says once a file opens with lines waiting on a tube: the analyses whose tube was
    /// refused and is to be drawn again (« re-prélèvement demandé »), then those whose tube is simply
    /// not received yet; null when every line can be entered. The refused tube's analyses read
    /// « tube pas encore réceptionné » before, as if it were in transit.
    /// </summary>
    public static string? AwaitingNotice(IEnumerable<ResultEntryRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var waiting = rows.Where(r => r.IsAwaitingSample).ToList();
        var recollected = waiting.Where(r => r.IsAwaitingRecollection).Select(r => r.AnalysisCode).Distinct().ToList();
        var inTransit = waiting.Where(r => !r.IsAwaitingRecollection).Select(r => r.AnalysisCode).Distinct().ToList();

        var parts = new List<string>(2);
        if (recollected.Count > 0) parts.Add(text("Result.AwaitingRecollection", string.Join(", ", recollected)));
        if (inTransit.Count > 0) parts.Add(text("Result.AwaitingSample", string.Join(", ", inTransit)));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The sectors (department codes) of a file of the worklist, for the sector chips.</summary>
    public static IEnumerable<string> SectorsOf(PendingEntryOrder order) =>
        order.Departments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Only a measured number has a curve; a coded or text result has none.</summary>
    public static bool IsCharted(ResultEntryRow? row) =>
        row is { IsHeading: false, ValueType: ResultValueType.Numeric or ResultValueType.NumericWithOperator };

    /// <summary>The same reading of a typed value as the entry service: an optional operator, then a number.</summary>
    public static bool TryParseTyped(string? raw, out string? oper, out decimal value)
    {
        oper = null;
        value = 0m;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var candidate in (string[])["<=", ">=", "≤", "≥", "<", ">"])
        {
            if (!text.StartsWith(candidate, StringComparison.Ordinal)) continue;
            oper = candidate;
            text = text[candidate.Length..].Trim();
            break;
        }

        return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
                                out value);
    }

    /// <summary>
    /// Whether a dossier of the worklist is listed: in the sector chosen (any, when none is), and
    /// containing the text typed in its number or its patient's name. A tube barcode matches none
    /// of them and is resolved on Enter.
    /// </summary>
    public static bool WorklistMatches(PendingEntryOrder order, string? sector, string? term, CultureInfo culture)
    {
        var text = term?.Trim() ?? string.Empty;

        return (sector is null || SectorsOf(order).Contains(sector, StringComparer.Ordinal))
               && (text.Length == 0
                   || order.AccessionNumber.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || culture.CompareInfo.IndexOf(order.PatientName, text, CompareOptions.IgnoreCase) >= 0);
    }

    /// <summary>
    /// The pill of a dossier: every value in — it stays listed for its technical validation, and
    /// says so — else « Retard » once past due, else « saisis / total » once its progress is known.
    /// </summary>
    public static EntryWorklistPill WorklistPill(PendingEntryOrder order, ResultEntryProgress? progress, DateTimeOffset now)
    {
        if (progress is { Total: > 0 } known && known.Entered >= known.Total) return EntryWorklistPill.ReadyToValidate;
        if (IsLate(order.DueAt, now)) return EntryWorklistPill.Late;
        return progress is null ? EntryWorklistPill.None : EntryWorklistPill.Progress;
    }

    /// <summary>
    /// The values owed by a sheet — not headings, not calculated, not on a tube still awaited — and
    /// how many are in. The lines of a refused tube to be drawn again were counted, and a file of
    /// three analyses ready read « 0 / 16 » for the hemogram nobody could enter.
    /// </summary>
    public static (int Entered, int Owed) Progress(IEnumerable<ResultEntryRow> rows)
    {
        var owed = rows.Where(r => !r.IsHeading && !r.IsCalculated && !r.IsAwaitingSample).ToList();
        return (owed.Count(r => r.Status != TestResultStatus.Awaited), owed.Count);
    }

    /// <summary>
    /// « N valeurs restent à saisir »: the lines still awaited whose box is still empty, so the
    /// count falls as values are typed, before they are saved.
    /// </summary>
    public static int RemainingToEnter(IEnumerable<ResultEntryRow> rows, Func<Guid, string?> typedValue) =>
        rows.Count(r => !r.IsHeading && !r.IsCalculated && !r.IsAwaitingSample && r.Status == TestResultStatus.Awaited
                        && string.IsNullOrWhiteSpace(typedValue(r.TestResultId)));

    /// <summary>
    /// The question before the technical validation: how many values it sends on to the biologist.
    /// It repeated its own title (« Validation technique ») and asked nothing.
    /// </summary>
    public static string TechnicalConfirmation(IEnumerable<ResultEntryRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("Validation.TechnicalConfirm", rows.Count(r => r.Status == TestResultStatus.Entered));
    }

    /// <summary>Technical validation only makes sense once something has been entered.</summary>
    public static bool CanTechnicallyValidate(IEnumerable<ResultEntryRow> rows) =>
        rows.Any(r => r.Status == TestResultStatus.Entered);

    /// <summary>Whether a line's value box takes a value: not calculated, not read-only, not a heading.</summary>
    public static bool TakesValue(ResultEntryRow row) => !row.IsReadOnly && !row.IsHeading;

    /// <summary>Whether a line takes a technical comment: not a heading, and its tube received.</summary>
    public static bool TakesComment(ResultEntryRow row) => !row.IsHeading && !row.IsAwaitingSample;

    /// <summary>
    /// Why a line's box refuses a value, as a text key, or null: its tube is not received yet, or the
    /// value is released — that one needs the revision path, with a recorded reason, because a
    /// released figure may already have been acted on.
    /// </summary>
    public static string? EditRefusal(ResultEntryRow row) =>
        row.IsAwaitingRecollection ? "Result_AwaitingRecollection"
        : row.IsAwaitingSample ? "Result_SampleNotReceived"
        : row.Status is TestResultStatus.Validated or TestResultStatus.Reported ? "Result_ValidatedRequiresRevision"
        : null;

    /// <summary>
    /// The menu's actions on the selected line: « Non fait » on a keyable line not already not
    /// done, « Effacer » on a keyable line holding a value, both with the right to enter; « Corriger »
    /// on a released measured value, with both rights the service asks for (with the withdrawal
    /// right alone the menu offered a correction the service then refused); the comment on a line
    /// that takes one.
    /// </summary>
    public static ResultRowActions RowActions(ResultEntryRow? row, ICurrentUser user)
    {
        if (row is null) return ResultRowActions.None;

        var canEnter = user.Has(Permissions.Results.Enter);

        return new ResultRowActions(
            NotDone: canEnter && IsKeyable(row) && row.Status != TestResultStatus.NotDone,
            Clear: canEnter && IsKeyable(row) && row.CurrentValue is not null,
            Correct: user.Has(Permissions.Results.Unvalidate) && user.Has(Permissions.Results.Modify)
                     && row is { IsHeading: false, IsCalculated: false }
                     && row.Status is TestResultStatus.Validated or TestResultStatus.Reported,
            Comment: TakesComment(row));
    }

    /// <summary>
    /// The entries a save sends: the lines whose typed value or comment changed, except
    /// <paramref name="except"/>. Only what changed — resubmitting an unchanged value would stamp a
    /// new entry time and lose the original one — and never an empty value, which a save skips. A
    /// line whose comment alone changed is sent as a comment (<see cref="ResultEntryAction.Comment"/>).
    /// </summary>
    public static List<ResultEntry> ChangedEntries(IEnumerable<ResultEntryRow> rows, Func<Guid, string?> typedValue,
                                                   Func<Guid, string?> typedComment, Guid? except = null)
    {
        var entries = new List<ResultEntry>();

        foreach (var row in rows)
        {
            if (row.TestResultId == except || row.IsHeading || row.IsReadOnly) continue;

            var typed = typedValue(row.TestResultId)?.Trim() ?? string.Empty;
            var comment = typedComment(row.TestResultId)?.Trim();

            var sameValue = string.Equals(typed, row.CurrentValue?.Trim() ?? string.Empty, StringComparison.Ordinal);
            var sameComment = string.Equals(comment ?? string.Empty, row.TechnicalComment ?? string.Empty,
                                            StringComparison.Ordinal);

            if ((sameValue && sameComment) || typed.Length == 0) continue;

            // Only the comment changed: the value is not sent again, which would re-enter it as
            // typed — a new entry time, and the box's text read back as a figure.
            entries.Add(sameValue
                ? new ResultEntry(row.TestResultId, null, comment ?? string.Empty,
                                  PrintTechnicalComment: !string.IsNullOrWhiteSpace(comment),
                                  Action: ResultEntryAction.Comment)
                : new ResultEntry(row.TestResultId, typed, comment,
                                  PrintTechnicalComment: !string.IsNullOrWhiteSpace(comment)));
        }

        return entries;
    }

    /// <summary>
    /// Whether opening another file, or leaving, would lose something typed: a value box that takes
    /// a value, or a comment, holding other text than was loaded. Surrounding blanks do not count,
    /// nor an empty box against no value. Unlike <see cref="ChangedEntries"/>, a value emptied
    /// counts: the operator typed it away and is told before it comes back.
    /// </summary>
    public static bool HasUnsavedTyping(IEnumerable<ResultEntryRow> rows, Func<Guid, string?> typedValue,
                                        Func<Guid, string?> typedComment) =>
        rows.Any(r => !r.IsHeading
                      && ((TakesValue(r) && Differs(typedValue(r.TestResultId), r.CurrentValue))
                          || Differs(typedComment(r.TestResultId), r.TechnicalComment)));

    private static bool Differs(string? typed, string? stored) =>
        !string.Equals(typed?.Trim() ?? string.Empty, stored?.Trim() ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The indicator of an interpretation: « ▲ Haut » in the warning pair, « ▼ Bas » in the info pair,
    /// critical in the critical pair, an abnormal coded answer in the warning pair; none for a normal
    /// or uninterpretable value. The entry and the validation screens show the same one.
    /// </summary>
    public static ResultIndicator? Indicator(ResultInterpretation interpretation) => interpretation switch
    {
        ResultInterpretation.High => new("▲", "ResultEntry.FlagHigh", ValidationTone.Warning),
        ResultInterpretation.Low => new("▼", "ResultEntry.FlagLow", ValidationTone.Info),
        ResultInterpretation.CriticalHigh => new("▲▲", "ResultEntry.FlagCritical", ValidationTone.Critical),
        ResultInterpretation.CriticalLow => new("▼▼", "ResultEntry.FlagCritical", ValidationTone.Critical),
        ResultInterpretation.Abnormal => new("◆", "Interpretation.Abnormal", ValidationTone.Warning),
        _ => null
    };

    /// <summary>
    /// Where a value came from: nothing on a heading, « — » for a calculated line or before any
    /// value, the analyser that sent it (its name, else its code), or null for a value keyed in —
    /// shown as « Manuel ».
    /// </summary>
    public static string? Origin(ResultEntryRow row, ResultEntryOrigin? origin, AppLanguage language)
    {
        if (row.IsHeading) return string.Empty;
        if (row.IsCalculated || row.CurrentValue is null) return "—";

        if (origin?.InstrumentName is { } name && name.For(language) is { Length: > 0 } text) return text;

        return origin?.InstrumentCode;
    }

    /// <summary>
    /// The point of the curve not released yet: the value typed and not saved, when it reads as a
    /// number and differs from the stored one, else the stored value while it is not released.
    /// Null when there is none — a released value is already on the curve.
    /// </summary>
    public static ResultHistoryCurrent? HistoryCurrent(ResultEntryRow row, string? typed)
    {
        if (TryParseTyped(typed, out var oper, out var value)
            && !string.Equals(typed?.Trim(), row.CurrentValue?.Trim(), StringComparison.Ordinal))
            return new ResultHistoryCurrent(value, oper);

        if (row.NumericValue is { } stored && row.Status is not (TestResultStatus.Validated or TestResultStatus.Reported))
            return new ResultHistoryCurrent(stored, row.ValueOperator);

        return null;
    }

    /// <summary>A point of the curve outside today's interval, drawn in the abnormal colour.</summary>
    public static bool IsOutsideInterval(decimal value, decimal? lower, decimal? upper) =>
        (lower is { } low && value < low) || (upper is { } high && value > high);
}
