using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;

namespace MarocLIS.Application.HumanResources;

/// <summary>A departure as typed: the last day worked, the reason and a comment.</summary>
public sealed class ExitForm
{
    public DateOnly Date { get; set; }
    public ExitReason Reason { get; set; } = ExitReason.Resignation;
    public string? Comment { get; set; }
}

/// <summary>
/// The rules of a departure and a re-hire that need no window: the form of a departure recorded or
/// corrected, the account it will switch off said before it is saved, the re-hire's earliest date,
/// and the boxes the service's refusals name. The workstation's EmployeeExitDialog and
/// EmployeeRehireDialog and the web's dialogs apply the same ones; what is done is PersonnelService's.
/// </summary>
public static class DepartureRules
{
    public const string DateField = "exit-date";
    public const string ReasonField = "exit-reason";
    public const string CommentField = "exit-comment";
    public const string RehireDateField = "rehire-date";

    public const int CommentMaxLength = 500;

    /// <summary>« Enregistrer la sortie », or « Corriger la sortie » for one not reached yet.</summary>
    public static string TitleKey(bool correcting) => correcting ? "Personnel.CorrectExit" : "Personnel.RecordExit";

    /// <summary>A new departure: today, a resignation; a correction: the departure as recorded.</summary>
    public static ExitForm FormFor(DateOnly today, DateOnly? exit = null, ExitReason? reason = null, string? comment = null) =>
        new() { Date = exit ?? today, Reason = reason ?? ExitReason.Resignation, Comment = comment };

    /// <summary>The linked LIS account stops with the departure: said, with the account's name, before the exit is saved.</summary>
    public static string? AccountNotice(string? linkedUserName, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return linkedUserName is null ? null : text("Personnel.ExitDeactivatesAccount", linkedUserName);
    }

    public static string? Comment(ExitForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return string.IsNullOrWhiteSpace(form.Comment) ? null : form.Comment.Trim();
    }

    /// <summary>A re-hire starts the day after the departure at the earliest.</summary>
    public static DateOnly EarliestRehire(DateOnly exit) => exit.AddDays(1);

    /// <summary>Today, or the earliest day a re-hire can start when today is before it.</summary>
    public static DateOnly RehireDefault(DateOnly exit, DateOnly today) =>
        today > EarliestRehire(exit) ? today : EarliestRehire(exit);

    /// <summary>A re-hire dated on or before the departure is refused beside its date.</summary>
    public static FieldRefusal? CheckRehire(DateOnly on, DateOnly exit) =>
        on < EarliestRehire(exit)
            ? new FieldRefusal(RehireDateField, "Employee_RehireBeforeExit", PersonnelScreenRules.Date(exit))
            : null;

    /// <summary>The box a refusal of the service names, when it names one.</summary>
    public static string? FieldOf(string errorKey) => errorKey switch
    {
        "Employee_ExitBeforeHire" or "Employee_LeaveAfterExit" => DateField,
        "Employee_RehireBeforeExit" => RehireDateField,
        _ => null
    };

    public static FieldRefusal? RefusalOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        // The leave's clash names three dates: said whole under the dialog, not beside the box.
        if (error.Args.Length > 1) return null;
        return FieldOf(error.ResourceKey) is { } field ? HrRefusals.Of(field, error) : null;
    }
}
