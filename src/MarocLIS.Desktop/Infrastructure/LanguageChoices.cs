using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Common;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The language lists of the screens: only the languages the laboratory works in, chosen by value.
/// <para>
/// Every picker of a language goes through here — the patient's, the report's, the user's, the
/// interface's — so a language switched off in Paramètres › Langues disappears from all of them at
/// once. A record that still holds such a language shows French selected, which is what saving it
/// will record and what the services assign in any case.
/// </para>
/// </summary>
internal static class LanguageChoices
{
    /// <summary>The process's reading of the setting, set at start-up once the database is open.</summary>
    public static ActiveLanguagesCache? Languages { get; set; }

    /// <summary>The languages offered, French first; the three before the setting was read.</summary>
    public static IReadOnlyList<AppLanguage> Active => Current.Active;

    /// <summary><paramref name="language"/> when it is offered, French otherwise.</summary>
    public static AppLanguage Coerce(AppLanguage language) => Current.Coerce(language);

    private static LanguageSettings Current => Languages?.Current ?? LanguageSettings.Default;

    /// <summary>Each language in its own name (<see cref="LanguageSettings.OwnName"/>).</summary>
    public static string OwnName(AppLanguage language) => LanguageSettings.OwnName(language);

    /// <summary>
    /// Fills <paramref name="combo"/> with the active languages, labelled by <paramref name="label"/>,
    /// and selects <paramref name="selected"/> — French when it is no longer offered.
    /// </summary>
    public static void Fill(ComboBox combo, Func<AppLanguage, string> label, AppLanguage selected)
    {
        ArgumentNullException.ThrowIfNull(combo);
        ArgumentNullException.ThrowIfNull(label);

        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (var language in Active)
            combo.Items.Add(new EnumChoice((int)language, label(language)));
        Select(combo, selected);
        combo.EndUpdate();
    }

    /// <summary>Selects <paramref name="language"/> in a list filled by <see cref="Fill"/>, French if absent.</summary>
    public static void Select(ComboBox combo, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(combo);

        var wanted = (int)Coerce(language);
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is EnumChoice choice && choice.Value == wanted)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    /// <summary>The language selected in a list filled by <see cref="Fill"/>; French when none is.</summary>
    public static AppLanguage Selected(ComboBox combo) =>
        combo?.SelectedItem is EnumChoice choice ? (AppLanguage)choice.Value : AppLanguage.French;
}
