using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// A supplier's invoice (facture fournisseur), entered against the purchase-order lines it bills.
/// <para>
/// Nothing is paid on the strength of the invoice alone. Each line is compared with what was
/// ordered and with what was actually received — the three-way match — and any quantity billed but
/// not delivered, or any price above the one agreed, blocks the invoice until someone with the
/// payment-approval right looks at it and says why it may be paid anyway.
/// </para>
/// <para>
/// The due date follows loi n° 69-21 (amending the code de commerce on payment terms): the agreed
/// term, at most 120 days and 60 days when nothing was agreed, counted from delivery.
/// </para>
/// </summary>
public sealed class SupplierInvoice : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<SupplierInvoiceLine> _lines = [];
    private readonly List<SupplierInvoiceVariance> _variances = [];
    private readonly List<SupplierPayment> _payments = [];

    private SupplierInvoice() { }

    public SupplierInvoice(Guid supplierId, Guid siteId, string invoiceNumber, DateOnly invoiceDate,
                           DateOnly receivedOn, Guid enteredByUserId)
    {
        SupplierId = supplierId;
        SiteId = siteId;
        InvoiceNumber = invoiceNumber.Trim();
        InvoiceDate = invoiceDate;
        ReceivedOn = receivedOn;
        EnteredByUserId = enteredByUserId;
        Status = SupplierInvoiceStatus.Pending;
    }

    public Guid SupplierId { get; private set; }
    public Guid SiteId { get; set; }

    /// <summary>The supplier's own number, unique for that supplier.</summary>
    public string InvoiceNumber { get; private set; } = string.Empty;

    public DateOnly InvoiceDate { get; private set; }

    /// <summary>The day the laboratory received the invoice.</summary>
    public DateOnly ReceivedOn { get; private set; }

    public Guid EnteredByUserId { get; private set; }

    public SupplierInvoiceStatus Status { get; private set; }

    /// <summary>Latest delivery of the goods invoiced: where the legal payment term starts.</summary>
    public DateOnly? DeliveryDate { get; private set; }

    /// <summary>The supplier's agreed term when the invoice was entered, already capped by the law.</summary>
    public int PaymentTermsDays { get; private set; }

    public DateOnly DueDate { get; private set; }

    public Guid? UnblockedByUserId { get; private set; }
    public DateTimeOffset? UnblockedAt { get; private set; }
    public string? UnblockJustification { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>Date of the payment that settled the invoice.</summary>
    public DateOnly? PaidOn { get; private set; }

    /// <summary>
    /// Payments recorded so far. Kept on the invoice row so that every payment changes it: a
    /// second partial payment leaves the status as it was, and without a change to the row its
    /// version would not catch two accountants paying the same balance at once.
    /// </summary>
    public int PaymentCount { get; private set; }

    /// <summary>File name of the attached scan, if any; the content is a <see cref="SupplierInvoiceFile"/>.</summary>
    public string? ScanFileName { get; set; }

    public string? Notes { get; set; }

    public IReadOnlyCollection<SupplierInvoiceLine> Lines => _lines.AsReadOnly();
    public IReadOnlyCollection<SupplierInvoiceVariance> Variances => _variances.AsReadOnly();
    public IReadOnlyCollection<SupplierPayment> Payments => _payments.AsReadOnly();

    public decimal TotalExcludingVat => Money.Round(_lines.Sum(l => l.LineTotalExcludingVat));
    public decimal TotalVat => Money.Round(_lines.Sum(l => l.VatAmount));
    public decimal TotalIncludingVat => Money.Round(TotalExcludingVat + TotalVat);

    public decimal AmountPaid => Money.Round(_payments.Sum(p => p.AmountMad));
    public decimal AmountOutstanding => Math.Max(0m, Money.Round(TotalIncludingVat - AmountPaid));

    /// <summary>Still owed, whatever the match said: a blocked invoice falls due all the same.</summary>
    public bool IsOpen => Status is SupplierInvoiceStatus.Pending or SupplierInvoiceStatus.Blocked
                                  or SupplierInvoiceStatus.ApprovedForPayment
                                  or SupplierInvoiceStatus.PartiallyPaid;

    public bool IsOverdue(DateOnly today) => IsOpen && AmountOutstanding > 0m && DueDate < today;

    public int DaysOverdue(DateOnly today) => IsOverdue(today) ? today.DayNumber - DueDate.DayNumber : 0;

    public Result AddLine(SupplierInvoiceLine line)
    {
        if (Status != SupplierInvoiceStatus.Pending)
            return Result.Failure(Error.Conflict("SupplierInvoice_NotEditable"));
        if (line.Quantity <= 0m)
            return Result.Failure(Error.Validation("SupplierInvoice_QuantityMustBePositive"));
        if (line.UnitPriceMad < 0m)
            return Result.Failure(Error.Validation("SupplierInvoice_PriceMustNotBeNegative"));
        if (line.VatRate is < 0m or > 1m)
            return Result.Failure(Error.Validation("SupplierInvoice_VatRateOutOfRange"));
        if (_lines.Any(l => l.PurchaseOrderLineId == line.PurchaseOrderLineId))
            return Result.Failure(Error.Conflict("SupplierInvoice_OrderLineTwice", line.Description));

        line.AttachTo(Id);
        line.LineNumber = _lines.Count + 1;
        _lines.Add(line);
        return Result.Success();
    }

    /// <summary>
    /// Sets the due date: <paramref name="termsDays"/> after the latest delivery of the invoiced
    /// goods, or after the invoice date when no delivery date is known.
    /// </summary>
    public void SetPaymentTerms(DateOnly? deliveryDate, int termsDays)
    {
        DeliveryDate = deliveryDate;
        PaymentTermsDays = PaymentTerms.Effective(termsDays);
        DueDate = PaymentTerms.DueDate(InvoiceDate, deliveryDate, termsDays);
    }

    /// <summary>
    /// Records the outcome of the three-way match. Any variance blocks the invoice; none approves it
    /// for payment. Only an invoice not yet approved can be matched again — once someone has
    /// decided to pay it, a later receipt does not reopen that decision.
    /// </summary>
    public Result ApplyMatch(IReadOnlyList<MatchVariance> variances)
    {
        if (Status is not (SupplierInvoiceStatus.Pending or SupplierInvoiceStatus.Blocked))
            return Result.Failure(Error.Conflict("SupplierInvoice_AlreadyMatched", Status));
        if (_lines.Count == 0)
            return Result.Failure(Error.Validation("SupplierInvoice_NoLines"));

        _variances.Clear();
        foreach (var variance in variances)
            _variances.Add(new SupplierInvoiceVariance(Id, variance.LineNumber, variance.Kind,
                                                       variance.Expected, variance.Actual));

        Status = _variances.Count == 0
            ? SupplierInvoiceStatus.ApprovedForPayment
            : SupplierInvoiceStatus.Blocked;
        return Result.Success();
    }

    /// <summary>
    /// Releases a blocked invoice for payment despite its variances. The justification is required
    /// and the person who entered the invoice cannot release it: the control is pointless if the
    /// same hand that keys the amount waves the difference through.
    /// </summary>
    public Result Unblock(DateTimeOffset at, Guid byUserId, string justification)
    {
        if (Status != SupplierInvoiceStatus.Blocked)
            return Result.Failure(Error.Conflict("SupplierInvoice_NotBlocked"));
        if (string.IsNullOrWhiteSpace(justification))
            return Result.Failure(Error.Validation("SupplierInvoice_JustificationRequired"));
        if (byUserId == EnteredByUserId)
            return Result.Failure(Error.Forbidden("SupplierInvoice_UnblockerMustDifferFromEnterer"));

        UnblockedAt = at;
        UnblockedByUserId = byUserId;
        UnblockJustification = justification.Trim();
        Status = SupplierInvoiceStatus.ApprovedForPayment;
        return Result.Success();
    }

    /// <summary>
    /// Records a payment, partial or in full. Only an invoice approved for payment — matched, or
    /// released by a manager — can be paid, and never for more than is still owed.
    /// </summary>
    public Result RecordPayment(SupplierPayment payment)
    {
        if (Status is not (SupplierInvoiceStatus.ApprovedForPayment or SupplierInvoiceStatus.PartiallyPaid))
            return Result.Failure(Error.Conflict("SupplierInvoice_NotApprovedForPayment", Status));
        if (payment.AmountMad <= 0m)
            return Result.Failure(Error.Validation("SupplierPayment_AmountMustBePositive"));
        if (payment.AmountMad > AmountOutstanding)
            return Result.Failure(Error.Validation("SupplierPayment_ExceedsOutstanding", AmountOutstanding));
        if (payment.PaidOn < InvoiceDate)
            return Result.Failure(Error.Validation("SupplierPayment_BeforeInvoice"));
        if (payment.Method != SupplierPaymentMethod.Cash && string.IsNullOrWhiteSpace(payment.Reference))
            return Result.Failure(Error.Validation("SupplierPayment_ReferenceRequired"));

        payment.AttachTo(Id);
        _payments.Add(payment);
        PaymentCount = _payments.Count;

        if (AmountOutstanding == 0m)
        {
            Status = SupplierInvoiceStatus.Paid;
            PaidOn = payment.PaidOn;
        }
        else
        {
            Status = SupplierInvoiceStatus.PartiallyPaid;
        }

        return Result.Success();
    }

    /// <summary>Cancels an invoice entered in error. A paid or part-paid invoice cannot be.</summary>
    public Result Cancel(DateTimeOffset at, string reason)
    {
        if (Status == SupplierInvoiceStatus.Cancelled)
            return Result.Failure(Error.Conflict("SupplierInvoice_AlreadyCancelled"));
        if (_payments.Count > 0)
            return Result.Failure(Error.Conflict("SupplierInvoice_HasPayments"));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("SupplierInvoice_ReasonRequired"));

        CancelledAt = at;
        CancellationReason = reason.Trim();
        Status = SupplierInvoiceStatus.Cancelled;
        return Result.Success();
    }
}

public enum SupplierInvoiceStatus
{
    /// <summary>Entered, not yet matched.</summary>
    Pending = 0,

    /// <summary>The match found variances: not payable until released.</summary>
    Blocked = 1,

    ApprovedForPayment = 2,
    PartiallyPaid = 3,
    Paid = 4,
    Cancelled = 9
}

/// <summary>One invoiced line, always tied to the purchase-order line it bills.</summary>
public sealed class SupplierInvoiceLine : Entity
{
    private SupplierInvoiceLine() { }

    public SupplierInvoiceLine(Guid purchaseOrderLineId, Guid stockItemId, string description,
                               decimal quantity, decimal unitPriceMad, decimal vatRate)
    {
        PurchaseOrderLineId = purchaseOrderLineId;
        StockItemId = stockItemId;
        Description = description;
        Quantity = quantity;
        UnitPriceMad = Money.Round(unitPriceMad);
        VatRate = vatRate;
    }

    public Guid SupplierInvoiceId { get; private set; }
    internal void AttachTo(Guid invoiceId) => SupplierInvoiceId = invoiceId;

    public int LineNumber { get; set; }

    public Guid PurchaseOrderLineId { get; private set; }
    public Guid StockItemId { get; private set; }
    public string Description { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    /// <summary>Unit price excluding VAT, net of any discount, as invoiced.</summary>
    public decimal UnitPriceMad { get; private set; }

    public decimal VatRate { get; private set; }

    public decimal LineTotalExcludingVat => Money.Round(Quantity * UnitPriceMad);
    public decimal VatAmount => Money.Round(LineTotalExcludingVat * VatRate);
}

public enum VarianceKind
{
    /// <summary>More billed than received and not yet invoiced.</summary>
    QuantityExceedsReceived = 0,

    /// <summary>Unit price outside the tolerance around the ordered net price.</summary>
    PriceDiffers = 1,

    /// <summary>A VAT rate other than the one on the order.</summary>
    VatRateDiffers = 2
}

/// <summary>A difference found by the three-way match, kept with the invoice for the record.</summary>
public sealed class SupplierInvoiceVariance : Entity
{
    private SupplierInvoiceVariance() { }

    internal SupplierInvoiceVariance(Guid invoiceId, int lineNumber, VarianceKind kind,
                                     decimal expected, decimal actual)
    {
        SupplierInvoiceId = invoiceId;
        LineNumber = lineNumber;
        Kind = kind;
        Expected = expected;
        Actual = actual;
    }

    public Guid SupplierInvoiceId { get; private set; }
    public int LineNumber { get; private set; }
    public VarianceKind Kind { get; private set; }

    /// <summary>What the order and the receipts allow: a quantity, a price or a rate.</summary>
    public decimal Expected { get; private set; }

    public decimal Actual { get; private set; }
}

public enum SupplierPaymentMethod
{
    /// <summary>Virement.</summary>
    Transfer = 0,
    Cheque = 1,
    /// <summary>Espèces.</summary>
    Cash = 2,
    /// <summary>Effet de commerce (lettre de change, billet à ordre).</summary>
    BillOfExchange = 3
}

public sealed class SupplierPayment : Entity
{
    private SupplierPayment() { }

    public SupplierPayment(DateOnly paidOn, decimal amountMad, SupplierPaymentMethod method,
                           string? reference, DateTimeOffset recordedAt, Guid recordedByUserId)
    {
        PaidOn = paidOn;
        AmountMad = Money.Round(amountMad);
        Method = method;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        RecordedAt = recordedAt;
        RecordedByUserId = recordedByUserId;
    }

    public Guid SupplierInvoiceId { get; private set; }
    internal void AttachTo(Guid invoiceId) => SupplierInvoiceId = invoiceId;

    public DateOnly PaidOn { get; private set; }
    public decimal AmountMad { get; private set; }
    public SupplierPaymentMethod Method { get; private set; }

    /// <summary>Transfer, cheque or bill number.</summary>
    public string? Reference { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }
    public Guid RecordedByUserId { get; private set; }
}

/// <summary>The scan of a supplier invoice, kept apart so listing invoices never loads it.</summary>
public sealed class SupplierInvoiceFile : AuditableAggregateRoot
{
    /// <summary>As for personnel documents: a few scanned pages.</summary>
    public const int MaximumBytes = 10 * 1024 * 1024;

    private SupplierInvoiceFile() { }

    public SupplierInvoiceFile(Guid supplierInvoiceId, string fileName, string contentType, byte[] content)
    {
        SupplierInvoiceId = supplierInvoiceId;
        Replace(fileName, contentType, content);
    }

    public Guid SupplierInvoiceId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Content { get; private set; } = [];

    public void Replace(string fileName, string contentType, byte[] content)
    {
        FileName = fileName;
        ContentType = contentType;
        Content = content;
    }
}
