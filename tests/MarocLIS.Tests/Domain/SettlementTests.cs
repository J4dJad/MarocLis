using FluentAssertions;
using MarocLIS.Domain.HumanResources;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class SettlementTests
{
    [Theory]
    [InlineData(0.5, 48)]
    [InlineData(3, 288)]       // 3 × 96
    [InlineData(7, 768)]       // 5 × 96 + 2 × 144
    [InlineData(12, 1584)]     // 480 + 720 + 2 × 192
    [InlineData(17, 2640)]     // 480 + 720 + 960 + 2 × 240
    public void Severance_hours_rise_by_bracket_of_service(double years, double hours) =>
        SettlementCalculator.SeveranceHours((decimal)years).Should().Be((decimal)hours);

    [Fact]
    public void Severance_is_paid_at_the_hourly_rate_of_the_reference_salary() =>
        SettlementCalculator.Severance(7m, 9550m, 191m).Should().Be(38400m);   // 768 h × 50

    [Fact]
    public void Severance_is_due_on_a_dismissal_after_six_months_only()
    {
        var hired = new DateOnly(2026, 1, 15);
        SettlementCalculator.SeveranceDue(ExitReason.Dismissal, hired, new DateOnly(2026, 7, 15)).Should().BeTrue();
        SettlementCalculator.SeveranceDue(ExitReason.Dismissal, hired, new DateOnly(2026, 7, 14)).Should().BeFalse();
        SettlementCalculator.SeveranceDue(ExitReason.DismissalForSeriousMisconduct, hired, new DateOnly(2027, 1, 1)).Should().BeFalse();
        SettlementCalculator.SeveranceDue(ExitReason.Resignation, hired, new DateOnly(2027, 1, 1)).Should().BeFalse();
    }

    [Theory]
    [InlineData(EmployeeJob.Biologist, 0.5, 1, 0)]
    [InlineData(EmployeeJob.Support, 3, 2, 0)]
    [InlineData(EmployeeJob.Biologist, 6, 3, 0)]
    [InlineData(EmployeeJob.Technician, 0.5, 0, 8)]
    [InlineData(EmployeeJob.Nurse, 5, 1, 0)]
    [InlineData(EmployeeJob.Cleaner, 5.5, 2, 0)]
    public void Notice_follows_the_decree_by_category_and_service(EmployeeJob job, double years, int months, int days) =>
        SettlementCalculator.Notice(SettlementCalculator.CategoryOf(job), (decimal)years)
            .Should().Be(new NoticePeriod(months, days));

    [Fact]
    public void Notice_and_leave_are_paid_at_the_daily_rate_over_26_days()
    {
        SettlementCalculator.NoticeIndemnity(new NoticePeriod(0, 8), 5200m, 26m).Should().Be(1600m);
        SettlementCalculator.NoticeIndemnity(new NoticePeriod(2, 0), 5200m, 26m).Should().Be(10400m);
        SettlementCalculator.LeaveIndemnity(13m, 5200m, 26m).Should().Be(2600m);
    }

    [Fact]
    public void Service_counts_the_last_day_and_part_years()
    {
        SettlementCalculator.ServiceYears(new DateOnly(2020, 1, 1), new DateOnly(2024, 12, 31)).Should().Be(5m);
        SettlementCalculator.ServiceYears(new DateOnly(2026, 1, 1), new DateOnly(2026, 7, 1)).Should().Be(0.5m);
    }

    [Fact]
    public void A_signed_settlement_no_longer_changes()
    {
        var s = new FinalSettlement(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 15), ExitReason.Dismissal);
        s.Update(new SettlementAmounts(6000m, 10m, 2307.69m, true, 6000m, 4000m, null)).IsSuccess.Should().BeTrue();

        s.Sign(new DateOnly(2026, 10, 14)).IsFailure.Should().BeTrue();   // before the departure
        s.Sign(new DateOnly(2026, 10, 20)).IsSuccess.Should().BeTrue();

        s.Update(new SettlementAmounts(6000m, 10m, 0m, false, 0m, 0m, null)).FirstError.ResourceKey.Should().Be("Settlement_Signed");
        s.Total.Should().Be(12307.69m);
    }

    [Fact]
    public void A_notice_indemnity_needs_the_notice_not_worked()
    {
        var s = new FinalSettlement(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 15), ExitReason.Resignation);
        s.Update(new SettlementAmounts(6000m, 0m, 0m, false, 500m, 0m, null)).FirstError.ResourceKey.Should().Be("Settlement_NoticeWorked");
        s.Update(new SettlementAmounts(-1m, 0m, 0m, false, 0m, 0m, null)).FirstError.ResourceKey.Should().Be("Settlement_NegativeAmount");
    }

    [Fact]
    public void On_the_payslip_leave_and_notice_are_taxed_and_severance_is_exempt()
    {
        var rates = new PayrollParameters(2026);
        PayslipAmounts Pay(PayslipVariables v) => PayrollCalculator.Compute(
            new PayslipInput(6000m, new DateOnly(2020, 1, 6), new DateOnly(2026, 10, 31), ContractType.Permanent, 0, v), rates);

        var plain = Pay(new());
        var leaving = Pay(new PayslipVariables { LeaveIndemnity = 1000m, NoticeIndemnity = 2000m, SeverancePay = 5000m });

        leaving.GrossTaxable.Should().Be(plain.GrossTaxable + 3000m);
        leaving.Gross.Should().Be(plain.Gross + 8000m);
        leaving.ExemptAllowances.Should().Be(plain.ExemptAllowances + 5000m);
        leaving.CnssEmployee.Should().Be(plain.CnssEmployee);              // already at the ceiling
        leaving.AmoEmployee.Should().Be(Math.Round((plain.GrossTaxable + 3000m) * 2.26m / 100m, 2));
        leaving.IncomeTax.Should().BeGreaterThan(plain.IncomeTax);
        (leaving.NetPay - plain.NetPay).Should().BeGreaterThan(5000m).And.BeLessThan(8000m);
    }
}
