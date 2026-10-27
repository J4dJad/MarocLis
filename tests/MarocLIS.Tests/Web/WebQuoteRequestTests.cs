using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Web.Components.Screens.Purchasing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Demandes de devis » in the browser: the answers side by side with the cheapest price and the
/// shortest delay marked and an expired offer muted, the offer chosen with its total in dirhams
/// drafting a purchase order, a request or an answer refused in its box before anything is sent, a
/// request without mail account said to go by telephone, and each operation kept by its right —
/// through the workstation's QuoteService.
/// </summary>
public sealed class WebQuoteRequestTests : WebScreenContext
{
    private readonly Fake<IQuoteQueries> _queries = Fake<IQuoteQueries>.Create();
    private readonly Fake<IPurchaseOrderQueries> _orderQueries = Fake<IPurchaseOrderQueries>.Create();
    private readonly Fake<IRepository<QuoteRequest>> _requests = Fake<IRepository<QuoteRequest>>.Create();
    private readonly Fake<IRepository<PurchaseOrder>> _orders = Fake<IRepository<PurchaseOrder>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    private static readonly Guid Biosystems = Guid.NewGuid(), Sobomed = Guid.NewGuid(), Promamec = Guid.NewGuid();

    public WebQuoteRequestTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_orderQueries.Object);
        Services.AddSingleton(_requests.Object);
        Services.AddSingleton(_orders.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(Fake<IRepository<PurchasingDocument>>.Create().Object);
        var sequences = Fake<ISequenceAllocator>.Create();
        sequences.On(nameof(ISequenceAllocator.NextAsync), _ => Result.Success("BC/2026/00031"));
        Services.AddSingleton(sequences.Object);
        Services.AddSingleton(Fake<IMessageSender>.Create().Object);
        Services.AddScoped<QuoteService>();
    }

    private static readonly string[] Buyer =
    [
        Permissions.Inventory.View, Permissions.Inventory.ManageQuotes, Permissions.Inventory.ManagePurchaseOrders
    ];

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    private static QuoteRequestRow Row(Guid id, string number = "DD/2026/0007", QuoteRequestStatus status = QuoteRequestStatus.Open) =>
        new(id, number, new DateOnly(2026, 10, 12), "Réactifs de biochimie T4", status, new DateOnly(2026, 10, 19), 2, 3, 2,
            "Karim Benali", null, null);

    /// <summary>Two articles asked of three suppliers: Biosystems cheaper on the first, Sobomed faster, Promamec's offer expired.</summary>
    private static QuoteRequestDetail Detail(Guid id, Guid itemA, Guid itemB, Guid offerBio, Guid offerSob, Guid offerPro, int missing = 0)
    {
        var glucose = new QuoteItemRow(itemA, 1, Guid.NewGuid(), "BIO-GLU", "Glucose GOD-PAP 4×100 mL", "coffret", 12m);
        var urea = new QuoteItemRow(itemB, 2, Guid.NewGuid(), "BIO-URE", "Urée UV 4×50 mL", "coffret", 6m);
        var sent = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(1));
        return new QuoteRequestDetail(id, "DD/2026/0007", QuoteRequestStatus.Open, new DateOnly(2026, 10, 12), "Réactifs de biochimie T4",
            new DateOnly(2026, 10, 19), null, null, [glucose, urea],
            [
                new QuoteSupplierRow(Biosystems, "F-BIO", "Biosystems Maroc", "devis@biosystems.ma", true, sent, true),
                new QuoteSupplierRow(Sobomed, "F-SOB", "Sobomed", "achats@sobomed.ma", true, sent, true),
                new QuoteSupplierRow(Promamec, "F-PRO", "Promamec", null, true, sent, true)
            ],
            [
                new QuoteOfferRow(offerBio, Biosystems, "Biosystems Maroc", new DateOnly(2026, 10, 14), new DateOnly(2026, 11, 14), 10, null, "BIO-Q-118",
                    false, 3540m, missing, null),
                new QuoteOfferRow(offerSob, Sobomed, "Sobomed", new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 15), 4, null, null,
                    false, 3810m, 0, "devis-sobomed.pdf"),
                new QuoteOfferRow(offerPro, Promamec, "Promamec", new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), 7, null, null,
                    true, 3200m, 0, null)
            ],
            [
                new QuoteComparisonLine(itemA, 1, glucose.StockItemId, 12m,
                [
                    new QuoteComparisonCell(offerBio, Biosystems, 220m, 0m, 220m, 0.2m, 2640m, 10, false, true, false),
                    new QuoteComparisonCell(offerSob, Sobomed, 240m, 0m, 240m, 0.2m, 2880m, 4, false, false, true),
                    new QuoteComparisonCell(offerPro, Promamec, 200m, 0m, 200m, 0.2m, 2400m, 7, true, false, false)
                ]),
                new QuoteComparisonLine(itemB, 2, urea.StockItemId, 6m,
                [
                    new QuoteComparisonCell(offerBio, Biosystems, 150m, 0m, 150m, 0.2m, 900m, 10, false, false, false),
                    new QuoteComparisonCell(offerSob, Sobomed, 155m, 0m, 155m, 0.2m, 930m, 4, false, false, true)
                ])
            ]);
    }

    private (Guid Id, QuoteRequestDetail Detail) Requests(int missing = 0)
    {
        var id = Guid.NewGuid();
        var detail = Detail(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), missing);
        _queries.On(nameof(IQuoteQueries.GetRequestsAsync), _ => new[] { Row(id) });
        _queries.On(nameof(IQuoteQueries.GetDetailAsync), _ => detail);
        return (id, detail);
    }

    [Fact]
    public void TheAnswers_AreSideBySide_TheCheapestAndTheFastestMarked_AnExpiredOfferMuted()
    {
        SignIn(AppLanguage.French, Buyer);
        Requests();

        var page = Render<QuoteRequests>();

        page.Find("[data-grid=requests] [data-row='DD/2026/0007']").ClassName.Should().Contain("is-selected");
        page.FindAll("[data-grid=comparison] thead th").Select(h => h.TextContent.Trim()).Should()
            .Contain("Biosystems Maroc").And.Contain(h => h.StartsWith("Promamec ("), "an expired offer says so");
        var glucose = page.Find("[data-grid=comparison] [data-row='1']");
        glucose.QuerySelector("[data-cell='Biosystems Maroc']")!.ClassName.Should().Contain("quote-cheapest");
        Flat(glucose.QuerySelector("[data-cell='Biosystems Maroc']")!.TextContent).Should().Be("220,00 DH · 10 j");
        glucose.QuerySelector("[data-cell='Sobomed']")!.ClassName.Should().Contain("strong", "the shortest delay is in bold");
        glucose.QuerySelector("[data-cell='Promamec']")!.ClassName.Should().Contain("muted").And.NotContain("quote-cheapest",
            "an expired offer is never the one chosen, however cheap");
        page.Find("[data-grid=comparison] [data-row='2'] [data-cell='Promamec']").TextContent.Should().Be("—");

        Flat(page.Find("[data-grid=suppliers] [data-row='F-SOB']").TextContent).Should().Contain("3 810,00 DH");
        page.Find("[data-grid=suppliers] [data-row='F-PRO']").ClassName.Should().Contain("muted");
        page.Markup.Should().NotContain("MAD");
    }

    [Fact]
    public void TheOfferChosen_AsksWithItsTotalInDirhams_ThenDraftsThePurchaseOrder()
    {
        SignIn(AppLanguage.French, Buyer);
        var quote = new QuoteRequest("DD/2026/0007", Guid.NewGuid(), new DateOnly(2026, 10, 12), Guid.NewGuid());
        quote.AddItem(Guid.NewGuid(), 12m, "Glucose").IsSuccess.Should().BeTrue();
        quote.AddSupplier(Biosystems).IsSuccess.Should().BeTrue();
        quote.AddSupplier(Sobomed).IsSuccess.Should().BeTrue();
        quote.AddSupplier(Promamec).IsSuccess.Should().BeTrue();
        var item = quote.Items.Single();
        var offer = quote.RecordOffer(Biosystems, new DateOnly(2026, 10, 14), [new SupplierQuoteLineInput(item.Id, 220m)],
                                      new DateOnly(2026, 11, 14), 10).Value;
        var detail = Detail(quote.Id, item.Id, Guid.NewGuid(), offer.Id, Guid.NewGuid(), Guid.NewGuid(), missing: 1);
        _queries.On(nameof(IQuoteQueries.GetRequestsAsync), _ => new[] { Row(quote.Id) });
        _queries.On(nameof(IQuoteQueries.GetDetailAsync), _ => detail);
        _requests.On(nameof(IRepository<QuoteRequest>.GetByIdWithDetailsAsync), _ => quote);
        _orderQueries.On(nameof(IPurchaseOrderQueries.IsSupplierApprovedAsync), _ => true);

        var page = Render<QuoteRequests>();
        page.Find("[data-grid=suppliers] [data-row='F-BIO']").Click();
        page.Find("[data-action='Quotes.Award']").Click();

        var question = Flat(page.Find("[data-modal=confirm]").TextContent);
        question.Should().Contain("Biosystems Maroc (3 540,00 DH HT)").And.Contain("1 article ; il ne figurera pas").And.NotContain("MAD");
        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();

        quote.Status.Should().Be(QuoteRequestStatus.Awarded);
        var order = (PurchaseOrder)_orders.Calls.Single(c => c.Method == nameof(IRepository<PurchaseOrder>.AddAsync)).Arguments[0]!;
        order.SupplierId.Should().Be(Biosystems);
        order.Lines.Single().UnitPriceMad.Should().Be(220m);
        AuditReason.Should().Be("Offre retenue sur la demande de devis DD/2026/0007 : bon de commande BC/2026/00031");
        page.Find("[data-state=quote-message]").TextContent.Should().Contain("BC/2026/00031");
    }

    [Fact]
    public void AnExpiredOffer_CannotBeChosen()
    {
        SignIn(AppLanguage.French, Buyer);
        Requests();

        var page = Render<QuoteRequests>();
        page.Find("[data-grid=suppliers] [data-row='F-PRO']").Click();

        page.Find("[data-action='Quotes.Award']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Quotes.RecordOffer']").HasAttribute("disabled").Should().BeFalse("a new answer can replace it");
    }

    [Fact]
    public void ARequest_WithoutASupplierOrWithAQuantityThatIsNone_IsRefusedInItsBox()
    {
        SignIn(AppLanguage.French, Buyer);
        Requests();
        _queries.On(nameof(IQuoteQueries.GetQuotableItemsAsync), _ => new[]
        {
            new QuotableItem(Guid.NewGuid(), "BIO-GLU", new MultilingualText("Glucose GOD-PAP"), "coffret", 12m, null),
            new QuotableItem(Guid.NewGuid(), "BIO-URE", new MultilingualText("Urée UV"), "coffret", 6m, null)
        });
        _queries.On(nameof(IQuoteQueries.GetSuppliersAsync), _ => new[]
        {
            new QuoteSupplierOption(Biosystems, "F-BIO", "Biosystems Maroc", "devis@biosystems.ma", true)
        });

        var page = Render<QuoteRequests>();
        page.Find("[data-action='Quotes.New']").Click();
        page.Find("[data-modal=quote-request] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={QuoteScreenRules.ItemsField}]").TextContent.Should().NotBeNullOrWhiteSpace();

        page.Find("[data-field=quote-include-0]").Change(true);
        page.Find($"[data-field={QuoteScreenRules.QuantityField(0)}]").Input("0");
        page.Find("[data-modal=quote-request] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={QuoteScreenRules.QuantityField(0)}]").TextContent.Should().Contain("La quantité demandée doit être positive");

        page.Find($"[data-field={QuoteScreenRules.QuantityField(0)}]").Input("12");
        page.Find("[data-modal=quote-request] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={QuoteScreenRules.SuppliersField}]").TextContent.Should().Contain("au moins un fournisseur");
        _requests.CallsTo(nameof(IRepository<QuoteRequest>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void AnAnswer_WithARateThatReadsNoNumber_IsRefusedInItsBox()
    {
        SignIn(AppLanguage.French, Buyer);
        Requests();

        var page = Render<QuoteRequests>();
        page.Find("[data-grid=suppliers] [data-row='F-SOB']").Click();
        page.Find("[data-action='Quotes.RecordOffer']").Click();
        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();

        page.Find("[data-modal=quote-offer] [data-action='Action.Save']").Click();
        page.Find($"[data-refusal={QuoteScreenRules.LinesField}]").TextContent.Should().Contain("ne chiffre aucun article");

        page.Find($"[data-field={QuoteScreenRules.OfferField("price", 0)}]").Input("240");
        page.Find($"[data-field={QuoteScreenRules.OfferField("discount", 0)}]").Input("10 %");
        page.Find("[data-modal=quote-offer] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={QuoteScreenRules.OfferField("discount", 0)}]").TextContent.Should().Contain("« 10 % »");
        _requests.CallsTo(nameof(IRepository<QuoteRequest>.GetByIdWithDetailsAsync)).Should().Be(0);
    }

    [Fact]
    public void WithoutAMailAccount_TheRequestIsSaidToGoByTelephone()
    {
        SignIn(AppLanguage.French, Buyer);
        var (id, detail) = Requests();
        _queries.On(nameof(IQuoteQueries.GetDetailAsync), _ => detail with
        {
            Suppliers = [.. detail.Suppliers.Select(s => s.Code == "F-PRO" ? s with { SentAt = null } : s)]
        });

        var page = Render<QuoteRequests>();
        page.Find("[data-action='Quotes.SendByEmail']").Click();

        page.Find("[data-state=quote-message]").TextContent.Should().Contain("messagerie").And.Contain("Marquer envoyée");
    }

    [Fact]
    public void WithoutTheRights_TheOperationsAreNotOffered()
    {
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Inventory.ManageQuotes);
        Requests();

        var page = Render<QuoteRequests>();

        page.Find("[data-action='Quotes.New']").Should().NotBeNull();
        page.FindAll("[data-action='Quotes.Award']").Should().BeEmpty("choosing drafts an order, which needs the purchasing right");
    }

    [Fact]
    public void TheScreen_IsInArabic_ForAnArabicSession()
    {
        SignIn(AppLanguage.Arabic, Buyer);
        Requests();

        var page = Render<QuoteRequests>();

        page.Find("[data-grid=suppliers] [data-row='F-SOB']").TextContent.Should().Contain("د.م.").And.NotContain("DH");
        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
    }

    private static string Flat(string text) => text.Replace(' ', ' ').Replace(' ', ' ');
}
