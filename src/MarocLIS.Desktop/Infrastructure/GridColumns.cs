using System.Runtime.CompilerServices;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Keeps every grid column wide enough for the longest word of its header, a code column wide
/// enough for its codes, and a short text column (a status, a sector, a unit, a tube) wide enough
/// for its values.
/// <para>
/// The grids fill their width by weight and their headers wrap, but a single word cannot wrap:
/// on a narrow window or beside a split, "Échantillons" came out as "Échantillo" and an accession
/// number as "2610…", which nobody can read off a rack. Each column now gets a minimum width;
/// when the minimums no longer fit, the grid scrolls sideways instead of cutting words.
/// </para>
/// <para>
/// Short values are as unreadable cut as codes: the file list showed "À pré…" and "Réce…" for its
/// statuses, the file's analyses "En at…" and "IMMU…", the result sheet "mL/min/1…", while the
/// designation and comment columns beside them stood half empty. Each text column's minimum now
/// covers its widest value up to <see cref="MaxTextMinimum"/>; longer free text (names,
/// designations) still shares what is left by weight.
/// </para>
/// <para>
/// Applied to the whole view from <see cref="LocalizedView"/> and <see cref="LocalizedForm"/>,
/// after the texts, so it follows a language change.
/// </para>
/// </summary>
public static class GridColumns
{
    private const int HeaderSlack = 24;   // header padding plus a sort glyph
    private const int CellSlack = 16;     // cell padding
    // An invoice number of the default site, « FAC/PRINCIPAL/2026/000005 », needs about 230.
    private const int MaxMinimum = 260;
    private const int MaxTextMinimum = 170;
    private const int SampledRows = 200;

    private static readonly ConditionalWeakTable<DataGridView, object> Hooked = new();

    public static void Apply(Control root)
    {
        foreach (var grid in Descendants(root).OfType<DataGridView>())
        {
            if (!Hooked.TryGetValue(grid, out _))
            {
                Hooked.Add(grid, new object());
                grid.RowsAdded += (_, _) => QueueFitCodes(grid);
                grid.DataBindingComplete += (_, _) => Fit(grid);
                grid.ColumnAdded += (_, _) => Fit(grid);
            }

            Fit(grid);
        }
    }

    private static void Fit(DataGridView grid)
    {
        var headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;

        foreach (DataGridViewColumn column in grid.Columns)
        {
            var longestWord = (column.HeaderText ?? string.Empty)
                .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(word => TextRenderer.MeasureText(word, headerFont).Width)
                .DefaultIfEmpty(0)
                .Max();

            SetMinimum(column, longestWord + HeaderSlack);
        }

        FitCodes(grid);
    }

    // Rows are often added one at a time; the codes are measured once the batch is in.
    private static readonly HashSet<DataGridView> Queued = [];

    private static void QueueFitCodes(DataGridView grid)
    {
        if (!grid.IsHandleCreated)
        {
            FitCodes(grid);
            return;
        }

        if (!Queued.Add(grid)) return;

        grid.BeginInvoke(() =>
        {
            Queued.Remove(grid);
            if (!grid.IsDisposed) FitCodes(grid);
        });
    }

    // Codes (accession numbers, barcodes) are set in the monospace font and cannot be shortened.
    // Other text columns get their widest value up to a cap; a column that wraps its text keeps
    // only its header's minimum.
    private static void FitCodes(DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (column is not DataGridViewTextBoxColumn and not DataGridViewComboBoxColumn) continue;

            var style = column.InheritedStyle;
            var font = column.DefaultCellStyle?.Font ?? style?.Font ?? grid.Font ?? UiTheme.Body;
            var isCode = font.FontFamily.Name == UiTheme.Monospace.FontFamily.Name;
            if (!isCode && style?.WrapMode == DataGridViewTriState.True) continue;

            var widest = 0;
            var rows = Math.Min(grid.Rows.Count, SampledRows);

            for (var i = 0; i < rows; i++)
            {
                var row = grid.Rows[i];
                var cell = row.Cells[column.Index];
                if (cell.FormattedValue is not string { Length: > 0 } text) continue;
                // A row or a cell set in bold (the file's open analyses) is measured in bold.
                var cellFont = cell.Style.Font ?? row.DefaultCellStyle.Font ?? font;
                widest = Math.Max(widest, TextRenderer.MeasureText(text, cellFont).Width);
            }

            if (widest > 0) SetMinimum(column, widest + CellSlack, isCode ? MaxMinimum : MaxTextMinimum);
        }
    }

    // Only ever raises a minimum, so a width a screen sets for its own column is kept.
    private static void SetMinimum(DataGridViewColumn column, int width, int cap = MaxMinimum)
    {
        width = Math.Clamp(width, 24, cap);
        if (column.MinimumWidth < width) column.MinimumWidth = width;
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
