using System.Globalization;
using MarocLIS.Application.Results;
using MarocLIS.Desktop.Controls;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Results;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The antériorités of one parameter: the patient's validated values as a curve against the
/// reference interval, and the same values as a table under it.
/// <para>
/// Opened from the entry and the validation screens, where the antériorité column shows only the
/// last value. One previous value says whether today's figure moved; it does not say whether it is
/// part of a slow climb, a return to baseline or a one-off, which is what a biologist signing a
/// creatinine or a TSH is actually asking.
/// </para>
/// <para>
/// The table is there because a chart is read for its shape, and a value is dictated from a
/// number: the date, the figure and the file it was reported in, newest first.
/// </para>
/// </summary>
internal sealed class ResultHistoryDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly ResultTrendChart _chart = new();
    private readonly DataGridView _grid = UiTheme.Grid();

    internal ResultHistoryDialog(ILocalizer localizer, string parameterName, ResultTrendSeries series)
    {
        _localizer = localizer;

        Text = string.IsNullOrWhiteSpace(series.UnitSymbol)
            ? $"{T("Result.History")} — {parameterName}"
            : $"{T("Result.History")} — {parameterName} ({series.UnitSymbol})";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(800, 560);
        MinimumSize = new Size(560, 460);
        BackColor = UiTheme.Surface;
        Font = UiTheme.Body;
        KeyPreview = true;

        _chart.Dock = DockStyle.Fill;
        _chart.EmptyText = T("Result.HistoryEmpty");
        _chart.CurrentText = T("Result.HistoryCurrent");
        _chart.SetSeries(series);

        BuildColumns();
        _grid.MultiSelect = false;
        _grid.CellFormatting += (_, e) => OnCellFormatting(e, series);

        // The curve and the table each in a white card on the work surface, between the head and
        // the foot of the dialog.
        var chartCard = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, Padding = new Padding(8) };
        chartCard.Controls.Add(_chart);
        UiChrome.AsCard(chartCard);

        var tableCard = new ListCard(_grid) { Dock = DockStyle.Bottom, Height = 170 };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(16, 14, 16, 14) };
        body.Controls.Add(chartCard);
        body.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = UiTheme.Surface });
        body.Controls.Add(tableCard);

        var close = UiTheme.SecondaryButton(T("Action.Close"));
        close.DialogResult = DialogResult.Cancel;

        Controls.Add(body);
        Controls.Add(DialogFrame.Foot(null, close));
        Controls.Add(DialogFrame.Head(Text, Summary(series), 800));

        CancelButton = close;

        Fill(series);

        RtlLayout.Apply(this, localizer.CurrentLanguage);
    }

    private string T(string key) => _localizer[key];

    /// <summary>The head's sentence: how many validated values, and the interval they are read against.</summary>
    private string Summary(ResultTrendSeries series)
    {
        var count = _localizer.Format("Result.HistoryCount", series.Points.Count);
        var format = "N" + series.Decimals;
        var interval = (series.LowerLimit, series.UpperLimit) switch
        {
            ({ } low, { } high) => $"{low.ToString(format)} – {high.ToString(format)}",
            ({ } low, null) => $"≥ {low.ToString(format)}",
            (null, { } high) => $"≤ {high.ToString(format)}",
            _ => null
        };
        return interval is null ? count : $"{count} · {_localizer.Format("Result.HistoryInterval", interval)}";
    }

    private void BuildColumns()
    {
        void Add(string name, string header, int weight, Font font, bool right = false)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("date", T("Field.Date"), 30, UiTheme.Monospace);
        Add("value", T("Result.HistoryValue"), 30, UiTheme.MonospaceBold, right: true);
        Add("accession", T("Order.AccessionNumber"), 40, UiTheme.Monospace);
    }

    /// <summary>Newest first, the value not yet validated on top and marked as such.</summary>
    private void Fill(ResultTrendSeries series)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        if (series.Current is { } current)
        {
            var index = AddRow(current, series);
            var row = _grid.Rows[index];
            row.Tag = current;
            row.DefaultCellStyle.Font = new Font(UiTheme.Monospace, FontStyle.Italic);
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = T("Result.HistoryCurrent");
        }

        foreach (var point in series.Points.Reverse())
            _grid.Rows[AddRow(point, series)].Tag = point;

        _grid.ResumeLayout(performLayout: true);
        _grid.ClearSelection();
    }

    private int AddRow(ResultTrendPoint point, ResultTrendSeries series) =>
        _grid.Rows.Add(
            point.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
            $"{point.Operator}{point.Value.ToString("N" + series.Decimals)}",
            // An antériorité reprise from the old application: its old number, and the word
            // « reprise » so nobody looks for that file in MarocLIS.
            point.Imported
                ? $"{point.AccessionNumber} · {T("Migration.HistoryMarker")}".TrimStart(' ', '·')
                : point.AccessionNumber ?? string.Empty);

    /// <summary>A value outside today's interval in the same colour as on the curve.</summary>
    private void OnCellFormatting(DataGridViewCellFormattingEventArgs e, ResultTrendSeries series)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Columns[e.ColumnIndex].Name != "value") return;
        if (_grid.Rows[e.RowIndex].Tag is not ResultTrendPoint point) return;

        var outside = (series.LowerLimit is { } low && point.Value < low)
                      || (series.UpperLimit is { } high && point.Value > high);

        if (outside) e.CellStyle!.ForeColor = UiTheme.Abnormal;
    }
}

/// <summary>What the entry and validation screens share to open <see cref="ResultHistoryDialog"/>.</summary>
internal static class ResultHistory
{
    /// <summary>Only a measured number has a curve; a coded or text result has none.</summary>
    internal static bool IsCharted(ResultEntryRow? row) =>
        row is { IsHeading: false, ValueType: ResultValueType.Numeric or ResultValueType.NumericWithOperator };

    /// <summary>
    /// The chart's data for <paramref name="row"/>: the validated values read from the database, the
    /// interval resolved for this patient today, and the value on screen when it is not one of them
    /// yet — <paramref name="typed"/>, a value typed and not saved, wins over the stored one.
    /// </summary>
    internal static ResultTrendSeries SeriesFor(ResultEntryRow row, IReadOnlyList<ResultHistoryPoint> history,
                                                string accessionNumber, string? typed = null)
    {
        var points = history
            .Select(p => new ResultTrendPoint(p.ValidatedAt, p.Value, p.ValueOperator, p.AccessionNumber, p.Imported))
            .ToList();

        ResultTrendPoint? current = null;

        // Dated now: it is not released, and when it is that is the date it will carry.
        if (TryParse(typed, out var oper, out var value)
            && !string.Equals(typed?.Trim(), row.CurrentValue?.Trim(), StringComparison.Ordinal))
            current = new ResultTrendPoint(DateTimeOffset.Now, value, oper, accessionNumber);
        else if (row.NumericValue is { } stored
                 && row.Status is not (TestResultStatus.Validated or TestResultStatus.Reported))
            current = new ResultTrendPoint(DateTimeOffset.Now, stored, row.ValueOperator, accessionNumber);

        return new ResultTrendSeries(points, row.LowerLimit, row.UpperLimit, row.Decimals, row.UnitSymbol,
                                     current);
    }

    /// <summary>The same reading of a typed value as the entry service: an optional operator, then a number.</summary>
    private static bool TryParse(string? raw, out string? oper, out decimal value)
    {
        oper = null;
        value = 0m;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var candidate in (string[])["<=", ">=", "≤", "≥", "<", ">"])
        {
            if (!text.StartsWith(candidate, StringComparison.Ordinal)) continue;
            oper = candidate;
            text = text[candidate.Length..].Trim();
            break;
        }

        return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
                                out value);
    }
}
