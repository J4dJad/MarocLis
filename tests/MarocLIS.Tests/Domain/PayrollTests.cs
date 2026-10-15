using FluentAssertions;
using MarocLIS.Domain.HumanResources;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class PayrollTests
{
    private static readonly DateOnly PayDate = new(2026, 10, 31);
    private static readonly PayrollParameters Rates2026 = new(2026);

    private static PayslipAmounts Pay(decimal baseSalary, DateOnly hired, int persons = 0,
                                      ContractType contract = ContractType.Permanent, PayslipVariables? variables = null) =>
        PayrollCalculator.Compute(new PayslipInput(baseSalary, hired, PayDate, contract, persons, variables ?? new()), Rates2026);

    [Fact]
    public void A_technician_with_six_years_and_a_family_pays_no_income_tax()
    {
        var a = Pay(6000m, new DateOnly(2020, 1, 6), persons: 3);

        a.SeniorityRate.Should().Be(10m);
        a.SeniorityBonus.Should().Be(600m);
        a.GrossTaxable.Should().Be(6600m);
        a.CnssEmployee.Should().Be(268.80m);      // 4.48 % of the 6,000 ceiling
        a.AmoEmployee.Should().Be(149.16m);       // 2.26 % uncapped
        a.ProfessionalExpenses.Should().Be(1650m); // 25 %: 79,200 a year is above 78,000
        a.NetTaxable.Should().Be(4532.04m);
        a.IncomeTaxBeforeFamily.Should().Be(119.87m);
        a.FamilyDeduction.Should().Be(119.87m);   // 3 × 500 / 12 = 125, never more than the tax
        a.IncomeTax.Should().Be(0m);
        a.NetPay.Should().Be(6182.04m);

        a.CnssEmployer.Should().Be(538.80m);
        a.FamilyAllowanceEmployer.Should().Be(422.40m);
        a.TrainingTaxEmployer.Should().Be(105.60m);
        a.AmoEmployer.Should().Be(271.26m);
        a.EmployerCost.Should().Be(7938.06m);
    }

    [Fact]
    public void A_biologist_with_overtime_reaches_the_34_percent_bracket_and_the_expense_cap()
    {
        var a = Pay(15000m, new DateOnly(2025, 3, 1), variables: new PayslipVariables { OvertimeHours25 = 10m });

        a.OvertimeAmount.Should().Be(981.68m);    // 15,000 / 191 × 1.25 × 10
        a.GrossTaxable.Should().Be(15981.68m);
        a.ProfessionalExpenses.Should().Be(2916.67m);
        a.NetTaxable.Should().Be(12435.02m);
        a.IncomeTax.Should().Be(2394.57m);        // 149,220.24 × 34 % − 22,000, over 12
        a.NetPay.Should().Be(12957.12m);
    }

    [Fact]
    public void The_scale_matches_the_rate_and_deduction_form_of_the_2025_finance_law()
    {
        var scale = Rates2026.ParseScale();
        (decimal income, decimal rate, decimal deduction)[] check =
            [(50000m, 10, 4000), (70000m, 20, 10000), (90000m, 30, 18000), (150000m, 34, 22000), (250000m, 37, 27400)];

        foreach (var (income, rate, deduction) in check)
            PayrollCalculator.AnnualIncomeTax(income, scale).Should().Be(income * rate / 100m - deduction);

        PayrollCalculator.AnnualIncomeTax(39000m, scale).Should().Be(0m);
    }

    [Fact]
    public void Unpaid_days_are_taken_at_one_twenty_sixth_of_the_base()
    {
        var a = Pay(5200m, new DateOnly(2026, 1, 5), variables: new PayslipVariables { AbsenceDays = 3m });

        a.AbsenceDeduction.Should().Be(600m);
        a.PaidDays.Should().Be(23m);
        a.GrossTaxable.Should().Be(4600m);
    }

    [Fact]
    public void Transport_is_exempt_up_to_its_cap_and_taxed_beyond()
    {
        var a = Pay(5000m, new DateOnly(2026, 1, 5), variables: new PayslipVariables { TransportAllowance = 700m });

        a.ExemptAllowances.Should().Be(500m);
        a.GrossTaxable.Should().Be(5200m);
        a.Gross.Should().Be(5700m);
    }

    [Fact]
    public void An_insertion_contract_bears_no_contribution_and_no_tax()
    {
        var a = Pay(4000m, new DateOnly(2026, 2, 1), contract: ContractType.Anapec);

        a.Exemption.Should().Be(PayrollExemption.InsertionContract);
        (a.CnssEmployee + a.AmoEmployee + a.IncomeTax + a.CnssEmployer + a.AmoEmployer).Should().Be(0m);
        a.NetPay.Should().Be(4000m);
    }

    [Fact]
    public void Tahfiz_leaves_the_employer_cnss_to_the_state_and_no_tax_up_to_ten_thousand()
    {
        var a = Pay(9000m, new DateOnly(2025, 6, 1), contract: ContractType.Tahfiz);

        a.CnssEmployee.Should().BeGreaterThan(0m);
        a.CnssEmployer.Should().Be(0m);
        a.TrainingTaxEmployer.Should().Be(0m);
        a.IncomeTax.Should().Be(0m);
        Pay(12000m, new DateOnly(2025, 6, 1), contract: ContractType.Tahfiz).IncomeTax.Should().BeGreaterThan(0m);
    }

    [Theory]
    [InlineData(2025, 0)]
    [InlineData(2024, 5)]
    [InlineData(2021, 10)]
    [InlineData(2014, 15)]
    [InlineData(2006, 20)]
    [InlineData(2000, 25)]
    public void Seniority_follows_article_350(int hiredYear, int rate)
    {
        PayrollCalculator.SeniorityRate(new DateOnly(hiredYear, 1, 1), PayDate).Should().Be(rate);
    }

    [Fact]
    public void A_malformed_scale_is_refused()
    {
        new PayrollParameters(2026) { IncomeTaxScale = "40000:0|30000:10|:37" }.Validate().FirstError.ResourceKey
            .Should().Be("Payroll_ScaleInvalid");
        new PayrollParameters(2026) { IncomeTaxScale = "40000:0|60000:10" }.Validate().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_closed_month_takes_no_new_payslip_and_cannot_close_twice()
    {
        var period = new PayrollPeriod(Guid.NewGuid(), 2026, 10);
        period.Close(DateTimeOffset.Now, Guid.NewGuid()).FirstError.ResourceKey.Should().Be("Payroll_Empty");

        period.Add(new Payslip(Guid.NewGuid())).IsSuccess.Should().BeTrue();
        period.Close(DateTimeOffset.Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        period.Add(new Payslip(Guid.NewGuid())).FirstError.ResourceKey.Should().Be("Payroll_Closed");
        period.Reopen().IsSuccess.Should().BeTrue();
    }
}
