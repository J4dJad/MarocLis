using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Bons de commande » offers, by right and by the state of the order and of the line
/// selected — the workstation's PermissionGate calls and RefreshButtons. PurchaseOrderService and
/// PurchaseOrderDocumentService stay the judges: each refuses an operation the right or the state
/// forbids.
/// </summary>
/// <param name="New">A new order raised (managing orders).</param>
/// <param name="AddLine">A line added to the draft selected (managing orders).</param>
/// <param name="Approve">The draft approved, by someone other than its author (approving orders).</param>
/// <param name="OwnOrder">The draft is the operator's own: the approval is someone else's, said on the button.</param>
/// <param name="Send">The approved order sent to the supplier, by e-mail or by hand (managing orders).</param>
/// <param name="Receive">The line selected received into stock as a quarantined lot (reception right).</param>
/// <param name="Cancel">The order cancelled, with its reason, before any delivery (managing orders).</param>
/// <param name="Print">The order's PDF, once approved.</param>
public sealed record PurchaseOrderScreenActions(
    bool New, bool AddLine, bool Approve, bool OwnOrder, bool Send, bool Receive, bool Cancel, bool Print);

/// <summary>A new order as typed: the workstation's PurchaseOrderDialog.</summary>
public sealed class PurchaseOrderForm
{
    public Guid? SupplierId { get; set; }

    /// <summary>The delivery date is optional: unticked, the order asks for none.</summary>
    public bool HasExpectedDelivery { get; set; } = true;

    public DateOnly ExpectedDelivery { get; set; }
    public string? SupplierReference { get; set; }
    public string? Notes { get; set; }
}

/// <summary>A line as typed: the workstation's PurchaseOrderLineDialog.</summary>
public sealed class PurchaseOrderLineForm
{
    public Guid? StockItemId { get; set; }
    public string? Quantity { get; set; }
    public string? UnitPrice { get; set; }
    public string? DiscountPercent { get; set; } = "0";
    public string? VatPercent { get; set; } = "20";
}

/// <summary>How an approved order goes to its supplier: by e-mail with its PDF, or sent by hand and confirmed.</summary>
public enum PurchaseOrderSendRoute
{
    /// <summary>The laboratory has a mail account and the supplier an address.</summary>
    Email,

    /// <summary>No mail account, no address, or the mail refused: the operator sends it and confirms.</summary>
    Manual
}

/// <summary>A delivery against a line, checked: refused on a box, or the request — with the ambient warning and the non-conformity to confirm.</summary>
public sealed record PurchaseOrderReceiptVerdict(FieldRefusal? Refusal, ReceivePurchaseOrderLineRequest? Request,
                                                 bool AmbientWarning, bool NeedsNonConformity);

/// <summary>
/// The rules of « Bons de commande » that need no window: what is offered by which right and in
/// which state, how an order and its lines read (the pending priorities first, a late delivery said
/// on its date, the state's pill, the totals), how a new order, a line and a delivery are typed and
/// refused box by box, and which way an approved order goes to its supplier. The workstation's
/// PurchaseOrderView and its dialogs and the web's screen apply the same ones; every decision stays
/// the services' and the domain's.
/// </summary>
public static class PurchaseOrderScreenRules
{
    // ---- The boxes, bounded to their columns: beyond, the save failed with « Erreur inattendue ».
    public const int SupplierReferenceMaxLength = 60;
    public const int NotesMaxLength = 1000;
    public const int CancelReasonMaxLength = 1000;
    public const int QuantityMaxLength = TypedNumber.MeasureMaxLength;
    public const int PriceMaxLength = TypedNumber.AmountMaxLength;
    public const int RateMaxLength = TypedNumber.MeasureMaxLength;

    /// <summary>A delivery's default expiry, one year ahead, and an order's default delivery date, a week ahead.</summary>
    public const int DefaultDeliveryDays = 7;

    public const string SupplierField = "po-supplier";
    public const string ExpectedField = "po-expected";
    public const string ItemField = "po-item";
    public const string QuantityField = "po-quantity";
    public const string PriceField = "po-price";
    public const string DiscountField = "po-discount";
    public const string VatField = "po-vat";

    // ---- Rights and states ------------------------------------------------------------------------

    public static PurchaseOrderScreenActions Actions(ICurrentUser user, PurchaseOrderRow? order, PurchaseOrderLineRow? line)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, user.UserId, order, line);
    }

    /// <summary>
    /// Only the steps the order's state allows: lines are added to a draft, a draft with lines is
    /// approved — never by its author —, an approved order is sent, a line still awaited is received
    /// on an order that is out, an order nothing was delivered on may be cancelled, and the PDF exists
    /// once the order is approved.
    /// </summary>
    public static PurchaseOrderScreenActions Actions(Func<string, bool> has, Guid userId, PurchaseOrderRow? order,
                                                     PurchaseOrderLineRow? line)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Inventory.ManagePurchaseOrders);
        var own = order is { Status: PurchaseOrderStatus.Draft } && order.RaisedByUserId == userId;

        return new PurchaseOrderScreenActions(
            New: manage,
            AddLine: manage && order is { Status: PurchaseOrderStatus.Draft },
            Approve: has(Permissions.Inventory.ApprovePurchaseOrders) && order is { Status: PurchaseOrderStatus.Draft, LineCount: > 0 } && !own,
            OwnOrder: own,
            Send: manage && order is { Status: PurchaseOrderStatus.Approved },
            Receive: has(Permissions.Inventory.Receive)
                     && order?.Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived
                     && line is { QuantityOutstanding: > 0m },
            Cancel: manage && order?.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent,
            Print: order is not null && PurchaseOrderDocumentService.IsPrintable(order.Status));
    }

    /// <summary>The priority orders still to be dealt with first: they are the ones that stop a consumable from running out.</summary>
    public static IReadOnlyList<PurchaseOrderRow> Ordered(IEnumerable<PurchaseOrderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.OrderByDescending(PurchaseOrderRules.IsPendingPriority)];
    }

    /// <summary>The order kept selected after a reload: the one worked on, else the first.</summary>
    public static PurchaseOrderRow? Keep(IReadOnlyList<PurchaseOrderRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.Id == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    /// <summary>The first line still awaiting goods: the one a receipt most likely concerns.</summary>
    public static PurchaseOrderLineRow? FirstOutstanding(IReadOnlyList<PurchaseOrderLineRow> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.FirstOrDefault(l => l.QuantityOutstanding > 0m) ?? (lines.Count > 0 ? lines[0] : null);
    }

    // ---- How an order reads ------------------------------------------------------------------------

    public static StockTone StatusTone(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Received => StockTone.Done,
        PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived => StockTone.Running,
        _ => StockTone.Waiting
    };

    public static string StatusKey(PurchaseOrderStatus status) => $"PurchaseOrderStatus.{status}";

    /// <summary>« Prioritaire (auto) » for an order the reorder check raised, « Prioritaire » for one marked by hand; nothing otherwise.</summary>
    public static string? PriorityKey(PurchaseOrderRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsPriority ? row.RaisedAutomatically ? "PurchaseOrders.PriorityAutomatic" : "PurchaseOrders.PriorityFlag" : null;
    }

    /// <summary>A priority still awaited in the alarm's colours; one dealt with, waiting.</summary>
    public static StockTone PriorityTone(PurchaseOrderRow row) =>
        PurchaseOrderRules.IsPendingPriority(row) ? StockTone.Critical : StockTone.Waiting;

    /// <summary>
    /// The priority shows on its pill and on the number, a late delivery on its date: a whole row in
    /// colour is kept for the critical cases. An order closed or cancelled is muted.
    /// </summary>
    /// <param name="column">number or expected carry the warning; any other is plain.</param>
    public static StockInk Ink(PurchaseOrderRow row, string column)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (PurchaseOrderRules.IsPendingPriority(row)) return column == "number" ? StockInk.CriticalBold : StockInk.Plain;
        if (row.IsOverdue) return column == "expected" ? StockInk.AbnormalBold : StockInk.Plain;
        return row.Status is PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Received ? StockInk.Muted : StockInk.Plain;
    }

    /// <summary>The whole row muted for an order closed or cancelled, else plain.</summary>
    public static StockInk RowInk(PurchaseOrderRow row) =>
        !PurchaseOrderRules.IsPendingPriority(row) && !row.IsOverdue && row.Status is PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Received
            ? StockInk.Muted
            : StockInk.Plain;

    public static string Date(DateOnly date) => date.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    /// <summary>« ! 12/10/26 » when the delivery is late, the date alone otherwise.</summary>
    public static string Expected(PurchaseOrderRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ExpectedDeliveryDate is { } expected ? (row.IsOverdue ? "! " : string.Empty) + Date(expected) : string.Empty;
    }

    /// <summary>« 12 commandes · à approuver 2 · en retard 1 ».</summary>
    public static string Count(IReadOnlyCollection<PurchaseOrderRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var overdue = rows.Count(r => r.IsOverdue);
        var toApprove = rows.Count(r => r.Status == PurchaseOrderStatus.Draft && r.LineCount > 0);
        var count = text("StocksUi.Orders", rows.Count);
        if (toApprove > 0) count += $" · {text("PurchaseOrders.ToApprove")} {toApprove.ToString(CultureInfo.InvariantCulture)}";
        if (overdue > 0) count += $" · {text("PurchaseOrders.Late")} {overdue.ToString(CultureInfo.InvariantCulture)}";
        return count;
    }

    /// <summary>« BC/2026/00012 · Biosystems Maroc », and the reason of a cancelled order.</summary>
    public static string LinesNote(PurchaseOrderRow? order, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (order is null) return string.Empty;
        return order is { Status: PurchaseOrderStatus.Cancelled, CancellationReason: { } why }
            ? $"{order.Number} · {order.SupplierName} · {text("PurchaseOrders.CancelledBecause", why)}"
            : $"{order.Number} · {order.SupplierName}";
    }

    /// <summary>The order's totals: without VAT, the VAT, and with it.</summary>
    public static (decimal ExcludingVat, decimal Vat, decimal IncludingVat) Totals(PurchaseOrderRow order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return (order.TotalExcludingVat, order.TotalIncludingVat - order.TotalExcludingVat, order.TotalIncludingVat);
    }

    public static string Amount(decimal value, IFormatProvider? provider = null) =>
        value.ToString("N2", provider ?? CultureInfo.CurrentCulture);

    /// <summary>Shown, never read back: written with the decimal comma of the language, not « 2.5 ».</summary>
    public static string Quantity(decimal value, IFormatProvider? provider = null) =>
        value.ToString("0.##", provider ?? CultureInfo.CurrentCulture);

    /// <summary>« 20% » from 0.20; a discount of none is left empty.</summary>
    public static string Rate(decimal rate, bool blankWhenZero = false, IFormatProvider? provider = null) =>
        blankWhenZero && rate == 0m ? string.Empty : (rate * 100m).ToString("0.#", provider ?? CultureInfo.CurrentCulture) + "%";

    // ---- A new order --------------------------------------------------------------------------------

    /// <summary>A new order's form: the goods wanted within a week.</summary>
    public static PurchaseOrderForm NewOrder(DateOnly today) => new() { ExpectedDelivery = today.AddDays(DefaultDeliveryDays) };

    /// <summary>The supplier, as « FRN-001 — Biosystems Maroc ».</summary>
    public static string SupplierLabel(SupplierOption supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        return $"{supplier.Code} — {supplier.Name}";
    }

    /// <summary>A supplier is required; the delivery date, when asked, is not in the past.</summary>
    public static (FieldRefusal? Refusal, CreatePurchaseOrderRequest? Request) CheckOrder(PurchaseOrderForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.SupplierId is not { } supplierId) return (new FieldRefusal(SupplierField, "Po_SupplierRequired"), null);
        if (form.HasExpectedDelivery && form.ExpectedDelivery < today)
            return (new FieldRefusal(ExpectedField, "Po_ExpectedDateInPast"), null);

        return (null, new CreatePurchaseOrderRequest
        {
            SupplierId = supplierId,
            ExpectedDeliveryDate = form.HasExpectedDelivery ? form.ExpectedDelivery : null,
            SupplierReference = Blank(form.SupplierReference),
            Notes = Blank(form.Notes)
        });
    }

    // ---- A line -------------------------------------------------------------------------------------

    /// <summary>The articles normally bought from this supplier first: the usual case, and the short list.</summary>
    public static IReadOnlyList<OrderableItem> ItemsFor(IEnumerable<OrderableItem> items, Guid? supplierId)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.OrderBy(i => supplierId is not null && i.PreferredSupplierId == supplierId ? 0 : 1)
                        .ThenBy(i => i.Code, StringComparer.Ordinal)];
    }

    public static string ItemLabel(OrderableItem item, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{item.Code} — {item.Name.For(language)}";
    }

    /// <summary>A new line's form, on the first article of the list.</summary>
    public static PurchaseOrderLineForm NewLine(IReadOnlyList<OrderableItem> items, Guid? supplierId)
    {
        ArgumentNullException.ThrowIfNull(items);
        var form = new PurchaseOrderLineForm();
        if (items.Count > 0) Defaults(form, items[0], supplierId);
        return form;
    }

    /// <summary>
    /// The reorder quantity, the last list price and, from the item's usual supplier, its usual
    /// discount, as starting values; a captive reagent ordered from its contract's supplier at the
    /// contracted price, without discount.
    /// </summary>
    public static void Defaults(PurchaseOrderLineForm form, OrderableItem item, Guid? supplierId, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(item);
        form.StockItemId = item.Id;
        culture ??= CultureInfo.InvariantCulture;
        form.Quantity = item.ReorderQuantity > 0m ? TypedNumber.ForEditing(item.ReorderQuantity, 0, culture) : string.Empty;
        form.UnitPrice = item.LastUnitPriceMad is { } last ? TypedNumber.ForEditing(last, 2, culture) : string.Empty;

        // The discount was agreed with the preferred supplier: another supplier starts from none.
        var discount = supplierId is not null && item.PreferredSupplierId == supplierId ? item.LastDiscountRate : 0m;
        if (item.ContractPriceMad is { } contracted && supplierId is not null && item.ContractSupplierId == supplierId)
        {
            form.UnitPrice = TypedNumber.ForEditing(contracted, 2, culture);
            discount = 0m;
        }

        form.DiscountPercent = TypedNumber.ForEditing(discount * 100m, 0, culture);
    }

    /// <summary>
    /// A line as the dialog checks it before it closes: every figure reads as one (« 10 % » in the
    /// discount was ordered with no discount, a slip in the VAT at 20 %), a quantity above zero, a
    /// price not negative, a discount below 100 % and a VAT within 0–100 %.
    /// </summary>
    public static (FieldRefusal? Refusal, AddPurchaseOrderLineRequest? Request) CheckLine(PurchaseOrderLineForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.StockItemId is not { } itemId) return (new FieldRefusal(ItemField, "Po_ItemRequired"), null);

        foreach (var (field, typed) in new[]
                 {
                     (QuantityField, form.Quantity), (PriceField, form.UnitPrice), (DiscountField, form.DiscountPercent), (VatField, form.VatPercent)
                 })
        {
            if (TypedNumber.IsUnreadable(typed)) return (new FieldRefusal(field, "Result_NotANumber", typed!.Trim()), null);
        }

        if (TypedNumber.Parse(form.Quantity) is not { } quantity || quantity <= 0m)
            return (new FieldRefusal(QuantityField, "Po_QuantityMustBePositive"), null);
        if (TypedNumber.Parse(form.UnitPrice) is not { } price || price < 0m)
            return (new FieldRefusal(PriceField, "Po_PriceMustNotBeNegative"), null);

        var discount = TypedNumber.Parse(form.DiscountPercent) ?? 0m;
        var vat = TypedNumber.Parse(form.VatPercent) ?? 20m;
        if (discount is < 0m or >= 100m) return (new FieldRefusal(DiscountField, "Po_RateOutOfRange"), null);
        if (vat is < 0m or > 100m) return (new FieldRefusal(VatField, "Po_RateOutOfRange"), null);

        return (null, new AddPurchaseOrderLineRequest
        {
            StockItemId = itemId,
            Quantity = quantity,
            UnitPriceMad = price,
            DiscountRate = discount / 100m,
            VatRate = vat / 100m
        });
    }

    // ---- Sending ------------------------------------------------------------------------------------

    /// <summary>By e-mail when the laboratory has a mail account and the supplier an address; by hand otherwise.</summary>
    public static PurchaseOrderSendRoute SendRoute(bool canEmail, string? supplierEmail) =>
        canEmail && !string.IsNullOrWhiteSpace(supplierEmail) ? PurchaseOrderSendRoute.Email : PurchaseOrderSendRoute.Manual;

    /// <summary>Why the order goes by hand: no mail account, or no address for the supplier.</summary>
    public static string ManualReason(bool canEmail, string supplierName, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return canEmail ? text("Po_SupplierEmailMissing", supplierName) : text("Purchasing_EmailNotConfigured");
    }

    /// <summary>The question of a manual send: why, then whether the order did go.</summary>
    public static string ManualQuestion(string reason, PurchaseOrderRow order, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(text);
        return $"{reason}\n\n{text("PurchaseOrders.ManualSendQuestion", order.Number, order.SupplierName)}";
    }

    // ---- A delivery ---------------------------------------------------------------------------------

    /// <summary>A delivery's form: what is still awaited on the line, expiring in a year, the checklist ticked.</summary>
    public static ReceiveLotForm NewReceipt(PurchaseOrderLineRow line, DateOnly today, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new ReceiveLotForm
        {
            Quantity = TypedNumber.ForEditing(line.QuantityOutstanding, 0, culture ?? CultureInfo.InvariantCulture),
            ExpiryDate = today.AddMonths(12)
        };
    }

    /// <summary>
    /// A delivery as the receipt dialog checks it: a quantity above zero, the lot number, an expiry
    /// not past, then the checks at the door (InventoryScreenRules.CheckArrival) — a refusal on the
    /// temperature, or the check with its ambient warning and its non-conformity to confirm.
    /// </summary>
    public static PurchaseOrderReceiptVerdict CheckReceipt(ReceiveLotForm form, PurchaseOrderLineRow line, StorageCondition condition,
                                                           DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(line);

        if (TypedNumber.IsUnreadable(form.Quantity))
            return Refused(new FieldRefusal(InventoryScreenRules.QuantityField, "Result_NotANumber", form.Quantity!.Trim()));
        if (TypedNumber.Parse(form.Quantity) is not { } quantity || quantity <= 0m)
            return Refused(new FieldRefusal(InventoryScreenRules.QuantityField, "Po_QuantityMustBePositive"));
        if (string.IsNullOrWhiteSpace(form.LotNumber))
            return Refused(new FieldRefusal(InventoryScreenRules.LotNumberField, "Lot_NumberRequired"));
        if (form.ExpiryDate < today)
            return Refused(new FieldRefusal(InventoryScreenRules.ExpiryField, "Lot_AlreadyExpired",
                                            form.ExpiryDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));

        var arrival = InventoryScreenRules.CheckArrival(form, condition);
        if (arrival.Refusal is not null) return Refused(arrival.Refusal);

        return new PurchaseOrderReceiptVerdict(null, new ReceivePurchaseOrderLineRequest
        {
            Reception = arrival.Check,
            LineId = line.Id,
            Quantity = quantity,
            LotNumber = form.LotNumber.Trim(),
            ExpiryDate = form.ExpiryDate,
            DeliveryNoteReference = Blank(form.DeliveryNote),
            StorageLocation = Blank(form.StorageLocation)
        }, arrival.AmbientWarning, arrival.NeedsNonConformity);
    }

    /// <summary>A delivery refused at the door: nothing received on the order, the refusal and its non-conformity kept.</summary>
    public static RefuseDeliveryRequest Refusal(ReceiveLotForm form, PurchaseOrderRow order, PurchaseOrderLineRow line, string reason)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(line);
        return new RefuseDeliveryRequest
        {
            PurchaseOrderId = order.Id,
            StockItemId = line.StockItemId,
            Reason = reason,
            ArrivalTemperatureCelsius = TypedNumber.Parse(form.ArrivalTemperature),
            DeliveryNoteReference = Blank(form.DeliveryNote),
            LotNumber = Blank(form.LotNumber)
        };
    }

    /// <summary>A scanned box that is another article than the line's: said, the receipt not switched.</summary>
    public static string? ScanMismatch(GtinMatch? match, PurchaseOrderLineRow line, string gtin, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);
        if (match is null) return text("Reception.GtinUnknown", gtin);
        return match.StockItemId != line.StockItemId ? text("Reception.GtinNotOnLine", match.Code, line.ItemCode) : null;
    }

    private static PurchaseOrderReceiptVerdict Refused(FieldRefusal refusal) => new(refusal, null, false, false);

    /// <summary>The box a refusal of the services names.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "Po_SupplierNotApproved" => SupplierField,
        "Po_ExpectedDateInPast" => ExpectedField,
        "Po_ItemAlreadyOnOrder" or "Stock_ItemNotFound" => ItemField,
        "Po_PriceMustNotBeNegative" => PriceField,
        "Po_RateOutOfRange" => DiscountField,
        "Po_ReceiptExceedsOrder" => InventoryScreenRules.QuantityField,
        _ => InventoryScreenRules.FieldOf(error)
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
