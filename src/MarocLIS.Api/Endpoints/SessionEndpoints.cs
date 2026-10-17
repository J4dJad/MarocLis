using MarocLIS.Api.Contracts;
using MarocLIS.Api.Security;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using IAuthenticationService = MarocLIS.Application.Abstractions.IAuthenticationService;

namespace MarocLIS.Api.Endpoints;

/// <summary>Signing in and out on the phone.</summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var session = app.MapGroup(ApiProtocol.SessionRoute);

        session.MapPost("", SignInAsync).AllowAnonymous();
        session.MapGet("", Current).RequireAuthorization();
        session.MapDelete("", SignOutAsync).RequireAuthorization();
    }

    /// <summary>
    /// Signs a biologist in through the same service as the workstation's login screen: the same
    /// password check, the same lockout after five failures, the same security log — recorded
    /// against the phone. Then two conditions of its own: the account must hold both the remote
    /// validation permission and the biological validation one (a phone that cannot validate has
    /// nothing to do here), and must not owe a password change, which only the workstation offers.
    /// </summary>
    private static async Task<IResult> SignInAsync(
        SignInRequest request, HttpContext http, IAuthenticationService authentication,
        ISecurityAuditor securityAuditor, MobileSessionStore store, CancellationToken ct)
    {
        var signedIn = await authentication.SignInAsync(request.UserName ?? string.Empty,
                                                        request.Password ?? string.Empty, null, ct);

        if (signedIn.IsFailure)
            return ApiProblems.From(http, signedIn.Errors, StatusCodes.Status401Unauthorized);

        var session = signedIn.Value;

        // The same sentence for either missing permission: which one it is, the administrator can
        // read in the security log.
        Error? refusal =
            !session.Permissions.Contains(Permissions.Results.ValidateRemote)
            || !session.Permissions.Contains(Permissions.Results.BiologicalValidate)
            || session.IsPrescriberScoped
                ? Error.Forbidden("Mobile_RemoteValidationNotGranted")
                : session.MustChangePassword
                    ? Error.Forbidden("Mobile_PasswordChangeRequired")
                    : null;

        if (refusal is not null)
        {
            // The request's operator was set by the sign-in; this scope ends with the response, so
            // nothing of the session outlives the refusal.
            await securityAuditor.LogAsync(SecurityEventKind.AuthorizationDenied, false, session.UserName,
                                           refusal.ResourceKey, details: "Mobile sign-in refused", ct: ct);
            return ApiProblems.From(http, refusal, StatusCodes.Status403Forbidden);
        }

        var opened = store.Open(session, RequestPipeline.WorkstationOf(http));

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                               SessionCookie.PrincipalFor(opened),
                               new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });

        return Results.Ok(Describe(session, store, opened.Workstation));
    }

    private static IResult Current(ICurrentUser user, MobileSessionStore store) =>
        Results.Ok(new SessionInfo(user.UserName, user.FullName, user.Qualification.ToString(), user.SiteCode,
                                   user.Language.ToString(), (int)store.IdleTimeout.TotalMinutes, user.Workstation));

    private static async Task<IResult> SignOutAsync(HttpContext http, IAuthenticationService authentication,
                                                    MobileSessionStore store, CancellationToken ct)
    {
        store.Remove(SessionCookie.SessionIdOf(http.User));
        await authentication.SignOutAsync(SecurityEventKind.Logout, "Mobile sign-out", ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static SessionInfo Describe(AuthenticatedSession session, MobileSessionStore store, string workstation) =>
        new(session.UserName, session.FullName, session.Qualification.ToString(), session.SiteCode,
            session.Language.ToString(), (int)store.IdleTimeout.TotalMinutes, workstation);
}
