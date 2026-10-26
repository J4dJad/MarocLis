using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Relations;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// What each prescriber and each clinic sends: orders, analyses and revenue for the period, against
/// the period before and the same period last year, month by month, with the prescribers who
/// started and those who stopped sending. Every site the operator may open is counted.
/// </summary>
public sealed class PrescriberStatisticsView : LocalizedView
{
    private readonly PrescriberStatisticsService _statistics;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PrescriberStatisticsView>? _logger;

    private readonly PeriodBar _bar;
    private readonly KpiTile[] _tiles = Enumerable.Range(0, 4).Select(_ => new KpiTile()).ToArray();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly DataGridView _prescribers = UiTheme.Grid();
    private readonly DataGridView _institutions = UiTheme.Grid();
    private readonly DataGridView _months = UiTheme.Grid();
    private readonly DataGridView _new = UiTheme.Grid();
    private readonly DataGridView _lost = UiTheme.Grid();
    private readonly DataGridView _detail = UiTheme.Grid();
    private readonly TabPage[] _pages;

    private PrescriberStatistics? _result;

    public PrescriberStatisticsView(PrescriberStatisticsService statistics, IDocumentOutput output, IUserDialogs dialogs,
                                    ILocalizer localizer, ICultureManager cultureManager,
                                    ILogger<PrescriberStatisticsView>? logger = null)
        : base(localizer, cultureManager)
    {
        _statistics = statistics;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;
        _bar = new PeriodBar(localizer);

        Padding = new Padding(24, 16, 24, 16);

        foreach (var grid in new[] { _prescribers, _new, _lost })
        {
            AddColumn(grid, "name", 22);
            AddColumn(grid, "institution", 18);
            AddVolumeColumns(grid);
        }

        AddColumn(_institutions, "institution", 30);
        AddVolumeColumns(_institutions);

        AddColumn(_months, "month", 12);
        AddColumn(_months, "orders", 9, numeric: true);
        AddColumn(_months, "tests", 9, numeric: true);
        AddColumn(_months, "revenue", 12, numeric: true);
        AddColumn(_months, "lyOrders", 9, numeric: true);
        AddColumn(_months, "lyRevenue", 12, numeric: true);
        AddColumn(_months, "vsLastYear", 9, numeric: true);
        AddColumn(_months, "active", 9, numeric: true);
        AddColumn(_months, "newOnes", 9, numeric: true);

        AddColumn(_detail, "name", 24);
        AddColumn(_detail, "institution", 20);
        AddColumn(_detail, "month", 10);
        AddColumn(_detail, "orders", 9, numeric: true);
        AddColumn(_detail, "tests", 9, numeric: true);
        AddColumn(_detail, "revenue", 12, numeric: true);

        _months.Column("month").DefaultCellStyle.Font = UiTheme.Monospace;
        _detail.Column("month").DefaultCellStyle.Font = UiTheme.Monospace;

        _pages = [.. new[] { _prescribers, _institutions, _months, _new, _lost, _detail }.Select(Pilotage.CardPage)];
        _tabs.TabPages.AddRange(_pages);

        Controls.Add(_tabs);
        Controls.Add(Tiles(_tiles, 4, 92));
        Controls.Add(Pilotage.Gap(14));
        Controls.Add(_bar);

        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) =>
            await ExportAsync(Pilotage.GridOf(_tabs.SelectedTab), _output, _dialogs,
                              _bar.Export.Text, Localizer);
    }

    private static void AddVolumeColumns(DataGridView grid)
    {
        AddColumn(grid, "orders", 8, numeric: true);
        AddColumn(grid, "tests", 8, numeric: true);
        AddColumn(grid, "revenue", 11, numeric: true);
        AddColumn(grid, "prevOrders", 8, numeric: true);
        AddColumn(grid, "prevRevenue", 11, numeric: true);
        AddColumn(grid, "ordersVsPrevious", 8, numeric: true);
        AddColumn(grid, "ordersVsLastYear", 8, numeric: true);
        AddColumn(grid, "revenueVsPrevious", 8, numeric: true);
        AddColumn(grid, "revenueVsLastYear", 8, numeric: true);
    }

    public override string Title => T("Nav.PrescriberStatistics");

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Relations.PrescriberStatisticsTitle");

        foreach (var tab in Enum.GetValues<PrescriberStatisticsTab>())
            _pages[(int)tab].Text = T(PrescriberStatisticsScreenRules.TabKey(tab));

        foreach (var grid in new[] { _prescribers, _institutions, _months, _new, _lost, _detail })
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderText = T($"Relations.Column.{column.Name}");

        Render();
    }

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            UseWaitCursor = true;
            var result = await _statistics.GetAsync(_bar.From, _bar.To, ct);
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
            _logger?.LogError(ex, "Computing prescriber statistics failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _bar.Compute.Text);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Render()
    {
        var grids = new[] { _prescribers, _institutions, _months, _new, _lost, _detail };
        foreach (var grid in grids) grid.Rows.Clear();
        if (_result is not { } s)
        {
            foreach (var tile in _tiles) tile.Text = string.Empty;
            _bar.Subtitle.Text = string.Empty;
            return;
        }

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string Money(decimal value) => Domain.Common.Money.Of(value).Format(Localizer.CurrentLanguage);

        var tiles = PrescriberStatisticsScreenRules.Tiles(s, T, Money, culture);
        for (var i = 0; i < _tiles.Length && i < tiles.Count; i++) ReportGrids.Show(_tiles[i], tiles[i]);

        foreach (var tab in Enum.GetValues<PrescriberStatisticsTab>())
        {
            var grid = grids[(int)tab];
            Fill(grid, PrescriberStatisticsScreenRules.Rows(s, tab, T, Amount, culture));
            // A prescriber who started sending in the period, on the green of a normal result.
            for (var i = 0; i < grid.Rows.Count; i++)
                if (PrescriberStatisticsScreenRules.IsNew(s, tab, i)) grid.Rows[i].DefaultCellStyle.BackColor = UiTheme.NormalBackground;
        }

        _bar.Subtitle.Text = PrescriberStatisticsScreenRules.Subtitle(s, T);
    }
}
