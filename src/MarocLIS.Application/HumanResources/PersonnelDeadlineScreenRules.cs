using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

/// <summary>How a deadline's date reads: overdue or missing in the alarm's ink, within a month in the warning's.</summary>
public enum DeadlineInk
{
    Plain = 0,
    Soon = 1,
    Late = 2
}

/// <summary>
/// The rules of « Échéances RH » that need no window: the horizons offered, a deadline's state and
/// tone, what it is about, the count, and the two ways into the file. The workstation's
/// PersonnelDeadlinesView and the web's screen apply the same ones; the deadlines themselves are
/// <see cref="PersonnelDeadlines"/>'.
/// </summary>
public static class PersonnelDeadlineScreenRules
{
    /// <summary>The horizons offered, in days.</summary>
    public static readonly IReadOnlyList<int> Horizons = [30, 60, 90, 180];

    /// <summary>The horizon the screen opens on: two months.</summary>
    public const int DefaultHorizon = 60;

    /// <summary>A deadline within a month is said in the warning's colour.</summary>
    public const int SoonDays = 30;

    /// <summary>A horizon offered, the default one when another is asked for.</summary>
    public static int Horizon(int days) => Horizons.Contains(days) ? days : DefaultHorizon;

    public static string HorizonCaption(int days, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Deadline.Horizon", days);
    }

    /// <summary>Overdue or missing leads; then due within a month; else further away.</summary>
    public static DeadlineInk Ink(DeadlineRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsOverdue || row.DueOn is null ? DeadlineInk.Late
            : row.DueOn.Value <= today.AddDays(SoonDays) ? DeadlineInk.Soon
            : DeadlineInk.Plain;
    }

    /// <summary>The state's pill tone: Critical, Warning or Info.</summary>
    public static string Tone(DeadlineRow row, DateOnly today) => Ink(row, today) switch
    {
        DeadlineInk.Late => "Critical",
        DeadlineInk.Soon => "Warning",
        _ => "Info"
    };

    /// <summary>« Manquant », « En retard », or « Dans 12 jours ».</summary>
    public static string State(DeadlineRow row, DateOnly today, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.DueOn is null ? text("Deadline.Missing")
            : row.IsOverdue ? text("Deadline.Overdue")
            : text("Deadline.InDaysFull", row.DueOn.Value.DayNumber - today.DayNumber);
    }

    public static string Due(DeadlineRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return PersonnelScreenRules.DateOrDash(row.DueOn);
    }

    public static string What(DeadlineRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return text($"DeadlineKind.{row.Kind}");
    }

    /// <summary>The detail: the habilitation's activity and its subject, or the document, the accident.</summary>
    public static string Subject(DeadlineRow row, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        return row.Kind == DeadlineKind.HabilitationReview && row.Activity is { } activity
            ? text($"HabilitationActivity.{activity}") + (row.Subject is null ? string.Empty : $" — {row.Subject}")
            : row.Subject ?? string.Empty;
    }

    /// <summary>The head's count: the points to deal with, those overdue or missing.</summary>
    public static string Count(IReadOnlyCollection<DeadlineRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return text("QualityHr.Deadlines.Count", rows.Count, rows.Count(r => r.IsOverdue));
    }

    /// <summary>The documents are the usual answer, for whoever may read files; the file itself for whoever may edit it.</summary>
    public static (bool Documents, bool Edit) Actions(DeadlineRow? row, Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return (row is not null && has(Permissions.HumanResources.View),
                row is not null && has(Permissions.HumanResources.Manage));
    }

    /// <summary>The line kept selected across a reload: the same person, the same kind of point, the same subject.</summary>
    public static (Guid, DeadlineKind, Domain.HumanResources.HabilitationActivity?, string?) KeyOf(DeadlineRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return (row.EmployeeId, row.Kind, row.Activity, row.Subject);
    }
}
