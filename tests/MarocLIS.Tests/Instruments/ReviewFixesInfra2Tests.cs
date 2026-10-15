using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Protocol;
using MarocLIS.Instruments.Transport;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// The ASTM driver's waits: acknowledgement windows enforced on a silent line, worklists sent when
/// the line is idle, and the analyser's bid honoured when both sides ask for the line at once.
/// </summary>
public sealed class ReviewFixesInfra2Tests
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly string[] AnalyserMessage =
        ["H|\\^&", "P|1", "O|1|TUBE-1||^^^GLU", "R|1|^^^GLU|5.6|mmol/L", "L|1|N"];

    private static InstrumentInterface Link(int ackSeconds = 1) =>
        new(Guid.NewGuid(), Guid.NewGuid(), InstrumentProtocol.AstmE1394, InstrumentTransportKind.Tcp)
        {
            Direction = InstrumentDirection.Bidirectional,
            AcknowledgementTimeoutSeconds = ackSeconds,
            MaxRetries = 0
        };

    private static IReadOnlyList<InstrumentOrderDownload> Worklist() =>
        [new InstrumentOrderDownload { SpecimenId = "TUBE-9", InstrumentTestCodes = ["GLU"] }];

    private static async Task Stopped(Task run)
    {
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }

        run.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task SilentAnalyser_BidTimesOutAtTheAcknowledgementWindow()
    {
        var transport = new ScriptedAnalyser(connected: true, _ => { });
        var driver = new AstmInstrumentDriver(Link(ackSeconds: 1), transport, new RecordingSink());

        var clock = Stopwatch.StartNew();
        var sent = await driver.SendOrdersAsync(Worklist()).WaitAsync(TimeSpan.FromSeconds(10));

        sent.Should().BeFalse("nobody acknowledged the ENQ");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the read used to block until a byte arrived, so the window was never applied");
        transport.Written.Should().ContainSingle().Which.Should().Equal(AstmControl.Enq);
    }

    [Fact]
    public async Task QueuedWorklist_IsSentWhileTheLineIsIdle()
    {
        // An analyser that acknowledges everything and never transmits on its own.
        ScriptedAnalyser? analyser = null;
        analyser = new ScriptedAnalyser(connected: false, written =>
        {
            if (written[0] is AstmControl.Enq or AstmControl.Stx) analyser!.Send(AstmControl.Ack);
        });

        var driver = new AstmInstrumentDriver(Link(), analyser, new RecordingSink());

        // Not connected yet: queued.
        (await driver.SendOrdersAsync(Worklist())).Should().BeFalse();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = driver.RunAsync(stop.Token);

        await analyser.WaitForAsync(w => w.Count > 0 && w[^1] is [AstmControl.Eot], stop.Token);
        await stop.CancelAsync();
        await Stopped(run);

        analyser.Written[0].Should().Equal(AstmControl.Enq);
        analyser.WrittenText.Should().Contain("TUBE-9");
    }

    [Fact]
    public async Task AnalyserBiddingDuringOurBid_GetsTheLine_AndOurWorklistFollowsAfterwards()
    {
        var frames = new Queue<byte[]>(AstmControl.BuildFrames(AnalyserMessage, Latin1));
        var contended = false;
        var analyserSending = false;

        ScriptedAnalyser? analyser = null;
        analyser = new ScriptedAnalyser(connected: false, written =>
        {
            switch (written[0])
            {
                case AstmControl.Enq when !contended:
                    // The analyser bids at the same moment as the LIS.
                    contended = true;
                    analyserSending = true;
                    analyser!.Send(AstmControl.Enq);
                    break;

                case AstmControl.Enq:
                    analyser!.Send(AstmControl.Ack);
                    break;

                case AstmControl.Ack when analyserSending:
                    if (frames.TryDequeue(out var frame)) analyser!.Send(frame);
                    else { analyserSending = false; analyser!.Send(AstmControl.Eot); }
                    break;

                case AstmControl.Stx:
                    analyser!.Send(AstmControl.Ack);
                    break;
            }
        });

        var sink = new RecordingSink();
        var driver = new AstmInstrumentDriver(Link(), analyser, sink);

        (await driver.SendOrdersAsync(Worklist())).Should().BeFalse();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = driver.RunAsync(stop.Token);

        await analyser.WaitForAsync(w => w.Count(b => b is [AstmControl.Eot]) >= 1, stop.Token);
        await stop.CancelAsync();
        await Stopped(run);

        sink.Handled.Should().ContainSingle("the analyser's message is received, not thrown away")
            .Which.Specimens.Should().ContainSingle(s => s.SpecimenId == "TUBE-1");

        analyser.Written.Count(w => w is [AstmControl.Enq]).Should().Be(2, "our bid is re-made later");
        analyser.Written[1].Should().Equal([AstmControl.Ack], "the analyser's ENQ is acknowledged at once");
        analyser.WrittenText.Should().Contain("TUBE-9", "the worklist was re-queued, not lost");
    }

    [Fact]
    public async Task DriverShutdown_IsNotMistakenForATimeout()
    {
        var transport = new ScriptedAnalyser(connected: true, _ => { });
        var driver = new AstmInstrumentDriver(Link(ackSeconds: 30), transport, new RecordingSink());

        using var stop = new CancellationTokenSource();
        var run = driver.RunAsync(stop.Token);

        await Task.Delay(200);
        await stop.CancelAsync();

        // Shutdown ends the loop (the host expects the cancellation to surface) instead of being
        // swallowed as one more timed-out read.
        await Stopped(run);
    }

    [Fact]
    public async Task TcpTransport_ReadCancelledAtADeadline_KeepsTheConnection()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var link = new InstrumentInterface(Guid.NewGuid(), Guid.NewGuid(), InstrumentProtocol.AstmE1394,
                                           InstrumentTransportKind.Tcp)
        { Host = "127.0.0.1", TcpPort = port };

        await using var transport = new TcpInstrumentTransport(link);
        var accept = listener.AcceptTcpClientAsync();
        (await transport.ConnectAsync()).Should().BeTrue();
        using var analyser = await accept;

        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            (await transport.ReadAsync(new byte[64], deadline.Token)).Should().Be(0);

        transport.IsConnected.Should().BeTrue("a timed-out read is not a dropped link");

        await analyser.GetStream().WriteAsync(new[] { AstmControl.Enq });

        var buffer = new byte[64];
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await transport.ReadAsync(buffer, wait.Token)).Should().Be(1);
        buffer[0].Should().Be(AstmControl.Enq);

        listener.Stop();
    }

    /// <summary>
    /// An analyser on the other end of a byte pipe. Reads block until it sends, and observe the
    /// token by throwing, as the file-drop transport's idle wait does.
    /// </summary>
    private sealed class ScriptedAnalyser(bool connected, Action<byte[]> respond) : IInstrumentTransport
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Lock _gate = new();
        private readonly List<byte[]> _written = [];

        public bool IsConnected { get; private set; } = connected;
        public string Description => "scripted analyser";
        public event EventHandler<string>? Disconnected { add { } remove { } }

        public List<byte[]> Written
        {
            get { lock (_gate) return [.. _written]; }
        }

        public string WrittenText =>
            string.Concat(Written.Select(w => Latin1.GetString(w)));

        public void Send(params byte[] bytes) => _inbound.Writer.TryWrite(bytes);

        public async Task WaitForAsync(Func<List<byte[]>, bool> condition, CancellationToken ct)
        {
            while (!condition(Written)) await Task.Delay(20, ct);
        }

        public Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.FromResult(true);
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct = default)
        {
            var bytes = await _inbound.Reader.ReadAsync(ct);
            bytes.CopyTo(buffer, 0);
            return bytes.Length;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var bytes = data.ToArray();
            lock (_gate) _written.Add(bytes);
            respond(bytes);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IInstrumentMessageSink
    {
        public List<InstrumentMessage> Handled { get; } = [];

        public Task<bool> HandleAsync(Guid interfaceId, InstrumentMessage message, CancellationToken ct = default)
        {
            Handled.Add(message);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<InstrumentOrderDownload>> ResolveQueryAsync(
            Guid interfaceId, IReadOnlyList<string> specimenIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstrumentOrderDownload>>([]);

        public Task LogRawAsync(Guid interfaceId, MessageDirection direction, string content,
                                CancellationToken ct = default) => Task.CompletedTask;

        public Task ReportConnectionStateAsync(Guid interfaceId, bool connected, string? error,
                                               CancellationToken ct = default) => Task.CompletedTask;
    }
}
