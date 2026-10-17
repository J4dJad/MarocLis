using System.Net;
using MarocLIS.Api.Contracts;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Security;

namespace MarocLIS.Api.Security;

/// <summary>
/// The steps every request goes through before an endpoint sees it, in the order
/// <see cref="ApiApplicationBuilderExtensions.UseMarocLisApi"/> installs them.
/// </summary>
public static class RequestPipeline
{
    private const string LoopbackKey = "MarocLIS.FromLoopback";
    private const string ConnectionKey = "MarocLIS.Connection";
    private const string WorkstationKey = "MarocLIS.Workstation";

    /// <summary>
    /// Notes whether the TCP connection comes from the server itself, before the forwarded headers
    /// replace the peer's address with the one <c>tailscale serve</c> reports. Only such a
    /// connection is believed about who is calling (<see cref="DeviceIdentity"/>).
    /// </summary>
    public static Task RecordOrigin(HttpContext context, Func<Task> next)
    {
        var peer = context.Connection.RemoteIpAddress;
        context.Items[ConnectionKey] = peer;
        context.Items[LoopbackKey] = peer is not null && IPAddress.IsLoopback(peer);
        return next();
    }

    /// <summary>
    /// Names the device for the request's audit records, before anything is resolved that could
    /// write one — the sign-in itself is recorded against it.
    /// </summary>
    public static Task NameDevice(HttpContext context, Func<Task> next)
    {
        var fromLoopback = context.Items[LoopbackKey] is true;
        var connection = context.Items[ConnectionKey] as IPAddress;
        var forwarded = context.Connection.RemoteIpAddress;

        var workstation = DeviceIdentity.Describe(
            fromLoopback,
            context.Request.Headers[DeviceIdentity.LoginHeader].ToString(),
            context.Request.Headers[DeviceIdentity.NameHeader].ToString(),
            fromLoopback && !Equals(forwarded, connection) ? forwarded : null,
            connection);

        context.Items[WorkstationKey] = workstation;
        context.RequestServices.GetRequiredService<RequestSession>().UseWorkstation(workstation);

        return next();
    }

    /// <summary>The device the request was attributed to by <see cref="NameDevice"/>.</summary>
    public static string WorkstationOf(HttpContext context) =>
        context.Items[WorkstationKey] as string ?? DeviceIdentity.Prefix + "?";

    /// <summary>
    /// Refuses a state-changing request that does not carry <see cref="ApiProtocol.RequestHeader"/>.
    /// See the header's documentation for why it, with the SameSite=Strict cookie, stands in for an
    /// antiforgery token: the phone's client is a single-page application with no server-rendered
    /// form to carry one, and the header achieves the same — no other origin can send it.
    /// </summary>
    public static async Task RequireClientHeader(HttpContext context, Func<Task> next)
    {
        if (IsApi(context) && !HttpMethods.IsGet(context.Request.Method)
                           && !HttpMethods.IsHead(context.Request.Method)
                           && !string.Equals(context.Request.Headers[ApiProtocol.RequestHeader].ToString(),
                                             ApiProtocol.RequestHeaderValue, StringComparison.Ordinal))
        {
            await ApiProblems.WriteAsync(context, Error.Forbidden("Mobile_ClientHeaderMissing"),
                                         StatusCodes.Status403Forbidden);
            return;
        }

        await next();
    }

    /// <summary>
    /// What the phone's page may load and run: its own files and nothing else.
    /// <para>
    /// <c>'wasm-unsafe-eval'</c> is the one allowance, and it is what Blazor WebAssembly needs to
    /// compile the .NET runtime; it permits WebAssembly compilation, not JavaScript <c>eval</c>. No
    /// inline script or style is allowed — the page has none (no import map: the framework files are
    /// not fingerprinted, see MarocLIS.Mobile.csproj) — so an injected script tag runs nowhere. No
    /// other origin is named: the fonts are served from here, and the application must work inside
    /// the laboratory's private network with nothing fetched from the Internet.
    /// </para>
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self'; " +
        "font-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// Nothing the API answers may be kept: by the phone's browser cache, by a proxy, or in the
    /// back-forward cache once the biologist has signed out. Every API answer carries patient data
    /// or is about to, so the rule is set once for all of them rather than endpoint by endpoint.
    /// </summary>
    public static Task ProtectResponses(HttpContext context, Func<Task> next)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            if (IsApi(context))
            {
                headers.CacheControl = "no-store";
                headers.Pragma = "no-cache";
            }

            return Task.CompletedTask;
        });

        return next();
    }

    /// <summary>
    /// The application's entry points — the page, the service worker and its list of files, the
    /// manifest — are checked with the server on every load (<c>no-cache</c>), so a new version of
    /// the application reaches the phones at their next opening rather than whenever a cache
    /// expires. The other files are named in that list with their hash and are re-fetched when it
    /// changes. None of them holds patient data.
    /// </summary>
    public static void CacheShell(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext context)
    {
        var name = context.File.Name;
        if (name is "index.html" or "service-worker.js" or "service-worker-assets.js" or "manifest.webmanifest")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }

    public static bool IsApi(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}
