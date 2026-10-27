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
                grid.CellPainting += PaintHeader;
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

    /// <summary>
    /// Draws a column header's caption on lines broken at its spaces only (<see cref="TextWrap"/>):
    /// Windows broke « Prélèvement pédiatrique » into « Prélèvement pé | diatrique ».
    /// </summary>
    private static void PaintHeader(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.Handled || e.RowIndex != -1 || e.ColumnIndex < 0 || e.Graphics is null) return;
        if (sender is not DataGridView grid || e.FormattedValue is not string { Length: > 0 } text) return;

        var style = e.CellStyle ?? grid.ColumnHeadersDefaultCellStyle;
        if (style.WrapMode == DataGridViewTriState.False) return;

        var font = style.Font ?? grid.Font;
        var column = grid.Columns[e.ColumnIndex];
        var glyph = column.SortMode == DataGridViewColumnSortMode.NotSortable ? 0 : 14;
        var inner = new Rectangle(e.CellBounds.X + style.Padding.Left + 4, e.CellBounds.Y + style.Padding.Top + 2,
                                  e.CellBounds.Width - style.Padding.Horizontal - 8 - glyph,
                                  e.CellBounds.Height - style.Padding.Vertical - 4);
        if (inner.Width <= 0 || inner.Height <= 0) return;

        const TextFormatFlags measureFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var lines = TextWrap.Lines(text, w => TextRenderer.MeasureText(e.Graphics, w, font, Size.Empty, measureFlags).Width,
                                   inner.Width);

        e.Paint(e.CellBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);

        var rtl = grid.RightToLeft == RightToLeft.Yes;
        var flags = measureFlags | TextFormatFlags.EndEllipsis;
        flags |= HorizontalOf(style.Alignment, rtl);
        if (rtl) flags |= TextFormatFlags.RightToLeft;

        var lineHeight = font.Height;
        var visible = Math.Max(1, Math.Min(lines.Count, inner.Height / lineHeight));
        var top = VerticalOf(style.Alignment) switch
        {
            0 => inner.Top,
            2 => inner.Bottom - visible * lineHeight,
            _ => inner.Top + (inner.Height - visible * lineHeight) / 2
        };

        var color = style.ForeColor.IsEmpty ? grid.ForeColor : style.ForeColor;
        for (var i = 0; i < visible; i++)
        {
            // The last line that fits takes what is left, cut with an ellipsis.
            var line = i == visible - 1 ? string.Join(" ", lines.Skip(i)) : lines[i];
            TextRenderer.DrawText(e.Graphics, line, font, new Rectangle(inner.X, top + i * lineHeight, inner.Width, lineHeight),
                                  color, flags);
        }

        e.Handled = true;
    }

    private static TextFormatFlags HorizontalOf(DataGridViewContentAlignment alignment, bool rtl)
    {
        var left = alignment is DataGridViewContentAlignment.TopLeft or DataGridViewContentAlignment.MiddleLeft
                       or DataGridViewContentAlignment.BottomLeft or DataGridViewContentAlignment.NotSet;
        var right = alignment is DataGridViewContentAlignment.TopRight or DataGridViewContentAlignment.MiddleRight
                        or DataGridViewContentAlignment.BottomRight;

        // A mirrored grid puts the start of a caption on the right.
        if (rtl) (left, right) = (right, left);
        return left ? TextFormatFlags.Left : right ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
    }

    // 0 top, 1 middle, 2 bottom.
    private static int VerticalOf(DataGridViewContentAlignment alignment) => alignment switch
    {
        DataGridViewContentAlignment.TopLeft or DataGridViewContentAlignment.TopCenter
            or DataGridViewContentAlignment.TopRight => 0,
        DataGridViewContentAlignment.BottomLeft or DataGridViewContentAlignment.BottomCenter
            or DataGridViewContentAlignment.BottomRight => 2,
        _ => 1
    };

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
