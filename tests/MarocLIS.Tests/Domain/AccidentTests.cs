using FluentAssertions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.HumanResources;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class AccidentTests
{
    private static readonly DateTime Monday = new(2026, 10, 12, 10, 30, 0);

    private static WorkAccident Accident(AccidentKind kind = AccidentKind.WorkAccident) =>
        new("AT-2026-0001", Guid.NewGuid(), Guid.NewGuid(), kind, Monday) { Circumstances = "Chute dans l'escalier" };

    [Fact]
    public void The_declaration_is_due_48_hours_after_the_accident() =>
        Accident().DeclarationDueAt.Should().Be(new DateTime(2026, 10, 14, 10, 30, 0));

    [Fact]
    public void A_blood_exposure_needs_its_exposure_type_and_circumstances()
    {
        var aes = Accident(AccidentKind.BloodExposure);
        aes.Validate().FirstError.ResourceKey.Should().Be("Accident_ExposureRequired");

        aes.Exposure = ExposureType.Needlestick;
        aes.Validate().IsSuccess.Should().BeTrue();

        aes.Circumstances = " ";
        aes.Validate().FirstError.ResourceKey.Should().Be("Accident_CircumstancesRequired");
    }

    [Fact]
    public void Dates_before_the_accident_are_refused()
    {
        var a = Accident();
        a.DeclaredToInsurerOn = new DateOnly(2026, 10, 11);
        a.Validate().FirstError.ResourceKey.Should().Be("Accident_DeclaredBeforeAccident");

        a.DeclaredToInsurerOn = null;
        a.ConsultedAt = Monday.AddHours(-1);
        a.Validate().FirstError.ResourceKey.Should().Be("Accident_ConsultedBeforeAccident");

        a.ConsultedAt = null;
        a.IncapacityRate = 120m;
        a.Validate().FirstError.ResourceKey.Should().Be("Accident_IncapacityInvalid");

        a.RecordReturn(new DateOnly(2026, 10, 11), Guid.NewGuid()).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_blood_exposure_schedules_its_four_checks_once()
    {
        var aes = Accident(AccidentKind.BloodExposure);
        aes.ScheduleSerology();
        aes.ScheduleSerology();

        aes.Checks.Select(c => c.DueOn).Should().Equal(
            new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 12), new DateOnly(2027, 1, 12), new DateOnly(2027, 4, 12));

        var work = Accident();
        work.ScheduleSerology();
        work.Checks.Should().BeEmpty();
    }

    [Fact]
    public void A_check_is_marked_done_with_its_date_only()
    {
        var aes = Accident(AccidentKind.BloodExposure);
        aes.ScheduleSerology();
        var first = aes.Checks.First();

        aes.MarkCheckDone(first.Id, new DateOnly(2026, 10, 11)).FirstError.ResourceKey.Should().Be("Accident_CheckBeforeAccident");
        aes.MarkCheckDone(Guid.NewGuid(), new DateOnly(2026, 10, 12)).FirstError.ResourceKey.Should().Be("Accident_CheckNotFound");
        aes.MarkCheckDone(first.Id, new DateOnly(2026, 10, 12)).IsSuccess.Should().BeTrue();
        first.DoneOn.Should().Be(new DateOnly(2026, 10, 12));
    }

    [Fact]
    public void Closing_and_a_relapse_are_written_in_the_history()
    {
        var a = Accident();
        var user = Guid.NewGuid();
        a.Close(new DateOnly(2026, 12, 1), "Consolidé sans séquelle", user);
        a.IsClosed.Should().BeTrue();

        a.Reopen(new DateOnly(2027, 1, 5), null, user);
        a.IsClosed.Should().BeFalse();
        a.Events.Select(e => e.Kind).Should().Equal(AccidentEventKind.Closed, AccidentEventKind.Relapse);
    }

    [Theory]
    [InlineData(7300, 14, 2240)]         // 7,300 × 12 / 365 = 240 a day × 14 calendar days × 2/3
    [InlineData(5200, 14, 1595.62)]
    [InlineData(7800, 0, 0)]
    [InlineData(0, 3, 0)]
    public void The_advanced_allowance_is_two_thirds_of_the_daily_salary_for_each_calendar_day(double salary, double off,
                                                                                               double expected) =>
        AccidentRules.Allowance((decimal)salary, (decimal)off).Should().Be((decimal)expected);

    [Fact]
    public void The_advanced_allowance_is_exempt_from_tax_and_contributions()
    {
        var rates = new PayrollParameters(2026);
        PayslipAmounts Pay(PayslipVariables v) =>
            PayrollCalculator.Compute(new PayslipInput(5200m, new DateOnly(2025, 1, 6), new DateOnly(2026, 10, 31),
                                                       ContractType.Permanent, 0, v), rates);

        var off = Pay(new PayslipVariables { AbsenceDays = 4m, WorkAccidentDays = 4m });
        var advanced = Pay(new PayslipVariables { AbsenceDays = 4m, WorkAccidentDays = 4m, WorkAccidentAllowance = 533.33m });

        advanced.GrossTaxable.Should().Be(off.GrossTaxable);
        advanced.CnssEmployee.Should().Be(off.CnssEmployee);
        advanced.IncomeTax.Should().Be(off.IncomeTax);
        advanced.NetPay.Should().Be(off.NetPay + 533.33m);
    }

    [Fact]
    public void Deadlines_list_the_late_declaration_and_the_checks_due()
    {
        var today = new DateOnly(2026, 10, 15);
        var facts = new AccidentDeadlineFacts(Guid.NewGuid(), "M0002", "Aït Ouahmane Mehdi", EmployeeJob.Technician,
                                              "AT-2026-0001", Monday, Declared: false,
                                              [("Bilan initial (J0)", new DateOnly(2026, 10, 12)),
                                               ("Contrôle à 1 mois", new DateOnly(2026, 11, 12)),
                                               ("Contrôle à 6 mois", new DateOnly(2027, 4, 12))]);

        var rows = PersonnelDeadlines.ForAccidents([facts], today, 60);

        rows.Should().HaveCount(3);
        rows[0].Kind.Should().Be(DeadlineKind.AccidentDeclaration);
        rows[0].DueOn.Should().Be(new DateOnly(2026, 10, 14));
        rows[0].IsOverdue.Should().BeTrue();
        rows[1].IsOverdue.Should().BeTrue();          // the baseline was due on the day
        rows[2].Subject.Should().Be("AT-2026-0001 — Contrôle à 1 mois");
        rows[2].IsOverdue.Should().BeFalse();

        PersonnelDeadlines.ForAccidents([facts with { Declared = true, PendingChecks = [] }], today, 60).Should().BeEmpty();
    }

    [Fact]
    public void The_yearly_summary_groups_by_department_and_kind()
    {
        AccidentRow Row(string? dept, AccidentKind kind, decimal days) =>
            new(Guid.NewGuid(), "AT", Guid.NewGuid(), "M1", "X", dept, kind, Monday, null, null, days, null, null, false);

        var summary = AccidentService.Summarise(
        [
            Row("Biochimie", AccidentKind.BloodExposure, 0m),
            Row("Biochimie", AccidentKind.BloodExposure, 3m),
            Row(null, AccidentKind.Commuting, 10m)
        ]);

        summary.Should().HaveCount(2);
        summary.Should().ContainEquivalentOf(new AccidentSummaryLine("Biochimie", AccidentKind.BloodExposure, 2, 3m, 1));
        summary.Should().ContainEquivalentOf(new AccidentSummaryLine("—", AccidentKind.Commuting, 1, 10m, 1));
    }
}
