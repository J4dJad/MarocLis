using System.Collections.Concurrent;
using System.Reflection;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Web.Security;

/// <summary>
/// The browsers' places under the licence's count of workstations, through the same
/// <see cref="WorkstationPresenceService"/> as the Windows workstations: claimed at sign-in, kept by
/// the heartbeat, given back at sign-out and when the browser's last tab closes.
/// <para>
/// A browser is one workstation however many tabs it has open, as a PC is one however many windows:
/// the tabs (circuits) of each are counted here, and the place is given back when the last one
/// closes — not when a tab is reloaded, which closes its old circuit only after the new one opened.
/// </para>
/// </summary>
public sealed class WebWorkstations(IServiceScopeFactory scopes, ILogger<WebWorkstations> logger)
{
    private readonly ConcurrentDictionary<string, int> _circuits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The version recorded with the workstation, as the Windows application records its own.</summary>
    public static string AppVersion { get; } =
        (typeof(WebWorkstations).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(WebWorkstations).Assembly.GetName().Version?.ToString() ?? "?").Split('+')[0];

    /// <summary>
    /// Asks for <paramref name="workstation"/>'s place, as the Windows login does. A database that
    /// cannot answer lets the sign-in through, like the other checks that are not the password's.
    /// </summary>
    public async Task<WorkstationClaim?> ClaimAsync(IServiceProvider services, string workstation, string userName,
                                                    CancellationToken ct = default)
    {
        try
        {
            return await services.GetRequiredService<WorkstationPresenceService>()
                                 .ClaimAsync(workstation, userName, AppVersion, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The web workstation {Workstation} could not be counted.", workstation);
            return null;
        }
    }

    /// <summary>A tab of <paramref name="workstation"/> opened.</summary>
    public void Enter(string workstation) => _circuits.AddOrUpdate(workstation, 1, (_, n) => n + 1);

    /// <summary>
    /// A tab of <paramref name="workstation"/> closed; the place is given back when it was the last.
    /// </summary>
    public async Task LeaveAsync(string workstation)
    {
        var remaining = _circuits.AddOrUpdate(workstation, 0, (_, n) => Math.Max(0, n - 1));
        if (remaining > 0) return;

        _circuits.TryRemove(new KeyValuePair<string, int>(workstation, 0));
        await ReleaseAsync(workstation);
    }

    /// <summary>Tabs open now for <paramref name="workstation"/>.</summary>
    public int OpenTabs(string workstation) => _circuits.TryGetValue(workstation, out var n) ? n : 0;

    /// <summary>Gives the place back at once: sign-out, end of the session, last tab closed.</summary>
    public async Task ReleaseAsync(string workstation)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WorkstationPresenceService>().ReleaseAsync(workstation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Freed after the quarter of an hour of silence instead, as at the workstation.
            logger.LogWarning(ex, "The place of the web workstation {Workstation} could not be released.", workstation);
        }
    }

    /// <summary>How often a tab renews its place: the workstations' heartbeat.</summary>
    public static TimeSpan HeartbeatInterval => Workstation.HeartbeatInterval;
}
