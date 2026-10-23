namespace MarocLIS.Application.HumanResources;

/// <summary>The rules of the staff list that need no window.</summary>
public static class PersonnelRules
{
    /// <summary>On the staff on <paramref name="today"/>: no departure, or one not reached yet (the notice being served).</summary>
    public static bool OnStaff(EmployeeRow row, DateOnly today) => row.ExitDate is not { } exit || exit >= today;
}
