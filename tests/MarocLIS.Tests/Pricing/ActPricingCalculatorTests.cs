using FluentAssertions;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Pricing;

/// <summary>
/// Tests for the AMO / TNR pricing split.
/// <para>
/// The behaviour that matters and is easy to get wrong: the payer's share is computed on the
/// <b>national reference base</b> (coefficient × letter-key value), not on what the laboratory
/// charged. A laboratory billing above the reference tariff does not increase the payer's
/// liability — it increases the patient's. Every test below is written so that a change which
/// reverses that would fail loudly.
/// </para>
/// </summary>
public sealed class ActPricingCalculatorTests
{
    /// <summary>A 1.50 MAD letter-key value is used throughout, so the arithmetic is checkable by hand.</summary>
    private const decimal LetterKeyValue = 1.50m;

    private static ActPricingInput Act(string code, decimal coefficient, decimal chargedPrice,
                                        bool vatExempt = true, decimal vatRate = 0m,
                                        int quantity = 1, bool chargeable = true) => new()
    {
        AnalysisTestId = Guid.NewGuid(),
        AnalysisCode = code,
        Description = code,
        Coefficient = coefficient,
        LetterKeyValue = LetterKeyValue,
        ChargedUnitPrice = chargedPrice,
        IsVatExempt = vatExempt,
        VatRate = vatRate,
        Quantity = quantity,
        IsChargeable = chargeable,
        ServiceDate = new DateOnly(2026, 9, 15),
        NationalActCode = code
    };

    private static CoverageContext Amo(decimal rate = 0.70m, bool directBilling = true) => new()
    {
        PayerId = Guid.NewGuid(),
        ReimbursementRate = rate,
        UsesReferenceTariff = true,
        DirectBilling = directBilling
    };

    // ---- Self-pay ------------------------------------------------------------------------------

    [Fact]
    public void SelfPay_PutsTheWholeAmountOnThePatient()
    {
        var pricing = ActPricingCalculator.Price(
            [Act("GLY", 5m, 30m)], CoverageContext.SelfPay);

        pricing.Total.Should().Be(Money.Of(30m));
        pricing.PayerShare.Should().Be(Money.Zero);
        pricing.PatientShare.Should().Be(Money.Of(30m));
        pricing.DueAtCounter.Should().Be(Money.Of(30m));
    }

    [Fact]
    public void NoChargeableActs_YieldsAnEmptyPricing()
    {
        var pricing = ActPricingCalculator.Price(
            [Act("REFLEX", 5m, 30m, chargeable: false)], CoverageContext.SelfPay);

        pricing.Acts.Should().BeEmpty();
        pricing.Total.Should().Be(Money.Zero);
    }

    // ---- The core AMO rule ---------------------------------------------------------------------

    [Fact]
    public void PayerShare_IsComputedOnTheReferenceBase_NotOnTheChargedPrice()
    {
        // Coefficient 20 × 1.50 = 30.00 reference base; the laboratory charges 45.00.
        // At 70 %, the payer owes 70 % of 30.00 = 21.00 — never 70 % of 45.00 = 31.50.
        var pricing = ActPricingCalculator.Price([Act("TSH", 20m, 45m)], Amo());

        var act = pricing.Acts.Single();

        act.ReferenceBase.Should().Be(Money.Of(30m));
        act.PayerShare.Should().Be(Money.Of(21m));
        act.PatientShare.Should().Be(Money.Of(24m));
    }

    [Fact]
    public void ExcessOverTheReferenceTariff_IsBorneEntirelyByThePatient()
    {
        // The 15.00 charged above the reference base is the figure a reception clerk is most often
        // asked to explain, so it is reported as its own number.
        var pricing = ActPricingCalculator.Price([Act("TSH", 20m, 45m)], Amo());

        pricing.ExcessOverReferenceTariff.Should().Be(Money.Of(15m));
        pricing.Acts.Single().ExcessOverReferenceTariff.Should().Be(Money.Of(15m));
    }

    [Fact]
    public void ChargingAtTheReferenceTariff_LeavesOnlyTheTicketModerateur()
    {
        // Coefficient 20 × 1.50 = 30.00, charged at 30.00, reimbursed at 70 %:
        // the patient owes the 30 % ticket modérateur and nothing more.
        var pricing = ActPricingCalculator.Price([Act("TSH", 20m, 30m)], Amo());

        pricing.PayerShare.Should().Be(Money.Of(21m));
        pricing.PatientShare.Should().Be(Money.Of(9m));
        pricing.ExcessOverReferenceTariff.Should().Be(Money.Zero);
    }

    [Fact]
    public void FullCoverage_OnAnActChargedAtTariff_LeavesThePatientNothingToPay()
    {
        // An ALD file with a valid prior agreement is reimbursed at 100 %.
        var pricing = ActPricingCalculator.Price([Act("GLY", 5m, 7.50m)], Amo(rate: 1.0m));

        pricing.PayerShare.Should().Be(Money.Of(7.50m));
        pricing.PatientShare.Should().Be(Money.Zero);
        pricing.DueAtCounter.Should().Be(Money.Zero);
    }

    [Fact]
    public void FullCoverage_CannotProduceACreditWhenTheBaseExceedsTheChargedPrice()
    {
        // Reference base 30.00, charged 20.00, reimbursed at 100 %. The payer's share is capped at
        // the act's own total: without the cap this would hand the patient a 10.00 credit.
        var pricing = ActPricingCalculator.Price([Act("TSH", 20m, 20m)], Amo(rate: 1.0m));

        pricing.PayerShare.Should().Be(Money.Of(20m));
        pricing.PatientShare.Should().Be(Money.Zero);
        pricing.PatientShare.IsNegative.Should().BeFalse();
    }

    [Fact]
    public void WithoutThirdPartyAgreement_ThePatientAdvancesTheWholeAmount()
    {
        // The entitlement still exists; the laboratory simply cannot bill the payer directly, so
        // the patient pays and claims it back themselves.
        var pricing = ActPricingCalculator.Price(
            [Act("TSH", 20m, 45m)], Amo(directBilling: false));

        pricing.PayerShare.Should().Be(Money.Of(21m));
        pricing.PatientShare.Should().Be(Money.Of(24m));
        pricing.DueAtCounter.Should().Be(Money.Of(45m));
    }

    [Fact]
    public void PayerNotUsingTheReferenceTariff_IsBilledOnTheLaboratoryPrice()
    {
        // A complementary insurer or an employer under convention settles on the invoice amount,
        // not on the TNR.
        var coverage = new CoverageContext
        {
            PayerId = Guid.NewGuid(),
            ReimbursementRate = 0.80m,
            UsesReferenceTariff = false,
            DirectBilling = true
        };

        var pricing = ActPricingCalculator.Price([Act("TSH", 20m, 45m)], coverage);

        pricing.PayerShare.Should().Be(Money.Of(36m));
        pricing.PatientShare.Should().Be(Money.Of(9m));
    }

    // ---- VAT -------------------------------------------------------------------------------------

    [Fact]
    public void BiologyActs_AreVatExempt()
    {
        var pricing = ActPricingCalculator.Price([Act("GLY", 5m, 30m)], CoverageContext.SelfPay);

        pricing.Vat.Should().Be(Money.Zero);
        pricing.Total.Should().Be(pricing.Net);
    }

    [Fact]
    public void NonExemptItems_CarryVatOnTheDiscountedBase()
    {
        // A laboratory also sells non-exempt items through the same invoice — a certificate, a
        // consumable — so exemption is per line, not per invoice.
        var pricing = ActPricingCalculator.Price(
            [Act("CERTIF", 0m, 100m, vatExempt: false, vatRate: 0.20m)],
            CoverageContext.SelfPay);

        pricing.Net.Should().Be(Money.Of(100m));
        pricing.Vat.Should().Be(Money.Of(20m));
        pricing.Total.Should().Be(Money.Of(120m));
    }

    [Fact]
    public void MixedInvoice_TaxesOnlyTheNonExemptLines()
    {
        var pricing = ActPricingCalculator.Price(
            [
                Act("GLY", 5m, 30m),
                Act("CERTIF", 0m, 100m, vatExempt: false, vatRate: 0.20m)
            ],
            CoverageContext.SelfPay);

        pricing.Net.Should().Be(Money.Of(130m));
        pricing.Vat.Should().Be(Money.Of(20m));
        pricing.Total.Should().Be(Money.Of(150m));
    }

    // ---- Discounts and allocation ----------------------------------------------------------------

    [Fact]
    public void PercentageDiscount_IsSpreadAcrossTheActsInProportion()
    {
        var pricing = ActPricingCalculator.Price(
            [Act("A", 5m, 100m), Act("B", 5m, 300m)],
            CoverageContext.SelfPay,
            orderDiscountPercent: 10m);

        pricing.Gross.Should().Be(Money.Of(400m));
        pricing.Discount.Should().Be(Money.Of(40m));
        pricing.Net.Should().Be(Money.Of(360m));

        pricing.Acts[0].Discount.Should().Be(Money.Of(10m));
        pricing.Acts[1].Discount.Should().Be(Money.Of(30m));
    }

    [Fact]
    public void DiscountAllocation_NeverLosesOrInventsACentime()
    {
        // Three equal acts and a discount that does not divide by three: the rounding remainder
        // has to land somewhere, and the parts must still add back to the whole.
        var pricing = ActPricingCalculator.Price(
            [Act("A", 5m, 10m), Act("B", 5m, 10m), Act("C", 5m, 10m)],
            CoverageContext.SelfPay,
            fixedDiscountAmount: 10m);

        pricing.Discount.Should().Be(Money.Of(10m));

        var allocated = Money.Of(pricing.Acts.Sum(a => a.Discount.Amount));
        allocated.Should().Be(pricing.Discount);

        var net = Money.Of(pricing.Acts.Sum(a => a.Net.Amount));
        net.Should().Be(pricing.Net);
    }

    [Fact]
    public void DiscountLargerThanTheGross_IsClampedToTheGross()
    {
        var pricing = ActPricingCalculator.Price(
            [Act("A", 5m, 50m)], CoverageContext.SelfPay, fixedDiscountAmount: 200m);

        pricing.Discount.Should().Be(Money.Of(50m));
        pricing.Net.Should().Be(Money.Zero);
        pricing.Total.Should().Be(Money.Zero);
    }

    [Fact]
    public void NegativeDiscount_IsTreatedAsZero()
    {
        var pricing = ActPricingCalculator.Price(
            [Act("A", 5m, 50m)], CoverageContext.SelfPay, fixedDiscountAmount: -20m);

        pricing.Discount.Should().Be(Money.Zero);
        pricing.Net.Should().Be(Money.Of(50m));
    }

    // ---- Quantity and totals ----------------------------------------------------------------------

    [Fact]
    public void Quantity_MultipliesBothTheChargedPriceAndTheReferenceBase()
    {
        var pricing = ActPricingCalculator.Price(
            [Act("HEMOC", 10m, 20m, quantity: 3)], Amo());

        var act = pricing.Acts.Single();

        act.Gross.Should().Be(Money.Of(60m));
        act.ReferenceBase.Should().Be(Money.Of(45m));       // 10 × 1.50 × 3
        act.PayerShare.Should().Be(Money.Of(31.50m));       // 70 % of 45.00
        act.PatientShare.Should().Be(Money.Of(28.50m));
    }

    [Fact]
    public void OrderTotals_AreTheSumOfTheActTotals()
    {
        var pricing = ActPricingCalculator.Price(
            [Act("A", 5m, 33.33m), Act("B", 7m, 66.67m), Act("C", 3m, 10.01m)],
            Amo());

        pricing.Gross.Should().Be(Money.Of(110.01m));
        Money.Of(pricing.Acts.Sum(a => a.Total.Amount)).Should().Be(pricing.Total);
        Money.Of(pricing.Acts.Sum(a => a.PayerShare.Amount)).Should().Be(pricing.PayerShare);
        (pricing.PayerShare + pricing.PatientShare).Should().Be(pricing.Total);
    }

    [Fact]
    public void AppliedRate_IsRecordedOnEachActForTheClaim()
    {
        var pricing = ActPricingCalculator.Price([Act("GLY", 5m, 10m)], Amo(rate: 0.70m));

        pricing.Acts.Single().AppliedRate.Should().Be(0.70m);
    }

    [Fact]
    public void SelfPay_RecordsNoAppliedRate()
    {
        var pricing = ActPricingCalculator.Price([Act("GLY", 5m, 10m)], CoverageContext.SelfPay);

        pricing.Acts.Single().AppliedRate.Should().BeNull();
    }
}

/// <summary>
/// Tests for dirham arithmetic. Money is a value type over <c>decimal</c> with away-from-zero
/// rounding to the centime, which is what the tax administration expects on an invoice.
/// </summary>
public sealed class MoneyTests
{
    [Theory]
    [InlineData(10.005, 10.01)]     // away from zero, not banker's rounding
    [InlineData(10.004, 10.00)]
    [InlineData(-10.005, -10.01)]
    [InlineData(2.675, 2.68)]
    public void Rounding_IsHalfAwayFromZeroToTheCentime(decimal input, decimal expected) =>
        Money.Of(input).Amount.Should().Be(expected);

    [Fact]
    public void Allocate_SplitsWithoutLosingACentime()
    {
        var parts = Money.Of(10m).Allocate(3);

        parts.Should().HaveCount(3);
        Money.Of(parts.Sum(p => p.Amount)).Should().Be(Money.Of(10m));

        // The remainder goes to the earliest shares, deterministically.
        parts[0].Amount.Should().Be(3.34m);
        parts[1].Amount.Should().Be(3.33m);
        parts[2].Amount.Should().Be(3.33m);
    }

    [Fact]
    public void Allocate_HandlesAnAmountSmallerThanTheNumberOfParts()
    {
        var parts = Money.Of(0.02m).Allocate(5);

        Money.Of(parts.Sum(p => p.Amount)).Should().Be(Money.Of(0.02m));
        parts.Count(p => p.Amount == 0.01m).Should().Be(2);
        parts.Count(p => p.IsZero).Should().Be(3);
    }

    [Fact]
    public void Allocate_RejectsANonPositivePartCount()
    {
        var act = () => Money.Of(10m).Allocate(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Arithmetic_StaysRoundedToTheCentime()
    {
        var sum = Money.Of(0.015m) + Money.Of(0.015m);
        sum.Amount.Should().Be(0.04m);      // 0.02 + 0.02, each rounded on construction
    }

    [Theory]
    [InlineData(AppLanguage.French, "DH")]
    [InlineData(AppLanguage.English, "MAD")]
    public void Format_UsesTheExpectedCurrencyToken(AppLanguage language, string token) =>
        Money.Of(1234.5m).Format(language).Should().Contain(token);

    [Fact]
    public void Format_InArabic_UsesTheArabicDirhamSymbol() =>
        Money.Of(1234.5m).Format(AppLanguage.Arabic).Should().Contain("د.م.");
}
