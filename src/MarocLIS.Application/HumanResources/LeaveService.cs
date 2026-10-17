using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

public sealed record LeaveRow(
    Guid LeaveRequestId,
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    string? DepartmentName,
    LeaveType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    bool StartsAfternoon,
    bool EndsMorning,
    decimal Days,
    string? Reason,
    bool JustificationProvided,
    LeaveStatus Status,
    DateTimeOffset SubmittedAt,
    string? DecidedBy,
    string? DecisionComment,
    /// <summary>Colleagues of the same department away on some of the same days.</summary>
    int ColleaguesAway)
{
    public bool IsLive => Status is LeaveStatus.Pending or LeaveStatus.Approved;
}

public sealed record LeaveBalanceRow(
    Guid EmployeeId,
    string Number,
    string FullName,
    decimal AnnualEntitlement,
    decimal Credited,
    decimal Taken,
    decimal Pending,
    decimal Balance)
{
    /// <summary>What can still be asked for: the balance less the requests awaiting a decision.</summary>
    public decimal Available => Balance - Pending;
}

public sealed record LeaveSubmission
{
    /// <summary>Null: the signed-in user's own file.</summary>
    public Guid? EmployeeId { get; init; }
    public required LeaveType Type { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public bool StartsAfternoon { get; init; }
    public bool EndsMorning { get; init; }
    public string? Reason { get; init; }
    public bool JustificationProvided { get; init; }
}

public sealed record HolidayRow(Guid HolidayId, DateOnly Date, string Name, bool IsReligious);

/// <summary>A leave a new public holiday falls in, recounted: it now counts <see cref="DaysBefore"/> - <see cref="DaysAfter"/> days fewer.</summary>
public sealed record HolidayRecount(Guid LeaveRequestId, string EmployeeNumber, string EmployeeName, DateOnly StartDate,
                                    DateOnly EndDate, LeaveStatus Status, decimal DaysBefore, decimal DaysAfter);

/// <summary>What the accrual needs to know about one active employee.</summary>
public sealed record AccrualFacts(Guid EmployeeId, DateOnly HireDate, DateOnly? BirthDate, DateOnly? ExitDate,
                                  DateOnly TrackedFrom, IReadOnlySet<string> CreditedReferences);

/// <summary>
/// The monthly accrual, apart from the database. A month is credited once it is over, if the
/// employee was there for at least half of it; the reference makes a second run a no-op.
/// </summary>
public static class LeaveAccrual
{
    public static IEnumerable<LeaveLedgerEntry> Due(AccrualFacts e, DateOnly today)
    {
        var first = new DateOnly(e.TrackedFrom.Year, e.TrackedFrom.Month, 1);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);

        for (var month = first; month < currentMonth; month = month.AddMonths(1))
        {
            var reference = LeaveLedgerEntry.AccrualReference(month.Year, month.Month);
            if (e.CreditedReferences.Contains(reference)) continue;

            var last = month.AddMonths(1).AddDays(-1);
            var from = e.HireDate > month ? e.HireDate : month;
            var to = e.ExitDate is { } exit && exit < last ? exit : last;
            if (to.DayNumber - from.DayNumber + 1 < 15) continue;

            yield return new LeaveLedgerEntry(e.EmployeeId, last, LeaveLedgerKind.Accrual,
                                              LeaveRules.MonthlyEntitlement(e.HireDate, e.BirthDate, last),
                                              reference, $"Acquis de {month:MM/yyyy}");
        }
    }
}

/// <summary>
/// Leave requests, the annual-leave accounts and the public-holiday calendar.
/// <para>
/// Every employee may ask for leave for themselves; deciding is a separate right, and nobody
/// decides on their own request. Only paid annual leave moves the balance: it is debited when the
/// leave is approved and credited back if an approved leave is cancelled before it starts.
/// </para>
/// </summary>
public sealed class LeaveService(
    IRepository<LeaveRequest> requests,
    IRepository<LeaveLedgerEntry> ledger,
    IRepository<PublicHoliday> holidays,
    IRepository<Employee> employees,
    ILeaveQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    private bool Has(string permission) => currentUser.Permissions.Contains(permission);

    private bool ActsForOthers => Has(Permissions.HumanResources.Manage) || Has(Permissions.HumanResources.ApproveLeave);

    /// <summary>The signed-in user's own personnel file, if they have one.</summary>
    public Task<Guid?> OwnEmployeeIdAsync(CancellationToken ct = default) =>
        queries.EmployeeIdForUserAsync(currentUser.UserId, ct);

    public bool CanDecide => Has(Permissions.HumanResources.ApproveLeave);
    public bool CanActForOthers => ActsForOthers;

    public async Task<Result<IReadOnlyList<LeaveRow>>> GetRequestsAsync(bool pendingOnly, int year,
                                                                        CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.RequestLeave,
                                         Permissions.HumanResources.ApproveLeave, Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<LeaveRow>>(permitted.FirstError);

        // Without a deciding or HR right, an employee sees their own requests only.
        Guid? only = ActsForOthers || Has(Permissions.HumanResources.View) ? null : await OwnEmployeeIdAsync(ct) ?? Guid.Empty;

        return Result.Success(await queries.GetRequestsAsync(pendingOnly, year, only, ct));
    }

    public async Task<Result<IReadOnlyList<LeaveBalanceRow>>> GetBalancesAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.RequestLeave,
                                         Permissions.HumanResources.ApproveLeave, Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<LeaveBalanceRow>>(permitted.FirstError);

        await AccrueAsync(ct);

        Guid? only = ActsForOthers || Has(Permissions.HumanResources.View) ? null : await OwnEmployeeIdAsync(ct) ?? Guid.Empty;
        return Result.Success(await queries.GetBalancesAsync(clock.Today, only, ct));
    }

    /// <summary>Credits every completed month not credited yet. Safe to run as often as wanted.</summary>
    public async Task<int> AccrueAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var due = (await queries.GetAccrualFactsAsync(ct)).SelectMany(f => LeaveAccrual.Due(f, today)).ToList();
        if (due.Count == 0) return 0;

        foreach (var entry in due) await ledger.AddAsync(entry, ct);

        unitOfWork.AuditReason = $"Acquisition mensuelle des congés ({due.Count} mois crédités)";
        await unitOfWork.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task<LeaveBalanceRow?> BalanceOfAsync(Guid employeeId, CancellationToken ct) =>
        await queries.GetBalancesAsync(clock.Today, employeeId, ct) is [var row, ..] ? row : null;

    public async Task<Result<decimal>> PreviewDaysAsync(LeaveType type, DateOnly start, DateOnly end, bool startsAfternoon,
                                                        bool endsMorning, CancellationToken ct = default)
    {
        var calendar = await queries.GetHolidayDatesAsync(start, end, ct);
        return Result.Success(LeaveRules.CountDays(type, start, end, startsAfternoon, endsMorning, calendar));
    }

    public async Task<Result<Guid>> SubmitAsync(LeaveSubmission submission, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.RequestLeave, Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var own = await OwnEmployeeIdAsync(ct);
        var employeeId = submission.EmployeeId ?? own;
        if (employeeId is null) return Result.Failure<Guid>(Error.Validation("Leave_NoPersonnelFile"));

        if (employeeId != own && !ActsForOthers)
            return Result.Failure<Guid>(Error.Forbidden("Leave_OwnRequestsOnly"));

        if (LeaveRules.IsHrOnly(submission.Type) && !Has(Permissions.HumanResources.Manage))
            return Result.Failure<Guid>(Error.Forbidden("Leave_HrOnlyType"));

        if (submission.EndDate < submission.StartDate)
            return Result.Failure<Guid>(Error.Validation("Leave_EndBeforeStart"));

        var employee = await employees.GetByIdAsync(employeeId.Value, ct);
        if (employee is null) return Result.Failure<Guid>(Error.NotFound("Employee_NotFound"));
        // Still on the staff today, and for the whole leave: a departure recorded ahead leaves the
        // notice to be worked, with leave, up to the exit date.
        if (!employee.IsActiveOn(clock.Today) || submission.StartDate < employee.HireDate
            || (employee.ExitDate is { } exit && submission.EndDate > exit))
            return Result.Failure<Guid>(Error.Validation("Leave_EmployeeNotEmployed"));

        var calendar = await queries.GetHolidayDatesAsync(submission.StartDate, submission.EndDate, ct);
        var days = LeaveRules.CountDays(submission.Type, submission.StartDate, submission.EndDate,
                                        submission.StartsAfternoon, submission.EndsMorning, calendar);
        if (days <= 0m) return Result.Failure<Guid>(Error.Validation("Leave_NoWorkingDay"));

        if (LeaveRules.Entitlement(submission.Type) is { } allowed && days > allowed)
            return Result.Failure<Guid>(Error.Validation("Leave_ExceedsEntitlement", allowed));

        if (await queries.OverlappingLeaveAsync(employee.Id, submission.StartDate, submission.EndDate, null, ct) is { } clash)
            return Result.Failure<Guid>(Error.Conflict("Leave_Overlaps", clash.StartDate, clash.EndDate));

        if (LeaveRules.DebitsBalance(submission.Type))
        {
            await AccrueAsync(ct);
            var balance = await BalanceOfAsync(employee.Id, ct);
            var available = balance?.Available ?? 0m;
            if (days > available)
                return Result.Failure<Guid>(Error.Validation("Leave_InsufficientBalance", days, available));
        }

        var request = new LeaveRequest(employee.Id, submission.Type, submission.StartDate, submission.EndDate,
                                       submission.StartsAfternoon, submission.EndsMorning, days, submission.Reason,
                                       submission.JustificationProvided, clock.Now, currentUser.UserId);
        await requests.AddAsync(request, ct);

        unitOfWork.AuditReason = $"Demande de congé {submission.Type} de {employee.Number} du " +
                                 $"{submission.StartDate:dd/MM/yyyy} au {submission.EndDate:dd/MM/yyyy} ({days} j)";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(request.Id);
    }

    public async Task<Result> ApproveAsync(Guid requestId, string? comment, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.ApproveLeave);
        if (permitted.IsFailure) return permitted;

        var request = await requests.GetByIdAsync(requestId, ct);
        if (request is null) return Result.Failure(Error.NotFound("Leave_NotFound"));

        if (await OwnEmployeeIdAsync(ct) == request.EmployeeId)
            return Result.Failure(Error.Forbidden("Leave_CannotDecideOwn"));

        if (LeaveRules.DebitsBalance(request.Type))
        {
            await AccrueAsync(ct);
            var balance = await BalanceOfAsync(request.EmployeeId, ct);
            if ((balance?.Balance ?? 0m) < request.Days)
                return Result.Failure(Error.Validation("Leave_InsufficientBalance", request.Days, balance?.Balance ?? 0m));
        }

        var approved = request.Approve(clock.Now, currentUser.UserId, comment);
        if (approved.IsFailure) return approved;

        requests.Update(request);

        if (LeaveRules.DebitsBalance(request.Type))
            await ledger.AddAsync(new LeaveLedgerEntry(request.EmployeeId, request.StartDate, LeaveLedgerKind.Taken,
                                                       -request.Days, $"CONGE-{request.StartDate:yyyyMMdd}",
                                                       null, request.Id), ct);

        unitOfWork.AuditReason = $"Congé validé ({request.Type}, {request.StartDate:dd/MM/yyyy} - {request.EndDate:dd/MM/yyyy})";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RefuseAsync(Guid requestId, string comment, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.ApproveLeave);
        if (permitted.IsFailure) return permitted;

        var request = await requests.GetByIdAsync(requestId, ct);
        if (request is null) return Result.Failure(Error.NotFound("Leave_NotFound"));

        if (await OwnEmployeeIdAsync(ct) == request.EmployeeId)
            return Result.Failure(Error.Forbidden("Leave_CannotDecideOwn"));

        var refused = request.Refuse(clock.Now, currentUser.UserId, comment);
        if (refused.IsFailure) return refused;

        requests.Update(request);
        unitOfWork.AuditReason = $"Congé refusé : {comment.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> CancelAsync(Guid requestId, string? comment, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.RequestLeave, Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return permitted;

        var request = await requests.GetByIdAsync(requestId, ct);
        if (request is null) return Result.Failure(Error.NotFound("Leave_NotFound"));

        if (await OwnEmployeeIdAsync(ct) != request.EmployeeId && !ActsForOthers)
            return Result.Failure(Error.Forbidden("Leave_OwnRequestsOnly"));

        // An absence HR recorded is HR's to withdraw, as is a request HR entered on the employee's
        // behalf: the employee cancelling it would get the days paid.
        if (CancelRefusal(request, currentUser.UserId, Has(Permissions.HumanResources.Manage), ActsForOthers) is { } refused)
            return Result.Failure(refused);

        var wasApproved = request.Status == LeaveStatus.Approved;

        var cancelled = request.Cancel(clock.Today, clock.Now, currentUser.UserId, comment);
        if (cancelled.IsFailure) return cancelled;

        requests.Update(request);

        if (wasApproved && LeaveRules.DebitsBalance(request.Type))
            await ledger.AddAsync(new LeaveLedgerEntry(request.EmployeeId, clock.Today, LeaveLedgerKind.Returned,
                                                       request.Days, $"ANNUL-{request.StartDate:yyyyMMdd}",
                                                       comment, request.Id), ct);

        unitOfWork.AuditReason = $"Congé annulé ({request.Type}, {request.StartDate:dd/MM/yyyy})";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Why the caller may not cancel <paramref name="request"/>, or null. An HR-only type
    /// (unjustified absence) needs the HR right; a request someone else submitted needs a right to
    /// act for others (HR or the leave approver).
    /// </summary>
    public static Error? CancelRefusal(LeaveRequest request, Guid callerUserId, bool managesHr, bool actsForOthers)
    {
        if (LeaveRules.IsHrOnly(request.Type) && !managesHr) return Error.Forbidden("Leave_HrOnlyType");
        if (request.SubmittedByUserId != callerUserId && !actsForOthers)
            return Error.Forbidden("Leave_SubmittedByOther");
        return null;
    }

    /// <summary>An opening balance or a correction, always with its reason.</summary>
    public async Task<Result> AdjustAsync(Guid employeeId, decimal days, string comment, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return permitted;

        if (days == 0m || Math.Abs(days) > 60m) return Result.Failure(Error.Validation("Leave_AdjustmentInvalid"));
        if (string.IsNullOrWhiteSpace(comment)) return Result.Failure(Error.Validation("Leave_AdjustmentReasonRequired"));

        if (!await employees.ExistsAsync(employeeId, ct)) return Result.Failure(Error.NotFound("Employee_NotFound"));

        await ledger.AddAsync(new LeaveLedgerEntry(employeeId, clock.Today, LeaveLedgerKind.Adjustment, days,
                                                   $"AJUST-{clock.Now:yyyyMMddHHmmss}", comment.Trim()), ct);

        unitOfWork.AuditReason = $"Ajustement du solde de congés ({days:+0.##;-0.##} j) : {comment.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Public holidays -------------------------------------------------------------------------

    /// <summary>The year's holidays. The civil ones are put in on first reading; the religious ones are entered by HR.</summary>
    public async Task<Result<IReadOnlyList<HolidayRow>>> GetHolidaysAsync(int year, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.RequestLeave,
                                         Permissions.HumanResources.ApproveLeave, Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<HolidayRow>>(permitted.FirstError);

        var existing = await queries.GetHolidaysAsync(year, ct);
        var recorded = await queries.RecordedHolidayDatesAsync(year, ct);
        var missing = PublicHoliday.FixedHolidays
            .Select(h => (Date: new DateOnly(year, h.Month, h.Day), h.Name))
            .Where(h => !recorded.Contains(h.Date))
            .ToList();

        if (missing.Count > 0)
        {
            foreach (var (date, name) in missing) await holidays.AddAsync(new PublicHoliday(date, name, false), ct);
            unitOfWork.AuditReason = $"Jours fériés civils {year} ajoutés au calendrier";
            await unitOfWork.SaveChangesAsync(ct);
            existing = await queries.GetHolidaysAsync(year, ct);
        }

        return Result.Success(existing);
    }

    /// <summary>
    /// Adds a holiday and recounts the leaves it falls in.
    /// <para>
    /// A religious holiday is entered once announced, often after leaves around it were asked and
    /// approved: the day stayed counted as leave taken. When it is not past yet, every pending or
    /// approved leave covering it is counted again, an approved annual leave gets the difference
    /// back on its balance, and the leaves recounted are returned so the screen names them. A day
    /// already past is a fact: HR corrects it with an adjustment.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<HolidayRecount>>> AddHolidayAsync(DateOnly date, string name, bool religious,
                                                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<HolidayRecount>>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<IReadOnlyList<HolidayRecount>>(Error.Validation("Holiday_NameRequired"));
        if ((await queries.GetHolidaysAsync(date.Year, ct)).Any(h => h.Date == date))
            return Result.Failure<IReadOnlyList<HolidayRecount>>(Error.Conflict("Holiday_AlreadyExists", date));

        await holidays.AddAsync(new PublicHoliday(date, name, religious), ct);

        var recounted = new List<HolidayRecount>();
        if (date >= clock.Today)
        {
            var covering = await requests.ListAsync(r => (r.Status == LeaveStatus.Pending || r.Status == LeaveStatus.Approved)
                                                         && r.StartDate <= date && r.EndDate >= date, ct);
            var employeeIds = covering.Select(r => r.EmployeeId).Distinct().ToList();
            var staff = employeeIds.Count == 0
                ? new Dictionary<Guid, Employee>()
                : (await employees.ListAsync(e => employeeIds.Contains(e.Id), ct)).ToDictionary(e => e.Id);

            foreach (var request in covering.OrderBy(r => r.StartDate))
            {
                var calendar = new HashSet<DateOnly>(await queries.GetHolidayDatesAsync(request.StartDate, request.EndDate, ct)) { date };
                var before = request.Days;
                var fewer = request.RecountDays(calendar);
                if (fewer == 0m) continue;

                requests.Update(request);
                if (request.Status == LeaveStatus.Approved && LeaveRules.DebitsBalance(request.Type))
                    await ledger.AddAsync(new LeaveLedgerEntry(request.EmployeeId, clock.Today, LeaveLedgerKind.Returned, fewer,
                                                               $"FERIE-{date:yyyyMMdd}", $"Jour férié {date:dd/MM/yyyy} : {name.Trim()}",
                                                               request.Id), ct);

                var employee = staff.GetValueOrDefault(request.EmployeeId);
                recounted.Add(new HolidayRecount(request.Id, employee?.Number ?? string.Empty, employee?.FullName ?? string.Empty,
                                                 request.StartDate, request.EndDate, request.Status, before, request.Days));
            }
        }

        unitOfWork.AuditReason = recounted.Count == 0
            ? $"Jour férié ajouté : {date:dd/MM/yyyy} {name.Trim()}"
            : $"Jour férié ajouté : {date:dd/MM/yyyy} {name.Trim()} ({recounted.Count} congé(s) recompté(s))";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success<IReadOnlyList<HolidayRecount>>(recounted);
    }

    public async Task<Result> RemoveHolidayAsync(Guid holidayId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Manage);
        if (permitted.IsFailure) return permitted;

        var holiday = await holidays.GetByIdAsync(holidayId, ct);
        if (holiday is null) return Result.Failure(Error.NotFound("Holiday_NotFound"));

        holidays.SoftDelete(holiday, currentUser.UserId, "Retiré du calendrier");
        unitOfWork.AuditReason = $"Jour férié retiré : {holiday.Date:dd/MM/yyyy} {holiday.Name}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>Read side of leave and holidays.</summary>
public interface ILeaveQueries
{
    Task<Guid?> EmployeeIdForUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<LeaveRow>> GetRequestsAsync(bool pendingOnly, int year, Guid? employeeId,
                                                   CancellationToken ct = default);

    Task<IReadOnlyList<LeaveBalanceRow>> GetBalancesAsync(DateOnly today, Guid? employeeId, CancellationToken ct = default);

    Task<IReadOnlyList<AccrualFacts>> GetAccrualFactsAsync(CancellationToken ct = default);

    /// <summary>A pending or approved request of the employee sharing a day with the period.</summary>
    Task<LeaveRow?> OverlappingLeaveAsync(Guid employeeId, DateOnly from, DateOnly to, Guid? excluding,
                                          CancellationToken ct = default);

    Task<IReadOnlySet<DateOnly>> GetHolidayDatesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<IReadOnlyList<HolidayRow>> GetHolidaysAsync(int year, CancellationToken ct = default);

    /// <summary>Every date of the year ever put in the calendar, removed ones included.</summary>
    Task<IReadOnlySet<DateOnly>> RecordedHolidayDatesAsync(int year, CancellationToken ct = default);
}
