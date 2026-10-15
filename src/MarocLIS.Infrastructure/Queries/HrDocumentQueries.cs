using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class HrDocumentQueries(LisDbContext db) : IHrDocumentQueries
{
    public async Task<IReadOnlyList<SalaryMonth>> ClosedSalaryMonthsAsync(Guid employeeId, int count,
                                                                         CancellationToken ct = default) =>
        await (from s in db.Payslips.AsNoTracking()
               join p in db.PayrollPeriods.AsNoTracking() on s.PayrollPeriodId equals p.Id
               where s.EmployeeId == employeeId && p.Status == PayrollStatus.Closed
               orderby p.Year descending, p.Month descending
               select new SalaryMonth(p.Year, p.Month, s.Amounts.Gross, s.Amounts.NetPay))
            .Take(count)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<decimal>> FullMonthGrossAsync(Guid employeeId, DateOnly before, decimal fullDays,
                                                                  int count, CancellationToken ct = default)
    {
        var rows = await (from s in db.Payslips.AsNoTracking()
                          join p in db.PayrollPeriods.AsNoTracking() on s.PayrollPeriodId equals p.Id
                          where s.EmployeeId == employeeId
                                && (p.Year < before.Year || (p.Year == before.Year && p.Month < before.Month))
                                && s.Amounts.PaidDays >= fullDays
                          orderby p.Year descending, p.Month descending
                          select new { s.Amounts.GrossTaxable, s.Variables.LeaveIndemnity, s.Variables.NoticeIndemnity })
            .Take(count)
            .ToListAsync(ct);

        return [.. rows.Select(r => r.GrossTaxable - r.LeaveIndemnity - r.NoticeIndemnity)];
    }

    public async Task<decimal> LeaveBalanceAtExitAsync(Guid employeeId, CancellationToken ct = default)
    {
        var balance = await db.LeaveLedger.AsNoTracking()
            .Where(l => l.EmployeeId == employeeId)
            .SumAsync(l => (decimal?)l.Days, ct) ?? 0m;

        return balance + (await ExitAccrualsDueAsync(employeeId, ct)).Sum(d => d.Days);
    }

    public async Task<IReadOnlyList<LeaveLedgerEntry>> ExitAccrualsDueAsync(Guid employeeId, CancellationToken ct = default)
    {
        var e = await db.Employees.AsNoTracking()
            .Where(x => x.Id == employeeId)
            .Select(x => new { x.Id, x.HireDate, x.BirthDate, x.ExitDate, x.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (e?.ExitDate is not { } exit) return [];

        // The monthly accrual stops with the file: months up to the departure not credited yet
        // are added here, by the same rule, as if the accrual had run on the following month.
        var credited = (await db.LeaveLedger.AsNoTracking()
                .Where(l => l.EmployeeId == employeeId && l.Kind == LeaveLedgerKind.Accrual)
                .Select(l => l.Reference)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var opened = DateOnly.FromDateTime(e.CreatedAt.Date);
        var trackedFrom = e.HireDate > opened ? e.HireDate : new DateOnly(opened.Year, opened.Month, 1);

        return
        [
            .. LeaveAccrual.Due(new AccrualFacts(e.Id, e.HireDate, e.BirthDate, exit, trackedFrom, credited),
                                new DateOnly(exit.Year, exit.Month, 1).AddMonths(1))
        ];
    }

    public async Task<(Guid PeriodId, PayrollStatus Status, Payslip? Payslip)?> PayslipOfMonthAsync(
        Guid employeeId, int year, int month, CancellationToken ct = default)
    {
        // Each site runs its own payroll, so a month can have one period per site: the one holding
        // the employee's payslip, else the month of the employee's own site — never another site's.
        var period = await db.PayrollPeriods.AsNoTracking()
            .Where(p => p.Year == year && p.Month == month
                        && (db.Payslips.Any(s => s.PayrollPeriodId == p.Id && s.EmployeeId == employeeId)
                            || db.Employees.Any(e => e.Id == employeeId && e.SiteId == p.SiteId)))
            .OrderByDescending(p => db.Payslips.Any(s => s.PayrollPeriodId == p.Id && s.EmployeeId == employeeId))
            .Select(p => new { p.Id, p.Status })
            .FirstOrDefaultAsync(ct);
        if (period is null) return null;

        var slip = await db.Payslips.AsNoTracking()
            .FirstOrDefaultAsync(s => s.PayrollPeriodId == period.Id && s.EmployeeId == employeeId, ct);
        return (period.Id, period.Status, slip);
    }
}
