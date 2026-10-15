using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.HumanResources;

/// <summary>
/// The legal rates of one year: CNSS, AMO, professional-expense allowance, income-tax scale, family
/// deduction, SMIG. They change with each finance law and CNSS decree, so they are data, entered
/// once a year by whoever holds the salary right, not code.
/// </summary>
public sealed class PayrollParameters : AuditableAggregateRoot
{
    private PayrollParameters() { }

    public PayrollParameters(int year) => Year = year;

    public int Year { get; private set; }

    // ---- CNSS, employee and employer -----------------------------------------------------------

    /// <summary>Monthly ceiling of the social-benefits contribution (prestations sociales).</summary>
    public decimal CnssCeiling { get; set; } = 6000m;
    public decimal CnssEmployeeRate { get; set; } = 4.48m;
    public decimal CnssEmployerRate { get; set; } = 8.98m;
    public decimal FamilyAllowanceRate { get; set; } = 6.40m;
    public decimal TrainingTaxRate { get; set; } = 1.60m;
    public decimal AmoEmployeeRate { get; set; } = 2.26m;

    /// <summary>2.26 % plus the 1.85 % solidarity participation.</summary>
    public decimal AmoEmployerRate { get; set; } = 4.11m;

    // ---- Income tax ----------------------------------------------------------------------------

    /// <summary>Professional-expense allowance below the threshold, in percent.</summary>
    public decimal ProfessionalExpensesLowRate { get; set; } = 35m;
    public decimal ProfessionalExpensesHighRate { get; set; } = 25m;

    /// <summary>Annual gross taxable income up to which the low rate applies.</summary>
    public decimal ProfessionalExpensesThreshold { get; set; } = 78000m;
    public decimal ProfessionalExpensesAnnualCap { get; set; } = 35000m;

    /// <summary>
    /// Annual scale as "upper bound:rate" pairs separated by "|", the last one without a bound:
    /// "40000:0|60000:10|80000:20|100000:30|180000:34|:37" (loi de finances 2025).
    /// </summary>
    public string IncomeTaxScale { get; set; } = DefaultScale;

    public const string DefaultScale = "40000:0|60000:10|80000:20|100000:30|180000:34|:37";

    /// <summary>Annual deduction per dependent person, and the most persons counted.</summary>
    public decimal FamilyDeductionPerPerson { get; set; } = 500m;
    public int FamilyDeductionMaxPersons { get; set; } = 6;

    // ---- Pay rules -----------------------------------------------------------------------------

    /// <summary>Statutory minimum wage for 191 hours, to warn on a salary below it.</summary>
    public decimal SmigMonthly { get; set; } = 3422.72m;

    /// <summary>Monthly hours used for the hourly rate (44 h × 52 / 12).</summary>
    public decimal MonthlyHours { get; set; } = 191m;

    /// <summary>Working days in a month, for an absence deduction.</summary>
    public decimal MonthlyWorkingDays { get; set; } = 26m;

    /// <summary>Transport allowance exempt from tax and CNSS in an urban area, per month.</summary>
    public decimal TransportExemptionCap { get; set; } = 500m;

    public IReadOnlyList<(decimal? UpTo, decimal Rate)> ParseScale() => ParseScale(IncomeTaxScale);

    public static IReadOnlyList<(decimal? UpTo, decimal Rate)> ParseScale(string scale)
    {
        var brackets = new List<(decimal?, decimal)>();
        foreach (var part in scale.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':');
            if (pieces.Length != 2
                || !decimal.TryParse(pieces[1], System.Globalization.NumberStyles.Number,
                                     System.Globalization.CultureInfo.InvariantCulture, out var rate))
                return [];

            decimal? upTo = null;
            if (pieces[0].Trim().Length > 0)
            {
                if (!decimal.TryParse(pieces[0], System.Globalization.NumberStyles.Number,
                                      System.Globalization.CultureInfo.InvariantCulture, out var bound))
                    return [];
                upTo = bound;
            }

            brackets.Add((upTo, rate));
        }

        // Rising bounds, the last one open.
        for (var i = 0; i < brackets.Count; i++)
        {
            var (upTo, _) = brackets[i];
            if ((upTo is null) != (i == brackets.Count - 1)) return [];
            if (i > 0 && upTo is { } b && b <= brackets[i - 1].Item1) return [];
        }

        return brackets;
    }

    public Result Validate()
    {
        if (ParseScale().Count == 0) return Result.Failure(Error.Validation("Payroll_ScaleInvalid"));

        decimal[] rates = [CnssEmployeeRate, CnssEmployerRate, FamilyAllowanceRate, TrainingTaxRate, AmoEmployeeRate,
                           AmoEmployerRate, ProfessionalExpensesLowRate, ProfessionalExpensesHighRate];
        if (rates.Any(r => r is < 0 or > 50)) return Result.Failure(Error.Validation("Payroll_RateInvalid"));

        if (CnssCeiling <= 0 || MonthlyHours <= 0 || MonthlyWorkingDays <= 0 || FamilyDeductionMaxPersons < 0)
            return Result.Failure(Error.Validation("Payroll_RateInvalid"));

        return Result.Success();
    }

    // ---- Snapshot ------------------------------------------------------------------------------

    /// <summary>
    /// The rates as a JSON text, kept with a closed month so that a payslip printed again later
    /// shows the rates it was computed with, not the year's rates as since corrected.
    /// </summary>
    public string ToSnapshot() => System.Text.Json.JsonSerializer.Serialize(new Snapshot(
        CnssCeiling, CnssEmployeeRate, CnssEmployerRate, FamilyAllowanceRate, TrainingTaxRate, AmoEmployeeRate,
        AmoEmployerRate, ProfessionalExpensesLowRate, ProfessionalExpensesHighRate, ProfessionalExpensesThreshold,
        ProfessionalExpensesAnnualCap, IncomeTaxScale, FamilyDeductionPerPerson, FamilyDeductionMaxPersons,
        SmigMonthly, MonthlyHours, MonthlyWorkingDays, TransportExemptionCap));

    /// <summary>The rates a snapshot holds; null when there is none or it cannot be read.</summary>
    public static PayrollParameters? FromSnapshot(int year, string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return null;

        Snapshot? s;
        try
        {
            s = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(snapshot);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (s is null) return null;

        return new PayrollParameters(year)
        {
            CnssCeiling = s.CnssCeiling, CnssEmployeeRate = s.CnssEmployeeRate, CnssEmployerRate = s.CnssEmployerRate,
            FamilyAllowanceRate = s.FamilyAllowanceRate, TrainingTaxRate = s.TrainingTaxRate,
            AmoEmployeeRate = s.AmoEmployeeRate, AmoEmployerRate = s.AmoEmployerRate,
            ProfessionalExpensesLowRate = s.ProfessionalExpensesLowRate,
            ProfessionalExpensesHighRate = s.ProfessionalExpensesHighRate,
            ProfessionalExpensesThreshold = s.ProfessionalExpensesThreshold,
            ProfessionalExpensesAnnualCap = s.ProfessionalExpensesAnnualCap, IncomeTaxScale = s.IncomeTaxScale,
            FamilyDeductionPerPerson = s.FamilyDeductionPerPerson, FamilyDeductionMaxPersons = s.FamilyDeductionMaxPersons,
            SmigMonthly = s.SmigMonthly, MonthlyHours = s.MonthlyHours, MonthlyWorkingDays = s.MonthlyWorkingDays,
            TransportExemptionCap = s.TransportExemptionCap
        };
    }

    private sealed record Snapshot(
        decimal CnssCeiling, decimal CnssEmployeeRate, decimal CnssEmployerRate, decimal FamilyAllowanceRate,
        decimal TrainingTaxRate, decimal AmoEmployeeRate, decimal AmoEmployerRate, decimal ProfessionalExpensesLowRate,
        decimal ProfessionalExpensesHighRate, decimal ProfessionalExpensesThreshold,
        decimal ProfessionalExpensesAnnualCap, string IncomeTaxScale, decimal FamilyDeductionPerPerson,
        int FamilyDeductionMaxPersons, decimal SmigMonthly, decimal MonthlyHours, decimal MonthlyWorkingDays,
        decimal TransportExemptionCap);
}

/// <summary>
/// One month's payroll for a site. Drafted, adjusted with the month's variables, then closed; a
/// closed month is the record the CNSS and tax returns were made from and is no longer recomputed.
/// </summary>
public sealed class PayrollPeriod : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<Payslip> _payslips = [];

    private PayrollPeriod() { }

    public PayrollPeriod(Guid siteId, int year, int month)
    {
        SiteId = siteId;
        Year = year;
        Month = month;
    }

    public Guid SiteId { get; set; }
    public int Year { get; private set; }
    public int Month { get; private set; }
    public PayrollStatus Status { get; private set; } = PayrollStatus.Draft;
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedByUserId { get; private set; }

    /// <summary>
    /// The rates the payslips were computed with, taken when the month is first closed
    /// (<see cref="PayrollParameters.ToSnapshot"/>). Null for a month never closed, and for a month
    /// closed before it was kept. A reopened month keeps it: see <see cref="RatesFor"/>.
    /// </summary>
    public string? RatesSnapshot { get; private set; }

    public IReadOnlyCollection<Payslip> Payslips => _payslips.AsReadOnly();

    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);
    public bool IsClosed => Status == PayrollStatus.Closed;

    public Result<Payslip> Add(Payslip payslip)
    {
        if (IsClosed) return Result.Failure<Payslip>(Error.Conflict("Payroll_Closed"));
        if (_payslips.Any(p => p.EmployeeId == payslip.EmployeeId))
            return Result.Failure<Payslip>(Error.Conflict("Payroll_AlreadyIncluded"));

        payslip.AttachTo(Id);
        _payslips.Add(payslip);
        return Result.Success(payslip);
    }

    /// <summary>
    /// Takes an employee off a draft month: no longer on the site's payroll for it (left before the
    /// month, salary cleared, now a partner…).
    /// </summary>
    public Result Remove(Guid employeeId)
    {
        if (IsClosed) return Result.Failure(Error.Conflict("Payroll_Closed"));
        var payslip = _payslips.FirstOrDefault(p => p.EmployeeId == employeeId);
        if (payslip is null) return Result.Failure(Error.NotFound("Payroll_PayslipNotFound"));

        _payslips.Remove(payslip);
        return Result.Success();
    }

    /// <summary>
    /// Closes the month. Every payslip is computed once more with the rates kept with it, so the
    /// amounts stored and the rates printed beside them always agree: the rates of the first
    /// closing for a month reopened since (see <see cref="RatesFor"/>), else
    /// <paramref name="rates"/>, the year's current ones, which the drafts already follow.
    /// </summary>
    /// <param name="rates">The year's current rates.</param>
    public Result Close(DateTimeOffset now, Guid byUserId, PayrollParameters? rates = null)
    {
        if (IsClosed) return Result.Failure(Error.Conflict("Payroll_Closed"));
        if (_payslips.Count == 0) return Result.Failure(Error.Validation("Payroll_Empty"));

        if (rates is not null)
        {
            var kept = RatesFor(rates);
            foreach (var payslip in _payslips) payslip.Recompute(payslip.Variables, kept, LastDay);
            RatesSnapshot = kept.ToSnapshot();
        }

        Status = PayrollStatus.Closed;
        ClosedAt = now;
        ClosedByUserId = byUserId;
        return Result.Success();
    }

    /// <summary>
    /// The rates the month is computed and printed with: those kept at its first closing, else
    /// <paramref name="current"/>.
    /// <para>
    /// A month reopened to correct one payslip keeps the rates it was closed with: the
    /// contributions of March are due at March's rates, and a rate changed since for the year
    /// must not leave one payslip at the new rate beside the others at the old one.
    /// </para>
    /// </summary>
    public PayrollParameters RatesFor(PayrollParameters current) =>
        PayrollParameters.FromSnapshot(Year, RatesSnapshot) is { } kept ? kept : current;

    /// <summary>The rates to print the month with: see <see cref="RatesFor"/>.</summary>
    public PayrollParameters RatesForPrinting(PayrollParameters current) => RatesFor(current);

    /// <summary>
    /// Back to draft, to correct an error found after closing. The reason goes to the audit trail.
    /// The rates of the closing stay with the month: see <see cref="RatesFor"/>.
    /// </summary>
    public Result Reopen()
    {
        if (!IsClosed) return Result.Failure(Error.Conflict("Payroll_NotClosed"));
        Status = PayrollStatus.Draft;
        ClosedAt = null;
        ClosedByUserId = null;
        return Result.Success();
    }
}

public enum PayrollStatus
{
    Draft = 0,
    Closed = 1
}

/// <summary>
/// One employee's pay for the month: the file as it stood (name, CNSS number, salary…), the
/// month's variables, and every amount computed from them, stored so that a payslip printed again
/// later is the one that was paid.
/// </summary>
public sealed class Payslip : Entity, IAuditable
{
    private Payslip() { }

    public Payslip(Guid employeeId) => EmployeeId = employeeId;

    public Guid PayrollPeriodId { get; private set; }
    internal void AttachTo(Guid periodId) => PayrollPeriodId = periodId;

    public Guid EmployeeId { get; private set; }

    // ---- The file, as of the month -------------------------------------------------------------

    public string Number { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Cin { get; set; }
    public string? CnssNumber { get; set; }
    public string? JobTitle { get; set; }
    public DateOnly HireDate { get; set; }
    public ContractType ContractType { get; set; }
    public int DependentPersons { get; set; }
    public decimal BaseSalary { get; set; }
    public string? Rib { get; set; }
    public string? BankName { get; set; }

    // ---- The month's variables -----------------------------------------------------------------

    public PayslipVariables Variables { get; private set; } = new();

    // ---- Computed ------------------------------------------------------------------------------

    public PayslipAmounts Amounts { get; private set; } = new();

    public void Recompute(PayslipVariables variables, PayrollParameters parameters, DateOnly payDate)
    {
        Variables = variables;
        Amounts = PayrollCalculator.Compute(Input(variables, payDate), parameters);
    }

    private PayslipInput Input(PayslipVariables v, DateOnly payDate) =>
        new(BaseSalary, HireDate, payDate, ContractType, DependentPersons, v);

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}

/// <summary>What changes from one month to the next. Entered by HR; absences are pre-filled from leave.</summary>
public sealed record PayslipVariables
{
    /// <summary>
    /// Working days not paid, taken from the approved leave (unpaid leave, unjustified absence,
    /// work-accident stoppage) each time the month is refreshed from the files.
    /// </summary>
    public decimal AbsenceDays { get; init; }

    /// <summary>
    /// Working days not paid entered by hand by HR, on top of <see cref="AbsenceDays"/>. Kept when
    /// the month is refreshed: only the leave-derived figure is taken again.
    /// </summary>
    public decimal ManualAbsenceDays { get; init; }

    /// <summary>Every unpaid day of the month: the leave-derived ones and those entered by hand.</summary>
    public decimal TotalAbsenceDays => AbsenceDays + ManualAbsenceDays;

    /// <summary>
    /// Working days of the whole month, Monday to Saturday less public holidays, when the employee
    /// joined or left during it; 0 for a month worked from end to end.
    /// </summary>
    public decimal MonthWorkingDays { get; init; }

    /// <summary>Of <see cref="MonthWorkingDays"/>, those between the hire date and the exit date.</summary>
    public decimal ContractWorkingDays { get; init; }

    /// <summary>Hired or left during the month: the base salary is paid for the days of the contract only.</summary>
    public bool IsProrated => MonthWorkingDays > 0 && ContractWorkingDays < MonthWorkingDays;

    /// <summary>Overtime hours at +25 % (6 h–21 h).</summary>
    public decimal OvertimeHours25 { get; init; }

    /// <summary>Overtime hours at +50 % (night, or day on the weekly rest day).</summary>
    public decimal OvertimeHours50 { get; init; }

    /// <summary>Overtime hours at +100 % (night on the weekly rest day).</summary>
    public decimal OvertimeHours100 { get; init; }

    /// <summary>Taxable bonuses: on-call allowance, output bonus, 13th month…</summary>
    public decimal TaxableBonus { get; init; }

    /// <summary>Transport allowance; exempt up to the yearly cap, taxable beyond.</summary>
    public decimal TransportAllowance { get; init; }

    /// <summary>Other allowances exempt by nature (meal basket, tools, representation within limits).</summary>
    public decimal OtherExemptAllowance { get; init; }

    /// <summary>Salary advance taken back this month.</summary>
    public decimal AdvanceDeduction { get; init; }

    /// <summary>Other deduction from net: loan repayment, garnishment…</summary>
    public decimal OtherDeduction { get; init; }

    // ---- Departure: carried over from the final settlement -------------------------------------

    /// <summary>Indemnité compensatrice de congés payés: taxable, subject to CNSS and AMO.</summary>
    public decimal LeaveIndemnity { get; init; }

    /// <summary>Indemnité compensatrice de préavis: taxable, subject to CNSS and AMO.</summary>
    public decimal NoticeIndemnity { get; init; }

    /// <summary>Indemnité légale de licenciement (art. 53): exempt from income tax and contributions.</summary>
    public decimal SeverancePay { get; init; }

    // ---- Work accident: carried over from the stoppages -----------------------------------------

    /// <summary>Working days of the month on a work-accident stoppage; already counted in <see cref="AbsenceDays"/>.</summary>
    public decimal WorkAccidentDays { get; init; }

    /// <summary>
    /// Daily allowances the employer advances on the insurer's behalf (subrogation), reimbursed by
    /// the insurer: exempt from income tax and contributions (CGI art. 57). Due for every calendar
    /// day of the stoppage, see <see cref="AccidentRules.Allowance"/>.
    /// </summary>
    public decimal WorkAccidentAllowance { get; init; }

    public string? Note { get; init; }
}

public sealed record PayslipInput(decimal BaseSalary, DateOnly HireDate, DateOnly PayDate, ContractType ContractType,
                                  int DependentPersons, PayslipVariables Variables);

/// <summary>Every amount of a payslip, computed by <see cref="PayrollCalculator"/>.</summary>
public sealed record PayslipAmounts
{
    public decimal PaidDays { get; init; }
    public decimal AbsenceDeduction { get; init; }
    public decimal SeniorityRate { get; init; }
    public decimal SeniorityBonus { get; init; }
    public decimal OvertimeAmount { get; init; }
    public decimal ExemptAllowances { get; init; }

    /// <summary>Everything paid before deductions.</summary>
    public decimal Gross { get; init; }

    /// <summary>Gross less the exempt allowances: the base for the CNSS, the AMO and the income tax.</summary>
    public decimal GrossTaxable { get; init; }

    public decimal CnssEmployee { get; init; }
    public decimal AmoEmployee { get; init; }
    public decimal ProfessionalExpenses { get; init; }
    public decimal NetTaxable { get; init; }
    public decimal IncomeTaxBeforeFamily { get; init; }
    public decimal FamilyDeduction { get; init; }
    public decimal IncomeTax { get; init; }
    public decimal OtherDeductions { get; init; }
    public decimal NetPay { get; init; }

    public decimal CnssEmployer { get; init; }
    public decimal FamilyAllowanceEmployer { get; init; }
    public decimal TrainingTaxEmployer { get; init; }
    public decimal AmoEmployer { get; init; }
    public decimal EmployerCost { get; init; }

    /// <summary>Why a contribution or the tax was not charged, when it was not (insertion contract, internship…).</summary>
    public PayrollExemption Exemption { get; init; }
}

public enum PayrollExemption
{
    None = 0,
    /// <summary>Contrat d'insertion ANAPEC (Idmaj): no CNSS, AMO or income tax on the stipend.</summary>
    InsertionContract = 1,
    /// <summary>Tahfiz: income tax exempt up to 10,000 DH gross, employer CNSS borne by the State.</summary>
    Tahfiz = 2,
    /// <summary>Internship gratification: no contributions, no tax.</summary>
    Internship = 3
}

/// <summary>
/// The Moroccan payslip arithmetic, apart from any database.
/// <list type="number">
/// <item>Gross: base, paid in proportion to the working days of the contract when the employee joined
/// or left during the month (base × days present / working days of the month, Monday to Saturday
/// less public holidays, for both), less unpaid days (base / 26 per day), plus seniority (Code du travail art. 350:
/// 5 % after 2 years, 10 % after 5, 15 % after 12, 20 % after 20, 25 % after 25), overtime (hourly
/// rate base / 191, +25, +50 or +100 %), bonuses and allowances.</item>
/// <item>CNSS social benefits on the gross taxable capped at 6,000 DH; AMO, family allowances and
/// training tax uncapped.</item>
/// <item>Income tax: gross taxable less the professional-expense allowance (35 % up to 78,000 DH a
/// year, 25 % above, 35,000 DH a year at most) and the employee's CNSS and AMO, taxed on the yearly
/// scale brought to the month, less 500 DH a year per dependent person (6 at most).</item>
/// </list>
/// </summary>
public static class PayrollCalculator
{
    public const decimal TahfizTaxExemptGross = 10000m;

    public static decimal SeniorityRate(DateOnly hireDate, DateOnly on)
    {
        var years = LeaveRules.FullYears(hireDate, on);
        return years switch
        {
            >= 25 => 25m,
            >= 20 => 20m,
            >= 12 => 15m,
            >= 5 => 10m,
            >= 2 => 5m,
            _ => 0m
        };
    }

    public static PayrollExemption ExemptionFor(ContractType type) => type switch
    {
        ContractType.Anapec => PayrollExemption.InsertionContract,
        ContractType.Tahfiz => PayrollExemption.Tahfiz,
        ContractType.Internship => PayrollExemption.Internship,
        _ => PayrollExemption.None
    };

    /// <summary>Partners are paid as associates and temporary staff by their agency, not here.</summary>
    public static bool IsOnPayroll(ContractType type) => type is not (ContractType.Partner or ContractType.Temporary);

    /// <summary>Yearly income tax on a yearly net taxable income, bracket by bracket.</summary>
    public static decimal AnnualIncomeTax(decimal annualNetTaxable, IReadOnlyList<(decimal? UpTo, decimal Rate)> scale)
    {
        decimal tax = 0m, lower = 0m;
        foreach (var (upTo, rate) in scale)
        {
            var top = upTo ?? decimal.MaxValue;
            if (annualNetTaxable > lower)
                tax += (Math.Min(annualNetTaxable, top) - lower) * rate / 100m;
            if (upTo is null || annualNetTaxable <= top) break;
            lower = top;
        }

        return tax;
    }

    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The base salary for the days of the contract: the whole base for a full month, otherwise
    /// base × working days present / working days of the month. A hire on the 2nd of a month whose
    /// 1st is a holiday is a full month; an exit on the 1st pays that one day.
    /// </summary>
    public static decimal ContractBase(decimal baseSalary, PayslipVariables v) =>
        v.IsProrated ? R(baseSalary * Math.Max(0m, v.ContractWorkingDays) / v.MonthWorkingDays) : baseSalary;

    public static PayslipAmounts Compute(PayslipInput input, PayrollParameters p)
    {
        var v = input.Variables;
        var exemption = ExemptionFor(input.ContractType);

        // Days before the hire or after the exit: the base in proportion to the days of the contract.
        // Ordinary unpaid absences inside it keep the 26-day convention.
        var contractBase = ContractBase(input.BaseSalary, v);
        var absenceDays = Math.Clamp(v.TotalAbsenceDays, 0m, p.MonthlyWorkingDays);
        var dailyRate = input.BaseSalary / p.MonthlyWorkingDays;
        var unpaid = absenceDays >= p.MonthlyWorkingDays ? input.BaseSalary : R(dailyRate * absenceDays);
        var earnedBase = Math.Max(0m, contractBase - unpaid);
        var absenceDeduction = input.BaseSalary - earnedBase;

        // The days declared to the CNSS are the days worked, whole: those of the contract within
        // the month, at most the 26 of a full month, less the unpaid ones. A day partly worked
        // (half a day of unpaid leave) is a day worked.
        var contractDays = v.IsProrated ? Math.Min(Math.Max(0m, Math.Ceiling(v.ContractWorkingDays)), p.MonthlyWorkingDays)
                                        : p.MonthlyWorkingDays;

        // Interns and insertion contracts receive a stipend, not a salary: no seniority, no overtime.
        var salaried = exemption is PayrollExemption.None or PayrollExemption.Tahfiz;

        var seniorityRate = salaried ? SeniorityRate(input.HireDate, input.PayDate) : 0m;
        var seniority = R(earnedBase * seniorityRate / 100m);

        var hourly = input.BaseSalary / p.MonthlyHours;
        var overtime = salaried
            ? R(hourly * (v.OvertimeHours25 * 1.25m + v.OvertimeHours50 * 1.5m + v.OvertimeHours100 * 2m))
            : 0m;

        var transportExempt = Math.Min(Math.Max(0m, v.TransportAllowance), p.TransportExemptionCap);
        var transportTaxable = Math.Max(0m, v.TransportAllowance) - transportExempt;
        var exempt = transportExempt + Math.Max(0m, v.OtherExemptAllowance) + Math.Max(0m, v.SeverancePay)
                     + Math.Max(0m, v.WorkAccidentAllowance);
        var departure = Math.Max(0m, v.LeaveIndemnity) + Math.Max(0m, v.NoticeIndemnity);

        var grossTaxable = R(earnedBase + seniority + overtime + Math.Max(0m, v.TaxableBonus) + transportTaxable + departure);
        var gross = R(grossTaxable + exempt);

        var contributes = exemption is PayrollExemption.None or PayrollExemption.Tahfiz;
        var cnssBase = Math.Min(grossTaxable, p.CnssCeiling);

        var cnssEmployee = contributes ? R(cnssBase * p.CnssEmployeeRate / 100m) : 0m;
        var amoEmployee = contributes ? R(grossTaxable * p.AmoEmployeeRate / 100m) : 0m;

        // The State takes on the employer's CNSS and training tax under Tahfiz.
        var employerCnssDue = exemption == PayrollExemption.None;
        var cnssEmployer = employerCnssDue ? R(cnssBase * p.CnssEmployerRate / 100m) : 0m;
        var familyAllowance = employerCnssDue ? R(grossTaxable * p.FamilyAllowanceRate / 100m) : 0m;
        var trainingTax = employerCnssDue ? R(grossTaxable * p.TrainingTaxRate / 100m) : 0m;
        var amoEmployer = contributes ? R(grossTaxable * p.AmoEmployerRate / 100m) : 0m;

        var expensesRate = grossTaxable * 12m <= p.ProfessionalExpensesThreshold
            ? p.ProfessionalExpensesLowRate
            : p.ProfessionalExpensesHighRate;
        var expenses = R(Math.Min(grossTaxable * expensesRate / 100m, p.ProfessionalExpensesAnnualCap / 12m));

        var netTaxable = Math.Max(0m, R(grossTaxable - expenses - cnssEmployee - amoEmployee));

        var taxed = exemption switch
        {
            PayrollExemption.InsertionContract or PayrollExemption.Internship => false,
            PayrollExemption.Tahfiz => grossTaxable > TahfizTaxExemptGross,
            _ => true
        };

        var taxBefore = taxed ? R(AnnualIncomeTax(netTaxable * 12m, p.ParseScale()) / 12m) : 0m;
        var persons = Math.Min(Math.Max(0, input.DependentPersons), p.FamilyDeductionMaxPersons);
        var family = R(Math.Min(taxBefore, persons * p.FamilyDeductionPerPerson / 12m));
        var tax = taxBefore - family;

        var other = Math.Max(0m, v.AdvanceDeduction) + Math.Max(0m, v.OtherDeduction);
        var net = R(gross - cnssEmployee - amoEmployee - tax - other);

        return new PayslipAmounts
        {
            PaidDays = Math.Max(0m, Math.Ceiling(contractDays - absenceDays)),
            AbsenceDeduction = absenceDeduction,
            SeniorityRate = seniorityRate,
            SeniorityBonus = seniority,
            OvertimeAmount = overtime,
            ExemptAllowances = exempt,
            Gross = gross,
            GrossTaxable = grossTaxable,
            CnssEmployee = cnssEmployee,
            AmoEmployee = amoEmployee,
            ProfessionalExpenses = expenses,
            NetTaxable = netTaxable,
            IncomeTaxBeforeFamily = taxBefore,
            FamilyDeduction = family,
            IncomeTax = tax,
            OtherDeductions = other,
            NetPay = net,
            CnssEmployer = cnssEmployer,
            FamilyAllowanceEmployer = familyAllowance,
            TrainingTaxEmployer = trainingTax,
            AmoEmployer = amoEmployer,
            EmployerCost = R(gross + cnssEmployer + familyAllowance + trainingTax + amoEmployer),
            Exemption = exemption
        };
    }
}
