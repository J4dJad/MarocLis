using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Quality;

/// <summary>
/// The judgement of one run of internal quality control, the same whether the values were typed on
/// the QC screen or sent by the analyser: each point anchored on the target's effective limits (the
/// laboratory's own once established), its Z-score evaluated with the Westgard rules against its
/// history and the run's other levels, an in-control routine point folded into the laboratory's
/// statistics, and the patient results a rejected routine run covers flagged.
/// <para>
/// Analyser QC used to be stored against the insert's limits with no Z-score and no evaluation: it
/// stayed « en attente », reached no review list, flagged no patient result, and was left out of
/// the history the rules read for later points.
/// </para>
/// </summary>
public static class QcRunEvaluator
{
    /// <summary>The value and Z-score columns hold twelve integer digits; a real control is far inside both.</summary>
    public const decimal MaximumQcValue = 10_000_000_000m;

    public const decimal MaximumZScore = 1_000_000m;

    /// <summary>A value no control can have — a key held down, a garbled field — against this target.</summary>
    public static bool IsImplausible(QcTarget? target, decimal value) =>
        Math.Abs(value) >= MaximumQcValue
        || target?.ZScore(value) is { } z && Math.Abs(z) >= MaximumZScore;

    /// <summary>
    /// Freezes on the point the limits it is judged against, so the chart stays reproducible after
    /// the statistics are recomputed, and its Z-score; both null when the target has no limits.
    /// </summary>
    public static void Anchor(QcResult point, QcTarget target, Guid runId)
    {
        point.QcRunId = runId;
        point.TargetMean = target.EffectiveMean;
        point.TargetSd = target.EffectiveSd;
        point.ZScore = target.ZScore(point.Value);
    }

    /// <summary>
    /// Evaluates every point of the run (anchored first with <see cref="Anchor"/>), folds the
    /// in-control routine points into their target's statistics, and flags the patient results
    /// each analyser's rejected routine points cover. Returns each point's evaluation and the
    /// results flagged.
    /// </summary>
    public static async Task<QcRunEvaluation> EvaluateAsync(
        IQualityQueries queries, IReadOnlyList<(QcResult Point, QcTarget Target)> run, Guid runId,
        CancellationToken ct = default)
    {
        var evaluations = new Dictionary<Guid, WestgardEvaluation>(run.Count);

        foreach (var (point, target) in run)
        {
            if (point.ZScore is not { } z)
            {
                // No limits established: the point is stored but cannot be interpreted. Silently
                // treating that as in control would be the worst of the options.
                point.ApplyEvaluation(WestgardEvaluation.NotEvaluable);
                evaluations[point.Id] = WestgardEvaluation.NotEvaluable;
                continue;
            }

            // A new lot's acceptance points are a series of their own.
            var history = await queries.GetZScoreHistoryAsync(
                target.Id, 12, point.IsLotEvaluation ? point.ReagentLotId : null, ct);

            // The point is the newest element of the history the rules evaluate.
            var series = new List<QcPoint>(history)
            {
                new(z, point.MeasuredAt, runId, await queries.GetMaterialLevelAsync(target.QcMaterialId, ct))
            };

            // The run's other levels of the same analyte on the same analyser and reagent lot, and
            // only those: a glucose at +2.1 SD and a creatinine at -2.1 SD are not one run's levels.
            var peers = run
                .Where(x => x.Point.Id != point.Id && x.Point.ZScore is not null
                            && x.Point.TestParameterId == point.TestParameterId
                            && x.Point.InstrumentId == point.InstrumentId
                            && x.Point.IsLotEvaluation == point.IsLotEvaluation
                            && x.Point.ReagentLotId == point.ReagentLotId)
                .Select(x => new QcPoint(x.Point.ZScore!.Value, x.Point.MeasuredAt, runId, QcLevel.Normal))
                .ToList();

            var evaluation = WestgardEvaluator.Evaluate(target.ActiveRules, series, peers);
            point.ApplyEvaluation(evaluation);
            evaluations[point.Id] = evaluation;

            // Only accepted points shape the laboratory's own mean and SD: a rejected point would
            // widen the limits to accommodate the very error detected. Nor does a new lot's
            // acceptance point: the limits are those of the lot in use.
            if (evaluation.Outcome is QcOutcome.Accepted or QcOutcome.Warning && !point.IsLotEvaluation)
                target.AccumulatePoint(point.Value);
        }

        // Grouped by analyser: each rejected control speaks for its own instrument only. A new
        // lot's rejected acceptance run flags nothing: no patient result was produced with it.
        var flagged = new List<Domain.Results.TestResult>();

        foreach (var instrument in run.Where(x => x.Point.IsRejected && !x.Point.IsLotEvaluation)
                                      .GroupBy(x => x.Point.InstrumentId, x => x.Target.TestParameterId))
        {
            flagged.AddRange(await queries.FlagResultsCoveredByRejectedQcAsync(
                [.. instrument.Distinct()], instrument.Key, runId, ct));
        }

        return new QcRunEvaluation(evaluations, flagged);
    }
}

/// <summary>What a run's evaluation decided: each point's evaluation, by point, and the patient results flagged.</summary>
public sealed record QcRunEvaluation(
    IReadOnlyDictionary<Guid, WestgardEvaluation> Evaluations,
    IReadOnlyList<Domain.Results.TestResult> FlaggedResults);
