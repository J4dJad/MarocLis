using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The parts of the design system that WinForms does not draw by itself: field borders in
/// line-strong, cards with a rounded line border, flat tabs, and the flat status strip.
/// <para>
/// Applied once to a whole form or view from <see cref="LocalizedForm"/> and
/// <see cref="LocalizedView"/>, like the mirroring and the icons, so a screen added later gets the
/// same look without having to remember it. Only the look changes: no control is moved, resized
/// or given another behaviour.
/// </para>
/// </summary>
public static class UiChrome
{
    private static readonly ConditionalWeakTable<Control, object> Styled = new();

    /// <summary>Styles every control under <paramref name="root"/> that the design system covers.</summary>
    public static void Apply(Control root)
    {
        foreach (Control child in root.Controls)
        {
            Style(child);
            Apply(child);
        }
    }

    private static void Style(Control control)
    {
        if (Styled.TryGetValue(control, out _)) return;

        switch (control)
        {
            case TextBox { BorderStyle: BorderStyle.FixedSingle, Multiline: false } box:
                box.WithFieldBorder();
                break;

            // A flat drop-down list draws no frame at all until it has the focus.
            case ComboBox { FlatStyle: FlatStyle.Flat } combo:
                Frame(combo);
                break;

            case TabControl tabs:
                StyleTabs(tabs);
                break;

            case GroupBox group:
                group.ForeColor = UiTheme.TextSecondary;
                break;

            // A white panel on the work surface is a card: give it the line border and the corners.
            case Panel panel when panel.GetType() == typeof(Panel) || panel is TableLayoutPanel or FlowLayoutPanel:
                if (panel.BackColor.ToArgb() == UiTheme.SurfaceRaised.ToArgb()
                    && panel.Parent?.BackColor.ToArgb() == UiTheme.Surface.ToArgb())
                    AsCard(panel);
                break;

            default:
                return;
        }

        Styled.AddOrUpdate(control, Marker);
    }

    private static readonly object Marker = new();

    // ---- Fields ---------------------------------------------------------------------------------

    /// <summary>
    /// Draws a single-line box's border in line-strong, and in the accent while it has the focus.
    /// A <see cref="BorderStyle.FixedSingle"/> box is otherwise framed in black by Windows, whatever
    /// its colours, and there is no property for it.
    /// </summary>
    public static TextBox WithFieldBorder(this TextBox box)
    {
        if (box.BorderStyle != BorderStyle.FixedSingle || Styled.TryGetValue(box, out _)) return box;
        Frame(box);
        return box;
    }

    /// <summary>The same border for a flat drop-down list, on a window that is not restyled as a whole.</summary>
    public static ComboBox WithFieldBorder(this ComboBox combo)
    {
        if (combo.FlatStyle != FlatStyle.Flat || Styled.TryGetValue(combo, out _)) return combo;
        Frame(combo);
        return combo;
    }

    private static void Frame(Control field)
    {
        Styled.AddOrUpdate(field, Marker);

        var painter = new FieldBorder(field);
        if (field.IsHandleCreated) painter.Attach();
        field.HandleCreated += (_, _) => painter.Attach();
        // No release on HandleDestroyed: the painter lets go of the window itself at
        // WM_NCDESTROY. Released earlier, inside the box's own WM_DESTROY, the box never saw its
        // WM_NCDESTROY and kept a dead window handle; disposing it then failed with « opération
        // inter-threads non valide » and every screen with such a box could no longer be left.
        field.GotFocus += (_, _) => painter.Redraw();
        field.LostFocus += (_, _) => painter.Redraw();
        field.EnabledChanged += (_, _) => painter.Redraw();
    }

    private sealed class FieldBorder(Control box) : NativeWindow
    {
        private const int WmNcPaint = 0x0085, WmPaint = 0x000F;
        private const uint RdwInvalidate = 0x0001, RdwFrame = 0x0400, RdwUpdateNow = 0x0100;

        public void Attach()
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
            AssignHandle(box.Handle);
        }

        public void Redraw()
        {
            if (Handle != IntPtr.Zero) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwFrame | RdwUpdateNow);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg is WmNcPaint or WmPaint) PaintBorder();
        }

        private void PaintBorder()
        {
            var dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;

            try
            {
                using var graphics = Graphics.FromHdc(dc);
                var color = !box.Enabled ? UiTheme.Border
                          : box.ContainsFocus ? UiTheme.Accent
                          : UiTheme.BorderStrong;
                using var pen = new Pen(color);
                graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
            }
            finally
            {
                _ = ReleaseDC(Handle, dc);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    }

    // ---- Cards ----------------------------------------------------------------------------------

    /// <summary>
    /// A card: the panel keeps its size and content and gains a line border with rounded corners
    /// (radius-lg), the corners outside it painted in the colour behind the panel.
    /// </summary>
    public static T AsCard<T>(T panel, Color? border = null) where T : Panel
    {
        panel.Paint -= PaintCard;
        panel.Paint += PaintCard;
        panel.Resize -= InvalidateCard;
        panel.Resize += InvalidateCard;
        if (border is { } color) CardBorders.AddOrUpdate(panel, color);
        Styled.AddOrUpdate(panel, Marker);
        return panel;
    }

    private static readonly ConditionalWeakTable<Control, object> CardBorders = new();

    private static void InvalidateCard(object? sender, EventArgs e) => (sender as Control)?.Invalidate();

    private static void PaintCard(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel panel || panel.Width < 4 || panel.Height < 4) return;

        var outside = panel.Parent?.BackColor ?? UiTheme.Surface;
        var border = CardBorders.TryGetValue(panel, out var stored) ? (Color)stored : UiTheme.Border;
        var bounds = new Rectangle(0, 0, panel.ClientSize.Width - 1, panel.ClientSize.Height - 1);

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundedRectangle(bounds, UiTheme.RadiusCard);
        using (var corners = new Region(new Rectangle(0, 0, panel.ClientSize.Width, panel.ClientSize.Height)))
        {
            corners.Exclude(path);
            using var brush = new SolidBrush(outside);
            graphics.FillRegion(brush, corners);
        }

        using var pen = new Pen(border);
        graphics.DrawPath(pen, path);
    }

    /// <summary>A rectangle with rounded corners, for cards, pills and the active navigation entry.</summary>
    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(2 * radius, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- Tabs -----------------------------------------------------------------------------------

    /// <summary>
    /// Flat tabs: the selected one in accent-ink on white with a 2 px accent line beneath, the others
    /// in ink-muted on the work surface. The tabs keep their order, captions and size.
    /// </summary>
    public static void StyleTabs(TabControl tabs)
    {
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.DrawItem -= DrawTab;
        tabs.DrawItem += DrawTab;

        foreach (TabPage page in tabs.TabPages)
        {
            page.UseVisualStyleBackColor = false;
            page.BackColor = UiTheme.Surface;
        }

        tabs.ControlAdded += (_, e) =>
        {
            if (e.Control is TabPage page)
            {
                page.UseVisualStyleBackColor = false;
                page.BackColor = UiTheme.Surface;
            }
        };
    }

    private static void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabPages.Count) return;

        var selected = e.Index == tabs.SelectedIndex;
        var bounds = e.Bounds;
        var graphics = e.Graphics;

        using (var fill = new SolidBrush(selected ? UiTheme.SurfaceRaised : UiTheme.Surface))
            graphics.FillRectangle(fill, bounds);

        if (selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            graphics.FillRectangle(accent, bounds.Left, bounds.Bottom - 2, bounds.Width, 2);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis;
        if (tabs.RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;

        TextRenderer.DrawText(graphics, tabs.TabPages[e.Index].Text,
                              selected ? UiTheme.BodyBold : tabs.Font, bounds,
                              selected ? UiTheme.AccentInk : UiTheme.TextSecondary, flags);
    }

    // ---- Status strip ---------------------------------------------------------------------------

    /// <summary>Paints a tool strip in its own back colour, flat, without the gradient or the border.</summary>
    public sealed class FlatStripRenderer : ToolStripProfessionalRenderer
    {
        public FlatStripRenderer() => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.ToolStrip.BackColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.ForeColor;
            base.OnRenderItemText(e);
        }
    }
}
