using System.Globalization;
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

    // The share of each column, in the rules' order of the columns.
    private static readonly IReadOnlyDictionary<StatisticsTab, int[]> Weights = new Dictionary<StatisticsTab, int[]>
    {
        [StatisticsTab.ByDepartment] = [8, 24, 9, 9, 10, 10, 12],
        [StatisticsTab.ByTest] = [8, 30, 9, 10, 12],
        [StatisticsTab.ByPayer] = [28, 9, 9, 12, 12, 12],
        [StatisticsTab.ByDay] = [12, 10, 10]
    };

    private DataGridView GridOf(StatisticsTab tab) => tab switch
    {
        StatisticsTab.ByTest => _tests,
        StatisticsTab.ByPayer => _payers,
        StatisticsTab.ByDay => _days,
        _ => _departments
    };

    private TabPage PageOf(StatisticsTab tab) => tab switch
    {
        StatisticsTab.ByTest => _testPage,
        StatisticsTab.ByPayer => _payerPage,
        StatisticsTab.ByDay => _dayPage,
        _ => _departmentPage
    };

    private void BuildColumns()
    {
        foreach (var tab in Enum.GetValues<StatisticsTab>())
        {
            var grid = GridOf(tab);
            var columns = StatisticsScreenRules.Columns(tab);
            for (var c = 0; c < columns.Count; c++)
            {
                ReportGrids.AddColumn(grid, columns[c].Name, Weights[tab][c], columns[c].Numeric);
                // Codes and dates in the fixed-pitch face, as everywhere an identifier is read off a list.
                if (StatisticsScreenRules.IsIdentifier(columns[c].Name)) grid.Column(columns[c].Name).DefaultCellStyle.Font = UiTheme.Monospace;
            }
        }
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

        foreach (var tab in Enum.GetValues<StatisticsTab>())
        {
            PageOf(tab).Text = T(StatisticsScreenRules.TabKey(tab));
            foreach (var column in StatisticsScreenRules.Columns(tab))
                GridOf(tab).Column(column.Name).HeaderText = T(column.CaptionKey);
        }

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

        var culture = CultureInfo.CurrentCulture;
        string Money(decimal value) => Domain.Common.Money.Of(value).Format(Localizer.CurrentLanguage);

        var tiles = StatisticsScreenRules.Tiles(stats, T, Money, culture);
        for (var i = 0; i < _figures.Length; i++)
        {
            if (i < tiles.Count) ReportGrids.Show(_figures[i], tiles[i]);
            else _figures[i].Text = string.Empty;
        }

        foreach (var tab in Enum.GetValues<StatisticsTab>())
            ReportGrids.Fill(GridOf(tab), StatisticsScreenRules.Rows(stats, tab, Localizer.CurrentLanguage, T, ReportGrids.Amount, culture));

        _bar.Subtitle.Text = StatisticsScreenRules.Subtitle(stats);
        _bar.Export.Enabled = true;
    }

    /// <summary>
    /// The visible table as CSV, semicolon-separated with a BOM: what a French-locale Excel opens
    /// directly into columns, accents intact (ReportGrids.ExportAsync, the shared rules' CSV).
    /// </summary>
    private async Task ExportAsync()
    {
        var grid = Pilotage.GridOf(_tabs.SelectedTab);
        if (grid is null || grid.Rows.Count == 0) return;

        try
        {
            await ReportGrids.ExportAsync(grid, _output, _dialogs, _bar.Export.Text, Localizer);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting statistics failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _bar.Export.Text);
        }
    }
}
