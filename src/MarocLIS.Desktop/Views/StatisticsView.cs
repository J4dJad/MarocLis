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

    private readonly PeriodBar _bar;
    private readonly KpiTile[] _figures = Enumerable.Range(0, 8).Select(_ => new KpiTile()).ToArray();

    private readonly TabControl _tabs = new();
    private readonly DataGridView _departments = UiTheme.Grid();
    private readonly DataGridView _tests = UiTheme.Grid();
    private readonly DataGridView _payers = UiTheme.Grid();
    private readonly DataGridView _days = UiTheme.Grid();
    private readonly TabPage _departmentPage;
    private readonly TabPage _testPage;
    private readonly TabPage _payerPage;
    private readonly TabPage _dayPage;

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
        _bar = new PeriodBar(localizer);

        _departmentPage = Pilotage.CardPage(_departments);
        _testPage = Pilotage.CardPage(_tests);
        _payerPage = Pilotage.CardPage(_payers);
        _dayPage = Pilotage.CardPage(_days);

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Statistics");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(24, 16, 24, 16);

        BuildColumns();
        _tabs.TabPages.AddRange([_departmentPage, _testPage, _payerPage, _dayPage]);
        _tabs.Dock = DockStyle.Fill;

        // Key figures: two rows of four, large enough to read across a meeting room.
        Controls.Add(_tabs);
        Controls.Add(ReportGrids.Tiles(_figures, 4, 172));
        Controls.Add(Pilotage.Gap(14));
        Controls.Add(_bar);
    }

    private static void AddColumn(DataGridView grid, string name, int weight, bool numeric = false) =>
        ReportGrids.AddColumn(grid, name, weight, numeric);

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

        // Codes and dates in the fixed-pitch face, as everywhere an identifier is read off a list.
        foreach (var column in new[] { _departments.Column("code"), _tests.Column("code"), _days.Column("date") })
            column.DefaultCellStyle.Font = UiTheme.Monospace;
    }

    private void WireBehaviour()
    {
        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) => await ExportAsync();
    }

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Statistics.Title");

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
            var result = await _statistics.GetActivityAsync(_bar.From, _bar.To, ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, _bar.Compute.Text);
                return;
            }

            _result = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing activity statistics failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _bar.Compute.Text);
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
            _bar.Subtitle.Text = string.Empty;
            _bar.Export.Enabled = false;
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

        _bar.Subtitle.Text = $"{ReportGrids.Date(stats.From)} → {ReportGrids.Date(stats.To)}";
        _bar.Export.Enabled = true;
    }

    /// <summary>
    /// The visible table as CSV, semicolon-separated with a BOM: what a French-locale Excel opens
    /// directly into columns, accents intact.
    /// </summary>
    private async Task ExportAsync()
    {
        var grid = Pilotage.GridOf(_tabs.SelectedTab);
        if (grid is null || grid.Rows.Count == 0) return;

        // Patient, prescriber and test names come from the database: the shared cell neutralises
        // formulas for Excel.
        static string Cell(object? value) =>
            MarocLIS.Application.Inventory.CsvCell.Of(Convert.ToString(value, CultureInfo.CurrentCulture));

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
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _bar.Export.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting statistics failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _bar.Export.Text);
        }
    }
}
