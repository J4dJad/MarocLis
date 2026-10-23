using MarocLIS.Application.Administration;
using MarocLIS.Application.Licensing;
using MarocLIS.Web.Session;

namespace MarocLIS.Web.Security;

/// <summary>The gates every request passes after the network filter, in the order the pipeline installs them.</summary>
public static class WebGates
{
    /// <summary>
    /// The language of a page before sign-in: the one the browser chose, among those the laboratory
    /// works in; the default otherwise. A signed-in request takes its session's in the cookie check.
    /// </summary>
    public static Task ChooseLanguage(HttpContext context, Func<Task> next)
    {
        var services = context.RequestServices;
        var active = services.GetRequiredService<ActiveLanguagesCache>().Current;
        var session = services.GetRequiredService<SessionContext>();

        session.UseLanguage(active.Coerce(WebEndpoints.ChosenLanguage(context, active) ?? session.Language));
        return next();
    }

    /// <summary>
    /// The licence opens the web interface or nothing does: without a licence, without its « Version
    /// Web » module, or before it was evaluated, every page is the licence's message — the files it
    /// needs excepted — and a tab already open is refused its connection.
    /// </summary>
    public static Task RequireLicence(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path;
        if (WebPaths.IsAsset(path) || path.StartsWithSegments(WebPaths.Licence, StringComparison.OrdinalIgnoreCase))
            return next();

        if (context.RequestServices.GetRequiredService<ILicenceState>().RequireWebAccess().IsSuccess)
            return next();

        if (WebPaths.IsCircuit(path) || !HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(WebPaths.Licence);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing opens before the password change the core asked for at sign-in — no page, no tab —
    /// but the change itself, signing out and the language.
    /// </summary>
    public static Task RequirePasswordChange(HttpContext context, Func<Task> next)
    {
        var session = context.RequestServices.GetRequiredService<SessionContext>().Session;
        var path = context.Request.Path;

        if (session is not { PasswordChangeRequired: true }
            || WebPaths.IsAsset(path)
            || path.StartsWithSegments(WebPaths.PasswordChange, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(WebPaths.SignOut, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(WebPaths.Language, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(WebPaths.Licence, StringComparison.OrdinalIgnoreCase))
            return next();

        if (WebPaths.IsCircuit(path) || !HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(WebPaths.PasswordChange);
        return Task.CompletedTask;
    }
}
