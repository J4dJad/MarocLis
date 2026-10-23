using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Navigation;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Web.Navigation;

/// <summary>What a screen asked for by its address comes to.</summary>
public enum ScreenDecision
{
    Open,

    /// <summary>No such screen in the menu (or a group's header, which is not a screen).</summary>
    NotFound,

    /// <summary>The user holds none of the screen's rights.</summary>
    NoPermission,

    /// <summary>The screen's module is not in the licence.</summary>
    NotLicensed
}

/// <summary>
/// Whether a screen opens when its address is typed, bookmarked or shared: by the rule that builds
/// the menu (<see cref="NavigationMenu.Access"/>), so a screen hidden from the menu is also refused
/// — hiding an entry the address bar still opens would be theatre.
/// </summary>
public static class ScreenAccess
{
    public static (ScreenDecision Decision, MenuEntry? Entry) Decide(string? key, ICurrentUser user, LicenceStatus? licence)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrEmpty(key) || NavigationMenu.Find(key) is not { IsGroupHeader: false } entry)
            return (ScreenDecision.NotFound, null);

        return (NavigationMenu.Access(entry, user, licence) switch
        {
            MenuAccess.Permitted => ScreenDecision.Open,
            MenuAccess.NoPermission => ScreenDecision.NoPermission,
            _ => ScreenDecision.NotLicensed
        }, entry);
    }

    /// <summary>The group a screen is listed under, for the breadcrumb; null above the first group.</summary>
    public static MenuEntry? GroupOf(string key) =>
        NavigationMenu.GroupOf(key) is { } group ? NavigationMenu.Find(group) : null;
}
