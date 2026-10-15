using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.HumanResources;

/// <summary>
/// One post on the planning: who works where, on which day, from when to when.
/// <para>
/// A night shift ends the next morning; the end time is then earlier than the start and the
/// duration crosses midnight. An on-call day (astreinte) is waiting time at home, paid as a flat
/// rate: it counts neither towards the weekly hours nor against the weekly rest.
/// </para>
/// </summary>
public sealed class ShiftAssignment : AuditableAggregateRoot, ISiteScoped, ISoftDeletable
{
    private ShiftAssignment() { }

    public ShiftAssignment(Guid employeeId, Guid siteId, DateOnly date, ShiftKind kind, TimeOnly start, TimeOnly end,
                           string? note)
    {
        EmployeeId = employeeId;
        SiteId = siteId;
        Date = date;
        Kind = kind;
        Start = start;
        End = end;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public Guid EmployeeId { get; private set; }
    public Guid SiteId { get; set; }
    public DateOnly Date { get; private set; }
    public ShiftKind Kind { get; private set; }
    public TimeOnly Start { get; private set; }
    public TimeOnly End { get; private set; }
    public string? Note { get; private set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public decimal Hours => ShiftRules.Hours(Kind, Start, End);

    public DateTime StartsAt => Date.ToDateTime(Start);
    public DateTime EndsAt => End > Start ? Date.ToDateTime(End) : Date.AddDays(1).ToDateTime(End);
}

public enum ShiftKind
{
    /// <summary>Matin, ex. 7 h - 14 h.</summary>
    Morning = 0,
    Afternoon = 1,
    /// <summary>Journée complète.</summary>
    Day = 2,
    Saturday = 3,
    /// <summary>Garde de nuit.</summary>
    Night = 4,
    /// <summary>Garde de dimanche ou de jour férié.</summary>
    SundayHoliday = 5,
    /// <summary>Astreinte à domicile.</summary>
    OnCall = 6
}

public static class ShiftRules
{
    /// <summary>Legal full time outside agriculture.</summary>
    public const decimal WeeklyHoursLimit = 44m;

    /// <summary>Minimum rest between the end of one shift and the start of the next.</summary>
    public static readonly TimeSpan MinimumRest = TimeSpan.FromHours(11);

    public static (TimeOnly Start, TimeOnly End) DefaultHours(ShiftKind kind) => kind switch
    {
        ShiftKind.Morning => (new TimeOnly(7, 0), new TimeOnly(14, 0)),
        ShiftKind.Afternoon => (new TimeOnly(14, 0), new TimeOnly(20, 0)),
        ShiftKind.Day => (new TimeOnly(8, 0), new TimeOnly(17, 0)),
        ShiftKind.Saturday => (new TimeOnly(7, 30), new TimeOnly(13, 0)),
        ShiftKind.Night => (new TimeOnly(20, 0), new TimeOnly(8, 0)),
        ShiftKind.SundayHoliday => (new TimeOnly(8, 0), new TimeOnly(14, 0)),
        _ => (new TimeOnly(0, 0), new TimeOnly(0, 0))
    };

    /// <summary>Worked hours. An on-call day counts none until the employee is called in.</summary>
    public static decimal Hours(ShiftKind kind, TimeOnly start, TimeOnly end)
    {
        if (kind == ShiftKind.OnCall) return 0m;
        var span = end > start ? end - start : TimeSpan.FromHours(24) - (start - end);
        return Math.Round((decimal)span.TotalHours, 2);
    }

    public static bool IsWorked(ShiftKind kind) => kind != ShiftKind.OnCall;
}
