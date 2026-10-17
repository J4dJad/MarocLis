using System.Globalization;
using System.Text.RegularExpressions;

namespace MarocLIS.Application.Migration;

/// <summary>
/// Reads the dates and amounts old applications export, whatever the workstation's culture: a
/// French Excel writes « 05/03/1971 » and « 1 234,50 », a database « 1971-03-05 » and « 1234.5 »,
/// and an .xlsx date may arrive as its serial number.
/// </summary>
public static partial class ValueParsers
{
    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd",
        "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.fffffff"
    ];

    private static readonly string[] ShortYearFormats = ["dd/MM/yy", "d/M/yy", "dd-MM-yy", "dd.MM.yy"];

    /// <summary>A date and, when the source gave one, its time; null when the text is not a date.</summary>
    public static DateTime? ParseDateTime(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();

        if (DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            return exact;

        // Two-digit years: a year ahead of this one is last century (« 05/03/71 » is 1971).
        if (DateTime.TryParseExact(value, ShortYearFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var shortYear))
        {
            var year = shortYear.Year % 100;
            var century = year > today.Year % 100 ? 1900 : 2000;
            return new DateTime(century + year, shortYear.Month, shortYear.Day);
        }

        // An Excel serial day number (1 = 1900-01-01), as an .xlsx cell formatted as text keeps it.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is >= 1 and < 2_958_466 && !value.Contains('/') && value.Length <= 12 && !IsCompactDate(value))
        {
            try { return DateTime.FromOADate(serial); }
            catch (ArgumentException) { return null; }
        }

        return null;
    }

    private static bool IsCompactDate(string value) => value.Length == 8 && value.All(char.IsAsciiDigit);

    public static DateOnly? ParseDate(string? text, DateOnly today) =>
        ParseDateTime(text, today) is { } at ? DateOnly.FromDateTime(at) : null;

    [GeneratedRegex(@"(MAD|DHS?|DIRHAMS?|€|EUR|درهم)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyWord();

    /// <summary>
    /// An amount or a measurement: « 1 234,50 », « 1.234,50 », « 1,234.50 », « 1234.5 », « 120 DH ».
    /// Null when the text is not a number.
    /// </summary>
    public static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = CurrencyWord().Replace(text, string.Empty)
                                  .Replace(" ", string.Empty, StringComparison.Ordinal)
                                  .Replace(" ", string.Empty, StringComparison.Ordinal)
                                  .Replace(" ", string.Empty, StringComparison.Ordinal)
                                  .Replace("'", string.Empty, StringComparison.Ordinal)
                                  .Trim();
        if (value.Length == 0) return null;

        var comma = value.LastIndexOf(',');
        var dot = value.LastIndexOf('.');

        if (comma >= 0 && dot >= 0)
        {
            // The later separator is the decimal one.
            value = comma > dot
                ? value.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
                : value.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (comma >= 0)
        {
            value = value.Replace(',', '.');
        }

        // Several dots left: thousands written with dots and no decimals (« 1.234.567 »).
        if (value.Count(c => c == '.') > 1) value = value.Replace(".", string.Empty, StringComparison.Ordinal);

        return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                                CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static int? ParseInteger(string? text) =>
        ParseDecimal(text) is { } value && value == decimal.Truncate(value) && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : null;

    /// <summary>
    /// A result as an old application writes it: « 5,4 », « &lt; 0.5 », « &gt;= 200 ». The operator,
    /// when there is one, and the number; null when the value is text (« Négatif », « Absence »).
    /// </summary>
    public static (string? Operator, decimal Value)? ParseResult(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();

        string? op = null;
        foreach (var candidate in new[] { "<=", ">=", "≤", "≥", "<", ">" })
        {
            if (!value.StartsWith(candidate, StringComparison.Ordinal)) continue;
            op = candidate switch { "≤" => "<=", "≥" => ">=", _ => candidate };
            value = value[candidate.Length..].Trim();
            break;
        }

        // A thousands separator in a result is rare and a space is a unit's: « 5,4 g/l » is not read.
        if (value.Length == 0 || value.Any(c => char.IsLetter(c) || c == ' ')) return null;

        return ParseDecimal(value) is { } number ? (op, number) : null;
    }
}
