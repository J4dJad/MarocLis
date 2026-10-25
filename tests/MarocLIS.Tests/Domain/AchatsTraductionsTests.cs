using FluentAssertions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The purchasing messages carry their values untranslated (an amount, a cold-chain range): the
/// screen's language writes them, so an Arabic or English screen never shows a French range or "MAD".
/// </summary>
public sealed class AchatsTraductionsTests
{
    private static readonly JsonLocalizer Localizer = new(new LocalizationOptions { MarkMissingKeys = true });

    [Theory]
    [InlineData(AppLanguage.French, "2 à 8 °C")]
    [InlineData(AppLanguage.English, "2 to 8 °C")]
    [InlineData(AppLanguage.Arabic, "من 2 إلى 8 °م")]
    public void TheMissingTemperature_NamesTheRange_InTheScreensLanguage(AppLanguage language, string range)
    {
        var error = ReceptionAssessment.Assess(StorageCondition.Refrigerated, new ReceptionCheck()).FirstError;

        var text = Localizer.Format(language, error.ResourceKey, error.Args);

        text.Should().Contain(range).And.NotContain("[").And.NotContain("Refrigerated");
        if (language != AppLanguage.French) text.Should().NotContain("à 8");
    }

    [Theory]
    [InlineData(AppLanguage.French, ColdChainRange.Room, "15 à 25 °C")]
    [InlineData(AppLanguage.English, ColdChainRange.Frozen, "≤ −15 °C")]
    [InlineData(AppLanguage.Arabic, ColdChainRange.DeepFrozen, "≤ −60 °م")]
    public void EveryRange_IsTranslated(AppLanguage language, ColdChainRange range, string expected)
    {
        foreach (var each in Enum.GetValues<ColdChainRange>())
            Localizer.Get($"ColdChainRange.{each}", language).Should().NotStartWith("[");

        Localizer.Get($"ColdChainRange.{range}", language).Should().Be(expected);
        ColdChain.Range(StorageCondition.Frozen).Should().Be(ColdChainRange.Frozen);
        ColdChain.Range(StorageCondition.RoomTemperature).Should().Be(ColdChainRange.Room);
    }

    [Theory]
    [InlineData(AppLanguage.French, "DH")]
    [InlineData(AppLanguage.English, "MAD")]
    [InlineData(AppLanguage.Arabic, "د.م.")]
    public void AnOverpayment_WritesTheOutstandingAmount_LikeEveryOtherAmount(AppLanguage language, string currency)
    {
        var error = Error.Validation("SupplierPayment_ExceedsOutstanding", Money.Of(1250.50m));

        var text = Localizer.Format(language, error.ResourceKey, error.Args);

        text.Should().Contain(currency).And.NotContain("[");
        if (language != AppLanguage.English) text.Should().NotContain("MAD");
        text.Should().Contain(Money.Of(1250.50m).Format(language,
            System.Globalization.CultureInfo.GetCultureInfo(language.ToCultureName())));
    }

    [Fact]
    public void ARefusal_KeepsTheErrorsValue_AsTheKindItIs()
    {
        FieldRefusal.Of("amount", Error.Validation("K", Money.Of(10m))).Should().Be(new FieldRefusal("amount", "K", Amount: 10m));
        FieldRefusal.Of("t", Error.Validation("K", ColdChainRange.Frozen)).Value.Should().Be(ColdChainRange.Frozen);
        FieldRefusal.Of("q", Error.Validation("K", 3)).Should().Be(new FieldRefusal("q", "K", "3"));
        FieldRefusal.Of("q", Error.Validation("K")).Should().Be(new FieldRefusal("q", "K"));
    }

    [Theory]
    [InlineData("DEMO-REA-GLU", "DEMO-REA-GLU — Glucose HK", "DEMO-REA-GLU — Glucose HK")]
    [InlineData("DEMO-REA-GLU", "Glucose HK", "DEMO-REA-GLU — Glucose HK")]
    [InlineData("GLU", "GLUCOSE oxydase", "GLU — GLUCOSE oxydase")]
    [InlineData("GLU", "GLU", "GLU")]
    [InlineData("GLU", "", "GLU")]
    [InlineData("GLU", null, "GLU")]
    public void AnInvoicedOrderLine_NamesItsCodeOnce(string code, string? description, string expected) =>
        Application.Inventory.SupplierInvoiceScreenRules.OrderLineLabel(code, description).Should().Be(expected);
}
