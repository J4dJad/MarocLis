using FluentAssertions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class LeaveAndPlanningTests
{
    private static readonly HashSet<DateOnly> NoHolidays = [];

    [Fact]
    public void A_week_of_annual_leave_counts_six_working_days()
    {
        // Monday 12 to Sunday 18 October 2026: the Sunday is not counted.
        LeaveRules.CountDays(LeaveType.Annual, new(2026, 10, 12), new(2026, 10, 18), false, false, NoHolidays)
                  .Should().Be(6m);
    }

    [Fact]
    public void Public_holidays_and_half_days_are_left_out()
    {
        HashSet<DateOnly> holidays = [new(2026, 11, 6)];   // Marche Verte, a Friday

        LeaveRules.CountDays(LeaveType.Annual, new(2026, 11, 2), new(2026, 11, 7), true, true, holidays)
                  .Should().Be(4m);   // Mon–Sat = 6, minus the holiday, minus two half days
    }

    [Fact]
    public void A_single_half_day_counts_half()
    {
        LeaveRules.CountDays(LeaveType.Annual, new(2026, 10, 14), new(2026, 10, 14), true, false, NoHolidays)
                  .Should().Be(0.5m);
        LeaveRules.CountDays(LeaveType.Annual, new(2026, 10, 14), new(2026, 10, 14), true, true, NoHolidays)
                  .Should().Be(0.5m);
    }

    [Fact]
    public void Sick_leave_runs_in_calendar_days()
    {
        LeaveRules.CountDays(LeaveType.Sick, new(2026, 10, 12), new(2026, 10, 18), false, false, NoHolidays)
                  .Should().Be(7m);
    }

    [Theory]
    [InlineData(2026, 1, 18)]    // newly hired
    [InlineData(2021, 1, 19.5)]  // five full years
    [InlineData(2016, 1, 21)]    // ten
    [InlineData(1980, 1, 30)]    // capped
    public void The_yearly_entitlement_grows_with_seniority_up_to_thirty_days(int hiredYear, int hiredMonth, double expected)
    {
        LeaveRules.AnnualEntitlement(new DateOnly(hiredYear, hiredMonth, 1), new DateOnly(1990, 5, 5), new(2026, 10, 15))
                  .Should().Be((decimal)expected);
    }

    [Fact]
    public void A_minor_earns_two_days_a_month()
    {
        LeaveRules.AnnualEntitlement(new(2026, 6, 1), new(2010, 1, 1), new(2026, 10, 15)).Should().Be(24m);
    }

    [Fact]
    public void Accrual_credits_each_completed_month_once_and_skips_a_short_first_month()
    {
        var facts = new AccrualFacts(Guid.NewGuid(), new DateOnly(2026, 7, 20), null, null, new DateOnly(2026, 7, 20),
                                     new HashSet<string> { LeaveLedgerEntry.AccrualReference(2026, 8) });

        var due = LeaveAccrual.Due(facts, new DateOnly(2026, 10, 15)).ToList();

        // July: 12 days only. August: already credited. September: due. October: not over.
        due.Should().ContainSingle();
        due[0].Reference.Should().Be("ACQ-2026-09");
        due[0].Days.Should().Be(1.5m);
    }

    [Fact]
    public void A_refusal_needs_a_reason_and_an_approved_leave_cannot_be_cancelled_once_started()
    {
        var request = new LeaveRequest(Guid.NewGuid(), LeaveType.Annual, new(2026, 10, 12), new(2026, 10, 16),
                                       false, false, 5m, null, false, DateTimeOffset.Now, Guid.NewGuid());

        request.Refuse(DateTimeOffset.Now, Guid.NewGuid(), " ").FirstError.ResourceKey
               .Should().Be("Leave_RefusalCommentRequired");

        request.Approve(DateTimeOffset.Now, Guid.NewGuid(), null).IsSuccess.Should().BeTrue();
        request.Cancel(new DateOnly(2026, 10, 13), DateTimeOffset.Now, Guid.NewGuid(), null).FirstError.ResourceKey
               .Should().Be("Leave_AlreadyStarted");
    }

    [Fact]
    public void Sick_leave_is_not_approved_without_its_certificate()
    {
        var request = new LeaveRequest(Guid.NewGuid(), LeaveType.Sick, new(2026, 10, 12), new(2026, 10, 14),
                                       false, false, 3m, null, false, DateTimeOffset.Now, Guid.NewGuid());

        request.Approve(DateTimeOffset.Now, Guid.NewGuid(), null).FirstError.ResourceKey
               .Should().Be("Leave_JustificationMissing");
    }

    [Fact]
    public void A_night_shift_crosses_midnight_and_on_call_counts_no_hours()
    {
        ShiftRules.Hours(ShiftKind.Night, new(20, 0), new(8, 0)).Should().Be(12m);
        ShiftRules.Hours(ShiftKind.OnCall, new(0, 0), new(0, 0)).Should().Be(0m);
    }

    // ---- Planning checks -------------------------------------------------------------------------

    private static readonly DateOnly Monday = new(2026, 10, 12);

    private static PlanningEmployee Person(EmployeeJob job, bool trainee = false) =>
        new(Guid.NewGuid(), "M0001", job.ToString(), job, null, new DateOnly(2020, 1, 1), null, trainee);

    private static ShiftCell Shift(PlanningEmployee who, DateOnly day, ShiftKind kind)
    {
        var (start, end) = ShiftRules.DefaultHours(kind);
        return new ShiftCell(Guid.NewGuid(), who.EmployeeId, day, kind, start, end, null);
    }

    [Fact]
    public void A_morning_after_a_night_is_too_little_rest()
    {
        var bio = Person(EmployeeJob.Biologist);
        var issues = PlanningChecks.Check([bio],
            [Shift(bio, Monday, ShiftKind.Night), Shift(bio, Monday.AddDays(1), ShiftKind.Morning)],
            [], Monday, Monday.AddDays(6));

        issues.Should().ContainSingle(i => i.Kind == PlanningIssueKind.RestTooShort)
              .Which.Value.Should().Be(0m);   // the night ends at 8:00, the morning starts at 7:00
    }

    [Fact]
    public void Six_days_of_nine_hours_break_the_44_hour_week_and_seven_leave_no_day_off()
    {
        var bio = Person(EmployeeJob.Biologist);
        var week = Enumerable.Range(0, 7).Select(d => Shift(bio, Monday.AddDays(d), ShiftKind.Day)).ToList();

        var issues = PlanningChecks.Check([bio], week, [], Monday, Monday.AddDays(6));

        issues.Should().Contain(i => i.Kind == PlanningIssueKind.WeeklyHoursExceeded && i.Value == 63m);
        issues.Should().Contain(i => i.Kind == PlanningIssueKind.NoWeeklyRest);
    }

    [Fact]
    public void A_day_without_a_biologist_is_flagged_but_an_on_call_biologist_covers_it()
    {
        var tech = Person(EmployeeJob.Technician);
        var bio = Person(EmployeeJob.Biologist);

        PlanningChecks.Check([tech, bio], [Shift(tech, Monday, ShiftKind.Day)], [], Monday, Monday)
                      .Should().ContainSingle(i => i.Kind == PlanningIssueKind.NoBiologist);

        PlanningChecks.Check([tech, bio], [Shift(tech, Monday, ShiftKind.Day), Shift(bio, Monday, ShiftKind.OnCall)],
                             [], Monday, Monday)
                      .Should().BeEmpty();
    }

    [Fact]
    public void A_trainee_alone_on_a_night_is_flagged()
    {
        var trainee = Person(EmployeeJob.Technician, trainee: true);
        var bio = Person(EmployeeJob.Biologist);

        PlanningChecks.Check([trainee, bio],
                             [Shift(trainee, Monday, ShiftKind.Night), Shift(bio, Monday, ShiftKind.OnCall)],
                             [], Monday, Monday)
                      .Should().ContainSingle(i => i.Kind == PlanningIssueKind.TraineeAlone);
    }

    [Fact]
    public void A_shift_left_on_a_day_of_approved_leave_is_flagged()
    {
        var bio = Person(EmployeeJob.Biologist);
        var leave = new LeaveSpan(bio.EmployeeId, LeaveType.Annual, LeaveStatus.Approved, Monday, Monday.AddDays(2));

        PlanningChecks.Check([bio], [Shift(bio, Monday.AddDays(1), ShiftKind.Day)], [leave], Monday, Monday.AddDays(6))
                      .Should().ContainSingle(i => i.Kind == PlanningIssueKind.ShiftDuringLeave);
    }
}
