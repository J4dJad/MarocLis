using System.Net;
using MarocLIS.Api.Endpoints;
using MarocLIS.Api.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MarocLIS.Api;

public static class ApiApplicationBuilderExtensions
{
    /// <summary>
    /// The request pipeline, in an order that is part of the security design: the origin of the
    /// connection is noted before the forwarded headers rewrite it, the device is named before
    /// anything can write an audit record, the client header is checked before a session is
    /// restored, and the session is re-checked (in the cookie's validation) before any endpoint.
    /// </summary>
    public static WebApplication UseMarocLisApi(this WebApplication app)
    {
        app.Use(RequestPipeline.RecordOrigin);
        app.UseForwardedHeaders();
        app.Use(RequestPipeline.ProtectResponses);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.Use(RequestPipeline.NameDevice);
        app.Use(RequestPipeline.RequireClientHeader);

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapSessionEndpoints();
        app.MapValidationEndpoints();
        app.MapCriticalEndpoints();
        app.MapReportEndpoints();

        // An /api route that matched nothing is a 404, not the application's page.
        app.Map("/api/{**rest}", () => Results.NotFound());

        // The phone's application: the MarocLIS.Mobile Blazor WebAssembly client, its framework
        // files (_framework/) and its own files (index.html, CSS, fonts, icons, service worker).
        // Any other path that is not a file is one of its screens (/dossier/…), answered with
        // index.html so a reload or a home-screen shortcut lands on the application.
        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = RequestPipeline.CacheShell });
        app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = RequestPipeline.CacheShell });

        return app;
    }

    /// <summary>
    /// Logs a warning, at start-up, for every address the API listens on beyond the loopback.
    /// <para>
    /// Not a refusal: a laboratory may have a reason. But the API speaks plain HTTP and believes
    /// the Tailscale identity headers from the loopback only; listening on the LAN puts passwords
    /// and patient data on the network in clear, which nobody should discover from an incident.
    /// </para>
    /// </summary>
    public static WebApplication WarnIfReachableBeyondLoopback(this WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

            foreach (var address in addresses)
            {
                if (!Uri.TryCreate(address.Replace("://*", "://0.0.0.0", StringComparison.Ordinal)
                                          .Replace("://+", "://0.0.0.0", StringComparison.Ordinal),
                                   UriKind.Absolute, out var uri))
                    continue;

                var loopback = uri.IsLoopback
                               || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

                if (!loopback)
                    app.Logger.LogWarning(
                        "MarocLIS.Api listens on {Address}, beyond the loopback. It is meant to be reached " +
                        "through `tailscale serve` only; see the administration manual.", address);
            }
        });

        return app;
    }
}
