using System.Globalization;
using System.Net.Sockets;
using System.Text;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments.Protocol;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Simulator;

/// <summary>
/// The messages a Mindray analyser sends, laid out as its LIS manual shows them: ORU^R01 results of
/// a sample, a control or a calibration (MSH-16 0, 2 or 1), the QRY^Q02 worklist query and the
/// ACK^Q03 that closes the dialogue. A BS chemistry or CL immunoassay result carries the barcode in
/// OBR-2 and the analyser's sample number in OBR-3, one OBX per test (code in OBX-3, name in OBX-4);
/// a BC haematology one the sample number in OBR-3, LOINC-coded parameters « 6690-2^WBC^LN »,
/// mode and flag lines (IS) and histograms as Base64 bitmaps (ED).
/// <para>
/// Values are plausible numbers, not Mindray's. What is laid out is the manuals' reading; a field
/// a real trace contradicts is a setting of the link, not a change here.
/// </para>
/// </summary>
public sealed class MindrayInstrumentEmulator(Hl7Dialect family = Hl7Dialect.MindrayBs, int? seed = null)
{
    private readonly Random _random = seed is { } s ? new Random(s) : new Random();
    private int _controlId;

    public Hl7Dialect Family { get; } = family;

    /// <summary>MSH-4 of every message: the analyser's model.</summary>
    public string Model { get; set; } = family switch
    {
        Hl7Dialect.MindrayBc => "BC-5380",
        Hl7Dialect.MindrayCl => "CL-1000i",
        _ => "BS-480"
    };

    /// <summary>The analyser's sample number, OBR-3, counted on.</summary>
    public int SampleNumber { get; set; } = 1;

    public DateTime Now { get; set; } = DateTime.Now;

    private string Timestamp => Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private string NextControlId() => (++_controlId).ToString(CultureInfo.InvariantCulture);

    private string Header(string type, string resultKind) =>
        $"MSH|^~\\&|Mindray|{Model}|||{Timestamp}||{type}|{NextControlId()}|P|{MindrayHl7.Version}||||{resultKind}||" +
        (Family == Hl7Dialect.MindrayBc ? "UNICODE" : "ASCII") + "|||";

    /// <summary>A sample's results, ORU^R01 with MSH-16 « 0 ».</summary>
    public string Results(string barcode, IReadOnlyList<(string Code, decimal Value, string Unit)> results,
                          string? patientName = null)
    {
        if (Family == Hl7Dialect.MindrayBc) return HaematologyResults(barcode, results, patientName);

        var segments = new List<string>
        {
            Header("ORU^R01", MindrayHl7.SampleResult),
            $"PID|1||||{patientName ?? string.Empty}||||||||||||||||||||||||||",
            $"OBR|1|{barcode}|{SampleNumber++}|Mindray^{Model}|N||{Timestamp}||||||||serum|||||||||||||||||||||||||||||||||"
        };

        var i = 1;
        foreach (var (code, value, unit) in results)
            segments.Add($"OBX|{i++}|NM|{code}|{code}|{Number(value)}|{unit}|-|N|||F||{Number(value)}|{Timestamp}||||");

        return Join(segments);
    }

    /// <summary>
    /// A BC complete blood count: the mode lines, then the parameters under LOINC (flagged « H »
    /// when high), a flag line, and the WBC histogram as a Base64 bitmap.
    /// </summary>
    private string HaematologyResults(string sampleId, IReadOnlyList<(string Code, decimal Value, string Unit)> results,
                                      string? patientName)
    {
        var segments = new List<string>
        {
            Header("ORU^R01", MindrayHl7.SampleResult),
            $"PID|1||^^^^MR||^{patientName ?? string.Empty}|||O",
            "PV1|1",
            $"OBR|1||{sampleId}|00001^Automated Count^99MRC||{Timestamp}|{Timestamp}|||||||||||||||||HM||||||||admin"
        };

        var i = 1;
        segments.Add($"OBX|{i++}|IS|08001^Take Mode^99MRC||O||||||F");
        segments.Add($"OBX|{i++}|IS|08002^Blood Mode^99MRC||W||||||F");
        segments.Add($"OBX|{i++}|IS|08003^Test Mode^99MRC||CBC+DIFF||||||F");
        segments.Add($"OBX|{i++}|NM|30525-0^Age^LN||35|yr|||||F");

        foreach (var (code, value, unit) in results)
        {
            var loinc = Loinc(code);
            var high = code == "WBC" && value > 10m;
            segments.Add($"OBX|{i++}|NM|{loinc}^{code}^LN||{Number(value, 2)}|{unit}|{RangeOf(code)}|{(high ? "H" : "N")}|||F");
        }

        segments.Add($"OBX|{i++}|IS|12014^Platelet Clump?^99MRC||T||||||F");
        segments.Add($"OBX|{i++}|ED|15000^WBC Histogram. BMP^99MRC||^Image^BMP^Base64^{Histogram()}||||||F");
        segments.Add($"OBX|{i}|NM|15051^WBC Histogram Left Line^99MRC||12||||||F");

        return Join(segments);
    }

    /// <summary>A control's results, ORU^R01 with MSH-16 « 2 »: number, name, lot, expiry and level in OBR-2 to OBR-6.</summary>
    public string ControlResults(string controlName, string lot, string level,
                                 IReadOnlyList<(string Code, decimal Value, string Unit)> results)
    {
        var segments = new List<string>
        {
            Header("ORU^R01", MindrayHl7.QcResult),
            $"OBR|1|{SampleNumber++}|{controlName}|{lot}|{Now.AddMonths(6):yyyyMMdd}|{level}|{Timestamp}||||||||serum|||||||||||||||||||||||||||||||||"
        };

        var i = 1;
        foreach (var (code, value, unit) in results)
            segments.Add($"OBX|{i++}|NM|{code}|{code}|{Number(value)}|{unit}|||||F||{Number(value)}|{Timestamp}||||");

        return Join(segments);
    }

    /// <summary>A calibration's results, ORU^R01 with MSH-16 « 1 ».</summary>
    public string CalibrationResults(string code) => Join(
    [
        Header("ORU^R01", MindrayHl7.CalibrationResult),
        $"OBR|1|||{code}|{code}|Linear|{Timestamp}||||||||||||||||||||||||||||||||||||||||",
        $"OBX|1|NM|{code}|{code}|0.0123|||||||F||0.0123|{Timestamp}||||"
    ]);

    /// <summary>The worklist query for a scanned barcode: QRY^Q02, the barcode in QRD-8.</summary>
    public string Query(string barcode) => Join(
    [
        Header("QRY^Q02", string.Empty),
        $"QRD|{Timestamp}|R|D|{_controlId}|||RD|{barcode}|OTH|||T|",
        $"QRF|{Model}|{Timestamp}|{Timestamp}|||RCT|COR|ALL||"
    ]);

    /// <summary>ACK^Q03, the analyser taking a DSR^Q03.</summary>
    public string AcknowledgeWorklist(string dsr)
    {
        var message = Hl7Message.Parse(dsr);
        return Join(
        [
            Header("ACK^Q03", string.Empty),
            $"MSA|AA|{message.ControlId}|Message accepted|||0|",
            "ERR|0|"
        ]);
    }

    /// <summary>The barcode and the test codes a DSR^Q03 asks for, read as the analyser reads them.</summary>
    public static (string? Barcode, IReadOnlyList<string> Tests, string? PatientName) ReadWorklist(string dsr)
    {
        var message = Hl7Message.Parse(dsr);
        string? barcode = null, name = null;
        var tests = new List<string>();

        foreach (var dsp in message.SegmentsNamed("DSP"))
        {
            if (!int.TryParse(message.Field(dsp, 1), out var line)) continue;

            var value = message.Field(dsp, 3);
            if (line == 3) name = message.Unescape(value);
            else if (line == 21) barcode = value;
            else if (line >= MindrayHl7.FirstTestLine && message.Component(dsp, 3, 1) is { } code) tests.Add(code);
        }

        return (barcode, tests, name);
    }

    /// <summary>A plausible value, in the unit an analyser of the family reports it.</summary>
    public (decimal Value, string Unit) Measure(string code)
    {
        var (low, high, unit) = code.ToUpperInvariant() switch
        {
            "GLU" or "GLUC" => (3.9, 6.1, "mmol/L"),
            "CREA" or "CRE" => (53.0, 106.0, "umol/L"),
            "UREA" or "BUN" => (2.5, 7.5, "mmol/L"),
            "ALT" or "AST" => (10.0, 40.0, "U/L"),
            "CHOL" or "TC" => (3.0, 5.2, "mmol/L"),
            "TG" => (0.5, 1.7, "mmol/L"),
            "WBC" => (4.0, 10.0, "10*9/L"),
            "RBC" => (4.0, 5.5, "10*12/L"),
            "HGB" => (120.0, 160.0, "g/L"),
            "HCT" => (37.0, 48.0, "%"),
            "PLT" => (150.0, 400.0, "10*9/L"),
            "TSH" => (0.4, 4.0, "uIU/mL"),
            "FT4" => (12.0, 22.0, "pmol/L"),
            "HBSAG" => (0.01, 0.2, "IU/mL"),
            _ => (1.0, 10.0, string.Empty)
        };

        return (Math.Round((decimal)(low + _random.NextDouble() * (high - low)), 2), unit);
    }

    /// <summary>The codes a frame is written for when the link maps none: what the family measures most.</summary>
    public static IReadOnlyList<string> DefaultCodes(Hl7Dialect family) => family switch
    {
        Hl7Dialect.MindrayBc => ["WBC", "RBC", "HGB", "PLT"],
        Hl7Dialect.MindrayCl => ["TSH", "FT4"],
        _ => ["GLU", "CREA", "ALT"]
    };

    /// <summary>
    /// A sample's result message for the « Tester une trame » window, for the tube of the last
    /// transmission and the link's own codes, one segment per line.
    /// </summary>
    public static string SampleTranscript(Hl7Dialect family, string barcode, IReadOnlyList<string>? codes = null)
    {
        var emulator = new MindrayInstrumentEmulator(family, seed: 7);
        var tests = (codes is { Count: > 0 } ? codes : DefaultCodes(family))
            .Select(c => { var (value, unit) = emulator.Measure(c); return (c, value, unit); })
            .ToList();

        return emulator.Results(barcode, tests).Replace("\r", "\r\n", StringComparison.Ordinal);
    }

    private static string Join(IEnumerable<string> segments) => string.Join('\r', segments) + "\r";

    private static string Number(decimal value, int decimals = 6) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Loinc(string code) => code.ToUpperInvariant() switch
    {
        "WBC" => "6690-2",
        "RBC" => "789-8",
        "HGB" => "718-7",
        "HCT" => "4544-3",
        "PLT" => "777-3",
        "MCV" => "787-2",
        _ => "99999-9"
    };

    private static string RangeOf(string code) => code.ToUpperInvariant() switch
    {
        "WBC" => "4.00-10.00",
        "RBC" => "3.50-5.50",
        "HGB" => "110-160",
        "PLT" => "100-300",
        _ => string.Empty
    };

    /// <summary>A few bytes of a bitmap header in Base64, standing for the histogram picture.</summary>
    private string Histogram()
    {
        var bytes = new byte[96];
        _random.NextBytes(bytes);
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        return Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// The emulator on the wire: like the analyser, it connects to the LIS (the LIS listens), sends an
/// MLLP-framed message and reads what comes back — ACK^R01 for results, QCK^Q02 then DSR^Q03 for a
/// query, the DSR acknowledged with ACK^Q03. For tests over a loopback socket, and for commissioning
/// a link before the analyser is on site.
/// </summary>
public sealed class MindrayAnalyserSimulator(MindrayInstrumentEmulator emulator, string host, int port,
                                             Encoding? encoding = null, ILogger? logger = null)
    : IAsyncDisposable
{
    private readonly Encoding _encoding = encoding ?? (emulator.Family == Hl7Dialect.MindrayBc ? Encoding.UTF8 : Encoding.Latin1);
    private readonly Mllp.Reader _reader = new(encoding ?? (emulator.Family == Hl7Dialect.MindrayBc ? Encoding.UTF8 : Encoding.Latin1));
    private TcpClient? _client;
    private NetworkStream? _stream;

    public MindrayInstrumentEmulator Emulator => emulator;

    /// <summary>Seconds to wait for each answer, as the analyser's own « response time-out ».</summary>
    public TimeSpan AnswerTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Every message the LIS sent, in order.</summary>
    public List<string> Received { get; } = [];

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, ct);
        _stream = _client.GetStream();
        logger?.LogInformation("Mindray simulator ({Model}) connected to {Host}:{Port}.", emulator.Model, host, port);
    }

    /// <summary>Sends a sample's results and returns the LIS's acknowledgement.</summary>
    public async Task<string?> ReportAsync(string barcode, IReadOnlyList<(string Code, decimal Value, string Unit)> results,
                                           CancellationToken ct = default)
    {
        await SendAsync(emulator.Results(barcode, results), ct);
        return await ReadAsync(ct);
    }

    /// <summary>Sends any message the emulator wrote and returns the LIS's first answer.</summary>
    public async Task<string?> ExchangeAsync(string message, CancellationToken ct = default)
    {
        await SendAsync(message, ct);
        return await ReadAsync(ct);
    }

    /// <summary>
    /// Queries the worklist of a barcode: QCK^Q02 and, when it says « OK », the DSR^Q03, which is
    /// acknowledged with ACK^Q03 as the analyser does.
    /// </summary>
    public async Task<(string? QueryAck, string? Worklist)> QueryAsync(string barcode, CancellationToken ct = default)
    {
        await SendAsync(emulator.Query(barcode), ct);

        var qck = await ReadAsync(ct);
        if (qck is null) return (null, null);

        var found = Hl7Message.Parse(qck) is var parsed && parsed.FirstSegment("QAK") is { } qak
                    && parsed.Field(qak, 2) == "OK";
        if (!found) return (qck, null);

        var dsr = await ReadAsync(ct);
        if (dsr is not null) await SendAsync(emulator.AcknowledgeWorklist(dsr), ct);

        return (qck, dsr);
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        await _stream.WriteAsync(Mllp.Wrap(message, _encoding), ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>The next message from the LIS, or null when none comes within the time-out.</summary>
    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AnswerTimeout);
        var buffer = new byte[8192];

        while (true)
        {
            if (_reader.TryReadMessage() is { } message)
            {
                Received.Add(message);
                return message;
            }

            int read;
            try
            {
                read = await _stream.ReadAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read == 0) return null;
            _reader.Feed(buffer.AsSpan(0, read));
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }
}
