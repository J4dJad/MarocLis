using MarocLIS.Api.Endpoints;
using MarocLIS.Api.Security;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace MarocLIS.Api;

public static class ApiServiceCollectionExtensions
{
    /// <summary>
    /// Wires the API on the same application and infrastructure registrations as the workstation —
    /// the same use cases, rules, audit and database settings — with one difference that matters:
    /// the operator is per request (<see cref="PerRequestIdentityServiceCollectionExtensions.UsePerRequestCurrentUser"/>).
    /// </summary>
    public static IServiceCollection AddMarocLisApi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(MobileApiOptions.SectionName).Get<MobileApiOptions>()
                      ?? new MobileApiOptions();
        services.AddSingleton(options);

        services.AddMarocLisLocalization(options.DefaultLanguage);
        services.AddMarocLisInfrastructure(configuration);
        services.UsePerRequestCurrentUser();
        services.AddMarocLisApplication(configuration);

        // ---- Reporting: the workstation's renderer, so a report issued from the phone is the
        // same document, fonts and licence included.
        var reporting = configuration.GetSection(ReportingOptions.SectionName).Get<ReportingOptions>()
                        ?? new ReportingOptions();
        services.AddSingleton(reporting);
        services.AddSingleton<IReportRenderer, QuestPdfReportRenderer>();
        services.AddSingleton<IFontAvailability, FontAvailability>();

        // ---- Read-side queries the validation screens use. Plainly scoped: the workstation runs
        // each call on its own context because a screen's handlers overlap; a request is one
        // sequence of awaits on one context, so it needs no such isolation.
        services.AddScoped<IResultQueries, ResultQueries>();
        services.AddScoped<IValidationQueries, ValidationQueries>();
        services.AddScoped<IReportQueries, ReportQueries>();

        // ---- Sessions
        services.AddSingleton<MobileSessionStore>();
        services.AddMemoryCache();
        services.AddSingleton<ReportPreviews>();

        // The cookie's keys live in memory and die with the process: the sessions they protect do
        // too (MobileSessionStore), and there is then no key ring on the server's disk to steal.
        var cookieKeys = new EphemeralDataProtectionProvider();

        var policy = configuration.GetSection("PasswordPolicy").Get<PasswordPolicy>() ?? new PasswordPolicy();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(cookie =>
                {
                    SessionCookie.Configure(cookie, policy);
                    cookie.DataProtectionProvider = cookieKeys;
                });
        services.AddAuthorization();

        // `tailscale serve` proxies from the loopback and says, in X-Forwarded-Proto, that the phone
        // came over HTTPS — which is what marks the cookie Secure. The defaults trust those headers
        // from the loopback only, which is exactly the proxy this API is meant to sit behind.
        services.Configure<ForwardedHeadersOptions>(forwarded =>
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        services.AddProblemDetails();

        return services;
    }
}
