using MarocLIS.Domain.Audit;
using MarocLIS.Web.Security;
using IAuthenticationService = MarocLIS.Application.Abstractions.IAuthenticationService;

namespace MarocLIS.Web.Session;

/// <summary>
/// Ends a browser's session the way the Windows shell ends one: the workstation's place given back,
/// the end recorded in the security log with its reason, and nothing of the session left behind.
/// </summary>
public sealed class SessionEnding(WebSessionStore store, WebWorkstations workstations, ILogger<SessionEnding> logger)
{
    /// <param name="services">A scope the session is attached to: the record names its operator.</param>
    /// <param name="reason">Logout for a sign-out or a revoked account, SessionTimeout for the idle lock.</param>
    public async Task EndAsync(IServiceProvider services, WebSession session, SecurityEventKind reason, string? details)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(session);

        // First: from here on no request and no tab finds the session.
        store.Remove(session.Id);

        await workstations.ReleaseAsync(session.Workstation);

        try
        {
            await services.GetRequiredService<IAuthenticationService>().SignOutAsync(reason, details);
        }
        catch (Exception ex)
        {
            // The session ends regardless; a missing record is logged, not a reason to stay open.
            logger.LogError(ex, "The end of the web session of {User} could not be recorded.", session.Session.UserName);
        }
    }
}
