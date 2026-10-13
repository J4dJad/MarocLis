using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Inventory;

/// <summary>One purchase order in the register.</summary>
public sealed record PurchaseOrderRow(
    Guid Id,
    string Number,
    string SupplierName,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
    PurchaseOrderStatus Status,
    int LineCount,
    decimal TotalExcludingVat,
    decimal TotalIncludingVat,
    string RaisedByName,
    string? ApprovedByName,
    string? SupplierReference,
    bool IsOverdue,
    bool IsPriority = false,
    bool RaisedAutomatically = false);

/// <summary>One line of a purchase order, with what is still to be delivered.</summary>
public sealed record PurchaseOrderLineRow(
    Guid Id,
    int LineNumber,
    Guid StockItemId,
    string ItemCode,
    string Description,
    string UnitOfMeasure,
    decimal QuantityOrdered,
    decimal QuantityReceived,
    decimal QuantityOutstanding,
    decimal UnitPriceMad,
    decimal DiscountRate,
    decimal VatRate,
    decimal LineTotalExcludingVat);

/// <summary>A stock item that can be ordered, with the defaults a new line starts from.</summary>
public sealed record OrderableItem(
    Guid Id,
    string Code,
    MultilingualText Name,
    string UnitOfMeasure,
    Guid? PreferredSupplierId,
    decimal ReorderQuantity,
    decimal? LastUnitPriceMad);

public sealed record SupplierOption(Guid Id, string Code, string Name);

public sealed record CreatePurchaseOrderRequest
{
    public required Guid SupplierId { get; init; }
    public DateOnly? ExpectedDeliveryDate { get; init; }
    public string? SupplierReference { get; init; }
    public string? Notes { get; init; }
}

public sealed record AddPurchaseOrderLineRequest
{
    public required Guid StockItemId { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitPriceMad { get; init; }
    public decimal DiscountRate { get; init; }
    public decimal VatRate { get; init; } = 0.20m;
}

/// <summary>A delivery against one line. The goods arrive as a lot, so the lot facts come with it.</summary>
public sealed record ReceivePurchaseOrderLineRequest
{
    public required Guid LineId { get; init; }
    public required decimal Quantity { get; init; }
    public required string LotNumber { get; init; }
    public required DateOnly ExpiryDate { get; init; }
    public string? DeliveryNoteReference { get; init; }
    public string? StorageLocation { get; init; }
}

/// <summary>
/// Purchase orders (bons de commande).
/// <para>
/// An order is drafted, approved by someone other than the person who raised it, sent, then
/// received line by line. Receiving a line also receives the lot into stock — quarantined, like
/// any other delivery — so the storeman does not type the same delivery twice and the stock
/// screen and the order agree on what arrived.
/// </para>
/// </summary>
public sealed class PurchaseOrderService(
    IRepository<PurchaseOrder> orders,
    IRepository<StockItem> items,
    IRepository<StockMovement> movements,
    IPurchaseOrderQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<PurchaseOrderRow>>> GetOrdersAsync(
        bool onlyOpen = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<PurchaseOrderRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetOrdersAsync(currentUser.SiteId, onlyOpen, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<PurchaseOrderLineRow>>> GetLinesAsync(
        Guid purchaseOrderId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<PurchaseOrderLineRow>>(permitted.FirstError);

        return Result.Success(await queries.GetLinesAsync(purchaseOrderId, ct));
    }

    public async Task<Result<IReadOnlyList<SupplierOption>>> GetSuppliersAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<SupplierOption>>(permitted.FirstError);

        return Result.Success(await queries.GetSuppliersAsync(ct));
    }

    public async Task<Result<IReadOnlyList<OrderableItem>>> GetOrderableItemsAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<OrderableItem>>(permitted.FirstError);

        return Result.Success(await queries.GetOrderableItemsAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<string>> CreateAsync(CreatePurchaseOrderRequest request,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        if (request.ExpectedDeliveryDate is { } expected && expected < clock.Today)
            return Result.Failure<string>(Error.Validation("Po_ExpectedDateInPast"));

        // Only an approved supplier may be ordered from: supplier evaluation (ISO 15189 §6.8) is
        // pointless if an order can bypass it.
        if (!await queries.IsSupplierApprovedAsync(request.SupplierId, ct))
            return Result.Failure<string>(Error.Regulatory("Po_SupplierNotApproved"));

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        // One series for the laboratory, as pre-created by the seeder ("BC/{YEAR}/{SEQ:5}").
        var number = await sequences.NextAsync(SequencePurpose.PurchaseOrder, null,
                                               clock.Today.Year, ct);
        if (number.IsFailure) return Result.Failure<string>(number.FirstError);

        var order = new PurchaseOrder(number.Value, request.SupplierId, currentUser.SiteId,
                                      clock.Today, currentUser.UserId)
        {
            ExpectedDeliveryDate = request.ExpectedDeliveryDate,
            SupplierReference = Blank(request.SupplierReference),
            Notes = Blank(request.Notes)
        };

        await orders.AddAsync(order, ct);

        unitOfWork.AuditReason = $"Création du bon de commande {order.Number}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(order.Number);
    }

    public async Task<Result> AddLineAsync(Guid purchaseOrderId, AddPurchaseOrderLineRequest request,
                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return permitted;

        if (request.Quantity <= 0m)
            return Result.Failure(Error.Validation("Po_QuantityMustBePositive"));
        if (request.UnitPriceMad < 0m)
            return Result.Failure(Error.Validation("Po_PriceMustNotBeNegative"));
        if (request.DiscountRate is < 0m or >= 1m || request.VatRate is < 0m or > 1m)
            return Result.Failure(Error.Validation("Po_RateOutOfRange"));

        var order = await orders.GetByIdWithDetailsAsync(purchaseOrderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Po_NotFound"));

        var item = await items.GetByIdAsync(request.StockItemId, ct);
        if (item is null) return Result.Failure(Error.NotFound("Stock_ItemNotFound"));

        if (order.Lines.Any(l => l.StockItemId == item.Id))
            return Result.Failure(Error.Conflict("Po_ItemAlreadyOnOrder", item.Code));

        var added = order.AddLine(new PurchaseOrderLine(item.Id, request.Quantity, request.UnitPriceMad)
        {
            Description = $"{item.Code} — {item.Name.Fr}",
            DiscountRate = request.DiscountRate,
            VatRate = request.VatRate
        });
        if (added.IsFailure) return added;

        unitOfWork.AuditReason = $"Ligne {item.Code} ajoutée au bon de commande {order.Number}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public Task<Result> ApproveAsync(Guid purchaseOrderId, CancellationToken ct = default) =>
        ApplyAsync(purchaseOrderId, o => o.Approve(clock.Now, currentUser.UserId), "Approbation", ct,
                   Permissions.Inventory.ApprovePurchaseOrders);

    public Task<Result> SendAsync(Guid purchaseOrderId, CancellationToken ct = default) =>
        ApplyAsync(purchaseOrderId, o => o.Send(clock.Now), "Envoi", ct);

    public async Task<Result> CancelAsync(Guid purchaseOrderId, string reason,
                                          CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Po_ReasonRequired"));

        return await ApplyAsync(purchaseOrderId, o => o.Cancel(reason.Trim()),
                                $"Annulation ({reason.Trim()})", ct);
    }

    /// <summary>
    /// Records a delivery against a line and receives the lot into stock in the same save, so the
    /// order and the stock cannot disagree about what arrived.
    /// </summary>
    public async Task<Result> ReceiveLineAsync(Guid purchaseOrderId,
                                               ReceivePurchaseOrderLineRequest request,
                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Receive);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(request.LotNumber))
            return Result.Failure(Error.Validation("Lot_NumberRequired"));
        if (request.ExpiryDate < clock.Today)
            return Result.Failure(Error.Validation("Lot_AlreadyExpired", request.ExpiryDate));

        var order = await orders.GetByIdWithDetailsAsync(purchaseOrderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Po_NotFound"));

        var line = order.Lines.FirstOrDefault(l => l.Id == request.LineId);
        if (line is null) return Result.Failure(Error.NotFound("Po_LineNotFound"));

        var item = await items.GetByIdWithDetailsAsync(line.StockItemId, ct);
        if (item is null) return Result.Failure(Error.NotFound("Stock_ItemNotFound"));

        var lotNumber = request.LotNumber.Trim();
        if (item.Lots.Any(l => string.Equals(l.LotNumber, lotNumber, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure(Error.Conflict("Lot_AlreadyReceived", lotNumber));

        var received = order.ReceiveLine(line.Id, request.Quantity);
        if (received.IsFailure) return received;

        var deliveryNote = Blank(request.DeliveryNoteReference) ?? order.Number;

        var lot = item.AddLot(new ReagentLot(lotNumber, request.ExpiryDate, request.Quantity,
                                             clock.Today)
        {
            SupplierId = order.SupplierId,
            DeliveryNoteReference = deliveryNote,
            UnitPriceMad = line.UnitPriceMad,
            StorageLocation = Blank(request.StorageLocation) ?? item.DefaultStorageLocation,
            ReceivedByUserId = currentUser.UserId
        });
        item.LastUnitPriceMad = line.UnitPriceMad;

        await movements.AddAsync(new StockMovement(item.Id, lot.Id, StockMovementKind.Receipt,
                                                   request.Quantity, clock.Now,
                                                   currentUser.UserId, currentUser.SiteId)
        {
            BalanceAfter = lot.QuantityRemaining,
            Reference = deliveryNote
        }, ct);

        unitOfWork.AuditReason =
            $"Réception sur {order.Number} : lot {lotNumber} de {item.Code} ({request.Quantity} {item.UnitOfMeasure})";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    private async Task<Result> ApplyAsync(Guid purchaseOrderId, Func<PurchaseOrder, Result> step,
                                          string label, CancellationToken ct,
                                          string permission = Permissions.Inventory.ManagePurchaseOrders)
    {
        var permitted = guard.Require(permission);
        if (permitted.IsFailure) return permitted;

        var order = await orders.GetByIdWithDetailsAsync(purchaseOrderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Po_NotFound"));

        var done = step(order);
        if (done.IsFailure) return done;

        unitOfWork.AuditReason = $"{label} du bon de commande {order.Number}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Read-side queries for the purchase-order screen.</summary>
public interface IPurchaseOrderQueries
{
    Task<IReadOnlyList<PurchaseOrderRow>> GetOrdersAsync(Guid siteId, bool onlyOpen, DateOnly today,
                                                         CancellationToken ct = default);

    Task<IReadOnlyList<PurchaseOrderLineRow>> GetLinesAsync(Guid purchaseOrderId,
                                                            CancellationToken ct = default);

    Task<IReadOnlyList<SupplierOption>> GetSuppliersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrderableItem>> GetOrderableItemsAsync(Guid siteId,
                                                              CancellationToken ct = default);

    Task<bool> IsSupplierApprovedAsync(Guid supplierId, CancellationToken ct = default);

    /// <summary>
    /// Active items at or under their reorder point that no open order already covers.
    /// </summary>
    Task<IReadOnlyList<ReorderCandidate>> GetReorderCandidatesAsync(Guid siteId, DateOnly today,
                                                                     CancellationToken ct = default);

    /// <summary>The automatic draft still open for this supplier, to add to rather than start another.</summary>
    Task<Guid?> FindAutomaticDraftAsync(Guid siteId, Guid supplierId, CancellationToken ct = default);
}

/// <summary>An item that has fallen to its reorder point and is not on any open order.</summary>
public sealed record ReorderCandidate(
    Guid StockItemId,
    string Code,
    string UnitOfMeasure,
    Guid? PreferredSupplierId,
    bool SupplierApproved,
    decimal Usable,
    decimal ReorderPoint,
    decimal ReorderQuantity,
    decimal? MaximumStock,
    decimal? LastUnitPriceMad);
