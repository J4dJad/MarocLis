using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class PlanningQueries(LisDbContext db) : IPlanningQueries
{
    public async Task<IReadOnlyList<PlanningEmployee>> GetEmployeesAsync(DateOnly from, DateOnly to,
                                                                         CancellationToken ct = default)
    {
        var rows = await (
                from e in db.Employees.AsNoTracking()
                where e.HireDate <= to && (e.ExitDate == null || e.ExitDate >= @from)
                join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id into dj
                from d in dj.DefaultIfEmpty()
                select new
                {
                    e.Id, e.Number, e.LastName, e.FirstName, e.Job, e.HireDate, e.ExitDate,
                    Department = d == null ? null : d.Name.Fr
                })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var levels = (await db.EmployeeHabilitations.AsNoTracking()
                .Where(h => ids.Contains(h.EmployeeId) && h.WithdrawnOn == null)
                .Select(h => new { h.EmployeeId, h.Level })
                .ToListAsync(ct))
            .ToLookup(h => h.EmployeeId, h => h.Level);

        static int JobOrder(EmployeeJob job) => job switch
        {
            EmployeeJob.Biologist => 0,
            EmployeeJob.Technician => 1,
            EmployeeJob.Nurse => 2,
            _ => 3
        };

        return
        [
            .. rows.OrderBy(r => JobOrder(r.Job))
                   .ThenBy(r => r.LastName, StringComparer.CurrentCulture)
                   .ThenBy(r => r.FirstName, StringComparer.CurrentCulture)
                   .Select(r => new PlanningEmployee(
                       r.Id, r.Number, $"{r.LastName} {r.FirstName}".Trim(), r.Job, r.Department, r.HireDate,
                       r.ExitDate,
                       levels[r.Id].Any() && levels[r.Id].All(l => l == HabilitationLevel.InTraining)))
        ];
    }

    public async Task<IReadOnlyList<ShiftCell>> GetShiftsAsync(DateOnly from, DateOnly to, Guid? employeeId,
                                                               CancellationToken ct = default)
    {
        var query = db.ShiftAssignments.AsNoTracking().Where(s => s.Date >= from && s.Date <= to);
        if (employeeId is { } only) query = query.Where(s => s.EmployeeId == only);

        return await query
            .OrderBy(s => s.Date).ThenBy(s => s.Start)
            .Select(s => new ShiftCell(s.Id, s.EmployeeId, s.Date, s.Kind, s.Start, s.End, s.Note))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LeaveSpan>> GetLeavesAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await db.LeaveRequests.AsNoTracking()
            .Where(r => (r.Status == LeaveStatus.Pending || r.Status == LeaveStatus.Approved)
                        && r.StartDate <= to && r.EndDate >= from)
            .Select(r => new LeaveSpan(r.EmployeeId, r.Type, r.Status, r.StartDate, r.EndDate))
            .ToListAsync(ct);
}
