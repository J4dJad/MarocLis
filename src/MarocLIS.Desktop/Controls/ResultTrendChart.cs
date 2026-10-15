using System.ComponentModel;
using System.Drawing.Drawing2D;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.Desktop.Controls;

/// <summary>One value on the antériorité chart.</summary>
public sealed record ResultTrendPoint(DateTimeOffset At, decimal Value, string? Operator = null,
                                      string? AccessionNumber = null);

/// <summary>
/// What the antériorité chart draws: the patient's validated values, the reference interval in force
/// today, and optionally the value on screen that is not validated yet.
/// </summary>
public sealed record ResultTrendSeries(
    IReadOnlyList<ResultTrendPoint> Points,
    decimal? LowerLimit,
    decimal? UpperLimit,
    int Decimals,
    string? UnitSymbol,
    ResultTrendPoint? Current = null);

/// <summary>
/// The patient's previous values for one parameter, painted directly — the antériorité chart.
/// <para>
/// Drawn the way <see cref="LeveyJenningsChart"/> is, and for the same reasons: the reference
/// interval is a band, not a pair of gridlines, so inside-versus-outside reads before any number;
/// a value out of the interval is set apart by colour <b>and</b> by shape, since the screen may be
/// read in monochrome; and the value being entered or validated now is hollow, because it is not
/// a released figure and must not look like one.
/// </para>
/// <para>
/// The time axis is in real time, not in rank: three values in a week and one a year later are not
/// evenly spaced, and spacing them evenly would show a gradual drift where there was a jump.
/// </para>
/// <para>
/// In Arabic the window mirrors and the chart with it, so time then runs from right to left like
/// the rest of the layout. Every label is drawn inside its own rectangle with an alignment rather
/// than at a bare x position, so it stays beside its tick whichever way the drawing runs.
/// </para>
/// </summary>
public sealed class ResultTrendChart : Control
{
    private ResultTrendSeries? _series;
    private int _hoverIndex = -1;
    private readonly ToolTip _tooltip = new() { ShowAlways = true };

    /// <summary>Plot area insets: room for the value labels on the left and the dates underneath.</summary>
    private const int MarginLeft = 64;
    private const int MarginRight = 20;
    private const int MarginTop = 16;
    private const int MarginBottom = 36;

    /// <summary>Distance kept between the first and last point and the frame.</summary>
    private const int Inset = 16;

    public ResultTrendChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint, true);

        BackColor = UiTheme.SurfaceRaised;
        MinimumSize = new Size(320, 200);
    }

    /// <summary>Shown in place of the chart when there is nothing to plot.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "—";

    /// <summary>Names the hollow point in its tooltip.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CurrentText { get; set; } = string.Empty;

    public void SetSeries(ResultTrendSeries? series)
    {
        _series = series;
        _hoverIndex = -1;
        Invalidate();
    }

    /// <summary>The validated points followed by the current one, in the order they are drawn.</summary>
    private List<(ResultTrendPoint Point, bool IsCurrent)> Plotted()
    {
        var all = new List<(ResultTrendPoint, bool)>();
        if (_series is null) return all;

        all.AddRange(_series.Points.Select(p => (p, false)));
        if (_series.Current is { } current) all.Add((current, true));

        return all;
    }

    // ---- Painting ---------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var plot = PlotArea;
        if (plot.Width <= 0 || plot.Height <= 0) return;

        var points = Plotted();

        if (_series is null || points.Count == 0)
        {
            DrawCentredText(g, EmptyText, plot);
            return;
        }

        var scale = Mapping.For(_series, points, plot);

        DrawValueAxis(g, plot, scale);
        DrawReferenceBand(g, plot, scale);
        DrawSeries(g, plot, scale, points);
        DrawDateAxis(g, plot, scale, points);

        using var framePen = new Pen(UiTheme.BorderStrong);
        g.DrawRectangle(framePen, plot);

        // Only the value on screen: it is drawn, and the absence of anything to compare it with
        // is said rather than left to be inferred from a lone dot.
        if (_series.Points.Count == 0) DrawCaption(g, plot, EmptyText);
    }

    private Rectangle PlotArea => new(
        MarginLeft, MarginTop,
        Math.Max(0, ClientSize.Width - MarginLeft - MarginRight),
        Math.Max(0, ClientSize.Height - MarginTop - MarginBottom));

    private static void DrawCentredText(Graphics g, string text, Rectangle plot)
    {
        using var brush = new SolidBrush(UiTheme.TextMuted);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        g.DrawString(text, UiTheme.Body, brush, plot, format);
    }

    /// <summary>A muted line at the top left of the plot, for a chart drawn with a single value.</summary>
    private static void DrawCaption(Graphics g, Rectangle plot, string text)
    {
        using var brush = new SolidBrush(UiTheme.TextMuted);
        using var format = new StringFormat { Alignment = StringAlignment.Near };

        g.DrawString(text, UiTheme.Small, brush,
                     new RectangleF(plot.X + 8, plot.Y + 4, plot.Width - 16, 18), format);
    }

    /// <summary>Faint gridlines on round values, labelled in the parameter's unit.</summary>
    private void DrawValueAxis(Graphics g, Rectangle plot, Mapping scale)
    {
        using var gridPen = new Pen(UiTheme.Border, 1f) { DashStyle = DashStyle.Dot };
        using var labelBrush = new SolidBrush(UiTheme.TextSecondary);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center
        };

        var decimals = Math.Max(ChartScale.DecimalsFor(scale.Step), 0);

        foreach (var tick in scale.Ticks)
        {
            var y = scale.YFor(tick);
            g.DrawLine(gridPen, plot.X, y, plot.Right, y);
            g.DrawString(tick.ToString("N" + decimals), UiTheme.Small, labelBrush,
                         new RectangleF(0, y - 9, MarginLeft - 6, 18), format);
        }

        if (!string.IsNullOrWhiteSpace(_series!.UnitSymbol))
        {
            using var unitFormat = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString(_series.UnitSymbol, UiTheme.Small, labelBrush,
                         new RectangleF(0, 0, MarginLeft - 6, MarginTop), unitFormat);
        }
    }

    /// <summary>
    /// The reference interval as a shaded band, with its edges drawn. An interval open on one side
    /// (only a lower or only an upper limit) runs to the edge of the plot on that side.
    /// </summary>
    private void DrawReferenceBand(Graphics g, Rectangle plot, Mapping scale)
    {
        var lower = _series!.LowerLimit;
        var upper = _series.UpperLimit;
        if (lower is null && upper is null) return;

        var top = upper is { } high ? scale.YFor(high) : plot.Top;
        var bottom = lower is { } low ? scale.YFor(low) : plot.Bottom;

        top = Math.Clamp(top, plot.Top, plot.Bottom);
        bottom = Math.Clamp(bottom, plot.Top, plot.Bottom);

        using var bandBrush = new SolidBrush(Color.FromArgb(28, UiTheme.Normal));
        g.FillRectangle(bandBrush, plot.X, top, plot.Width, Math.Max(0, bottom - top));

        using var edgePen = new Pen(Color.FromArgb(140, UiTheme.Normal), 1f) { DashStyle = DashStyle.Dash };
        if (upper is not null) g.DrawLine(edgePen, plot.X, top, plot.Right, top);
        if (lower is not null) g.DrawLine(edgePen, plot.X, bottom, plot.Right, bottom);
    }

    private void DrawSeries(Graphics g, Rectangle plot, Mapping scale,
                            List<(ResultTrendPoint Point, bool IsCurrent)> points)
    {
        var positions = points.Select(p => scale.PositionOf(p.Point)).ToArray();

        // The validated values joined by a solid line; the step to the value not yet validated is
        // dashed, since it is a provisional figure.
        var validatedCount = _series!.Points.Count;

        using (var linePen = new Pen(UiTheme.Accent, 1.5f))
        {
            if (validatedCount > 1) g.DrawLines(linePen, positions[..validatedCount]);
        }

        if (_series.Current is not null && validatedCount > 0)
        {
            using var provisionalPen = new Pen(UiTheme.Accent, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawLine(provisionalPen, positions[validatedCount - 1], positions[^1]);
        }

        for (var i = 0; i < points.Count; i++)
        {
            var (point, isCurrent) = points[i];
            var position = positions[i];
            var outside = IsOutside(point.Value);
            var colour = outside ? UiTheme.Abnormal : UiTheme.Accent;
            var size = i == _hoverIndex ? 11f : 8f;
            var box = new RectangleF(position.X - size / 2, position.Y - size / 2, size, size);

            if (isCurrent)
            {
                // Hollow, and larger: the eye finds today's value first, and cannot take it for a
                // released one.
                box.Inflate(2f, 2f);
                using var fill = new SolidBrush(UiTheme.SurfaceRaised);
                using var ring = new Pen(colour, 2f);
                g.FillEllipse(fill, box);
                g.DrawEllipse(ring, box);
                continue;
            }

            using var brush = new SolidBrush(colour);
            using var outline = new Pen(UiTheme.SurfaceRaised, 1.5f);

            // Shape as well as colour: a diamond outside the interval, a circle inside it.
            if (outside)
            {
                var diamond = new PointF[]
                {
                    new(position.X, box.Y - 1),
                    new(box.Right + 1, position.Y),
                    new(position.X, box.Bottom + 1),
                    new(box.X - 1, position.Y)
                };
                g.FillPolygon(brush, diamond);
                g.DrawPolygon(outline, diamond);
            }
            else
            {
                g.FillEllipse(brush, box);
                g.DrawEllipse(outline, box);
            }
        }
    }

    private bool IsOutside(decimal value) =>
        _series is not null
        && ((_series.LowerLimit is { } low && value < low) || (_series.UpperLimit is { } high && value > high));

    /// <summary>
    /// Dates under their points, dd/MM/yy, skipping any that would overlap the one before: the
    /// exact date of every point is on hover and in the table under the chart.
    /// </summary>
    private static void DrawDateAxis(Graphics g, Rectangle plot, Mapping scale,
                                     List<(ResultTrendPoint Point, bool IsCurrent)> points)
    {
        using var brush = new SolidBrush(UiTheme.TextMuted);
        using var tickPen = new Pen(UiTheme.BorderStrong);
        using var format = new StringFormat { Alignment = StringAlignment.Center };

        var lastRight = float.MinValue;
        var lastLabel = string.Empty;

        foreach (var (point, _) in points)
        {
            var x = scale.PositionOf(point).X;
            var label = point.At.ToLocalTime().ToString("dd/MM/yy");
            if (label == lastLabel) continue;

            var width = g.MeasureString(label, UiTheme.Small).Width;
            if (x - width / 2 < lastRight + 6) continue;

            g.DrawLine(tickPen, x, plot.Bottom, x, plot.Bottom + 4);
            g.DrawString(label, UiTheme.Small, brush,
                         new RectangleF(x - width / 2 - 2, plot.Bottom + 6, width + 4, 18), format);

            lastRight = x + width / 2;
            lastLabel = label;
        }
    }

    /// <summary>The mapping from values and dates to pixels, computed once per paint.</summary>
    private sealed class Mapping
    {
        public required IReadOnlyList<decimal> Ticks { get; init; }
        public required decimal Step { get; init; }
        private Rectangle Plot { get; init; }
        private decimal Low { get; init; }
        private decimal High { get; init; }
        private DateTimeOffset First { get; init; }
        private DateTimeOffset Last { get; init; }

        public static Mapping For(ResultTrendSeries series,
                                List<(ResultTrendPoint Point, bool IsCurrent)> points, Rectangle plot)
        {
            // The interval is part of the range, so the band is always on the chart: a run of
            // values all within it would otherwise fill the plot and look alarming.
            var values = points.Select(p => p.Point.Value).ToList();
            if (series.LowerLimit is { } low) values.Add(low);
            if (series.UpperLimit is { } high) values.Add(high);

            // Room above the highest value, where the caption is written: a value on the top
            // gridline sat under "Aucune antériorité".
            var span = Math.Max(values.Max() - values.Min(), Math.Abs(values.Max()) * 0.1m);
            var ticks = ChartScale.NiceTicks(values.Min(), values.Max() + span * 0.15m);

            return new Mapping
            {
                Ticks = ticks,
                Step = ticks.Count > 1 ? ticks[1] - ticks[0] : 1m,
                Plot = plot,
                Low = ticks[0],
                High = ticks[^1],
                First = points.Min(p => p.Point.At),
                Last = points.Max(p => p.Point.At)
            };
        }

        public float YFor(decimal value)
        {
            if (High == Low) return Plot.Y + Plot.Height / 2f;

            return Plot.Bottom - (float)((value - Low) / (High - Low)) * Plot.Height;
        }

        public PointF PositionOf(ResultTrendPoint point)
        {
            var span = (Last - First).TotalSeconds;

            var x = span <= 0
                ? Plot.X + Plot.Width / 2f
                : Plot.X + Inset + (float)((point.At - First).TotalSeconds / span) * (Plot.Width - 2 * Inset);

            return new PointF(x, YFor(point.Value));
        }
    }

    // ---- Interaction --------------------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var index = HitTest(e.Location);
        if (index == _hoverIndex) return;

        _hoverIndex = index;

        var points = Plotted();

        if (index >= 0 && index < points.Count && _series is not null)
        {
            var (point, isCurrent) = points[index];

            var lines = new List<string>
            {
                $"{point.Operator}{point.Value.ToString("N" + _series.Decimals)} {_series.UnitSymbol}".Trim(),
                point.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
            };

            if (isCurrent && !string.IsNullOrWhiteSpace(CurrentText)) lines.Insert(0, CurrentText);
            if (!string.IsNullOrWhiteSpace(point.AccessionNumber)) lines.Add(point.AccessionNumber);

            _tooltip.SetToolTip(this, string.Join(Environment.NewLine, lines));
        }
        else
        {
            _tooltip.SetToolTip(this, string.Empty);
        }

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    private int HitTest(Point location)
    {
        var points = Plotted();
        if (_series is null || points.Count == 0) return -1;

        var plot = PlotArea;
        if (plot.Width <= 0 || plot.Height <= 0) return -1;

        var scale = Mapping.For(_series, points, plot);
        var best = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < points.Count; i++)
        {
            var position = scale.PositionOf(points[i].Point);
            var dx = position.X - location.X;
            var dy = position.Y - location.Y;
            var distance = dx * dx + dy * dy;

            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }

        // Within roughly 10 px counts as a hit; beyond that the pointer is not on a point.
        return bestDistance <= 100f ? best : -1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tooltip.Dispose();
        base.Dispose(disposing);
    }
}
