using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Steering;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Cost price and margin per analysis, per site and per analyser for a period: the average net price
/// charged against the reagent cost of a test and the allocation of indirect costs. Each table
/// exports as CSV.
/// </summary>
public sealed class CostMarginView : LocalizedView
{
    private readonly CostMarginService _costs;
    private readonly ICurrentUser _currentUser;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<CostMarginView>? _logger;

    private readonly PeriodBar _bar;
    // The four key figures as the design system's indicator cards: caption above, figure below.
    private readonly KpiTile[] _tiles = [.. Enumerable.Range(0, 4).Select(_ => new KpiTile())];
    private readonly Label _method = new()
    {
        Dock = DockStyle.Top, Height = 44, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
        Padding = new Padding(0, 8, 0, 4)
    };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _byAnalysis = UiTheme.Grid();
    private readonly DataGridView _bySite = UiTheme.Grid();
    private readonly DataGridView _byInstrument = UiTheme.Grid();
    private readonly TabPage[] _pages;
    private readonly Button _allocation = UiTheme.SecondaryButton(string.Empty);

    private MarginAnalysis? _result;

    public CostMarginView(CostMarginService costs, ICurrentUser currentUser, IDocumentOutput output, IUserDialogs dialogs,
                          ILocalizer localizer, ICultureManager cultureManager, ILogger<CostMarginView>? logger = null)
        : base(localizer, cultureManager)
    {
        _costs = costs;
        _currentUser = currentUser;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;
        _bar = new PeriodBar(localizer);
        Padding = new Padding(UiTheme.Gutter);

        foreach (var grid in new[] { _byAnalysis, _bySite, _byInstrument })
        {
            AddColumn(grid, "code", 8);
            AddColumn(grid, "name", 20);
            AddColumn(grid, "tests", 7, numeric: true);
            AddColumn(grid, "revenue", 10, numeric: true);
            AddColumn(grid, "averagePrice", 8, numeric: true);
            AddColumn(grid, "reagentCost", 10, numeric: true);
            AddColumn(grid, "allocation", 9, numeric: true);
            AddColumn(grid, "costPerTest", 8, numeric: true);
            AddColumn(grid, "marginPerTest", 8, numeric: true);
            AddColumn(grid, "margin", 10, numeric: true);
            AddColumn(grid, "marginPercent", 7, numeric: true);
            AddColumn(grid, "withoutCost", 7, numeric: true);
        }

        _pages = [CardPage(_byAnalysis), CardPage(_bySite), CardPage(_byInstrument)];
        _tabs.TabPages.AddRange(_pages);

        _allocation.AutoSize = true;
        _allocation.Visible = currentUser.Has(Permissions.Steering.ManageCosts);
        _bar.Extra.Controls.Add(_allocation);

        Controls.Add(_tabs);
        Controls.Add(_method);
        Controls.Add(KpiRow(_tiles));
        Controls.Add(_bar);

        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) =>
            await ExportAsync(SelectedGrid, _output, _dialogs, _bar.Export.Text, Localizer);
        _allocation.Click += async (_, _) => await EditAllocationAsync();
    }

    public override string Title => T("Nav.CostMargin");

    /// <summary>The grid of the tab in front, the one the CSV export writes.</summary>
    private DataGridView? SelectedGrid => _tabs.SelectedIndex switch
    {
        0 => _byAnalysis,
        1 => _bySite,
        2 => _byInstrument,
        _ => null
    };

    /// <summary>A tab whose grid sits in a white card on the work surface.</summary>
    private static TabPage CardPage(DataGridView grid)
    {
        var page = new TabPage { BackColor = UiTheme.Surface, Padding = new Padding(0, 12, 0, 0) };
        page.Controls.Add(new FinanceCard(grid));
        return page;
    }

    private static TableLayoutPanel KpiRow(KpiTile[] tiles)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 104,
            ColumnCount = tiles.Length,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 8),
            BackColor = UiTheme.Surface
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < tiles.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / tiles.Length));
            tiles[i].Value.Font = UiTheme.SectionTitle;
            tiles[i].Margin = new Padding(i == 0 ? 0 : 6, 0, i == tiles.Length - 1 ? 0 : 6, 0);
            row.Controls.Add(tiles[i], i, 0);
        }
        return row;
    }

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Steering.CostMarginTitle");
        _pages[0].Text = T("Steering.ByAnalysis");
        _pages[1].Text = T("Steering.BySite");
        _pages[2].Text = T("Steering.ByInstrument");
        _allocation.Text = T("Steering.Allocation");
        _method.Text = T("Steering.CostMethod");

        foreach (var grid in new[] { _byAnalysis, _bySite, _byInstrument })
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderText = T($"Steering.Column.{column.Name}");

        Render();
    }

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            UseWaitCursor = true;
            var result = await _costs.GetAsync(_bar.From, _bar.To, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result, _bar.Compute.Text); return; }
            _result = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing cost and margin failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _bar.Compute.Text);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Render()
    {
        foreach (var grid in new[] { _byAnalysis, _bySite, _byInstrument }) grid.Rows.Clear();
        if (_result is not { } r)
        {
            foreach (var tile in _tiles) tile.Caption.Text = tile.Value.Text = string.Empty;
            return;
        }

        void Tile(int i, string key, string value) => (_tiles[i].Caption.Text, _tiles[i].Value.Text) = (T(key), value);
        Tile(0, "Steering.Column.revenue", $"{Amount(r.Totals.Revenue)} MAD · {Count(r.Totals.Tests)} {T("Steering.TestsUnit")}");
        Tile(1, "Steering.Column.reagentCost", $"{Amount(r.Totals.ReagentCost)} MAD");
        Tile(2, "Steering.Column.allocation", $"{Amount(r.Totals.Allocation)} MAD ({Amount(r.AllocationPerTest)} {T("Steering.PerTest")})");
        Tile(3, "Steering.Column.margin", $"{Amount(r.Totals.Margin)} MAD · {Share(r.Totals.MarginPercent)}");
        _tiles[1].Value.ForeColor = r.Totals.TestsWithoutCost > 0 ? UiTheme.Abnormal : UiTheme.TextPrimary;

        void Fill(DataGridView grid, IEnumerable<MarginRow> rows)
        {
            foreach (var m in rows)
            {
                var i = grid.Rows.Add(m.Code, m.Name, Count(m.Tests), Amount(m.Revenue), Amount(m.AverageNetPrice),
                                      Amount(m.ReagentCost), Amount(m.Allocation), Amount(m.CostPerTest),
                                      Amount(m.MarginPerTest), Amount(m.Margin), Share(m.MarginPercent),
                                      m.TestsWithoutCost == 0 ? string.Empty : Count(m.TestsWithoutCost));
                if (m.Margin < 0m) grid.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Critical;
                else if (m.TestsWithoutCost > 0) grid.Rows[i].Cells["withoutCost"].Style.ForeColor = UiTheme.Abnormal;
            }
        }

        Fill(_byAnalysis, r.ByAnalysis);
        Fill(_bySite, r.BySite);
        Fill(_byInstrument, r.ByInstrument);
        _bar.Heading.Text = $"{T("Steering.CostMarginTitle")} — {Date(r.From)} → {Date(r.To)}";
    }

    private async Task EditAllocationAsync()
    {
        var current = await _costs.GetSettingsAsync();
        if (current.IsFailure) { _dialogs.ShowFailure(current, _allocation.Text); return; }

        using var dialog = new FieldDialog(Localizer, _allocation.Text, height: 260);
        dialog.Note(T("Steering.AllocationHelp"));
        var amount = dialog.Number(T("Steering.AllocationPerTest"), current.Value.AllocationPerTestMad, 0m, 100000m, 2);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var saved = await _costs.SaveSettingsAsync(new CostSettings { AllocationPerTestMad = amount.Value });
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, _allocation.Text); return; }
        await LoadAsync();
    }
}
