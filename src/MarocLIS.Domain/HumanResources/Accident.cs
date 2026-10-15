using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.HumanResources;

/// <summary>
/// A work accident, a commuting accident or a blood exposure (accident d'exposition au sang, AES),
/// from the declaration to the insurer to the return to work.
/// <para>
/// In Morocco work accidents are not covered by the CNSS but by the employer's private insurance
/// (loi 18-12), which must receive the declaration within 48 hours. The file keeps what the
/// declaration says, the insurer's references, the stoppages, the follow-up and, for a blood
/// exposure, the dates of the serological checks. Never a test result or a diagnosis: those stay
/// with the occupational physician.
/// </para>
/// </summary>
public sealed class WorkAccident : AuditableAggregateRoot, ISiteScoped
{
    // Named after the navigations: EF Core finds a collection's backing field by name.
    private readonly List<AccidentEvent> _events = [];
    private readonly List<SerologyCheck> _checks = [];

    private WorkAccident() { }

    public WorkAccident(string number, Guid employeeId, Guid siteId, AccidentKind kind, DateTime occurredAt)
    {
        Number = number;
        EmployeeId = employeeId;
        SiteId = siteId;
        Kind = kind;
        OccurredAt = occurredAt;
    }

    /// <summary>AT-2026-0001: the reference given to the insurer and printed on the declaration.</summary>
    public string Number { get; private set; } = string.Empty;

    public Guid EmployeeId { get; private set; }
    public Guid SiteId { get; set; }
    public AccidentKind Kind { get; set; }

    /// <summary>Local date and time of the accident.</summary>
    public DateTime OccurredAt { get; set; }

    public string? Place { get; set; }
    public string Circumstances { get; set; } = string.Empty;

    /// <summary>Nature and seat of the injuries, as the first certificate describes them.</summary>
    public string? Injuries { get; set; }

    public string? Witnesses { get; set; }

    /// <summary>First aid given on the spot.</summary>
    public bool FirstAidGiven { get; set; }

    // ---- Blood exposure ------------------------------------------------------------------------

    public ExposureType? Exposure { get; set; }

    /// <summary>Whether the source (patient sample) is identified, so its status can be sought.</summary>
    public bool SourceIdentified { get; set; }

    /// <summary>When the exposed employee saw the referring physician; within hours for an AES.</summary>
    public DateTime? ConsultedAt { get; set; }

    // ---- Insurer --------------------------------------------------------------------------------

    public string? InsurerName { get; set; }
    public string? InsurerClaimNumber { get; set; }
    public DateOnly? DeclaredToInsurerOn { get; set; }

    /// <summary>The employer advances the daily allowances and is reimbursed by the insurer.</summary>
    public bool Subrogation { get; set; }

    // ---- Outcome --------------------------------------------------------------------------------

    public DateOnly? ReturnedToWorkOn { get; private set; }

    /// <summary>Permanent incapacity rate set by the insurer's physician, in percent.</summary>
    public decimal? IncapacityRate { get; set; }

    public string? Note { get; set; }

    public bool IsClosed { get; private set; }

    public IReadOnlyCollection<AccidentEvent> Events => _events.AsReadOnly();
    public IReadOnlyCollection<SerologyCheck> Checks => _checks.AsReadOnly();

    /// <summary>The legal time to declare to the insurer.</summary>
    public static readonly TimeSpan DeclarationDelay = TimeSpan.FromHours(48);

    public DateTime DeclarationDueAt => OccurredAt + DeclarationDelay;

    public bool IsBloodExposure => Kind == AccidentKind.BloodExposure;

    public Result Validate()
    {
        if (string.IsNullOrWhiteSpace(Circumstances))
            return Result.Failure(Error.Validation("Accident_CircumstancesRequired"));
        if (IsBloodExposure && Exposure is null)
            return Result.Failure(Error.Validation("Accident_ExposureRequired"));
        if (DeclaredToInsurerOn is { } declared && declared < DateOnly.FromDateTime(OccurredAt))
            return Result.Failure(Error.Validation("Accident_DeclaredBeforeAccident"));
        if (ConsultedAt is { } consulted && consulted < OccurredAt)
            return Result.Failure(Error.Validation("Accident_ConsultedBeforeAccident"));
        if (IncapacityRate is < 0 or > 100)
            return Result.Failure(Error.Validation("Accident_IncapacityInvalid"));
        return Result.Success();
    }

    /// <summary>
    /// The serological follow-up of a blood exposure: a baseline at once, then one, three and six
    /// months after. The physician may change the dates; a check is marked done, never with its result.
    /// </summary>
    public void ScheduleSerology()
    {
        if (!IsBloodExposure || _checks.Count > 0) return;

        var day = DateOnly.FromDateTime(OccurredAt);
        foreach (var (label, due) in AccidentRules.SerologySchedule(day))
            _checks.Add(new SerologyCheck(Id, label, due));
    }

    public Result MarkCheckDone(Guid checkId, DateOnly on)
    {
        var check = _checks.FirstOrDefault(c => c.Id == checkId);
        if (check is null) return Result.Failure(Error.NotFound("Accident_CheckNotFound"));
        if (on < DateOnly.FromDateTime(OccurredAt)) return Result.Failure(Error.Validation("Accident_CheckBeforeAccident"));
        check.DoneOn = on;
        return Result.Success();
    }

    public void Log(AccidentEventKind kind, DateOnly on, string? text, Guid byUserId) =>
        _events.Add(new AccidentEvent(Id, kind, on, string.IsNullOrWhiteSpace(text) ? null : text.Trim(), byUserId));

    public Result RecordReturn(DateOnly on, Guid byUserId)
    {
        if (on < DateOnly.FromDateTime(OccurredAt)) return Result.Failure(Error.Validation("Accident_ReturnBeforeAccident"));
        ReturnedToWorkOn = on;
        Log(AccidentEventKind.Return, on, null, byUserId);
        return Result.Success();
    }

    /// <summary>A relapse puts the employee off work again.</summary>
    public void ClearReturn() => ReturnedToWorkOn = null;

    /// <summary>The file is closed once the insurer has settled it; a relapse reopens it.</summary>
    public void Close(DateOnly on, string? note, Guid byUserId)
    {
        IsClosed = true;
        Log(AccidentEventKind.Closed, on, note, byUserId);
    }

    public void Reopen(DateOnly on, string? note, Guid byUserId)
    {
        IsClosed = false;
        Log(AccidentEventKind.Relapse, on, note, byUserId);
    }
}

public enum AccidentKind
{
    /// <summary>Accident du travail, on the premises or during a mission (home collection…).</summary>
    WorkAccident = 0,
    /// <summary>Accident de trajet, between home and work.</summary>
    Commuting = 1,
    /// <summary>Accident d'exposition au sang ou à un liquide biologique.</summary>
    BloodExposure = 2
}

public enum ExposureType
{
    /// <summary>Piqûre par aiguille.</summary>
    Needlestick = 0,
    /// <summary>Coupure par objet tranchant.</summary>
    Cut = 1,
    /// <summary>Projection sur muqueuse (œil, bouche).</summary>
    MucousSplash = 2,
    /// <summary>Projection sur peau lésée.</summary>
    SkinSplash = 3
}

public enum AccidentEventKind
{
    Declared = 0,
    /// <summary>Relance de l'assureur.</summary>
    InsurerReminder = 1,
    InsurerResponse = 2,
    Stoppage = 3,
    Extension = 4,
    Return = 5,
    Relapse = 6,
    Note = 7,
    Closed = 8
}

/// <summary>A dated line of the file's history: declaration, reminders, stoppages, return.</summary>
public sealed class AccidentEvent : Entity
{
    private AccidentEvent() { }

    internal AccidentEvent(Guid accidentId, AccidentEventKind kind, DateOnly on, string? text, Guid byUserId)
    {
        WorkAccidentId = accidentId;
        Kind = kind;
        On = on;
        Text = text;
        ByUserId = byUserId;
    }

    public Guid WorkAccidentId { get; private set; }
    public AccidentEventKind Kind { get; private set; }
    public DateOnly On { get; private set; }
    public string? Text { get; private set; }
    public Guid ByUserId { get; private set; }
}

/// <summary>A serological check after a blood exposure: when it is due and when it was done.</summary>
public sealed class SerologyCheck : Entity
{
    private SerologyCheck() { }

    internal SerologyCheck(Guid accidentId, string label, DateOnly dueOn)
    {
        WorkAccidentId = accidentId;
        Label = label;
        DueOn = dueOn;
    }

    public Guid WorkAccidentId { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public DateOnly DueOn { get; private set; }
    public DateOnly? DoneOn { get; set; }
}

public static class AccidentRules
{
    /// <summary>The insurer's daily allowance: two thirds of the daily salary (loi 18-12).</summary>
    public const decimal DailyAllowanceShare = 2m / 3m;

    /// <summary>
    /// What the employer advances under subrogation for the days withheld from the salary. A first
    /// figure: the insurer's statement, which follows its own daily rate, decides the reimbursement.
    /// </summary>
    public static decimal Allowance(decimal monthlySalary, decimal monthlyWorkingDays, decimal days) =>
        monthlyWorkingDays <= 0 || days <= 0 ? 0m
            : Math.Round(monthlySalary / monthlyWorkingDays * days * DailyAllowanceShare, 2, MidpointRounding.AwayFromZero);

    public static IReadOnlyList<(string Label, DateOnly Due)> SerologySchedule(DateOnly exposure) =>
    [
        ("Bilan initial (J0)", exposure),
        ("Contrôle à 1 mois", exposure.AddMonths(1)),
        ("Contrôle à 3 mois", exposure.AddMonths(3)),
        ("Contrôle à 6 mois", exposure.AddMonths(6))
    ];
}
