using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CostMargin = MarocLIS.Web.Components.Screens.Finance.CostMargin;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Coût de revient et marge » in the browser: the month's analyses with their reagent cost and
/// the indirect costs per test in DH, an analysis sold at a loss in red, the table downloaded, a
/// period typed backwards and an allocation typed wrong refused in their boxes — the allocation
/// offered only with the right to set the costs — through the workstation's CostMarginService.
/// </summary>
public sealed class WebCostMarginTests : WebScreenContext
{
    private readonly Fake<ICostQueries> _queries = Fake<ICostQueries>.Create();
    private readonly Fake<IConfigurationWriter> _writer = Fake<IConfigurationWriter>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Guid _site = Guid.NewGuid();
    private readonly Guid _hba1c = Guid.NewGuid();
    private readonly Guid _glucose = Guid.NewGuid();

    private static readonly string[] Director = [Permissions.Steering.View, Permissions.Steering.ManageCosts];

    public WebCostMarginTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_writer.Object);
        Services.AddSingleton(_work.Object);
        Services.AddScoped<CostMarginService>();

        _queries.On(nameof(ICostQueries.GetActivityAsync), _ =>
            Enumerable.Repeat(new CostActivityRow(_site, _hba1c, "HBA1C", null, null, 100m), 10)
                      .Concat(Enumerable.Repeat(new CostActivityRow(_site, _glucose, "GLY", null, null, 30m), 6)).ToList());
        _queries.On(nameof(ICostQueries.GetReagentsAsync), _ => new[]
        {
            new CostReagentItem(Guid.NewGuid(), _site, "REA-HBA1C", "HBA1C", null, 50, null, 4_650m, null),
            new CostReagentItem(Guid.NewGuid(), _site, "REA-GLU", "GLY", null, 800, null, 1_450m, null)
        });
        _queries.On(nameof(ICostQueries.GetLabelsAsync), _ => new CostLabels(
            new Dictionary<Guid, (string, MultilingualText)>
            {
                [_hba1c] = ("HBA1C", new MultilingualText("Hémoglobine glyquée", "الهيموغلوبين السكري", "Glycated haemoglobin")),
                [_glucose] = ("GLY", new MultilingualText("Glycémie à jeun", "سكر الدم", "Fasting glucose"))
            },
            new Dictionary<Guid, (string, MultilingualText)> { [_site] = ("CAS", new MultilingualText("Casablanca")) },
            new Dictionary<Guid, (string, MultilingualText)>()));
        _queries.On(nameof(ICostQueries.GetSettingValuesAsync), _ =>
            new Dictionary<string, string?> { [CostSettings.AllocationPerTestKey] = "14" });
    }

    private static string Mad(decimal amount) => Money.Of(amount).Format(AppLanguage.French, MarocLIS.Localization.CultureManager.Build(AppLanguage.French));

    [Fact]
    public void TheMonth_ShowsAnAnalysisSoldAtALossInRed_AndIsDownloaded()
    {
        SignIn(AppLanguage.French, Permissions.Steering.View);

        var page = Render<CostMargin>();

        var loss = page.Find("[data-grid=margins] [data-row=HBA1C]");
        loss.GetAttribute("data-loss").Should().Be("true");
        loss.ClassName.Should().Contain("margin-loss");
        loss.TextContent.Should().Contain("Hémoglobine glyquée").And.Contain(Mad(-70m), "93 DH of reagent and 14 DH of indirect costs on a 100 DH test");
        page.Find("[data-grid=margins] [data-row=GLY]").GetAttribute("data-loss").Should().Be("false");
        page.Find("[data-state=tiles]").TextContent.Should().Contain("DH");

        page.Find("[data-action='Statistics.ExportCsv']").Click();
        var download = JSInterop.Invocations.Single(i => i.Identifier == "marocLis.download");
        ((string)download.Arguments[0]!).Should().StartWith("marge-par-analyse-").And.EndWith(".csv");
    }

    [Fact]
    public void WithoutTheRightToSetTheCosts_TheAllocationIsNotOffered()
    {
        SignIn(AppLanguage.French, Permissions.Steering.View);

        Render<CostMargin>().FindAll("[data-action='Steering.Allocation']").Should().BeEmpty();
    }

    [Fact]
    public void AnAllocationTypedWrong_IsRefusedInItsBox_ThenSaved()
    {
        SignIn(AppLanguage.French, Director);
        var page = Render<CostMargin>();

        page.Find("[data-action='Steering.Allocation']").Click();
        page.Find($"[data-field={CostMarginScreenRules.AllocationField}]").Input("douze");
        page.Find("[data-modal=margin-allocation] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={CostMarginScreenRules.AllocationField}]").TextContent.Should().Contain("« douze »");

        page.Find($"[data-field={CostMarginScreenRules.AllocationField}]").Input("250 000");
        page.Find("[data-modal=margin-allocation] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={CostMarginScreenRules.AllocationField}]").TextContent.Should().Contain("100 000 DH");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find($"[data-field={CostMarginScreenRules.AllocationField}]").Input("12,50");
        page.Find("[data-modal=margin-allocation] [data-action='Action.Save']").Click();

        _writer.CallsTo(nameof(IConfigurationWriter.AddSettingAsync)).Should().Be(1);
        page.FindAll("[data-modal=margin-allocation]").Should().BeEmpty();
    }

    [Fact]
    public void APeriodTypedBackwards_IsRefusedInItsBox()
    {
        SignIn(AppLanguage.French, Permissions.Steering.View);
        var page = Render<CostMargin>();

        page.Find("[data-field=margin-from]").Change("2026-10-20");
        page.Find($"[data-field={CostMarginScreenRules.PeriodField}]").Change("2026-10-01");
        page.Find("[data-action='Statistics.Compute']").Click();

        page.Find($"[data-refusal={CostMarginScreenRules.PeriodField}]").TextContent.Should().NotBeEmpty();
        page.Find($"[data-filter={PeriodPreset.Custom}]").ClassName.Should().Contain("is-pressed");
    }

    [Theory]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void TheScreen_IsTranslated(AppLanguage language)
    {
        SignIn(language, Director);

        Render<CostMargin>().Markup.Should().NotContain("[[");
    }
}
