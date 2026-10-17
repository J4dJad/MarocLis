using System.Runtime.CompilerServices;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Captions that a shared helper gives a control and that follow the language by themselves.
/// <para>
/// A screen re-reads its own texts in <c>ApplyTexts</c> on a language change, but a control that a
/// shared helper captions (« Plus d'actions ») is not one the screen names, and stayed in the
/// language it was built in on every screen that did not think of it — Facturation among them. The
/// helper binds the key here, and the forms and views re-apply every bound caption after their
/// own texts.
/// </para>
/// </summary>
internal static class LocalizedCaptions
{
    private static readonly ConditionalWeakTable<Control, string> Keys = new();

    /// <summary>Captions <paramref name="control"/> with <paramref name="key"/>, now and on every language change.</summary>
    public static void Bind(Control control, ILocalizer localizer, string key)
    {
        Keys.AddOrUpdate(control, key);
        control.Text = localizer[key];
    }

    /// <summary>Re-reads every bound caption under <paramref name="root"/>.</summary>
    public static void Apply(Control root, ILocalizer localizer)
    {
        if (Keys.TryGetValue(root, out var key)) root.Text = localizer[key];
        foreach (Control child in root.Controls) Apply(child, localizer);
    }
}
