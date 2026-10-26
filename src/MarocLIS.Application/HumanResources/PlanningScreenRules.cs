using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>
/// How a day of the planning reads: outside the contract, a Sunday or a holiday shaded; a leave in
/// the accent's colours (bold once approved); a post in the normal ink, a night, a Sunday or an
/// on-call in the warning's, one the checks flag in the alarm's.
/// </summary>
public enum PlanningInk
{
    Empty = 0,
    NotEmployed = 1,
    LeaveApproved = 2,
    LeavePending = 3,
    Shift = 4,
    ShiftUnusual = 5,
    ShiftFlagged = 6
}

/// <summary>One person's day of the month: the posts held, the leave and the holiday that fall on it.</summary>
public sealed record PlanningDay(
    PlanningEmployee Employee,
    DateOnly Date,
    IReadOnlyList<ShiftCell> Shifts,
    LeaveSpan? Leave,
    HolidayRow? Holiday,
    bool Employed,
    bool Flagged);

/// <summary>One line of the grid: the person, the hours of the month and the month's days.</summary>
public sealed record PlanningLine(PlanningEmployee Employee, string Name, string Hours, IReadOnlyList<PlanningDay> Days);

/// <summary>What the planning offers on the days selected.</summary>
public sealed record PlanningActions(bool Assign, bool Clear, bool CopyWeek);

/// <summary>
/// The rules of « Planning » that need no window: the month's grid (one line per person, one
/// column per day, the leave and the holidays where they fall), how a day reads, the points to
/// check in words, the selection and what it allows, the week to copy, and the post's form. The
/// workstation's PlanningView and ShiftDialog and the web's screen apply the same ones; the checks
/// themselves are PlanningChecks', what is done PlanningService's.
/// </summary>
public static class PlanningScreenRules
{
    /// <summary>A post's note.</summary>
    public const int NoteMaxLength = 300;

    /// <summary>The first of the month that holds <paramref name="day"/>.</summary>
    public static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>« Octobre 2026 », in the language's month names.</summary>
    public static string MonthLabel(DateOnly month, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TextInfo.ToTitleCase(month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", culture));
    }

    /// <summary>The month's days, the grid's columns.</summary>
    public static IReadOnlyList<DateOnly> Dates(int year, int month) =>
        [.. Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Select(d => new DateOnly(year, month, d))];

    /// <summary>The column's head: the day's short name over its number.</summary>
    public static (string Day, string Number) DayHeader(DateOnly date, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text($"WeekdayShort.{date.DayOfWeek}"), date.Day.ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>A Sunday or a public holiday: the column is shaded.</summary>
    public static bool IsDayOff(DateOnly date, IReadOnlyList<HolidayRow> holidays)
    {
        ArgumentNullException.ThrowIfNull(holidays);
        return date.DayOfWeek == DayOfWeek.Sunday || holidays.Any(h => h.Date == date);
    }

    /// <summary>The grid: one line per person, the month's hours and each day's posts, leave and holiday.</summary>
    public static IReadOnlyList<PlanningLine> Lines(PlanningMonth data, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(text);
        var dates = Dates(data.Year, data.Month);

        return [.. data.Employees.Select(employee =>
        {
            var mine = data.Shifts.Where(s => s.EmployeeId == employee.EmployeeId).ToList();
            var days = dates.Select(date => new PlanningDay(
                employee, date,
                [.. mine.Where(s => s.Date == date)],
                data.Leaves.FirstOrDefault(l => l.EmployeeId == employee.EmployeeId && l.Covers(date)),
                data.Holidays.FirstOrDefault(h => h.Date == date),
                date >= employee.HireDate && (employee.ExitDate is null || date <= employee.ExitDate),
                data.Issues.Any(i => i.EmployeeId == employee.EmployeeId && i.Date == date))).ToList();

            return new PlanningLine(employee, $"{employee.FullName} · {text($"EmployeeJobShort.{employee.Job}")}",
                                    Hours(mine.Sum(s => s.Hours), culture), days);
        })];
    }

    /// <summary>Hours: « 38,5 ».</summary>
    public static string Hours(decimal hours, IFormatProvider culture) => hours.ToString("0.#", culture);

    /// <summary>What a day shows: its posts' letters, else its leave's, else nothing.</summary>
    public static string Code(PlanningDay day, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(text);
        return day.Shifts.Count > 0 ? string.Join("+", day.Shifts.Select(s => text($"ShiftKindShort.{s.Kind}")))
             : day.Leave is { } leave ? text($"LeaveTypeShort.{leave.Type}")
             : string.Empty;
    }

    /// <summary>How the day reads (see <see cref="PlanningInk"/>).</summary>
    public static PlanningInk Ink(PlanningDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (!day.Employed) return PlanningInk.NotEmployed;
        if (day.Leave is { } leave && day.Shifts.Count == 0)
            return leave.Status == LeaveStatus.Approved ? PlanningInk.LeaveApproved : PlanningInk.LeavePending;
        if (day.Shifts.Count == 0) return PlanningInk.Empty;
        if (day.Flagged) return PlanningInk.ShiftFlagged;
        return day.Shifts.Any(s => s.Kind is ShiftKind.Night or ShiftKind.SundayHoliday or ShiftKind.OnCall)
            ? PlanningInk.ShiftUnusual
            : PlanningInk.Shift;
    }

    /// <summary>A Sunday, a holiday or a day outside the contract: shaded behind its ink.</summary>
    public static bool Shaded(PlanningDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return !day.Employed || day.Holiday is not null || day.Date.DayOfWeek == DayOfWeek.Sunday;
    }

    /// <summary>A post in words: « Garde de nuit 20:00-08:00 », the on-call without hours.</summary>
    public static string ShiftText(ShiftCell shift, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(text);
        return shift.Kind == ShiftKind.OnCall
            ? text("ShiftKind.OnCall")
            : $"{text($"ShiftKind.{shift.Kind}")} {Time(shift.Start)}-{Time(shift.End)}";
    }

    /// <summary>An hour of a post, « 08:00 ».</summary>
    public static string Time(TimeOnly time) => time.ToString("HH\\:mm", CultureInfo.InvariantCulture);

    /// <summary>A point to check, in words.</summary>
    public static string IssueText(PlanningIssue issue, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(text);
        var week = PlanningChecks.WeekStart(issue.Date).ToString("dd/MM", CultureInfo.InvariantCulture);
        return issue.Kind switch
        {
            PlanningIssueKind.RestTooShort => text("PlanningIssue.RestTooShort", Hours(issue.Value ?? 0m, culture)),
            PlanningIssueKind.WeeklyHoursExceeded => text("PlanningIssue.WeeklyHoursExceeded", Hours(issue.Value ?? 0m, culture), week),
            PlanningIssueKind.NoWeeklyRest => text("PlanningIssue.NoWeeklyRest", week),
            _ => text($"PlanningIssue.{issue.Kind}")
        };
    }

    /// <summary>A day without a biologist, a post on approved leave, a trainee alone: in the alarm's ink; the others in the warning's.</summary>
    public static bool IsCritical(PlanningIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Kind is PlanningIssueKind.NoBiologist or PlanningIssueKind.ShiftDuringLeave or PlanningIssueKind.TraineeAlone;
    }

    /// <summary>The head's count: the people, the posts and the points to check.</summary>
    public static string Count(PlanningMonth data, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(text);
        return text("QualityHr.Planning.Count", data.Employees.Count, data.Shifts.Count, data.Issues.Count);
    }

    /// <summary>The day the grid opens on: today in this month, else the first.</summary>
    public static DateOnly InitialDay(int year, int month, DateOnly today) =>
        today.Year == year && today.Month == month ? today : new DateOnly(year, month, 1);

    /// <summary>The days selected, by person then by date, as the actions take them.</summary>
    public static IReadOnlyList<PlanningDay> Ordered(IEnumerable<PlanningDay> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return [.. selected.OrderBy(d => d.Employee.FullName, StringComparer.CurrentCulture).ThenBy(d => d.Date)];
    }

    /// <summary>The selection's actions, for whoever may change the planning.</summary>
    public static PlanningActions Actions(IReadOnlyList<PlanningDay> selected, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(has);
        var may = has(Permissions.HumanResources.ManagePlanning);
        return new PlanningActions(may && selected.Count > 0, may && selected.Any(d => d.Shifts.Count > 0), may && selected.Count > 0);
    }

    /// <summary>
    /// The selection under the grid: one day in full (the person, the day, its posts with their
    /// notes, its leave and its holiday), several counted, none the hint.
    /// </summary>
    public static string Detail(IReadOnlyList<PlanningDay> selected, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(text);
        if (selected.Count != 1)
            return selected.Count > 1 ? text("Planning.CellsSelected", selected.Count) : text("Planning.Hint");

        var day = selected[0];
        var parts = new List<string> { $"{day.Employee.FullName} — {text($"Weekday.{day.Date.DayOfWeek}")} {LeaveScreenRules.Date(day.Date)}" };
        parts.AddRange(day.Shifts.Select(s => s.Note is null ? ShiftText(s, text) : $"{ShiftText(s, text)} ({s.Note})"));
        if (day.Leave is { } leave) parts.Add($"{text($"LeaveType.{leave.Type}")} ({text($"LeaveStatus.{leave.Status}")})");
        if (day.Holiday is { } holiday) parts.Add(holiday.Name);
        return string.Join(" · ", parts);
    }

    /// <summary>The days a post is assigned to: those within the person's contract.</summary>
    public static IReadOnlyList<PlanningDay> Assignable(IReadOnlyList<PlanningDay> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return [.. Ordered(selected).Where(d => d.Employed)];
    }

    /// <summary>The post form's subtitle: the day first, then the person; or the number of days.</summary>
    public static string AssignSubtitle(IReadOnlyList<PlanningDay> days, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(text);
        return days.Count == 1
            ? $"{LeaveScreenRules.Date(days[0].Date)} — {days[0].Employee.FullName}"
            : text("Planning.CellsSelected", days.Count);
    }

    /// <summary>The post the form opens on: the one already on the first day, if any.</summary>
    public static ShiftCell? Current(IReadOnlyList<PlanningDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        return days.Count > 0 && days[0].Shifts.Count > 0 ? days[0].Shifts[0] : null;
    }

    /// <summary>One day refused: « EL AMRANI Salma 12/10 : the reason ».</summary>
    public static string RefusedLine(PlanningDay day, string reason)
    {
        ArgumentNullException.ThrowIfNull(day);
        return $"{day.Employee.FullName} {day.Date.ToString("dd/MM", CultureInfo.InvariantCulture)} : {reason}";
    }

    /// <summary>The days refused, twelve at most named.</summary>
    public static string SomeRefused(IReadOnlyList<string> refused, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(text);
        return text("Planning.SomeRefused", refused.Count) + "\n\n" + string.Join("\n", refused.Take(12));
    }

    /// <summary>The posts the selection holds, to be removed.</summary>
    public static IReadOnlyList<ShiftCell> Removable(IReadOnlyList<PlanningDay> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return [.. Ordered(selected).SelectMany(d => d.Shifts)];
    }

    /// <summary>The week copied onto the selected day's: the previous one, Monday to Sunday.</summary>
    public static (DateOnly Source, DateOnly Target) CopyWeek(DateOnly day)
    {
        var target = PlanningChecks.WeekStart(day);
        return (target.AddDays(-7), target);
    }

    public static string CopyWeekPrompt(DateOnly day, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (source, target) = CopyWeek(day);
        return text("Planning.CopyWeekPrompt", source.ToString("dd/MM", CultureInfo.InvariantCulture),
                    target.ToString("dd/MM", CultureInfo.InvariantCulture));
    }

    // ---- The post ------------------------------------------------------------------------------------

    /// <summary>The kind's usual hours, or the post's own when it has some.</summary>
    public static (TimeOnly Start, TimeOnly End) HoursOf(ShiftKind kind, ShiftCell? current) =>
        current is { } c && c.Kind == kind ? (c.Start, c.End) : ShiftRules.DefaultHours(kind);

    /// <summary>The on-call is held at home: it has no hours to type.</summary>
    public static bool TimesEditable(ShiftKind kind) => kind != ShiftKind.OnCall;

    /// <summary>« 12 h de travail ».</summary>
    public static string HoursCounted(ShiftKind kind, TimeOnly start, TimeOnly end, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Planning.HoursCounted", Hours(ShiftRules.Hours(kind, start, end), culture));
    }

    /// <summary>The post asked for one day, the note trimmed.</summary>
    public static ShiftRequest Request(PlanningDay day, ShiftKind kind, TimeOnly start, TimeOnly end, string? note)
    {
        ArgumentNullException.ThrowIfNull(day);
        return new ShiftRequest
        {
            EmployeeId = day.Employee.EmployeeId, Date = day.Date, Kind = kind, Start = start, End = end,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }
}
