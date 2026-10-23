using MarocLIS.Application.Administration;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using IAuthenticationService = MarocLIS.Application.Abstractions.IAuthenticationService;

namespace MarocLIS.Web.Security;

/// <summary>What a sign-in came to.</summary>
/// <param name="Error">The refusal, in the session's language, as the Windows login shows it.</param>
/// <param name="Workstations">The workstations signed in, when the licence's count refused this browser.</param>
/// <param name="Redirect">Where the browser goes once signed in.</param>
public sealed record WebSignInOutcome(string? Error, WorkstationClaim? Workstations, string? Redirect)
{
    public bool Succeeded => Redirect is not null;
}

/// <summary>
/// Signs an operator in from the browser, in the order and with the services of the Windows login
/// (<c>LoginForm.SignInAsync</c>): the same password check, lockout and security log; the account's
/// language taking over, French when the laboratory switched it off; a place among the licence's
/// workstations for this browser, refused like a workstation one too many; the password change the
/// core asks for before anything else opens; and the licence's notice, once.
/// </summary>
public sealed class WebSignIn(
    IAuthenticationService authentication,
    IActiveLanguages languages,
    ILicenceState licence,
    WebSessionStore store,
    WebWorkstations workstations,
    WebTexts texts,
    IServiceProvider services)
{
    public async Task<WebSignInOutcome> SignInAsync(HttpContext http, string? userName, string? password, Guid? siteId,
                                                    string? returnUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        var active = await languages.GetAsync(ct);

        var result = await authentication.SignInAsync((userName ?? string.Empty).Trim(), password ?? string.Empty,
                                                      siteId, ct);
        if (result.IsFailure) return new WebSignInOutcome(texts.Describe(result.Errors), null, null);

        var session = result.Value;
        var workstation = BrowserIdentity.WorkstationOf(http);

        // A place among the licence's workstations, once the operator is known: their name is what
        // the workstation one too many is shown in the list.
        var claim = await workstations.ClaimAsync(services, workstation, session.UserName, ct);
        if (claim is { Granted: false })
        {
            await authentication.SignOutAsync(ct: ct);
            return new WebSignInOutcome(texts.T("Licence.Workstations.Reached", claim.InUse, claim.Limit), claim, null);
        }

        var opened = store.Open(session, workstation,
                                BrowserIdentity.AuditName(workstation, http.Connection.RemoteIpAddress),
                                active.Coerce(session.Language),
                                LicenceNavigation.SignInNoticeKey(licence.Current) is not null);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                               WebSessionCookie.PrincipalFor(opened),
                               new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });

        return new WebSignInOutcome(null, null,
                                    opened.PasswordChangeRequired ? WebPaths.PasswordChange : WebPaths.LocalOrHome(returnUrl));
    }
}
