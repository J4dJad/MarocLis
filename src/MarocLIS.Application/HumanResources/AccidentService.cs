using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.HumanResources;

public sealed record AccidentRow(
    Guid AccidentId,
    string Number,
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    string? DepartmentName,
    AccidentKind Kind,
    DateTime OccurredAt,
    string? Place,
    DateOnly? DeclaredToInsurerOn,
    decimal StoppageDays,
    DateOnly? ReturnedToWorkOn,
    DateOnly? NextCheckDue,
    bool IsClosed)
{
    /// <summary>Not declared to the insurer and the 48 hours are over.</summary>
    public bool DeclarationLate(DateTime now) => DeclaredToInsurerOn is null && now > OccurredAt + WorkAccident.DeclarationDelay;
}

public sealed record AccidentRequest
{
    public Guid? AccidentId { get; init; }
    public required Guid EmployeeId { get; init; }
    public required AccidentKind Kind { get; init; }
    public required DateTime OccurredAt { get; init; }
    public string? Place { get; init; }
    public string Circumstances { get; init; } = string.Empty;
    public string? Injuries { get; init; }
    public string? Witnesses { get; init; }
    public bool FirstAidGiven { get; init; }
    public ExposureType? Exposure { get; init; }
    public bool SourceIdentified { get; init; }
    public DateTime? ConsultedAt { get; init; }
    public string? InsurerName { get; init; }
    public string? InsurerClaimNumber { get; init; }
    public DateOnly? DeclaredToInsurerOn { get; init; }
    public bool Subrogation { get; init; }
    public decimal? IncapacityRate { get; init; }
    public string? Note { get; init; }
}

public sealed record AccidentEventRow(DateOnly On, AccidentEventKind Kind, string? Text, string? By);

public sealed record SerologyCheckRow(Guid CheckId, string Label, DateOnly DueOn, DateOnly? DoneOn);

public sealed record StoppageRow(Guid LeaveRequestId, DateOnly StartDate, DateOnly EndDate, decimal Days, LeaveStatus Status);

public sealed record AccidentDetail(
    AccidentRequest Request,
    string Number,
    string EmployeeNumber,
    string EmployeeName,
    DateOnly? ReturnedToWorkOn,
    bool IsClosed,
    IReadOnlyList<AccidentEventRow> Events,
    IReadOnlyList<SerologyCheckRow> Checks,
    IReadOnlyList<StoppageRow> Stoppages);

/// <summary>The accidents of a year by department and kind, for the hygiene and safety committee.</summary>
public sealed record AccidentSummaryLine(string Department, AccidentKind Kind, int Count, decimal StoppageDays, int WithStoppage);

/// <summary>What the printed declaration needs.</summary>
public sealed record AccidentDeclarationDocument(
    InvoiceIssuer Employer,
    AccidentDetail Accident,
    HrDocumentEmployee Employee,
    string? DepartmentName,
    DateOnly IssuedOn,
    string IssuedBy);

public sealed record AccidentReportDocument(InvoiceIssuer Employer, int Year, IReadOnlyList<AccidentRow> Accidents,
                                            IReadOnlyList<AccidentSummaryLine> Summary, DateOnly IssuedOn);

public interface IAccidentRenderer
{
    byte[] RenderDeclaration(AccidentDeclarationDocument document);

    byte[] RenderReport(AccidentReportDocument document);
}

public interface IAccidentQueries
{
    Task<IReadOnlyList<AccidentRow>> GetAccidentsAsync(int year, CancellationToken ct = default);

    Task<IReadOnlyList<StoppageRow>> StoppagesAsync(Guid accidentId, CancellationToken ct = default);

    Task<string?> DepartmentOfAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>The insurer named on the latest file, to propose it on the next one.</summary>
    Task<string?> LastInsurerAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
}

/// <summary>
/// Work accidents and blood exposures: the declaration, the stoppages, the insurer's follow-up
/// and the serological checks.
/// <para>
/// A stoppage is entered as an approved leave of type « Accident du travail »: the planning then
/// refuses to place the employee, and the payroll withholds those days, which the insurer pays as
/// daily allowances.
/// </para>
/// </summary>
public sealed class AccidentService(
    IRepository<WorkAccident> accidents,
    IRepository<Employee> employees,
    IRepository<LeaveRequest> leave,
    IAccidentQueries queries,
    IPersonnelQueries personnelQueries,
    ILeaveQueries leaveQueries,
    IPayrollQueries payrollQueries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    private Result Require() => guard.Require(Permissions.HumanResources.Accidents);

    public async Task<Result<IReadOnlyList<AccidentRow>>> GetAccidentsAsync(int year, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.Accidents, Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<AccidentRow>>(permitted.FirstError);
        return Result.Success(await queries.GetAccidentsAsync(year, ct));
    }

    public async Task<string?> LastInsurerAsync(CancellationToken ct = default) => await queries.LastInsurerAsync(ct);

    /// <summary>The people an accident can be declared for, under the accident right alone.</summary>
    public async Task<Result<IReadOnlyList<EmployeeRow>>> GetEmployeesAsync(CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<EmployeeRow>>(permitted.FirstError);
        return Result.Success(await personnelQueries.GetEmployeesAsync(true, ct));
    }

    public async Task<Result<AccidentDetail>> GetAsync(Guid accidentId, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.HumanResources.Accidents, Permissions.HumanResources.View);
        if (permitted.IsFailure) return Result.Failure<AccidentDetail>(permitted.FirstError);

        var accident = await accidents.GetByIdWithDetailsAsync(accidentId, ct);
        if (accident is null) return Result.Failure<AccidentDetail>(Error.NotFound("Accident_NotFound"));
        var employee = await employees.GetByIdAsync(accident.EmployeeId, ct);

        var names = await queries.UserNamesAsync(accident.Events.Select(e => e.ByUserId).Distinct(), ct);
        return Result.Success(new AccidentDetail(
            new AccidentRequest
            {
                AccidentId = accident.Id, EmployeeId = accident.EmployeeId, Kind = accident.Kind,
                OccurredAt = accident.OccurredAt, Place = accident.Place, Circumstances = accident.Circumstances,
                Injuries = accident.Injuries, Witnesses = accident.Witnesses, FirstAidGiven = accident.FirstAidGiven,
                Exposure = accident.Exposure, SourceIdentified = accident.SourceIdentified,
                ConsultedAt = accident.ConsultedAt, InsurerName = accident.InsurerName,
                InsurerClaimNumber = accident.InsurerClaimNumber, DeclaredToInsurerOn = accident.DeclaredToInsurerOn,
                Subrogation = accident.Subrogation, IncapacityRate = accident.IncapacityRate, Note = accident.Note
            },
            accident.Number, employee?.Number ?? string.Empty, employee?.FullName ?? string.Empty,
            accident.ReturnedToWorkOn, accident.IsClosed,
            [.. accident.Events.OrderBy(e => e.On).Select(e => new AccidentEventRow(e.On, e.Kind, e.Text, names.GetValueOrDefault(e.ByUserId)))],
            [.. accident.Checks.OrderBy(c => c.DueOn).Select(c => new SerologyCheckRow(c.Id, c.Label, c.DueOn, c.DoneOn))],
            await queries.StoppagesAsync(accident.Id, ct)));
    }

    public async Task<Result<Guid>> SaveAsync(AccidentRequest request, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var employee = await employees.GetByIdAsync(request.EmployeeId, ct);
        if (employee is null) return Result.Failure<Guid>(Error.NotFound("Employee_NotFound"));

        var day = DateOnly.FromDateTime(request.OccurredAt);
        if (day < employee.HireDate || (employee.ExitDate is { } exit && day > exit))
            return Result.Failure<Guid>(Error.Validation("Accident_NotEmployed"));
        if (request.OccurredAt > clock.Now.LocalDateTime.AddMinutes(5))
            return Result.Failure<Guid>(Error.Validation("Accident_InFuture"));

        if (request.AccidentId is { } id)
        {
            var existing = await accidents.GetByIdWithDetailsAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Accident_NotFound"));
            if (existing.EmployeeId != request.EmployeeId)
                return Result.Failure<Guid>(Error.Validation("Accident_EmployeeChanged"));

            var wasDeclared = existing.DeclaredToInsurerOn is not null;
            Apply(existing, request);
            var valid = existing.Validate();
            if (valid.IsFailure) return Result.Failure<Guid>(valid.FirstError);

            if (!wasDeclared && existing.DeclaredToInsurerOn is { } on)
                existing.Log(AccidentEventKind.Declared, on, existing.InsurerName, currentUser.UserId);
            existing.ScheduleSerology();

            accidents.Update(existing);
            unitOfWork.AuditReason = $"Accident {existing.Number} modifié";
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(existing.Id);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.WorkAccident, null, day.Year, ct);
        if (number.IsFailure) return Result.Failure<Guid>(number.FirstError);

        var accident = new WorkAccident(number.Value, employee.Id, employee.SiteId, request.Kind, request.OccurredAt);
        Apply(accident, request);
        var check = accident.Validate();
        if (check.IsFailure) return Result.Failure<Guid>(check.FirstError);

        accident.ScheduleSerology();
        if (accident.DeclaredToInsurerOn is { } declared)
            accident.Log(AccidentEventKind.Declared, declared, accident.InsurerName, currentUser.UserId);

        await accidents.AddAsync(accident, ct);
        unitOfWork.AuditReason = $"Déclaration {accident.Number} ({accident.Kind}) pour {employee.Number}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);
        return Result.Success(accident.Id);
    }

    private static void Apply(WorkAccident a, AccidentRequest r)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        a.Kind = r.Kind;
        a.OccurredAt = r.OccurredAt;
        a.Place = Clean(r.Place);
        a.Circumstances = r.Circumstances.Trim();
        a.Injuries = Clean(r.Injuries);
        a.Witnesses = Clean(r.Witnesses);
        a.FirstAidGiven = r.FirstAidGiven;
        a.Exposure = r.Kind == AccidentKind.BloodExposure ? r.Exposure : null;
        a.SourceIdentified = r.Kind == AccidentKind.BloodExposure && r.SourceIdentified;
        a.ConsultedAt = r.ConsultedAt;
        a.InsurerName = Clean(r.InsurerName);
        a.InsurerClaimNumber = Clean(r.InsurerClaimNumber);
        a.DeclaredToInsurerOn = r.DeclaredToInsurerOn;
        a.Subrogation = r.Subrogation;
        a.IncapacityRate = r.IncapacityRate;
        a.Note = Clean(r.Note);
    }

    /// <summary>
    /// A stoppage, or its extension, from the medical certificate: an approved accident leave on
    /// those days. The day of the accident is paid by the employer, so a stoppage starts the day after at the earliest.
    /// </summary>
    public async Task<Result> AddStoppageAsync(Guid accidentId, DateOnly from, DateOnly to, bool relapse,
                                               CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        var accident = await accidents.GetByIdWithDetailsAsync(accidentId, ct);
        if (accident is null) return Result.Failure(Error.NotFound("Accident_NotFound"));
        if (accident.IsClosed && !relapse) return Result.Failure(Error.Conflict("Accident_Closed"));
        if (to < from) return Result.Failure(Error.Validation("Leave_EndBeforeStart"));
        if (from <= DateOnly.FromDateTime(accident.OccurredAt))
            return Result.Failure(Error.Validation("Accident_StoppageOnAccidentDay"));

        var employee = await employees.GetByIdAsync(accident.EmployeeId, ct);
        if (employee is null) return Result.Failure(Error.NotFound("Employee_NotFound"));
        if (employee.ExitDate is { } exit && to > exit) return Result.Failure(Error.Validation("Leave_EmployeeNotEmployed"));

        if (await leaveQueries.OverlappingLeaveAsync(employee.Id, from, to, null, ct) is { } clash)
            return Result.Failure(Error.Conflict("Leave_Overlaps", clash.StartDate, clash.EndDate));

        var previous = await queries.StoppagesAsync(accidentId, ct);
        var kind = relapse ? AccidentEventKind.Relapse
                 : previous.Any(s => s.Status == LeaveStatus.Approved) ? AccidentEventKind.Extension
                 : AccidentEventKind.Stoppage;

        var holidays = await leaveQueries.GetHolidayDatesAsync(from, to, ct);
        var days = LeaveRules.CountDays(LeaveType.WorkAccident, from, to, false, false, holidays);
        var request = new LeaveRequest(employee.Id, LeaveType.WorkAccident, from, to, false, false, days,
                                       $"{accident.Number} — {(relapse ? "rechute" : kind == AccidentEventKind.Extension ? "prolongation" : "arrêt")}",
                                       true, clock.Now, currentUser.UserId);
        request.WorkAccidentId = accident.Id;
        var approved = request.Approve(clock.Now, currentUser.UserId, accident.Number);
        if (approved.IsFailure) return approved;
        await leave.AddAsync(request, ct);

        if (relapse && accident.IsClosed) accident.Reopen(from, null, currentUser.UserId);
        else accident.Log(kind, from, $"{from:dd/MM/yyyy} → {to:dd/MM/yyyy} ({days:0.#} j)", currentUser.UserId);
        if (relapse) accident.ClearReturn();

        accidents.Update(accident);
        unitOfWork.AuditReason = $"Accident {accident.Number} : arrêt du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RecordReturnAsync(Guid accidentId, DateOnly on, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        if (on > clock.Today) return Result.Failure(Error.Validation("Accident_ReturnInFuture"));

        var accident = await accidents.GetByIdWithDetailsAsync(accidentId, ct);
        if (accident is null) return Result.Failure(Error.NotFound("Accident_NotFound"));

        // Back at work while a certificate still covers the day: the stoppage is shortened first.
        var stoppages = await queries.StoppagesAsync(accidentId, ct);
        if (stoppages.Where(s => s.Status == LeaveStatus.Approved).Select(s => (DateOnly?)s.EndDate).Max() is { } last && on <= last)
            return Result.Failure(Error.Validation("Accident_ReturnDuringStoppage", last));

        var returned = accident.RecordReturn(on, currentUser.UserId);
        if (returned.IsFailure) return returned;

        accidents.Update(accident);
        unitOfWork.AuditReason = $"Accident {accident.Number} : reprise le {on:dd/MM/yyyy}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> LogAsync(Guid accidentId, AccidentEventKind kind, string? text, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;
        if (kind is not (AccidentEventKind.InsurerReminder or AccidentEventKind.InsurerResponse or AccidentEventKind.Note))
            return Result.Failure(Error.Validation("Accident_EventKindInvalid"));
        if (string.IsNullOrWhiteSpace(text) && kind != AccidentEventKind.InsurerReminder)
            return Result.Failure(Error.Validation("Accident_EventTextRequired"));

        var accident = await accidents.GetByIdWithDetailsAsync(accidentId, ct);
        if (accident is null) return Result.Failure(Error.NotFound("Accident_NotFound"));

        accident.Log(kind, clock.Today, text, currentUser.UserId);
        accidents.Update(accident);
        unitOfWork.AuditReason = $"Accident {accident.Number} : suivi ({kind})";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> MarkCheckDoneAsync(Guid accidentId, Guid checkId, DateOnly on, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;
        if (on > clock.Today) return Result.Failure(Error.Validation("Accident_CheckInFuture"));

        var accident = await accidents.GetByIdWithDetailsAsync(accidentId, ct);
        if (accident is null) return Result.Failure(Error.NotFound("Accident_NotFound"));

        var done = accident.MarkCheckDone(checkId, on);
        if (done.IsFailure) return done;

        accidents.Update(accident);
        unitOfWork.AuditReason = $"Accident {accident.Number} : contrôle sérologique fait le {on:dd/MM/yyyy}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> CloseAsync(Guid accidentId, string? note, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        var accident = await accidents.GetByIdWithDetailsAsync(accidentId, ct);
        if (accident is null) return Result.Failure(Error.NotFound("Accident_NotFound"));
        if (accident.IsClosed) return Result.Failure(Error.Conflict("Accident_Closed"));
        if (accident.Checks.Any(c => c.DoneOn is null))
            return Result.Failure(Error.Validation("Accident_ChecksPending"));

        accident.Close(clock.Today, note, currentUser.UserId);
        accidents.Update(accident);
        unitOfWork.AuditReason = $"Accident {accident.Number} clos";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<AccidentDeclarationDocument>> GetDeclarationAsync(Guid accidentId, CancellationToken ct = default)
    {
        var detail = await GetAsync(accidentId, ct);
        if (detail.IsFailure) return Result.Failure<AccidentDeclarationDocument>(detail.FirstError);

        var employee = await employees.GetByIdAsync(detail.Value.Request.EmployeeId, ct);
        if (employee is null) return Result.Failure<AccidentDeclarationDocument>(Error.NotFound("Employee_NotFound"));

        return Result.Success(new AccidentDeclarationDocument(
            await payrollQueries.EmployerAsync(employee.SiteId, ct), detail.Value, HrDocumentService.Describe(employee),
            await queries.DepartmentOfAsync(employee.Id, ct), clock.Today, currentUser.FullName));
    }

    public async Task<Result<AccidentReportDocument>> GetReportAsync(int year, CancellationToken ct = default)
    {
        var rows = await GetAccidentsAsync(year, ct);
        if (rows.IsFailure) return Result.Failure<AccidentReportDocument>(rows.FirstError);

        return Result.Success(new AccidentReportDocument(
            await payrollQueries.EmployerAsync(currentUser.SiteId, ct), year, rows.Value, Summarise(rows.Value),
            clock.Today));
    }

    public static IReadOnlyList<AccidentSummaryLine> Summarise(IEnumerable<AccidentRow> rows) =>
    [
        .. rows.GroupBy(r => (Department: r.DepartmentName ?? "—", r.Kind))
               .OrderBy(g => g.Key.Department, StringComparer.CurrentCulture).ThenBy(g => g.Key.Kind)
               .Select(g => new AccidentSummaryLine(g.Key.Department, g.Key.Kind, g.Count(), g.Sum(r => r.StoppageDays),
                                                    g.Count(r => r.StoppageDays > 0)))
    ];
}
