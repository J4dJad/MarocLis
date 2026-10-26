using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Assets;
using MarocLIS.Application.Administration;
using MarocLIS.Reporting;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FixedAssets = MarocLIS.Web.Components.Screens.Finance.FixedAssets;
using Rules = MarocLIS.Tests.Desktop.FinanceScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Immobilisations » in the browser: the year's register in DH with an asset sold out of it and the
/// analyser's interventions under it, an asset refused in its box then saved, a disposal refused in its
/// box then recorded, the plan by year downloaded, an account typed wrong refused in its box, an asset
/// drafted from an instrument, and each action kept by its right — through the workstation's
/// FixedAssetService.
/// </summary>
public sealed class WebFixedAssetTests : WebScreenContext
{
    private readonly Fake<IFixedAssetQueries> _queries = Fake<IFixedAssetQueries>.Create();
    private readonly Fake<IRepository<FixedAsset>> _assets = Fake<IRepository<FixedAsset>>.Create();
    private readonly Fake<IConfigurationWriter> _writer = Fake<IConfigurationWriter>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Guid _analyserId = Guid.NewGuid();
    private readonly FixedAsset _centrifuge;

    private static readonly string[] Manager = [Permissions.Assets.View, Permissions.Assets.Manage];

    private sealed class PdfStub : IListPdfRenderer
    {
        public byte[] Render(ListDocument document) => "%PDF-1.7"u8.ToArray();
    }

    public WebFixedAssetTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_assets.Object);
        Services.AddSingleton(_writer.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton<IListPdfRenderer>(new PdfStub());
        Services.AddScoped<FixedAssetService>();

        _queries.On(nameof(IFixedAssetQueries.GetAssetsAsync), _ => new[]
        {
            Rules.Source("IMM-2026-001", instrument: _analyserId),
            Rules.Source("IMM-2026-007", disposal: new DateOnly(2026, 6, 30))
        });
        _queries.On(nameof(IFixedAssetQueries.GetInstrumentSummaryAsync), _ => new AssetInstrumentSummary(
            _analyserId, "AUT-01", new MultilingualText("Automate de biochimie"), EquipmentStatus.Operational, "Beckman", "AU480",
            "SN-4471", "Plateau technique", null, null, null,
            [new AssetMaintenanceRow(new DateOnly(2026, 9, 14), MaintenanceKind.Corrective, new MultilingualText("Pompe à réactif remplacée"),
                                     6m, 4_200m, "Technicien Beckman", true)]));
        _queries.On(nameof(IFixedAssetQueries.GetOptionsAsync), _ => new FixedAssetOptions(
            [],
            [new AssetInstrumentOption(Guid.NewGuid(), "CENT-01", new MultilingualText("Centrifugeuse réfrigérée"),
                                       new DateOnly(2024, 3, 12), new DateOnly(2024, 3, 18), null, null, null)]));

        _centrifuge = new FixedAsset("IMM-2024-002", "Centrifugeuse réfrigérée", FixedAssetCategory.LaboratoryEquipment, Guid.NewGuid())
        {
            AcquisitionDate = new DateOnly(2024, 3, 12), InServiceDate = new DateOnly(2024, 3, 18),
            CostExcludingVatMad = 85_000m, VatAmountMad = 17_000m, UsefulLifeYears = 5, Method = DepreciationMethod.StraightLine
        };
        _assets.On(nameof(IRepository<FixedAsset>.GetByIdAsync), _ => _centrifuge);
    }

    private static string Mad(decimal amount) => Money.Of(amount).Format(AppLanguage.French, MarocLIS.Localization.CultureManager.Build(AppLanguage.French));

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    [Fact]
    public void TheRegister_SaysTheYearInDh_AnAssetSoldOutOfIt_AndTheAnalysersInterventions()
    {
        SignIn(AppLanguage.French, Manager);

        var page = Render<FixedAssets>();

        page.Find("[data-field=asset-year]").GetAttribute("value").Should().Be("2026");
        var kept = page.Find("[data-grid=assets] [data-row='IMM-2026-001']");
        kept.TextContent.Should().Contain(Mad(720_000m), "the VAT not recovered is in the gross value").And.Contain("Automate de biochimie");

        var sold = page.Find("[data-grid=assets] [data-row='IMM-2026-007']");
        sold.TextContent.Should().Contain("30/06/2026").And.Contain(Mad(10_000m));

        kept.Click();
        page.Find("[data-state=instrument-heading]").TextContent.Should().Contain("AUT-01");
        page.Find("[data-grid=interventions] tbody tr").TextContent.Should().Contain("Pompe à réactif remplacée").And.Contain(Mad(4_200m));

        page.Find("[data-grid=assets] [data-row='IMM-2026-007']").Click();
        page.Find("[data-action='Assets.CancelDisposal']").Should().NotBeNull("an asset already out offers its cancellation");
        page.FindAll("[data-action='Assets.Disposal']").Should().BeEmpty();
    }

    [Fact]
    public void ANewAsset_IsRefusedInItsBox_ThenSaved()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<FixedAssets>();

        page.Find("[data-action='Assets.New']").Click();
        page.Find($"[data-field={FixedAssetScreenRules.CodeField}]").Input("IMM-2026-008");
        page.Find($"[data-field={FixedAssetScreenRules.LabelField}]").Input("Hotte à flux laminaire");
        page.Find($"[data-field={FixedAssetScreenRules.CostField}]").Input("85 mille");
        page.Find("[data-modal=fixed-asset] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={FixedAssetScreenRules.CostField}]").TextContent.Should().Contain("« 85 mille »");
        page.Find($"[data-field={FixedAssetScreenRules.CostField}]").ClassName.Should().Contain("is-refused");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find($"[data-field={FixedAssetScreenRules.CostField}]").Input("85 000");
        page.Find("[data-modal=fixed-asset] [data-action='Action.Save']").Click();

        var added = (FixedAsset)_assets.Calls.Single(c => c.Method == nameof(IRepository<FixedAsset>.AddAsync)).Arguments[0]!;
        added.Code.Should().Be("IMM-2026-008");
        added.CostExcludingVatMad.Should().Be(85_000m);
        AuditReason.Should().Contain("IMM-2026-008");
        page.FindAll("[data-modal=fixed-asset]").Should().BeEmpty();
    }

    [Fact]
    public void ADisposal_IsRefusedInItsBox_ThenRecorded()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<FixedAssets>();
        page.Find("[data-grid=assets] [data-row='IMM-2026-001']").Click();

        page.Find("[data-action='Assets.Disposal']").Click();
        page.Find($"[data-field={FixedAssetScreenRules.DisposalPriceField}]").Input("cinq mille");
        page.Find($"[data-field={FixedAssetScreenRules.DisposalReasonField}]").Input("Remplacée par un modèle plus récent");
        page.Find("[data-modal=asset-disposal] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={FixedAssetScreenRules.DisposalPriceField}]").TextContent.Should().Contain("« cinq mille »");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find($"[data-field={FixedAssetScreenRules.DisposalPriceField}]").Input("5 000");
        page.Find("[data-modal=asset-disposal] [data-action='Action.Save']").Click();

        _centrifuge.DisposalDate.Should().Be(new DateOnly(2026, 10, 18));
        _centrifuge.DisposalPriceMad.Should().Be(5_000m);
        AuditReason.Should().StartWith("Sortie de l'immobilisation IMM-2024-002");
    }

    [Fact]
    public void ThePlan_IsShownByYear_AndDownloaded()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<FixedAssets>();
        page.Find("[data-grid=assets] [data-row='IMM-2026-001']").Click();

        page.Find("[data-action='Assets.Schedule']").Click();

        page.FindAll("[data-grid=schedule-years] tbody tr").Should().HaveCountGreaterThan(4);
        page.Find("[data-grid=schedule-years]").TextContent.Should().Contain("DH");
        page.Find("[data-modal=asset-schedule] [data-action='Assets.Csv']").Click();
        page.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                                               && (string)i.Arguments[0]! == "plan-amortissement-IMM-2026-001.csv"));
    }

    [Fact]
    public void AnAccountTypedWrong_IsRefusedInItsBox()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<FixedAssets>();

        page.Find("[data-action='Assets.Accounts']").Click();
        var field = FixedAssetScreenRules.AccountField(FixedAssetCategory.LaboratoryEquipment, "Depreciation");
        page.Find($"[data-field='{field}']").Input("2355");
        page.Find("[data-modal=asset-accounts] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal='{field}']").TextContent.Should().Contain("Compte invalide");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    [Fact]
    public void AnAsset_IsDraftedFromAnInstrument()
    {
        SignIn(AppLanguage.French, Manager);
        var page = Render<FixedAssets>();

        page.Find("[data-action='Assets.FromInstrument']").Click();
        page.Find("[data-grid=candidates] tbody tr").Click();
        page.Find("[data-modal=asset-candidates] [data-action='Action.Select']").Click();

        page.Find($"[data-field={FixedAssetScreenRules.CodeField}]").GetAttribute("value").Should().Be("CENT-01");
        page.Find($"[data-field={FixedAssetScreenRules.AcquiredField}]").GetAttribute("value").Should().Be("2024-03-12");
    }

    [Fact]
    public void AReader_SeesTheRegisterAndThePlan_ButChangesNothing()
    {
        SignIn(AppLanguage.French, Permissions.Assets.View);
        var page = Render<FixedAssets>();
        page.Find("[data-grid=assets] [data-row='IMM-2026-001']").Click();

        page.FindAll("[data-action='Assets.New']").Should().BeEmpty();
        page.FindAll("[data-action='Action.Edit']").Should().BeEmpty();
        page.FindAll("[data-action='Assets.Disposal']").Should().BeEmpty();
        page.FindAll("[data-action='Assets.Accounts']").Should().BeEmpty();
        page.Find("[data-action='Assets.Schedule']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Manager);
        var page = Render<FixedAssets>();

        page.Find("[data-grid=assets] [data-row='IMM-2026-001']").TextContent.Should().Contain("د.م.");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }
}
