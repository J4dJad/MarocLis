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

    /// <summary>
    /// Agreed payment term in days. Loi n° 69-21: 60 days when nothing was agreed, at most 120 by
    /// agreement; the supplier invoice's due date is computed from it.
    /// </summary>
    public int PaymentTermsDays { get; set; } = PaymentTerms.DefaultDays;

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

    /// <summary>
    /// The supplier's auxiliary (third-party) account in the accounting export; the supplier code
    /// when left empty.
    /// </summary>
    public string? AccountingAccount { get; set; }

    public bool IsEvaluationOverdue(DateOnly today) =>
        IsApproved && (LastEvaluationDate is null || LastEvaluationDate.Value.AddYears(1) < today);

    // ---- Regulatory (loi 84-12) ----------------------------------------------------------------

    /// <summary>
    /// Registration number with the AMMPS / DMP (Direction du médicament et de la pharmacie) as an
    /// establishment allowed to place in vitro diagnostic devices on the Moroccan market (loi 84-12).
    /// </summary>
    public string? RegulatoryRegistrationNumber { get; set; }

    public DateOnly? RegulatoryRegistrationValidUntil { get; set; }

    /// <summary>
    /// A registration whose validity date has passed. A supplier without a recorded registration is
    /// not "expired": it may not need one (office supplies) and the screen says it is missing instead.
    /// </summary>
    public bool IsRegistrationExpired(DateOnly today) =>
        RegulatoryRegistrationValidUntil is { } until && until < today;

    public Result Validate()
    {
        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add(Error.Validation("Supplier_NameRequired"));
        if (Ice is not null && !MoroccanIdentifiers.IsValidIce(Ice))
            errors.Add(Error.Validation("Supplier_IceInvalid"));
        if (EvaluationScore is < 0 or > 100)
            errors.Add(Error.Validation("Supplier_ScoreOutOfRange"));
        if (PaymentTermsDays is < 0 or > PaymentTerms.MaximumDays)
            errors.Add(Error.Validation("Supplier_PaymentTermsTooLong", PaymentTerms.MaximumDays));
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

    /// <summary>
    /// Last unit price ordered from the supplier, before the line discount: what the next order
    /// line starts from. Each lot carries its own net valuation.
    /// </summary>
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

    /// <summary>
    /// GS1 trade item number (GTIN-14, shorter GTINs left-padded with zeros), as read from the
    /// DataMatrix on the box. Lets a scan at reception pick the article without typing.
    /// </summary>
    public string? Gtin { get; set; }

    /// <summary>The device carries the CE (CE-IVD) marking, required for an IVD sold in Morocco.</summary>
    public bool IsCeMarked { get; set; }

    public IReadOnlyCollection<ReagentLot> Lots => _lots.AsReadOnly();

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    // ---- Derived -------------------------------------------------------------------------------

    /// <summary>Quantity on hand across all usable lots.</summary>
    public decimal QuantityOnHand(DateOnly today) =>
        _lots.Where(l => l.IsIssuable(today)).Sum(l => l.QuantityRemaining);

    public bool NeedsReorder(DateOnly today) => QuantityOnHand(today) <= ReorderPoint;

    public bool IsBelowMinimum(DateOnly today) => QuantityOnHand(today) < MinimumStock;

    /// <summary>
    /// The lot to consume next: first-expiry-first-out. Using FEFO rather than FIFO is what keeps
    /// a laboratory from discarding usable reagent while an older one expires on the shelf.
    /// </summary>
    public ReagentLot? NextLotToUse(DateOnly today) =>
        _lots.Where(l => l.IsIssuable(today) && l.QuantityRemaining > 0)
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

    // ---- Consumption and computed reorder policy -----------------------------------------------

    /// <summary>Share of a pack lost to dead volume at each opening, in percent.</summary>
    public decimal? DeadVolumePercent { get; set; }

    /// <summary>Tests lost at each opening of a pack.</summary>
    public decimal? DeadVolumeTestsPerOpening { get; set; }

    /// <summary>Tests per result for the analyses that take more than one, "CODE=factor" entries.</summary>
    public string? TestsPerResultCsv { get; set; }

    /// <summary>Tests one calibration uses; one when unset.</summary>
    public int? TestsPerCalibration { get; set; }

    /// <summary>Safety stock in days of consumption, for the computed reorder point.</summary>
    public int? SafetyStockDays { get; set; }

    /// <summary>Shelf life a delivery typically has left on arrival; caps the quantity ordered.</summary>
    public int? TypicalShelfLifeDays { get; set; }

    /// <summary>
    /// The reorder point follows the consumption: recomputed before the reorder planner runs,
    /// instead of the figure typed in.
    /// </summary>
    public bool ReorderPointComputed { get; set; }

    public StockConsumptionPolicy ConsumptionPolicy => new()
    {
        DeadVolumePercent = DeadVolumePercent,
        DeadVolumeTestsPerOpening = DeadVolumeTestsPerOpening,
        TestsPerResultCsv = TestsPerResultCsv,
        TestsPerCalibration = TestsPerCalibration,
        SafetyStockDays = SafetyStockDays,
        TypicalShelfLifeDays = TypicalShelfLifeDays,
        ReorderPointComputed = ReorderPointComputed
    };

    public Result ApplyConsumptionPolicy(StockConsumptionPolicy policy)
    {
        var valid = policy.Validate();
        if (valid.IsFailure) return valid;

        DeadVolumePercent = policy.DeadVolumePercent;
        DeadVolumeTestsPerOpening = policy.DeadVolumeTestsPerOpening;
        TestsPerResultCsv = string.IsNullOrWhiteSpace(policy.TestsPerResultCsv) ? null : policy.TestsPerResultCsv.Trim();
        TestsPerCalibration = policy.TestsPerCalibration;
        SafetyStockDays = policy.SafetyStockDays;
        TypicalShelfLifeDays = policy.TypicalShelfLifeDays;
        ReorderPointComputed = policy.ReorderPointComputed;
        return Result.Success();
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

    /// <summary>Date the lot was put into use: the first unit issued from it.</summary>
    public DateOnly? OpenedOn { get; set; }

    /// <summary>
    /// Date the latest unit of the lot was issued, which is the one on the bench. On-board
    /// stability runs from here: a lot of six kits is not out of stability on day 29 because its
    /// first kit was opened a month ago while the fourth went on the analyser last week.
    /// </summary>
    public DateOnly? LastOpenedOn { get; private set; }

    /// <summary>
    /// The analyser on which this quarantined lot is loaded for its acceptance QC. Cleared when the
    /// lot is released or rejected, or when its evaluation is stopped.
    /// </summary>
    public Guid? EvaluationInstrumentId { get; private set; }

    /// <summary>
    /// When the evaluation run on <see cref="EvaluationInstrumentId"/> was explicitly started. The
    /// controls that analyser transmits from then on, the same day and once per control level, are
    /// the new lot's; every other transmission is routine QC of the lot in use. Null: the analyser's
    /// transmissions are all routine. Guessing the session from what was typed in the QC screen
    /// credited the evaluation run to the lot in use and the afternoon's routine QC to the new lot.
    /// </summary>
    public DateTimeOffset? EvaluationStartedAt { get; private set; }
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

    // ---- Reception checks ----------------------------------------------------------------------

    /// <summary>Temperature measured when the parcel arrived, °C. Null when not taken.</summary>
    public decimal? ArrivalTemperatureCelsius { get; private set; }

    /// <summary>The delivery matched the order and the delivery note. Null: not checked.</summary>
    public bool? ReceptionMatchesOrder { get; private set; }
    public bool? ReceptionPackagingIntact { get; private set; }

    /// <summary>Label and instructions for use in French.</summary>
    public bool? ReceptionFrenchLabelling { get; private set; }
    public bool? CertificateOfAnalysisReceived { get; private set; }

    /// <summary>Released although the QC on the lot did not conform, by a biologist's derogation.</summary>
    /// <summary>
    /// Shortest derogation justification accepted. One constant for the rule and the prompt that
    /// states it: the prompt said 10 while the dialog demanded 15.
    /// </summary>
    public const int QcOverrideJustificationMinLength = 15;

    public bool ReleasedWithQcOverride { get; private set; }
    public string? QcOverrideJustification { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    // ---- Behaviour -----------------------------------------------------------------------------

    /// <summary>Keeps what was checked when the lot arrived, as the reception record.</summary>
    public void RecordReception(ReceptionCheck check)
    {
        ArrivalTemperatureCelsius = check.ArrivalTemperatureCelsius;
        ReceptionMatchesOrder = check.MatchesOrder;
        ReceptionPackagingIntact = check.PackagingIntact;
        ReceptionFrenchLabelling = check.FrenchLabelling;
        CertificateOfAnalysisReceived = check.CertificateOfAnalysisReceived;
    }

    /// <summary>
    /// Releases the lot although its QC did not conform. Only the derogation is recorded here; who
    /// may grant it is decided by the service.
    /// </summary>
    public Result ReleaseOverridingQc(DateOnly on, Guid byUserId, string evidence, string justification)
    {
        if (string.IsNullOrWhiteSpace(justification))
            return Result.Failure(Error.Regulatory("Lot_QcOverrideJustificationRequired"));

        if (justification.Trim().Length < QcOverrideJustificationMinLength)
            return Result.Failure(Error.Validation("Lot_QcOverrideJustificationTooShort",
                                                   QcOverrideJustificationMinLength));

        var released = Release(on, byUserId, evidence);
        if (released.IsFailure) return released;

        ReleasedWithQcOverride = true;
        QcOverrideJustification = justification.Trim();
        return Result.Success();
    }

    public bool IsExpired(DateOnly today) => ExpiryDate < today;

    /// <summary>
    /// The unit on the bench (the latest one issued) is past its stability after opening. Lots
    /// opened before <see cref="LastOpenedOn"/> was kept count from their first opening.
    /// </summary>
    public bool IsBeyondOpenStability(DateOnly today) =>
        (LastOpenedOn ?? OpenedOn) is { } opened && StabilityAfterOpeningDays is { } days &&
        today > opened.AddDays(days);

    /// <summary>Usable on the analyser: released, in date, and the unit on the bench still stable.</summary>
    public bool IsUsable(DateOnly today) => IsIssuable(today) && !IsBeyondOpenStability(today);

    /// <summary>
    /// Units may still be issued from it. The stock left on the shelf is sealed, so the stability
    /// after opening of the unit already on the bench does not stop a fresh one being issued.
    /// </summary>
    public bool IsIssuable(DateOnly today) =>
        !IsQuarantined && !IsRejected && !IsRecalled && !IsExpired(today);

    /// <summary>A quarantined lot that has never been released (or whose release was withdrawn).</summary>
    public bool IsAwaitingRelease => IsQuarantined && !IsRejected && ReleasedOn is null;

    /// <summary>
    /// Records that this lot, still awaiting release, is loaded on <paramref name="instrumentId"/>
    /// for its acceptance QC. False when the lot is no longer awaiting release.
    /// </summary>
    public bool StartEvaluationOn(Guid instrumentId)
    {
        if (!IsAwaitingRelease) return false;

        // Loaded on another analyser: a run started on the previous one no longer stands.
        if (EvaluationInstrumentId != instrumentId) EvaluationStartedAt = null;

        EvaluationInstrumentId = instrumentId;
        return true;
    }

    /// <summary>
    /// Starts an evaluation run of this lot on <paramref name="instrumentId"/> at
    /// <paramref name="at"/>: the next control of each level the analyser transmits today for the
    /// lot's analyses is credited to the lot (see <see cref="EvaluationStartedAt"/>). Started again
    /// for each further run. False when the lot is no longer awaiting release.
    /// </summary>
    public bool StartEvaluationOn(Guid instrumentId, DateTimeOffset at)
    {
        if (!StartEvaluationOn(instrumentId)) return false;
        EvaluationStartedAt = at;
        return true;
    }

    /// <summary>Ends the evaluation: the analyser's transmissions are routine QC again.</summary>
    public void StopEvaluation()
    {
        EvaluationInstrumentId = null;
        EvaluationStartedAt = null;
    }

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
        StopEvaluation();
        return Result.Success();
    }

    public Result Reject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Lot_RejectionReasonRequired"));

        IsRejected = true;
        RejectionReason = reason;
        IsQuarantined = true;
        StopEvaluation();
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
        ReleasedWithQcOverride = false;
        QcOverrideJustification = null;

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
        if (!IsIssuable(today))
            return Result.Failure(Error.Regulatory("Lot_NotUsable", LotNumber));
        if (quantity > QuantityRemaining)
            return Result.Failure(Error.Validation("Lot_InsufficientQuantity", QuantityRemaining));

        QuantityRemaining -= quantity;
        OpenedOn ??= today;
        LastOpenedOn = today;
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

    /// <summary>
    /// Adds a further delivery of the same manufacturer lot: the rest of a partial delivery, or a
    /// standing order still on the lot. It is the same lot, so its release and QC stand; a lot
    /// refused or recalled takes no more stock. The unit price becomes the average of what is on
    /// the shelf.
    /// </summary>
    public Result ReceiveMore(decimal quantity, DateOnly expiryDate, decimal? unitPriceMad)
    {
        var allowed = CanReceiveMore(quantity, expiryDate);
        if (allowed.IsFailure) return allowed;

        if (unitPriceMad is { } price)
        {
            UnitPriceMad = UnitPriceMad is { } current && QuantityRemaining > 0m
                ? Money.Round((current * QuantityRemaining + price * quantity) / (QuantityRemaining + quantity))
                : price;
        }

        QuantityReceived += quantity;
        QuantityRemaining += quantity;
        return Result.Success();
    }

    /// <summary>
    /// Whether <see cref="ReceiveMore"/> would take the delivery, without changing anything: a
    /// reception checks it before it counts the quantity against the order.
    /// </summary>
    public Result CanReceiveMore(decimal quantity, DateOnly expiryDate)
    {
        if (quantity <= 0m)
            return Result.Failure(Error.Validation("Lot_QuantityMustBePositive"));
        if (IsRejected || IsRecalled)
            return Result.Failure(Error.Conflict("Lot_ReceiveIntoRefusedLot", LotNumber));
        if (expiryDate != ExpiryDate)
            return Result.Failure(Error.Validation("Lot_ReceiveExpiryDiffers", LotNumber, ExpiryDate));
        return Result.Success();
    }

    /// <summary>Writes off the remaining quantity, e.g. on expiry.</summary>
    public decimal WriteOffRemainder()
    {
        var written = QuantityRemaining;
        QuantityRemaining = 0m;
        return written;
    }

    /// <summary>
    /// Applies the difference an inventory count found (counted less expected) to the quantity on
    /// the lot, whatever its state: a count records what is on the shelf, usable or not. Movements
    /// made while the count was running stay applied. Never below zero; returns the change made.
    /// </summary>
    public decimal ApplyCountVariance(decimal variance)
    {
        var before = QuantityRemaining;
        QuantityRemaining = Math.Max(0m, QuantityRemaining + variance);
        return QuantityRemaining - before;
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

    /// <summary>
    /// The order a receipt was delivered on. A lot can be received more than once (a backorder of
    /// the same manufacturer lot), each time on its own order: the receipts, not the lot, date them.
    /// </summary>
    public Guid? PurchaseOrderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    /// <summary>Signed effect on stock, for running-balance queries.</summary>
    public decimal SignedQuantity => Signed(Kind, Quantity);

    /// <summary>What comes in adds, everything else takes away.</summary>
    public static decimal Signed(StockMovementKind kind, decimal quantity) => kind switch
    {
        StockMovementKind.Receipt or StockMovementKind.Return or StockMovementKind.PositiveAdjustment
            or StockMovementKind.TransferIn => quantity,
        _ => -quantity
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
