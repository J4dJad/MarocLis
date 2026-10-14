using System.Text;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Transport;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Drivers;

/// <summary>
/// CLSI POCT1-A driver: bedside and near-patient devices (glucose meters, blood gas, HbA1c, INR)
/// or the docking station that collects them.
/// <para>
/// The conversation is led by the device and answered by the LIS. Every message is acknowledged,
/// and an observation the LIS could not store is acknowledged <c>AE</c>, never <c>AA</c>: a device
/// that receives <c>AA</c> marks its results as uploaded and may never send them again.
/// </para>
/// <list type="bullet">
/// <item><c>HEL</c> (hello) — acknowledged.</item>
/// <item><c>DST</c> (status) — acknowledged; when it reports new observations, the LIS asks for
/// them with <c>REQ ROBS</c>.</item>
/// <item><c>OBS</c> (observations) — handed to the sink, then acknowledged with the outcome.</item>
/// <item><c>EOT</c> (end of topic) — acknowledged, then the LIS closes with <c>END</c>.</item>
/// </list>
/// <para>
/// Results only: POCT1-A has no worklist query, and patient lists (<c>OPL</c>) are a later step.
/// </para>
/// </summary>
public sealed class Poct1AInstrumentDriver : IInstrumentDriver
{
    private readonly InstrumentInterface _config;
    private readonly IInstrumentTransport _transport;
    private readonly IInstrumentMessageSink _sink;
    private readonly ILogger? _logger;
    private readonly Encoding _encoding;
    private readonly Poct1A.Reader _reader;
    private readonly Func<DateTimeOffset> _now;

    private int _controlId;

    public Poct1AInstrumentDriver(InstrumentInterface config, IInstrumentTransport transport,
                                  IInstrumentMessageSink sink, ILogger? logger = null,
                                  Func<DateTimeOffset>? now = null)
    {
        _config = config;
        _transport = transport;
        _sink = sink;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.Now);

        // POCT1-A documents are UTF-8 unless they say otherwise; ISO-8859-1, the form's default,
        // is an ASTM habit.
        _encoding = string.Equals(config.Encoding, "ISO-8859-1", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8
            : FileDropInstrumentTransport.ResolveEncoding(config.Encoding);

        _reader = new Poct1A.Reader(_encoding);

        _transport.Disconnected += (_, reason) =>
        {
            _logger?.LogWarning("POCT1-A link {Interface} dropped: {Reason}", config.Id, reason);
            _reader.Reset();
        };
    }

    public Guid InterfaceId => _config.Id;
    public bool IsConnected => _transport.IsConnected;
    public string Description => $"POCT1-A — {_transport.Description}";

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

            await FeedAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>Feeds received bytes and answers every complete message in them. Public for tests.</summary>
    public async Task FeedAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        _reader.Feed(bytes.Span);

        while (_reader.TryReadMessage() is { } xml)
            await HandleMessageAsync(xml, ct);
    }

    private async Task HandleMessageAsync(string xml, CancellationToken ct)
    {
        var header = Poct1A.ReadHeader(xml);

        if (header is null)
        {
            await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, xml, ct);
            _logger?.LogWarning("POCT1-A link {Interface}: unreadable XML discarded.", _config.Id);
            return;
        }

        var type = header.Type.ToUpperInvariant();

        if (type.StartsWith("ACK.", StringComparison.Ordinal)
            || type.StartsWith("END.", StringComparison.Ordinal))
        {
            await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, xml, ct);
            return;
        }

        if (type.StartsWith("OBS.", StringComparison.Ordinal))
        {
            // Recorded by the sink itself, with its outcome; logging it here too would list every
            // observation twice.
            var message = Poct1AResultParser.Parse(xml, _config.MessageFormat);
            var accepted = await _sink.HandleAsync(_config.Id, message, ct);

            await SendAsync(Poct1A.BuildAck(NextControlId(), header.ControlId, accepted,
                accepted ? null : "Observations could not be stored; retain and resend", _now()), ct);
            return;
        }

        await _sink.LogRawAsync(_config.Id, MessageDirection.Inbound, xml, ct);

        var known = type.StartsWith("HEL.", StringComparison.Ordinal)
                    || type.StartsWith("DST.", StringComparison.Ordinal)
                    || type.StartsWith("EOT.", StringComparison.Ordinal)
                    || type.StartsWith("DTV.", StringComparison.Ordinal)
                    || type.StartsWith("EVS.", StringComparison.Ordinal)
                    || type.StartsWith("KPA.", StringComparison.Ordinal);

        await SendAsync(Poct1A.BuildAck(NextControlId(), header.ControlId, known,
            known ? null : $"Message {header.Type} not supported", _now()), ct);

        if (type.StartsWith("DST.", StringComparison.Ordinal) && header.NewObservations > 0)
            await SendAsync(Poct1A.BuildRequest(NextControlId(), "ROBS", _now()), ct);

        if (type.StartsWith("EOT.", StringComparison.Ordinal))
            await SendAsync(Poct1A.BuildEnd(NextControlId(), _now()), ct);
    }

    private string NextControlId() => Interlocked.Increment(ref _controlId).ToString();

    private async Task SendAsync(string xml, CancellationToken ct)
    {
        await _transport.WriteAsync(_encoding.GetBytes(xml), ct);
        await _sink.LogRawAsync(_config.Id, MessageDirection.Outbound, xml, ct);
    }

    /// <summary>POCT1-A has no order download in this build: results flow one way.</summary>
    public Task<bool> SendOrdersAsync(IReadOnlyList<InstrumentOrderDownload> orders,
                                      CancellationToken ct = default) => Task.FromResult(false);

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();
}
