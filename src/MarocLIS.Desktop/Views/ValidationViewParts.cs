using System.Drawing.Drawing2D;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The small painted parts shared by the « Validation biologique » and « Comptes rendus » screens:
/// the tone of a pill, the key drawn on a button, a framed multi-line box, and a card section
/// heading. Kept apart from the views, which are long enough with their behaviour.
/// </summary>
internal static class ValidationPaint
{
    /// <summary>The soft background of an abnormal result line (#FFF8F2), as in the mock-up.</summary>
    public static readonly Color AbnormalRow = Color.FromArgb(0xFF, 0xF8, 0xF2);

    /// <summary>The ok pair of the design system's pills: success-soft, success-ink.</summary>
    public static readonly (Color Back, Color Fore) Ok = (UiTheme.NormalBackground, UiTheme.Normal);

    public static (Color Back, Color Fore) Colours(ValidationTone tone) => tone switch
    {
        ValidationTone.Ok => Ok,
        ValidationTone.Info => EntryPaint.Info,
        ValidationTone.Warning => EntryPaint.Warning,
        ValidationTone.Critical => EntryPaint.Critical,
        _ => EntryPaint.Waiting
    };

    /// <summary>
    /// Draws a row of pills inside a grid cell, from the reading side, as many as fit; returns how
    /// many were drawn so the rest can go in the cell's tooltip.
    /// </summary>
    public static int DrawPills(Graphics graphics, Rectangle cell, IReadOnlyList<(string Text, (Color Back, Color Fore) Colours)> pills,
                                bool rightToLeft, int padding = 10)
    {
        var x = cell.X + padding;
        var drawn = 0;

        foreach (var (text, colours) in pills)
        {
            var size = EntryPaint.MeasurePill(text, UiTheme.SmallBold, 8);
            if (x + size.Width > cell.Right - 4 && drawn > 0) break;

            var bounds = EntryPaint.Mirror(
                new Rectangle(x, cell.Y + (cell.Height - size.Height) / 2,
                              Math.Min(size.Width, cell.Right - 4 - x), size.Height),
                cell, rightToLeft);
            EntryPaint.DrawPill(graphics, bounds, text, UiTheme.SmallBold, colours, rightToLeft);

            x += size.Width + 6;
            drawn++;
        }

        return drawn;
    }

    /// <summary>A caption above a field in a card: 12 px semibold in ink-muted.</summary>
    public static Label Caption() => new()
    {
        AutoSize = true,
        Font = UiTheme.SmallBold,
        ForeColor = UiTheme.TextSecondary,
        Margin = new Padding(0, 0, 0, 4),
        UseMnemonic = false
    };
}

/// <summary>
/// The key of a primary action drawn at the end of its button, at three quarters of the caption's
/// strength, as the design system shows « Valider la sélection  F9 ». Drawn rather than written into
/// the caption, which stays a plain resource text.
/// </summary>
internal static class ButtonKey
{
    private sealed class State
    {
        public string Key = string.Empty;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, State> Keys = new();

    private static readonly Font KeyFont = UiTheme.Mono(8.25f);

    public static void Show(Button button, string key)
    {
        var state = Keys.GetValue(button, b =>
        {
            b.Paint += Paint;
            b.RightToLeftChanged += (_, _) => Pad(b);
            return new State();
        });

        state.Key = key;
        Pad(button);
        button.Invalidate();
    }

    private static int Width(string key) =>
        key.Length == 0 ? 0 : TextRenderer.MeasureText(key, KeyFont, Size.Empty, TextFormatFlags.NoPadding).Width + 10;

    private static void Pad(Button button)
    {
        if (!Keys.TryGetValue(button, out var state)) return;

        var width = Width(state.Key);
        button.Padding = button.RightToLeft == RightToLeft.Yes
            ? new Padding(14 + width, 0, 14, 0)
            : new Padding(14, 0, 14 + width, 0);
    }

    private static void Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button || !Keys.TryGetValue(button, out var state) || state.Key.Length == 0) return;

        var font = KeyFont;
        var width = TextRenderer.MeasureText(state.Key, font, Size.Empty, TextFormatFlags.NoPadding).Width;
        var bounds = button.RightToLeft == RightToLeft.Yes
            ? new Rectangle(12, 0, width, button.Height)
            : new Rectangle(button.Width - 12 - width, 0, width, button.Height);

        var primary = button.BackColor.ToArgb() == UiTheme.Accent.ToArgb();
        var colour = !button.Enabled ? UiTheme.TextMuted
                   : primary ? Color.FromArgb(0xC2, 0xDB, 0xDE)
                   : UiTheme.TextSecondary;

        TextRenderer.DrawText(e.Graphics, state.Key, font, bounds, colour,
                              TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    /// <summary>« Plus d'actions ▾ »: the chevron drawn after the caption, where a font may lack the glyph.</summary>
    public static void ShowChevron(Button button)
    {
        void Pad() => button.Padding = button.RightToLeft == RightToLeft.Yes
            ? new Padding(26, 0, 12, 0)
            : new Padding(12, 0, 26, 0);

        Pad();
        button.RightToLeftChanged += (_, _) => Pad();
        button.Paint += (_, e) =>
        {
            var x = button.RightToLeft == RightToLeft.Yes ? 12 : button.Width - 20;
            var y = button.Height / 2 - 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(button.Enabled ? UiTheme.TextPrimary : UiTheme.TextMuted, 1.6f);
            e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y)]);
        };
    }
}

/// <summary>
/// A multi-line box in a line-strong frame with rounded corners, accent while it has the focus.
/// Windows draws no themed frame round a multi-line box, so the frame is the panel's.
/// </summary>
internal sealed class FramedTextBox : Panel
{
    public TextBox Box { get; } = new()
    {
        Multiline = true,
        BorderStyle = BorderStyle.None,
        Dock = DockStyle.Fill,
        Font = UiTheme.Body,
        BackColor = UiTheme.SurfaceRaised,
        ForeColor = UiTheme.TextPrimary,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true
    };

    public FramedTextBox()
    {
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = UiTheme.SurfaceRaised;
        Padding = new Padding(8, 6, 4, 6);
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.EnabledChanged += (_, _) =>
        {
            Box.BackColor = Box.Enabled ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken;
            BackColor = Box.BackColor;
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var behind = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            e.Graphics.FillRectangle(behind, ClientRectangle);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiChrome.RoundedRectangle(bounds, 6);
        using (var fill = new SolidBrush(BackColor))
            e.Graphics.FillPath(fill, path);
        using var pen = new Pen(!Box.Enabled ? UiTheme.Border : Box.Focused ? UiTheme.Accent : UiTheme.BorderStrong);
        e.Graphics.DrawPath(pen, path);
    }
}
