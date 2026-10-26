using MarocLIS.Application.Samples;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The closed list of sample storage places (« Paramètres › Lieux de stockage »), as reception and
/// the serum bank's dialogs offer it.
/// <para>
/// Only the site's active places are offered. A tube already recorded in a place that is not, or no
/// longer, in that list keeps it: the place is added to the list, marked, and stays selected, so
/// opening the tube and saving it again never silently moves it somewhere else.
/// </para>
/// </summary>
internal static class StoragePlacePicker
{
    /// <summary>One entry: the name written on the tube, and what the list shows.</summary>
    private sealed record Choice(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    public static void Configure(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Font = UiTheme.Body;
        // The serum bank's dialogs are plain windows, not restyled as a whole: without its own
        // border the list showed no frame at all.
        combo.WithFieldBorder();
    }

    /// <summary>
    /// Fills <paramref name="combo"/> with <paramref name="places"/>, the empty entry first, and
    /// selects <paramref name="current"/> — kept and marked when it is not in the list
    /// (SampleStorageScreenRules.PlaceChoices, the web's list).
    /// </summary>
    /// <param name="emptyText">The first entry, meaning no place.</param>
    /// <param name="outOfList">How a kept place missing from the list is shown, {0} being its name.</param>
    public static void Fill(ComboBox combo, IReadOnlyList<string> places, string? current,
                            string emptyText, Func<string, string> outOfList)
    {
        var choices = SampleStorageScreenRules.PlaceChoices(places, current, emptyText, outOfList);

        combo.BeginUpdate();
        combo.Items.Clear();

        foreach (var choice in choices) combo.Items.Add(new Choice(choice.Value, choice.Display));

        combo.SelectedIndex = Math.Max(0, SampleStorageScreenRules.SelectedIndex(choices, current));
        combo.EndUpdate();
    }

    /// <summary>The place chosen, or null for the empty entry.</summary>
    public static string? Selected(ComboBox combo) =>
        combo.SelectedItem is Choice choice ? SampleStorageScreenRules.Chosen(choice.Value) : null;
}
