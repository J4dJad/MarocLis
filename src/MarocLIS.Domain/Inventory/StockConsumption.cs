using System.Globalization;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// How an item is consumed and replenished, beyond the manual reorder fields: what a result costs
/// in tests, what is lost each time a vial is opened, the safety margin and the shelf life a
/// delivery usually arrives with. Held on <see cref="StockItem"/>; this record is what the editor
/// and the service pass around.
/// </summary>
public sealed record StockConsumptionPolicy
{
    /// <summary>Share of a pack lost to dead volume each time one is opened, in percent (0–99).</summary>
    public decimal? DeadVolumePercent { get; init; }

    /// <summary>Tests lost each time a pack is opened (priming, dead volume), when known as a count.</summary>
    public decimal? DeadVolumeTestsPerOpening { get; init; }

    /// <summary>
    /// Tests consumed per result for the analyses that take more than one, e.g. "HBA1C=2; TSH=1.5".
    /// An analysis not listed takes one test per result.
    /// </summary>
    public string? TestsPerResultCsv { get; init; }

    /// <summary>Tests one calibration of an analysis uses (levels × replicates). One when unset.</summary>
    public int? TestsPerCalibration { get; init; }

    /// <summary>Safety stock as days of average consumption; the minimum stock is used when unset.</summary>
    public int? SafetyStockDays { get; init; }

    /// <summary>Shelf life a delivery typically has left on arrival, in days; caps the quantity ordered.</summary>
    public int? TypicalShelfLifeDays { get; init; }

    /// <summary>The reorder point is recomputed from the consumption rather than typed.</summary>
    public bool ReorderPointComputed { get; init; }

    public Result Validate()
    {
        var errors = new List<Error>();

        if (DeadVolumePercent is < 0m or >= 100m)
            errors.Add(Error.Validation("Stock_DeadVolumeOutOfRange"));
        if (DeadVolumeTestsPerOpening is < 0m)
            errors.Add(Error.Validation("Stock_DeadVolumeOutOfRange"));
        if (TestsPerCalibration is < 0 or > 1000)
            errors.Add(Error.Validation("Stock_PolicyValueOutOfRange"));
        if (SafetyStockDays is < 0 or > 365 || TypicalShelfLifeDays is < 1 or > 3650)
            errors.Add(Error.Validation("Stock_PolicyValueOutOfRange"));

        var factors = ConsumptionFactors.Parse(TestsPerResultCsv);
        if (factors.IsFailure) errors.AddRange(factors.Errors);

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

/// <summary>Reads the "tests per result" list of an item: <c>CODE=factor</c> entries.</summary>
public static class ConsumptionFactors
{
    private static readonly char[] Separators = [';', ',', ' ', '\t', '\r', '\n', '|'];

    public static Result<IReadOnlyDictionary<string, decimal>> Parse(string? csv)
    {
        var factors = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(csv)) return Result.Success<IReadOnlyDictionary<string, decimal>>(factors);

        foreach (var entry in csv.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(['=', ':'], 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0
                || !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var factor)
                || factor <= 0m || factor > 1000m)
                return Result.Failure<IReadOnlyDictionary<string, decimal>>(
                    Error.Validation("Stock_ConsumptionFactorInvalid", entry));

            factors[parts[0].ToUpperInvariant()] = factor;
        }

        return Result.Success<IReadOnlyDictionary<string, decimal>>(factors);
    }

    /// <summary>The factor for an analysis: the one listed, otherwise one. An unreadable list counts as empty.</summary>
    public static decimal For(string? csv, string analysisCode)
    {
        var parsed = Parse(csv);
        return parsed.IsSuccess && parsed.Value.TryGetValue(analysisCode.Trim().ToUpperInvariant(), out var factor)
            ? factor
            : 1m;
    }
}

/// <summary>What an analysis ran in a period, as counted from the results, the QC and the calibrations.</summary>
/// <param name="PatientTests">Ordered analyses with a value entered (any validation stage), once each.</param>
/// <param name="QcTests">Control levels run for the analysis.</param>
/// <param name="Calibrations">Calibrations of the analysis' parameters.</param>
/// <param name="Reruns">Repeats of the patient analyses (an analysis sent back to the bench).</param>
public sealed record AnalysisActivity(string AnalysisCode, int PatientTests, int QcTests, int Calibrations, int Reruns);

/// <summary>A theoretical consumption, in tests by origin and in the item's units.</summary>
public sealed record ConsumptionBreakdown(
    decimal PatientTests,
    decimal QcTests,
    decimal CalibrationTests,
    decimal RerunTests,
    decimal DeadVolumeTests,
    decimal Openings,
    decimal TestsPerUnit)
{
    public static readonly ConsumptionBreakdown None = new(0m, 0m, 0m, 0m, 0m, 0m, 1m);

    public decimal TotalTests => PatientTests + QcTests + CalibrationTests + RerunTests + DeadVolumeTests;

    /// <summary>The consumption in the unit stock is counted in (packs, vials), to three decimals.</summary>
    public decimal Units => Math.Round(TotalTests / TestsPerUnit, 3);
}

/// <summary>
/// The consumption the activity explains: every patient result, every control level, every
/// calibration and every rerun takes its tests, and each pack opened loses its dead volume. It is
/// computed, never posted: the stock moves stay what people recorded, and the difference between
/// the two is the point of the comparison.
/// </summary>
public static class TheoreticalConsumption
{
    /// <param name="openings">
    /// Packs actually opened in the period (the units issued), when the issues were recorded;
    /// otherwise the packs the tests needed are taken as opened.
    /// </param>
    public static ConsumptionBreakdown Compute(StockConsumptionPolicy policy, int? testsPerPack,
                                               IEnumerable<AnalysisActivity> activity, decimal? openings = null)
    {
        var perUnit = testsPerPack is > 0 ? testsPerPack.Value : 1m;
        var perCalibration = policy.TestsPerCalibration is > 0 ? policy.TestsPerCalibration.Value : 1m;

        decimal patients = 0m, qc = 0m, calibrations = 0m, reruns = 0m;

        foreach (var analysis in activity)
        {
            var factor = ConsumptionFactors.For(policy.TestsPerResultCsv, analysis.AnalysisCode);
            patients += analysis.PatientTests * factor;
            qc += analysis.QcTests * factor;
            calibrations += analysis.Calibrations * perCalibration * factor;
            reruns += analysis.Reruns * factor;
        }

        var used = patients + qc + calibrations + reruns;
        var opened = openings is > 0m ? openings.Value : used > 0m ? Math.Ceiling(used / perUnit) : 0m;

        var dead = opened * (policy.DeadVolumeTestsPerOpening ?? 0m)
                   + opened * perUnit * (policy.DeadVolumePercent ?? 0m) / 100m;

        return new ConsumptionBreakdown(patients, qc, calibrations, reruns, Math.Round(dead, 2), opened, perUnit);
    }
}

/// <summary>
/// Theoretical against real stock over a period: what should be left, given what was there, what
/// came in and what the activity used, against what is actually there.
/// </summary>
/// <param name="Computable">The item names the analyses it serves; without them there is no theoretical figure.</param>
public sealed record StockReconciliation(
    decimal Opening,
    decimal Receipts,
    decimal TheoreticalConsumption,
    decimal RealClosing,
    decimal ThresholdPercent,
    bool Computable = true)
{
    public decimal TheoreticalClosing => Opening + Receipts - TheoreticalConsumption;

    /// <summary>What really went: issues, write-offs, losses, whatever the ledger and the counts say.</summary>
    public decimal RealConsumption => Opening + Receipts - RealClosing;

    /// <summary>Real minus theoretical closing: negative when more went than the activity explains.</summary>
    public decimal Variance => Math.Round(RealClosing - TheoreticalClosing, 3);

    /// <summary>The variance as a share of the theoretical consumption, in percent.</summary>
    public decimal? VariancePercent =>
        TheoreticalConsumption == 0m ? null : Math.Round(Variance / TheoreticalConsumption * 100m, 1);

    /// <summary>
    /// Beyond the threshold. With no theoretical consumption at all, any difference is flagged:
    /// stock went that no result explains.
    /// </summary>
    public bool IsFlagged =>
        Computable && (VariancePercent is { } percent ? Math.Abs(percent) > ThresholdPercent : Variance != 0m);
}

/// <summary>A reorder point worked out from the consumption, with the figures behind it.</summary>
public sealed record ReorderSuggestion(
    decimal AverageDailyConsumption,
    int LeadTimeDays,
    decimal SafetyStock,
    decimal ReorderPoint,
    bool FromTheoretical);

/// <summary>
/// The reorder rules that depend on the consumption: the computed reorder point, and the cap that
/// keeps an order from bringing in more than will be used before it expires.
/// </summary>
public static class ReorderPolicy
{
    /// <summary>
    /// Average units used per day: the real issues when there were any, otherwise the theoretical
    /// consumption (a new item, or a laboratory that does not record its issues). Null when neither
    /// says anything.
    /// </summary>
    public static (decimal Daily, bool FromTheoretical)? AverageDailyConsumption(decimal realIssued,
                                                                                   decimal theoreticalUnits,
                                                                                   int days)
    {
        if (days <= 0) return null;
        if (realIssued > 0m) return (Math.Round(realIssued / days, 4), false);
        if (theoreticalUnits > 0m) return (Math.Round(theoreticalUnits / days, 4), true);
        return null;
    }

    /// <summary>
    /// Average daily consumption × supplier lead time + safety stock, rounded up to a whole unit.
    /// The safety stock is the safety days' worth of consumption when set, otherwise the minimum stock.
    /// Never below the minimum stock: the alert has to fire before the laboratory goes under it.
    /// </summary>
    public static ReorderSuggestion Suggest(decimal averageDaily, bool fromTheoretical, int leadTimeDays,
                                            int? safetyStockDays, decimal minimumStock)
    {
        var lead = Math.Max(0, leadTimeDays);
        var safety = safetyStockDays is { } days ? Math.Round(averageDaily * days, 3) : minimumStock;
        var point = Math.Max(Math.Ceiling(averageDaily * lead + safety), Math.Ceiling(minimumStock));
        return new ReorderSuggestion(averageDaily, lead, safety, point, fromTheoretical);
    }

    /// <summary>
    /// Caps a proposed quantity at what will be used before the delivery expires: the daily
    /// consumption over the shelf life it arrives with, less the usable stock already there. Never
    /// below one unit: an item at its reorder point still needs ordering.
    /// </summary>
    /// <returns>The quantity to order, and the cap when it applied.</returns>
    public static (decimal Quantity, decimal? Cap) CapByShelfLife(decimal proposed, decimal? averageDaily,
                                                                  int? shelfLifeDays, decimal usable)
    {
        if (averageDaily is not > 0m || shelfLifeDays is not > 0) return (proposed, null);

        var cap = Math.Max(1m, Math.Floor(averageDaily.Value * shelfLifeDays.Value - usable));
        return proposed > cap ? (cap, cap) : (proposed, null);
    }
}

/// <summary>The two expiry levels of the alerts, and the stock-out forecast.</summary>
public enum ExpiryAlertLevel
{
    None = 0,
    /// <summary>Within the first threshold (J−30 by default).</summary>
    Warning = 1,
    /// <summary>Within the second threshold (J−7 by default).</summary>
    Critical = 2,
    Expired = 3
}

public static class StockAlerts
{
    public static ExpiryAlertLevel ExpiryLevel(DateOnly expiry, DateOnly today, int warningDays, int criticalDays)
    {
        if (expiry < today) return ExpiryAlertLevel.Expired;
        var days = expiry.DayNumber - today.DayNumber;
        if (days <= criticalDays) return ExpiryAlertLevel.Critical;
        return days <= warningDays ? ExpiryAlertLevel.Warning : ExpiryAlertLevel.None;
    }

    /// <summary>The day the usable stock runs out at the average consumption; null without consumption.</summary>
    public static DateOnly? PredictedStockOut(decimal usable, decimal? averageDaily, DateOnly today)
    {
        if (averageDaily is not > 0m) return null;
        var days = (int)Math.Min(36_500m, Math.Floor(Math.Max(0m, usable) / averageDaily.Value));
        return today.AddDays(days);
    }

    /// <summary>The stock runs out before an order placed today could arrive.</summary>
    public static bool RunsOutBeforeDelivery(DateOnly? stockOut, DateOnly today, int leadTimeDays) =>
        stockOut is { } date && date < today.AddDays(Math.Max(0, leadTimeDays));
}
