using MarocLIS.Application.Billing;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>The purchase order as printed and e-mailed.</summary>
public sealed class PurchaseOrderDocumentQueries(LisDbContext db) : IPurchaseOrderDocumentQueries
{
    public async Task<PurchaseOrderDocument?> GetDocumentAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == purchaseOrderId, ct);
        if (order is null) return null;

        var laboratory = await PurchasingSql.LaboratoryAsync(db, order.SiteId, ct);

        // The supplier as it stands: a purchase order is addressed today, unlike an invoice frozen at issue.
        var supplier = await db.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == order.SupplierId)
            .Select(s => new PurchaseOrderSupplierParty(s.Code, s.Name, s.Ice, s.FiscalId, s.CommercialRegisterNumber,
                                                        s.AddressLine, s.City, s.ContactName, s.Phone, s.Email))
            .FirstOrDefaultAsync(ct)
            ?? new PurchaseOrderSupplierParty("?", "?", null, null, null, null, null, null, null, null);

        var itemIds = order.Lines.Select(l => l.StockItemId).Distinct().ToList();
        var items = await db.StockItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, NameFr = i.Name.Fr, i.UnitOfMeasure, i.SupplierCatalogueReference })
            .ToDictionaryAsync(i => i.Id, ct);

        var names = await db.Users.AsNoTracking()
            .Where(u => u.Id == order.RaisedByUserId || u.Id == order.ApprovedByUserId)
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var lines = order.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var item = items.GetValueOrDefault(l.StockItemId);
            return new PurchaseOrderDocumentLine(
                l.LineNumber,
                // The supplier's own reference is what its order desk keys in; the internal code is
                // the fallback when the catalogue has none.
                item?.SupplierCatalogueReference is { Length: > 0 } reference ? reference : item?.Code ?? "?",
                item is null ? l.Description ?? "?" : item.NameFr,
                item?.UnitOfMeasure ?? string.Empty,
                l.QuantityOrdered, l.UnitPriceMad, l.DiscountRate, l.VatRate);
        }).ToList();

        var delivery = string.Join(" — ", new[]
        {
            string.IsNullOrWhiteSpace(laboratory.Name) ? laboratory.LegalName : laboratory.Name,
            laboratory.Address,
            string.Join(" ", new[] { laboratory.PostalCode, laboratory.City }.Where(s => !string.IsNullOrWhiteSpace(s)))
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new PurchaseOrderDocument(
            order.Id, order.Number, order.Status, order.OrderDate, order.ExpectedDeliveryDate, laboratory, supplier,
            delivery.Length == 0 ? null : delivery, order.SupplierReference, order.Notes,
            names.GetValueOrDefault(order.RaisedByUserId) ?? "?",
            order.ApprovedByUserId is { } approver ? names.GetValueOrDefault(approver) : null,
            order.ApprovedAt, lines);
    }
}

/// <summary>Requests for quotation.</summary>
public sealed class QuoteQueries(LisDbContext db) : IQuoteQueries
{
    public async Task<IReadOnlyList<QuoteRequestRow>> GetRequestsAsync(Guid siteId, bool onlyOpen,
                                                                      CancellationToken ct = default)
    {
        var rows = await db.QuoteRequests.AsNoTracking()
            .Where(q => q.SiteId == siteId && (!onlyOpen || q.Status == QuoteRequestStatus.Open))
            .OrderByDescending(q => q.RequestedOn).ThenByDescending(q => q.Number)
            .Select(q => new
            {
                q.Id, q.Number, q.RequestedOn, q.Subject, q.Status, q.ResponseDueDate,
                Items = q.Items.Count,
                Suppliers = q.Suppliers.Count,
                Offers = q.Offers.Count,
                RaisedBy = db.Users.Where(u => u.Id == q.RaisedByUserId).Select(u => u.FullName).FirstOrDefault(),
                AwardedSupplier = db.Suppliers.IgnoreQueryFilters()
                    .Where(s => q.Offers.Any(o => o.Id == q.AwardedQuoteId && o.SupplierId == s.Id))
                    .Select(s => s.Name).FirstOrDefault(),
                Order = db.PurchaseOrders.Where(o => o.Id == q.PurchaseOrderId).Select(o => o.Number).FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows.Select(r => new QuoteRequestRow(r.Id, r.Number, r.RequestedOn, r.Subject, r.Status,
                                                    r.ResponseDueDate, r.Items, r.Suppliers, r.Offers,
                                                    r.RaisedBy ?? "?", r.AwardedSupplier, r.Order)).ToList();
    }

    public async Task<QuoteRequestDetail?> GetDetailAsync(Guid requestId, DateOnly today, CancellationToken ct = default)
    {
        var quote = await db.QuoteRequests.AsNoTracking().FirstOrDefaultAsync(q => q.Id == requestId, ct);
        if (quote is null) return null;

        var itemIds = quote.Items.Select(i => i.StockItemId).ToList();
        var items = await db.StockItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, NameFr = i.Name.Fr, i.UnitOfMeasure })
            .ToDictionaryAsync(i => i.Id, ct);

        var supplierIds = quote.Suppliers.Select(s => s.SupplierId)
                               .Concat(quote.Offers.Select(o => o.SupplierId)).Distinct().ToList();
        var suppliers = await db.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Code, s.Name, s.Email, Approved = s.IsActive && s.IsApproved })
            .ToDictionaryAsync(s => s.Id, ct);

        var offerIds = quote.Offers.Select(o => o.Id).ToList();
        var files = await db.PurchasingDocuments.AsNoTracking()
            .Where(d => d.OwnerKind == PurchasingDocumentOwner.SupplierQuote && offerIds.Contains(d.OwnerId))
            .GroupBy(d => d.OwnerId)
            .Select(g => new { OwnerId = g.Key, FileName = g.OrderByDescending(d => d.AttachedAt).Select(d => d.FileName).First() })
            .ToDictionaryAsync(f => f.OwnerId, f => f.FileName, ct);

        var itemRows = quote.Items.OrderBy(i => i.LineNumber).Select(i =>
        {
            var item = items.GetValueOrDefault(i.StockItemId);
            return new QuoteItemRow(i.Id, i.LineNumber, i.StockItemId, item?.Code ?? "?",
                                    item?.NameFr ?? i.Description ?? "?", item?.UnitOfMeasure ?? string.Empty,
                                    i.Quantity);
        }).ToList();

        var supplierRows = quote.Suppliers.Select(s =>
        {
            var supplier = suppliers.GetValueOrDefault(s.SupplierId);
            return new QuoteSupplierRow(s.SupplierId, supplier?.Code ?? "?", supplier?.Name ?? "?", supplier?.Email,
                                        supplier?.Approved ?? false, s.SentAt,
                                        quote.Offers.Any(o => o.SupplierId == s.SupplierId));
        }).OrderBy(s => s.Name).ToList();

        var offerRows = quote.Offers.Select(o =>
        {
            var (total, missing) = QuoteComparison.Totals(quote, o);
            return new QuoteOfferRow(o.Id, o.SupplierId, suppliers.GetValueOrDefault(o.SupplierId)?.Name ?? "?",
                                     o.ReceivedOn, o.ValidUntil, o.DeliveryDelayDays, o.Conditions,
                                     o.SupplierReference, o.IsExpired(today), total, missing,
                                     files.GetValueOrDefault(o.Id));
        }).OrderBy(o => o.MissingItems).ThenBy(o => o.TotalExcludingVat).ToList();

        return new QuoteRequestDetail(quote.Id, quote.Number, quote.Status, quote.RequestedOn, quote.Subject,
                                      quote.ResponseDueDate, quote.Notes, quote.AwardedQuoteId, itemRows,
                                      supplierRows, offerRows, QuoteComparison.Build(quote, today));
    }

    public async Task<IReadOnlyList<QuotableItem>> GetQuotableItemsAsync(Guid siteId, DateOnly today,
                                                                        CancellationToken ct = default)
    {
        var rows = await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive)
            .OrderBy(i => i.Code)
            .Select(i => new { i.Id, i.Code, i.Name, i.UnitOfMeasure, i.ReorderQuantity })
            .ToListAsync(ct);

        var captive = (await InstrumentContractQueries.CaptiveReagentsAsync(db, siteId, today, null, ct))
            .GroupBy(c => c.StockItemId).ToDictionary(g => g.Key, g => g.First().ContractReference);

        return rows.Select(r => new QuotableItem(r.Id, r.Code, r.Name, r.UnitOfMeasure, r.ReorderQuantity,
                                                 captive.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<IReadOnlyList<QuoteSupplierOption>> GetSuppliersAsync(CancellationToken ct = default) =>
        await db.Suppliers.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new QuoteSupplierOption(s.Id, s.Code, s.Name, s.Email, s.IsApproved))
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetItemCodesAsync(IReadOnlyList<Guid> stockItemIds,
                                                                          CancellationToken ct = default)
    {
        var ids = stockItemIds.ToList();
        return await db.StockItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, NameFr = i.Name.Fr })
            .ToDictionaryAsync(i => i.Id, i => $"{i.Code} — {i.NameFr}", ct);
    }

    public Task<IReadOnlyList<CaptiveReagent>> GetCaptiveReagentsAsync(Guid siteId, DateOnly today,
                                                                      IReadOnlyList<Guid>? stockItemIds,
                                                                      CancellationToken ct = default) =>
        InstrumentContractQueries.CaptiveReagentsAsync(db, siteId, today, stockItemIds, ct);

    public async Task<string> GetLaboratoryNameAsync(Guid siteId, CancellationToken ct = default)
    {
        var laboratory = await PurchasingSql.LaboratoryAsync(db, siteId, ct);
        return string.IsNullOrWhiteSpace(laboratory.Name) ? laboratory.LegalName : laboratory.Name;
    }

    public Task<(string FileName, byte[] Content)?> GetDocumentAsync(PurchasingDocumentOwner ownerKind, Guid ownerId,
                                                                     CancellationToken ct = default) =>
        PurchasingSql.LatestDocumentAsync(db, ownerKind, ownerId, ct);
}

/// <summary>Instrument contracts and their dashboard.</summary>
public sealed class InstrumentContractQueries(LisDbContext db) : IInstrumentContractQueries
{
    /// <summary>Results that count as a test produced: a value entered, whatever validation stage it reached.</summary>
    private static readonly TestResultStatus[] Produced =
    [
        TestResultStatus.Entered, TestResultStatus.TechnicallyValidated, TestResultStatus.Validated,
        TestResultStatus.Reported
    ];

    /// <summary>Orders that commit money: approved and beyond, never a draft or a cancelled order.</summary>
    private static readonly PurchaseOrderStatus[] Committed =
    [
        PurchaseOrderStatus.Approved, PurchaseOrderStatus.Sent, PurchaseOrderStatus.PartiallyReceived,
        PurchaseOrderStatus.Received
    ];

    public async Task<IReadOnlyList<InstrumentContractRow>> GetContractsAsync(Guid siteId, DateOnly today,
                                                                             bool includeInactive,
                                                                             CancellationToken ct = default)
    {
        var contracts = await db.InstrumentContracts.AsNoTracking()
            .Where(c => c.SiteId == siteId && (includeInactive || c.IsActive))
            .ToListAsync(ct);

        var supplierIds = contracts.Select(c => c.SupplierId).Distinct().ToList();
        var suppliers = await db.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var instrumentIds = contracts.SelectMany(c => c.Instruments.Select(i => i.InstrumentId)).Distinct().ToList();
        var instruments = await db.Instruments.IgnoreQueryFilters().AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Code, ct);

        var contractIds = contracts.Select(c => c.Id).ToList();
        var files = await db.PurchasingDocuments.AsNoTracking()
            .Where(d => d.OwnerKind == PurchasingDocumentOwner.InstrumentContract && contractIds.Contains(d.OwnerId))
            .GroupBy(d => d.OwnerId)
            .Select(g => new { OwnerId = g.Key, FileName = g.OrderByDescending(d => d.AttachedAt).Select(d => d.FileName).First() })
            .ToDictionaryAsync(f => f.OwnerId, f => f.FileName, ct);

        var rows = new List<InstrumentContractRow>();

        foreach (var contract in contracts)
        {
            var ids = contract.Instruments.Select(i => i.InstrumentId).ToList();
            var (from, to) = ContractCommitment.Period(contract, today);
            var until = today < to ? today : to;

            decimal consumed = 0m;
            if (until >= from)
            {
                if (ContractCommitment.CountsTests(contract.CommitmentBasis))
                    consumed = await CountTestsAsync(ids, contract.CountedAnalysisCodes, from, until, ct);
                else if (ContractCommitment.CountsPurchases(contract.CommitmentBasis))
                    consumed = await SumPurchasesAsync(contract.SupplierId,
                                                       [.. contract.Reagents.Select(r => r.StockItemId)],
                                                       from, until, ct);
            }

            var interventions = await InterventionsAsync(contract, ct);

            rows.Add(new InstrumentContractRow(
                contract.Id, contract.Reference, contract.SupplierId,
                suppliers.GetValueOrDefault(contract.SupplierId) ?? "?", contract.Kind, contract.StartDate,
                contract.EndDate, contract.DaysToEnd(today), contract.IsActive, contract.IsEndingSoon(today),
                string.Join(", ", ids.Select(id => instruments.GetValueOrDefault(id) ?? "?").Order()),
                ContractCommitment.Evaluate(contract, today, consumed),
                interventions.Count, interventions.Sum(i => i.DowntimeHours ?? 0m),
                interventions.Count(i => i.DelayExceeded), contract.MaxInterventionDelayHours,
                files.GetValueOrDefault(contract.Id)));
        }

        // What needs attention first: a commitment at risk, then the contracts ending soonest.
        return [.. rows.OrderByDescending(r => r.Commitment.AtRisk)
                       .ThenByDescending(r => r.IsEndingSoon)
                       .ThenBy(r => r.EndDate)];
    }

    public async Task<IReadOnlyList<ContractInterventionRow>> GetInterventionsAsync(Guid contractId,
                                                                                   CancellationToken ct = default)
    {
        var contract = await db.InstrumentContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct);
        return contract is null ? [] : await InterventionsAsync(contract, ct);
    }

    /// <summary>The interventions on the contract's analysers during its term, newest first.</summary>
    private async Task<IReadOnlyList<ContractInterventionRow>> InterventionsAsync(InstrumentContract contract,
                                                                                 CancellationToken ct)
    {
        var ids = contract.Instruments.Select(i => i.InstrumentId).ToList();
        if (ids.Count == 0) return [];

        var start = contract.StartDate;
        var end = contract.EndDate;

        var records = await db.MaintenanceRecords.AsNoTracking()
            .Where(m => ids.Contains(m.InstrumentId) && m.PerformedOn >= start && m.PerformedOn <= end)
            .OrderByDescending(m => m.PerformedOn)
            .ToListAsync(ct);

        var codes = await db.Instruments.IgnoreQueryFilters().AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Code, ct);

        return [.. records.Select(m => new ContractInterventionRow(
            m.Id, m.InstrumentId, codes.GetValueOrDefault(m.InstrumentId) ?? "?", m.PerformedOn, m.Kind,
            m.Description.Fr, m.DowntimeHours, m.RequestedAt, m.ArrivedAt,
            ContractCommitment.InterventionDelayHours(m), ContractCommitment.IsDelayExceeded(contract, m),
            m.ExternalTechnicianName))];
    }

    public async Task<ContractOptions> GetOptionsAsync(Guid siteId, Guid? contractId, CancellationToken ct = default)
    {
        // What the edited contract already uses stays offered whatever its state: otherwise the editor
        // would fall back to another supplier and drop a decommissioned analyser or a deactivated
        // reagent on the next save.
        var current = contractId is { } id
            ? await db.InstrumentContracts.AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => new
                {
                    c.SupplierId,
                    Instruments = c.Instruments.Select(i => i.InstrumentId).ToList(),
                    Items = c.Reagents.Select(r => r.StockItemId).ToList()
                })
                .FirstOrDefaultAsync(ct)
            : null;

        var supplierId = current?.SupplierId ?? Guid.Empty;
        var instrumentIds = current?.Instruments ?? [];
        var itemIds = current?.Items ?? [];

        var suppliers = (await db.Suppliers.AsNoTracking()
                .Where(s => s.IsActive || s.Id == supplierId)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Code, s.Name, s.IsActive })
                .ToListAsync(ct))
            .Select(s => new ContractLookup(s.Id, s.Code, MultilingualText.FromFrench(s.Name), s.IsActive))
            .ToList();

        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && (i.Status != EquipmentStatus.Decommissioned || instrumentIds.Contains(i.Id)))
            .OrderBy(i => i.Code)
            .Select(i => new ContractLookup(i.Id, i.Code, i.Name, i.Status != EquipmentStatus.Decommissioned))
            .ToListAsync(ct);

        var items = await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && (i.IsActive || itemIds.Contains(i.Id)))
            .OrderBy(i => i.Code)
            .Select(i => new ContractLookup(i.Id, i.Code, i.Name, i.IsActive))
            .ToListAsync(ct);

        return new ContractOptions(suppliers, instruments, items);
    }

    public Task<bool> ReferenceExistsAsync(Guid supplierId, string reference, Guid? exceptContractId,
                                           CancellationToken ct = default) =>
        db.InstrumentContracts.IgnoreQueryFilters().AsNoTracking()
          .AnyAsync(c => c.SupplierId == supplierId && c.Reference == reference
                         && (exceptContractId == null || c.Id != exceptContractId), ct);

    public async Task<int> CountTestsAsync(IReadOnlyList<Guid> instrumentIds, IReadOnlyList<string> analysisCodes,
                                           DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (instrumentIds.Count == 0) return 0;

        var ids = instrumentIds.ToList();
        var codes = analysisCodes.ToList();
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(from.ToDateTime(TimeOnly.MinValue)));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue),
                                     TimeZoneInfo.Local.GetUtcOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue)));

        // Every site's results: the analyser counts what it ran, whichever site sent the tube.
        var results = db.TestResults.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.InstrumentId != null && ids.Contains(r.InstrumentId.Value)
                        && Produced.Contains(r.Status)
                        && (r.MeasuredAt ?? r.EnteredAt) >= start && (r.MeasuredAt ?? r.EnteredAt) < end);

        if (codes.Count > 0)
            results = results.Where(r => db.TestParameters
                .Where(p => p.Id == r.TestParameterId)
                .Any(p => db.AnalysisTests.Any(a => a.Id == p.AnalysisTestId && codes.Contains(a.Code))));

        // One test per ordered analysis, however many parameters it reports: a blood count is one
        // test on the placement contract, not twenty.
        return await results.Select(r => r.LabOrderItemId).Distinct().CountAsync(ct);
    }

    public async Task<decimal> SumPurchasesAsync(Guid supplierId, IReadOnlyList<Guid> stockItemIds, DateOnly from,
                                                 DateOnly to, CancellationToken ct = default)
    {
        if (stockItemIds.Count == 0) return 0m;

        var ids = stockItemIds.ToList();
        var lines = await db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.SupplierId == supplierId && Committed.Contains(o.Status)
                        && o.OrderDate >= from && o.OrderDate <= to)
            .SelectMany(o => o.Lines.Where(l => ids.Contains(l.StockItemId)))
            .Select(l => new { l.QuantityOrdered, l.UnitPriceMad, l.DiscountRate })
            .ToListAsync(ct);

        return Money.Round(lines.Sum(l => Money.Round(l.QuantityOrdered * l.UnitPriceMad * (1m - l.DiscountRate))));
    }

    public Task<(string FileName, byte[] Content)?> GetDocumentAsync(PurchasingDocumentOwner ownerKind, Guid ownerId,
                                                                     CancellationToken ct = default) =>
        PurchasingSql.LatestDocumentAsync(db, ownerKind, ownerId, ct);

    /// <summary>
    /// Captive reagents of the site's contracts in force today. Shared with the purchase-order and
    /// quotation queries, which need the same answer on the same context.
    /// </summary>
    internal static async Task<IReadOnlyList<CaptiveReagent>> CaptiveReagentsAsync(
        LisDbContext db, Guid siteId, DateOnly today, IReadOnlyList<Guid>? stockItemIds, CancellationToken ct)
    {
        var contracts = await db.InstrumentContracts.AsNoTracking()
            .Where(c => c.SiteId == siteId && c.IsActive && c.StartDate <= today)
            .ToListAsync(ct);

        var wanted = stockItemIds?.ToHashSet();

        return [.. contracts.Where(c => c.IsInForce(today))
                            .OrderByDescending(c => c.StartDate)
                            .SelectMany(c => c.Reagents
                                .Where(r => wanted is null || wanted.Contains(r.StockItemId))
                                .Select(r => new CaptiveReagent(r.StockItemId, c.Id, c.Reference, c.SupplierId,
                                                                r.ContractPriceMad)))];
    }
}

/// <summary>Reads shared by the purchasing queries.</summary>
internal static class PurchasingSql
{
    public static async Task<InvoiceIssuer> LaboratoryAsync(LisDbContext db, Guid siteId, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId)
            .Select(s => new
            {
                Name = s.Name.Fr, s.LegalName, Address = s.AddressLine.Fr, s.City, s.PostalCode, s.Phone, s.Email,
                s.Ice, s.FiscalId, s.CommercialRegisterNumber, s.CommercialRegisterCourt, s.ProfessionalTaxNumber,
                s.CnssAffiliationNumber, s.Inpe, s.LogoPng
            })
            .FirstOrDefaultAsync(ct);

        return site is null
            ? new InvoiceIssuer(string.Empty, string.Empty, null, null, null, null, null, null, null, null, null, null,
                                null, null, null)
            : new InvoiceIssuer(site.Name, site.LegalName, site.Address, site.City, site.PostalCode, site.Phone,
                                site.Email, site.Ice, site.FiscalId, site.CommercialRegisterNumber,
                                site.CommercialRegisterCourt, site.ProfessionalTaxNumber, site.CnssAffiliationNumber,
                                site.Inpe, site.LogoPng);
    }

    public static async Task<(string FileName, byte[] Content)?> LatestDocumentAsync(
        LisDbContext db, PurchasingDocumentOwner ownerKind, Guid ownerId, CancellationToken ct)
    {
        var document = await db.PurchasingDocuments.AsNoTracking()
            .Where(d => d.OwnerKind == ownerKind && d.OwnerId == ownerId)
            .OrderByDescending(d => d.AttachedAt)
            .Select(d => new { d.FileName, d.Content })
            .FirstOrDefaultAsync(ct);

        return document is null ? null : (document.FileName, document.Content);
    }
}
