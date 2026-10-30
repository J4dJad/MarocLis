using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Localization;
using MarocLIS.Web.Components;
using MarocLIS.Web.Security;

namespace MarocLIS.Web.Hosting;

public static class WebApplicationExtensions
{
    /// <summary>
    /// The request pipeline, in an order that is part of the security design: the network filter
    /// before anything else; the browser named before anything can write an audit record; the
    /// licence before the sign-in; the session restored and re-checked (in the cookie's validation)
    /// before any page; the mandatory password change before any other page.
    /// </summary>
    public static WebApplication UseMarocLisWeb(this WebApplication app)
    {
        var network = app.Services.GetRequiredService<NetworkAccess>();
        app.Use(NetworkAccess.Filter(network, app.Services.GetRequiredService<ILocalizer>(),
                                     app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<NetworkAccess>()));

        app.Use(ProtectResponses);

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/erreur", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.Use(BrowserIdentity.Identify);
        app.Use(WebGates.ChooseLanguage);
        app.Use(WebGates.RequireLicence);

        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(WebGates.RequirePasswordChange);

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapWebEndpoints();
        app.MapRazorComponents<App>()
           .AddInteractiveServerRenderMode();

        return app;
    }

    /// <summary>
    /// The policy of every page. The only frame and the only embedded object a page may show are
    /// documents it built itself in the browser (blob:), such as the scanned prescription shown in
    /// « Dossier »: a document built so inherits this policy, and the browser's PDF viewer is an
    /// embedded object. Nothing may frame the application itself.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; frame-src blob:; object-src blob:; base-uri 'self'; " +
        "form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// What a page may load and run: its own files and its circuit, nothing from another origin —
    /// the web interface must work inside the laboratory's network with nothing fetched from the
    /// Internet. Inline styles are allowed: Blazor's reconnection dialog sets its own. Nothing a
    /// page shows may be kept by a cache once the operator has signed out.
    /// </summary>
    private static Task ProtectResponses(HttpContext context, Func<Task> next)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            if (!WebPaths.IsAsset(context.Request.Path))
            {
                headers.CacheControl = "no-store";
                headers.Pragma = "no-cache";
            }

            return Task.CompletedTask;
        });

        return next();
    }

    /// <summary>
    /// Creates or migrates the database and seeds what the application cannot start without — the
    /// first administrator among it — as each Windows workstation does at its start
    /// (<see cref="DatabaseInitializer"/>). A failure is logged and the server keeps running: its
    /// pages then say the licence could not be checked, which sends the administrator to the
    /// database, and a database started after the web service is found at the next evaluation.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<WebOptions>();
        if (!options.InitializeDatabase) return;

        var seedDemo = app.Configuration.GetValue<bool>("Application:SeedDemoDataOnFirstRun");

        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(seedDemo);
            if (result.IsFailure)
                app.Logger.LogError("The database could not be initialised: {Reason}.",
                                    string.Join(", ", result.Errors.Select(e => e.ResourceKey)));
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "The database could not be initialised.");
        }
    }
}
