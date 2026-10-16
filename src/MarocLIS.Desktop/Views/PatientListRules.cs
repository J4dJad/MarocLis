using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Queries;

namespace MarocLIS.Desktop.Views;

/// <summary>The colour family of a pill on the patient screen.</summary>
public enum PatientListTone
{
    None = 0,
    Waiting,
    Running,
    Done,
    Warning,
    Critical
}

/// <summary>
/// The rules of the patient screen that need no window, kept free of WinForms so the test project
/// can compile them in.
/// </summary>
public static class PatientListRules
{
    /// <summary>
    /// Whether a term is searched. Below three characters the result set is meaninglessly broad and
    /// each keystroke would be a full-table scan, so the clerk sees nothing rather than everything;
    /// an empty box lists the first page.
    /// </summary>
    public static bool IsSearchable(string term) => term.Length is 0 or >= 3;

    /// <summary>
    /// The text key saying what an incomplete identity lacks, null when it lacks nothing. A
    /// provisional "X-URG" file says so first: its sex and age are estimates, not facts to complete.
    /// </summary>
    public static string? IdentityGapKey(PatientIdentityGap gaps)
    {
        if (gaps.HasFlag(PatientIdentityGap.Provisional)) return "PatientList.GapProvisional";

        var gender = gaps.HasFlag(PatientIdentityGap.Gender);
        var born = gaps.HasFlag(PatientIdentityGap.DateOfBirth);

        return (gender, born) switch
        {
            (true, true) => "PatientList.GapGenderAndDateOfBirth",
            (true, false) => "PatientList.GapGender",
            (false, true) => "PatientList.GapDateOfBirth",
            _ => null
        };
    }

    /// <summary>"né le", "née le", or the neutral "né(e) le" when the sex is not a man's or a woman's.</summary>
    public static string BornOnKey(Gender gender) => gender switch
    {
        Gender.Male => "PatientList.BornOnMale",
        Gender.Female => "PatientList.BornOnFemale",
        _ => "OrderEntry.BornOn"
    };

    /// <summary>
    /// The pill of an order's status: grey while nothing has been drawn, blue while the work is
    /// under way, green once validated or reported, grey again when cancelled.
    /// </summary>
    public static PatientListTone StatusTone(LabOrderStatus status) => status switch
    {
        LabOrderStatus.PartiallyCollected or LabOrderStatus.Received or LabOrderStatus.InProgress
            or LabOrderStatus.PartiallyValidated => PatientListTone.Running,
        LabOrderStatus.Validated or LabOrderStatus.Reported => PatientListTone.Done,
        _ => PatientListTone.Waiting
    };

    /// <summary>The pill of an order's priority; none for a routine order.</summary>
    public static PatientListTone PriorityTone(OrderPriority priority) => priority switch
    {
        OrderPriority.VitalEmergency => PatientListTone.Critical,
        OrderPriority.Urgent => PatientListTone.Warning,
        _ => PatientListTone.None
    };
}
