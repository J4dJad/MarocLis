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
/// <param name="ExpectedQuantity">Frozen at the start; for information once stock has moved.</param>
/// <param name="StockAtCount">What the stock showed when the shelf was counted: the variance is counted less this.</param>
/// <param name="CountedAt">When the shelf was counted.</param>
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
    string? Comment,
    decimal? StockAtCount = null,
    DateTimeOffset? CountedAt = null);

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

    /// <summary>
    /// The quantity the stock showed for a lot at a time: what it shows now, read afresh, less the
    /// movements recorded after that time. The reference a count of the shelf at that time is
    /// measured against.
    /// </summary>
    Task<decimal?> GetLotQuantityAtAsync(Guid reagentLotId, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>The lots on the site's other counts still being counted, with the count's number.</summary>
    Task<IReadOnlyList<(Guid ReagentLotId, string CountNumber)>> GetLotsInOpenCountsAsync(
        Guid siteId, Guid exceptInventoryCountId, CancellationToken ct = default);

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

        // A count records what is on the shelf; a future date counted stock that does not exist yet.
        if (request.CountDate > clock.Today)
            return Result.Failure<string>(Error.Validation("InventoryCount_DateInFuture"));

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

        var lots = (await queries.GetCountableLotsAsync(currentUser.SiteId, ct))
                   .Where(l => count.Covers(l.Lot.StorageLocation, l.Kind)).Select(l => l.Lot).ToList();

        // A lot on two open counts would have its difference posted by each of them at closing.
        var elsewhere = await queries.GetLotsInOpenCountsAsync(currentUser.SiteId, count.Id, ct);
        var overlap = elsewhere.Where(e => lots.Any(l => l.ReagentLotId == e.ReagentLotId))
                               .Select(e => e.CountNumber).Distinct().Order().ToList();
        if (overlap.Count > 0)
            return Result.Failure<int>(Error.Conflict("InventoryCount_LotsInOpenCount", string.Join(", ", overlap)));

        var started = count.StartCounting(lots, clock.Now);
        if (started.IsFailure) return Result.Failure<int>(started.Errors);

        unitOfWork.AuditReason = $"Début du comptage de l'inventaire {count.Number} ({count.Lines.Count} lots)";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(count.Lines.Count);
    }

    /// <param name="shelfCountedAt">
    /// When the shelf was counted, for a figure keyed in from the paper sheet. Left out: the time a
    /// counted line already has (a correction or a comment keeps its reference), else now.
    /// </param>
    public async Task<Result> RecordCountAsync(Guid inventoryCountId, Guid lineId, decimal counted, string? comment,
                                               DateTimeOffset? shelfCountedAt = null, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Inventory.Receive, Permissions.Inventory.CloseCounts);
        if (permitted.IsFailure) return permitted;

        var count = await counts.GetByIdAsync(inventoryCountId, ct);
        if (count is null) return Result.Failure(Error.NotFound("InventoryCount_NotFound"));

        // What the stock showed when the shelf was counted, read afresh: issues made since the start
        // are already off the shelf, and must not be taken off the lot again at closing; issues made
        // after the shelf was counted, before the sheet was keyed in, must not be added back.
        var now = clock.Now;
        var countedLine = count.Lines.FirstOrDefault(l => l.Id == lineId);
        var stockAtCount = countedLine is null
            ? null
            : await queries.GetLotQuantityAtAsync(countedLine.ReagentLotId,
                                                  count.ShelfTimeFor(countedLine, now, shelfCountedAt), ct);

        var recorded = count.RecordCount(lineId, counted, currentUser.UserId, now, comment, stockAtCount, shelfCountedAt);
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

        // Two counts of the same lot would each post their difference. Starting a count refuses
        // that, but counts already open together (opened before the check existed) must not both close.
        var elsewhere = await queries.GetLotsInOpenCountsAsync(count.SiteId, count.Id, ct);
        var overlap = elsewhere.Where(e => count.Lines.Any(l => l.ReagentLotId == e.ReagentLotId))
                               .Select(e => e.CountNumber).Distinct().Order().ToList();
        if (overlap.Count > 0 && count.Status == InventoryCountStatus.Counting)
            return Result.Failure<int>(Error.Conflict("InventoryCount_LotsInOtherOpenCount", string.Join(", ", overlap)));

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

        unitOfWork.AuditReason = PluralText.French("Clôture de l'inventaire {0} : {1} {1:plural:ajustement|ajustements} de stock", count.Number, posted);
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
