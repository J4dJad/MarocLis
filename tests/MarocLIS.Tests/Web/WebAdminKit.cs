using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Licensing;
using MarocLIS.Application.Samples;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Samples;
using Microsoft.Extensions.DependencyInjection;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Utilisateurs », « Journal d'audit », « Paramètres » and « Visa des caisses » rendered in
/// memory: the workstation's services over fakes of their queries, repositories and engines, the
/// security log kept by the context's auditor.
/// </summary>
public abstract class WebAdminContext : WebScreenContext
{
    protected readonly Fake<IAdministrationQueries> Admin = Fake<IAdministrationQueries>.Create();
    protected readonly Fake<IAuditQueries> AuditQueries = Fake<IAuditQueries>.Create();
    protected readonly Fake<ITreasuryQueries> Treasury = Fake<ITreasuryQueries>.Create();
    protected readonly Fake<ISampleStorageLocationQueries> Places = Fake<ISampleStorageLocationQueries>.Create();
    protected readonly Fake<IInvoicePrintSettingsQueries> PrintQueries = Fake<IInvoicePrintSettingsQueries>.Create();
    protected readonly Fake<ILanguageSettingsQueries> LanguageQueries = Fake<ILanguageSettingsQueries>.Create();
    protected readonly Fake<IDatabaseBackupEngine> Engine = Fake<IDatabaseBackupEngine>.Create();
    protected readonly Fake<IWorkstationRegistry> Registry = Fake<IWorkstationRegistry>.Create();
    protected readonly Fake<IConfigurationWriter> Writer = Fake<IConfigurationWriter>.Create();
    protected readonly Fake<IRepository<AppUser>> UserStore = Fake<IRepository<AppUser>>.Create();
    protected readonly Fake<IRepository<Role>> RoleStore = Fake<IRepository<Role>>.Create();
    protected readonly Fake<IRepository<LabSite>> SiteStore = Fake<IRepository<LabSite>>.Create();
    protected readonly Fake<IRepository<CashSession>> TillStore = Fake<IRepository<CashSession>>.Create();
    protected readonly Fake<IRepository<SampleStorageLocation>> PlaceStore = Fake<IRepository<SampleStorageLocation>>.Create();
    protected readonly Fake<IUnitOfWork> Work = Fake<IUnitOfWork>.Create();
    private protected readonly Store LicenceStore = new();
    protected readonly LicenceStatusHolder Holder = new();

    protected WebAdminContext()
    {
        Holder.Publish(FullStatus());
        Services.AddSingleton(Holder);
        Services.AddSingleton<ILicenceState>(Holder);
        Services.AddSingleton(new ActiveLanguagesCache());
        Services.AddSingleton(new LicenceTrust());
        Services.AddSingleton<ILicenceStore>(LicenceStore);

        Services.AddSingleton(Admin.Object);
        Services.AddSingleton(AuditQueries.Object);
        Services.AddSingleton(Treasury.Object);
        Services.AddSingleton(Places.Object);
        Services.AddSingleton(PrintQueries.Object);
        Services.AddSingleton(LanguageQueries.Object);
        Services.AddSingleton(Engine.Object);
        Services.AddSingleton(Registry.Object);
        Services.AddSingleton(Writer.Object);
        Services.AddSingleton(UserStore.Object);
        Services.AddSingleton(RoleStore.Object);
        Services.AddSingleton(SiteStore.Object);
        Services.AddSingleton(TillStore.Object);
        Services.AddSingleton(PlaceStore.Object);
        Services.AddSingleton(Work.Object);
        Services.AddSingleton(Fake<IAuthenticationService>.Create().Object);
        Services.AddSingleton(Fake<IPasswordHasher>.Create().Object);

        Services.AddScoped<UserAdministrationService>();
        Services.AddScoped<AuditTrailService>();
        Services.AddScoped<CashSessionReviewService>();
        Services.AddScoped<ConfigurationService>();
        Services.AddScoped<SampleStorageLocationService>();
        Services.AddScoped<InvoicePrintSettingsService>();
        Services.AddScoped<ActiveLanguagesService>();
        Services.AddScoped<BackupService>();
        Services.AddScoped<LicenceService>();
        Services.AddScoped<WorkstationPresenceService>();
    }

    /// <summary>The security log the services wrote, as the context's auditor kept it.</summary>
    private protected Auditor SecurityLog => (Auditor)Services.GetRequiredService<ISecurityAuditor>();

    /// <summary>The administrator: the accounts, the roles, the settings, the backups and the licence.</summary>
    protected static readonly string[] Administrator =
    [
        Permissions.Administration.ViewUsers, Permissions.Administration.ManageUsers, Permissions.Administration.ManageRoles,
        Permissions.Administration.ViewAuditTrail, Permissions.Administration.ManageSites, Permissions.Administration.ManageSettings,
        Permissions.Administration.Backup, Permissions.Administration.License
    ];

    /// <summary>Whether the unit of work was given a reason for the audit trail, and which.</summary>
    protected string? AuditReason => Work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;
}
