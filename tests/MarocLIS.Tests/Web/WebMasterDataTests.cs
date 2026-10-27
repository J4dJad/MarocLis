using Bunit;
using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Web.Components.Screens.MasterData;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Rules = MarocLIS.Tests.Desktop.MasterDataScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Données de référence » in the browser: the six tabs shown to everyone, each kept by its own
/// right; the prescribers and the payers as the workstation lists them; the editors refusing a
/// value before the service is asked and locking what a payer's kind forbids; the Westgard rules
/// picked from their presets; a payer saved with the reason the workstation writes.
/// </summary>
public sealed class WebMasterDataTests : WebReferenceContext
{
    /// <summary>Who may read every list of the screen, before any right to change one.</summary>
    private static readonly string[] Readers =
    [
        Permissions.Equipment.View, Permissions.Quality.View, Permissions.Inventory.View, Permissions.Billing.View,
        Permissions.Orders.View, Permissions.Catalog.View
    ];

    private static string[] With(params string[] rights) => [.. Readers, .. rights];

    private static ParameterOption Parameter() => new(Guid.NewGuid(), "GLY", "GLY", new MultilingualText("Glucose"), "g/L", 2);

    private void Payers(params PayerRow[] rows) => MasterData.On(nameof(IMasterDataQueries.GetPayersAsync), _ => rows);

    [Fact]
    public void TheScreen_ShowsItsSixTabs_TheInstrumentsFirst_AndSaysWhatTheReferenceDataStillLeavesUnusable()
    {
        SignIn(AppLanguage.French, With(Permissions.Equipment.Manage));
        MasterData.On(nameof(IMasterDataQueries.GetInstrumentsAsync), _ => new[] { Rules.Instrument() });

        var page = Render<MasterDataScreen>();

        page.FindAll("[role=tab]").Should().HaveCount(6);
        page.FindAll("[data-grid=instruments] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("AU480");
        page.Find("[data-state=status]").TextContent.Should().NotBeNullOrWhiteSpace("no target, article, prescriber or payer yet");
        page.Find("[data-action='Equipment.NewInstrument']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ASecretary_SeesOnlyThePrescribersAndPayers_AndTheScreenOpensOnThePrescribers()
    {
        SignIn(AppLanguage.French, [Permissions.Orders.Create, Permissions.Billing.View]);
        MasterData.On(nameof(IMasterDataQueries.GetPrescribersAsync), _ => new[] { Rules.Prescriber() });
        Payers(Rules.Payer());

        var page = Render<MasterDataScreen>();

        page.FindAll("[role=tab]").Select(t => t.GetAttribute("data-tab")).Should().Equal("Prescribers", "Payers");
        page.Find("[role=tab][aria-selected=true]").GetAttribute("data-tab").Should().Be("Prescribers");
        page.FindAll("[data-grid=instruments]").Should().BeEmpty();
    }

    [Fact]
    public void WhoKeepsOnlyThePrescribers_SeesEveryTab_ButTheOtherTabsButtonsTurnedOff()
    {
        SignIn(AppLanguage.French, With(Permissions.Catalog.Manage));
        MasterData.On(nameof(IMasterDataQueries.GetInstrumentsAsync), _ => new[] { Rules.Instrument() });
        MasterData.On(nameof(IMasterDataQueries.GetPrescribersAsync), _ => new[] { Rules.Prescriber() });
        Payers(Rules.Payer());

        var page = Render<MasterDataScreen>();

        page.FindAll("[role=tab]").Should().HaveCount(6);
        page.Find("[data-action='Equipment.NewInstrument']").HasAttribute("disabled").Should().BeTrue("instruments are Equipment.Manage");

        page.Find("[data-tab=Prescribers]").Click();
        page.Find("[data-action='Order.NewPrescriber']").HasAttribute("disabled").Should().BeFalse();

        page.Find("[data-tab=Payers]").Click();
        page.Find("[data-action='Coverage.NewPayer']").HasAttribute("disabled").Should().BeTrue("payers are Billing.ManageTariffs");
        page.Find("[data-action='Action.Edit']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void ThePrescribersAndThePayers_AreListed_TheRateAsAPercentage_AndAPayerThatWouldMisPriceAClaimFlagged()
    {
        SignIn(AppLanguage.French, Readers);
        MasterData.On(nameof(IMasterDataQueries.GetPrescribersAsync), _ => new[] { Rules.Prescriber(inpe: "123456789") });
        Payers(Rules.Payer(), Rules.Payer(kind: PayerKind.Mutuelle, rate: 0m));

        var page = Render<MasterDataScreen>();
        page.Find("[data-tab=Prescribers]").Click();

        page.FindAll("[data-grid=prescribers] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("123456789");

        page.Find("[data-tab=Payers]").Click();
        var rows = page.FindAll("[data-grid=payers] tbody tr");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("70");
        rows[1].TextContent.Should().NotBeEmpty();
        rows[1].ClassList.Should().Contain(c => c.StartsWith("is-tone-"), "a covering payer reimbursing nothing is a problem");
    }

    [Fact]
    public void AStockLevelThatIsNoNumber_IsRefusedInTheArticleEditor_BeforeTheServiceIsAsked()
    {
        SignIn(AppLanguage.French, With(Permissions.Inventory.ManagePurchaseOrders));

        var page = Render<MasterDataScreen>();
        page.Find("[data-tab=Stock]").Click();
        page.Find("[data-action='Inventory.NewItem']").Click();

        page.Find("[data-field=code]").Input("REA-GLY");
        page.Find("[data-field=namefr]").Input("Réactif glucose");
        page.Find("[data-field=minimum]").Change("dix");
        page.Find(".modal [data-action='Action.Save']").Click();

        page.Find("[data-refusal=minimum]").TextContent.Should().Contain("dix");
        Work.CallsTo("SaveChangesAsync").Should().Be(0);
        page.FindAll(".modal").Should().NotBeEmpty("the editor stays open with what was typed");
    }

    [Fact]
    public void ChoosingSelfPay_ZeroesAndLocksTheRate_AndTurnsTheAgreementOff()
    {
        SignIn(AppLanguage.French, With(Permissions.Billing.ManageTariffs));

        var page = Render<MasterDataScreen>();
        page.Find("[data-tab=Payers]").Click();
        page.Find("[data-action='Coverage.NewPayer']").Click();

        page.Find("[data-field=tnr]").HasAttribute("disabled").Should().BeTrue("an AMO scheme settles against the TNR");
        page.Find("[data-state=amo-notice]");

        page.Find("[data-field=kind]").Change(nameof(PayerKind.SelfPay));

        page.Find("[data-field=rate]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-field=rate]").GetAttribute("value").Should().Be("0");
        page.Find("[data-field=thirdparty]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-field=agreement]").HasAttribute("disabled").Should().BeTrue();
        page.FindAll("[data-state=amo-notice]").Should().BeEmpty();
    }

    [Fact]
    public void APayerAdded_IsSaved_WithTheRateInTheReason_AndBecomesTheCurrentOne()
    {
        SignIn(AppLanguage.French, With(Permissions.Billing.ManageTariffs));

        var page = Render<MasterDataScreen>();
        page.Find("[data-tab=Payers]").Click();
        var before = MasterData.CallsTo(nameof(IMasterDataQueries.GetPayersAsync));

        page.Find("[data-action='Coverage.NewPayer']").Click();
        page.Find("[data-field=code]").Input("cnops");
        page.Find("[data-field=namefr]").Input("CNOPS — AMO");
        page.Find("[data-field=kind]").Change(nameof(PayerKind.AmoCnops));
        page.Find(".modal [data-action='Action.Save']").Click();

        PayerStore.CallsTo("AddAsync").Should().Be(1);
        Work.CallsTo("SaveChangesAsync").Should().Be(1);
        AuditReason.Should().Be("Création de l'organisme payeur CNOPS (CNOPS — AMO), taux 70 %");
        MasterData.CallsTo(nameof(IMasterDataQueries.GetPayersAsync)).Should().BeGreaterThan(before);
        page.FindAll(".modal").Should().BeEmpty();
        page.Find("[data-state=status]").TextContent.Should().Contain("CNOPS");
    }

    /// <summary>
    /// The rate is written and read as the screen writes numbers: « 12,5 » in French, as the other
    /// boxes, where it opened with « 12.5 » in a browser number box.
    /// </summary>
    [Fact]
    public void ThePayerRate_IsShownAndTypedWithTheDecimalComma()
    {
        SignIn(AppLanguage.French, With(Permissions.Billing.ManageTariffs));

        var page = Render<MasterDataScreen>();
        page.Find("[data-tab=Payers]").Click();
        page.Find("[data-action='Coverage.NewPayer']").Click();
        page.Find("[data-field=kind]").Change(nameof(PayerKind.Mutuelle));

        page.Find("[data-field=rate]").GetAttribute("type").Should().Be("text");
        page.Find("[data-field=rate]").Change("12,5");
        page.Find("[data-field=rate]").GetAttribute("value").Should().Be("12,5");
        page.Find("[data-field=rate]").Change("80.25");
        page.Find("[data-field=rate]").GetAttribute("value").Should().Be("80,25", "a point typed is read too, and shown back with the comma");
    }

    [Fact]
    public void TheWestgardRules_ArePickedFromTheirPresets_AndNoneTickedCannotBeSaved()
    {
        SignIn(AppLanguage.French, With(Permissions.Quality.ManageQcRules));
        MasterData.On(nameof(IMasterDataQueries.GetInstrumentsAsync), _ => new[] { Rules.Instrument() });
        MasterData.On(nameof(IMasterDataQueries.GetQcMaterialsAsync), _ => new[] { Rules.Material() });
        MasterData.On(nameof(IMasterDataQueries.GetParametersAsync), _ => new[] { Parameter() });

        var page = Render<MasterDataScreen>();
        page.Find("[data-tab=QualityControl]").Click();
        page.Find("[data-action='Quality.NewQcTarget']").Click();

        page.Find("[data-field=mean]").Input("1,00");
        page.Find("[data-field=sd]").Input("0,05");
        page.Find("[data-state=cv-echo]").TextContent.Should().Contain("CV 5");

        page.Find("[data-action='Quality.Rules']").TextContent.Should().Contain("6");
        page.Find("[data-action='Quality.Rules']").Click();

        var rules = page.Find("[data-modal=westgard-rules]");
        rules.QuerySelector(".modal-title")!.TextContent.Should().Contain("GLY").And.Contain("L-2026-04");
        page.Find("[data-modal=westgard-rules] [data-action='Quality.SingleRuleOnly']").Click();
        page.Find("[data-state=rule-summary]").TextContent.Should().Contain("1");

        page.Find("[data-rule=OneThreeS] input").Change(false);
        page.Find("[data-state=rule-summary]").ClassList.Should().Contain("critical");
        page.Find("[data-modal=westgard-rules] [data-action='Action.Save']").HasAttribute("disabled").Should().BeTrue();

        page.Find("[data-modal=westgard-rules] [data-action='Quality.SingleRuleOnly']").Click();
        page.Find("[data-modal=westgard-rules] [data-action='Action.Save']").Click();

        page.FindAll("[data-modal=westgard-rules]").Should().BeEmpty();
        page.Find("[data-action='Quality.Rules']").TextContent.Should().Contain("1");
    }

    [Fact]
    public void InArabic_TheTabsAreArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Readers);
        Payers(Rules.Payer());

        var page = Render<MasterDataScreen>();

        page.Find("[data-tab=Instruments]").TextContent.Trim().Should().NotBe("Instruments").And.NotContain("[");
        page.Find("[data-tab=Payers]").Click();
        page.Find("[data-grid=payers] tbody tr").TextContent.Should().Contain("الصندوق الوطني");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
