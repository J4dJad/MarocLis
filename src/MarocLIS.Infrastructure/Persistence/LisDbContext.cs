using System.Linq.Expressions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// The application's single EF Core context, mapped onto SQL Server Express.
/// <para>
/// Two cross-cutting behaviours are applied here rather than left to each query, because a query
/// that forgets them is a data-protection or a correctness incident:
/// </para>
/// <list type="bullet">
/// <item><b>Soft-delete filtering.</b> Every <see cref="ISoftDeletable"/> entity gets a global
/// filter, so deleted clinical records stay in the database for the retention period without
/// appearing in normal work.</item>
/// <item><b>Site scoping.</b> Every <see cref="ISiteScoped"/> entity is filtered to the sites the
/// signed-in operator may see. A collection centre's clerk cannot enumerate another site's
/// patients by opening a grid, which is what loi 08-09 expects of the controller.</item>
/// </list>
/// <para>
/// Both filters can be suspended deliberately — for a cross-site management report, or for the
/// retention purge — through <see cref="IgnoreSiteScope"/> and EF's own
/// <c>IgnoreQueryFilters()</c>. Making that explicit is the point: bypassing is visible in the
/// calling code.
/// </para>
/// </summary>
public class LisDbContext : DbContext
{
    /// <summary>
    /// One database operation at a time on this context. A screen keeps one context, and two of its
    /// handlers can overlap — a second click while a load is running, F5 during a save — which EF Core
    /// refuses with "a second operation was started on this context instance". The repositories and
    /// the unit of work queue behind this gate instead, so the second operation waits its turn.
    /// </summary>
    internal SemaphoreSlim OperationGate { get; } = new(1, 1);

    private readonly ICurrentUser? _currentUser;

    public LisDbContext(DbContextOptions<LisDbContext> options, ICurrentUser? currentUser = null)
        : base(options)
        => _currentUser = currentUser;

    /// <summary>
    /// Suspends the site filter for the lifetime of the context. Set by cross-site reports and by
    /// maintenance routines; never by an interactive screen.
    /// </summary>
    public bool IgnoreSiteScope { get; set; }

    /// <summary>Sites the current query may read. Empty means "no restriction applied".</summary>
    public IReadOnlyList<Guid> ScopedSiteIds =>
        IgnoreSiteScope || _currentUser is not { IsAuthenticated: true }
            ? []
            : _currentUser.AccessibleSiteIds;

    // ---- Organisation and identity ------------------------------------------------------------

    public DbSet<LabSite> Sites => Set<LabSite>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserSiteAccess> UserSiteAccess => Set<UserSiteAccess>();
    public DbSet<PasswordHistoryEntry> PasswordHistory => Set<PasswordHistoryEntry>();

    // ---- Catalogue ----------------------------------------------------------------------------

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<SpecimenType> SpecimenTypes => Set<SpecimenType>();
    public DbSet<ContainerType> ContainerTypes => Set<ContainerType>();
    public DbSet<MeasurementUnit> Units => Set<MeasurementUnit>();
    public DbSet<AnalysisTest> AnalysisTests => Set<AnalysisTest>();
    public DbSet<TestParameter> TestParameters => Set<TestParameter>();
    public DbSet<ReferenceRange> ReferenceRanges => Set<ReferenceRange>();
    public DbSet<CodedResultOption> CodedResultOptions => Set<CodedResultOption>();
    public DbSet<AnalysisTestTariff> Tariffs => Set<AnalysisTestTariff>();
    public DbSet<TestPanel> TestPanels => Set<TestPanel>();
    public DbSet<TestPanelItem> TestPanelItems => Set<TestPanelItem>();
    public DbSet<ReflexRule> ReflexRules => Set<ReflexRule>();

    // ---- Patients -----------------------------------------------------------------------------

    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientCoverage> PatientCoverages => Set<PatientCoverage>();
    public DbSet<PatientConsent> PatientConsents => Set<PatientConsent>();
    public DbSet<PatientAlert> PatientAlerts => Set<PatientAlert>();
    public DbSet<Payer> Payers => Set<Payer>();

    // ---- Orders, samples, results -------------------------------------------------------------

    public DbSet<Prescriber> Prescribers => Set<Prescriber>();
    public DbSet<LabOrder> LabOrders => Set<LabOrder>();
    public DbSet<LabOrderItem> LabOrderItems => Set<LabOrderItem>();
    public DbSet<Sample> Samples => Set<Sample>();
    public DbSet<SampleEvent> SampleEvents => Set<SampleEvent>();
    public DbSet<SampleAliquot> SampleAliquots => Set<SampleAliquot>();
    public DbSet<TestResult> TestResults => Set<TestResult>();
    public DbSet<ResultRevision> ResultRevisions => Set<ResultRevision>();
    public DbSet<CriticalResultNotification> CriticalResultNotifications => Set<CriticalResultNotification>();

    // ---- Reports ------------------------------------------------------------------------------

    public DbSet<LabReport> LabReports => Set<LabReport>();
    public DbSet<LabReportDelivery> ReportDeliveries => Set<LabReportDelivery>();

    // ---- Billing ------------------------------------------------------------------------------

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<Deposit> Deposits => Set<Deposit>();

    // ---- Quality ------------------------------------------------------------------------------

    public DbSet<QcMaterial> QcMaterials => Set<QcMaterial>();
    public DbSet<QcTarget> QcTargets => Set<QcTarget>();
    public DbSet<QcResult> QcResults => Set<QcResult>();
    public DbSet<NonConformity> NonConformities => Set<NonConformity>();
    public DbSet<CorrectiveAction> CorrectiveActions => Set<CorrectiveAction>();
    public DbSet<ControlledDocument> ControlledDocuments => Set<ControlledDocument>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<DocumentAcknowledgement> DocumentAcknowledgements => Set<DocumentAcknowledgement>();
    public DbSet<EqaParticipation> EqaParticipations => Set<EqaParticipation>();

    // ---- Inventory and equipment --------------------------------------------------------------

    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<ReagentLot> ReagentLots => Set<ReagentLot>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<CalibrationRecord> CalibrationRecords => Set<CalibrationRecord>();
    public DbSet<InstrumentInterface> InstrumentInterfaces => Set<InstrumentInterface>();
    public DbSet<InstrumentTestMapping> InstrumentTestMappings => Set<InstrumentTestMapping>();
    public DbSet<InstrumentMessageLog> InstrumentMessageLog => Set<InstrumentMessageLog>();

    // ---- Personnel ----------------------------------------------------------------------------

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeCredential> EmployeeCredentials => Set<EmployeeCredential>();
    public DbSet<EmployeeHabilitation> EmployeeHabilitations => Set<EmployeeHabilitation>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveLedgerEntry> LeaveLedger => Set<LeaveLedgerEntry>();
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();
    public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();
    public DbSet<PayrollParameters> PayrollParameters => Set<PayrollParameters>();
    public DbSet<PayrollPeriod> PayrollPeriods => Set<PayrollPeriod>();
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<FinalSettlement> FinalSettlements => Set<FinalSettlement>();
    public DbSet<WorkAccident> WorkAccidents => Set<WorkAccident>();

    // ---- Traceability and sequences -----------------------------------------------------------

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<DataAccessLog> DataAccessLogs => Set<DataAccessLog>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder;

        builder.ApplyConfigurationsFromAssembly(typeof(LisDbContext).Assembly);

        ApplyConventions(builder);
        ApplyGlobalFilters(builder);

        base.OnModelCreating(builder);
    }

    /// <summary>
    /// Conventions applied to every entity, so a new module inherits the right column types
    /// without its author having to remember them.
    /// </summary>
    private static void ApplyConventions(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            // Optimistic concurrency for everything derived from Entity. Two reception desks
            // editing the same patient must collide loudly, not silently overwrite.
            if (typeof(Entity).IsAssignableFrom(entity.ClrType))
            {
                builder.Entity(entity.ClrType)
                       .Property(nameof(Entity.RowVersion))
                       .IsRowVersion();
            }

            // Keys are assigned in the domain (Guid version 7, at construction), never by the
            // server. Left to the default convention, EF treats a Guid key as store-generated and
            // so takes any new child that already carries an Id — a lot added to a stock item, a
            // version added to a document — for an existing row: it issues an UPDATE, matches
            // nothing, and the save fails as a concurrency conflict.
            if (entity.FindPrimaryKey() is { Properties: [{ ClrType: var keyType } key] }
                && keyType == typeof(Guid))
            {
                key.ValueGenerated = ValueGenerated.Never;
            }

            foreach (var property in entity.GetProperties())
            {
                // Dirham amounts and all clinical measurements: fixed precision, never float.
                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                    property.SetColumnType(property.Name.Contains("Amount", StringComparison.Ordinal) ||
                                           property.Name.Contains("Price", StringComparison.Ordinal) ||
                                           property.Name.Contains("Share", StringComparison.Ordinal) ||
                                           property.Name.Contains("Mad", StringComparison.Ordinal)
                        ? "decimal(18,2)"
                        : "decimal(18,6)");

                // Store instants with their offset so a result's time is unambiguous across a
                // daylight-saving change — Morocco has shifted its clocks more than once.
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetColumnType("datetimeoffset(3)");

                if (property.ClrType == typeof(DateOnly) || property.ClrType == typeof(DateOnly?))
                    property.SetColumnType("date");

                // Arabic text needs Unicode; nvarchar is the default in SQL Server, but the
                // collation must be accent- and case-insensitive for name search to behave.
                if (property.ClrType == typeof(string) && property.GetMaxLength() is null)
                    property.SetMaxLength(400);
            }
        }
    }

    private void ApplyGlobalFilters(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            if (entity.BaseType is not null) continue;

            var softDeletable = typeof(ISoftDeletable).IsAssignableFrom(entity.ClrType);
            var siteScoped = typeof(ISiteScoped).IsAssignableFrom(entity.ClrType);

            if (!softDeletable && !siteScoped) continue;

            var parameter = Expression.Parameter(entity.ClrType, "e");
            Expression? filter = null;

            if (softDeletable)
            {
                filter = Expression.Not(
                    Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted)));
            }

            if (siteScoped)
            {
                // e => ScopedSiteIds.Count == 0 || ScopedSiteIds.Contains(e.SiteId)
                var scoped = Expression.Property(
                    Expression.Constant(this), nameof(ScopedSiteIds));

                // Resolved through IReadOnlyCollection<Guid>, which is where Count is declared.
                // Reflection does not surface an interface's inherited members, so looking the name
                // up on IReadOnlyList<Guid> — the static type of ScopedSiteIds — throws
                // "Instance property 'Count' is not defined", and it throws while the model is being
                // built, which means it took the whole context down rather than one query. The
                // nameof() read correctly and hid that: it compiles to "Count" either way.
                var count = Expression.Property(
                    scoped,
                    typeof(IReadOnlyCollection<Guid>)
                        .GetProperty(nameof(IReadOnlyCollection<Guid>.Count))!);

                var unrestricted = Expression.Equal(count, Expression.Constant(0));

                var contains = Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Contains),
                    [typeof(Guid)],
                    scoped,
                    Expression.Property(parameter, nameof(ISiteScoped.SiteId)));

                var siteFilter = Expression.OrElse(unrestricted, contains);
                filter = filter is null ? siteFilter : Expression.AndAlso(filter, siteFilter);
            }

            builder.Entity(entity.ClrType).HasQueryFilter(Expression.Lambda(filter!, parameter));
        }
    }
}

/// <summary>
/// A previous password hash, kept so the reuse rule in the password policy can be enforced. Only
/// the hash is retained; there is no path that recovers a former password.
/// </summary>
public sealed class PasswordHistoryEntry : Entity
{
    private PasswordHistoryEntry() { }

    public PasswordHistoryEntry(Guid userId, string passwordHash, DateTimeOffset changedAt)
    {
        UserId = userId;
        PasswordHash = passwordHash;
        ChangedAt = changedAt;
    }

    public Guid UserId { get; private set; }
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; private set; }
}
