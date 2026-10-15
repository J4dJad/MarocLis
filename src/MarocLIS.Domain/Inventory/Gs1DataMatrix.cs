using System.Globalization;
using System.Text;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>What a GS1 DataMatrix on a reagent box says.</summary>
public sealed record Gs1Data
{
    /// <summary>AI 01, always fourteen digits.</summary>
    public string? Gtin { get; init; }

    /// <summary>AI 17. A day of 00 means the last day of the month.</summary>
    public DateOnly? ExpiryDate { get; init; }

    /// <summary>AI 10.</summary>
    public string? LotNumber { get; init; }

    /// <summary>AI 21.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>AI 11.</summary>
    public DateOnly? ProductionDate { get; init; }
}

/// <summary>
/// Reads the GS1 element string a keyboard-wedge scanner types when it reads a DataMatrix.
/// <para>
/// Scanners differ in how they render the FNC1 separator: the ASCII group separator (0x1D), a
/// "]d2" symbology prefix, or nothing visible at all in the human-readable "(01)…(17)…" form. All
/// three are accepted. A variable-length field (lot, serial) ends at a separator or at the end of
/// the string; the fixed-length ones are cut by their length, as the GS1 general specifications say.
/// </para>
/// </summary>
public static class Gs1Parser
{
    public const char GroupSeparator = '\u001d';

    /// <summary>Parses a scan. Unknown application identifiers are skipped, not refused.</summary>
    public static Result<Gs1Data> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Failure<Gs1Data>(Error.Validation("Gs1_Empty"));

        var text = Normalise(raw);

        if (text.Length == 0)
            return Result.Failure<Gs1Data>(Error.Validation("Gs1_Empty"));

        if (text.StartsWith('('))
            text = FromHumanReadable(text);

        string? gtin = null, lot = null, serial = null;
        DateOnly? expiry = null, production = null;

        var position = 0;

        while (position < text.Length)
        {
            if (text[position] == GroupSeparator)
            {
                position++;
                continue;
            }

            if (!TryReadAi(text, position, out var ai, out var fixedLength))
                return Result.Failure<Gs1Data>(Error.Validation("Gs1_Unreadable", text[position..]));

            position += ai.Length;

            string value;

            if (fixedLength is { } length)
            {
                if (position + length > text.Length)
                    return Result.Failure<Gs1Data>(Error.Validation("Gs1_Truncated", ai));

                value = text.Substring(position, length);
                position += length;
            }
            else
            {
                var end = text.IndexOf(GroupSeparator, position);
                if (end < 0) end = text.Length;

                value = text[position..end];
                position = end;
            }

            switch (ai)
            {
                case "01":
                    if (!IsValidGtin(value))
                        return Result.Failure<Gs1Data>(Error.Validation("Gs1_GtinInvalid", value));
                    gtin = value;
                    break;

                case "17":
                    expiry = ParseDate(value);
                    if (expiry is null)
                        return Result.Failure<Gs1Data>(Error.Validation("Gs1_DateInvalid", value));
                    break;

                case "11":
                    production = ParseDate(value);
                    if (production is null)
                        return Result.Failure<Gs1Data>(Error.Validation("Gs1_DateInvalid", value));
                    break;

                case "10":
                    lot = value.Trim();
                    break;

                case "21":
                    serial = value.Trim();
                    break;
            }
        }

        if (gtin is null && lot is null && expiry is null)
            return Result.Failure<Gs1Data>(Error.Validation("Gs1_Unreadable", raw.Trim()));

        return Result.Success(new Gs1Data
        {
            Gtin = gtin,
            ExpiryDate = expiry,
            LotNumber = string.IsNullOrEmpty(lot) ? null : lot,
            SerialNumber = string.IsNullOrEmpty(serial) ? null : serial,
            ProductionDate = production
        });
    }

    /// <summary>
    /// A GTIN-8, -12, -13 or -14, left-padded to fourteen digits, or null when it is not one (wrong
    /// length, a letter, a bad check digit).
    /// </summary>
    public static string? NormaliseGtin(string? value)
    {
        var digits = value?.Trim();
        if (string.IsNullOrEmpty(digits) || digits.Any(c => c is < '0' or > '9')) return null;
        if (digits.Length is not (8 or 12 or 13 or 14)) return null;

        var padded = digits.PadLeft(14, '0');
        return IsValidGtin(padded) ? padded : null;
    }

    /// <summary>Fourteen digits whose last one is the GS1 modulo-10 check digit of the others.</summary>
    public static bool IsValidGtin(string value)
    {
        if (value.Length != 14 || value.Any(c => c is < '0' or > '9')) return false;

        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var digit = value[i] - '0';
            // From the right of the 13 data digits, weights alternate 3, 1, 3…
            sum += (12 - i) % 2 == 0 ? digit * 3 : digit;
        }

        return (10 - sum % 10) % 10 == value[13] - '0';
    }

    /// <summary>YYMMDD, with DD = 00 meaning the last day of the month (GS1 general specifications).</summary>
    public static DateOnly? ParseDate(string value)
    {
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return null;

        var year = 2000 + int.Parse(value[..2], CultureInfo.InvariantCulture);
        var month = int.Parse(value.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(value.AsSpan(4, 2), CultureInfo.InvariantCulture);

        if (month is < 1 or > 12) return null;

        var last = DateTime.DaysInMonth(year, month);
        if (day == 0) day = last;
        if (day > last) return null;

        return new DateOnly(year, month, day);
    }

    // Strips the symbology identifier and maps the textual stand-ins for FNC1 some scanners type.
    private static string Normalise(string raw)
    {
        var text = raw.Trim().TrimEnd('\r', '\n');

        foreach (var prefix in new[] { "]d2", "]D2", "]C1", "]Q3", "]e0" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text.Replace("<GS>", GroupSeparator.ToString(), StringComparison.OrdinalIgnoreCase)
                   .Replace("{GS}", GroupSeparator.ToString(), StringComparison.OrdinalIgnoreCase)
                   .TrimStart(GroupSeparator);
    }

    // "(01)03453120000011(17)261231(10)ABC" → the same with separators after variable fields.
    private static string FromHumanReadable(string text)
    {
        var builder = new StringBuilder();
        var position = 0;

        while (position < text.Length)
        {
            if (text[position] != '(') return text;

            var close = text.IndexOf(')', position);
            if (close < 0) return text;

            var ai = text[(position + 1)..close];
            var next = text.IndexOf('(', close);
            var value = next < 0 ? text[(close + 1)..] : text[(close + 1)..next];

            builder.Append(ai).Append(value);
            if (!TryReadAi(ai, 0, out _, out var fixedLength) || fixedLength is null)
                builder.Append(GroupSeparator);

            position = next < 0 ? text.Length : next;
        }

        return builder.ToString().TrimEnd(GroupSeparator);
    }

    /// <summary>
    /// The application identifier starting at <paramref name="position"/>, and its data length when
    /// that is fixed. The table covers the predefined-length AIs of the GS1 specifications and the
    /// usual variable ones; anything else is read as a two-digit variable field.
    /// </summary>
    private static bool TryReadAi(string text, int position, out string ai, out int? fixedLength)
    {
        ai = string.Empty;
        fixedLength = null;

        if (position + 2 > text.Length || !char.IsAsciiDigit(text[position]) || !char.IsAsciiDigit(text[position + 1]))
            return false;

        var two = text.Substring(position, 2);

        int? Fixed2(string p) => p switch
        {
            "00" => 18,
            "01" or "02" or "03" => 14,
            "04" => 16,
            "11" or "12" or "13" or "14" or "15" or "16" or "17" or "18" or "19" => 6,
            "20" => 2,
            _ => null
        };

        if (two is "00" or "01" or "02" or "03" or "04" or "11" or "12" or "13" or "14" or "15"
                or "16" or "17" or "18" or "19" or "20")
        {
            ai = two;
            fixedLength = Fixed2(two);
            return true;
        }

        // 31nn–36nn: four-digit AI, six digits of measure.
        if (two[0] == '3' && two[1] is >= '1' and <= '6')
        {
            if (position + 4 > text.Length) return false;
            ai = text.Substring(position, 4);
            fixedLength = 6;
            return true;
        }

        // 41n: three-digit AI, thirteen-digit GLN.
        if (two == "41")
        {
            if (position + 3 > text.Length) return false;
            ai = text.Substring(position, 3);
            fixedLength = 13;
            return true;
        }

        // Three-digit variable AIs (23n, 24n, 25n, 40n, 42n) and four-digit ones (7nnn, 8nnn).
        if (two is "23" or "24" or "25" or "40" or "42")
        {
            if (position + 3 > text.Length) return false;
            ai = text.Substring(position, 3);
            return true;
        }

        if (two[0] is '7' or '8')
        {
            if (position + 4 > text.Length) return false;
            ai = text.Substring(position, 4);
            return true;
        }

        ai = two;
        return true;
    }
}
