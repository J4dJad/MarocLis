using System.Collections.Concurrent;
using System.Text;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Transport;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Drivers;

/// <summary>
/// The COBAS INTEGRA host-interface settings the analyser's own configuration decides and the
/// manual leaves open. Read from <c>Instruments:RocheIntegra</c> in appsettings, so a link can be
/// made to match the analyser without a change to the database. Every default is the manual's
/// recommendation or its example; each one is to be confirmed against the analyser's
/// "Configuration &gt; General &gt; Interface" screen and a trace.
/// </summary>
public sealed class IntegraHostOptions
{
    public const string SectionName = "Instruments:RocheIntegra";

    /// <summary>Instrument code in the host's own headers. "14" is the INTEGRA 400 plus's.</summary>
    public string InstrumentCode { get; set; } = RocheIntegra.Integra400PlusCode;

    /// <summary>The 16-character identifier in the host's headers. The analyser does not read it.</summary>
    public string Identifier { get; set; } = "MAROCLIS";

    /// <summary>
    /// Block check on the host's messages ("Block check: True" on the analyser). The manual
    /// strongly recommends it; the analyser's answers are read with or without one.
    /// </summary>
    public bool BlockCheck { get; set; } = true;

    /// <summary>"LF" or "CRLF", as the analyser's "Line terminator". Read either way.</summary>
    public string LineTerminator { get; set; } = "LF";

    /// <summary>
    /// Result type selector of the result request (line 10): "01" any accepted result — patients,
    /// and controls and calibrations when the analyser is set to send them — or "07" patients only.
    /// The manual advises against the collated selectors 03 to 06.
    /// </summary>
    public string ResultSelector { get; set; } = "01";

    /// <summary>
    /// Seconds between two rounds of requests once the analyser has nothing more to send. The
    /// manual asks for long intervals, 60 s in its example: every request reads its database.
    /// </summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>The most results fetched in one round, so a backlog cannot starve the order download.</summary>
    public int MaxResultsPerRound { get; set; } = 200;

    /// <summary>
    /// Priority of a routine order in line 54. The manual lists "A" and "S" (STAT); a STAT order is
    /// always "S".
    /// </summary>
    public string RoutinePriority { get; set; } = "A";

    /// <summary>Integers zero-padded ("004") instead of the space-padded form of the manual's examples.</summary>
    public bool ZeroPadIntegers { get; set; }

    /// <summary>Minutes before a tube the LIS found no order for is asked about again.</summary>
    public int UnknownTubeRetryMinutes { get; set; } = 10;

    /// <summary>The sequence counter of the first message after synchronisation (0 or 1).</summary>
    public int FirstSequenceCounter { get; set; } = 1;

    internal string LineEnd => string.Equals(LineTerminator?.Trim(), "CRLF", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(LineTerminator?.Trim(), "CR+LF", StringComparison.OrdinalIgnoreCase)
        ? "\r\n"
        : "\n";
}

/// <summary>
/// Drives a COBAS INTEGRA 400 plus over its RS-232 host interface.
/// <para>
/// The opposite of an ASTM link: the LIS is the master. The analyser never speaks first — it keeps
/// its results until asked, and answers every request with one block. So the driver polls, in
/// rounds:
/// </para>
/// <list type="number">
/// <item>Results: block 09 (result request) again and again, each answer one result block, until
/// the analyser answers block 00 (nothing more). Each result block goes to the sink.</item>
/// <item>Tubes without orders, for a link that downloads orders: block 60 service 1, answered by
/// block 62 (the barcodes) or 69 (none). Each barcode the LIS knows gets its order, block 10,
/// answered by block 19.</item>
/// <item>Worklists pushed by the application, block 10 again.</item>
/// </list>
/// <para>
/// Every request carries the sequence counter, toggled for each new request and kept for a
/// retransmission: a timeout, a bad sum on the answer, or the analyser's block 99 for a
/// transmission error make the driver send the same request with the same counter, which is how
/// the manual asks for the previous answer again. When retries run out the link is synchronised
/// (block 00 without a check) and the round ends.
/// </para>
/// </summary>
public sealed class RocheIntegraInstrumentDriver : IInstrumentDriver
{
    private readonly InstrumentInterface _config;
    private readonly IInstrumentTransport _transport;
    private readonly IInstrumentMessageSink _sink;
    private readonly ILogger? _logger;
    private readonly IntegraHostOptions _options;
    private readonly Encoding _encoding;
    private readonly RocheIntegra.Reader _reader;
    private readonly Func<DateTimeOffset> _now;

    private readonly ConcurrentQueue<IReadOnlyList<InstrumentOrderDownload>> _outbound = new();
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Barcodes the LIS had no order for, with when they were asked about.</summary>
    private readonly Dictionary<string, DateTimeOffset> _unknownTubes = new(StringComparer.Ordinal);

    private int _sequence;
    private bool _synchronised;

    /// <summary>
    /// Whether the analyser answers with the request's counter (true) or the other one, learnt from
    /// its first answer after a synchronisation. The manual says the two must "be updated
    /// simultaneously" without saying which value an answer carries; learning it keeps the driver
    /// right either way and still detects a repeated answer.
    /// </summary>
    private bool? _answerEchoesCounter;

    private string? _lastResultBlock;

    public RocheIntegraInstrumentDriver(InstrumentInterface config, IInstrumentTransport transport,
                                        IInstrumentMessageSink sink, ILogger? logger = null,
                                        IntegraHostOptions? options = null,
                                        Func<DateTimeOffset>? now = null)
    {
        _config = config;
        _transport = transport;
        _sink = sink;
        _logger = logger;
        _options = options ?? new IntegraHostOptions();
        _now = now ?? (() => DateTimeOffset.Now);

        // The host interface is ASCII: bytes past 127 are not allowed in a field. Latin-1 reads
        // whatever arrives without losing a byte, so the sum is computed on what was sent.
        _encoding = Encoding.Latin1;
        _reader = new RocheIntegra.Reader(_encoding);

        _transport.Disconnected += (_, reason) =>
        {
            _logger?.LogWarning("INTEGRA link {Interface} dropped: {Reason}", config.Id, reason);
            _reader.Reset();
            _synchronised = false;
        };
    }

    public Guid InterfaceId => _config.Id;
    public bool IsConnected => _transport.IsConnected;
    public string Description => $"Roche COBAS INTEGRA — {_transport.Description}";

    /// <summary>Requests sent, for tests and the trace: block code and lines.</summary>
    internal int RequestsSent { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            if (!_transport.IsConnected)
            {
                var connected = await _transport.ConnectAsync(ct);
                await _sink.ReportConnectionStateAsync(_config.Id, connected,
                    connected ? null : "Connection failed", ct);

                if (!connected)
                {
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                    continue;
                }

                backoff = TimeSpan.FromSeconds(2);
                _synchronised = false;
            }

            try
            {
                await RoundAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unexpected answer must not end the polling of an analyser holding results.
                _logger?.LogError(ex, "INTEGRA round failed on {Interface}.", _config.Id);
                _synchronised = false;
            }

            // Until the next round, unless the application pushes a worklist.
            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(_options.PollSeconds, 5, 3600)), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One polling round: results, then tubes without orders, then pushed worklists.</summary>
    public async Task RoundAsync(CancellationToken ct = default)
    {
        if (!_synchronised) await SynchroniseAsync(ct);

        await FetchResultsAsync(ct);

        if (_config.Direction != InstrumentDirection.Unidirectional)
            await DownloadForWaitingTubesAsync(ct);

        while (_outbound.TryDequeue(out var orders))
        {
            foreach (var order in orders) await SendOrderAsync(order, null, ct);
        }
    }

    private async Task FetchResultsAsync(CancellationToken ct)
    {
        for (var i = 0; i < Math.Max(1, _options.MaxResultsPerRound); i++)
        {
            var answer = await RequestAsync(RocheIntegra.BlockResultRequest,
                                            RocheIntegra.ResultRequest(_options.ResultSelector), ct);

            if (answer is null) return;

            switch (answer.BlockCode)
            {
                case RocheIntegra.BlockIdle:
                    return;

                case RocheIntegra.BlockPatient or RocheIntegra.BlockPatientWithLots
                    or RocheIntegra.BlockControl or RocheIntegra.BlockControlWithLots:
                    if (answer.Stored == _lastResultBlock && answer.SequenceCounter is not null
                        && !CounterMatches(answer))
                    {
                        // The analyser repeated its previous answer: already handed to the sink.
                        _logger?.LogInformation("INTEGRA {Interface}: repeated result block skipped.", _config.Id);
                        _synchronised = false;
                        return;
                    }

                    _lastResultBlock = answer.Stored;
                    // Recorded by the sink itself, with its outcome.
                    await _sink.HandleAsync(_config.Id, IntegraMessageParser.Parse(answer.Stored, _config.MessageFormat), ct);
                    break;

                case RocheIntegra.BlockCalibration or RocheIntegra.BlockCalibrationWithLots:
                    // Taken so the analyser moves on to the next result; the LIS keeps no calibration.
                    await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, answer.Stored, ct);
                    break;

                default:
                    await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, answer.Stored, ct);
                    return;
            }
        }
    }

    private async Task DownloadForWaitingTubesAsync(CancellationToken ct)
    {
        var answer = await RequestAsync(RocheIntegra.BlockServiceRequest, RocheIntegra.ServiceRequest(1), ct);

        // Block 69 with line 96: no tube waits. Not logged — it is the answer most minutes.
        if (answer is null || answer.BlockCode != RocheIntegra.BlockTubes) return;

        var now = _now();
        var retry = TimeSpan.FromMinutes(Math.Max(1, _options.UnknownTubeRetryMinutes));

        foreach (var stale in _unknownTubes.Where(t => now - t.Value > retry).Select(t => t.Key).ToList())
            _unknownTubes.Remove(stale);

        var tubes = RocheIntegra.Tubes(answer)
            .Where(t => t.Barcoded && !_unknownTubes.ContainsKey(t.Barcode))
            .ToList();

        if (tubes.Count == 0) return;

        await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, answer.Stored, ct);

        var orders = await _sink.ResolveQueryAsync(_config.Id, [.. tubes.Select(t => t.Barcode)], ct);

        foreach (var tube in tubes)
        {
            var order = orders.FirstOrDefault(o => string.Equals(o.SpecimenId, tube.Barcode, StringComparison.Ordinal));

            if (order is null)
            {
                // Asked again later, not every minute: the sink has logged it once already.
                _unknownTubes[tube.Barcode] = now;
                continue;
            }

            await SendOrderAsync(order, tube.SampleType, ct);
        }
    }

    private async Task SendOrderAsync(InstrumentOrderDownload order, string? sampleType, CancellationToken ct)
    {
        var blocks = RocheIntegra.OrderEntry(order, sampleType, _options.RoutinePriority,
                                             _options.ZeroPadIntegers, out var skipped);

        if (skipped.Count > 0)
            _logger?.LogWarning("INTEGRA {Interface}: codes {Codes} for {Specimen} are not test numbers; not sent.",
                                _config.Id, string.Join(", ", skipped), order.SpecimenId);

        foreach (var lines in blocks)
        {
            var text = string.Join('\r',
                new[] { RocheIntegra.Header(_options.InstrumentCode, _options.Identifier, RocheIntegra.BlockOrderEntry) }
                    .Concat(lines));
            await _sink.LogRawAsync(_config.Id, MessageDirection.Outbound, text, ct);

            var answer = await RequestAsync(RocheIntegra.BlockOrderEntry, lines, ct);
            if (answer is null) return;

            // Block 19, line 96 00: accepted. Anything else is recorded with its code.
            await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, answer.Stored, ct);

            if (answer.BlockCode != RocheIntegra.BlockOrderResponse || answer.ErrorCode != "00")
            {
                _logger?.LogWarning("INTEGRA {Interface}: order {Specimen} answered {Block}/{Code}.",
                                    _config.Id, order.SpecimenId, answer.BlockCode, answer.ErrorCode);
                return;
            }
        }
    }

    public Task<bool> SendOrdersAsync(IReadOnlyList<InstrumentOrderDownload> orders,
                                      CancellationToken ct = default)
    {
        if (orders.Count == 0) return Task.FromResult(true);

        // Sent by the polling loop, between two requests: the line is the loop's alone.
        _outbound.Enqueue(orders);
        _wake.Release();
        return Task.FromResult(_transport.IsConnected);
    }

    /// <summary>
    /// Block 00 without a block check, then counters back to their start. The manual makes
    /// synchronisation the host's first step and the way to skip a retransmission; whatever the
    /// analyser answers, or its silence, the counters restart.
    /// </summary>
    private async Task SynchroniseAsync(CancellationToken ct)
    {
        var sync = RocheIntegra.Build(_options.InstrumentCode, _options.Identifier, RocheIntegra.BlockIdle,
                                      [], sequenceCounter: null, _options.LineEnd);
        await WriteAsync(sync, ct);

        var answer = await ReadBlockAsync(ct);
        if (answer is null)
            _logger?.LogInformation("INTEGRA {Interface}: no answer to synchronisation.", _config.Id);

        _sequence = (_options.FirstSequenceCounter + 1) % 2;
        _answerEchoesCounter = null;
        _lastResultBlock = null;
        _synchronised = true;
    }

    /// <summary>
    /// Sends one request and returns the analyser's answer, retransmitting with the same counter on
    /// a timeout, a wrong sum or a transmission error. Null when the retries are spent; the link is
    /// then synchronised again before the next round.
    /// </summary>
    private async Task<RocheIntegra.Block?> RequestAsync(string blockCode, IReadOnlyList<string> lines,
                                                         CancellationToken ct)
    {
        _sequence = (_sequence + 1) % 2;

        var text = RocheIntegra.Build(_options.InstrumentCode, _options.Identifier, blockCode, lines,
                                      _options.BlockCheck ? _sequence : null, _options.LineEnd);

        for (var attempt = 0; attempt <= Math.Max(0, _config.MaxRetries); attempt++)
        {
            await WriteAsync(text, ct);
            RequestsSent++;

            var answer = await ReadBlockAsync(ct);

            if (answer is null)
            {
                _logger?.LogWarning("INTEGRA {Interface}: no answer to block {Block} (attempt {Attempt}).",
                                    _config.Id, blockCode, attempt + 1);
                continue;
            }

            if (!answer.ChecksumValid)
            {
                await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound,
                                        $"[CHECKSUM FAILED] {answer.Stored}", ct);
                continue;
            }

            if (answer.BlockCode == RocheIntegra.BlockError)
            {
                await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, answer.Stored, ct);

                // 01 block check sum, 02 parity: the request was damaged on the way, send it again.
                // Any other code says the request itself is wrong; sending it again cannot help.
                if (answer.ErrorCode is "01" or "02") continue;

                _synchronised = false;
                return answer;
            }

            if (answer.SequenceCounter is not null && _answerEchoesCounter is null)
                _answerEchoesCounter = answer.SequenceCounter == _sequence;

            return answer;
        }

        _synchronised = false;
        await _sink.ReportConnectionStateAsync(_config.Id, _transport.IsConnected,
                                               $"No answer to block {blockCode}", ct);
        return null;
    }

    private bool CounterMatches(RocheIntegra.Block answer) =>
        _answerEchoesCounter is not { } echoes
        || (answer.SequenceCounter == _sequence) == echoes;

    private async Task WriteAsync(string text, CancellationToken ct) =>
        await _transport.WriteAsync(_encoding.GetBytes(text), ct);

    /// <summary>The analyser's next block, within the configured answer window; null on silence.</summary>
    private async Task<RocheIntegra.Block?> ReadBlockAsync(CancellationToken ct)
    {
        var deadline = _now().AddSeconds(Math.Max(1, _config.AcknowledgementTimeoutSeconds));
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            if (_reader.TryRead() is { } text)
            {
                if (RocheIntegra.ParseFramed(text) is { } block) return block;
                continue;
            }

            var remaining = deadline - _now();
            if (remaining <= TimeSpan.Zero || !_transport.IsConnected) return null;

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(remaining);

            int read;
            try
            {
                read = await _transport.ReadAsync(buffer, window.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }

            if (read > 0) _reader.Feed(buffer.AsSpan(0, read));
            else await Task.Delay(20, ct);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _wake.Dispose();
        await _transport.DisposeAsync();
    }
}
