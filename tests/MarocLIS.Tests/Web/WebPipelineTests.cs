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

    /// <summary>
    /// The server's public certificate, for the « Poste Web » setup of a workstation: served to the
    /// local network whatever the licence (the setup comes before any sign-in), never outside it, and
    /// a plain 404 while the installer has not exported it.
    /// </summary>
    [Fact]
    public async Task TheServersCertificate_IsServedToTheLocalNetwork_BeforeAnyLicenceOrSignIn()
    {
        var file = Path.Combine(Path.GetTempPath(), $"maroclis-cert-{Guid.NewGuid():N}.cer");
        byte[] der = [0x30, 0x82, 0x01, 0x0A, 0x02, 0x01, 0x01];
        await File.WriteAllBytesAsync(file, der);
        try
        {
            var missing = new LicenceStatus { State = LicenceState.Missing, Mode = LicenceMode.ReadOnly, Today = Today };
            using var web = new WebFactory(missing, file);

            using var lan = web.Client("192.168.1.20");
            var response = await lan.GetAsync(WebPaths.ServerCertificate);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/pkix-cert");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(der);

            var pem = await (await lan.GetAsync(WebPaths.ServerCertificatePem)).Content.ReadAsStringAsync();
            pem.Should().Be("-----BEGIN CERTIFICATE-----\r\n" + Convert.ToBase64String(der) + "\r\n-----END CERTIFICATE-----\r\n");

            using var outside = web.Client("8.8.8.8");
            (await outside.GetAsync(WebPaths.ServerCertificate)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            File.Delete(file);
        }

        using var notExported = new WebFactory(FullStatus(), Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.cer"));
        using var client = notExported.Client("127.0.0.1");
        (await client.GetAsync(WebPaths.ServerCertificate)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string WebHtml(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    /// <summary>
    /// The web interface on no database: the pages reached here need none. The test server has no
    /// TCP peer, so a filter gives each request the address the client names.
    /// </summary>
    /// <summary>
    /// What the patient and order screens ask for, in the web's own composition: a screen opened on a
    /// service that is not registered would fail on the operator's first click, not at start-up.
    /// </summary>
    [Fact]
    public void TheServicesOfThePatientAndOrderScreens_AreRegistered()
    {
        using var web = new WebFactory(FullStatus());
        using var scope = web.Services.CreateScope();

        foreach (var type in new[]
                 {
                     typeof(MarocLIS.Application.Patients.PatientRegistrationService),
                     typeof(MarocLIS.Application.Orders.OrderRegistrationService),
                     typeof(MarocLIS.Application.Orders.OrderAmendmentService),
                     typeof(MarocLIS.Application.Billing.BillingService),
                     typeof(MarocLIS.Application.Samples.SampleWorkflowService),
                     typeof(MarocLIS.Application.Reports.ReportReleaseService),
                     typeof(MarocLIS.Application.Administration.MasterDataService),
                     typeof(MarocLIS.Application.Administration.ActiveLanguagesCache),
                     typeof(MarocLIS.Application.Abstractions.IDataAccessAuditor),
                     typeof(MarocLIS.Infrastructure.Queries.IPatientQueries),
                     typeof(MarocLIS.Infrastructure.Queries.ICatalogQueries),
                     typeof(MarocLIS.Application.Orders.IOrderQueries),
                     typeof(MarocLIS.Application.Orders.IOrderEntryQueries),
                     typeof(MarocLIS.Application.Orders.IOrderFileQueries),
                     typeof(MarocLIS.Application.Orders.IOrderQuotes),
                     typeof(MarocLIS.Application.Administration.IMasterDataQueries),
                     typeof(MarocLIS.Web.Session.UserScopes)
                 })
            scope.ServiceProvider.GetService(type).Should().NotBeNull(type.Name);
    }

    /// <summary>The same for the sample and result screens: collection, reception, recollections, entry and validation.</summary>
    [Fact]
    public void TheServicesOfTheSampleAndResultScreens_AreRegistered()
    {
        using var web = new WebFactory(FullStatus());
        using var scope = web.Services.CreateScope();

        foreach (var type in new[]
                 {
                     typeof(MarocLIS.Application.Samples.SampleWorkflowService),
                     typeof(MarocLIS.Application.Samples.RecollectionService),
                     typeof(MarocLIS.Application.Results.ResultEntryService),
                     typeof(MarocLIS.Application.Results.ResultEntryContextService),
                     typeof(MarocLIS.Application.Results.BenchWorksheetService),
                     typeof(MarocLIS.Application.Results.ValidationService),
                     typeof(MarocLIS.Application.Reports.ReportReleaseService),
                     typeof(MarocLIS.Application.Samples.ISampleQueries),
                     typeof(MarocLIS.Application.Samples.IRecollectionQueries),
                     typeof(MarocLIS.Application.Results.IResultQueries),
                     typeof(MarocLIS.Application.Results.IValidationQueries),
                     typeof(MarocLIS.Application.Results.IBenchWorksheetQueries),
                     typeof(MarocLIS.Application.Results.IResultEntryContextQueries),
                     typeof(MarocLIS.Application.Results.IBenchWorksheetRenderer)
                 })
            scope.ServiceProvider.GetService(type).Should().NotBeNull(type.Name);
    }

    /// <summary>The same for the report, billing, cash and claim screens, and the invoice's renderer.</summary>
    [Fact]
    public void TheServicesOfTheReportAndBillingScreens_AreRegistered()
    {
        using var web = new WebFactory(FullStatus());
        using var scope = web.Services.CreateScope();

        foreach (var type in new[]
                 {
                     typeof(MarocLIS.Application.Reports.ReportReleaseService),
                     typeof(MarocLIS.Application.Billing.BillingService),
                     typeof(MarocLIS.Application.Billing.InvoicePrintSettingsService),
                     typeof(MarocLIS.Application.Billing.IInvoiceRenderer),
                     typeof(MarocLIS.Application.Billing.IInvoicePrintSettingsQueries),
                     typeof(MarocLIS.Application.Billing.IBillingQueries),
                     typeof(MarocLIS.Application.Reports.IReportQueries),
                     typeof(MarocLIS.Application.Administration.ActiveLanguagesCache)
                 })
            scope.ServiceProvider.GetService(type).Should().NotBeNull(type.Name);
    }

    internal sealed class WebFactory(LicenceStatus licence, string? serverCertificateFile = null) : WebApplicationFactory<MarocLIS.Web.Program>
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
            if (serverCertificateFile is not null) builder.UseSetting("Web:ServerCertificateFile", serverCertificateFile);
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
