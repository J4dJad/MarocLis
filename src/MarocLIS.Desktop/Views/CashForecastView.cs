using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The 90-day cash forecast, week by week: the bank balances today, what the payers should pay on
/// the batches they have, the supplier invoices falling due, the salaries, the flows entered by
/// hand, and the balance each week closes on. A week closing below zero shows in red.
/// </summary>
public sealed class CashForecastView : LocalizedView
{
    private readonly TreasuryService _treasury;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<CashForecastView>? _logger;

    private readonly Label _heading = new();
    private readonly CheckBox _patients = new() { AutoSize = true };
    private readonly CheckBox _overdue = new() { AutoSize = true };
    private readonly Label _payDayLabel = new();
    private readonly NumericUpDown _payDay = new() { Minimum = 0, Maximum = 31 };
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _items = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _csv = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _notes = new();
    private readonly Label _count = new();
    private readonly Label _weeksTitle = new();
    private readonly Label _notesTitle = new();

    private CashForecastResult? _forecast;

    public CashForecastView(TreasuryService treasury, IDocumentOutput output, IUserDialogs dialogs,
                            ILocalizer localizer, ICultureManager cultureManager,
                            ILogger<CashForecastView>? logger = null)
        : base(localizer, cultureManager)
    {
        _treasury = treasury;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();

        _refresh.Click += async (_, _) => await ActivateAsync();
        _patients.CheckedChanged += async (_, _) => await ActivateAsync();
        _overdue.CheckedChanged += async (_, _) => await ActivateAsync();
        _items.Click += async (_, _) => await RunBusyAsync(ItemsAsync);
        _csv.Click += async (_, _) => await RunBusyAsync(ExportAsync);
    }

    public override string Title => T("Nav.CashForecast");

    private void BuildLayout()
    {
        Padding = new Padding(24, 20, 24, 16);

        // The title, the lowest closing balance in grey, and the actions at the far end.
        var header = FinanceUi.PageHeader(_heading, _count, _items, _csv, _refresh);

        _payDay.Width = 60;
        _patients.Margin = new Padding(0, 8, 16, 0);
        _overdue.Margin = new Padding(0, 8, 16, 0);
        var filters = FinanceUi.FilterRow(_patients, _overdue, FinanceUi.Inline(_payDayLabel, _payDay));
        _patients.Margin = new Padding(0, 8, 16, 0);
        _overdue.Margin = new Padding(0, 8, 16, 0);

        BankReconciliationView.AddColumns(_grid, ("week", 7), ("from", 9), ("to", 9), ("opening", 11), ("payers", 11),
                                          ("patients", 11), ("suppliers", 11), ("payroll", 11), ("other", 10),
                                          ("net", 11), ("closing", 12));
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.DefaultCellStyle.Font = UiTheme.Monospace;
            if (column.Index >= 3) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        _grid.Columns["closing"]!.DefaultCellStyle.Font = UiTheme.MonospaceBold;

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not ForecastWeek week) return;
            if (CashForecastScreenRules.IsShortfall(week))
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };

        _notes.Dock = DockStyle.Fill;
        _notes.Font = UiTheme.Small;
        _notes.ForeColor = UiTheme.TextSecondary;
        _notes.Padding = new Padding(16, 0, 16, 4);

        var notesCard = new FinanceCard(_notes, FinanceUi.CardHeader(_notesTitle, null))
        {
            Dock = DockStyle.Bottom,
            Height = 150
        };
        var gap = new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };

        Controls.Add(new FinanceCard(_grid, FinanceUi.CardHeader(_weeksTitle, null)));
        Controls.Add(gap);
        Controls.Add(notesCard);
        Controls.Add(filters);
        Controls.Add(header);
    }

    private IReadOnlyList<string> Headers() => CashForecastScreenRules.Headers(T);

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.CashForecast");
        _patients.Text = T("Treasury.IncludePatients");
        _overdue.Text = T("Treasury.IncludeOverdue");
        _payDayLabel.Text = T("Treasury.PayDay");
        _refresh.Text = T("Action.Refresh");
        _items.Text = T("Treasury.ForecastItems");
        _csv.Text = T("Treasury.Csv");
        _weeksTitle.Text = T("Finance.ForecastWeeks");
        _notesTitle.Text = T("Finance.ForecastAssumptions");

        var headers = Headers();
        for (var i = 0; i < _grid.Columns.Count; i++) _grid.Columns[i].HeaderText = headers[i];

        Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _treasury.GetForecastAsync(CashForecastScreenRules.Options(new CashForecastForm
            {
                IncludePatientReceipts = _patients.Checked,
                IncludeOverduePayerReceipts = _overdue.Checked,
                PayrollDay = (int)_payDay.Value
            }), ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _forecast = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing the cash forecast failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private string Amount(decimal value) => Money.Of(value).Format(Localizer.CurrentLanguage);

    private void Render()
    {
        _grid.Rows.Clear();
        if (_forecast is null) { _notes.Text = string.Empty; _count.Text = string.Empty; return; }

        var lowest = CashForecastScreenRules.LowestClosing(_forecast, T, Amount);
        _count.Text = lowest.Text;
        _count.ForeColor = lowest.Critical ? UiTheme.Critical : UiTheme.TextSecondary;

        foreach (var w in _forecast.Weeks)
        {
            var index = _grid.Rows.Add([w.Number.ToString(CultureInfo.InvariantCulture),
                                        CashForecastScreenRules.Date(w.Start), CashForecastScreenRules.Date(w.End),
                                        .. CashForecastScreenRules.Amounts(w).Select(Amount)]);
            _grid.Rows[index].Tag = w;
        }

        // What the opening adds to the statements, so the director sees what it opens on.
        var openings = string.Join("   ·   ", CashForecastScreenRules.Openings(_forecast, T, Amount));

        _notes.Text = string.Join(Environment.NewLine,
            $"{T("Treasury.Opening")} : {openings}",
            CashForecastScreenRules.Notes(_forecast, T, Amount),
            lowest.Text,
            T("Treasury.ForecastMethod"));
    }

    private async Task ItemsAsync()
    {
        using var dialog = new ForecastItemsDialog(Localizer, _dialogs, _treasury,
                                                   PermissionGate.Allows(Permissions.Treasury.Manage));
        dialog.ShowDialog(this);
        await ActivateAsync();
    }

    private async Task ExportAsync()
    {
        if (_forecast is null) return;

        var opened = await _output.OpenAsync(CashForecastScreenRules.Csv(_forecast, T), "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _csv.Text);
    }
}
