using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Forms;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Desktop.Composition;

public static class DesktopServiceCollectionExtensions
{
    public static IServiceCollection AddMarocLisDesktop(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Reporting -------------------------------------------------------------------------

        var reportingOptions = configuration.GetSection(ReportingOptions.SectionName)
                                            .Get<ReportingOptions>() ?? new ReportingOptions();
        services.AddSingleton(reportingOptions);

        var labelOptions = configuration.GetSection(LabelOptions.SectionName)
                                        .Get<LabelOptions>() ?? new LabelOptions();
        services.AddSingleton(labelOptions);

        // Singletons: the renderers are stateless after font registration, and registering fonts
        // per report would be wasteful on a reception workstation printing continuously.
        services.AddSingleton<IReportRenderer, QuestPdfReportRenderer>();
        services.AddSingleton<IListPdfRenderer, ListPdfRenderer>();
        services.AddSingleton<IInvoiceRenderer, QuestPdfInvoiceRenderer>();
        services.AddSingleton<Application.HumanResources.IPayslipRenderer, QuestPdfPayslipRenderer>();
        services.AddSingleton<QuestPdfHrDocumentRenderer>();
        services.AddSingleton<Application.HumanResources.IHrDocumentRenderer>(sp => sp.GetRequiredService<QuestPdfHrDocumentRenderer>());
        services.AddSingleton<Application.HumanResources.IAccidentRenderer>(sp => sp.GetRequiredService<QuestPdfHrDocumentRenderer>());
        services.AddSingleton<IBenchWorksheetRenderer, QuestPdfBenchWorksheetRenderer>();
        services.AddSingleton<Application.Inventory.IPurchaseOrderRenderer, QuestPdfPurchaseOrderRenderer>();

        // Resolved once: the installed font set does not change while the application runs, and the
        // report screen would otherwise pay to enumerate it on every release.
        services.AddSingleton<IFontAvailability, FontAvailability>();
        services.AddSingleton<ISampleLabelRenderer, SampleLabelRenderer>();

        // Shares the Labels configuration section with LabelOptions: the geometry and the printer
        // that geometry is cut for are one decision, and splitting them across two sections is how
        // they end up disagreeing.
        var labelPrintOptions = configuration.GetSection(LabelPrintOptions.SectionName)
                                              .Get<LabelPrintOptions>() ?? new LabelPrintOptions();

        services.AddSingleton(labelPrintOptions);
        services.AddSingleton<ILabelPrinter, LabelPrinter>();

        // Per-workstation choice of the A4 and label printers; overrides Labels:LabelPrinterName.
        services.AddSingleton<WorkstationPrinters>();

        // Singleton and disposable: it owns one folder for the life of the process and deletes it on
        // exit. Registered as the concrete type as well so the host disposes it — resolving only the
        // interface would still dispose it, but naming both makes the ownership explicit.
        services.AddSingleton<SessionDocumentOutput>();
        services.AddSingleton<IDocumentOutput>(sp => sp.GetRequiredService<SessionDocumentOutput>());

        // ---- Read-side queries -----------------------------------------------------------------

        services.AddIsolatedQueries<IResultQueries, ResultQueries>();
        services.AddIsolatedQueries<IValidationQueries, ValidationQueries>();
        services.AddIsolatedQueries<IBenchWorksheetQueries, BenchWorksheetQueries>();
        services.AddIsolatedQueries<IResultEntryContextQueries, ResultEntryContextQueries>();
        services.AddIsolatedQueries<IReportQueries, ReportQueries>();
        services.AddIsolatedQueries<IDashboardQueries, DashboardQueries>();
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<Application.Orders.IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<Application.Orders.IOrderEntryQueries, OrderEntryQueries>();
        services.AddIsolatedQueries<Application.Orders.IOrderFileQueries, OrderFileQueries>();
        // The counter's running quote: the registration's own pricing calls, on a context of their own.
        services.AddIsolatedQueries<Application.Orders.IOrderQuotes, Application.Orders.OrderQuoteService>();
        services.AddIsolatedQueries<Application.Catalog.ICatalogMaintenanceQueries, CatalogMaintenanceQueries>();
        services.AddIsolatedQueries<Application.Catalog.INationalActQueries, NationalActQueries>();
        services.AddIsolatedQueries<Application.Instruments.IInstrumentInterfaceQueries, InstrumentInterfaceQueries>();
        services.AddIsolatedQueries<Application.Samples.ISampleQueries, SampleQueries>();
        services.AddIsolatedQueries<Application.Samples.ISampleStorageLocationQueries, SampleStorageLocationQueries>();
        services.AddIsolatedQueries<Application.Samples.ISubcontractQueries, SubcontractQueries>();
        services.AddIsolatedQueries<Application.Samples.IRecollectionQueries, RecollectionQueries>();
        services.AddIsolatedQueries<Application.Billing.IBillingQueries, BillingQueries>();
        services.AddIsolatedQueries<Application.Billing.IInvoicePrintSettingsQueries, InvoicePrintSettingsQueries>();
        services.AddIsolatedQueries<Application.Quality.IQualityQueries, QualityQueries>();
        services.AddIsolatedQueries<Application.Inventory.IInventoryQueries, InventoryQueries>();
        services.AddIsolatedQueries<Application.HumanResources.IPersonnelQueries, PersonnelQueries>();
        services.AddIsolatedQueries<Application.HumanResources.ILeaveQueries, LeaveQueries>();
        services.AddIsolatedQueries<Application.HumanResources.IPlanningQueries, PlanningQueries>();
        services.AddIsolatedQueries<Application.HumanResources.IPayrollQueries, PayrollQueries>();
        services.AddIsolatedQueries<Application.HumanResources.IHrDocumentQueries, HrDocumentQueries>();
        services.AddIsolatedQueries<Application.HumanResources.IAccidentQueries, AccidentQueries>();
        services.AddIsolatedQueries<Application.Inventory.IPurchaseOrderQueries, PurchaseOrderQueries>();
        services.AddIsolatedQueries<Application.Inventory.IPurchaseOrderDocumentQueries, PurchaseOrderDocumentQueries>();
        services.AddIsolatedQueries<Application.Inventory.IQuoteQueries, QuoteQueries>();
        services.AddIsolatedQueries<Application.Inventory.IInstrumentContractQueries, InstrumentContractQueries>();
        services.AddIsolatedQueries<Application.Inventory.ISupplierInvoiceQueries, SupplierInvoiceQueries>();
        services.AddIsolatedQueries<Application.Accounting.IAccountingQueries, AccountingQueries>();
        services.AddIsolatedQueries<Application.Inventory.IPurchasingQualityQueries, PurchasingQualityQueries>();
        services.AddIsolatedQueries<Application.Inventory.IStockPlanningQueries, StockPlanningQueries>();
        services.AddIsolatedQueries<Application.Inventory.IInventoryCountQueries, InventoryCountQueries>();
        services.AddIsolatedQueries<Application.Inventory.ITemperatureQueries, TemperatureQueries>();
        services.AddIsolatedQueries<Application.Quality.IDocumentControlQueries, DocumentControlQueries>();
        services.AddIsolatedQueries<Application.Quality.IEqaQueries, EqaQueries>();
        services.AddIsolatedQueries<Application.Statistics.IStatisticsQueries, StatisticsQueries>();
        services.AddIsolatedQueries<Application.Relations.IPrescriberStatisticsQueries, PrescriberStatisticsQueries>();
        services.AddIsolatedQueries<Application.Relations.IRelationsQueries, RelationsQueries>();
        services.AddIsolatedQueries<Application.Relations.IPatientMessagingQueries, PatientMessagingQueries>();
        services.AddIsolatedQueries<Application.Steering.ICostQueries, CostQueries>();
        services.AddIsolatedQueries<Application.Steering.ISteeringQueries, SteeringQueries>();
        services.AddIsolatedQueries<Application.Administration.IAdministrationQueries, AdministrationQueries>();
        services.AddIsolatedQueries<Application.Administration.IMasterDataQueries, MasterDataQueries>();
        services.AddIsolatedQueries<Application.Administration.IAuditQueries, AuditQueries>();
        services.AddIsolatedQueries<Application.Assets.IFixedAssetQueries, FixedAssetQueries>();
        services.AddIsolatedQueries<Application.Treasury.ITreasuryQueries, TreasuryQueries>();

        // ---- Shell and dialogs -----------------------------------------------------------------

        // Transient so a screen reopened after an error starts from a clean state rather than
        // inheriting the half-populated one that failed.
        services.AddTransient<LoginForm>();
        services.AddTransient<ChangePasswordForm>();
        services.AddSingleton<MainShellForm>();

        services.AddTransient<DashboardView>();
        services.AddTransient<PatientSearchView>();
        services.AddTransient<OrderRegistrationView>();
        services.AddTransient<OrderDetailView>();
        services.AddTransient<CollectionView>();
        services.AddTransient<SampleReceptionView>();
        services.AddTransient<SampleStorageView>();
        services.AddTransient<ResultEntryView>();
        services.AddTransient<SubcontractingView>();
        services.AddTransient<RecollectionView>();
        services.AddTransient<ValidationView>();
        services.AddTransient<ReportRegisterView>();
        services.AddTransient<BillingView>();
        services.AddTransient<CashSessionView>();
        services.AddTransient<ClaimBatchView>();
        services.AddTransient<QualityControlView>();
        services.AddTransient<NonConformityView>();
        services.AddTransient<InventoryView>();
        services.AddTransient<EquipmentView>();
        services.AddTransient<PersonnelView>();
        services.AddTransient<HabilitationView>();
        services.AddTransient<PersonnelDeadlinesView>();
        services.AddTransient<LeaveView>();
        services.AddTransient<PlanningView>();
        services.AddTransient<PayrollView>();
        services.AddTransient<AccidentView>();
        services.AddTransient<PurchaseOrderView>();
        services.AddTransient<QuoteRequestView>();
        services.AddTransient<InstrumentContractView>();
        services.AddTransient<SupplierInvoiceView>();
        services.AddTransient<SupplierEvaluationView>();
        services.AddTransient<AccountingExportView>();
        services.AddTransient<AccountingSettingsView>();
        services.AddTransient<ConsumptionView>();
        services.AddTransient<InventoryCountView>();
        services.AddTransient<TemperatureView>();
        services.AddTransient<QualityDocumentsView>();
        services.AddTransient<EqaView>();
        services.AddTransient<StatisticsView>();
        services.AddTransient<SteeringView>();
        services.AddTransient<PrescriberStatisticsView>();
        services.AddTransient<RelationsView>();
        services.AddTransient<PatientMessagingView>();
        services.AddTransient<CostMarginView>();
        services.AddTransient<UserAdministrationView>();
        services.AddTransient<AuditTrailView>();
        services.AddTransient<DataMigrationView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<MasterDataView>();
        services.AddTransient<CatalogView>();
        services.AddTransient<BankReconciliationView>();
        services.AddTransient<CashForecastView>();
        services.AddTransient<CashSessionReviewView>();
        services.AddTransient<InstrumentInterfaceView>();
        services.AddTransient<FixedAssetView>();

        // Scoped, not singleton: it constructs a form over a scoped application service, and a
        // singleton would pin one change tracker for the life of the process.
        services.AddScoped<PatientEditorFactory>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUserDialogs, UserDialogs>();

        return services;
    }
}
