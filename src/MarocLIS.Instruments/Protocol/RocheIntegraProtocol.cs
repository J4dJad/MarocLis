using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarocLIS.Domain.Equipment;

namespace MarocLIS.Instruments.Protocol;

/// <summary>
/// The COBAS INTEGRA host interface (COBAS INTEGRA 400 plus, Cedex Bio HT): Roche's own line
/// protocol, not ASTM.
/// <para>
/// Written from the Roche Host Interface Manual (version 1.24, software 3.6/5.1). A message is a
/// <em>block</em>: a header line (instrument code, a 16-character identifier, block code), data
/// lines each starting with a two-digit line code, and an optional block check — a sequence counter
/// and a three-digit sum. Every line ends with LF (or CR LF, as configured on the analyser):
/// </para>
/// <code>
/// &lt;SOH&gt;
/// 09 COBAS INTEGRA    04       header: instrument code, identifier, block 04 = patient result
/// &lt;STX&gt;
/// 53 Order# 211044711 20/10/93 SER
/// 55 178                       test number
/// 00 +3.234000E+01 mg/dl 004 023 014 000
/// &lt;ETX&gt;
/// 1                            sequence counter
/// 562                          block check sum
/// &lt;EOT&gt;
/// </code>
/// <para>
/// The sum is every byte from SOH up to and including the line end after the sequence counter,
/// modulo 1000, right-aligned on three characters. The example above, which is the manual's, sums
/// to 562 — the test suite checks it, so the reading of the formula is not a guess.
/// </para>
/// <para>
/// The host leads the conversation: the analyser sends results only when asked (block 09), lists
/// the barcoded tubes it holds no order for when asked (block 60, answered by 62), and takes orders
/// (block 10, answered by 19). It answers each request with exactly one block.
/// </para>
/// </summary>
public static partial class RocheIntegra
{
    public const char Soh = '\x01';
    public const char Stx = '\x02';
    public const char Etx = '\x03';
    public const char Eot = '\x04';
    public const char XOn = '\x11';
    public const char XOff = '\x13';

    /// <summary>Instrument code of the COBAS INTEGRA 400 plus (manual, "Instrument codes").</summary>
    public const string Integra400PlusCode = "14";

    // Block codes the host sends or reads (manual chapter 4).
    public const string BlockIdle = "00";
    public const string BlockCalibration = "02";
    public const string BlockControl = "03";
    public const string BlockPatient = "04";
    public const string BlockCalibrationWithLots = "05";
    public const string BlockControlWithLots = "06";
    public const string BlockPatientWithLots = "07";
    public const string BlockResultRequest = "09";
    public const string BlockOrderEntry = "10";
    public const string BlockOrderDeletion = "11";
    public const string BlockOrderResponse = "19";
    public const string BlockServiceRequest = "60";
    public const string BlockTubes = "62";
    public const string BlockServiceResponse = "69";
    public const string BlockError = "99";

    /// <summary>
    /// The block check sum: the bytes from SOH through the line end after the sequence counter,
    /// modulo 1000.
    /// </summary>
    public static int Checksum(string fromSohThroughSequenceLine)
    {
        var sum = 0;
        foreach (var c in fromSohThroughSequenceLine) sum += c & 0xFF;
        return sum % 1000;
    }

    /// <summary>
    /// Builds one block, framed and ready to send. Characters the manual forbids inside fields
    /// (control codes, anything past 126) are replaced, so a stray accent in a patient name cannot
    /// corrupt the frame.
    /// </summary>
    /// <param name="sequenceCounter">0 or 1; null sends the block without a block check (the
    /// synchronisation block, or an analyser configured with the block check off).</param>
    /// <param name="lineEnd">"\n" or "\r\n", as configured on the analyser.</param>
    public static string Build(string instrumentCode, string identifier, string blockCode,
                               IEnumerable<string> lines, int? sequenceCounter, string lineEnd = "\n")
    {
        var text = new StringBuilder();

        text.Append(Soh).Append(lineEnd);
        text.Append(Header(instrumentCode, identifier, blockCode)).Append(lineEnd);
        text.Append(Stx).Append(lineEnd);

        foreach (var line in lines) text.Append(Clean(line)).Append(lineEnd);

        text.Append(Etx).Append(lineEnd);

        if (sequenceCounter is { } sc)
        {
            text.Append(sc % 2).Append(lineEnd);
            var sum = Checksum(text.ToString());
            text.Append(sum.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(lineEnd);
        }

        text.Append(Eot).Append(lineEnd);
        return text.ToString();
    }

    /// <summary>"09 COBAS INTEGRA    04": two digits, the identifier padded to sixteen, two digits.</summary>
    public static string Header(string instrumentCode, string identifier, string blockCode) =>
        $"{Fixed(instrumentCode, 2, right: true, pad: '0')} {Fixed(Clean(identifier), 16)} " +
        $"{Fixed(blockCode, 2, right: true, pad: '0')}";

    /// <summary>A fixed-length field: padded, and cut when too long.</summary>
    public static string Fixed(string? value, int width, bool right = false, char pad = ' ')
    {
        var text = value ?? string.Empty;
        if (text.Length > width) text = text[..width];
        return right ? text.PadLeft(width, pad) : text.PadRight(width, pad);
    }

    /// <summary>An integer field (Ix), right-aligned. The manual's examples pad with spaces.</summary>
    public static string Integer(int value, int width, bool zeroPad = false) =>
        Fixed(value.ToString(CultureInfo.InvariantCulture), width, right: true, pad: zeroPad ? '0' : ' ');

    private static string Clean(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var c in value) text.Append(c is >= ' ' and <= '~' ? c : '?');
        return text.ToString();
    }

    // ---- Reading ---------------------------------------------------------------------------------

    /// <summary>One block as read: the header's three fields, the data lines and the block check.</summary>
    public sealed record Block(
        string InstrumentCode, string Identifier, string BlockCode, IReadOnlyList<string> Lines,
        int? SequenceCounter, int? SentChecksum, int? ComputedChecksum, string Text)
    {
        /// <summary>True when the block carries no check, or carries a correct one.</summary>
        public bool ChecksumValid => SentChecksum is null || SentChecksum == ComputedChecksum;

        public bool HasBlockCheck => SentChecksum is not null;

        /// <summary>Header and data lines joined by CR: what the message log stores and replays.</summary>
        public string Stored => string.Join('\r',
            new[] { Header(InstrumentCode, Identifier, BlockCode) }.Concat(Lines));

        /// <summary>The data lines carrying <paramref name="lineCode"/>.</summary>
        public IEnumerable<string> LinesOf(string lineCode) =>
            Lines.Where(l => l.Length >= 2 && l.StartsWith(lineCode, StringComparison.Ordinal)
                             && (l.Length == 2 || l[2] == ' '));

        /// <summary>The error code of line 96 (order and service responses) or 99 (block 99).</summary>
        public string? ErrorCode =>
            (LinesOf("96").FirstOrDefault() ?? LinesOf("99").FirstOrDefault()) is { } line
                ? Fields(line).ElementAtOrDefault(1)
                : null;
    }

    /// <summary>The fields of a data line: the line code first, then each space-separated value.</summary>
    public static IReadOnlyList<string> Fields(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Reads one framed block, SOH to EOT. Null when the text is not a block at all.
    /// <para>
    /// Lenient about what the manual leaves to configuration: LF or CR LF line ends, a block with or
    /// without its check, an identifier padded or not. Strict about the sum when there is one.
    /// </para>
    /// </summary>
    public static Block? ParseFramed(string text)
    {
        var soh = text.IndexOf(Soh);
        if (soh < 0) return null;

        var body = text[soh..];
        var eot = body.IndexOf(Eot);
        if (eot >= 0) body = body[..(eot + 1)];

        var lines = SplitKeepingEnds(body);

        var index = 0;
        if (index < lines.Count && Content(lines[index]) == Soh.ToString()) index++;
        else return null;

        if (index >= lines.Count) return null;
        var header = ReadHeader(Content(lines[index]));
        if (header is null) return null;
        index++;

        if (index < lines.Count && Content(lines[index]) == Stx.ToString()) index++;

        var data = new List<string>();
        var consumed = lines.Take(index).Sum(l => l.Length);

        while (index < lines.Count && Content(lines[index]) != Etx.ToString()
               && Content(lines[index]) != Eot.ToString())
        {
            data.Add(Content(lines[index]));
            consumed += lines[index].Length;
            index++;
        }

        int? sequence = null, sent = null, computed = null;

        if (index < lines.Count && Content(lines[index]) == Etx.ToString())
        {
            consumed += lines[index].Length;
            index++;

            if (index < lines.Count && int.TryParse(Content(lines[index]).Trim(), out var sc)
                && Content(lines[index]).Trim().Length == 1)
            {
                sequence = sc;
                consumed += lines[index].Length;
                computed = Checksum(body[..consumed]);
                index++;

                if (index < lines.Count && int.TryParse(Content(lines[index]).Trim(), out var bcs))
                    sent = bcs;
                else
                    sent = -1; // A counter with no sum after it: a broken check, never a valid one.
            }
        }

        return new Block(header.Value.Code, header.Value.Identifier, header.Value.BlockCode, data,
                         sequence, sent, computed, body);
    }

    /// <summary>
    /// Reads one or more blocks from text a person pasted or the log stored: framed blocks, a trace
    /// with the control characters written out ("&lt;SOH&gt;"), or the bare lines with the header
    /// first, as <see cref="Block.Stored"/> keeps them.
    /// </summary>
    public static IReadOnlyList<Block> ParseText(string text)
    {
        var real = FromTranscript(text);

        if (real.Contains(Soh))
        {
            // Each block from its own SOH, exactly as received: the sum counts every byte.
            var blocks = new List<Block>();
            for (var at = real.IndexOf(Soh); at >= 0; at = real.IndexOf(Soh, at + 1))
            {
                var next = real.IndexOf(Soh, at + 1);
                var piece = next < 0 ? real[at..] : real[at..next];
                var block = ParseFramed(piece);

                // A trace pasted in a Windows text box gains a CR before every LF it held: when the
                // sum only matches with the line ends as LF, the analyser sent LF.
                if (block is { ChecksumValid: false } && piece.Contains("\r\n"))
                {
                    var asLf = ParseFramed(piece.Replace("\r\n", "\n"));
                    if (asLf is { ChecksumValid: true, HasBlockCheck: true }) block = asLf;
                }

                if (block is not null) blocks.Add(block);
            }
            return blocks;
        }

        var lines = real.Replace("\r\n", "\n").Replace('\r', '\n')
                        .Split('\n')
                        .Select(l => l.TrimEnd())
                        .Where(l => l.Length > 0 && l != Stx.ToString() && l != Etx.ToString()
                                    && l != Eot.ToString())
                        .ToList();

        var result = new List<Block>();
        Block? current = null;
        var data = new List<string>();

        void Close()
        {
            if (current is not null) result.Add(current with { Lines = [.. data] });
            data.Clear();
        }

        foreach (var line in lines)
        {
            if (ReadHeader(line) is { } header)
            {
                Close();
                current = new Block(header.Code, header.Identifier, header.BlockCode, [], null, null, null, text);
                continue;
            }

            // The block check left on its own lines by a copied trace: a lone digit and a sum.
            if (Regex.IsMatch(line, @"^\s*\d{1,3}$")) continue;

            data.Add(line);
        }

        Close();
        return result;
    }

    /// <summary>
    /// A trace as printed — "&lt;SOH&gt;", "&lt;LF&gt;" — turned back into the characters it stands for.
    /// </summary>
    public static string FromTranscript(string text)
    {
        var real = text.Replace("<SOH>", Soh.ToString(), StringComparison.OrdinalIgnoreCase)
                       .Replace("<STX>", Stx.ToString(), StringComparison.OrdinalIgnoreCase)
                       .Replace("<ETX>", Etx.ToString(), StringComparison.OrdinalIgnoreCase)
                       .Replace("<EOT>", Eot.ToString(), StringComparison.OrdinalIgnoreCase);

        // "<CR><LF>" followed by a real line break, as a trace tool prints both.
        real = Regex.Replace(real, @"(<CR>)?<LF>(\r?\n)?", "\n", RegexOptions.IgnoreCase);
        real = Regex.Replace(real, @"<CR>(\r?\n)?", "\n", RegexOptions.IgnoreCase);
        return real;
    }

    /// <summary>
    /// The header: two digits, the identifier, two digits. Matched loosely on the identifier, which
    /// a copied trace often loses the padding of; never on a data line, none of which ends on a
    /// two-digit field after a run of at least one other field.
    /// </summary>
    private static (string Code, string Identifier, string BlockCode)? ReadHeader(string line)
    {
        var match = HeaderPattern().Match(line);
        if (!match.Success) return null;

        return (match.Groups[1].Value, match.Groups[2].Value.Trim(), match.Groups[3].Value);
    }

    [GeneratedRegex(@"^(\d{2}) (.{1,16}?) +(\d{2})\s*$")]
    private static partial Regex HeaderPattern();

    private static string Content(string line) => line.TrimEnd('\r', '\n');

    private static List<string> SplitKeepingEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>
    /// Splits a byte stream into blocks, SOH to EOT. XON/XOFF bytes from a software handshake and
    /// anything outside a block are dropped.
    /// </summary>
    public sealed class Reader(Encoding encoding)
    {
        private readonly StringBuilder _buffer = new();
        private readonly Decoder _decoder = encoding.GetDecoder();

        public void Feed(ReadOnlySpan<byte> bytes)
        {
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            var count = _decoder.GetChars(bytes, chars, flush: false);

            for (var i = 0; i < count; i++)
                if (chars[i] is not (XOn or XOff)) _buffer.Append(chars[i]);
        }

        public void Reset() => _buffer.Clear();

        /// <summary>The next complete block's text, SOH through EOT, or null while incomplete.</summary>
        public string? TryRead()
        {
            var text = _buffer.ToString();
            var soh = text.IndexOf(Soh);

            if (soh < 0)
            {
                _buffer.Clear();
                return null;
            }

            var eot = text.IndexOf(Eot, soh);
            if (eot < 0)
            {
                if (soh > 0) _buffer.Remove(0, soh);
                return null;
            }

            var end = eot + 1;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;

            _buffer.Remove(0, end);
            return text[soh..end];
        }
    }

    // ---- Host requests ---------------------------------------------------------------------------

    /// <summary>Block 09 with line 10: the result type selector (01 any result, 07 single patient result…).</summary>
    public static IReadOnlyList<string> ResultRequest(string selector) =>
        [$"10 {Fixed(selector, 2, right: true, pad: '0')}"];

    /// <summary>Block 60 with line 40: service 1 lists the barcoded tubes that have no order.</summary>
    public static IReadOnlyList<string> ServiceRequest(int service) =>
        [$"40 {service.ToString(CultureInfo.InvariantCulture)}"];

    /// <summary>
    /// A test number as the analyser reads it (line 55, I3, 1 to 999), from a mapping code written
    /// "031", "31" or "0-031" as Roche's documents print it. Null when the code is not one.
    /// </summary>
    public static int? TestNumber(string code)
    {
        var text = code.Trim();
        if (text.StartsWith("0-", StringComparison.Ordinal)) text = text[2..];

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
               && number is >= 1 and <= 999
            ? number
            : null;
    }

    /// <summary>The code a result's test number is reported under: three digits, as the Test-ID prints it.</summary>
    public static string TestCode(int number) => number.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Block 10 (order entry) for one tube, in sample mode: line 53 (order number = barcode, order
    /// date "00/00/0000" = today on the analyser, sample type if known), line 54 (rack 0 and tube 0:
    /// position defined by the barcode, priority), then one line 55 per test. An order holds at most
    /// 50 tests; more are sent as a second block 10 without line 54, which the manual describes as
    /// adding tests to the existing order.
    /// </summary>
    /// <param name="skipped">Codes that are not test numbers, reported rather than sent.</param>
    public static IReadOnlyList<IReadOnlyList<string>> OrderEntry(
        InstrumentOrderDownload order, string? sampleType, string routinePriority,
        bool zeroPadIntegers, out IReadOnlyList<string> skipped)
    {
        var numbers = new List<int>();
        var rejected = new List<string>();

        foreach (var code in order.InstrumentTestCodes)
        {
            if (TestNumber(code) is { } number) { if (!numbers.Contains(number)) numbers.Add(number); }
            else rejected.Add(code);
        }

        skipped = rejected;
        if (numbers.Count == 0) return [];

        var orderLine = $"53 {Fixed(order.SpecimenId.Replace(' ', '_'), 15)} 00/00/0000" +
                        (string.IsNullOrWhiteSpace(sampleType) ? string.Empty : $" {Fixed(sampleType.Trim(), 3)}");

        var priority = string.Equals(order.Priority, "S", StringComparison.OrdinalIgnoreCase)
            ? "S"
            : routinePriority;

        var blocks = new List<IReadOnlyList<string>>();

        foreach (var chunk in numbers.Chunk(50))
        {
            var lines = new List<string> { orderLine };

            if (blocks.Count == 0)
                lines.Add($"54 {Integer(0, 3, zeroPadIntegers)} {Integer(0, 2, zeroPadIntegers)} {Fixed(priority, 1)}");

            lines.AddRange(chunk.Select(n => $"55 {Integer(n, 3, zeroPadIntegers)}"));
            blocks.Add(lines);
        }

        return blocks;
    }

    /// <summary>A tube block 62 lists: rack, position, barcode and sample type (line 42).</summary>
    public sealed record Tube(string Barcode, string? Rack, string? Position, bool Barcoded, string? SampleType);

    /// <summary>
    /// The tubes of a block 62. Line 42 is "42 rack tube type barcode [sample type]"; the barcode
    /// holds no space (the manual forbids it in an order number), so the fields split on spaces.
    /// </summary>
    public static IReadOnlyList<Tube> Tubes(Block block)
    {
        var tubes = new List<Tube>();

        foreach (var line in block.LinesOf("42"))
        {
            var fields = Fields(line);
            if (fields.Count < 5) continue;

            tubes.Add(new Tube(fields[4], fields[1], fields[2], fields[3] == "1",
                               fields.Count > 5 ? fields[5] : null));
        }

        return tubes;
    }
}

/// <summary>
/// Turns COBAS INTEGRA result blocks into <see cref="InstrumentMessage"/>.
/// <list type="bullet">
/// <item>Patient results, blocks 04 and 07: line 53 (order number = the tube's barcode), 56 (sample
/// name), 55 (test number), 00 (result). Several tests follow one 53 when results are collated.</item>
/// <item>Control results, blocks 03 and 06: 55 (test), 01 (time), then 02 (control name) and 00
/// (result) for each control. Line 12 (block 06) adds the control lot.</item>
/// <item>Calibrations (02, 05) are the analyser's business: read, not imported.</item>
/// </list>
/// <para>
/// Line 00 is "value unit flagX flagS flagCALC flagQC [range value] [range limit]". The value is a
/// floating-point number ("+3.234000E+01"); ±9.999999E+99 stands for "???", a result the flags
/// prevented. The flags are numbers, translated here to their names so the biologist reads
/// "&gt; TEST RNG", not "31".
/// </para>
/// </summary>
public static class IntegraMessageParser
{
    /// <summary>The sentinel the analyser sends for a result it could not calculate.</summary>
    private const double Uncalculated = 9.999999E+99;

    public static InstrumentMessage Parse(string raw, InstrumentMessageFormat? format = null)
    {
        format ??= InstrumentMessageFormat.Auto;
        var blocks = RocheIntegra.ParseText(raw);

        if (blocks.Count == 0)
        {
            return new InstrumentMessage
            {
                Kind = InstrumentMessageKind.Unsupported,
                RawText = raw,
                Issues = [InstrumentParseIssue.Blocking("Interface_IntegraNoHeader")]
            };
        }

        var issues = new List<InstrumentParseIssue>();
        var specimens = new List<InstrumentSpecimenResults>();
        var queried = new List<string>();
        var kinds = new List<InstrumentMessageKind>();

        foreach (var block in blocks)
        {
            if (!block.ChecksumValid)
                issues.Add(InstrumentParseIssue.Warning("Interface_IntegraChecksum",
                    $"{block.SentChecksum} ≠ {block.ComputedChecksum}"));

            switch (block.BlockCode)
            {
                case RocheIntegra.BlockPatient or RocheIntegra.BlockPatientWithLots:
                    kinds.Add(InstrumentMessageKind.Results);
                    specimens.AddRange(Patients(block, format, issues));
                    break;

                case RocheIntegra.BlockControl or RocheIntegra.BlockControlWithLots:
                    kinds.Add(InstrumentMessageKind.Results);
                    specimens.AddRange(Controls(block, format, issues));
                    break;

                case RocheIntegra.BlockCalibration or RocheIntegra.BlockCalibrationWithLots:
                    kinds.Add(InstrumentMessageKind.Status);
                    issues.Add(InstrumentParseIssue.Warning("Interface_IntegraCalibrationIgnored"));
                    break;

                case RocheIntegra.BlockTubes:
                    kinds.Add(InstrumentMessageKind.Query);
                    queried.AddRange(RocheIntegra.Tubes(block).Select(t => t.Barcode));
                    break;

                case RocheIntegra.BlockIdle or RocheIntegra.BlockOrderResponse
                    or RocheIntegra.BlockServiceResponse:
                    kinds.Add(InstrumentMessageKind.Acknowledgement);
                    break;

                case RocheIntegra.BlockError:
                    kinds.Add(InstrumentMessageKind.Status);
                    issues.Add(InstrumentParseIssue.Warning("Interface_IntegraErrorBlock",
                        block.ErrorCode ?? "?"));
                    break;

                default:
                    kinds.Add(InstrumentMessageKind.Unsupported);
                    issues.Add(InstrumentParseIssue.Warning("Interface_IntegraBlockIgnored", block.BlockCode));
                    break;
            }
        }

        var first = blocks[0];

        var kind = kinds.Contains(InstrumentMessageKind.Results) ? InstrumentMessageKind.Results
            : kinds.Contains(InstrumentMessageKind.Query) ? InstrumentMessageKind.Query
            : kinds[0];

        return new InstrumentMessage
        {
            Kind = kind,
            SenderId = first.Identifier,
            SenderVersion = first.InstrumentCode,
            Specimens = specimens,
            QueriedSpecimenIds = queried,
            Issues = issues,
            // The bare lines, header first and CR between them, as the ASTM log keeps its records:
            // the stored text parses back to the same message, which is what a replay relies on.
            RawText = string.Join('\r', blocks.Select(b => b.Stored))
        };
    }

    private static IEnumerable<InstrumentSpecimenResults> Patients(
        RocheIntegra.Block block, InstrumentMessageFormat format, List<InstrumentParseIssue> issues)
    {
        var specimens = new List<InstrumentSpecimenResults>();

        string? specimenId = null;
        string? test = null;
        string? rawTest = null;
        DateTimeOffset? at = null;
        var results = new List<InstrumentResult>();

        void Close()
        {
            if (specimenId is null && results.Count == 0) return;

            specimens.Add(new InstrumentSpecimenResults
            {
                SpecimenId = specimenId,
                Results = [.. results]
            });

            results.Clear();
        }

        foreach (var line in block.Lines)
        {
            var fields = RocheIntegra.Fields(line);
            if (fields.Count == 0) continue;

            switch (fields[0])
            {
                case "53":
                    Close();
                    specimenId = fields.ElementAtOrDefault(1);
                    at = null;
                    break;

                case "55":
                    rawTest = line.Length > 3 ? line[3..] : string.Empty;
                    test = Code(fields.ElementAtOrDefault(1));
                    break;

                case "01":
                    // A time with no date: kept only with the access date of line 15, never on a
                    // date guessed from the clock — a replay days later would date it wrongly.
                    break;

                case "15":
                    at = DateTime(fields.ElementAtOrDefault(2), fields.ElementAtOrDefault(1));
                    break;

                case "00" when test is not null:
                    results.Add(Result(test, rawTest, fields, block.BlockCode, at, format));
                    break;

                case "00":
                    issues.Add(InstrumentParseIssue.Warning("Interface_IntegraResultWithoutTest", line));
                    break;
            }
        }

        Close();

        if (specimens.Count == 0)
            issues.Add(InstrumentParseIssue.Warning("Interface_IntegraNoResult"));

        return specimens;
    }

    private static IEnumerable<InstrumentSpecimenResults> Controls(
        RocheIntegra.Block block, InstrumentMessageFormat format, List<InstrumentParseIssue> issues)
    {
        // One "specimen" per control material, its results gathered across the tests of the block.
        var byControl = new Dictionary<string, (string? Lot, List<InstrumentResult> Results)>(StringComparer.Ordinal);
        var order = new List<string>();

        string? test = null, rawTest = null, control = null, lot = null;
        DateTimeOffset? at = null;

        foreach (var line in block.Lines)
        {
            var fields = RocheIntegra.Fields(line);
            if (fields.Count == 0) continue;

            switch (fields[0])
            {
                case "55":
                    rawTest = line.Length > 3 ? line[3..] : string.Empty;
                    test = Code(fields.ElementAtOrDefault(1));
                    break;

                case "15":
                    at = DateTime(fields.ElementAtOrDefault(2), fields.ElementAtOrDefault(1));
                    break;

                case "02":
                    control = line.Length > 3 ? line[3..].Trim() : null;
                    lot = null;
                    break;

                case "12":
                    lot = fields.ElementAtOrDefault(1);
                    break;

                case "00" when test is not null && control is not null:
                    if (!byControl.TryGetValue(control, out var entry))
                    {
                        entry = (lot, []);
                        byControl[control] = entry;
                        order.Add(control);
                    }
                    else if (lot is not null && entry.Lot is null)
                    {
                        byControl[control] = entry = (lot, entry.Results);
                    }

                    entry.Results.Add(Result(test, rawTest, fields, block.BlockCode, at, format));
                    break;

                case "00":
                    issues.Add(InstrumentParseIssue.Warning("Interface_IntegraResultWithoutTest", line));
                    break;
            }
        }

        return order.Select(name => new InstrumentSpecimenResults
        {
            SpecimenId = name,
            IsControl = true,
            // The control's name as the analyser knows it, then its lot when line 12 gave one: the
            // QC targets are found by lot number, which may stand in either.
            ControlIdentifier = byControl[name].Lot is { } l ? $"{name} {l}" : name,
            Results = byControl[name].Results
        }).ToList();
    }

    /// <summary>A test number in its three-digit form, or the text as sent when it is not one.</summary>
    private static string Code(string? field) =>
        field is not null && int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? RocheIntegra.TestCode(n)
            : field ?? string.Empty;

    /// <summary>
    /// Line 00: "00 value unit flagX flagS flagCALC flagQC [range value] [range limit]". The unit
    /// may be blank, so it is recognised by not being a number rather than by its column.
    /// </summary>
    private static InstrumentResult Result(string test, string? rawTest, IReadOnlyList<string> fields,
                                           string blockCode, DateTimeOffset? at,
                                           InstrumentMessageFormat format)
    {
        var raw = fields.ElementAtOrDefault(1);
        var rest = fields.Skip(2).ToList();

        string? unit = null;
        if (rest.Count > 0 && !int.TryParse(rest[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            unit = rest[0];
            rest.RemoveAt(0);
        }

        int Flag(int i) => i < rest.Count && int.TryParse(rest[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        var flagX = Flag(0);
        var flagCalc = Flag(2);
        var flagQc = Flag(3);

        // The flags as the analyser's own screen names them, language-neutral like any vendor flag.
        var comments = new List<string>();
        comments.AddRange(DescribeFlagX(flagX).Select(f => $"X {f}"));

        if (flagCalc != 0)
        {
            // With > TEST RNG and < TEST RNG the analyser sends the range limit that was crossed.
            comments.Add(flagCalc is 30 or 31 && Number(rest.ElementAtOrDefault(4)) is { } limit and not 0
                ? $"{CalcFlagName(flagCalc)} ({Format(limit)})"
                : CalcFlagName(flagCalc));
        }

        if (flagQc != 0) comments.Add(QcFlagName(flagQc));

        decimal? number = null;
        var notCalculated = false;
        if (raw is not null) number = NumberOrSentinel(raw, out notCalculated);

        return new InstrumentResult
        {
            InstrumentTestCode = test,
            RawTestId = rawTest?.Trim(),
            Value = number is { } value ? Format(value) : notCalculated ? null : raw,
            Unit = unit,
            AbnormalFlag = AbnormalFlag(flagCalc, flagQc),
            // "???" on the analyser: no value to report, and saying so is what X is for.
            ResultStatus = notCalculated ? "X" : "F",
            MeasuredAt = at,
            Comments = comments,
            ResultType = blockCode,
            // A result the analyser itself marks deleted (flag X, d) is shown, never imported.
            IsExcluded = (flagX & 3) == 3 || !format.Accepts(blockCode)
        };
    }

    /// <summary>
    /// The interpretation flag the LIS shows: the analyser's reference-range check (QC flag 13/14,
    /// critical 21/22) first, then the test range (CALC 30/31) as a censored value.
    /// </summary>
    private static string? AbnormalFlag(int calc, int qc) => qc switch
    {
        13 => "L",
        14 => "H",
        21 => "LL",
        22 => "HH",
        _ => calc switch
        {
            30 => "<",
            31 => ">",
            _ => null
        }
    };

    /// <summary>
    /// Flag X is a byte of two-bit groups: calibration (C, bits 7–6), control (Q, bits 5–4),
    /// replicate/dilution (bits 3–2) and the result itself (bits 1–0).
    /// </summary>
    public static IReadOnlyList<string> DescribeFlagX(int flagX)
    {
        var parts = new List<string>();

        switch ((flagX & 192) >> 6)
        {
            case 1: parts.Add("c"); break;   // calibration erroneous or corrected, accepted
            case 2: parts.Add("C"); break;   // calibration not accepted
            case 3: parts.Add("p"); break;   // postponed calibration
        }

        switch ((flagX & 48) >> 4)
        {
            case 1: parts.Add("q"); break;   // control erroneous or corrected, accepted
            case 2: parts.Add("Q"); break;   // control not accepted
        }

        switch ((flagX & 12) >> 2)
        {
            case 1: parts.Add("->"); break;  // replicate
            case 2: parts.Add("v"); break;   // after dilution
            case 3: parts.Add("^"); break;   // after concentration
        }

        switch (flagX & 3)
        {
            case 1: parts.Add("r"); break;   // result erroneous or corrected, accepted
            case 2: parts.Add("R"); break;   // result not accepted
            case 3: parts.Add("d"); break;   // result deleted
        }

        return parts;
    }

    /// <summary>The CALC flags by number (manual, "Flag CALC"): 1 is the highest priority.</summary>
    public static string CalcFlagName(int value) => value switch
    {
        1 => "HIGH ABS",
        2 => "DARK ERR",
        3 => "OUTLIERS",
        4 => "FP UNSTAB",
        5 => "POL ERR",
        6 => "LOW SIGNAL",
        7 => "NO FLUID",
        8 => "NO ISECAL",
        9 => "SEG FLUID",
        10 => "SEG ISECAL",
        11 => "AIR FLUID",
        12 => "AIR ISECAL",
        13 => "ISE UNSTAB",
        14 => "< REAG RNG",
        15 => "> REAG RNG",
        16 => "EP UNSTAB",
        17 => "HIGH ACT",
        18 => "LOW ACT",
        19 => "CURVE DIR",
        20 => "NON LINEAR",
        21 => "< KIN READ",
        22 => "HIGH BKG",
        23 => "> REPL DEV",
        24 => "CALC ERROR",
        25 => "NON MONO",
        26 => "OUT OF RNG",
        27 => "> STD DEV",
        28 => "SOL 1 F DEV",
        29 => "AG EXCESS",
        30 => "< TEST RNG",
        31 => "> TEST RNG",
        33 => "TEST BLOCKED",
        _ => $"CALC {value}"
    };

    /// <summary>The QC flags by number (manual, "Flag QC"): Westgard rules and range checks.</summary>
    public static string QcFlagName(int value) => value switch
    {
        1 => "R1(2s)",
        2 => "R1(3s)",
        3 => "R2(2s)",
        4 => "R(4s)",
        5 => "R4(1s)",
        6 => "R7v",
        7 => "R7^",
        8 => "R7x",
        9 => "R10x",
        10 => "> ACC RNG",
        11 => "> UPLIMIT",
        12 => "< LOWLIMIT",
        13 => "<RR",
        14 => ">RR",
        19 => "POS",
        20 => "POSITIVE",
        21 => "< Critical Range",
        22 => "> Critical Range",
        23 => "DC >",
        24 => "DC > %",
        _ => $"QC {value}"
    };

    private static decimal? NumberOrSentinel(string raw, out bool uncalculated)
    {
        uncalculated = false;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;

        if (Math.Abs(number) >= Uncalculated * 0.999999)
        {
            uncalculated = true;
            return null;
        }

        return Number(raw);
    }

    private static decimal? Number(string? raw) =>
        raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && Math.Abs(d) < 7.9e27
            ? Math.Round((decimal)d, 6)
            : null;

    private static string Format(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>D1 "dd/MM/yyyy" (or the older D2 "dd-MMM-yy") with T1 "HH:mm:ss".</summary>
    private static DateTimeOffset? DateTime(string? date, string? time)
    {
        if (date is null) return null;

        string[] formats = ["dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd-MMM-yy HH:mm:ss", "dd/MM/yyyy"];

        return System.DateTime.TryParseExact($"{date} {time}".Trim(), formats, CultureInfo.InvariantCulture,
                                             DateTimeStyles.AssumeLocal, out var value)
            ? new DateTimeOffset(value)
            : null;
    }
}
