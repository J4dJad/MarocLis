namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Keeps the button strips and notice bands of a screen usable on a narrow window.
/// <para>
/// The screens place their action buttons at fixed x positions in a panel docked to the top or the
/// bottom. Laid out for a wide monitor, the last buttons fall outside the panel on a 1366-pixel
/// laptop or beside a split grid, and an operator cannot click what they cannot see — "Expédier"
/// and "Imprimer les étiquettes" on the collection screen were simply gone. When everything fits,
/// the original positions are kept untouched; when it does not, the strip wraps onto further lines
/// and the panel grows to hold them, and a fixed-width notice shrinks to the window and takes the
/// height its text needs.
/// </para>
/// <para>
/// Applied once to the whole view from <see cref="LocalizedView"/>, so a screen added later gets it
/// without having to remember it.
/// </para>
/// </summary>
public static class BarReflow
{
    private const int Gap = 10;
    private const int LineGap = 6;

    public static void AttachAll(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (IsEligible(child)) Attach(child);
            AttachAll(child);
        }
    }

    private static bool IsEligible(Control control)
    {
        if (control.GetType() != typeof(Panel)) return false;
        if (control.Dock is not (DockStyle.Top or DockStyle.Bottom)) return false;
        if (((Panel)control).AutoScroll || control.Controls.Count == 0) return false;

        foreach (Control child in control.Controls)
        {
            if (child.Dock != DockStyle.None) return false;
            if ((child.Anchor & (AnchorStyles.Right | AnchorStyles.Bottom)) != 0) return false;
        }

        return true;
    }

    private static void Attach(Control panel)
    {
        var original = panel.Controls.Cast<Control>().ToDictionary(c => c, c => c.Bounds);
        var originalHeight = panel.Height;
        var originalContentBottom = original.Values.Max(r => r.Bottom);
        var busy = false;

        void Apply()
        {
            if (busy || panel.IsDisposed) return;
            busy = true;

            try
            {
                var available = panel.ClientSize.Width - panel.Padding.Right;
                if (available <= 0) return;

                var live = original.Where(p => !p.Key.IsDisposed).ToList();

                var fits = live.All(p => p.Value.Right <= available
                                         && WidthFor(p.Key, p.Value.Width) == p.Value.Width
                                         && !(p.Key is Label { AutoSize: false } label
                                              && NeedsMoreHeight(label, p.Value.Width, p.Value.Height)));

                panel.SuspendLayout();

                if (fits)
                {
                    foreach (var (control, bounds) in live)
                        if (control.Bounds != bounds) control.Bounds = bounds;

                    if (panel.Height != originalHeight) panel.Height = originalHeight;
                    return;
                }

                var bottom = 0;
                var shift = 0;

                foreach (var row in Rows(live))
                {
                    var rowTop = row.Min(p => p.Value.Top);
                    var rowHeight = row.Max(p => p.Value.Bottom) - rowTop;
                    var startX = row.Min(p => p.Value.Left);

                    var lineTop = rowTop + shift;
                    var lineHeight = rowHeight;
                    var x = startX;

                    foreach (var (control, bounds) in row.OrderBy(p => p.Value.Left))
                    {
                        var width = WidthFor(control, bounds.Width);
                        var stretchable = control is Label { AutoSize: false };

                        if (stretchable) width = Math.Min(width, available - x);

                        if (x > startX && (x + width > available || (stretchable && width < 160)))
                        {
                            lineTop += lineHeight + LineGap;
                            lineHeight = rowHeight;
                            x = startX;
                            width = stretchable ? Math.Min(bounds.Width, available - x)
                                                : WidthFor(control, bounds.Width);
                        }

                        var height = bounds.Height;

                        if (control is Label { AutoSize: false } label && width > 0)
                        {
                            var needed = TextRenderer.MeasureText(label.Text, label.Font,
                                new Size(width - label.Padding.Horizontal, int.MaxValue),
                                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height
                                + label.Padding.Vertical + 2;

                            height = Math.Max(height, needed);
                        }

                        var top = lineTop + (bounds.Top - rowTop);
                        control.Bounds = new Rectangle(x, top, Math.Max(width, 1), height);

                        lineHeight = Math.Max(lineHeight, top + height - lineTop);
                        bottom = Math.Max(bottom, top + height);
                        x += width + Gap;
                    }

                    shift = lineTop + lineHeight - (rowTop + rowHeight);
                }

                var wanted = Math.Max(originalHeight, bottom + (originalHeight - originalContentBottom));
                if (panel.Height != wanted) panel.Height = wanted;
            }
            finally
            {
                panel.ResumeLayout(performLayout: true);
                busy = false;
            }
        }

        panel.Resize += (_, _) => Apply();

        foreach (var label in original.Keys.OfType<Label>().Where(l => !l.AutoSize))
            label.TextChanged += (_, _) => Apply();

        foreach (var control in original.Keys)
        {
            control.VisibleChanged += (_, _) => Apply();
            if (control is Button) control.TextChanged += (_, _) => Apply();
        }

        Apply();
    }

    /// <summary>A button is widened to its caption; every other control keeps its width.</summary>
    private static int WidthFor(Control control, int width)
    {
        if (control is not Button { AutoSize: false } button || string.IsNullOrEmpty(button.Text))
            return width;

        var needed = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                         TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width
                     + button.Padding.Horizontal + 12;

        return Math.Max(width, needed);
    }

    private static bool NeedsMoreHeight(Label label, int width, int height) =>
        !string.IsNullOrEmpty(label.Text)
        && TextRenderer.MeasureText(label.Text, label.Font,
               new Size(Math.Max(1, width - label.Padding.Horizontal), int.MaxValue),
               TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height
           > height - label.Padding.Vertical + 2;

    /// <summary>Groups the controls into the rows they were laid out in, by vertical overlap.</summary>
    private static List<List<KeyValuePair<Control, Rectangle>>> Rows(
        IEnumerable<KeyValuePair<Control, Rectangle>> controls)
    {
        var rows = new List<List<KeyValuePair<Control, Rectangle>>>();

        foreach (var item in controls.OrderBy(p => p.Value.Top))
        {
            var row = rows.LastOrDefault();

            if (row is not null && item.Value.Top < row.Max(p => p.Value.Bottom) - 4)
                row.Add(item);
            else
                rows.Add([item]);
        }

        return rows;
    }
}
