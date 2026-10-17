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
/// Every message a driver delivers is handled in a DI scope of its own, with its own database
/// context (<see cref="ScopedInstrumentMessageSink"/>). Sharing one would mean a change tracker
/// shared between eight analysers transmitting concurrently, where one failed import rolls back
/// another analyser's results; keeping one per link for its whole life meant reading mappings and
/// tubes as they were days ago.
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
    private readonly LinkRestartPolicy _restarts = new();

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

                // A pass that went through clears the last failure. The host starts before the
                // database is migrated, so the first pass after an update fails on the new
                // columns; without this the banner kept that stale error for the whole session.
                monitor.ClearHostError();
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

        // Every enabled link, whatever site the operator signed in here works on. Under the
        // operator's site filter, signing in stopped the other sites' analysers at the next pass.
        scope.ServiceProvider.GetService<ScopeIdentity>()?.RunAs(serviceAccount.AsCurrentUser(),
                                                                  ignoreSiteScope: true);

        var repository = scope.ServiceProvider.GetRequiredService<IRepository<InstrumentInterface>>();

        // Loaded one by one with their mappings. The list query does not bring the mapping table,
        // and validation reads it: a bidirectional link looked empty and was refused as
        // misconfigured however many codes it had, so no bidirectional analyser ever started.
        var enabled = new List<InstrumentInterface>();

        foreach (var row in await repository.ListAsync(i => i.IsEnabled, ct))
            if (await repository.GetByIdWithDetailsAsync(row.Id, ct) is { } full) enabled.Add(full);

        var wanted = enabled.ToDictionary(i => i.Id);

        foreach (var (id, link) in _running.ToList())
        {
            var unchanged = wanted.TryGetValue(id, out var desired)
                            && link.Fingerprint == Fingerprint(desired);

            if (unchanged && !link.Task.IsCompleted) continue;

            if (unchanged)
            {
                // The driver ended on its own — an exception that escaped it (a share that blipped
                // under an acknowledgement, a serial write timeout), or a loop that returned. It
                // stayed in this table with the same fingerprint, and both checks here skipped it
                // on every pass: the analyser's results stopped until somebody restarted the
                // application. It is restarted once its back-off has run out.
                if (!_restarts.IsDue(id, DateTimeOffset.UtcNow)) continue;

                logger?.LogInformation("Restarting analyser link {Interface} after a failure.", id);

                await link.StopAsync();
                _running.Remove(id);
                continue;
            }

            // Either disabled or reconfigured. A reconfigured link is stopped and started rather
            // than mutated: a driver holds a transport whose port, baud rate and encoding were
            // fixed when it opened.
            logger?.LogInformation("Stopping analyser link {Interface}.", id);

            await link.StopAsync();
            _running.Remove(id);
            monitor.ReportStopped(id);

            // Disabled or edited: whoever touched it starts the next run with a clean slate.
            _restarts.Forget(id);
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
            var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
            // Under the service account, not the operator signed in here: see ScopedInstrumentMessageSink.
            var sink = new ScopedInstrumentMessageSink(scopes, loggerFactory?.CreateLogger("Instrument.Sink"),
                                                       serviceAccount.AsCurrentUser());

            var transport = BuildTransport(link, loggerFactory?.CreateLogger("Instrument.Transport"));

            if (transport is null)
            {
                monitor.ReportMisconfigured(link.Id, ["Interface_TransportNotSupported"]);
                scope.Dispose();
                return;
            }

            var driver = BuildDriver(link, transport, sink,
                                      loggerFactory?.CreateLogger("Instrument.Driver"),
                                      scope.ServiceProvider.GetService<IntegraHostOptions>());

            if (driver is null)
            {
                monitor.ReportMisconfigured(link.Id, ["Interface_ProtocolNotSupported"]);
                scope.Dispose();
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var startedAt = DateTimeOffset.UtcNow;

            var task = Task.Run(async () =>
            {
                try
                {
                    await driver.RunAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    // A driver that throws is reported and left stopped; a reconciliation pass
                    // restarts it once LinkRestartPolicy's back-off has run out, so a flapping link
                    // waits longer each time instead of crash-looping.
                    logger?.LogError(ex, "Analyser link {Interface} failed.", link.Id);
                    monitor.ReportFailed(link.Id, ex.Message);
                }
                finally
                {
                    // Recorded for any end the host did not ask for, a driver loop that simply
                    // returned included: either way the analyser is no longer being listened to.
                    if (!cts.IsCancellationRequested)
                        _restarts.RecordFailure(link.Id, startedAt, DateTimeOffset.UtcNow);
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
                                                   ILogger? logger,
                                                   IntegraHostOptions? integra = null) =>
        link.Protocol switch
        {
            InstrumentProtocol.AstmE1394 => new AstmInstrumentDriver(link, transport, sink, logger),
            InstrumentProtocol.Hl7V2 => new Hl7InstrumentDriver(link, transport, sink, logger),
            InstrumentProtocol.Poct1A => new Poct1AInstrumentDriver(link, transport, sink, logger),
            InstrumentProtocol.RocheIntegra => new RocheIntegraInstrumentDriver(link, transport, sink, logger, integra),
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
                     link.Encoding, link.AcknowledgementTimeoutSeconds, link.MaxRetries,
                     // The parsers read the format from the link they were started with.
                     link.MessageFormat);

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
/// When an analyser link whose driver failed is started again.
/// <para>
/// The first restart comes after thirty seconds, then the wait doubles with each failure in a row,
/// up to ten minutes: a share that blipped is back within a minute, while a link that fails as
/// soon as it starts does not fill the log. A run that lasted longer than the longest wait counts
/// as having worked, so the next failure starts again from thirty seconds.
/// </para>
/// </summary>
public sealed class LinkRestartPolicy
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset DueAt)> _failed = [];

    /// <summary>The wait before the restart that follows <paramref name="failuresInARow"/> failures.</summary>
    public static TimeSpan DelayAfter(int failuresInARow)
    {
        if (failuresInARow <= 1) return FirstDelay;

        var seconds = FirstDelay.TotalSeconds * Math.Pow(2, Math.Min(failuresInARow - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    /// <summary>Called by the link's own task when its driver ends without being asked to.</summary>
    public void RecordFailure(Guid interfaceId, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        lock (_gate)
        {
            var inARow = _failed.TryGetValue(interfaceId, out var previous)
                         && endedAt - startedAt < MaxDelay
                ? previous.Failures + 1
                : 1;

            _failed[interfaceId] = (inARow, endedAt + DelayAfter(inARow));
        }
    }

    /// <summary>True once a failed link's wait has run out.</summary>
    public bool IsDue(Guid interfaceId, DateTimeOffset now)
    {
        lock (_gate) return !_failed.TryGetValue(interfaceId, out var state) || now >= state.DueAt;
    }

    /// <summary>Forgets a link's failures, when it is disabled or reconfigured.</summary>
    public void Forget(Guid interfaceId)
    {
        lock (_gate) _failed.Remove(interfaceId);
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

    public string UserName { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    public bool IsResolved => UserId != Guid.Empty;

    /// <summary>The identity the host's scopes work under; only meaningful once resolved.</summary>
    public ICurrentUser AsCurrentUser() => new ServiceAccountUser(UserId, UserName, FullName);

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

            UserName = account.UserName;
            FullName = account.FullName;
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

    internal void ClearHostError()
    {
        lock (_gate) _hostError = null;
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
