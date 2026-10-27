using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MarocLIS.Web.Security;

/// <summary>Signing out, and the choice of language: two forms every page can post.</summary>
public static class WebEndpoints
{
    public const string LanguageCookie = "MarocLIS.Langue";

    /// <summary>The reasons the sign-in page may be told; anything else in the address is ignored.</summary>
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "Message.SessionExpired", "Auth_SessionRevoked", "Login.PasswordExpiredMessage"
    };

    public static void MapWebEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(WebPaths.SignOut, SignOutAsync).AllowAnonymous();
        app.MapPost(WebPaths.Language, ChooseLanguageAsync).AllowAnonymous();
    }

    /// <summary>
    /// Signs out (« Déconnexion »), or ends a session whose operator declined the mandatory password
    /// change — as the Windows login ends it — and returns to the sign-in page.
    /// </summary>
    private static async Task<IResult> SignOutAsync(HttpContext http, SessionContext context, SessionEnding ending)
    {
        if (!IsSameOrigin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);

        var form = await http.Request.ReadFormAsync();
        var reason = form["motif"].ToString();

        if (context.Session is { } session)
            await ending.EndAsync(http.RequestServices, session, SecurityEventKind.Logout,
                                  reason.Length > 0 ? "Web: password change declined" : null);

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        return Results.LocalRedirect(Reasons.Contains(reason)
            ? $"{WebSessionCookie.LoginPath}?{WebSessionCookie.ReasonParameter}={Uri.EscapeDataString(reason)}"
            : WebSessionCookie.LoginPath);
    }

    /// <summary>
    /// The language of the interface: the browser's from now on, and the session's when signed in.
    /// Only a language the laboratory works in; the page is reloaded, mirrored for Arabic.
    /// </summary>
    private static async Task<IResult> ChooseLanguageAsync(HttpContext http, SessionContext context,
                                                           WebSessionStore store, IActiveLanguages languages)
    {
        if (!IsSameOrigin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);

        var form = await http.Request.ReadFormAsync();
        var back = WebPaths.LocalOrHome(form["retour"].ToString());

        if (!Enum.TryParse<AppLanguage>(form["langue"].ToString(), ignoreCase: false, out var language)
            || !Enum.IsDefined(language)
            || !(await languages.GetAsync(http.RequestAborted)).IsActive(language))
            return Results.LocalRedirect(back);

        http.Response.Cookies.Append(LanguageCookie, language.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365)
        });

        // Only the session, as the shell's picker: the account's preferred language is set in the
        // user administration, and an operator may read the web in Arabic and the bench in French.
        if (context.Session is { } session) store.Update(session.Id, s => s with { Language = language });

        return Results.LocalRedirect(back);
    }

    /// <summary>
    /// These forms are posted by the application's own pages only. The cookies are SameSite=Strict,
    /// so another site's form would arrive signed out anyway; the browser's own word on the origin
    /// (Sec-Fetch-Site, else Origin) closes the rest, as an antiforgery token would.
    /// </summary>
    public static bool IsSameOrigin(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var site = http.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0) return site is "same-origin" or "none";

        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && string.Equals(uri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The browser's chosen language, if it chose one the laboratory works in.</summary>
    public static AppLanguage? ChosenLanguage(HttpContext http, LanguageSettings active)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(active);

        return LanguageSettings.Parse(http.Request.Cookies[LanguageCookie]) is { } language && active.IsActive(language)
            ? language
            : null;
    }
}
