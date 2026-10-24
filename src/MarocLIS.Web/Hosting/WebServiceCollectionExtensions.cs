using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Licensing;
using MarocLIS.Infrastructure.Queries;
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
        services.AddSingleton<WebLicenceRefresher>();
        services.AddHostedService(sp => sp.GetRequiredService<WebLicenceRefresher>());

        // ---- Reporting: the workstation's renderer, so a document printed from the web is the
        // same document. The screens that print bring their renderers as they arrive.
        var reporting = configuration.GetSection(ReportingOptions.SectionName).Get<ReportingOptions>()
                        ?? new ReportingOptions();
        services.AddSingleton(reporting);
        services.AddSingleton<IReportRenderer, QuestPdfReportRenderer>();
        services.AddSingleton<IFontAvailability, FontAvailability>();
        // The fiche de paillasse of « Saisie des résultats », downloaded rather than printed.
        services.AddSingleton<IBenchWorksheetRenderer, QuestPdfBenchWorksheetRenderer>();
        // The invoice and the payment receipt of « Facturation », downloaded rather than printed.
        services.AddSingleton<IInvoiceRenderer, QuestPdfInvoiceRenderer>();

        // ---- The read side of the screens: the workstation's queries, one per scope. The
        // workstation isolates each call on a context of its own (IsolatedQueries); a page here runs
        // each operation on a scope of its own instead (UserScopes.RunAsync), which is the same.
        services.AddScoped<IResultQueries, ResultQueries>();
        services.AddScoped<IReportQueries, ReportQueries>();
        services.AddScoped<IPatientQueries, PatientQueries>();
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<IOrderQueries, OrderQueries>();
        services.AddScoped<IOrderEntryQueries, OrderEntryQueries>();
        services.AddScoped<IOrderFileQueries, OrderFileQueries>();
        services.AddScoped<IOrderQuotes, OrderQuoteService>();
        services.AddScoped<ISampleQueries, SampleQueries>();
        services.AddScoped<IBillingQueries, BillingQueries>();
        // The invoice print settings and the discount ceiling of Paramètres › Factures.
        services.AddScoped<IInvoicePrintSettingsQueries, InvoicePrintSettingsQueries>();
        services.AddScoped<IMasterDataQueries, MasterDataQueries>();
        // The sample and result screens: the patients to call back, the files to enter and to
        // validate, the bench's pending work and the context of a file being entered.
        services.AddScoped<IRecollectionQueries, RecollectionQueries>();
        services.AddScoped<IValidationQueries, ValidationQueries>();
        services.AddScoped<IBenchWorksheetQueries, BenchWorksheetQueries>();
        services.AddScoped<IResultEntryContextQueries, ResultEntryContextQueries>();

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
