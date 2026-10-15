using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class LeaveQueries(LisDbContext db) : ILeaveQueries
{
    public Task<Guid?> EmployeeIdForUserAsync(Guid userId, CancellationToken ct = default) =>
        db.Employees.AsNoTracking()
          .Where(e => e.UserId == userId && e.ExitDate == null)
          .Select(e => (Guid?)e.Id)
          .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<LeaveRow>> GetRequestsAsync(bool pendingOnly, int year, Guid? employeeId,
                                                                CancellationToken ct = default)
    {
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);

        var query = db.LeaveRequests.AsNoTracking();
        query = pendingOnly
            ? query.Where(r => r.Status == LeaveStatus.Pending)
            : query.Where(r => r.StartDate <= to && r.EndDate >= from);
        if (employeeId is { } only) query = query.Where(r => r.EmployeeId == only);

        var rows = await Project(query).ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Who else in the same department is away (or asked to be) on some of the same days.
        var low = rows.Min(r => r.StartDate);
        var high = rows.Max(r => r.EndDate);
        var away = await (
                from r in db.LeaveRequests.AsNoTracking()
                join e in db.Employees.AsNoTracking() on r.EmployeeId equals e.Id
                where (r.Status == LeaveStatus.Pending || r.Status == LeaveStatus.Approved)
                      && r.StartDate <= high && r.EndDate >= low && e.DepartmentId != null
                select new { r.EmployeeId, e.DepartmentId, r.StartDate, r.EndDate })
            .ToListAsync(ct);

        return
        [
            .. rows.OrderBy(r => r.Status == LeaveStatus.Pending ? 0 : 1)
                   .ThenByDescending(r => r.StartDate)
                   .Select(r => r.ToRow(r.DepartmentId is null
                       ? 0
                       : away.Where(a => a.DepartmentId == r.DepartmentId && a.EmployeeId != r.EmployeeId
                                         && a.StartDate <= r.EndDate && a.EndDate >= r.StartDate)
                             .Select(a => a.EmployeeId).Distinct().Count()))
        ];
    }

    public async Task<LeaveRow?> OverlappingLeaveAsync(Guid employeeId, DateOnly from, DateOnly to, Guid? excluding,
                                                       CancellationToken ct = default)
    {
        var query = db.LeaveRequests.AsNoTracking()
            .Where(r => r.EmployeeId == employeeId && r.Id != excluding
                        && (r.Status == LeaveStatus.Pending || r.Status == LeaveStatus.Approved)
                        && r.StartDate <= to && r.EndDate >= from);
        var row = await Project(query).OrderBy(r => r.StartDate).FirstOrDefaultAsync(ct);
        return row?.ToRow(0);
    }

    private IQueryable<RequestProjection> Project(IQueryable<LeaveRequest> requests) =>
        from r in requests
        join e in db.Employees.AsNoTracking() on r.EmployeeId equals e.Id
        join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id into dj
        from d in dj.DefaultIfEmpty()
        join u in db.Users.AsNoTracking() on r.DecidedByUserId equals u.Id into uj
        from u in uj.DefaultIfEmpty()
        select new RequestProjection
        {
            Id = r.Id, EmployeeId = r.EmployeeId, Number = e.Number,
            Name = e.LastName + " " + e.FirstName,
            DepartmentId = e.DepartmentId,
            Department = d == null ? null : d.Name.Fr,
            Type = r.Type, StartDate = r.StartDate, EndDate = r.EndDate,
            StartsAfternoon = r.StartsAfternoon, EndsMorning = r.EndsMorning, Days = r.Days,
            Reason = r.Reason, JustificationProvided = r.JustificationProvided, Status = r.Status,
            SubmittedAt = r.SubmittedAt, DecidedBy = u == null ? null : u.FullName,
            DecisionComment = r.DecisionComment
        };

    private sealed class RequestProjection
    {
        public Guid Id { get; init; }
        public Guid EmployeeId { get; init; }
        public string Number { get; init; } = "";
        public string Name { get; init; } = "";
        public Guid? DepartmentId { get; init; }
        public string? Department { get; init; }
        public LeaveType Type { get; init; }
        public DateOnly StartDate { get; init; }
        public DateOnly EndDate { get; init; }
        public bool StartsAfternoon { get; init; }
        public bool EndsMorning { get; init; }
        public decimal Days { get; init; }
        public string? Reason { get; init; }
        public bool JustificationProvided { get; init; }
        public LeaveStatus Status { get; init; }
        public DateTimeOffset SubmittedAt { get; init; }
        public string? DecidedBy { get; init; }
        public string? DecisionComment { get; init; }

        public LeaveRow ToRow(int colleaguesAway) =>
            new(Id, EmployeeId, Number, Name.Trim(), Department, Type, StartDate, EndDate, StartsAfternoon,
                EndsMorning, Days, Reason, JustificationProvided, Status, SubmittedAt, DecidedBy, DecisionComment,
                colleaguesAway);
    }

    public async Task<IReadOnlyList<LeaveBalanceRow>> GetBalancesAsync(DateOnly today, Guid? employeeId,
                                                                       CancellationToken ct = default)
    {
        var people = db.Employees.AsNoTracking().Where(e => e.ExitDate == null);
        if (employeeId is { } only) people = people.Where(e => e.Id == only);

        var staff = await people
            .Select(e => new { e.Id, e.Number, e.LastName, e.FirstName, e.HireDate, e.BirthDate })
            .ToListAsync(ct);
        if (staff.Count == 0) return [];

        var ids = staff.Select(s => s.Id).ToList();

        var movements = await db.LeaveLedger.AsNoTracking()
            .Where(l => ids.Contains(l.EmployeeId))
            .GroupBy(l => new { l.EmployeeId, l.Kind })
            .Select(g => new { g.Key.EmployeeId, g.Key.Kind, Days = g.Sum(l => l.Days) })
            .ToListAsync(ct);

        var pending = await db.LeaveRequests.AsNoTracking()
            .Where(r => ids.Contains(r.EmployeeId) && r.Status == LeaveStatus.Pending && r.Type == LeaveType.Annual)
            .GroupBy(r => r.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Days = g.Sum(r => r.Days) })
            .ToListAsync(ct);

        decimal Sum(Guid id, params LeaveLedgerKind[] kinds) =>
            movements.Where(m => m.EmployeeId == id && kinds.Contains(m.Kind)).Sum(m => m.Days);

        return
        [
            .. staff.OrderBy(s => s.LastName, StringComparer.CurrentCulture)
                    .ThenBy(s => s.FirstName, StringComparer.CurrentCulture)
                    .Select(s => new LeaveBalanceRow(
                        s.Id, s.Number, $"{s.LastName} {s.FirstName}".Trim(),
                        LeaveRules.AnnualEntitlement(s.HireDate, s.BirthDate, today),
                        Sum(s.Id, LeaveLedgerKind.Accrual, LeaveLedgerKind.Adjustment),
                        -Sum(s.Id, LeaveLedgerKind.Taken, LeaveLedgerKind.Returned),
                        pending.FirstOrDefault(p => p.EmployeeId == s.Id)?.Days ?? 0m,
                        Sum(s.Id, LeaveLedgerKind.Accrual, LeaveLedgerKind.Adjustment, LeaveLedgerKind.Taken,
                            LeaveLedgerKind.Returned)))
        ];
    }

    public async Task<IReadOnlyList<AccrualFacts>> GetAccrualFactsAsync(CancellationToken ct = default)
    {
        var staff = await db.Employees.AsNoTracking()
            .Where(e => e.ExitDate == null)
            .Select(e => new { e.Id, e.HireDate, e.BirthDate, e.ExitDate, e.CreatedAt })
            .ToListAsync(ct);
        if (staff.Count == 0) return [];

        var ids = staff.Select(s => s.Id).ToList();
        var credited = (await db.LeaveLedger.AsNoTracking()
                .Where(l => ids.Contains(l.EmployeeId) && l.Kind == LeaveLedgerKind.Accrual)
                .Select(l => new { l.EmployeeId, l.Reference })
                .ToListAsync(ct))
            .ToLookup(l => l.EmployeeId, l => l.Reference);

        // The account starts with the file: earlier service comes in as an opening balance, entered
        // by HR, rather than as years of accrual nobody can reconcile with the leave already taken.
        return
        [
            .. staff.Select(s =>
            {
                var opened = DateOnly.FromDateTime(s.CreatedAt.Date);
                var trackedFrom = s.HireDate > opened ? s.HireDate : new DateOnly(opened.Year, opened.Month, 1);
                return new AccrualFacts(s.Id, s.HireDate, s.BirthDate, s.ExitDate, trackedFrom,
                                        credited[s.Id].ToHashSet(StringComparer.Ordinal));
            })
        ];
    }

    public async Task<IReadOnlySet<DateOnly>> GetHolidayDatesAsync(DateOnly from, DateOnly to,
                                                                   CancellationToken ct = default) =>
        (await db.PublicHolidays.AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .Select(h => h.Date)
            .ToListAsync(ct)).ToHashSet();

    public async Task<IReadOnlyList<HolidayRow>> GetHolidaysAsync(int year, CancellationToken ct = default)
    {
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);
        return await db.PublicHolidays.AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .OrderBy(h => h.Date)
            .Select(h => new HolidayRow(h.Id, h.Date, h.Name, h.IsReligious))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<DateOnly>> RecordedHolidayDatesAsync(int year, CancellationToken ct = default)
    {
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);
        return (await db.PublicHolidays.IgnoreQueryFilters().AsNoTracking()
            .Where(h => h.Date >= from && h.Date <= to)
            .Select(h => h.Date)
            .ToListAsync(ct)).ToHashSet();
    }
}
