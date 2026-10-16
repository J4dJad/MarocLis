using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// A flat tab of the « Comptes rendus » screen: its caption, then its count — in a pill when the
/// tab holds work to do — and a 2 px accent line under the selected one, as in the mock-up.
/// </summary>
internal sealed class FlatTab : Control
{
    private bool _selected;
    private int _count;
    private (Color Back, Color Fore)? _pill;

    public FlatTab()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = UiTheme.Body;
        Cursor = Cursors.Hand;
        Height = 34;
        Margin = new Padding(0, 0, 4, 0);
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
    }

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

    /// <summary>The count, in a pill of <paramref name="pill"/>'s colours, or after a « · » when null.</summary>
    public void Show(string caption, int count, (Color Back, Color Fore)? pill)
    {
        Text = caption;
        _count = count;
        _pill = pill;
        AccessibleName = $"{caption} {count}";
        Width = Measure();
        Invalidate();
    }

    private string CountText => _pill is null ? $" · {_count}" : _count.ToString();

    private int Measure()
    {
        var caption = TextRenderer.MeasureText(Text, UiTheme.BodyBold, Size.Empty, TextFormatFlags.NoPadding).Width;
        var count = _pill is null
            ? TextRenderer.MeasureText(CountText, UiTheme.Body, Size.Empty, TextFormatFlags.NoPadding).Width
            : EntryPaint.MeasurePill(CountText, UiTheme.SmallBold, 7).Width + 6;
        return caption + count + 24;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        using (var behind = new SolidBrush(Parent?.BackColor ?? UiTheme.Surface))
            graphics.FillRectangle(behind, ClientRectangle);

        var rtl = RightToLeft == RightToLeft.Yes;
        var font = _selected ? UiTheme.BodyBold : UiTheme.Body;
        var ink = _selected ? UiTheme.TextPrimary : UiTheme.TextSecondary;
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                      | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        var captionWidth = TextRenderer.MeasureText(Text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
        var bounds = new Rectangle(0, 0, Width, Height - 2);
        var caption = EntryPaint.Mirror(new Rectangle(12, 0, captionWidth, Height - 2), bounds, rtl);
        TextRenderer.DrawText(graphics, Text, font, caption, ink, flags | (rtl ? TextFormatFlags.RightToLeft : 0));

        var x = 12 + captionWidth;
        if (_pill is { } colours)
        {
            var size = EntryPaint.MeasurePill(CountText, UiTheme.SmallBold, 7);
            var pill = EntryPaint.Mirror(new Rectangle(x + 6, (Height - 2 - size.Height) / 2, size.Width, size.Height), bounds, rtl);
            EntryPaint.DrawPill(graphics, pill, CountText, UiTheme.SmallBold, colours, rtl);
        }
        else
        {
            var width = TextRenderer.MeasureText(CountText, UiTheme.Body, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(graphics, CountText, UiTheme.Body,
                                  EntryPaint.Mirror(new Rectangle(x, 0, width, Height - 2), bounds, rtl), ink,
                                  flags | (rtl ? TextFormatFlags.RightToLeft : 0));
        }

        if (_selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            graphics.FillRectangle(accent, 0, Height - 2, Width, 2);
        }

        if (Focused)
        {
            var focus = new Rectangle(2, 2, Width - 5, Height - 7);
            ControlPaint.DrawFocusRectangle(graphics, focus);
        }
    }
}

/// <summary>
/// The sketch of a report's first page shown in « Aperçu »: the laboratory's site, the version, the
/// patient and the file, then grey lines where the results go, and the signatory at the foot. A
/// sketch with the selected file's own data, not a rendering of the document — « Ouvrir en PDF »
/// opens the document itself.
/// </summary>
internal sealed class ReportPagePreview : Control
{
    private string _header = string.Empty, _version = string.Empty, _patient = string.Empty,
                   _detail = string.Empty, _footer = string.Empty;

    public ReportPagePreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.SurfaceSunken;
        TabStop = false;
    }

    public void Show(string header, string version, string patient, string detail, string footer)
    {
        (_header, _version, _patient, _detail, _footer) = (header, version, patient, detail, footer);
        AccessibleName = $"{patient} {detail} {version}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (var behind = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            graphics.FillRectangle(behind, ClientRectangle);

        using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 6))
        using (var sunken = new SolidBrush(UiTheme.SurfaceSunken))
            graphics.FillPath(sunken, path);

        // An A4 page, as tall as fits with 12 px round it.
        var height = Height - 24;
        var width = Math.Min(Width - 24, (int)(height / 1.3));
        var page = new Rectangle((Width - width) / 2, 12, width, height);

        graphics.SmoothingMode = SmoothingMode.None;
        graphics.FillRectangle(Brushes.White, page);
        using (var border = new Pen(UiTheme.Border))
            graphics.DrawRectangle(border, page);

        var rtl = RightToLeft == RightToLeft.Yes;
        var inner = Rectangle.Inflate(page, -14, -14);
        const TextFormatFlags single = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                       | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var near = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        var far = rtl ? TextFormatFlags.Left | TextFormatFlags.RightToLeft : TextFormatFlags.Right;

        using var tiny = new Font(UiTheme.Small.FontFamily, 6.75f, FontStyle.Bold);
        using var tinyMono = UiTheme.Mono(6.75f);
        using var name = new Font(UiTheme.Small.FontFamily, 7.5f, FontStyle.Bold);
        using var note = new Font(UiTheme.Small.FontFamily, 6.25f);

        TextRenderer.DrawText(graphics, _version, tinyMono, new Rectangle(inner.X, inner.Y, inner.Width, 12),
                              UiTheme.TextSecondary, single | far);
        TextRenderer.DrawText(graphics, _header, tiny, new Rectangle(inner.X, inner.Y, inner.Width - 30, 12),
                              UiTheme.Accent, single | near);
        TextRenderer.DrawText(graphics, _patient, name, new Rectangle(inner.X, inner.Y + 20, inner.Width, 14),
                              UiTheme.TextPrimary, single | near);
        TextRenderer.DrawText(graphics, _detail, note, new Rectangle(inner.X, inner.Y + 36, inner.Width, 12),
                              UiTheme.TextSecondary, single | near);

        using var line = new SolidBrush(Color.FromArgb(0xE6, 0xEB, 0xEF));
        var y = inner.Y + 54;
        graphics.FillRectangle(line, inner.X, y, inner.Width, 1);
        y += 10;

        int[] widths = [90, 82, 88, 70, 86, 78, 84, 74, 80];
        foreach (var percent in widths)
        {
            if (y > inner.Bottom - 24) break;
            var w = inner.Width * percent / 100;
            var bar = EntryPaint.Mirror(new Rectangle(inner.X, y, w, 6), inner, rtl);
            graphics.FillRectangle(line, bar);
            y += 12;
        }

        TextRenderer.DrawText(graphics, _footer, note, new Rectangle(inner.X, inner.Bottom - 12, inner.Width, 12),
                              UiTheme.TextSecondary, single | far);
    }
}
