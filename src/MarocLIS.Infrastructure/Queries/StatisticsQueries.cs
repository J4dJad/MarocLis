using MarocLIS.Application.Statistics;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class StatisticsQueries(LisDbContext db) : IStatisticsQueries
{
    public async Task<IReadOnlyList<ActivityItem>> GetActivityAsync(
        Guid siteId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        await db.LabOrderItems.AsNoTracking()
            .Where(i => i.LabOrder!.SiteId == siteId
                        && i.LabOrder.RegisteredAt >= start && i.LabOrder.RegisteredAt < end)
            .Select(i => new ActivityItem(
                i.LabOrderId,
                i.LabOrder!.PatientId,
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
                i.PatientShare))
            .ToListAsync(ct);

    public async Task<StatisticsLabels> GetLabelsAsync(CancellationToken ct = default)
    {
        // Deleted tests, departments and payers stay nameable: last year's statistics still
        // mention them.
        var tests = await db.AnalysisTests.AsNoTracking().IgnoreQueryFilters()
            .Select(t => new { t.Id, t.Code, t.Name }).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().IgnoreQueryFilters()
            .Select(d => new { d.Id, d.Code, d.Name }).ToListAsync(ct);
        var payers = await db.Payers.AsNoTracking().IgnoreQueryFilters()
            .Select(p => new { p.Id, p.Name }).ToListAsync(ct);

        return new StatisticsLabels(
            tests.ToDictionary(t => t.Id, t => (t.Code, t.Name)),
            departments.ToDictionary(d => d.Id, d => (d.Code, d.Name)),
            payers.ToDictionary(p => p.Id, p => p.Name));
    }

    public async Task<FinanceTotals> GetFinanceAsync(Guid siteId, DateOnly from, DateOnly to,
                                                     CancellationToken ct = default)
    {
        var issued = db.Invoices.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IssueDate >= from && i.IssueDate <= to
                        && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled);

        var invoiced = await issued.Where(i => i.Kind == InvoiceKind.Invoice)
                                   .SumAsync(i => (decimal?)i.TotalAmount, ct) ?? 0m;

        // Credit notes are summed by magnitude whatever sign they were stored with, and shown as
        // their own figure rather than netted silently into the invoiced total.
        var credited = await issued.Where(i => i.Kind == InvoiceKind.CreditNote)
                                   .SumAsync(i => (decimal?)Math.Abs(i.TotalAmount), ct) ?? 0m;

        var collected = await db.Payments.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.PaymentDate >= from && p.PaymentDate <= to
                        && p.Status == PaymentStatus.Settled)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        return new FinanceTotals(Money.Round(invoiced), Money.Round(credited), Money.Round(collected));
    }
}
