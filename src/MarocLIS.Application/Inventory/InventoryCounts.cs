using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Inventory;

/// <summary>An inventory in the list.</summary>
public sealed record InventoryCountRow(
    Guid InventoryCountId,
    string Number,
    DateOnly CountDate,
    InventoryCountScope Scope,
    string? StorageLocation,
    StockItemKind? Category,
    InventoryCountStatus Status,
    int Lines,
    int Counted,
    int WithVariance,
    DateTimeOffset? ClosedAt);

/// <summary>One lot of a count sheet, with the item's name and unit for the printout.</summary>
public sealed record InventoryCountLineRow(
    Guid LineId,
    int LineNumber,
    Guid StockItemId,
    string ItemCode,
    string ItemName,
    string UnitOfMeasure,
    Guid ReagentLotId,
    string LotNumber,
    DateOnly ExpiryDate,
    string? StorageLocation,
    decimal ExpectedQuantity,
    decimal? CountedQuantity,
    decimal? Variance,
    string? Comment);

public sealed record InventoryCountDetail(InventoryCountRow Count, IReadOnlyList<InventoryCountLineRow> Lines);

public sealed record CreateInventoryCountRequest
{
    public required DateOnly CountDate { get; init; }
    public InventoryCountScope Scope { get; init; }
    public string? StorageLocation { get; init; }
    public StockItemKind? Category { get; init; }
    public string? Notes { get; init; }
}

public interface IInventoryCountQueries
{
    Task<IReadOnlyList<InventoryCountRow>> GetCountsAsync(Guid siteId, bool includeClosed,
                                                         CancellationToken ct = default);

    Task<InventoryCountDetail?> GetDetailAsync(Guid inventoryCountId, CancellationToken ct = default);

    /// <summary>The site's lots with stock left, rejected and recalled ones included: they are on the shelf too.</summary>
    Task<IReadOnlyList<(InventoryCountLineInput Lot, StockItemKind Kind)>> GetCountableLotsAsync(
        Guid siteId, CancellationToken ct = default);

    /// <summary>The storage locations in use, for the scope picker.</summary>
    Task<IReadOnlyList<string>> GetStorageLocationsAsync(Guid siteId, CancellationToken ct = default);
}

/// <summary>
/// Periodic inventories. Whoever may close counts opens them and closes them; counting the shelves
/// is the technicians' work, with the reception right. Closing posts one adjustment per lot that
/// differs, with "Inventaire n°" as its reason, through the same movements as a manual adjustment.
/// </summary>
public sealed class InventoryCountService(
    IRepository<InventoryCount> counts,
    IRepository<StockMovement> movements,
    IInventoryCountQueries queries,
    IInventoryQueries inventoryQueries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<InventoryCountRow>>> GetCountsAsync(bool includeClosed,
                                                                              CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Inventory.Receive, Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<InventoryCountRow>>(permitted.FirstError);

        return Result.Success(await queries.GetCountsAsync(currentUser.SiteId, includeClosed, ct));
    }

    public async Task<Result<InventoryCountDetail>> GetDetailAsync(Guid inventoryCountId, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Inventory.Receive, Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return Result.Failure<InventoryCountDetail>(permitted.FirstError);

        var detail = await queries.GetDetailAsync(inventoryCountId, ct);
        return detail is null
            ? Result.Failure<InventoryCountDetail>(Error.NotFound("InventoryCount_NotFound"))
            : Result.Success(detail);
    }

    public async Task<Result<IReadOnlyList<string>>> GetStorageLocationsAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Inventory.Receive, Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<string>>(permitted.FirstError);

        return Result.Success(await queries.GetStorageLocationsAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<string>> CreateAsync(CreateInventoryCountRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.InventoryCount, null, request.CountDate.Year, ct);
        if (number.IsFailure) return Result.Failure<string>(number.Errors);

        var count = new InventoryCount(number.Value, currentUser.SiteId, request.CountDate, InventoryCountScope.All,
                                       currentUser.UserId)
        {
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        };

        var scoped = count.SetScope(request.Scope, request.StorageLocation, request.Category);
        if (scoped.IsFailure) return Result.Failure<string>(scoped.Errors);

        await counts.AddAsync(count, ct);
        unitOfWork.AuditReason = $"Ouverture de l'inventaire {count.Number}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(count.Number);
    }

    /// <summary>Puts the lots in scope on the sheet with the quantities the stock shows now.</summary>
    public async Task<Result<int>> StartCountingAsync(Guid inventoryCountId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        var count = await counts.GetByIdAsync(inventoryCountId, ct);
        if (count is null) return Result.Failure<int>(Error.NotFound("InventoryCount_NotFound"));

        var lots = await queries.GetCountableLotsAsync(currentUser.SiteId, ct);
        var started = count.StartCounting(lots.Where(l => count.Covers(l.Lot.StorageLocation, l.Kind))
                                              .Select(l => l.Lot), clock.Now);
        if (started.IsFailure) return Result.Failure<int>(started.Errors);

        unitOfWork.AuditReason = $"Début du comptage de l'inventaire {count.Number} ({count.Lines.Count} lots)";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(count.Lines.Count);
    }

    public async Task<Result> RecordCountAsync(Guid inventoryCountId, Guid lineId, decimal counted, string? comment,
                                               CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Inventory.Receive, Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return permitted;

        var count = await counts.GetByIdAsync(inventoryCountId, ct);
        if (count is null) return Result.Failure(Error.NotFound("InventoryCount_NotFound"));

        var recorded = count.RecordCount(lineId, counted, currentUser.UserId, clock.Now, comment);
        if (recorded.IsFailure) return recorded;

        var line = count.Lines.First(l => l.Id == lineId);
        unitOfWork.AuditReason = $"Comptage {count.Number} : lot {line.LotNumber} ({line.ItemCode}) = {counted:0.###}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // The count is tracked from the read: its new lines and changes are found at the save. Update
    // would take the new lines, whose keys are set in the domain, for existing rows.

    /// <summary>
    /// Closes the count and posts the differences: a positive or negative adjustment per lot that
    /// differs, reason "Inventaire n°". Returns the number of adjustments posted.
    /// </summary>
    public async Task<Result<int>> CloseAsync(Guid inventoryCountId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        var count = await counts.GetByIdAsync(inventoryCountId, ct);
        if (count is null) return Result.Failure<int>(Error.NotFound("InventoryCount_NotFound"));

        var closed = count.Close(currentUser.UserId, clock.Now);
        if (closed.IsFailure) return Result.Failure<int>(closed.Errors);

        var posted = 0;
        foreach (var line in count.Lines.Where(l => l.Variance is not null and not 0m))
        {
            var lot = await inventoryQueries.GetLotEntityAsync(line.ReagentLotId, ct);
            if (lot is null) continue;

            var applied = lot.ApplyCountVariance(line.Variance!.Value);
            if (applied == 0m) continue;

            var movement = new StockMovement(lot.StockItemId, lot.Id,
                                             applied > 0m ? StockMovementKind.PositiveAdjustment
                                                          : StockMovementKind.NegativeAdjustment,
                                             Math.Abs(applied), clock.Now, currentUser.UserId, currentUser.SiteId)
            {
                BalanceAfter = lot.QuantityRemaining,
                Reason = count.AdjustmentReason,
                Reference = count.Number
            };

            await movements.AddAsync(movement, ct);
            line.AdjustmentMovementId = movement.Id;
            posted++;
        }

        unitOfWork.AuditReason = $"Clôture de l'inventaire {count.Number} : {posted} ajustement(s) de stock";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(posted);
    }

    public async Task<Result> CancelAsync(Guid inventoryCountId, string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure(Error.Validation("InventoryCount_CancelReasonRequired"));

        var count = await counts.GetByIdAsync(inventoryCountId, ct);
        if (count is null) return Result.Failure(Error.NotFound("InventoryCount_NotFound"));

        var cancelled = count.Cancel();
        if (cancelled.IsFailure) return cancelled;

        unitOfWork.AuditReason = $"Annulation de l'inventaire {count.Number} : {reason.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
