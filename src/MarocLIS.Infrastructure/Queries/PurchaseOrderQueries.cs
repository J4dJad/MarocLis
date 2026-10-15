using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class PurchaseOrderQueries(LisDbContext db) : IPurchaseOrderQueries
{
    public async Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(
        Guid siteId, bool onlyOpen, DateOnly today, CancellationToken ct = default)
    {
        var rows = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId
                        && (!onlyOpen || (o.Status != PurchaseOrderStatus.Received
                                          && o.Status != PurchaseOrderStatus.Cancelled)))
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Number)
            .Select(o => new
            {
                o.Id,
                o.Number,
                SupplierName = db.Suppliers.IgnoreQueryFilters()
                                 .Where(s => s.Id == o.SupplierId).Select(s => s.Name).FirstOrDefault(),
                o.OrderDate,
                o.ExpectedDeliveryDate,
                o.Status,
                Lines = o.Lines.Select(l => new { l.QuantityOrdered, l.UnitPriceMad, l.DiscountRate, l.VatRate })
                               .ToList(),
                RaisedByName = db.Users.Where(u => u.Id == o.RaisedByUserId)
                                       .Select(u => u.FullName).FirstOrDefault(),
                ApprovedByName = db.Users.Where(u => u.Id == o.ApprovedByUserId)
                                         .Select(u => u.FullName).FirstOrDefault(),
                o.SupplierReference,
                o.IsPriority,
                o.RaisedAutomatically,
                o.SupplierId
            })
            .ToListAsync(ct);

        return rows.Select(o =>
        {
            // Same rounding as the aggregate: each line rounded, then the order total.
            var excluding = o.Lines.Select(l => Money.Round(l.QuantityOrdered * l.UnitPriceMad
                                                            * (1m - l.DiscountRate))).ToList();
            var vat = o.Lines.Zip(excluding, (l, ht) => Money.Round(ht * l.VatRate)).Sum();
            var totalExcluding = Money.Round(excluding.Sum());

            return new PurchaseOrderRow(
                o.Id, o.Number, o.SupplierName ?? "?", o.OrderDate, o.ExpectedDeliveryDate,
                o.Status, o.Lines.Count, totalExcluding, Money.Round(totalExcluding + vat),
                o.RaisedByName ?? "?", o.ApprovedByName, o.SupplierReference,
                o.Status is PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived
                    && o.ExpectedDeliveryDate is { } expected && expected < today,
                o.IsPriority, o.RaisedAutomatically, o.SupplierId);
        }).ToList();
    }

    public async Task<IReadOnlyList<PurchaseOrderLineRow>> GetLinesAsync(
        Guid purchaseOrderId, CancellationToken ct = default)
    {
        var rows = await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.PurchaseOrderId == purchaseOrderId)
            .OrderBy(l => l.LineNumber)
            .Select(l => new
            {
                l.Id,
                l.LineNumber,
                l.StockItemId,
                Item = db.StockItems.IgnoreQueryFilters()
                         .Where(i => i.Id == l.StockItemId)
                         .Select(i => new { i.Code, i.UnitOfMeasure }).FirstOrDefault(),
                l.Description,
                l.QuantityOrdered,
                l.QuantityReceived,
                l.UnitPriceMad,
                l.DiscountRate,
                l.VatRate
            })
            .ToListAsync(ct);

        return rows.Select(l => new PurchaseOrderLineRow(
            l.Id, l.LineNumber, l.StockItemId, l.Item?.Code ?? "?", l.Description ?? string.Empty,
            l.Item?.UnitOfMeasure ?? string.Empty, l.QuantityOrdered, l.QuantityReceived,
            Math.Max(0m, l.QuantityOrdered - l.QuantityReceived), l.UnitPriceMad, l.DiscountRate,
            l.VatRate, Money.Round(l.QuantityOrdered * l.UnitPriceMad * (1m - l.DiscountRate))))
            .ToList();
    }

    /// <summary>Approved, active suppliers only: the ones an order may be raised against.</summary>
    public async Task<IReadOnlyList<SupplierOption>> GetSuppliersAsync(CancellationToken ct = default) =>
        await db.Suppliers.AsNoTracking()
            .Where(s => s.IsActive && s.IsApproved)
            .OrderBy(s => s.Name)
            .Select(s => new SupplierOption(s.Id, s.Code, s.Name))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<OrderableItem>> GetOrderableItemsAsync(
        Guid siteId, CancellationToken ct = default)
    {
        var rows = await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive)
            .OrderBy(i => i.Code)
            .Select(i => new
            {
                i.Id, i.Code, i.Name, i.UnitOfMeasure, i.PreferredSupplierId, i.ReorderQuantity,
                i.LastUnitPriceMad,
                // The supplier's usual discount: the one of the last delivery of the item from it.
                LastDiscountRate = db.PurchaseOrders
                    .Where(o => o.SupplierId == i.PreferredSupplierId)
                    .SelectMany(o => o.Lines.Where(l => l.StockItemId == i.Id && l.QuantityReceived > 0m),
                                (o, l) => new { o.OrderDate, o.Number, l.DiscountRate })
                    .OrderByDescending(x => x.OrderDate).ThenByDescending(x => x.Number)
                    .Select(x => (decimal?)x.DiscountRate)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        // Captive reagents of a contract in force start from the contracted price.
        var captive = await InstrumentContractQueries.CaptiveReagentsAsync(db, siteId, DateOnly.FromDateTime(DateTime.Today),
                                                                           null, ct);
        var contracted = captive.GroupBy(c => c.StockItemId).ToDictionary(g => g.Key, g => g.First());

        return rows.Select(i => new OrderableItem(i.Id, i.Code, i.Name, i.UnitOfMeasure,
                                                  i.PreferredSupplierId, i.ReorderQuantity,
                                                  i.LastUnitPriceMad, i.LastDiscountRate ?? 0m,
                                                  contracted.GetValueOrDefault(i.Id)?.ContractPriceMad,
                                                  contracted.GetValueOrDefault(i.Id)?.SupplierId)).ToList();
    }

    public Task<bool> IsSupplierApprovedAsync(Guid supplierId, CancellationToken ct = default) =>
        db.Suppliers.AsNoTracking().AnyAsync(s => s.Id == supplierId && s.IsActive && s.IsApproved, ct);

    public async Task<IReadOnlyList<ReorderCandidate>> GetReorderCandidatesAsync(
        Guid siteId, DateOnly today, CancellationToken ct = default)
    {
        var open = new[]
        {
            PurchaseOrderStatus.Draft, PurchaseOrderStatus.Approved, PurchaseOrderStatus.Sent,
            PurchaseOrderStatus.PartiallyReceived
        };

        var rows = await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive && i.ReorderPoint > 0m)
            // Already being ordered: the stock will come back up when that delivery arrives.
            .Where(i => !db.PurchaseOrderLines.Any(l => l.StockItemId == i.Id
                && l.QuantityReceived < l.QuantityOrdered
                && db.PurchaseOrders.Any(o => o.Id == l.PurchaseOrderId && open.Contains(o.Status))))
            .Select(i => new
            {
                i.Id, i.Code, i.UnitOfMeasure, i.PreferredSupplierId, i.ReorderPoint,
                i.ReorderQuantity, i.MaximumStock, i.LastUnitPriceMad, i.TypicalShelfLifeDays,
                LastDiscountRate = db.PurchaseOrders
                    .Where(o => o.SupplierId == i.PreferredSupplierId)
                    .SelectMany(o => o.Lines.Where(l => l.StockItemId == i.Id && l.QuantityReceived > 0m),
                                (o, l) => new { o.OrderDate, o.Number, l.DiscountRate })
                    .OrderByDescending(x => x.OrderDate).ThenByDescending(x => x.Number)
                    .Select(x => (decimal?)x.DiscountRate)
                    .FirstOrDefault(),
                SupplierApproved = db.Suppliers.Any(s => s.Id == i.PreferredSupplierId
                                                         && s.IsActive && s.IsApproved),
                // Usable only, as on the stock screen: released, in date, within open stability.
                Usable = i.Lots
                    .Where(l => !l.IsQuarantined && !l.IsRejected && !l.IsRecalled
                                && l.ExpiryDate >= today
                                && (l.OpenedOn == null
                                    || l.StabilityAfterOpeningDays == null
                                    || l.OpenedOn.Value.AddDays(l.StabilityAfterOpeningDays.Value) >= today))
                    .Sum(l => l.QuantityRemaining)
            })
            .ToListAsync(ct);

        var due = rows.Where(r => r.Usable <= r.ReorderPoint).ToList();
        if (due.Count == 0) return [];

        // Captive reagents of a placement contract in force are ordered from its supplier, at its price.
        var captive = (await InstrumentContractQueries.CaptiveReagentsAsync(db, siteId, today,
                                                                            [.. due.Select(r => r.Id)], ct))
            .GroupBy(c => c.StockItemId).ToDictionary(g => g.Key, g => g.First());
        var captiveSuppliers = captive.Values.Select(c => c.SupplierId).Distinct().ToList();
        var approved = (await db.Suppliers.AsNoTracking()
                .Where(s => captiveSuppliers.Contains(s.Id) && s.IsActive && s.IsApproved)
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();

        return due.Select(r =>
                   {
                       var contract = captive.GetValueOrDefault(r.Id);
                       return new ReorderCandidate(r.Id, r.Code, r.UnitOfMeasure, r.PreferredSupplierId,
                                                   r.SupplierApproved, r.Usable, r.ReorderPoint,
                                                   r.ReorderQuantity, r.MaximumStock, r.LastUnitPriceMad,
                                                   r.LastDiscountRate ?? 0m, r.TypicalShelfLifeDays,
                                                   contract?.SupplierId,
                                                   contract is not null && approved.Contains(contract.SupplierId),
                                                   contract?.ContractPriceMad, contract?.ContractReference);
                   })
                  .ToList();
    }

    public async Task<Guid?> FindAutomaticDraftAsync(Guid siteId, Guid supplierId,
                                                     CancellationToken ct = default) =>
        await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId && o.SupplierId == supplierId && o.RaisedAutomatically
                        && o.Status == PurchaseOrderStatus.Draft)
            .OrderByDescending(o => o.OrderDate)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(ct);
}
