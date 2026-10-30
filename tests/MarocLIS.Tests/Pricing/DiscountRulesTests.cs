using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;
using Xunit;

namespace MarocLIS.Tests.Pricing;

/// <summary>
/// Discounts and negotiated prices: the profiles of the counter, the prices of a convention, and
/// which discount an order gets when several could apply. The precedence the laboratory chose is
/// « the highest, never two together »: a convention and a prescriber, a convention and a profile,
/// a negotiated price and the order's rate — each time the one that leaves the patient less to pay,
/// alone.
/// </summary>
public sealed class DiscountRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);
    private static readonly Guid Site = Guid.NewGuid();

    private static ActPricingInput Act(string code, decimal price, decimal? negotiated = null) => new()
    {
        AnalysisTestId = Guid.NewGuid(),
        AnalysisCode = code,
        Description = code,
        Coefficient = 10m,
        LetterKeyValue = 1.10m,
        ChargedUnitPrice = price,
        NegotiatedUnitPrice = negotiated,
        ServiceDate = Today
    };

    private static Partnership Convention(string name, decimal? percent, Guid? prescriberId = null,
                                          DateOnly? start = null, DateOnly? end = null, bool active = true, Guid? site = null) =>
        new(site ?? Site, name, PartnerKind.Clinic, PartnershipKind.CareConvention, start ?? Today.AddMonths(-6))
        {
            DiscountPercent = percent,
            PrescriberId = prescriberId,
            EndDate = end,
            IsActive = active
        };

    // ---- Profiles ------------------------------------------------------------------------------------

    [Fact]
    public void AProfile_IsOfferedWhileActiveAndWithinItsDates()
    {
        var campaign = new DiscountProfile("Campagne diabète", 30m) { ValidFrom = Today.AddDays(-5), ValidUntil = Today.AddDays(5) };

        campaign.Validate().IsSuccess.Should().BeTrue();
        campaign.IsAvailableOn(Today).Should().BeTrue();
        campaign.IsAvailableOn(Today.AddDays(6)).Should().BeFalse("the campaign is over");
        campaign.IsAvailableOn(Today.AddDays(-6)).Should().BeFalse("the campaign has not started");

        campaign.IsActive = false;
        campaign.IsAvailableOn(Today).Should().BeFalse("a profile switched off is not offered");

        new DiscountProfile("Personnel", 50m).IsAvailableOn(Today).Should().BeTrue("no dates: always");
    }

    [Fact]
    public void AProfile_NeedsAName_ARateAboveZeroUpToAHundred_AndDatesInOrder()
    {
        new DiscountProfile(" ", 0m) { ValidFrom = Today, ValidUntil = Today.AddDays(-1) }
            .Validate().Errors.Select(e => e.ResourceKey).Should()
            .BeEquivalentTo(["DiscountProfile_NameRequired", "DiscountProfile_PercentOutOfRange", "DiscountProfile_UntilBeforeFrom"]);

        new DiscountProfile("Cas social", 100m).Validate().IsSuccess.Should().BeTrue();
        new DiscountProfile("Trop", 100.5m).Validate().Errors.Single().ResourceKey.Should().Be("DiscountProfile_PercentOutOfRange");
    }

    // ---- Negotiated prices -----------------------------------------------------------------------------

    [Fact]
    public void ANegotiatedPrice_IsAFixedPriceOrARate_NeverBoth_WithinItsDates()
    {
        var analysis = Guid.NewGuid();
        var price = new PartnershipPrice(Guid.NewGuid(), analysis, Today.AddMonths(-1)) { FixedPrice = 80m };
        price.Validate().IsSuccess.Should().BeTrue();
        price.PriceFor(120m).Should().Be(80m);
        price.IsValidOn(Today).Should().BeTrue();
        price.IsValidOn(Today.AddMonths(-2)).Should().BeFalse();

        var rate = new PartnershipPrice(Guid.NewGuid(), analysis, Today) { DiscountPercent = 15m, ValidUntil = Today.AddDays(30) };
        rate.PriceFor(120m).Should().Be(102m);
        rate.IsValidOn(Today.AddDays(31)).Should().BeFalse();

        new PartnershipPrice(Guid.NewGuid(), analysis, Today) { FixedPrice = 80m, DiscountPercent = 10m }
            .Validate().Errors.Select(e => e.ResourceKey).Should().Contain("PartnershipPrice_PriceOrPercent");
        new PartnershipPrice(Guid.NewGuid(), analysis, Today)
            .Validate().Errors.Select(e => e.ResourceKey).Should().Contain("PartnershipPrice_PriceOrPercent");
        new PartnershipPrice(Guid.NewGuid(), analysis, Today) { DiscountPercent = 0m, ValidUntil = Today.AddDays(-1) }
            .Validate().Errors.Select(e => e.ResourceKey).Should()
            .Contain(["PartnershipPrice_PercentOutOfRange", "PartnershipPrice_UntilBeforeFrom"]);
        new PartnershipPrice(Guid.NewGuid(), Guid.Empty, Today) { FixedPrice = -1m }
            .Validate().Errors.Select(e => e.ResourceKey).Should()
            .Contain(["PartnershipPrice_AnalysisRequired", "PartnershipPrice_PriceOutOfRange"]);
    }

    [Fact]
    public void TwoPricesOfTheSameAnalysis_InTheSameConvention_MayNotShareADay()
    {
        var convention = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var first = new PartnershipPrice(convention, analysis, new DateOnly(2026, 1, 1)) { FixedPrice = 80m, ValidUntil = new DateOnly(2026, 6, 30) };
        var next = new PartnershipPrice(convention, analysis, new DateOnly(2026, 7, 1)) { FixedPrice = 75m };
        var overlapping = new PartnershipPrice(convention, analysis, new DateOnly(2026, 6, 30)) { FixedPrice = 70m };

        first.Overlaps(next).Should().BeFalse();
        first.Overlaps(overlapping).Should().BeTrue();
        next.Overlaps(overlapping).Should().BeTrue("an open end overlaps whatever starts after it");
        first.Overlaps(new PartnershipPrice(Guid.NewGuid(), analysis, new DateOnly(2026, 2, 1)) { FixedPrice = 1m })
             .Should().BeFalse("another convention");

        DiscountRules.NegotiatedFor([first, next], analysis, new DateOnly(2026, 3, 1))!.FixedPrice.Should().Be(80m);
        DiscountRules.NegotiatedFor([first, next], analysis, Today)!.FixedPrice.Should().Be(75m);
        DiscountRules.NegotiatedFor([first, next], Guid.NewGuid(), Today).Should().BeNull();
    }

    // ---- The calculator: a negotiated price or the order's rate, never both ----------------------------

    [Fact]
    public void WithoutARate_ANegotiatedPriceReplacesTheListPrice_AndIsShownAsTheDiscount()
    {
        var pricing = ActPricingCalculator.Price([Act("NFS", 120m, negotiated: 80m), Act("GLY", 30m)], CoverageContext.SelfPay);

        pricing.Gross.Should().Be(Money.Of(150m));
        pricing.Discount.Should().Be(Money.Of(40m));
        pricing.NegotiatedDiscount.Should().Be(Money.Of(40m));
        pricing.RateDiscount.Should().Be(Money.Zero);
        pricing.Total.Should().Be(Money.Of(110m));
        pricing.Acts[0].DiscountKind.Should().Be(ActDiscountKind.Negotiated);
        pricing.Acts[0].Net.Should().Be(Money.Of(80m));
        pricing.Acts[1].DiscountKind.Should().Be(ActDiscountKind.None);
    }

    [Fact]
    public void WithARate_EachActTakesTheLowerOfItsNegotiatedPriceAndTheRate_NeverBoth()
    {
        // NFS: 120 listed, 80 negotiated; 20 % off would leave 96 — the negotiated price wins, alone.
        // TSH: 100 listed, 90 negotiated; 20 % off leaves 80 — the rate wins, on the list price.
        // GLY: 30 listed, nothing negotiated — the rate.
        var pricing = ActPricingCalculator.Price(
            [Act("NFS", 120m, negotiated: 80m), Act("TSH", 100m, negotiated: 90m), Act("GLY", 30m)],
            CoverageContext.SelfPay, orderDiscountPercent: 20m);

        pricing.Acts.Select(a => a.Net.Amount).Should().Equal(80m, 80m, 24m);
        pricing.Acts.Select(a => a.DiscountKind).Should().Equal(ActDiscountKind.Negotiated, ActDiscountKind.Rate, ActDiscountKind.Rate);
        pricing.NegotiatedDiscount.Should().Be(Money.Of(40m));
        pricing.RateDiscount.Should().Be(Money.Of(26m), "20 % of the 130 DH the rate applies to");
        pricing.Discount.Should().Be(Money.Of(66m));
        pricing.Total.Should().Be(Money.Of(184m));
    }

    [Fact]
    public void AnEqualOutcome_KeepsTheNegotiatedPrice_WhichNeedsNoApproval()
    {
        var pricing = ActPricingCalculator.Price([Act("NFS", 100m, negotiated: 90m)], CoverageContext.SelfPay, orderDiscountPercent: 10m);

        pricing.Acts[0].DiscountKind.Should().Be(ActDiscountKind.Negotiated);
        pricing.RateDiscount.Should().Be(Money.Zero);
        pricing.Total.Should().Be(Money.Of(90m));
    }

    [Fact]
    public void ANegotiatedPriceAboveTheListPrice_IsNotCharged()
    {
        var pricing = ActPricingCalculator.Price([Act("NFS", 100m, negotiated: 130m)], CoverageContext.SelfPay);

        pricing.Total.Should().Be(Money.Of(100m));
        pricing.Discount.Should().Be(Money.Zero);
        pricing.Acts[0].DiscountKind.Should().Be(ActDiscountKind.None);
    }

    [Fact]
    public void WithoutNegotiatedPrices_TheRateIsSpreadAsBefore()
    {
        var pricing = ActPricingCalculator.Price([Act("A", 33.33m), Act("B", 66.67m)], CoverageContext.SelfPay, orderDiscountPercent: 10m);

        pricing.Discount.Should().Be(Money.Of(10m));
        pricing.RateDiscount.Should().Be(Money.Of(10m));
        pricing.Acts.Sum(a => a.Discount.Amount).Should().Be(10m);
        pricing.Acts.Should().OnlyContain(a => a.DiscountKind == ActDiscountKind.Rate);
    }

    // ---- Which convention, which rate --------------------------------------------------------------------

    [Fact]
    public void TheConvention_IsTheOneChosen_ElseThePrescribersInForce_TheHighestRateFirst()
    {
        var doctor = Guid.NewGuid();
        var low = Convention("Clinique A", 5m, doctor);
        var high = Convention("Clinique B", 12m, doctor);
        var ended = Convention("Clinique C", 40m, doctor, end: Today.AddDays(-1));
        var stopped = Convention("Clinique D", 30m, doctor, active: false);
        var elsewhere = Convention("Clinique E", 35m, doctor, site: Guid.NewGuid());
        var company = Convention("Société F", 20m);
        List<Partnership> all = [low, high, ended, stopped, elsewhere, company];

        DiscountRules.ConventionFor(all, Site, doctor, null, false, Today).Should().BeSameAs(high);
        DiscountRules.ConventionFor(all, Site, null, null, false, Today).Should().BeNull("no prescriber, nothing chosen");
        DiscountRules.ConventionFor(all, Site, doctor, company.Id, false, Today).Should().BeSameAs(company);
        DiscountRules.ConventionFor(all, Site, doctor, ended.Id, false, Today).Should().BeNull("a convention ended is not in force");
        DiscountRules.ConventionFor(all, Site, doctor, null, true, Today).Should().BeNull("« Sans convention »");
    }

    [Fact]
    public void TheAutomaticRate_IsTheHigherOfTheConventionsAndThePrescribers_TheConventionOnATie()
    {
        var doctor = new Prescriber("ALAMI", "Karim", PrescriberKind.GeneralPractitioner) { DiscountPercent = 10m };
        var convention = Convention("Clinique Al Amal", 15m, doctor.Id);

        var both = DiscountRules.Automatic(convention, doctor, Today)!;
        both.Source.Should().Be(DiscountSource.Convention);
        both.Percent.Should().Be(15m);
        both.Label.Should().Be("Clinique Al Amal");
        both.PartnershipId.Should().Be(convention.Id);

        doctor.DiscountPercent = 20m;
        DiscountRules.Automatic(convention, doctor, Today)!.Should().Match<DiscountOffer>(o => o.Source == DiscountSource.Prescriber && o.Percent == 20m);

        doctor.DiscountPercent = 15m;
        DiscountRules.Automatic(convention, doctor, Today)!.Source.Should().Be(DiscountSource.Convention);

        doctor.IsActive = false;
        convention.DiscountPercent = null;
        DiscountRules.Automatic(convention, doctor, Today).Should().BeNull("a prescriber no longer active gives nothing, nor a convention without a rate");
        DiscountRules.Automatic(null, null, Today).Should().BeNull();
    }

    [Fact]
    public void TheOrdersRate_IsTheHigherOfTheAutomaticAndTheCounters_NeverTheirSum()
    {
        var convention = new DiscountOffer(DiscountSource.Convention, 10m, "Clinique");
        var staff = new DiscountOffer(DiscountSource.Profile, 50m, "Personnel", ProfileId: Guid.NewGuid());
        var small = new DiscountOffer(DiscountSource.Manual, 5m, string.Empty);

        DiscountRules.Applied(convention, staff).Should().BeSameAs(staff);
        DiscountRules.Applied(convention, small).Should().BeSameAs(convention);
        DiscountRules.Applied(convention, convention with { Source = DiscountSource.Manual }).Should().BeSameAs(convention, "a tie keeps the convention");
        DiscountRules.Applied(null, small).Should().BeSameAs(small);
        DiscountRules.Applied(convention, null).Should().BeSameAs(convention);
        DiscountRules.Applied(null, null).Should().BeNull();
    }

    [Fact]
    public void OnlyTheCountersRate_IsHeldToTheCeiling_OnThePartItGives()
    {
        var cap = new DiscountCapSettings { MaxPercent = 10m };
        var pricing = ActPricingCalculator.Price([Act("NFS", 120m, negotiated: 60m), Act("GLY", 100m)], CoverageContext.SelfPay, 15m);
        // 15 % of the 100 DH the rate applies to is 15 DH, under 10 % of 220 DH; the negotiated 60 DH does not count.
        pricing.RateDiscount.Should().Be(Money.Of(15m));

        DiscountRules.NeedsApproval(new DiscountOffer(DiscountSource.Profile, 15m, "Famille"), pricing, cap).Should().BeFalse();

        var above = ActPricingCalculator.Price([Act("GLY", 100m)], CoverageContext.SelfPay, 15m);
        DiscountRules.NeedsApproval(new DiscountOffer(DiscountSource.Profile, 15m, "Famille"), above, cap).Should().BeTrue();
        DiscountRules.NeedsApproval(new DiscountOffer(DiscountSource.Manual, 15m, string.Empty), above, cap).Should().BeTrue();
        DiscountRules.NeedsApproval(new DiscountOffer(DiscountSource.Convention, 15m, "Clinique"), above, cap).Should().BeFalse("the convention was agreed by the laboratory");
        DiscountRules.NeedsApproval(new DiscountOffer(DiscountSource.Prescriber, 15m, "Dr"), above, cap).Should().BeFalse();
        DiscountRules.NeedsApproval(null, above, cap).Should().BeFalse();
    }

    [Fact]
    public void AnAutomaticDiscount_IsRecordedWithItsSource()
    {
        DiscountRules.ReasonFor(new DiscountOffer(DiscountSource.Convention, 10m, "Clinique Al Amal")).Should().Be("Convention : Clinique Al Amal");
        DiscountRules.ReasonFor(new DiscountOffer(DiscountSource.Prescriber, 10m, "Dr ALAMI Karim")).Should().Be("Prescripteur : Dr ALAMI Karim");
    }
}
