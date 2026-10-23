using System.Security.Claims;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Audit;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MarocLIS.Web.Security;

/// <summary>
/// The browser's session cookie, and the check that runs on every request carrying it — the
/// phone's (<c>MarocLIS.Api.Security.SessionCookie</c>) for a browser of the local network.
/// <para>
/// HttpOnly (no script on the page reads it), SameSite=Strict (no other site makes the browser send
/// it), Secure over HTTPS, and gone when the browser closes. It holds a random session identifier
/// and nothing else: the session is found in <see cref="WebSessionStore"/>, must belong to this
/// browser (<see cref="BrowserIdentity"/>), is replayed into the request's operator and is checked
/// against the account by <see cref="ISessionValidator"/>, as the Windows shell checks its own.
/// </para>
/// </summary>
public static class WebSessionCookie
{
    public const string Name = "MarocLIS.Web";

    /// <summary>The claim carrying the session identifier.</summary>
    public const string SessionClaim = "maroclis:web-session";

    public const string LoginPath = "/connexion";

    /// <summary>The query parameter that tells the sign-in page why the session ended.</summary>
    public const string ReasonParameter = "motif";

    /// <summary>
    /// How often a request re-checks the account behind its session. The shell checks every minute;
    /// a page load is a dozen requests in a second, and checking each against the database would
    /// tell nothing the first did not.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private const string EndReasonKey = "MarocLIS.Web.SessionEndReason";

    public static void Configure(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Cookie.Name = Name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;

        // The idle timeout is the store's, counted from what the operator does in the page — which
        // reaches the server over the circuit, never renewing a cookie. The ticket's own lifetime is
        // only an outer bound.
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;

        options.LoginPath = LoginPath;
        options.AccessDeniedPath = LoginPath;
        options.ReturnUrlParameter = "retour";

        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = ValidateAsync,
            OnRedirectToLogin = RedirectToLoginAsync,
            OnRedirectToAccessDenied = RedirectToLoginAsync
        };
    }

    public static ClaimsPrincipal PrincipalFor(WebSession session) =>
        new(new ClaimsIdentity([new Claim(SessionClaim, session.Id), new Claim(ClaimTypes.Name, session.Session.UserName)],
                               CookieAuthenticationDefaults.AuthenticationScheme));

    public static string? SessionIdOf(ClaimsPrincipal? principal) => principal?.FindFirst(SessionClaim)?.Value;

    /// <summary>Finds, matches, restores and re-checks the session behind the cookie, or ends it.</summary>
    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var store = services.GetRequiredService<WebSessionStore>();
        var auditor = services.GetRequiredService<ISecurityAuditor>();

        var (session, end) = store.Find(SessionIdOf(context.Principal));

        if (session is null || end != WebSessionEnd.None)
        {
            // An idle session is recorded as the shell records its idle lock — found here when the
            // browser was closed without signing out, so no tab ended it. One unknown was already
            // ended (signed out, or by a restart of the server) and was recorded then.
            if (session is not null)
            {
                await services.GetRequiredService<WebWorkstations>().ReleaseAsync(session.Workstation);
                await auditor.LogAsync(SecurityEventKind.SessionTimeout, true, session.Session.UserName,
                                       details: "Web session ended: idle timeout");
            }

            await RejectAsync(context, "Message.SessionExpired");
            return;
        }

        // The cookie belongs to the browser that signed in. Presented by another one — copied off
        // the first — it opens nothing, and the session is ended as compromised.
        if (!string.Equals(session.Workstation, BrowserIdentity.WorkstationOf(http), StringComparison.Ordinal))
        {
            store.Remove(session.Id);
            await services.GetRequiredService<WebWorkstations>().ReleaseAsync(session.Workstation);
            await auditor.LogAsync(SecurityEventKind.AuthorizationDenied, false, session.Session.UserName,
                                   "Web_DeviceMismatch",
                                   details: $"Session of {session.Workstation} presented by {BrowserIdentity.WorkstationOf(http)}");
            await RejectAsync(context, "Message.SessionExpired");
            return;
        }

        var scope = services.GetRequiredService<SessionContext>();
        scope.Attach(session);

        var now = DateTimeOffset.Now;
        if (now - session.LastCheckedAt < CheckInterval) return;

        bool valid;
        try
        {
            valid = await services.GetRequiredService<ISessionValidator>().IsStillValidAsync(http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // As at the workstation: a database out of reach does not sign everyone out.
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebSessionCookie))
                    .LogWarning(ex, "The web session could not be re-checked against the account.");
            return;
        }

        if (valid)
        {
            store.Update(session.Id, s => s with { LastCheckedAt = now });
            return;
        }

        // Ended as the shell ends a revoked session: the place given back, recorded, cleared.
        await services.GetRequiredService<SessionEnding>().EndAsync(
            services, session, SecurityEventKind.Logout, "Web session ended: account changed since sign-in");
        await RejectAsync(context, "Auth_SessionRevoked");
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context, string reasonKey)
    {
        context.HttpContext.Items[EndReasonKey] = reasonKey;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// To the sign-in page, saying why when a session just ended. The circuit's own connection is
    /// answered 401 rather than redirected: it is not a page, and the tab's monitor sends it away.
    /// </summary>
    private static Task RedirectToLoginAsync(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (WebPaths.IsCircuit(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        var target = context.RedirectUri;
        if (context.HttpContext.Items[EndReasonKey] is string reason)
            target += (target.Contains('?', StringComparison.Ordinal) ? "&" : "?") + ReasonParameter + "=" + Uri.EscapeDataString(reason);

        context.Response.Redirect(target);
        return Task.CompletedTask;
    }
}
