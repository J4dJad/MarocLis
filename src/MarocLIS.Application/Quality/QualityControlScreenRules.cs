using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Quality;

/// <summary>How a line, a value or a point of « Contrôle qualité » reads: the workstation's cell colours.</summary>
public enum QcTone
{
    Plain,

    /// <summary>Context (an accepted last value, a pending point): the secondary colour.</summary>
    Muted,

    /// <summary>In control: the normal green.</summary>
    Normal,

    /// <summary>A warning, or limits that are still the manufacturer's: the warning's colour.</summary>
    Abnormal,

    /// <summary>A rejection: the alarm's colour.</summary>
    Critical,

    /// <summary>A line filled in the alarm's colours: an unusable control lot, a rejection awaiting review.</summary>
    CriticalFill
}

/// <summary>The shape of a point on the Levey-Jennings chart: the outcome is read by shape as well as colour.</summary>
public enum QcPointShape { Circle, Triangle, Square }

/// <summary>What a control limit line means, so it is drawn by its weight: ±3 s rejects, ±2 s warns, ±1 s is context.</summary>
public enum QcLimitKind { Mean, Context, Warning, Rejection }

/// <summary>
/// What « Contrôle qualité » offers, by right and by what is on screen — the workstation's
/// PermissionGate calls and UpdateLotControls. Disabled is a hint, not the control: the service
/// re-checks each right.
/// </summary>
/// <param name="Submit">« Enregistrer » the run (entering QC).</param>
/// <param name="Review">« Revue du contrôle » of a rejection (reviewing QC, a rejection to review).</param>
/// <param name="EstablishLimits">The laboratory's own limits of the charted control (managing QC rules).</param>
/// <param name="EditRules">The Westgard rules of the charted control (managing QC rules).</param>
/// <param name="ChartMore">« Plus d'actions » of the chart: one of the two above.</param>
/// <param name="ChooseReagentLot">The reagent lot list: only who enters QC may name a lot.</param>
/// <param name="ChooseLotAnalyser">The analyser of the named lot: only once a lot is named.</param>
public sealed record QcScreenActions(
    bool Submit, bool Review, bool EstablishLimits, bool EditRules, bool ChartMore,
    bool ChooseReagentLot, bool ChooseLotAnalyser, bool StartEvaluation, bool StopEvaluation);

/// <summary>An analyser of the entry sheet, offered as the one the named lot is loaded on.</summary>
public sealed record QcAnalyserChoice(Guid Id, string Code);

/// <summary>
/// The run as typed: the entries to submit, or what refuses it — a value that is no number, or a
/// named lot without its analyser — with the field it names.
/// </summary>
public sealed record QcRunReading(IReadOnlyList<QcEntry> Entries, FieldRefusal? Refusal)
{
    /// <summary>Nothing typed and nothing refused: « Aucun élément sélectionné ».</summary>
    public bool IsEmpty => Refusal is null && Entries.Count == 0;
}

/// <summary>What a submitted run says: the rejections and the warnings to be acknowledged, and the bar's line.</summary>
/// <param name="Rejection">The rejected controls, one per line, and the corrective action they call for; null when none.</param>
/// <param name="Warnings">The warnings of the run (an expired control lot, patient results flagged); null when none.</param>
public sealed record QcSubmissionSummary(string? Rejection, string? Warnings, string Status, QcTone StatusTone);

/// <summary>
/// The rules of « Contrôle qualité » that need no window: what is offered by which right, how a
/// control of the entry sheet and a rejection awaiting review read (their target, their last
/// value, their rules, their colours), how a run typed is read and refused field by field, what a
/// submission, a review and the laboratory's limits ask and say, and how the screen's filters pick
/// the controls. The workstation's QualityControlView and the web's screen apply the same ones;
/// every decision on recording, evaluating and reviewing stays QualityControlService's and
/// WestgardEvaluator's.
/// </summary>
public static class QualityControlScreenRules
{
    /// <summary>A corrective action is a description, not a word: fifteen characters at least.</summary>
    public const int CorrectiveActionMinimumLength = 15;

    /// <summary>The longest corrective action recorded.</summary>
    public const int CorrectiveActionMaxLength = 2000;

    /// <summary>The window offered for the laboratory's own limits, in days.</summary>
    public const int DefaultLimitDays = 60;

    /// <summary>The longest window: beyond the calendar the date arithmetic threw (« Erreur inattendue »).</summary>
    public const int MaximumLimitDays = 3650;

    /// <summary>How far back the Levey-Jennings chart reads, as the service's default.</summary>
    public const int ChartDays = 60;

    /// <summary>The chart's periods the web offers, in days; the workstation shows <see cref="ChartDays"/>.</summary>
    public static readonly IReadOnlyList<int> ChartPeriods = [30, ChartDays, 90, 180];

    /// <summary>The field of the named lot's analyser, where its refusal is said.</summary>
    public const string LotAnalyserField = "lot-analyser";

    /// <summary>The field of the window of the laboratory's limits.</summary>
    public const string LimitDaysField = "limit-days";

    /// <summary>The value box of one control, where its refusal is said.</summary>
    public static string ValueField(Guid qcTargetId) => "value-" + qcTargetId.ToString("N");

    // ---- Rights --------------------------------------------------------------------------------

    /// <summary>
    /// Entering a run, reviewing a rejection and managing a control's limits and rules are three
    /// rights; the lot of a run is named by who enters it, and only on one analyser.
    /// </summary>
    /// <param name="charted">Whether a control is charted (the chart's actions act on it).</param>
    /// <param name="pendingReview">How many rejections await review.</param>
    /// <param name="namedLot">The reagent lot named for the run, null for the lot in use.</param>
    /// <param name="lotAnalyser">The analyser the named lot is loaded on.</param>
    public static QcScreenActions Actions(ICurrentUser user, bool charted, int pendingReview,
                                          Guid? namedLot, Guid? lotAnalyser)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, charted, pendingReview, namedLot, lotAnalyser);
    }

    /// <summary>The same, the rights read by <paramref name="has"/> (the workstation's PermissionGate).</summary>
    public static QcScreenActions Actions(Func<string, bool> has, bool charted, int pendingReview,
                                          Guid? namedLot, Guid? lotAnalyser)
    {
        ArgumentNullException.ThrowIfNull(has);

        var enter = has(Permissions.Quality.EnterQc);
        var manage = has(Permissions.Quality.ManageQcRules);
        var limits = manage && charted;
        var named = namedLot is not null;

        return new QcScreenActions(
            Submit: enter,
            Review: has(Permissions.Quality.ReviewQc) && pendingReview > 0,
            EstablishLimits: limits,
            EditRules: limits,
            ChartMore: limits,
            ChooseReagentLot: enter,
            ChooseLotAnalyser: named,
            StartEvaluation: enter && named && lotAnalyser is not null,
            StopEvaluation: enter && named);
    }

    // ---- The entry sheet ---------------------------------------------------------------------------

    /// <summary>« 1,00 ± 0,03 » at the control's decimals, or that no target is established.</summary>
    public static string Target(QcEntryRow row, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        return row.TargetMean is { } mean && row.TargetSd is { } sd
            ? $"{mean.ToString("N" + row.Decimals, culture)} ± {sd.ToString("N" + row.Decimals, culture)}"
            : text("Qc_NoTargetEstablished");
    }

    /// <summary>The last value at the control's decimals, empty when the control was never run.</summary>
    public static string Last(QcEntryRow row, IFormatProvider? culture = null) =>
        row.LastValue?.ToString("N" + row.Decimals, culture) ?? string.Empty;

    /// <summary>The rule set abbreviated to its count — the names are in the tooltip and under the chart.</summary>
    public static string RulesCell(QcEntryRow row) =>
        row.ActiveRules == WestgardRule.None ? "—" : CountRules(row.ActiveRules).ToString(CultureInfo.InvariantCulture);

    /// <summary>The rule set's tooltip: the rules, the CV against its maximum, the points, an expired lot.</summary>
    public static IReadOnlyList<string> RulesTip(QcEntryRow row, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<string> { WestgardRuleNames.Describe(row.ActiveRules, " · ") };

        if (row.CvPercent is { } cv)
            lines.Add(string.Format(culture, "CV {0:N2} %", cv)
                      + (row.MaxAcceptableCvPercent is { } max ? string.Format(culture, " / {0:N2} %", max) : string.Empty));

        lines.Add($"{row.PointCount} {text("Quality.PointCount").ToLowerInvariant()}");

        if (!row.MaterialUsable) lines.Add(text("Qc_MaterialExpired", row.MaterialLotNumber));

        return lines;
    }

    /// <summary>
    /// An unusable control lot fills its line in the alarm's colours: running an expired control
    /// produces a verdict nobody can rely on, and it is said on the line, not in a tooltip.
    /// </summary>
    public static QcTone Tone(QcEntryRow row) =>
        row.MaterialUsable ? QcTone.Plain : QcTone.CriticalFill;

    /// <summary>
    /// The target in the warning's colour while it is the manufacturer's: ISO 15189 expects the
    /// laboratory to establish its own.
    /// </summary>
    public static QcTone TargetTone(QcEntryRow row) =>
        row.MaterialUsable && row.UsingAssignedStatistics ? QcTone.Abnormal : QcTone.Plain;

    /// <summary>The last value in the colour of its verdict.</summary>
    public static QcTone LastTone(QcEntryRow row) => row.LastOutcome switch
    {
        QcOutcome.Rejected => QcTone.Critical,
        QcOutcome.Warning => QcTone.Abnormal,
        null => QcTone.Plain,
        _ => QcTone.Muted
    };

    /// <summary>« 4 contrôles · 1 rejet à revoir ».</summary>
    public static string Count(int controls, int pendingReview, ScreenText text) =>
        text("QualityHr.Qc.Count", controls, pendingReview);

    /// <summary>The rules in force for the control charted, by their short names; none is said in the alarm's colour.</summary>
    public static (string Text, QcTone Tone) ActiveRules(QcEntryRow? row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (row is null) return (string.Empty, QcTone.Plain);

        var active = WestgardRuleNames.Of(row.ActiveRules);
        return (text("QualityHr.Qc.ActiveRules", active.Count == 0 ? "—" : string.Join(" · ", active)),
                active.Count == 0 ? QcTone.Critical : QcTone.Muted);
    }

    public static int CountRules(WestgardRule rules) =>
        Enum.GetValues<WestgardRule>().Count(r => r != WestgardRule.None && rules.HasFlag(r));

    // ---- Rejections awaiting review ----------------------------------------------------------------

    /// <summary>When the rejected control was measured, as the review list writes it.</summary>
    public static string Measured(QcReviewRow row) =>
        row.MeasuredAt.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    public static string Value(QcReviewRow row, IFormatProvider? culture = null) => row.Value.ToString("N3", culture);

    public static string ZScore(decimal? z, IFormatProvider? culture = null) => z?.ToString("N2", culture) ?? string.Empty;

    public static string Affected(QcReviewRow row) =>
        row.AffectedResultCount > 0 ? row.AffectedResultCount.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>The patient results the rejections hold back, said where the reviewer looks first; null when none.</summary>
    public static string? Blocked(IEnumerable<QcReviewRow> pending, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(text);

        var blocked = pending.Sum(r => r.AffectedResultCount);
        return blocked > 0 ? text("QualityHr.Qc.Blocked", blocked) : null;
    }

    /// <summary>
    /// What the review asks: the control and its rules, then the corrective action. Mandatory, and
    /// long enough to describe what was done (<see cref="CorrectiveActionMinimumLength"/>).
    /// </summary>
    public static string ReviewPrompt(QcReviewRow row, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        return $"{row.ParameterName.For(language)} · {WestgardRuleNames.Describe(row.ViolatedRules)}\n\n"
               + text("Quality.CorrectiveActionPrompt");
    }

    /// <summary>
    /// A rejection that held back patient results opens a non-conformity without asking: a departure
    /// has demonstrably occurred. Otherwise the reviewer is asked.
    /// </summary>
    public static bool OpensNonConformityWithoutAsking(QcReviewRow row) => row.AffectedResultCount > 0;

    // ---- The run --------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the values typed into a run. A lot named for the run is the lot of one analyser's rows
    /// — the other analysers' rows are routine QC — so a named lot without its analyser is refused
    /// first; then a value that is no number, on its own box. An empty box is a control not run.
    /// </summary>
    /// <param name="typed">The text typed for a control, by its target.</param>
    public static QcRunReading ReadRun(IEnumerable<QcEntryRow> rows, Func<Guid, string?> typed,
                                       Guid? namedLot, Guid? lotAnalyser)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(typed);

        if (namedLot is not null && lotAnalyser is null)
            return new QcRunReading([], new FieldRefusal(LotAnalyserField, "Qc.ChooseLotAnalyser"));

        var entries = new List<QcEntry>();

        foreach (var row in rows)
        {
            var text = typed(row.QcTargetId)?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (TypedNumber.Parse(text) is not { } value)
                return new QcRunReading([], new FieldRefusal(ValueField(row.QcTargetId), "Result_NotANumber", text));

            entries.Add(new QcEntry(row.QcTargetId, value, row.InstrumentId == lotAnalyser ? namedLot : null));
        }

        return new QcRunReading(entries, null);
    }

    /// <summary>Whether a value is typed and not yet submitted: leaving the screen then asks first.</summary>
    public static bool HasTyped(IEnumerable<string?> typed) =>
        typed.Any(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>
    /// The box a refused submission names: a value the service found technically impossible is
    /// said on the control it was typed for; any other refusal belongs to the run as a whole.
    /// </summary>
    public static FieldRefusal? FieldOf(Error error, IEnumerable<QcEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(entries);

        if (error.ResourceKey != "Result_TechnicallyImpossible" || error.Args is not [decimal value, ..]) return null;

        return entries.FirstOrDefault(e => e.Value == value) is { } entry
            ? new FieldRefusal(ValueField(entry.QcTargetId), error.ResourceKey, value.ToString(CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// What a run says once recorded. A rejection is announced so it is acted on rather than noticed
    /// later — the patient results the run covers are now held back — each rejected control on its
    /// line with the rules it broke; the warnings follow; the bar counts the accepted controls.
    /// </summary>
    /// <param name="describe">The screen's wording of a list of errors (the workstation's Localizer.Describe).</param>
    public static QcSubmissionSummary Summarise(QcSubmissionOutcome outcome, ScreenText text,
                                                Func<IReadOnlyList<Error>, string> describe,
                                                IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(describe);

        string? rejection = null;

        if (outcome.AnyRejected)
        {
            var lines = outcome.Results
                .Where(r => r.Outcome == QcOutcome.Rejected)
                .Select(r => string.Format(culture, "• {0} : {1:N3} (Z {2:N2}) — ", r.ParameterCode, r.Value, r.ZScore)
                             + describe([.. r.ViolationKeys.Select(k => Error.Regulatory(k))]));

            rejection = $"{text("QcOutcome.Rejected")}\n\n{string.Join('\n', lines)}\n\n"
                        + text("Qc_RejectionRequiresCorrectiveAction");
        }

        var warnings = outcome.Warnings.Count > 0 ? describe(outcome.Warnings) : null;
        var accepted = outcome.Results.Count(r => r.Outcome == QcOutcome.Accepted);

        return new QcSubmissionSummary(
            rejection, warnings,
            $"{accepted}/{outcome.Results.Count} {text("QcOutcome.Accepted").ToLowerInvariant()}",
            outcome.AnyRejected ? QcTone.Critical : QcTone.Normal);
    }

    // ---- The reagent lot of the run -------------------------------------------------------------------

    /// <summary>« Lot de réactif en cours (automatique) », or « CODE · LOT », said quarantined when it is.</summary>
    public static string LotLabel(ReagentLotOption? lot, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return lot is null
            ? text("Qc.ReagentLotInUse")
            : $"{lot.ItemCode} · {lot.LotNumber}"
              + (lot.IsQuarantined ? $" ({text("Inventory.Quarantined").ToLowerInvariant()})" : string.Empty);
    }

    /// <summary>The analysers of the sheet, by code: the one the named lot is loaded on is chosen among them.</summary>
    public static IReadOnlyList<QcAnalyserChoice> LotAnalysers(IEnumerable<QcEntryRow> rows, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return [.. rows.GroupBy(r => r.InstrumentId)
                       .Select(g => new QcAnalyserChoice(g.Key, g.First().InstrumentCode))
                       .OrderBy(a => a.Code, StringComparer.Create(culture ?? CultureInfo.CurrentCulture, false))];
    }

    /// <summary>The analyser kept on a reload, or the only one of the sheet; otherwise none, to be chosen.</summary>
    public static Guid? PickAnalyser(IReadOnlyList<QcAnalyserChoice> analysers, Guid? keep)
    {
        ArgumentNullException.ThrowIfNull(analysers);

        if (keep is { } kept && analysers.Any(a => a.Id == kept)) return kept;
        return analysers.Count == 1 ? analysers[0].Id : null;
    }

    // ---- The laboratory's limits ----------------------------------------------------------------------

    /// <summary>The question, naming the control charted — whichever list it was picked in.</summary>
    public static string LimitsPrompt(QcEntryRow row, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);

        return $"{row.ParameterName.For(language)} · {text($"QcLevel.{row.Level}")} · {row.MaterialLotNumber} · {row.InstrumentCode}\n\n"
               + text("Quality.EstablishLimitsPrompt", QcTarget.MinimumPointsForLaboratoryStatistics);
    }

    /// <summary>
    /// The window typed, in days: a whole number from 1 to <see cref="MaximumLimitDays"/>, or the
    /// key of what refuses it, quoting what was typed.
    /// </summary>
    public static (int? Days, FieldRefusal? Refusal) ReadLimitDays(string? typed)
    {
        var text = typed?.Trim() ?? string.Empty;

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1)
            return (null, new FieldRefusal(LimitDaysField, "Result_NotANumber", text));

        return days > MaximumLimitDays
            ? (null, new FieldRefusal(LimitDaysField, "QcTarget_PeriodOutOfRange"))
            : (days, null);
    }

    /// <summary>The window ending today.</summary>
    public static (DateOnly From, DateOnly To) LimitsWindow(int days, DateOnly today) => (today.AddDays(-days), today);

    /// <summary>The limits established, said back: mean, SD and the points they rest on.</summary>
    public static string LimitsSummary(decimal mean, decimal sd, int points, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Format(culture, "{0} : {1:N4}\n{2} : {3:N4}\n{4} : {5}",
                             text("Quality.LaboratoryMean"), mean, text("Quality.LaboratorySd"), sd,
                             text("Quality.PointCount"), points);
    }

    // ---- Filters (the web's) --------------------------------------------------------------------------

    /// <summary>
    /// Whether a control is shown: its analyte (code or name, in the screen's language), its lot or
    /// its analyser contains the text; and it is of the analyser and the level chosen, if any.
    /// </summary>
    public static bool Matches(QcEntryRow row, string? search, AppLanguage language,
                               Guid? instrumentId = null, QcLevel? level = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (instrumentId is { } instrument && row.InstrumentId != instrument) return false;
        if (level is { } wanted && row.Level != wanted) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;

        var term = search.Trim();
        return row.ParameterCode.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.ParameterName.For(language).Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || row.MaterialLotNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.InstrumentCode.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The levels of the sheet, in their order, for the level filter.</summary>
    public static IReadOnlyList<QcLevel> Levels(IEnumerable<QcEntryRow> rows) =>
        [.. rows.Select(r => r.Level).Distinct().Order()];

    /// <summary>A rejection awaiting review, kept by the same filters as the sheet.</summary>
    public static bool Matches(QcReviewRow row, string? search, AppLanguage language,
                               Guid? instrumentId, QcLevel? level, IReadOnlyCollection<QcEntryRow> sheet)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(sheet);

        if (level is { } wanted && row.Level != wanted) return false;
        if (instrumentId is { } instrument
            && sheet.FirstOrDefault(r => r.QcTargetId == row.QcTargetId) is { } entry && entry.InstrumentId != instrument)
            return false;
        if (string.IsNullOrWhiteSpace(search)) return true;

        var term = search.Trim();
        return row.ParameterCode.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.ParameterName.For(language).Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || row.MaterialLotNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.InstrumentCode.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The Levey-Jennings chart, laid out: where a point and a limit go, what they are labelled, what
/// a point says under the pointer. The workstation paints it and the web draws it in SVG from the
/// same figures.
/// <para>
/// The Y axis is in Z-score units, so a chart looks the same whatever the analyte; a point beyond
/// ±4 SD is held at the edge rather than rescaling the axis and flattening every other point. The
/// limits are weighted by what they mean — ±3 s rejects, ±2 s warns, ±1 s is context — and labelled
/// with their value in measurement units, which is what the technician compares with the analyser.
/// </para>
/// </summary>
public static class LeveyJenningsLayout
{
    /// <summary>The chart runs from −4 to +4 SD.</summary>
    public const decimal ZRange = 4m;

    /// <summary>The limit lines, top to bottom.</summary>
    public static readonly IReadOnlyList<decimal> Limits = [3m, 2m, 1m, 0m, -1m, -2m, -3m];

    /// <summary>The most dates under the chart: more and they collide; each point's own is on hover.</summary>
    public const int MaximumDateLabels = 6;

    /// <summary>Whether the points can be placed on a Z axis: a mean and a non-zero SD are established.</summary>
    public static bool CanPlot(LeveyJenningsSeries? series) =>
        series is { Mean: not null, Sd: { } sd } && sd != 0m;

    /// <summary>
    /// What the chart says in place of points: nothing charted (« — »), or that no target is
    /// established — better than a chart implying limits that do not exist. Null when it plots.
    /// </summary>
    public static string? EmptyText(LeveyJenningsSeries? series, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (series is null) return "—";
        if (!CanPlot(series)) return text("Qc.ChartNoTarget");
        return series.Points.Count == 0 ? "—" : null;
    }

    public static QcLimitKind KindOf(decimal z) => Math.Abs(z) switch
    {
        0m => QcLimitKind.Mean,
        3m => QcLimitKind.Rejection,
        2m => QcLimitKind.Warning,
        _ => QcLimitKind.Context
    };

    /// <summary>« x̄  1,00 », « +2s  1,06 »: the SD multiple and its value in measurement units.</summary>
    public static string LimitLabel(LeveyJenningsSeries series, decimal z, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(series);

        var value = (series.Mean ?? 0m) + z * (series.Sd ?? 0m);
        var shown = value.ToString("N" + series.Decimals, culture);

        return z == 0m
            ? $"x̄  {shown}"
            : $"{(z > 0 ? "+" : string.Empty)}{z.ToString("0", CultureInfo.InvariantCulture)}s  {shown}";
    }

    /// <summary>The horizontal position of a point, its index among <paramref name="count"/>, 12 px in from each side.</summary>
    public static double X(int index, int count, double left, double width) =>
        count <= 1 ? left + width / 2 : left + 12 + (width - 24) * index / (count - 1);

    /// <summary>The vertical position of a Z-score, held inside ±<see cref="ZRange"/>, 8 px in from the edges.</summary>
    public static double Y(decimal z, double top, double height)
    {
        var clamped = Math.Clamp(z, -ZRange, ZRange);
        return top + height / 2 - (double)clamped / (double)ZRange * (height / 2 - 8);
    }

    /// <summary>The points that carry a date under the chart.</summary>
    public static IReadOnlyList<int> DateTicks(int count)
    {
        var step = Math.Max(1, count / MaximumDateLabels);
        return [.. Enumerable.Range(0, Math.Max(0, count)).Where(i => i % step == 0)];
    }

    public static string DateLabel(LeveyJenningsPoint point) =>
        point.MeasuredAt.ToString("dd/MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// The line above the points: the analyte, the lot, the analyser, the CV, and whose limits these
    /// are — the manufacturer's insert or the laboratory's own, which ISO 15189 §5.6 cares about.
    /// </summary>
    public static string Header(LeveyJenningsSeries series, ScreenText text, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(text);

        var cv = series.CvPercent is { } value ? string.Format(culture, "CV {0:N2} %", value) : string.Empty;
        var basis = text(series.UsingAssignedStatistics ? "Quality.SupplierLimits" : "Quality.LaboratoryLimits").ToLowerInvariant();

        return string.Join("   ·   ",
            new[] { series.ParameterCode, series.MaterialLotNumber, series.InstrumentCode, cv, basis }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public static QcPointShape Shape(QcOutcome outcome) => outcome switch
    {
        QcOutcome.Rejected => QcPointShape.Square,
        QcOutcome.Warning => QcPointShape.Triangle,
        _ => QcPointShape.Circle
    };

    public static QcTone Tone(QcOutcome outcome) => outcome switch
    {
        QcOutcome.Rejected => QcTone.Critical,
        QcOutcome.Warning => QcTone.Abnormal,
        QcOutcome.Accepted => QcTone.Normal,
        _ => QcTone.Muted
    };

    /// <summary>What a point says under the pointer: the value and unit, Z, when, the rules it broke, a derogation, its comment.</summary>
    public static IReadOnlyList<string> PointTip(LeveyJenningsPoint point, LeveyJenningsSeries series, ScreenText text,
                                                 IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<string>
        {
            $"{point.Value.ToString("N" + series.Decimals, culture)} {series.UnitSymbol}".Trim(),
            string.Format(culture, "Z = {0:N2}", point.ZScore),
            point.MeasuredAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        };

        if (point.ViolatedRules != WestgardRule.None) lines.Add(WestgardRuleNames.Describe(point.ViolatedRules));
        if (point.WasOverridden) lines.Add(text("Qc.Overridden"));
        if (!string.IsNullOrWhiteSpace(point.Comment)) lines.Add(point.Comment);

        return lines;
    }

    /// <summary>The point picked, on one line — the workstation's status line — in the colour of a rejection or not.</summary>
    public static (string Text, QcTone Tone) PointLine(LeveyJenningsPoint point, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(point);

        var text = point.MeasuredAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                   + string.Format(culture, " · {0:N3} · Z {1:N2}", point.Value, point.ZScore)
                   + (point.ViolatedRules == WestgardRule.None ? string.Empty : $" · {WestgardRuleNames.Describe(point.ViolatedRules)}");

        return (text, point.Outcome == QcOutcome.Rejected ? QcTone.Critical : QcTone.Muted);
    }
}
