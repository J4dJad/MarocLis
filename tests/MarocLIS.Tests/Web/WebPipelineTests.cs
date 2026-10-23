using System.Net;
using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Licensing;
using MarocLIS.Localization;
using MarocLIS.Web.Hosting;
using MarocLIS.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The web interface's own gates, in its real pipeline started in memory: the local network only,
/// and the licence's « Version Web » module before anything else.
/// </summary>
public sealed class WebPipelineTests
{
    // ---- The network filter, as a rule -------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("100.100.1.2", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void TheDefaultNetworks_AreTheLocalOnes(string address, bool allowed)
    {
        var access = NetworkAccess.From(new WebOptions());

        access.IsAllowed(IPAddress.Parse(address)).Should().Be(allowed);
    }

    [Fact]
    public void TheNetworks_AreTheConfiguredOnes_WhenConfigured_AndAWrongRangeStopsTheStart()
    {
        var access = NetworkAccess.From(new WebOptions { AllowedNetworks = ["192.168.10.0/24"] });

        access.IsAllowed(IPAddress.Parse("192.168.10.7")).Should().BeTrue();
        access.IsAllowed(IPAddress.Parse("192.168.11.7")).Should().BeFalse("only the laboratory's subnet was written");
        access.IsAllowed(IPAddress.Parse("127.0.0.1")).Should().BeFalse();
        access.IsAllowed(null).Should().BeFalse();

        var wrong = () => new NetworkAccess(["192.168.1.0/33"]);
        wrong.Should().Throw<FormatException>().WithMessage("*192.168.1.0/33*");
    }

    // ---- The pipeline ------------------------------------------------------------------------------

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:203.0.113.9")]
    public async Task AnAddressOutsideTheLocalNetwork_GetsA403_ForEveryPath(string peer)
    {
        using var web = new WebFactory(FullStatus());
        using var client = web.Client(peer);

        foreach (var path in new[] { "/", "/connexion", "/css/app.css", "/_blazor/negotiate", "/ecran/billing" })
        {
            var response = await client.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            response.Headers.Contains("Set-Cookie").Should().BeFalse("nothing behind the filter has run");
            (await response.Content.ReadAsStringAsync()).Should().Contain("réseau local");
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    public async Task AnAddressOfTheLocalNetwork_ReachesTheSignIn(string peer)
    {
        using var web = new WebFactory(FullStatus());
        using var client = web.Client(peer);

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        new Uri(new Uri("http://localhost"), response.Headers.Location!).AbsolutePath.Should().Be("/connexion");
    }

    [Fact]
    public async Task WithoutTheWebModule_EveryPageIsTheLicencesMessage_AndATabIsRefusedItsConnection()
    {
        var withoutWeb = FullStatus() with
        {
            Modules = new HashSet<string>(LicenceModules.All.Where(m => m != LicenceModules.Web), StringComparer.Ordinal)
        };
        using var web = new WebFactory(withoutWeb);
        using var client = web.Client("192.168.1.20");

        foreach (var path in new[] { "/", "/connexion", "/ecran/billing", "/mot-de-passe" })
        {
            var page = await client.GetAsync(path);
            page.StatusCode.Should().Be(HttpStatusCode.Redirect, path);
            page.Headers.Location!.OriginalString.Should().Be("/licence", path);
        }

        (await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/connexion", new FormUrlEncodedContent([]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WithoutALicence_ThePageSaysToImportItFromTheWindowsApplication()
    {
        var missing = new LicenceStatus { State = LicenceState.Missing, Mode = LicenceMode.ReadOnly, Today = Today };
        using var web = new WebFactory(missing);
        using var client = web.Client("127.0.0.1");

        var redirect = await client.GetAsync("/");
        redirect.Headers.Location!.OriginalString.Should().Be("/licence");

        var page = await client.GetAsync("/licence");
        var html = await page.Content.ReadAsStringAsync();

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("data-state=\"licence\"");
        html.Should().Contain(WebHtml(web.Services.GetRequiredService<ILocalizer>().Get("Licence_WebNoLicence", AppLanguage.French)));
    }

    [Fact]
    public void TheWebGate_IsTheModule_OrTheMissingLicencesOwnMessage()
    {
        FullStatus().RequireWebAccess().IsSuccess.Should().BeTrue();
        ReadOnlyStatus().RequireWebAccess().IsSuccess.Should().BeTrue("read-only is the licence's own limit, enforced by the core");

        var withoutWeb = FullStatus() with { Modules = new HashSet<string>([LicenceModules.Quality], StringComparer.Ordinal) };
        withoutWeb.RequireWebAccess().FirstError.ResourceKey.Should().Be("Licence_ModuleMissing");

        new LicenceStatus { State = LicenceState.Missing, Today = Today }.RequireWebAccess().FirstError.ResourceKey
            .Should().Be("Licence_WebNoLicence");
        LicenceStatus.Pending.RequireWebAccess().FirstError.ResourceKey.Should().Be("Licence_WebPending");
    }

    private static string WebHtml(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    /// <summary>
    /// The web interface on no database: the pages reached here need none. The test server has no
    /// TCP peer, so a filter gives each request the address the client names.
    /// </summary>
    private sealed class WebFactory(LicenceStatus licence) : WebApplicationFactory<MarocLIS.Web.Program>
    {
        private const string PeerHeader = "X-Test-Peer";

        public HttpClient Client(string peer)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
            client.DefaultRequestHeaders.Add(PeerHeader, peer);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Web:InitializeDatabase", "false");
            builder.UseSetting("Database:ConnectionString",
                               "Server=127.0.0.1,1;Database=Aucune;User Id=x;Password=x;Connect Timeout=1;TrustServerCertificate=true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, PeerAddress>();
                services.RemoveAll<ILicenceState>();
                services.AddSingleton<ILicenceState>(new FixedLicenceState(licence));
            });
        }

        private sealed class PeerAddress : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, following) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[PeerHeader].ToString());
                    return following(context);
                });
                next(app);
            };
        }
    }
}
