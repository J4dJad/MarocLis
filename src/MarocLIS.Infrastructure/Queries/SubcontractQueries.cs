using MarocLIS.Application.Samples;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class SubcontractQueries(LisDbContext db) : ISubcontractQueries
{
    /// <summary>
    /// Acts to send first, oldest reception first, then those awaiting return, oldest dispatch
    /// first: the order in which they need chasing.
    /// </summary>
    public async Task<IReadOnlyList<SubcontractRow>> GetWorklistAsync(Guid siteId, CancellationToken ct = default)
    {
        var rows = await Rows(db.LabOrderItems.AsNoTracking()
                .Where(i => i.IsSubcontracted
                            && i.Status != LabOrderItemStatus.Cancelled
                            && i.ResultEnteredAt == null
                            && (i.SentToSubcontractorAt != null
                                || i.Status == LabOrderItemStatus.Received
                                || i.Status == LabOrderItemStatus.InProgress)
                            && db.LabOrders.Any(o => o.Id == i.LabOrderId
                                                     && o.SiteId == siteId
                                                     && o.Status != LabOrderStatus.Cancelled)))
            .Take(1000)
            .ToListAsync(ct);

        return
        [
            .. rows.OrderBy(r => r.SentAt is not null)
                   .ThenByDescending(r => r.Priority)
                   .ThenBy(r => r.SentAt ?? r.ReceivedAt)
        ];
    }

    public async Task<IReadOnlyList<SubcontractPartner>> GetPartnersAsync(CancellationToken ct = default) =>
        await db.Sites.AsNoTracking()
            .Where(s => s.IsActive && s.Kind == LabSiteKind.ReferralPartner)
            .OrderBy(s => s.Code)
            .Select(s => new SubcontractPartner(s.Id, s.Code, s.Name))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetOrderIdsForItemsAsync(IReadOnlyList<Guid> itemIds,
                                                                    CancellationToken ct = default) =>
        await db.LabOrderItems.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .Select(i => i.LabOrderId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<SubcontractSlip?> GetSlipAsync(Guid siteId, string dispatchNumber,
                                                     CancellationToken ct = default)
    {
        var items = await Rows(db.LabOrderItems.AsNoTracking()
                .Where(i => i.SubcontractDispatchNumber == dispatchNumber
                            && db.LabOrders.Any(o => o.Id == i.LabOrderId && o.SiteId == siteId)))
            .ToListAsync(ct);

        if (items.Count == 0) return null;

        var first = items.OrderBy(i => i.SentAt).First();

        var partner = await db.Sites.AsNoTracking()
            .Where(s => s.Id == first.PartnerSiteId)
            .Select(s => new SubcontractPartner(s.Id, s.Code, s.Name))
            .FirstOrDefaultAsync(ct);

        if (partner is null) return null;

        // The slip numbers are per site, so the same number exists at another site, sent by somebody else.
        var sentBy = await db.LabOrderItems.AsNoTracking()
            .Where(i => i.SubcontractDispatchNumber == dispatchNumber
                        && db.LabOrders.Any(o => o.Id == i.LabOrderId && o.SiteId == siteId))
            .Select(i => db.Users.Where(u => u.Id == i.SubcontractSentByUserId).Select(u => u.FullName).FirstOrDefault())
            .FirstOrDefaultAsync(ct);

        return new SubcontractSlip(dispatchNumber, partner, first.SentAt!.Value, sentBy ?? string.Empty,
                                   [.. items.OrderBy(i => i.AccessionNumber, StringComparer.Ordinal)
                                            .ThenBy(i => i.AnalysisCode, StringComparer.Ordinal)]);
    }

    private IQueryable<SubcontractRow> Rows(IQueryable<LabOrderItem> items) =>
        from i in items
        join o in db.LabOrders on i.LabOrderId equals o.Id
        join t in db.AnalysisTests on i.AnalysisTestId equals t.Id
        join s in db.Samples on i.SampleId equals s.Id into sampleJoin
        from s in sampleJoin.DefaultIfEmpty()
        join site in db.Sites on i.SubcontractorSiteId equals site.Id into siteJoin
        from site in siteJoin.DefaultIfEmpty()
        select new SubcontractRow(
            i.Id,
            o.Id,
            o.AccessionNumber,
            o.Patient!.LastName + " " + o.Patient!.FirstName,
            o.Patient!.Gender,
            o.Patient!.DateOfBirth,
            i.Priority,
            t.Code,
            t.Name,
            s == null ? null : s.Barcode,
            s == null ? null : db.ContainerTypes.Where(c => c.Id == s.ContainerTypeId).Select(c => c.Name).FirstOrDefault(),
            s == null ? null : s.CollectedAt,
            i.ReceivedAt,
            i.SubcontractorSiteId,
            site == null ? null : site.Code,
            site == null ? null : site.Name,
            i.SentToSubcontractorAt,
            i.SubcontractDispatchNumber,
            i.SubcontractorReference,
            i.DueAt);
}
