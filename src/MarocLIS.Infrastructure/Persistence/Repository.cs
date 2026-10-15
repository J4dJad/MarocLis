using System.Linq.Expressions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Patients;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Samples;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// EF Core repository. <see cref="GetByIdWithDetailsAsync"/> is specialised per aggregate rather
/// than generic, because "with details" means something different for each: an order needs its
/// items and their analyses, a patient needs coverages and consents, and loading the wrong graph
/// is either a missing-data bug or a performance problem.
/// </summary>
public class Repository<T>(LisDbContext context, IClock clock) : IRepository<T>
    where T : AggregateRoot
{
    protected LisDbContext Context { get; } = context;
    protected IClock Clock { get; } = clock;

    public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Context.RunExclusiveAsync(async () =>
        {
            await RefreshTrackedAsync(id, ct);
            return await Context.Set<T>().FirstOrDefaultAsync(e => e.Id == id, ct);
        }, ct);

    public Task<T?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.RunExclusiveAsync(async () =>
        {
            await RefreshTrackedAsync(id, ct);
            return await LoadWithDetailsAsync(id, ct);
        }, ct);

    protected virtual Task<T?> LoadWithDetailsAsync(Guid id, CancellationToken ct) =>
        Context.Set<T>().FirstOrDefaultAsync(e => e.Id == id, ct);

    /// <summary>
    /// Re-reads an aggregate this context already tracks before handing it out again.
    /// <para>
    /// A screen keeps one context for as long as it is open. An aggregate it loaded and saved an
    /// hour ago is still tracked, and a query hands back that tracked instance as it was — values
    /// and row version included — even when another screen or workstation has saved it since.
    /// The next save then fails as a concurrency conflict that nobody caused: an analyser edited in
    /// "Équipements" could no longer be saved from "Données de référence". An unchanged instance
    /// is reloaded; one this operation has already modified is left alone.
    /// </para>
    /// </summary>
    private async Task RefreshTrackedAsync(Guid id, CancellationToken ct)
    {
        var tracked = Context.Set<T>().Local.FirstOrDefault(e => e.Id == id);
        if (tracked is null) return;

        var entry = Context.Entry(tracked);
        if (entry.State == EntityState.Unchanged) await entry.ReloadAsync(ct);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        Context.RunExclusiveAsync(() => Context.Set<T>().AnyAsync(e => e.Id == id, ct), ct);

    public Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate,
                                            CancellationToken ct = default) =>
        Context.RunExclusiveAsync<IReadOnlyList<T>>(
            async () => await Context.Set<T>().Where(predicate).ToListAsync(ct), ct);

    public async Task AddAsync(T entity, CancellationToken ct = default) =>
        await Context.Set<T>().AddAsync(entity, ct);

    public void Update(T entity)
    {
        // A service creates an aggregate with AddAsync and then runs the same "apply the request
        // and Update" path as an edit. Update on an entity still in the Added state would turn it
        // into Modified, and the UPDATE of a row that does not exist yet fails as a concurrency
        // conflict: no new analyser could ever be saved. An added entity is left as it is.
        if (Context.Entry(entity).State == EntityState.Added) return;

        Context.Set<T>().Update(entity);
    }

    public void SoftDelete(T entity, Guid byUserId, string reason)
    {
        if (entity is not ISoftDeletable deletable)
            throw new DomainException(
                $"{typeof(T).Name} is not soft-deletable; clinical and accounting records are never removed.");

        deletable.IsDeleted = true;
        deletable.DeletedAt = Clock.Now;
        deletable.DeletedByUserId = byUserId;
        deletable.DeletionReason = reason;
        Context.Set<T>().Update(entity);
    }
}

public sealed class LabOrderRepository(LisDbContext context, IClock clock)
    : Repository<LabOrder>(context, clock)
{
    protected override Task<LabOrder?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.LabOrders
               .Include(o => o.Patient)
               .Include(o => o.Prescriber)
               .Include(o => o.Items).ThenInclude(i => i.AnalysisTest)
               .FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<LabOrder?> GetByAccessionNumberAsync(string accessionNumber, CancellationToken ct = default) =>
        Context.LabOrders
               .Include(o => o.Patient)
               .Include(o => o.Items)
               .FirstOrDefaultAsync(o => o.AccessionNumber == accessionNumber, ct);
}

public sealed class PatientRepository(LisDbContext context, IClock clock)
    : Repository<Patient>(context, clock), IPatientDuplicateFinder
{
    protected override Task<Patient?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.Patients
               .Include(p => p.Coverages).ThenInclude(c => c.Payer)
               .Include(p => p.Consents)
               .Include(p => p.Alerts)
               .FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>
    /// Candidate duplicates for a patient about to be registered. Run before every creation: a
    /// duplicate file splits a patient's history in two, which costs a clinician the antériorité
    /// column that makes a delta check meaningful.
    /// </summary>
    public async Task<IReadOnlyList<Patient>> FindPossibleDuplicatesAsync(
        string searchKey, string? cin, DateOnly? dateOfBirth, CancellationToken ct = default)
    {
        var query = Context.Patients.AsNoTracking().Where(p => p.MergedIntoPatientId == null);

        if (!string.IsNullOrWhiteSpace(cin))
            return await query.Where(p => p.Cin == cin).Take(20).ToListAsync(ct);

        query = query.Where(p => p.SearchKey == searchKey);

        if (dateOfBirth is { } dob)
            query = query.Where(p => p.DateOfBirth == dob);

        return await query.Take(20).ToListAsync(ct);
    }
}

public sealed class SampleRepository(LisDbContext context, IClock clock)
    : Repository<Sample>(context, clock)
{
    protected override Task<Sample?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.Samples
               .Include(s => s.Events)
               .Include(s => s.Aliquots)
               .FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>
    /// The lookup the reception scanner performs. Deliberately exact-match and case-sensitive: a
    /// loose barcode match is how one patient's tube gets attached to another's order.
    /// </summary>
    public Task<Sample?> GetByBarcodeAsync(string barcode, CancellationToken ct = default) =>
        Context.Samples
               .Include(s => s.Events)
               .FirstOrDefaultAsync(s => s.Barcode == barcode, ct);
}

public sealed class AnalysisTestRepository(LisDbContext context, IClock clock)
    : Repository<AnalysisTest>(context, clock)
{
    protected override Task<AnalysisTest?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.AnalysisTests
               .Include(t => t.Department)
               .Include(t => t.SpecimenType)
               .Include(t => t.ContainerType)
               .Include(t => t.Parameters).ThenInclude(p => p.Unit)
               .Include(t => t.Parameters).ThenInclude(p => p.ReferenceRanges)
               .Include(t => t.Parameters).ThenInclude(p => p.CodedOptions)
               .Include(t => t.Tariffs)
               .FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>
    /// Loads the full catalogue graph for the analyses on one order. One query rather than one per
    /// analysis: result entry opens with every parameter, range and coded option already in hand.
    /// </summary>
    public async Task<IReadOnlyList<AnalysisTest>> GetForOrderAsync(IReadOnlyList<Guid> analysisIds,
                                                                    CancellationToken ct = default) =>
        await Context.AnalysisTests
                     .Include(t => t.Parameters).ThenInclude(p => p.Unit)
                     .Include(t => t.Parameters).ThenInclude(p => p.ReferenceRanges)
                     .Include(t => t.Parameters).ThenInclude(p => p.CodedOptions)
                     .Where(t => analysisIds.Contains(t.Id))
                     .AsSplitQuery()
                     .ToListAsync(ct);
}

public sealed class AppUserRepository(LisDbContext context, IClock clock)
    : Repository<AppUser>(context, clock)
{
    /// <summary>
    /// Roles and site access included: administration edits them as collections, and revoking a
    /// role from a navigation that was never loaded is a silent no-op rather than an error.
    /// </summary>
    protected override Task<AppUser?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.Users
               .Include(u => u.Roles)
               .Include(u => u.SiteAccess)
               .FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<AppUser?> GetByUserNameAsync(string userName, CancellationToken ct = default)
    {
        var normalised = userName.Trim().ToLowerInvariant();
        return Context.Users
                      .Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
                      .Include(u => u.SiteAccess)
                      .FirstOrDefaultAsync(u => u.UserName == normalised, ct);
    }
}

public sealed class InvoiceRepository(LisDbContext context, IClock clock)
    : Repository<Invoice>(context, clock)
{
    /// <summary>
    /// Lines and payments included, and this is not an optimisation.
    /// <para>
    /// <see cref="Invoice.Recalculate"/> recomputes the gross, the VAT, the payer share and the
    /// paid total by summing those two collections. Loaded without them, every call to
    /// <see cref="Invoice.RecordPayment"/> — which recalculates — would sum an empty line list and
    /// silently zero the totals of an already-issued invoice. An issued invoice is supposed to be
    /// immutable, so that corruption would be both unlawful and invisible until somebody read the
    /// receivables report.
    /// </para>
    /// </summary>
    protected override Task<Invoice?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.Invoices
               .Include(i => i.Lines)
               .Include(i => i.Payments)
               .AsSplitQuery()
               .FirstOrDefaultAsync(i => i.Id == id, ct);
}

public sealed class RoleRepository(LisDbContext context, IClock clock)
    : Repository<Role>(context, clock)
{
    /// <summary>
    /// Permissions included: the role editor replaces the whole set, and
    /// <see cref="Role.ReplacePermissions"/> clears a collection it must therefore have seen.
    /// </summary>
    protected override Task<Role?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.Roles
               .Include(r => r.Permissions)
               .FirstOrDefaultAsync(r => r.Id == id, ct);
}

public sealed class InstrumentInterfaceRepository(LisDbContext context, IClock clock)
    : Repository<InstrumentInterface>(context, clock)
{
    /// <summary>
    /// Mappings included, and this is the interface's whole safety story. The driver hands over a
    /// code the analyser chose ("NA", "K", "GLU") and the mapping table is what turns it into a
    /// catalogue parameter; loaded without it, every inbound result would look unmapped and the
    /// transmission would be rejected wholesale.
    /// </summary>
    protected override Task<InstrumentInterface?> LoadWithDetailsAsync(
        Guid id, CancellationToken ct = default) =>
        Context.InstrumentInterfaces
               .Include(i => i.Mappings)
               .FirstOrDefaultAsync(i => i.Id == id, ct);
}

/// <summary>
/// The analyser message log. Not a repository: <see cref="InstrumentMessageLog"/> is not an
/// aggregate root, and the only operations are append and read back.
/// </summary>
public sealed class InstrumentMessageLogStore(LisDbContext context) : IInstrumentMessageLogStore
{
    public async Task<Guid> RecordAsync(InstrumentMessageLog entry, CancellationToken ct = default)
    {
        await context.InstrumentMessageLog.AddAsync(entry, ct);
        await context.SaveChangesAsync(ct);

        return entry.Id;
    }

    public async Task MarkProcessedAsync(Guid logId, InstrumentMessageProcessingStatus status,
                                          int? resultCount, int? importedCount, string? issues,
                                          DateTimeOffset processedAt, CancellationToken ct = default)
    {
        var entry = await context.InstrumentMessageLog.FirstOrDefaultAsync(l => l.Id == logId, ct);
        if (entry is null) return;

        entry.Status = status;
        entry.ResultCount = resultCount;
        entry.ImportedCount = importedCount;
        entry.Issues = issues;
        entry.ProcessedAt = processedAt;

        await context.SaveChangesAsync(ct);
    }

    public Task<InstrumentMessageLog?> GetByIdAsync(Guid logId, CancellationToken ct = default) =>
        context.InstrumentMessageLog.FirstOrDefaultAsync(l => l.Id == logId, ct);

    public async Task<IReadOnlyList<InstrumentMessageLog>> GetRecentAsync(
        Guid? interfaceId, int take, CancellationToken ct = default)
    {
        var query = context.InstrumentMessageLog.AsNoTracking();

        if (interfaceId is { } id) query = query.Where(l => l.InstrumentInterfaceId == id);

        return await query
            .OrderByDescending(l => l.OccurredAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
    }

    public async Task MarkReplayedAsync(Guid logId, Guid byUserId, DateTimeOffset at,
                                         CancellationToken ct = default)
    {
        var entry = await context.InstrumentMessageLog.FirstOrDefaultAsync(l => l.Id == logId, ct);
        if (entry is null) return;

        entry.ReplayedAt = at;
        entry.ReplayedByUserId = byUserId;

        await context.SaveChangesAsync(ct);
    }

    public async Task<MessageLogCensus> CensusAsync(DateTimeOffset cutoff,
                                                     CancellationToken ct = default)
    {
        var old = context.InstrumentMessageLog.AsNoTracking().Where(l => l.OccurredAt < cutoff);

        return new MessageLogCensus(
            Purgeable: await old.CountAsync(l => !ReplayableStatuses.Contains(l.Status), ct),
            RetainedReplayable: await old.CountAsync(l => ReplayableStatuses.Contains(l.Status), ct),
            OldestPurgeable: await old
                .Where(l => !ReplayableStatuses.Contains(l.Status))
                .OrderBy(l => l.OccurredAt)
                .Select(l => (DateTimeOffset?)l.OccurredAt)
                .FirstOrDefaultAsync(ct),
            TotalRows: await context.InstrumentMessageLog.AsNoTracking().CountAsync(ct));
    }

    /// <summary>
    /// Deleted in the database rather than loaded and removed.
    /// <para>
    /// This is the one write in the solution that deliberately bypasses the audit interceptor, and
    /// the reason is arithmetic: a retention purge removes months of frames, and writing one
    /// <c>aud.AuditEntries</c> row per deleted frame would leave the audit table larger than the
    /// space the purge reclaimed. Accountability comes instead from a single security event carrying
    /// the operator, the cutoff, the count and the reason — written by the caller, which is why this
    /// method is not public API for anything but <c>PurgeMessageLogAsync</c>.
    /// </para>
    /// </summary>
    public Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        context.InstrumentMessageLog
               .Where(l => l.OccurredAt < cutoff && !ReplayableStatuses.Contains(l.Status))
               .ExecuteDeleteAsync(ct);

    /// <summary>
    /// The states a technician can still act on. Held back from every purge: a frame awaiting
    /// acceptance is pending work, and a failed or partial one is the only remaining route by which
    /// its values can reach a patient file.
    /// </summary>
    private static readonly InstrumentMessageProcessingStatus[] ReplayableStatuses =
    [
        InstrumentMessageProcessingStatus.Failed,
        InstrumentMessageProcessingStatus.PartiallyImported,
        InstrumentMessageProcessingStatus.AwaitingAcceptance
    ];
}

public sealed class TestPanelRepository(LisDbContext context, IClock clock)
    : Repository<TestPanel>(context, clock)
{
    /// <summary>
    /// Members included, and this is not an optimisation. The panel editor replaces the whole
    /// membership, and <see cref="TestPanel.RemoveAnalysis"/> and <see cref="TestPanel.AddAnalysis"/>
    /// both work against a collection they must therefore have seen. Loaded without it, a panel
    /// edited once would lose every analysis it already had — and the loss shows up not here but at
    /// the counter, on the first order that selects it.
    /// </summary>
    protected override Task<TestPanel?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.TestPanels
               .Include(p => p.Items).ThenInclude(i => i.AnalysisTest)
               .FirstOrDefaultAsync(p => p.Id == id, ct);
}

public sealed class StockItemRepository(LisDbContext context, IClock clock)
    : Repository<StockItem>(context, clock)
{
    /// <summary>
    /// Lots included: first-expiry-first-out consumption picks across them, and a partially loaded
    /// set would silently issue from the wrong lot — the one failure lot traceability exists to
    /// prevent.
    /// </summary>
    protected override Task<StockItem?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.StockItems
               .Include(i => i.Lots)
               .FirstOrDefaultAsync(i => i.Id == id, ct);
}

public sealed class InstrumentRepository(LisDbContext context, IClock clock)
    : Repository<Instrument>(context, clock)
{
    /// <summary>
    /// Maintenance and calibration history included: the aggregate derives its last-intervention
    /// dates from those records when a new one is added.
    /// </summary>
    protected override Task<Instrument?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.Instruments
               .Include(i => i.MaintenanceRecords)
               .Include(i => i.Calibrations)
               .AsSplitQuery()
               .FirstOrDefaultAsync(i => i.Id == id, ct);
}

public sealed class EmployeeRepository(LisDbContext context, IClock clock)
    : Repository<Employee>(context, clock)
{
    /// <summary>
    /// Documents and habilitations included: a new habilitation closes the active one for the
    /// same activity, which the aggregate can only do when it sees them.
    /// </summary>
    protected override Task<Employee?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.Employees
               .Include(e => e.Credentials)
               .Include(e => e.Habilitations)
               .AsSplitQuery()
               .FirstOrDefaultAsync(e => e.Id == id, ct);
}

public sealed class PayrollPeriodRepository(LisDbContext context, IClock clock)
    : Repository<PayrollPeriod>(context, clock)
{
    /// <summary>The month with its payslips: they are recomputed and closed together.</summary>
    protected override Task<PayrollPeriod?> LoadWithDetailsAsync(Guid id, CancellationToken ct = default) =>
        Context.PayrollPeriods
               .Include(p => p.Payslips)
               .FirstOrDefaultAsync(p => p.Id == id, ct);
}

public sealed class QcResultRepository(LisDbContext context, IClock clock)
    : Repository<QcResult>(context, clock)
{
    /// <summary>
    /// The trailing window of points for one target, oldest first — the shape
    /// <see cref="WestgardEvaluator"/> expects. Twelve is the longest run any shipped rule needs
    /// (12-x), so the query never reads more history than the rules can use.
    /// </summary>
    public async Task<IReadOnlyList<QcResult>> GetRecentForTargetAsync(
        Guid qcTargetId, int count = 12, CancellationToken ct = default)
    {
        var rows = await Context.QcResults
            .AsNoTracking()
            .Where(r => r.QcTargetId == qcTargetId && r.ZScore != null)
            .OrderByDescending(r => r.MeasuredAt)
            .Take(count)
            .ToListAsync(ct);

        rows.Reverse();
        return rows;
    }

    /// <summary>The other levels measured in the same run, needed for 2-2s within-run and R-4s.</summary>
    public async Task<IReadOnlyList<QcResult>> GetRunPeersAsync(
        Guid qcRunId, Guid excludeResultId, CancellationToken ct = default) =>
        await Context.QcResults
            .AsNoTracking()
            .Where(r => r.QcRunId == qcRunId && r.Id != excludeResultId && r.ZScore != null)
            .ToListAsync(ct);
}

/// <summary>
/// Transaction boundary over <see cref="LisDbContext"/>. Wraps EF's concurrency exception into a
/// domain error so the UI can show "another workstation changed this" rather than a stack trace —
/// a real event on a shared reception counter, not an exceptional one.
/// </summary>
public sealed class UnitOfWork(LisDbContext context) : IUnitOfWork
{
    private IDbContextTransaction? _transaction;

    public string? AuditReason
    {
        get => AuditSaveChangesInterceptor.PendingReason;
        set => AuditSaveChangesInterceptor.PendingReason = value;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            return await context.RunExclusiveAsync(() => context.SaveChangesAsync(ct), ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Abandon();
            throw new ConcurrencyConflictException("Message.ConcurrencyConflict", ex);
        }
        catch (DbUpdateException)
        {
            Abandon();
            throw;
        }
        finally
        {
            // The reason belongs to one save only; leaving it set would mislabel the next one.
            AuditReason = null;
        }
    }

    /// <summary>
    /// Forgets the changes of a save that failed.
    /// <para>
    /// The screen's context outlives the operation. Left tracked, the rejected changes ride along
    /// with the next unrelated save on that screen and fail it again, so one refused save turned
    /// every later one into the same error until the screen was closed. Inside a transaction the
    /// caller owns the rollback and the tracker is left to it.
    /// </para>
    /// </summary>
    private void Abandon()
    {
        if (_transaction is null) context.ChangeTracker.Clear();
    }

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction is not null) return new NoopScope();

        _transaction = await context.RunExclusiveAsync(() => context.Database.BeginTransactionAsync(ct), ct);
        return new TransactionScope(this);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is null) return;
        await context.RunExclusiveAsync(async () => { await _transaction.CommitAsync(ct); return 0; }, ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction is null) return;
        await _transaction.RollbackAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    private sealed class TransactionScope(UnitOfWork owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // An un-committed transaction rolls back. A gapless number taken inside it is returned
            // with it, which is exactly why numbering is not left to an IDENTITY column.
            await owner.RollbackAsync();
        }
    }

    private sealed class NoopScope : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class OperationGateExtensions
{
    /// <summary>Runs <paramref name="operation"/> once no other operation is using the context.</summary>
    internal static async Task<TResult> RunExclusiveAsync<TResult>(this LisDbContext context,
                                                                  Func<Task<TResult>> operation,
                                                                  CancellationToken ct)
    {
        await context.OperationGate.WaitAsync(ct);
        try
        {
            return await operation();
        }
        finally
        {
            context.OperationGate.Release();
        }
    }
}

/// <summary>Raised when two workstations edited the same record. Carries a resource key, not prose.</summary>
public sealed class ConcurrencyConflictException(string resourceKey, Exception inner)
    : Exception(resourceKey, inner)
{
    public string ResourceKey { get; } = resourceKey;
}

public sealed class ControlledDocumentRepository(LisDbContext context, IClock clock)
    : Repository<ControlledDocument>(context, clock)
{
    /// <summary>
    /// Versions and acknowledgements included: the aggregate decides which version is in force and
    /// numbers the next one from that list, so a partial load would number a draft wrongly.
    /// </summary>
    protected override Task<ControlledDocument?> LoadWithDetailsAsync(Guid id,
                                                                      CancellationToken ct = default) =>
        Context.ControlledDocuments
               .Include(d => d.Versions)
               .Include(d => d.Acknowledgements)
               .AsSplitQuery()
               .FirstOrDefaultAsync(d => d.Id == id, ct);
}

public sealed class PurchaseOrderRepository(LisDbContext context, IClock clock)
    : Repository<PurchaseOrder>(context, clock)
{
    /// <summary>Lines included: approval, receipt and the totals all work across them.</summary>
    protected override Task<PurchaseOrder?> LoadWithDetailsAsync(Guid id,
                                                                 CancellationToken ct = default) =>
        Context.PurchaseOrders
               .Include(o => o.Lines)
               .FirstOrDefaultAsync(o => o.Id == id, ct);
}

public sealed class NonConformityRepository(LisDbContext context, IClock clock)
    : Repository<NonConformity>(context, clock)
{
    /// <summary>
    /// Actions included: starting, completing or cancelling an action looks it up in the
    /// aggregate, and closure checks that every action is done. Loaded without them, every action
    /// step answered "action not found" and no non-conformity could ever be closed.
    /// </summary>
    protected override Task<NonConformity?> LoadWithDetailsAsync(Guid id, CancellationToken ct) =>
        Context.NonConformities
               .Include(n => n.Actions)
               .FirstOrDefaultAsync(n => n.Id == id, ct);
}
