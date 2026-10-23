namespace MarocLIS.Application.Catalog;

/// <summary>The rules of the catalogue screen that need no window.</summary>
public static class CatalogRules
{
    /// <summary>
    /// An analysis somebody has to come back to: one that cannot be ordered (no tariff, no active
    /// parameter) or has age bands whose unit is still to confirm — the warnings of the list.
    /// </summary>
    public static bool NeedsAttention(AnalysisRow row) => !row.IsOrderable || row.UnconfirmedAgeBandCount > 0;
}
