using System.Globalization;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Results;

namespace MarocLIS.Application.Results;

/// <summary>
/// The rules of the « Saisie des résultats » screen that need no window, and those it shares with
/// « Validation biologique » to open a result's history.
/// </summary>
public static class ResultEntryRules
{
    /// <summary>A line a value can be keyed on: not derived, not a heading, its tube received, not released.</summary>
    public static bool IsKeyable(ResultEntryRow? row) =>
        row is { IsEditable: true, IsAwaitingSample: false }
        && row.Status is not (TestResultStatus.Validated or TestResultStatus.Reported);

    /// <summary>The sectors (department codes) of a file of the worklist, for the sector chips.</summary>
    public static IEnumerable<string> SectorsOf(PendingEntryOrder order) =>
        order.Departments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Only a measured number has a curve; a coded or text result has none.</summary>
    public static bool IsCharted(ResultEntryRow? row) =>
        row is { IsHeading: false, ValueType: ResultValueType.Numeric or ResultValueType.NumericWithOperator };

    /// <summary>The same reading of a typed value as the entry service: an optional operator, then a number.</summary>
    public static bool TryParseTyped(string? raw, out string? oper, out decimal value)
    {
        oper = null;
        value = 0m;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var candidate in (string[])["<=", ">=", "≤", "≥", "<", ">"])
        {
            if (!text.StartsWith(candidate, StringComparison.Ordinal)) continue;
            oper = candidate;
            text = text[candidate.Length..].Trim();
            break;
        }

        return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
                                out value);
    }
}
