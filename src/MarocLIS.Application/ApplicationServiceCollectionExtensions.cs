using MarocLIS.Application.Orders;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the use-case services. All scoped: each one works through a single
    /// <c>IUnitOfWork</c> and change tracker, and sharing those across screens would let one
    /// screen's abandoned edits be saved by another's commit.
    /// </summary>
    public static IServiceCollection AddMarocLisApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var registrationOptions =
            configuration.GetSection(OrderRegistrationOptions.SectionName)
                         .Get<OrderRegistrationOptions>() ?? new OrderRegistrationOptions();

        services.AddSingleton(registrationOptions);

        services.AddScoped<Patients.PatientRegistrationService>();
        services.AddScoped<OrderComposer>();
        services.AddScoped<OrderRegistrationService>();
        services.AddScoped<OrderAmendmentService>();
        services.AddScoped<Catalog.CatalogMaintenanceService>();
        services.AddScoped<Instruments.InstrumentInterfaceService>();
        services.AddScoped<Samples.SampleWorkflowService>();
        services.AddScoped<Samples.SubcontractingService>();
        services.AddScoped<Samples.RecollectionService>();
        services.AddScoped<ResultPostEntryPipeline>();
        services.AddScoped<ResultEntryService>();
        services.AddScoped<ValidationService>();
        services.AddScoped<BenchWorksheetService>();
        services.AddScoped<ReportReleaseService>();
        services.AddScoped<Billing.BillingService>();
        services.AddScoped<Quality.QualityControlService>();
        services.AddScoped<Quality.NonConformityService>();
        services.AddScoped<Quality.DocumentControlService>();
        services.AddScoped<Quality.EqaService>();
        services.AddScoped<Inventory.ReorderPlanner>();
        services.AddScoped<Inventory.InventoryService>();
        services.AddScoped<Inventory.EquipmentService>();
        services.AddScoped<HumanResources.PersonnelService>();
        services.AddScoped<HumanResources.LeaveService>();
        services.AddScoped<HumanResources.PlanningService>();
        services.AddScoped<HumanResources.PayrollService>();
        services.AddScoped<HumanResources.HrDocumentService>();
        services.AddScoped<HumanResources.SettlementService>();
        services.AddScoped<HumanResources.AccidentService>();
        services.AddScoped<Inventory.PurchaseOrderService>();
        services.AddScoped<Statistics.StatisticsService>();
        services.AddScoped<Administration.UserAdministrationService>();
        services.AddScoped<Administration.ConfigurationService>();
        services.AddScoped<Administration.MasterDataService>();
        services.AddScoped<Administration.AuditTrailService>();

        return services;
    }
}
