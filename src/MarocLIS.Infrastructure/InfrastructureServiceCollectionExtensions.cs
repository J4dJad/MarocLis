using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Persistence.Demo;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Infrastructure.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Connection string. On a laboratory LAN this points at the server's SQL Server Express
    /// instance, e.g. <c>Server=LABSRV\SQLEXPRESS;Database=MarocLIS;…</c>.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Seconds a command may run. Kept modest: a screen that hangs for two minutes on a locked row
    /// is worse for a reception desk than one that fails fast and can be retried.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Retries on a transient network fault, which a Wi-Fi collection centre will see.</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Logs SQL and parameter values. Never enable in production: parameters carry health data.</summary>
    public bool EnableSensitiveDataLogging { get; set; }

    public bool EnableDetailedErrors { get; set; }
}

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddMarocLisInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                      ?? new DatabaseOptions();

        services.AddSingleton(options);

        var policy = configuration.GetSection("PasswordPolicy").Get<PasswordPolicy>() ?? new PasswordPolicy();
        services.AddSingleton(policy);

        services.AddSingleton<IClock, SystemClock>();

        // One accessor instance serves both the interface and the concrete type: the authentication
        // service needs the setters, everything else must only read.
        services.AddSingleton<CurrentUserAccessor>();
        services.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserAccessor>());

        services.AddSingleton<IPasswordHasher>(_ => new BCryptPasswordHasher());

        var messaging = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()
                        ?? new MessagingOptions();
        services.AddSingleton(messaging);
        services.AddSingleton<IMessageSender, Messaging.MessageSender>();

        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<LisDbContext>((sp, builder) =>
        {
            builder.UseSqlServer(BuildConnectionString(options), sql =>
            {
                sql.CommandTimeout(options.CommandTimeoutSeconds);
                // Retries transient failures, but not inside the transactions the use cases open
                // around gapless numbering; see TransactionAwareExecutionStrategy.
                sql.ExecutionStrategy(d => new TransactionAwareExecutionStrategy(d, options.MaxRetryCount));
                sql.MigrationsHistoryTable("__MigrationsHistory", "cfg");
            });

            builder.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());

            if (options.EnableSensitiveDataLogging) builder.EnableSensitiveDataLogging();
            if (options.EnableDetailedErrors) builder.EnableDetailedErrors();

            // A desktop form holds its context for the life of a screen; no-tracking by default
            // would break the change tracking the audit interceptor depends on, so tracking stays
            // on and read-only queries opt out explicitly with AsNoTracking().
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISequenceAllocator, SequenceAllocator>();
        services.AddScoped<ISecurityAuditor, SecurityAuditor>();
        services.AddScoped<IDataAccessAuditor, DataAccessAuditor>();
        services.AddScoped<IPermissionGuard, PermissionGuard>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();

        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<LabOrderRepository>();
        services.AddScoped<PatientRepository>();
        services.AddScoped<SampleRepository>();
        services.AddScoped<AnalysisTestRepository>();
        services.AddScoped<AppUserRepository>();
        services.AddScoped<InvoiceRepository>();
        services.AddScoped<RoleRepository>();
        services.AddScoped<TestPanelRepository>();
        services.AddScoped<InstrumentInterfaceRepository>();
        services.AddScoped<StockItemRepository>();
        services.AddScoped<InstrumentRepository>();
        services.AddScoped<EmployeeRepository>();
        services.AddScoped<PayrollPeriodRepository>();
        services.AddScoped<QcResultRepository>();
        services.AddScoped<ControlledDocumentRepository>();
        services.AddScoped<PurchaseOrderRepository>();
        services.AddScoped<NonConformityRepository>();

        // The specialised repositories also satisfy the generic contract, so a use case can depend
        // on IRepository<LabOrder> and still get the detail-loading override.
        services.AddScoped<IRepository<LabOrder>>(sp => sp.GetRequiredService<LabOrderRepository>());
        services.AddScoped<IRepository<Patient>>(sp => sp.GetRequiredService<PatientRepository>());

        // The duplicate finder is the same repository behind a narrow interface, so the application
        // layer can run the pre-creation check without depending on this assembly.
        services.AddScoped<Application.Patients.IPatientDuplicateFinder>(
            sp => sp.GetRequiredService<PatientRepository>());

        services.AddScoped<IRepository<Sample>>(sp => sp.GetRequiredService<SampleRepository>());
        services.AddScoped<IRepository<AnalysisTest>>(sp => sp.GetRequiredService<AnalysisTestRepository>());
        services.AddScoped<IRepository<AppUser>>(sp => sp.GetRequiredService<AppUserRepository>());
        services.AddScoped<IRepository<Invoice>>(sp => sp.GetRequiredService<InvoiceRepository>());
        services.AddScoped<IRepository<Role>>(sp => sp.GetRequiredService<RoleRepository>());
        services.AddScoped<IRepository<TestPanel>>(sp => sp.GetRequiredService<TestPanelRepository>());
        services.AddScoped<IRepository<InstrumentInterface>>(
            sp => sp.GetRequiredService<InstrumentInterfaceRepository>());

        services.AddScoped<IInstrumentMessageLogStore, InstrumentMessageLogStore>();
        services.AddScoped<IRepository<StockItem>>(sp => sp.GetRequiredService<StockItemRepository>());
        services.AddScoped<IRepository<Instrument>>(sp => sp.GetRequiredService<InstrumentRepository>());
        services.AddScoped<IRepository<Domain.HumanResources.Employee>>(
            sp => sp.GetRequiredService<EmployeeRepository>());
        services.AddScoped<IRepository<Domain.HumanResources.PayrollPeriod>>(
            sp => sp.GetRequiredService<PayrollPeriodRepository>());
        services.AddScoped<IRepository<QcResult>>(sp => sp.GetRequiredService<QcResultRepository>());
        services.AddScoped<IRepository<ControlledDocument>>(
            sp => sp.GetRequiredService<ControlledDocumentRepository>());
        services.AddScoped<IRepository<PurchaseOrder>>(
            sp => sp.GetRequiredService<PurchaseOrderRepository>());
        services.AddScoped<IRepository<NonConformity>>(
            sp => sp.GetRequiredService<NonConformityRepository>());

        services.AddScoped<Application.Administration.IConfigurationWriter, ConfigurationWriter>();

        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<DemoDataSeeder>();

        return services;
    }

    /// <summary>
    /// Normalises the connection string and applies the settings a laboratory deployment needs
    /// whether or not whoever wrote the configuration file remembered them.
    /// </summary>
    private static string BuildConnectionString(DatabaseOptions options)
    {
        var builder = new SqlConnectionStringBuilder(options.ConnectionString)
        {
            // SQL Server Express on a laboratory server rarely has a trusted certificate. Encrypt
            // the channel but do not require a chain the site cannot supply — the alternative in
            // practice is an unencrypted connection, which is worse.
            Encrypt = true,
            TrustServerCertificate = true,

            ApplicationName = "MarocLIS",

            // Multi-subnet failover off, modest pool: a laboratory has tens of workstations, not
            // thousands, and Express caps at 1 GB of buffer pool anyway.
            MaxPoolSize = 100,
            MinPoolSize = 1,
            ConnectTimeout = 15
        };

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            builder.InitialCatalog = "MarocLIS";

        return builder.ConnectionString;
    }
}
