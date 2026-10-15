using MarocLIS.Application.Relations;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Relations;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class PrescriberStatisticsQueries(LisDbContext db) : IPrescriberStatisticsQueries
{
    public async Task<IReadOnlyList<PrescriberOrderRow>> GetOrdersAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start,
                                                                        DateTimeOffset end, CancellationToken ct = default)
    {
        // Aggregated per order in SQL: three windows of up to a year each would otherwise bring
        // every test of three years into memory.
        var rows = await db.LabOrders.AsNoTracking()
            .Where(o => siteIds.Contains(o.SiteId) && o.RegisteredAt >= start && o.RegisteredAt < end
                        && o.Status != LabOrderStatus.Cancelled)
            .Select(o => new
            {
                o.Id,
                o.SiteId,
                o.PrescriberId,
                o.RegisteredAt,
                Tests = db.LabOrderItems.Count(i => i.LabOrderId == o.Id && i.Status != LabOrderItemStatus.Cancelled),
                Revenue = db.LabOrderItems.Where(i => i.LabOrderId == o.Id && i.Status != LabOrderItemStatus.Cancelled)
                                          .Sum(i => (decimal?)i.ChargedPrice) ?? 0m
            })
            .ToListAsync(ct);

        return rows.Select(r => new PrescriberOrderRow(r.Id, r.SiteId, r.PrescriberId, r.RegisteredAt, r.Tests, r.Revenue))
                   .ToList();
    }

    public async Task<IReadOnlySet<Guid>> GetActivePrescriberIdsAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start,
                                                                      DateTimeOffset end, CancellationToken ct = default)
    {
        var ids = await db.LabOrders.AsNoTracking()
            .Where(o => siteIds.Contains(o.SiteId) && o.RegisteredAt >= start && o.RegisteredAt < end
                        && o.Status != LabOrderStatus.Cancelled && o.PrescriberId != null)
            .Select(o => o.PrescriberId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, PrescriberLabel>> GetLabelsAsync(CancellationToken ct = default)
    {
        var rows = await db.Prescribers.AsNoTracking().IgnoreQueryFilters()
            .Select(p => new { p.Id, p.LastName, p.FirstName, p.InstitutionName, p.Kind, p.City })
            .ToListAsync(ct);

        return rows.ToDictionary(
            p => p.Id,
            p => new PrescriberLabel(
                p.Id,
                p.Kind == PrescriberKind.Institution
                    ? p.InstitutionName ?? p.LastName
                    : $"Dr {p.LastName} {p.FirstName}".Trim(),
                string.IsNullOrWhiteSpace(p.InstitutionName) ? null : p.InstitutionName.Trim(),
                p.Kind, p.City));
    }
}

public sealed class RelationsQueries(LisDbContext db) : IRelationsQueries
{
    public async Task<IReadOnlyList<DelegateVisitRow>> GetVisitsAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                      Guid? prescriberId, DateOnly today,
                                                                      CancellationToken ct = default)
    {
        var query = db.DelegateVisits.AsNoTracking()
            .Where(v => v.SiteId == siteId && v.VisitDate >= from && v.VisitDate <= to);
        if (prescriberId is { } id) query = query.Where(v => v.PrescriberId == id);

        return await Project(query.OrderByDescending(v => v.VisitDate), today, ct);
    }

    public async Task<IReadOnlyList<DelegateVisitRow>> GetDueVisitsAsync(Guid siteId, DateOnly today,
                                                                         CancellationToken ct = default)
    {
        // The latest visit per prescriber decides: an earlier "next visit" date is answered by it.
        var query = db.DelegateVisits.AsNoTracking()
            .Where(v => v.SiteId == siteId && v.NextVisitOn != null && v.NextVisitOn <= today
                        && !db.DelegateVisits.Any(l => l.SiteId == siteId && l.PrescriberId == v.PrescriberId
                                                       && l.VisitDate > v.VisitDate))
            .OrderBy(v => v.NextVisitOn);

        return await Project(query, today, ct);
    }

    private async Task<IReadOnlyList<DelegateVisitRow>> Project(IQueryable<DelegateVisit> query, DateOnly today,
                                                                CancellationToken ct)
    {
        var rows = await query
            .Select(v => new
            {
                v.Id, v.VisitDate, v.PrescriberId, v.DelegateUserId, v.Purpose, v.Notes, v.NextVisitOn,
                Prescriber = db.Prescribers.IgnoreQueryFilters().Where(p => p.Id == v.PrescriberId)
                    .Select(p => new { p.Kind, p.LastName, p.FirstName, p.InstitutionName }).FirstOrDefault(),
                Delegate = db.Users.IgnoreQueryFilters().Where(u => u.Id == v.DelegateUserId)
                    .Select(u => u.FullName).FirstOrDefault()
            })
            .Take(2000)
            .ToListAsync(ct);

        return rows.Select(r => new DelegateVisitRow(
            r.Id, r.VisitDate, r.PrescriberId,
            r.Prescriber is null ? "?"
                : r.Prescriber.Kind == PrescriberKind.Institution
                    ? r.Prescriber.InstitutionName ?? r.Prescriber.LastName
                    : $"Dr {r.Prescriber.LastName} {r.Prescriber.FirstName}".Trim(),
            r.Prescriber?.InstitutionName,
            r.DelegateUserId, r.Delegate ?? "?", r.Purpose, r.Notes, r.NextVisitOn,
            r.NextVisitOn is { } next && next <= today)).ToList();
    }

    public async Task<RelationOptions> GetOptionsAsync(Guid siteId, CancellationToken ct = default)
    {
        var prescribers = await db.Prescribers.AsNoTracking()
            .Where(p => p.IsActive && p.Kind != PrescriberKind.SelfReferral)
            .OrderBy(p => p.Kind == PrescriberKind.Institution ? p.InstitutionName : p.LastName).ThenBy(p => p.FirstName)
            .Select(p => new { p.Id, p.Kind, p.LastName, p.FirstName, p.InstitutionName, p.City })
            .ToListAsync(ct);

        var staff = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.PrescriberId == null
                        && db.UserSiteAccess.Any(a => a.UserId == u.Id && a.SiteId == siteId))
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync(ct);

        return new RelationOptions(
            prescribers.Select(p => new RelationLookup(p.Id,
                (p.Kind == PrescriberKind.Institution
                    ? p.InstitutionName ?? p.LastName
                    : $"Dr {p.LastName} {p.FirstName}".Trim())
                + (string.IsNullOrWhiteSpace(p.City) ? string.Empty : $" — {p.City}"))).ToList(),
            staff.Select(u => new RelationLookup(u.Id, u.FullName)).ToList());
    }

    public async Task<IReadOnlyList<PartnershipRow>> GetPartnershipsAsync(Guid siteId, bool includeInactive, DateOnly today,
                                                                          CancellationToken ct = default)
    {
        var query = db.Partnerships.AsNoTracking().Where(p => p.SiteId == siteId);
        if (!includeInactive) query = query.Where(p => p.IsActive);

        var rows = await query
            .OrderBy(p => p.PartnerName)
            .Select(p => new
            {
                Entity = p,
                FileName = db.PartnershipDocuments.Where(d => d.PartnershipId == p.Id)
                    .OrderByDescending(d => d.AttachedAt).Select(d => d.FileName).FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows.Select(r => new PartnershipRow(
            r.Entity.Id, r.Entity.PartnerName, r.Entity.PartnerKind, r.Entity.Kind, r.Entity.Reference,
            r.Entity.StartDate, r.Entity.EndDate, r.Entity.IsActive, r.Entity.IsInForce(today),
            r.Entity.IsEndingSoon(today), r.Entity.DiscountPercent, r.Entity.ContactName, r.Entity.ContactPhone,
            r.FileName)).ToList();
    }

    public async Task<(string FileName, byte[] Content)?> GetLatestDocumentAsync(Guid partnershipId,
                                                                                CancellationToken ct = default)
    {
        var file = await db.PartnershipDocuments.AsNoTracking()
            .Where(d => d.PartnershipId == partnershipId)
            .OrderByDescending(d => d.AttachedAt)
            .Select(d => new { d.FileName, d.Content })
            .FirstOrDefaultAsync(ct);

        return file is null ? null : (file.FileName, file.Content);
    }
}

public sealed class PatientMessagingQueries(LisDbContext db) : IPatientMessagingQueries
{
    public async Task<IReadOnlyList<ReminderRuleRow>> GetRulesAsync(CancellationToken ct = default)
    {
        var rules = await db.ReminderRules.AsNoTracking().ToListAsync(ct);
        var targets = (await GetTargetsCoreAsync(includeInactive: true, ct)).ToDictionary(t => t.Id);

        return rules.Select(r =>
            {
                var id = r.AnalysisTestId ?? r.TestPanelId ?? Guid.Empty;
                var target = targets.GetValueOrDefault(id);
                return new ReminderRuleRow(r.Id, r.AnalysisTestId, r.TestPanelId, target?.Code ?? "?",
                                           target?.Name ?? new MultilingualText("?"), r.DelayMonths, r.WindowDays,
                                           r.Template, r.IsActive);
            })
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<ReminderTarget>> GetTargetsAsync(CancellationToken ct = default) =>
        await GetTargetsCoreAsync(includeInactive: false, ct);

    private async Task<IReadOnlyList<ReminderTarget>> GetTargetsCoreAsync(bool includeInactive, CancellationToken ct)
    {
        var tests = db.AnalysisTests.AsNoTracking();
        var panels = db.TestPanels.AsNoTracking();
        if (includeInactive)
        {
            tests = tests.IgnoreQueryFilters();
            panels = panels.IgnoreQueryFilters();
        }
        else
        {
            tests = tests.Where(t => t.IsActive);
            panels = panels.Where(p => p.IsActive);
        }

        var analyses = await tests.Select(t => new { t.Id, t.Code, t.Name }).ToListAsync(ct);
        var groups = await panels.Select(p => new { p.Id, p.Code, p.Name }).ToListAsync(ct);

        return analyses.Select(t => new ReminderTarget(t.Id, false, t.Code, t.Name))
                       .Concat(groups.Select(p => new ReminderTarget(p.Id, true, p.Code, p.Name)))
                       .OrderBy(t => t.Code, StringComparer.Ordinal)
                       .ToList();
    }

    public Task<bool> RuleExistsAsync(Guid? analysisTestId, Guid? testPanelId, Guid? exceptId,
                                      CancellationToken ct = default) =>
        db.ReminderRules.AsNoTracking()
          .AnyAsync(r => r.AnalysisTestId == analysisTestId && r.TestPanelId == testPanelId
                         && (exceptId == null || r.Id != exceptId), ct);

    public async Task<IReadOnlyList<ReminderSourceRow>> GetReminderSourcesAsync(Guid siteId, Guid? analysisTestId,
                                                                                Guid? testPanelId, DateTimeOffset since,
                                                                                CancellationToken ct = default)
    {
        var items = db.LabOrderItems.AsNoTracking()
            .Where(i => i.LabOrder!.SiteId == siteId && i.LabOrder.RegisteredAt >= since
                        && i.LabOrder.Status != LabOrderStatus.Cancelled
                        && i.Status != LabOrderItemStatus.Cancelled);

        items = analysisTestId is { } test
            ? items.Where(i => i.AnalysisTestId == test)
            : items.Where(i => i.TestPanelId == testPanelId);

        // A panel is validated when its last item is: per order, the latest validation, and none
        // while an item of it is still open.
        var rows = await items
            .Select(i => new
            {
                i.LabOrder!.PatientId,
                i.LabOrderId,
                i.LabOrder.RegisteredAt,
                i.ValidatedAt,
                Validated = i.Status == LabOrderItemStatus.Validated || i.Status == LabOrderItemStatus.Reported
            })
            .ToListAsync(ct);

        return rows.GroupBy(r => (r.PatientId, r.LabOrderId))
                   .Select(g => new ReminderSourceRow(
                       g.Key.PatientId, g.Key.LabOrderId, g.First().RegisteredAt,
                       g.All(r => r.Validated && r.ValidatedAt is not null) ? g.Max(r => r.ValidatedAt) : null))
                   .ToList();
    }

    public async Task<IReadOnlySet<(Guid RuleId, Guid OrderId)>> GetRemindedAsync(Guid siteId, CancellationToken ct = default)
    {
        var rows = await db.PatientMessages.AsNoTracking()
            .Where(m => m.SiteId == siteId && m.Kind == PatientMessageKind.BilanReminder
                        && m.Status == PatientMessageStatus.Sent
                        && m.ReminderRuleId != null && m.SourceOrderId != null)
            .Select(m => new { RuleId = m.ReminderRuleId!.Value, OrderId = m.SourceOrderId!.Value })
            .Distinct()
            .ToListAsync(ct);

        return rows.Select(r => (r.RuleId, r.OrderId)).ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, MessageRecipient>> GetRecipientsAsync(IReadOnlyList<Guid> patientIds,
                                                                                     DateOnly today,
                                                                                     CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, MessageRecipient>();

        // In slices: an IN list of thousands of parameters is refused by SQL Server past 2,100.
        foreach (var slice in patientIds.Chunk(1000))
        {
            var rows = await db.Patients.AsNoTracking()
                .Where(p => slice.Contains(p.Id) && !p.IsAnonymized && p.IsActive && p.MergedIntoPatientId == null)
                .Select(p => new
                {
                    p.Id, p.LastName, p.FirstName, p.MobilePhone, p.PreferredLanguage, p.OptedOutOfSecondaryUse,
                    Consent = db.PatientConsents.Any(c => c.PatientId == p.Id
                                                          && c.Purpose == ConsentPurpose.FollowUpMessages
                                                          && c.WithdrawnAt == null
                                                          && (c.ExpiresOn == null || c.ExpiresOn >= today))
                })
                .ToListAsync(ct);

            foreach (var p in rows)
                result[p.Id] = new MessageRecipient(p.Id, $"{p.LastName} {p.FirstName}".Trim(), p.MobilePhone,
                                                    p.PreferredLanguage, p.Consent, p.OptedOutOfSecondaryUse);
        }

        return result;
    }

    public async Task<MessageSignature> GetSignatureAsync(Guid siteId, CancellationToken ct = default)
    {
        var site = await db.Sites.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.Id == siteId)
            .Select(s => new { s.Name, s.Phone })
            .FirstOrDefaultAsync(ct);

        return new MessageSignature(site?.Name.Fr ?? string.Empty, site?.Phone);
    }

    public async Task<IReadOnlyList<SurveySourceRow>> GetSurveySourcesAsync(Guid siteId, DateTimeOffset start,
                                                                            DateTimeOffset end, CancellationToken ct = default) =>
        await db.LabOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId && o.FirstReportedAt >= start && o.FirstReportedAt < end
                        && o.Status != LabOrderStatus.Cancelled)
            .Select(o => new SurveySourceRow(o.Id, o.PatientId, o.AccessionNumber, o.FirstReportedAt!.Value))
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastSurveyedAsync(IReadOnlyList<Guid> patientIds,
                                                                                     CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        foreach (var slice in patientIds.Chunk(1000))
        {
            var rows = await db.SurveyInvitations.AsNoTracking().IgnoreQueryFilters()
                .Where(s => slice.Contains(s.PatientId))
                .GroupBy(s => s.PatientId)
                .Select(g => new { PatientId = g.Key, Last = g.Max(s => s.InvitedAt) })
                .ToListAsync(ct);
            foreach (var r in rows) result[r.PatientId] = r.Last;
        }
        return result;
    }

    public async Task<IReadOnlySet<Guid>> GetSurveyedOrdersAsync(IReadOnlyList<Guid> orderIds, CancellationToken ct = default)
    {
        var result = new HashSet<Guid>();
        foreach (var slice in orderIds.Chunk(1000))
            result.UnionWith(await db.SurveyInvitations.AsNoTracking().IgnoreQueryFilters()
                .Where(s => slice.Contains(s.LabOrderId)).Select(s => s.LabOrderId).ToListAsync(ct));
        return result;
    }

    public Task<bool> SurveyCodeExistsAsync(string code, CancellationToken ct = default) =>
        db.SurveyInvitations.AsNoTracking().IgnoreQueryFilters().AnyAsync(s => s.Code == code, ct);

    public async Task<Guid?> FindSurveyAsync(string code, CancellationToken ct = default) =>
        // Within the operator's sites: a code from another site's campaign is not theirs to answer.
        await db.SurveyInvitations.AsNoTracking().Where(s => s.Code == code).Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SurveyAnswerRow>> GetSurveyAnswersAsync(Guid siteId, DateTimeOffset start,
                                                                            DateTimeOffset end, CancellationToken ct = default) =>
        await db.SurveyInvitations.AsNoTracking()
            .Where(s => s.SiteId == siteId && s.InvitedAt >= start && s.InvitedAt < end)
            .Select(s => new SurveyAnswerRow(s.InvitedAt, s.Delivered, s.Score, s.Comment, s.AnsweredAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PatientMessageRow>> GetMessagesAsync(Guid siteId, DateTimeOffset start,
                                                                         DateTimeOffset end, CancellationToken ct = default)
    {
        var rows = await db.PatientMessages.AsNoTracking()
            .Where(m => m.SiteId == siteId && m.SentAt >= start && m.SentAt < end)
            .OrderByDescending(m => m.SentAt)
            .Take(5000)
            .Select(m => new
            {
                m.SentAt, m.Kind, m.PhoneNumber, m.Status, m.Failure, m.Text,
                Patient = db.Patients.IgnoreQueryFilters().Where(p => p.Id == m.PatientId)
                    .Select(p => p.LastName + " " + p.FirstName).FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows.Select(r => new PatientMessageRow(r.SentAt, r.Kind, r.Patient ?? "?", r.PhoneNumber, r.Status,
                                                      r.Failure, r.Text)).ToList();
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
