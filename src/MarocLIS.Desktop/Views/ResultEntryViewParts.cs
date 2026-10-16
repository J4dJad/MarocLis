using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The small painted parts of the result entry screen: status pills, sector chips and the
/// section rows of the grid. Kept apart from the view, which is long enough with its behaviour.
/// </summary>
internal static class EntryPaint
{
    /// <summary>The colour pairs of the design system's pills: soft fill, matching ink.</summary>
    public static readonly (Color Back, Color Fore) Critical = (UiTheme.CriticalBackground, UiTheme.CriticalInk);
    public static readonly (Color Back, Color Fore) Warning = (UiTheme.AbnormalBackground, UiTheme.Abnormal);
    public static readonly (Color Back, Color Fore) Info = (UiTheme.InfoBackground, UiTheme.Info);
    public static readonly (Color Back, Color Fore) Waiting = (UiTheme.SurfaceSunken, UiTheme.TextPrimary);

    /// <summary>The background of a section row in the results grid (#FAFBFC).</summary>
    public static readonly Color SectionBackground = Color.FromArgb(0xFA, 0xFB, 0xFC);

    /// <summary>A pill's size for its text: 10 px either side, its height from the font.</summary>
    public static Size MeasurePill(string text, Font font, int horizontalPadding = 10)
    {
        var size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(size.Width + 2 * horizontalPadding, size.Height + 4);
    }

    /// <summary>Draws a fully rounded pill with its text centred.</summary>
    public static void DrawPill(Graphics graphics, Rectangle bounds, string text, Font font,
                                (Color Back, Color Fore) colours, bool rightToLeft)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = UiChrome.RoundedRectangle(bounds, bounds.Height / 2))
        using (var brush = new SolidBrush(colours.Back))
            graphics.FillPath(brush, path);

        graphics.SmoothingMode = smoothing;

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        if (rightToLeft) flags |= TextFormatFlags.RightToLeft;

        TextRenderer.DrawText(graphics, text, font, bounds, colours.Fore, flags);
    }

    /// <summary>
    /// <paramref name="rectangle"/> mirrored inside <paramref name="within"/> when the layout runs
    /// right to left; unchanged otherwise. The painted parts are laid out left to right and mirrored
    /// here, rather than written twice.
    /// </summary>
    public static Rectangle Mirror(Rectangle rectangle, Rectangle within, bool rightToLeft) =>
        rightToLeft
            ? new Rectangle(within.Right - (rectangle.X - within.Left) - rectangle.Width,
                            rectangle.Y, rectangle.Width, rectangle.Height)
            : rectangle;

    /// <summary>A font of the same face as <paramref name="like"/>, at another size.</summary>
    public static Font Resized(Font like, float size) => new(like.FontFamily, size, like.Style);
}

/// <summary>A status pill: a soft fill, an ink, and a short text. Sizes itself to the text.</summary>
internal sealed class PillLabel : Control
{
    private (Color Back, Color Fore) _colours = EntryPaint.Waiting;

    public PillLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Font = UiTheme.SmallBold;
        BackColor = Color.Transparent;
        TabStop = false;
    }

    public void Show(string text, (Color Back, Color Fore) colours)
    {
        _colours = colours;
        Text = text;
        Visible = !string.IsNullOrEmpty(text);
        Size = EntryPaint.MeasurePill(text, Font);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => EntryPaint.MeasurePill(Text, Font);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EntryPaint.DrawPill(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Text, Font, _colours,
                            RightToLeft == RightToLeft.Yes);
    }
}

/// <summary>
/// A filter chip: a pill-shaped toggle, outlined when off, in accent-soft with an accent outline
/// when on.
/// </summary>
internal sealed class ChipButton : Button
{
    private bool _selected;

    public ChipButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = UiTheme.Small;
        Height = 26;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 6, 6);
        // The caption is a department's name: no Alt+letter mark in it.
        Tag = UiTags.NoMnemonic;
    }

    /// <summary>The value the chip filters on; null for "all".</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Value { get; init; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            AccessibleDescription = value ? "selected" : null;
            Invalidate();
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = TextRenderer.MeasureText(Text, UiTheme.SmallBold, Size.Empty, TextFormatFlags.NoPadding).Width + 22;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        using (var behind = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            graphics.FillRectangle(behind, ClientRectangle);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = UiChrome.RoundedRectangle(bounds, bounds.Height / 2))
        {
            using (var fill = new SolidBrush(_selected ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised))
                graphics.FillPath(fill, path);

            using var pen = new Pen(_selected ? UiTheme.Accent : Focused ? UiTheme.Accent : UiTheme.BorderStrong);
            graphics.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;

        TextRenderer.DrawText(graphics, Text, _selected ? UiTheme.SmallBold : UiTheme.Small, bounds,
                              _selected ? UiTheme.AccentInk : UiTheme.TextPrimary, flags);
    }
}

/// <summary>
/// A white strip docked at the foot of the screen, with the line of the design system along its top.
/// A <see cref="Panel"/> subclass, so the screen chrome does not take it for a card.
/// </summary>
internal sealed class EntryStrip : Panel
{
    public EntryStrip()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        Padding = new Padding(0, 1, 0, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

/// <summary>The tag of a section row of the results grid: one per analysis, "CODE · SECTEUR".</summary>
internal sealed record EntrySection(string Caption);
