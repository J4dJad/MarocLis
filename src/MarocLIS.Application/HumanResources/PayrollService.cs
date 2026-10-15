using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.HumanResources;

public sealed record PayrollPeriodRow(Guid PeriodId, int Year, int Month, PayrollStatus Status, int Payslips,
                                      decimal TotalGross, decimal TotalNet, decimal TotalEmployerCost,
                                      DateTimeOffset? ClosedAt, string? ClosedBy);

public enum PayslipWarning
{
    /// <summary>Full-time salary below the SMIG.</summary>
    BelowMinimumWage = 0,
    NoCnssNumber = 1,
    NoRib = 2,
    /// <summary>Deductions exceed what is left to pay.</summary>
    NegativeNet = 3
}

public sealed record PayslipRow(
    Guid PayslipId,
    Guid EmployeeId,
    string Number,
    string FullName,
    string? JobTitle,
    ContractType ContractType,
    decimal BaseSalary,
    PayslipVariables Variables,
    PayslipAmounts Amounts,
    IReadOnlyList<PayslipWarning> Warnings);

public sealed record PayrollPeriodDetail(PayrollPeriodRow Period, IReadOnlyList<PayslipRow> Payslips)
{
    public decimal Total(Func<PayslipAmounts, decimal> amount) => Payslips.Sum(p => amount(p.Amounts));
}

/// <summary>What the printed payslip needs: the employer, the period and the payslip as stored.</summary>
public sealed record PayslipDocument(InvoiceIssuer Employer, int Year, int Month, PayrollStatus Status, Payslip Payslip,
                                     PayrollParameters Parameters);

public interface IPayslipRenderer
{
    /// <summary>One PDF, one page per payslip.</summary>
    byte[] Render(IReadOnlyList<PayslipDocument> payslips);
}

/// <summary>Who goes on a month's payroll, with what the leave already says about their absences.</summary>
/// <param name="UnpaidDays">Working days withheld from the salary within the contract, work-accident days included.</param>
/// <param name="WorkAccidentDays">Of which on a work-accident stoppage.</param>
/// <param name="SubrogatedDays">
/// Calendar days of the month on a stoppage for which the employer advances the insurer's allowance.
/// </param>
/// <param name="MonthWorkingDays">
/// Working days of the month (Monday to Saturday less holidays) when the employee joined or left
/// during it; 0 for a full month.
/// </param>
/// <param name="ContractWorkingDays">Of those, the days between the hire and the exit.</param>
public sealed record PayrollCandidate(Guid EmployeeId, decimal UnpaidDays, decimal WorkAccidentDays = 0m,
                                      decimal SubrogatedDays = 0m, decimal MonthWorkingDays = 0m,
                                      decimal ContractWorkingDays = 0m);

/// <summary>
/// Monthly payroll. Everything here requires the salary right: nobody without it sees a salary.
/// <para>
/// A month is opened as a draft with a payslip per salaried employee, the unpaid days already
/// taken from approved unpaid leave and unjustified absences. HR enters the month's variables
/// (overtime, bonuses, advances), each change recomputing that payslip; the month is then closed,
/// after which its payslips are the record the declarations were made from.
/// </para>
/// </summary>
public sealed class PayrollService(
    IRepository<PayrollPeriod> periods,
    IRepository<PayrollParameters> parameters,
    IRepository<Employee> employees,
    IPayrollQueries queries,
    IRepository<FinalSettlement> settlements,
    IRepository<LeaveLedgerEntry> ledger,
    IHrDocumentQueries hrQueries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    private Result Require() => guard.Require(Permissions.HumanResources.Salary);

    public async Task<Result<IReadOnlyList<PayrollPeriodRow>>> GetPeriodsAsync(CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PayrollPeriodRow>>(permitted.FirstError);
        return Result.Success(await queries.GetPeriodsAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<PayrollParameters>> GetParametersAsync(int year, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<PayrollParameters>(permitted.FirstError);
        return Result.Success(await ParametersForAsync(year, ct));
    }

    /// <summary>The year's rates; a new year starts from the previous one's, or from the 2026 law.</summary>
    private async Task<PayrollParameters> ParametersForAsync(int year, CancellationToken ct)
    {
        // Two handlers of the same screen asking for a year not created yet would each add it, and
        // the second insert breaks the one-row-per-year index. One creation at a time per screen.
        await _parametersGate.WaitAsync(ct);
        try
        {
            var all = await parameters.ListAsync(p => p.Year <= year, ct);
            if (all.FirstOrDefault(p => p.Year == year) is { } found) return found;

            var created = new PayrollParameters(year);
            if (all.OrderByDescending(p => p.Year).FirstOrDefault() is { } previous) CopyRates(previous, created);

            await parameters.AddAsync(created, ct);
            unitOfWork.AuditReason = $"Paramètres de paie {year} créés";
            await unitOfWork.SaveChangesAsync(ct);
            return created;
        }
        finally
        {
            _parametersGate.Release();
        }
    }

    private readonly SemaphoreSlim _parametersGate = new(1, 1);

    private static void CopyRates(PayrollParameters from, PayrollParameters to)
    {
        to.CnssCeiling = from.CnssCeiling;
        to.CnssEmployeeRate = from.CnssEmployeeRate;
        to.CnssEmployerRate = from.CnssEmployerRate;
        to.FamilyAllowanceRate = from.FamilyAllowanceRate;
        to.TrainingTaxRate = from.TrainingTaxRate;
        to.AmoEmployeeRate = from.AmoEmployeeRate;
        to.AmoEmployerRate = from.AmoEmployerRate;
        to.ProfessionalExpensesLowRate = from.ProfessionalExpensesLowRate;
        to.ProfessionalExpensesHighRate = from.ProfessionalExpensesHighRate;
        to.ProfessionalExpensesThreshold = from.ProfessionalExpensesThreshold;
        to.ProfessionalExpensesAnnualCap = from.ProfessionalExpensesAnnualCap;
        to.IncomeTaxScale = from.IncomeTaxScale;
        to.FamilyDeductionPerPerson = from.FamilyDeductionPerPerson;
        to.FamilyDeductionMaxPersons = from.FamilyDeductionMaxPersons;
        to.SmigMonthly = from.SmigMonthly;
        to.MonthlyHours = from.MonthlyHours;
        to.MonthlyWorkingDays = from.MonthlyWorkingDays;
        to.TransportExemptionCap = from.TransportExemptionCap;
    }

    /// <summary>
    /// Saves the year's rates. Draft months of that year are recomputed with them, except a month
    /// reopened after closing, which keeps the rates it was closed with (<see cref="PayrollPeriod.RatesFor"/>).
    /// </summary>
    public async Task<Result> SaveParametersAsync(int year, Action<PayrollParameters> change, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        // Tried on a copy first: a refused change must not stay on the tracked rates.
        var current = await ParametersForAsync(year, ct);
        var trial = new PayrollParameters(year);
        CopyRates(current, trial);
        change(trial);
        var valid = trial.Validate();
        if (valid.IsFailure) return valid;

        CopyRates(trial, current);

        parameters.Update(current);

        foreach (var id in await queries.DraftPeriodIdsAsync(year, ct))
            if (await periods.GetByIdWithDetailsAsync(id, ct) is { } period)
            {
                var rates = period.RatesFor(current);
                foreach (var slip in period.Payslips) slip.Recompute(slip.Variables, rates, period.LastDay);
                periods.Update(period);
            }

        unitOfWork.AuditReason = $"Paramètres de paie {year} modifiés";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<Guid>> OpenAsync(int year, int month, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (month is < 1 or > 12 || year < 2000) return Result.Failure<Guid>(Error.Validation("Payroll_PeriodInvalid"));
        if (await queries.FindPeriodAsync(currentUser.SiteId, year, month, ct) is not null)
            return Result.Failure<Guid>(Error.Conflict("Payroll_PeriodExists", $"{month:00}/{year}"));

        var rates = await ParametersForAsync(year, ct);
        var period = new PayrollPeriod(currentUser.SiteId, year, month);

        var added = await FillAsync(period, rates, ct);
        if (added == 0) return Result.Failure<Guid>(Error.Validation("Payroll_NobodyToPay"));

        await periods.AddAsync(period, ct);
        unitOfWork.AuditReason = $"Paie {month:00}/{year} ouverte ({added} bulletins)";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(period.Id);
    }

    /// <summary>
    /// Takes the files as they are now: new salaried staff are added, salaries and family
    /// situations updated, absences taken again from leave, and the payslips of those no longer
    /// on the month's payroll (left before it, salary cleared, partner…) taken off. The variables
    /// already typed, unpaid days entered by hand included, are kept.
    /// </summary>
    public async Task<Result<int>> RefreshAsync(Guid periodId, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure<int>(Error.NotFound("Payroll_NotFound"));
        if (period.IsClosed) return Result.Failure<int>(Error.Conflict("Payroll_Closed"));

        var rates = period.RatesFor(await ParametersForAsync(period.Year, ct));
        var added = await FillAsync(period, rates, ct);

        periods.Update(period);
        unitOfWork.AuditReason = $"Paie {period.Month:00}/{period.Year} actualisée depuis les dossiers";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(added);
    }

    private async Task<int> FillAsync(PayrollPeriod period, PayrollParameters rates, CancellationToken ct)
    {
        var added = 0;
        var settled = await queries.SettlementsAsync(period.FirstDay, period.LastDay, ct);
        var paid = new HashSet<Guid>();
        foreach (var candidate in await queries.CandidatesAsync(period.SiteId, period.FirstDay, period.LastDay,
                                                             rates.MonthlyWorkingDays, ct))
        {
            var employee = await employees.GetByIdAsync(candidate.EmployeeId, ct);
            if (employee?.BaseSalary is null || !PayrollCalculator.IsOnPayroll(employee.ContractType)) continue;
            paid.Add(employee.Id);

            var slip = period.Payslips.FirstOrDefault(p => p.EmployeeId == employee.Id);
            var isNew = slip is null;
            slip ??= new Payslip(employee.Id);

            slip.Number = employee.Number;
            slip.FullName = employee.FullName;
            slip.Cin = employee.Cin;
            slip.CnssNumber = employee.CnssNumber;
            slip.JobTitle = employee.JobTitle;
            (slip.HireDate, slip.BaseSalary) = ContractOfMonth(employee, period, isNew ? null : slip.BaseSalary);
            slip.ContractType = employee.ContractType;
            slip.DependentPersons = DependentPersons(employee);
            slip.Rib = employee.Rib;
            slip.BankName = employee.BankName;

            // The leave-derived figures are taken again; what HR typed, the unpaid days entered by
            // hand included, stays.
            var variables = FromFiles(isNew ? new PayslipVariables() : slip.Variables, candidate,
                                      settled.GetValueOrDefault(employee.Id), slip.BaseSalary);
            slip.Recompute(variables, rates, period.LastDay);

            if (isNew)
            {
                period.Add(slip);
                added++;
            }
        }

        // A draft month is rebuilt from the files: whoever is no longer on it is taken off rather
        // than left with a full month's pay. Never a payslip that pays a departure, though: the
        // indemnities of a final settlement are paid with the month whatever the file now says.
        foreach (var gone in period.Payslips.Where(p => !paid.Contains(p.EmployeeId) && !PaysDeparture(p, settled))
                                    .Select(p => p.EmployeeId).ToList())
            period.Remove(gone);

        return added;
    }

    /// <summary>
    /// The hire date and base salary the month is paid on. The month of a departure followed by a
    /// re-hire is the last month of the contract that ended: its seniority runs from that
    /// contract's hire date and its salary is the one of that contract, not the new contract's.
    /// </summary>
    /// <param name="kept">The salary already on the draft payslip, for a re-hire recorded before the previous salary was kept.</param>
    public static (DateOnly HireDate, decimal BaseSalary) ContractOfMonth(Employee employee, PayrollPeriod period,
                                                                         decimal? kept)
    {
        var current = employee.BaseSalary ?? 0m;
        if (employee.PreviousHireDate is { } hire && employee.PreviousExitDate is { } exit
            && hire <= period.LastDay && exit >= period.FirstDay)
            return (hire, employee.PreviousBaseSalary ?? kept ?? current);

        return (employee.HireDate, current);
    }

    private static bool PaysDeparture(Payslip slip, IReadOnlyDictionary<Guid, SettledAmounts> settled) =>
        slip.Variables.LeaveIndemnity + slip.Variables.NoticeIndemnity + slip.Variables.SeverancePay > 0m
        || (settled.GetValueOrDefault(slip.EmployeeId) is { } s && s.LeaveIndemnity + s.NoticeIndemnity + s.SeverancePay > 0m);

    /// <summary>
    /// The variables as the files give them: absences from the leave, days of the contract,
    /// work-accident days and allowance, departure indemnities from the final settlement. The rest
    /// of <paramref name="typed"/> (overtime, bonuses, unpaid days entered by hand…) is kept.
    /// </summary>
    public static PayslipVariables FromFiles(PayslipVariables typed, PayrollCandidate candidate, SettledAmounts? settled,
                                             decimal baseSalary) =>
        typed with
        {
            AbsenceDays = candidate.UnpaidDays,
            MonthWorkingDays = candidate.MonthWorkingDays,
            ContractWorkingDays = candidate.ContractWorkingDays,
            // What the final settlement says is owed on departure, as it now stands.
            LeaveIndemnity = settled?.LeaveIndemnity ?? 0m,
            NoticeIndemnity = settled?.NoticeIndemnity ?? 0m,
            SeverancePay = settled?.SeverancePay ?? 0m,
            WorkAccidentDays = candidate.WorkAccidentDays,
            WorkAccidentAllowance = AccidentRules.Allowance(baseSalary, candidate.SubrogatedDays)
        };

    /// <summary>Spouse when married, plus the dependent children: the persons the income-tax deduction counts.</summary>
    public static int DependentPersons(Employee employee) =>
        (employee.FamilyStatus == FamilyStatus.Married ? 1 : 0) + Math.Max(0, employee.DependentChildren);

    public async Task<Result<PayrollPeriodDetail>> GetPeriodAsync(Guid periodId, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<PayrollPeriodDetail>(permitted.FirstError);

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure<PayrollPeriodDetail>(Error.NotFound("Payroll_NotFound"));

        var rates = period.RatesFor(await ParametersForAsync(period.Year, ct));
        var row = (await queries.GetPeriodsAsync(period.SiteId, ct)).First(p => p.PeriodId == periodId);

        return Result.Success(new PayrollPeriodDetail(row,
        [
            .. period.Payslips.OrderBy(p => p.FullName, StringComparer.CurrentCulture)
                  .Select(p => new PayslipRow(p.Id, p.EmployeeId, p.Number, p.FullName, p.JobTitle, p.ContractType,
                                              p.BaseSalary, p.Variables, p.Amounts, Warnings(p, rates)))
        ]));
    }

    public static IReadOnlyList<PayslipWarning> Warnings(Payslip p, PayrollParameters rates)
    {
        var warnings = new List<PayslipWarning>();
        var exemption = PayrollCalculator.ExemptionFor(p.ContractType);
        if (exemption is PayrollExemption.None or PayrollExemption.Tahfiz && p.BaseSalary < rates.SmigMonthly)
            warnings.Add(PayslipWarning.BelowMinimumWage);
        // Every payslip the BDS declares (all but an internship's, ANAPEC included) needs the number.
        if (exemption != PayrollExemption.Internship && CnssBdsFile.Digits(p.CnssNumber).Length == 0)
            warnings.Add(PayslipWarning.NoCnssNumber);
        if (string.IsNullOrWhiteSpace(p.Rib)) warnings.Add(PayslipWarning.NoRib);
        if (p.Amounts.NetPay < 0) warnings.Add(PayslipWarning.NegativeNet);
        return warnings;
    }

    public async Task<Result> UpdateVariablesAsync(Guid periodId, Guid payslipId, PayslipVariables variables,
                                                   CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        decimal[] values = [variables.ManualAbsenceDays, variables.OvertimeHours25, variables.OvertimeHours50,
                            variables.OvertimeHours100, variables.TaxableBonus, variables.TransportAllowance,
                            variables.OtherExemptAllowance, variables.AdvanceDeduction, variables.OtherDeduction];
        if (values.Any(v => v < 0)) return Result.Failure(Error.Validation("Payroll_NegativeVariable"));

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure(Error.NotFound("Payroll_NotFound"));
        if (period.IsClosed) return Result.Failure(Error.Conflict("Payroll_Closed"));

        var slip = period.Payslips.FirstOrDefault(p => p.Id == payslipId);
        if (slip is null) return Result.Failure(Error.NotFound("Payroll_PayslipNotFound"));

        // What HR types; the figures taken from the files (leave, contract days, accident,
        // settlement) stay as the last refresh left them.
        var typed = slip.Variables with
        {
            ManualAbsenceDays = variables.ManualAbsenceDays,
            OvertimeHours25 = variables.OvertimeHours25,
            OvertimeHours50 = variables.OvertimeHours50,
            OvertimeHours100 = variables.OvertimeHours100,
            TaxableBonus = variables.TaxableBonus,
            TransportAllowance = variables.TransportAllowance,
            OtherExemptAllowance = variables.OtherExemptAllowance,
            AdvanceDeduction = variables.AdvanceDeduction,
            OtherDeduction = variables.OtherDeduction,
            Note = string.IsNullOrWhiteSpace(variables.Note) ? null : variables.Note.Trim()
        };

        var rates = period.RatesFor(await ParametersForAsync(period.Year, ct));
        if (typed.TotalAbsenceDays > rates.MonthlyWorkingDays)
            return Result.Failure(Error.Validation("Payroll_TooManyAbsenceDays", rates.MonthlyWorkingDays));
        if (variables.OvertimeHours25 + variables.OvertimeHours50 + variables.OvertimeHours100 > 100m)
            return Result.Failure(Error.Validation("Payroll_TooManyOvertimeHours"));

        slip.Recompute(typed, rates, period.LastDay);
        periods.Update(period);

        unitOfWork.AuditReason = $"Paie {period.Month:00}/{period.Year} : variables de {slip.Number} modifiées";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> CloseAsync(Guid periodId, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure(Error.NotFound("Payroll_NotFound"));
        if (period.IsClosed) return Result.Failure(Error.Conflict("Payroll_Closed"));

        var current = await ParametersForAsync(period.Year, ct);
        var rates = period.RatesFor(current);

        // A departure corrected or withdrawn since the last refresh leaves a payslip prorated to
        // the old exit date: the month is not closed on it before it is taken again from the files.
        var changed = await DepartureChangedAsync(period, rates, ct);
        if (changed.Count > 0)
            return Result.Failure(Error.Conflict("Payroll_DepartureChanged", string.Join(", ", changed)));

        // The indemnities paid are those of the final settlements as they now stand: a settlement
        // emptied since (exit corrected or withdrawn) pays nothing, and the leave written off
        // below is the leave the payslip pays.
        await ApplySettlementsAsync(period, rates, ct);

        if (period.Payslips.Any(p => p.Amounts.NetPay < 0))
            return Result.Failure(Error.Validation("Payroll_NegativeNetPay"));

        // The rates the payslips are computed with, kept for every later reprint: the year's, or
        // those of the first closing for a month reopened since.
        var closed = period.Close(clock.Now, currentUser.UserId, current);
        if (closed.IsFailure) return closed;

        // The leave indemnities of the month's departures are paid now: the days leave the
        // account, whether or not the receipt is ever signed.
        await SettlementService.WriteSettledLeaveAsync(period, settlements, employees, ledger, hrQueries, ct);

        periods.Update(period);
        unitOfWork.AuditReason = $"Paie {period.Month:00}/{period.Year} clôturée";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// The payslips whose departure figures (days of the month and of the contract) no longer
    /// match the files, by employee number.
    /// </summary>
    private async Task<IReadOnlyList<string>> DepartureChangedAsync(PayrollPeriod period, PayrollParameters rates,
                                                                    CancellationToken ct)
    {
        var candidates = (await queries.CandidatesAsync(period.SiteId, period.FirstDay, period.LastDay,
                                                        rates.MonthlyWorkingDays, ct))
            .ToDictionary(c => c.EmployeeId);
        return
        [
            .. period.Payslips
                  .Where(p => candidates.TryGetValue(p.EmployeeId, out var c)
                              && (c.MonthWorkingDays != p.Variables.MonthWorkingDays
                                  || c.ContractWorkingDays != p.Variables.ContractWorkingDays))
                  .Select(p => p.Number)
                  .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>Puts on each payslip the indemnities its final settlement now says, as <see cref="FromFiles"/> does.</summary>
    private async Task ApplySettlementsAsync(PayrollPeriod period, PayrollParameters rates, CancellationToken ct)
    {
        var (first, last) = (period.FirstDay, period.LastDay);
        var settled = (await settlements.ListAsync(s => s.ExitDate >= first && s.ExitDate <= last, ct))
            .GroupBy(s => s.EmployeeId)
            .ToDictionary(g => g.Key, g => (Leave: g.Sum(s => s.LeaveIndemnity), Notice: g.Sum(s => s.NoticeIndemnity),
                                            Severance: g.Sum(s => s.SeverancePay)));

        foreach (var slip in period.Payslips)
        {
            var (leave, notice, severance) = settled.GetValueOrDefault(slip.EmployeeId);
            if (slip.Variables.LeaveIndemnity == leave && slip.Variables.NoticeIndemnity == notice
                && slip.Variables.SeverancePay == severance) continue;

            slip.Recompute(slip.Variables with { LeaveIndemnity = leave, NoticeIndemnity = notice, SeverancePay = severance },
                           rates, period.LastDay);
        }
    }

    /// <param name="applyCurrentRates">
    /// Recompute the month with the year's rates as they now stand instead of those it was closed
    /// with, to correct a rate typed wrong (see <see cref="PayrollPeriod.Reopen"/>). Written to the
    /// audit trail.
    /// </param>
    public async Task<Result> ReopenAsync(Guid periodId, string reason, bool applyCurrentRates = false,
                                          CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;
        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure(Error.Validation("Payroll_ReopenReasonRequired"));

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure(Error.NotFound("Payroll_NotFound"));

        var reopened = period.Reopen(applyCurrentRates);
        if (reopened.IsFailure) return reopened;

        if (applyCurrentRates)
        {
            var rates = period.RatesFor(await ParametersForAsync(period.Year, ct));
            foreach (var slip in period.Payslips) slip.Recompute(slip.Variables, rates, period.LastDay);
        }

        periods.Update(period);
        unitOfWork.AuditReason = $"Paie {period.Month:00}/{period.Year} rouverte : {reason.Trim()}"
                                 + (applyCurrentRates ? $" (recalculée aux taux de paie {period.Year} actuels)" : string.Empty);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>The payslips to print: one, or the whole month when <paramref name="payslipId"/> is null.</summary>
    public async Task<Result<IReadOnlyList<PayslipDocument>>> GetDocumentsAsync(Guid periodId, Guid? payslipId,
                                                                               CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PayslipDocument>>(permitted.FirstError);

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure<IReadOnlyList<PayslipDocument>>(Error.NotFound("Payroll_NotFound"));

        // A month prints with the rates it was closed with, not the year's as since changed.
        var rates = period.RatesForPrinting(await ParametersForAsync(period.Year, ct));
        var employer = await queries.EmployerAsync(period.SiteId, ct);

        return Result.Success<IReadOnlyList<PayslipDocument>>(
        [
            .. period.Payslips.Where(p => payslipId is null || p.Id == payslipId)
                  .OrderBy(p => p.FullName, StringComparer.CurrentCulture)
                  .Select(p => new PayslipDocument(employer, period.Year, period.Month, period.Status, p, rates))
        ]);
    }

    // ---- Exports ---------------------------------------------------------------------------------

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private static string Csv(IEnumerable<IEnumerable<object?>> rows)
    {
        static string Cell(object? value) => value switch
        {
            null => string.Empty,
            decimal d => d.ToString("0.00", Fr),
            DateOnly date => date.ToString("dd/MM/yyyy", Fr),
            // Names, CIN and RIB go through the shared cell: these files open straight in Excel.
            _ => Inventory.CsvCell.Of(value.ToString())
        };

        var text = new StringBuilder();
        foreach (var row in rows) text.AppendLine(string.Join(';', row.Select(Cell)));
        return text.ToString();
    }

    /// <summary>Journal de paie: every amount of every payslip, with the month's totals.</summary>
    public async Task<Result<byte[]>> ExportJournalAsync(Guid periodId, CancellationToken ct = default)
    {
        var docs = await GetDocumentsAsync(periodId, null, ct);
        if (docs.IsFailure) return Result.Failure<byte[]>(docs.FirstError);

        var slips = docs.Value.Select(d => d.Payslip).ToList();
        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "Matricule", "Nom", "CIN", "N° CNSS", "Jours payés", "Salaire de base", "Retenue absences",
                "Ancienneté %", "Prime d'ancienneté", "Heures supp.", "Primes imposables", "Indemnités de départ", "Indemnités exonérées",
                "Brut", "Brut imposable", "CNSS salarié", "AMO salarié", "Frais professionnels", "Net imposable",
                "IR", "Avances et retenues", "Net à payer", "CNSS patronale", "Allocations familiales",
                "Formation professionnelle", "AMO patronale", "Coût employeur", "RIB" }
        };

        foreach (var p in slips)
        {
            var a = p.Amounts;
            rows.Add(new object?[] { p.Number, p.FullName, p.Cin, p.CnssNumber, a.PaidDays, p.BaseSalary,
                a.AbsenceDeduction, a.SeniorityRate, a.SeniorityBonus, a.OvertimeAmount, p.Variables.TaxableBonus,
                p.Variables.LeaveIndemnity + p.Variables.NoticeIndemnity + p.Variables.SeverancePay, a.ExemptAllowances, a.Gross, a.GrossTaxable, a.CnssEmployee, a.AmoEmployee, a.ProfessionalExpenses,
                a.NetTaxable, a.IncomeTax, a.OtherDeductions, a.NetPay, a.CnssEmployer, a.FamilyAllowanceEmployer,
                a.TrainingTaxEmployer, a.AmoEmployer, a.EmployerCost, p.Rib });
        }

        decimal Sum(Func<PayslipAmounts, decimal> f) => slips.Sum(p => f(p.Amounts));
        rows.Add(new object?[] { "TOTAL", $"{slips.Count} salariés", null, null, null, slips.Sum(p => p.BaseSalary),
            Sum(a => a.AbsenceDeduction), null, Sum(a => a.SeniorityBonus), Sum(a => a.OvertimeAmount),
            slips.Sum(p => p.Variables.TaxableBonus),
            slips.Sum(p => p.Variables.LeaveIndemnity + p.Variables.NoticeIndemnity + p.Variables.SeverancePay),
            Sum(a => a.ExemptAllowances), Sum(a => a.Gross),
            Sum(a => a.GrossTaxable), Sum(a => a.CnssEmployee), Sum(a => a.AmoEmployee), Sum(a => a.ProfessionalExpenses),
            Sum(a => a.NetTaxable), Sum(a => a.IncomeTax), Sum(a => a.OtherDeductions), Sum(a => a.NetPay),
            Sum(a => a.CnssEmployer), Sum(a => a.FamilyAllowanceEmployer), Sum(a => a.TrainingTaxEmployer),
            Sum(a => a.AmoEmployer), Sum(a => a.EmployerCost), null });

        return Result.Success(Encode(Csv(rows)));
    }

    /// <summary>
    /// What the CNSS declaration (DAMANCOM) needs, per employee and in total: days, gross, capped
    /// base and each contribution, to check the declaration. The file DAMANCOM takes is
    /// <see cref="ExportBdsAsync"/>.
    /// </summary>
    public async Task<Result<byte[]>> ExportCnssAsync(Guid periodId, CancellationToken ct = default)
    {
        var docs = await GetDocumentsAsync(periodId, null, ct);
        if (docs.IsFailure) return Result.Failure<byte[]>(docs.FirstError);

        var contributing = docs.Value.Where(d => d.Payslip.Amounts.Exemption != PayrollExemption.InsertionContract
                                                 && d.Payslip.Amounts.Exemption != PayrollExemption.Internship).ToList();

        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "N° CNSS", "Nom et prénom", "CIN", "Jours déclarés", "Salaire brut", "Base plafonnée",
                "Prestations sociales salarié", "Prestations sociales patronale", "Allocations familiales",
                "Formation professionnelle", "AMO salarié", "AMO patronale", "Total" }
        };

        foreach (var d in contributing)
        {
            var p = d.Payslip; var a = p.Amounts;
            var capped = Math.Min(a.GrossTaxable, d.Parameters.CnssCeiling);
            rows.Add(new object?[] { p.CnssNumber, p.FullName, p.Cin, a.PaidDays, a.GrossTaxable, capped,
                a.CnssEmployee, a.CnssEmployer, a.FamilyAllowanceEmployer, a.TrainingTaxEmployer, a.AmoEmployee,
                a.AmoEmployer, a.CnssEmployee + a.CnssEmployer + a.FamilyAllowanceEmployer + a.TrainingTaxEmployer
                               + a.AmoEmployee + a.AmoEmployer });
        }

        decimal Sum(Func<PayslipAmounts, decimal> f) => contributing.Sum(d => f(d.Payslip.Amounts));
        rows.Add(new object?[] { "TOTAL", $"{contributing.Count} salariés", null, Sum(a => a.PaidDays), Sum(a => a.GrossTaxable),
            contributing.Sum(d => Math.Min(d.Payslip.Amounts.GrossTaxable, d.Parameters.CnssCeiling)),
            Sum(a => a.CnssEmployee), Sum(a => a.CnssEmployer), Sum(a => a.FamilyAllowanceEmployer),
            Sum(a => a.TrainingTaxEmployer), Sum(a => a.AmoEmployee), Sum(a => a.AmoEmployer),
            Sum(a => a.CnssEmployee + a.CnssEmployer + a.FamilyAllowanceEmployer + a.TrainingTaxEmployer
                     + a.AmoEmployee + a.AmoEmployer) });

        return Result.Success(Encode(Csv(rows)));
    }

    /// <summary>
    /// The month's net pay for the bank: who, which account, how much. Only a closed month is
    /// transferred, so the bank never pays an amount that may still change.
    /// </summary>
    public async Task<Result<TransferOrder>> GetTransferOrderAsync(Guid periodId, CancellationToken ct = default)
    {
        var docs = await GetDocumentsAsync(periodId, null, ct);
        if (docs.IsFailure) return Result.Failure<TransferOrder>(docs.FirstError);
        if (docs.Value is []) return Result.Failure<TransferOrder>(Error.Validation("Payroll_Empty"));

        var first = docs.Value[0];
        if (first.Status != PayrollStatus.Closed) return Result.Failure<TransferOrder>(Error.Conflict("Payroll_TransferNotClosed"));

        return Result.Success(new TransferOrder(first.Employer, first.Year, first.Month, clock.Today,
        [
            .. docs.Value.Where(d => d.Payslip.Amounts.NetPay > 0)
                  .Select(d => new TransferLine(d.Payslip.Number, d.Payslip.FullName, d.Payslip.BankName,
                                                string.IsNullOrWhiteSpace(d.Payslip.Rib) ? null : d.Payslip.Rib.Replace(" ", string.Empty),
                                                d.Payslip.Amounts.NetPay))
        ]));
    }

    /// <summary>The transfer list as a file to import into the bank's online service, one line per account.</summary>
    public async Task<Result<byte[]>> ExportTransferAsync(Guid periodId, CancellationToken ct = default)
    {
        var order = await GetTransferOrderAsync(periodId, ct);
        if (order.IsFailure) return Result.Failure<byte[]>(order.FirstError);

        var o = order.Value;
        var reference = $"SALAIRE {o.Month:00}/{o.Year}";
        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "Matricule", "Bénéficiaire", "Banque", "RIB", "Montant", "Motif" }
        };
        rows.AddRange(o.ByTransfer.Select(l => new object?[] { l.Number, l.FullName, l.BankName, l.Rib, l.Amount, reference }));
        rows.Add(new object?[] { "TOTAL", $"{o.ByTransfer.Count()} virements", null, null, o.ByTransfer.Sum(l => l.Amount), null });

        return Result.Success(Encode(Csv(rows)));
    }

    private static byte[] Encode(string csv) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];

    // ---- Declarations ----------------------------------------------------------------------------

    /// <summary>
    /// The month's BDS for DAMANCOM (<see cref="CnssBdsFile"/>), built from the préétabli DAMANCOM
    /// issued for it. Only a closed month is declared.
    /// <para>
    /// Declared: every payslip but an internship's (an intern is not insured; as in
    /// <see cref="ExportCnssAsync"/>). ANAPEC insertion contracts are declared with their pay: the
    /// beneficiary is insured, and the exemption of the contributions is the CNSS's to apply.
    /// The salary is the gross subject to the CNSS, the days those paid (26 at most).
    /// </para>
    /// </summary>
    public async Task<Result<PayrollExportFile>> ExportBdsAsync(Guid periodId, byte[] preestablished,
                                                                CancellationToken ct = default)
    {
        var docs = await GetDocumentsAsync(periodId, null, ct);
        if (docs.IsFailure) return Result.Failure<PayrollExportFile>(docs.FirstError);
        if (docs.Value is []) return Result.Failure<PayrollExportFile>(Error.Validation("Payroll_Empty"));

        var first = docs.Value[0];
        if (first.Status != PayrollStatus.Closed)
            return Result.Failure<PayrollExportFile>(Error.Conflict("Payroll_BdsNotClosed"));

        var file = CnssBdsFile.Parse(preestablished);
        if (file.IsFailure) return Result.Failure<PayrollExportFile>(file.FirstError);
        var matches = CnssBdsFile.Matches(file.Value, first.Employer.CnssAffiliationNumber, first.Year, first.Month);
        if (matches.IsFailure) return Result.Failure<PayrollExportFile>(matches.FirstError);

        var declared = docs.Value.Where(d => d.Payslip.Amounts.Exemption != PayrollExemption.Internship).ToList();

        var missing = declared.Where(d => CnssBdsFile.Digits(d.Payslip.CnssNumber).Length == 0)
                              .Select(d => $"{d.Payslip.Number} {d.Payslip.FullName}").ToList();
        if (missing.Count > 0)
            return Result.Failure<PayrollExportFile>(Error.Validation("Payroll_BdsMissingCnss", string.Join(", ", missing)));

        var invalid = declared.Where(d => CnssBdsFile.Digits(d.Payslip.CnssNumber).Length > 9)
                              .Select(d => $"{d.Payslip.Number} {d.Payslip.FullName} ({d.Payslip.CnssNumber})").ToList();
        if (invalid.Count > 0)
            return Result.Failure<PayrollExportFile>(Error.Validation("Payroll_BdsCnssNumberInvalid", string.Join(", ", invalid)));

        // The BDS has one line per insured number: two payslips under the same number (typed twice
        // by mistake) would lose one salary from the declaration.
        var duplicates = declared.GroupBy(d => long.Parse(CnssBdsFile.Digits(d.Payslip.CnssNumber), CultureInfo.InvariantCulture))
                                 .Where(g => g.Count() > 1)
                                 .OrderBy(g => g.Key)
                                 .Select(g => $"{g.Key:000000000} ({string.Join(", ", g.Select(d => $"{d.Payslip.Number} {d.Payslip.FullName}"))})")
                                 .ToList();
        if (duplicates.Count > 0)
            return Result.Failure<PayrollExportFile>(Error.Validation("Payroll_BdsDuplicateCnss", string.Join("; ", duplicates)));

        var lastDay = new DateOnly(first.Year, first.Month, 1).AddMonths(1).AddDays(-1);
        var paid = declared.Select(d => d.Payslip.EmployeeId).ToList();
        var staff = await employees.ListAsync(e => e.CnssNumber != null || paid.Contains(e.Id), ct);
        var byId = staff.ToDictionary(e => e.Id);

        var salaries = declared.Select(d =>
        {
            var p = d.Payslip;
            var a = p.Amounts;
            return new BdsSalary(long.Parse(CnssBdsFile.Digits(p.CnssNumber), CultureInfo.InvariantCulture), p.FullName, p.Cin,
                                 (int)a.PaidDays, a.GrossTaxable, Math.Min(a.GrossTaxable, d.Parameters.CnssCeiling),
                                 BdsSituationOf(byId.GetValueOrDefault(p.EmployeeId), p, d.Parameters, lastDay));
        }).ToList();

        // An insured person the CNSS expects and the month does not pay has left (SO), or died.
        var absent = new Dictionary<long, BdsSituation>();
        foreach (var e in staff)
            if (e.ExitReason == ExitReason.Death && e.ExitDate <= lastDay
                && long.TryParse(CnssBdsFile.Digits(e.CnssNumber), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                absent[number] = BdsSituation.Deceased;

        var content = CnssBdsFile.Build(file.Value, salaries, absent);
        var expected = file.Value.Insured.Select(i => i.InsuredNumber).ToHashSet();
        var entrants = salaries.Select(s => s.InsuredNumber).Distinct().Count(n => !expected.Contains(n));
        return Result.Success(new PayrollExportFile(
            CnssBdsFile.FileName(file.Value.AffiliateNumber.Trim(), file.Value.Period), content,
            expected.Count + entrants, entrants));
    }

    /// <summary>
    /// The BDS situation of a payslip: SO for a departure in or before the month (DE for a death);
    /// AT or CS for a month spent entirely on a work-accident stoppage or on unpaid leave, with no
    /// day declared. Sick leave is paid here, so its days are declared and MS never arises.
    /// </summary>
    public static BdsSituation BdsSituationOf(Employee? employee, Payslip payslip, PayrollParameters rates, DateOnly lastDay)
    {
        if (employee?.ExitDate is { } exit && exit <= lastDay)
            return employee.ExitReason == ExitReason.Death ? BdsSituation.Deceased : BdsSituation.Left;
        if (payslip.Amounts.PaidDays > 0) return BdsSituation.Normal;
        return payslip.Variables.WorkAccidentDays >= rates.MonthlyWorkingDays ? BdsSituation.WorkAccident
                                                                              : BdsSituation.UnpaidLeave;
    }

    /// <summary>
    /// The year's état 9421 for Simpl-IR (<see cref="SimplIrSalaryDeclaration"/>), from the site's
    /// closed payslips of the year. Refused while a month of the year is still a draft.
    /// </summary>
    public async Task<Result<PayrollExportFile>> ExportSimplIrAsync(int year, SimplIrCodes codes, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<PayrollExportFile>(permitted.FirstError);
        if (year < 2000) return Result.Failure<PayrollExportFile>(Error.Validation("Payroll_PeriodInvalid"));

        var site = currentUser.SiteId;
        var months = (await periods.ListAsync(p => p.SiteId == site && p.Year == year, ct)).OrderBy(p => p.Month).ToList();

        var open = months.Where(p => !p.IsClosed).Select(p => $"{p.Month:00}/{p.Year}").ToList();
        if (open.Count > 0)
            return Result.Failure<PayrollExportFile>(Error.Conflict("Payroll_SimplIrOpenMonths", year, string.Join(", ", open)));
        if (months.Count == 0) return Result.Failure<PayrollExportFile>(Error.Validation("Payroll_SimplIrNothing", year));

        var employer = await queries.EmployerAsync(site, ct);
        if (string.IsNullOrWhiteSpace(employer.FiscalId))
            return Result.Failure<PayrollExportFile>(Error.Validation("Payroll_SimplIrNoFiscalId"));

        var current = await ParametersForAsync(year, ct);
        var slips = new List<(int Month, Payslip Slip, PayrollParameters Rates)>();
        foreach (var month in months)
        {
            var period = await periods.GetByIdWithDetailsAsync(month.Id, ct) ?? month;
            var rates = period.RatesFor(current);
            slips.AddRange(period.Payslips.Select(p => (period.Month, p, rates)));
        }

        var ids = slips.Select(s => s.Slip.EmployeeId).Distinct().ToList();
        var files = (await employees.ListAsync(e => ids.Contains(e.Id), ct)).ToDictionary(e => e.Id);

        var lines = slips.Select(s =>
        {
            var (month, p, rates) = s;
            var e = files.GetValueOrDefault(p.EmployeeId);
            // As PayrollCalculator chose it: the low-income rate while the month's taxable pay,
            // brought to the year, stays within the threshold.
            var high = p.Amounts.GrossTaxable * 12m > rates.ProfessionalExpensesThreshold;
            var address = e is null
                ? null
                : string.Join(", ", new[] { e.Address, e.City }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
            return new SimplIrPayslip(month, p.EmployeeId, p.Number, e?.LastName ?? p.FullName, e?.FirstName ?? string.Empty,
                                      address, p.Cin, p.CnssNumber, e?.FamilyStatus ?? FamilyStatus.Unmarried, p.ContractType,
                                      p.DependentPersons, high, p.Amounts);
        }).ToList();

        return Result.Success(new PayrollExportFile(SimplIrSalaryDeclaration.FileName(employer.FiscalId, year),
                                                    SimplIrSalaryDeclaration.Build(employer, year, lines, codes),
                                                    ids.Count));
    }
}

/// <summary>A declaration file, with the name the administration expects for it.</summary>
/// <param name="Declared">The persons declared.</param>
/// <param name="Entrants">For the BDS, of which not in the CNSS's préétabli.</param>
public sealed record PayrollExportFile(string FileName, byte[] Content, int Declared = 0, int Entrants = 0);

public interface IPayrollQueries
{
    /// <summary>The site's payroll months. A payroll is one site's: an operator with several sites sees the one worked on.</summary>
    Task<IReadOnlyList<PayrollPeriodRow>> GetPeriodsAsync(Guid siteId, CancellationToken ct = default);

    Task<Guid?> FindPeriodAsync(Guid siteId, int year, int month, CancellationToken ct = default);

    /// <summary>
    /// Draft months of the year on every site the operator may see: the rates are the same for all
    /// sites, so a change to them is carried to each site's open month.
    /// </summary>
    Task<IReadOnlyList<Guid>> DraftPeriodIdsAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Staff of the site employed during the month, with their unpaid working days within the
    /// contract (approved unpaid leave, unjustified absences, work-accident stoppages) and, for a
    /// hire or an exit during the month, the working days of the month and of the contract.
    /// </summary>
    Task<IReadOnlyList<PayrollCandidate>> CandidatesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                          decimal monthlyWorkingDays, CancellationToken ct = default);

    Task<InvoiceIssuer> EmployerAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>Final settlements of employees who left during the period, by employee.</summary>
    Task<IReadOnlyDictionary<Guid, SettledAmounts>> SettlementsAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

public sealed record SettledAmounts(decimal LeaveIndemnity, decimal NoticeIndemnity, decimal SeverancePay);
