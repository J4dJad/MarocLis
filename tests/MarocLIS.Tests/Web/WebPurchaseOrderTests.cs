using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Web.Components.Screens.Purchasing;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Bons de commande » in the browser: the orders with the pending priorities first and their lines
/// and totals in dirhams, a line that reads no rate refused in its box before anything is sent, an
/// order its author cannot approve while another approver does, an order sent by hand when the
/// laboratory has no mail account, a delivery refused box by box and then refused at the door, and
/// each operation kept by the right the workstation asks — through the workstation's services.
/// </summary>
public sealed class WebPurchaseOrderTests : WebScreenContext
{
    private readonly Fake<IPurchaseOrderQueries> _queries = Fake<IPurchaseOrderQueries>.Create();
    private readonly Fake<IRepository<PurchaseOrder>> _orders = Fake<IRepository<PurchaseOrder>>.Create();
    private readonly Fake<IRepository<RefusedDelivery>> _refusals = Fake<IRepository<RefusedDelivery>>.Create();
    private readonly Fake<IPurchasingQualityQueries> _quality = Fake<IPurchasingQualityQueries>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    public WebPurchaseOrderTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_orders.Object);
        Services.AddSingleton(_refusals.Object);
        Services.AddSingleton(_quality.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(Fake<IRepository<StockItem>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<StockMovement>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<NonConformity>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<LotCertificateFile>>.Create().Object);
        Services.AddSingleton(Fake<IInventoryQueries>.Create().Object);
        var sequences = Fake<ISequenceAllocator>.Create();
        sequences.On(nameof(ISequenceAllocator.NextAsync), _ => Result.Success("NC-2026-0013"));
        Services.AddSingleton(sequences.Object);
        Services.AddSingleton(Fake<IPurchaseOrderDocumentQueries>.Create().Object);
        Services.AddSingleton(Fake<IPurchaseOrderRenderer>.Create().Object);
        Services.AddSingleton(Fake<IMessageSender>.Create().Object);
        Services.AddScoped<ReceptionService>();
        Services.AddScoped<PurchaseOrderService>();
        Services.AddScoped<PurchaseOrderDocumentService>();
    }

    private static readonly string[] Buyer =
    [
        Permissions.Inventory.View, Permissions.Inventory.ManagePurchaseOrders, Permissions.Inventory.ApprovePurchaseOrders,
        Permissions.Inventory.Receive
    ];

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    private Guid UserId => Services.GetRequiredService<SessionContext>().User.UserId;

    internal static PurchaseOrderRow Order(string number, PurchaseOrderStatus status = PurchaseOrderStatus.Draft, int lines = 1,
                                           bool priority = false, bool overdue = false, Guid? raisedBy = null, Guid? id = null,
                                           DateOnly? expected = null) =>
        new(id ?? Guid.NewGuid(), number, "Biosystems Maroc", new DateOnly(2026, 10, 10), expected ?? new DateOnly(2026, 10, 24), status, lines,
            1000m, 1200m, "Karim Benali", status is PurchaseOrderStatus.Draft ? null : "Salma Idrissi", "PF-2026-118", overdue,
            IsPriority: priority, RaisedAutomatically: priority, SupplierId: Guid.NewGuid(), RaisedByUserId: raisedBy ?? Guid.NewGuid());

    internal static PurchaseOrderLineRow Line(int number = 1, string code = "BIO-GLU", decimal outstanding = 10m, Guid? itemId = null) =>
        new(Guid.NewGuid(), number, itemId ?? Guid.NewGuid(), code, "Glucose GOD-PAP 4×100 mL", "coffret", 10m, 10m - outstanding, outstanding,
            100m, 0.05m, 0.20m, 950m);

    private void Orders(params PurchaseOrderRow[] rows)
    {
        _queries.On(nameof(IPurchaseOrderQueries.GetOrdersAsync), _ => rows);
        _queries.On(nameof(IPurchaseOrderQueries.GetLinesAsync), _ => new[] { Line(), Line(2, "CQ-CHIM", 0m) });
    }

    [Fact]
    public void TheOrders_ThePrioritiesFirst_WithTheirLinesAndTotalsInDirhams()
    {
        SignIn(AppLanguage.French, Buyer);
        Orders(Order("BC/2026/00010", PurchaseOrderStatus.Sent, overdue: true, expected: new DateOnly(2026, 10, 12)),
               Order("BC/2026/00011", priority: true));

        var page = Render<PurchaseOrders>();

        var rows = page.FindAll("[data-grid=orders] tbody tr");
        rows.Should().HaveCount(2);
        rows[0].GetAttribute("data-row").Should().Be("BC/2026/00011", "a priority still to be dealt with comes first");
        rows[0].TextContent.Should().Contain("Prioritaire (auto)");
        page.Find("[data-row='BC/2026/00010'] td:nth-child(4)").ClassName.Should().Contain("abnormal", "a late delivery is said on its date");
        Flat(page.Find("[data-row='BC/2026/00010']").TextContent).Should().Contain("! 12/10/26").And.Contain("1 200,00 DH");
        page.Find("[data-state=count]").TextContent.Should().Contain("à approuver 1").And.Contain("en retard 1");

        page.FindAll("[data-grid=lines] tbody tr").Should().HaveCount(2);
        page.Find("[data-grid=lines] [data-row='1']").ClassName.Should().Contain("is-selected", "the first line still awaited is the one received");
        Flat(page.Find("[data-grid=lines] [data-row='1']").TextContent).Should().Contain("5%").And.Contain("950,00 DH");
        Flat(page.Find("[data-state=totals]").TextContent).Should().Be("Total HT 1 000,00 DH · TVA 200,00 DH · Total TTC 1 200,00 DH");
        page.Markup.Should().NotContain("MAD");

        page.Find("[data-field=order-search]").Input("00010");
        page.FindAll("[data-grid=orders] tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void ALineThatReadsNoRate_IsRefusedInItsBox_BeforeAnythingIsSent()
    {
        SignIn(AppLanguage.French, Buyer);
        Orders(Order("BC/2026/00011"));
        var glucose = new OrderableItem(Guid.NewGuid(), "BIO-GLU", new MultilingualText("Glucose GOD-PAP"), "coffret", null, 6m, 410m);
        _queries.On(nameof(IPurchaseOrderQueries.GetOrderableItemsAsync), _ => new[] { glucose });

        var page = Render<PurchaseOrders>();
        page.Find("[data-action='PurchaseOrders.AddLine']").Click();

        page.Find($"[data-field={PurchaseOrderScreenRules.QuantityField}]").GetAttribute("value").Should().Be("6", "the reorder quantity is offered");
        page.Find($"[data-field={PurchaseOrderScreenRules.PriceField}]").GetAttribute("value").Should().Be("410.00");
        page.Find($"[data-field={PurchaseOrderScreenRules.DiscountField}]").Input("10 %");
        page.Find("[data-modal=purchase-order-line] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={PurchaseOrderScreenRules.DiscountField}]").TextContent.Should().Contain("10 %");
        page.Find($"[data-field={PurchaseOrderScreenRules.DiscountField}]").ClassName.Should().Contain("is-refused");
        _orders.CallsTo(nameof(IRepository<PurchaseOrder>.GetByIdWithDetailsAsync)).Should().Be(0);
    }

    [Fact]
    public void ANewOrder_WithoutItsSupplier_IsRefusedOnTheSupplierBox()
    {
        SignIn(AppLanguage.French, Buyer);
        Orders();
        _queries.On(nameof(IPurchaseOrderQueries.GetSuppliersAsync), _ => new[] { new SupplierOption(Guid.NewGuid(), "FRN-001", "Biosystems Maroc") });

        var page = Render<PurchaseOrders>();
        page.Find("[data-action='PurchaseOrders.NewOrder']").Click();
        page.Find("[data-modal=purchase-order]").TextContent.Should().Contain("FRN-001 — Biosystems Maroc").And.Contain("approuvée par une autre personne");
        page.Find("[data-modal=purchase-order] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={PurchaseOrderScreenRules.SupplierField}]").TextContent.Should().Contain("fournisseur");
        _orders.CallsTo(nameof(IRepository<PurchaseOrder>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void ItsAuthor_CannotApproveAnOrder_AnotherApproverDoes()
    {
        SignIn(AppLanguage.French, Buyer);
        var own = Order("BC/2026/00011", raisedBy: UserId);
        var order = new PurchaseOrder("BC/2026/00012", Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 10), Guid.NewGuid());
        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 4m, 100m));
        var other = Order("BC/2026/00012", id: order.Id);
        Orders(own, other);
        _orders.On(nameof(IRepository<PurchaseOrder>.GetByIdWithDetailsAsync), _ => order);

        var page = Render<PurchaseOrders>();
        page.Find("[data-row='BC/2026/00011']").Click();
        var approve = page.Find("[data-action='PurchaseOrders.Approve']");
        approve.HasAttribute("disabled").Should().BeTrue();
        approve.GetAttribute("title").Should().Contain("approuvée par une autre personne");

        page.Find("[data-row='BC/2026/00012']").Click();
        page.Find("[data-action='PurchaseOrders.Approve']").Click();

        order.Status.Should().Be(PurchaseOrderStatus.Approved);
        AuditReason.Should().Be("Approbation du bon de commande BC/2026/00012");
    }

    [Fact]
    public void WithoutAMailAccount_TheOrderGoesByHand_AndIsMarkedSentOnlyOnceConfirmed()
    {
        SignIn(AppLanguage.French, Buyer);
        var order = new PurchaseOrder("BC/2026/00012", Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 10), Guid.NewGuid());
        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 4m, 100m));
        order.Approve(TestClock.Now, Guid.NewGuid());
        Orders(Order("BC/2026/00012", PurchaseOrderStatus.Approved, id: order.Id));
        _orders.On(nameof(IRepository<PurchaseOrder>.GetByIdWithDetailsAsync), _ => order);

        var page = Render<PurchaseOrders>();
        page.Find("[data-action='PurchaseOrders.SendToSupplier']").Click();

        var manual = page.Find("[data-modal=manual-send]");
        manual.TextContent.Should().Contain("messagerie").And.Contain("BC/2026/00012");
        manual.QuerySelector("[data-action='PurchaseOrders.PrintPdf']")!.TextContent.Should().Contain("Télécharger le PDF");
        order.Status.Should().Be(PurchaseOrderStatus.Approved, "nothing is marked sent before the operator says it went");

        page.Find("[data-modal=manual-send] [data-action='Action.Yes']").Click();
        order.Status.Should().Be(PurchaseOrderStatus.Sent);
    }

    [Fact]
    public void ADelivery_IsRefusedBoxByBox_ThenRefusedAtTheDoor()
    {
        SignIn(AppLanguage.French, Buyer);
        var order = new PurchaseOrder("BC/2026/00012", Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 10), Guid.NewGuid());
        _orders.On(nameof(IRepository<PurchaseOrder>.GetByIdAsync), _ => order);
        _queries.On(nameof(IPurchaseOrderQueries.GetOrdersAsync), _ => new[] { Order("BC/2026/00012", PurchaseOrderStatus.Sent, id: order.Id) });
        _queries.On(nameof(IPurchaseOrderQueries.GetLinesAsync), _ => new[] { Line() });
        _quality.On(nameof(IPurchasingQualityQueries.GetStorageConditionAsync), _ => (StorageCondition?)StorageCondition.Refrigerated);

        var page = Render<PurchaseOrders>();
        page.Find("[data-action='PurchaseOrders.ReceiveLine']").Click();
        page.Find("[data-modal=purchase-order-receipt]").TextContent.Should().Contain("Obligatoire").And.Contain("quarantaine");
        page.Find($"[data-field={InventoryScreenRules.QuantityField}]").GetAttribute("value").Should().Be("10");

        page.Find("[data-modal=purchase-order-receipt] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InventoryScreenRules.LotNumberField}]").Should().NotBeNull();

        page.Find($"[data-field={InventoryScreenRules.LotNumberField}]").Input("GLU-2702");
        page.Find("[data-modal=purchase-order-receipt] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={InventoryScreenRules.TemperatureField}]").TextContent.Should().Contain("température");
        _orders.CallsTo(nameof(IRepository<PurchaseOrder>.GetByIdWithDetailsAsync)).Should().Be(0);

        page.Find($"[data-field={InventoryScreenRules.TemperatureField}]").Input("14");
        page.Find("[data-modal=purchase-order-receipt] [data-action='Reception.Refuse']").Click();
        page.Find("[data-field=reason]").Input("Rupture de la chaîne du froid à la livraison");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();

        _refusals.CallsTo(nameof(IRepository<RefusedDelivery>.AddAsync)).Should().Be(1);
        var refusal = (RefusedDelivery)_refusals.Calls.Single(c => c.Method == nameof(IRepository<RefusedDelivery>.AddAsync)).Arguments[0]!;
        refusal.ArrivalTemperatureCelsius.Should().Be(14m);
        refusal.LotNumber.Should().Be("GLU-2702");
        AuditReason.Should().StartWith("Refus de la livraison BC/2026/00012");
        page.Find("[data-state=purchase-message]").TextContent.Should().Contain("NC-2026-0013");
    }

    [Fact]
    public void WithoutTheRights_TheOperationsAreNotOffered()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View);
        Orders(Order("BC/2026/00011"));

        var page = Render<PurchaseOrders>();

        page.FindAll("[data-action='PurchaseOrders.NewOrder']").Should().BeEmpty();
        page.FindAll("[data-action='PurchaseOrders.AddLine']").Should().BeEmpty();
        page.FindAll("[data-action='PurchaseOrders.Approve']").Should().BeEmpty();
        page.FindAll("[data-action='PurchaseOrders.SendToSupplier']").Should().BeEmpty();
        page.FindAll("[data-action='PurchaseOrders.CancelOrder']").Should().BeEmpty();
        page.FindAll("[data-action='PurchaseOrders.ReceiveLine']").Should().BeEmpty();
        page.Find("[data-action='PurchaseOrders.PrintPdf']").HasAttribute("disabled").Should().BeTrue("a draft is not a commitment to print");
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Buyer);
        Orders(Order("BC/2026/00011", priority: true));

        var page = Render<PurchaseOrders>();

        page.Find("[data-action='PurchaseOrders.NewOrder']").TextContent.Should().NotContain("Nouvelle");
        page.Find("[data-state=totals]").TextContent.Should().Contain("د.م.").And.NotContain("DH");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }

    /// <summary>The thin spaces grouping the thousands read as plain spaces.</summary>
    private static string Flat(string text) => text.Replace('\u202F', ' ').Replace('\u00A0', ' ');
}
