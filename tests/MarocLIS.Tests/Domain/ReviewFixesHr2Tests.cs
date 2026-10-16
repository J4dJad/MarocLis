using FluentAssertions;
using MarocLIS.Application.Abstractions;
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
using static MarocLIS.Tests.Domain.ReviewFixesHrTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Second HR review: departures corrected or withdrawn before they take effect (H1), the leave
/// paid by a settlement written off without the signature (H2), the contract before a re-hire kept
/// for the payroll (H3), accidents during the notice (H4), habilitations held on the exit day (H5),
/// whole CNSS days (H7) and the rates of a reopened month (H8).
/// </summary>
public sealed class ReviewFixesHr2Tests
{
    private static readonly DateOnly Today = new(2026, 10, 15);   // FixedClock
    private static readonly Guid Site = Guid.NewGuid();

    private static Employee Technician(string number = "M0001", decimal? salary = 6000m) =>
        new(number, "ALAMI", "Sara", EmployeeJob.Technician, new DateOnly(2024, 1, 2), Site) { BaseSalary = salary };

    private static PersonnelService Personnel(Employee employee, InMemoryRepository<FinalSettlement> settlements,
                                              InMemoryRepository<LeaveLedgerEntry> ledger, FakeHrDocumentQueries? hr = null) =>
        new(Repo(employee), new InMemoryRepository<CredentialFile>(), settlements, ledger, new InMemoryRepository<LeaveRequest>(), null!,
            hr ?? new FakeHrDocumentQueries(), null!, new FakeUnitOfWork(), new AllowAll(),
            new FakeUser(Permissions.HumanResources.Manage), new FixedClock());

    // ---- H1 ------------------------------------------------------------------------------------

    [Fact]
    public void H1_A_resignation_taken_back_before_the_exit_is_withdrawn_with_the_habilitations()
    {
        var e = Technician();
        var habilitation = e.Grant(HabilitationActivity.AdultCollection, null, HabilitationLevel.Authorized,
                                   new DateOnly(2025, 2, 1), null, Guid.NewGuid(), null).Value;
        e.RecordExit(new DateOnly(2026, 11, 30), ExitReason.Resignation, "Démission").IsSuccess.Should().BeTrue();

        // Re-hiring is not the way: the departure has not happened.
        e.Rehire(Today).FirstError.ResourceKey.Should().Be("Employee_RehireBeforeExit");

        e.CancelExit(Today).IsSuccess.Should().BeTrue();

        e.HasDeparture.Should().BeFalse();
        e.ExitReason.Should().BeNull();
        e.HireDate.Should().Be(new DateOnly(2024, 1, 2), "seniority and leave entitlement are untouched");
        habilitation.WithdrawnOn.Should().BeNull();
        habilitation.IsActiveOn(new DateOnly(2026, 12, 15)).Should().BeTrue();
    }

    [Fact]
    public void H1_An_exit_saved_today_by_mistake_can_be_undone_the_same_day_but_not_once_passed()
    {
        var e = Technician();
        e.RecordExit(Today, ExitReason.Resignation, null);
        e.CancelExit(Today).IsSuccess.Should().BeTrue();

        e.RecordExit(Today.AddDays(-1), ExitReason.Resignation, null);
        e.CancelExit(Today).FirstError.ResourceKey.Should().Be("Employee_ExitAlreadyEffective");
        e.CorrectExit(Today.AddDays(5), ExitReason.Resignation, null, Today).FirstError.ResourceKey
         .Should().Be("Employee_ExitAlreadyEffective");
        Technician().CancelExit(Today).FirstError.ResourceKey.Should().Be("Employee_NoDeparture");
    }

    [Fact]
    public void H1_Correcting_the_date_moves_the_end_of_the_habilitations_with_it()
    {
        var e = Technician();
        var habilitation = e.Grant(HabilitationActivity.SampleReception, null, HabilitationLevel.Authorized,
                                   new DateOnly(2025, 2, 1), null, Guid.NewGuid(), null).Value;
        e.RecordExit(new DateOnly(2026, 11, 30), ExitReason.Resignation, null);

        e.CorrectExit(new DateOnly(2026, 12, 31), ExitReason.Dismissal, "Préavis prolongé", Today).IsSuccess.Should().BeTrue();

        e.ExitDate.Should().Be(new DateOnly(2026, 12, 31));
        e.ExitReason.Should().Be(ExitReason.Dismissal);
        e.ExitComment.Should().Be("Préavis prolongé");
        habilitation.WithdrawnOn.Should().Be(new DateOnly(2027, 1, 1));
    }

    [Fact]
    public async Task H1_Withdrawing_a_departure_empties_its_settlement_and_is_refused_once_it_is_signed()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 10, 31), ExitReason.Resignation, null);
        var settlement = new FinalSettlement(e.Id, Site, e.ExitDate!.Value, ExitReason.Resignation);
        settlement.Update(new SettlementAmounts(6000m, 12m, 2769.23m, false, 0m, 0m, null));
        var settlements = new InMemoryRepository<FinalSettlement>();
        settlements.Items.Add(settlement);
        var service = Personnel(e, settlements, new InMemoryRepository<LeaveLedgerEntry>());

        (await service.CancelExitAsync(e.Id, " ")).FirstError.ResourceKey.Should().Be("Employee_ExitCancelReasonRequired");
        (await service.CancelExitAsync(e.Id, "Démission retirée")).IsSuccess.Should().BeTrue();

        e.HasDeparture.Should().BeFalse();
        settlement.Total.Should().Be(0m, "an orphan settlement must not pay indemnities with the month");
        settlement.LeaveDays.Should().Be(0m);
        settlement.Note.Should().Contain("Démission retirée");

        // Signed: the departure is final.
        var other = Technician("M0002");
        other.RecordExit(Today, ExitReason.Resignation, null);
        var signed = new FinalSettlement(other.Id, Site, Today, ExitReason.Resignation);
        signed.Sign(Today);
        var signedRepo = new InMemoryRepository<FinalSettlement>();
        signedRepo.Items.Add(signed);
        (await Personnel(other, signedRepo, new InMemoryRepository<LeaveLedgerEntry>()).CancelExitAsync(other.Id, "Erreur"))
            .FirstError.ResourceKey.Should().Be("Employee_ExitSettled");
        other.HasDeparture.Should().BeTrue();
    }

    [Fact]
    public async Task H1_Moving_the_exit_date_empties_the_settlement_computed_for_the_old_one()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 11, 30), ExitReason.Resignation, null);
        var settlement = new FinalSettlement(e.Id, Site, e.ExitDate!.Value, ExitReason.Resignation);
        settlement.Update(new SettlementAmounts(6000m, 12m, 2769.23m, false, 0m, 0m, null));
        var settlements = new InMemoryRepository<FinalSettlement>();
        settlements.Items.Add(settlement);

        (await Personnel(e, settlements, new InMemoryRepository<LeaveLedgerEntry>())
            .CorrectExitAsync(e.Id, new DateOnly(2026, 12, 15), ExitReason.Resignation, null)).IsSuccess.Should().BeTrue();

        e.ExitDate.Should().Be(new DateOnly(2026, 12, 15));
        settlement.Total.Should().Be(0m);
    }

    // ---- H2 ------------------------------------------------------------------------------------

    [Fact]
    public async Task H2_Closing_the_month_of_departure_takes_the_paid_leave_off_without_a_signature()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 9, 30), ExitReason.EndOfFixedTerm, null);
        var period = new PayrollPeriod(Site, 2026, 9);
        var slip = new Payslip(e.Id) { BaseSalary = 6000m };
        var rates = new PayrollParameters(2026);
        slip.Recompute(new PayslipVariables { LeaveIndemnity = 2769.23m }, rates, period.LastDay);
        period.Add(slip);

        var settlement = new FinalSettlement(e.Id, Site, e.ExitDate!.Value, ExitReason.EndOfFixedTerm);
        settlement.Update(new SettlementAmounts(6000m, 12m, 2769.23m, false, 0m, 0m, null));
        var settlements = new InMemoryRepository<FinalSettlement>();
        settlements.Items.Add(settlement);
        var ledger = new InMemoryRepository<LeaveLedgerEntry>();
        var september = new LeaveLedgerEntry(e.Id, new DateOnly(2026, 9, 30), LeaveLedgerKind.Accrual, 1.5m, "ACQ-2026-09");
        var service = Payroll(new FakePayrollQueries(), [period], [e], [rates], settlements, ledger,
                              new FakeHrDocumentQueries { AccrualsDue = [september] });

        (await service.CloseAsync(period.Id)).IsSuccess.Should().BeTrue();

        var reference = SettlementService.SettledLeaveReference(e.ExitDate!.Value);
        ledger.Items.Should().Contain(september);
        ledger.Items.Where(l => l.Reference == reference).Sum(l => l.Days).Should().Be(-12m);

        // Reopened, the settlement corrected to 10 days, closed again: the debit follows, once.
        (await service.ReopenAsync(period.Id, "Correction du solde")).IsSuccess.Should().BeTrue();
        settlement.Update(new SettlementAmounts(6000m, 10m, 2307.69m, false, 0m, 0m, null));
        (await service.CloseAsync(period.Id)).IsSuccess.Should().BeTrue();
        (await service.ReopenAsync(period.Id, "Vérification")).IsSuccess.Should().BeTrue();
        (await service.CloseAsync(period.Id)).IsSuccess.Should().BeTrue();

        ledger.Items.Where(l => l.Reference == reference).Sum(l => l.Days).Should().Be(-10m);
        ledger.Items.Count(l => l.Reference == reference).Should().Be(2, "the first debit and one correction");
    }

    [Fact]
    public async Task H2_A_rehire_writes_off_the_leave_of_a_settlement_never_signed()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 9, 30), ExitReason.EndOfFixedTerm, null);
        var settlement = new FinalSettlement(e.Id, Site, e.ExitDate!.Value, ExitReason.EndOfFixedTerm);
        settlement.Update(new SettlementAmounts(6000m, 12m, 2769.23m, false, 0m, 0m, null));
        var settlements = new InMemoryRepository<FinalSettlement>();
        settlements.Items.Add(settlement);
        var ledger = new InMemoryRepository<LeaveLedgerEntry>();
        ledger.Items.Add(new LeaveLedgerEntry(e.Id, new DateOnly(2026, 8, 31), LeaveLedgerKind.Accrual, 10.5m, "ACQ-2026-08"));
        var september = new LeaveLedgerEntry(e.Id, new DateOnly(2026, 9, 30), LeaveLedgerKind.Accrual, 1.5m, "ACQ-2026-09");

        (await Personnel(e, settlements, ledger, new FakeHrDocumentQueries { AccrualsDue = [september] })
            .RehireAsync(e.Id, new DateOnly(2026, 11, 2))).IsSuccess.Should().BeTrue();

        ledger.Items.Sum(l => l.Days).Should().Be(0m, "the 12 days paid in September are not found again");
        e.HasDeparture.Should().BeFalse();
    }

    [Fact]
    public async Task H2_A_rehire_without_settlement_keeps_the_leave_accrued_up_to_the_departure()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 9, 20), ExitReason.Resignation, null);
        var ledger = new InMemoryRepository<LeaveLedgerEntry>();
        var september = new LeaveLedgerEntry(e.Id, new DateOnly(2026, 9, 30), LeaveLedgerKind.Accrual, 1.5m, "ACQ-2026-09");

        (await Personnel(e, new InMemoryRepository<FinalSettlement>(), ledger,
                         new FakeHrDocumentQueries { AccrualsDue = [september] })
            .RehireAsync(e.Id, new DateOnly(2026, 9, 28))).IsSuccess.Should().BeTrue();

        ledger.Items.Should().ContainSingle().Which.Should().BeSameAs(september);
    }

    // ---- H3 ------------------------------------------------------------------------------------

    [Fact]
    public void H3_A_rehire_keeps_the_dates_of_the_contract_it_follows()
    {
        var e = Technician();
        e.RecordExit(new DateOnly(2026, 9, 20), ExitReason.Resignation, null);

        e.Rehire(new DateOnly(2026, 9, 28)).IsSuccess.Should().BeTrue();

        e.PreviousHireDate.Should().Be(new DateOnly(2024, 1, 2));
        e.PreviousExitDate.Should().Be(new DateOnly(2026, 9, 20));
        e.HireDate.Should().Be(new DateOnly(2026, 9, 28));
    }

    [Fact]
    public async Task H3_A_refresh_never_takes_off_a_draft_payslip_that_pays_a_departure()
    {
        var e = Technician();
        var period = new PayrollPeriod(Site, 2026, 9);
        var slip = new Payslip(e.Id) { BaseSalary = 6000m };
        slip.Recompute(new PayslipVariables { LeaveIndemnity = 2769.23m }, new PayrollParameters(2026), period.LastDay);
        period.Add(slip);

        // The employee is no longer found for the month (as before the fix after a re-hire on 1 October).
        (await Payroll(new FakePayrollQueries(), [period], [e], [new PayrollParameters(2026)]).RefreshAsync(period.Id))
            .IsSuccess.Should().BeTrue();

        period.Payslips.Should().ContainSingle();
    }

    // ---- H4 ------------------------------------------------------------------------------------

    [Fact]
    public void H4_An_accident_can_be_declared_for_someone_serving_their_notice()
    {
        AccidentService.IsDeclarableFor(null, Today).Should().BeTrue();
        AccidentService.IsDeclarableFor(new DateOnly(2026, 11, 30), Today).Should().BeTrue("the notice is being worked");
        AccidentService.IsDeclarableFor(Today.AddDays(-3), Today).Should().BeTrue("an accident on the last day is reported after");
        AccidentService.IsDeclarableFor(Today.AddDays(-AccidentService.DeclarableDaysAfterExit - 1), Today).Should().BeFalse();
    }

    // ---- H5 ------------------------------------------------------------------------------------

    [Fact]
    public void H5_Habilitations_are_held_on_the_exit_day()
    {
        var e = Technician();
        var habilitation = e.Grant(HabilitationActivity.TechnicalValidation, Guid.NewGuid(), HabilitationLevel.Authorized,
                                   new DateOnly(2025, 2, 1), null, Guid.NewGuid(), null).Value;
        var exit = new DateOnly(2026, 11, 30);
        e.RecordExit(exit, ExitReason.Resignation, null);

        e.IsActiveOn(exit).Should().BeTrue();
        habilitation.IsActiveOn(exit).Should().BeTrue("the employee works that day, authorised as before");
        habilitation.IsActiveOn(exit.AddDays(1)).Should().BeFalse();
        habilitation.WithdrawalReason.Should().Be(Employee.ExitWithdrawalReason);

        // Granted on the exit day itself, a habilitation holds for it.
        e.Grant(HabilitationActivity.SampleReception, null, HabilitationLevel.InTraining, exit, null, Guid.NewGuid(), null)
         .Value.IsActiveOn(exit).Should().BeTrue();
    }

    // ---- H7 ------------------------------------------------------------------------------------

    private static PayslipAmounts Pay(PayslipVariables v) =>
        PayrollCalculator.Compute(new PayslipInput(6000m, new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 31),
                                                   ContractType.Permanent, 0, v), new PayrollParameters(2026));

    [Fact]
    public void H7_The_days_declared_to_the_CNSS_are_the_whole_days_worked()
    {
        // December 2026: 27 working days; hired on the 16th, 14 of them in the contract.
        Pay(new PayslipVariables { MonthWorkingDays = 27m, ContractWorkingDays = 14m }).PaidDays.Should().Be(14m);

        // A half day of unpaid leave is still a day worked.
        Pay(new PayslipVariables { MonthWorkingDays = 27m, ContractWorkingDays = 14m, AbsenceDays = 2.5m })
            .PaidDays.Should().Be(12m);

        // Never more than the 26 days of a full month.
        Pay(new PayslipVariables { MonthWorkingDays = 27m, ContractWorkingDays = 26.5m }).PaidDays.Should().Be(26m);
        Pay(new PayslipVariables()).PaidDays.Should().Be(26m);
    }

    // ---- H8 ------------------------------------------------------------------------------------

    [Fact]
    public async Task H8_A_reopened_month_keeps_the_rates_it_was_closed_with()
    {
        var march = new PayrollParameters(2026) { AmoEmployeeRate = 2.26m };
        var a = Technician("M0001", 5000m);
        var b = Technician("M0002", 5000m);
        var period = new PayrollPeriod(Site, 2026, 3);
        foreach (var e in new[] { a, b })
        {
            var slip = new Payslip(e.Id) { BaseSalary = 5000m, HireDate = new DateOnly(2025, 6, 2) };   // no seniority yet
            slip.Recompute(new PayslipVariables(), march, period.LastDay);
            period.Add(slip);
        }
        var service = Payroll(new FakePayrollQueries(), [period], [a, b], [march]);
        (await service.CloseAsync(period.Id)).IsSuccess.Should().BeTrue();

        // In June the year's AMO rate changes; March is reopened to correct one bonus.
        (await service.SaveParametersAsync(2026, p => p.AmoEmployeeRate = 2.40m)).IsSuccess.Should().BeTrue();
        (await service.ReopenAsync(period.Id, "Prime oubliée")).IsSuccess.Should().BeTrue();
        var corrected = period.Payslips.First(p => p.EmployeeId == a.Id);
        (await service.UpdateVariablesAsync(period.Id, corrected.Id, corrected.Variables with { TaxableBonus = 500m }))
            .IsSuccess.Should().BeTrue();
        (await service.CloseAsync(period.Id)).IsSuccess.Should().BeTrue();

        corrected.Amounts.AmoEmployee.Should().Be(124.30m);   // 5,500 × 2.26 %
        period.Payslips.First(p => p.EmployeeId == b.Id).Amounts.AmoEmployee.Should().Be(113.00m);
        var printed = (await service.GetDocumentsAsync(period.Id, null)).Value;
        printed.Should().OnlyContain(d => d.Parameters.AmoEmployeeRate == 2.26m);
    }

    [Fact]
    public void H8_A_month_closed_before_the_snapshot_existed_is_recomputed_with_the_rates_it_is_now_closed_with()
    {
        var period = new PayrollPeriod(Site, 2025, 12);
        var slip = new Payslip(Guid.NewGuid()) { BaseSalary = 5000m, HireDate = new DateOnly(2024, 1, 2) };
        slip.Recompute(new PayslipVariables(), new PayrollParameters(2025) { AmoEmployeeRate = 2.26m }, period.LastDay);
        period.Add(slip);
        period.Close(DateTimeOffset.Now, Guid.NewGuid());       // no snapshot, as before R7
        period.Reopen();

        period.Close(DateTimeOffset.Now, Guid.NewGuid(), new PayrollParameters(2025) { AmoEmployeeRate = 2.40m });

        slip.Amounts.AmoEmployee.Should().Be(120.00m, "the amounts agree with the rates printed beside them");
        period.RatesForPrinting(new PayrollParameters(2025)).AmoEmployeeRate.Should().Be(2.40m);
    }
}

/// <summary>The payroll candidates of the month of a re-hire, against SQL Server (H3).</summary>
public sealed class ReviewFixesHr2DatabaseTests : IDisposable
{
    private readonly string _connection;
    private readonly bool _created;

    public ReviewFixesHr2DatabaseTests()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) { _connection = ""; return; }

        var builder = new SqlConnectionStringBuilder(configured);
        builder.InitialCatalog = (string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "MarocLIS_Test" : builder.InitialCatalog)
                                 + "_Hr2" + Guid.NewGuid().ToString("N")[..6];
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
    public async Task H3_The_month_of_a_rehire_pays_both_contracts_and_the_month_before_still_finds_the_employee()
    {
        var site = new LabSite("R" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), new MultilingualText("Site"),
                               LabSiteKind.MainLaboratory);
        await using var context = Context(new SiteOperator(site.Id));
        context.Sites.Add(site);

        // Same month: left on Sunday 20/09/2026, back on Monday 28/09.
        var same = new Employee("R1" + site.Code, "A", "B", EmployeeJob.Technician, new DateOnly(2025, 1, 6), site.Id)
            { BaseSalary = 6000m };
        same.RecordExit(new DateOnly(2026, 9, 20), ExitReason.Resignation, null);
        same.Rehire(new DateOnly(2026, 9, 28));

        // Next month: left on 30/09, back on 01/10.
        var next = new Employee("R2" + site.Code, "C", "D", EmployeeJob.Technician, new DateOnly(2025, 1, 6), site.Id)
            { BaseSalary = 6000m };
        next.RecordExit(new DateOnly(2026, 9, 30), ExitReason.EndOfFixedTerm, null);
        next.Rehire(new DateOnly(2026, 10, 1));

        context.Employees.AddRange(same, next);
        await context.SaveChangesAsync();

        var queries = new PayrollQueries(context);
        var september = await queries.CandidatesAsync(site.Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 26m);

        // September 2026: 26 working days. 1–19 September: 17; 28–30: 3.
        var both = september.Single(c => c.EmployeeId == same.Id);
        both.MonthWorkingDays.Should().Be(26m);
        both.ContractWorkingDays.Should().Be(20m);

        september.Single(c => c.EmployeeId == next.Id).MonthWorkingDays.Should().Be(0m, "a full September is paid");

        var october = await queries.CandidatesAsync(site.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 26m);
        october.Select(c => c.EmployeeId).Should().Contain([same.Id, next.Id]);
    }

    private sealed class SiteOperator(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "rh";
        public string FullName => "RH";
        public Guid SiteId => siteId;
        public string SiteCode => "R";
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
