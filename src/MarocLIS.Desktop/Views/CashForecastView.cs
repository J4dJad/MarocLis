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
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        _patients.Location = new Point(0, 40);
        _overdue.Location = new Point(250, 40);
        _payDayLabel.Font = UiTheme.Small;
        _payDayLabel.ForeColor = UiTheme.TextSecondary;
        _payDayLabel.AutoSize = true;
        _payDayLabel.Location = new Point(520, 42);
        _payDay.Bounds = new Rectangle(640, 38, 60, UiTheme.InputHeight);
        _refresh.Bounds = new Rectangle(716, 34, 110, UiTheme.ButtonHeight);
        _items.Bounds = new Rectangle(836, 34, 190, UiTheme.ButtonHeight);
        _csv.Bounds = new Rectangle(1036, 34, 110, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _patients, _overdue, _payDayLabel, _payDay, _refresh, _items, _csv]);

        BankReconciliationView.AddColumns(_grid, ("week", 5), ("from", 9), ("to", 9), ("opening", 11), ("payers", 11),
                                          ("patients", 11), ("suppliers", 11), ("payroll", 11), ("other", 10),
                                          ("net", 11), ("closing", 12));
        foreach (DataGridViewColumn column in _grid.Columns)
            if (column.Index >= 3) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not ForecastWeek week) return;
            if (week.ClosingMad < 0m)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };

        _notes.Dock = DockStyle.Bottom;
        _notes.Height = 96;
        _notes.Font = UiTheme.Small;
        _notes.ForeColor = UiTheme.TextSecondary;

        Controls.Add(_grid);
        Controls.Add(_notes);
        Controls.Add(bar);
    }

    private IReadOnlyList<string> Headers() =>
    [
        T("Treasury.Week"), T("Field.From"), T("Field.To"), T("Treasury.Opening"), T("Treasury.PayerReceipts"),
        T("Treasury.PatientReceipts"), T("Treasury.SupplierPayments"), T("Treasury.Payroll"), T("Treasury.OtherFlows"),
        T("Treasury.Net"), T("Treasury.Closing")
    ];

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.CashForecast");
        _patients.Text = T("Treasury.IncludePatients");
        _overdue.Text = T("Treasury.IncludeOverdue");
        _payDayLabel.Text = T("Treasury.PayDay");
        _refresh.Text = T("Action.Refresh");
        _items.Text = T("Treasury.ForecastItems");
        _csv.Text = T("Treasury.Csv");

        var headers = Headers();
        for (var i = 0; i < _grid.Columns.Count; i++) _grid.Columns[i].HeaderText = headers[i];

        Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _treasury.GetForecastAsync(new CashForecastOptions
            {
                IncludePatientReceipts = _patients.Checked,
                IncludeOverduePayerReceipts = _overdue.Checked,
                PayrollDay = (int)_payDay.Value
            }, ct);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _forecast = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing the cash forecast failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private string Amount(decimal value) => Money.Of(value).Format(Localizer.CurrentLanguage);

    private void Render()
    {
        _grid.Rows.Clear();
        if (_forecast is null) { _notes.Text = string.Empty; return; }

        foreach (var w in _forecast.Weeks)
        {
            var index = _grid.Rows.Add(w.Number.ToString(CultureInfo.InvariantCulture),
                                       w.Start.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                                       w.End.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                                       Amount(w.OpeningMad), Amount(w.PayerReceiptsMad), Amount(w.PatientReceiptsMad),
                                       Amount(w.SupplierPaymentsMad), Amount(w.PayrollMad), Amount(w.OtherMad),
                                       Amount(w.NetMad), Amount(w.ClosingMad));
            _grid.Rows[index].Tag = w;
        }

        var openings = _forecast.Openings.Count == 0
            ? T("Treasury.NoBankAccount")
            : string.Join("   ·   ", _forecast.Openings.Select(o => o.IsRecordedNotOnStatement
                // What the opening adds to the statements, so the director sees what it opens on.
                ? T("Treasury.OpeningRecordedNotOnStatement",
                    (o.AsOf ?? _forecast.From).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), Amount(o.BalanceMad))
                : $"{o.Account} : {Amount(o.BalanceMad)}" +
                  (o.AsOf is { } asOf ? $" ({asOf.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)})" : string.Empty)));

        _notes.Text = string.Join(Environment.NewLine,
            $"{T("Treasury.Opening")} : {openings}",
            T("Treasury.ForecastNotes", Amount(_forecast.OverduePayerReceiptsMad),
              Amount(_forecast.UnapprovedSupplierInvoicesMad), Amount(_forecast.LastPayrollNetMad),
              Amount(_forecast.WeeklyPatientAverageMad)),
            $"{T("Treasury.LowestClosing")} : {Amount(_forecast.LowestClosingMad)}",
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

        var opened = await _output.OpenAsync(CashForecastCalculator.ToCsv(_forecast, Headers()), "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _csv.Text);
    }
}
