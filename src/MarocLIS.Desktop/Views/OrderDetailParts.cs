using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Desktop.Views;

// The pieces the "Consultation d'un dossier" screen draws itself: the file's progress line, the
// tabs of its content card and the colour of its status pills. Kept beside the screen rather than
// in the shared theme, which other screens are restyled against.

/// <summary>The colour pairs of the screen's pills, from the design system's soft fills and inks.</summary>
internal static class FilePaint
{
    public static readonly (Color Back, Color Fore) Ok = (UiTheme.NormalBackground, UiTheme.Normal);

    /// <summary>A file's state: waiting in grey, under way in blue, released in green, cancelled in red.</summary>
    public static (Color Back, Color Fore) ForOrder(LabOrderStatus status) => status switch
    {
        LabOrderStatus.Draft or LabOrderStatus.Registered or LabOrderStatus.AwaitingCollection => EntryPaint.Waiting,
        LabOrderStatus.Validated or LabOrderStatus.Reported => Ok,
        LabOrderStatus.Cancelled => EntryPaint.Critical,
        _ => EntryPaint.Info
    };

    /// <summary>An analysis's state, on the same scale.</summary>
    public static (Color Back, Color Fore) ForItem(LabOrderItemStatus status) => status switch
    {
        LabOrderItemStatus.Pending => EntryPaint.Waiting,
        LabOrderItemStatus.Validated or LabOrderItemStatus.Reported => Ok,
        LabOrderItemStatus.Cancelled => EntryPaint.Critical,
        _ => EntryPaint.Info
    };

    /// <summary>A tube's state: to draw in amber, refused or discarded in red, the rest under way.</summary>
    public static (Color Back, Color Fore) ForSample(SampleStatus status) => status switch
    {
        SampleStatus.AwaitingCollection => EntryPaint.Warning,
        SampleStatus.Rejected or SampleStatus.Discarded => EntryPaint.Critical,
        SampleStatus.Stored => Ok,
        _ => EntryPaint.Info
    };

    /// <summary>A released state is written with a tick before it, as the mockup does.</summary>
    public static string Ticked(string text, bool released) => released ? "✓ " + text : text;
}

/// <summary>
/// The file's progress line: six stages, a dot each, joined by a bar — teal and ticked when
/// reached, a teal ring for the one the file waits on, grey ahead — with the stage's name and its
/// time or count beneath. Mirrored in Arabic.
/// </summary>
internal sealed class FileProgressLine : Control
{
    private IReadOnlyList<(FileStep Step, string Name, string Detail)> _steps = [];

    public FileProgressLine()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = UiTheme.Small;
        Height = 58;
        TabStop = false;
    }

    public void Show(IReadOnlyList<(FileStep Step, string Name, string Detail)> steps)
    {
        _steps = steps;
        AccessibleName = string.Join(" · ", steps.Select(s => $"{s.Name} {s.Detail}"));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.SurfaceRaised))
            g.FillRectangle(back, ClientRectangle);

        if (_steps.Count == 0) return;

        var rtl = RightToLeft == RightToLeft.Yes;
        var column = Width / _steps.Count;
        const int dot = 20;
        var align = rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
        const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                                     | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

        for (var i = 0; i < _steps.Count; i++)
        {
            var (step, name, detail) = _steps[i];
            var cell = EntryPaint.Mirror(new Rectangle(i * column, 0, column, Height), ClientRectangle, rtl);
            var dotBox = EntryPaint.Mirror(new Rectangle(i * column, 0, dot, dot), ClientRectangle, rtl);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // The bar to the next stage, teal once this one is reached and the next is too.
            if (i < _steps.Count - 1)
            {
                var reached = step.State == FileStageState.Done && _steps[i + 1].Step.State == FileStageState.Done;
                var bar = EntryPaint.Mirror(new Rectangle(i * column + dot + 2, dot / 2 - 1, column - dot - 4, 2),
                                            ClientRectangle, rtl);
                using var brush = new SolidBrush(reached ? UiTheme.Accent : UiTheme.Border);
                g.FillRectangle(brush, bar);
            }

            switch (step.State)
            {
                case FileStageState.Done:
                    using (var fill = new SolidBrush(UiTheme.Accent)) g.FillEllipse(fill, dotBox);
                    TextRenderer.DrawText(g, "✓", UiTheme.SmallBold, dotBox, UiTheme.TextOnAccent,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding);
                    break;
                case FileStageState.Current:
                    using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) g.FillEllipse(fill, dotBox);
                    using (var pen = new Pen(UiTheme.Accent, 3))
                        g.DrawEllipse(pen, dotBox.X + 1.5f, dotBox.Y + 1.5f, dot - 3, dot - 3);
                    break;
                default:
                    using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) g.FillEllipse(fill, dotBox);
                    using (var pen = new Pen(UiTheme.BorderStrong, 2))
                        g.DrawEllipse(pen, dotBox.X + 1, dotBox.Y + 1, dot - 2, dot - 2);
                    break;
            }

            var nameColour = step.State switch
            {
                FileStageState.Done => UiTheme.TextPrimary,
                FileStageState.Current => UiTheme.AccentInk,
                _ => UiTheme.TextSecondary
            };

            var text = new Rectangle(cell.X, dot + 5, cell.Width - 8, 17);
            if (rtl) text.X += 8;
            TextRenderer.DrawText(g, name, step.State == FileStageState.Ahead ? UiTheme.Small : UiTheme.SmallBold,
                                  text, nameColour, line | align);

            text.Y += 17;
            var font = step.At is not null && step.State == FileStageState.Done ? UiTheme.Mono(8.25f) : UiTheme.Small;
            TextRenderer.DrawText(g, detail, font, text, UiTheme.TextSecondary, line | align);
        }
    }
}

/// <summary>
/// The tabs of the file's content card: flat captions, the selected one in semibold ink over a
/// 2-pixel teal line, the others in muted ink; the line of the card under them all.
/// </summary>
internal sealed class FileTabStrip : Control
{
    private readonly List<string> _captions = [];
    private readonly List<Rectangle> _bounds = [];
    private int _selected;

    public FileTabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = UiTheme.Body;
        Height = 40;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public event EventHandler? SelectedIndexChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= Math.Max(1, _captions.Count) || value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetCaptions(params string[] captions)
    {
        _captions.Clear();
        _captions.AddRange(captions);
        AccessibleName = string.Join(" · ", captions);
        Invalidate();
    }

    private void Measure()
    {
        _bounds.Clear();
        var x = 12;
        foreach (var caption in _captions)
        {
            var width = TextRenderer.MeasureText(caption, UiTheme.BodyBold).Width + 20;
            _bounds.Add(EntryPaint.Mirror(new Rectangle(x, 0, width, Height - 1), ClientRectangle,
                                          RightToLeft == RightToLeft.Yes));
            x += width + 4;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Measure();
        var index = _bounds.FindIndex(b => b.Contains(e.Location));
        if (index >= 0) SelectedIndex = index;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyCode switch
        {
            Keys.Right => RightToLeft == RightToLeft.Yes ? -1 : 1,
            Keys.Left => RightToLeft == RightToLeft.Yes ? 1 : -1,
            _ => 0
        };
        if (step != 0 && _captions.Count > 0)
            SelectedIndex = (_selected + step + _captions.Count) % _captions.Count;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(UiTheme.SurfaceRaised)) g.FillRectangle(back, ClientRectangle);
        using (var line = new Pen(UiTheme.Border)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        Measure();
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix;
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;

        for (var i = 0; i < _captions.Count; i++)
        {
            var selected = i == _selected;
            var bounds = _bounds[i];

            if (selected)
            {
                using var accent = new SolidBrush(UiTheme.Accent);
                g.FillRectangle(accent, bounds.X, Height - 2, bounds.Width, 2);
            }

            TextRenderer.DrawText(g, _captions[i], selected ? UiTheme.BodyBold : UiTheme.Body, bounds,
                                  selected ? UiTheme.TextPrimary : UiTheme.TextSecondary, flags);

            if (selected && Focused)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(bounds, -3, -6));
        }
    }
}

/// <summary>A hairline in the line colour, between the parts of the file's header card.</summary>
internal sealed class FileRule : Control
{
    public FileRule()
    {
        Height = 1;
        BackColor = UiTheme.Border;
        TabStop = false;
    }
}
