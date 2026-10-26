using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Migration;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Licensing;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Instruments;
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
        // The lists of the stock screens — the consumption report, the count sheet and its
        // variances, a storage unit's monthly temperature sheet — downloaded rather than opened.
        services.AddSingleton<IListPdfRenderer, ListPdfRenderer>();
        // The purchase order of « Bons de commande », downloaded to be printed or sent, and e-mailed
        // to the supplier through the laboratory's mail account, as at the workstation.
        services.AddSingleton<Application.Inventory.IPurchaseOrderRenderer, QuestPdfPurchaseOrderRenderer>();

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
        // The administration screens: the accounts, roles and sites, the audit trail, the tills to
        // sign off and the storage places of « Paramètres ».
        services.AddScoped<IAdministrationQueries, AdministrationQueries>();
        services.AddScoped<IAuditQueries, AuditQueries>();
        services.AddScoped<Application.Treasury.ITreasuryQueries, TreasuryQueries>();
        services.AddScoped<ISampleStorageLocationQueries, SampleStorageLocationQueries>();
        // « Catalogue » and « Référentiels »: the analyses and what hangs from them, the national
        // nomenclature of acts and its gaps, and the consumption a stock article's reorder point is
        // proposed from.
        services.AddScoped<Application.Catalog.ICatalogMaintenanceQueries, CatalogMaintenanceQueries>();
        services.AddScoped<Application.Catalog.INationalActQueries, NationalActQueries>();
        services.AddScoped<Application.Inventory.IStockPlanningQueries, StockPlanningQueries>();
        // « Interfaces automates »: the links, their codes and their message log; the control
        // targets a replayed transmission's control results are checked against.
        services.AddScoped<Application.Instruments.IInstrumentInterfaceQueries, InstrumentInterfaceQueries>();
        services.AddScoped<Application.Quality.IQualityQueries, QualityQueries>();
        // « Contrôle qualité »: the reagent lots a control run can be recorded against (a new lot
        // under evaluation, or the lot in use).
        services.AddScoped<Application.Inventory.IPurchasingQualityQueries, PurchasingQualityQueries>();
        // « Évaluation externe de la qualité » and « Documents qualité »: the rounds and their
        // participations, the controlled documents, their versions and their files.
        services.AddScoped<Application.Quality.IEqaQueries, EqaQueries>();
        services.AddScoped<Application.Quality.IDocumentControlQueries, DocumentControlQueries>();
        // « Stock », « Consommation », « Inventaires » and « Températures »: the articles and their
        // lots, the open purchase orders a reorder draft goes on, the counts and their sheets, the
        // storage units and their readings.
        services.AddScoped<Application.Inventory.IInventoryQueries, InventoryQueries>();
        services.AddScoped<Application.Inventory.IPurchaseOrderQueries, PurchaseOrderQueries>();
        services.AddScoped<Application.Inventory.IInventoryCountQueries, InventoryCountQueries>();
        services.AddScoped<Application.Inventory.ITemperatureQueries, TemperatureQueries>();
        // « Bons de commande », « Demandes de devis », « Factures fournisseurs » and « Évaluation
        // fournisseurs »: the order's document, the requests and their offers, the invoices, their
        // matching and their payments.
        services.AddScoped<Application.Inventory.IPurchaseOrderDocumentQueries, PurchaseOrderDocumentQueries>();
        services.AddScoped<Application.Inventory.IQuoteQueries, QuoteQueries>();
        services.AddScoped<Application.Inventory.ISupplierInvoiceQueries, SupplierInvoiceQueries>();
        // « Contrats automates »: the dashboard of the contracts, their interventions and the editor's choices.
        services.AddScoped<Application.Inventory.IInstrumentContractQueries, InstrumentContractQueries>();
        // « Export comptable », « Comptes et journaux » and « Immobilisations »: the entries of the
        // period across the sites, the settings of the export, the register and its instruments.
        services.AddScoped<Application.Accounting.IAccountingQueries, AccountingQueries>();
        services.AddScoped<Application.Assets.IFixedAssetQueries, FixedAssetQueries>();
        // « Coût de revient et marge »: the tests of the period, their reagents and the analysers' contracts.
        // (« Rapprochement bancaire » and « Prévision de trésorerie » read ITreasuryQueries, registered above.)
        services.AddScoped<Application.Steering.ICostQueries, CostQueries>();
        // « Tableau de bord », « Statistiques d'activité » and « Pilotage »: the day's figures of the
        // site, the period's activity and money, and the period across the sites with its
        // receivables, payroll and quality indicators.
        services.AddScoped<Application.Dashboard.IDashboardQueries, DashboardQueries>();
        services.AddScoped<Application.Statistics.IStatisticsQueries, StatisticsQueries>();
        services.AddScoped<Application.Steering.ISteeringQueries, SteeringQueries>();

        // ---- « Interfaces automates »: configuring the links, replaying a held transmission and
        // trying a frame, through the workstation's services. Never the host: the web server opens
        // no serial port and no listener, the analysers being driven by one Windows workstation.
        services.AddInstrumentConfiguration(configuration);

        // ---- « Reprise de données »: the profiles of the server, one JSON file each, kept
        // without a password (the web reads uploaded files, never an old application's database).
        services.AddSingleton(new MigrationProfileStore(options.EffectiveMigrationProfilesFolder));

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
