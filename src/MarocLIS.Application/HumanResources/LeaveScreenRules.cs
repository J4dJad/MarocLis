using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>How a line of the leave list reads: a request waiting in bold, a refused or cancelled one muted.</summary>
public enum LeaveInk
{
    Plain = 0,
    Pending = 1,
    Closed = 2
}

/// <summary>
/// What « Congés et absences » offers, by right and by request: a new request, the decisions on the
/// selected one (approve, refuse), the certificate received, its cancellation, the balances' correction
/// and the holiday calendar's.
/// </summary>
public sealed record LeaveActions(
    bool New,
    bool Approve,
    bool Refuse,
    bool ProofReceived,
    bool Cancel,
    bool Adjust,
    bool ManageHolidays);

/// <summary>What the request form shows as the dates change: the days counted, and the balance or the law's length.</summary>
/// <param name="Count">« 3 jours ouvrables décomptés », « 1 jour ouvrable décompté ».</param>
/// <param name="Balance">The balance left, or the length the law gives the kind; empty when neither applies.</param>
/// <param name="Over">More days asked than the balance holds: said in the alarm's ink.</param>
public sealed record LeavePreview(string Count, string Balance, bool Over);

/// <summary>
/// The rules of « Congés et absences » that need no window: how a request reads in the list and
/// under it, what may be done on it by right and by state, the questions asked before a decision,
/// the request form (the kinds offered, the days counted, the balance), the balances and their
/// correction, and the holiday calendar. The workstation's LeaveView and its dialogs and the web's
/// screen apply the same ones; the counting itself is LeaveRules', what is done LeaveService's.
/// </summary>
public static class LeaveScreenRules
{
    /// <summary>The reason given with a request, a refusal or a cancellation: the column takes 500 characters.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>A holiday's name.</summary>
    public const int HolidayNameMaxLength = 150;

    public const string AdjustDaysField = "leave-adjust-days";
    public const string AdjustReasonField = "leave-adjust-reason";
    public const string HolidayNameField = "holiday-name";
    public const string HolidayDateField = "holiday-date";
    public const string StartField = "leave-start";
    public const string EndField = "leave-end";

    /// <summary>The years offered: two back, this one, the next.</summary>
    public static IReadOnlyList<int> Years(DateOnly today) => [.. Enumerable.Range(today.Year - 2, 4)];

    /// <summary>A date of these screens, as the workstation writes it whatever the language.</summary>
    public static string Date(DateOnly date) => PersonnelScreenRules.Date(date);

    /// <summary>A number of days: « 2,5 » with one decimal at most.</summary>
    public static string Days(decimal days, IFormatProvider culture) => days.ToString("0.#", culture);

    /// <summary>A balance's days, to the hundredth the monthly accrual leaves.</summary>
    public static string Balance(decimal days, IFormatProvider culture) => days.ToString("0.##", culture);

    /// <summary>The status as a pill: waiting in amber, approved in green, refused or cancelled muted.</summary>
    public static string Tone(LeaveStatus status) => status switch
    {
        LeaveStatus.Pending => "Warning",
        LeaveStatus.Approved => "Ok",
        _ => "Muted"
    };

    public static LeaveInk Ink(LeaveRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Status switch
        {
            LeaveStatus.Refused or LeaveStatus.Cancelled => LeaveInk.Closed,
            LeaveStatus.Pending => LeaveInk.Pending,
            _ => LeaveInk.Plain
        };
    }

    /// <summary>A certificate the kind needs, not handed in, on a request still waiting: in the alarm's ink.</summary>
    public static bool ProofMissing(LeaveRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Status == LeaveStatus.Pending && !row.JustificationProvided && LeaveRules.RequiresJustification(row.Type);
    }

    /// <summary>« Fourni » or « Manquant » for a kind that needs a certificate; empty for the others.</summary>
    public static string Proof(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return !LeaveRules.RequiresJustification(row.Type) ? string.Empty
             : row.JustificationProvided ? text("Leave.ProofGiven") : text("Leave.ProofMissing");
    }

    /// <summary>When it was asked for, while it waits; who decided and what they said, once decided.</summary>
    public static string Decision(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.Status == LeaveStatus.Pending
            ? text("Leave.SubmittedOn", row.SubmittedAt.LocalDateTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
            : string.Join(" · ", new[] { row.DecidedBy, row.DecisionComment }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>The first day, « (ap.-midi) » after it when the leave starts in the afternoon.</summary>
    public static string From(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return Half(row.StartDate, row.StartsAfternoon, text("Leave.AfternoonShort"));
    }

    /// <summary>The last day, « (matin) » after it when the leave ends in the morning.</summary>
    public static string To(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return Half(row.EndDate, row.EndsMorning, text("Leave.MorningShort"));
    }

    private static string Half(DateOnly date, bool half, string halfText) => half ? $"{Date(date)} {halfText}" : Date(date);

    /// <summary>The colleagues of the department away on some of the same days, a dash when none is.</summary>
    public static string Away(LeaveRow row, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ColleaguesAway == 0 ? "—" : row.ColleaguesAway.ToString(culture);
    }

    /// <summary>The head's count: the requests waiting, then those approved.</summary>
    public static string Count(IReadOnlyCollection<LeaveRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("QualityHr.Leave.Count", rows.Count(r => r.Status == LeaveStatus.Pending),
                    rows.Count(r => r.Status == LeaveStatus.Approved));
    }

    /// <summary>The colleagues away, as said under the list and in the approval question; null when nobody is.</summary>
    public static string? AwayWarning(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.ColleaguesAway > 0 ? text("Leave.AwayWarning", row.ColleaguesAway, row.DepartmentName) : null;
    }

    /// <summary>The selected request under the list: its kind, the days counted, its reason, the colleagues away.</summary>
    public static string Detail(LeaveRow? row, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (row is null) return string.Empty;
        return string.Join(" · ", new[]
        {
            text($"LeaveType.{row.Type}"),
            text("Leave.DaysCounted", Days(row.Days, culture)),
            row.Reason,
            AwayWarning(row, text)
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>
    /// The actions on the request selected: deciding is the approval right's, on a request still
    /// waiting; the certificate received is HR's, on a waiting request whose kind needs one; a live
    /// request can be cancelled while it waits or until its first day.
    /// </summary>
    public static LeaveActions Actions(LeaveRow? row, DateOnly today, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.HumanResources.Manage);
        var request = has(Permissions.HumanResources.RequestLeave) || manage;
        var approve = has(Permissions.HumanResources.ApproveLeave);
        var pending = row?.Status == LeaveStatus.Pending;

        return new LeaveActions(
            New: request,
            Approve: approve && pending,
            Refuse: approve && pending,
            ProofReceived: manage && row is not null && pending && !row.JustificationProvided
                           && LeaveRules.RequiresJustification(row.Type),
            Cancel: request && row is { IsLive: true } && (row.Status == LeaveStatus.Pending || row.StartDate > today),
            Adjust: manage,
            ManageHolidays: manage);
    }

    /// <summary>« Valider Congé annuel payé de … du … au … (3 j) ? », with the colleagues away after it.</summary>
    public static string ApprovePrompt(LeaveRow row, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var question = text("Leave.ApprovePrompt", text($"LeaveType.{row.Type}"), row.EmployeeName,
                            Date(row.StartDate), Date(row.EndDate), Days(row.Days, culture));
        return AwayWarning(row, text) is { } away ? question + "\n\n" + away : question;
    }

    public static string ProofReceivedPrompt(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Leave.ProofReceivedPrompt", text($"LeaveType.{row.Type}"), row.EmployeeName,
                    Date(row.StartDate), Date(row.EndDate));
    }

    public static string RefusePrompt(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Leave.RefusePrompt", row.EmployeeName);
    }

    public static string CancelPrompt(LeaveRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Leave.CancelPrompt", text($"LeaveType.{row.Type}"), Date(row.StartDate));
    }

    // ---- The request ---------------------------------------------------------------------------------

    /// <summary>Whom a request can be made for: anyone for HR and the approvers, oneself for the others.</summary>
    public static IReadOnlyList<LeaveBalanceRow> People(IReadOnlyList<LeaveBalanceRow> balances, bool actsForOthers, Guid? own)
    {
        ArgumentNullException.ThrowIfNull(balances);
        return actsForOthers ? balances : [.. balances.Where(b => b.EmployeeId == own)];
    }

    /// <summary>Why nobody can be asked for: no file at all for HR, an account linked to no file for the others.</summary>
    public static string NobodyKey(bool actsForOthers) => actsForOthers ? "Leave_NoEmployees" : "Leave_NoPersonnelFile";

    /// <summary>The person a new request opens on: oneself when one has a file among them, else the first.</summary>
    public static Guid DefaultPerson(IReadOnlyList<LeaveBalanceRow> people, Guid? own)
    {
        ArgumentNullException.ThrowIfNull(people);
        return own is { } id && people.Any(p => p.EmployeeId == id) ? id : people[0].EmployeeId;
    }

    /// <summary>The kinds offered: an unjustified absence is HR's to record.</summary>
    public static IReadOnlyList<LeaveType> Types(bool hrTypes) =>
        [.. Enum.GetValues<LeaveType>().Where(t => hrTypes || !LeaveRules.IsHrOnly(t))];

    /// <summary>A new request opens on tomorrow, for one day.</summary>
    public static DateOnly DefaultDay(DateOnly today) => today.AddDays(1);

    /// <summary>The first day moved past the last: the last follows it.</summary>
    public static DateOnly EndAfter(DateOnly start, DateOnly end) => end < start ? start : end;

    /// <summary>The days counted as they will be, and the balance or the law's length beside them.</summary>
    public static LeavePreview Preview(LeaveType type, decimal days, LeaveBalanceRow? person, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = text(LeaveRules.CountsCalendarDays(type) ? "Leave.CalendarDays" : "Leave.WorkingDays", Days(days, culture), days);

        if (LeaveRules.DebitsBalance(type) && person is not null)
            return new LeavePreview(count, text("Leave.AvailableBalance", Days(person.Available, culture)), days > person.Available);

        return new LeavePreview(count,
                                LeaveRules.Entitlement(type) is { } allowed ? text("Leave.Entitlement", Days(allowed, culture)) : string.Empty,
                                false);
    }

    /// <summary>The form sent: the reason trimmed, nothing when blank.</summary>
    public static LeaveSubmission Submission(Guid? employeeId, LeaveType type, DateOnly start, DateOnly end, bool startsAfternoon,
                                             bool endsMorning, string? reason, bool proof) =>
        new()
        {
            EmployeeId = employeeId,
            Type = type,
            StartDate = start,
            EndDate = end,
            StartsAfternoon = startsAfternoon,
            EndsMorning = endsMorning,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            JustificationProvided = proof
        };

    /// <summary>A service's refusal of the request beside the box it concerns: the dates, else under the form.</summary>
    public static string? FieldOf(string resourceKey) => resourceKey switch
    {
        "Leave_EndBeforeStart" => EndField,
        "Leave_NoWorkingDay" or "Leave_Overlaps" or "Leave_EmployeeNotEmployed" => StartField,
        "Leave_AdjustmentInvalid" => AdjustDaysField,
        "Leave_AdjustmentReasonRequired" => AdjustReasonField,
        "Holiday_NameRequired" => HolidayNameField,
        "Holiday_AlreadyExists" => HolidayDateField,
        _ => null
    };

    // ---- The balances --------------------------------------------------------------------------------

    /// <summary>More asked for than acquired: the available days in the alarm's ink.</summary>
    public static bool Overdrawn(LeaveBalanceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Available < 0;
    }

    /// <summary>« M0002 AÏT OUAHMANE Youssef »: whose balance is corrected.</summary>
    public static string Who(LeaveBalanceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.Number} {row.FullName}";
    }

    /// <summary>
    /// The correction typed: a number of days other than zero, either sign; the reason is the
    /// service's to require. A refusal names its box.
    /// </summary>
    public static (FieldRefusal? Refusal, decimal Days) CheckAdjustment(string? days)
    {
        if (TypedNumber.Parse(days) is not { } value || value == 0m)
            return (new FieldRefusal(AdjustDaysField, "Leave_AdjustmentInvalid"), 0m);
        return (null, value);
    }

    // ---- The holidays --------------------------------------------------------------------------------

    /// <summary>« Jours fériés 2026 ».</summary>
    public static string HolidaysTitle(int year, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Leave.Holidays")} {year.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string Weekday(HolidayRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text($"Weekday.{row.Date.DayOfWeek}");
    }

    public static string Kind(HolidayRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text(row.IsReligious ? "Holiday.Religious" : "Holiday.Civil");
    }

    /// <summary>A holiday is entered within its year: today when the year is this one, else its 1st January.</summary>
    public static (DateOnly First, DateOnly Last, DateOnly Initial) HolidayDates(int year, DateOnly today) =>
        (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), today.Year == year ? today : new DateOnly(year, 1, 1));

    /// <summary>The holiday typed: a name, and a date within its year; a refusal names its box.</summary>
    public static FieldRefusal? CheckHoliday(int year, DateOnly date, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new FieldRefusal(HolidayNameField, "Holiday_NameRequired");
        if (date.Year != year) return new FieldRefusal(HolidayDateField, "Holiday_OutsideYear", year.ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>
    /// The leaves a new holiday fell in, recounted: named, so HR knows whose balance moved; null
    /// when it fell in none.
    /// </summary>
    public static string? Recounted(IReadOnlyList<HolidayRecount> recounted, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(recounted);
        ArgumentNullException.ThrowIfNull(text);
        if (recounted.Count == 0) return null;
        return text("Holiday.Recounted") + "\n" + string.Join("\n", recounted.Select(r =>
            text("Holiday.RecountedLine", r.EmployeeNumber, r.EmployeeName, Date(r.StartDate), Date(r.EndDate),
                 text($"LeaveStatus.{r.Status}"), Days(r.DaysBefore, culture), Days(r.DaysAfter, culture))));
    }

    public static string RemoveHolidayPrompt(HolidayRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text("Holiday.RemovePrompt", row.Name, Date(row.Date));
    }
}
