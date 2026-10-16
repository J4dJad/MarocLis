using MarocLIS.Application.Orders;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class OrderEntryQueries(LisDbContext db) : IOrderEntryQueries
{
    /// <summary>
    /// Counted per file, not per analysis: a profile of six analyses ordered once is one order of
    /// the profile, not six.
    /// </summary>
    public async Task<IReadOnlyList<FrequentPanel>> GetFrequentPanelsAsync(
        Guid siteId, DateTimeOffset since, int take = 5, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 20);

        var counts = await db.LabOrderItems.AsNoTracking()
            .Where(i => i.TestPanelId != null && i.Status != LabOrderItemStatus.Cancelled)
            .Join(db.LabOrders.AsNoTracking()
                      .Where(o => o.SiteId == siteId && o.RegisteredAt >= since
                                  && o.Status != LabOrderStatus.Cancelled),
                  i => i.LabOrderId, o => o.Id, (i, o) => new { PanelId = i.TestPanelId!.Value, OrderId = o.Id })
            .Distinct()
            .GroupBy(x => x.PanelId)
            .Select(g => new { PanelId = g.Key, Orders = g.Count() })
            .ToListAsync(ct);

        var panels = await db.TestPanels.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Code)
            .Select(p => new { p.Id, p.Code, p.Name })
            .ToListAsync(ct);

        var ordered = counts.ToDictionary(c => c.PanelId, c => c.Orders);

        // Most ordered first; the catalogue's order breaks ties and fills the row on a new site.
        return
        [
            .. panels
                .Select((p, index) => (p, index, Orders: ordered.GetValueOrDefault(p.Id)))
                .OrderByDescending(x => x.Orders).ThenBy(x => x.index)
                .Take(take)
                .Select(x => new FrequentPanel(x.p.Id, x.p.Code, x.p.Name, x.Orders))
        ];
    }

    public async Task<IReadOnlyList<RecentAnalysis>> GetRecentAnalysesAsync(
        Guid patientId, DateTimeOffset since, CancellationToken ct = default) =>
        await db.LabOrderItems.AsNoTracking()
            .Where(i => i.Status != LabOrderItemStatus.Cancelled)
            .Join(db.LabOrders.AsNoTracking()
                      .Where(o => o.PatientId == patientId && o.RegisteredAt >= since
                                  && o.Status != LabOrderStatus.Cancelled),
                  i => i.LabOrderId, o => o.Id,
                  (i, o) => new { i.AnalysisTestId, o.RegisteredAt, o.AccessionNumber })
            .OrderByDescending(x => x.RegisteredAt)
            .Select(x => new RecentAnalysis(x.AnalysisTestId, x.RegisteredAt, x.AccessionNumber))
            .ToListAsync(ct);
}
