using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Licensing;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using MarocLIS.Web.Security;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;

namespace MarocLIS.Web.Hosting;

public static class WebServiceCollectionExtensions
{
    /// <summary>
    /// Wires the web interface on the same application and infrastructure registrations as the
    /// workstation — the same use cases, rules, rights, audit, licence and database settings — with
    /// the one difference a server needs: the operator is per scope, a scope being one request or
    /// one circuit (<see cref="PerRequestIdentityServiceCollectionExtensions.UsePerRequestCurrentUser"/>),
    /// never shared between two browsers.
    /// </summary>
    public static IServiceCollection AddMarocLisWeb(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
        services.AddSingleton(options);
        services.AddSingleton(NetworkAccess.From(options));

        var language = Enum.TryParse<AppLanguage>(configuration["Application:DefaultLanguage"], out var parsed)
            ? parsed
            : AppLanguage.French;
        services.AddSingleton(new WebDefaults(language));

        services.AddMarocLisLocalization(language);
        services.AddMarocLisInfrastructure(configuration);
        services.UsePerRequestCurrentUser();
        services.AddMarocLisApplication(configuration);

        // ---- The licence: the same evaluation as the workstation's, refreshed in the background.
        // The web interface opens only with the « Version Web » module (WebGates.RequireLicence),
        // and each browser signed in takes a workstation's place (WebWorkstations).
        services.AddMarocLisLicensing();
        services.AddHostedService<WebLicenceRefresher>();

        // ---- Reporting: the workstation's renderer, so a document printed from the web is the
        // same document. The screens that print bring their renderers as they arrive.
        var reporting = configuration.GetSection(ReportingOptions.SectionName).Get<ReportingOptions>()
                        ?? new ReportingOptions();
        services.AddSingleton(reporting);
        services.AddSingleton<IReportRenderer, QuestPdfReportRenderer>();
        services.AddSingleton<IFontAvailability, FontAvailability>();

        // ---- Sessions: the browsers signed in, their workstations, and each scope's session.
        services.AddSingleton<WebSessionStore>();
        services.AddSingleton<WebWorkstations>();
        services.AddSingleton<SessionEnding>();
        services.AddScoped<SessionContext>();
        services.AddScoped<WebTexts>();
        services.AddScoped<UserScopes>();
        services.AddScoped<SessionMonitor>();
        services.AddScoped<WebSignIn>();
        services.AddScoped<CircuitHandler, SessionCircuitHandler>();

        // The cookie's keys live in memory and die with the process: the sessions they protect do
        // too (WebSessionStore), and there is then no key ring on the server's disk to steal.
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(WebSessionCookie.Configure);
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = "MarocLIS.Formulaire";
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddRazorComponents()
                .AddInteractiveServerComponents();

        return services;
    }
}
