namespace MarocLIS.Domain.Inventory;

/// <summary>
/// Which reagent lot a result was produced with, worked out from the stock rather than typed.
/// <para>
/// The rule: the reagent articles whose "used by analyses" list names the analysis; if exactly one,
/// its opened, released, in-date lot, first-expiry-first when several are open. Anything that does
/// not single out one lot — two reagent articles for the analysis, two open lots of the same
/// expiry, nothing opened yet — answers "unknown" rather than a guess: a wrong lot on a result
/// would send a recall to the wrong patients, which is worse than a blank.
/// </para>
/// </summary>
public static class ReagentLotSelector
{
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n', '|'];

    /// <summary>The analysis codes of an item's list, upper-cased, without blanks or repeats.</summary>
    public static IReadOnlyList<string> AnalysisCodes(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(c => c.ToUpperInvariant())
                     .Distinct(StringComparer.Ordinal)];

    public static bool IsUsedBy(StockItem item, string analysisCode) =>
        AnalysisCodes(item.UsedByAnalysisCodesCsv)
            .Contains(analysisCode.Trim().ToUpperInvariant(), StringComparer.Ordinal);

    /// <summary>The lot in use for an analysis, or null when the stock does not say unambiguously.</summary>
    public static ReagentLot? LotInUse(IEnumerable<StockItem> items, string analysisCode, DateOnly today)
    {
        var reagents = items
            .Where(i => i.IsActive && i.Kind == StockItemKind.Reagent && IsUsedBy(i, analysisCode))
            .ToList();

        return reagents.Count == 1 ? LotInUse(reagents[0], today) : null;
    }

    /// <summary>
    /// The item's lot in use: opened and usable. Lots with stock left come first, by expiry; a lot
    /// drawn to zero (the whole box issued to the bench) is in use only when no other open lot is.
    /// </summary>
    public static ReagentLot? LotInUse(StockItem item, DateOnly today)
    {
        var opened = item.Lots.Where(l => l.OpenedOn is not null && l.IsUsable(today)).ToList();
        if (opened.Count == 0) return null;

        var withStock = opened.Where(l => l.QuantityRemaining > 0m)
                              .OrderBy(l => l.ExpiryDate).ThenBy(l => l.ReceivedOn).ToList();

        if (withStock.Count > 0)
        {
            // Two open lots expiring the same day: FEFO cannot say which one is on the analyser.
            if (withStock.Count > 1 && withStock[0].ExpiryDate == withStock[1].ExpiryDate) return null;
            return withStock[0];
        }

        var latest = opened.OrderByDescending(l => l.OpenedOn).ToList();
        if (latest.Count > 1 && latest[0].OpenedOn == latest[1].OpenedOn) return null;
        return latest[0];
    }
}
