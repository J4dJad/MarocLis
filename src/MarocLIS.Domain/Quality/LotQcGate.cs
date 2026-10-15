using MarocLIS.Domain.Inventory;

namespace MarocLIS.Domain.Quality;

/// <summary>One control point measured with a reagent lot.</summary>
public sealed record LotQcPoint(Guid QcResultId, Guid QcTargetId, Guid? QcRunId, QcOutcome Outcome,
                                DateTimeOffset MeasuredAt);

/// <summary>A control level configured for an analysis the lot is used for.</summary>
public sealed record RequiredQcLevel(Guid QcTargetId, string Label);

public enum LotQcVerdictKind
{
    /// <summary>Not a reagent tied to an analysis: released on the evidence typed, as before.</summary>
    NotRequired = 0,
    Conform = 1,
    /// <summary>No control was ever run with the lot.</summary>
    NoQcRun = 2,
    /// <summary>Every run on the lot has a rejected or unevaluated point.</summary>
    NoConformingRun = 3,
    /// <summary>A configured level has no conforming point on the lot, or its last one was rejected.</summary>
    LevelsNotConform = 4
}

public sealed record LotQcVerdict(LotQcVerdictKind Kind, IReadOnlyList<string> FailingLevels)
{
    public bool AllowsRelease => Kind is LotQcVerdictKind.NotRequired or LotQcVerdictKind.Conform;

    public string ResourceKey => Kind switch
    {
        LotQcVerdictKind.NoQcRun => "Lot_ReleaseNoQcRun",
        LotQcVerdictKind.NoConformingRun => "Lot_ReleaseQcRejected",
        LotQcVerdictKind.LevelsNotConform => "Lot_ReleaseQcLevelsMissing",
        _ => string.Empty
    };
}

/// <summary>
/// Whether a new reagent lot has the QC evidence ISO 15189 §5.3.2.3 expects before it touches a
/// patient sample: at least one control run on the lot with no Westgard rejection, and every
/// control level configured for the analyses using the lot conforming on it — the last point of
/// each level on the lot accepted (or only warned).
/// <para>
/// A pure function: the service gathers the points, this decides.
/// </para>
/// </summary>
public static class LotQcGate
{
    /// <summary>Only a reagent tied to at least one analysis can be checked with that analysis's QC.</summary>
    public static bool RequiresQc(StockItemKind kind, IReadOnlyCollection<string> analysisCodes) =>
        kind == StockItemKind.Reagent && analysisCodes.Count > 0;

    public static LotQcVerdict Evaluate(bool requiresQc, IReadOnlyList<RequiredQcLevel> required,
                                        IReadOnlyList<LotQcPoint> points)
    {
        if (!requiresQc) return new LotQcVerdict(LotQcVerdictKind.NotRequired, []);

        if (points.Count == 0) return new LotQcVerdict(LotQcVerdictKind.NoQcRun, []);

        // A point without a run is its own run.
        var anyConformingRun = points
            .GroupBy(p => p.QcRunId ?? p.QcResultId)
            .Any(run => run.All(p => Conforms(p.Outcome)));

        if (!anyConformingRun) return new LotQcVerdict(LotQcVerdictKind.NoConformingRun, []);

        var failing = required
            .Where(level =>
            {
                var last = points.Where(p => p.QcTargetId == level.QcTargetId)
                                 .OrderByDescending(p => p.MeasuredAt)
                                 .FirstOrDefault();
                return last is null || !Conforms(last.Outcome);
            })
            .Select(level => level.Label)
            .ToList();

        return failing.Count == 0
            ? new LotQcVerdict(LotQcVerdictKind.Conform, [])
            : new LotQcVerdict(LotQcVerdictKind.LevelsNotConform, failing);
    }

    private static bool Conforms(QcOutcome outcome) => outcome is QcOutcome.Accepted or QcOutcome.Warning;
}

/// <summary>An accepted control value, labelled with the level it belongs to.</summary>
public sealed record QcValueOnLot(Guid QcTargetId, string Label, decimal Value);

/// <summary>One control level compared between the new lot and the previous one.</summary>
public sealed record LotComparisonRow(
    string Label,
    int NewCount, decimal? NewMean, decimal? NewSd,
    int OldCount, decimal? OldMean, decimal? OldSd,
    decimal? DifferencePercent);

/// <summary>
/// The new-lot / old-lot comparison: mean, SD and number of control values per level on each lot,
/// and the shift of the mean in percent. A shift a biologist would not accept shows up here before
/// the patients' results do.
/// </summary>
public static class LotComparison
{
    public static IReadOnlyList<LotComparisonRow> Compare(IReadOnlyList<QcValueOnLot> newLot,
                                                          IReadOnlyList<QcValueOnLot> oldLot)
    {
        var labels = newLot.Concat(oldLot)
                           .GroupBy(v => v.QcTargetId)
                           .Select(g => (Id: g.Key, Label: g.First().Label))
                           .OrderBy(x => x.Label, StringComparer.Ordinal);

        var rows = new List<LotComparisonRow>();

        foreach (var (id, label) in labels)
        {
            var current = newLot.Where(v => v.QcTargetId == id).Select(v => v.Value).ToList();
            var previous = oldLot.Where(v => v.QcTargetId == id).Select(v => v.Value).ToList();

            var newMean = Mean(current);
            var oldMean = Mean(previous);

            rows.Add(new LotComparisonRow(
                label,
                current.Count, Round(newMean), Round(Sd(current)),
                previous.Count, Round(oldMean), Round(Sd(previous)),
                newMean is { } n && oldMean is { } o && o != 0m
                    ? Math.Round((n - o) / Math.Abs(o) * 100m, 2)
                    : null));
        }

        return rows;
    }

    private static decimal? Mean(List<decimal> values) =>
        values.Count == 0 ? null : values.Average();

    /// <summary>Sample standard deviation; undefined below two values.</summary>
    private static decimal? Sd(List<decimal> values)
    {
        if (values.Count < 2) return null;
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return (decimal)Math.Sqrt((double)variance);
    }

    private static decimal? Round(decimal? value) => value is { } v ? Math.Round(v, 4) : null;
}
