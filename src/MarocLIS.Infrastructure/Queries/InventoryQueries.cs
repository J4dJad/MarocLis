using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class InventoryQueries(LisDbContext db) : IInventoryQueries
{
    /// <summary>
    /// Stock positions.
    /// <para>
    /// The quantity on hand counts only <em>usable</em> lots — not quarantined, not expired, not
    /// recalled. That is deliberate and is the figure a laboratory needs: a shelf holding six
    /// quarantined boxes and nothing released is out of stock for every practical purpose, and a
    /// total that says otherwise is how a run gets started that cannot be finished.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<StockRow>> GetStockAsync(
        Guid siteId, bool onlyAlerts, DateOnly today, CancellationToken ct = default)
    {
        var rows = await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive)
            .Select(i => new
            {
                i.Id,
                i.Code,
                i.Name,
                i.Kind,
                i.UnitOfMeasure,
                i.ReorderPoint,
                i.MinimumStock,
                i.IsCritical,
                i.StorageCondition,
                SupplierName = i.PreferredSupplierId == null
                    ? null
                    : db.Suppliers.Where(s => s.Id == i.PreferredSupplierId)
                                  .Select(s => s.Name).FirstOrDefault(),
                // Usable only: released, not rejected, not recalled, in date, within open stability.
                Usable = i.Lots
                    .Where(l => !l.IsQuarantined && !l.IsRejected && !l.IsRecalled
                                && l.ExpiryDate >= today
                                && (l.OpenedOn == null
                                    || l.StabilityAfterOpeningDays == null
                                    || l.OpenedOn.Value.AddDays(l.StabilityAfterOpeningDays.Value) >= today))
                    .Sum(l => l.QuantityRemaining),
                LotCount = i.Lots.Count(l => l.QuantityRemaining > 0m),
                QuarantinedLotCount = i.Lots.Count(l => l.IsQuarantined && !l.IsRejected
                                                        && l.QuantityRemaining > 0m),
                EarliestExpiry = i.Lots
                    .Where(l => l.QuantityRemaining > 0m && !l.IsRejected && !l.IsRecalled)
                    .Min(l => (DateOnly?)l.ExpiryDate)
            })
            .ToListAsync(ct);

        var mapped = rows.Select(r => new StockRow(
            r.Id, r.Code, r.Name, r.Kind, r.UnitOfMeasure,
            r.Usable, r.ReorderPoint, r.MinimumStock,
            r.Usable <= r.ReorderPoint,
            r.Usable < r.MinimumStock,
            r.IsCritical,
            r.SupplierName,
            r.StorageCondition,
            r.LotCount,
            r.QuarantinedLotCount,
            r.EarliestExpiry,
            r.EarliestExpiry is { } expiry ? expiry.DayNumber - today.DayNumber : null))
            .ToList();

        if (onlyAlerts)
        {
            mapped = mapped
                .Where(r => r.NeedsReorder || r.BelowMinimum
                            || r.QuarantinedLotCount > 0
                            || r.DaysToEarliestExpiry is <= 30)
                .ToList();
        }

        // A critical item short of stock heads the list: a stock-out there stops an analysis being
        // offered at all, which has to be declared to prescribers.
        return
        [
            .. mapped
                .OrderByDescending(r => r.IsCritical && r.BelowMinimum)
                .ThenByDescending(r => r.BelowMinimum)
                .ThenByDescending(r => r.NeedsReorder)
                .ThenBy(r => r.Code)
        ];
    }

    public async Task<IReadOnlyList<LotRow>> GetLotsAsync(
        Guid siteId, Guid? stockItemId, bool onlyQuarantined, DateOnly today,
        CancellationToken ct = default)
    {
        var query = db.ReagentLots.AsNoTracking()
            .Join(db.StockItems.AsNoTracking().Where(i => i.SiteId == siteId),
                  l => l.StockItemId, i => i.Id, (l, i) => new { Lot = l, Item = i });

        if (stockItemId is { } item) query = query.Where(x => x.Item.Id == item);
        if (onlyQuarantined) query = query.Where(x => x.Lot.IsQuarantined && !x.Lot.IsRejected);

        var rows = await query
            .OrderBy(x => x.Lot.ExpiryDate)
            .Take(1000)
            .Select(x => new
            {
                x.Lot.Id,
                x.Lot.StockItemId,
                x.Item.Code,
                x.Item.Name,
                x.Lot.LotNumber,
                x.Lot.ExpiryDate,
                x.Lot.QuantityReceived,
                x.Lot.QuantityRemaining,
                x.Lot.ReceivedOn,
                x.Lot.OpenedOn,
                x.Lot.StabilityAfterOpeningDays,
                x.Lot.IsQuarantined,
                x.Lot.IsRejected,
                x.Lot.IsRecalled,
                x.Lot.StorageLocation,
                SupplierName = x.Lot.SupplierId == null
                    ? null
                    : db.Suppliers.Where(s => s.Id == x.Lot.SupplierId)
                                  .Select(s => s.Name).FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r =>
            {
                var expired = r.ExpiryDate < today;

                var beyondOpen = r.OpenedOn is { } opened
                                 && r.StabilityAfterOpeningDays is { } days
                                 && today > opened.AddDays(days);

                return new LotRow(
                    r.Id, r.StockItemId, r.Code, r.Name, r.LotNumber, r.ExpiryDate,
                    r.ExpiryDate.DayNumber - today.DayNumber,
                    r.QuantityReceived, r.QuantityRemaining, r.ReceivedOn, r.OpenedOn,
                    r.IsQuarantined, r.IsRejected, r.IsRecalled, expired, beyondOpen,
                    !r.IsQuarantined && !r.IsRejected && !r.IsRecalled && !expired && !beyondOpen,
                    r.StorageLocation, r.SupplierName);
            })
        ];
    }

    public Task<ReagentLot?> GetLotEntityAsync(Guid reagentLotId, CancellationToken ct = default) =>
        db.ReagentLots.FirstOrDefaultAsync(l => l.Id == reagentLotId, ct);

    public Task<int> CountResultsProducedWithLotAsync(Guid reagentLotId,
                                                       CancellationToken ct = default) =>
        db.TestResults.AsNoTracking().CountAsync(r => r.ReagentLotId == reagentLotId, ct);

    /// <summary>
    /// Equipment with its next due dates.
    /// <para>
    /// Due dates are computed from the interval and the last intervention rather than read from a
    /// stored column: a stored "next due" goes stale the moment an interval is changed, and an
    /// instrument silently believed to be in calibration is the failure this list exists to catch.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<EquipmentRow>> GetEquipmentAsync(
        Guid siteId, bool onlyDue, DateOnly today, CancellationToken ct = default)
    {
        var rows = await db.Instruments.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.Status != Domain.Equipment.EquipmentStatus.Decommissioned)
            .Select(i => new
            {
                i.Id,
                i.Code,
                i.Name,
                i.Kind,
                i.Status,
                i.Manufacturer,
                i.Model,
                i.SerialNumber,
                i.Location,
                i.LastStatusReason,
                i.LastPreventiveMaintenanceOn,
                i.PreventiveMaintenanceIntervalDays,
                i.LastCalibrationOn,
                i.CalibrationIntervalDays,
                i.LastVerificationOn,
                i.VerificationIntervalDays,
                i.MaintenanceContractValidUntil
            })
            .ToListAsync(ct);

        var mapped = rows.Select(r =>
        {
            DateOnly? Due(DateOnly? last, int? interval) =>
                last is { } value && interval is { } days ? value.AddDays(days) : null;

            var maintenanceDue = Due(r.LastPreventiveMaintenanceOn, r.PreventiveMaintenanceIntervalDays);
            var calibrationDue = Due(r.LastCalibrationOn, r.CalibrationIntervalDays);
            var verificationDue = Due(r.LastVerificationOn, r.VerificationIntervalDays);

            return new EquipmentRow(
                r.Id, r.Code, r.Name, r.Kind, r.Status,
                r.Manufacturer, r.Model, r.SerialNumber, r.Location,
                maintenanceDue, calibrationDue, verificationDue,
                maintenanceDue is { } m && m < today,
                calibrationDue is { } c && c < today,
                r.MaintenanceContractValidUntil is { } contract && contract < today,
                r.MaintenanceContractValidUntil,
                r.LastStatusReason);
        }).ToList();

        if (onlyDue)
        {
            mapped = mapped
                .Where(r => r.MaintenanceOverdue || r.CalibrationOverdue || r.ContractExpired
                            || r.Status != Domain.Equipment.EquipmentStatus.Operational)
                .ToList();
        }

        // An overdue calibration first: results produced on that instrument are not defensible.
        return
        [
            .. mapped
                .OrderByDescending(r => r.CalibrationOverdue)
                .ThenByDescending(r => r.MaintenanceOverdue)
                .ThenBy(r => r.Code)
        ];
    }
}
