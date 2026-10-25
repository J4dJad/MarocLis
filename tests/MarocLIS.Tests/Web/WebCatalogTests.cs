using Bunit;
using FluentAssertions;
using MarocLIS.Application.Catalog;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Catalog;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MarocLIS.Domain.Patients;
using Rules = MarocLIS.Tests.Desktop.CatalogScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Catalogue des analyses » in the browser: every tab shown to whoever may read the catalogue,
/// each action offered by its own right and by what is selected; the analysis with its parameters
/// and intervals, its tariffs, the panels, the lists, the national acts and the gaps — the
/// editors refusing a value before the service is asked, and a change recorded with the reason
/// the workstation writes.
/// </summary>
public sealed class WebCatalogTests : WebReferenceContext
{
    private static NationalActRow Act(string code, string label, decimal coefficient, int decree = 2016, int used = 0) =>
        new(Guid.NewGuid(), code, label, coefficient, "B", "Biochimie", null, decree, true, used);

    private void Catalogue(params AnalysisRow[] analyses) =>
        CatalogQueries.On(nameof(ICatalogMaintenanceQueries.GetAnalysesAsync), _ => analyses);

    [Fact]
    public void TheCatalogue_ShowsItsSevenTabs_AndTheFirstAnalysisWithItsParametersAndIntervals()
    {
        SignIn(AppLanguage.French, CatalogManager);
        var glycaemia = Rules.Analysis("GLY");
        Catalogue(glycaemia, Rules.Analysis("CHOL", tariffs: 0));
        CatalogQueries.On(nameof(ICatalogMaintenanceQueries.GetParametersAsync), _ => new[] { Rules.Parameter("GLY") });
        CatalogQueries.On(nameof(ICatalogMaintenanceQueries.GetRangesAsync), _ => new[] { Rules.Range(), Rules.Range(gender: Gender.Female) });

        var page = Render<CatalogScreen>();

        page.FindAll("[role=tab]").Should().HaveCount(7);
        page.FindAll("[data-grid=analyses] tbody tr").Should().HaveCount(2);
        page.Find("[data-grid=analyses] tr[data-row=CHOL]").ClassList.Should().Contain("is-tone-abnormal", "an analysis without a tariff cannot be ordered");
        page.Find("[data-card=analysis] .card-title").TextContent.Should().Contain("GLY");
        page.FindAll("[data-grid=parameters] tbody tr").Should().ContainSingle();
        page.FindAll("[data-grid=ranges] tbody tr").Should().HaveCount(2);
        page.Find("[data-chip=to-complete]").TextContent.Should().Contain("1");
        page.Find("[data-action='Catalog.NewAnalysis']").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-action='Catalog.NewRange']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void WhoMayOnlyRead_SeesEveryTab_ButEveryActionTurnedOff()
    {
        SignIn(AppLanguage.French, Permissions.Catalog.View);
        Catalogue(Rules.Analysis("GLY"));
        CatalogQueries.On(nameof(ICatalogMaintenanceQueries.GetParametersAsync), _ => new[] { Rules.Parameter("GLY") });
        CatalogQueries.On(nameof(ICatalogMaintenanceQueries.GetRangesAsync), _ => new[] { Rules.Range() });

        var page = Render<CatalogScreen>();

        page.FindAll("[role=tab]").Should().HaveCount(7);
        foreach (var key in new[] { "Catalog.NewAnalysis", "Catalog.EditAnalysis", "Catalog.NewParameter", "Catalog.EditRange", "Catalog.RecordReview" })
            page.Find($"[data-action='{key}']").HasAttribute("disabled").Should().BeTrue(key);

        page.Find("[data-tab=Tariffs]").Click();
        page.Find("[data-action='Catalog.NewTariff']").HasAttribute("disabled").Should().BeTrue("setting a price is Billing.ManageTariffs");
    }

    [Fact]
    public void AValueThatIsNoNumber_IsRefusedInTheAnalysisEditor_BeforeTheServiceIsAsked()
    {
        SignIn(AppLanguage.French, CatalogManager);
        Catalogue();

        var page = Render<CatalogScreen>();
        page.Find("[data-action='Catalog.NewAnalysis']").Click();

        page.Find("[data-field=code]").Input("GLY");
        page.Find("[data-field=namefr]").Input("Glycémie");
        page.Find("[data-field=volume]").Change("deux");
        page.Find(".modal [data-action='Action.Save']").Click();

        page.Find("[data-refusal=volume]").TextContent.Should().Contain("deux");
        Work.CallsTo("SaveChangesAsync").Should().Be(0);
        page.FindAll(".modal").Should().NotBeEmpty("the editor stays open with what was typed");
    }

    [Fact]
    public void WithoutTheTariffRight_TheLabPriceIsReadOnlyInTheAnalysisEditor()
    {
        SignIn(AppLanguage.French, Permissions.Catalog.View, Permissions.Catalog.Manage);
        Catalogue();

        var page = Render<CatalogScreen>();
        page.Find("[data-action='Catalog.NewAnalysis']").Click();

        page.Find("[data-card=lab-price]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void TheTariffs_OfTheAnalysis_ShowTheBaseAndTheExcess_TheClosedOnesGrey()
    {
        SignIn(AppLanguage.French, CatalogManager);
        Catalogue(Rules.Analysis("GLY"));
        CatalogQueries.On(nameof(ICatalogMaintenanceQueries.GetTariffsAsync), _ => new[]
        {
            Rules.Tariff(price: 40m, coefficient: 30m, keyValue: 1.1m),
            Rules.Tariff(from: new DateOnly(2024, 1, 1), until: new DateOnly(2025, 9, 30), price: 30m)
        });

        var page = Render<CatalogScreen>();
        page.Find("[data-tab=Tariffs]").Click();

        var rows = page.FindAll("[data-grid=tariffs] tbody tr");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("33,00").And.Contain("7,00");
        rows[1].ClassList.Should().Contain("is-tone-muted");
        page.Find("[data-action='Catalog.EditTariff']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ADepartmentAdded_IsSaved_WithTheReasonOfTheWorkstation_AndTheListsAreReadAgain()
    {
        SignIn(AppLanguage.French, CatalogManager);
        Catalogue();

        var page = Render<CatalogScreen>();
        page.Find("[data-tab=Nomenclature]").Click();
        var before = CatalogQueries.CallsTo(nameof(ICatalogMaintenanceQueries.GetNomenclatureAsync));

        page.Find("[data-action='Action.Add']").Click();
        page.Find("[data-field=code]").Input("hemato");
        page.Find("[data-field=namefr]").Input("Hématologie");
        page.Find(".modal [data-action='Action.Save']").Click();

        DepartmentStore.CallsTo("AddAsync").Should().Be(1);
        Work.CallsTo("SaveChangesAsync").Should().Be(1);
        AuditReason.Should().Be("Création de Department HEMATO");
        CatalogQueries.CallsTo(nameof(ICatalogMaintenanceQueries.GetNomenclatureAsync)).Should().BeGreaterThan(before + 1,
            "the editors' lists are read again with the tab's");
        page.Find("[data-state=status]").TextContent.Should().Contain("HEMATO");
    }

    [Fact]
    public void TheNationalActs_AreFoundByCode_AndCountedByDecree()
    {
        SignIn(AppLanguage.French, Permissions.Catalog.View);
        Catalogue();
        ActQueries.On(nameof(INationalActQueries.GetActsAsync), _ => new[]
        {
            Act("B0102", "Glucose", 30m, used: 2), Act("B0410", "Cholestérol total", 30m, decree: 2012), Act("B0510", "Ferritine", 70m)
        });

        var page = Render<CatalogScreen>();
        page.Find("[data-tab=Acts]").Click();

        page.FindAll("[data-grid=acts] tbody tr").Should().HaveCount(3);
        page.Find("[data-chip='2016']").TextContent.Should().Contain("2");
        page.Find("[data-field=act-search]").Input("0102");
        page.FindAll("[data-grid=acts] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("Glucose");
    }

    [Fact]
    public void AGapOfCoefficient_IsPutRightAfterTheQuestion_AndAGapOfCodeOpensTheEditorInstead()
    {
        SignIn(AppLanguage.French, CatalogManager);
        Catalogue();
        var differing = Guid.NewGuid();
        ActQueries.On(nameof(INationalActQueries.GetActsAsync), _ => new[] { Act("B0102", "Glucose", 30m) });
        ActQueries.On(nameof(INationalActQueries.GetAnalysisFactsAsync), _ => new[]
        {
            new NomenclatureAnalysisFact(differing, "GLY", new MultilingualText("Glycémie"), "B0102", [new NomenclatureTariffFact(null, "B", 25m)]),
            new NomenclatureAnalysisFact(Guid.NewGuid(), "VS", new MultilingualText("Vitesse de sédimentation"), null, [])
        });

        var page = Render<CatalogScreen>();
        page.Find("[data-tab=Gaps]").Click();

        page.FindAll("[data-grid=gaps] tbody tr").Should().HaveCount(2);
        page.Find("[data-grid=gaps] tr[data-row=VS]").Click();
        page.Find("[data-action='Nabm.ApplyOfficial']").HasAttribute("disabled").Should().BeTrue("a missing code needs the editor");
        page.Find("[data-action='Catalog.EditAnalysis']").HasAttribute("disabled").Should().BeFalse();

        page.Find("[data-grid=gaps] tr[data-row=GLY]").Click();
        page.Find("[data-action='Nabm.ApplyOfficial']").Click();
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("0102").And.Contain("GLY");
    }

    [Fact]
    public void InArabic_TheTabsAreArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Permissions.Catalog.View);
        Catalogue(Rules.Analysis("GLY"));

        var page = Render<CatalogScreen>();

        page.Find("[data-tab=Analyses]").TextContent.Trim().Should().NotBe("Analyses").And.NotContain("[");
        page.Find("[data-grid=analyses] .cell-name").TextContent.Should().Contain("سكر الدم");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
