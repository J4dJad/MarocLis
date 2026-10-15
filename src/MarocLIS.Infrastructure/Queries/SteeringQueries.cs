using MarocLIS.Application.Statistics;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class CostQueries(LisDbContext db) : ICostQueries
{
    public async Task<IReadOnlyList<CostActivityRow>> GetActivityAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start,
                                                                       DateTimeOffset end, CancellationToken ct = default) =>
        await db.LabOrderItems.AsNoTracking()
            .Where(i => siteIds.Contains(i.LabOrder!.SiteId)
                        && i.LabOrder.RegisteredAt >= start && i.LabOrder.RegisteredAt < end
                        && i.LabOrder.Status != LabOrderStatus.Cancelled
                        && i.Status != LabOrderItemStatus.Cancelled
                        && !i.IsSubcontracted)
            .Select(i => new CostActivityRow(
                i.LabOrder!.SiteId,
                i.AnalysisTestId,
                i.AnalysisTest!.Code,
                db.TestResults.Where(r => r.LabOrderItemId == i.Id && r.InstrumentId != null)
                              .Select(r => r.InstrumentId).FirstOrDefault(),
                db.TestResults.Where(r => r.LabOrderItemId == i.Id && r.ReagentLotId != null)
                              .Select(r => r.ReagentLotId).FirstOrDefault(),
                i.ChargedPrice))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CostReagentItem>> GetReagentsAsync(IReadOnlyList<Guid> siteIds, DateOnly asOf,
                                                                       CancellationToken ct = default) =>
        await db.StockItems.AsNoTracking()
            .Where(i => siteIds.Contains(i.SiteId) && i.Kind == StockItemKind.Reagent && i.UsedByAnalysisCodesCsv != null)
            .Select(i => new CostReagentItem(
                i.Id, i.SiteId, i.Code, i.UsedByAnalysisCodesCsv, i.TestsPerResultCsv, i.TestsPerPack,
                i.DeadVolumePercent, i.LastUnitPriceMad,
                db.ReagentLots.Where(l => l.StockItemId == i.Id && l.UnitPriceMad != null && l.ReceivedOn <= asOf
                                          && !l.IsRejected)
                              .OrderByDescending(l => l.ReceivedOn)
                              .Select(l => l.UnitPriceMad).FirstOrDefault()))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CostContractReagent>> GetContractReagentsAsync(IReadOnlyList<Guid> siteIds, DateOnly asOf,
                                                                                   CancellationToken ct = default)
    {
        // The end date is computed from the duration, so "in force" is decided here rather than in SQL.
        var contracts = await db.InstrumentContracts.AsNoTracking()
            .Where(c => siteIds.Contains(c.SiteId) && c.IsActive && c.StartDate <= asOf)
            .ToListAsync(ct);

        return contracts.Where(c => c.IsInForce(asOf))
            .SelectMany(c => c.Reagents.Select(r => new CostContractReagent(
                c.SiteId, r.StockItemId, [.. c.Instruments.Select(i => i.InstrumentId)], r.ContractPriceMad,
                r.CostPerTestMad)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, (Guid StockItemId, decimal? UnitPriceMad)>> GetLotPricesAsync(
        IReadOnlyList<Guid> lotIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, (Guid, decimal?)>();
        foreach (var slice in lotIds.Chunk(1000))
        {
            var rows = await db.ReagentLots.AsNoTracking()
                .Where(l => slice.Contains(l.Id))
                .Select(l => new { l.Id, l.StockItemId, l.UnitPriceMad })
                .ToListAsync(ct);
            foreach (var r in rows) result[r.Id] = (r.StockItemId, r.UnitPriceMad);
        }
        return result;
    }

    public async Task<CostLabels> GetLabelsAsync(CancellationToken ct = default)
    {
        var tests = await db.AnalysisTests.AsNoTracking().IgnoreQueryFilters()
            .Select(t => new { t.Id, t.Code, t.Name }).ToListAsync(ct);
        var sites = await db.Sites.AsNoTracking().IgnoreQueryFilters()
            .Select(s => new { s.Id, s.Code, s.Name }).ToListAsync(ct);
        var instruments = await db.Instruments.AsNoTracking().IgnoreQueryFilters()
            .Select(i => new { i.Id, i.Code, i.Name }).ToListAsync(ct);

        return new CostLabels(
            tests.ToDictionary(t => t.Id, t => (t.Code, t.Name)),
            sites.ToDictionary(s => s.Id, s => (s.Code, s.Name)),
            instruments.ToDictionary(i => i.Id, i => (i.Code, i.Name)));
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix,
                                                                                  CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.SiteId == null && s.Key.StartsWith(prefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }
}

public sealed class SteeringQueries(LisDbContext db) : ISteeringQueries
{
    public async Task<IReadOnlyList<SteeringActivityRow>> GetActivityAsync(IReadOnlyList<Guid> siteIds,
                                                                           DateTimeOffset start, DateTimeOffset end,
                                                                           CancellationToken ct = default) =>
        await db.LabOrderItems.AsNoTracking()
            .Where(i => siteIds.Contains(i.LabOrder!.SiteId)
                        && i.LabOrder.RegisteredAt >= start && i.LabOrder.RegisteredAt < end)
            .Select(i => new SteeringActivityRow(
                i.LabOrder!.SiteId,
                i.LabOrder.PrescriberId,
                new ActivityItem(
                    i.LabOrderId,
                    i.LabOrder.PatientId,
                    i.LabOrder.RegisteredAt,
                    i.LabOrder.PayerId,
                    i.AnalysisTestId,
                    i.DepartmentId,
                    i.Status,
                    i.ReceivedAt,
                    i.ValidatedAt,
                    i.DueAt,
                    i.ChargedPrice,
                    i.PayerShare,
                    i.PatientShare)))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ReceivableRow>> GetReceivablesAsync(IReadOnlyList<Guid> siteIds,
                                                                        CancellationToken ct = default) =>
        await db.Invoices.AsNoTracking()
            .Where(i => siteIds.Contains(i.SiteId) && i.Kind == InvoiceKind.Invoice
                        && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid))
            .Select(i => new ReceivableRow(i.SiteId, i.Kind, i.IssueDate, i.DueDate, i.TotalAmount, i.PaidAmount,
                                           i.CreditedAmount))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PayrollMonthRow>> GetClosedPayrollAsync(IReadOnlyList<Guid> siteIds, DateOnly from,
                                                                            DateOnly to, CancellationToken ct = default)
    {
        var first = from.Year * 12 + from.Month;
        var last = to.Year * 12 + to.Month;

        var periods = await db.PayrollPeriods.AsNoTracking()
            .Where(p => siteIds.Contains(p.SiteId) && p.Status == PayrollStatus.Closed
                        && p.Year * 12 + p.Month >= first && p.Year * 12 + p.Month <= last)
            .Select(p => new { p.Id, p.SiteId, p.Year, p.Month })
            .ToListAsync(ct);

        var ids = periods.Select(p => p.Id).ToList();
        var slips = await db.Payslips.AsNoTracking()
            .Where(s => ids.Contains(s.PayrollPeriodId))
            .Select(s => new { s.PayrollPeriodId, s.Amounts.Gross, s.Amounts.EmployerCost })
            .ToListAsync(ct);

        return periods.Select(p =>
            {
                var own = slips.Where(s => s.PayrollPeriodId == p.Id).ToList();
                return new PayrollMonthRow(p.SiteId, p.Year, p.Month, own.Count, own.Sum(s => s.Gross),
                                           own.Sum(s => s.EmployerCost));
            })
            .ToList();
    }

    public async Task<QualityCounts> GetQualityAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start, DateTimeOffset end,
                                                     DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var qc = db.QcResults.AsNoTracking()
            .Where(r => siteIds.Contains(r.SiteId) && r.MeasuredAt >= start && r.MeasuredAt < end);
        var qcResults = await qc.CountAsync(ct);
        var qcRejected = await qc.CountAsync(r => r.Outcome == QcOutcome.Rejected, ct);

        var nc = db.NonConformities.AsNoTracking().Where(n => siteIds.Contains(n.SiteId));
        var open = await nc.CountAsync(n => n.Status == NonConformityStatus.Open
                                            || n.Status == NonConformityStatus.InProgress
                                            || n.Status == NonConformityStatus.AwaitingVerification, ct);
        var opened = await nc.CountAsync(n => n.DetectedAt >= start && n.DetectedAt < end, ct);

        var eqa = await db.EqaParticipations.AsNoTracking()
            .Where(e => siteIds.Contains(e.SiteId) && e.RoundDate >= from && e.RoundDate <= to)
            .GroupBy(e => e.Outcome)
            .Select(g => new { Outcome = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new QualityCounts(qcResults, qcRejected, open, opened, eqa.ToDictionary(e => e.Outcome, e => e.Count));
    }

    public async Task<SteeringLabels> GetLabelsAsync(CancellationToken ct = default)
    {
        var sites = await db.Sites.AsNoTracking().IgnoreQueryFilters()
            .Select(s => new { s.Id, s.Code, s.Name }).ToListAsync(ct);
        var payers = await db.Payers.AsNoTracking().IgnoreQueryFilters()
            .Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var prescribers = await new PrescriberStatisticsQueries(db).GetLabelsAsync(ct);

        return new SteeringLabels(
            sites.ToDictionary(s => s.Id, s => $"{s.Code} — {s.Name.Fr}"),
            payers.ToDictionary(p => p.Id, p => p.Name),
            prescribers.ToDictionary(p => p.Key, p => p.Value.Name));
    }
}
