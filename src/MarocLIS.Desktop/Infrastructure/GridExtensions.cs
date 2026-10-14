namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Grid helpers shared by every view.
/// </summary>
public static class GridExtensions
{
    /// <summary>
    /// The column called <paramref name="name"/>, or an exception naming it.
    /// <para>
    /// <see cref="DataGridViewColumnCollection"/>'s string indexer returns null for a name it does
    /// not hold, so <c>grid.Columns["payer"].HeaderText = …</c> — the shape every
    /// <c>ApplyTexts</c> override uses to translate its headers — is a null dereference waiting on a
    /// typo or on a column that was renamed in one place and not the other. There were 263 of them,
    /// and the failure they produce is a <see cref="NullReferenceException"/> from inside a language
    /// switch, with nothing in it to say which column or which screen.
    /// </para>
    /// <para>
    /// Throwing rather than returning null and letting the caller skip: a header that silently keeps
    /// its design-time text is a screen that stays untranslated in production, which on an Arabic
    /// workstation is the defect the whole localisation layer exists to prevent. A missing column is
    /// a programming error, it surfaces the first time the screen is opened, and the message says
    /// which name was asked for.
    /// </para>
    /// </summary>
    public static DataGridViewColumn Column(this DataGridView grid, string name) =>
        grid.Columns[name]
        ?? throw new ArgumentException(
            $"The grid has no column named '{name}'. The column names set up in the view's grid " +
            "construction and the ones its ApplyTexts override translates have diverged.",
            nameof(name));

    /// <summary>
    /// Makes the first row the current one, not only the highlighted one.
    /// <para>
    /// <c>Rows[0].Selected = true</c> paints the row but leaves <see cref="DataGridView.CurrentRow"/>
    /// unset, so a detail panel that follows the current row stayed empty under a row that looked
    /// chosen: the patient list opened with its first patient highlighted and no history beside it.
    /// </para>
    /// </summary>
    public static void SelectFirstRow(this DataGridView grid)
    {
        if (grid.Rows.Count == 0) return;

        var row = grid.Rows[0];
        var cell = row.Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.Visible);

        try
        {
            if (cell is not null)
            {
                // The first row added already became current while its Tag was still empty, and
                // setting the same cell again raises nothing: step off it so the views that load
                // details on SelectionChanged see the row with its data.
                if (grid.CurrentCell == cell) grid.CurrentCell = null;
                grid.CurrentCell = cell;
            }
        }
        catch (InvalidOperationException)
        {
            // Raised when the grid is mid-edit or mid-layout; the highlight below still applies.
        }

        row.Selected = true;
    }

    /// <summary>
    /// Puts back the fill weights a screen declared, kept in each column's <see cref="DataGridViewColumn.Tag"/>.
    /// <para>
    /// WinForms rewrites a fill column's weight whenever its minimum width wins over its share. A
    /// grid first laid out while its view is still small comes out of that with weights nobody
    /// chose, and kept them at full size: the matricule column of the personnel list took a
    /// quarter of the screen. Re-applied once the grid has its real width, the declared
    /// proportions hold.
    /// </para>
    /// </summary>
    public static void RestoreFillWeights(this DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns)
            if (column.Tag is int weight && column.AutoSizeMode is DataGridViewAutoSizeColumnMode.Fill
                                                                  or DataGridViewAutoSizeColumnMode.NotSet)
                column.FillWeight = weight;
    }
}
