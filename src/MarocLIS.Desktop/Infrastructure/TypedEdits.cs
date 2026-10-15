namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// What an operator has typed into a grid and not yet saved, keyed by row (and column).
/// <para>
/// A grid is rebuilt from its data on a language change, and replaced when another file is
/// opened. Both used to throw away typed results and QC values without a word, so they had to be
/// typed again — or, worse, were believed saved. The screen captures them here before rebuilding,
/// puts them back afterwards, and asks before opening something else while any are pending.
/// </para>
/// </summary>
public sealed class TypedEdits<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, string> _values = [];

    public int Count => _values.Count;

    public bool IsEmpty => _values.Count == 0;

    /// <summary>
    /// Whether the text in a cell differs from what was loaded into it. Surrounding blanks do not
    /// count, and neither does an empty cell against a missing value.
    /// </summary>
    public static bool Differs(string? typed, string? stored) =>
        !string.Equals(typed?.Trim() ?? string.Empty, stored?.Trim() ?? string.Empty,
                       StringComparison.Ordinal);

    /// <summary>Remembers the cell's text if, and only if, it differs from what was loaded.</summary>
    public void Capture(TKey key, string? typed, string? stored)
    {
        if (Differs(typed, stored)) _values[key] = typed ?? string.Empty;
    }

    public bool TryGet(TKey key, out string typed) => _values.TryGetValue(key, out typed!);
}
