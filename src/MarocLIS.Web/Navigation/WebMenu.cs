using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Navigation;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Web.Navigation;

/// <summary>An entry of the web menu: the core's entry, its address and the workstation's icon.</summary>
public sealed record WebMenuItem(MenuEntry Entry, string Href, Glyph? Icon);

/// <summary>A group of the web menu: its header (none for the entries above the first) and the entries shown.</summary>
public sealed record WebMenuGroup(MenuEntry? Header, IReadOnlyList<WebMenuItem> Items);

/// <summary>
/// The Windows shell's menu, for the browser: the same entries in the same order and groups, shown
/// to the same people — <see cref="NavigationMenu.IsPermitted"/> and
/// <see cref="NavigationMenu.IsGroupShown"/> decide, with the user's rights and the licence's
/// modules, exactly as they decide for the shell. A screen the user may not open is not shown at
/// all, and refused when asked for by its address (<see cref="ScreenAccess"/>).
/// </summary>
public static class WebMenu
{
    /// <summary>The address of the screen <paramref name="key"/>.</summary>
    public static string HrefOf(string key) => "ecran/" + Uri.EscapeDataString(key);

    public static Glyph? IconOf(string key) =>
        IconCatalog.Navigation.TryGetValue(key, out var glyph) ? glyph : null;

    public static IReadOnlyList<WebMenuGroup> Build(ICurrentUser user, LicenceStatus? licence)
    {
        ArgumentNullException.ThrowIfNull(user);

        var groups = new List<WebMenuGroup>();
        MenuEntry? header = null;
        var items = new List<WebMenuItem>();
        var shown = true;

        void Close()
        {
            if (shown && items.Count > 0) groups.Add(new WebMenuGroup(header, [.. items]));
            items.Clear();
        }

        foreach (var entry in NavigationMenu.Entries)
        {
            if (entry.IsGroupHeader)
            {
                Close();
                header = entry;
                shown = NavigationMenu.IsGroupShown(entry.Key, user, licence);
                continue;
            }

            if (NavigationMenu.IsPermitted(entry, user, licence))
                items.Add(new WebMenuItem(entry, HrefOf(entry.Key), IconOf(entry.Key)));
        }

        Close();
        return groups;
    }
}
