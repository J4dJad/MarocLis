using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>What « Accidents du travail » offers on the file selected, by right and by state.</summary>
public sealed record AccidentActions(
    bool New,
    bool Edit,
    bool Declaration,
    bool Stoppage,
    bool Return,
    bool Log,
    bool CheckDone,
    bool Close);

/// <summary>An accident as typed: the dates and the rate held as typed, so one that is no number is refused on its box.</summary>
public sealed class AccidentForm
{
    public Guid? EmployeeId { get; set; }
    public AccidentKind Kind { get; set; } = AccidentKind.WorkAccident;
    public DateTime OccurredAt { get; set; }
    public string? Place { get; set; }
    public string? Circumstances { get; set; }
    public string? Injuries { get; set; }
    public string? Witnesses { get; set; }
    public bool FirstAidGiven { get; set; }
    public ExposureType Exposure { get; set; } = ExposureType.Needlestick;
    public bool SourceIdentified { get; set; }
    public DateOnly? ConsultedOn { get; set; }
    public string? InsurerName { get; set; }
    public string? ClaimNumber { get; set; }
    public DateOnly? DeclaredOn { get; set; }
    public bool Subrogation { get; set; }
    public string? IncapacityRate { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// The rules of « Accidents du travail » that need no window: how an accident reads in the list
/// (its 48-hour declaration, its next check and their alarms), the file under it, the actions by
/// right and by state, the declaration form and its check, the stoppage, the return, the check
/// done and the follow-up step. The workstation's AccidentView and its dialogs and the web's
/// screen apply the same ones; what is done is AccidentService's.
/// </summary>
public static class AccidentScreenRules
{
    public const int PlaceMaxLength = 200;
    public const int CircumstancesMaxLength = 2000;
    public const int InjuriesMaxLength = 1000;
    public const int WitnessesMaxLength = 500;
    public const int InsurerMaxLength = 150;
    public const int ClaimMaxLength = 60;
    public const int NoteMaxLength = 1000;
    public const int RateMaxLength = TypedNumber.MeasureMaxLength;

    public const string EmployeeField = "accident-employee";
    public const string OccurredField = "accident-occurred";
    public const string CircumstancesField = "accident-circumstances";
    public const string RateField = "accident-rate";
    public const string ConsultedField = "accident-consulted";
    public const string DeclaredField = "accident-declared";
    public const string ExposureField = "accident-exposure";
    public const string FromField = "stoppage-from";
    public const string ToField = "stoppage-to";
    public const string DateField = "accident-date";
    public const string EventTextField = "accident-event-text";

    /// <summary>The steps the follow-up adds by hand; the others are recorded by their own buttons.</summary>
    public static IReadOnlyList<AccidentEventKind> LogKinds { get; } =
        [AccidentEventKind.InsurerReminder, AccidentEventKind.InsurerResponse, AccidentEventKind.Note];

    /// <summary>The years offered: this one and the five before.</summary>
    public static IReadOnlyList<int> Years(DateOnly today) => [.. Enumerable.Range(0, 6).Select(i => today.Year - i)];

    public static string Date(DateOnly? date) => PersonnelScreenRules.Date(date);

    /// <summary>« 12/10/2026 14:30 ».</summary>
    public static string At(DateTime at) => at.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The 48 hours are over and the insurer has not been told: the number and the declaration in the alarm's colours.</summary>
    public static bool DeclarationLate(AccidentRow row, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DeclarationLate(now);
    }

    /// <summary>The day the declaration is due by: the accident and 48 hours.</summary>
    public static DateTime DeclareBy(DateTime occurredAt) => occurredAt + WorkAccident.DeclarationDelay;

    /// <summary>The declaration: the day it was sent, else « non déclaré » once late, else the deadline.</summary>
    public static string Declared(AccidentRow row, DateTime now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.DeclaredToInsurerOn is { } d ? Date(d)
             : row.DeclarationLate(now) ? text("Accident.DeclarationLate")
             : text("Accident.DeclareBefore", DeclareBy(row.OccurredAt).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>A serological check due before today and not done: in the alarm's colours.</summary>
    public static bool CheckOverdue(AccidentRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.NextCheckDue is { } due && due < today;
    }

    /// <summary>Days of stoppage: « 7 ».</summary>
    public static string Days(decimal days, IFormatProvider culture) => days.ToString("0.#", culture);

    /// <summary>The state as a pill: an open file in amber, a closed one muted.</summary>
    public static HrPill State(AccidentRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.IsClosed ? new HrPill(text("Accident.Closed"), "Muted") : new HrPill(text("Accident.Open"), "Warning");
    }

    /// <summary>The head: the year's accidents, the files open, the declarations late.</summary>
    public static string Heading(IReadOnlyCollection<AccidentRow> rows, int year, DateTime now, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("Accident.Heading", rows.Count(r => r.OccurredAt.Year == year), rows.Count(r => !r.IsClosed),
                    rows.Count(r => r.DeclarationLate(now)));
    }

    /// <summary>
    /// The file in a line: number, person and kind; for a blood exposure its type and whether the
    /// source is known; the insurer and its claim number; the subrogation; the incapacity rate.
    /// </summary>
    public static string Summary(AccidentDetail a, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(text);
        var r = a.Request;
        var parts = new List<string> { $"{a.Number} · {a.EmployeeName} · {text($"AccidentKind.{r.Kind}")}" };
        if (r.Kind == AccidentKind.BloodExposure && r.Exposure is { } exposure)
            parts.Add(text($"ExposureType.{exposure}") + (r.SourceIdentified ? " · " + text("Accident.SourceIdentified") : string.Empty));
        if (!string.IsNullOrWhiteSpace(r.InsurerName))
            parts.Add(r.InsurerClaimNumber is { } claim ? text("Accident.InsurerClaim", r.InsurerName, claim) : r.InsurerName);
        if (r.Subrogation) parts.Add(text("Accident.Subrogation"));
        if (r.IncapacityRate is { } rate) parts.Add(text("Accident.IncapacityShort", rate.ToString("0.##", culture)));
        return string.Join("   ·   ", parts);
    }

    /// <summary>
    /// The actions on the file selected. Declaring and every step of the file are the accident
    /// right's; the declaration is printed by whoever sees the file. A return follows an approved
    /// stoppage, a check is marked done while one is pending, an open file is closed.
    /// </summary>
    public static AccidentActions Actions(AccidentDetail? current, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var may = has(Permissions.HumanResources.Accidents);
        var any = current is not null;
        var open = current is { IsClosed: false };
        return new AccidentActions(
            New: may,
            Edit: may && any,
            Declaration: any,
            Stoppage: may && any,
            Return: may && open && current!.Stoppages.Any(s => s.Status == LeaveStatus.Approved),
            Log: may && any,
            CheckDone: may && open && current!.Checks.Any(c => c.DoneOn is null),
            Close: may && open);
    }

    /// <summary>
    /// Whom an accident can be declared for: the staff, someone serving a notice, someone gone
    /// within the month after (see <see cref="AccidentService.IsDeclarableFor"/>); the file's own
    /// person always, when it is completed.
    /// </summary>
    public static IReadOnlyList<EmployeeRow> Declarable(IReadOnlyList<EmployeeRow> people, DateOnly today, Guid? fileEmployee)
    {
        ArgumentNullException.ThrowIfNull(people);
        return [.. people.Where(p => AccidentService.IsDeclarableFor(p.ExitDate, today) || fileEmployee == p.EmployeeId)];
    }

    /// <summary>The form for a new accident (now, the last insurer proposed) or for the file being completed.</summary>
    public static AccidentForm FormFor(AccidentDetail? detail, string? lastInsurer, DateTime now, IFormatProvider culture)
    {
        if (detail is null)
            return new AccidentForm { OccurredAt = Minute(now), InsurerName = lastInsurer };

        var r = detail.Request;
        return new AccidentForm
        {
            EmployeeId = r.EmployeeId, Kind = r.Kind, OccurredAt = Minute(r.OccurredAt), Place = r.Place,
            Circumstances = r.Circumstances, Injuries = r.Injuries, Witnesses = r.Witnesses, FirstAidGiven = r.FirstAidGiven,
            Exposure = r.Exposure ?? ExposureType.Needlestick, SourceIdentified = r.SourceIdentified,
            ConsultedOn = r.ConsultedAt is { } c ? DateOnly.FromDateTime(c) : null,
            InsurerName = r.InsurerName, ClaimNumber = r.InsurerClaimNumber, DeclaredOn = r.DeclaredToInsurerOn,
            Subrogation = r.Subrogation, IncapacityRate = r.IncapacityRate is { } rate ? TypedNumber.ForEditing(rate, 0, culture) : null, Note = r.Note
        };
    }

    private static DateTime Minute(DateTime at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0);

    /// <summary>The exposure type and the source are a blood exposure's only.</summary>
    public static bool IsBloodExposure(AccidentKind kind) => kind == AccidentKind.BloodExposure;

    /// <summary>
    /// The accident typed, checked: a person, circumstances described, a rate between 0 and 100 %, no
    /// date ahead of today. The consultation's day keeps the accident's hour when it is the same day,
    /// else is put at nine. A refusal names its box.
    /// </summary>
    public static (FieldRefusal? Refusal, AccidentRequest? Request) Check(AccidentForm form, Guid? accidentId, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.EmployeeId is not { } employee) return (new FieldRefusal(EmployeeField, "Leave_NoEmployees"), null);
        if (string.IsNullOrWhiteSpace(form.Circumstances)) return (new FieldRefusal(CircumstancesField, "Accident_CircumstancesRequired"), null);

        var today = DateOnly.FromDateTime(now);
        var occurred = Minute(form.OccurredAt);
        if (DateOnly.FromDateTime(occurred) > today) return (new FieldRefusal(OccurredField, "Accident_InFuture"), null);
        if (form.ConsultedOn is { } seen && seen > today) return (new FieldRefusal(ConsultedField, "Accident_InFuture"), null);
        if (form.DeclaredOn is { } sent && sent > today) return (new FieldRefusal(DeclaredField, "Accident_InFuture"), null);

        decimal? rate = null;
        if (!string.IsNullOrWhiteSpace(form.IncapacityRate))
        {
            rate = TypedNumber.Parse(form.IncapacityRate);
            if (rate is null or < 0 or > 100) return (new FieldRefusal(RateField, "Accident_IncapacityInvalid"), null);
        }

        var blood = IsBloodExposure(form.Kind);
        return (null, new AccidentRequest
        {
            AccidentId = accidentId,
            EmployeeId = employee,
            Kind = form.Kind,
            OccurredAt = occurred,
            Place = Blank(form.Place),
            Circumstances = form.Circumstances.Trim(),
            Injuries = Blank(form.Injuries),
            Witnesses = Blank(form.Witnesses),
            FirstAidGiven = form.FirstAidGiven,
            Exposure = blood ? form.Exposure : null,
            SourceIdentified = blood && form.SourceIdentified,
            ConsultedAt = form.ConsultedOn is { } day
                ? (day == DateOnly.FromDateTime(occurred) ? occurred : day.ToDateTime(new TimeOnly(9, 0)))
                : null,
            InsurerName = Blank(form.InsurerName),
            InsurerClaimNumber = Blank(form.ClaimNumber),
            DeclaredToInsurerOn = form.DeclaredOn,
            Subrogation = form.Subrogation,
            IncapacityRate = rate,
            Note = Blank(form.Note)
        });
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>A service's refusal of the form beside its box.</summary>
    public static string? FieldOf(string resourceKey) => resourceKey switch
    {
        "Accident_CircumstancesRequired" => CircumstancesField,
        "Accident_IncapacityInvalid" => RateField,
        "Accident_InFuture" or "Accident_NotEmployed" => OccurredField,
        "Accident_ConsultedBeforeAccident" => ConsultedField,
        "Accident_DeclaredBeforeAccident" => DeclaredField,
        "Accident_ExposureRequired" => ExposureField,
        "Accident_EmployeeChanged" => EmployeeField,
        "Accident_StoppageOnAccidentDay" or "Leave_Overlaps" => FromField,
        "Leave_EndBeforeStart" => ToField,
        "Accident_ReturnBeforeAccident" or "Accident_ReturnInFuture" or "Accident_ReturnDuringStoppage"
            or "Accident_CheckBeforeAccident" or "Accident_CheckInFuture" => DateField,
        "Accident_EventTextRequired" => EventTextField,
        _ => null
    };

    // ---- The stoppage, the return, the check -----------------------------------------------------------

    /// <summary>
    /// A stoppage opens the day after the last one approved (or after the accident) for a week; it
    /// starts at the earliest the day after the accident. A closed file reopens as a relapse.
    /// </summary>
    public static (DateOnly Min, DateOnly From, DateOnly To, bool Relapse) StoppageDefaults(AccidentDetail accident)
    {
        ArgumentNullException.ThrowIfNull(accident);
        var day = DateOnly.FromDateTime(accident.Request.OccurredAt);
        var last = accident.Stoppages.Where(s => s.Status == LeaveStatus.Approved).Select(s => (DateOnly?)s.EndDate).Max();
        var start = (last ?? day).AddDays(1);
        return (day.AddDays(1), start, start.AddDays(6), accident.IsClosed);
    }

    /// <summary>The certificate's dates: the end not before the start, the start not on the accident's day.</summary>
    public static FieldRefusal? CheckStoppage(AccidentDetail accident, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(accident);
        if (from <= DateOnly.FromDateTime(accident.Request.OccurredAt)) return new FieldRefusal(FromField, "Accident_StoppageOnAccidentDay");
        if (to < from) return new FieldRefusal(ToField, "Leave_EndBeforeStart");
        return null;
    }

    /// <summary>The return proposed: the day after the last approved stoppage; not before the accident.</summary>
    public static (DateOnly Value, DateOnly Min) ReturnDefaults(AccidentDetail accident)
    {
        ArgumentNullException.ThrowIfNull(accident);
        var min = DateOnly.FromDateTime(accident.Request.OccurredAt);
        var approved = accident.Stoppages.Where(s => s.Status == LeaveStatus.Approved).ToList();
        var value = approved.Count == 0 ? min : approved.Max(s => s.EndDate).AddDays(1);
        return (value < min ? min : value, min);
    }

    /// <summary>The check marked done: the one selected if it is pending, else the first due.</summary>
    public static SerologyCheckRow? CheckToMark(AccidentDetail accident, Guid? selected)
    {
        ArgumentNullException.ThrowIfNull(accident);
        return accident.Checks.FirstOrDefault(c => c.CheckId == selected && c.DoneOn is null)
               ?? accident.Checks.Where(c => c.DoneOn is null).OrderBy(c => c.DueOn).FirstOrDefault();
    }

    /// <summary>A date chosen within its bounds: a date before the first or after the last is refused on its box.</summary>
    public static FieldRefusal? CheckDate(DateOnly value, DateOnly min, DateOnly? max, string beforeKey, string afterKey)
    {
        if (value < min) return new FieldRefusal(DateField, beforeKey);
        if (max is { } last && value > last) return new FieldRefusal(DateField, afterKey);
        return null;
    }

    /// <summary>A reminder can go without a word; an answer or a note says what it is.</summary>
    public static FieldRefusal? CheckLog(AccidentEventKind kind, string? text) =>
        kind != AccidentEventKind.InsurerReminder && string.IsNullOrWhiteSpace(text)
            ? new FieldRefusal(EventTextField, "Accident_EventTextRequired")
            : null;

    /// <summary>A line of the file's history.</summary>
    public static string EventKind(AccidentEventRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text($"AccidentEventKind.{row.Kind}");
    }

    /// <summary>« Clore le dossier — AT-2026-0003 ».</summary>
    public static string CloseTitle(AccidentDetail accident, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(accident);
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Accident.Close")} — {accident.Number}";
    }

    /// <summary>« accident-AT-2026-0003.pdf », « registre-accidents-2026.pdf ».</summary>
    public static string DeclarationFileName(AccidentDetail accident)
    {
        ArgumentNullException.ThrowIfNull(accident);
        return $"declaration-accident-{new string([.. accident.Number.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')])}.pdf";
    }

    public static string RegisterFileName(int year) => $"registre-accidents-{year.ToString(CultureInfo.InvariantCulture)}.pdf";
}
