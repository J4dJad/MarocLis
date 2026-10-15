using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MarocLIS.Instruments.Protocol;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Simulator;

public sealed class SimulatorOptions
{
    /// <summary>TCP port the simulated analyser listens on.</summary>
    public int Port { get; set; } = 5150;

    /// <summary>Instrument identifier sent in the header record.</summary>
    public string InstrumentId { get; set; } = "SIMU-BIOCHEM";

    /// <summary>
    /// Whether the simulator asks the host what to run on a scanned tube before reporting, which
    /// is what exercises the bidirectional path.
    /// </summary>
    public bool QueryHostBeforeReporting { get; set; } = true;

    /// <summary>Seconds between the simulated scan of one tube and the next.</summary>
    public int SecondsBetweenSpecimens { get; set; } = 10;

    /// <summary>
    /// Proportion of frames deliberately sent with a bad checksum, 0 to 1. The point is to prove
    /// the LIS NAKs and the retry works — an integration that has never seen a corrupted frame has
    /// not been tested against the failure that actually happens on a long RS-232 run.
    /// </summary>
    public double CorruptFrameRate { get; set; }

    /// <summary>Proportion of specimens reported as quality-control material rather than patient.</summary>
    public double ControlSpecimenRate { get; set; } = 0.1;

    /// <summary>Emit values outside the reference interval, to exercise flagging and validation.</summary>
    public bool ProduceAbnormalValues { get; set; } = true;
}

/// <summary>
/// A simulated ASTM analyser.
/// <para>
/// Exists because the alternative is testing the interface on the laboratory's live instrument
/// during working hours. It speaks the real E1381 handshake — bids for the line with ENQ, waits for
/// ACK, frames with correct checksums, releases with EOT — so a driver that works against it works
/// against hardware, and it can be told to corrupt frames and send control specimens so the
/// failure paths get exercised too.
/// </para>
/// </summary>
public sealed class AstmAnalyserSimulator(SimulatorOptions options, ILogger? logger = null)
    : IAsyncDisposable
{
    private readonly Encoding _encoding = Encoding.Latin1;
    private readonly Random _random = new();
    private TcpListener? _listener;

    /// <summary>Barcodes the simulator will report on. Queue tubes here to drive a test.</summary>
    public Queue<string> PendingSpecimens { get; } = new();

    /// <summary>Worklists the host sent back, so a test can assert what was downloaded.</summary>
    public List<string> ReceivedOrderRecords { get; } = [];

    public async Task RunAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Loopback, options.Port);
        _listener.Start();

        logger?.LogInformation("Analyser simulator listening on 127.0.0.1:{Port}.", options.Port);

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // One session per connection, sequentially: a real analyser has one host link.
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();

            logger?.LogInformation("Simulator: host connected.");

            while (!ct.IsCancellationRequested && client.Connected)
            {
                if (PendingSpecimens.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                var specimenId = PendingSpecimens.Dequeue();

                if (options.QueryHostBeforeReporting)
                {
                    var tests = await QueryHostAsync(stream, specimenId, ct);

                    if (tests.Count == 0)
                    {
                        // The host had no order for this tube. A real analyser parks it; the
                        // simulator logs it so the test can assert the situation was detected.
                        logger?.LogWarning("Simulator: host returned no worklist for {Specimen}.",
                                           specimenId);
                        continue;
                    }

                    await ReportResultsAsync(stream, specimenId, tests, ct);
                }
                else
                {
                    await ReportResultsAsync(stream, specimenId, DefaultPanel(), ct);
                }

                await Task.Delay(TimeSpan.FromSeconds(options.SecondsBetweenSpecimens), ct);
            }
        }
    }

    // ---- Host query (analyser → LIS) ------------------------------------------------------------

    private async Task<IReadOnlyList<string>> QueryHostAsync(NetworkStream stream, string specimenId,
                                                              CancellationToken ct)
    {
        var records = new List<string>
        {
            Header(),
            // Q|1|^barcode||ALL||||||||O
            $"Q|1|^{specimenId}||ALL||||||||O",
            "L|1|N"
        };

        if (!await TransmitAsync(stream, records, ct)) return [];

        // The host answers with its own ENQ-framed transmission carrying P and O records.
        var reply = await ReceiveAsync(stream, ct);
        ReceivedOrderRecords.AddRange(reply);

        var tests = new List<string>();

        foreach (var record in reply.Where(r => r.StartsWith('O')))
        {
            var fields = record.Split('|');
            if (fields.Length < 5) continue;

            // Field 5 is the universal test identifier, repeated with "\" per requested test.
            foreach (var repetition in fields[4].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var components = repetition.Split('^');
                var code = components.Length >= 4 && components[3].Length > 0
                    ? components[3]
                    : components.FirstOrDefault(c => c.Length > 0);

                if (!string.IsNullOrWhiteSpace(code)) tests.Add(code!);
            }
        }

        logger?.LogInformation("Simulator: host returned {Count} test(s) for {Specimen}.",
                               tests.Count, specimenId);
        return tests;
    }

    // ---- Result transmission --------------------------------------------------------------------

    private async Task ReportResultsAsync(NetworkStream stream, string specimenId,
                                           IReadOnlyList<string> testCodes, CancellationToken ct)
    {
        var isControl = _random.NextDouble() < options.ControlSpecimenRate;
        var now = DateTimeOffset.Now;

        var records = new List<string> { Header() };

        records.Add(isControl
            ? "P|1"
            : $"P|1||{specimenId}||SIMULE^PATIENT||19850315|{(_random.Next(2) == 0 ? "M" : "F")}");

        // Action code "Q" in field 12 marks the specimen as quality control, which is how the LIS
        // routes it to the QC module instead of into a patient file.
        var actionCode = isControl ? "Q" : "N";
        var descriptor = isControl ? "CONTROL-NORMAL" : string.Empty;

        records.Add($"O|1|{specimenId}||{string.Join("\\", testCodes.Select(c => $"^^^{c}"))}" +
                    $"|R|{Timestamp(now)}|{Timestamp(now.AddMinutes(-30))}||||{actionCode}|||" +
                    $"{Timestamp(now.AddMinutes(-20))}|{descriptor}");

        var sequence = 1;

        foreach (var code in testCodes)
        {
            var (value, unit, range, flag) = Measure(code);

            records.Add($"R|{sequence}|^^^{code}|{value}|{unit}|{range}|{flag}||F||" +
                        $"SIMU|{Timestamp(now.AddMinutes(-2))}|{Timestamp(now)}|{options.InstrumentId}");

            if (flag.Length > 0)
                records.Add($"C|1|I|Valeur hors intervalle de référence — automate simulé|I");

            sequence++;
        }

        records.Add("L|1|N");

        await TransmitAsync(stream, records, ct);

        logger?.LogInformation("Simulator: reported {Count} result(s) for {Specimen}{Control}.",
                               testCodes.Count, specimenId, isControl ? " (control)" : string.Empty);
    }

    /// <summary>
    /// Produces a plausible value for a test code. Ranges are the ones the starter catalogue seeds,
    /// so a simulated run lands inside the reference intervals the LIS actually holds — otherwise
    /// every simulated result would flag and the flagging itself could not be tested.
    /// </summary>
    private (string Value, string Unit, string Range, string Flag) Measure(string code)
    {
        var (low, high, unit, decimals) = code.ToUpperInvariant() switch
        {
            "GLY" => (0.70, 1.10, "g/L", 2),
            "CREAT" => (7.0, 13.0, "mg/L", 1),
            "UREE" => (0.15, 0.45, "g/L", 2),
            "CHOL" => (1.20, 2.00, "g/L", 2),
            "HDL" => (0.40, 0.90, "g/L", 2),
            "TG" => (0.40, 1.50, "g/L", 2),
            "ASAT" => (10.0, 40.0, "UI/L", 0),
            "ALAT" => (10.0, 41.0, "UI/L", 0),
            "NA" => (136.0, 145.0, "mEq/L", 0),
            "K" => (3.5, 5.1, "mEq/L", 1),
            "CL" => (98.0, 107.0, "mEq/L", 0),
            "CRP" => (0.5, 5.0, "mg/L", 1),
            "HB" => (13.0, 17.0, "g/dL", 1),
            "GB" => (4.0, 10.0, "10*9/L", 2),
            "PLQ" => (150.0, 400.0, "10*9/L", 0),
            "HBA1C" => (4.5, 5.6, "%", 1),
            "TSH" => (0.27, 4.20, "mUI/L", 3),
            _ => (1.0, 10.0, "", 2)
        };

        var abnormal = options.ProduceAbnormalValues && _random.NextDouble() < 0.25;

        double value;
        string flag;

        if (!abnormal)
        {
            value = low + _random.NextDouble() * (high - low);
            flag = string.Empty;
        }
        else if (_random.Next(2) == 0)
        {
            value = low * (0.35 + _random.NextDouble() * 0.5);
            flag = "L";
        }
        else
        {
            value = high * (1.15 + _random.NextDouble() * 1.4);
            flag = "H";
        }

        var format = "F" + decimals.ToString(CultureInfo.InvariantCulture);

        return (value.ToString(format, CultureInfo.InvariantCulture),
                unit,
                $"{low.ToString(format, CultureInfo.InvariantCulture)} to " +
                $"{high.ToString(format, CultureInfo.InvariantCulture)}",
                flag);
    }

    private static IReadOnlyList<string> DefaultPanel() =>
        ["GLY", "CREAT", "UREE", "NA", "K", "CL", "ASAT", "ALAT"];

    // ---- Link layer -----------------------------------------------------------------------------

    /// <summary>
    /// Runs the full E1381 send sequence: ENQ, wait for ACK, frames each acknowledged, EOT.
    /// Deliberately corrupts a configurable share of frames so the LIS's NAK-and-resend path gets
    /// exercised rather than merely existing.
    /// </summary>
    private async Task<bool> TransmitAsync(NetworkStream stream, IReadOnlyList<string> records,
                                            CancellationToken ct)
    {
        await stream.WriteAsync(new[] { AstmControl.Enq }, ct);

        if (await ReadControlAsync(stream, ct) != AstmControl.Ack)
        {
            logger?.LogWarning("Simulator: host did not grant the line.");
            return false;
        }

        var frames = AstmControl.BuildFrames(records, _encoding);

        foreach (var frame in frames)
        {
            var toSend = frame;

            if (_random.NextDouble() < options.CorruptFrameRate)
            {
                // Flip a checksum digit. The frame is otherwise valid, so the LIS must detect it
                // by checksum alone — exactly the failure a noisy serial line produces.
                toSend = frame.ToArray();
                var checksumIndex = toSend.Length - 3;
                toSend[checksumIndex] = toSend[checksumIndex] == (byte)'0' ? (byte)'1' : (byte)'0';
                logger?.LogInformation("Simulator: sending a deliberately corrupted frame.");
            }

            var attempts = 0;

            while (attempts < 4)
            {
                await stream.WriteAsync(toSend, ct);
                var response = await ReadControlAsync(stream, ct);

                if (response == AstmControl.Ack) break;

                if (response == AstmControl.Nak)
                {
                    // A real analyser resends the frame as originally built, uncorrupted.
                    toSend = frame;
                    attempts++;
                    continue;
                }

                logger?.LogWarning("Simulator: no acknowledgement; abandoning the transmission.");
                await stream.WriteAsync(new[] { AstmControl.Eot }, ct);
                return false;
            }

            if (attempts >= 4)
            {
                await stream.WriteAsync(new[] { AstmControl.Eot }, ct);
                return false;
            }
        }

        await stream.WriteAsync(new[] { AstmControl.Eot }, ct);
        return true;
    }

    /// <summary>Receives one complete host transmission, acknowledging each frame.</summary>
    private async Task<IReadOnlyList<string>> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        var reader = new AstmFrameReader(_encoding);
        var assembler = new AstmMessageAssembler();
        var records = new List<string>();
        var buffer = new byte[4096];
        var deadline = DateTimeOffset.Now.AddSeconds(30);

        while (DateTimeOffset.Now < deadline && !ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            reader.Feed(buffer.AsSpan(0, read));

            var finished = false;

            while (reader.TryDequeueControl() is { } control)
            {
                if (control == AstmControl.Enq) await stream.WriteAsync(new[] { AstmControl.Ack }, ct);
                else if (control == AstmControl.Eot) finished = true;
            }

            while (reader.TryReadFrame() is { } frame)
            {
                var disposition = assembler.Accept(frame, out var message);
                if (disposition is AstmFrameDisposition.Accepted or AstmFrameDisposition.Duplicate)
                {
                    records.AddRange(message);
                    await stream.WriteAsync(new[] { AstmControl.Ack }, ct);
                }
                else
                {
                    await stream.WriteAsync(new[] { AstmControl.Nak }, ct);
                }
            }

            if (finished) break;
        }

        return records;
    }

    private static async Task<byte> ReadControlAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            return read == 0 ? (byte)0 : buffer[0];
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private string Header() =>
        $"H|\\^&|||{options.InstrumentId}^1.0|||||||P|1|{Timestamp(DateTimeOffset.Now)}";

    private static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener = null;
        await Task.CompletedTask;
    }
}
