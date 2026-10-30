using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Application.Navigation;

/// <summary>
/// An entry of the application's menu: the key that names the screen (shortcuts, help, licence
/// modules), the resource of its label, and the rights of which the user needs one. A group header
/// names the entries listed under it, up to the next header.
/// </summary>
public sealed record MenuEntry(
    string Key,
    string LabelKey,
    IReadOnlyList<string> RequiredPermissions,
    bool IsGroupHeader = false);

/// <summary>Why a menu entry is or is not open to a user.</summary>
public enum MenuAccess
{
    Permitted,

    /// <summary>The user holds none of the entry's rights.</summary>
    NoPermission,

    /// <summary>The entry belongs to a module the licence does not open.</summary>
    NotLicensed
}

/// <summary>
/// The menu every client shows, in its order, and who is shown which entry.
/// <para>
/// It lives in the application layer rather than in the workstation's shell so a screen the user has
/// no right to, or that the licence does not open, is left out by every client alike: the web client
/// must not offer what the workstation hides. The services refuse the operation whatever a menu
/// shows (<see cref="IPermissionGuard"/>); the menu only keeps from showing what would be refused.
/// </para>
/// </summary>
public static class NavigationMenu
{
    public static IReadOnlyList<MenuEntry> Entries { get; } =
    [
        new("dashboard", "Nav.Dashboard", []),

        new("statistics", "Nav.Statistics", [Permissions.Statistics.View]),

        // The director's dashboard sits with the dashboard and the statistics: it reads every
        // module, and belongs to none of the groups below.
        new("steering", "Nav.Steering", [Permissions.Steering.View]),

        new("group.reception", "Nav.Reception", [Permissions.Patients.View], IsGroupHeader: true),

        new("patients", "Nav.Patients", [Permissions.Patients.View]),

        new("orders", "Nav.Orders", [Permissions.Orders.Create]),

        new("orderdetail", "Nav.OrderFile", [Permissions.Orders.View]),

        new("group.technical", "Nav.Worklist", [Permissions.Results.View], IsGroupHeader: true),

        new("collection", "Nav.Collection", [Permissions.Samples.Collect]),

        new("reception", "Nav.SampleReception", [Permissions.Samples.Receive]),

        new("recollections", "Nav.Recollections", [Permissions.Orders.Edit, Permissions.Samples.Reject]),

        new("serumbank", "Nav.SerumBank", [Permissions.Samples.Receive]),

        new("subcontracting", "Nav.Subcontracting", [Permissions.Samples.Subcontract]),

        new("entry", "Nav.ResultEntry", [Permissions.Results.Enter]),

        new("validation", "Nav.Validation", [Permissions.Results.BiologicalValidate]),

        new("reports", "Nav.Reports", [Permissions.Reports.View]),

        new("group.billing", "Nav.Billing", [Permissions.Billing.View], IsGroupHeader: true),

        new("billing", "Nav.Billing", [Permissions.Billing.View]),

        new("cash", "Nav.CashDesk", [Permissions.Billing.RecordPayment]),

        new("claims", "Nav.Claims", [Permissions.Billing.View]),

        // Who granted which discount, why, and who approved it: read by whoever approves discounts
        // above the ceiling or steers the laboratory.
        new("discounts", "Nav.Discounts", [Permissions.Billing.ApproveDiscount, Permissions.Steering.View]),

        // The profiles the front desk grants by name are tariffs, kept with them.
        new("discountprofiles", "Nav.DiscountProfiles", [Permissions.Billing.ManageTariffs]),

        new("group.quality", "Nav.Quality", [Permissions.Quality.View], IsGroupHeader: true),

        new("qc", "Nav.QualityControl", [Permissions.Quality.EnterQc,
                                          Permissions.Quality.ReviewQc]),

        new("nonconformities", "Nav.NonConformities", [Permissions.Quality.ManageNonConformity,
                                                        Permissions.Quality.View]),

        new("eqa", "Nav.Eqa", [Permissions.Quality.View]),

        new("documents", "Nav.Documents", [Permissions.Quality.View]),

        new("group.logistics", "Nav.Inventory", [Permissions.Inventory.View,
                                                  Permissions.Equipment.View,
                                                  Permissions.Inventory.EnterInvoices,
                                                  Permissions.Inventory.RecordTemperatures], IsGroupHeader: true),

        new("inventory", "Nav.Inventory", [Permissions.Inventory.View]),

        new("purchaseorders", "Nav.PurchaseOrders", [Permissions.Inventory.View]),

        new("quotes", "Nav.Quotes", [Permissions.Inventory.ManageQuotes]),
        new("supplierinvoices", "Nav.SupplierInvoices", [Permissions.Inventory.EnterInvoices,
                                                          Permissions.Inventory.ApprovePayments]),
        new("supplierevaluation", "Nav.SupplierEvaluation", [Permissions.Inventory.ManagePurchaseOrders]),
        new("consumption", "Nav.Consumption", [Permissions.Inventory.View]),
        new("inventorycounts", "Nav.InventoryCounts", [Permissions.Inventory.Receive,
                                                        Permissions.Inventory.CloseCounts]),
        new("temperatures", "Nav.Temperatures", [Permissions.Inventory.RecordTemperatures,
                                                  Permissions.Inventory.ManageStorageUnits]),

        new("equipment", "Nav.Equipment", [Permissions.Equipment.View]),

        new("contracts", "Nav.Contracts", [Permissions.Equipment.ViewContracts]),

        new("group.personnel", "Nav.HumanResources",
            [Permissions.HumanResources.View, Permissions.HumanResources.RequestLeave,
             Permissions.HumanResources.ApproveLeave, Permissions.HumanResources.ManagePlanning,
             Permissions.HumanResources.Salary, Permissions.HumanResources.Accidents], IsGroupHeader: true),

        new("personnel", "Nav.Personnel", [Permissions.HumanResources.View]),

        new("habilitations", "Nav.Habilitations", [Permissions.HumanResources.View]),

        new("hrdeadlines", "Nav.PersonnelDeadlines", [Permissions.HumanResources.View]),

        // Every employee asks for their own leave here; the list shows them their own only.
        new("leave", "Nav.Leave",
            [Permissions.HumanResources.RequestLeave, Permissions.HumanResources.ApproveLeave,
             Permissions.HumanResources.View]),

        new("planning", "Nav.Planning",
            [Permissions.HumanResources.View, Permissions.HumanResources.ManagePlanning]),

        new("payroll", "Nav.Payroll", [Permissions.HumanResources.Salary]),

        // A blood exposure is declared by whoever is on duty with the right, HR or biologist.
        new("accidents", "Nav.Accidents", [Permissions.HumanResources.Accidents, Permissions.HumanResources.View]),

        new("group.relations", "Nav.RelationsGroup", [Permissions.Relations.View, Permissions.Messaging.Campaign], IsGroupHeader: true),

        new("prescriberstats", "Nav.PrescriberStatistics", [Permissions.Relations.View]),

        new("relations", "Nav.Relations", [Permissions.Relations.View]),

        new("patientmessaging", "Nav.PatientMessaging", [Permissions.Messaging.Campaign]),

        new("group.finance", "Nav.Finance", [Permissions.Accounting.Export], IsGroupHeader: true),

        new("accountingexport", "Nav.AccountingExport", [Permissions.Accounting.Export]),

        new("accountingsettings", "Nav.AccountingSettings", [Permissions.Accounting.Export]),

        new("fixedassets", "Nav.FixedAssets", [Permissions.Assets.View]),

        new("bankreconciliation", "Nav.BankReconciliation", [Permissions.Treasury.View, Permissions.Treasury.Manage]),

        new("cashforecast", "Nav.CashForecast", [Permissions.Treasury.View, Permissions.Treasury.Manage]),

        new("cashreview", "Nav.CashSessionReview", [Permissions.Billing.ReconcileCashSession]),

        new("costmargin", "Nav.CostMargin", [Permissions.Steering.View]),

        new("group.administration", "Nav.Administration", [Permissions.Administration.ViewUsers], IsGroupHeader: true),

        new("users", "Nav.Users", [Permissions.Administration.ViewUsers]),

        new("audit", "Nav.AuditTrail", [Permissions.Administration.ViewAuditTrail]),

        new("catalog", "Nav.Catalog", [Permissions.Catalog.View]),

        new("interfaces", "Nav.Instruments", [Permissions.Instruments.View]),

        new("masterdata", "Nav.MasterData", [Permissions.Equipment.Manage,
                                          Permissions.Quality.ManageQcRules,
                                          Permissions.Inventory.ManagePurchaseOrders,
                                          Permissions.Catalog.Manage,
                                          Permissions.Billing.ManageTariffs]),

        new("migration", "Nav.Migration", [Permissions.Administration.Migration]),

        // The licence tab is reachable with its own right alone: importing the next licence
        // must not take the right to change every setting.
        new("settings", "Nav.Settings", [Permissions.Administration.ManageSites,
                                          Permissions.Administration.ManageSettings,
                                          Permissions.Administration.License])
    ];

    /// <summary>The entry of <paramref name="key"/>, or null when the menu has none.</summary>
    public static MenuEntry? Find(string key) => Entries.FirstOrDefault(e => e.Key == key);

    /// <summary>
    /// Whether <paramref name="user"/> is shown <paramref name="entry"/>: one of its rights (an
    /// entry without any is open to all), and the module that holds it in the licence. Without a
    /// licence status — no licensing in this installation — only the rights count.
    /// <para>
    /// An entry the user has no permission for is not offered at all rather than disabled: a
    /// disabled entry would still tell them the feature exists and that they are not trusted with
    /// it, which is noise on a screen that has to stay scannable.
    /// </para>
    /// </summary>
    public static bool IsPermitted(MenuEntry entry, ICurrentUser user, LicenceStatus? licence) =>
        Access(entry, user, licence) == MenuAccess.Permitted;

    /// <summary>
    /// Whether <paramref name="user"/> may open <paramref name="entry"/>, and if not why: no right
    /// to it, or a module the licence does not open. The menu hides both alike; a client asked for
    /// the screen directly — the web's address bar — refuses it and says which.
    /// </summary>
    public static MenuAccess Access(MenuEntry entry, ICurrentUser user, LicenceStatus? licence)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(user);

        if (entry.RequiredPermissions.Count > 0 && !entry.RequiredPermissions.Any(user.Has))
            return MenuAccess.NoPermission;

        return licence is null || LicenceNavigation.IsLicensed(entry.Key, licence)
            ? MenuAccess.Permitted
            : MenuAccess.NotLicensed;
    }

    /// <summary>The entries listed under a group header, up to the next header.</summary>
    public static IEnumerable<MenuEntry> GroupEntries(string groupKey) =>
        Entries.SkipWhile(e => e.Key != groupKey).Skip(1).TakeWhile(e => !e.IsGroupHeader);

    /// <summary>
    /// Whether a group is shown: when the user can open at least one of its screens, whatever the
    /// header itself was declared with — a phlebotomist has Collection without Results.View, and the
    /// technical header used to vanish above the entry it held.
    /// </summary>
    public static bool IsGroupShown(string groupKey, ICurrentUser user, LicenceStatus? licence) =>
        GroupEntries(groupKey).Any(e => IsPermitted(e, user, licence));

    /// <summary>The group an entry is listed under, or null for the entries above the first header.</summary>
    public static string? GroupOf(string key)
    {
        string? group = null;
        foreach (var entry in Entries)
        {
            if (entry.IsGroupHeader) group = entry.Key;
            else if (entry.Key == key) return group;
        }
        return null;
    }
}
