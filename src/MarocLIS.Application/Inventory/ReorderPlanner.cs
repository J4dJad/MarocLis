using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Sequencing;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Application.Inventory;

/// <summary>What one pass of the reorder planner did.</summary>
public sealed record ReorderOutcome(
    IReadOnlyList<string> DraftedItems,
    IReadOnlyList<string> PurchaseOrders,
    IReadOnlyList<string> ItemsWithoutSupplier)
{
    public static readonly ReorderOutcome None = new([], [], []);
    public bool Any => DraftedItems.Count > 0;
}

/// <summary>
/// Drafts a priority purchase order as soon as an item falls to its reorder point.
/// <para>
/// The items go on one automatic draft per supplier, added to while it stays a draft, so a morning
/// of issues does not leave five orders to the same supplier. An item already on an open order is
/// left alone: its delivery is on the way. The draft is only a draft: a person still checks the
/// quantities, and approval stays with someone other than whoever raised it.
/// </para>
/// <para>
/// An item without an approved preferred supplier cannot be ordered automatically, since an order
/// needs a supplier and only an approved one may be used; it is reported instead, and stays in
/// the stock screen's alerts.
/// </para>
/// </summary>
public sealed class ReorderPlanner(
    IRepository<PurchaseOrder> orders,
    IPurchaseOrderQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<ReorderPlanner>? logger = null)
{
    public async Task<ReorderOutcome> RaiseDraftsAsync(CancellationToken ct = default)
    {
        try
        {
            var candidates = await queries.GetReorderCandidatesAsync(currentUser.SiteId, clock.Today, ct);
            if (candidates.Count == 0) return ReorderOutcome.None;

            var drafted = new List<string>();
            var numbers = new List<string>();
            var withoutSupplier = candidates
                .Where(c => c.PreferredSupplierId is null || !c.SupplierApproved)
                .Select(c => c.Code)
                .ToList();

            foreach (var group in candidates.Where(c => c.PreferredSupplierId is not null && c.SupplierApproved)
                                            .GroupBy(c => c.PreferredSupplierId!.Value))
            {
                var order = await DraftForAsync(group.Key, ct);
                if (order is null) continue;

                foreach (var item in group)
                {
                    if (order.Lines.Any(l => l.StockItemId == item.StockItemId)) continue;

                    var added = order.AddLine(new PurchaseOrderLine(item.StockItemId, Quantity(item),
                                                                    item.LastUnitPriceMad ?? 0m)
                    {
                        Description = item.Code
                    });

                    if (added.IsSuccess) drafted.Add(item.Code);
                }

                order.IsPriority = true;
                numbers.Add(order.Number);
            }

            if (drafted.Count == 0) return new ReorderOutcome([], [], withoutSupplier);

            unitOfWork.AuditReason =
                $"Bon de commande prioritaire préparé automatiquement (seuil de réapprovisionnement atteint) : {string.Join(", ", drafted)}";
            await unitOfWork.SaveChangesAsync(ct);

            return new ReorderOutcome(drafted, numbers.Distinct().ToList(), withoutSupplier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stock operation that triggered this has already been saved; failing to draft the
            // order must not turn it into an error. The stock screen still shows the alert.
            logger?.LogError(ex, "Drafting the reorder purchase orders failed.");
            return ReorderOutcome.None;
        }
    }

    private async Task<PurchaseOrder?> DraftForAsync(Guid supplierId, CancellationToken ct)
    {
        if (await queries.FindAutomaticDraftAsync(currentUser.SiteId, supplierId, ct) is { } existing)
            return await orders.GetByIdWithDetailsAsync(existing, ct);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.PurchaseOrder, null, clock.Today.Year, ct);
        if (number.IsFailure) return null;

        var order = new PurchaseOrder(number.Value, supplierId, currentUser.SiteId, clock.Today,
                                      currentUser.UserId)
        {
            IsPriority = true,
            RaisedAutomatically = true,
            Notes = "Préparé automatiquement : seuil de réapprovisionnement atteint."
        };

        await orders.AddAsync(order, ct);
        unitOfWork.AuditReason = $"Création automatique du bon de commande {order.Number}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return order;
    }

    // The quantity set on the item; failing that, back up to the maximum; failing that, twice the
    // reorder point. Never less than one unit.
    private static decimal Quantity(ReorderCandidate item)
    {
        if (item.ReorderQuantity > 0m) return item.ReorderQuantity;

        var target = item.MaximumStock ?? item.ReorderPoint * 2m;
        return Math.Max(1m, Math.Ceiling(target - item.Usable));
    }
}
