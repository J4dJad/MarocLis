using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Bons de commande », « Demandes de devis », « Factures fournisseurs » and
/// « Évaluation fournisseurs », moved out of the workstation's views and dialogs, now shared with
/// the web.
/// </summary>
public sealed class PurchasingScreenRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Colleague = Guid.NewGuid();

    private static string Text(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}({string.Join(",", args)})";

    private static Func<string, bool> Rights(params string[] granted) => granted.Contains;

    private static string Mad(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    // ---- Purchase orders ---------------------------------------------------------------------------

    internal static PurchaseOrderRow Order(PurchaseOrderStatus status = PurchaseOrderStatus.Draft, int lines = 2, Guid? raisedBy = null,
                                           bool priority = false, bool overdue = false, string number = "BC/2026/00012",
                                           string? cancelled = null) =>
        new(Guid.NewGuid(), number, "Biosystems Maroc", Today.AddDays(-10), Today.AddDays(overdue ? -2 : 5), status, lines,
            1000m, 1200m, "Karim Tahiri", null, null, overdue, priority, priority, Guid.NewGuid(), raisedBy ?? Colleague, cancelled);

    internal static PurchaseOrderLineRow OrderLine(decimal ordered = 10m, decimal received = 4m) =>
        new(Guid.NewGuid(), 1, Guid.NewGuid(), "BIO-GLU", "Glucose GOD-PAP", "coffret", ordered, received, ordered - received,
            120m, 0.05m, 0.20m, ordered * 114m);

    [Fact]
    public void An_order_offers_only_the_steps_its_state_allows_and_never_its_own_approval()
    {
        var all = Rights(Permissions.Inventory.ManagePurchaseOrders, Permissions.Inventory.ApprovePurchaseOrders, Permissions.Inventory.Receive);

        var draft = PurchaseOrderScreenRules.Actions(all, Me, Order(), null);
        draft.Should().Match<PurchaseOrderScreenActions>(a => a.New && a.AddLine && a.Approve && !a.OwnOrder && !a.Send && a.Cancel && !a.Print);

        var mine = PurchaseOrderScreenRules.Actions(all, Me, Order(raisedBy: Me), null);
        mine.Approve.Should().BeFalse("the author of an order cannot approve it");
        mine.OwnOrder.Should().BeTrue();

        PurchaseOrderScreenRules.Actions(all, Me, Order(lines: 0), null).Approve.Should().BeFalse("an empty draft is not approved");
        PurchaseOrderScreenRules.Actions(Rights(Permissions.Inventory.ManagePurchaseOrders), Me, Order(), null).Approve.Should().BeFalse();

        var approved = PurchaseOrderScreenRules.Actions(all, Me, Order(PurchaseOrderStatus.Approved), OrderLine());
        approved.Should().Match<PurchaseOrderScreenActions>(a => a.Send && a.Receive && a.Print && a.Cancel && !a.AddLine);

        var partly = PurchaseOrderScreenRules.Actions(all, Me, Order(PurchaseOrderStatus.PartiallyReceived), OrderLine(received: 10m));
        partly.Receive.Should().BeFalse("nothing is left to deliver on the line");
        partly.Cancel.Should().BeFalse("a delivery came in");

        PurchaseOrderScreenRules.Actions(Rights(), Me, Order(PurchaseOrderStatus.Sent), OrderLine())
            .Should().Match<PurchaseOrderScreenActions>(a => !a.New && !a.Receive && !a.Cancel && a.Print);
    }

    [Fact]
    public void A_pending_priority_comes_first_and_a_late_delivery_is_said_on_its_date()
    {
        var late = Order(PurchaseOrderStatus.Sent, overdue: true, number: "BC-1");
        var priority = Order(priority: true, number: "BC-2");
        var closed = Order(PurchaseOrderStatus.Received, number: "BC-3");

        PurchaseOrderScreenRules.Ordered([late, closed, priority]).Select(o => o.Number).Should().Equal("BC-2", "BC-1", "BC-3");
        PurchaseOrderScreenRules.Ink(priority, "number").Should().Be(StockInk.CriticalBold);
        PurchaseOrderScreenRules.PriorityKey(priority).Should().Be("PurchaseOrders.PriorityAutomatic");
        PurchaseOrderScreenRules.PriorityTone(priority).Should().Be(StockTone.Critical);
        PurchaseOrderScreenRules.Ink(late, "expected").Should().Be(StockInk.AbnormalBold);
        PurchaseOrderScreenRules.Expected(late).Should().StartWith("! ");
        PurchaseOrderScreenRules.RowInk(closed).Should().Be(StockInk.Muted);
        PurchaseOrderScreenRules.StatusTone(PurchaseOrderStatus.Sent).Should().Be(StockTone.Running);

        PurchaseOrderScreenRules.Count([late, priority, closed], Text)
            .Should().Be("StocksUi.Orders(3) · PurchaseOrders.ToApprove 1 · PurchaseOrders.Late 1");
        PurchaseOrderScreenRules.LinesNote(Order(PurchaseOrderStatus.Cancelled, cancelled: "Doublon"), Text)
            .Should().Contain("PurchaseOrders.CancelledBecause(Doublon)");
        PurchaseOrderScreenRules.Totals(late).Should().Be((1000m, 200m, 1200m));
        PurchaseOrderScreenRules.Rate(0m, blankWhenZero: true).Should().BeEmpty();
        PurchaseOrderScreenRules.Rate(0.2m, provider: CultureInfo.InvariantCulture).Should().Be("20%");
    }

    [Fact]
    public void A_new_order_needs_its_supplier_and_a_delivery_date_not_past()
    {
        var form = PurchaseOrderScreenRules.NewOrder(Today);
        form.ExpectedDelivery.Should().Be(Today.AddDays(7));

        PurchaseOrderScreenRules.CheckOrder(form, Today).Refusal!.Field.Should().Be(PurchaseOrderScreenRules.SupplierField);

        form.SupplierId = Guid.NewGuid();
        form.ExpectedDelivery = Today.AddDays(-1);
        PurchaseOrderScreenRules.CheckOrder(form, Today).Refusal!.Key.Should().Be("Po_ExpectedDateInPast");

        form.HasExpectedDelivery = false;
        form.SupplierReference = "  PF-2026-88 ";
        var (refusal, request) = PurchaseOrderScreenRules.CheckOrder(form, Today);
        refusal.Should().BeNull();
        request!.ExpectedDeliveryDate.Should().BeNull();
        request.SupplierReference.Should().Be("PF-2026-88");
    }

    [Fact]
    public void A_line_starts_from_the_usual_supplier_or_the_contract_and_is_refused_on_its_box()
    {
        var supplier = Guid.NewGuid();
        var usual = new OrderableItem(Guid.NewGuid(), "BIO-GLU", new MultilingualText("Glucose"), "coffret", supplier, 6m, 120m, 0.10m);
        var captive = new OrderableItem(Guid.NewGuid(), "BIO-CRP", new MultilingualText("CRP"), "coffret", null, 2m, 300m,
                                        0.05m, 250m, supplier);
        var other = new OrderableItem(Guid.NewGuid(), "AAA-1", new MultilingualText("Gants"), "boîte", Guid.NewGuid(), 0m, null);

        PurchaseOrderScreenRules.ItemsFor([other, captive, usual], supplier).Select(i => i.Code).Should().Equal("BIO-GLU", "AAA-1", "BIO-CRP");

        var form = new PurchaseOrderLineForm();
        PurchaseOrderScreenRules.Defaults(form, usual, supplier);
        form.Should().Match<PurchaseOrderLineForm>(f => f.Quantity == "6" && f.UnitPrice == "120.00" && f.DiscountPercent == "10");
        PurchaseOrderScreenRules.Defaults(form, usual, Guid.NewGuid());
        form.DiscountPercent.Should().Be("0", "the discount was agreed with the usual supplier");
        PurchaseOrderScreenRules.Defaults(form, captive, supplier);
        form.UnitPrice.Should().Be("250.00");
        form.DiscountPercent.Should().Be("0");

        form.DiscountPercent = "10 %";
        PurchaseOrderScreenRules.CheckLine(form).Refusal.Should().Be(new Application.Input.FieldRefusal(PurchaseOrderScreenRules.DiscountField, "Result_NotANumber", "10 %"));
        form.DiscountPercent = "100";
        PurchaseOrderScreenRules.CheckLine(form).Refusal!.Key.Should().Be("Po_RateOutOfRange");
        form.DiscountPercent = "5";
        form.Quantity = "0";
        PurchaseOrderScreenRules.CheckLine(form).Refusal!.Field.Should().Be(PurchaseOrderScreenRules.QuantityField);
        form.Quantity = "2,5";
        var (refusal, request) = PurchaseOrderScreenRules.CheckLine(form);
        refusal.Should().BeNull();
        request!.Should().Match<AddPurchaseOrderLineRequest>(r => r.Quantity == 2.5m && r.DiscountRate == 0.05m && r.VatRate == 0.20m);
    }

    [Fact]
    public void A_delivery_on_a_line_is_checked_like_a_reception_and_goes_to_stock_quarantined()
    {
        var line = OrderLine();
        var form = PurchaseOrderScreenRules.NewReceipt(line, Today);
        form.Quantity.Should().Be("6");
        form.ExpiryDate.Should().Be(Today.AddMonths(12));

        PurchaseOrderScreenRules.CheckReceipt(form, line, StorageCondition.Refrigerated, Today).Refusal!.Key.Should().Be("Lot_NumberRequired");
        form.LotNumber = "GLU-2611";
        PurchaseOrderScreenRules.CheckReceipt(form, line, StorageCondition.Refrigerated, Today)
            .Refusal!.Field.Should().Be(InventoryScreenRules.TemperatureField, "a cold-chain article needs its temperature");

        form.ArrivalTemperature = "11";
        var verdict = PurchaseOrderScreenRules.CheckReceipt(form, line, StorageCondition.Refrigerated, Today);
        verdict.NeedsNonConformity.Should().BeTrue("11 °C is out of the cold chain");
        verdict.Request!.Should().Match<ReceivePurchaseOrderLineRequest>(r => r.LineId == line.Id && r.Quantity == 6m && r.LotNumber == "GLU-2611");

        form.ExpiryDate = Today.AddDays(-1);
        PurchaseOrderScreenRules.CheckReceipt(form, line, StorageCondition.Refrigerated, Today).Refusal!.Key.Should().Be("Lot_AlreadyExpired");

        var order = Order(PurchaseOrderStatus.Sent);
        PurchaseOrderScreenRules.Refusal(form, order, line, "Carton écrasé").Should()
            .Match<RefuseDeliveryRequest>(r => r.PurchaseOrderId == order.Id && r.StockItemId == line.StockItemId && r.ArrivalTemperatureCelsius == 11m);
        PurchaseOrderScreenRules.ScanMismatch(new GtinMatch(Guid.NewGuid(), "BIO-CRP", new MultilingualText("CRP")), line, "0123", Text)
            .Should().Be("Reception.GtinNotOnLine(BIO-CRP,BIO-GLU)");
        PurchaseOrderScreenRules.ScanMismatch(null, line, "0123", Text).Should().Be("Reception.GtinUnknown(0123)");
    }

    [Fact]
    public void An_approved_order_goes_by_mail_only_with_an_account_and_an_address()
    {
        PurchaseOrderScreenRules.SendRoute(true, "commandes@biosystems.ma").Should().Be(PurchaseOrderSendRoute.Email);
        PurchaseOrderScreenRules.SendRoute(true, " ").Should().Be(PurchaseOrderSendRoute.Manual);
        PurchaseOrderScreenRules.SendRoute(false, "commandes@biosystems.ma").Should().Be(PurchaseOrderSendRoute.Manual);
        PurchaseOrderScreenRules.ManualReason(false, "Biosystems", Text).Should().Be("Purchasing_EmailNotConfigured");
        PurchaseOrderScreenRules.ManualReason(true, "Biosystems", Text).Should().Be("Po_SupplierEmailMissing(Biosystems)");
    }

    // ---- Quotes -------------------------------------------------------------------------------------

    private static QuoteRequestDetail Quote(QuoteRequestStatus status = QuoteRequestStatus.Open, bool expired = false, bool orderCancelled = false)
    {
        var item = new QuoteItemRow(Guid.NewGuid(), 1, Guid.NewGuid(), "BIO-GLU", "Glucose", "coffret", 12m);
        var a = new QuoteSupplierRow(Guid.NewGuid(), "F1", "Biosystems", "a@b.ma", true, Today.ToDateTime(TimeOnly.MinValue), true);
        var b = new QuoteSupplierRow(Guid.NewGuid(), "F2", "Labomaroc", null, false, null, false);
        var offer = new QuoteOfferRow(Guid.NewGuid(), a.SupplierId, a.Name, Today, Today.AddDays(30), 5, null, null, expired, 1440m, 0, "devis.pdf");
        var line = new QuoteComparisonLine(item.Id, 1, item.StockItemId, 12m,
            [new QuoteComparisonCell(offer.Id, a.SupplierId, 120m, 0m, 120m, 0.2m, 1440m, 5, expired, !expired, !expired)]);
        return new QuoteRequestDetail(Guid.NewGuid(), "DD/2026/0004", status, Today, "Biochimie", Today.AddDays(7), null,
                                      status == QuoteRequestStatus.Awarded ? offer.Id : null, [item], [a, b], [offer], [line], orderCancelled);
    }

    [Fact]
    public void A_request_offers_its_steps_while_open_and_its_award_needs_both_rights()
    {
        var quotes = Rights(Permissions.Inventory.ManageQuotes);
        var both = Rights(Permissions.Inventory.ManageQuotes, Permissions.Inventory.ManagePurchaseOrders);
        var detail = Quote();
        var answered = detail.Suppliers[0];
        var silent = detail.Suppliers[1];

        QuoteScreenRules.Actions(both, detail, answered)
            .Should().Match<QuoteScreenActions>(a => a.SendByEmail && !a.MarkSent && a.RecordOffer && a.OpenFile && a.Award && a.Cancel);
        QuoteScreenRules.Actions(both, detail, silent).Should().Match<QuoteScreenActions>(a => a.MarkSent && !a.Award && !a.OpenFile);
        QuoteScreenRules.Actions(quotes, detail, answered).Award.Should().BeFalse("choosing the offer drafts a purchase order");
        QuoteScreenRules.Actions(both, Quote(expired: true), Quote(expired: true).Suppliers[0]).Award.Should().BeFalse();

        var awarded = Quote(QuoteRequestStatus.Awarded);
        QuoteScreenRules.Actions(both, awarded, awarded.Suppliers[0]).Should().Match<QuoteScreenActions>(a => !a.Award && !a.AddSupplier);
        var reawardable = Quote(QuoteRequestStatus.Awarded, orderCancelled: true);
        QuoteScreenRules.Actions(both, reawardable, reawardable.Suppliers[0]).Award.Should().BeTrue("its purchase order was cancelled");
        QuoteScreenRules.IsAwarded(awarded, awarded.Suppliers[0]).Should().BeTrue();
        QuoteScreenRules.KeepSupplier(awarded, null).Should().Be(awarded.Suppliers[0]);
    }

    [Fact]
    public void The_comparison_marks_the_cheapest_and_fastest_never_an_expired_offer()
    {
        var detail = Quote();
        var look = QuoteScreenRules.Cell(detail.Comparison[0], detail.Offers[0], Text, Mad);
        look.Should().Be(new QuoteCellLook("120.00 · 5 Quotes.DaysShort", true, true, false));

        var expired = Quote(expired: true);
        QuoteScreenRules.Cell(expired.Comparison[0], expired.Offers[0], Text, Mad).Should().Match<QuoteCellLook>(l => l.Expired && !l.Cheapest);
        QuoteScreenRules.OfferHeader(expired.Offers[0], Text).Should().Be("Biosystems (Quotes.Expired)");
        QuoteScreenRules.SupplierInk(expired, expired.Suppliers[1]).Should().Be(StockInk.Muted);
        QuoteScreenRules.SupplierName(detail.Suppliers[1], Text).Should().Be("Labomaroc (Quotes.NotApproved)");
        QuoteScreenRules.Sent(detail.Suppliers[1], Text).Should().Be("Quotes.NotSent");
        QuoteScreenRules.ItemLabel(detail, detail.Comparison[0]).Should().Be("BIO-GLU — Glucose");
        QuoteScreenRules.AwardQuestion(detail.Offers[0] with { MissingItems = 2 }, "1440", Text)
            .Should().Be("Quotes.AwardQuestion(Biosystems,1440)\n\nQuotes.AwardMissingItems(2)");
        QuoteScreenRules.SendOutcome(new QuoteSendOutcome(1, ["Labomaroc"], []), Text)
            .Should().Be("Quotes.SentCount(1)\n\nQuotes.WithoutEmail(Labomaroc)");
    }

    [Fact]
    public void A_request_is_refused_without_items_or_suppliers_and_an_article_on_contract_is_never_quoted()
    {
        var glucose = new QuotableItem(Guid.NewGuid(), "BIO-GLU", new MultilingualText("Glucose"), "coffret", 6m, null);
        var captive = new QuotableItem(Guid.NewGuid(), "BIO-CRP", new MultilingualText("CRP"), "coffret", 2m, "CTR-2025-01");
        var supplier = Guid.NewGuid();
        var form = QuoteScreenRules.NewRequest([glucose, captive], Today);
        form.Items[0].Quantity.Should().Be("6");
        form.Items[1].OnContract.Should().BeTrue();

        QuoteScreenRules.CheckRequest(form, [supplier], Today).Refusal!.Key.Should().Be("Quote_NoItems");
        form.Items[1].Included = true;
        QuoteScreenRules.CheckRequest(form, [supplier], Today).Refusal!.Key.Should().Be("Quote_NoItems", "an article on contract is left out");

        form.Items[0].Included = true;
        form.Items[0].Quantity = "0";
        QuoteScreenRules.CheckRequest(form, [supplier], Today).Refusal!.Field.Should().Be(QuoteScreenRules.QuantityField(0));
        form.Items[0].Quantity = "6";
        QuoteScreenRules.CheckRequest(form, [supplier], Today).Refusal!.Key.Should().Be("Quote_NoSuppliers");

        form.SupplierIds.Add(supplier);
        var (refusal, request) = QuoteScreenRules.CheckRequest(form, [supplier], Today);
        refusal.Should().BeNull();
        request!.Items.Should().ContainSingle().Which.StockItemId.Should().Be(glucose.Id);
        request.ResponseDueDate.Should().Be(Today.AddDays(7));
    }

    [Fact]
    public void An_answer_is_refused_on_the_figure_that_is_no_number_and_needs_one_price()
    {
        var detail = Quote();
        var form = QuoteScreenRules.NewOffer(detail, Today);
        form.ValidUntil.Should().Be(Today.AddMonths(1));

        QuoteScreenRules.CheckOffer(form, Guid.NewGuid(), Today).Refusal!.Key.Should().Be("Quote_OfferHasNoPrice");
        form.Lines[0].UnitPrice = "118";
        form.Lines[0].DiscountPercent = "10 %";
        QuoteScreenRules.CheckOffer(form, Guid.NewGuid(), Today).Refusal!.Field.Should().Be(QuoteScreenRules.OfferField("discount", 0));
        form.Lines[0].DiscountPercent = "10";
        form.DelayDays = "-3";
        QuoteScreenRules.CheckOffer(form, Guid.NewGuid(), Today).Refusal!.Key.Should().Be("Quote_DelayMustNotBeNegative");
        form.DelayDays = "5";
        form.ReceivedOn = Today.AddDays(1);
        QuoteScreenRules.CheckOffer(form, Guid.NewGuid(), Today).Refusal!.Key.Should().Be("Quote_ReceivedInFuture");
        form.ReceivedOn = Today;
        form.FileName = "devis.exe";
        form.FileContent = [1, 2, 3];
        QuoteScreenRules.CheckOffer(form, Guid.NewGuid(), Today).Refusal!.Key.Should().Be("Purchasing_FileType");
        form.FileName = "devis.pdf";

        var (refusal, request) = QuoteScreenRules.CheckOffer(form, detail.Suppliers[0].SupplierId, Today);
        refusal.Should().BeNull();
        request!.Lines.Should().ContainSingle().Which.Should().Be(new SupplierQuoteLineInput(detail.Items[0].Id, 118m, 0.2m, 0.1m));
        request.DeliveryDelayDays.Should().Be(5);
        request.FileName.Should().Be("devis.pdf");
    }

    // ---- Supplier invoices --------------------------------------------------------------------------

    internal static SupplierInvoiceRow Invoice(SupplierInvoiceStatus status = SupplierInvoiceStatus.Blocked, int days = 12, decimal paid = 0m,
                                               Guid? enteredBy = null, bool scan = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Biosystems Maroc", "FA-2026-0778", Today.AddDays(-20), Today.AddDays(-18), Today.AddDays(-25),
            Today.AddDays(days), days, status, 1000m, 1200m, paid, status == SupplierInvoiceStatus.Blocked ? 2 : 0, scan, "Salma Amrani",
            enteredBy ?? Colleague);

    [Fact]
    public void A_blocked_invoice_is_released_by_someone_else_and_only_one_good_to_pay_is_paid()
    {
        var all = Rights(Permissions.Inventory.EnterInvoices, Permissions.Inventory.ApprovePayments);

        SupplierInvoiceScreenRules.Actions(all, Me, Invoice())
            .Should().Match<SupplierInvoiceScreenActions>(a => a.Rematch && a.Unblock && !a.UnblockOwn && !a.Pay && a.Cancel && a.Attach && !a.ViewScan);
        SupplierInvoiceScreenRules.Actions(all, Me, Invoice(enteredBy: Me)).UnblockOwn.Should().BeTrue();
        SupplierInvoiceScreenRules.Actions(all, Me, Invoice(SupplierInvoiceStatus.ApprovedForPayment, scan: true))
            .Should().Match<SupplierInvoiceScreenActions>(a => a.Pay && !a.Unblock && a.ViewScan);
        SupplierInvoiceScreenRules.Actions(all, Me, Invoice(SupplierInvoiceStatus.PartiallyPaid, paid: 100m)).Cancel
            .Should().BeFalse("something was paid on it");
        SupplierInvoiceScreenRules.Actions(Rights(Permissions.Inventory.EnterInvoices), Me, Invoice(SupplierInvoiceStatus.ApprovedForPayment))
            .Should().Match<SupplierInvoiceScreenActions>(a => !a.Pay && !a.Unblock && !a.Export && !a.Settings);
        SupplierInvoiceScreenRules.Actions(Rights(Permissions.Billing.ExportAccounting, Permissions.Administration.ManageSettings), Me, null)
            .Should().Match<SupplierInvoiceScreenActions>(a => a.Export && a.Settings && !a.New);
    }

    [Fact]
    public void An_overdue_invoice_is_in_the_alarm_and_a_blocked_one_on_its_number()
    {
        var overdue = Invoice(SupplierInvoiceStatus.ApprovedForPayment, days: -5);
        SupplierInvoiceScreenRules.RowInk(overdue).Should().Be(StockInk.Critical);
        SupplierInvoiceScreenRules.StatusTone(overdue).Should().Be(StockTone.Critical);
        SupplierInvoiceScreenRules.Days(overdue, Text).Should().Be("SupplierInvoices.DaysLate(5)");
        SupplierInvoiceScreenRules.Days(Invoice(), Text).Should().Be("SupplierInvoices.DaysLeft(12)");
        SupplierInvoiceScreenRules.NumberInk(Invoice()).Should().Be(StockInk.AbnormalBold);
        SupplierInvoiceScreenRules.StatusTone(Invoice()).Should().Be(StockTone.Warning);
        SupplierInvoiceScreenRules.RowInk(Invoice(SupplierInvoiceStatus.Paid, paid: 1200m)).Should().Be(StockInk.Muted);
        SupplierInvoiceScreenRules.Days(Invoice(SupplierInvoiceStatus.Paid, paid: 1200m), Text).Should().BeEmpty();

        var due = new SupplierDueSummary(1, 1200m, 2, 300m, 3, 400m, 1);
        SupplierInvoiceScreenRules.Chip(SupplierInvoiceFilter.Overdue, due, Text).Should().Be("SupplierInvoiceFilter.Overdue · 1");
        SupplierInvoiceScreenRules.Chip(SupplierInvoiceFilter.All, due, Text).Should().Be("SupplierInvoiceFilter.All");
        SupplierInvoiceScreenRules.DueSummary(due, Text, Mad).Should().Be(("SupplierInvoices.DueSummary(1,1200.00,2,300.00,3,400.00,1)", true));

        SupplierInvoiceScreenRules.Variance(new SupplierInvoiceVarianceRow(1, VarianceKind.PriceDiffers, 114m, 120m), Text, Mad)
            .Should().Be("VarianceKind.PriceDiffers(114.00,120.00)");
        SupplierInvoiceScreenRules.Variance(new SupplierInvoiceVarianceRow(2, VarianceKind.VatRateDiffers, 0.2m, 0.14m), Text, Mad, CultureInfo.InvariantCulture)
            .Should().Be("VarianceKind.VatRateDiffers(20%,14%)");
        SupplierInvoiceScreenRules.Outcome(new SupplierInvoiceOutcome(Guid.NewGuid(), SupplierInvoiceStatus.Blocked, 2, Today), Text)
            .Should().Be(("SupplierInvoices.CreatedBlocked(2,18/10/26)", true));
        SupplierInvoiceScreenRules.Outcome(new SupplierInvoiceOutcome(Guid.NewGuid(), SupplierInvoiceStatus.Blocked, 1, Today), Text, rematch: true)
            .Should().Be(("SupplierInvoices.StillBlocked(1)", true));
    }

    [Fact]
    public void An_invoice_is_entered_against_the_lines_received_and_refused_on_its_box()
    {
        var received = new InvoiceableOrderLine(Guid.NewGuid(), "BC/2026/00012", Today.AddDays(-30), "BIO-GLU", "Glucose", "coffret",
                                                10m, 6m, 0m, 114m, 0.2m);
        var awaited = new InvoiceableOrderLine(Guid.NewGuid(), "BC/2026/00013", Today.AddDays(-30), "GANT-M", "Gants", "boîte",
                                               20m, 0m, 0m, 35m, 0.2m);
        var form = SupplierInvoiceScreenRules.NewInvoice(Today);
        form.Lines = SupplierInvoiceScreenRules.Lines([received, awaited]);
        form.Lines[0].Should().Match<SupplierInvoiceLineForm>(l => l.Included && l.Quantity == "6" && l.UnitPrice == "114.00" && l.VatPercent == "20");
        form.Lines[1].Should().Match<SupplierInvoiceLineForm>(l => !l.Included && l.Quantity == "20");
        SupplierInvoiceScreenRules.Totals(form).Should().Be((684m, 136.8m, 820.8m));

        SupplierInvoiceScreenRules.CheckInvoice(form, Today).Refusal!.Field.Should().Be(SupplierInvoiceScreenRules.SupplierField);
        form.SupplierId = Guid.NewGuid();
        SupplierInvoiceScreenRules.CheckInvoice(form, Today).Refusal!.Field.Should().Be(SupplierInvoiceScreenRules.NumberField);
        form.InvoiceNumber = " FA-2026-0778 ";
        form.ReceivedOn = Today.AddDays(-1);
        form.InvoiceDate = Today;
        SupplierInvoiceScreenRules.CheckInvoice(form, Today).Refusal!.Key.Should().Be("SupplierInvoice_ReceivedOnInvalid");
        form.ReceivedOn = Today;
        form.Lines[0].VatPercent = "120";
        SupplierInvoiceScreenRules.CheckInvoice(form, Today).Refusal.Should()
            .Be(new Application.Input.FieldRefusal(SupplierInvoiceScreenRules.LineField("vat", 0), "SupplierInvoice_VatRateOutOfRange"));
        form.Lines[0].VatPercent = "20";
        form.Lines[0].UnitPrice = "120";

        var (refusal, request) = SupplierInvoiceScreenRules.CheckInvoice(form, Today);
        refusal.Should().BeNull();
        request!.InvoiceNumber.Should().Be("FA-2026-0778");
        request.Lines.Should().ContainSingle().Which.Should().Be(new SupplierInvoiceLineRequest(received.PurchaseOrderLineId, 6m, 120m, 0.2m));

        form.Lines[0].Included = false;
        SupplierInvoiceScreenRules.CheckInvoice(form, Today).Refusal!.Key.Should().Be("SupplierInvoice_NoLines");

        SupplierInvoiceScreenRules.CheckScan("scan.docx", 100).Should().Be(new Application.Input.FieldRefusal(SupplierInvoiceScreenRules.ScanField, "SupplierInvoice_ScanType"));
        SupplierInvoiceScreenRules.CheckScan("scan.pdf", SupplierInvoiceFile.MaximumBytes + 1L)!.Key.Should().Be("SupplierInvoice_ScanTooLarge");
        SupplierInvoiceScreenRules.CheckScan("scan.PDF", 2048).Should().BeNull();
    }

    [Fact]
    public void A_payment_needs_its_reference_and_a_bill_of_exchange_its_later_due_date()
    {
        var form = SupplierInvoiceScreenRules.NewPayment(Invoice(SupplierInvoiceStatus.ApprovedForPayment, paid: 200m), Today);
        form.Amount.Should().Be("1000.00");

        SupplierInvoiceScreenRules.CheckPayment(form).Refusal!.Key.Should().Be("SupplierPayment_ReferenceRequired");
        form.Method = SupplierPaymentMethod.Cash;
        SupplierInvoiceScreenRules.CheckPayment(form).Request!.MaturityDate.Should().BeNull();

        form.Method = SupplierPaymentMethod.BillOfExchange;
        form.Reference = "LCN 551204";
        SupplierInvoiceScreenRules.CheckPayment(form).Refusal!.Key.Should().Be("SupplierPayment_MaturityRequired");
        form.MaturityDate = Today.AddDays(-1);
        SupplierInvoiceScreenRules.CheckPayment(form).Refusal!.Key.Should().Be("SupplierPayment_MaturityBeforePayment");
        form.MaturityDate = Today.AddDays(60);
        SupplierInvoiceScreenRules.CheckPayment(form).Request!.MaturityDate.Should().Be(Today.AddDays(60));

        form.Method = SupplierPaymentMethod.Transfer;
        SupplierInvoiceScreenRules.CheckPayment(form).Request!.MaturityDate.Should().BeNull("a transfer has no due date of its own");
        form.Amount = "0";
        SupplierInvoiceScreenRules.CheckPayment(form).Refusal!.Field.Should().Be(SupplierInvoiceScreenRules.AmountField);
    }

    /// <summary>
    /// The figures an order line, a delivery, an invoice, a payment and a quote start from are written
    /// as the screen's language writes them — « 120,00 » in French — and read back unchanged: the web
    /// showed « 120.00 » with a dot where every other box of the screen took the comma.
    /// </summary>
    [Fact]
    public void The_purchasing_forms_are_prefilled_with_the_decimal_comma_of_the_screen()
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        var supplier = Guid.NewGuid();
        var usual = new OrderableItem(Guid.NewGuid(), "BIO-GLU", new MultilingualText("Glucose"), "coffret", supplier, 2.5m, 120.5m, 0.125m);

        var line = new PurchaseOrderLineForm();
        PurchaseOrderScreenRules.Defaults(line, usual, supplier, fr);
        line.Should().Match<PurchaseOrderLineForm>(f => f.Quantity == "2,5" && f.UnitPrice == "120,50" && f.DiscountPercent == "12,5");
        var (refusal, request) = PurchaseOrderScreenRules.CheckLine(line);
        refusal.Should().BeNull();
        request!.Should().Match<AddPurchaseOrderLineRequest>(r => r.Quantity == 2.5m && r.UnitPriceMad == 120.5m && r.DiscountRate == 0.125m);

        var received = new InvoiceableOrderLine(Guid.NewGuid(), "BC/2026/00012", Today.AddDays(-30), "BIO-GLU", "Glucose", "coffret",
                                                10m, 6.5m, 0m, 114m, 0.2m);
        SupplierInvoiceScreenRules.Lines([received], fr)[0]
            .Should().Match<SupplierInvoiceLineForm>(l => l.Quantity == "6,5" && l.UnitPrice == "114,00" && l.VatPercent == "20");
        SupplierInvoiceScreenRules.NewPayment(Invoice(SupplierInvoiceStatus.ApprovedForPayment, paid: 200.5m), Today, fr)
            .Amount.Should().Be("999,50");
    }

    [Fact]
    public void The_period_is_the_last_quarter_and_the_parameters_are_refused_out_of_range()
    {
        var period = SupplierInvoiceScreenRules.DefaultPeriod(Today);
        (period.From, period.To).Should().Be((new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)));
        SupplierInvoiceScreenRules.LatePaymentsFileName(period).Should().Be("retards-paiement-20260701-20260930.csv");
        SupplierInvoiceScreenRules.ExportFileName(period).Should().Be("export-achats-sage-20260701-20260930.csv");
        SupplierInvoiceScreenRules.CheckPeriod(new PurchasingPeriodForm { From = Today, To = Today.AddDays(-1) })!.Key.Should().Be("Purchasing_PeriodReversed");

        var current = PurchasingSettings.Default;
        var form = SupplierInvoiceScreenRules.SettingsForm(current);
        form.KeyRate.Should().Be("2.25");
        form.KeyRate = "150";
        SupplierInvoiceScreenRules.CheckSettings(form, current).Refusal!.Field.Should().Be(SupplierInvoiceScreenRules.SettingField("key-rate"));
        form.KeyRate = "2,5";
        form.BankJournal = " ";
        form.ChargeAccounts[StockItemKind.Reagent] = "61221";
        var (refusal, settings) = SupplierInvoiceScreenRules.CheckSettings(form, current);
        refusal.Should().BeNull();
        settings!.KeyRatePercent.Should().Be(2.5m);
        settings.BankJournal.Should().Be(current.BankJournal, "a code left blank keeps the current one");
        settings.ChargeAccountFor(StockItemKind.Reagent).Should().Be("61221");
    }

    // ---- Supplier evaluation ------------------------------------------------------------------------

    [Fact]
    public void A_poor_score_or_a_lapsed_registration_is_in_the_alarm_and_the_score_applied_is_read_whole()
    {
        var poor = new SupplierEvaluationRow(Guid.NewGuid(), "F1", "Labomaroc", true, 70, Today.AddYears(-1), "AMMPS-1", Today.AddYears(1), false,
                                             new SupplierKpis(10, 10, 4, 40m, 6m, 2, 1, 47));
        var lapsed = poor with { RegistrationExpired = true, Kpis = poor.Kpis with { ProposedScore = 90 } };
        var none = poor with { Kpis = poor.Kpis with { ProposedScore = null } };

        SupplierEvaluationScreenRules.Ink(poor).Should().Be(StockInk.Critical);
        SupplierEvaluationScreenRules.Ink(lapsed).Should().Be(StockInk.Critical);
        SupplierEvaluationScreenRules.Ink(lapsed with { RegistrationExpired = false }).Should().Be(StockInk.Plain);

        var manage = Rights(Permissions.Inventory.ManagePurchaseOrders);
        SupplierEvaluationScreenRules.Actions(manage, poor).Should().Be(new SupplierEvaluationScreenActions(true, true, true));
        SupplierEvaluationScreenRules.Actions(manage, none).Apply.Should().BeFalse("no evidence, no score proposed");
        SupplierEvaluationScreenRules.Actions(Rights(), poor).Should().Be(new SupplierEvaluationScreenActions(false, false, false));

        SupplierEvaluationScreenRules.Initial(poor).Should().Be("47");
        SupplierEvaluationScreenRules.ReadScore(" 55 ").Should().Be(((int?)55, (Application.Input.FieldRefusal?)null));
        SupplierEvaluationScreenRules.ReadScore("bon").Refusal!.Key.Should().Be("Result_NotANumber");
        SupplierEvaluationScreenRules.ReadScore("120").Refusal!.Key.Should().Be("Supplier_ScoreOutOfRange");
        SupplierEvaluationScreenRules.Headers(Text).Should().HaveCount(SupplierEvaluationScreenRules.Cells(poor).Count);
        SupplierEvaluationScreenRules.FileName(2026, "pdf").Should().Be("evaluation-fournisseurs-2026.pdf");
        SupplierEvaluationScreenRules.Year(1990).Should().Be(SupplierEvaluationScreenRules.YearMin);
    }
}
