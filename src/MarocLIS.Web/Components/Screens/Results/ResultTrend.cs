using MarocLIS.Application.Results;

namespace MarocLIS.Web.Components.Screens.Results;

/// <summary>One value on the antériorité chart, the browser's ResultTrendPoint.</summary>
public sealed record TrendPoint(DateTimeOffset At, decimal Value, string? Operator = null,
                                string? AccessionNumber = null, bool Imported = false);

/// <summary>
/// What the antériorité chart draws, the browser's ResultTrendSeries: the patient's validated
/// values, the reference interval in force today, and the value on screen not validated yet.
/// The workstation's record lives beside its WinForms chart, which the web server does not load.
/// </summary>
public sealed record TrendSeries(
    IReadOnlyList<TrendPoint> Points,
    decimal? LowerLimit,
    decimal? UpperLimit,
    int Decimals,
    string? UnitSymbol,
    TrendPoint? Current = null)
{
    /// <summary>
    /// The chart's data for <paramref name="row"/>, built the way the workstation's
    /// ResultHistory.SeriesFor builds it: <paramref name="typed"/>, a value typed and not saved,
    /// wins over the stored one (ResultEntryRules.HistoryCurrent).
    /// </summary>
    public static TrendSeries For(ResultEntryRow row, IReadOnlyList<ResultHistoryPoint> history,
                                  string accessionNumber, string? typed = null)
    {
        var points = history
            .Select(p => new TrendPoint(p.ValidatedAt, p.Value, p.ValueOperator, p.AccessionNumber, p.Imported))
            .ToList();

        // Dated now: it is not released, and when it is that is the date it will carry.
        var current = ResultEntryRules.HistoryCurrent(row, typed) is { } point
            ? new TrendPoint(DateTimeOffset.Now, point.Value, point.Operator, accessionNumber)
            : null;

        return new TrendSeries(points, row.LowerLimit, row.UpperLimit, row.Decimals, row.UnitSymbol, current);
    }
}
