using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Views;

// The small pieces the "Prélèvements" and "Réception des échantillons" screens draw themselves: the
// tube in its cap colour, the fasting band's clock and its Oui / Non / Ne sait pas segments, and
// the non-conformity toggles. Kept beside the screens rather than in the shared theme.

/// <summary>A tube drawn in its cap colour, rounded more at the bottom than at the top.</summary>
internal static class CollectionTubeCap
{
    public static void Draw(Graphics g, Rectangle tube, string? hex)
    {
        var colour = TubeSwatch.Parse(hex);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = Shape(tube))
        {
            // A white cap (a sterile pot) gets an outline so it does not vanish on white.
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

        g.SmoothingMode = smoothing;
    }

    private static GraphicsPath Shape(Rectangle r)
    {
        var top = Math.Min(8, r.Width);
        var bottom = Math.Min(14, r.Width);
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, top, top, 180, 90);
        path.AddArc(r.Right - top, r.Top, top, top, 270, 90);
        path.AddArc(r.Right - bottom, r.Bottom - bottom, bottom, bottom, 0, 90);
        path.AddArc(r.Left, r.Bottom - bottom, bottom, bottom, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A tube in its cap colour as a control, at the head of the scanned tube's band.</summary>
internal sealed class CollectionTubeCapControl : Control
{
    private string? _hex;

    public CollectionTubeCapControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Size = new Size(14, 42);
        TabStop = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? ColourHex
    {
        get => _hex;
        set { _hex = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            e.Graphics.FillRectangle(back, ClientRectangle);
        if (_hex is not null) CollectionTubeCap.Draw(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), _hex);
    }
}

/// <summary>The clock at the head of the fasting band, in the band's warning ink.</summary>
internal sealed class CollectionClockGlyph : Control
{
    public CollectionClockGlyph()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Size = new Size(20, 20);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.AbnormalBackground))
            g.FillRectangle(back, ClientRectangle);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(ForeColor, 1.6f);
        var size = Math.Min(Width, Height) - 4;
        var circle = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        g.DrawEllipse(pen, circle);
        var centre = new PointF(circle.X + size / 2f, circle.Y + size / 2f);
        g.DrawLine(pen, centre, new PointF(centre.X, circle.Y + size * 0.22f));
        g.DrawLine(pen, centre, new PointF(centre.X + size * 0.25f, centre.Y + size * 0.15f));
    }
}

/// <summary>
/// One segment of a choice such as « Oui / Non / Ne sait pas »: white, or in the accent once chosen.
/// Sits in a row of segments with a line between them.
/// </summary>
internal sealed class CollectionSegmentButton : Button
{
    private bool _pressed;

    public CollectionSegmentButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = UiTheme.Body;
        Height = 32;
        Margin = new Padding(0);
        Cursor = Cursors.Hand;
        Tag = UiTags.NoMnemonic;
        UseMnemonic = false;
    }

    /// <summary>The colour of the line around and between the segments.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color LineColor { get; set; } = Color.FromArgb(0xE0, 0xB4, 0x8F);

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

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = TextRenderer.MeasureText(Text, UiTheme.BodyBold).Width + 30;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var fill = new SolidBrush(_pressed ? UiTheme.Accent : UiTheme.SurfaceRaised))
            g.FillRectangle(fill, ClientRectangle);

        using (var pen = new Pen(Focused && !_pressed ? UiTheme.Accent : LineColor))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, _pressed ? UiTheme.BodyBold : UiTheme.Body, ClientRectangle,
                              !Enabled ? UiTheme.TextMuted : _pressed ? UiTheme.TextOnAccent : UiTheme.TextPrimary, flags);
    }
}

/// <summary>
/// A non-conformity at reception: a check box drawn as a button, outlined while off and in the
/// warning colours once signalled. Still a <see cref="CheckBox"/>, so Space toggles it and the
/// screen reads <see cref="CheckBox.Checked"/> as before.
/// </summary>
internal sealed class CollectionDefectToggle : CheckBox
{
    public CollectionDefectToggle()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = false;
        Font = UiTheme.Body;
        Height = 30;
        Margin = new Padding(0, 0, 6, 6);
        Cursor = Cursors.Hand;
        UseMnemonic = false;
    }

    // The screen gives a check box its Alt+letter mark: it is drawn underlined, as on a button.
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = TextRenderer.MeasureText(Mnemonics.Plain(Text), UiTheme.BodyBold).Width + 30;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var behind = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            g.FillRectangle(behind, ClientRectangle);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 6);

        using (var fill = new SolidBrush(Checked ? UiTheme.AbnormalBackground : UiTheme.SurfaceRaised))
            g.FillPath(fill, path);

        var border = Focused ? UiTheme.Accent : Checked ? UiTheme.Abnormal : UiTheme.BorderStrong;
        using (var pen = new Pen(border)) g.DrawPath(pen, path);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Checked ? UiTheme.BodyBold : UiTheme.Body, ClientRectangle,
                              !Enabled ? UiTheme.TextMuted : Checked ? UiTheme.Abnormal : UiTheme.TextPrimary, flags);
    }
}

/// <summary>A label above its field, as the design system's forms stack them.</summary>
internal static class CollectionStackedField
{
    public static TableLayoutPanel Of(Label label, Control field, int width, Padding margin)
    {
        label.Font = UiTheme.SmallBold;
        label.ForeColor = UiTheme.TextSecondary;
        label.AutoSize = true;
        label.Margin = new Padding(0, 0, 0, 4);

        field.Margin = new Padding(0);
        field.Dock = DockStyle.Fill;

        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Margin = margin,
            Padding = new Padding(0),
            Width = width,
            BackColor = UiTheme.SurfaceRaised
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(label, 0, 0);
        stack.Controls.Add(field, 0, 1);
        return stack;
    }
}

/// <summary>
/// Like <see cref="ShortcutHints"/>, for a key whose name is translated (« Entrée », « Enter »):
/// the hint is read at each paint, so it follows a change of language.
/// </summary>
internal static class CollectionKeyHint
{
    private static readonly Font HintFont = UiTheme.Mono(8.5f);

    public static void Attach(Button button, Func<string> hint)
    {
        button.Paint += (_, e) =>
        {
            var text = hint();
            var width = TextRenderer.MeasureText(text, HintFont).Width;
            var color = button.BackColor.ToArgb() == UiTheme.Accent.ToArgb()
                ? Color.FromArgb(220, UiTheme.TextOnAccent)
                : UiTheme.TextSecondary;
            var rtl = button.RightToLeft == RightToLeft.Yes;
            var area = rtl
                ? new Rectangle(6, 0, width + 4, button.Height)
                : new Rectangle(button.Width - width - 10, 0, width + 4, button.Height);
            TextRenderer.DrawText(e.Graphics, text, HintFont, area, color,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        };
    }

    /// <summary>Makes room for the hint after the caption, on the side the caption ends.</summary>
    public static void Reserve(Button button, string hint)
    {
        var room = TextRenderer.MeasureText(hint, HintFont).Width + 14;
        button.Padding = button.RightToLeft == RightToLeft.Yes
            ? new Padding(room, 0, button.Padding.Left, 0)
            : new Padding(button.Padding.Left, 0, room, 0);
        button.Invalidate();
    }
}
