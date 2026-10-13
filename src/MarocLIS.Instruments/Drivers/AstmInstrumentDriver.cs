using System.Text;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Transport;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Drivers;

/// <summary>
/// Drives one analyser link: owns the protocol handshake, turns bytes into
/// <see cref="InstrumentMessage"/> and hands them to the sink.
/// </summary>
public interface IInstrumentDriver : IAsyncDisposable
{
    Guid InterfaceId { get; }

    bool IsConnected { get; }

    string Description { get; }

    /// <summary>Runs until the token is cancelled, reconnecting on its own when the link drops.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Sends a worklist down to the analyser, for a bidirectional interface.</summary>
    Task<bool> SendOrdersAsync(IReadOnlyList<InstrumentOrderDownload> orders, CancellationToken ct = default);
}

/// <summary>
/// Where a driver delivers what it received. Implemented in the application layer, so the driver
/// knows nothing about the database or the catalogue.
/// </summary>
public interface IInstrumentMessageSink
{
    /// <summary>Handles an inbound message. Returns false if it could not be accepted at all.</summary>
    Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default);

    /// <summary>
    /// Answers an analyser's query for a scanned tube. Returns the worklist to send back, or an
    /// empty list when the specimen is unknown — which is itself worth logging, because it usually
    /// means the tube was drawn without an order.
    /// </summary>
    Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
        Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default);

    /// <summary>Records a raw frame verbatim, whether or not it could be parsed.</summary>
    Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                      CancellationToken ct = default);

    Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                     CancellationToken ct = default);
}

/// <summary>
/// ASTM E1381 / E1394 driver.
/// <para>
/// Implements the link-layer handshake as a state machine over a byte stream: the analyser bids for
/// the line with ENQ, the LIS grants it with ACK, frames are acknowledged individually, and EOT
/// releases it. Every frame's checksum is verified and a bad one is NAKed so the analyser resends —
/// which is the entire reason the checksum exists, and the step most commonly skipped by
/// integrations that then report corrupted values.
/// </para>
/// <para>
/// Contention is resolved in the analyser's favour. If both sides want the line, the LIS backs off:
/// the analyser has results in volatile memory and the LIS has a worklist it can send a second
/// later, so yielding costs nothing and losing results costs a redraw.
/// </para>
/// </summary>
public sealed class AstmInstrumentDriver : IInstrumentDriver
{
    private readonly InstrumentInterface _config;
    private readonly IInstrumentTransport _transport;
    private readonly IInstrumentMessageSink _sink;
    private readonly ILogger? _logger;
    private readonly Encoding _encoding;
    private readonly AstmFrameReader _reader;

    private readonly List<string> _sessionRecords = [];
    private bool _analyserHoldsLine;
    private DateTimeOffset _lineGrantedAt;

    /// <summary>Queue of worklists to push once the line is free.</summary>
    private readonly Queue<IReadOnlyList<InstrumentOrderDownload>> _outbound = new();

    public AstmInstrumentDriver(InstrumentInterface config, IInstrumentTransport transport,
                                IInstrumentMessageSink sink, ILogger? logger = null)
    {
        _config = config;
        _transport = transport;
        _sink = sink;
        _logger = logger;
        _encoding = FileDropInstrumentTransport.ResolveEncoding(config.Encoding);
        _reader = new AstmFrameReader(_encoding);

        _transport.Disconnected += OnTransportDisconnected;
    }

    public Guid InterfaceId => _config.Id;
    public bool IsConnected => _transport.IsConnected;
    public string Description => $"ASTM — {_transport.Description}";

    private void OnTransportDisconnected(object? sender, string reason)
    {
        _logger?.LogWarning("Instrument link {Interface} dropped: {Reason}", _config.Id, reason);
        _analyserHoldsLine = false;
        _sessionRecords.Clear();
        _reader.Reset();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
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
                    // Capped exponential backoff: an analyser switched off overnight should not
                    // produce thousands of log lines by morning.
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                    continue;
                }

                backoff = TimeSpan.FromSeconds(2);
            }

            var read = await _transport.ReadAsync(buffer, ct);

            if (read == 0)
            {
                // The line has gone quiet. If the analyser is still nominally holding it, the
                // session was abandoned mid-transfer — release it rather than deadlocking.
                if (_analyserHoldsLine &&
                    DateTimeOffset.Now - _lineGrantedAt >
                        TimeSpan.FromSeconds(_config.AcknowledgementTimeoutSeconds * 4))
                {
                    _logger?.LogWarning("Abandoned ASTM session on {Interface}; releasing the line.",
                                        _config.Id);
                    await FinishSessionAsync(ct);
                }

                await TrySendQueuedOrdersAsync(ct);
                continue;
            }

            _reader.Feed(buffer.AsSpan(0, read));
            await ProcessAsync(ct);
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        // Control characters first: they carry the line state and must be honoured in order.
        while (_reader.TryDequeueControl() is { } control)
        {
            switch (control)
            {
                case AstmControl.Enq:
                    _analyserHoldsLine = true;
                    _lineGrantedAt = DateTimeOffset.Now;
                    _sessionRecords.Clear();
                    await _transport.WriteAsync(new[] { AstmControl.Ack }, ct);
                    break;

                case AstmControl.Eot:
                    await FinishSessionAsync(ct);
                    break;

                case AstmControl.Ack:
                case AstmControl.Nak:
                    // Acknowledgements to our own transmission are consumed by the send path.
                    break;
            }
        }

        while (_reader.TryReadFrame() is { } frame)
        {
            if (!frame.ChecksumValid)
            {
                // NAK asks the analyser to resend. Accepting a bad frame would mean storing a
                // result whose digits may have been corrupted in transit.
                _logger?.LogWarning("Bad ASTM checksum on {Interface}; requesting a resend.", _config.Id);
                await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound,
                                        $"[CHECKSUM FAILED] {frame.Text}", ct);
                await _transport.WriteAsync(new[] { AstmControl.Nak }, ct);
                continue;
            }

            _sessionRecords.AddRange(frame.Records);
            await _transport.WriteAsync(new[] { AstmControl.Ack }, ct);

            // A terminator record closes the message even if EOT is slow to arrive; some analysers
            // send the two far enough apart that waiting would delay the results noticeably.
            if (frame.IsLast && frame.Records.Any(r => r.StartsWith('L')))
                await FinishSessionAsync(ct);
        }
    }

    private async Task FinishSessionAsync(CancellationToken ct)
    {
        _analyserHoldsLine = false;

        if (_sessionRecords.Count == 0)
        {
            await TrySendQueuedOrdersAsync(ct);
            return;
        }

        var raw = string.Join("\r", _sessionRecords);
        await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, raw, ct);

        var message = AstmMessageParser.Parse(_sessionRecords);
        _sessionRecords.Clear();

        if (message.Kind == InstrumentMessageKind.Query
            && _config.Direction != InstrumentDirection.Unidirectional)
        {
            var orders = await _sink.ResolveQueryAsync(_config.Id, message.QueriedSpecimenIds, ct);
            await SendOrdersAsync(orders, ct);
            return;
        }

        await _sink.HandleAsync(_config.Id, message, ct);
        await TrySendQueuedOrdersAsync(ct);
    }

    public async Task<bool> SendOrdersAsync(IReadOnlyList<InstrumentOrderDownload> orders,
                                             CancellationToken ct = default)
    {
        if (orders.Count == 0) return true;

        if (!_transport.IsConnected)
        {
            _outbound.Enqueue(orders);
            return false;
        }

        // Never interrupt an inbound transfer. Queue and send when the line is free.
        if (_analyserHoldsLine)
        {
            _outbound.Enqueue(orders);
            return false;
        }

        var records = AstmMessageParser.BuildOrderDownload(
            orders, senderId: "MAROCLIS", DateTimeOffset.Now);

        var frames = AstmControl.BuildFrames(records, _encoding);

        // Bid for the line.
        await _transport.WriteAsync(new[] { AstmControl.Enq }, ct);

        if (!await AwaitAckAsync(ct))
        {
            _logger?.LogWarning("Analyser {Interface} refused the line; worklist re-queued.", _config.Id);
            _outbound.Enqueue(orders);
            return false;
        }

        foreach (var frame in frames)
        {
            var attempts = 0;
            var acknowledged = false;

            while (attempts <= _config.MaxRetries && !acknowledged)
            {
                await _transport.WriteAsync(frame, ct);
                acknowledged = await AwaitAckAsync(ct);
                attempts++;
            }

            if (!acknowledged)
            {
                _logger?.LogError("Frame not acknowledged after {Attempts} attempts on {Interface}.",
                                  attempts, _config.Id);
                await _transport.WriteAsync(new[] { AstmControl.Eot }, ct);
                return false;
            }
        }

        await _transport.WriteAsync(new[] { AstmControl.Eot }, ct);

        await _sink.LogRawAsync(_config.Id, MessageDirection.Outbound,
                                string.Join("\r", records), ct);
        return true;
    }

    /// <summary>
    /// Sends one queued worklist when the line is free.
    /// <para>
    /// One per call, not the whole queue: the analyser may bid for the line between transmissions,
    /// and draining a long queue would keep it waiting with results in volatile memory.
    /// <see cref="SendOrdersAsync"/> re-queues on failure, so a worklist that cannot get through
    /// goes to the back and is retried rather than lost.
    /// </para>
    /// </summary>
    private async Task TrySendQueuedOrdersAsync(CancellationToken ct)
    {
        if (_outbound.Count == 0 || _analyserHoldsLine || !_transport.IsConnected) return;

        await SendOrdersAsync(_outbound.Dequeue(), ct);
    }

    /// <summary>
    /// Waits for an ACK, treating NAK and timeout as failure. The timeout is the configured
    /// acknowledgement window, because analyser response times vary by an order of magnitude
    /// between vendors and a fixed value would either be too slow or spuriously fail.
    /// </summary>
    private async Task<bool> AwaitAckAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(_config.AcknowledgementTimeoutSeconds);
        var buffer = new byte[64];

        while (DateTimeOffset.Now < deadline && !ct.IsCancellationRequested)
        {
            while (_reader.TryDequeueControl() is { } control)
            {
                if (control == AstmControl.Ack) return true;
                if (control is AstmControl.Nak or AstmControl.Eot) return false;
            }

            var read = await _transport.ReadAsync(buffer, ct);
            if (read > 0) _reader.Feed(buffer.AsSpan(0, read));
            else await Task.Delay(50, ct);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _transport.Disconnected -= OnTransportDisconnected;
        await _transport.DisposeAsync();
    }
}

/// <summary>
/// HL7 v2.x driver over MLLP.
/// <para>
/// Every inbound message is acknowledged, and the acknowledgement tells the truth: an application
/// error produces <c>AE</c>, not <c>AA</c>. That distinction matters more than it looks — an
/// analyser that receives <c>AA</c> considers its results safely delivered and is free to discard
/// them, so acknowledging a message the LIS could not store is how results disappear silently.
/// </para>
/// </summary>
public sealed class Hl7InstrumentDriver : IInstrumentDriver
{
    private const string SendingApplication = "MAROCLIS";

    private readonly InstrumentInterface _config;
    private readonly IInstrumentTransport _transport;
    private readonly IInstrumentMessageSink _sink;
    private readonly ILogger? _logger;
    private readonly Encoding _encoding;
    private readonly Mllp.Reader _reader;

    public Hl7InstrumentDriver(InstrumentInterface config, IInstrumentTransport transport,
                               IInstrumentMessageSink sink, ILogger? logger = null)
    {
        _config = config;
        _transport = transport;
        _sink = sink;
        _logger = logger;
        _encoding = FileDropInstrumentTransport.ResolveEncoding(config.Encoding);
        _reader = new Mllp.Reader(_encoding);

        _transport.Disconnected += (_, reason) =>
        {
            _logger?.LogWarning("HL7 link {Interface} dropped: {Reason}", config.Id, reason);
            _reader.Reset();
        };
    }

    public Guid InterfaceId => _config.Id;
    public bool IsConnected => _transport.IsConnected;
    public string Description => $"HL7 v2 — {_transport.Description}";

    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
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
            }

            var read = await _transport.ReadAsync(buffer, ct);
            if (read == 0) continue;

            _reader.Feed(buffer.AsSpan(0, read));

            while (_reader.TryReadMessage() is { } text)
                await HandleMessageAsync(text, ct);
        }
    }

    private async Task HandleMessageAsync(string text, CancellationToken ct)
    {
        await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, text, ct);

        var parsed = Hl7Message.Parse(text);
        var message = Hl7ResultParser.Parse(text);

        if (message.Kind == InstrumentMessageKind.Acknowledgement) return;

        if (message.Kind == InstrumentMessageKind.Query
            && _config.Direction != InstrumentDirection.Unidirectional)
        {
            var orders = await _sink.ResolveQueryAsync(_config.Id, message.QueriedSpecimenIds, ct);
            await SendOrdersAsync(orders, ct);
            await SendAckAsync(parsed, null, ct);
            return;
        }

        var accepted = await _sink.HandleAsync(_config.Id, message, ct);

        // Tell the analyser the truth about whether we took the results.
        await SendAckAsync(parsed,
            accepted ? null : "Results could not be stored; retain and resend", ct);
    }

    private async Task SendAckAsync(Hl7Message inbound, string? errorText, CancellationToken ct)
    {
        var ack = inbound.BuildAck(SendingApplication, DateTimeOffset.Now, errorText);
        await _transport.WriteAsync(Mllp.Wrap(ack, _encoding), ct);
        await _sink.LogRawAsync(_config.Id, MessageDirection.Outbound, ack, ct);
    }

    public async Task<bool> SendOrdersAsync(IReadOnlyList<InstrumentOrderDownload> orders,
                                             CancellationToken ct = default)
    {
        if (orders.Count == 0 || !_transport.IsConnected) return false;

        foreach (var order in orders)
        {
            var message = Hl7ResultParser.BuildOrderMessage(
                order, SendingApplication,
                receivingApplication: _config.InstrumentId.ToString("N")[..8],
                DateTimeOffset.Now);

            await _transport.WriteAsync(Mllp.Wrap(message, _encoding), ct);
            await _sink.LogRawAsync(_config.Id, MessageDirection.Outbound, message, ct);
        }

        return true;
    }

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();
}
