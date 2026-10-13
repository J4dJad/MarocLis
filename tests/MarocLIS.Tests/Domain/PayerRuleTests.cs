using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Rules a payer record has to satisfy before it can price an invoice.
/// <para>
/// These are worth testing because of what consumes them. <c>ActPricingCalculator</c> multiplies a
/// reimbursement base by <c>DefaultReimbursementRate</c> and treats the result as money owed by the
/// payer, with the remainder owed by the patient. Every rule here protects that arithmetic from a
/// value an administrator could plausibly type.
/// </para>
/// </summary>
public sealed class PayerRuleTests
{
    private static Payer Valid(PayerKind kind = PayerKind.PrivateInsurer) =>
        new("TEST", new MultilingualText("Assurance test"), kind)
        {
            DefaultReimbursementRate = 0.70m,
            UsesNationalReferenceTariff = true,
            PaymentTermsDays = 60
        };

    // ---- The rate is a fraction, not a percentage -------------------------------------------------

    /// <summary>
    /// The single most costly thing that can be typed into this record. "70" meaning 70 % would
    /// bill the payer seventy times the reimbursement base, and nothing downstream re-checks it.
    /// </summary>
    [Theory]
    [InlineData(1.01)]
    [InlineData(70)]
    [InlineData(100)]
    [InlineData(-0.1)]
    public void ARateOutsideZeroToOne_IsRefused(decimal rate)
    {
        var payer = Valid();
        payer.DefaultReimbursementRate = rate;

        payer.Validate().Errors
             .Should().Contain(e => e.ResourceKey == "Payer_RateOutOfRange");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.70)]
    [InlineData(1)]
    public void ARateWithinZeroToOne_IsAccepted(decimal rate)
    {
        var payer = Valid();
        payer.DefaultReimbursementRate = rate;

        payer.Validate().IsSuccess.Should().BeTrue();
    }

    // ---- AMO settles on the national reference tariff ---------------------------------------------

    [Theory]
    [InlineData(PayerKind.AmoCnss)]
    [InlineData(PayerKind.AmoCnops)]
    [InlineData(PayerKind.AmoTadamon)]
    [InlineData(PayerKind.AmoAchamil)]
    public void AnAmoScheme_CannotBePricedOnTheLaboratoryPriceList(PayerKind kind)
    {
        // Pricing AMO off the charged price overstates the payer's liability and understates the
        // patient's ticket modérateur — and the payer rejects the claim weeks later.
        var payer = Valid(kind);
        payer.UsesNationalReferenceTariff = false;

        payer.Validate().Errors
             .Should().Contain(e => e.ResourceKey == "Payer_AmoRequiresReferenceTariff");
    }

    [Fact]
    public void ANonAmoPayer_MayBePricedOnTheLaboratoryPriceList()
    {
        // A private insurer or an employer under convention settles on what was charged. Only the
        // compulsory schemes are tied to the TNR.
        var payer = Valid(PayerKind.PrivateInsurer);
        payer.UsesNationalReferenceTariff = false;

        payer.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void IsCompulsoryScheme_CoversExactlyTheFourAmoKinds()
    {
        var compulsory = Enum.GetValues<PayerKind>()
            .Where(k => new Payer("X", new MultilingualText("X"), k).IsCompulsoryScheme)
            .ToList();

        compulsory.Should().BeEquivalentTo(new[]
        {
            PayerKind.AmoCnss, PayerKind.AmoCnops, PayerKind.AmoTadamon, PayerKind.AmoAchamil
        });
    }

    // ---- Self-pay ---------------------------------------------------------------------------------

    [Fact]
    public void SelfPay_CannotReimburseAnything()
    {
        var payer = Valid(PayerKind.SelfPay);
        payer.DefaultReimbursementRate = 0.70m;

        payer.Validate().Errors
             .Should().Contain(e => e.ResourceKey == "Payer_SelfPayRateMustBeZero");
    }

    [Fact]
    public void SelfPay_CannotCarryAThirdPartyAgreement()
    {
        var payer = Valid(PayerKind.SelfPay);
        payer.DefaultReimbursementRate = 0m;
        payer.ThirdPartyPaymentAgreement = true;

        payer.Validate().Errors
             .Should().Contain(e => e.ResourceKey == "Payer_SelfPayCannotHaveAgreement");
    }

    [Fact]
    public void SelfPay_WithNoRateAndNoAgreement_IsValid()
    {
        var payer = Valid(PayerKind.SelfPay);
        payer.DefaultReimbursementRate = 0m;

        payer.Validate().IsSuccess.Should().BeTrue();
    }

    // ---- Settlement delay and identifiers ---------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public void PaymentTermsOutsideAYear_AreRefused(int days)
    {
        var payer = Valid();
        payer.PaymentTermsDays = days;

        payer.Validate().Errors
             .Should().Contain(e => e.ResourceKey == "Payer_PaymentTermsOutOfRange");
    }

    [Fact]
    public void AMalformedIce_IsRefused()
    {
        var payer = Valid();
        payer.Ice = "12345";

        payer.Validate().Errors.Should().Contain(e => e.ResourceKey == "Payer_IceInvalid");
    }

    [Fact]
    public void AMissingFrenchName_IsRefused()
    {
        // French is the reference language: every other catalogue falls back to it, so a payer
        // without one has no name at all on a report printed in Arabic.
        var payer = new Payer("TEST", new MultilingualText(string.Empty), PayerKind.Mutuelle);

        payer.Validate().Errors
             .Should().Contain(e => e.ResourceKey == "Payer_NameFrenchRequired");
    }

    // ---- The agreement is reported, not refused ---------------------------------------------------

    [Fact]
    public void AThirdPartyAgreementWithoutANumber_IsAllowed()
    {
        // Deliberate. For CNSS and CNOPS tiers payant is a national arrangement rather than a
        // numbered per-laboratory convention, and the identifier a claim carries is the
        // biologist's INPE — so this is the normal state of most payers, including every row the
        // seeder ships. PayerRow.Problems surfaces it on the maintenance grid instead.
        var payer = Valid(PayerKind.AmoCnss);
        payer.ThirdPartyPaymentAgreement = true;
        payer.AgreementNumber = null;

        payer.Validate().IsSuccess.Should().BeTrue();
    }

    // ---- Agreement validity -----------------------------------------------------------------------

    [Fact]
    public void AnAgreementIsValidUntilItsEndDateInclusive()
    {
        var payer = Valid();
        payer.ThirdPartyPaymentAgreement = true;
        payer.AgreementValidUntil = new DateOnly(2026, 9, 30);

        payer.IsAgreementValid(new DateOnly(2026, 9, 30)).Should().BeTrue();
        payer.IsAgreementValid(new DateOnly(2026, 10, 1)).Should().BeFalse();
    }

    // ---- The shipped reference data ---------------------------------------------------------------

    /// <summary>
    /// Every payer a new installation starts with has to satisfy the rules the editor enforces.
    /// <para>
    /// Asserted against the real seeder rather than a copy of its values, because a copy is the
    /// thing that goes stale. Without this, reference data that violates its own invariants would
    /// surface the first time an administrator opened one of these records and pressed Save — and
    /// the first such record is CNSS, which is most of a laboratory's revenue.
    /// </para>
    /// </summary>
    [Fact]
    public void EverySeededPayer_SatisfiesItsOwnRules()
    {
        var seeded = MarocLIS.Infrastructure.Persistence.ReferenceDataSeeder.BuildSeedPayers();

        seeded.Should().NotBeEmpty();

        foreach (var payer in seeded)
        {
            var result = payer.Validate();

            result.IsSuccess.Should().BeTrue(
                "the seeded payer {0} must be valid, but: {1}",
                payer.Code,
                string.Join(", ", result.Errors.Select(e => e.ResourceKey)));
        }
    }

    [Fact]
    public void TheSeededSelfPayPayer_ReimbursesNothing()
    {
        var direct = MarocLIS.Infrastructure.Persistence.ReferenceDataSeeder.BuildSeedPayers()
            .Single(p => p.Kind == PayerKind.SelfPay);

        direct.DefaultReimbursementRate.Should().Be(0m);
        direct.ThirdPartyPaymentAgreement.Should().BeFalse();
        direct.PaymentTermsDays.Should().Be(0, "nothing is owed later when the patient pays now");
    }

    [Fact]
    public void EverySeededAmoScheme_IsPricedOnTheReferenceTariff()
    {
        var amo = MarocLIS.Infrastructure.Persistence.ReferenceDataSeeder.BuildSeedPayers()
            .Where(p => p.IsCompulsoryScheme)
            .ToList();

        amo.Should().HaveCount(4);
        amo.Should().OnlyContain(p => p.UsesNationalReferenceTariff);
        amo.Should().OnlyContain(p => p.RequiresInpeOnClaim);
    }

    [Fact]
    public void APayerWithNoAgreement_IsNeverReportedAsExpired()
    {
        // "No convention" and "expired convention" are different states. A payer that settles
        // directly has nothing to expire, and flagging it would train the operator to ignore the
        // column.
        var payer = Valid();
        payer.ThirdPartyPaymentAgreement = false;
        payer.AgreementValidUntil = new DateOnly(2000, 1, 1);

        payer.IsAgreementValid(new DateOnly(2026, 9, 30)).Should().BeTrue();
    }
}
