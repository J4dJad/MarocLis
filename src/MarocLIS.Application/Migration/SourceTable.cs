using System.Globalization;
using System.Text;

namespace MarocLIS.Application.Migration;

/// <summary>
/// Rows read from a file or from the old application's database, every value as text: the
/// validation reads dates and numbers itself, the same way whatever the source. A database's typed
/// values arrive in invariant form (<c>yyyy-MM-dd</c>, a dot for decimals).
/// </summary>
public sealed class SourceTable
{
    public SourceTable(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows, bool truncated = false)
    {
        Columns = columns;
        Rows = rows;
        Truncated = truncated;
    }

    public IReadOnlyList<string> Columns { get; }

    /// <summary>The data rows, without the header. Row <c>i</c> is line <c>i + 2</c> of a file.</summary>
    public IReadOnlyList<string?[]> Rows { get; }

    /// <summary>The read stopped at its row ceiling: a preview, or a source larger than the import accepts.</summary>
    public bool Truncated { get; }

    public int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static SourceTable Empty { get; } = new([], []);

    /// <summary>Text of a typed database value, in the form the validation reads.</summary>
    public static string? ToText(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        DateTime d => d.TimeOfDay == TimeSpan.Zero
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double f => f.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        byte[] => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}

/// <summary>
/// Reads a CSV file as Excel and the usual laboratory applications write one: the separator found
/// from the header line (« ; » from a French Excel, « , » or a tab otherwise), double quotes around
/// a value that holds one, UTF-8 with or without its mark, or Windows-1252 when the bytes are not
/// valid UTF-8 (an export from an old application rarely says which).
/// </summary>
public static class CsvSource
{
    public static SourceTable Read(Stream stream, int maxRows = int.MaxValue)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(Decode(buffer.ToArray()), maxRows);
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return FromWindows1252(bytes, Encoding.Latin1.GetString(bytes));
        }
    }

    // Windows-1252 differs from Latin-1 only on 0x80–0x9F (€, œ, ’…); mapped by hand so the
    // application needs no code-page provider.
    private static readonly char[] Cp1252High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ'
    ];

    private static string FromWindows1252(byte[] bytes, string latin)
    {
        var chars = latin.ToCharArray();
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] is >= 0x80 and <= 0x9F) chars[i] = Cp1252High[bytes[i] - 0x80];
        return new string(chars);
    }

    public static char DetectSeparator(string headerLine)
    {
        var candidates = new[] { ';', ',', '\t', '|' };
        var best = ';';
        var bestCount = 0;

        foreach (var candidate in candidates)
        {
            var count = 0;
            var quoted = false;
            foreach (var c in headerLine)
            {
                if (c == '"') quoted = !quoted;
                else if (c == candidate && !quoted) count++;
            }

            if (count > bestCount) { best = candidate; bestCount = count; }
        }

        return best;
    }

    public static SourceTable Parse(string text, int maxRows = int.MaxValue)
    {
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        var separator = DetectSeparator(firstLineEnd < 0 ? text : text[..firstLineEnd]);

        var records = Records(text, separator).GetEnumerator();
        if (!records.MoveNext()) return SourceTable.Empty;

        var header = records.Current.Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Colonne{i + 1}" : h.Trim()).ToList();
        var rows = new List<string?[]>();
        var truncated = false;

        while (records.MoveNext())
        {
            var record = records.Current;
            if (record.All(string.IsNullOrWhiteSpace)) continue;

            if (rows.Count >= maxRows) { truncated = true; break; }

            var row = new string?[header.Count];
            for (var i = 0; i < header.Count && i < record.Count; i++)
                row[i] = string.IsNullOrWhiteSpace(record[i]) ? null : record[i];
            rows.Add(row);
        }

        return new SourceTable(header, rows, truncated);
    }

    private static IEnumerable<List<string>> Records(string text, char separator)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var quoted = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    quoted = false; i++; continue;
                }

                field.Append(c); i++; continue;
            }

            if (c == '"' && field.Length == 0) { quoted = true; i++; continue; }

            if (c == separator) { record.Add(field.ToString()); field.Clear(); i++; continue; }

            if (c is '\r' or '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                yield return record;
                record = [];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                continue;
            }

            field.Append(c);
            i++;
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    /// <summary>A CSV line, « ; »-separated as a French Excel reads it, quoting what needs it.</summary>
    public static string Line(IEnumerable<string?> values, char separator = ';') =>
        string.Join(separator, values.Select(v => Quote(v ?? string.Empty, separator)));

    private static string Quote(string value, char separator)
    {
        // A leading =, +, - or @ makes Excel evaluate the cell: an anomaly report quoting a value
        // from the old application must not run it as a formula.
        if (value.Length > 0 && value[0] is '=' or '+' or '@' or '-' && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            value = "'" + value;

        return value.IndexOfAny([separator, '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
