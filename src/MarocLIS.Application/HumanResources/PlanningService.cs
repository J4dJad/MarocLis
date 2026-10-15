using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

public sealed record PlanningEmployee(
    Guid EmployeeId,
    string Number,
    string FullName,
    EmployeeJob Job,
    string? DepartmentName,
    DateOnly HireDate,
    DateOnly? ExitDate,
    /// <summary>Has habilitations, all of them "in training": must not hold a post alone.</summary>
    bool OnlyInTraining);

public sealed record ShiftCell(Guid ShiftId, Guid EmployeeId, DateOnly Date, ShiftKind Kind, TimeOnly Start,
                               TimeOnly End, string? Note)
{
    public decimal Hours => ShiftRules.Hours(Kind, Start, End);
    public DateTime StartsAt => Date.ToDateTime(Start);
    public DateTime EndsAt => End > Start ? Date.ToDateTime(End) : Date.AddDays(1).ToDateTime(End);
}

public sealed record LeaveSpan(Guid EmployeeId, LeaveType Type, LeaveStatus Status, DateOnly StartDate, DateOnly EndDate)
{
    public bool Covers(DateOnly day) => StartDate <= day && EndDate >= day;
}

public enum PlanningIssueKind
{
    /// <summary>Less than 11 hours between two shifts.</summary>
    RestTooShort = 0,
    /// <summary>More than 44 worked hours in the week.</summary>
    WeeklyHoursExceeded = 1,
    /// <summary>A shift on each of the seven days of the week.</summary>
    NoWeeklyRest = 2,
    /// <summary>Staff on duty that day but no biologist, not even on call.</summary>
    NoBiologist = 3,
    /// <summary>A night or Sunday shift held only by someone still in training.</summary>
    TraineeAlone = 4,
    /// <summary>A shift on a day of approved leave (the leave was approved after the shift was set).</summary>
    ShiftDuringLeave = 5
}

public sealed record PlanningIssue(PlanningIssueKind Kind, DateOnly Date, Guid? EmployeeId, string? EmployeeName,
                                   decimal? Value = null);

public sealed record PlanningMonth(
    int Year,
    int Month,
    IReadOnlyList<PlanningEmployee> Employees,
    IReadOnlyList<ShiftCell> Shifts,
    IReadOnlyList<LeaveSpan> Leaves,
    IReadOnlyList<HolidayRow> Holidays,
    IReadOnlyList<PlanningIssue> Issues);

public sealed record ShiftRequest
{
    public required Guid EmployeeId { get; init; }
    public required DateOnly Date { get; init; }
    public required ShiftKind Kind { get; init; }
    /// <summary>Null: the usual hours of the kind.</summary>
    public TimeOnly? Start { get; init; }
    public TimeOnly? End { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// The checks on a planning, apart from the database. None of them blocks: a laboratory sometimes
/// has to go over, and the planner decides with the warning in front of them. What cannot be
/// decided away (a shift on approved leave, two shifts at once) is refused at entry instead.
/// </summary>
public static class PlanningChecks
{
    public static IReadOnlyList<PlanningIssue> Check(IReadOnlyList<PlanningEmployee> employees,
                                                     IReadOnlyList<ShiftCell> shifts,
                                                     IReadOnlyList<LeaveSpan> leaves,
                                                     DateOnly from, DateOnly to)
    {
        var issues = new List<PlanningIssue>();
        var people = employees.ToDictionary(e => e.EmployeeId);
        string? Name(Guid id) => people.TryGetValue(id, out var e) ? e.FullName : null;

        foreach (var group in shifts.GroupBy(s => s.EmployeeId))
        {
            var worked = group.Where(s => ShiftRules.IsWorked(s.Kind)).OrderBy(s => s.StartsAt).ToList();

            // Daily rest: 11 consecutive hours between the end of one shift and the next.
            for (var i = 1; i < worked.Count; i++)
            {
                var rest = worked[i].StartsAt - worked[i - 1].EndsAt;
                if (rest < ShiftRules.MinimumRest && worked[i].Date >= from && worked[i].Date <= to)
                    issues.Add(new PlanningIssue(PlanningIssueKind.RestTooShort, worked[i].Date, group.Key,
                                                 Name(group.Key), (decimal)Math.Max(0, rest.TotalHours)));
            }

            // Weekly hours and weekly rest, Monday to Sunday, for the weeks that touch the period.
            foreach (var week in worked.GroupBy(s => WeekStart(s.Date)))
            {
                var monday = week.Key;
                if (monday.AddDays(6) < from || monday > to) continue;

                var hours = week.Sum(s => s.Hours);
                if (hours > ShiftRules.WeeklyHoursLimit)
                    issues.Add(new PlanningIssue(PlanningIssueKind.WeeklyHoursExceeded, Clamp(monday, from, to),
                                                 group.Key, Name(group.Key), hours));

                if (week.Select(s => s.Date).Distinct().Count() == 7)
                    issues.Add(new PlanningIssue(PlanningIssueKind.NoWeeklyRest, Clamp(monday, from, to),
                                                 group.Key, Name(group.Key)));
            }

            foreach (var shift in group.Where(s => s.Date >= from && s.Date <= to))
                if (leaves.Any(l => l.EmployeeId == shift.EmployeeId && l.Status == LeaveStatus.Approved
                                    && l.Covers(shift.Date)))
                    issues.Add(new PlanningIssue(PlanningIssueKind.ShiftDuringLeave, shift.Date, group.Key,
                                                 Name(group.Key)));
        }

        foreach (var day in shifts.Where(s => s.Date >= from && s.Date <= to).GroupBy(s => s.Date))
        {
            // Loi 12-01: a biologist answers for the laboratory whenever it is open.
            if (!day.Any(s => people.TryGetValue(s.EmployeeId, out var e) && e.Job == EmployeeJob.Biologist))
                issues.Add(new PlanningIssue(PlanningIssueKind.NoBiologist, day.Key, null, null));

            foreach (var post in day.Where(s => s.Kind is ShiftKind.Night or ShiftKind.SundayHoliday)
                                    .GroupBy(s => s.Kind))
            {
                var holders = post.Select(s => people.GetValueOrDefault(s.EmployeeId)).ToList();
                if (holders.Count > 0 && holders.All(h => h is { OnlyInTraining: true }))
                    foreach (var h in holders)
                        issues.Add(new PlanningIssue(PlanningIssueKind.TraineeAlone, day.Key, h!.EmployeeId,
                                                     h.FullName));
            }
        }

        return [.. issues.OrderBy(i => i.Date).ThenBy(i => i.Kind).ThenBy(i => i.EmployeeName)];
    }

    public static DateOnly WeekStart(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateOnly Clamp(DateOnly day, DateOnly from, DateOnly to) =>
        day < from ? from : day > to ? to : day;
}

/// <summary>
/// The monthly shift and on-call planning of the current site.
/// </summary>
public sealed class PlanningService(
    IRepository<ShiftAssignment> shifts,
    IRepository<Employee> employees,
    IPlanningQueries queries,
    ILeaveQueries leaveQueries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser)
{
    public bool CanEdit => currentUser.Permissions.Contains(Permissions.HumanResources.ManagePlanning);

    public async Task<Result<PlanningMonth>> GetMonthAsync(int year, int month, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.View, Permissions.HumanResources.ManagePlanning);
        if (permitted.IsFailure) return Result.Failure<PlanningMonth>(permitted.FirstError);
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
            return Result.Failure<PlanningMonth>(Error.Validation("Planning_MonthInvalid"));

        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);

        // A week either side, so that rest and weekly hours across the month boundary are checked.
        var people = await queries.GetEmployeesAsync(from, to, ct);
        var cells = await queries.GetShiftsAsync(from.AddDays(-7), to.AddDays(7), null, ct);
        var leaves = await queries.GetLeavesAsync(from, to, ct);
        var holidays = (await leaveQueries.GetHolidaysAsync(year, ct)).Where(h => h.Date.Month == month).ToList();

        var issues = PlanningChecks.Check(people, cells, leaves, from, to);
        return Result.Success(new PlanningMonth(year, month, people,
                                                [.. cells.Where(c => c.Date >= from && c.Date <= to)],
                                                leaves, holidays, issues));
    }

    public async Task<Result<Guid>> AssignAsync(ShiftRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.ManagePlanning);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var employee = await employees.GetByIdAsync(request.EmployeeId, ct);
        if (employee is null) return Result.Failure<Guid>(Error.NotFound("Employee_NotFound"));
        if (request.Date < employee.HireDate || employee.ExitDate is { } exit && request.Date > exit)
            return Result.Failure<Guid>(Error.Validation("Planning_EmployeeNotEmployed"));

        var (defaultStart, defaultEnd) = ShiftRules.DefaultHours(request.Kind);
        var start = request.Kind == ShiftKind.OnCall ? defaultStart : request.Start ?? defaultStart;
        var end = request.Kind == ShiftKind.OnCall ? defaultEnd : request.End ?? defaultEnd;
        if (request.Kind != ShiftKind.OnCall && start == end)
            return Result.Failure<Guid>(Error.Validation("Planning_HoursInvalid"));

        var cell = new ShiftCell(Guid.Empty, employee.Id, request.Date, request.Kind, start, end, request.Note);
        var blocked = await BlockedAsync(cell, ct);
        if (blocked is not null) return Result.Failure<Guid>(blocked);

        var shift = new ShiftAssignment(employee.Id, currentUser.SiteId, request.Date, request.Kind, start, end,
                                        request.Note);
        await shifts.AddAsync(shift, ct);

        unitOfWork.AuditReason = $"Planning : {employee.Number} {request.Kind} le {request.Date:dd/MM/yyyy} " +
                                 $"{start:HH\\:mm}-{end:HH\\:mm}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(shift.Id);
    }

    public async Task<Result> RemoveAsync(Guid shiftId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.ManagePlanning);
        if (permitted.IsFailure) return permitted;

        var shift = await shifts.GetByIdAsync(shiftId, ct);
        if (shift is null) return Result.Failure(Error.NotFound("Planning_ShiftNotFound"));

        shifts.SoftDelete(shift, currentUser.UserId, "Retiré du planning");
        unitOfWork.AuditReason = $"Planning : poste {shift.Kind} du {shift.Date:dd/MM/yyyy} retiré";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Repeats a week's shifts on another week. A shift that would be refused (leave, departure,
    /// a shift already there) is left out and counted.
    /// </summary>
    public async Task<Result<(int Copied, int Skipped)>> CopyWeekAsync(DateOnly sourceMonday, DateOnly targetMonday,
                                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.ManagePlanning);
        if (permitted.IsFailure) return Result.Failure<(int, int)>(permitted.FirstError);

        sourceMonday = PlanningChecks.WeekStart(sourceMonday);
        targetMonday = PlanningChecks.WeekStart(targetMonday);
        if (sourceMonday == targetMonday) return Result.Failure<(int, int)>(Error.Validation("Planning_SameWeek"));

        var source = await queries.GetShiftsAsync(sourceMonday, sourceMonday.AddDays(6), null, ct);
        var offset = targetMonday.DayNumber - sourceMonday.DayNumber;
        var people = (await queries.GetEmployeesAsync(targetMonday, targetMonday.AddDays(6), ct))
            .ToDictionary(p => p.EmployeeId);

        int copied = 0, skipped = 0;
        var added = new List<ShiftCell>();
        foreach (var s in source)
        {
            var cell = s with { ShiftId = Guid.Empty, Date = s.Date.AddDays(offset) };
            var employed = people.TryGetValue(s.EmployeeId, out var p) && cell.Date >= p.HireDate
                           && (p.ExitDate is null || cell.Date <= p.ExitDate);
            if (!employed || await BlockedAsync(cell, ct, added) is not null)
            {
                skipped++;
                continue;
            }

            await shifts.AddAsync(new ShiftAssignment(cell.EmployeeId, currentUser.SiteId, cell.Date, cell.Kind,
                                                      cell.Start, cell.End, cell.Note), ct);
            added.Add(cell);
            copied++;
        }

        if (copied > 0)
        {
            unitOfWork.AuditReason = $"Planning : semaine du {sourceMonday:dd/MM/yyyy} recopiée sur celle du " +
                                     $"{targetMonday:dd/MM/yyyy} ({copied} postes)";
            await unitOfWork.SaveChangesAsync(ct);
        }

        return Result.Success((copied, skipped));
    }

    /// <summary>What no warning can excuse: approved leave that day, or two posts at once.</summary>
    private async Task<Error?> BlockedAsync(ShiftCell cell, CancellationToken ct, IReadOnlyList<ShiftCell>? pending = null)
    {
        var leaves = await queries.GetLeavesAsync(cell.Date, cell.Date, ct);
        if (leaves.Any(l => l.EmployeeId == cell.EmployeeId && l.Status == LeaveStatus.Approved))
            return Error.Conflict("Planning_OnLeave");

        var around = (await queries.GetShiftsAsync(cell.Date.AddDays(-1), cell.Date.AddDays(1), cell.EmployeeId, ct))
            .Concat(pending?.Where(p => p.EmployeeId == cell.EmployeeId) ?? []);

        foreach (var other in around)
        {
            if (cell.Kind == ShiftKind.OnCall || other.Kind == ShiftKind.OnCall)
            {
                // On call is a whole day at home: once a day, alongside any worked post.
                if (cell.Kind == other.Kind && cell.Date == other.Date) return Error.Conflict("Planning_ShiftOverlaps");
                continue;
            }

            if (cell.StartsAt < other.EndsAt && other.StartsAt < cell.EndsAt)
                return Error.Conflict("Planning_ShiftOverlaps");
        }

        return null;
    }
}

public interface IPlanningQueries
{
    /// <summary>Staff of the current site employed at some point of the period.</summary>
    Task<IReadOnlyList<PlanningEmployee>> GetEmployeesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<IReadOnlyList<ShiftCell>> GetShiftsAsync(DateOnly from, DateOnly to, Guid? employeeId,
                                                  CancellationToken ct = default);

    /// <summary>Pending and approved leave touching the period.</summary>
    Task<IReadOnlyList<LeaveSpan>> GetLeavesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}
