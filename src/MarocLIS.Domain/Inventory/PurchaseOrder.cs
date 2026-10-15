using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// A purchase order (bon de commande) to a supplier. Receipts are recorded per line so a partial
/// delivery — the norm for reagents — is visible rather than being approximated.
/// </summary>
public sealed class PurchaseOrder : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<PurchaseOrderLine> _lines = [];

    private PurchaseOrder() { }

    public PurchaseOrder(string number, Guid supplierId, Guid siteId, DateOnly orderDate,
                         Guid raisedByUserId)
    {
        Number = number;
        SupplierId = supplierId;
        SiteId = siteId;
        OrderDate = orderDate;
        RaisedByUserId = raisedByUserId;
        Status = PurchaseOrderStatus.Draft;
    }

    /// <summary>Sequential order number, e.g. "BC/2026/0117".</summary>
    public string Number { get; private set; } = string.Empty;

    public Guid SupplierId { get; private set; }
    public Guid SiteId { get; set; }

    public DateOnly OrderDate { get; private set; }
    public Guid RaisedByUserId { get; private set; }

    public PurchaseOrderStatus Status { get; private set; }

    public DateOnly? ExpectedDeliveryDate { get; set; }

    public Guid? ApprovedByUserId { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Supplier's acknowledgement or proforma reference.</summary>
    public string? SupplierReference { get; set; }

    public string? Notes { get; set; }

    /// <summary>To be dealt with first: raised because an item fell to its reorder point.</summary>
    public bool IsPriority { get; set; }

    /// <summary>Drafted by the application, not by a person, when stock reached its reorder point.</summary>
    public bool RaisedAutomatically { get; set; }

    public decimal TotalExcludingVat => Money.Round(_lines.Sum(l => l.LineTotalExcludingVat));
    public decimal TotalVat => Money.Round(_lines.Sum(l => l.VatAmount));
    public decimal TotalIncludingVat => Money.Round(TotalExcludingVat + TotalVat);

    public IReadOnlyCollection<PurchaseOrderLine> Lines => _lines.AsReadOnly();

    public bool IsMutable => Status is PurchaseOrderStatus.Draft;

    public Result AddLine(PurchaseOrderLine line)
    {
        if (!IsMutable)
            return Result.Failure(Error.Conflict("Po_NotEditable", Status));

        line.AttachTo(Id);
        line.LineNumber = _lines.Count + 1;
        _lines.Add(line);
        return Result.Success();
    }

    /// <summary>
    /// Approval, required before the order can be sent. The approver must differ from the person
    /// who raised it — the usual separation of duties on committed spend.
    /// </summary>
    public Result Approve(DateTimeOffset at, Guid byUserId)
    {
        if (Status != PurchaseOrderStatus.Draft)
            return Result.Failure(Error.Conflict("Po_NotADraft"));
        if (_lines.Count == 0)
            return Result.Failure(Error.Validation("Po_NoLines"));
        if (byUserId == RaisedByUserId)
            return Result.Failure(Error.Forbidden("Po_ApproverMustDifferFromRequester"));

        ApprovedAt = at;
        ApprovedByUserId = byUserId;
        Status = PurchaseOrderStatus.Approved;
        return Result.Success();
    }

    public Result Send(DateTimeOffset at)
    {
        if (Status != PurchaseOrderStatus.Approved)
            return Result.Failure(Error.Conflict("Po_MustBeApprovedBeforeSending"));

        SentAt = at;
        Status = PurchaseOrderStatus.Sent;
        return Result.Success();
    }

    /// <summary>
    /// Records a delivery against a line and recomputes the order status, so a part-delivered
    /// order stays distinguishable from a complete one without a manual flag.
    /// </summary>
    public Result ReceiveLine(Guid lineId, decimal quantity)
    {
        if (Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Po_CannotReceiveInStatus", Status));

        var line = _lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return Result.Failure(Error.NotFound("Po_LineNotFound"));

        var received = line.Receive(quantity);
        if (received.IsFailure) return received;

        Status = _lines.All(l => l.IsFullyReceived)
            ? PurchaseOrderStatus.Received
            : PurchaseOrderStatus.PartiallyReceived;

        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (Status is PurchaseOrderStatus.Received)
            return Result.Failure(Error.Conflict("Po_AlreadyReceived"));
        if (_lines.Any(l => l.QuantityReceived > 0m))
            return Result.Failure(Error.Conflict("Po_PartiallyReceivedCannotCancel"));

        Status = PurchaseOrderStatus.Cancelled;
        Notes = reason;
        return Result.Success();
    }

    public bool IsOverdue(DateOnly today) =>
        Status is PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived &&
        ExpectedDeliveryDate is { } expected && expected < today;
}

public enum PurchaseOrderStatus
{
    Draft = 0,
    Approved = 1,
    Sent = 2,
    PartiallyReceived = 3,
    Received = 4,
    Cancelled = 9
}

public sealed class PurchaseOrderLine : Entity
{
    private PurchaseOrderLine() { }

    public PurchaseOrderLine(Guid stockItemId, decimal quantityOrdered, decimal unitPriceMad)
    {
        StockItemId = stockItemId;
        QuantityOrdered = quantityOrdered;
        UnitPriceMad = Money.Round(unitPriceMad);
    }

    public Guid PurchaseOrderId { get; private set; }
    internal void AttachTo(Guid purchaseOrderId) => PurchaseOrderId = purchaseOrderId;

    public int LineNumber { get; set; }

    public Guid StockItemId { get; private set; }

    /// <summary>Description as ordered, in case the catalogue entry is later renamed.</summary>
    public string? Description { get; set; }

    public decimal QuantityOrdered { get; private set; }
    public decimal QuantityReceived { get; private set; }

    public decimal UnitPriceMad { get; set; }

    /// <summary>Discount on the line as a fraction (0.05 = 5 %).</summary>
    public decimal DiscountRate { get; set; }

    /// <summary>Reagents and consumables are normally taxed at the standard 20 % rate.</summary>
    public decimal VatRate { get; set; } = 0.20m;

    /// <summary>
    /// What one unit actually costs once the line discount is taken off: the price a received lot
    /// is valued at. The list price would overstate the stock by the discount negotiated.
    /// </summary>
    public decimal NetUnitPriceMad => Money.Round(UnitPriceMad * (1m - DiscountRate));

    public decimal LineTotalExcludingVat =>
        Money.Round(QuantityOrdered * UnitPriceMad * (1m - DiscountRate));

    public decimal VatAmount => Money.Round(LineTotalExcludingVat * VatRate);

    public decimal QuantityOutstanding => Math.Max(0m, QuantityOrdered - QuantityReceived);

    public bool IsFullyReceived => QuantityReceived >= QuantityOrdered;

    /// <summary>
    /// Date of the latest delivery on this line. The legal payment term of the supplier's invoice
    /// runs from the delivery (loi n° 69-21), so it is kept where the invoice can find it.
    /// </summary>
    public DateOnly? LastReceivedOn { get; set; }

    internal Result Receive(decimal quantity)
    {
        if (quantity <= 0m)
            return Result.Failure(Error.Validation("Po_QuantityMustBePositive"));
        // Over-delivery is common and legitimate; only flagrant excess is refused.
        if (QuantityReceived + quantity > QuantityOrdered * 1.1m + 1m)
            return Result.Failure(Error.Validation("Po_ReceiptExceedsOrder", QuantityOutstanding));

        QuantityReceived += quantity;
        return Result.Success();
    }
}
