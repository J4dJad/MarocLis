using MarocLIS.Reporting;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Turns the list a screen is showing into a <see cref="ListDocument"/>.
/// <para>
/// Reads the grid as displayed — visible columns in their on-screen order, values as formatted for
/// the screen — so the printout matches what the operator chose to print, filters and sort
/// included, without each screen having to describe its own columns a second time.
/// </para>
/// </summary>
public static class ListPrinting
{
    /// <summary>
    /// Tag of a column kept off the screen for room but still printed — the result entry's
    /// technical comment, edited in a box and printed with the grid as it was when it had a column.
    /// </summary>
    public const string PrintedWhenHidden = "printed-when-hidden";

    /// <summary>
    /// Tag of the grid a screen names as its main list, printed with Ctrl+P whatever its size — the
    /// file screen's list of files, which is no longer its largest grid.
    /// </summary>
    public const string MainList = "main-list";

    /// <summary>The screen's main list: the grid tagged as such, else the largest grid currently visible.</summary>
    public static DataGridView? MainGrid(Control view) =>
        Descendants(view)
            .OfType<DataGridView>()
            .Where(g => g.Visible && g.Columns.Count > 0 && IsOnScreen(g))
            .OrderByDescending(g => g.Tag is MainList)
            .ThenByDescending(g => g.Width * g.Height)
            .FirstOrDefault();

    public static ListDocument Capture(DataGridView grid, string title, string caption, bool rightToLeft)
    {
        var columns = grid.Columns.Cast<DataGridViewColumn>()
            .Where(c => (c.Visible || c.Tag is PrintedWhenHidden) && c is not DataGridViewImageColumn && c is not DataGridViewButtonColumn)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        var rows = grid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && r.Visible)
            .Select(r => (IReadOnlyList<string>)columns.Select(c => Format(r.Cells[c.Index])).ToList())
            .ToList();

        return new ListDocument(title, caption,
                                columns.Select(c => c.HeaderText.Replace("\n", " ")).ToList(),
                                rows, rightToLeft);
    }

    private static string Format(DataGridViewCell cell)
    {
        var value = cell.FormattedValue ?? cell.Value;

        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "✓" : string.Empty,
            CheckState state => state == CheckState.Checked ? "✓" : string.Empty,
            _ => value.ToString()?.Replace('⁦', ' ').Replace('⁩', ' ').Trim() ?? string.Empty
        };
    }

    private static bool IsOnScreen(Control control)
    {
        for (var c = control; c is not null; c = c.Parent)
            if (!c.Visible) return false;

        return true;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
