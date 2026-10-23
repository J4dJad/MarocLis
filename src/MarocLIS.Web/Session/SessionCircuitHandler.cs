using MarocLIS.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MarocLIS.Web.Session;

/// <summary>
/// Gives each circuit — each browser tab — its session, before anything of the tab is rendered.
/// <para>
/// The circuit is authenticated by the cookie of the request that opened it, which the cookie's
/// check already matched with a live session and with this browser. Its session is attached to the
/// circuit's own scope here: from then on, every service the tab resolves works for this operator.
/// A circuit whose cookie names no live session gets none, and its pages send it to the sign-in.
/// </para>
/// </summary>
public sealed class SessionCircuitHandler(
    AuthenticationStateProvider authentication,
    WebSessionStore store,
    SessionContext context,
    WebWorkstations workstations) : CircuitHandler
{
    private string? _workstation;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        var (session, end) = store.Find(WebSessionCookie.SessionIdOf(state.User));

        if (session is null || end != WebSessionEnd.None || context.IsSignedIn) return;

        context.Attach(session);
        _workstation = session.Workstation;
        workstations.Enter(session.Workstation);
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_workstation is { } workstation)
        {
            _workstation = null;
            await workstations.LeaveAsync(workstation);
        }
    }

    /// <summary>
    /// Every click, key and navigation of the tab reaches the server through here: each one is the
    /// operator's activity, for the idle lock.
    /// </summary>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        activity =>
        {
            context.RecordActivity();
            return next(activity);
        };
}
