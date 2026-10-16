using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// A single dashboard figure, with the navigation target it stands for: the key of the menu entry
/// whose screen lists what the figure counts.
/// </summary>
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

        // Tubes drawn and not yet accepted on the platform: the reception screen's list.
        var awaitingReception = await db.Samples.AsNoTracking()
            .CountAsync(s => s.SiteId == siteId
                             && (s.Status == SampleStatus.Collected || s.Status == SampleStatus.InTransit), ct);

        // Files with a value still to type at the bench, by the entry screen's own selection —
        // calculated parameters and headings are never typed, so they do not keep a file there.
        var enterable = db.TestParameters.AsNoTracking()
            .Where(p => (p.CalculationFormula == null || p.CalculationFormula == "")
                        && p.ValueType != ResultValueType.Heading)
            .Select(p => p.Id);

        var awaitingEntry = await BenchWorksheetQueries.PendingItems(db, siteId)
            .Where(i => db.TestResults.Any(r => r.LabOrderItemId == i.Id
                                                && r.Status == TestResultStatus.Awaited
                                                && enterable.Contains(r.TestParameterId)))
            .Select(i => i.LabOrderId)
            .Distinct()
            .CountAsync(ct);

        var awaitingValidation = await db.TestResults.AsNoTracking()
            .CountAsync(r => r.SiteId == siteId
                             && r.Status == TestResultStatus.TechnicallyValidated, ct);

        var reportedToday = await db.LabReports.AsNoTracking()
            .CountAsync(r => r.SiteId == siteId
                             && r.ReleasedAt >= today && r.ReleasedAt < tomorrow, ct);

        // Files holding a validated value of an analysis still ordered: the report register's list
        // of reports to issue (ResultQueries.GetPendingReleaseAsync), counted.
        var reportsToRelease = await db.LabOrders.AsNoTracking()
            .CountAsync(o => o.SiteId == siteId && o.Status != LabOrderStatus.Cancelled
                             && db.TestResults.Any(r => r.LabOrderId == o.Id
                                                        && r.SiteId == siteId
                                                        && r.Status == TestResultStatus.Validated
                                                        && db.LabOrderItems.Any(i => i.Id == r.LabOrderItemId
                                                                                     && i.Status != LabOrderItemStatus.Cancelled)), ct);

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

        // Reagent lots expiring at the two alert levels of the stock settings (J−30 and J−7 by
        // default), anything already expired with stock left counted in both.
        var planning = new StockPlanningQueries(db);
        var stockSettings = StockSettings.FromValues(await planning.GetSettingValuesAsync(StockSettings.Prefix, ct));

        var expiryHorizon = todayOnly.AddDays(stockSettings.ExpiryWarningDays);
        var criticalHorizon = todayOnly.AddDays(stockSettings.ExpiryCriticalDays);

        var expiring = db.ReagentLots.AsNoTracking()
            .Where(l => l.QuantityRemaining > 0
                        && !l.IsRejected && !l.IsRecalled
                        && db.StockItems.Any(i => i.Id == l.StockItemId && i.SiteId == siteId));

        var expiringLots = await expiring.CountAsync(l => l.ExpiryDate <= expiryHorizon, ct);
        var expiringCritical = await expiring.CountAsync(l => l.ExpiryDate <= criticalHorizon, ct);

        // Items whose usable stock runs out, at the average consumption, before an order placed
        // today would arrive.
        var window = stockSettings.ConsumptionWindowDays;
        var consumption = await planning.GetConsumptionDataAsync(siteId, todayOnly.AddDays(-window),
                                                                 todayOnly.AddDays(-1), todayOnly, null, ct);
        var stockOuts = StockPlanningCalculator.StockOuts(consumption, window, todayOnly).Count;

        // Fridges and freezers without a reading today.
        var start = StockPlanningQueries.Start(todayOnly);
        var end = StockPlanningQueries.Start(todayOnly.AddDays(1));
        var unitsWithoutReading = await db.StorageUnits.AsNoTracking()
            .CountAsync(u => u.SiteId == siteId && u.IsActive
                             && !db.TemperatureReadings.Any(r => r.StorageUnitId == u.Id
                                                                 && r.ReadAt >= start && r.ReadAt < end), ct);

        // Items at or under their reorder point, counting only what can be used: a lot still in
        // quarantine, expired or past its open stability does not fill a rack.
        var toReorder = await db.StockItems.AsNoTracking()
            .CountAsync(i => i.Lots
                .Where(l => !l.IsQuarantined && !l.IsRejected && !l.IsRecalled
                            && l.ExpiryDate >= todayOnly)
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
                    awaitingCollection > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "collection"),
                new("Dashboard.AwaitingReception", awaitingReception,
                    awaitingReception > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "reception"),
                new("Dashboard.AwaitingEntry", awaitingEntry,
                    awaitingEntry > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "entry"),
                new("Dashboard.AwaitingValidation", awaitingValidation,
                    awaitingValidation > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "validation"),
                new("Dashboard.ReportsToRelease", reportsToRelease,
                    reportsToRelease > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "reports"),
                new("Report.Plural", reportedToday, DashboardSeverity.Neutral, "reports")
            ],
            Alerts:
            [
                new("Dashboard.CriticalPending", uncommunicatedCritical,
                    uncommunicatedCritical > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral,
                    "validation"),
                new("Dashboard.OverdueOrders", overdueOrders,
                    overdueOrders > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral,
                    "validation"),
                new("Dashboard.QcAlerts", qcRejected,
                    qcRejected > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral, "qc"),
                new("Equipment.NextCalibration", calibrationOverdue,
                    calibrationOverdue > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral,
                    "equipment"),
                new("Dashboard.OpenNonConformities", openNonConformities,
                    openNonConformities > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "nonconformities"),
                new("Inventory.ToReorder", toReorder,
                    toReorder > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "inventory"),
                new("Inventory.ExpiringSoon", expiringLots,
                    expiringLots > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "inventory"),
                new("Stock.DashboardExpiringCritical", expiringCritical,
                    expiringCritical > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral, "inventory"),
                new("Stock.DashboardStockOuts", stockOuts,
                    stockOuts > 0 ? DashboardSeverity.Urgent : DashboardSeverity.Neutral, "inventory"),
                new("Temperatures.DashboardMissing", unitsWithoutReading,
                    unitsWithoutReading > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "temperatures"),
                new("Dashboard.RejectedSamplesToday", rejectedSamplesToday,
                    rejectedSamplesToday > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "recollections"),
                new("Dashboard.Receivables", unpaidInvoices,
                    unpaidInvoices > 0 ? DashboardSeverity.Attention : DashboardSeverity.Neutral,
                    "billing"),
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
