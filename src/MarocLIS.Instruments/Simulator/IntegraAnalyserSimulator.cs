using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MarocLIS.Instruments.Protocol;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Simulator;

/// <summary>
/// A simulated COBAS INTEGRA 400 plus, answering the host the way the manual describes.
/// <para>
/// The analyser is the slave, so this is a responder: every block the host sends gets one block
/// back. A result request (09) gets the next waiting result — one test per block, as with the
/// selector 01 or 07 — or block 00 when none is left. A service request (60, service 1) gets the
/// barcoded tubes still without an order (62) or block 69 with line 96. An order entry (10) is
/// taken, answered 19 with line 96 00, and its tests "measured", so their results queue up for the
/// next request. A request repeating the previous counter gets the previous answer again, and a
/// request whose sum is wrong gets block 99 with error 01, as on the analyser.
/// </para>
/// <para>
/// What is not in the manual is not invented here: the answer to a synchronisation is block 00,
/// and values are plausible numbers, not Roche's.
/// </para>
/// </summary>
public sealed class IntegraInstrumentEmulator
{
    private readonly Random _random;
    private readonly Queue<(string Block, IReadOnlyList<string> Lines)> _results = new();
    private int? _lastCounter;
    private string? _lastAnswer;

    public IntegraInstrumentEmulator(int? seed = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
    }

    /// <summary>The analyser's identifier, sixteen characters in every header it sends.</summary>
    public string Identifier { get; set; } = "COBAS INTEGRA400";

    public string InstrumentCode { get; set; } = RocheIntegra.Integra400PlusCode;

    /// <summary>"Block check: True" on the analyser.</summary>
    public bool BlockCheck { get; set; } = true;

    public string LineEnd { get; set; } = "\n";

    /// <summary>Barcoded tubes on board with no order yet: what block 62 lists.</summary>
    public List<(string Barcode, string? SampleType)> TubesWithoutOrder { get; } = [];

    /// <summary>Order entries received, block by block, for a test to assert what was downloaded.</summary>
    public List<IReadOnlyList<string>> ReceivedOrders { get; } = [];

    /// <summary>Every block the host sent, in order.</summary>
    public List<string> ReceivedBlockCodes { get; } = [];

    /// <summary>Queues one patient result, sent as block 04 on a later result request.</summary>
    public void QueuePatientResult(string barcode, int test, decimal value, string unit,
                                   string sampleType = "SER", int flagCalc = 0, int flagQc = 0)
        => _results.Enqueue((RocheIntegra.BlockPatient, PatientLines(barcode, test, value, unit, sampleType, flagCalc, flagQc)));

    /// <summary>Queues one control result, sent as block 03.</summary>
    public void QueueControlResult(string control, int test, decimal value, string unit)
        => _results.Enqueue((RocheIntegra.BlockControl,
        [
            $"55 {RocheIntegra.Integer(test, 3)}",
            $"01 {DateTime.Now:HH:mm:ss}",
            $"02 {RocheIntegra.Fixed(control, 5)}",
            ResultLine(value, unit, 0, 0)
        ]));

    public int PendingResults => _results.Count;

    /// <summary>Answers one host block; null when the text is not a block (the analyser stays silent).</summary>
    public string? Respond(string hostBlock)
    {
        var block = RocheIntegra.ParseFramed(hostBlock);
        if (block is null) return null;

        ReceivedBlockCodes.Add(block.BlockCode);

        if (!block.ChecksumValid) return Answer(RocheIntegra.BlockError, ["99 01"], block.SequenceCounter);

        // Synchronisation: block 00 without a block check. Counters start again.
        if (block.BlockCode == RocheIntegra.BlockIdle && !block.HasBlockCheck)
        {
            _lastCounter = null;
            _lastAnswer = null;
            return Answer(RocheIntegra.BlockIdle, [], null);
        }

        // The same counter as last time: the host asks for the previous answer again.
        if (block.SequenceCounter is { } sc && sc == _lastCounter && _lastAnswer is not null)
            return _lastAnswer;

        var answer = block.BlockCode switch
        {
            RocheIntegra.BlockResultRequest => _results.Count > 0
                ? Answer(_results.Peek().Block, _results.Dequeue().Lines, block.SequenceCounter)
                : Answer(RocheIntegra.BlockIdle, [], block.SequenceCounter),

            RocheIntegra.BlockServiceRequest => TubesWithoutOrder.Count > 0
                ? Answer(RocheIntegra.BlockTubes,
                         [.. TubesWithoutOrder.Select((t, i) =>
                             $"42 {RocheIntegra.Integer(1, 3)} {RocheIntegra.Integer(i + 1, 2)} 1 " +
                             $"{RocheIntegra.Fixed(t.Barcode, 15)}" + (t.SampleType is null ? "" : $" {t.SampleType}"))],
                         block.SequenceCounter)
                : Answer(RocheIntegra.BlockServiceResponse, ["96 61"], block.SequenceCounter),

            RocheIntegra.BlockOrderEntry => TakeOrder(block),

            _ => Answer(RocheIntegra.BlockError, ["99 06"], block.SequenceCounter)
        };

        _lastCounter = block.SequenceCounter;
        _lastAnswer = answer;
        return answer;
    }

    private string TakeOrder(RocheIntegra.Block block)
    {
        ReceivedOrders.Add(block.Lines);

        var order = block.LinesOf("53").FirstOrDefault();
        var barcode = order is null ? null : RocheIntegra.Fields(order).ElementAtOrDefault(1);
        var sampleType = order is null ? null : RocheIntegra.Fields(order).ElementAtOrDefault(3);

        if (barcode is null) return Answer(RocheIntegra.BlockOrderResponse, ["96 23"], block.SequenceCounter);

        TubesWithoutOrder.RemoveAll(t => t.Barcode == barcode);

        foreach (var line in block.LinesOf("55"))
        {
            if (!int.TryParse(RocheIntegra.Fields(line).ElementAtOrDefault(1), out var test)) continue;
            var (value, unit) = Measure(test);
            QueuePatientResult(barcode, test, value, unit, sampleType ?? "SER");
        }

        return Answer(RocheIntegra.BlockOrderResponse, ["96 00"], block.SequenceCounter);
    }

    private string Answer(string blockCode, IReadOnlyList<string> lines, int? counter) =>
        RocheIntegra.Build(InstrumentCode, Identifier, blockCode, lines,
                           BlockCheck ? counter ?? 0 : null, LineEnd);

    /// <summary>Lines 53, 55 and 00 of one patient result, as block 04 carries them.</summary>
    public static IReadOnlyList<string> PatientLines(string barcode, int test, decimal value, string unit,
                                                     string sampleType = "SER", int flagCalc = 0, int flagQc = 0) =>
    [
        $"53 {RocheIntegra.Fixed(barcode, 15)} {DateTime.Today:dd/MM/yyyy} {RocheIntegra.Fixed(sampleType, 3)}",
        $"55 {RocheIntegra.Integer(test, 3)}",
        ResultLine(value, unit, flagCalc, flagQc)
    ];

    /// <summary>
    /// Line 00 laid out as the manual's example: value (F13), unit (S6), the four flags (I3) and the
    /// two range fields (F13).
    /// </summary>
    public static string ResultLine(decimal value, string unit, int flagCalc, int flagQc) =>
        $"00 {Scientific((double)value)} {RocheIntegra.Fixed(unit, 6)} {RocheIntegra.Integer(0, 3)} " +
        $"{RocheIntegra.Integer(0, 3)} {RocheIntegra.Integer(flagCalc, 3)} {RocheIntegra.Integer(flagQc, 3)} " +
        $"{Scientific(0)} {Scientific(0)}";

    /// <summary>"+3.234000E+01" or " 8.694475E+01": thirteen characters, seven significant digits.</summary>
    public static string Scientific(double value)
    {
        var text = value.ToString("0.000000E+00", CultureInfo.InvariantCulture);
        return RocheIntegra.Fixed(text, 13, right: true);
    }

    /// <summary>
    /// A result block as a person would paste it from a trace, for the "Tester une trame" window: one
    /// block 04 per test, framed and summed.
    /// </summary>
    public static string SampleTranscript(string barcode, IReadOnlyList<int>? tests = null)
    {
        var emulator = new IntegraInstrumentEmulator(seed: 7);
        var text = new StringBuilder();

        foreach (var test in tests ?? [31, 445, 3])
        {
            var (value, unit) = emulator.Measure(test);
            var block = RocheIntegra.Build(RocheIntegra.Integra400PlusCode, emulator.Identifier,
                                           RocheIntegra.BlockPatient,
                                           PatientLines(barcode, test, value, unit), 1);

            text.Append(block.Replace(RocheIntegra.Soh.ToString(), "<SOH>")
                             .Replace(RocheIntegra.Stx.ToString(), "<STX>")
                             .Replace(RocheIntegra.Etx.ToString(), "<ETX>")
                             .Replace(RocheIntegra.Eot.ToString(), "<EOT>")
                             .Replace("\n", "<LF>\r\n"));
        }

        return text.ToString();
    }

    /// <summary>
    /// A plausible value for a test number, in the unit of the starter catalogue (the analyser
    /// reports in whatever unit its test definition holds).
    /// </summary>
    public (decimal Value, string Unit) Measure(int test)
    {
        var (low, high, unit) = test switch
        {
            31 or 231 => (0.70, 1.10, "g/L"),     // GLUC3
            445 or 612 => (7.0, 13.0, "mg/L"),    // CREJ2 / CREP2
            3 => (0.15, 0.45, "g/L"),             // UREAL
            586 => (1.20, 2.00, "g/L"),           // CHOL2
            10 => (0.40, 1.50, "g/L"),            // TRIGL
            494 or 594 => (10.0, 40.0, "U/L"),    // ASTL
            495 or 595 => (10.0, 41.0, "U/L"),    // ALTL
            _ => (1.0, 10.0, "")
        };

        var value = low + _random.NextDouble() * (high - low);
        return (Math.Round((decimal)value, 2), unit);
    }
}

/// <summary>
/// The emulator on a TCP port, for commissioning the link with a serial-to-Ethernet adapter's
/// address replaced by 127.0.0.1, or for a demonstration without the analyser.
/// </summary>
public sealed class IntegraAnalyserSimulator(IntegraInstrumentEmulator emulator, int port = 0,
                                             ILogger? logger = null) : IAsyncDisposable
{
    private TcpListener? _listener;

    public IntegraInstrumentEmulator Emulator => emulator;

    /// <summary>The port listened on, once started (useful with port 0).</summary>
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        logger?.LogInformation("INTEGRA simulator listening on 127.0.0.1:{Port}.", Port);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_listener is null) Start();

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            // One host at a time, as on the analyser's single serial port.
            await ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            var reader = new RocheIntegra.Reader(Encoding.Latin1);
            var buffer = new byte[4096];

            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    return;
                }

                if (read == 0) return;
                reader.Feed(buffer.AsSpan(0, read));

                while (reader.TryRead() is { } block)
                {
                    var answer = emulator.Respond(block);
                    if (answer is null) continue;

                    try
                    {
                        await stream.WriteAsync(Encoding.Latin1.GetBytes(answer), ct);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                        return;
                    }
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener = null;
        return ValueTask.CompletedTask;
    }
}
