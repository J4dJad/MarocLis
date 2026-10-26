using MarocLIS.Application.HumanResources;

namespace MarocLIS.Web.Components.Screens.HumanResources;

/// <summary>
/// The classes of the HR screens' tones, as the workstation's QhTone colours their pills, rows and
/// cells: shared by the personnel, habilitation and deadline screens and the screens of leave,
/// planning, payroll and accidents built on them.
/// </summary>
public static class HrTones
{
    /// <summary>A line of the staff list: a departure taken effect grey, a contract over filled in the alarm's colours.</summary>
    public static string? Row(PersonnelInk ink) => ink switch
    {
        PersonnelInk.Departed => "is-tone-muted",
        PersonnelInk.ContractOver => "is-tone-critical-fill",
        _ => null
    };

    /// <summary>A deadline's date: overdue or missing in the alarm's ink, within a month in the warning's.</summary>
    public static string? Due(DeadlineInk ink) => ink switch
    {
        DeadlineInk.Late => "critical strong",
        DeadlineInk.Soon => "abnormal strong",
        _ => null
    };

    /// <summary>A document of a file: expired, or a medical visit that found the employee unfit, in the alarm's colours.</summary>
    public static string? Credential(bool alarm) => alarm ? "is-tone-critical-fill" : null;

    /// <summary>The class of a matrix cell: the review overdue, in training, or held.</summary>
    public static string? Cell(HabilitationInk ink) => ink switch
    {
        HabilitationInk.ReviewOverdue => "hab-cell-overdue",
        HabilitationInk.InTraining => "hab-cell-training",
        HabilitationInk.Held => "hab-cell-held",
        _ => null
    };

    /// <summary>The action bar's message class for a tone of the rules (normal, abnormal, critical, muted).</summary>
    public static string Status(string? tone) => $"is-{tone ?? "muted"}";
}
