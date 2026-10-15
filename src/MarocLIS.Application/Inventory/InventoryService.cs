using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>A stock item with its computed position.</summary>
public sealed record StockRow(
    Guid StockItemId,
    string Code,
    MultilingualText Name,
    StockItemKind Kind,
    string UnitOfMeasure,
    decimal QuantityOnHand,
    decimal ReorderPoint,
    decimal MinimumStock,
    bool NeedsReorder,
    bool BelowMinimum,
    bool IsCritical,
    string? SupplierName,
    StorageCondition StorageCondition,
    int LotCount,
    int QuarantinedLotCount,
    DateOnly? EarliestExpiry,
    int? DaysToEarliestExpiry);

/// <summary>A received lot, with the facts that decide whether it may be used.</summary>
public sealed record LotRow(
    Guid ReagentLotId,
    Guid StockItemId,
    string ItemCode,
    MultilingualText ItemName,
    string LotNumber,
    DateOnly ExpiryDate,
    int DaysToExpiry,
    decimal QuantityReceived,
    decimal QuantityRemaining,
    DateOnly ReceivedOn,
    DateOnly? OpenedOn,
    bool IsQuarantined,
    bool IsRejected,
    bool IsRecalled,
    bool IsExpired,
    bool IsBeyondOpenStability,
    bool IsUsable,
    string? StorageLocation,
    string? SupplierName);

public sealed record ReceiveLotRequest
{
    public required Guid StockItemId { get; init; }
    public required string LotNumber { get; init; }
    public required DateOnly ExpiryDate { get; init; }
    public required decimal Quantity { get; init; }
    public Guid? SupplierId { get; init; }
    public string? DeliveryNoteReference { get; init; }
    public decimal? UnitPriceMad { get; init; }
    public string? StorageLocation { get; init; }
    public int? StabilityAfterOpeningDays { get; init; }

    /// <summary>Temperature and checklist at the door; the temperature is required for cold-chain items.</summary>
    public ReceptionCheck? Reception { get; init; }
}

/// <summary>
/// Reagent and consumable stock.
/// <para>
/// Three behaviours here exist for traceability rather than for stock accuracy, and are the reason
/// this is not a generic inventory module:
/// </para>
/// <list type="bullet">
/// <item>A newly received lot is <b>quarantined by default</b>. ISO 15189 §5.3.2.3 requires
/// verification that a new lot performs acceptably before it touches a patient sample, so the
/// default is "not usable" rather than "usable unless somebody objects".</item>
/// <item>Consumption is <b>first-expiry-first-out</b>, not first-in-first-out. FEFO is what stops
/// a laboratory discarding usable reagent while an older tin expires behind it.</item>
/// <item>Every movement is an immutable row. The quantity on a lot is a cached projection of
/// those rows, which is what makes a stock count explicable rather than merely wrong.</item>
/// </list>
/// </summary>
public sealed class InventoryService(
    IRepository<StockItem> items,
    IRepository<StockMovement> movements,
    IRepository<Supplier> suppliers,
    IInventoryQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock,
    ReorderPlanner? reorder = null,
    ReceptionService? reception = null,
    IPurchasingQualityQueries? qualityQueries = null)
{
    /// <summary>
    /// What the last stock operation drafted on the priority purchase orders, so the screen can say
    /// so. Empty when nothing reached its reorder point.
    /// </summary>
    public ReorderOutcome LastReorder { get; private set; } = ReorderOutcome.None;

    /// <summary>
    /// Drafts the priority purchase orders for every item at or below its reorder point. Run after
    /// each operation that lowers the stock, and when the stock screen opens, so that lots expiring
    /// overnight are caught too.
    /// </summary>
    public async Task<ReorderOutcome> RaiseReorderDraftsAsync(CancellationToken ct = default)
    {
        LastReorder = reorder is null ? ReorderOutcome.None : await reorder.RaiseDraftsAsync(ct);
        return LastReorder;
    }

    public async Task<Result<IReadOnlyList<StockRow>>> GetStockAsync(
        bool onlyAlerts = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<StockRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetStockAsync(currentUser.SiteId, onlyAlerts, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<LotRow>>> GetLotsAsync(
        Guid? stockItemId = null, bool onlyQuarantined = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<LotRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetLotsAsync(currentUser.SiteId, stockItemId, onlyQuarantined,
                                        clock.Today, ct));
    }

    /// <summary>
    /// Records a delivery. The lot lands quarantined; it takes an explicit release, with evidence,
    /// before anything can be consumed from it.
    /// </summary>
    public async Task<Result<Guid>> ReceiveLotAsync(ReceiveLotRequest request,
                                                     CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Receive);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(request.LotNumber))
            return Result.Failure<Guid>(Error.Validation("Lot_NumberRequired"));

        if (request.Quantity <= 0m)
            return Result.Failure<Guid>(Error.Validation("Lot_QuantityMustBePositive"));

        if (request.ExpiryDate < clock.Today)
            return Result.Failure<Guid>(Error.Validation("Lot_AlreadyExpired", request.ExpiryDate));

        var item = await items.GetByIdWithDetailsAsync(request.StockItemId, ct);
        if (item is null) return Result.Failure<Guid>(Error.NotFound("Stock_ItemNotFound"));

        // The same manufacturer lot delivered again: the quantity joins the lot already on the
        // shelf, which keeps its release and its QC.
        var existing = item.Lots.FirstOrDefault(l => string.Equals(l.LotNumber, request.LotNumber.Trim(),
                                                                   StringComparison.OrdinalIgnoreCase));

        // Temperature against the storage condition, and the checklist: a cold-chain item without
        // a temperature is refused, a failed check is received (quarantined) and reported.
        var assessment = ReceptionAssessment.Assess(item.StorageCondition, request.Reception);
        if (assessment.IsFailure) return Result.Failure<Guid>(assessment.Errors);

        // A failed check keeps a new lot in quarantine; it cannot be mixed into a released one.
        if (existing is { IsAwaitingRelease: false } && assessment.Value.NeedsNonConformity)
            return Result.Failure<Guid>(Error.Conflict("Lot_ReceiveNonConformingIntoReleased", existing.LotNumber));

        await using var transaction = assessment.Value.NeedsNonConformity && reception is not null
            ? await unitOfWork.BeginTransactionAsync(ct)
            : null;

        ReagentLot lot;
        if (existing is not null)
        {
            var added = existing.ReceiveMore(request.Quantity, request.ExpiryDate, request.UnitPriceMad);
            if (added.IsFailure) return Result.Failure<Guid>(added.Errors);
            lot = existing;
        }
        else
        {
            lot = item.AddLot(new ReagentLot(request.LotNumber.Trim(), request.ExpiryDate,
                                             request.Quantity, clock.Today)
            {
                SupplierId = request.SupplierId ?? item.PreferredSupplierId,
                DeliveryNoteReference = request.DeliveryNoteReference,
                UnitPriceMad = request.UnitPriceMad,
                StorageLocation = request.StorageLocation ?? item.DefaultStorageLocation,
                StabilityAfterOpeningDays = request.StabilityAfterOpeningDays,
                ReceivedByUserId = currentUser.UserId
            });

            if (request.Reception is { } check) lot.RecordReception(check);
        }

        if (request.UnitPriceMad is { } price) item.LastUnitPriceMad = price;

        if (assessment.Value.NeedsNonConformity && reception is not null)
        {
            var opened = await reception.OpenReceptionNonConformityAsync(
                item, lot, request.SupplierId ?? lot.SupplierId, assessment.Value,
                request.Reception?.ArrivalTemperatureCelsius, ct);
            if (opened.IsFailure) return Result.Failure<Guid>(opened.Errors);
        }

        await movements.AddAsync(new StockMovement(item.Id, lot.Id, StockMovementKind.Receipt,
                                                    request.Quantity, clock.Now,
                                                    currentUser.UserId, currentUser.SiteId)
        {
            BalanceAfter = lot.QuantityRemaining,
            Reference = request.DeliveryNoteReference
        }, ct);

        unitOfWork.AuditReason =
            $"Réception du lot {lot.LotNumber} de {item.Code} ({request.Quantity} {item.UnitOfMeasure})";

        await unitOfWork.SaveChangesAsync(ct);
        if (transaction is not null) await unitOfWork.CommitAsync(ct);

        return Result.Success(lot.Id);
    }

    /// <summary>
    /// Releases a quarantined lot for use.
    /// <para>
    /// Evidence is mandatory and is stored: the question an assessor asks is "what showed this lot
    /// performed acceptably?", and "the storeman ticked a box" is not an answer.
    /// </para>
    /// </summary>
    public Task<Result> ReleaseLotAsync(Guid reagentLotId, string evidence,
                                        CancellationToken ct = default) =>
        ReleaseAsync(reagentLotId, evidence, null, ct);

    /// <summary>
    /// Releases a reagent lot whose QC does not conform, by derogation. Reserved to a biologist —
    /// the permission to validate results biologically, the act this derogation stands in for —
    /// with a written justification kept on the lot and in the audit trail.
    /// </summary>
    public Task<Result> ReleaseLotOverridingQcAsync(Guid reagentLotId, string evidence,
                                                    string justification, CancellationToken ct = default) =>
        ReleaseAsync(reagentLotId, evidence, justification, ct);

    private async Task<Result> ReleaseAsync(Guid reagentLotId, string evidence, string? overrideJustification,
                                            CancellationToken ct)
    {
        // A quality decision, taken by whoever reviews the controls, not by whoever unpacked the box.
        var permitted = guard.Require(Permissions.Quality.ReviewQc);
        if (permitted.IsFailure) return permitted;

        var lot = await queries.GetLotEntityAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure(Error.NotFound("Lot_NotFound", reagentLotId));

        // A reagent tied to analyses is released on QC run with it, not on a sentence: ISO 15189
        // §5.3.2.3. Consumables and unlinked items keep the evidence-only release.
        var verdict = qualityQueries is null
            ? null
            : await qualityQueries.GetLotQcEvidenceAsync(reagentLotId, clock.Today, ct) is { } qc
                ? LotQualityService.Verdict(qc)
                : null;

        if (verdict is { AllowsRelease: false })
        {
            if (overrideJustification is null)
                return Result.Failure(Error.Regulatory(verdict.ResourceKey, lot.LotNumber,
                                                       string.Join(", ", verdict.FailingLevels)));

            var biologist = guard.Require(Permissions.Results.BiologicalValidate);
            if (biologist.IsFailure)
                return Result.Failure(Error.Forbidden("Lot_QcOverrideRequiresBiologist"));

            var overridden = lot.ReleaseOverridingQc(clock.Today, currentUser.UserId, evidence,
                                                     overrideJustification);
            if (overridden.IsFailure) return overridden;

            unitOfWork.AuditReason =
                $"Libération du lot {lot.LotNumber} PAR DÉROGATION au CQ ({verdict.Kind}) : " +
                $"{overrideJustification.Trim()} — preuve : {evidence}";
            await unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }

        var released = lot.Release(clock.Today, currentUser.UserId, evidence);
        if (released.IsFailure) return released;

        unitOfWork.AuditReason = $"Libération du lot {lot.LotNumber} : {evidence}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> RejectLotAsync(Guid reagentLotId, string reason,
                                              CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Inventory.Receive, Permissions.Quality.ReviewQc);
        if (permitted.IsFailure) return permitted;

        var lot = await queries.GetLotEntityAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure(Error.NotFound("Lot_NotFound", reagentLotId));

        // Refusing a delivery still in quarantine is part of receiving it. Withdrawing a lot that
        // was released is a quality decision, taken by those who may release one.
        if (!lot.IsAwaitingRelease)
        {
            var review = guard.Require(Permissions.Quality.ReviewQc);
            if (review.IsFailure) return review;
        }

        var rejected = lot.Reject(reason);
        if (rejected.IsFailure) return rejected;

        unitOfWork.AuditReason = $"Refus du lot {lot.LotNumber} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Withdraws a rejection recorded against the wrong lot.
    /// <para>
    /// Rejection was previously a one-way door: <see cref="ReagentLot.Release"/> refuses a rejected
    /// lot for ever, so a mis-click wrote off usable reagent permanently. The lot returns to
    /// <b>quarantine</b>, not to service — the acceptance check it never had is still owed, and a
    /// lot that was once rejected is the last one that should skip it.
    /// </para>
    /// </summary>
    public async Task<Result> ClearLotRejectionAsync(Guid reagentLotId, string reason,
                                                      CancellationToken ct = default)
    {
        // Deliberately the adjust permission rather than receive: this reverses a quality decision,
        // which is a heavier act than recording a delivery.
        var permitted = guard.Require(Permissions.Inventory.Adjust);
        if (permitted.IsFailure) return permitted;

        var lot = await queries.GetLotEntityAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure(Error.NotFound("Lot_NotFound", reagentLotId));

        var cleared = lot.ClearRejection(reason);
        if (cleared.IsFailure) return cleared;

        unitOfWork.AuditReason =
            $"Refus du lot {lot.LotNumber} annulé, retour en quarantaine : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Records a manufacturer recall.
    /// <para>
    /// Returns the number of patient results produced with the lot, because that is the question a
    /// recall actually raises: which reports may have to be reviewed. Lot-level traceability exists
    /// for exactly this moment.
    /// </para>
    /// </summary>
    public async Task<Result<int>> RecallLotAsync(Guid reagentLotId, string recallReference,
                                                   CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Adjust);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(recallReference))
            return Result.Failure<int>(Error.Validation("Lot_RecallReferenceRequired"));

        var lot = await queries.GetLotEntityAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure<int>(Error.NotFound("Lot_NotFound", reagentLotId));

        lot.Recall(clock.Today, recallReference);

        var affected = await queries.CountResultsProducedWithLotAsync(reagentLotId, ct);

        unitOfWork.AuditReason =
            $"Rappel du lot {lot.LotNumber} ({recallReference}) — {affected} résultat(s) concerné(s)";

        await unitOfWork.SaveChangesAsync(ct);

        await RaiseReorderDraftsAsync(ct);

        return Result.Success(affected);
    }

    /// <summary>
    /// Issues stock to a bench or an instrument, consuming first-expiry-first-out.
    /// <para>
    /// The caller names an item, not a lot: choosing the lot is a policy decision, and leaving it
    /// to whoever is at the store cupboard is how usable reagent gets discarded while an older tin
    /// expires behind it.
    /// </para>
    /// </summary>
    public async Task<Result<string>> IssueAsync(Guid stockItemId, decimal quantity,
                                                  Guid? instrumentId = null, string? reason = null,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Issue);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        if (quantity <= 0m)
            return Result.Failure<string>(Error.Validation("Lot_QuantityMustBePositive"));

        var item = await items.GetByIdWithDetailsAsync(stockItemId, ct);
        if (item is null) return Result.Failure<string>(Error.NotFound("Stock_ItemNotFound"));

        var today = clock.Today;
        var remaining = quantity;
        var consumedFrom = new List<string>();

        // Walk the usable lots in expiry order, drawing down each in turn. A single issue can
        // legitimately span two lots when the first is nearly empty.
        while (remaining > 0m)
        {
            var lot = item.NextLotToUse(today);

            if (lot is null)
            {
                return Result.Failure<string>(
                    Error.Validation("Stock_InsufficientUsableStock",
                                      item.Code, item.QuantityOnHand(today)));
            }

            var draw = Math.Min(remaining, lot.QuantityRemaining);

            var consumed = lot.Consume(draw, today);
            if (consumed.IsFailure) return Result.Failure<string>(consumed.Errors);

            await movements.AddAsync(new StockMovement(item.Id, lot.Id, StockMovementKind.Issue,
                                                        draw, clock.Now, currentUser.UserId,
                                                        currentUser.SiteId)
            {
                BalanceAfter = lot.QuantityRemaining,
                InstrumentId = instrumentId,
                Reason = reason
            }, ct);

            consumedFrom.Add($"{lot.LotNumber} ({draw:N2})");
            remaining -= draw;
        }

        unitOfWork.AuditReason =
            $"Sortie de {quantity} {item.UnitOfMeasure} de {item.Code} — lot(s) {string.Join(", ", consumedFrom)}";

        await unitOfWork.SaveChangesAsync(ct);

        await RaiseReorderDraftsAsync(ct);

        return Result.Success(string.Join(", ", consumedFrom));
    }

    /// <summary>
    /// Adjusts a lot's quantity, or writes it off. Always requires a reason: an unexplained stock
    /// adjustment is indistinguishable from a loss.
    /// </summary>
    public async Task<Result> AdjustAsync(Guid reagentLotId, decimal quantity,
                                           StockMovementKind kind, string reason,
                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Adjust);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Stock_AdjustmentReasonRequired"));

        var lot = await queries.GetLotEntityAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure(Error.NotFound("Lot_NotFound", reagentLotId));

        decimal applied;

        switch (kind)
        {
            case StockMovementKind.ExpiryWriteOff:
            case StockMovementKind.DamageWriteOff:
                // Writes off whatever is left, which is what these two always mean in practice.
                applied = lot.WriteOffRemainder();
                break;

            case StockMovementKind.PositiveAdjustment:
            case StockMovementKind.Return:
                var restored = lot.Restore(quantity);
                if (restored.IsFailure) return restored;
                applied = quantity;
                break;

            default:
                var consumed = lot.Consume(quantity, clock.Today);
                if (consumed.IsFailure) return consumed;
                applied = quantity;
                break;
        }

        await movements.AddAsync(new StockMovement(lot.StockItemId, lot.Id, kind, applied,
                                                    clock.Now, currentUser.UserId,
                                                    currentUser.SiteId)
        {
            BalanceAfter = lot.QuantityRemaining,
            Reason = reason
        }, ct);

        unitOfWork.AuditReason = $"{kind} de {applied} sur le lot {lot.LotNumber} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        await RaiseReorderDraftsAsync(ct);

        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<Supplier>>> GetSuppliersAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<Supplier>>(permitted.FirstError);

        return Result.Success(await suppliers.ListAsync(s => s.IsActive, ct));
    }
}

/// <summary>An equipment record with its next due dates.</summary>
public sealed record EquipmentRow(
    Guid InstrumentId,
    string Code,
    MultilingualText Name,
    EquipmentKind Kind,
    EquipmentStatus Status,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    DateOnly? NextMaintenanceDue,
    DateOnly? NextCalibrationDue,
    DateOnly? NextVerificationDue,
    bool MaintenanceOverdue,
    bool CalibrationOverdue,
    bool ContractExpired,
    DateOnly? ContractValidUntil,
    string? LastStatusReason);

/// <summary>
/// Equipment: status, maintenance and calibration.
/// <para>
/// The consequential rule is that an instrument whose calibration has lapsed cannot produce
/// patient results and cannot be returned to service without recalibrating. That is enforced in
/// the domain (<see cref="Instrument.CanProduceResults"/> and
/// <see cref="Instrument.ReturnToService"/>) rather than here, because results produced on an
/// uncalibrated analyser are not defensible and a warning somebody can dismiss is not a control.
/// </para>
/// </summary>
public sealed class EquipmentService(
    IRepository<Instrument> instruments,
    IInventoryQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<EquipmentRow>>> GetEquipmentAsync(
        bool onlyDue = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<EquipmentRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetEquipmentAsync(currentUser.SiteId, onlyDue, clock.Today, ct));
    }

    public async Task<Result> RecordMaintenanceAsync(
        Guid instrumentId, MaintenanceKind kind, MultilingualText description,
        decimal? downtimeHours = null, string? partsReplaced = null,
        bool postCheckPassed = true, string? externalTechnician = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.RecordMaintenance);
        if (permitted.IsFailure) return permitted;

        var instrument = await instruments.GetByIdWithDetailsAsync(instrumentId, ct);
        if (instrument is null) return Result.Failure(Error.NotFound("Equipment_NotFound"));

        if (string.IsNullOrWhiteSpace(description.Fr))
            return Result.Failure(Error.Validation("Equipment_DescriptionRequired"));

        instrument.AddMaintenance(new MaintenanceRecord(kind, clock.Today, description)
        {
            PerformedByUserId = currentUser.UserId,
            ExternalTechnicianName = externalTechnician,
            DowntimeHours = downtimeHours,
            PartsReplaced = partsReplaced,
            PostInterventionCheckPassed = postCheckPassed
        });

        unitOfWork.AuditReason = $"Maintenance {kind} sur {instrument.Code}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Records a calibration.
    /// <para>
    /// The calibrator lot is required for a passing calibration: it is the traceability link to a
    /// reference material, and a calibration that cannot be traced is not a calibration as far as
    /// ISO 15189 §5.3.1.4 is concerned.
    /// </para>
    /// </summary>
    public async Task<Result> RecordCalibrationAsync(
        Guid instrumentId, Guid? testParameterId, CalibrationOutcome outcome,
        string? calibratorLotNumber, DateOnly? validUntil,
        decimal? slope = null, decimal? intercept = null, string? comment = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.Calibrate);
        if (permitted.IsFailure) return permitted;

        var instrument = await instruments.GetByIdWithDetailsAsync(instrumentId, ct);
        if (instrument is null) return Result.Failure(Error.NotFound("Equipment_NotFound"));

        if (outcome == CalibrationOutcome.Passed && string.IsNullOrWhiteSpace(calibratorLotNumber))
            return Result.Failure(Error.Regulatory("Calibration_CalibratorLotRequired"));

        instrument.AddCalibration(new CalibrationRecord(clock.Today, clock.Now, currentUser.UserId)
        {
            TestParameterId = testParameterId,
            Outcome = outcome,
            CalibratorLotNumber = calibratorLotNumber,
            ValidUntil = validUntil,
            Slope = slope,
            Intercept = intercept,
            Comment = comment
        });

        unitOfWork.AuditReason = $"Étalonnage {outcome} sur {instrument.Code}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> ChangeStatusAsync(Guid instrumentId, EquipmentStatus status,
                                                 string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.Manage);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Equipment_StatusReasonRequired"));

        var instrument = await instruments.GetByIdWithDetailsAsync(instrumentId, ct);
        if (instrument is null) return Result.Failure(Error.NotFound("Equipment_NotFound"));

        switch (status)
        {
            case EquipmentStatus.Operational:
                // Refused while a calibration is overdue: bringing an analyser back without
                // recalibrating is precisely the shortcut the guard exists to block.
                var returned = instrument.ReturnToService(clock.Today);
                if (returned.IsFailure) return returned;
                break;

            case EquipmentStatus.UnderMaintenance:
                instrument.PlaceUnderMaintenance(reason);
                break;

            case EquipmentStatus.OutOfService:
                instrument.PlaceOutOfService(reason);
                break;

            case EquipmentStatus.Decommissioned:
                instrument.Decommission(clock.Today, reason);
                break;

            default:
                return Result.Failure(Error.Validation("Equipment_StatusNotSettable", status));
        }

        unitOfWork.AuditReason = $"{instrument.Code} → {status} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}

/// <summary>Read-side queries for the inventory and equipment screens.</summary>
public interface IInventoryQueries
{
    Task<IReadOnlyList<StockRow>> GetStockAsync(Guid siteId, bool onlyAlerts, DateOnly today,
                                                 CancellationToken ct = default);

    Task<IReadOnlyList<LotRow>> GetLotsAsync(Guid siteId, Guid? stockItemId, bool onlyQuarantined,
                                              DateOnly today, CancellationToken ct = default);

    /// <summary>The tracked lot entity, for the operations that mutate it.</summary>
    Task<ReagentLot?> GetLotEntityAsync(Guid reagentLotId, CancellationToken ct = default);

    /// <summary>
    /// How many patient results were produced with a lot. The question a manufacturer recall
    /// actually raises, and the reason results carry a reagent-lot reference at all.
    /// </summary>
    Task<int> CountResultsProducedWithLotAsync(Guid reagentLotId, CancellationToken ct = default);

    Task<IReadOnlyList<EquipmentRow>> GetEquipmentAsync(Guid siteId, bool onlyDue, DateOnly today,
                                                         CancellationToken ct = default);
}
