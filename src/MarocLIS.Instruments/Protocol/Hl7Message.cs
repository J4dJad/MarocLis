using System.Globalization;
using System.Text;

namespace MarocLIS.Instruments.Protocol;

/// <summary>Encoding characters declared in MSH-2.</summary>
public sealed record Hl7Encoding(char Field, char Component, char Repeat, char Escape, char SubComponent)
{
    public static readonly Hl7Encoding Default = new('|', '^', '~', '\\', '&');

    /// <summary>
    /// Reads the encoding characters from an MSH segment. MSH-1 is the field separator itself and
    /// MSH-2 declares the rest, which is why MSH is the one segment that cannot be split before its
    /// own separators are known.
    /// </summary>
    public static Hl7Encoding FromMsh(string mshSegment)
    {
        if (mshSegment.Length < 4) return Default;

        var field = mshSegment[3];
        var parts = mshSegment.Split(field);
        if (parts.Length < 2 || parts[1].Length < 4) return Default with { Field = field };

        var e = parts[1];
        return new Hl7Encoding(field, e[0], e[1], e[2], e.Length > 3 ? e[3] : '&');
    }

    public string EncodingCharacters => $"{Component}{Repeat}{Escape}{SubComponent}";
}

/// <summary>
/// A parsed HL7 v2 message with positional access to segments, fields and components.
/// <para>
/// Field accessors use HL7's own 1-based numbering, including its MSH quirk: MSH-1 is the field
/// separator, so MSH-9 is the ninth field as the standard counts it and not the ninth token after
/// splitting. Getting that offset wrong is the classic HL7 integration bug, so it is handled once
/// here rather than at every call site.
/// </para>
/// </summary>
public sealed class Hl7Message
{
    private readonly List<string[]> _segments = [];

    private Hl7Message(Hl7Encoding encoding) => Encoding = encoding;

    public Hl7Encoding Encoding { get; }

    public string? RawText { get; private init; }

    public static Hl7Message Parse(string text)
    {
        var normalised = text.Replace("\r\n", "\r").Replace('\n', '\r');

        var lines = normalised
            .Split('\r', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('', '', ' '))   // strip MLLP wrapper bytes if present
            .Where(l => l.Length >= 3)
            .ToList();

        var msh = lines.FirstOrDefault(l => l.StartsWith("MSH", StringComparison.Ordinal));
        var encoding = msh is not null ? Hl7Encoding.FromMsh(msh) : Hl7Encoding.Default;

        var message = new Hl7Message(encoding) { RawText = normalised };

        foreach (var line in lines)
            message._segments.Add(line.Split(encoding.Field));

        return message;
    }

    public IReadOnlyList<string[]> Segments => _segments;

    public IEnumerable<string[]> SegmentsNamed(string name) =>
        _segments.Where(s => s.Length > 0 && string.Equals(s[0], name, StringComparison.Ordinal));

    public string[]? FirstSegment(string name) => SegmentsNamed(name).FirstOrDefault();

    /// <summary>
    /// Field <paramref name="fieldNumber"/> of <paramref name="segment"/> in HL7's numbering.
    /// For MSH, field 1 is the separator, so field n sits at array index n − 1.
    /// <para>
    /// An instance method although it reads no instance state, because the overload below is one and
    /// C# will not let a single name be reached both ways: declaring this one static made every
    /// <c>message.Field(segment, n)</c> in the parser a compile error while
    /// <c>message.Component(segment, n, c)</c> beside it was fine. Consistent with
    /// <see cref="Component"/> and <see cref="Repetitions"/>, which do need the message's encoding
    /// characters, so a caller never has to remember which accessor is which.
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "Instance by necessity, not by omission: the overload below takes a " +
                        "segment name and is an instance method, and C# will not resolve one name " +
                        "both ways. Made static, every message.Field(segment, n) call in the " +
                        "parsers becomes CS0176. See the remarks above.")]
    public string? Field(string[] segment, int fieldNumber)
    {
        if (segment.Length == 0 || fieldNumber < 1) return null;

        var isMsh = string.Equals(segment[0], "MSH", StringComparison.Ordinal);

        // Non-MSH: segment[0] is the name, so field n is at index n. MSH: field 1 is the separator
        // (not in the array at all) and field 2 is at index 1, so field n is at index n − 1.
        var index = isMsh ? fieldNumber - 1 : fieldNumber;

        if (isMsh && fieldNumber == 1) return null;
        if (index < 0 || index >= segment.Length) return null;

        return segment[index] is { Length: > 0 } value ? value : null;
    }

    public string? Field(string segmentName, int fieldNumber)
    {
        var segment = FirstSegment(segmentName);
        return segment is null ? null : Field(segment, fieldNumber);
    }

    /// <summary>Component <paramref name="componentNumber"/> (1-based) of a field.</summary>
    public string? Component(string[] segment, int fieldNumber, int componentNumber)
    {
        var field = Field(segment, fieldNumber);
        if (field is null) return null;

        var components = field.Split(Encoding.Component);
        return componentNumber >= 1 && componentNumber <= components.Length
            ? components[componentNumber - 1] is { Length: > 0 } value ? value : null
            : null;
    }

    public IReadOnlyList<string> Repetitions(string[] segment, int fieldNumber)
    {
        var field = Field(segment, fieldNumber);
        return field is null ? [] : field.Split(Encoding.Repeat);
    }

    /// <summary>Message type as "ORU^R01", from MSH-9.</summary>
    public string? MessageType
    {
        get
        {
            var msh = FirstSegment("MSH");
            if (msh is null) return null;

            var code = Component(msh, 9, 1);
            var trigger = Component(msh, 9, 2);
            return trigger is null ? code : $"{code}^{trigger}";
        }
    }

    public string? ControlId => Field("MSH", 10);
    public string? SendingApplication => Component(FirstSegment("MSH") ?? [], 3, 1);
    public string? SendingFacility => Component(FirstSegment("MSH") ?? [], 4, 1);
    public string? Version => Component(FirstSegment("MSH") ?? [], 12, 1);

    public DateTimeOffset? MessageTimestamp => ParseTimestamp(Field("MSH", 7));

    /// <summary>
    /// Unescapes HL7 escape sequences in a text field: <c>\F\ \S\ \R\ \T\ \E\</c> and
    /// <c>\X..\</c> hexadecimal. Needed for comments and free-text results, which regularly contain
    /// the delimiters themselves.
    /// </summary>
    public string Unescape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOf(Encoding.Escape) < 0) return value;

        var builder = new StringBuilder(value.Length);
        var i = 0;

        while (i < value.Length)
        {
            if (value[i] != Encoding.Escape)
            {
                builder.Append(value[i++]);
                continue;
            }

            var end = value.IndexOf(Encoding.Escape, i + 1);
            if (end < 0)
            {
                builder.Append(value[i..]);
                break;
            }

            var code = value[(i + 1)..end];
            builder.Append(code switch
            {
                "F" => Encoding.Field.ToString(),
                "S" => Encoding.Component.ToString(),
                "R" => Encoding.Repeat.ToString(),
                "T" => Encoding.SubComponent.ToString(),
                "E" => Encoding.Escape.ToString(),
                ".br" => "\n",
                _ when code.StartsWith('X') && code.Length > 1 => DecodeHex(code[1..]),
                _ => string.Empty
            });

            i = end + 1;
        }

        return builder.ToString();
    }

    private static string DecodeHex(string hex)
    {
        var bytes = new List<byte>();
        for (var i = 0; i + 1 < hex.Length; i += 2)
            if (byte.TryParse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                bytes.Add(b);
        return System.Text.Encoding.Latin1.GetString([.. bytes]);
    }

    /// <summary>Escapes delimiters so a value can be placed in an outgoing field safely.</summary>
    public string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value
            .Replace(Encoding.Escape.ToString(), $"{Encoding.Escape}E{Encoding.Escape}")
            .Replace(Encoding.Field.ToString(), $"{Encoding.Escape}F{Encoding.Escape}")
            .Replace(Encoding.Component.ToString(), $"{Encoding.Escape}S{Encoding.Escape}")
            .Replace(Encoding.Repeat.ToString(), $"{Encoding.Escape}R{Encoding.Escape}")
            .Replace(Encoding.SubComponent.ToString(), $"{Encoding.Escape}T{Encoding.Escape}");
    }

    /// <summary>
    /// Parses an HL7 timestamp (<c>YYYYMMDDHHMMSS[.S]±ZZZZ</c>), accepting the truncated forms and
    /// honouring an explicit offset when one is present.
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        var offset = TimeSpan.Zero;
        var hasOffset = false;

        var signIndex = text.LastIndexOfAny(['+', '-']);
        if (signIndex > 7 && text.Length - signIndex == 5)
        {
            if (int.TryParse(text.AsSpan(signIndex + 1, 2), out var hours) &&
                int.TryParse(text.AsSpan(signIndex + 3, 2), out var minutes))
            {
                offset = new TimeSpan(hours, minutes, 0);
                if (text[signIndex] == '-') offset = -offset;
                hasOffset = true;
            }
            text = text[..signIndex];
        }

        var dot = text.IndexOf('.');
        if (dot > 0) text = text[..dot];

        var digits = new string(text.Where(char.IsDigit).ToArray());
        string[] formats = ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMddHH", "yyyyMMdd", "yyyyMM", "yyyy"];

        foreach (var format in formats)
        {
            if (digits.Length >= format.Length &&
                DateTime.TryParseExact(digits[..format.Length], format, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var parsed))
                return hasOffset
                    ? new DateTimeOffset(parsed, offset)
                    : new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        }

        return null;
    }

    public static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyyMMddHHmmsszzz", CultureInfo.InvariantCulture).Replace(":", string.Empty);

    /// <summary>
    /// Builds an ACK for this message. <paramref name="errorText"/> produces an application-error
    /// acknowledgement (AE) instead of an accept (AA), which is how an analyser learns that its
    /// results were not taken — otherwise it deletes them believing they are safe.
    /// </summary>
    public string BuildAck(string receivingApplication, DateTimeOffset now, string? errorText = null)
    {
        var f = Encoding.Field;
        var code = errorText is null ? "AA" : "AE";

        var builder = new StringBuilder();
        builder.Append("MSH").Append(f)
               .Append(Encoding.EncodingCharacters).Append(f)
               .Append(receivingApplication).Append(f)
               .Append(f)                                        // sending facility
               .Append(SendingApplication ?? string.Empty).Append(f)
               .Append(SendingFacility ?? string.Empty).Append(f)
               .Append(Timestamp(now)).Append(f)
               .Append(f)                                        // security
               .Append("ACK").Append(f)
               .Append(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append(f)
               .Append('P').Append(f)
               .Append(Version ?? "2.5")
               .Append('\r');

        builder.Append("MSA").Append(f)
               .Append(code).Append(f)
               .Append(ControlId ?? string.Empty);

        if (errorText is not null)
            builder.Append(f).Append(Escape(errorText));

        builder.Append('\r');
        return builder.ToString();
    }
}
