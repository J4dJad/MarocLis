namespace MarocLIS.Application.Inventory;

/// <summary>
/// The search box of the stock screens — the workstation's StockGrid.Filter: a line stays shown
/// when any of its cells contains what was typed, whatever the case; an empty box shows them all.
/// </summary>
public static class StockSearch
{
    public static bool Matches(string? query, params string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var needle = query?.Trim();
        if (string.IsNullOrEmpty(needle)) return true;
        return cells.Any(c => c is not null && c.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
    }
}
