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

        _pages[0].Text = T("Relations.ByPrescriber");
        _pages[1].Text = T("Relations.ByInstitution");
        _pages[2].Text = T("Relations.ByMonth");
        _pages[3].Text = T("Relations.NewPrescribers");
        _pages[4].Text = T("Relations.LostPrescribers");
        _pages[5].Text = T("Relations.MonthlyDetail");

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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing prescriber statistics failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _bar.Compute.Text);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private string NameOf(PrescriberVolumeRow row) => row.PrescriberId is null ? T("Relations.NoPrescriber") : row.Name ?? "?";

    private void Render()
    {
        foreach (var grid in new[] { _prescribers, _institutions, _months, _new, _lost, _detail }) grid.Rows.Clear();
        if (_result is not { } s)
        {
            foreach (var tile in _tiles) tile.Text = string.Empty;
            _bar.Subtitle.Text = string.Empty;
            return;
        }

        string Tile(string key, string value) => $"{T(key)}\n{value}";
        _tiles[0].Text = Tile("Relations.Column.orders",
            $"{Count(s.Total.Orders)}  ({Percent(PrescriberStatisticsService.Change(s.Total.Orders, s.TotalPrevious.Orders))})");
        _tiles[1].Text = Tile("Relations.Column.revenue",
            $"{Amount(s.Total.Revenue)} MAD  ({Percent(PrescriberStatisticsService.Change(s.Total.Revenue, s.TotalPrevious.Revenue))})");
        _tiles[2].Text = Tile("Relations.NewPrescribers", Count(s.NewPrescribers.Count));
        _tiles[3].Text = Tile("Relations.LostPrescribers", Count(s.LostPrescribers.Count));

        void Volume(DataGridView grid, IEnumerable<PrescriberVolumeRow> rows, bool withName)
        {
            foreach (var r in rows)
            {
                var cells = new List<object>();
                if (withName) cells.Add(NameOf(r));
                cells.Add(r.Institution ?? (withName ? string.Empty : T("Relations.NoInstitution")));
                cells.AddRange([Count(r.Current.Orders), Count(r.Current.Tests), Amount(r.Current.Revenue),
                                Count(r.Previous.Orders), Amount(r.Previous.Revenue),
                                Percent(r.OrdersVsPrevious), Percent(r.OrdersVsLastYear),
                                Percent(r.RevenueVsPrevious), Percent(r.RevenueVsLastYear)]);
                var index = grid.Rows.Add([.. cells]);
                if (r.IsNew) grid.Rows[index].DefaultCellStyle.BackColor = UiTheme.NormalBackground;
            }
        }

        Volume(_prescribers, s.Prescribers, withName: true);
        Volume(_institutions, s.Institutions, withName: false);
        Volume(_new, s.NewPrescribers, withName: true);
        Volume(_lost, s.LostPrescribers, withName: true);

        foreach (var m in s.Months)
            _months.Rows.Add(m.Month.ToString("MM/yyyy", System.Globalization.CultureInfo.CurrentCulture),
                             Count(m.Current.Orders), Count(m.Current.Tests), Amount(m.Current.Revenue),
                             Count(m.LastYear.Orders), Amount(m.LastYear.Revenue), Percent(m.OrdersVsLastYear),
                             Count(m.ActivePrescribers), Count(m.NewPrescribers));

        foreach (var d in s.Detail)
            _detail.Rows.Add(d.PrescriberId is null ? T("Relations.NoPrescriber") : d.Name ?? "?", d.Institution ?? string.Empty,
                             d.Month.ToString("MM/yyyy", System.Globalization.CultureInfo.CurrentCulture),
                             Count(d.Figures.Orders), Count(d.Figures.Tests), Amount(d.Figures.Revenue));

        _bar.Subtitle.Text = $"{Date(s.From)} → {Date(s.To)}"
                             + $"  ({T("Relations.ComparedWith")} {Date(s.PreviousFrom)} → {Date(s.PreviousTo)})";
    }
}
