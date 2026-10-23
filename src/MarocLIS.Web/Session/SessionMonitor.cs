using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Web.Security;

namespace MarocLIS.Web.Session;

/// <summary>
/// What the Windows shell's timers do, for one browser tab: the idle lock, the check of the session
/// against the account, and the heartbeat that keeps the workstation's place.
/// <para>
/// Every quarter of a minute it looks at the time since the operator last did something in the tab;
/// past the password policy's idle timeout the session is ended (recorded as a timeout) and the tab
/// sent to the sign-in page. Every minute it re-checks the account (<see cref="ISessionValidator"/>):
/// deactivated, password reset, a role withdrawn — the session ends, as the shell's does. Every
/// <see cref="WebWorkstations.HeartbeatInterval"/> it renews the place under the licence; a tab
/// found over the count is not signed out in the middle of a task, it is refused at its next sign-in.
/// </para>
/// </summary>
public sealed class SessionMonitor(
    SessionContext context,
    UserScopes scopes,
    WebSessionStore store,
    WebWorkstations workstations,
    SessionEnding ending,
    ILogger<SessionMonitor> logger) : IAsyncDisposable
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SessionCheckInterval = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBeat = DateTimeOffset.MinValue;
    private int _ending;

    /// <summary>Sends the tab to the sign-in page, with the reason to say there.</summary>
    private Func<string, Task>? _leave;

    /// <summary>
    /// Starts the checks for the tab. <paramref name="leave"/> navigates the tab away, on its
    /// renderer's thread, once its session has ended.
    /// </summary>
    public void Start(Func<string, Task> leave)
    {
        if (_loop is not null || context.Session is null) return;

        _leave = leave;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Tick);

        try
        {
            do
            {
                if (!await CheckAsync(DateTimeOffset.Now)) return;
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
            // The tab closed.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The session monitor of a web tab stopped.");
        }
    }

    /// <summary>One round of the checks; false once the session has ended and the tab was sent away.</summary>
    internal async Task<bool> CheckAsync(DateTimeOffset now)
    {
        // Signed out in another tab of the browser, or ended by the server: nothing to record here.
        if (!context.Refresh() || context.Session is not { } session)
        {
            await LeaveAsync("Message.SessionExpired");
            return false;
        }

        // Activity in any tab of the browser counts, as activity in any window of the Windows
        // application does: the session's own time is touched by every tab.
        var idle = store.IdleTimeout;
        var lastActivity = context.LastActivity > session.LastSeenAt ? context.LastActivity : session.LastSeenAt;
        if (idle > TimeSpan.Zero && now - lastActivity >= idle)
        {
            await EndAsync(SecurityEventKind.SessionTimeout, $"Web: idle for {idle.TotalMinutes:0} min",
                           "Message.SessionExpired");
            return false;
        }

        if (now - _lastCheck >= SessionCheckInterval)
        {
            _lastCheck = now;
            if (!await IsStillValidAsync())
            {
                await EndAsync(SecurityEventKind.Logout, "Web session ended: account changed since sign-in",
                               "Auth_SessionRevoked");
                return false;
            }
        }

        if (now - _lastBeat >= WebWorkstations.HeartbeatInterval)
        {
            _lastBeat = now;
            await using var scope = scopes.Create();
            await workstations.ClaimAsync(scope.ServiceProvider, session.Workstation, session.Session.UserName, _stop.Token);
        }

        return true;
    }

    /// <summary>
    /// A check that cannot reach the database leaves the session alone, as at the workstation: the
    /// next operation fails on its own, and a network blip must not sign every browser out at once.
    /// </summary>
    private async Task<bool> IsStillValidAsync()
    {
        try
        {
            await using var scope = scopes.Create();
            return await scope.ServiceProvider.GetRequiredService<ISessionValidator>().IsStillValidAsync(_stop.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The web session could not be re-checked against the account.");
            return true;
        }
    }

    /// <summary>Ends the session for <paramref name="reason"/>, then sends the tab to the sign-in page.</summary>
    public async Task EndAsync(SecurityEventKind reason, string? details, string reasonKey)
    {
        if (Interlocked.Exchange(ref _ending, 1) == 1 || context.Session is not { } session) return;

        await using (var scope = scopes.Create())
            await ending.EndAsync(scope.ServiceProvider, session, reason, details);

        await LeaveAsync(reasonKey);
    }

    private async Task LeaveAsync(string reasonKey)
    {
        if (_leave is null) return;

        try
        {
            await _leave(reasonKey);
        }
        catch (Exception ex)
        {
            // The tab is already gone; its circuit will close on its own.
            logger.LogDebug(ex, "A web tab could not be sent to the sign-in page.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
