using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Instruments.Drivers;
using MarocLIS.Instruments.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Instruments.Hosting;

/// <summary>
/// Runs the analyser links for as long as the application is up.
/// <para>
/// This is what was missing. The drivers, transports and parsers existed and compiled, but nothing
/// started them: no hosted service, no DI registration, no sink. A laboratory running this
/// application had a fully written ASTM and HL7 stack and still typed every result in by hand.
/// </para>
/// <para>
/// One driver per enabled interface, each in its own loop, each reconnecting on its own. The
/// interface list is re-read periodically so enabling an analyser takes effect without restarting
/// the application — a laboratory that has to close the LIS to connect an analyser will connect it
/// at the end of the day instead, which is to say not at all.
/// </para>
/// <para>
/// Every driver runs in its own DI scope with its own database context. Sharing one would mean a
/// change tracker shared between eight analysers transmitting concurrently, where one failed import
/// rolls back another analyser's results.
/// </para>
/// </summary>
public sealed class InstrumentHostedService(
    IServiceScopeFactory scopes,
    InstrumentHostOptions options,
    InstrumentServiceAccount serviceAccount,
    InstrumentLinkMonitor monitor,
    ILogger<InstrumentHostedService>? logger = null) : BackgroundService
{
    private readonly Dictionary<Guid, RunningLink> _running = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableInstrumentHost)
        {
            // Off by default and silent about it beyond one line. An interface pointed at a serial
            // port nobody configured would otherwise log a reconnection failure every two seconds
            // for as long as the application is open.
            logger?.LogInformation("Instrument host disabled by configuration.");
            return;
        }

        logger?.LogInformation("Instrument host started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Resolved inside the loop, not before it. The host starts before the database has
                // been created on a first run, and resolving once would leave the interfaces dead
                // for the life of the process over an ordering accident. Re-checking each pass also
                // means creating the account later takes effect without a restart.
                if (!serviceAccount.IsResolved && !await serviceAccount.ResolveAsync(stoppingToken))
                {
                    // Refused rather than degraded, and this is the one failure where carrying on
                    // is worse than not starting: results written against no identity, or against
                    // whichever operator happened to be signed in, are a falsified record.
                    monitor.ReportHostError("Interface_ServiceAccountMissing");

                    await StopAllAsync();

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The host must survive a bad configuration row. One unparseable interface should
                // not take the other seven analysers off the air.
                logger?.LogError(ex, "Instrument host reconciliation failed.");
                monitor.ReportHostError(ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.ConfigurationPollSeconds,
                                                              5, 600)),
                              stoppingToken);
        }

        await StopAllAsync();
    }

    /// <summary>
    /// Brings the running links into line with the configuration: starts what is newly enabled,
    /// stops what is no longer, and restarts what has been edited.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IRepository<InstrumentInterface>>();

        var enabled = await repository.ListAsync(i => i.IsEnabled, ct);

        var wanted = enabled.ToDictionary(i => i.Id);

        foreach (var (id, link) in _running.ToList())
        {
            if (wanted.TryGetValue(id, out var desired)
                && link.Fingerprint == Fingerprint(desired)) continue;

            // Either disabled or reconfigured. A reconfigured link is stopped and started rather
            // than mutated: a driver holds a transport whose port, baud rate and encoding were
            // fixed when it opened.
            logger?.LogInformation("Stopping analyser link {Interface}.", id);

            await link.StopAsync();
            _running.Remove(id);
            monitor.ReportStopped(id);
        }

        foreach (var link in enabled)
        {
            if (_running.ContainsKey(link.Id)) continue;

            var valid = link.Validate();

            if (valid.IsFailure)
            {
                // Reported, not started. An interface with no serial port named cannot connect, and
                // retrying it every thirty seconds would bury the real reason in the log.
                monitor.ReportMisconfigured(link.Id, valid.Errors.Select(e => e.ResourceKey).ToList());
                continue;
            }

            Start(link, ct);
        }
    }

    private void Start(InstrumentInterface link, CancellationToken ct)
    {
        var scope = scopes.CreateScope();

        try
        {
            var sink = scope.ServiceProvider.GetRequiredService<IInstrumentMessageSink>();
            var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();

            var transport = BuildTransport(link, loggerFactory?.CreateLogger("Instrument.Transport"));

            if (transport is null)
            {
                monitor.ReportMisconfigured(link.Id, ["Interface_TransportNotSupported"]);
                scope.Dispose();
                return;
            }

            var driver = BuildDriver(link, transport, sink,
                                      loggerFactory?.CreateLogger("Instrument.Driver"));

            if (driver is null)
            {
                monitor.ReportMisconfigured(link.Id, ["Interface_ProtocolNotSupported"]);
                scope.Dispose();
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var task = Task.Run(async () =>
            {
                try
                {
                    await driver.RunAsync(cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // A driver that throws is reported and left stopped. The next reconciliation
                    // starts it again, which gives a flapping link a thirty-second floor instead of
                    // a tight crash loop.
                    logger?.LogError(ex, "Analyser link {Interface} failed.", link.Id);
                    monitor.ReportFailed(link.Id, ex.Message);
                }
            }, cts.Token);

            _running[link.Id] = new RunningLink(link.Id, Fingerprint(link), driver, cts, task, scope);

            monitor.ReportStarted(link.Id, driver.Description);

            logger?.LogInformation("Analyser link {Interface} started: {Description}",
                                    link.Id, driver.Description);
        }
        catch (Exception)
        {
            scope.Dispose();
            throw;
        }
    }

    private static IInstrumentTransport? BuildTransport(InstrumentInterface link, ILogger? logger) =>
        link.Transport switch
        {
            InstrumentTransportKind.Serial => new SerialInstrumentTransport(link, logger),
            InstrumentTransportKind.Tcp => new TcpInstrumentTransport(link, logger),
            InstrumentTransportKind.FileDrop => new FileDropInstrumentTransport(link, logger),

            // A vendor SDK needs a plug-in this build does not carry. Reported rather than
            // approximated with a serial port that would connect to nothing.
            _ => null
        };

    private static IInstrumentDriver? BuildDriver(InstrumentInterface link,
                                                   IInstrumentTransport transport,
                                                   IInstrumentMessageSink sink,
                                                   ILogger? logger) =>
        link.Protocol switch
        {
            InstrumentProtocol.AstmE1394 => new AstmInstrumentDriver(link, transport, sink, logger),
            InstrumentProtocol.Hl7V2 => new Hl7InstrumentDriver(link, transport, sink, logger),
            _ => null
        };

    /// <summary>
    /// Everything a driver reads at construction. When it changes the link is restarted, because a
    /// transport cannot change its port or its encoding once open.
    /// </summary>
    private static string Fingerprint(InstrumentInterface link) =>
        string.Join('|', link.Protocol, link.Transport, link.Direction, link.SerialPortName,
                     link.BaudRate, link.DataBits, link.Parity, link.StopBits, link.Handshake,
                     link.Host, link.TcpPort, link.ListenAsServer, link.InboundDirectory,
                     link.OutboundDirectory, link.FilePattern, link.ProcessedDirectory,
                     link.Encoding, link.AcknowledgementTimeoutSeconds, link.MaxRetries);

    private async Task StopAllAsync()
    {
        foreach (var link in _running.Values) await link.StopAsync();

        _running.Clear();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopAllAsync();
        await base.StopAsync(cancellationToken);
    }

    private sealed record RunningLink(Guid Id, string Fingerprint, IInstrumentDriver Driver,
                                       CancellationTokenSource Cancellation, Task Task,
                                       IServiceScope Scope)
    {
        internal async Task StopAsync()
        {
            await Cancellation.CancelAsync();

            try
            {
                // Bounded: a driver blocked on a serial read that never returns must not hold the
                // application open when the operator closes it.
                await Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }

            await Driver.DisposeAsync();
            Cancellation.Dispose();
            Scope.Dispose();
        }
    }
}

/// <summary>
/// Resolves the non-interactive account analyser results are attributed to, once, at start-up.
/// </summary>
public sealed class InstrumentServiceAccount(IServiceScopeFactory scopes,
                                              InstrumentHostOptions options)
    : IInstrumentServiceAccount
{
    public Guid UserId { get; private set; }

    public bool IsResolved => UserId != Guid.Empty;

    public Task<bool> EnsureResolvedAsync(CancellationToken ct = default) => ResolveAsync(ct);

    internal async Task<bool> ResolveAsync(CancellationToken ct)
    {
        if (IsResolved) return true;

        try
        {
            using var scope = scopes.CreateScope();

            var users = scope.ServiceProvider.GetRequiredService<IRepository<AppUser>>();

            var name = options.ServiceAccountUserName.Trim().ToLowerInvariant();

            var found = await users.ListAsync(u => u.UserName == name, ct);

            var account = found.FirstOrDefault(u => u.IsActive);

            if (account is null) return false;

            UserId = account.Id;
            return true;
        }
        catch (Exception)
        {
            // The schema may not exist yet on a first run. Treated as "not resolved" so the caller
            // retries, rather than as a fault that takes the host down.
            return false;
        }
    }
}

/// <summary>
/// Live state of every analyser link, for the interface screen.
/// <para>
/// In memory and not persisted: "is the analyser talking to us right now?" is a question about this
/// process, and writing it to the database would make a stale row look like a live connection after
/// a crash. What <em>is</em> persisted — last connection, last message, last error — lives on the
/// interface row and survives a restart, which is the part a laboratory needs tomorrow morning.
/// </para>
/// </summary>
public sealed class InstrumentLinkMonitor
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, InstrumentLinkState> _states = [];

    private string? _hostError;

    public string? HostError
    {
        get { lock (_gate) return _hostError; }
    }

    public IReadOnlyList<InstrumentLinkState> Snapshot()
    {
        lock (_gate) return [.. _states.Values];
    }

    public InstrumentLinkState? For(Guid interfaceId)
    {
        lock (_gate) return _states.TryGetValue(interfaceId, out var state) ? state : null;
    }

    internal void ReportHostError(string error)
    {
        lock (_gate) _hostError = error;
    }

    internal void ReportStarted(Guid interfaceId, string description)
    {
        lock (_gate)
            _states[interfaceId] = new InstrumentLinkState(interfaceId, InstrumentLinkStatus.Running,
                                                            description, null, DateTimeOffset.Now);
    }

    internal void ReportStopped(Guid interfaceId)
    {
        lock (_gate)
            _states[interfaceId] = new InstrumentLinkState(interfaceId, InstrumentLinkStatus.Stopped,
                                                            null, null, DateTimeOffset.Now);
    }

    internal void ReportFailed(Guid interfaceId, string error)
    {
        lock (_gate)
            _states[interfaceId] = new InstrumentLinkState(interfaceId, InstrumentLinkStatus.Failed,
                                                            null, error, DateTimeOffset.Now);
    }

    internal void ReportMisconfigured(Guid interfaceId, IReadOnlyList<string> problems)
    {
        lock (_gate)
            _states[interfaceId] = new InstrumentLinkState(
                interfaceId, InstrumentLinkStatus.Misconfigured, null,
                string.Join(", ", problems), DateTimeOffset.Now);
    }
}

public sealed record InstrumentLinkState(Guid InterfaceId, InstrumentLinkStatus Status,
                                          string? Description, string? Error,
                                          DateTimeOffset ChangedAt);

public enum InstrumentLinkStatus
{
    Stopped = 0,
    Running = 1,
    Failed = 2,

    /// <summary>Enabled but not startable: a missing port, host or directory.</summary>
    Misconfigured = 3
}
