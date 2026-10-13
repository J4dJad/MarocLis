using MarocLIS.Application.Quality;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Desktop.Controls;

/// <summary>
/// A Levey-Jennings chart, painted directly.
/// <para>
/// Hand-drawn rather than taken from a charting library, for three reasons that matter here. The
/// control limits are not gridlines — they are ±1, ±2 and ±3 SD, and they have to be labelled and
/// weighted so a technician reads the violation at a glance. A rejected point must be
/// distinguishable by <b>shape</b> as well as colour, because these charts get printed in
/// monochrome for the quality file. And a lot change has to be markable as a discontinuity in the
/// series, since comparing across it is exactly the mistake the chart exists to prevent.
/// </para>
/// <para>
/// The Y axis is in Z-score units, not measurement units, so a chart looks the same whatever the
/// analyte — which is the point of plotting Z rather than the raw value. The raw value is still
/// shown on hover, because that is what gets dictated and re-entered.
/// </para>
/// </summary>
public sealed class LeveyJenningsChart : Control
{
    private LeveyJenningsSeries? _series;
    private int _hoverIndex = -1;
    private readonly ToolTip _tooltip = new() { ShowAlways = true };

    /// <summary>Plot area insets: room for the Z labels on the left and the dates underneath.</summary>
    private const int MarginLeft = 54;
    private const int MarginRight = 16;
    private const int MarginTop = 14;
    private const int MarginBottom = 34;

    /// <summary>The chart is drawn from −4 to +4 SD; anything beyond is clamped to the edge.</summary>
    private const decimal ZRange = 4m;

    public LeveyJenningsChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint, true);

        BackColor = UiTheme.SurfaceRaised;
        MinimumSize = new Size(320, 200);
    }

    public void SetSeries(LeveyJenningsSeries? series)
    {
        _series = series;
        _hoverIndex = -1;
        Invalidate();
    }

    /// <summary>Raised when a point is clicked, so the host can open it for review.</summary>
    public event EventHandler<LeveyJenningsPoint>? PointSelected;

    // ---- Painting ---------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var plot = PlotArea;

        if (plot.Width <= 0 || plot.Height <= 0) return;

        if (_series is null)
        {
            DrawCentredText(g, "—", plot);
            return;
        }

        if (_series.Mean is null || _series.Sd is null or 0m)
        {
            // No established target: the points cannot be placed on a Z axis at all. Saying so is
            // better than drawing a chart that implies limits which do not exist.
            DrawCentredText(g, "Cible non établie", plot);
            return;
        }

        DrawControlLimits(g, plot);
        DrawSeries(g, plot);
        DrawHeader(g);
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

    /// <summary>
    /// Draws the mean and the ±1/±2/±3 SD lines, weighted by what they mean: ±3 SD is a rejection
    /// boundary and is drawn solid, ±2 SD is a warning boundary and is dashed, ±1 SD is context and
    /// is faint. A uniform grid would make all three look equally important.
    /// </summary>
    private void DrawControlLimits(Graphics g, Rectangle plot)
    {
        using var meanPen = new Pen(UiTheme.Accent, 1.5f);
        using var threeSdPen = new Pen(UiTheme.Critical, 1f);
        using var twoSdPen = new Pen(UiTheme.Abnormal, 1f)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dash
        };
        using var oneSdPen = new Pen(UiTheme.Border, 1f)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dot
        };
        using var labelBrush = new SolidBrush(UiTheme.TextSecondary);

        // The in-control band, so the eye reads inside-versus-outside before reading any number.
        var bandTop = YFor(2m, plot);
        var bandBottom = YFor(-2m, plot);

        using var bandBrush = new SolidBrush(Color.FromArgb(18, UiTheme.Normal));
        g.FillRectangle(bandBrush, plot.X, bandTop, plot.Width, bandBottom - bandTop);

        foreach (var z in (decimal[])[3m, 2m, 1m, 0m, -1m, -2m, -3m])
        {
            var y = YFor(z, plot);

            var pen = z == 0m ? meanPen
                : Math.Abs(z) == 3m ? threeSdPen
                : Math.Abs(z) == 2m ? twoSdPen
                : oneSdPen;

            g.DrawLine(pen, plot.X, y, plot.Right, y);

            // Label with both the SD multiple and the actual value: the technician compares against
            // the analyser reading, which is in measurement units.
            var value = _series!.Mean!.Value + z * _series.Sd!.Value;

            var label = z == 0m
                ? $"x̄  {value.ToString("N" + _series.Decimals)}"
                : $"{(z > 0 ? "+" : string.Empty)}{z:0}s  {value.ToString("N" + _series.Decimals)}";

            g.DrawString(label, UiTheme.Small, labelBrush, 2, y - 7);
        }

        using var framePen = new Pen(UiTheme.BorderStrong);
        g.DrawRectangle(framePen, plot);
    }

    private void DrawSeries(Graphics g, Rectangle plot)
    {
        var points = _series!.Points;

        if (points.Count == 0)
        {
            DrawCentredText(g, "—", plot);
            return;
        }

        var positions = new PointF[points.Count];

        for (var i = 0; i < points.Count; i++)
            positions[i] = new PointF(XFor(i, points.Count, plot), YFor(points[i].ZScore ?? 0m, plot));

        // Connect consecutive points: a trend is only visible as a line, and 7-T is a trend rule.
        if (points.Count > 1)
        {
            using var linePen = new Pen(UiTheme.TextSecondary, 1f);
            g.DrawLines(linePen, positions);
        }

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var position = positions[i];
            var hovered = i == _hoverIndex;

            var colour = point.Outcome switch
            {
                QcOutcome.Rejected => UiTheme.Critical,
                QcOutcome.Warning => UiTheme.Abnormal,
                QcOutcome.Accepted => UiTheme.Normal,
                _ => UiTheme.TextMuted
            };

            var size = hovered ? 10f : 7f;
            var box = new RectangleF(position.X - size / 2, position.Y - size / 2, size, size);

            using var fill = new SolidBrush(colour);
            using var outline = new Pen(UiTheme.SurfaceRaised, 1.5f);

            // Shape carries the outcome as well as colour: a square for a rejection, a triangle
            // for a warning, a circle for an accepted point. These charts get printed in mono.
            switch (point.Outcome)
            {
                case QcOutcome.Rejected:
                    g.FillRectangle(fill, box);
                    g.DrawRectangle(outline, box.X, box.Y, box.Width, box.Height);
                    break;

                case QcOutcome.Warning:
                    var triangle = new PointF[]
                    {
                        new(position.X, box.Y),
                        new(box.Right, box.Bottom),
                        new(box.X, box.Bottom)
                    };
                    g.FillPolygon(fill, triangle);
                    g.DrawPolygon(outline, triangle);
                    break;

                default:
                    g.FillEllipse(fill, box);
                    g.DrawEllipse(outline, box);
                    break;
            }

            // An overridden rejection is ringed, so a chart shows at a glance where the laboratory
            // released results despite an out-of-control run.
            if (point.WasOverridden)
            {
                using var overridePen = new Pen(UiTheme.Critical, 1.5f)
                {
                    DashStyle = System.Drawing.Drawing2D.DashStyle.Dot
                };

                g.DrawEllipse(overridePen, box.X - 4, box.Y - 4, box.Width + 8, box.Height + 8);
            }
        }

        DrawDateAxis(g, plot, points);
    }

    private static void DrawDateAxis(Graphics g, Rectangle plot, IReadOnlyList<LeveyJenningsPoint> points)
    {
        using var brush = new SolidBrush(UiTheme.TextMuted);

        // At most six labels: more than that and they collide, and the precise date of each point
        // is available on hover anyway.
        var step = Math.Max(1, points.Count / 6);

        for (var i = 0; i < points.Count; i += step)
        {
            var x = XFor(i, points.Count, plot);
            var label = points[i].MeasuredAt.ToString("dd/MM");

            var width = g.MeasureString(label, UiTheme.Small).Width;
            g.DrawString(label, UiTheme.Small, brush, x - width / 2, plot.Bottom + 6);
        }
    }

    private void DrawHeader(Graphics g)
    {
        using var brush = new SolidBrush(UiTheme.TextSecondary);

        var cv = _series!.CvPercent is { } value ? $"CV {value:N2} %" : string.Empty;

        // States whose limits these are. A chart drawn on the manufacturer's insert rather than
        // the laboratory's own data means something different, and ISO 15189 §5.6 cares which.
        var basis = _series.UsingAssignedStatistics
            ? "limites fournisseur"
            : "limites du laboratoire";

        var header = string.Join("   ·   ",
            new[] { _series.ParameterCode, _series.MaterialLotNumber,
                    _series.InstrumentCode, cv, basis }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        g.DrawString(header, UiTheme.Small, brush, MarginLeft, 1);
    }

    private static float XFor(int index, int count, Rectangle plot) =>
        count <= 1
            ? plot.X + plot.Width / 2f
            : plot.X + 12 + (plot.Width - 24) * index / (float)(count - 1);

    private static float YFor(decimal z, Rectangle plot)
    {
        // Clamped rather than allowed off-canvas: a 6 SD outlier still has to be visible, and
        // rescaling the whole axis to fit it would flatten every other point into the mean line.
        var clamped = Math.Clamp(z, -ZRange, ZRange);

        return plot.Y + plot.Height / 2f
               - (float)clamped / (float)ZRange * (plot.Height / 2f - 8);
    }

    // ---- Interaction --------------------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var index = HitTest(e.Location);

        if (index == _hoverIndex) return;

        _hoverIndex = index;

        if (index >= 0 && _series is not null)
        {
            var point = _series.Points[index];

            var lines = new List<string>
            {
                $"{point.Value.ToString("N" + _series.Decimals)} {_series.UnitSymbol}".Trim(),
                $"Z = {point.ZScore:N2}",
                point.MeasuredAt.ToString("dd/MM/yyyy HH:mm")
            };

            if (point.ViolatedRules != WestgardRule.None) lines.Add(point.ViolatedRules.ToString());
            if (point.WasOverridden) lines.Add("dérogation");
            if (!string.IsNullOrWhiteSpace(point.Comment)) lines.Add(point.Comment);

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

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        var index = HitTest(e.Location);

        if (index >= 0 && _series is not null)
            PointSelected?.Invoke(this, _series.Points[index]);
    }

    private int HitTest(Point location)
    {
        if (_series is null || _series.Points.Count == 0) return -1;
        if (_series.Mean is null || _series.Sd is null or 0m) return -1;

        var plot = PlotArea;
        var best = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < _series.Points.Count; i++)
        {
            var x = XFor(i, _series.Points.Count, plot);
            var y = YFor(_series.Points[i].ZScore ?? 0m, plot);

            var dx = x - location.X;
            var dy = y - location.Y;
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
