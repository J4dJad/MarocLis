using System.Net;
using FluentAssertions;
using Xunit;

namespace MarocLIS.Tests.Api;

/// <summary>
/// The phone's application served by the API itself: the page at every address of the application,
/// the API's own 404 for an unknown /api route (never the page), the Content-Security-Policy, and a
/// service worker that keeps nothing of the API. No database: none of this reads one.
/// </summary>
public sealed class MobileHostingTests : IDisposable
{
    private readonly MobileApiFactory _api = new("Server=127.0.0.1,1;Database=Unused;Connect Timeout=1");

    public void Dispose() => _api.Dispose();

    private HttpClient Phone() => _api.CreateClient(new() { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/")]
    [InlineData("/connexion")]
    [InlineData("/dossier/xyz")]
    [InlineData("/dossier/7d2f0c4e-0000-4000-8000-000000000001/historique/7d2f0c4e-0000-4000-8000-000000000002")]
    public async Task EveryAddressOfTheApplication_IsTheApplicationsPage_NeverCachedAndUnderTheStrictPolicy(string path)
    {
        var response = await Phone().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("_framework/blazor.webassembly.js").And.Contain("manifest.webmanifest");
        html.Should().NotContain("<script>", "no inline script: the policy below would refuse it");
        html.Should().NotContain("fonts.googleapis", "the fonts are the application's own");

        response.Headers.CacheControl!.NoCache.Should().BeTrue("a new version is picked up at the next opening");
        var policy = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        policy.Should().Contain("script-src 'self' 'wasm-unsafe-eval'").And.Contain("default-src 'self'")
              .And.Contain("frame-ancestors 'none'").And.NotContain("unsafe-inline");
    }

    [Theory]
    [InlineData("/api/unknown")]
    [InlineData("/api/dossiers/xyz/rien")]
    [InlineData("/api")]
    public async Task AnUnknownApiRoute_IsTheApisJson404_NotThePage(string path)
    {
        var response = await Phone().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("<html");
    }

    [Fact]
    public async Task TheManifestAndTheFramework_AreServed_AndTheInstallableIconsExist()
    {
        var phone = Phone();

        var manifest = await phone.GetAsync("/manifest.webmanifest");
        manifest.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await manifest.Content.ReadAsStringAsync();
        text.Should().Contain("\"MarocLIS Validation\"").And.Contain("#0B6E79");

        foreach (var icon in new[] { "/icons/icon-192.png", "/icons/icon-512.png", "/icons/icon-180.png" })
            (await phone.GetAsync(icon)).StatusCode.Should().Be(HttpStatusCode.OK, icon);

        (await phone.GetAsync("/_framework/blazor.webassembly.js")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await phone.GetAsync("/fonts/ibm-plex-sans-arabic-arabic-400-normal.woff2")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The published service worker is the one a phone runs. It must never answer an /api request
    /// from its cache — a patient's file seen offline, or a stale value validated — nor keep one.
    /// </summary>
    [Fact]
    public void ThePublishedServiceWorker_LetsEveryApiRequestThrough_AndCachesNoneOfThem()
    {
        var worker = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MarocLIS.Mobile", "wwwroot", "service-worker.published.js"));

        worker.Should().Contain("url.pathname === '/api' || url.pathname.startsWith('/api/')");
        worker.Should().Contain("if (isApi(event.request)) return;", "an /api request is left to the network, untouched");
        worker.Should().Contain("/^api\\//", "the asset list excludes anything under api/");
        worker.Should().NotContain("cache.put", "only the shell, listed at build time, is ever cached");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarocLIS.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("MarocLIS.sln not found above the test binaries.");
    }
}
