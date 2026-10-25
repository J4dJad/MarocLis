using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Catalogue » and « Données de référence » rendered in memory: the workstation's
/// CatalogMaintenanceService, NationalActService, MasterDataService and StockPlanningService over
/// fakes of their queries and repositories, the unit of work kept so a test can read the reason
/// written to the audit trail.
/// </summary>
public abstract class WebReferenceContext : WebScreenContext
{
    protected readonly Fake<ICatalogMaintenanceQueries> CatalogQueries = Fake<ICatalogMaintenanceQueries>.Create();
    protected readonly Fake<INationalActQueries> ActQueries = Fake<INationalActQueries>.Create();
    protected readonly Fake<IStockPlanningQueries> Planning = Fake<IStockPlanningQueries>.Create();
    protected readonly Fake<IConfigurationWriter> Writer = Fake<IConfigurationWriter>.Create();
    protected readonly Fake<IUnitOfWork> Work = Fake<IUnitOfWork>.Create();

    protected readonly Fake<IRepository<AnalysisTest>> AnalysisStore = Fake<IRepository<AnalysisTest>>.Create();
    protected readonly Fake<IRepository<Department>> DepartmentStore = Fake<IRepository<Department>>.Create();
    protected readonly Fake<IRepository<Instrument>> InstrumentStore = Fake<IRepository<Instrument>>.Create();
    protected readonly Fake<IRepository<Prescriber>> PrescriberStore = Fake<IRepository<Prescriber>>.Create();
    protected readonly Fake<IRepository<Payer>> PayerStore = Fake<IRepository<Payer>>.Create();

    protected WebReferenceContext()
    {
        Services.AddSingleton(CatalogQueries.Object);
        Services.AddSingleton(ActQueries.Object);
        Services.AddSingleton(Planning.Object);
        Services.AddSingleton(Writer.Object);
        Services.AddSingleton(Work.Object);

        Services.AddSingleton(AnalysisStore.Object);
        Services.AddSingleton(DepartmentStore.Object);
        Services.AddSingleton(InstrumentStore.Object);
        Services.AddSingleton(PrescriberStore.Object);
        Services.AddSingleton(PayerStore.Object);
        Services.AddSingleton(Fake<IRepository<TestPanel>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<ReflexRule>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<SpecimenType>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<ContainerType>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<MeasurementUnit>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<QcMaterial>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<QcTarget>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<StockItem>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<Supplier>>.Create().Object);

        Services.AddScoped<CatalogMaintenanceService>();
        Services.AddScoped<NationalActService>();
        Services.AddScoped<MasterDataService>();
        Services.AddScoped<StockPlanningService>();
    }

    /// <summary>The reason the unit of work was given for the audit trail, if any.</summary>
    protected string? AuditReason => Work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    /// <summary>Who maintains the catalogue: the analyses, their intervals and their prices.</summary>
    protected static readonly string[] CatalogManager =
    [
        Permissions.Catalog.View, Permissions.Catalog.Manage, Permissions.Catalog.ManageReferenceRanges, Permissions.Billing.ManageTariffs
    ];
}
