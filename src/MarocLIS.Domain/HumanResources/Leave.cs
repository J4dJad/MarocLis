using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.HumanResources;

/// <summary>
/// A leave or absence request, from submission to decision.
/// <para>
/// The working days are computed once, at submission, from the calendar in force: Sundays and
/// public holidays are not counted (Code du travail: congé en jours ouvrables). They are stored
/// with the request so that a holiday entered later does not silently change a balance that was
/// already debited.
/// </para>
/// </summary>
public sealed class LeaveRequest : AuditableAggregateRoot
{
    private LeaveRequest() { }

    public LeaveRequest(Guid employeeId, LeaveType type, DateOnly startDate, DateOnly endDate,
                        bool startsAfternoon, bool endsMorning, decimal days, string? reason,
                        bool justificationProvided, DateTimeOffset submittedAt, Guid submittedByUserId)
    {
        EmployeeId = employeeId;
        Type = type;
        StartDate = startDate;
        EndDate = endDate;
        StartsAfternoon = startsAfternoon;
        EndsMorning = endsMorning;
        Days = days;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        JustificationProvided = justificationProvided;
        SubmittedAt = submittedAt;
        SubmittedByUserId = submittedByUserId;
    }

    public Guid EmployeeId { get; private set; }
    public LeaveType Type { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }

    /// <summary>The first day is taken from the afternoon only.</summary>
    public bool StartsAfternoon { get; private set; }

    /// <summary>The last day is taken in the morning only.</summary>
    public bool EndsMorning { get; private set; }

    /// <summary>Days counted against the entitlement, computed at submission.</summary>
    public decimal Days { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Medical certificate, civil-status document… handed to HR.</summary>
    public bool JustificationProvided { get; set; }

    /// <summary>The accident file a work-accident stoppage belongs to.</summary>
    public Guid? WorkAccidentId { get; set; }

    public LeaveStatus Status { get; private set; } = LeaveStatus.Pending;

    public DateTimeOffset SubmittedAt { get; private set; }
    public Guid SubmittedByUserId { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public string? DecisionComment { get; private set; }

    public bool Overlaps(DateOnly from, DateOnly to) => StartDate <= to && EndDate >= from;

    public bool IsLive => Status is LeaveStatus.Pending or LeaveStatus.Approved;

    public Result Approve(DateTimeOffset now, Guid byUserId, string? comment)
    {
        if (Status != LeaveStatus.Pending) return Result.Failure(Error.Conflict("Leave_NotPending"));

        if (LeaveRules.RequiresJustification(Type) && !JustificationProvided)
            return Result.Failure(Error.Validation("Leave_JustificationMissing"));

        Status = LeaveStatus.Approved;
        DecidedAt = now;
        DecidedByUserId = byUserId;
        DecisionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        return Result.Success();
    }

    /// <summary>A refusal always says why: the employee reads it.</summary>
    public Result Refuse(DateTimeOffset now, Guid byUserId, string comment)
    {
        if (Status != LeaveStatus.Pending) return Result.Failure(Error.Conflict("Leave_NotPending"));
        if (string.IsNullOrWhiteSpace(comment)) return Result.Failure(Error.Validation("Leave_RefusalCommentRequired"));

        Status = LeaveStatus.Refused;
        DecidedAt = now;
        DecidedByUserId = byUserId;
        DecisionComment = comment.Trim();
        return Result.Success();
    }

    /// <summary>
    /// Withdrawn by the employee or HR. Possible until the leave has started: a leave in progress
    /// or taken is a fact, corrected by HR with a balance adjustment.
    /// </summary>
    public Result Cancel(DateOnly today, DateTimeOffset now, Guid byUserId, string? comment)
    {
        if (!IsLive) return Result.Failure(Error.Conflict("Leave_NotCancellable"));
        if (Status == LeaveStatus.Approved && StartDate <= today)
            return Result.Failure(Error.Conflict("Leave_AlreadyStarted"));

        Status = LeaveStatus.Cancelled;
        DecidedAt = now;
        DecidedByUserId = byUserId;
        DecisionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        return Result.Success();
    }
}

public enum LeaveStatus
{
    Pending = 0,
    Approved = 1,
    Refused = 2,
    Cancelled = 3
}

public enum LeaveType
{
    /// <summary>Congé annuel payé.</summary>
    Annual = 0,
    Unpaid = 1,
    Sick = 2,
    Maternity = 3,
    /// <summary>Naissance (père).</summary>
    Birth = 4,
    Marriage = 5,
    ChildMarriage = 6,
    /// <summary>Décès du conjoint, d'un enfant, d'un ascendant.</summary>
    CloseBereavement = 7,
    /// <summary>Décès d'un frère ou d'une sœur.</summary>
    SiblingBereavement = 8,
    /// <summary>Circoncision, opération chirurgicale du conjoint ou d'un enfant.</summary>
    FamilyEvent = 9,
    WorkAccident = 10,
    /// <summary>Absence non justifiée, saisie par la RH.</summary>
    UnjustifiedAbsence = 11
}

/// <summary>
/// The Moroccan leave rules as data. Defaults from the Code du travail; a collective agreement or
/// company accord more favourable to the employee replaces them.
/// </summary>
public static class LeaveRules
{
    /// <summary>Monthly accrual for an adult: 1.5 working days per month of service.</summary>
    public const decimal MonthlyAccrual = 1.5m;

    /// <summary>Monthly accrual under 18: 2 days.</summary>
    public const decimal MonthlyAccrualMinor = 2m;

    /// <summary>Annual ceiling, seniority bonus included.</summary>
    public const decimal AnnualCeiling = 30m;

    /// <summary>Only paid annual leave is debited from the balance.</summary>
    public static bool DebitsBalance(LeaveType type) => type == LeaveType.Annual;

    /// <summary>Days granted by law for a family event; null where there is no fixed entitlement.</summary>
    public static decimal? Entitlement(LeaveType type) => type switch
    {
        LeaveType.Birth => 3m,
        LeaveType.Marriage => 4m,
        LeaveType.ChildMarriage => 2m,
        LeaveType.CloseBereavement => 3m,
        LeaveType.SiblingBereavement => 2m,
        LeaveType.FamilyEvent => 2m,
        LeaveType.Maternity => 98m,   // 14 weeks, calendar days
        _ => null
    };

    /// <summary>Sickness, maternity and accidents run in calendar days; the rest in working days.</summary>
    public static bool CountsCalendarDays(LeaveType type) =>
        type is LeaveType.Sick or LeaveType.Maternity or LeaveType.WorkAccident;

    public static bool RequiresJustification(LeaveType type) => type is LeaveType.Sick or LeaveType.Maternity
        or LeaveType.Birth or LeaveType.Marriage or LeaveType.ChildMarriage or LeaveType.CloseBereavement
        or LeaveType.SiblingBereavement or LeaveType.FamilyEvent or LeaveType.WorkAccident;

    /// <summary>Recorded by HR, not requested: nobody asks for an unjustified absence.</summary>
    public static bool IsHrOnly(LeaveType type) => type == LeaveType.UnjustifiedAbsence;

    /// <summary>
    /// The yearly entitlement: 18 working days (24 under 18), plus 1.5 days per full five years of
    /// service, never more than 30.
    /// </summary>
    public static decimal AnnualEntitlement(DateOnly hireDate, DateOnly? birthDate, DateOnly on)
    {
        var minor = birthDate is { } born && born.AddYears(18) > on;
        var basis = minor ? MonthlyAccrualMinor * 12 : MonthlyAccrual * 12;
        var years = FullYears(hireDate, on);
        return Math.Min(AnnualCeiling, basis + 1.5m * (years / 5));
    }

    /// <summary>
    /// A twelfth of the yearly entitlement, to the hundredth, a half rounded up (19.5 / 12 = 1.625
    /// gives 1.63). December takes what is left so the twelve months add up to the entitlement
    /// exactly: 11 × 1.63 + 1.57 = 19.5.
    /// </summary>
    public static decimal MonthlyEntitlement(DateOnly hireDate, DateOnly? birthDate, DateOnly on)
    {
        var annual = AnnualEntitlement(hireDate, birthDate, on);
        var monthly = Math.Round(annual / 12m, 2, MidpointRounding.AwayFromZero);
        return on.Month == 12 ? annual - 11m * monthly : monthly;
    }

    public static int FullYears(DateOnly from, DateOnly to)
    {
        var years = to.Year - from.Year;
        if (to < from.AddYears(years)) years--;
        return Math.Max(0, years);
    }

    /// <summary>
    /// Days a leave counts. Working days are Monday to Saturday; Sundays and public holidays are
    /// not counted. A half day at either end counts 0.5 when that day would have counted.
    /// </summary>
    public static decimal CountDays(LeaveType type, DateOnly start, DateOnly end, bool startsAfternoon,
                                    bool endsMorning, IReadOnlySet<DateOnly> holidays)
    {
        if (end < start) return 0m;

        if (CountsCalendarDays(type)) return end.DayNumber - start.DayNumber + 1;

        bool Counts(DateOnly d) => d.DayOfWeek != DayOfWeek.Sunday && !holidays.Contains(d);

        decimal days = 0m;
        for (var d = start; d <= end; d = d.AddDays(1))
            if (Counts(d)) days += 1m;

        if (startsAfternoon && Counts(start)) days -= 0.5m;
        if (endsMorning && Counts(end) && (end != start || !startsAfternoon)) days -= 0.5m;

        return Math.Max(0m, days);
    }
}

/// <summary>
/// One movement on an employee's annual-leave account: monthly accrual, leave taken, a cancelled
/// leave given back, or an HR adjustment (opening balance, correction). The balance is their sum.
/// </summary>
public sealed class LeaveLedgerEntry : AuditableAggregateRoot
{
    private LeaveLedgerEntry() { }

    public LeaveLedgerEntry(Guid employeeId, DateOnly date, LeaveLedgerKind kind, decimal days, string reference,
                            string? comment = null, Guid? leaveRequestId = null)
    {
        EmployeeId = employeeId;
        Date = date;
        Kind = kind;
        Days = days;
        Reference = reference;
        Comment = comment;
        LeaveRequestId = leaveRequestId;
    }

    public Guid EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public LeaveLedgerKind Kind { get; private set; }

    /// <summary>Positive for a credit, negative for a debit.</summary>
    public decimal Days { get; private set; }

    /// <summary>"ACQ-2026-09" for an accrual, so a month is never credited twice.</summary>
    public string Reference { get; private set; } = string.Empty;

    public string? Comment { get; private set; }
    public Guid? LeaveRequestId { get; private set; }

    public static string AccrualReference(int year, int month) => $"ACQ-{year:0000}-{month:00}";
}

public enum LeaveLedgerKind
{
    Accrual = 0,
    Taken = 1,
    Returned = 2,
    Adjustment = 3
}

/// <summary>
/// A public holiday. The civil ones fall on fixed dates; the religious ones move with the lunar
/// calendar and are entered each year once announced. A removed holiday stays removed: the civil
/// calendar is not put back over a decision of HR.
/// </summary>
public sealed class PublicHoliday : AuditableAggregateRoot, ISoftDeletable
{
    private PublicHoliday() { }

    public PublicHoliday(DateOnly date, string name, bool isReligious)
    {
        Date = date;
        Name = name.Trim();
        IsReligious = isReligious;
    }

    public DateOnly Date { get; private set; }
    public string Name { get; set; } = string.Empty;
    public bool IsReligious { get; private set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    /// <summary>The civil holidays, the same date every year.</summary>
    public static IReadOnlyList<(int Month, int Day, string Name)> FixedHolidays { get; } =
    [
        (1, 1, "Nouvel an"),
        (1, 11, "Manifeste de l'Indépendance"),
        (1, 14, "Nouvel an amazigh"),
        (5, 1, "Fête du Travail"),
        (7, 30, "Fête du Trône"),
        (8, 14, "Allégeance Oued Eddahab"),
        (8, 20, "Révolution du Roi et du Peuple"),
        (8, 21, "Fête de la Jeunesse"),
        (10, 31, "Fête de l'Unité"),
        (11, 6, "Marche Verte"),
        (11, 18, "Fête de l'Indépendance")
    ];
}
