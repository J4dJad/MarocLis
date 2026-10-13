using MarocLIS.Domain.Common;
using MarocLIS.Domain.Morocco;

namespace MarocLIS.Domain.Inventory;

/// <summary>A supplier of reagents, consumables or equipment.</summary>
public sealed class Supplier : AuditableAggregateRoot, ISoftDeletable
{
    private Supplier() { }

    public Supplier(string code, string name)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string? Ice { get; set; }
    public string? FiscalId { get; set; }
    public string? CommercialRegisterNumber { get; set; }

    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public MoroccanRegion Region { get; set; }

    /// <summary>Contractual delivery lead time, used to set reorder points.</summary>
    public int LeadTimeDays { get; set; } = 7;

    public int PaymentTermsDays { get; set; } = 30;

    /// <summary>
    /// Whether the supplier has been evaluated as required by ISO 15189 §4.6 — a laboratory has to
    /// assess and monitor its critical suppliers, not just buy from them.
    /// </summary>
    public bool IsApproved { get; set; }
    public DateOnly? LastEvaluationDate { get; set; }

    /// <summary>Performance score from the last evaluation, 0–100.</summary>
    public int? EvaluationScore { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public bool IsEvaluationOverdue(DateOnly today) =>
        IsApproved && (LastEvaluationDate is null || LastEvaluationDate.Value.AddYears(1) < today);

    public Result Validate()
    {
        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add(Error.Validation("Supplier_NameRequired"));
        if (Ice is not null && !MoroccanIdentifiers.IsValidIce(Ice))
            errors.Add(Error.Validation("Supplier_IceInvalid"));
        if (EvaluationScore is < 0 or > 100)
            errors.Add(Error.Validation("Supplier_ScoreOutOfRange"));
        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

/// <summary>
/// A stock item: a reagent, a control material, a consumable. Quantities are held per lot on
/// <see cref="ReagentLot"/>; this record carries the identity and the reorder policy.
/// </summary>
public sealed class StockItem : AuditableAggregateRoot, ISoftDeletable, ISiteScoped
{
    private readonly List<ReagentLot> _lots = [];

    private StockItem() { }

    public StockItem(string code, MultilingualText name, StockItemKind kind, string unitOfMeasure,
                     Guid siteId)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
        Kind = kind;
        UnitOfMeasure = unitOfMeasure;
        SiteId = siteId;
    }

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();
    public StockItemKind Kind { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>Unit stock is counted in: "boîte", "flacon", "test", "mL".</summary>
    public string UnitOfMeasure { get; set; } = string.Empty;

    /// <summary>Number of tests one pack yields, where that is meaningful.</summary>
    public int? TestsPerPack { get; set; }

    public Guid? PreferredSupplierId { get; set; }
    public string? SupplierCatalogueReference { get; set; }
    public string? Manufacturer { get; set; }

    public Guid? DepartmentId { get; set; }

    /// <summary>Analyses this item is consumed by, so a stock-out can be linked to its impact.</summary>
    public string? UsedByAnalysisCodesCsv { get; set; }

    // ---- Reorder policy ------------------------------------------------------------------------

    /// <summary>Quantity at which a purchase order should be raised.</summary>
    public decimal ReorderPoint { get; set; }

    /// <summary>Quantity normally ordered.</summary>
    public decimal ReorderQuantity { get; set; }

    /// <summary>Safety stock the laboratory never wants to go below.</summary>
    public decimal MinimumStock { get; set; }

    public decimal? MaximumStock { get; set; }

    /// <summary>Last known unit purchase price, for stock valuation.</summary>
    public decimal? LastUnitPriceMad { get; set; }

    // ---- Storage -------------------------------------------------------------------------------

    public StorageCondition StorageCondition { get; set; } = StorageCondition.RoomTemperature;
    public string? DefaultStorageLocation { get; set; }

    /// <summary>Hazard classification, for the safety data sheet and the risk register.</summary>
    public bool IsHazardous { get; set; }
    public string? HazardClass { get; set; }
    public Guid? SafetyDataSheetDocumentId { get; set; }

    /// <summary>
    /// Whether the item is critical: a stock-out stops an analysis being offered, which has to be
    /// declared to prescribers and raises a non-conformity.
    /// </summary>
    public bool IsCritical { get; set; }

    public bool IsActive { get; set; } = true;

    public IReadOnlyCollection<ReagentLot> Lots => _lots.AsReadOnly();

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    // ---- Derived -------------------------------------------------------------------------------

    /// <summary>Quantity on hand across all usable lots.</summary>
    public decimal QuantityOnHand(DateOnly today) =>
        _lots.Where(l => l.IsUsable(today)).Sum(l => l.QuantityRemaining);

    public bool NeedsReorder(DateOnly today) => QuantityOnHand(today) <= ReorderPoint;

    public bool IsBelowMinimum(DateOnly today) => QuantityOnHand(today) < MinimumStock;

    /// <summary>
    /// The lot to consume next: first-expiry-first-out. Using FEFO rather than FIFO is what keeps
    /// a laboratory from discarding usable reagent while an older one expires on the shelf.
    /// </summary>
    public ReagentLot? NextLotToUse(DateOnly today) =>
        _lots.Where(l => l.IsUsable(today) && l.QuantityRemaining > 0)
             .OrderBy(l => l.ExpiryDate)
             .ThenBy(l => l.ReceivedOn)
             .FirstOrDefault();

    public IReadOnlyList<ReagentLot> LotsExpiringWithin(DateOnly today, int days) =>
        [.. _lots.Where(l => l.QuantityRemaining > 0 && !l.IsQuarantined
                             && l.ExpiryDate <= today.AddDays(days))
                 .OrderBy(l => l.ExpiryDate)];

    public ReagentLot AddLot(ReagentLot lot)
    {
        lot.AttachTo(Id);
        _lots.Add(lot);
        return lot;
    }
}

public enum StockItemKind
{
    Reagent = 0,
    /// <summary>Internal quality control material.</summary>
    Control = 1,
    Calibrator = 2,
    Consumable = 3,
    /// <summary>Tubes, containers, needles.</summary>
    CollectionSupply = 4,
    /// <summary>Cleaning and decontamination products.</summary>
    Maintenance = 5,
    OfficeSupply = 6,
    SparePart = 7
}

public enum StorageCondition
{
    RoomTemperature = 0,
    /// <summary>2–8 °C.</summary>
    Refrigerated = 1,
    /// <summary>−20 °C.</summary>
    Frozen = 2,
    /// <summary>−80 °C.</summary>
    DeepFrozen = 3,
    ProtectFromLight = 4,
    Desiccated = 5
}

/// <summary>
/// A received lot of a stock item. Lot-level tracking is what allows the laboratory to answer the
/// question an auditor or a manufacturer recall actually asks: which patient results were produced
/// with this lot?
/// </summary>
public sealed class ReagentLot : Entity, IAuditable
{
    private ReagentLot() { }

    public ReagentLot(string lotNumber, DateOnly expiryDate, decimal quantityReceived,
                      DateOnly receivedOn)
    {
        LotNumber = lotNumber;
        ExpiryDate = expiryDate;
        QuantityReceived = quantityReceived;
        QuantityRemaining = quantityReceived;
        ReceivedOn = receivedOn;
    }

    public Guid StockItemId { get; private set; }
    internal void AttachTo(Guid stockItemId) => StockItemId = stockItemId;

    public string LotNumber { get; private set; } = string.Empty;
    public DateOnly ExpiryDate { get; private set; }

    public decimal QuantityReceived { get; private set; }
    public decimal QuantityRemaining { get; private set; }

    public DateOnly ReceivedOn { get; private set; }
    public Guid? ReceivedByUserId { get; set; }

    public Guid? SupplierId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public string? DeliveryNoteReference { get; set; }
    public string? SupplierInvoiceReference { get; set; }

    public decimal? UnitPriceMad { get; set; }

    /// <summary>Date the lot was put into use; on-board stability runs from here.</summary>
    public DateOnly? OpenedOn { get; set; }
    public int? StabilityAfterOpeningDays { get; set; }

    public string? StorageLocation { get; set; }

    // ---- Acceptance ----------------------------------------------------------------------------

    /// <summary>
    /// A new lot is quarantined until it has been checked. ISO 15189 §5.3.2.3 requires verification
    /// that a new lot performs acceptably before it is used on patient samples, so the default is
    /// "not usable" rather than "usable unless someone objects".
    /// </summary>
    public bool IsQuarantined { get; private set; } = true;

    public DateOnly? ReleasedOn { get; private set; }
    public Guid? ReleasedByUserId { get; private set; }

    /// <summary>The lot-to-lot comparison or control run that justified release.</summary>
    public string? AcceptanceEvidence { get; private set; }

    /// <summary>Set when the lot was rejected on receipt or withdrawn afterwards.</summary>
    public bool IsRejected { get; private set; }
    public string? RejectionReason { get; private set; }

    /// <summary>Set when the manufacturer recalled the lot.</summary>
    public bool IsRecalled { get; private set; }
    public DateOnly? RecalledOn { get; private set; }
    public string? RecallReference { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    // ---- Behaviour -----------------------------------------------------------------------------

    public bool IsExpired(DateOnly today) => ExpiryDate < today;

    public bool IsBeyondOpenStability(DateOnly today) =>
        OpenedOn is { } opened && StabilityAfterOpeningDays is { } days && today > opened.AddDays(days);

    public bool IsUsable(DateOnly today) =>
        !IsQuarantined && !IsRejected && !IsRecalled &&
        !IsExpired(today) && !IsBeyondOpenStability(today);

    public int DaysUntilExpiry(DateOnly today) => ExpiryDate.DayNumber - today.DayNumber;

    public Result Release(DateOnly on, Guid byUserId, string evidence)
    {
        if (IsRejected) return Result.Failure(Error.Conflict("Lot_Rejected"));
        if (string.IsNullOrWhiteSpace(evidence))
            return Result.Failure(Error.Regulatory("Lot_AcceptanceEvidenceRequired"));

        IsQuarantined = false;
        ReleasedOn = on;
        ReleasedByUserId = byUserId;
        AcceptanceEvidence = evidence;
        return Result.Success();
    }

    public Result Reject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Lot_RejectionReasonRequired"));

        IsRejected = true;
        RejectionReason = reason;
        IsQuarantined = true;
        return Result.Success();
    }

    /// <summary>
    /// Withdraws a rejection recorded in error, returning the lot to quarantine.
    /// <para>
    /// Deliberately returns it to <b>quarantine</b> rather than to usable: the verification that
    /// <see cref="Release"/> demands has still never been done, and a lot that was once rejected is
    /// the last one that should skip it. So this undoes the clerical mistake and nothing more —
    /// the lot still needs releasing on its own evidence.
    /// </para>
    /// <para>
    /// A recalled lot cannot be un-rejected: a manufacturer recall is not a clerical error, and the
    /// reason the lot is out of service does not go away because somebody clicked the wrong button.
    /// </para>
    /// </summary>
    public Result ClearRejection(string reason)
    {
        if (!IsRejected) return Result.Failure(Error.Conflict("Lot_NotRejected"));

        if (IsRecalled) return Result.Failure(Error.Regulatory("Lot_RecalledCannotBeRestored"));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Lot_ClearRejectionReasonRequired"));

        IsRejected = false;
        RejectionReason = null;
        IsQuarantined = true;

        // The previous acceptance evidence, if any, is cleared with the rejection: it was recorded
        // against a decision that has just been withdrawn, and re-releasing has to stand on a
        // fresh check.
        ReleasedOn = null;
        ReleasedByUserId = null;
        AcceptanceEvidence = null;

        return Result.Success();
    }

    public void Recall(DateOnly on, string reference)
    {
        IsRecalled = true;
        RecalledOn = on;
        RecallReference = reference;
    }

    /// <summary>
    /// Takes quantity out of the lot. Refuses to consume from a lot that is not usable: that check
    /// belongs here, because the alternative is a result produced with expired reagent.
    /// </summary>
    public Result Consume(decimal quantity, DateOnly today)
    {
        if (quantity <= 0m)
            return Result.Failure(Error.Validation("Lot_QuantityMustBePositive"));
        if (!IsUsable(today))
            return Result.Failure(Error.Regulatory("Lot_NotUsable", LotNumber));
        if (quantity > QuantityRemaining)
            return Result.Failure(Error.Validation("Lot_InsufficientQuantity", QuantityRemaining));

        QuantityRemaining -= quantity;
        OpenedOn ??= today;
        return Result.Success();
    }

    /// <summary>Puts quantity back, for a returned or miscounted issue.</summary>
    public Result Restore(decimal quantity)
    {
        if (quantity <= 0m)
            return Result.Failure(Error.Validation("Lot_QuantityMustBePositive"));
        if (QuantityRemaining + quantity > QuantityReceived)
            return Result.Failure(Error.Validation("Lot_RestoreExceedsReceived"));

        QuantityRemaining += quantity;
        return Result.Success();
    }

    /// <summary>Writes off the remaining quantity, e.g. on expiry.</summary>
    public decimal WriteOffRemainder()
    {
        var written = QuantityRemaining;
        QuantityRemaining = 0m;
        return written;
    }
}

/// <summary>
/// An immutable stock movement. The quantity on a lot is a cached projection of these rows; keeping
/// the movements is what makes a stock count explicable.
/// <para>
/// An aggregate root rather than a child entity, which is what lets <c>IRepository&lt;T&gt;</c>
/// reach it. That is not a concession to the repository's type constraint — a movement is created
/// on its own terms, never modified afterwards, owned by no collection and referenced by nothing.
/// An append-only ledger row is its own aggregate; <see cref="StockItem"/> deliberately does not
/// hold a movements collection, because loading an item should not drag in two years of history.
/// </para>
/// </summary>
public sealed class StockMovement : AggregateRoot, IAuditable, ISiteScoped
{
    private StockMovement() { }

    public StockMovement(Guid stockItemId, Guid? reagentLotId, StockMovementKind kind,
                         decimal quantity, DateTimeOffset occurredAt, Guid byUserId, Guid siteId)
    {
        StockItemId = stockItemId;
        ReagentLotId = reagentLotId;
        Kind = kind;
        Quantity = quantity;
        OccurredAt = occurredAt;
        ByUserId = byUserId;
        SiteId = siteId;
    }

    public Guid StockItemId { get; private set; }
    public Guid? ReagentLotId { get; private set; }
    public Guid SiteId { get; set; }

    public StockMovementKind Kind { get; private set; }

    /// <summary>Always positive; the direction is carried by <see cref="Kind"/>.</summary>
    public decimal Quantity { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
    public Guid ByUserId { get; private set; }

    /// <summary>Quantity remaining on the lot after the movement, for reconciliation.</summary>
    public decimal? BalanceAfter { get; set; }

    public Guid? InstrumentId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>Reason, required for adjustments and write-offs.</summary>
    public string? Reason { get; set; }

    public string? Reference { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    /// <summary>Signed effect on stock, for running-balance queries.</summary>
    public decimal SignedQuantity => Kind switch
    {
        StockMovementKind.Receipt or StockMovementKind.Return or StockMovementKind.PositiveAdjustment
            or StockMovementKind.TransferIn => Quantity,
        _ => -Quantity
    };
}

public enum StockMovementKind
{
    /// <summary>Received from a supplier.</summary>
    Receipt = 0,
    /// <summary>Issued to a bench or loaded onto an instrument.</summary>
    Issue = 1,
    /// <summary>Consumed by analyses.</summary>
    Consumption = 2,
    /// <summary>Returned unused.</summary>
    Return = 3,
    /// <summary>Discarded on expiry.</summary>
    ExpiryWriteOff = 4,
    /// <summary>Discarded after breakage or a defect.</summary>
    DamageWriteOff = 5,
    /// <summary>Returned to the supplier.</summary>
    SupplierReturn = 6,
    PositiveAdjustment = 7,
    NegativeAdjustment = 8,
    TransferIn = 9,
    TransferOut = 10
}
