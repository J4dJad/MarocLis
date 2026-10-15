using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class SupplierInvoiceQueries(LisDbContext db) : ISupplierInvoiceQueries
{
    private sealed record LineAmounts(decimal Quantity, decimal UnitPriceMad, decimal VatRate);

    /// <summary>Same rounding as the aggregate: each line, then the totals.</summary>
    private static (decimal ExcludingVat, decimal IncludingVat) Totals(IEnumerable<LineAmounts> lines)
    {
        var excluding = lines.Select(l => (Ht: Money.Round(l.Quantity * l.UnitPriceMad), l.VatRate)).ToList();
        var ht = Money.Round(excluding.Sum(l => l.Ht));
        var vat = Money.Round(excluding.Sum(l => Money.Round(l.Ht * l.VatRate)));
        return (ht, Money.Round(ht + vat));
    }

    public async Task<IReadOnlyList<SupplierInvoiceRow>> GetInvoicesAsync(
        Guid siteId, bool includeClosed, DateOnly today, CancellationToken ct = default)
    {
        var rows = await db.SupplierInvoices.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && (includeClosed || (i.Status != SupplierInvoiceStatus.Paid
                                              && i.Status != SupplierInvoiceStatus.Cancelled)))
            .OrderBy(i => i.DueDate).ThenBy(i => i.InvoiceNumber)
            .Select(i => new
            {
                i.Id,
                i.SupplierId,
                SupplierName = db.Suppliers.IgnoreQueryFilters()
                                 .Where(s => s.Id == i.SupplierId).Select(s => s.Name).FirstOrDefault(),
                i.InvoiceNumber,
                i.InvoiceDate,
                i.ReceivedOn,
                i.DeliveryDate,
                i.DueDate,
                i.Status,
                Lines = i.Lines.Select(l => new LineAmounts(l.Quantity, l.UnitPriceMad, l.VatRate)).ToList(),
                Paid = i.Payments.Sum(p => (decimal?)p.AmountMad) ?? 0m,
                VarianceCount = i.Variances.Count(),
                HasScan = i.ScanFileName != null,
                EnteredBy = db.Users.Where(u => u.Id == i.EnteredByUserId).Select(u => u.FullName).FirstOrDefault()
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        return
        [
            .. rows.Select(i =>
            {
                var (ht, ttc) = Totals(i.Lines);
                return new SupplierInvoiceRow(
                    i.Id, i.SupplierId, i.SupplierName ?? "?", i.InvoiceNumber, i.InvoiceDate, i.ReceivedOn,
                    i.DeliveryDate, i.DueDate, i.DueDate.DayNumber - today.DayNumber, i.Status, ht, ttc,
                    i.Paid, i.VarianceCount, i.HasScan, i.EnteredBy ?? "?");
            })
        ];
    }

    public async Task<SupplierInvoiceDetail?> GetDetailAsync(Guid invoiceId, DateOnly today,
                                                             CancellationToken ct = default)
    {
        var header = await db.SupplierInvoices.AsNoTracking()
            .Where(i => i.Id == invoiceId)
            .Select(i => new
            {
                i.Id,
                i.SupplierId,
                SupplierName = db.Suppliers.IgnoreQueryFilters()
                                 .Where(s => s.Id == i.SupplierId).Select(s => s.Name).FirstOrDefault(),
                i.InvoiceNumber,
                i.InvoiceDate,
                i.ReceivedOn,
                i.DeliveryDate,
                i.DueDate,
                i.Status,
                i.PaymentTermsDays,
                HasScan = i.ScanFileName != null,
                EnteredBy = db.Users.Where(u => u.Id == i.EnteredByUserId).Select(u => u.FullName).FirstOrDefault(),
                UnblockedBy = db.Users.Where(u => u.Id == i.UnblockedByUserId).Select(u => u.FullName).FirstOrDefault(),
                i.UnblockedAt,
                i.UnblockJustification,
                i.CancellationReason,
                i.Notes
            })
            .FirstOrDefaultAsync(ct);

        if (header is null) return null;

        var lines = await db.SupplierInvoiceLines.AsNoTracking()
            .Where(l => l.SupplierInvoiceId == invoiceId)
            .OrderBy(l => l.LineNumber)
            .Select(l => new
            {
                l.LineNumber,
                OrderNumber = (from pl in db.PurchaseOrderLines
                               join o in db.PurchaseOrders.IgnoreQueryFilters() on pl.PurchaseOrderId equals o.Id
                               where pl.Id == l.PurchaseOrderLineId
                               select o.Number).FirstOrDefault(),
                ItemCode = db.StockItems.IgnoreQueryFilters()
                             .Where(s => s.Id == l.StockItemId).Select(s => s.Code).FirstOrDefault(),
                l.Description,
                l.Quantity,
                l.UnitPriceMad,
                l.VatRate
            })
            .ToListAsync(ct);

        var variances = await db.SupplierInvoiceVariances.AsNoTracking()
            .Where(v => v.SupplierInvoiceId == invoiceId)
            .OrderBy(v => v.LineNumber).ThenBy(v => v.Kind)
            .Select(v => new SupplierInvoiceVarianceRow(v.LineNumber, v.Kind, v.Expected, v.Actual))
            .ToListAsync(ct);

        var payments = await db.SupplierPayments.AsNoTracking()
            .Where(p => p.SupplierInvoiceId == invoiceId)
            .OrderBy(p => p.PaidOn).ThenBy(p => p.RecordedAt)
            .Select(p => new
            {
                p.PaidOn,
                p.AmountMad,
                p.Method,
                p.Reference,
                RecordedBy = db.Users.Where(u => u.Id == p.RecordedByUserId).Select(u => u.FullName).FirstOrDefault()
            })
            .ToListAsync(ct);

        var (ht, ttc) = Totals(lines.Select(l => new LineAmounts(l.Quantity, l.UnitPriceMad, l.VatRate)));

        var row = new SupplierInvoiceRow(
            header.Id, header.SupplierId, header.SupplierName ?? "?", header.InvoiceNumber, header.InvoiceDate,
            header.ReceivedOn, header.DeliveryDate, header.DueDate, header.DueDate.DayNumber - today.DayNumber,
            header.Status, ht, ttc, payments.Sum(p => p.AmountMad), variances.Count, header.HasScan,
            header.EnteredBy ?? "?");

        return new SupplierInvoiceDetail(
            row,
            [
                .. lines.Select(l => new SupplierInvoiceLineRow(
                    l.LineNumber, l.OrderNumber ?? "?", l.ItemCode ?? "?", l.Description, l.Quantity,
                    l.UnitPriceMad, l.VatRate, Money.Round(l.Quantity * l.UnitPriceMad)))
            ],
            variances,
            [.. payments.Select(p => new SupplierPaymentRow(p.PaidOn, p.AmountMad, p.Method, p.Reference,
                                                            p.RecordedBy ?? "?"))],
            header.PaymentTermsDays, header.UnblockedBy, header.UnblockedAt, header.UnblockJustification,
            header.CancellationReason, header.Notes);
    }

    public async Task<IReadOnlyList<SupplierOption>> GetInvoicingSuppliersAsync(Guid siteId,
                                                                               CancellationToken ct = default) =>
        await db.Suppliers.AsNoTracking()
            .Where(s => s.IsActive
                        && db.PurchaseOrders.Any(o => o.SupplierId == s.Id && o.SiteId == siteId
                                                      && o.Status != PurchaseOrderStatus.Draft
                                                      && o.Status != PurchaseOrderStatus.Cancelled))
            .OrderBy(s => s.Name)
            .Select(s => new SupplierOption(s.Id, s.Code, s.Name))
            .ToListAsync(ct);

    /// <summary>Quantity on the live (not cancelled) invoices of each order line.</summary>
    private IQueryable<SupplierInvoiceLine> LiveInvoiceLines(Guid? excludingInvoiceId) =>
        from il in db.SupplierInvoiceLines
        join si in db.SupplierInvoices.IgnoreQueryFilters() on il.SupplierInvoiceId equals si.Id
        where si.Status != SupplierInvoiceStatus.Cancelled
              && (excludingInvoiceId == null || si.Id != excludingInvoiceId)
        select il;

    public async Task<IReadOnlyList<InvoiceableOrderLine>> GetInvoiceableLinesAsync(
        Guid siteId, Guid supplierId, CancellationToken ct = default)
    {
        var live = LiveInvoiceLines(null);

        var rows = await (
                from l in db.PurchaseOrderLines.AsNoTracking()
                join o in db.PurchaseOrders.AsNoTracking() on l.PurchaseOrderId equals o.Id
                where o.SupplierId == supplierId && o.SiteId == siteId
                      && o.Status != PurchaseOrderStatus.Draft && o.Status != PurchaseOrderStatus.Cancelled
                orderby o.OrderDate, o.Number, l.LineNumber
                select new
                {
                    l.Id,
                    o.Number,
                    o.OrderDate,
                    Item = db.StockItems.IgnoreQueryFilters()
                             .Where(i => i.Id == l.StockItemId)
                             .Select(i => new { i.Code, i.UnitOfMeasure }).FirstOrDefault(),
                    l.Description,
                    l.QuantityOrdered,
                    l.QuantityReceived,
                    l.UnitPriceMad,
                    l.DiscountRate,
                    l.VatRate,
                    Invoiced = live.Where(il => il.PurchaseOrderLineId == l.Id).Sum(il => (decimal?)il.Quantity) ?? 0m
                })
            .ToListAsync(ct);

        return
        [
            .. rows.Where(l => l.Invoiced < Math.Max(l.QuantityOrdered, l.QuantityReceived))
                   .Select(l => new InvoiceableOrderLine(
                       l.Id, l.Number, l.OrderDate, l.Item?.Code ?? "?", l.Description ?? l.Item?.Code ?? string.Empty,
                       l.Item?.UnitOfMeasure ?? string.Empty, l.QuantityOrdered, l.QuantityReceived, l.Invoiced,
                       Money.Round(l.UnitPriceMad * (1m - l.DiscountRate)), l.VatRate))
        ];
    }

    public async Task<IReadOnlyList<OrderLineMatchSource>> GetOrderLineSourcesAsync(
        IReadOnlyCollection<Guid> purchaseOrderLineIds, Guid? excludingInvoiceId, CancellationToken ct = default)
    {
        var ids = purchaseOrderLineIds.Distinct().ToList();
        var live = LiveInvoiceLines(excludingInvoiceId);

        var rows = await (
                from l in db.PurchaseOrderLines.AsNoTracking()
                join o in db.PurchaseOrders.AsNoTracking() on l.PurchaseOrderId equals o.Id
                where ids.Contains(l.Id)
                select new
                {
                    l.Id,
                    l.PurchaseOrderId,
                    o.SupplierId,
                    l.StockItemId,
                    l.Description,
                    ItemCode = db.StockItems.IgnoreQueryFilters()
                                 .Where(i => i.Id == l.StockItemId).Select(i => i.Code).FirstOrDefault(),
                    l.QuantityReceived,
                    l.UnitPriceMad,
                    l.DiscountRate,
                    l.VatRate,
                    l.LastReceivedOn,
                    Invoiced = live.Where(il => il.PurchaseOrderLineId == l.Id).Sum(il => (decimal?)il.Quantity) ?? 0m
                })
            .ToListAsync(ct);

        // The dated deliveries of each line: the lots received on its order for its item.
        var orderIds = rows.Select(l => (Guid?)l.PurchaseOrderId).Distinct().ToList();
        var lots = await db.ReagentLots.IgnoreQueryFilters().AsNoTracking()
                           .Where(l => l.PurchaseOrderId != null && orderIds.Contains(l.PurchaseOrderId))
                           .Select(l => new { l.PurchaseOrderId, l.StockItemId, l.ReceivedOn, l.QuantityReceived })
                           .ToListAsync(ct);
        var billed = await (
                from il in live
                join si in db.SupplierInvoices.IgnoreQueryFilters() on il.SupplierInvoiceId equals si.Id
                where ids.Contains(il.PurchaseOrderLineId)
                select new { il.PurchaseOrderLineId, si.InvoiceDate, il.Quantity })
            .AsNoTracking().ToListAsync(ct);

        return
        [
            .. rows.Select(l => new OrderLineMatchSource(
                l.Id, l.SupplierId, l.StockItemId, l.Description ?? l.ItemCode ?? string.Empty,
                l.QuantityReceived, l.Invoiced, Money.Round(l.UnitPriceMad * (1m - l.DiscountRate)), l.VatRate,
                l.LastReceivedOn,
                [.. lots.Where(r => r.PurchaseOrderId == l.PurchaseOrderId && r.StockItemId == l.StockItemId)
                        .Select(r => new DatedQuantity(r.ReceivedOn, r.QuantityReceived))],
                [.. billed.Where(b => b.PurchaseOrderLineId == l.Id).Select(b => new DatedQuantity(b.InvoiceDate, b.Quantity))]))
        ];
    }

    public Task<bool> InvoiceNumberExistsAsync(Guid supplierId, string invoiceNumber, CancellationToken ct = default) =>
        // Across every site, like the unique index: the supplier numbered the invoice once.
        db.SupplierInvoices.IgnoreQueryFilters().AsNoTracking()
          .AnyAsync(i => i.SupplierId == supplierId && i.InvoiceNumber == invoiceNumber
                         && i.Status != SupplierInvoiceStatus.Cancelled, ct);

    public async Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix,
                                                                                  CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.SiteId == null && s.Key.StartsWith(prefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<LatePaymentSource>> GetLatePaymentSourcesAsync(
        Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.SupplierInvoices.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.Status != SupplierInvoiceStatus.Cancelled
                        && i.DueDate < to && (i.PaidOn == null || i.PaidOn >= from))
            .Select(i => new
            {
                Supplier = db.Suppliers.IgnoreQueryFilters().Where(s => s.Id == i.SupplierId)
                             .Select(s => new { s.Name, s.Ice, s.FiscalId }).FirstOrDefault(),
                i.InvoiceNumber,
                i.InvoiceDate,
                i.DeliveryDate,
                i.DueDate,
                Lines = i.Lines.Select(l => new LineAmounts(l.Quantity, l.UnitPriceMad, l.VatRate)).ToList(),
                Payments = i.Payments.Select(p => new PaymentFact(p.PaidOn, p.AmountMad)).ToList()
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        return
        [
            .. rows.Select(i => new LatePaymentSource(
                i.Supplier?.Name ?? "?", i.Supplier?.Ice, i.Supplier?.FiscalId, i.InvoiceNumber, i.InvoiceDate,
                i.DeliveryDate, i.DueDate, Totals(i.Lines).IncludingVat, i.Payments))
        ];
    }

    public async Task<IReadOnlyList<AccountingInvoiceSource>> GetAccountingInvoicesAsync(
        Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.SupplierInvoices.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.Status != SupplierInvoiceStatus.Cancelled
                        && i.InvoiceDate >= from && i.InvoiceDate <= to)
            .Select(i => new
            {
                Supplier = db.Suppliers.IgnoreQueryFilters().Where(s => s.Id == i.SupplierId)
                             .Select(s => new { s.Code, s.Name, s.AccountingAccount }).FirstOrDefault(),
                i.InvoiceNumber,
                i.InvoiceDate,
                Lines = i.Lines.Select(l => new
                {
                    l.Quantity,
                    l.UnitPriceMad,
                    l.VatRate,
                    Kind = db.StockItems.IgnoreQueryFilters().Where(s => s.Id == l.StockItemId)
                             .Select(s => (StockItemKind?)s.Kind).FirstOrDefault()
                }).ToList()
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        return
        [
            .. rows.Select(i => new AccountingInvoiceSource(
                i.Supplier?.Code ?? "?", i.Supplier?.Name ?? "?", i.Supplier?.AccountingAccount, i.InvoiceNumber,
                i.InvoiceDate,
                [
                    .. i.Lines.Select(l =>
                    {
                        var ht = Money.Round(l.Quantity * l.UnitPriceMad);
                        return new AccountingInvoiceLine(l.Kind ?? StockItemKind.Consumable, ht,
                                                         Money.Round(ht * l.VatRate));
                    })
                ]))
        ];
    }

    public async Task<IReadOnlyList<AccountingPaymentSource>> GetAccountingPaymentsAsync(
        Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // "from" is a query keyword: the period is renamed for the query below.
        var (start, end) = (from, to);

        return await (
                from p in db.SupplierPayments.AsNoTracking()
                join i in db.SupplierInvoices.AsNoTracking() on p.SupplierInvoiceId equals i.Id
                join s in db.Suppliers.IgnoreQueryFilters().AsNoTracking() on i.SupplierId equals s.Id
                where i.SiteId == siteId && p.PaidOn >= start && p.PaidOn <= end
                select new AccountingPaymentSource(s.Code, s.Name, s.AccountingAccount, i.InvoiceNumber,
                                                   p.PaidOn, p.AmountMad, p.Method, p.Reference))
            .ToListAsync(ct);
    }
}
