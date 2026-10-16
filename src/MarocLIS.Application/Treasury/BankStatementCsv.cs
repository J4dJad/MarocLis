using System.Globalization;
using System.Text;
using System.Text.Json;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Treasury;

/// <summary>
/// Where each field sits in a bank's CSV export. Every Moroccan bank has its own layout, and the
/// same bank changes it between its web banking and its statement service, so nothing is assumed:
/// the operator picks the columns once per account against a preview, and the mapping is kept
/// with the account.
/// <para>Columns are numbered from 1, as the preview shows them; 0 means "not in this file".</para>
/// </summary>
public sealed record BankStatementCsvMapping
{
    /// <summary>Field separator; '\0' detects it from the first lines (semicolon, comma or tab).</summary>
    public char Delimiter { get; init; }

    /// <summary>Lines before the column headers: the bank's title, the account, the period.</summary>
    public int SkipLines { get; init; }

    public bool HasHeader { get; init; } = true;

    public int DateColumn { get; init; } = 1;
    public int ValueDateColumn { get; init; }
    public int LabelColumn { get; init; } = 2;
    public int ReferenceColumn { get; init; }

    /// <summary>One signed amount column; when 0, <see cref="DebitColumn"/> and <see cref="CreditColumn"/>.</summary>
    public int AmountColumn { get; init; }
    public int DebitColumn { get; init; } = 3;
    public int CreditColumn { get; init; } = 4;

    public string DateFormat { get; init; } = "dd/MM/yyyy";

    /// <summary>
    /// ',' for the French layout of most Moroccan banks, '.' otherwise; '\0' (the default) reads
    /// it from the file's amounts, so a new account's first import does not take "1500.00" for
    /// 150 000 (see <see cref="BankStatementCsv.DetectDecimalSeparator"/>).
    /// </summary>
    public char DecimalSeparator { get; init; }

    public bool UsesDebitCreditColumns => AmountColumn <= 0;

    public static BankStatementCsvMapping Default { get; } = new();

    public Result Validate()
    {
        if (DateColumn <= 0) return Result.Failure(Error.Validation("BankStatement_DateColumnRequired"));
        if (LabelColumn <= 0) return Result.Failure(Error.Validation("BankStatement_LabelColumnRequired"));
        if (UsesDebitCreditColumns && (DebitColumn <= 0 || CreditColumn <= 0))
            return Result.Failure(Error.Validation("BankStatement_AmountColumnRequired"));
        if (SkipLines is < 0 or > 100)
            return Result.Failure(Error.Validation("BankStatement_SkipLinesInvalid"));
        return Result.Success();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The mapping kept with an account, or the default when there is none or it is unreadable.</summary>
    public static BankStatementCsvMapping FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            return JsonSerializer.Deserialize<BankStatementCsvMapping>(json, Json) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}

/// <summary>One line of the file as read with the mapping: the values, or why it could not be read.</summary>
public sealed record ParsedStatementLine(
    int LineNumber,
    DateOnly? OperationDate,
    DateOnly? ValueDate,
    string Label,
    string? Reference,
    decimal? Amount,
    string? ErrorKey,
    IReadOnlyList<string> RawCells)
{
    public bool IsValid => ErrorKey is null && OperationDate is not null && Amount is not null;
}

/// <summary>Reads a bank's CSV statement with a <see cref="BankStatementCsvMapping"/>.</summary>
public static class BankStatementCsv
{
    /// <summary>
    /// The text of the file. Most bank exports are Windows-1252 rather than UTF-8; a file that is
    /// not valid UTF-8 is read as Windows-1252, so the accents of the labels survive either way.
    /// A file Excel saved as "Texte Unicode" is UTF-16 and says so with its byte order mark.
    /// </summary>
    public static string Decode(byte[] content)
    {
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);

        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(content);
        }
    }

    /// <summary>The first lines of the file split into cells, for the preview the columns are picked on.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Preview(string text, BankStatementCsvMapping mapping,
                                                               int maxLines = 15)
    {
        var lines = SplitLines(text);
        var delimiter = DelimiterOf(lines, mapping);
        return [.. lines.Skip(mapping.SkipLines).Take(maxLines).Select(l => SplitCells(l, delimiter))];
    }

    public static IReadOnlyList<ParsedStatementLine> Parse(string text, BankStatementCsvMapping mapping)
    {
        var lines = SplitLines(text);
        var delimiter = DelimiterOf(lines, mapping);
        var result = new List<ParsedStatementLine>();

        var first = mapping.SkipLines + (mapping.HasHeader ? 1 : 0);

        int[] amountColumns = mapping.UsesDebitCreditColumns
            ? [mapping.DebitColumn, mapping.CreditColumn]
            : [mapping.AmountColumn];
        var decimalSeparator = mapping.DecimalSeparator is ',' or '.'
            ? mapping.DecimalSeparator
            : DetectDecimalSeparator(lines.Skip(first).Select(l => SplitCells(l, delimiter))
                                          .SelectMany(cells => amountColumns.Where(c => c > 0 && c <= cells.Count)
                                                                            .Select(c => cells[c - 1])));

        for (var i = first; i < lines.Count; i++)
        {
            var cells = SplitCells(lines[i], delimiter);
            if (cells.All(string.IsNullOrWhiteSpace)) continue;

            var lineNumber = i + 1;
            string Cell(int column) => column > 0 && column <= cells.Count ? cells[column - 1].Trim() : string.Empty;

            var label = Cell(mapping.LabelColumn);
            var reference = mapping.ReferenceColumn > 0 ? NullIfEmpty(Cell(mapping.ReferenceColumn)) : null;

            var date = ParseDate(Cell(mapping.DateColumn), mapping.DateFormat);
            var valueDate = mapping.ValueDateColumn > 0 ? ParseDate(Cell(mapping.ValueDateColumn), mapping.DateFormat) : null;

            decimal? amount;
            string? amountError = null;
            if (mapping.UsesDebitCreditColumns)
            {
                var debitText = Cell(mapping.DebitColumn);
                var creditText = Cell(mapping.CreditColumn);
                var debit = ParseAmount(debitText, decimalSeparator);
                var credit = ParseAmount(creditText, decimalSeparator);

                if ((debitText.Length > 0 && debit is null) || (creditText.Length > 0 && credit is null))
                {
                    amount = null;
                    amountError = "BankStatement_AmountUnreadable";
                }
                else
                {
                    // Some banks print the debit negative, others positive: its sign is the column's.
                    amount = Money.Round(Math.Abs(credit ?? 0m) - Math.Abs(debit ?? 0m));
                }
            }
            else
            {
                amount = ParseAmount(Cell(mapping.AmountColumn), decimalSeparator);
                if (amount is null) amountError = "BankStatement_AmountUnreadable";
            }

            // A line with a label but neither date nor amount is the bank's running text (a
            // carried-forward balance, a page footer): left out rather than reported.
            if (date is null && (amount is null or 0m) && Cell(mapping.DateColumn).Length == 0) continue;

            var error = date is null ? "BankStatement_DateUnreadable"
                : amountError
                  ?? (amount == 0m ? "BankStatement_AmountZero"
                      : string.IsNullOrWhiteSpace(label) ? "BankStatement_LabelEmpty" : null);

            result.Add(new ParsedStatementLine(lineNumber, date, valueDate, label, reference,
                                               amount, error, cells));
        }

        return result;
    }

    public static DateOnly? ParseDate(string text, string format)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;

        // Two-digit years and a time after the date, both seen in bank exports.
        string[] fallbacks = ["dd/MM/yyyy", "dd/MM/yy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "dd/MM/yyyy HH:mm:ss"];
        foreach (var fallback in fallbacks)
        {
            if (DateTime.TryParseExact(text, fallback, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return DateOnly.FromDateTime(dt);
        }

        return null;
    }

    /// <summary>
    /// The decimal separator the amounts of a file use: in each amount, the last '.' or ',' that is
    /// followed by one or two final digits ("1 500,00", "1,500.00", "12.5"). The more frequent one
    /// wins; ',' when nothing tells (whole amounts only).
    /// </summary>
    public static char DetectDecimalSeparator(IEnumerable<string> amountCells)
    {
        int commas = 0, dots = 0;
        foreach (var cell in amountCells)
        {
            var t = new string([.. cell.Where(c => char.IsAsciiDigit(c) || c is '.' or ',')]);
            var last = t.LastIndexOfAny(['.', ',']);
            if (last < 0) continue;

            var tail = t.Length - last - 1;
            if (tail is < 1 or > 2) continue;
            if (t[last] == ',') commas++; else dots++;
        }

        return dots > commas ? '.' : ',';
    }

    /// <summary>The first amount the statement line's column cannot hold.</summary>
    public const decimal MaximumAmount = 10_000_000_000_000_000m;

    /// <summary>
    /// An amount as banks print it: "1 234,56", "1.234,56", "-120,00", "(120,00)", "120,00-",
    /// with or without "DH" / "MAD". Null when it is not a number; zero for an empty cell.
    /// <para>
    /// A thousands separator followed by only one or two final digits ("1500.00" read with ',')
    /// is a decimal separator the mapping got wrong: the amount is refused rather than read a
    /// hundred times too large.
    /// </para>
    /// </summary>
    public static decimal? ParseAmount(string text, char decimalSeparator)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;

        var t = text.Trim().ToUpperInvariant().Replace("MAD", string.Empty).Replace("DH", string.Empty).Trim();

        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.EndsWith('-')) { negative = true; t = t[..^1]; }
        if (t.StartsWith('-')) { negative = !negative; t = t[1..]; }
        if (t.StartsWith('+')) t = t[1..];

        var thousands = decimalSeparator == ',' ? '.' : ',';

        var lastThousands = t.LastIndexOf(thousands);
        if (lastThousands >= 0)
        {
            var tail = t[(lastThousands + 1)..].Trim();
            if (tail.Length is 1 or 2 && tail.All(char.IsAsciiDigit)) return null;
        }
        var sb = new StringBuilder();
        foreach (var c in t)
        {
            if (char.IsAsciiDigit(c)) sb.Append(c);
            else if (c == decimalSeparator) sb.Append('.');
            else if (c == thousands || char.IsWhiteSpace(c) || c == ' ' || c == ' ' || c == '\'') continue;
            else return null;
        }

        if (sb.Length == 0) return null;
        if (!decimal.TryParse(sb.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;

        // Beyond what the decimal(18,2) column holds: a mapping pointing at the RIB or a reference
        // read it as an amount, and the import failed on the database with an unexpected error.
        if (value >= MaximumAmount) return null;

        return Money.Round(negative ? -value : value);
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];

    private static char DelimiterOf(IReadOnlyList<string> lines, BankStatementCsvMapping mapping)
    {
        if (mapping.Delimiter != '\0') return mapping.Delimiter;

        var sample = lines.Skip(mapping.SkipLines).Take(10).ToList();
        char[] candidates = [';', '\t', ','];
        return candidates.OrderByDescending(c => sample.Sum(l => SplitCells(l, c).Count)).First();
    }

    /// <summary>Splits a CSV line, honouring double quotes around a cell and doubled quotes within it.</summary>
    public static IReadOnlyList<string> SplitCells(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        cells.Add(current.ToString());
        return cells;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
