using MarocLIS.Application.Billing;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class PayrollQueries(LisDbContext db) : IPayrollQueries
{
    public async Task<IReadOnlyList<PayrollPeriodRow>> GetPeriodsAsync(CancellationToken ct = default)
    {
        var rows = await (
                from p in db.PayrollPeriods.AsNoTracking()
                join u in db.Users.AsNoTracking() on p.ClosedByUserId equals u.Id into uj
                from u in uj.DefaultIfEmpty()
                select new
                {
                    p.Id, p.Year, p.Month, p.Status, p.ClosedAt,
                    ClosedBy = u == null ? null : u.FullName,
                    Count = db.Payslips.Count(s => s.PayrollPeriodId == p.Id),
                    Gross = db.Payslips.Where(s => s.PayrollPeriodId == p.Id).Sum(s => (decimal?)s.Amounts.Gross) ?? 0m,
                    Net = db.Payslips.Where(s => s.PayrollPeriodId == p.Id).Sum(s => (decimal?)s.Amounts.NetPay) ?? 0m,
                    Cost = db.Payslips.Where(s => s.PayrollPeriodId == p.Id).Sum(s => (decimal?)s.Amounts.EmployerCost) ?? 0m
                })
            .ToListAsync(ct);

        return
        [
            .. rows.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
                   .Select(r => new PayrollPeriodRow(r.Id, r.Year, r.Month, r.Status, r.Count, r.Gross, r.Net, r.Cost,
                                                     r.ClosedAt, r.ClosedBy))
        ];
    }

    public Task<Guid?> FindPeriodAsync(int year, int month, CancellationToken ct = default) =>
        db.PayrollPeriods.AsNoTracking()
          .Where(p => p.Year == year && p.Month == month)
          .Select(p => (Guid?)p.Id)
          .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Guid>> DraftPeriodIdsAsync(int year, CancellationToken ct = default) =>
        await db.PayrollPeriods.AsNoTracking()
            .Where(p => p.Year == year && p.Status == PayrollStatus.Draft)
            .Select(p => p.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PayrollCandidate>> CandidatesAsync(DateOnly from, DateOnly to, decimal monthlyWorkingDays,
                                                                       CancellationToken ct = default)
    {
        var staff = await db.Employees.AsNoTracking()
            .Where(e => e.HireDate <= to && (e.ExitDate == null || e.ExitDate >= from))
            .Select(e => new { e.Id, e.HireDate, e.ExitDate })
            .ToListAsync(ct);
        if (staff.Count == 0) return [];

        var ids = staff.Select(s => s.Id).ToList();
        var unpaid = await db.LeaveRequests.AsNoTracking()
            .Where(r => ids.Contains(r.EmployeeId) && r.Status == LeaveStatus.Approved
                        && (r.Type == LeaveType.Unpaid || r.Type == LeaveType.UnjustifiedAbsence)
                        && r.StartDate <= to && r.EndDate >= from)
            .Select(r => new { r.EmployeeId, r.Type, r.StartDate, r.EndDate, r.StartsAfternoon, r.EndsMorning })
            .ToListAsync(ct);

        var holidays = (await db.PublicHolidays.AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .Select(h => h.Date)
            .ToListAsync(ct)).ToHashSet();

        decimal Working(DateOnly a, DateOnly b, bool afternoon = false, bool morning = false) =>
            b < a ? 0m : LeaveRules.CountDays(LeaveType.Unpaid, a, b, afternoon, morning, holidays);

        return
        [
            .. staff.Select(s =>
            {
                // Days of the month outside the contract, then the unpaid leave inside it.
                var days = Working(from, s.HireDate.AddDays(-1));
                if (s.ExitDate is { } exit) days += Working(exit.AddDays(1), to);

                foreach (var r in unpaid.Where(r => r.EmployeeId == s.Id))
                {
                    var start = r.StartDate < from ? from : r.StartDate;
                    var end = r.EndDate > to ? to : r.EndDate;
                    days += Working(start, end, r.StartsAfternoon && start == r.StartDate, r.EndsMorning && end == r.EndDate);
                }

                return new PayrollCandidate(s.Id, Math.Min(days, monthlyWorkingDays));
            })
        ];
    }

    public async Task<InvoiceIssuer> EmployerAsync(Guid siteId, CancellationToken ct = default)
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
}
