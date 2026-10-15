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
    private readonly Label[] _tiles = Enumerable.Range(0, 4).Select(_ => new Label()).ToArray();
    private readonly Label _method = new()
    {
        Dock = DockStyle.Top, Height = 40, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary
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

        _pages = [Page(_byAnalysis), Page(_bySite), Page(_byInstrument)];
        _tabs.TabPages.AddRange(_pages);

        _allocation.AutoSize = true;
        _allocation.Visible = currentUser.Has(Permissions.Steering.ManageCosts);
        _bar.Extra.Controls.Add(_allocation);

        Controls.Add(_tabs);
        Controls.Add(_method);
        Controls.Add(Tiles(_tiles, 4, 80));
        Controls.Add(_bar);

        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) =>
            await ExportAsync(_tabs.SelectedTab?.Controls.OfType<DataGridView>().FirstOrDefault(), _output, _dialogs,
                              _bar.Export.Text, Localizer);
        _allocation.Click += async (_, _) => await EditAllocationAsync();
    }

    public override string Title => T("Nav.CostMargin");

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
            foreach (var tile in _tiles) tile.Text = string.Empty;
            return;
        }

        string Tile(string key, string value) => $"{T(key)}\n{value}";
        _tiles[0].Text = Tile("Steering.Column.revenue", $"{Amount(r.Totals.Revenue)} MAD · {Count(r.Totals.Tests)} {T("Steering.TestsUnit")}");
        _tiles[1].Text = Tile("Steering.Column.reagentCost", $"{Amount(r.Totals.ReagentCost)} MAD");
        _tiles[2].Text = Tile("Steering.Column.allocation", $"{Amount(r.Totals.Allocation)} MAD ({Amount(r.AllocationPerTest)} {T("Steering.PerTest")})");
        _tiles[3].Text = Tile("Steering.Column.margin", $"{Amount(r.Totals.Margin)} MAD · {Share(r.Totals.MarginPercent)}");
        _tiles[1].ForeColor = r.Totals.TestsWithoutCost > 0 ? UiTheme.Abnormal : UiTheme.TextPrimary;

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
