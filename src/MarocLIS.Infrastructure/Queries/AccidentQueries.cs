using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class AccidentQueries(LisDbContext db) : IAccidentQueries
{
    public async Task<IReadOnlyList<AccidentRow>> GetAccidentsAsync(int year, CancellationToken ct = default)
    {
        var start = new DateTime(year, 1, 1);
        var end = start.AddYears(1);

        var rows = await (from a in db.WorkAccidents.AsNoTracking()
                          join e in db.Employees.AsNoTracking() on a.EmployeeId equals e.Id
                          join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id into dj
                          from d in dj.DefaultIfEmpty()
                          // A file still open from an earlier year stays on the list until it is closed.
                          where (a.OccurredAt >= start && a.OccurredAt < end) || (!a.IsClosed && a.OccurredAt < end)
                          orderby a.OccurredAt descending
                          select new
                          {
                              a.Id, a.Number, a.EmployeeId, EmployeeNumber = e.Number,
                              Name = e.LastName + " " + e.FirstName,
                              Department = d == null ? null : d.Name.Fr,
                              a.Kind, a.OccurredAt, a.Place, a.DeclaredToInsurerOn, a.ReturnedToWorkOn, a.IsClosed,
                              NextCheck = a.Checks.Where(c => c.DoneOn == null).OrderBy(c => c.DueOn)
                                                  .Select(c => (DateOnly?)c.DueOn).FirstOrDefault()
                          })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var days = await db.LeaveRequests.AsNoTracking()
            .Where(l => l.WorkAccidentId != null && ids.Contains(l.WorkAccidentId.Value) && l.Status == LeaveStatus.Approved)
            .GroupBy(l => l.WorkAccidentId!.Value)
            .Select(g => new { Id = g.Key, Days = g.Sum(l => l.Days) })
            .ToDictionaryAsync(x => x.Id, x => x.Days, ct);

        return [.. rows.Select(r => new AccidentRow(r.Id, r.Number, r.EmployeeId, r.EmployeeNumber, r.Name, r.Department,
                                                    r.Kind, r.OccurredAt, r.Place, r.DeclaredToInsurerOn,
                                                    days.GetValueOrDefault(r.Id), r.ReturnedToWorkOn, r.NextCheck, r.IsClosed))];
    }

    public async Task<IReadOnlyList<StoppageRow>> StoppagesAsync(Guid accidentId, CancellationToken ct = default) =>
        await db.LeaveRequests.AsNoTracking()
            .Where(l => l.WorkAccidentId == accidentId)
            .OrderBy(l => l.StartDate)
            .Select(l => new StoppageRow(l.Id, l.StartDate, l.EndDate, l.Days, l.Status))
            .ToListAsync(ct);

    public async Task<string?> DepartmentOfAsync(Guid employeeId, CancellationToken ct = default) =>
        await (from e in db.Employees.AsNoTracking()
               join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id
               where e.Id == employeeId
               select d.Name.Fr).FirstOrDefaultAsync(ct);

    public async Task<string?> LastInsurerAsync(CancellationToken ct = default) =>
        await db.WorkAccidents.AsNoTracking()
            .Where(a => a.InsurerName != null)
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => a.InsurerName)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }
}
