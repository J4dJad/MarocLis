using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// A periodic inventory: the lots of a site (all of them, one storage location or one category)
/// counted on a day, compared with what the stock says, and the differences posted as adjustments
/// when the count is closed.
/// <para>
/// The expected quantities are frozen when counting starts. Stock keeps moving while the shelves
/// are counted — a technician still issues a box — so each line also keeps what the stock showed
/// when the shelf was counted, and closing applies the difference found (counted less that
/// quantity) to the lot as it stands then, rather than overwriting it with the count. Measured
/// against the quantity frozen at the start instead, an issue made before the shelf was counted
/// would be taken off the lot a second time.
/// </para>
/// <para>
/// The shelf is often counted on the printed sheet and keyed in hours later. The time the shelf
/// was counted is therefore recorded on the line (the time of entry unless the counter gives the
/// sheet's), and the reference is the stock at that time — not at the time of entry, which would
/// add back every issue made in between.
/// </para>
/// </summary>
public sealed class InventoryCount : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<InventoryCountLine> _lines = [];

    private InventoryCount() { }

    public InventoryCount(string number, Guid siteId, DateOnly countDate, InventoryCountScope scope,
                          Guid openedByUserId)
    {
        Number = number;
        SiteId = siteId;
        CountDate = countDate;
        Scope = scope;
        OpenedByUserId = openedByUserId;
        Status = InventoryCountStatus.Draft;
    }

    /// <summary>Sequential number, e.g. "INV/2026/0003", quoted on every adjustment it posts.</summary>
    public string Number { get; private set; } = string.Empty;

    public Guid SiteId { get; set; }
    public DateOnly CountDate { get; private set; }

    public InventoryCountScope Scope { get; private set; }

    /// <summary>The storage location counted, for <see cref="InventoryCountScope.StorageLocation"/>.</summary>
    public string? StorageLocation { get; private set; }

    /// <summary>The category counted, for <see cref="InventoryCountScope.Category"/>.</summary>
    public StockItemKind? Category { get; private set; }

    public InventoryCountStatus Status { get; private set; }

    public Guid OpenedByUserId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedByUserId { get; private set; }

    public string? Notes { get; set; }

    public IReadOnlyCollection<InventoryCountLine> Lines => _lines.AsReadOnly();

    public bool IsClosed => Status == InventoryCountStatus.Closed;

    /// <summary>The reason written on each adjustment the count posts.</summary>
    public string AdjustmentReason => $"Inventaire {Number}";

    public Result SetScope(InventoryCountScope scope, string? storageLocation, StockItemKind? category)
    {
        if (Status != InventoryCountStatus.Draft) return Result.Failure(Error.Conflict("InventoryCount_NotDraft"));

        if (scope == InventoryCountScope.StorageLocation && string.IsNullOrWhiteSpace(storageLocation))
            return Result.Failure(Error.Validation("InventoryCount_LocationRequired"));
        if (scope == InventoryCountScope.Category && category is null)
            return Result.Failure(Error.Validation("InventoryCount_CategoryRequired"));

        Scope = scope;
        StorageLocation = scope == InventoryCountScope.StorageLocation ? storageLocation!.Trim() : null;
        Category = scope == InventoryCountScope.Category ? category : null;
        return Result.Success();
    }

    /// <summary>Whether a lot falls within the count, by its location and its item's category.</summary>
    public bool Covers(string? lotLocation, StockItemKind kind) => Scope switch
    {
        InventoryCountScope.StorageLocation =>
            string.Equals(lotLocation?.Trim(), StorageLocation, StringComparison.OrdinalIgnoreCase),
        InventoryCountScope.Category => kind == Category,
        _ => true
    };

    /// <summary>Freezes the expected quantities of the lots in scope and opens the count sheet.</summary>
    public Result StartCounting(IEnumerable<InventoryCountLineInput> lots, DateTimeOffset at)
    {
        if (Status != InventoryCountStatus.Draft) return Result.Failure(Error.Conflict("InventoryCount_NotDraft"));

        var lines = lots.ToList();
        if (lines.Count == 0) return Result.Failure(Error.Validation("InventoryCount_NothingToCount"));

        _lines.Clear();
        var number = 0;
        foreach (var lot in lines.OrderBy(l => l.StorageLocation).ThenBy(l => l.ItemCode).ThenBy(l => l.ExpiryDate))
        {
            var line = new InventoryCountLine(lot.StockItemId, lot.ReagentLotId, lot.ItemCode, lot.LotNumber,
                                              lot.ExpiryDate, lot.ExpectedQuantity)
            {
                LineNumber = ++number,
                StorageLocation = lot.StorageLocation
            };
            line.AttachTo(Id);
            _lines.Add(line);
        }

        Status = InventoryCountStatus.Counting;
        StartedAt = at;
        return Result.Success();
    }

    /// <param name="at">When the count is entered.</param>
    /// <param name="stockAtCount">
    /// What the stock showed for the lot when the shelf was counted; the variance is measured
    /// against it. Left out, the quantity frozen at the start.
    /// </param>
    /// <param name="shelfCountedAt">
    /// When the shelf was counted, for a count keyed in from the paper sheet. Left out: the time
    /// already recorded on a counted line (a corrected figure or a comment keeps its reference),
    /// else <paramref name="at"/>.
    /// </param>
    public Result RecordCount(Guid lineId, decimal counted, Guid byUserId, DateTimeOffset at, string? comment,
                              decimal? stockAtCount = null, DateTimeOffset? shelfCountedAt = null)
    {
        if (Status != InventoryCountStatus.Counting) return Result.Failure(Error.Conflict("InventoryCount_NotCounting"));
        if (counted < 0m) return Result.Failure(Error.Validation("InventoryCount_NegativeQuantity"));

        var line = _lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return Result.Failure(Error.NotFound("InventoryCount_LineNotFound"));

        if (shelfCountedAt is { } shelf)
        {
            // A sheet time is written to the minute: within the minute the count started, it is the start.
            if (StartedAt is { } started && shelf < started)
            {
                if (started - shelf >= TimeSpan.FromMinutes(1))
                    return Result.Failure(Error.Validation("InventoryCount_CountedBeforeStart"));
                shelfCountedAt = started;
            }
            if (shelfCountedAt > at) return Result.Failure(Error.Validation("InventoryCount_CountedInFuture"));
        }

        line.Record(counted, byUserId, at, comment, stockAtCount, shelfCountedAt);
        return Result.Success();
    }

    /// <summary>
    /// When the shelf of a line is taken to have been counted, for an entry made at
    /// <paramref name="at"/>: the time given, else the one a counted line already has, else now.
    /// </summary>
    public DateTimeOffset ShelfTimeFor(InventoryCountLine line, DateTimeOffset at, DateTimeOffset? given)
    {
        var shelf = given ?? (line.CountedQuantity is not null ? line.CountedAt : null) ?? at;
        return StartedAt is { } started && shelf < started ? started : shelf;
    }

    /// <summary>
    /// Closes the count. Every line has to be counted: a line left blank would read as "nothing
    /// there" and write the lot off. The adjustments themselves are posted by the service, which
    /// holds the lots; it records each movement on its line.
    /// </summary>
    public Result Close(Guid byUserId, DateTimeOffset at)
    {
        if (Status != InventoryCountStatus.Counting) return Result.Failure(Error.Conflict("InventoryCount_NotCounting"));

        var missing = _lines.Count(l => l.CountedQuantity is null);
        if (missing > 0) return Result.Failure(Error.Validation("InventoryCount_LinesNotCounted", missing));

        Status = InventoryCountStatus.Closed;
        ClosedAt = at;
        ClosedByUserId = byUserId;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == InventoryCountStatus.Closed) return Result.Failure(Error.Conflict("InventoryCount_Closed"));
        Status = InventoryCountStatus.Cancelled;
        return Result.Success();
    }
}

/// <summary>A lot to put on the count sheet, with what the stock says is there.</summary>
public sealed record InventoryCountLineInput(
    Guid StockItemId,
    Guid ReagentLotId,
    string ItemCode,
    string LotNumber,
    DateOnly ExpiryDate,
    string? StorageLocation,
    decimal ExpectedQuantity);

/// <summary>One lot of the count sheet.</summary>
public sealed class InventoryCountLine : Entity
{
    private InventoryCountLine() { }

    internal InventoryCountLine(Guid stockItemId, Guid reagentLotId, string itemCode, string lotNumber,
                                DateOnly expiryDate, decimal expectedQuantity)
    {
        StockItemId = stockItemId;
        ReagentLotId = reagentLotId;
        ItemCode = itemCode;
        LotNumber = lotNumber;
        ExpiryDate = expiryDate;
        ExpectedQuantity = expectedQuantity;
    }

    public Guid InventoryCountId { get; private set; }
    internal void AttachTo(Guid countId) => InventoryCountId = countId;

    public int LineNumber { get; set; }

    public Guid StockItemId { get; private set; }
    public Guid ReagentLotId { get; private set; }

    /// <summary>Copied when counting starts, so the sheet reads the same after the item is renamed.</summary>
    public string ItemCode { get; private set; } = string.Empty;
    public string LotNumber { get; private set; } = string.Empty;
    public DateOnly ExpiryDate { get; private set; }
    public string? StorageLocation { get; set; }

    public decimal ExpectedQuantity { get; private set; }
    public decimal? CountedQuantity { get; private set; }

    /// <summary>
    /// What the stock showed for the lot when the shelf was counted. Issues made between the start
    /// and the count are already off the shelf and off the lot: the variance is taken against this.
    /// </summary>
    public decimal? StockAtCount { get; private set; }

    public Guid? CountedByUserId { get; private set; }

    /// <summary>
    /// When the shelf was counted: the time of entry, or the time written on the paper sheet the
    /// figure was keyed in from. <see cref="StockAtCount"/> is the stock at that time.
    /// </summary>
    public DateTimeOffset? CountedAt { get; private set; }
    public string? Comment { get; private set; }

    /// <summary>The adjustment posted for the difference at closing.</summary>
    public Guid? AdjustmentMovementId { get; set; }

    /// <summary>Counted less what the stock showed at the count; null until counted.</summary>
    public decimal? Variance => CountedQuantity is { } counted ? counted - (StockAtCount ?? ExpectedQuantity) : null;

    internal void Record(decimal counted, Guid byUserId, DateTimeOffset at, string? comment, decimal? stockAtCount,
                         DateTimeOffset? shelfCountedAt)
    {
        // A comment added or a figure corrected later is still the same count of the shelf: its
        // reference stays the stock at that count, not the stock at the new entry. Only a shelf
        // counted again (a time given that differs) takes a new reference.
        var sameCount = CountedQuantity is not null && StockAtCount is not null
                        && (shelfCountedAt is null || shelfCountedAt == CountedAt);
        if (!sameCount)
        {
            StockAtCount = stockAtCount ?? ExpectedQuantity;
            CountedAt = shelfCountedAt ?? (CountedQuantity is not null ? CountedAt : null) ?? at;
        }

        CountedQuantity = counted;
        CountedByUserId = byUserId;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
    }
}

public enum InventoryCountScope
{
    All = 0,
    StorageLocation = 1,
    Category = 2
}

public enum InventoryCountStatus
{
    Draft = 0,
    Counting = 1,
    Closed = 2,
    Cancelled = 3
}
