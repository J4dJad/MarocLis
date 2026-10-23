using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>The rules of the purchase order list that need no window.</summary>
public static class PurchaseOrderRules
{
    /// <summary>
    /// A priority order still awaited: listed first and marked, until it is received or cancelled.
    /// </summary>
    public static bool IsPendingPriority(PurchaseOrderRow row) =>
        row.IsPriority && row.Status is not (PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled);
}
