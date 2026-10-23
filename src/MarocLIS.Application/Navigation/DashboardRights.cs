using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Navigation;

/// <summary>
/// Who sees which figure of the dashboard, and which figures open a screen for them. The dashboard
/// is open to everyone signed in; what it shows is not.
/// </summary>
public static class DashboardRights
{
    /// <summary>
    /// The screens a figure can open. A figure offers its screen with the rights of the menu entry
    /// of the same key (<see cref="NavigationMenu"/>): only to whom the menu offers it.
    /// </summary>
    public static IReadOnlySet<string> TargetKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "collection",
        "reception",
        "recollections",
        "entry",
        "validation",
        "reports",
        "billing",
        "qc",
        "nonconformities",
        "inventory",
        "supplierinvoices",
        "temperatures",
        "equipment",
        "catalog"
    };

    /// <summary>
    /// The right each figure belongs to, by its label. Showing the cashier the critical results
    /// waiting, or the technician the unpaid invoices, is telling them about work the rest of the
    /// application keeps from them. A figure not listed is shown to all.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TileRights { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Dashboard.OrdersToday"] = Permissions.Orders.View,
        ["Dashboard.OverdueOrders"] = Permissions.Orders.View,
        ["Dashboard.AwaitingCollection"] = Permissions.Samples.View,
        ["Dashboard.AwaitingReception"] = Permissions.Samples.View,
        ["Dashboard.RejectedSamplesToday"] = Permissions.Samples.View,
        ["Dashboard.AwaitingEntry"] = Permissions.Results.View,
        ["Dashboard.AwaitingValidation"] = Permissions.Results.View,
        ["Dashboard.CriticalPending"] = Permissions.Results.View,
        ["Dashboard.ReportsToRelease"] = Permissions.Reports.View,
        ["Report.Plural"] = Permissions.Reports.View,
        ["Dashboard.QcAlerts"] = Permissions.Quality.View,
        ["Dashboard.OpenNonConformities"] = Permissions.Quality.View,
        ["Equipment.NextCalibration"] = Permissions.Equipment.View,
        ["Inventory.ToReorder"] = Permissions.Inventory.View,
        ["Inventory.ExpiringSoon"] = Permissions.Inventory.View,
        ["Stock.DashboardExpiringCritical"] = Permissions.Inventory.View,
        ["Stock.DashboardStockOuts"] = Permissions.Inventory.View,
        ["Temperatures.DashboardMissing"] = Permissions.Inventory.RecordTemperatures,
        ["Dashboard.Receivables"] = Permissions.Billing.View,
        ["SupplierInvoices.DashboardOverdue"] = Permissions.Inventory.EnterInvoices,
        ["SupplierInvoices.DashboardDueSoon"] = Permissions.Inventory.EnterInvoices
    };

    /// <summary>Whether <paramref name="user"/> may see the figure labelled <paramref name="labelKey"/>.</summary>
    public static bool MaySee(string labelKey, ICurrentUser? user) =>
        !TileRights.TryGetValue(labelKey, out var right) || (user is not null && user.Has(right));

    /// <summary>
    /// Whether a figure leading to <paramref name="key"/> opens it for <paramref name="user"/>: a
    /// screen figures may open, and one of the rights its menu entry asks for.
    /// </summary>
    public static bool CanOpen(string? key, ICurrentUser? user) =>
        key is not null
        && TargetKeys.Contains(key)
        && NavigationMenu.Find(key) is { } entry
        && user is not null
        && entry.RequiredPermissions.Any(user.Has);
}
