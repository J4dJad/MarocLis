using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// "Consommation": for a period, the consumption the activity explains (patient results, QC,
/// calibrations, reruns, dead volume) against what really left the shelves, item by item. A line
/// whose variance goes beyond the threshold is flagged: reagent went that no result accounts for,
/// or results were produced with reagent nobody recorded. Double-click shows the breakdown.
/// </summary>
public sealed class ConsumptionView : LocalizedView
{
    private readonly StockPlanningService _planning;
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<ConsumptionView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _heading = new();
    private readonly Label _fromLabel = new();
    private readonly Label _toLabel = new();
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short };
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _detail = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _pdf = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _csv = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _settings = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _notice = new();

    private IReadOnlyList<ConsumptionRow> _rows = [];

    public ConsumptionView(StockPlanningService planning, IListPdfRenderer lists, IDocumentOutput output,
                           IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                           ILogger<ConsumptionView>? logger = null)
        : base(localizer, cultureManager)
    {
        _planning = planning;
        _lists = lists;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        var today = DateTime.Today;
        _from.Value = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        _to.Value = new DateTime(today.Year, today.Month, 1).AddDays(-1);

        BuildLayout();

        _refresh.Click += async (_, _) => await ActivateAsync();
        _detail.Click += (_, _) => ShowDetail();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowDetail(); };
        _pdf.Click += async (_, _) => await RunBusyAsync(PrintAsync);
        _csv.Click += async (_, _) => await RunBusyAsync(ExportAsync);
        _settings.Click += async (_, _) => await RunBusyAsync(EditSettingsAsync);

        PermissionGate.Require(Permissions.Administration.ManageSettings, _settings);
    }

    public override string Title => T("Nav.Consumption");

    private ConsumptionRow? Current => _grid.CurrentRow?.Tag as ConsumptionRow;

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = UiTheme.Surface };

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

        _fromLabel.Location = new Point(0, 34);
        _from.Bounds = new Rectangle(40, 30, 110, UiTheme.InputHeight);
        _toLabel.Location = new Point(160, 34);
        _to.Bounds = new Rectangle(190, 30, 110, UiTheme.InputHeight);

        _refresh.Bounds = new Rectangle(320, 26, 110, UiTheme.ButtonHeight);
        _detail.Bounds = new Rectangle(440, 26, 150, UiTheme.ButtonHeight);
        _pdf.Bounds = new Rectangle(600, 26, 110, UiTheme.ButtonHeight);
        _csv.Bounds = new Rectangle(720, 26, 110, UiTheme.ButtonHeight);
        _settings.Bounds = new Rectangle(840, 26, 150, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _fromLabel, _from, _toLabel, _to, _refresh, _detail, _pdf, _csv, _settings]);

        foreach (var (name, weight) in new[]
                 {
                     ("code", 8), ("name", 16), ("unit", 6), ("opening", 7), ("receipts", 7), ("tests", 7),
                     ("theoretical", 8), ("real", 8), ("theoclosing", 8), ("realclosing", 8), ("variance", 7),
                     ("percent", 6), ("count", 8)
                 })
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = name is "code" ? UiTheme.Monospace : UiTheme.Body,
                    Alignment = name is "code" or "name" or "unit" or "count"
                        ? DataGridViewContentAlignment.MiddleLeft
                        : DataGridViewContentAlignment.MiddleRight
                }
            });
        }

        _grid.Dock = DockStyle.Fill;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not ConsumptionRow row) return;
            if (row.Reconciliation.IsFlagged)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
            else if (!row.Reconciliation.Computable)
            {
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
            }
        };

        _notice.Dock = DockStyle.Bottom;
        _notice.Height = 46;
        _notice.Font = UiTheme.Small;
        _notice.ForeColor = UiTheme.TextSecondary;

        Controls.Add(_grid);
        Controls.Add(_notice);
        Controls.Add(bar);
    }

    private IReadOnlyList<string> Headers() =>
    [
        T("Field.Code"), T("Inventory.Item"), T("Field.Unit"), T("Consumption.Opening"), T("Consumption.Receipts"),
        T("Consumption.Tests"), T("Consumption.Theoretical"), T("Consumption.Real"),
        T("Consumption.TheoreticalClosing"), T("Consumption.RealClosing"), T("Consumption.Variance"),
        T("Consumption.VariancePercent"), T("Consumption.Count")
    ];

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.Consumption");
        _fromLabel.Text = T("Consumption.From");
        _toLabel.Text = T("Consumption.To");
        _refresh.Text = T("Action.Refresh");
        _detail.Text = T("Consumption.Detail");
        _pdf.Text = T("Consumption.Pdf");
        _csv.Text = T("Consumption.Csv");
        _settings.Text = T("Stock.Settings");

        var headers = Headers();
        for (var i = 0; i < _grid.Columns.Count; i++) _grid.Columns[i].HeaderText = headers[i];

        if (_rows.Count > 0) Render();
        else _notice.Text = T("Consumption.Notice");
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _planning.GetConsumptionAsync(DateOnly.FromDateTime(_from.Value),
                                                             DateOnly.FromDateTime(_to.Value), ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing the consumption failed.");
        }
    }

    private void Render()
    {
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var index = _grid.Rows.Add([.. StockCsv.ConsumptionCells(row)]);
            _grid.Rows[index].Tag = row;
        }

        var flagged = _rows.Count(r => r.Reconciliation.IsFlagged);
        _heading.Text = $"{T("Nav.Consumption")} · {_rows.Count}";
        _notice.Text = flagged > 0 ? T("Consumption.Flagged", flagged) : T("Consumption.Notice");
        _notice.ForeColor = flagged > 0 ? UiTheme.Critical : UiTheme.TextSecondary;
    }

    private string Period() =>
        $"{_from.Value:dd/MM/yyyy} – {_to.Value:dd/MM/yyyy}";

    private void ShowDetail()
    {
        if (Current is not { } row) return;
        using var dialog = new ConsumptionDetailDialog(Localizer, row, Period());
        dialog.ShowDialog(FindForm());
    }

    private async Task PrintAsync()
    {
        var document = new ListDocument(
            T("Consumption.ReportTitle", Period()),
            $"{T("Consumption.Notice")} — {DateTime.Now:dd/MM/yyyy HH:mm}",
            Headers(),
            [.. _rows.Select(StockCsv.ConsumptionCells)],
            Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic);

        var opened = await _output.OpenAsync(_lists.Render(document), "pdf");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _pdf.Text);
    }

    private async Task ExportAsync()
    {
        var opened = await _output.OpenAsync(StockCsv.Table(Headers(), _rows.Select(StockCsv.ConsumptionCells)), "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _csv.Text);
    }

    private async Task EditSettingsAsync()
    {
        var current = await _planning.GetSettingsAsync();
        if (current.IsFailure) { _dialogs.ShowFailure(current, _settings.Text); return; }

        using var dialog = new StockSettingsDialog(Localizer, _dialogs, current.Value);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Settings is null) return;

        var saved = await _planning.SaveSettingsAsync(dialog.Settings);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, _settings.Text); return; }

        await ActivateAsync();
    }
}

/// <summary>Where an item's theoretical consumption comes from: patients, QC, calibrations, reruns, dead volume.</summary>
internal sealed class ConsumptionDetailDialog : Form
{
    internal ConsumptionDetailDialog(ILocalizer localizer, ConsumptionRow row, string period)
    {
        Text = localizer.Format("Consumption.DetailTitle", row.Code, period);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(820, 460);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        static string N(decimal v) => v.ToString("0.###", CultureInfo.CurrentCulture);

        var b = row.Breakdown;
        var r = row.Reconciliation;

        var summary = UiTheme.Grid();
        summary.Dock = DockStyle.Top;
        summary.Height = 250;
        summary.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = localizer["Consumption.Origin"], FillWeight = 60, ReadOnly = true });
        summary.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = localizer["Consumption.Tests"], FillWeight = 20, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        summary.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = row.UnitOfMeasure, FillWeight = 20, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });

        string Units(decimal tests) => N(Math.Round(tests / b.TestsPerUnit, 3));

        summary.Rows.Add(localizer["Consumption.Patients"], N(b.PatientTests), Units(b.PatientTests));
        summary.Rows.Add(localizer["Consumption.Qc"], N(b.QcTests), Units(b.QcTests));
        summary.Rows.Add(localizer["Consumption.Calibrations"], N(b.CalibrationTests), Units(b.CalibrationTests));
        summary.Rows.Add(localizer["Consumption.Reruns"], N(b.RerunTests), Units(b.RerunTests));
        summary.Rows.Add(localizer.Format("Consumption.DeadVolume", N(b.Openings)), N(b.DeadVolumeTests),
                         Units(b.DeadVolumeTests));
        summary.Rows.Add(localizer["Consumption.Theoretical"], N(b.TotalTests), N(r.TheoreticalConsumption));
        summary.Rows.Add(localizer["Consumption.Real"], "—", N(r.RealConsumption));
        summary.Rows.Add(localizer["Consumption.Variance"], "—",
                         r.VariancePercent is { } p ? $"{N(r.Variance)} ({p:0.#} %)" : N(r.Variance));

        var analyses = UiTheme.Grid();
        analyses.Dock = DockStyle.Fill;
        foreach (var key in new[] { "Consumption.Analysis", "Consumption.Patients", "Consumption.Qc",
                                    "Consumption.Calibrations", "Consumption.Reruns" })
            analyses.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = localizer[key], ReadOnly = true });

        foreach (var a in row.Activity)
            analyses.Rows.Add(a.AnalysisCode, a.PatientTests, a.QcTests, a.Calibrations, a.Reruns);

        var notice = new Label
        {
            Text = row.Reconciliation.Computable ? localizer["Consumption.DetailNotice"]
                                                 : localizer["Consumption.NotComputable"],
            Dock = DockStyle.Bottom,
            Height = 40,
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            Padding = new Padding(8, 4, 8, 4)
        };

        Controls.Add(analyses);
        Controls.Add(summary);
        Controls.Add(notice);
    }
}

/// <summary>The variance threshold, the consumption window and the two expiry alert levels.</summary>
internal sealed class StockSettingsDialog : MasterDataEditor
{
    private readonly StockSettings _current;
    private readonly NumericUpDown _threshold, _window, _warning, _critical;

    internal StockSettings? Settings { get; private set; }

    internal StockSettingsDialog(ILocalizer localizer, IUserDialogs dialogs, StockSettings current)
        : base(localizer, dialogs, "Stock.Settings", null, 560, 220)
    {
        _current = current;

        _threshold = Number("Stock.VarianceThreshold", 20, 14, 240, current.VarianceThresholdPercent, 100m, 1);
        _window = Number("Stock.ConsumptionWindow", 290, 14, 240, current.ConsumptionWindowDays, 730m);
        _warning = Number("Stock.ExpiryWarningDays", 20, 70, 240, current.ExpiryWarningDays, 365m);
        _critical = Number("Stock.ExpiryCriticalDays", 290, 70, 240, current.ExpiryCriticalDays, 365m);

        Buttons(160, 560);
    }

    protected override void Accept()
    {
        var settings = _current with
        {
            VarianceThresholdPercent = _threshold.Value,
            ConsumptionWindowDays = (int)_window.Value,
            ExpiryWarningDays = (int)_warning.Value,
            ExpiryCriticalDays = (int)_critical.Value
        };

        if (settings.Validate().IsFailure)
        {
            Dialogs.Warn(Localizer["Stock_SettingOutOfRange"], Text);
            return;
        }

        Settings = settings;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// The stock alerts of the stock screen: lots at the two expiry levels, and items whose stock runs
/// out, at their average consumption, before an order placed today would arrive.
/// </summary>
internal sealed class StockAlertsDialog : Form
{
    internal StockAlertsDialog(ILocalizer localizer, StockAlertSummary alerts)
    {
        Text = localizer["Stock.Alerts"];
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 560);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var culture = CultureInfo.CurrentCulture;
        static string N(decimal v) => v.ToString("0.###", CultureInfo.CurrentCulture);

        var stockOuts = UiTheme.Grid();
        stockOuts.Dock = DockStyle.Top;
        stockOuts.Height = 230;
        foreach (var key in new[] { "Field.Code", "Inventory.Item", "Stock.Usable", "Stock.AverageDaily",
                                    "Stock.StockOutDate", "Stock.LeadTime" })
            stockOuts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = localizer[key], ReadOnly = true });

        foreach (var s in alerts.StockOuts)
        {
            var index = stockOuts.Rows.Add(s.Code, s.Name.Fr, $"{N(s.Usable)} {s.UnitOfMeasure}", N(s.AverageDailyConsumption),
                                           s.StockOutDate.ToString("dd/MM/yyyy", culture),
                                           localizer.Format("Stock.Days", s.LeadTimeDays));
            stockOuts.Rows[index].DefaultCellStyle.ForeColor = UiTheme.Critical;
            stockOuts.Rows[index].DefaultCellStyle.BackColor = UiTheme.CriticalBackground;
        }

        var lots = UiTheme.Grid();
        lots.Dock = DockStyle.Fill;
        foreach (var key in new[] { "Field.Code", "Inventory.Item", "Inventory.LotNumber", "Inventory.Expiry",
                                    "Field.Quantity", "Stock.ExpiryLevel" })
            lots.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = localizer[key], ReadOnly = true });

        foreach (var l in alerts.ExpiringLots)
        {
            var index = lots.Rows.Add(l.ItemCode, l.ItemName.Fr, l.LotNumber, l.ExpiryDate.ToString("dd/MM/yyyy", culture),
                                      $"{N(l.QuantityRemaining)} {l.UnitOfMeasure}",
                                      localizer[$"ExpiryAlertLevel.{l.Level}"]);
            var urgent = l.Level is ExpiryAlertLevel.Critical or ExpiryAlertLevel.Expired;
            lots.Rows[index].DefaultCellStyle.ForeColor = urgent ? UiTheme.Critical : UiTheme.Abnormal;
            lots.Rows[index].DefaultCellStyle.BackColor = urgent ? UiTheme.CriticalBackground : UiTheme.AbnormalBackground;
        }

        Label Heading(string text) => new()
        {
            Text = text, Dock = DockStyle.Top, Height = 26, Font = UiTheme.SectionTitle,
            ForeColor = UiTheme.TextPrimary, Padding = new Padding(4, 4, 4, 0)
        };

        var notice = new Label
        {
            Text = localizer.Format("Stock.AlertsNotice", alerts.Settings.ExpiryWarningDays,
                                    alerts.Settings.ExpiryCriticalDays, alerts.Settings.ConsumptionWindowDays),
            Dock = DockStyle.Bottom,
            Height = 40,
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            Padding = new Padding(8, 4, 8, 4)
        };

        // Docked in reverse: the last added top control sits highest.
        Controls.Add(lots);
        Controls.Add(Heading(localizer.Format("Stock.ExpiringLots", alerts.ExpiringLots.Count)));
        Controls.Add(stockOuts);
        Controls.Add(Heading(localizer.Format("Stock.StockOuts", alerts.StockOuts.Count)));
        Controls.Add(notice);
    }
}
