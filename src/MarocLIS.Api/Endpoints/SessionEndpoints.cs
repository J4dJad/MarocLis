using MarocLIS.Api.Contracts;
using MarocLIS.Api.Security;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
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
        app.MapPut(ApiProtocol.LanguageRoute, ChangeLanguageAsync).RequireAuthorization();
        app.MapGet(ApiProtocol.LanguagesRoute, LanguagesAsync).AllowAnonymous();
    }

    /// <summary>
    /// The languages the laboratory works in, for the sign-in screen's buttons. Anonymous: it is
    /// shown before anyone signs in, and says nothing about a patient or an account.
    /// </summary>
    private static async Task<IResult> LanguagesAsync(IActiveLanguages languages, CancellationToken ct) =>
        Results.Ok(new LanguagesInfo([.. (await languages.GetAsync(ct)).Active.Select(l => l.ToString())]));

    /// <summary>
    /// Signs a biologist in through the same service as the workstation's login screen: the same
    /// password check, the same lockout after five failures, the same security log — recorded
    /// against the phone. Then two conditions of its own: the account must hold both the remote
    /// validation permission and the biological validation one (a phone that cannot validate has
    /// nothing to do here), and must not owe a password change, which only the workstation offers.
    /// </summary>
    private static async Task<IResult> SignInAsync(
        SignInRequest request, HttpContext http, IAuthenticationService authentication,
        ISecurityAuditor securityAuditor, MobileSessionStore store, LisDbContext db, IActiveLanguages languages,
        CancellationToken ct)
    {
        // Read first: a refusal below is written in a language the laboratory works in.
        var active = await languages.GetAsync(ct);

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

        // An account whose language the laboratory has switched off works in French on the phone,
        // as it does at the bench.
        if (!active.IsActive(session.Language)) session = session with { Language = AppLanguage.French };

        var opened = store.Open(session, RequestPipeline.WorkstationOf(http));

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                               SessionCookie.PrincipalFor(opened),
                               new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });

        return Results.Ok(await DescribeAsync(opened, store, db, active, ct));
    }

    private static async Task<IResult> Current(HttpContext http, MobileSessionStore store, LisDbContext db,
                                               IActiveLanguages languages, CancellationToken ct)
    {
        var (session, _) = store.Find(SessionCookie.SessionIdOf(http.User));
        if (session is null)
            return ApiProblems.From(http, Error.Forbidden("Message.SessionExpired"), StatusCodes.Status401Unauthorized);

        // A language switched off since the sign-in: the phone, reopened, carries on in French.
        var active = await languages.GetAsync(ct);
        if (!active.IsActive(session.Session.Language))
            session = store.ChangeLanguage(session.Id, AppLanguage.French) ?? session;

        return Results.Ok(await DescribeAsync(session, store, db, active, ct));
    }

    /// <summary>
    /// Switches the session's language (« Mon compte »): what the server writes from the next
    /// request on — alert pills, analysis names, refusals — follows the phone's screen. Only to a
    /// language the laboratory works in.
    /// </summary>
    private static async Task<IResult> ChangeLanguageAsync(LanguageRequest request, HttpContext http,
                                                           MobileSessionStore store, LisDbContext db,
                                                           IActiveLanguages languages, CancellationToken ct)
    {
        var active = await languages.GetAsync(ct);

        var parsed = ParseLanguage(request.Language, active);
        if (parsed.IsFailure) return ApiProblems.From(http, parsed.Errors);

        var changed = store.ChangeLanguage(SessionCookie.SessionIdOf(http.User), parsed.Value);
        return changed is null
            ? ApiProblems.From(http, Error.Forbidden("Message.SessionExpired"), StatusCodes.Status401Unauthorized)
            : Results.Ok(await DescribeAsync(changed, store, db, active, ct));
    }

    /// <summary>
    /// The language a phone asks for: one of the three by its name, and one the laboratory works
    /// in — a language switched off is refused rather than quietly replaced, so the phone says so.
    /// </summary>
    public static Result<AppLanguage> ParseLanguage(string? name, LanguageSettings active)
    {
        ArgumentNullException.ThrowIfNull(active);

        if (!Enum.TryParse<AppLanguage>(name, ignoreCase: true, out var language) || !Enum.IsDefined(language))
            return Result.Failure<AppLanguage>(Error.Validation("Mobile_UnknownLanguage", name));

        return active.IsActive(language)
            ? Result.Success(language)
            : Result.Failure<AppLanguage>(Error.Validation("Mobile_LanguageNotActive"));
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, IAuthenticationService authentication,
                                                    MobileSessionStore store, CancellationToken ct)
    {
        store.Remove(SessionCookie.SessionIdOf(http.User));
        await authentication.SignOutAsync(SecurityEventKind.Logout, "Mobile sign-out", ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static async Task<SessionInfo> DescribeAsync(MobileSession opened, MobileSessionStore store, LisDbContext db,
                                                         LanguageSettings languages, CancellationToken ct)
    {
        var session = opened.Session;

        // The site's name for « Mon compte »; the code is enough to work with, but nobody calls
        // their laboratory « A ».
        var site = await db.Sites.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == session.SiteId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);

        return new SessionInfo(session.UserName, session.FullName, session.Qualification.ToString(), session.SiteCode,
                               session.Language.ToString(), (int)store.IdleTimeout.TotalMinutes, opened.Workstation,
                               site?.For(session.Language), opened.OpenedAt,
                               [.. languages.Active.Select(l => l.ToString())]);
    }
}
