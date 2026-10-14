using MarocLIS.Application.Samples;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class RecollectionQueries(LisDbContext db) : IRecollectionQueries
{
    public async Task<IReadOnlyList<RecollectionRow>> GetPendingAsync(Guid siteId, CancellationToken ct = default)
    {
        var rows = await (
                from rejected in db.Samples.AsNoTracking()
                join replacement in db.Samples.AsNoTracking() on rejected.ReplacementSampleId equals replacement.Id
                join o in db.LabOrders.AsNoTracking() on rejected.LabOrderId equals o.Id
                where rejected.Status == SampleStatus.Rejected
                      && rejected.RecollectionRequested
                      && replacement.Status == SampleStatus.AwaitingCollection
                      && o.SiteId == siteId
                      && o.Status != LabOrderStatus.Cancelled
                select new
                {
                    RejectedId = rejected.Id,
                    ReplacementId = replacement.Id,
                    OrderId = o.Id,
                    o.AccessionNumber,
                    o.Patient!.LastName,
                    o.Patient!.FirstName,
                    o.Patient!.MobilePhone,
                    o.Patient!.AlternatePhone,
                    o.Priority,
                    RejectedBarcode = rejected.Barcode,
                    ReplacementBarcode = replacement.Barcode,
                    rejected.RejectionReason,
                    rejected.RejectionComment,
                    rejected.RejectedAt
                })
            .Take(500)
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        var replacementIds = rows.Select(r => r.ReplacementId).ToList();

        var codes = await (
                from i in db.LabOrderItems.AsNoTracking()
                join t in db.AnalysisTests.AsNoTracking() on i.AnalysisTestId equals t.Id
                where i.SampleId != null && replacementIds.Contains(i.SampleId.Value)
                      && i.Status != LabOrderItemStatus.Cancelled
                select new { SampleId = i.SampleId!.Value, t.Code })
            .ToListAsync(ct);

        var contacts = await db.Set<SampleEvent>().AsNoTracking()
            .Where(e => replacementIds.Contains(e.SampleId) && e.Kind == SampleEventKind.PatientRecalled)
            .Select(e => new { e.SampleId, e.OccurredAt, e.Detail })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r =>
                {
                    var calls = contacts.Where(c => c.SampleId == r.ReplacementId)
                                        .OrderByDescending(c => c.OccurredAt)
                                        .ToList();

                    return new RecollectionRow(
                        r.RejectedId,
                        r.ReplacementId,
                        r.OrderId,
                        r.AccessionNumber,
                        $"{r.LastName} {r.FirstName}".Trim(),
                        string.IsNullOrWhiteSpace(r.MobilePhone) ? r.AlternatePhone : r.MobilePhone,
                        r.Priority,
                        r.RejectedBarcode,
                        r.ReplacementBarcode,
                        r.RejectionReason ?? SampleRejectionReason.Other,
                        r.RejectionComment,
                        r.RejectedAt ?? DateTimeOffset.MinValue,
                        string.Join(" ", codes.Where(c => c.SampleId == r.ReplacementId)
                                              .Select(c => c.Code).Distinct().Order(StringComparer.Ordinal)),
                        calls.Count,
                        calls.FirstOrDefault()?.OccurredAt,
                        calls.FirstOrDefault()?.Detail);
                })
               .OrderByDescending(r => r.Priority)
               .ThenBy(r => r.RejectedAt)
        ];
    }
}
