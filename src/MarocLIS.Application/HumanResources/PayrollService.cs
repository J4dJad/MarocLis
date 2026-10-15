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
public sealed record PayrollCandidate(Guid EmployeeId, decimal UnpaidDays);

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
        return Result.Success(await queries.GetPeriodsAsync(ct));
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
        var all = await parameters.ListAsync(p => p.Year <= year, ct);
        if (all.FirstOrDefault(p => p.Year == year) is { } found) return found;

        var created = new PayrollParameters(year);
        if (all.OrderByDescending(p => p.Year).FirstOrDefault() is { } previous) CopyRates(previous, created);

        await parameters.AddAsync(created, ct);
        unitOfWork.AuditReason = $"Paramètres de paie {year} créés";
        await unitOfWork.SaveChangesAsync(ct);
        return created;
    }

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

    /// <summary>Saves the year's rates. Draft months of that year are recomputed with them.</summary>
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
                foreach (var slip in period.Payslips) slip.Recompute(slip.Variables, current, period.LastDay);
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
        if (await queries.FindPeriodAsync(year, month, ct) is not null)
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
    /// situations updated, absences taken again from leave. The variables already typed are kept.
    /// </summary>
    public async Task<Result<int>> RefreshAsync(Guid periodId, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure<int>(Error.NotFound("Payroll_NotFound"));
        if (period.IsClosed) return Result.Failure<int>(Error.Conflict("Payroll_Closed"));

        var rates = await ParametersForAsync(period.Year, ct);
        var added = await FillAsync(period, rates, ct);

        periods.Update(period);
        unitOfWork.AuditReason = $"Paie {period.Month:00}/{period.Year} actualisée depuis les dossiers";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(added);
    }

    private async Task<int> FillAsync(PayrollPeriod period, PayrollParameters rates, CancellationToken ct)
    {
        var added = 0;
        var settlements = await queries.SettlementsAsync(period.FirstDay, period.LastDay, ct);
        foreach (var candidate in await queries.CandidatesAsync(period.FirstDay, period.LastDay, rates.MonthlyWorkingDays, ct))
        {
            var employee = await employees.GetByIdAsync(candidate.EmployeeId, ct);
            if (employee?.BaseSalary is null || !PayrollCalculator.IsOnPayroll(employee.ContractType)) continue;

            var slip = period.Payslips.FirstOrDefault(p => p.EmployeeId == employee.Id);
            var isNew = slip is null;
            slip ??= new Payslip(employee.Id);

            slip.Number = employee.Number;
            slip.FullName = employee.FullName;
            slip.Cin = employee.Cin;
            slip.CnssNumber = employee.CnssNumber;
            slip.JobTitle = employee.JobTitle;
            slip.HireDate = employee.HireDate;
            slip.ContractType = employee.ContractType;
            slip.DependentPersons = DependentPersons(employee);
            slip.BaseSalary = employee.BaseSalary.Value;
            slip.Rib = employee.Rib;
            slip.BankName = employee.BankName;

            var variables = isNew
                ? new PayslipVariables { AbsenceDays = candidate.UnpaidDays }
                : slip.Variables with { AbsenceDays = candidate.UnpaidDays };

            // What the final settlement says is owed on departure, as it now stands.
            var settled = settlements.GetValueOrDefault(employee.Id);
            variables = variables with
            {
                LeaveIndemnity = settled?.LeaveIndemnity ?? 0m,
                NoticeIndemnity = settled?.NoticeIndemnity ?? 0m,
                SeverancePay = settled?.SeverancePay ?? 0m
            };
            slip.Recompute(variables, rates, period.LastDay);

            if (isNew)
            {
                period.Add(slip);
                added++;
            }
        }

        return added;
    }

    /// <summary>Spouse when married, plus the dependent children: the persons the income-tax deduction counts.</summary>
    public static int DependentPersons(Employee employee) =>
        (employee.FamilyStatus == FamilyStatus.Married ? 1 : 0) + Math.Max(0, employee.DependentChildren);

    public async Task<Result<PayrollPeriodDetail>> GetPeriodAsync(Guid periodId, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<PayrollPeriodDetail>(permitted.FirstError);

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure<PayrollPeriodDetail>(Error.NotFound("Payroll_NotFound"));

        var rates = await ParametersForAsync(period.Year, ct);
        var row = (await queries.GetPeriodsAsync(ct)).First(p => p.PeriodId == periodId);

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
        if (PayrollCalculator.ExemptionFor(p.ContractType) is PayrollExemption.None or PayrollExemption.Tahfiz)
        {
            if (p.BaseSalary < rates.SmigMonthly) warnings.Add(PayslipWarning.BelowMinimumWage);
            if (string.IsNullOrWhiteSpace(p.CnssNumber)) warnings.Add(PayslipWarning.NoCnssNumber);
        }
        if (string.IsNullOrWhiteSpace(p.Rib)) warnings.Add(PayslipWarning.NoRib);
        if (p.Amounts.NetPay < 0) warnings.Add(PayslipWarning.NegativeNet);
        return warnings;
    }

    public async Task<Result> UpdateVariablesAsync(Guid periodId, Guid payslipId, PayslipVariables variables,
                                                   CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        decimal[] values = [variables.AbsenceDays, variables.OvertimeHours25, variables.OvertimeHours50,
                            variables.OvertimeHours100, variables.TaxableBonus, variables.TransportAllowance,
                            variables.OtherExemptAllowance, variables.AdvanceDeduction, variables.OtherDeduction,
                            variables.LeaveIndemnity, variables.NoticeIndemnity, variables.SeverancePay];
        if (values.Any(v => v < 0)) return Result.Failure(Error.Validation("Payroll_NegativeVariable"));

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure(Error.NotFound("Payroll_NotFound"));
        if (period.IsClosed) return Result.Failure(Error.Conflict("Payroll_Closed"));

        var slip = period.Payslips.FirstOrDefault(p => p.Id == payslipId);
        if (slip is null) return Result.Failure(Error.NotFound("Payroll_PayslipNotFound"));

        var rates = await ParametersForAsync(period.Year, ct);
        if (variables.AbsenceDays > rates.MonthlyWorkingDays)
            return Result.Failure(Error.Validation("Payroll_TooManyAbsenceDays", rates.MonthlyWorkingDays));
        if (variables.OvertimeHours25 + variables.OvertimeHours50 + variables.OvertimeHours100 > 100m)
            return Result.Failure(Error.Validation("Payroll_TooManyOvertimeHours"));

        slip.Recompute(variables with { Note = string.IsNullOrWhiteSpace(variables.Note) ? null : variables.Note.Trim() },
                       rates, period.LastDay);
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

        if (period.Payslips.Any(p => p.Amounts.NetPay < 0))
            return Result.Failure(Error.Validation("Payroll_NegativeNetPay"));

        var closed = period.Close(clock.Now, currentUser.UserId);
        if (closed.IsFailure) return closed;

        periods.Update(period);
        unitOfWork.AuditReason = $"Paie {period.Month:00}/{period.Year} clôturée";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ReopenAsync(Guid periodId, string reason, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;
        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure(Error.Validation("Payroll_ReopenReasonRequired"));

        var period = await periods.GetByIdWithDetailsAsync(periodId, ct);
        if (period is null) return Result.Failure(Error.NotFound("Payroll_NotFound"));

        var reopened = period.Reopen();
        if (reopened.IsFailure) return reopened;

        periods.Update(period);
        unitOfWork.AuditReason = $"Paie {period.Month:00}/{period.Year} rouverte : {reason.Trim()}";
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

        var rates = await ParametersForAsync(period.Year, ct);
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
            _ => Quote(value.ToString() ?? string.Empty)
        };

        static string Quote(string s) =>
            s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

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
    /// base and each contribution. Typed or imported into DAMANCOM by hand; the e-BDS file format
    /// itself is not produced.
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
}

public interface IPayrollQueries
{
    Task<IReadOnlyList<PayrollPeriodRow>> GetPeriodsAsync(CancellationToken ct = default);

    Task<Guid?> FindPeriodAsync(int year, int month, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> DraftPeriodIdsAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Staff of the current site employed during the month, with their unpaid working days: approved
    /// unpaid leave and unjustified absences, and the days before hire or after departure.
    /// </summary>
    Task<IReadOnlyList<PayrollCandidate>> CandidatesAsync(DateOnly from, DateOnly to, decimal monthlyWorkingDays,
                                                          CancellationToken ct = default);

    Task<InvoiceIssuer> EmployerAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>Final settlements of employees who left during the period, by employee.</summary>
    Task<IReadOnlyDictionary<Guid, SettledAmounts>> SettlementsAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

public sealed record SettledAmounts(decimal LeaveIndemnity, decimal NoticeIndemnity, decimal SeverancePay);
