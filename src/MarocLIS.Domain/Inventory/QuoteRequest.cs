using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// A request for quotation (demande de devis) for an off-contract purchase: the articles and
/// quantities wanted, sent to one or more suppliers, whose answers are compared line by line before
/// one offer is chosen and turned into a draft purchase order at its prices.
/// <para>
/// A captive reagent of an active placement contract is not put out to quotation: its price was
/// fixed by the contract, and buying it elsewhere would break the commitment the contract rests on.
/// That rule is checked by the service, which can see the contracts; the aggregate keeps its own
/// invariants — one answer per supplier, answers only from suppliers that were asked, and nothing
/// changes once an offer has been chosen.
/// </para>
/// </summary>
public sealed class QuoteRequest : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<QuoteRequestItem> _items = [];
    private readonly List<QuoteRequestSupplier> _suppliers = [];
    private readonly List<SupplierQuote> _offers = [];

    private QuoteRequest() { }

    public QuoteRequest(string number, Guid siteId, DateOnly requestedOn, Guid raisedByUserId)
    {
        Number = number;
        SiteId = siteId;
        RequestedOn = requestedOn;
        RaisedByUserId = raisedByUserId;
        Status = QuoteRequestStatus.Open;
    }

    /// <summary>Sequential number, e.g. "DP/2026/00004".</summary>
    public string Number { get; private set; } = string.Empty;

    public Guid SiteId { get; set; }

    public DateOnly RequestedOn { get; private set; }
    public Guid RaisedByUserId { get; private set; }

    /// <summary>What the purchase is for, in a few words.</summary>
    public string? Subject { get; set; }

    /// <summary>Date by which the suppliers are asked to answer.</summary>
    public DateOnly? ResponseDueDate { get; set; }

    public string? Notes { get; set; }

    public QuoteRequestStatus Status { get; private set; }

    /// <summary>The chosen offer, once the request is awarded.</summary>
    public Guid? AwardedQuoteId { get; private set; }
    public DateOnly? AwardedOn { get; private set; }
    public Guid? AwardedByUserId { get; private set; }

    /// <summary>The draft purchase order created from the chosen offer.</summary>
    public Guid? PurchaseOrderId { get; private set; }

    public string? CancellationReason { get; private set; }

    public IReadOnlyCollection<QuoteRequestItem> Items => _items.AsReadOnly();
    public IReadOnlyCollection<QuoteRequestSupplier> Suppliers => _suppliers.AsReadOnly();
    public IReadOnlyCollection<SupplierQuote> Offers => _offers.AsReadOnly();

    public bool IsOpen => Status == QuoteRequestStatus.Open;

    public Result AddItem(Guid stockItemId, decimal quantity, string? description)
    {
        if (!IsOpen) return Result.Failure(Error.Conflict("Quote_NotOpen"));
        if (quantity <= 0m) return Result.Failure(Error.Validation("Quote_QuantityMustBePositive"));
        if (_items.Any(i => i.StockItemId == stockItemId))
            return Result.Failure(Error.Conflict("Quote_ItemAlreadyRequested"));

        var item = new QuoteRequestItem(stockItemId, quantity) { Description = description };
        item.AttachTo(Id);
        item.LineNumber = _items.Count + 1;
        _items.Add(item);
        return Result.Success();
    }

    public Result AddSupplier(Guid supplierId)
    {
        if (!IsOpen) return Result.Failure(Error.Conflict("Quote_NotOpen"));
        if (_suppliers.Any(s => s.SupplierId == supplierId))
            return Result.Failure(Error.Conflict("Quote_SupplierAlreadyAsked"));

        var supplier = new QuoteRequestSupplier(supplierId);
        supplier.AttachTo(Id);
        _suppliers.Add(supplier);
        return Result.Success();
    }

    /// <summary>Notes that the request went out to a supplier, by e-mail or by hand.</summary>
    public void MarkSentTo(Guid supplierId, DateTimeOffset at)
    {
        var supplier = _suppliers.FirstOrDefault(s => s.SupplierId == supplierId);
        if (supplier is not null) supplier.SentAt ??= at;
    }

    /// <summary>
    /// Records a supplier's answer. A second answer from the same supplier replaces the first: a
    /// revised offer cancels the previous one, and comparing both would count the supplier twice.
    /// </summary>
    public Result<SupplierQuote> RecordOffer(Guid supplierId, DateOnly receivedOn,
                                             IReadOnlyList<SupplierQuoteLineInput> lines,
                                             DateOnly? validUntil = null, int? deliveryDelayDays = null,
                                             string? conditions = null, string? supplierReference = null)
    {
        if (!IsOpen) return Result.Failure<SupplierQuote>(Error.Conflict("Quote_NotOpen"));
        if (_suppliers.All(s => s.SupplierId != supplierId))
            return Result.Failure<SupplierQuote>(Error.Validation("Quote_SupplierNotAsked"));
        if (lines.Count == 0)
            return Result.Failure<SupplierQuote>(Error.Validation("Quote_OfferHasNoPrice"));
        if (deliveryDelayDays is < 0)
            return Result.Failure<SupplierQuote>(Error.Validation("Quote_DelayMustNotBeNegative"));
        if (validUntil is { } until && until < receivedOn)
            return Result.Failure<SupplierQuote>(Error.Validation("Quote_ValidityBeforeReceipt"));

        foreach (var line in lines)
        {
            if (_items.All(i => i.Id != line.QuoteRequestItemId))
                return Result.Failure<SupplierQuote>(Error.NotFound("Quote_ItemNotFound"));
            if (line.UnitPriceMad < 0m)
                return Result.Failure<SupplierQuote>(Error.Validation("Po_PriceMustNotBeNegative"));
            if (line.DiscountRate is < 0m or >= 1m || line.VatRate is < 0m or > 1m)
                return Result.Failure<SupplierQuote>(Error.Validation("Po_RateOutOfRange"));
        }

        if (lines.GroupBy(l => l.QuoteRequestItemId).Any(g => g.Count() > 1))
            return Result.Failure<SupplierQuote>(Error.Validation("Quote_ItemPricedTwice"));

        _offers.RemoveAll(o => o.SupplierId == supplierId);

        var offer = new SupplierQuote(supplierId, receivedOn)
        {
            ValidUntil = validUntil,
            DeliveryDelayDays = deliveryDelayDays,
            Conditions = conditions,
            SupplierReference = supplierReference
        };
        offer.AttachTo(Id);

        foreach (var line in lines)
            offer.AddLine(new SupplierQuoteLine(line.QuoteRequestItemId, line.UnitPriceMad)
            {
                VatRate = line.VatRate,
                DiscountRate = line.DiscountRate
            });

        _offers.Add(offer);
        return Result.Success(offer);
    }

    /// <summary>
    /// Chooses the winning offer. An expired offer cannot be chosen: its prices are no longer the
    /// supplier's, and the purchase order built from them would be disputed on the invoice.
    /// </summary>
    public Result<SupplierQuote> Award(Guid supplierQuoteId, DateOnly today, Guid byUserId)
    {
        if (!IsOpen) return Result.Failure<SupplierQuote>(Error.Conflict("Quote_NotOpen"));

        var offer = _offers.FirstOrDefault(o => o.Id == supplierQuoteId);
        if (offer is null) return Result.Failure<SupplierQuote>(Error.NotFound("Quote_OfferNotFound"));
        if (offer.IsExpired(today))
            return Result.Failure<SupplierQuote>(Error.Validation("Quote_OfferExpired", offer.ValidUntil));

        AwardedQuoteId = offer.Id;
        AwardedOn = today;
        AwardedByUserId = byUserId;
        Status = QuoteRequestStatus.Awarded;
        return Result.Success(offer);
    }

    /// <summary>Links the draft purchase order raised from the chosen offer.</summary>
    public void LinkPurchaseOrder(Guid purchaseOrderId) => PurchaseOrderId = purchaseOrderId;

    public Result Cancel(string reason)
    {
        if (!IsOpen) return Result.Failure(Error.Conflict("Quote_NotOpen"));
        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure(Error.Validation("Po_ReasonRequired"));

        Status = QuoteRequestStatus.Cancelled;
        CancellationReason = reason.Trim();
        return Result.Success();
    }
}

public enum QuoteRequestStatus
{
    /// <summary>Sent or being sent; answers are being collected.</summary>
    Open = 0,
    /// <summary>An offer was chosen and turned into a purchase order.</summary>
    Awarded = 1,
    Cancelled = 9
}

/// <summary>One article and the quantity wanted.</summary>
public sealed class QuoteRequestItem : Entity
{
    private QuoteRequestItem() { }

    internal QuoteRequestItem(Guid stockItemId, decimal quantity)
    {
        StockItemId = stockItemId;
        Quantity = quantity;
    }

    public Guid QuoteRequestId { get; private set; }
    internal void AttachTo(Guid quoteRequestId) => QuoteRequestId = quoteRequestId;

    public int LineNumber { get; set; }
    public Guid StockItemId { get; private set; }
    public decimal Quantity { get; private set; }

    /// <summary>Designation as requested, in case the catalogue entry is later renamed.</summary>
    public string? Description { get; set; }
}

/// <summary>A supplier asked to quote.</summary>
public sealed class QuoteRequestSupplier : Entity
{
    private QuoteRequestSupplier() { }

    internal QuoteRequestSupplier(Guid supplierId) => SupplierId = supplierId;

    public Guid QuoteRequestId { get; private set; }
    internal void AttachTo(Guid quoteRequestId) => QuoteRequestId = quoteRequestId;

    public Guid SupplierId { get; private set; }

    /// <summary>When the request went out to this supplier; empty until it has.</summary>
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A supplier's answer (devis reçu).</summary>
public sealed class SupplierQuote : Entity
{
    private readonly List<SupplierQuoteLine> _lines = [];

    private SupplierQuote() { }

    internal SupplierQuote(Guid supplierId, DateOnly receivedOn)
    {
        SupplierId = supplierId;
        ReceivedOn = receivedOn;
    }

    public Guid QuoteRequestId { get; private set; }
    internal void AttachTo(Guid quoteRequestId) => QuoteRequestId = quoteRequestId;

    public Guid SupplierId { get; private set; }
    public DateOnly ReceivedOn { get; private set; }

    /// <summary>The supplier's own quotation number.</summary>
    public string? SupplierReference { get; set; }

    /// <summary>Last day the prices hold.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>Delivery delay promised, in days from the order.</summary>
    public int? DeliveryDelayDays { get; set; }

    /// <summary>Payment terms, minimum order, carriage and the like, as stated by the supplier.</summary>
    public string? Conditions { get; set; }

    public IReadOnlyCollection<SupplierQuoteLine> Lines => _lines.AsReadOnly();

    public bool IsExpired(DateOnly today) => ValidUntil is { } until && until < today;

    internal void AddLine(SupplierQuoteLine line)
    {
        line.AttachTo(Id);
        _lines.Add(line);
    }
}

/// <summary>The price a supplier quoted for one requested article.</summary>
public sealed class SupplierQuoteLine : Entity
{
    private SupplierQuoteLine() { }

    internal SupplierQuoteLine(Guid quoteRequestItemId, decimal unitPriceMad)
    {
        QuoteRequestItemId = quoteRequestItemId;
        UnitPriceMad = Money.Round(unitPriceMad);
    }

    public Guid SupplierQuoteId { get; private set; }
    internal void AttachTo(Guid supplierQuoteId) => SupplierQuoteId = supplierQuoteId;

    public Guid QuoteRequestItemId { get; private set; }

    /// <summary>List price per unit, excluding VAT.</summary>
    public decimal UnitPriceMad { get; private set; }

    /// <summary>Discount as a fraction (0.05 = 5 %).</summary>
    public decimal DiscountRate { get; set; }

    public decimal VatRate { get; set; } = 0.20m;

    /// <summary>What one unit costs once the discount is taken off: what offers are compared on.</summary>
    public decimal NetUnitPriceMad => Money.Round(UnitPriceMad * (1m - DiscountRate));
}

/// <summary>A price entered for one requested article.</summary>
public sealed record SupplierQuoteLineInput(Guid QuoteRequestItemId, decimal UnitPriceMad,
                                            decimal VatRate = 0.20m, decimal DiscountRate = 0m);

/// <summary>
/// The comparison grid: for each requested article, every supplier's price and delay, with the
/// cheapest and the fastest marked. Expired offers are shown but never marked, since they cannot be
/// chosen.
/// </summary>
public static class QuoteComparison
{
    public static IReadOnlyList<QuoteComparisonLine> Build(QuoteRequest request, DateOnly today)
    {
        var lines = new List<QuoteComparisonLine>();

        foreach (var item in request.Items.OrderBy(i => i.LineNumber))
        {
            var cells = request.Offers
                .SelectMany(o => o.Lines.Where(l => l.QuoteRequestItemId == item.Id)
                                        .Select(l => (Offer: o, Line: l)))
                .Select(x => new QuoteComparisonCell(
                    x.Offer.Id, x.Offer.SupplierId, x.Line.UnitPriceMad, x.Line.DiscountRate,
                    x.Line.NetUnitPriceMad, x.Line.VatRate,
                    Money.Round(x.Line.NetUnitPriceMad * item.Quantity),
                    x.Offer.DeliveryDelayDays, x.Offer.IsExpired(today), false, false))
                .ToList();

            var valid = cells.Where(c => !c.IsExpired).ToList();

            if (valid.Count > 0)
            {
                var cheapest = valid.Min(c => c.NetUnitPriceMad);
                var delays = valid.Where(c => c.DeliveryDelayDays is not null).ToList();
                int? fastest = delays.Count > 0 ? delays.Min(c => c.DeliveryDelayDays) : null;

                cells = cells.Select(c => c.IsExpired ? c : c with
                {
                    IsCheapest = c.NetUnitPriceMad == cheapest,
                    // Only meaningful when there is a choice: a single offer is not "the fastest".
                    IsFastest = fastest is not null && valid.Count > 1 && c.DeliveryDelayDays == fastest
                }).ToList();

                if (valid.Count == 1)
                    cells = cells.Select(c => c with { IsCheapest = false }).ToList();
            }

            lines.Add(new QuoteComparisonLine(item.Id, item.LineNumber, item.StockItemId, item.Quantity, cells));
        }

        return lines;
    }

    /// <summary>
    /// Total excluding VAT of an offer over the articles it priced, and how many of the requested
    /// articles it left out — an offer that is cheaper because it skipped a line is not cheaper.
    /// </summary>
    public static (decimal TotalExcludingVat, int MissingItems) Totals(QuoteRequest request, SupplierQuote offer)
    {
        var total = 0m;
        var missing = 0;

        foreach (var item in request.Items)
        {
            var line = offer.Lines.FirstOrDefault(l => l.QuoteRequestItemId == item.Id);
            if (line is null) missing++;
            else total += Money.Round(line.NetUnitPriceMad * item.Quantity);
        }

        return (Money.Round(total), missing);
    }
}

public sealed record QuoteComparisonLine(
    Guid QuoteRequestItemId,
    int LineNumber,
    Guid StockItemId,
    decimal Quantity,
    IReadOnlyList<QuoteComparisonCell> Cells);

public sealed record QuoteComparisonCell(
    Guid SupplierQuoteId,
    Guid SupplierId,
    decimal UnitPriceMad,
    decimal DiscountRate,
    decimal NetUnitPriceMad,
    decimal VatRate,
    decimal LineTotalExcludingVat,
    int? DeliveryDelayDays,
    bool IsExpired,
    bool IsCheapest,
    bool IsFastest);
