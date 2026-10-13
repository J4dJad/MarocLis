using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Statistics;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Activity statistics for a period: what the management review needs on one page.
/// <para>
/// Volumes, turnaround and money for the period, broken down by department, by analysis, by payer
/// and by day. Each table exports as CSV so it can go into the review report or a spreadsheet
/// without retyping.
/// </para>
/// </summary>
public sealed class StatisticsView : LocalizedView
{
    private readonly StatisticsService _statistics;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<StatisticsView>? _logger;

    private readonly Label _heading = new();
    private readonly Label _fromLabel = new();
    private readonly Label _toLabel = new();
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly ComboBox _preset = new();
    private readonly Button _compute = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _export = UiTheme.SecondaryButton(string.Empty);

    private readonly Label[] _figures = Enumerable.Range(0, 8).Select(_ => new Label()).ToArray();

    private readonly TabControl _tabs = new();
    private readonly TabPage _departmentPage = new();
    private readonly TabPage _testPage = new();
    private readonly TabPage _payerPage = new();
    private readonly TabPage _dayPage = new();

    private readonly DataGridView _departments = UiTheme.Grid();
    private readonly DataGridView _tests = UiTheme.Grid();
    private readonly DataGridView _payers = UiTheme.Grid();
    private readonly DataGridView _days = UiTheme.Grid();

    private ActivityStatistics? _result;

    public StatisticsView(StatisticsService statistics, IDocumentOutput output, IUserDialogs dialogs,
                          ILocalizer localizer, ICultureManager cultureManager,
                          ILogger<StatisticsView>? logger = null)
        : base(localizer, cultureManager)
    {
        _statistics = statistics;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Statistics");

    private enum Preset { ThisMonth, LastMonth, ThisYear, LastYear, Custom }

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        foreach (var label in new[] { _fromLabel, _toLabel })
        {
            label.Font = UiTheme.Small;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = true;
        }

        _preset.DropDownStyle = ComboBoxStyle.DropDownList;
        _preset.FlatStyle = FlatStyle.Flat;
        _preset.Bounds = new Rectangle(0, 52, 180, UiTheme.InputHeight);

        _fromLabel.Location = new Point(196, 34);
        _from.Format = DateTimePickerFormat.Short;
        _from.Bounds = new Rectangle(196, 52, 130, UiTheme.InputHeight);

        _toLabel.Location = new Point(338, 34);
        _to.Format = DateTimePickerFormat.Short;
        _to.Bounds = new Rectangle(338, 52, 130, UiTheme.InputHeight);

        _compute.Bounds = new Rectangle(484, 50, 140, UiTheme.ButtonHeight);
        _export.Bounds = new Rectangle(634, 50, 160, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _preset, _fromLabel, _from, _toLabel, _to, _compute, _export]);

        // Key figures: two rows of four, large enough to read across a meeting room.
        var figures = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 136,
            ColumnCount = 4,
            RowCount = 2,
            BackColor = UiTheme.Surface,
            Padding = new Padding(0, 6, 0, 6)
        };
        for (var i = 0; i < 4; i++)
            figures.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        figures.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        figures.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        for (var i = 0; i < _figures.Length; i++)
        {
            var label = _figures[i];
            label.Dock = DockStyle.Fill;
            label.Font = UiTheme.SectionTitle;
            label.ForeColor = UiTheme.TextPrimary;
            label.BackColor = UiTheme.SurfaceRaised;
            label.Margin = new Padding(0, 0, 8, 6);
            label.Padding = new Padding(8, 4, 8, 4);
            label.TextAlign = ContentAlignment.MiddleLeft;
            figures.Controls.Add(label, i % 4, i / 4);
        }

        BuildColumns();

        foreach (var (page, grid) in new[] { (_departmentPage, _departments), (_testPage, _tests),
                                             (_payerPage, _payers), (_dayPage, _days) })
        {
            page.BackColor = UiTheme.Surface;
            page.Controls.Add(grid);
            _tabs.TabPages.Add(page);
        }

        _tabs.Dock = DockStyle.Fill;

        Controls.Add(_tabs);
        Controls.Add(figures);
        Controls.Add(bar);
    }

    private static void AddColumn(DataGridView grid, string name, int weight, bool numeric = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = numeric ? UiTheme.Monospace : UiTheme.Body,
                Alignment = numeric ? DataGridViewContentAlignment.MiddleRight
                                    : DataGridViewContentAlignment.MiddleLeft
            }
        });

    private void BuildColumns()
    {
        AddColumn(_departments, "code", 8);
        AddColumn(_departments, "name", 24);
        AddColumn(_departments, "tests", 9, numeric: true);
        AddColumn(_departments, "validated", 9, numeric: true);
        AddColumn(_departments, "tat", 10, numeric: true);
        AddColumn(_departments, "onTime", 10, numeric: true);
        AddColumn(_departments, "amount", 12, numeric: true);

        AddColumn(_tests, "code", 8);
        AddColumn(_tests, "name", 30);
        AddColumn(_tests, "tests", 9, numeric: true);
        AddColumn(_tests, "tat", 10, numeric: true);
        AddColumn(_tests, "amount", 12, numeric: true);

        AddColumn(_payers, "name", 28);
        AddColumn(_payers, "orders", 9, numeric: true);
        AddColumn(_payers, "tests", 9, numeric: true);
        AddColumn(_payers, "amount", 12, numeric: true);
        AddColumn(_payers, "payerShare", 12, numeric: true);
        AddColumn(_payers, "patientShare", 12, numeric: true);

        AddColumn(_days, "date", 12);
        AddColumn(_days, "orders", 10, numeric: true);
        AddColumn(_days, "tests", 10, numeric: true);
    }

    private void WireBehaviour()
    {
        _preset.SelectedIndexChanged += (_, _) => ApplyPreset();
        _from.ValueChanged += (_, _) => MarkCustom();
        _to.ValueChanged += (_, _) => MarkCustom();
        _compute.Click += async (_, _) => await LoadAsync();
        _export.Click += async (_, _) => await ExportAsync();
    }

    private bool _applyingPreset;

    private void ApplyPreset()
    {
        if (_preset.SelectedItem is not EnumChoice { Value: var value }) return;

        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);

        (DateTime From, DateTime To)? range = (Preset)value switch
        {
            Preset.ThisMonth => (month, today),
            Preset.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            Preset.ThisYear => (new DateTime(today.Year, 1, 1), today),
            Preset.LastYear => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31)),
            _ => null
        };

        if (range is not { } r) return;

        _applyingPreset = true;
        try
        {
            // Widened first so that a new start after the old end is never rejected by the picker.
            _from.Value = r.From;
            _to.Value = r.To;
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private void MarkCustom()
    {
        if (_applyingPreset) return;
        var custom = _preset.Items.Cast<EnumChoice>().FirstOrDefault(c => c.Value == (int)Preset.Custom);
        if (custom is not null && !Equals(_preset.SelectedItem, custom)) _preset.SelectedItem = custom;
    }

    protected override void ApplyTexts()
    {
        _heading.Text = T("Statistics.Title");
        _fromLabel.Text = T("Field.From");
        _toLabel.Text = T("Field.To");
        _compute.Text = T("Statistics.Compute");
        _export.Text = T("Statistics.ExportCsv");

        var selected = (_preset.SelectedItem as EnumChoice)?.Value ?? (int)Preset.ThisMonth;
        _applyingPreset = true;
        _preset.Items.Clear();
        foreach (var preset in Enum.GetValues<Preset>())
        {
            var index = _preset.Items.Add(new EnumChoice((int)preset, T($"Statistics.Preset{preset}")));
            if ((int)preset == selected) _preset.SelectedIndex = index;
        }
        _applyingPreset = false;
        if (_result is null) ApplyPreset();

        _departmentPage.Text = T("Statistics.ByDepartment");
        _testPage.Text = T("Statistics.ByTest");
        _payerPage.Text = T("Statistics.ByPayer");
        _dayPage.Text = T("Statistics.ByDay");

        _departments.Column("code").HeaderText = T("Field.Code");
        _departments.Column("name").HeaderText = T("Field.Department");
        _departments.Column("tests").HeaderText = T("Statistics.Tests");
        _departments.Column("validated").HeaderText = T("Statistics.Validated");
        _departments.Column("tat").HeaderText = T("Statistics.AverageTat");
        _departments.Column("onTime").HeaderText = T("Statistics.OnTime");
        _departments.Column("amount").HeaderText = T("Statistics.Charged");

        _tests.Column("code").HeaderText = T("Field.Code");
        _tests.Column("name").HeaderText = T("Field.Name");
        _tests.Column("tests").HeaderText = T("Statistics.Tests");
        _tests.Column("tat").HeaderText = T("Statistics.AverageTat");
        _tests.Column("amount").HeaderText = T("Statistics.Charged");

        _payers.Column("name").HeaderText = T("Statistics.Payer");
        _payers.Column("orders").HeaderText = T("Statistics.Orders");
        _payers.Column("tests").HeaderText = T("Statistics.Tests");
        _payers.Column("amount").HeaderText = T("Statistics.Charged");
        _payers.Column("payerShare").HeaderText = T("Statistics.PayerShare");
        _payers.Column("patientShare").HeaderText = T("Statistics.PatientShare");

        _days.Column("date").HeaderText = T("Field.Date");
        _days.Column("orders").HeaderText = T("Statistics.Orders");
        _days.Column("tests").HeaderText = T("Statistics.Tests");

        Render();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            UseWaitCursor = true;
            var result = await _statistics.GetActivityAsync(DateOnly.FromDateTime(_from.Value.Date),
                                                            DateOnly.FromDateTime(_to.Value.Date), ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, _compute.Text);
                return;
            }

            _result = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing activity statistics failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _compute.Text);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private static string Hours(double? value) => value is { } h ? $"{h:0.0} h" : "—";
    private static string Percent(double? value) => value is { } p ? $"{p:0.0} %" : "—";

    private void Render()
    {
        foreach (var grid in new[] { _departments, _tests, _payers, _days }) grid.Rows.Clear();

        if (_result is not { } stats)
        {
            foreach (var label in _figures) label.Text = string.Empty;
            _export.Enabled = false;
            return;
        }

        var s = stats.Summary;
        var language = Localizer.CurrentLanguage;

        string Figure(string key, string value) => $"{T(key)}\n{value}";

        _figures[0].Text = Figure("Statistics.Orders", s.OrderCount.ToString("N0", CultureInfo.CurrentCulture));
        _figures[1].Text = Figure("Statistics.Patients", s.PatientCount.ToString("N0", CultureInfo.CurrentCulture));
        _figures[2].Text = Figure("Statistics.Tests",
            s.CancelledTestCount > 0
                ? $"{s.TestCount:N0}  ({T("Statistics.Cancelled")} {s.CancelledTestCount:N0})"
                : s.TestCount.ToString("N0", CultureInfo.CurrentCulture));
        _figures[3].Text = Figure("Statistics.TurnaroundLine",
                                  $"{Hours(s.AverageTurnaroundHours)} · {Percent(s.OnTimePercent)}");
        _figures[4].Text = Figure("Statistics.Charged", $"{Amount(s.ChargedAmount)} MAD");
        _figures[5].Text = Figure("Statistics.Invoiced", $"{Amount(s.InvoicedAmount)} MAD");
        _figures[6].Text = Figure("Statistics.Credited", $"{Amount(s.CreditedAmount)} MAD");
        _figures[7].Text = Figure("Statistics.Collected", $"{Amount(s.CollectedAmount)} MAD");

        foreach (var d in stats.Departments)
            _departments.Rows.Add(d.Code, d.Name.For(language), d.TestCount, d.ValidatedCount,
                                  Hours(d.AverageTurnaroundHours), Percent(d.OnTimePercent),
                                  Amount(d.ChargedAmount));

        foreach (var t in stats.Tests)
            _tests.Rows.Add(t.Code, t.Name.For(language), t.TestCount, Hours(t.AverageTurnaroundHours),
                            Amount(t.ChargedAmount));

        foreach (var p in stats.Payers)
            _payers.Rows.Add(p.Name?.For(language) ?? T("Statistics.SelfPaying"), p.OrderCount,
                             p.TestCount, Amount(p.ChargedAmount), Amount(p.PayerShare),
                             Amount(p.PatientShare));

        foreach (var day in stats.Days)
            _days.Rows.Add(day.Date.ToString("ddd dd/MM/yy", CultureInfo.CurrentCulture),
                           day.OrderCount, day.TestCount);

        _heading.Text = $"{T("Statistics.Title")} — {stats.From:dd/MM/yy} → {stats.To:dd/MM/yy}";
        _export.Enabled = true;
    }

    /// <summary>
    /// The visible table as CSV, semicolon-separated with a BOM: what a French-locale Excel opens
    /// directly into columns, accents intact.
    /// </summary>
    private async Task ExportAsync()
    {
        var grid = _tabs.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault();
        if (grid is null || grid.Rows.Count == 0) return;

        static string Cell(object? value)
        {
            var text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            return text.IndexOfAny([';', '"', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
        }

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', grid.Columns.Cast<DataGridViewColumn>()
                                            .OrderBy(c => c.DisplayIndex)
                                            .Select(c => Cell(c.HeaderText))));

        foreach (DataGridViewRow row in grid.Rows)
            csv.AppendLine(string.Join(';', grid.Columns.Cast<DataGridViewColumn>()
                                                .OrderBy(c => c.DisplayIndex)
                                                .Select(c => Cell(row.Cells[c.Index].Value))));

        try
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            var opened = await _output.OpenAsync(bytes, "csv");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _export.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting statistics failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _export.Text);
        }
    }
}
