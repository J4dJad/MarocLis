using System.Drawing.Drawing2D;
using System.Globalization;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Infrastructure.Queries;

namespace MarocLIS.Desktop.Views;

// The pieces the patient screen draws itself: the search box with its magnifier, the key a button
// answers to, the "Plus d'actions" chevron and the order cards of the selected patient. Kept beside
// the screen rather than in the shared theme, which other screens are restyled against.

internal static class PatientPaint
{
    public static (Color Back, Color Fore) Colours(PatientListTone tone) => tone switch
    {
        PatientListTone.Running => EntryPaint.Info,
        PatientListTone.Done => (UiTheme.NormalBackground, UiTheme.Normal),
        PatientListTone.Warning => EntryPaint.Warning,
        PatientListTone.Critical => EntryPaint.Critical,
        _ => EntryPaint.Waiting
    };

    private static readonly Font HintFont = UiTheme.Mono(8.5f);

    /// <summary>
    /// Writes the key a button answers to ("Entrée", "Ctrl+N") in small type at the button's far
    /// end, on the side the reading direction ends on; the caption keeps its own text, so the icon
    /// catalogue still matches it.
    /// </summary>
    public static void KeyHint(Button button, Func<string> hint)
    {
        void Pad()
        {
            var width = TextRenderer.MeasureText(hint(), HintFont).Width + 14;
            button.Padding = button.RightToLeft == RightToLeft.Yes
                ? new Padding(width, 0, 10, 0)
                : new Padding(10, 0, width, 0);
        }

        Pad();
        button.RightToLeftChanged += (_, _) => Pad();
        button.TextChanged += (_, _) => Pad();

        button.Paint += (_, e) =>
        {
            var text = hint();
            var width = TextRenderer.MeasureText(text, HintFont).Width;
            var rtl = button.RightToLeft == RightToLeft.Yes;
            var area = rtl
                ? new Rectangle(8, 0, width + 4, button.Height)
                : new Rectangle(button.Width - width - 10, 0, width + 4, button.Height);
            var colour = !button.Enabled ? UiTheme.TextMuted
                : button.BackColor.ToArgb() == UiTheme.Accent.ToArgb() ? Color.FromArgb(0xC2, 0xDB, 0xDE)
                : UiTheme.TextSecondary;
            TextRenderer.DrawText(e.Graphics, text, HintFont, area, colour,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        };
    }

    /// <summary>« Plus d'actions ▾ »: the chevron drawn at the far end, where a font may lack the glyph.</summary>
    public static void Chevron(Button button)
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

    /// <summary>A borderless box inside a framed host, the magnifier at the start of the line.</summary>
    public static Panel SearchHost(TextBox box)
    {
        var host = new Panel { BackColor = UiTheme.SurfaceRaised, Height = 34, Tag = UiTags.OwnLayout };
        box.BorderStyle = BorderStyle.None;
        box.Font = UiTheme.Body;
        box.BackColor = UiTheme.SurfaceRaised;
        box.ForeColor = UiTheme.TextPrimary;
        host.Controls.Add(box);

        bool Rtl() => host.RightToLeft == RightToLeft.Yes;

        void Place()
        {
            var width = Math.Max(40, host.Width - 46);
            box.Bounds = new Rectangle(Rtl() ? 10 : 36, (host.Height - box.PreferredHeight) / 2 + 1,
                                       width, box.PreferredHeight);
            host.Invalidate();
        }

        host.Paint += (_, e) =>
        {
            var focused = box.Focused;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(host.Parent?.BackColor ?? UiTheme.Surface))
                e.Graphics.FillRectangle(back, host.ClientRectangle);
            using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, host.Width - 1, host.Height - 1),
                                                        UiTheme.RadiusControl))
            {
                using var fill = new SolidBrush(UiTheme.SurfaceRaised);
                e.Graphics.FillPath(fill, path);
                using var pen = new Pen(focused ? UiTheme.Accent : UiTheme.BorderStrong, focused ? 2 : 1);
                e.Graphics.DrawPath(pen, path);
            }

            if (Icons.Render(Glyph.Search, UiTheme.TextSecondary, 16) is { } icon)
                e.Graphics.DrawImage(icon, Rtl() ? host.Width - 27 : 11, (host.Height - 16) / 2, 16, 16);
        };

        host.Resize += (_, _) => Place();
        host.RightToLeftChanged += (_, _) => Place();
        box.GotFocus += (_, _) => host.Invalidate();
        box.LostFocus += (_, _) => host.Invalidate();
        return host;
    }

    /// <summary>A hairline across the top of a control, in the line colour.</summary>
    public static void TopRule(Control control) =>
        control.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, control.Width, 0);
        };
}

/// <summary>
/// One order of the selected patient: accession number and date, status and priority pills, the
/// number of analyses and the prescriber. A click, Enter or Space opens the file.
/// </summary>
internal sealed class PatientOrderCard : Control
{
    private readonly PatientOrderRow _order;
    private readonly string _status;
    private readonly string? _priority;
    private readonly string _detail;
    private bool _hover;

    public PatientOrderCard(PatientOrderRow order, string status, string? priority, string detail, bool opens)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _order = order;
        _status = status;
        _priority = priority;
        _detail = detail;
        Opens = opens;
        Height = 80;
        TabStop = true;
        Font = UiTheme.Body;
        Margin = new Padding(0, 0, 0, 8);
        Cursor = opens ? Cursors.Hand : Cursors.Default;
        AccessibleRole = AccessibleRole.Link;
        AccessibleName = $"{order.AccessionNumber} {status}";
    }

    public Guid OrderId => _order.Id;

    public bool Opens { get; }

    public event EventHandler? Open;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        if (Opens) Open?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.Enter or Keys.Space) || !Opens) return;
        e.Handled = true;
        Open?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rtl = RightToLeft == RightToLeft.Yes;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            g.FillRectangle(back, ClientRectangle);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), UiTheme.RadiusControl))
        {
            using var fill = new SolidBrush(_hover && Opens ? UiTheme.SurfaceSunken : UiTheme.SurfaceRaised);
            g.FillPath(fill, path);
            using var pen = new Pen(Focused || (_hover && Opens) ? UiTheme.Accent : UiTheme.Border);
            g.DrawPath(pen, path);
        }

        var inner = new Rectangle(12, 0, Width - 24, Height);
        var line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                   | TextFormatFlags.VerticalCenter;
        var start = rtl ? line | TextFormatFlags.Right | TextFormatFlags.RightToLeft : line;
        var end = rtl ? line : line | TextFormatFlags.Right;

        var date = _order.RegisteredAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        TextRenderer.DrawText(g, _order.AccessionNumber, UiTheme.MonospaceBold,
                              new Rectangle(inner.X, 8, inner.Width, 20), UiTheme.TextPrimary, start);
        TextRenderer.DrawText(g, date, UiTheme.Monospace, new Rectangle(inner.X, 8, inner.Width, 20),
                              UiTheme.TextSecondary, end);

        var x = inner.X;
        foreach (var (text, tone) in new[] { (_status, PatientListRules.StatusTone(_order.Status)),
                                             (_priority, PatientListRules.PriorityTone(_order.Priority)) })
        {
            if (string.IsNullOrEmpty(text)) continue;
            var size = EntryPaint.MeasurePill(text, UiTheme.SmallBold);
            var pill = EntryPaint.Mirror(new Rectangle(x, 32, size.Width, size.Height), inner, rtl);
            EntryPaint.DrawPill(g, pill, text, UiTheme.SmallBold, PatientPaint.Colours(tone), rtl);
            x += size.Width + 6;
        }

        TextRenderer.DrawText(g, _detail, UiTheme.Small, new Rectangle(inner.X, 54, inner.Width, 20),
                              UiTheme.TextSecondary, start);
    }
}
