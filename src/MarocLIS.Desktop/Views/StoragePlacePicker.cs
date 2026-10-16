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
    }

    /// <summary>
    /// Fills <paramref name="combo"/> with <paramref name="places"/>, the empty entry first, and
    /// selects <paramref name="current"/>.
    /// </summary>
    /// <param name="emptyText">The first entry, meaning no place.</param>
    /// <param name="outOfList">How a kept place missing from the list is shown, {0} being its name.</param>
    public static void Fill(ComboBox combo, IReadOnlyList<string> places, string? current,
                            string emptyText, Func<string, string> outOfList)
    {
        var wanted = current?.Trim() ?? string.Empty;

        combo.BeginUpdate();
        combo.Items.Clear();
        combo.Items.Add(new Choice(string.Empty, emptyText));

        foreach (var place in places) combo.Items.Add(new Choice(place, place));

        var index = IndexOf(combo, wanted);

        if (index < 0 && wanted.Length > 0)
            index = combo.Items.Add(new Choice(wanted, outOfList(wanted)));

        combo.SelectedIndex = Math.Max(0, index);
        combo.EndUpdate();
    }

    /// <summary>The place chosen, or null for the empty entry.</summary>
    public static string? Selected(ComboBox combo) =>
        combo.SelectedItem is Choice { Value.Length: > 0 } choice ? choice.Value : null;

    private static int IndexOf(ComboBox combo, string value)
    {
        if (value.Length == 0) return 0;

        for (var i = 1; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is Choice c && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
