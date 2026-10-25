using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// What « Demandes de devis » offers, by right and by the state of the request, of the supplier
/// selected and of its offer — the workstation's PermissionGate calls and RefreshButtons.
/// QuoteService stays the judge.
/// </summary>
/// <param name="New">A new request (managing quotes).</param>
/// <param name="SendByEmail">The request e-mailed to the suppliers not sent it yet (managing quotes).</param>
/// <param name="MarkSent">The request marked sent to the supplier selected, sent by hand (managing quotes).</param>
/// <param name="AddSupplier">One more supplier asked (managing quotes).</param>
/// <param name="RecordOffer">The supplier's answer typed, or retyped (managing quotes).</param>
/// <param name="OpenFile">The quotation file the supplier sent, opened.</param>
/// <param name="Award">The offer selected chosen, which drafts a purchase order (managing quotes and orders).</param>
/// <param name="Cancel">The request cancelled, with its reason (managing quotes).</param>
public sealed record QuoteScreenActions(
    bool New, bool SendByEmail, bool MarkSent, bool AddSupplier, bool RecordOffer, bool OpenFile, bool Award, bool Cancel);

/// <summary>One article of a new request: ticked or not, and how many.</summary>
public sealed class QuoteItemForm
{
    public required QuotableItem Item { get; init; }
    public bool Included { get; set; }
    public string? Quantity { get; set; }

    /// <summary>On contract: bought at the contracted price from the contract's supplier, never quoted.</summary>
    public bool OnContract => Item.ContractReference is not null;
}

/// <summary>A new request as typed: the workstation's QuoteRequestDialog.</summary>
public sealed class QuoteRequestForm
{
    public string? Subject { get; set; }
    public bool HasResponseDue { get; set; } = true;
    public DateOnly ResponseDue { get; set; }
    public string? Notes { get; set; }
    public List<QuoteItemForm> Items { get; init; } = [];
    public HashSet<Guid> SupplierIds { get; init; } = [];
}

/// <summary>One article of an offer: a blank price is an article the supplier did not quote.</summary>
public sealed class QuoteOfferLineForm
{
    public required QuoteItemRow Item { get; init; }
    public string? UnitPrice { get; set; }
    public string? DiscountPercent { get; set; } = "0";
    public string? VatPercent { get; set; } = "20";
}

/// <summary>A supplier's answer as typed: the workstation's QuoteOfferDialog.</summary>
public sealed class QuoteOfferForm
{
    public DateOnly ReceivedOn { get; set; }
    public bool HasValidUntil { get; set; } = true;
    public DateOnly ValidUntil { get; set; }
    public string? DelayDays { get; set; }
    public string? SupplierReference { get; set; }
    public string? Conditions { get; set; }
    public List<QuoteOfferLineForm> Lines { get; init; } = [];
    public string? FileName { get; set; }
    public byte[]? FileContent { get; set; }
}

/// <summary>How a cell of the comparison reads: the cheapest in the normal ink on its fill, the fastest in bold, an expired one muted.</summary>
public sealed record QuoteCellLook(string Text, bool Cheapest, bool Fastest, bool Expired);

/// <summary>
/// The rules of « Demandes de devis » that need no window: what is offered by which right and in
/// which state, how a request and its comparison read (the offers side by side, the cheapest and
/// fastest marked per line, an expired one muted and never chosen, the offer chosen set apart), how
/// a request and an answer are typed and refused box by box, and what the award and the e-mailing
/// say. The workstation's QuoteRequestView and its dialogs and the web's screen apply the same ones.
/// </summary>
public static class QuoteScreenRules
{
    public const int SubjectMaxLength = 200;
    public const int NotesMaxLength = 1000;
    public const int ReferenceMaxLength = 60;
    public const int ConditionsMaxLength = 1000;
    public const int DelayMaxLength = 4;
    public const int CancelReasonMaxLength = 500;
    public const int QuantityMaxLength = TypedNumber.MeasureMaxLength;
    public const int PriceMaxLength = TypedNumber.AmountMaxLength;
    public const int RateMaxLength = TypedNumber.MeasureMaxLength;

    /// <summary>A response wanted within a week; an offer valid for a month.</summary>
    public const int DefaultResponseDays = 7;

    public const string SubjectField = "quote-subject";
    public const string DueField = "quote-due";
    public const string ItemsField = "quote-items";
    public const string SuppliersField = "quote-suppliers";
    public const string ReceivedField = "offer-received";
    public const string ValidField = "offer-valid";
    public const string DelayField = "offer-delay";
    public const string LinesField = "offer-lines";
    public const string FileField = "offer-file";

    /// <summary>The kinds of file a supplier's quotation is kept as: a PDF, a scan, a Word or Excel file.</summary>
    public static readonly IReadOnlyList<string> FileExtensions = [".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".docx", ".xlsx"];

    public static string FileAccept => string.Join(",", FileExtensions);

    /// <summary>A quantity box of the request: « quote-qty-3 » for the third article.</summary>
    public static string QuantityField(int index) => $"quote-qty-{index.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A figure of the offer: « offer-price-2 », « offer-discount-2 », « offer-vat-2 ».</summary>
    public static string OfferField(string column, int index) => $"offer-{column}-{index.ToString(CultureInfo.InvariantCulture)}";

    // ---- Rights and states ------------------------------------------------------------------------

    public static QuoteScreenActions Actions(ICurrentUser user, QuoteRequestDetail? detail, QuoteSupplierRow? supplier)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Actions(user.Has, detail, supplier);
    }

    /// <summary>
    /// An open request takes suppliers, sends and answers; its offer is chosen while it is open — or
    /// again once the purchase order it drafted was cancelled —, never an expired one. Choosing drafts
    /// a purchase order: both rights are needed.
    /// </summary>
    public static QuoteScreenActions Actions(Func<string, bool> has, QuoteRequestDetail? detail, QuoteSupplierRow? supplier)
    {
        ArgumentNullException.ThrowIfNull(has);
        var manage = has(Permissions.Inventory.ManageQuotes);
        var open = detail is { Status: QuoteRequestStatus.Open };
        var offer = OfferOf(detail, supplier);
        var reawardable = detail is { Status: QuoteRequestStatus.Awarded, OrderCancelled: true };

        return new QuoteScreenActions(
            New: manage,
            SendByEmail: manage && open && detail!.Suppliers.Any(s => s.SentAt is null),
            MarkSent: manage && open && supplier is { SentAt: null },
            AddSupplier: manage && open,
            RecordOffer: manage && open && supplier is not null,
            OpenFile: offer?.FileName is not null,
            Award: manage && has(Permissions.Inventory.ManagePurchaseOrders) && (open || reawardable) && offer is { IsExpired: false },
            Cancel: manage && open);
    }

    /// <summary>The offer of the supplier selected, if it answered.</summary>
    public static QuoteOfferRow? OfferOf(QuoteRequestDetail? detail, QuoteSupplierRow? supplier) =>
        supplier is null ? null : detail?.Offers.FirstOrDefault(o => o.SupplierId == supplier.SupplierId);

    public static QuoteRequestRow? Keep(IReadOnlyList<QuoteRequestRow> rows, Guid? kept)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => r.Id == kept) ?? (rows.Count > 0 ? rows[0] : null);
    }

    /// <summary>The supplier kept after a reload: the one worked on, else the one chosen, else the first.</summary>
    public static QuoteSupplierRow? KeepSupplier(QuoteRequestDetail? detail, Guid? kept)
    {
        if (detail is null) return null;
        var awarded = detail.Offers.FirstOrDefault(o => o.Id == detail.AwardedQuoteId)?.SupplierId;
        return detail.Suppliers.FirstOrDefault(s => s.SupplierId == kept)
               ?? detail.Suppliers.FirstOrDefault(s => s.SupplierId == awarded)
               ?? (detail.Suppliers.Count > 0 ? detail.Suppliers[0] : null);
    }

    // ---- How a request reads -----------------------------------------------------------------------

    public static StockTone StatusTone(QuoteRequestStatus status) => status switch
    {
        QuoteRequestStatus.Open => StockTone.Running,
        QuoteRequestStatus.Awarded => StockTone.Done,
        _ => StockTone.Waiting
    };

    public static string StatusKey(QuoteRequestStatus status) => $"QuoteRequestStatus.{status}";

    /// <summary>A request closed — chosen or cancelled — is muted.</summary>
    public static StockInk RowInk(QuoteRequestRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Status == QuoteRequestStatus.Open ? StockInk.Plain : StockInk.Muted;
    }

    public static string Date(DateOnly date) => date.ToString("dd/MM/yy", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? date) => date is { } d ? Date(d) : string.Empty;

    /// <summary>« 2 / 3 »: the answers against the suppliers asked.</summary>
    public static string Answers(QuoteRequestRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{row.OfferCount.ToString(CultureInfo.InvariantCulture)} / {row.SupplierCount.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>« 6 demandes · en cours 2 ».</summary>
    public static string Count(IReadOnlyCollection<QuoteRequestRow> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        var open = rows.Count(r => r.Status == QuoteRequestStatus.Open);
        return text("StocksUi.Quotes", rows.Count) + (open > 0 ? $" · {text("Quotes.OpenCount")} {open.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
    }

    /// <summary>« DD/2026/0004 · Réactifs de biochimie ».</summary>
    public static string DetailNote(QuoteRequestDetail? detail) =>
        detail is null ? string.Empty : detail.Number + (detail.Subject is { Length: > 0 } subject ? $" · {subject}" : string.Empty);

    /// <summary>The column of an offer: the supplier, « (expirée) » when it can no longer be chosen.</summary>
    public static string OfferHeader(QuoteOfferRow offer, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(text);
        return offer.IsExpired ? $"{offer.SupplierName} ({text("Quotes.Expired")})" : offer.SupplierName;
    }

    /// <summary>« BIO-GLU — Glucose 4×100 mL », or « ? » for an article no longer found.</summary>
    public static string ItemLabel(QuoteRequestDetail detail, QuoteComparisonLine line)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(line);
        return detail.Items.FirstOrDefault(i => i.Id == line.QuoteRequestItemId) is { } item ? $"{item.Code} — {item.Designation}" : "?";
    }

    /// <summary>« 12 coffret ».</summary>
    public static string LineQuantity(QuoteRequestDetail detail, QuoteComparisonLine line, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(line);
        var unit = detail.Items.FirstOrDefault(i => i.Id == line.QuoteRequestItemId)?.Unit;
        return $"{line.Quantity.ToString("0.##", provider ?? CultureInfo.CurrentCulture)} {unit}".TrimEnd();
    }

    /// <summary>
    /// One cell of the comparison: the net unit price and the delay, « — » for an article the offer
    /// does not quote. Cheapest and fastest marked, never on an expired offer.
    /// </summary>
    /// <param name="amount">How the screen writes an amount.</param>
    public static QuoteCellLook Cell(QuoteComparisonLine line, QuoteOfferRow offer, ScreenText text, Func<decimal, string> amount)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(amount);
        var cell = line.Cells.FirstOrDefault(c => c.SupplierQuoteId == offer.Id);
        if (cell is null) return new QuoteCellLook("—", false, false, offer.IsExpired);

        var price = amount(cell.NetUnitPriceMad);
        var shown = cell.DeliveryDelayDays is { } days
            ? $"{price} · {days.ToString(CultureInfo.InvariantCulture)} {text("Quotes.DaysShort")}"
            : price;
        return cell.IsExpired
            ? new QuoteCellLook(shown, false, false, true)
            : new QuoteCellLook(shown, cell.IsCheapest, cell.IsFastest, false);
    }

    /// <summary>The supplier asked, « (non approuvé) » when no order can go to it.</summary>
    public static string SupplierName(QuoteSupplierRow supplier, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(text);
        return supplier.IsApproved ? supplier.Name : $"{supplier.Name} ({text("Quotes.NotApproved")})";
    }

    /// <summary>The day the request went, or « non envoyée ».</summary>
    public static string Sent(QuoteSupplierRow supplier, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(text);
        return supplier.SentAt is { } sent ? sent.ToLocalTime().ToString("dd/MM/yy", CultureInfo.InvariantCulture) : text("Quotes.NotSent");
    }

    /// <summary>An expired offer or no answer muted; the offer chosen is set apart on its own (<see cref="IsAwarded"/>).</summary>
    public static StockInk SupplierInk(QuoteRequestDetail detail, QuoteSupplierRow supplier)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var offer = OfferOf(detail, supplier);
        if (offer is null) return StockInk.Muted;
        if (offer.Id == detail.AwardedQuoteId) return StockInk.Plain;
        return offer.IsExpired ? StockInk.Muted : StockInk.Plain;
    }

    /// <summary>Whether the supplier's offer is the one chosen: on the accent's fill, in bold.</summary>
    public static bool IsAwarded(QuoteRequestDetail detail, QuoteSupplierRow supplier) =>
        OfferOf(detail, supplier) is { } offer && offer.Id == detail.AwardedQuoteId;

    /// <summary>The suppliers not yet asked, to add one.</summary>
    public static IReadOnlyList<QuoteSupplierOption> Addable(IEnumerable<QuoteSupplierOption> suppliers, QuoteRequestDetail detail)
    {
        ArgumentNullException.ThrowIfNull(suppliers);
        ArgumentNullException.ThrowIfNull(detail);
        return [.. suppliers.Where(s => detail.Suppliers.All(a => a.SupplierId != s.Id))];
    }

    /// <summary>A supplier offered for a request: « (sans e-mail) » or « (non approuvé) » said after its name.</summary>
    public static string OptionLabel(QuoteSupplierOption supplier, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(text);
        return supplier.IsApproved
            ? string.IsNullOrWhiteSpace(supplier.Email) ? $"{supplier.Name} ({text("Quotes.NoEmail")})" : supplier.Name
            : $"{supplier.Name} ({text("Quotes.NotApproved")})";
    }

    /// <summary>The question of the award: the offer, its total, and what it leaves out.</summary>
    public static string AwardQuestion(QuoteOfferRow offer, string total, ScreenText text, string key = "Quotes.AwardQuestion")
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(text);
        var question = text(key, offer.SupplierName, total);
        if (offer.MissingItems > 0) question += "\n\n" + text("Quotes.AwardMissingItems", offer.MissingItems);
        return question;
    }

    /// <summary>How the e-mailing went: how many were sent, who has no address, who the server refused.</summary>
    public static string SendOutcome(QuoteSendOutcome outcome, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(text);
        var message = text("Quotes.SentCount", outcome.Sent);
        if (outcome.WithoutEmail.Count > 0) message += "\n\n" + text("Quotes.WithoutEmail", string.Join(", ", outcome.WithoutEmail));
        if (outcome.Failed.Count > 0) message += "\n\n" + text("Quotes.SendFailed", string.Join(", ", outcome.Failed));
        return message;
    }

    /// <summary>No mail account: the request goes by telephone or fax, then marked sent.</summary>
    public static string NoEmail(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"{text("Purchasing_EmailNotConfigured")}\n\n{text("Quotes.SendManually")}";
    }

    // ---- A new request ------------------------------------------------------------------------------

    /// <summary>A new request: every article listed unticked, its reorder quantity offered; a response wanted within a week.</summary>
    public static QuoteRequestForm NewRequest(IEnumerable<QuotableItem> items, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new QuoteRequestForm
        {
            ResponseDue = today.AddDays(DefaultResponseDays),
            Items = [.. items.Select(i => new QuoteItemForm
            {
                Item = i,
                Quantity = i.ReorderQuantity > 0m ? i.ReorderQuantity.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty
            })]
        };
    }

    public static string ItemLabel(QuotableItem item, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{item.Code} — {item.Name.For(language)} ({item.UnitOfMeasure})";
    }

    /// <summary>
    /// A request as the dialog checks it: each article ticked with a quantity above zero (never one
    /// on contract), at least one article, at least one supplier, a response date not past.
    /// </summary>
    public static (FieldRefusal? Refusal, CreateQuoteRequest? Request) CheckRequest(QuoteRequestForm form, IReadOnlyList<Guid> supplierOrder,
                                                                                   DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(supplierOrder);

        if (form.HasResponseDue && form.ResponseDue < today) return (new FieldRefusal(DueField, "Quote_DueDateInPast"), null);

        var chosen = new List<QuoteItemInput>();
        for (var i = 0; i < form.Items.Count; i++)
        {
            var line = form.Items[i];
            if (line.OnContract || !line.Included) continue;
            if (TypedNumber.Parse(line.Quantity) is not { } quantity || quantity <= 0m)
                return (new FieldRefusal(QuantityField(i), "Quote_QuantityMustBePositive"), null);
            chosen.Add(new QuoteItemInput(line.Item.Id, quantity));
        }

        if (chosen.Count == 0) return (new FieldRefusal(ItemsField, "Quote_NoItems"), null);

        var suppliers = supplierOrder.Where(form.SupplierIds.Contains).ToList();
        if (suppliers.Count == 0) return (new FieldRefusal(SuppliersField, "Quote_NoSuppliers"), null);

        return (null, new CreateQuoteRequest
        {
            Subject = Blank(form.Subject),
            ResponseDueDate = form.HasResponseDue ? form.ResponseDue : null,
            Notes = Blank(form.Notes),
            Items = chosen,
            SupplierIds = suppliers
        });
    }

    // ---- An answer -----------------------------------------------------------------------------------

    /// <summary>An answer received today, valid for a month, every article at 0 % discount and 20 % VAT, unpriced.</summary>
    public static QuoteOfferForm NewOffer(QuoteRequestDetail detail, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new QuoteOfferForm
        {
            ReceivedOn = today,
            ValidUntil = today.AddMonths(1),
            Lines = [.. detail.Items.Select(i => new QuoteOfferLineForm { Item = i })]
        };
    }

    /// <summary>« BIO-GLU — Glucose » and « 12 coffret ».</summary>
    public static (string Item, string Quantity) OfferLineLabels(QuoteItemRow item, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ($"{item.Code} — {item.Designation}", $"{item.Quantity.ToString("0.##", provider ?? CultureInfo.CurrentCulture)} {item.Unit}".TrimEnd());
    }

    /// <summary>
    /// An answer as the dialog checks it: a received date not in the future and a validity not
    /// before it; each figure typed reads as one (« 10 % » in the discount read as no discount, and
    /// the offer was compared on a price never quoted); a price not negative, a discount below
    /// 100 %, a VAT within 0–100 %; at least one article priced; a delay a whole number of days.
    /// </summary>
    public static (FieldRefusal? Refusal, RecordQuoteOfferRequest? Request) CheckOffer(QuoteOfferForm form, Guid supplierId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.ReceivedOn > today) return (new FieldRefusal(ReceivedField, "Quote_ReceivedInFuture"), null);
        if (form.HasValidUntil && form.ValidUntil < form.ReceivedOn) return (new FieldRefusal(ValidField, "Quote_ValidityBeforeReceipt"), null);

        var lines = new List<SupplierQuoteLineInput>();
        for (var i = 0; i < form.Lines.Count; i++)
        {
            var line = form.Lines[i];
            if (string.IsNullOrWhiteSpace(line.UnitPrice)) continue;

            foreach (var (column, typed) in new[] { ("price", line.UnitPrice), ("discount", line.DiscountPercent), ("vat", line.VatPercent) })
            {
                if (TypedNumber.IsUnreadable(typed)) return (new FieldRefusal(OfferField(column, i), "Result_NotANumber", typed!.Trim()), null);
            }

            if (TypedNumber.Parse(line.UnitPrice) is not { } price || price < 0m)
                return (new FieldRefusal(OfferField("price", i), "Po_PriceMustNotBeNegative"), null);

            var discount = TypedNumber.Parse(line.DiscountPercent) ?? 0m;
            var vat = TypedNumber.Parse(line.VatPercent) ?? 20m;
            if (discount is < 0m or >= 100m) return (new FieldRefusal(OfferField("discount", i), "Po_RateOutOfRange"), null);
            if (vat is < 0m or > 100m) return (new FieldRefusal(OfferField("vat", i), "Po_RateOutOfRange"), null);

            lines.Add(new SupplierQuoteLineInput(line.Item.Id, price, vat / 100m, discount / 100m));
        }

        if (lines.Count == 0) return (new FieldRefusal(LinesField, "Quote_OfferHasNoPrice"), null);

        int? delay = null;
        if (!string.IsNullOrWhiteSpace(form.DelayDays))
        {
            if (!int.TryParse(form.DelayDays.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days))
                return (new FieldRefusal(DelayField, "Quote_DelayMustNotBeNegative"), null);
            delay = days;
        }

        if (CheckFile(form.FileName, form.FileContent?.LongLength) is { } fileRefusal) return (fileRefusal, null);

        return (null, new RecordQuoteOfferRequest
        {
            SupplierId = supplierId,
            ReceivedOn = form.ReceivedOn,
            ValidUntil = form.HasValidUntil ? form.ValidUntil : null,
            DeliveryDelayDays = delay,
            SupplierReference = Blank(form.SupplierReference),
            Conditions = Blank(form.Conditions),
            Lines = lines,
            FileName = form.FileContent is { Length: > 0 } ? form.FileName : null,
            FileContent = form.FileContent is { Length: > 0 } ? form.FileContent : null
        });
    }

    /// <summary>A quotation file chosen: none, or one of the kinds kept, not empty and not past the size kept.</summary>
    public static FieldRefusal? CheckFile(string? fileName, long? length)
    {
        if (string.IsNullOrWhiteSpace(fileName) && length is null or 0) return null;
        if (length is null or <= 0) return new FieldRefusal(FileField, "Purchasing_FileEmpty");
        if (!FileExtensions.Contains(Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant()))
            return new FieldRefusal(FileField, "Purchasing_FileType");
        if (length > PurchasingDocument.MaximumBytes)
            return new FieldRefusal(FileField, "Purchasing_FileTooLarge",
                                    (PurchasingDocument.MaximumBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>The box a refusal of the service names.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "Quote_DueDateInPast" => DueField,
        "Quote_NoItems" or "Quote_ItemOnContract" or "Quote_ItemAlreadyRequested" => ItemsField,
        "Quote_NoSuppliers" => SuppliersField,
        "Quote_ReceivedInFuture" => ReceivedField,
        "Quote_ValidityBeforeReceipt" => ValidField,
        "Quote_DelayMustNotBeNegative" or "Quote_DelayOutOfRange" => DelayField,
        "Quote_OfferHasNoPrice" or "Quote_ItemPricedTwice" => LinesField,
        "Purchasing_FileTooLarge" or "Purchasing_FileType" or "Purchasing_FileEmpty" => FileField,
        _ => null
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
