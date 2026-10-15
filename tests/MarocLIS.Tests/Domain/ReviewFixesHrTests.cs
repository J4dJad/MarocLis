using System.Linq.Expressions;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Payroll refresh and manual absences, leave paid by the final settlement, departures recorded
/// ahead, rates kept with a closed month, prorated hires and exits, accrual rounding, the
/// work-accident allowance and the cancelling of HR-recorded absences.
/// </summary>
public sealed class ReviewFixesHrTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly Guid Site = Guid.NewGuid();

    private static PayslipAmounts Pay(decimal baseSalary, PayslipVariables variables) =>
        PayrollCalculator.Compute(new PayslipInput(baseSalary, new DateOnly(2026, 1, 5), new DateOnly(2026, 10, 31),
                                                   ContractType.Permanent, 0, variables), new PayrollParameters(2026));

    private static Employee Technician(string number = "M0001", decimal? salary = 5200m) =>
        new(number, "ALAMI", "Sara", EmployeeJob.Technician, new DateOnly(2026, 1, 5), Site) { BaseSalary = salary };

    // ---- R2 ------------------------------------------------------------------------------------

    [Fact]
    public void R2_Unpaid_days_typed_by_hand_are_added_to_those_from_the_leave()
    {
        var both = Pay(5200m, new PayslipVariables { AbsenceDays = 2m, ManualAbsenceDays = 1m });

        both.AbsenceDeduction.Should().Be(600m);   // 3 × 200
        both.PaidDays.Should().Be(23m);
    }

    [Fact]
    public void R2_Taking_the_files_again_keeps_what_HR_typed()
    {
        var typed = new PayslipVariables { AbsenceDays = 5m, ManualAbsenceDays = 1.5m, TaxableBonus = 300m, Note = "Astreinte" };

        var refreshed = PayrollService.FromFiles(typed, new PayrollCandidate(Guid.NewGuid(), 2m), null, 5200m);

        refreshed.AbsenceDays.Should().Be(2m, "the leave-derived figure is taken again");
        refreshed.ManualAbsenceDays.Should().Be(1.5m);
        refreshed.TaxableBonus.Should().Be(300m);
        refreshed.Note.Should().Be("Astreinte");
    }

    [Fact]
    public async Task R2_Refresh_keeps_manual_absences_and_takes_off_those_no_longer_on_the_payroll()
    {
        var stays = Technician("M0001");
        var left = Technician("M0002");
        var period = new PayrollPeriod(Site, 2026, 10);
        var rates = new PayrollParameters(2026);
        foreach (var e in new[] { stays, left })
        {
            var slip = new Payslip(e.Id) { BaseSalary = 5200m };
            slip.Recompute(new PayslipVariables { AbsenceDays = 4m, ManualAbsenceDays = 1m }, rates, period.LastDay);
            period.Add(slip);
        }

        var queries = new FakePayrollQueries { Candidates = [new PayrollCandidate(stays.Id, 2m)] };
        var service = Payroll(queries, [period], [stays, left], [rates]);

        var refreshed = await service.RefreshAsync(period.Id);

        refreshed.IsSuccess.Should().BeTrue();
        period.Payslips.Should().ContainSingle().Which.EmployeeId.Should().Be(stays.Id);
        var kept = period.Payslips.Single();
        kept.Variables.AbsenceDays.Should().Be(2m);
        kept.Variables.ManualAbsenceDays.Should().Be(1m);
        kept.Amounts.AbsenceDeduction.Should().Be(600m);
    }

    [Fact]
    public async Task R2_Typed_variables_change_the_manual_days_only()
    {
        var e = Technician();
        var period = new PayrollPeriod(Site, 2026, 10);
        var rates = new PayrollParameters(2026);
        var slip = new Payslip(e.Id) { BaseSalary = 5200m };
        slip.Recompute(new PayslipVariables { AbsenceDays = 2m, WorkAccidentDays = 2m, WorkAccidentAllowance = 100m },
                       rates, period.LastDay);
        period.Add(slip);
        var service = Payroll(new FakePayrollQueries(), [period], [e], [rates]);

        // The dialog sends the variables back, here with a stale leave figure.
        var sent = slip.Variables with { AbsenceDays = 0m, ManualAbsenceDays = 3m, WorkAccidentAllowance = 0m };
        (await service.UpdateVariablesAsync(period.Id, slip.Id, sent)).IsSuccess.Should().BeTrue();

        slip.Variables.AbsenceDays.Should().Be(2m);
        slip.Variables.WorkAccidentAllowance.Should().Be(100m);
        slip.Variables.ManualAbsenceDays.Should().Be(3m);
        slip.Amounts.AbsenceDeduction.Should().Be(1000m);

        (await service.UpdateVariablesAsync(period.Id, slip.Id, sent with { ManualAbsenceDays = 25m }))
            .FirstError.ResourceKey.Should().Be("Payroll_TooManyAbsenceDays");
    }

    // ---- R3 ------------------------------------------------------------------------------------

    [Fact]
    public async Task R3_Signing_the_settlement_takes_the_paid_leave_off_the_account_once()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 9, 20), ExitReason.Resignation, null);
        var ledger = new InMemoryRepository<LeaveLedgerEntry>();
        ledger.Items.Add(new LeaveLedgerEntry(e.Id, new DateOnly(2026, 8, 31), LeaveLedgerKind.Accrual, 10m, "ACQ-2026-08"));

        // September was not credited when the employee left: the settlement counted it.
        var september = new LeaveLedgerEntry(e.Id, new DateOnly(2026, 9, 30), LeaveLedgerKind.Accrual, 1.5m, "ACQ-2026-09");
        var settlement = new FinalSettlement(e.Id, Site, e.ExitDate!.Value, ExitReason.Resignation);
        settlement.Update(new SettlementAmounts(5200m, 11.5m, 2300m, false, 0m, 0m, null));
        var settlements = new InMemoryRepository<FinalSettlement>();
        settlements.Items.Add(settlement);

        var service = new SettlementService(settlements, Repo(e), ledger, new InMemoryRepository<PayrollPeriod>(),
                                            new InMemoryRepository<PayrollParameters>(),
                                            new FakeHrDocumentQueries { AccrualsDue = [september] },
                                            new FakePayrollQueries(), new FakeUnitOfWork(), new AllowAll(),
                                            new FakeUser(HrManager), new FixedClock());

        (await service.SignAsync(e.Id, Today)).IsSuccess.Should().BeTrue();

        ledger.Items.Should().Contain(september);
        var debit = ledger.Items.Single(l => l.Reference == SettlementService.SettledLeaveReference(e.ExitDate!.Value));
        debit.Days.Should().Be(-11.5m);
        debit.Kind.Should().Be(LeaveLedgerKind.Adjustment);
        debit.Comment.Should().Contain("solde de tout compte");
        ledger.Items.Sum(l => l.Days).Should().Be(0m, "a re-hired employee does not find the paid days again");

        // Signed twice: refused, nothing written again.
        (await service.SignAsync(e.Id, Today)).IsFailure.Should().BeTrue();
        ledger.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task R3_A_debit_already_written_for_the_departure_is_not_written_again()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 9, 20), ExitReason.Resignation, null);
        var ledger = new InMemoryRepository<LeaveLedgerEntry>();
        ledger.Items.Add(new LeaveLedgerEntry(e.Id, e.ExitDate!.Value, LeaveLedgerKind.Adjustment, -4m,
                                              SettlementService.SettledLeaveReference(e.ExitDate!.Value)));
        var settlement = new FinalSettlement(e.Id, Site, e.ExitDate!.Value, ExitReason.Resignation);
        settlement.Update(new SettlementAmounts(5200m, 4m, 800m, false, 0m, 0m, null));
        var settlements = new InMemoryRepository<FinalSettlement>();
        settlements.Items.Add(settlement);

        var service = new SettlementService(settlements, Repo(e), ledger, new InMemoryRepository<PayrollPeriod>(),
                                            new InMemoryRepository<PayrollParameters>(), new FakeHrDocumentQueries(),
                                            new FakePayrollQueries(), new FakeUnitOfWork(), new AllowAll(),
                                            new FakeUser(HrManager), new FixedClock());

        (await service.SignAsync(e.Id, Today)).IsSuccess.Should().BeTrue();
        ledger.Items.Should().ContainSingle();
    }

    // ---- R4 ------------------------------------------------------------------------------------

    [Fact]
    public void R4_A_departure_recorded_ahead_leaves_the_employee_on_the_staff_until_the_exit_date()
    {
        var e = Technician();
        var habilitation = e.Grant(HabilitationActivity.AdultCollection, null, HabilitationLevel.Authorized,
                                   new DateOnly(2026, 2, 1), null, Guid.NewGuid(), null).Value;
        var exit = Today.AddDays(30);

        e.RecordExit(exit, ExitReason.Resignation, null).IsSuccess.Should().BeTrue();

        e.IsActiveOn(Today).Should().BeTrue();
        e.IsActiveOn(exit).Should().BeTrue("the exit date is the last day worked");
        e.IsActiveOn(exit.AddDays(1)).Should().BeFalse();

        habilitation.WithdrawnOn.Should().Be(exit, "it ends with the departure, not when it is recorded");
        habilitation.IsActiveOn(Today).Should().BeTrue();
        habilitation.IsActiveOn(exit.AddDays(1)).Should().BeFalse();

        // Granted during the notice, a habilitation ends with it; nothing is granted from the exit on.
        e.Grant(HabilitationActivity.SampleReception, null, HabilitationLevel.Authorized, Today, null, Guid.NewGuid(), null)
         .Value.WithdrawnOn.Should().Be(exit);
        e.Grant(HabilitationActivity.HomeCollection, null, HabilitationLevel.Authorized, exit, null, Guid.NewGuid(), null)
         .FirstError.ResourceKey.Should().Be("Habilitation_EmployeeExited");

        // A withdrawal during the notice still applies at once.
        e.Withdraw(habilitation.Id, Today, "Erreur de saisie").IsSuccess.Should().BeTrue();
        habilitation.WithdrawnOn.Should().Be(Today);
    }

    [Fact]
    public void R4_The_exit_date_cannot_precede_the_hire_and_a_rehire_follows_the_exit()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2025, 12, 31), ExitReason.Resignation, null).FirstError.ResourceKey
         .Should().Be("Employee_ExitBeforeHire");

        e.RecordExit(new DateOnly(2026, 9, 30), ExitReason.Resignation, null).IsSuccess.Should().BeTrue();
        e.Rehire(new DateOnly(2026, 9, 30)).FirstError.ResourceKey.Should().Be("Employee_RehireBeforeExit");
        e.Rehire(new DateOnly(2026, 10, 1)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task R4_Leave_can_still_be_asked_during_the_notice_but_not_beyond_the_exit()
    {
        var user = new FakeUser(Permissions.HumanResources.RequestLeave);
        var e = Technician();
        e.UserId = user.UserId;
        e.RecordExit(Today.AddDays(30), ExitReason.Resignation, null);
        var service = Leave(user, new FakeLeaveQueries { Own = e.Id }, new InMemoryRepository<LeaveRequest>(), e);

        LeaveSubmission Unpaid(DateOnly from, DateOnly to) => new() { Type = LeaveType.Unpaid, StartDate = from, EndDate = to };

        (await service.SubmitAsync(Unpaid(Today.AddDays(5), Today.AddDays(6)))).IsSuccess.Should().BeTrue();
        (await service.SubmitAsync(Unpaid(Today.AddDays(29), Today.AddDays(32)))).FirstError.ResourceKey
            .Should().Be("Leave_EmployeeNotEmployed");
    }

    // ---- R6 ------------------------------------------------------------------------------------

    private const string HrManager = Permissions.HumanResources.Manage;

    [Fact]
    public async Task R6_An_employee_cannot_cancel_an_absence_HR_recorded_on_their_file()
    {
        var employeeUser = new FakeUser(Permissions.HumanResources.RequestLeave);
        var hr = Guid.NewGuid();
        var e = Technician();
        var requests = new InMemoryRepository<LeaveRequest>();

        var absence = new LeaveRequest(e.Id, LeaveType.UnjustifiedAbsence, Today.AddDays(2), Today.AddDays(2), false,
                                       false, 1m, null, false, DateTimeOffset.Now, hr);
        var enteredByHr = new LeaveRequest(e.Id, LeaveType.Unpaid, Today.AddDays(5), Today.AddDays(5), false, false, 1m,
                                           null, false, DateTimeOffset.Now, hr);
        var own = new LeaveRequest(e.Id, LeaveType.Unpaid, Today.AddDays(8), Today.AddDays(8), false, false, 1m, null,
                                   false, DateTimeOffset.Now, employeeUser.UserId);
        requests.Items.AddRange([absence, enteredByHr, own]);

        var asEmployee = Leave(employeeUser, new FakeLeaveQueries { Own = e.Id }, requests, e);
        (await asEmployee.CancelAsync(absence.Id, null)).FirstError.ResourceKey.Should().Be("Leave_HrOnlyType");
        (await asEmployee.CancelAsync(enteredByHr.Id, null)).FirstError.ResourceKey.Should().Be("Leave_SubmittedByOther");
        (await asEmployee.CancelAsync(own.Id, null)).IsSuccess.Should().BeTrue();
        absence.Status.Should().Be(LeaveStatus.Pending);

        var asHr = Leave(new FakeUser(HrManager, Permissions.HumanResources.ApproveLeave), new FakeLeaveQueries(), requests, e);
        (await asHr.CancelAsync(absence.Id, "Justifiée finalement")).IsSuccess.Should().BeTrue();
        (await asHr.CancelAsync(enteredByHr.Id, null)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void R6_An_approver_without_the_HR_right_may_not_cancel_an_unjustified_absence()
    {
        var absence = new LeaveRequest(Guid.NewGuid(), LeaveType.UnjustifiedAbsence, Today, Today, false, false, 1m, null,
                                       false, DateTimeOffset.Now, Guid.NewGuid());

        LeaveService.CancelRefusal(absence, Guid.NewGuid(), managesHr: false, actsForOthers: true)!
            .ResourceKey.Should().Be("Leave_HrOnlyType");
        LeaveService.CancelRefusal(absence, Guid.NewGuid(), managesHr: true, actsForOthers: true).Should().BeNull();
    }

    // ---- R7 ------------------------------------------------------------------------------------

    [Fact]
    public void R7_A_closed_month_prints_with_the_rates_it_was_closed_with()
    {
        var period = new PayrollPeriod(Site, 2026, 3);
        period.Add(new Payslip(Guid.NewGuid()) { BaseSalary = 5200m });
        var rates = new PayrollParameters(2026) { CnssCeiling = 6000m, AmoEmployeeRate = 2.26m, MonthlyWorkingDays = 26m,
                                                  IncomeTaxScale = "50000:0|:30" };

        period.Close(DateTimeOffset.Now, Guid.NewGuid(), rates).IsSuccess.Should().BeTrue();
        period.RatesSnapshot.Should().NotBeNullOrWhiteSpace();

        // The year's rates corrected afterwards.
        rates.CnssCeiling = 7000m;
        rates.AmoEmployeeRate = 3m;
        rates.IncomeTaxScale = PayrollParameters.DefaultScale;

        var printed = period.RatesForPrinting(rates);
        printed.CnssCeiling.Should().Be(6000m);
        printed.AmoEmployeeRate.Should().Be(2.26m);
        printed.MonthlyWorkingDays.Should().Be(26m);
        printed.IncomeTaxScale.Should().Be("50000:0|:30");
        printed.Year.Should().Be(2026);

        period.Reopen().IsSuccess.Should().BeTrue();
        period.RatesSnapshot.Should().BeNull();
        period.RatesForPrinting(rates).Should().BeSameAs(rates, "a draft follows the year's rates");
    }

    [Fact]
    public void R7_A_month_closed_before_the_snapshot_existed_prints_with_the_current_rates()
    {
        var period = new PayrollPeriod(Site, 2025, 12);
        period.Add(new Payslip(Guid.NewGuid()));
        period.Close(DateTimeOffset.Now, Guid.NewGuid());
        var current = new PayrollParameters(2025);

        period.RatesForPrinting(current).Should().BeSameAs(current);
        PayrollParameters.FromSnapshot(2025, "not json").Should().BeNull();
    }

    // ---- R8 ------------------------------------------------------------------------------------

    [Fact]
    public void R8_An_exit_on_the_first_pays_the_day_worked()
    {
        var a = Pay(5400m, new PayslipVariables { MonthWorkingDays = 27m, ContractWorkingDays = 1m });

        a.AbsenceDeduction.Should().Be(5200m);   // 5,400 − 5,400 × 1 / 27
        a.GrossTaxable.Should().Be(200m);
        a.PaidDays.Should().Be(0.96m);           // 26 × 1 / 27
    }

    [Fact]
    public void R8_A_hire_after_a_holiday_on_the_first_is_a_full_month()
    {
        // 2 January, 1 January a holiday: every working day of the month is in the contract.
        var a = Pay(5200m, new PayslipVariables { MonthWorkingDays = 25m, ContractWorkingDays = 25m });

        a.AbsenceDeduction.Should().Be(0m);
        a.PaidDays.Should().Be(26m);
    }

    [Fact]
    public void R8_A_hire_mid_month_is_paid_its_days_and_absences_keep_the_26_day_rate()
    {
        var a = Pay(5200m, new PayslipVariables { MonthWorkingDays = 26m, ContractWorkingDays = 13m, AbsenceDays = 2m });

        a.AbsenceDeduction.Should().Be(3000m);   // 2,600 for the days before the hire, 2 × 200
        a.GrossTaxable.Should().Be(2200m);
        a.PaidDays.Should().Be(11m);
    }

    [Fact]
    public void R8_Absences_never_take_the_pay_below_zero()
    {
        var a = Pay(5200m, new PayslipVariables { MonthWorkingDays = 26m, ContractWorkingDays = 2m, AbsenceDays = 5m });

        a.AbsenceDeduction.Should().Be(5200m);
        a.GrossTaxable.Should().Be(0m);
        a.PaidDays.Should().Be(0m);
    }

    // ---- R9 ------------------------------------------------------------------------------------

    [Fact]
    public void R9_A_monthly_accrual_rounds_a_half_up_and_the_year_adds_up_to_the_entitlement()
    {
        // Six years of service: 18 + 1.5 = 19.5 days a year, 1.625 a month.
        var hired = new DateOnly(2020, 1, 1);
        LeaveRules.MonthlyEntitlement(hired, null, new DateOnly(2026, 1, 31)).Should().Be(1.63m);

        var facts = new AccrualFacts(Guid.NewGuid(), hired, null, null, new DateOnly(2026, 1, 1), new HashSet<string>());
        var year = LeaveAccrual.Due(facts, new DateOnly(2027, 1, 10)).ToList();

        year.Should().HaveCount(12);
        year.Sum(e => e.Days).Should().Be(19.5m);
        year[^1].Days.Should().Be(1.57m);
    }

    [Fact]
    public void R9_An_entitlement_divisible_by_twelve_is_credited_evenly()
    {
        var facts = new AccrualFacts(Guid.NewGuid(), new DateOnly(2024, 1, 1), null, null, new DateOnly(2026, 1, 1),
                                     new HashSet<string>());
        LeaveAccrual.Due(facts, new DateOnly(2027, 1, 10)).Should().OnlyContain(e => e.Days == 1.5m);
    }

    // ---- R10 -----------------------------------------------------------------------------------

    [Fact]
    public void R10_The_advanced_allowance_runs_on_calendar_days()
    {
        // 14 calendar days of stoppage, 12 of them working days.
        var candidate = new PayrollCandidate(Guid.NewGuid(), 12m, 12m, 14m);

        var v = PayrollService.FromFiles(new PayslipVariables(), candidate, null, 7300m);

        v.WorkAccidentDays.Should().Be(12m, "the salary withheld is the working days");
        v.WorkAccidentAllowance.Should().Be(2240m);  // 7,300 × 12 / 365 = 240 a day × 14 × 2/3
    }

    // ---- Fakes ---------------------------------------------------------------------------------

    private static InMemoryRepository<Employee> Repo(params Employee[] employees)
    {
        var repo = new InMemoryRepository<Employee>();
        repo.Items.AddRange(employees);
        return repo;
    }

    private static PayrollService Payroll(FakePayrollQueries queries, PayrollPeriod[] periods, Employee[] employees,
                                          PayrollParameters[] rates)
    {
        var periodRepo = new InMemoryRepository<PayrollPeriod>();
        periodRepo.Items.AddRange(periods);
        var rateRepo = new InMemoryRepository<PayrollParameters>();
        rateRepo.Items.AddRange(rates);
        return new PayrollService(periodRepo, rateRepo, Repo(employees), queries, new FakeUnitOfWork(), new AllowAll(),
                                  new FakeUser(Permissions.HumanResources.Salary), new FixedClock());
    }

    private static LeaveService Leave(FakeUser user, FakeLeaveQueries queries, InMemoryRepository<LeaveRequest> requests,
                                      params Employee[] employees) =>
        new(requests, new InMemoryRepository<LeaveLedgerEntry>(), new InMemoryRepository<PublicHoliday>(), Repo(employees),
            queries, new FakeUnitOfWork(), new AllowAll(), user, new FixedClock());

    internal sealed class InMemoryRepository<T> : IRepository<T> where T : AggregateRoot
    {
        public List<T> Items { get; } = [];

        public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

        public Task<T?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default) => GetByIdAsync(id, ct);

        public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.Any(i => i.Id == id));

        public Task<IReadOnlyList<T>> ListAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T>>([.. Items.Where(predicate.Compile())]);

        public Task AddAsync(T entity, CancellationToken ct = default)
        {
            Items.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(T entity) { }

        public void SoftDelete(T entity, Guid byUserId, string reason) => Items.Remove(entity);
    }

    internal sealed class FakeUnitOfWork : IUnitOfWork
    {
        public string? AuditReason { get; set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(1);
        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable>(new Nothing());
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class AllowAll : IPermissionGuard
    {
        public Result Require(string permission) => Result.Success();
        public Result RequireAny(params string[] permissions) => Result.Success();
        public Result RequireAll(params string[] permissions) => Result.Success();
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    internal sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));
        public DateOnly Today => new(2026, 10, 15);
    }

    internal sealed class FakeUser(params string[] permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "user";
        public string FullName => "User";
        public Guid SiteId => Site;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [Site];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(permissions);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "TEST";
    }

    internal sealed class FakePayrollQueries : IPayrollQueries
    {
        public IReadOnlyList<PayrollCandidate> Candidates { get; init; } = [];

        public Task<IReadOnlyList<PayrollPeriodRow>> GetPeriodsAsync(Guid siteId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PayrollPeriodRow>>([]);
        public Task<Guid?> FindPeriodAsync(Guid siteId, int year, int month, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<Guid>> DraftPeriodIdsAsync(int year, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<PayrollCandidate>> CandidatesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                     decimal monthlyWorkingDays, CancellationToken ct = default) =>
            Task.FromResult(Candidates);
        public Task<InvoiceIssuer> EmployerAsync(Guid siteId, CancellationToken ct = default) =>
            Task.FromResult(new InvoiceIssuer("Lab", "Lab", null, null, null, null, null, null, null, null, null, null,
                                              null, null, null));
        public Task<IReadOnlyDictionary<Guid, SettledAmounts>> SettlementsAsync(DateOnly from, DateOnly to,
                                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SettledAmounts>>(new Dictionary<Guid, SettledAmounts>());
    }

    internal sealed class FakeHrDocumentQueries : IHrDocumentQueries
    {
        public IReadOnlyList<LeaveLedgerEntry> AccrualsDue { get; init; } = [];

        public Task<IReadOnlyList<SalaryMonth>> ClosedSalaryMonthsAsync(Guid employeeId, int count, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SalaryMonth>>([]);
        public Task<IReadOnlyList<decimal>> FullMonthGrossAsync(Guid employeeId, DateOnly before, decimal fullDays, int count,
                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<decimal>>([]);
        public Task<decimal> LeaveBalanceAtExitAsync(Guid employeeId, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<IReadOnlyList<LeaveLedgerEntry>> ExitAccrualsDueAsync(Guid employeeId, CancellationToken ct = default) =>
            Task.FromResult(AccrualsDue);
        public Task<(Guid PeriodId, PayrollStatus Status, Payslip? Payslip)?> PayslipOfMonthAsync(
            Guid employeeId, int year, int month, CancellationToken ct = default) =>
            Task.FromResult<(Guid, PayrollStatus, Payslip?)?>(null);
    }

    internal sealed class FakeLeaveQueries : ILeaveQueries
    {
        public Guid? Own { get; init; }

        public Task<Guid?> EmployeeIdForUserAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Own);
        public Task<IReadOnlyList<LeaveRow>> GetRequestsAsync(bool pendingOnly, int year, Guid? employeeId,
                                                              CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeaveRow>>([]);
        public Task<IReadOnlyList<LeaveBalanceRow>> GetBalancesAsync(DateOnly today, Guid? employeeId,
                                                                     CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeaveBalanceRow>>([]);
        public Task<IReadOnlyList<AccrualFacts>> GetAccrualFactsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AccrualFacts>>([]);
        public Task<LeaveRow?> OverlappingLeaveAsync(Guid employeeId, DateOnly from, DateOnly to, Guid? excluding,
                                                     CancellationToken ct = default) => Task.FromResult<LeaveRow?>(null);
        public Task<IReadOnlySet<DateOnly>> GetHolidayDatesAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<DateOnly>>(new HashSet<DateOnly>());
        public Task<IReadOnlyList<HolidayRow>> GetHolidaysAsync(int year, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HolidayRow>>([]);
        public Task<IReadOnlySet<DateOnly>> RecordedHolidayDatesAsync(int year, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<DateOnly>>(new HashSet<DateOnly>());
    }
}

/// <summary>
/// The holiday calendar and the payroll candidates against SQL Server (R5, R8). Runs on a scratch
/// database of its own, named after the one of <see cref="SqlServerFactAttribute"/> with a suffix,
/// so it never meets the other database tests' one.
/// </summary>
public sealed class ReviewFixesHrDatabaseTests : IDisposable
{
    private readonly string _connection;
    private readonly bool _created;

    public ReviewFixesHrDatabaseTests()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) { _connection = ""; return; }

        var builder = new SqlConnectionStringBuilder(configured);
        builder.InitialCatalog = (string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "MarocLIS_Test" : builder.InitialCatalog)
                                 + "_Hr" + Guid.NewGuid().ToString("N")[..6];
        _connection = builder.ConnectionString;

        using var context = Context(null);
        context.Database.EnsureCreated();
        _created = true;
    }

    private LisDbContext Context(ICurrentUser? user) =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connection).Options, user);

    public void Dispose()
    {
        if (!_created) return;
        using var context = Context(null);
        context.Database.EnsureDeleted();
    }

    [SqlServerFact]
    public async Task R5_Reading_holiday_dates_puts_in_the_civil_holidays_of_every_year_touched()
    {
        await using var context = Context(null);
        var queries = new LeaveQueries(context, new ReviewFixesHrTests.FixedClock());

        var dates = await queries.GetHolidayDatesAsync(new DateOnly(2030, 12, 28), new DateOnly(2031, 1, 12));

        dates.Should().Contain([new DateOnly(2031, 1, 1), new DateOnly(2031, 1, 11)]);
        (await context.PublicHolidays.CountAsync(h => h.Date.Year == 2031)).Should().Be(PublicHoliday.FixedHolidays.Count);

        // Read again: nothing added twice.
        await queries.GetHolidayDatesAsync(new DateOnly(2031, 1, 1), new DateOnly(2031, 12, 31));
        (await context.PublicHolidays.CountAsync(h => h.Date.Year == 2031)).Should().Be(PublicHoliday.FixedHolidays.Count);
    }

    [SqlServerFact]
    public async Task R8_A_hire_on_the_second_after_new_year_is_a_full_month_and_a_mid_month_hire_is_prorated()
    {
        var site = new LabSite("H" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), new MultilingualText("Site"),
                               LabSiteKind.MainLaboratory);
        await using var context = Context(new SiteOperator(site.Id));
        context.Sites.Add(site);
        var second = new Employee("H1" + site.Code, "A", "B", EmployeeJob.Technician, new DateOnly(2032, 1, 2), site.Id)
            { BaseSalary = 5200m };
        var mid = new Employee("H2" + site.Code, "C", "D", EmployeeJob.Technician, new DateOnly(2032, 1, 19), site.Id)
            { BaseSalary = 5200m };
        context.Employees.AddRange(second, mid);
        await context.SaveChangesAsync();

        var candidates = await new PayrollQueries(context)
            .CandidatesAsync(site.Id, new DateOnly(2032, 1, 1), new DateOnly(2032, 1, 31), 26m);

        var full = candidates.Single(c => c.EmployeeId == second.Id);
        full.UnpaidDays.Should().Be(0m);
        full.MonthWorkingDays.Should().Be(0m, "1 January is a holiday: every working day is in the contract");

        var prorated = candidates.Single(c => c.EmployeeId == mid.Id);
        prorated.UnpaidDays.Should().Be(0m, "days before the hire are not absences");
        prorated.MonthWorkingDays.Should().BeGreaterThan(prorated.ContractWorkingDays);
        prorated.ContractWorkingDays.Should().BeGreaterThan(0m);
    }

    private sealed class SiteOperator(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "rh";
        public string FullName => "RH";
        public Guid SiteId => siteId;
        public string SiteCode => "H";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "TEST";
    }
}
