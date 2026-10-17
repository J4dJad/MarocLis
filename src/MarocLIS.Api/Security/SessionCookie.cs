using System.Security.Claims;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using IAuthenticationService = MarocLIS.Application.Abstractions.IAuthenticationService;

namespace MarocLIS.Api.Security;

/// <summary>
/// The phone's session cookie, and the check that runs on every request carrying it.
/// <para>
/// The cookie is HttpOnly (no script on the page can read it), SameSite=Strict (no other site can
/// make the browser send it), Secure whenever the request arrived over HTTPS — which, behind
/// <c>tailscale serve</c>, the forwarded scheme says it did — and slides with activity over the
/// password policy's idle timeout, the workstation's lock delay.
/// </para>
/// <para>
/// It holds a random session identifier and nothing else. On each request the session is found in
/// <see cref="MobileSessionStore"/>, replayed into the request's operator, and checked against the
/// account by <see cref="ISessionValidator"/> — the check the desktop shell runs every minute. An
/// account deactivated, a password changed or reset, a role or a permission withdrawn: the very
/// next tap on the phone is refused and the session is gone.
/// </para>
/// </summary>
public static class SessionCookie
{
    public const string Name = "MarocLIS.Mobile";

    /// <summary>The claim carrying the session identifier.</summary>
    public const string SessionClaim = "maroclis:session";

    private const string EndReasonKey = "MarocLIS.SessionEndReason";

    public static void Configure(CookieAuthenticationOptions options, PasswordPolicy policy)
    {
        options.Cookie.Name = Name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = policy.IdleTimeout;
        options.SlidingExpiration = true;

        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = ValidateAsync,

            // An API answers 401 and 403; it does not redirect to a login page.
            OnRedirectToLogin = context => ApiProblems.WriteAsync(
                context.HttpContext,
                Error.Forbidden(context.HttpContext.Items[EndReasonKey] as string ?? "Message.SessionExpired"),
                StatusCodes.Status401Unauthorized),
            OnRedirectToAccessDenied = context => ApiProblems.WriteAsync(
                context.HttpContext, Error.Forbidden("Message.PermissionDenied"), StatusCodes.Status403Forbidden)
        };
    }

    public static ClaimsPrincipal PrincipalFor(MobileSession session) =>
        new(new ClaimsIdentity([new Claim(SessionClaim, session.Id)], CookieAuthenticationDefaults.AuthenticationScheme));

    public static string? SessionIdOf(ClaimsPrincipal? principal) => principal?.FindFirst(SessionClaim)?.Value;

    /// <summary>Restores and re-checks the session behind the cookie, or ends it.</summary>
    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var store = services.GetRequiredService<MobileSessionStore>();
        var auditor = services.GetRequiredService<ISecurityAuditor>();

        var (session, end) = store.Find(SessionIdOf(context.Principal));

        if (session is null || end != SessionEnd.None)
        {
            // An idle session is recorded as the desktop records its idle lock. One unknown here was
            // already ended (signed out, or by the service restarting) and was recorded then.
            if (session is not null)
                await auditor.LogAsync(end == SessionEnd.Idle ? SecurityEventKind.SessionTimeout : SecurityEventKind.Logout,
                                       true, session.Session.UserName,
                                       details: end == SessionEnd.Idle
                                           ? "Mobile session ended: idle timeout"
                                           : "Mobile session ended: maximum lifetime reached");

            await RejectAsync(context, "Message.SessionExpired");
            return;
        }

        // The cookie belongs to the device that signed in. Presented by another one — copied off
        // the phone — it opens nothing, and the session it belonged to is ended as compromised.
        if (!string.Equals(session.Workstation, RequestPipeline.WorkstationOf(http), StringComparison.Ordinal))
        {
            store.Remove(session.Id);
            await auditor.LogAsync(SecurityEventKind.AuthorizationDenied, false, session.Session.UserName,
                                   "Mobile_DeviceMismatch",
                                   details: $"Session of {session.Workstation} presented by {RequestPipeline.WorkstationOf(http)}");
            await RejectAsync(context, "Mobile_DeviceMismatch");
            return;
        }

        services.GetRequiredService<RequestSession>().Restore(session.Session);

        if (!await services.GetRequiredService<ISessionValidator>().IsStillValidAsync(http.RequestAborted))
        {
            store.Remove(session.Id);

            // Signed out as the shell signs out a revoked session: recorded, then cleared.
            await services.GetRequiredService<IAuthenticationService>()
                .SignOutAsync(SecurityEventKind.Logout, "Mobile session ended: account changed since sign-in",
                              http.RequestAborted);

            await RejectAsync(context, "Auth_SessionRevoked");
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context, string reasonKey)
    {
        context.HttpContext.Items[EndReasonKey] = reasonKey;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
