using MarocLIS.Application.Orders;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class OrderFileQueries(LisDbContext db) : IOrderFileQueries
{
    /// <summary>
    /// Read from the audit journal by entity, which is how it is indexed: the file, its analyses,
    /// its tubes and its results are named one by one rather than found through the aggregate
    /// column, which is not. A save touches many rows under one correlation id; the history shows
    /// it once, at its first row, with the reason it was given.
    /// </summary>
    public async Task<IReadOnlyList<OrderFileEvent>> GetHistoryAsync(
        Guid orderId, Guid siteId, CancellationToken ct = default)
    {
        var order = await db.LabOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.SiteId == siteId)
            .Select(o => new { o.Id })
            .FirstOrDefaultAsync(ct);

        if (order is null) return [];

        var itemIds = await db.LabOrderItems.AsNoTracking()
            .Where(i => i.LabOrderId == orderId).Select(i => i.Id).ToListAsync(ct);
        var sampleIds = await db.Samples.AsNoTracking()
            .Where(s => s.LabOrderId == orderId).Select(s => s.Id).ToListAsync(ct);
        var resultIds = await db.TestResults.AsNoTracking()
            .Where(r => r.LabOrderId == orderId).Select(r => r.Id).ToListAsync(ct);

        var entities = new List<(string Name, List<Guid> Ids)>
        {
            (nameof(LabOrder), [orderId]),
            (nameof(LabOrderItem), itemIds),
            (nameof(Sample), sampleIds),
            (nameof(TestResult), resultIds)
        };

        var rows = new List<(Guid Id, DateTimeOffset At, string? User, string Reason, Guid? Correlation)>();

        foreach (var (name, ids) in entities.Where(e => e.Ids.Count > 0))
        {
            var found = await db.AuditEntries.AsNoTracking()
                .Where(a => a.EntityName == name && ids.Contains(a.EntityId) && a.Reason != null)
                .OrderByDescending(a => a.OccurredAt)
                .Take(IOrderFileQueries.HistoryLimit * 4)
                .Select(a => new { a.Id, a.OccurredAt, a.UserName, a.Reason, a.CorrelationId })
                .ToListAsync(ct);

            rows.AddRange(found.Select(a => (a.Id, a.OccurredAt, a.UserName, a.Reason!, a.CorrelationId)));
        }

        var changes = rows
            .GroupBy(r => r.Correlation ?? r.Id)
            .Select(g => g.OrderBy(r => r.At).First())
            .Select(r => new OrderFileEvent(r.At, OrderFileEventKind.Change, r.User, r.Reason, null));

        var reports = await db.LabReports.AsNoTracking()
            .Where(r => r.LabOrderId == orderId)
            .Select(r => new { r.ReleasedAt, r.VersionNumber, r.SignatoryName })
            .ToListAsync(ct);

        return
        [
            .. changes
                .Concat(reports.Select(r => new OrderFileEvent(r.ReleasedAt, OrderFileEventKind.ReportReleased,
                                                                r.SignatoryName, null, r.VersionNumber)))
                .OrderByDescending(e => e.OccurredAt)
                .Take(IOrderFileQueries.HistoryLimit)
                .OrderBy(e => e.OccurredAt)
        ];
    }
}
