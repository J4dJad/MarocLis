using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.HumanResources;

public enum HrDocumentKind
{
    /// <summary>Attestation de travail: the employee works here, since when, as what.</summary>
    EmploymentAttestation = 0,
    /// <summary>Attestation de salaire: the same, with the salary and the last months paid.</summary>
    SalaryAttestation = 1,
    /// <summary>Certificat de travail (art. 72): delivered on departure, dates and posts only.</summary>
    WorkCertificate = 2,
    /// <summary>Reçu pour solde de tout compte (art. 73 to 76).</summary>
    SettlementReceipt = 3
}

/// <summary>The employee as an HR document presents them.</summary>
public sealed record HrDocumentEmployee(string Number, string FullName, Gender Gender, DateOnly? BirthDate, string? Cin,
                                        string? CnssNumber, EmployeeJob Job, string? JobTitle, ContractType ContractType,
                                        DateOnly HireDate, DateOnly? ExitDate);

public sealed record SalaryMonth(int Year, int Month, decimal Gross, decimal NetPay);

/// <summary>What the receipt lists: the indemnities, and the final payslip they were paid with.</summary>
public sealed record SettlementReceiptFacts(
    DateOnly ExitDate,
    decimal LeaveDays,
    decimal LeaveIndemnity,
    decimal NoticeIndemnity,
    decimal SeverancePay,
    SalaryMonth? FinalPayslip,
    decimal FinalPayslipDeductions,
    DateOnly? SignedOn);

public sealed record HrDocument(
    HrDocumentKind Kind,
    InvoiceIssuer Employer,
    HrDocumentEmployee Employee,
    DateOnly IssuedOn,
    string IssuedBy,
    decimal? BaseSalary,
    IReadOnlyList<SalaryMonth> SalaryMonths,
    SettlementReceiptFacts? Settlement);

public sealed record TransferLine(string Number, string FullName, string? BankName, string? Rib, decimal Amount);

/// <summary>The month's net pay, as the bank is asked to transfer it.</summary>
public sealed record TransferOrder(InvoiceIssuer Employer, int Year, int Month, DateOnly IssuedOn,
                                   IReadOnlyList<TransferLine> Lines)
{
    public IEnumerable<TransferLine> ByTransfer => Lines.Where(l => !string.IsNullOrWhiteSpace(l.Rib));
    public IEnumerable<TransferLine> OtherwisePaid => Lines.Where(l => string.IsNullOrWhiteSpace(l.Rib));
}

public interface IHrDocumentRenderer
{
    byte[] Render(HrDocument document);

    byte[] RenderTransferOrder(TransferOrder order);
}

/// <summary>Read side of HR documents and departures.</summary>
public interface IHrDocumentQueries
{
    /// <summary>Closed payslips of the employee, latest first.</summary>
    Task<IReadOnlyList<SalaryMonth>> ClosedSalaryMonthsAsync(Guid employeeId, int count, CancellationToken ct = default);

    /// <summary>
    /// Gross taxable of the employee's full months before <paramref name="before"/>, departure
    /// indemnities left out, latest first: the basis of the reference salary.
    /// </summary>
    Task<IReadOnlyList<decimal>> FullMonthGrossAsync(Guid employeeId, DateOnly before, decimal fullDays, int count,
                                                     CancellationToken ct = default);

    /// <summary>
    /// Paid-leave days left on the day of departure, the months not yet credited included, before
    /// the debit of the settlement of that departure.
    /// </summary>
    Task<decimal> LeaveBalanceAtExitAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>
    /// The monthly accruals up to the departure not credited yet: counted in
    /// <see cref="LeaveBalanceAtExitAsync"/>, written to the account with the leave the settlement
    /// pays (<see cref="SettlementService.WriteSettledLeaveAsync"/>).
    /// </summary>
    Task<IReadOnlyList<LeaveLedgerEntry>> ExitAccrualsDueAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>The employee's payslip for a month, and the month's status, when the month is open.</summary>
    Task<(Guid PeriodId, PayrollStatus Status, Payslip? Payslip)?> PayslipOfMonthAsync(Guid employeeId, int year, int month,
                                                                                        CancellationToken ct = default);
}

/// <summary>
/// The attestations HR is asked for every week, and the work certificate due on departure.
/// <para>
/// The attestation de travail and the certificate state no salary and need the personnel right; the
/// attestation de salaire needs the salary right.
/// </para>
/// </summary>
public sealed class HrDocumentService(
    IRepository<Employee> employees,
    IHrDocumentQueries queries,
    IPayrollQueries payrollQueries,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int SalaryAttestationMonths = 3;

    public async Task<Result<HrDocument>> GetAsync(HrDocumentKind kind, Guid employeeId, CancellationToken ct = default)
    {
        var permitted = kind switch
        {
            HrDocumentKind.SalaryAttestation or HrDocumentKind.SettlementReceipt => guard.Require(Permissions.HumanResources.Salary),
            _ => guard.Require(Permissions.HumanResources.Manage)
        };
        if (permitted.IsFailure) return Result.Failure<HrDocument>(permitted.FirstError);

        if (kind == HrDocumentKind.SettlementReceipt)
            return Result.Failure<HrDocument>(Error.Validation("HrDocument_UseSettlement"));

        var employee = await employees.GetByIdAsync(employeeId, ct);
        if (employee is null) return Result.Failure<HrDocument>(Error.NotFound("Employee_NotFound"));

        switch (kind)
        {
            case HrDocumentKind.EmploymentAttestation when !employee.IsActiveOn(clock.Today):
                return Result.Failure<HrDocument>(Error.Conflict("HrDocument_EmployeeExited"));
            case HrDocumentKind.WorkCertificate when !employee.HasDeparture:
                return Result.Failure<HrDocument>(Error.Conflict("HrDocument_EmployeeNotExited"));
            case HrDocumentKind.SalaryAttestation when employee.BaseSalary is null:
                return Result.Failure<HrDocument>(Error.Validation("HrDocument_NoSalary"));
        }

        var months = kind == HrDocumentKind.SalaryAttestation
            ? await queries.ClosedSalaryMonthsAsync(employeeId, SalaryAttestationMonths, ct)
            : [];

        return Result.Success(new HrDocument(kind, await payrollQueries.EmployerAsync(employee.SiteId, ct),
                                             Describe(employee), clock.Today, currentUser.FullName,
                                             kind == HrDocumentKind.SalaryAttestation ? employee.BaseSalary : null,
                                             months, null));
    }

    public static HrDocumentEmployee Describe(Employee e) =>
        new(e.Number, e.FullName, e.Gender, e.BirthDate, e.Cin, e.CnssNumber, e.Job, e.JobTitle, e.ContractType,
            e.HireDate, e.ExitDate);
}

/// <summary>Where the settlement's amounts went on the payroll of the month of departure.</summary>
public enum SettlementPayroll
{
    /// <summary>The month is not open yet, or the employee is not on it: opening or refreshing it will carry them.</summary>
    NotOpened = 0,
    /// <summary>The draft payslip was recomputed with them.</summary>
    Applied = 1,
    /// <summary>The month is closed: it must be reopened for the payslip to change.</summary>
    Closed = 2
}

public sealed record SettlementDetail(
    Guid EmployeeId,
    string Number,
    string FullName,
    EmployeeJob Job,
    DateOnly HireDate,
    DateOnly ExitDate,
    ExitReason ExitReason,
    decimal ServiceYears,
    StaffCategory Category,
    NoticePeriod Notice,
    bool NoticeApplies,
    bool SeveranceDue,
    decimal SeveranceHours,
    decimal BaseSalary,
    /// <summary>Full months the reference salary was averaged over; 0 when it is the current salary.</summary>
    int ReferenceMonths,
    decimal MonthlyHours,
    decimal MonthlyWorkingDays,
    SettlementAmounts Suggested,
    SettlementAmounts? Saved,
    DateOnly? SignedOn,
    SettlementPayroll Payroll);

/// <summary>
/// The final settlement of an employee who has left. Requires the salary right.
/// <para>
/// The service proposes the amounts from the law and the file (service, leave balance, reference
/// salary); HR may change them, for instance where a collective agreement gives more. Saving
/// carries them onto the payslip of the month of departure while that month is a draft.
/// </para>
/// </summary>
public sealed class SettlementService(
    IRepository<FinalSettlement> settlements,
    IRepository<Employee> employees,
    IRepository<LeaveLedgerEntry> ledger,
    IRepository<PayrollPeriod> periods,
    IRepository<PayrollParameters> parameters,
    IHrDocumentQueries queries,
    IPayrollQueries payrollQueries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>The reference salary is the average of up to twelve full months (art. 55: the last 52 weeks).</summary>
    public const int ReferenceMonthCount = 12;

    private async Task<PayrollParameters> RatesAsync(int year, CancellationToken ct)
    {
        var all = await parameters.ListAsync(p => p.Year <= year, ct);
        return all.OrderByDescending(p => p.Year).FirstOrDefault() ?? new PayrollParameters(year);
    }

    private async Task<FinalSettlement?> FindAsync(Guid employeeId, DateOnly exit, CancellationToken ct) =>
        (await settlements.ListAsync(s => s.EmployeeId == employeeId && s.ExitDate == exit, ct)).FirstOrDefault();

    public async Task<Result<SettlementDetail>> GetAsync(Guid employeeId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Salary);
        if (permitted.IsFailure) return Result.Failure<SettlementDetail>(permitted.FirstError);

        var employee = await employees.GetByIdAsync(employeeId, ct);
        if (employee is null) return Result.Failure<SettlementDetail>(Error.NotFound("Employee_NotFound"));
        if (employee.ExitDate is not { } exit || employee.ExitReason is not { } reason)
            return Result.Failure<SettlementDetail>(Error.Conflict("HrDocument_EmployeeNotExited"));
        if (employee.BaseSalary is not { } baseSalary || !PayrollCalculator.IsOnPayroll(employee.ContractType))
            return Result.Failure<SettlementDetail>(Error.Validation("HrDocument_NoSalary"));

        var rates = await RatesAsync(exit.Year, ct);
        var months = await queries.FullMonthGrossAsync(employeeId, new DateOnly(exit.Year, exit.Month, 1),
                                                       rates.MonthlyWorkingDays, ReferenceMonthCount, ct);
        var reference = months.Count > 0
            ? Math.Round(months.Average(), 2)
            : Math.Round(baseSalary * (1m + PayrollCalculator.SeniorityRate(employee.HireDate, exit) / 100m), 2);

        var years = SettlementCalculator.ServiceYears(employee.HireDate, exit);
        var category = SettlementCalculator.CategoryOf(employee.Job);
        var notice = SettlementCalculator.Notice(category, years);
        var severanceDue = SettlementCalculator.SeveranceDue(reason, employee.HireDate, exit);
        var leaveDays = Math.Max(0m, await queries.LeaveBalanceAtExitAsync(employeeId, ct));

        var suggested = new SettlementAmounts(
            reference, leaveDays,
            SettlementCalculator.LeaveIndemnity(leaveDays, reference, rates.MonthlyWorkingDays),
            false, 0m,
            severanceDue ? SettlementCalculator.Severance(years, reference, rates.MonthlyHours) : 0m,
            null);

        var saved = await FindAsync(employeeId, exit, ct);
        var month = await queries.PayslipOfMonthAsync(employeeId, exit.Year, exit.Month, ct);

        return Result.Success(new SettlementDetail(
            employee.Id, employee.Number, employee.FullName, employee.Job, employee.HireDate, exit, reason, years,
            category, notice, SettlementCalculator.NoticeApplies(reason), severanceDue,
            SettlementCalculator.SeveranceHours(years), baseSalary, months.Count, rates.MonthlyHours,
            rates.MonthlyWorkingDays, suggested,
            saved is null ? null : new SettlementAmounts(saved.ReferenceSalary, saved.LeaveDays, saved.LeaveIndemnity,
                                                         saved.NoticeNotWorked, saved.NoticeIndemnity, saved.SeverancePay,
                                                         saved.Note),
            saved?.SignedOn,
            month switch
            {
                null or { Payslip: null } => SettlementPayroll.NotOpened,
                { Status: PayrollStatus.Closed } => SettlementPayroll.Closed,
                _ => SettlementPayroll.Applied
            }));
    }

    public async Task<Result<SettlementPayroll>> SaveAsync(Guid employeeId, SettlementAmounts amounts,
                                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Salary);
        if (permitted.IsFailure) return Result.Failure<SettlementPayroll>(permitted.FirstError);

        var employee = await employees.GetByIdAsync(employeeId, ct);
        if (employee is null) return Result.Failure<SettlementPayroll>(Error.NotFound("Employee_NotFound"));
        if (employee.ExitDate is not { } exit || employee.ExitReason is not { } reason)
            return Result.Failure<SettlementPayroll>(Error.Conflict("HrDocument_EmployeeNotExited"));

        var settlement = await FindAsync(employeeId, exit, ct);
        var isNew = settlement is null;
        settlement ??= new FinalSettlement(employeeId, employee.SiteId, exit, reason);

        var updated = settlement.Update(amounts);
        if (updated.IsFailure) return Result.Failure<SettlementPayroll>(updated.FirstError);

        if (isNew) await settlements.AddAsync(settlement, ct);
        else settlements.Update(settlement);

        var outcome = SettlementPayroll.NotOpened;
        if (await queries.PayslipOfMonthAsync(employeeId, exit.Year, exit.Month, ct) is { Payslip: not null } month)
        {
            if (month.Status == PayrollStatus.Closed) outcome = SettlementPayroll.Closed;
            else if (await periods.GetByIdWithDetailsAsync(month.PeriodId, ct) is { } period
                     && period.Payslips.FirstOrDefault(p => p.EmployeeId == employeeId) is { } slip)
            {
                slip.Recompute(slip.Variables with
                {
                    LeaveIndemnity = settlement.LeaveIndemnity,
                    NoticeIndemnity = settlement.NoticeIndemnity,
                    SeverancePay = settlement.SeverancePay
                }, period.RatesFor(await RatesAsync(period.Year, ct)), period.LastDay);
                periods.Update(period);
                outcome = SettlementPayroll.Applied;
            }
        }

        unitOfWork.AuditReason = $"Solde de tout compte de {employee.Number} enregistré";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(outcome);
    }

    /// <summary>
    /// Records that the employee signed the receipt. From then on the settlement is final. The
    /// leave it paid leaves the account here too, if the closing of the month of departure has
    /// not already taken it off (see <see cref="WriteSettledLeaveAsync"/>).
    /// </summary>
    public async Task<Result> SignAsync(Guid employeeId, DateOnly on, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Salary);
        if (permitted.IsFailure) return permitted;

        var employee = await employees.GetByIdAsync(employeeId, ct);
        if (employee?.ExitDate is not { } exit) return Result.Failure(Error.Conflict("HrDocument_EmployeeNotExited"));
        if (on > clock.Today) return Result.Failure(Error.Validation("Settlement_SignedInFuture"));

        var settlement = await FindAsync(employeeId, exit, ct);
        if (settlement is null) return Result.Failure(Error.NotFound("Settlement_NotFound"));

        var signed = settlement.Sign(on);
        if (signed.IsFailure) return signed;

        settlements.Update(settlement);

        await WriteSettledLeaveAsync(employee, settlement, ledger, queries, ct);

        unitOfWork.AuditReason = $"Reçu pour solde de tout compte de {employee.Number} signé le {on:dd/MM/yyyy}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>The reference of the debit for the leave paid on a departure: one per departure.</summary>
    public static string SettledLeaveReference(DateOnly exit) => $"SOLDE-{exit:yyyyMMdd}";

    /// <summary>
    /// Takes the leave a settlement pays off the account, whatever happens to the receipt: when
    /// the month of departure is closed (the indemnity is then paid), when the receipt is signed,
    /// and at the latest when the employee is re-hired, before the departure is cleared from the
    /// file. Safe to run again: the months already credited are not credited twice, and the debit
    /// is brought to what the settlement says, a later change of it written as a correction.
    /// </summary>
    /// <param name="employee">The employee as before any re-hire: the accruals due are those up to the departure.</param>
    public static async Task WriteSettledLeaveAsync(Employee employee, FinalSettlement settlement,
                                                    IRepository<LeaveLedgerEntry> ledger, IHrDocumentQueries queries,
                                                    CancellationToken ct)
    {
        var accrualsDue = employee.ExitDate == settlement.ExitDate
            ? await queries.ExitAccrualsDueAsync(employee.Id, ct)
            : [];

        var reference = SettledLeaveReference(settlement.ExitDate);
        var written = (await ledger.ListAsync(l => l.EmployeeId == employee.Id && l.Reference == reference, ct))
            .Sum(l => l.Days);

        foreach (var entry in SettledLeaveEntries(settlement, accrualsDue, written))
            await ledger.AddAsync(entry, ct);
    }

    /// <summary>
    /// What the settlement writes to the leave account: the accruals counted at the departure but
    /// not credited yet, then the days paid as an indemnity taken off, less what
    /// <paramref name="alreadyDebited"/> (negative) took off before.
    /// </summary>
    public static IReadOnlyList<LeaveLedgerEntry> SettledLeaveEntries(FinalSettlement settlement,
                                                                    IReadOnlyList<LeaveLedgerEntry> accrualsDue,
                                                                    decimal alreadyDebited = 0m)
    {
        var entries = new List<LeaveLedgerEntry>(accrualsDue);
        var change = -Math.Max(0m, settlement.LeaveDays) - alreadyDebited;
        if (change != 0m)
            entries.Add(new LeaveLedgerEntry(settlement.EmployeeId, settlement.ExitDate, LeaveLedgerKind.Adjustment,
                                             change, SettledLeaveReference(settlement.ExitDate),
                                             alreadyDebited == 0m
                                                 ? $"Congés payés par l'indemnité compensatrice du solde de tout compte " +
                                                   $"(sortie le {settlement.ExitDate:dd/MM/yyyy})"
                                                 : $"Correction des congés payés par le solde de tout compte " +
                                                   $"(sortie le {settlement.ExitDate:dd/MM/yyyy}) : {settlement.LeaveDays:0.##} j"));
        return entries;
    }

    /// <summary>
    /// Takes the leave paid off the account for every settlement paid with a month being closed:
    /// those of the period's employees whose departure falls in it.
    /// </summary>
    public static async Task WriteSettledLeaveAsync(PayrollPeriod period, IRepository<FinalSettlement> settlements,
                                                    IRepository<Employee> employees, IRepository<LeaveLedgerEntry> ledger,
                                                    IHrDocumentQueries queries, CancellationToken ct)
    {
        var (first, last) = (period.FirstDay, period.LastDay);
        var paid = period.Payslips.Select(p => p.EmployeeId).ToHashSet();
        foreach (var settlement in await settlements.ListAsync(s => s.ExitDate >= first && s.ExitDate <= last, ct))
            if (paid.Contains(settlement.EmployeeId) && await employees.GetByIdAsync(settlement.EmployeeId, ct) is { } employee)
                await WriteSettledLeaveAsync(employee, settlement, ledger, queries, ct);
    }

    public async Task<Result<HrDocument>> ReceiptAsync(Guid employeeId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.HumanResources.Salary);
        if (permitted.IsFailure) return Result.Failure<HrDocument>(permitted.FirstError);

        var employee = await employees.GetByIdAsync(employeeId, ct);
        if (employee?.ExitDate is not { } exit) return Result.Failure<HrDocument>(Error.Conflict("HrDocument_EmployeeNotExited"));

        var settlement = await FindAsync(employeeId, exit, ct);
        if (settlement is null) return Result.Failure<HrDocument>(Error.NotFound("Settlement_NotFound"));

        SalaryMonth? final = null;
        var deductions = 0m;
        if (await queries.PayslipOfMonthAsync(employeeId, exit.Year, exit.Month, ct) is { Payslip: { } slip })
        {
            final = new SalaryMonth(exit.Year, exit.Month, slip.Amounts.Gross, slip.Amounts.NetPay);
            deductions = slip.Amounts.Gross - slip.Amounts.NetPay;
        }

        return Result.Success(new HrDocument(
            HrDocumentKind.SettlementReceipt, await payrollQueries.EmployerAsync(employee.SiteId, ct),
            HrDocumentService.Describe(employee), clock.Today, currentUser.FullName, employee.BaseSalary, [],
            new SettlementReceiptFacts(exit, settlement.LeaveDays, settlement.LeaveIndemnity, settlement.NoticeIndemnity,
                                       settlement.SeverancePay, final, deductions, settlement.SignedOn)));
    }
}
