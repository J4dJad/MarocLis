using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>The reads of « Remises »: the discounts granted over a period, and a convention's negotiated prices.</summary>
public sealed class DiscountQueries(LisDbContext db) : IDiscountQueries
{
    public async Task<IReadOnlyList<DiscountReportRow>> GetDiscountsAsync(IReadOnlyCollection<Guid> siteIds, DateTimeOffset start,
                                                                          DateTimeOffset end, DateOnly from, DateOnly to,
                                                                          CancellationToken ct = default)
    {
        var sites = siteIds.ToList();

        var orders = await db.LabOrders.AsNoTracking().IgnoreQueryFilters()
            .Where(o => sites.Contains(o.SiteId) && o.RegisteredAt >= start && o.RegisteredAt < end && o.DiscountAmount > 0m)
            .Select(o => new
            {
                o.SiteId,
                o.RegisteredAt,
                o.AccessionNumber,
                o.PatientId,
                o.DiscountSource,
                o.DiscountPercent,
                o.DiscountAmount,
                o.DiscountReason,
                o.PartnershipId,
                o.PrescriberId,
                o.DiscountProfileId,
                o.RegisteredByUserId,
                o.DiscountApprovedByUserId,
                Charged = db.LabOrderItems.Where(i => i.LabOrderId == o.Id).Sum(i => (decimal?)i.ChargedPrice) ?? 0m
            })
            .ToListAsync(ct);

        var invoices = await db.Invoices.AsNoTracking().IgnoreQueryFilters()
            .Where(i => sites.Contains(i.SiteId) && i.Kind == InvoiceKind.Invoice && i.IssuedAt != null
                        && i.IssueDate >= from && i.IssueDate <= to && i.DiscountAmount > 0m)
            .Select(i => new
            {
                i.SiteId,
                IssuedAt = i.IssuedAt!.Value,
                i.Number,
                i.PatientId,
                i.BilledToName,
                i.DiscountAmount,
                i.GrossAmount,
                i.DiscountReason,
                i.IssuedByUserId,
                i.DiscountApprovedByUserId
            })
            .ToListAsync(ct);

        var patientIds = orders.Select(o => o.PatientId).Concat(invoices.Where(i => i.PatientId != null).Select(i => i.PatientId!.Value))
                               .Distinct().ToList();
        var patients = await db.Patients.AsNoTracking().IgnoreQueryFilters()
            .Where(p => patientIds.Contains(p.Id))
            .Select(p => new { p.Id, p.LastName, p.FirstName, p.MedicalRecordNumber })
            .ToDictionaryAsync(p => p.Id, ct);

        var userIds = orders.Select(o => o.RegisteredByUserId).Concat(orders.Where(o => o.DiscountApprovedByUserId != null).Select(o => o.DiscountApprovedByUserId!.Value))
                            .Concat(invoices.Select(i => i.IssuedByUserId)).Concat(invoices.Where(i => i.DiscountApprovedByUserId != null).Select(i => i.DiscountApprovedByUserId!.Value))
                            .Distinct().ToList();
        var users = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var partnershipIds = orders.Where(o => o.PartnershipId != null).Select(o => o.PartnershipId!.Value).Distinct().ToList();
        var partnerships = await db.Partnerships.AsNoTracking().IgnoreQueryFilters()
            .Where(p => partnershipIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.PartnerName, ct);

        var profileIds = orders.Where(o => o.DiscountProfileId != null).Select(o => o.DiscountProfileId!.Value).Distinct().ToList();
        var profiles = await db.DiscountProfiles.AsNoTracking()
            .Where(p => profileIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var prescriberIds = orders.Where(o => o.DiscountSource == DiscountSource.Prescriber && o.PrescriberId != null)
                                  .Select(o => o.PrescriberId!.Value).Distinct().ToList();
        var prescribers = (await db.Prescribers.AsNoTracking().IgnoreQueryFilters()
                .Where(p => prescriberIds.Contains(p.Id))
                .ToListAsync(ct))
            .ToDictionary(p => p.Id, p => p.FullName);

        string Patient(Guid id, string fallback) =>
            patients.TryGetValue(id, out var p) ? $"{p.LastName} {p.FirstName}".Trim() : fallback;
        string? Record(Guid? id) => id is { } key && patients.TryGetValue(key, out var p) ? p.MedicalRecordNumber : null;
        string? User(Guid? id) => id is { } key && users.TryGetValue(key, out var name) ? name : null;

        string? Label(DiscountSource source, Guid? partnershipId, Guid? profileId, Guid? prescriberId) => source switch
        {
            DiscountSource.Convention => partnershipId is { } c && partnerships.TryGetValue(c, out var n) ? n : null,
            DiscountSource.Profile => profileId is { } p && profiles.TryGetValue(p, out var n) ? n : null,
            DiscountSource.Prescriber => prescriberId is { } d && prescribers.TryGetValue(d, out var n) ? n : null,
            _ => null
        };

        var rows = new List<DiscountReportRow>(orders.Count + invoices.Count);

        rows.AddRange(orders.Select(o => new DiscountReportRow(
            o.SiteId, o.RegisteredAt, false, o.AccessionNumber, Patient(o.PatientId, string.Empty), Record(o.PatientId),
            o.DiscountSource, Label(o.DiscountSource, o.PartnershipId, o.DiscountProfileId, o.PrescriberId),
            o.DiscountPercent, o.DiscountAmount, o.Charged + o.DiscountAmount, o.DiscountReason,
            User(o.RegisteredByUserId), User(o.DiscountApprovedByUserId))));

        rows.AddRange(invoices.Select(i => new DiscountReportRow(
            i.SiteId, i.IssuedAt, true, i.Number,
            i.PatientId is { } patientId ? Patient(patientId, i.BilledToName) : i.BilledToName, Record(i.PatientId),
            DiscountSource.Manual, null,
            i.GrossAmount > 0m ? Math.Round(i.DiscountAmount * 100m / i.GrossAmount, 2) : null,
            i.DiscountAmount, i.GrossAmount, i.DiscountReason, User(i.IssuedByUserId), User(i.DiscountApprovedByUserId))));

        return rows;
    }

    public async Task<IReadOnlyList<PartnershipPriceRow>> GetPartnershipPricesAsync(Guid partnershipId, DateOnly on,
                                                                                    CancellationToken ct = default)
    {
        var prices = await db.PartnershipPrices.AsNoTracking()
            .Where(p => p.PartnershipId == partnershipId)
            .ToListAsync(ct);

        var analysisIds = prices.Select(p => p.AnalysisTestId).Distinct().ToList();
        var analyses = await db.AnalysisTests.AsNoTracking().IgnoreQueryFilters()
            .Where(a => analysisIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Code, a.Name })
            .ToDictionaryAsync(a => a.Id, ct);

        // The laboratory's own price on the day: the tariff without a payer, the latest in force.
        var tariffs = await db.Tariffs.AsNoTracking()
            .Where(t => analysisIds.Contains(t.AnalysisTestId) && t.PayerId == null && t.ValidFrom <= on
                        && (t.ValidUntil == null || t.ValidUntil >= on))
            .Select(t => new { t.AnalysisTestId, t.ValidFrom, t.Price })
            .ToListAsync(ct);
        var listPrices = tariffs.GroupBy(t => t.AnalysisTestId)
                                .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.ValidFrom).First().Price);

        return prices
            .Select(p =>
            {
                var analysis = analyses.GetValueOrDefault(p.AnalysisTestId);
                return new PartnershipPriceRow(
                    p.Id, p.PartnershipId, p.AnalysisTestId, analysis?.Code ?? "?", analysis?.Name ?? new Domain.Common.MultilingualText("?"),
                    listPrices.TryGetValue(p.AnalysisTestId, out var list) ? list : null,
                    p.FixedPrice, p.DiscountPercent, p.ValidFrom, p.ValidUntil, p.IsValidOn(on));
            })
            .OrderBy(r => r.AnalysisCode, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.ValidFrom)
            .ToList();
    }
}
