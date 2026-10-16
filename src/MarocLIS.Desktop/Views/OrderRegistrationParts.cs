using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Views;

// The small pieces the "Nouvelle demande" screen draws itself: rounded chips, the patient's
// initials, the suggestion list under a search box and the key a button answers to. Kept beside
// the screen rather than in the shared theme, which other screens are restyled against.

/// <summary>A rounded "pill" button: the frequent profiles and the catalogue's sector filter.</summary>
internal sealed class PillButton : Button
{
    private bool _hover;

    public PillButton(string text)
    {
        Text = text;
        Tag = UiTags.NoMnemonic;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;
        Height = 28;
        Cursor = Cursors.Hand;
        UseMnemonic = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>Pressed: the filter in force, drawn on accent-soft with an accent border.</summary>
    private bool _pressed;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Pressed
    {
        get => _pressed;
        set
        {
            if (_pressed == value) return;
            _pressed = value;
            AccessibleDescription = value ? "pressed" : null;
            Invalidate();
        }
    }

    public int PreferredWidth => TextRenderer.MeasureText(Text, Pressed ? UiTheme.BodyBold : Font).Width + 26;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            g.FillRectangle(back, ClientRectangle);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiChrome.RoundedRectangle(bounds, Height / 2);

        var fill = Pressed ? UiTheme.AccentSubtle : _hover ? UiTheme.SurfaceSunken : UiTheme.SurfaceRaised;
        var border = Pressed || Focused ? UiTheme.Accent : UiTheme.BorderStrong;
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        using (var pen = new Pen(border)) g.DrawPath(pen, path);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Pressed ? UiTheme.BodyBold : Font, ClientRectangle,
                              !Enabled ? UiTheme.TextMuted : Pressed ? UiTheme.AccentInk : UiTheme.TextPrimary, flags);
    }
}

/// <summary>The patient's initials in an accent circle, at the head of the selected patient's band.</summary>
internal sealed class InitialsBadge : Control
{
    public InitialsBadge()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(40, 40);
        Font = UiTheme.BodyBold;
        TabStop = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = UiTheme.Accent;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            g.FillRectangle(back, ClientRectangle);
        using (var brush = new SolidBrush(Fill))
            g.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, UiTheme.TextOnAccent,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                              | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// The list that opens under a search box while the clerk types: one row per match, drawn by the
/// screen. Never takes the focus, so typing carries on in the box; the box moves the highlight
/// with the arrow keys and picks with Enter.
/// </summary>
internal sealed class SuggestionList : ListBox
{
    public SuggestionList(int rowHeight)
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = rowHeight;
        BorderStyle = BorderStyle.FixedSingle;
        IntegralHeight = false;
        Font = UiTheme.Body;
        BackColor = UiTheme.SurfaceRaised;
        TabStop = false;
        Visible = false;
        SetStyle(ControlStyles.Selectable, false);
    }

    /// <summary>Shows <paramref name="rows"/>, up to <paramref name="visibleRows"/> high, the first highlighted.</summary>
    public void Show(IReadOnlyList<object> rows, int visibleRows)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        EndUpdate();

        if (Items.Count == 0)
        {
            Visible = false;
            return;
        }

        Height = Math.Min(Items.Count, visibleRows) * ItemHeight + 2;
        SelectedIndex = 0;
        Visible = true;
        BringToFront();
    }

    public void Step(int delta)
    {
        if (!Visible || Items.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Items.Count - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexFromPoint(e.Location);
        if (index >= 0 && index != SelectedIndex) SelectedIndex = index;
    }
}

/// <summary>
/// Writes the key a button answers to ("F12", "Échap") in small grey type after its caption,
/// as the design system's action bar shows them. The caption itself is left as it was, so the
/// shell's Ctrl+S still finds "Enregistrer" and the icon catalogue still matches it.
/// </summary>
internal static class ShortcutHints
{
    private static readonly Font HintFont = UiTheme.Mono(8.5f);

    public static void Attach(Button button, string hint)
    {
        button.Paint += (_, e) =>
        {
            var width = TextRenderer.MeasureText(hint, HintFont).Width;
            var color = button.BackColor.ToArgb() == UiTheme.Accent.ToArgb()
                ? Color.FromArgb(220, UiTheme.TextOnAccent)
                : UiTheme.TextSecondary;
            var area = new Rectangle(button.Width - width - 10, 0, width + 4, button.Height);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, hint, HintFont, area, color, flags);
        };

        button.Padding = new Padding(button.Padding.Left, 0,
                                     TextRenderer.MeasureText(hint, HintFont).Width + 14, 0);
    }

    public static int Width(string hint) => TextRenderer.MeasureText(hint, HintFont).Width + 14;
}

/// <summary>Draws a container's colour: a dot in a list, a tube in the summary.</summary>
internal static class TubeSwatch
{
    public static Color Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return UiTheme.TextMuted;
        try
        {
            return ColorTranslator.FromHtml(hex.StartsWith('#') ? hex : "#" + hex);
        }
        catch (Exception)
        {
            return UiTheme.TextMuted;
        }
    }

    /// <summary>A 10-pixel dot; a white cap (a sterile pot) gets an outline so it does not vanish.</summary>
    public static void Dot(Graphics g, Rectangle area, string? hex, int size = 10)
    {
        var colour = Parse(hex);
        var box = new Rectangle(area.Left, area.Top + (area.Height - size) / 2, size, size);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (colour.GetBrightness() > 0.92f)
        {
            using var pen = new Pen(UiTheme.TextSecondary);
            g.DrawRectangle(pen, box.X, box.Y, size - 1, size - 1);
            return;
        }

        using var brush = new SolidBrush(colour);
        g.FillEllipse(brush, box);
    }
}

/// <summary>The number of analyses in an accent pill, after the basket's heading.</summary>
internal sealed class CountBadge : Control
{
    public CountBadge()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Font = UiTheme.SmallBold;
        Size = new Size(24, 18);
        TabStop = false;
        Visible = false;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = Math.Max(22, TextRenderer.MeasureText(Text, Font).Width + 12);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            g.FillRectangle(back, ClientRectangle);
        using var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
        using (var fill = new SolidBrush(UiTheme.Accent)) g.FillPath(fill, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, UiTheme.TextOnAccent,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>One container of the "Tubes à prélever" summary: a tube in its cap colour, then "EDTA 4 mL ×1".</summary>
internal sealed class TubeChip : Control
{
    private readonly string _colour;

    public TubeChip(string text, string colour)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _colour = colour;
        Text = text;
        Font = UiTheme.Body;
        TabStop = false;
        Margin = new Padding(0, 0, 14, 0);
        Size = new Size(TextRenderer.MeasureText(text, Font).Width + 30, 26);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceSunken))
            g.FillRectangle(back, ClientRectangle);

        var colour = TubeSwatch.Parse(_colour);
        var tube = new Rectangle(0, (Height - 22) / 2, 10, 22);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiChrome.RoundedRectangle(tube, 3))
        {
            if (colour.GetBrightness() > 0.92f)
            {
                using var pen = new Pen(UiTheme.TextSecondary);
                g.DrawPath(pen, path);
            }
            else
            {
                using var fill = new SolidBrush(colour);
                g.FillPath(fill, path);
            }
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(16, 0, Width - 16, Height), UiTheme.TextPrimary, flags);
    }
}
