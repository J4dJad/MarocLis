using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>A single dashboard figure, with the navigation target it stands for.</summary>
public sealed record DashboardMetric(
    string LabelKey,
    int Value,
    DashboardSeverity Severity,
    string? NavigationKey = null,
    string? DetailKey = null);

public enum DashboardSeverity
{
    /// <summary>Informational: a count of normal activity.</summary>
    Neutral = 0,

    /// <summary>Needs attention today.</summary>
    Attention = 1,

    /// <summary>Needs attention now — a breached obligation or an uncommunicated critical result.</summary>
    Urgent = 2
}

public sealed record DashboardSnapshot(
    IReadOnlyList<DashboardMetric> Activity,
    IReadOnlyList<DashboardMetric> Alerts,
    DateTimeOffset GeneratedAt);

public interface IDashboardQueries
{
    Task<DashboardSnapshot> GetSnapshotAsync(Guid siteId, DateTimeOffset now,
                                              CancellationToken ct = default);
}

/// <summary>
/// The dashboard.
/// <para>
/// Every figure here is one an operator can act on, and each is severity-graded so the screen
/// answers "what needs me now?" rather than "how busy were we?". Counts that nobody would act on
/// were left out on purpose: a dashboard that shows twenty numbers gets read as decoration.
/// </para>
/// <para>
/// Each metric is its own <c>COUNT</c> rather than one wide aggregate. On SQL Server Express these
/// all hit covering indexes and stay cheap, and one slow metric cannot then hold up the whole
/// screen — which a single joined query would.
/// </para>
/// </summary>
public sealed class DashboardQueries(LisDbContext db) : IDashboardQueries
{
    public async Task<DashboardSnapshot> GetSnapshotAsync(Guid siteId, DateTimeOffset now,
                                                           CancellationToken ct = default)
    {
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var todayOnly = DateOnly.FromDateTime(today);

        // ---- Activity ---------------------------------------------------------------------------

        var ordersToday = await db.LabOrders.AsNoTracking()
            .CountAsync(o => o.SiteId == siteId
                             && o.RegisteredAt >= today && o.RegisteredAt < tomorrow
                             && o.Status != LabOrderStatus.Cancelled, ct);

        var awaitingCollection = await db.Samples.AsNoTracking()
            .CountAsync(s => s.SiteId == siteId && s.Status == SampleStatus.AwaitingCollection, ct);

        var awaitingValidation = await db.TestResults.AsNoTracking()
            .CountAsync(r => r.SiteId == siteId
                             && r.Status == TestResultStatus.TechnicallyValidated, ct);

        var reportedToday = await db.LabReports.AsNoTracking()
            .CountAsync(r => r.SiteId == siteId
                             && r.ReleasedAt >= today && r.ReleasedAt < tomorrow, ct);

        // ---- Alerts -----------------------------------------------------------------------------

        // Critical results with no completed communication. The only figure on this screen that
        // represents a breached obligation rather than a backlog, so it leads the alerts.
        var communicated = db.CriticalResultNotifications.AsNoTracking()
            .Where(n => n.Outcome == NotificationOutcome.Acknowledged && n.ReadBackConfirmed)
            .Select(n => n.TestResultId);

        var uncommunicatedCritical = await db.TestResults.AsNoTracking()
            .CountAsync(r => r.SiteId == siteId
                             && ((r.Flags & ResultFlag.CriticalLow) != 0
                                 || (r.Flags & ResultFlag.CriticalHigh) != 0)
                             && r.Status != TestResultStatus.Awaited
                             && !communicated.Contains(r.Id), ct);

        var overdueOrders = await db.LabOrders.AsNoTracking()
            .CountAsync(o => o.SiteId == siteId
                             && o.DueAt != null && o.DueAt < now
                             && o.Status != LabOrderStatus.Reported
                             && o.Status != LabOrderStatus.Cancelled, ct);

        var qcRejected = await db.QcResults.AsNoTracking()
            .CountAsync(r => r.SiteId == siteId
                             && r.Outcome == QcOutcome.Rejected
                             && r.ReviewedAt == null, ct);

        var openNonConformities = await db.NonConformities.AsNoTracking()
            .CountAsync(n => n.SiteId == siteId
                             && (n.Status == NonConformityStatus.Open
                                 || n.Status == NonConformityStatus.InProgress
                                 || n.Status == NonConformityStatus.AwaitingVerification), ct);

        // Reagent lots expiring inside a fortnight, and anything already expired with stock left.
        var expiryHorizon = todayOnly.AddDays(14);

        var expiringLots = await db.ReagentLots.AsNoTracking()
            .CountAsync(l => l.QuantityRemaining > 0
                             && !l.IsRejected && !l.IsRecalled
                             && l.ExpiryDate <= expiryHorizon, ct);

        // Items at or under their reorder point, counting only what can be used: a lot still in
        // quarantine, expired or past its open stability does not fill a rack.
        var toReorder = await db.StockItems.AsNoTracking()
            .CountAsync(i => i.Lots
                .Where(l => !l.IsQuarantined && !l.IsRejected && !l.IsRecalled
                            && l.ExpiryDate >= todayOnly
                            && (l.OpenedOn == null
                                || l.StabilityAfterOpeningDays == null
                                || l.OpenedOn.Value.AddDays(l.StabilityAfterOpeningDays.Value) >= todayOnly))
                .Sum(l => l.QuantityRemaining) <= i.ReorderPoint, ct);

        // Computed in SQL from the interval rather than read from a stored "next due" column: a
        // stored date would go stale the moment an interval is changed, and an instrument silently
        // considered in-calibration is precisely the failure this metric exists to catch.
        var calibrationOverdue = await db.Instruments.AsNoTracking()
            .CountAsync(i => i.SiteId == siteId
                             && i.Status == Domain.Equipment.EquipmentStatus.Operational
                             && i.LastCalibrationOn != null
                             && i.CalibrationIntervalDays != null
                             && i.LastCalibrationOn.Value.AddDays(i.CalibrationIntervalDays.Value)
                                 < todayOnly, ct);

        var rejectedSamplesToday = await db.Samples.AsNoTracking()
            .CountAsync(s => s.SiteId == siteId
                             && s.Status == SampleStatus.Rejected
                             && s.RejectedAt >= today && s.RejectedAt < tomorrow, ct);

        var unpaidInvoices = await db.Invoices.AsNoTracking()
            .CountAsync(i => i.SiteId == siteId
                             && (i.Status == InvoiceStatus.Issued
                                 || i.Status == InvoiceStatus.PartiallyPaid)
                             && i.DueDate != null && i.DueDate < todayOnly, ct);

        // Supplier invoices still owed: overdue ones breach the loi 69-21 term and carry a fine,
        // the ones due within a week are the next payment run.
        var openSupplierInvoices = db.SupplierInvoices.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && i.Status != SupplierInvoiceStatus.Paid
                        && i.Status != SupplierInvoiceStatus.Cancelled);

        var supplierInvoicesOverdue = await openSupplierInvoices.CountAsync(i => i.DueDate < todayOnly, ct);

        var paymentHorizon = todayOnly.AddDays(7);
        var supplierInvoicesDueSoon = await openSupplierInvoices
            .CountAsync(i => i.DueDate >= todayOnly && i.DueDate <= paymentHorizon, ct);

        return new DashboardSnapshot(
            Activity:
            [
                new("Dashboard.OrdersToday", ordersToday, DashboardSeverity.Neutral, "orders"),
                new("Dashboard.AwaitingCollection", awaitingCollection,
                    awaitingCollection > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral),
                new("Dashboard.AwaitingValidation", awaitingValidation,
                    awaitingValidation > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "validation"),
                new("Report.Plural", reportedToday, DashboardSeverity.Neutral)
            ],
            Alerts:
            [
                new("Dashboard.CriticalPending", uncommunicatedCritical,
                    uncommunicatedCritical > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral,
                    "validation"),
                new("Dashboard.OverdueOrders", overdueOrders,
                    overdueOrders > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral),
                new("Dashboard.QcAlerts", qcRejected,
                    qcRejected > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral),
                new("Equipment.NextCalibration", calibrationOverdue,
                    calibrationOverdue > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral),
                new("Dashboard.OpenNonConformities", openNonConformities,
                    openNonConformities > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral),
                new("Inventory.ToReorder", toReorder,
                    toReorder > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "inventory"),
                new("Inventory.ExpiringSoon", expiringLots,
                    expiringLots > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral),
                new("SampleStatus.Rejected", rejectedSamplesToday,
                    rejectedSamplesToday > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral),
                new("Dashboard.Receivables", unpaidInvoices,
                    unpaidInvoices > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral),
                new("SupplierInvoices.DashboardOverdue", supplierInvoicesOverdue,
                    supplierInvoicesOverdue > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral,
                    "supplierinvoices"),
                new("SupplierInvoices.DashboardDueSoon", supplierInvoicesDueSoon,
                    supplierInvoicesDueSoon > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "supplierinvoices")
            ],
            GeneratedAt: now);
    }
}
