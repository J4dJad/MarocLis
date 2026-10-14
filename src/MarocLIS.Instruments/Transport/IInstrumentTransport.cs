using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MarocLIS.Domain.Equipment;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Transport;

/// <summary>
/// A byte pipe to one analyser, with the physical layer hidden.
/// <para>
/// Deliberately byte-oriented rather than message-oriented: the framing belongs to the protocol
/// (ASTM or MLLP), and the transport must not assume where a message ends. A serial port delivers
/// arbitrary chunks and a TCP socket coalesces writes, so any transport that promised whole
/// messages would be lying about one of them.
/// </para>
/// </summary>
public interface IInstrumentTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Human-readable endpoint, for the interface screen and the logs.</summary>
    string Description { get; }

    Task<bool> ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>
    /// Reads whatever is available, blocking until at least one byte arrives or the token fires.
    /// Returns an empty span when the peer closed the connection.
    /// </summary>
    Task<int> ReadAsync(byte[] buffer, CancellationToken ct = default);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Raised when the link drops, so the host can schedule a reconnection.</summary>
    event EventHandler<string>? Disconnected;
}

/// <summary>
/// Serial transport. Still the most common analyser connection in a Moroccan laboratory: plenty of
/// installed instruments are a decade old and speak RS-232 only.
/// </summary>
public sealed class SerialInstrumentTransport : IInstrumentTransport
{
    private readonly InstrumentInterface _config;
    private readonly ILogger? _logger;
    private SerialPort? _port;

    public SerialInstrumentTransport(InstrumentInterface config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsConnected => _port?.IsOpen == true;

    public string Description => $"{_config.SerialPortName} @ {_config.BaudRate}-{_config.DataBits}-" +
                                 $"{_config.Parity}-{_config.StopBits}";

    public event EventHandler<string>? Disconnected;

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return Task.FromResult(true);

        try
        {
            _port = new SerialPort(_config.SerialPortName!)
            {
                BaudRate = _config.BaudRate,
                DataBits = _config.DataBits,
                Parity = Enum.TryParse<Parity>(_config.Parity, true, out var parity) ? parity : Parity.None,
                StopBits = Enum.TryParse<StopBits>(_config.StopBits, true, out var stop) ? stop : StopBits.One,
                Handshake = Enum.TryParse<Handshake>(_config.Handshake, true, out var hs) ? hs : Handshake.None,
                // No read timeout: the host loop owns the waiting and the cancellation.
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 5000,
                // Several analysers will not transmit unless these are asserted, and the symptom is
                // a link that looks connected and stays permanently silent.
                DtrEnable = true,
                RtsEnable = _config.Handshake is not "RequestToSend"
            };

            _port.ErrorReceived += OnErrorReceived;
            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            _logger?.LogInformation("Serial port {Port} opened for instrument interface {Interface}.",
                                    Description, _config.Id);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or ArgumentException or InvalidOperationException)
        {
            _logger?.LogError(ex, "Could not open serial port {Port}.", _config.SerialPortName);
            _port?.Dispose();
            _port = null;
            return Task.FromResult(false);
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        _logger?.LogWarning("Serial error {Error} on {Port}.", e.EventType, _config.SerialPortName);

        // A framing or parity error usually means the baud rate or parity is misconfigured. Worth
        // surfacing loudly: the alternative is silently corrupted result values.
        if (e.EventType is SerialError.Frame or SerialError.RXParity)
            Disconnected?.Invoke(this, $"Serial {e.EventType} — check baud rate and parity");
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct = default)
    {
        if (_port is null || !_port.IsOpen) return 0;

        try
        {
            return await _port.BaseStream.ReadAsync(buffer, ct);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or ObjectDisposedException)
        {
            // Closed so the driver reopens it on its next pass, with back-off. Left open, an
            // unplugged USB adapter threw on every read and the driver spun on it.
            await DisconnectAsync();
            Disconnected?.Invoke(this, ex.Message);
            return 0;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_port is null || !_port.IsOpen) return;

        try
        {
            await _port.BaseStream.WriteAsync(data, ct);
            await _port.BaseStream.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or ObjectDisposedException)
        {
            Disconnected?.Invoke(this, ex.Message);
        }
    }

    public Task DisconnectAsync()
    {
        if (_port is not null)
        {
            _port.ErrorReceived -= OnErrorReceived;
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
            _port = null;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

/// <summary>
/// TCP transport, in either direction: the LIS connects to the analyser, or listens and lets the
/// analyser connect. Which way round is a vendor decision, so both are supported.
/// </summary>
public sealed class TcpInstrumentTransport : IInstrumentTransport
{
    private readonly InstrumentInterface _config;
    private readonly ILogger? _logger;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpInstrumentTransport(InstrumentInterface config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public string Description => _config.ListenAsServer
        ? $"listening on :{_config.TcpPort}"
        : $"{_config.Host}:{_config.TcpPort}";

    public event EventHandler<string>? Disconnected;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        try
        {
            if (_config.ListenAsServer)
            {
                _listener ??= new TcpListener(IPAddress.Any, _config.TcpPort ?? 0);
                _listener.Start();

                _logger?.LogInformation("Awaiting analyser connection on port {Port}.", _config.TcpPort);
                _client = await _listener.AcceptTcpClientAsync(ct);
            }
            else
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_config.Host!, _config.TcpPort ?? 0, ct);
            }

            // Analyser traffic is small and latency-sensitive; Nagle would delay an ACK behind a
            // partially filled segment and trip the instrument's own timeout.
            _client.NoDelay = true;
            _stream = _client.GetStream();

            _logger?.LogInformation("Instrument link established: {Description}.", Description);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            _logger?.LogError(ex, "Could not establish the instrument link {Description}.", Description);
            await DisconnectAsync();
            return false;
        }
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct = default)
    {
        if (_stream is null) return 0;

        try
        {
            var read = await _stream.ReadAsync(buffer, ct);
            if (read == 0) await DropAsync("Peer closed the connection");
            return read;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            await DropAsync(ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Closes the socket when the analyser has gone, so the driver's next pass reconnects.
    /// <para>
    /// The socket used to be left open. <see cref="TcpClient.Connected"/> stays true after the
    /// peer closes, so the driver kept reading a stream that returned nothing at once — a tight
    /// loop that logged "link dropped" without pause and filled the disk with hundreds of
    /// megabytes of log in an hour, every time an analyser closed its connection.
    /// </para>
    /// </summary>
    private async Task DropAsync(string reason)
    {
        await DisconnectAsync();
        Disconnected?.Invoke(this, reason);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_stream is null) return;

        try
        {
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            await DropAsync(ex.Message);
        }
    }

    public Task DisconnectAsync()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;

        // The listener stays up: the analyser will reconnect to the same port, and tearing the
        // listener down would race the reconnection.
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _listener?.Stop();
        _listener = null;
    }
}

/// <summary>
/// File-drop transport, for analysers whose only output is a file on a share.
/// <para>
/// A consumed file is <em>moved</em> to a processed folder rather than deleted, and a file is only
/// read once its size has stopped changing. Both matter: reading a half-written export produces a
/// truncated result set, and deleting the original leaves nothing to re-import when a mapping turns
/// out to be wrong.
/// </para>
/// </summary>
public sealed class FileDropInstrumentTransport : IInstrumentTransport
{
    private readonly InstrumentInterface _config;
    private readonly ILogger? _logger;
    private readonly Encoding _encoding;
    private readonly Queue<byte> _pending = new();

    public FileDropInstrumentTransport(InstrumentInterface config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
        _encoding = ResolveEncoding(config.Encoding);
    }

    public bool IsConnected { get; private set; }

    public string Description => $"{_config.InboundDirectory} ({_config.FilePattern})";

    public event EventHandler<string>? Disconnected;

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.InboundDirectory)
            || !Directory.Exists(_config.InboundDirectory))
        {
            _logger?.LogError("Instrument inbound folder {Folder} does not exist.",
                              _config.InboundDirectory);
            return Task.FromResult(false);
        }

        IsConnected = true;
        return Task.FromResult(true);
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct = default)
    {
        if (!IsConnected) return 0;

        if (_pending.Count == 0) await LoadNextFileAsync(ct);

        var count = 0;
        while (count < buffer.Length && _pending.Count > 0) buffer[count++] = _pending.Dequeue();

        // Nothing waiting: yield so the host loop does not spin on an idle folder.
        if (count == 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);

        return count;
    }

    private async Task LoadNextFileAsync(CancellationToken ct)
    {
        try
        {
            // The folder going away is this transport's disconnection: an unmounted share, a
            // renamed directory, an analyser's export path reconfigured by its own engineer. It
            // was not reported, so the link stayed "connected" and received nothing for as long
            // as that lasted — the one failure mode of a file interface that looks exactly like a
            // quiet day on the bench. EnumerateFiles would throw DirectoryNotFoundException, which
            // is not an IOException subclass the catch below handles, so it would also have come
            // out of the host loop as an unhandled exception.
            if (!Directory.Exists(_config.InboundDirectory))
            {
                IsConnected = false;
                Disconnected?.Invoke(
                    this, $"Inbound folder {_config.InboundDirectory} is no longer reachable");
                return;
            }

            var candidate = Directory
                .EnumerateFiles(_config.InboundDirectory!, _config.FilePattern ?? "*.txt")
                .OrderBy(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (candidate is null) return;

            // Wait for the writer to finish: an analyser writing a large export would otherwise be
            // read mid-write, producing a truncated and silently incomplete result set.
            if (!await IsStableAsync(candidate, ct)) return;

            var content = await File.ReadAllBytesAsync(candidate, ct);
            foreach (var b in content) _pending.Enqueue(b);

            Archive(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not read from the instrument folder {Folder}.",
                                _config.InboundDirectory);
        }
    }

    private static async Task<bool> IsStableAsync(string path, CancellationToken ct)
    {
        try
        {
            var first = new FileInfo(path).Length;
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            var second = new FileInfo(path).Length;

            if (first != second) return false;

            // An exclusive open is the definitive test that the writer has let go.
            await using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void Archive(string path)
    {
        var target = _config.ProcessedDirectory;

        if (string.IsNullOrWhiteSpace(target))
        {
            // Without a processed folder the file would be read again on the next pass. Renaming in
            // place is the least destructive way to make it stop matching the pattern.
            File.Move(path, path + ".processed", overwrite: true);
            return;
        }

        Directory.CreateDirectory(target);

        var name = $"{Path.GetFileNameWithoutExtension(path)}-" +
                   $"{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}";

        File.Move(path, Path.Combine(target, name), overwrite: true);
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.OutboundDirectory)) return Task.CompletedTask;

        Directory.CreateDirectory(_config.OutboundDirectory);

        var path = Path.Combine(_config.OutboundDirectory,
                                $"orders-{DateTime.Now:yyyyMMdd-HHmmssfff}.txt");

        return File.WriteAllBytesAsync(path, data.ToArray(), ct);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        _pending.Clear();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    internal static Encoding ResolveEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            // Latin-1 rather than UTF-8: almost every analyser that predates Unicode sends
            // ISO-8859-1, and decoding those bytes as UTF-8 corrupts accented comments.
            return Encoding.Latin1;
        }
    }
}
