namespace MarocLIS.Domain.Quality;

/// <summary>
/// The Westgard multirule set. A flags enum so a laboratory can choose the combination that suits
/// each analyte's performance: running every rule on a low-precision assay produces false
/// rejections, while running only 1-3s on a high-precision one misses real shifts.
/// </summary>
[Flags]
public enum WestgardRule
{
    None = 0,

    /// <summary>1₂ₛ — one point beyond ±2 SD. A warning, not a rejection.</summary>
    OneTwoS = 1 << 0,

    /// <summary>1₃ₛ — one point beyond ±3 SD. Random error.</summary>
    OneThreeS = 1 << 1,

    /// <summary>2₂ₛ — two consecutive points beyond the same ±2 SD limit. Systematic error.</summary>
    TwoTwoS = 1 << 2,

    /// <summary>R₄ₛ — two points in the same run spanning more than 4 SD. Random error.</summary>
    R4S = 1 << 3,

    /// <summary>4₁ₛ — four consecutive points beyond the same ±1 SD limit. Systematic error.</summary>
    FourOneS = 1 << 4,

    /// <summary>10ₓ — ten consecutive points on the same side of the mean. Bias.</summary>
    TenX = 1 << 5,

    /// <summary>2of3₂ₛ — two of three consecutive points beyond the same ±2 SD limit.</summary>
    TwoOfThreeTwoS = 1 << 6,

    /// <summary>3₁ₛ — three consecutive points beyond the same ±1 SD limit.</summary>
    ThreeOneS = 1 << 7,

    /// <summary>7ₜ — seven consecutive points trending in one direction. Drift.</summary>
    SevenT = 1 << 8,

    /// <summary>8ₓ — eight consecutive points on the same side of the mean.</summary>
    EightX = 1 << 9,

    /// <summary>12ₓ — twelve consecutive points on the same side of the mean.</summary>
    TwelveX = 1 << 10
}

/// <summary>
/// The rules by the short names the bench uses (1-2s, 1-3s, R-4s…), for everything people read:
/// the enum's own names are identifiers, not labels.
/// </summary>
public static class WestgardRuleNames
{
    // Plain characters: the subscript letters are missing from the interface fonts.
    private static readonly (WestgardRule Rule, string Name)[] Names =
    [
        (WestgardRule.OneTwoS, "1-2s"), (WestgardRule.OneThreeS, "1-3s"), (WestgardRule.TwoTwoS, "2-2s"),
        (WestgardRule.R4S, "R-4s"), (WestgardRule.ThreeOneS, "3-1s"), (WestgardRule.FourOneS, "4-1s"),
        (WestgardRule.TwoOfThreeTwoS, "2/3-2s"), (WestgardRule.SevenT, "7T"), (WestgardRule.EightX, "8x"),
        (WestgardRule.TenX, "10x"), (WestgardRule.TwelveX, "12x")
    ];

    /// <summary>The short name of each rule set in <paramref name="rules"/>, in the usual order.</summary>
    public static IReadOnlyList<string> Of(WestgardRule rules) =>
        [.. Names.Where(n => (rules & n.Rule) != 0).Select(n => n.Name)];

    public static string Describe(WestgardRule rules, string separator = ", ") =>
        string.Join(separator, Of(rules));
}

/// <summary>
/// One control point reduced to what the rules actually need. Using a projection rather than the
/// entity keeps the evaluator pure and trivially testable.
/// </summary>
public readonly record struct QcPoint(
    decimal ZScore,
    DateTimeOffset MeasuredAt,
    Guid? RunId,
    QcLevel Level);

/// <summary>Outcome of evaluating a control point against the active rules.</summary>
public sealed record WestgardEvaluation(
    WestgardRule ViolatedRules,
    QcOutcome Outcome,
    IReadOnlyList<string> ViolationKeys)
{
    public static readonly WestgardEvaluation InControl =
        new(WestgardRule.None, QcOutcome.Accepted, []);

    /// <summary>Not evaluable — no target mean and SD are established yet.</summary>
    public static readonly WestgardEvaluation NotEvaluable =
        new(WestgardRule.None, QcOutcome.Pending, ["Qc_NoTargetEstablished"]);

    public bool IsRejection => Outcome == QcOutcome.Rejected;
}

/// <summary>
/// Evaluates a control point against the Westgard multirules.
/// <para>
/// Deliberately a pure function over a projected history: quality control is the part of a LIS a
/// laboratory is most likely to have to defend in an accreditation audit, so the logic must be
/// verifiable without a database, an instrument or a UI.
/// </para>
/// </summary>
public static class WestgardEvaluator
{
    /// <summary>
    /// Applies the enabled rules to the newest point.
    /// </summary>
    /// <param name="activeRules">Rules the laboratory has switched on for this target.</param>
    /// <param name="history">
    /// Points for the same target in chronological order, oldest first, with the point being
    /// evaluated as the last element. Only the trailing points the rules need are read.
    /// </param>
    /// <param name="sameRunOtherLevels">
    /// Points measured in the same run at the other control levels. Needed for R₄ₛ and for the
    /// within-run form of 2₂ₛ, which compare levels rather than consecutive runs.
    /// </param>
    public static WestgardEvaluation Evaluate(
        WestgardRule activeRules,
        IReadOnlyList<QcPoint> history,
        IReadOnlyList<QcPoint>? sameRunOtherLevels = null)
    {
        if (history.Count == 0) return WestgardEvaluation.NotEvaluable;

        var current = history[^1];
        var peers = sameRunOtherLevels ?? [];

        var violated = WestgardRule.None;
        var keys = new List<string>();

        void Fail(WestgardRule rule, string key)
        {
            violated |= rule;
            keys.Add(key);
        }

        // --- Single-point rules ---------------------------------------------------------------

        if (Enabled(activeRules, WestgardRule.OneThreeS) && Math.Abs(current.ZScore) > 3m)
            Fail(WestgardRule.OneThreeS, "Qc_Rule_1_3s");

        if (Enabled(activeRules, WestgardRule.OneTwoS) && Math.Abs(current.ZScore) > 2m)
            Fail(WestgardRule.OneTwoS, "Qc_Rule_1_2s");

        // --- 2₂ₛ: across consecutive runs at this level, or across levels within the run --------

        if (Enabled(activeRules, WestgardRule.TwoTwoS))
        {
            var acrossRuns = history.Count >= 2 && SameSideBeyond(history[^2], current, 2m);
            var withinRun = peers.Any(peer => SameSideBeyond(peer, current, 2m));
            if (acrossRuns || withinRun)
                Fail(WestgardRule.TwoTwoS, "Qc_Rule_2_2s");
        }

        // --- R₄ₛ: within-run spread over 4 SD on opposite sides of the mean --------------------

        if (Enabled(activeRules, WestgardRule.R4S))
        {
            var breached = peers.Any(peer =>
                Math.Abs(current.ZScore - peer.ZScore) > 4m &&
                Math.Sign(current.ZScore) != Math.Sign(peer.ZScore) &&
                Math.Abs(current.ZScore) > 2m && Math.Abs(peer.ZScore) > 2m);

            if (breached) Fail(WestgardRule.R4S, "Qc_Rule_R_4s");
        }

        // --- Consecutive same-side rules --------------------------------------------------------

        if (Enabled(activeRules, WestgardRule.ThreeOneS) && ConsecutiveSameSideBeyond(history, 3, 1m))
            Fail(WestgardRule.ThreeOneS, "Qc_Rule_3_1s");

        if (Enabled(activeRules, WestgardRule.FourOneS) && ConsecutiveSameSideBeyond(history, 4, 1m))
            Fail(WestgardRule.FourOneS, "Qc_Rule_4_1s");

        if (Enabled(activeRules, WestgardRule.TwoOfThreeTwoS) && TwoOfThreeBeyond(history, 2m))
            Fail(WestgardRule.TwoOfThreeTwoS, "Qc_Rule_2of3_2s");

        // --- Bias rules: runs of points on one side of the mean ---------------------------------

        if (Enabled(activeRules, WestgardRule.EightX) && ConsecutiveSameSide(history, 8))
            Fail(WestgardRule.EightX, "Qc_Rule_8x");

        if (Enabled(activeRules, WestgardRule.TenX) && ConsecutiveSameSide(history, 10))
            Fail(WestgardRule.TenX, "Qc_Rule_10x");

        if (Enabled(activeRules, WestgardRule.TwelveX) && ConsecutiveSameSide(history, 12))
            Fail(WestgardRule.TwelveX, "Qc_Rule_12x");

        // --- Drift ------------------------------------------------------------------------------

        if (Enabled(activeRules, WestgardRule.SevenT) && MonotonicTrend(history, 7))
            Fail(WestgardRule.SevenT, "Qc_Rule_7T");

        if (violated == WestgardRule.None)
            return WestgardEvaluation.InControl;

        // 1₂ₛ alone is the classic warning: it is expected to fire on about 5 % of in-control
        // points, so on its own it must not stop patient results from being released.
        var outcome = violated == WestgardRule.OneTwoS ? QcOutcome.Warning : QcOutcome.Rejected;

        return new WestgardEvaluation(violated, outcome, keys);
    }

    private static bool Enabled(WestgardRule active, WestgardRule rule) => (active & rule) == rule;

    /// <summary>Both points beyond the same limit on the same side of the mean.</summary>
    private static bool SameSideBeyond(QcPoint a, QcPoint b, decimal limit) =>
        Math.Abs(a.ZScore) > limit &&
        Math.Abs(b.ZScore) > limit &&
        Math.Sign(a.ZScore) == Math.Sign(b.ZScore);

    /// <summary>The trailing <paramref name="count"/> points all beyond the same limit, same side.</summary>
    private static bool ConsecutiveSameSideBeyond(IReadOnlyList<QcPoint> history, int count, decimal limit)
    {
        if (history.Count < count) return false;

        var sign = Math.Sign(history[^1].ZScore);
        if (sign == 0) return false;

        for (var i = history.Count - count; i < history.Count; i++)
            if (Math.Sign(history[i].ZScore) != sign || Math.Abs(history[i].ZScore) <= limit)
                return false;

        return true;
    }

    /// <summary>
    /// At least two of the trailing three points beyond the same limit on the same side. The
    /// newest point must be one of them, otherwise the rule would refire on every later point.
    /// </summary>
    private static bool TwoOfThreeBeyond(IReadOnlyList<QcPoint> history, decimal limit)
    {
        if (history.Count < 3) return false;

        var current = history[^1];
        if (Math.Abs(current.ZScore) <= limit) return false;

        var sign = Math.Sign(current.ZScore);
        var matches = 0;
        for (var i = history.Count - 3; i < history.Count; i++)
            if (Math.Sign(history[i].ZScore) == sign && Math.Abs(history[i].ZScore) > limit)
                matches++;

        return matches >= 2;
    }

    /// <summary>The trailing <paramref name="count"/> points all on the same side of the mean.</summary>
    private static bool ConsecutiveSameSide(IReadOnlyList<QcPoint> history, int count)
    {
        if (history.Count < count) return false;

        var sign = Math.Sign(history[^1].ZScore);
        if (sign == 0) return false;

        for (var i = history.Count - count; i < history.Count; i++)
            if (Math.Sign(history[i].ZScore) != sign) return false;

        return true;
    }

    /// <summary>
    /// The trailing <paramref name="count"/> points strictly increasing or strictly decreasing.
    /// A flat step breaks the trend, which is the conventional reading of 7ₜ.
    /// </summary>
    private static bool MonotonicTrend(IReadOnlyList<QcPoint> history, int count)
    {
        if (history.Count < count) return false;

        var start = history.Count - count;
        var rising = true;
        var falling = true;

        for (var i = start + 1; i < history.Count; i++)
        {
            if (history[i].ZScore <= history[i - 1].ZScore) rising = false;
            if (history[i].ZScore >= history[i - 1].ZScore) falling = false;
        }

        return rising || falling;
    }
}
