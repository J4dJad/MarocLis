using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.HumanResources;

/// <summary>
/// What is owed to an employee who leaves, on top of the last month's salary: the leave not taken,
/// the notice not worked, the legal severance pay. The amounts are carried onto the payslip of the
/// month of departure, so the contributions and the tax are computed once, there.
/// <para>
/// Once the employee has signed the receipt (reçu pour solde de tout compte, Code du travail
/// art. 73 to 76) the settlement no longer changes. The employee may still contest it within 60
/// days of signing; a correction is then a new payslip, not an edit of this record.
/// </para>
/// </summary>
public sealed class FinalSettlement : AuditableAggregateRoot, ISiteScoped
{
    private FinalSettlement() { }

    public FinalSettlement(Guid employeeId, Guid siteId, DateOnly exitDate, ExitReason exitReason)
    {
        EmployeeId = employeeId;
        SiteId = siteId;
        ExitDate = exitDate;
        ExitReason = exitReason;
    }

    public Guid EmployeeId { get; private set; }
    public Guid SiteId { get; set; }
    public DateOnly ExitDate { get; private set; }
    public ExitReason ExitReason { get; private set; }

    /// <summary>Monthly salary the indemnities are computed on: the average of the last full months.</summary>
    public decimal ReferenceSalary { get; private set; }

    public decimal LeaveDays { get; private set; }
    public decimal LeaveIndemnity { get; private set; }

    /// <summary>The employer released the employee from working the notice, or dismissed without it.</summary>
    public bool NoticeNotWorked { get; private set; }
    public decimal NoticeIndemnity { get; private set; }

    public decimal SeverancePay { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Date the employee signed the receipt; the settlement is then final.</summary>
    public DateOnly? SignedOn { get; private set; }

    public bool IsSigned => SignedOn is not null;

    public decimal Total => LeaveIndemnity + NoticeIndemnity + SeverancePay;

    public Result Update(SettlementAmounts amounts)
    {
        if (IsSigned) return Result.Failure(Error.Conflict("Settlement_Signed"));

        decimal[] values = [amounts.ReferenceSalary, amounts.LeaveDays, amounts.LeaveIndemnity,
                            amounts.NoticeIndemnity, amounts.SeverancePay];
        if (values.Any(v => v < 0)) return Result.Failure(Error.Validation("Settlement_NegativeAmount"));
        if (!amounts.NoticeNotWorked && amounts.NoticeIndemnity > 0)
            return Result.Failure(Error.Validation("Settlement_NoticeWorked"));

        ReferenceSalary = amounts.ReferenceSalary;
        LeaveDays = amounts.LeaveDays;
        LeaveIndemnity = amounts.LeaveIndemnity;
        NoticeNotWorked = amounts.NoticeNotWorked;
        NoticeIndemnity = amounts.NoticeIndemnity;
        SeverancePay = amounts.SeverancePay;
        Note = string.IsNullOrWhiteSpace(amounts.Note) ? null : amounts.Note.Trim();
        return Result.Success();
    }

    /// <summary>
    /// Empties a settlement whose departure was corrected or withdrawn before taking effect: it no
    /// longer pays anything with the month. Kept, with why, rather than deleted, for the audit.
    /// </summary>
    /// <param name="reason">The corrected reason of a departure kept on the same date.</param>
    public Result Void(string note, ExitReason? reason = null)
    {
        if (IsSigned) return Result.Failure(Error.Conflict("Settlement_Signed"));

        if (reason is { } corrected) ExitReason = corrected;

        LeaveDays = LeaveIndemnity = NoticeIndemnity = SeverancePay = 0m;
        NoticeNotWorked = false;
        Note = note.Length > 500 ? note[..500] : note;
        return Result.Success();
    }

    public Result Sign(DateOnly on)
    {
        if (IsSigned) return Result.Failure(Error.Conflict("Settlement_Signed"));
        if (on < ExitDate) return Result.Failure(Error.Validation("Settlement_SignedBeforeExit"));
        SignedOn = on;
        return Result.Success();
    }
}

public sealed record SettlementAmounts(decimal ReferenceSalary, decimal LeaveDays, decimal LeaveIndemnity,
                                       bool NoticeNotWorked, decimal NoticeIndemnity, decimal SeverancePay,
                                       string? Note);

/// <summary>The two scales of the notice decree: executives, and employees and workers.</summary>
public enum StaffCategory
{
    /// <summary>Cadres et assimilés.</summary>
    Executive = 0,
    /// <summary>Employés et ouvriers.</summary>
    Employee = 1
}

/// <summary>Notice period: whole months, or a number of days for a short service.</summary>
public sealed record NoticePeriod(int Months, int Days);

/// <summary>
/// The departure arithmetic of the Code du travail, apart from any database.
/// <list type="bullet">
/// <item>Severance pay (art. 52 and 53): due on a dismissal after six months of service, except for
/// serious misconduct. Per year of service, 96 hours of salary for the first five years, 144 hours
/// from the sixth to the tenth, 192 hours from the eleventh to the fifteenth and 240 hours beyond;
/// a part year counts in proportion.</item>
/// <item>Notice (décret 2-04-469): executives 1, 2 or 3 months, employees 8 days, 1 or 2 months,
/// for less than one year, one to five years and more than five years of service.</item>
/// <item>Leave not taken is paid at the daily rate, the monthly salary over 26 working days.</item>
/// </list>
/// </summary>
public static class SettlementCalculator
{
    public static StaffCategory CategoryOf(EmployeeJob job) =>
        job is EmployeeJob.Biologist or EmployeeJob.Support ? StaffCategory.Executive : StaffCategory.Employee;

    /// <summary>Years of service, a part year as a fraction.</summary>
    public static decimal ServiceYears(DateOnly hire, DateOnly exit) =>
        exit < hire ? 0m : Math.Round((exit.DayNumber - hire.DayNumber + 1) / 365.25m, 2);

    public static bool SeveranceDue(ExitReason reason, DateOnly hire, DateOnly exit) =>
        reason == ExitReason.Dismissal && exit >= hire.AddMonths(6);

    public static decimal SeveranceHours(decimal years)
    {
        (decimal UpTo, decimal Hours)[] brackets = [(5m, 96m), (10m, 144m), (15m, 192m), (decimal.MaxValue, 240m)];
        decimal hours = 0m, lower = 0m;
        foreach (var (upTo, perYear) in brackets)
        {
            if (years <= lower) break;
            hours += (Math.Min(years, upTo) - lower) * perYear;
            lower = upTo;
        }

        return Math.Round(hours, 2);
    }

    public static decimal Severance(decimal years, decimal monthlySalary, decimal monthlyHours) =>
        monthlyHours <= 0 ? 0m : R(SeveranceHours(years) * monthlySalary / monthlyHours);

    /// <summary>Notice is owed on a dismissal or a resignation, not after misconduct, a trial period or a fixed term.</summary>
    public static bool NoticeApplies(ExitReason reason) => reason is ExitReason.Dismissal or ExitReason.Resignation;

    public static NoticePeriod Notice(StaffCategory category, decimal years) => (category, years) switch
    {
        (StaffCategory.Executive, < 1m) => new(1, 0),
        (StaffCategory.Executive, <= 5m) => new(2, 0),
        (StaffCategory.Executive, _) => new(3, 0),
        (_, < 1m) => new(0, 8),
        (_, <= 5m) => new(1, 0),
        _ => new(2, 0)
    };

    public static decimal NoticeIndemnity(NoticePeriod notice, decimal monthlySalary, decimal monthlyWorkingDays) =>
        R(notice.Months * monthlySalary
          + (monthlyWorkingDays <= 0 ? 0m : notice.Days * monthlySalary / monthlyWorkingDays));

    public static decimal LeaveIndemnity(decimal days, decimal monthlySalary, decimal monthlyWorkingDays) =>
        monthlyWorkingDays <= 0 ? 0m : R(Math.Max(0m, days) * monthlySalary / monthlyWorkingDays);

    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
