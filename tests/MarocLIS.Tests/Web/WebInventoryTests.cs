using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Web.Components.Screens.Stock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Rules = MarocLIS.Tests.Desktop.InventoryScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Stocks » in the browser: the articles in the alarm's colours with their glyphs and the lots of
/// the one selected, a delivery refused box by box before anything is sent, a failed check asked
/// about and « Non » back to the delivery as typed, a delivery received into quarantine, an issue
/// that reads no number refused in its box, a lot released on its evidence, and each operation kept
/// by the right the workstation asks — all through the workstation's InventoryService.
/// </summary>
public sealed class WebInventoryTests : WebScreenContext
{
    private readonly Fake<IInventoryQueries> _queries = Fake<IInventoryQueries>.Create();
    private readonly Fake<IRepository<StockItem>> _items = Fake<IRepository<StockItem>>.Create();
    private readonly Fake<IRepository<StockMovement>> _movements = Fake<IRepository<StockMovement>>.Create();
    private readonly Fake<IRepository<Supplier>> _suppliers = Fake<IRepository<Supplier>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    public WebInventoryTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_items.Object);
        Services.AddSingleton(_movements.Object);
        Services.AddSingleton(_suppliers.Object);
        Services.AddSingleton(_work.Object);
        Services.AddScoped<InventoryService>();
    }

    private static readonly string[] Storeman =
    [
        Permissions.Inventory.View, Permissions.Inventory.Receive, Permissions.Inventory.Issue, Permissions.Inventory.Adjust
    ];

    private static readonly string[] Reviewer = [Permissions.Inventory.View, Permissions.Quality.ReviewQc];

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    /// <summary>The glucose reagent below its safety stock, the chemistry control at its reorder point, the tubes plain.</summary>
    private (StockRow Glucose, StockRow Control, StockRow Tubes) Stock(StorageCondition glucose = StorageCondition.Refrigerated)
    {
        var rows = (Rules.Item("BIO-GLU", onHand: 1m, belowMinimum: true, critical: true, quarantined: 1, days: 12, condition: glucose),
                    Rules.Item("CQ-CHIM", reorder: true, name: "Contrôle chimie N1/N2", days: 90),
                    Rules.Item("TUB-EDTA", name: "Tubes EDTA 4 mL", days: 400, condition: StorageCondition.RoomTemperature));
        _queries.On(nameof(IInventoryQueries.GetStockAsync), _ => new[] { rows.Item1, rows.Item2, rows.Item3 });
        _queries.On(nameof(IInventoryQueries.GetLotsAsync), args => (bool)args[2]!
            ? new[] { Rules.Lot("GLU-2611", quarantined: true, itemId: rows.Item1.StockItemId) }
            : (Guid?)args[1] == rows.Item1.StockItemId
                ? new[] { Rules.Lot("GLU-2611", quarantined: true, itemId: rows.Item1.StockItemId), Rules.Lot("GLU-2603", days: 12, itemId: rows.Item1.StockItemId) }
                : new[] { Rules.Lot("CQ-0912", itemId: rows.Item2.StockItemId, item: "CQ-CHIM") });
        return rows;
    }

    [Fact]
    public void TheArticles_InTheAlarmsColours_AndTheLotsOfTheOneSelected()
    {
        SignIn(AppLanguage.French, Storeman);
        var (glucose, control, _) = Stock();

        var page = Render<InventoryScreen>();

        page.FindAll("[data-grid=stock] tbody tr").Should().HaveCount(3);
        var first = page.Find("[data-row='BIO-GLU']");
        first.ClassName.Should().Contain("stock-alarm-strong", "a critical article below its safety stock stops an analysis");
        first.QuerySelector(".stock-glyphs")!.TextContent.Should().Contain("!!").And.Contain("Q").And.Contain("★");
        first.QuerySelector(".stock-glyphs")!.GetAttribute("title").Should().Contain("Sous le stock de sécurité").And.Contain("Article critique");
        page.Find("[data-row='CQ-CHIM'] .stock-glyphs").ClassName.Should().Contain("abnormal", "a warning is said on its cells");
        page.FindAll("[data-grid=lots] tbody tr").Should().HaveCount(2);
        page.Find("[data-grid=lots] [data-row='GLU-2611']").TextContent.Should().Contain("En quarantaine");

        page.Find("[data-row='CQ-CHIM']").Click();
        _queries.Calls.Last(c => c.Method == nameof(IInventoryQueries.GetLotsAsync)).Arguments[1].Should().Be(control.StockItemId);
        page.FindAll("[data-grid=lots] tbody tr").Should().ContainSingle();

        page.Find("[data-chip=only-quarantined]").Click();
        _queries.Calls.Last(c => c.Method == nameof(IInventoryQueries.GetLotsAsync)).Arguments[2].Should().Be(true);
        page.Find("[data-grid=lots] [data-row='GLU-2611']").Should().NotBeNull();
        glucose.Should().NotBeNull();

        page.Find("[data-field=stock-search]").Input("edta");
        page.FindAll("[data-grid=stock] tbody tr").Should().ContainSingle().Which.GetAttribute("data-row").Should().Be("TUB-EDTA");
    }

    [Fact]
    public void ADelivery_IsRefusedBoxByBox_BeforeAnythingIsSent()
    {
        SignIn(AppLanguage.French, Storeman);
        Stock();

        var page = Render<InventoryScreen>();
        page.Find("[data-action='Inventory.ReceiveLot']").Click();
        page.Find("[data-modal=receive-lot]").TextContent.Should().Contain("BIO-GLU").And.Contain("Obligatoire");

        page.Find("[data-modal=receive-lot] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InventoryScreenRules.LotNumberField}]").TextContent.Should().Contain("numéro de lot");

        page.Find($"[data-field={InventoryScreenRules.LotNumberField}]").Input("GLU-2702");
        page.Find($"[data-field={InventoryScreenRules.QuantityField}]").Input("6");
        page.Find($"[data-field={InventoryScreenRules.PriceField}]").Input("450 DH");
        page.Find("[data-modal=receive-lot] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InventoryScreenRules.PriceField}]").TextContent.Should().Contain("450 DH");

        page.Find($"[data-field={InventoryScreenRules.PriceField}]").Input("450");
        page.Find("[data-modal=receive-lot] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InventoryScreenRules.TemperatureField}]").TextContent.Should().Contain("température",
            "a refrigerated reagent is not received without its temperature on arrival");

        _items.CallsTo(nameof(IRepository<StockItem>.GetByIdWithDetailsAsync)).Should().Be(0);
        _movements.CallsTo(nameof(IRepository<StockMovement>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void AFailedCheck_IsAskedAbout_AndNoGoesBackToTheDeliveryAsTyped()
    {
        SignIn(AppLanguage.French, Storeman);
        Stock();

        var page = Render<InventoryScreen>();
        page.Find("[data-action='Inventory.ReceiveLot']").Click();
        page.Find($"[data-field={InventoryScreenRules.LotNumberField}]").Input("GLU-2702");
        page.Find($"[data-field={InventoryScreenRules.QuantityField}]").Input("6");
        page.Find($"[data-field={InventoryScreenRules.TemperatureField}]").Input("4,5");
        page.Find("[data-field=packaging]").Change(false);
        page.Find("[data-modal=receive-lot] [data-action='Action.Save']").Click();

        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("non-conformité");
        page.Find("[data-action='Action.No']").Click();

        page.Find($"[data-field={InventoryScreenRules.LotNumberField}]").GetAttribute("value").Should().Be("GLU-2702");
        page.Find("[data-field=packaging]").HasAttribute("checked").Should().BeFalse();
        _movements.CallsTo(nameof(IRepository<StockMovement>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void ADelivery_IsReceivedIntoQuarantine_AndSaidSo()
    {
        SignIn(AppLanguage.French, Storeman);
        var (_, _, tubes) = Stock();
        var item = new StockItem("TUB-EDTA", new MultilingualText("Tubes EDTA 4 mL"), StockItemKind.Consumable, "boîte", Guid.NewGuid());
        _items.On(nameof(IRepository<StockItem>.GetByIdWithDetailsAsync), _ => item);

        var page = Render<InventoryScreen>();
        page.Find("[data-row='TUB-EDTA']").Click();
        page.Find("[data-action='Inventory.ReceiveLot']").Click();
        page.Find($"[data-field={InventoryScreenRules.LotNumberField}]").Input("ED-5521");
        page.Find($"[data-field={InventoryScreenRules.QuantityField}]").Input("10");
        page.Find("[data-modal=receive-lot] [data-action='Action.Save']").Click();

        _items.Calls.Last(c => c.Method == nameof(IRepository<StockItem>.GetByIdWithDetailsAsync)).Arguments[0].Should().Be(tubes.StockItemId);
        _movements.CallsTo(nameof(IRepository<StockMovement>.AddAsync)).Should().Be(1);
        AuditReason.Should().StartWith("Réception du lot ED-5521 de TUB-EDTA");
        item.Lots.Should().ContainSingle().Which.IsQuarantined.Should().BeTrue();
        page.Find("[data-state=stock-message]").TextContent.Should().Contain("quarantaine");
    }

    [Fact]
    public void AnIssueThatReadsNoNumber_IsRefusedInItsBox()
    {
        SignIn(AppLanguage.French, Storeman);
        Stock();

        var page = Render<InventoryScreen>();
        page.Find("[data-action='Inventory.Issue']").Click();
        page.Find("[data-modal=issue]").TextContent.Should().Contain("BIO-GLU").And.Contain("coffret");
        page.Find("[data-modal=issue] [data-field=text]").Input("deux");
        page.Find("[data-modal=issue] [data-action='Action.Ok']").Click();

        page.Find("[data-refusal=issue]").TextContent.Should().Contain("deux");
        _items.CallsTo(nameof(IRepository<StockItem>.GetByIdWithDetailsAsync)).Should().Be(0);
    }

    [Fact]
    public void AQuarantinedLot_IsReleasedOnItsEvidence()
    {
        SignIn(AppLanguage.French, Reviewer);
        Stock();
        var lot = new ReagentLot("GLU-2611", TestClock.Today.AddDays(140), 6m, TestClock.Today.AddDays(-2));
        _queries.On(nameof(IInventoryQueries.GetLotEntityAsync), _ => lot);

        var page = Render<InventoryScreen>();
        page.Find("[data-grid=lots] [data-row='GLU-2611']").Click();
        page.Find("[data-action='Action.Release']").Click();
        page.Find("[data-modal=reason]").TextContent.Should().Contain("GLU-2611");
        page.Find("[data-field=reason]").Input("CQ N1 et N2 conformes sur le nouveau lot, série du 17/10");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        AuditReason.Should().StartWith("Libération du lot GLU-2611 : CQ N1 et N2 conformes");
        lot.IsAwaitingRelease.Should().BeFalse();
        page.Find(".action-bar").TextContent.Should().Contain("GLU-2611 — Utilisable");
    }

    [Fact]
    public void WithoutTheRights_TheOperationsAreNotOffered()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View);
        Stock();

        var page = Render<InventoryScreen>();

        page.FindAll("[data-action='Inventory.ReceiveLot']").Should().BeEmpty();
        page.FindAll("[data-action='Inventory.Issue']").Should().BeEmpty();
        page.FindAll("[data-action='Action.Reject']").Should().BeEmpty();
        page.FindAll("[data-action='Inventory.WriteOff']").Should().BeEmpty();
        page.FindAll("[data-action='Recall.Menu']").Should().BeEmpty();
        page.Find("[data-action='Action.Release']").HasAttribute("disabled").Should().BeTrue("releasing a lot is a QC decision");
        page.Find("[data-action='Stock.Alerts']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Storeman);
        Stock();

        var page = Render<InventoryScreen>();

        page.Find("[data-row='BIO-GLU']").TextContent.Should().Contain("غلوكوز");
        page.Find("[data-action='Inventory.ReceiveLot']").TextContent.Should().NotContain("Réceptionner");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }
}
