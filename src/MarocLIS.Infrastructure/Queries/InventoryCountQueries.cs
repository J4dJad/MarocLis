using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class InventoryCountQueries(LisDbContext db) : IInventoryCountQueries
{
    public async Task<IReadOnlyList<InventoryCountRow>> GetCountsAsync(Guid siteId, bool includeClosed,
                                                                      CancellationToken ct = default) =>
        await db.InventoryCounts.AsNoTracking().IgnoreAutoIncludes()
            .Where(c => c.SiteId == siteId
                        && (includeClosed || c.Status == InventoryCountStatus.Draft
                                          || c.Status == InventoryCountStatus.Counting))
            .OrderByDescending(c => c.CountDate).ThenByDescending(c => c.Number)
            .Select(c => new InventoryCountRow(
                c.Id, c.Number, c.CountDate, c.Scope, c.StorageLocation, c.Category, c.Status,
                c.Lines.Count(),
                c.Lines.Count(l => l.CountedQuantity != null),
                c.Lines.Count(l => l.CountedQuantity != null && l.CountedQuantity != l.ExpectedQuantity),
                c.ClosedAt))
            .ToListAsync(ct);

    public async Task<InventoryCountDetail?> GetDetailAsync(Guid inventoryCountId, CancellationToken ct = default)
    {
        var count = await db.InventoryCounts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == inventoryCountId, ct);
        if (count is null) return null;

        var itemIds = count.Lines.Select(l => l.StockItemId).Distinct().ToList();
        var items = await db.StockItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Name, i.UnitOfMeasure })
            .ToDictionaryAsync(i => i.Id, ct);

        // In the operator's language, as the screen and the printed sheets around it are.
        var lines = count.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var item = items.GetValueOrDefault(l.StockItemId);
            return new InventoryCountLineRow(l.Id, l.LineNumber, l.StockItemId, l.ItemCode, item?.Name.Current ?? l.ItemCode,
                                             item?.UnitOfMeasure ?? string.Empty, l.ReagentLotId, l.LotNumber,
                                             l.ExpiryDate, l.StorageLocation, l.ExpectedQuantity, l.CountedQuantity,
                                             l.Variance, l.Comment);
        }).ToList();

        var row = new InventoryCountRow(count.Id, count.Number, count.CountDate, count.Scope, count.StorageLocation,
                                        count.Category, count.Status, lines.Count,
                                        lines.Count(l => l.CountedQuantity is not null),
                                        lines.Count(l => l.Variance is not null and not 0m), count.ClosedAt);

        return new InventoryCountDetail(row, lines);
    }

    public async Task<IReadOnlyList<(InventoryCountLineInput Lot, StockItemKind Kind)>> GetCountableLotsAsync(
        Guid siteId, CancellationToken ct = default)
    {
        var rows = await (
                from l in db.ReagentLots.AsNoTracking()
                join i in db.StockItems.AsNoTracking() on l.StockItemId equals i.Id
                where i.SiteId == siteId && l.QuantityRemaining > 0m
                select new
                {
                    i.Id, i.Code, i.Kind, LotId = l.Id, l.LotNumber, l.ExpiryDate,
                    // The lot's own place, else where the item is normally kept.
                    Location = l.StorageLocation ?? i.DefaultStorageLocation,
                    l.QuantityRemaining
                })
            .ToListAsync(ct);

        return [.. rows.Select(r => (new InventoryCountLineInput(r.Id, r.LotId, r.Code, r.LotNumber, r.ExpiryDate,
                                                                 r.Location, r.QuantityRemaining), r.Kind))];
    }

    public async Task<IReadOnlyList<string>> GetStorageLocationsAsync(Guid siteId, CancellationToken ct = default)
    {
        var lots = await (
                from l in db.ReagentLots.AsNoTracking()
                join i in db.StockItems.AsNoTracking() on l.StockItemId equals i.Id
                where i.SiteId == siteId && l.QuantityRemaining > 0m && l.StorageLocation != null
                select l.StorageLocation!)
            .Distinct()
            .ToListAsync(ct);

        var defaults = await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive && i.DefaultStorageLocation != null)
            .Select(i => i.DefaultStorageLocation!)
            .Distinct()
            .ToListAsync(ct);

        return [.. lots.Concat(defaults).Select(l => l.Trim()).Where(l => l.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }
}

public sealed class TemperatureQueries(LisDbContext db) : ITemperatureQueries
{
    public async Task<IReadOnlyList<StorageUnitRow>> GetUnitsAsync(Guid siteId, DateOnly today, bool includeInactive,
                                                                  CancellationToken ct = default)
    {
        var start = StockPlanningQueries.Start(today);
        var end = StockPlanningQueries.Start(today.AddDays(1));

        var rows = await db.StorageUnits.AsNoTracking()
            .Where(u => u.SiteId == siteId && (includeInactive || u.IsActive))
            .OrderBy(u => u.Name)
            .Select(u => new
            {
                Unit = u,
                Today = db.TemperatureReadings.Count(r => r.StorageUnitId == u.Id && r.ReadAt >= start && r.ReadAt < end),
                Last = db.TemperatureReadings.Where(r => r.StorageUnitId == u.Id)
                                             .OrderByDescending(r => r.ReadAt)
                                             .Select(r => new { r.Celsius, r.ReadAt, r.IsOutOfRange })
                                             .FirstOrDefault()
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new StorageUnitRow(
            r.Unit.Id, r.Unit.Name, r.Unit.Kind, r.Unit.Location, r.Unit.SerialNumber, r.Unit.MinimumCelsius,
            r.Unit.MaximumCelsius, r.Unit.ReadingsPerDay, r.Unit.IsActive, r.Today, r.Last?.Celsius, r.Last?.ReadAt,
            r.Last?.IsOutOfRange ?? false))];
    }

    public async Task<IReadOnlyList<TemperatureReadingRow>> GetReadingsAsync(Guid storageUnitId, DateOnly from,
                                                                            DateOnly to, CancellationToken ct = default)
    {
        var start = StockPlanningQueries.Start(from);
        var end = StockPlanningQueries.Start(to.AddDays(1));

        return await (
                from r in db.TemperatureReadings.AsNoTracking()
                where r.StorageUnitId == storageUnitId && r.ReadAt >= start && r.ReadAt < end
                orderby r.ReadAt
                select new TemperatureReadingRow(
                    r.Id, r.ReadAt, r.Celsius, r.PeriodMinimumCelsius, r.PeriodMaximumCelsius, r.IsOutOfRange,
                    db.Users.IgnoreQueryFilters().Where(u => u.Id == r.ByUserId).Select(u => u.FullName)
                      .FirstOrDefault() ?? "—",
                    r.Comment,
                    db.NonConformities.IgnoreQueryFilters().Where(n => n.Id == r.NonConformityId)
                      .Select(n => n.Reference).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public Task<bool> NameExistsAsync(Guid siteId, string name, Guid? exceptUnitId, CancellationToken ct = default) =>
        db.StorageUnits.AsNoTracking()
          .AnyAsync(u => u.SiteId == siteId && u.Name == name && (exceptUnitId == null || u.Id != exceptUnitId), ct);
}
