using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
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
    private readonly FinanceKpiTile[] _tiles = [.. Enumerable.Range(0, 4).Select(_ => new FinanceKpiTile())];
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

        int[] weights = [8, 20, 7, 10, 8, 10, 9, 8, 8, 10, 7, 7];
        foreach (var grid in new[] { _byAnalysis, _bySite, _byInstrument })
            for (var c = 0; c < CostMarginScreenRules.Columns.Count; c++)
            {
                var column = CostMarginScreenRules.Columns[c];
                AddColumn(grid, column, weights[c], numeric: CostMarginScreenRules.IsNumeric(column));
            }

        _pages = [CardPage(_byAnalysis), CardPage(_bySite), CardPage(_byInstrument)];
        _tabs.TabPages.AddRange(_pages);

        _allocation.AutoSize = true;
        _allocation.Visible = CostMarginScreenRules.Actions(currentUser.Has, null).Allocation;
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

    private static TableLayoutPanel KpiRow(FinanceKpiTile[] tiles)
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
        _pages[0].Text = T(CostMarginScreenRules.TabKey(MarginTab.ByAnalysis));
        _pages[1].Text = T(CostMarginScreenRules.TabKey(MarginTab.BySite));
        _pages[2].Text = T(CostMarginScreenRules.TabKey(MarginTab.ByInstrument));
        _allocation.Text = T("Steering.Allocation");
        _method.Text = T("Steering.CostMethod");

        foreach (var grid in new[] { _byAnalysis, _bySite, _byInstrument })
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderText = T(CostMarginScreenRules.ColumnKey(column.Name));

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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing cost and margin failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _bar.Compute.Text);
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

        // The figures written with their currency (« DH »), no longer « MAD » fixed in the text.
        string Money(decimal value) => Domain.Common.Money.Of(value).Format(Localizer.CurrentLanguage);
        var tiles = CostMarginScreenRules.Tiles(r, T, Money, CultureInfo.CurrentCulture);
        for (var t = 0; t < tiles.Count; t++)
        {
            (_tiles[t].Caption.Text, _tiles[t].Value.Text) = (T(tiles[t].CaptionKey), tiles[t].Value);
            _tiles[t].Value.ForeColor = tiles[t].Ink switch
            {
                StockInk.Abnormal => UiTheme.Abnormal,
                StockInk.Critical => UiTheme.Critical,
                _ => UiTheme.TextPrimary
            };
        }

        void Fill(DataGridView grid, MarginTab tab)
        {
            foreach (var m in CostMarginScreenRules.Rows(r, tab))
            {
                var i = grid.Rows.Add([.. CostMarginScreenRules.Cells(m, Amount, CultureInfo.CurrentCulture)]);
                if (CostMarginScreenRules.IsLoss(m)) grid.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Critical;
                else if (CostMarginScreenRules.WarnsWithoutCost(m)) grid.Rows[i].Cells["withoutCost"].Style.ForeColor = UiTheme.Abnormal;
            }
        }

        Fill(_byAnalysis, MarginTab.ByAnalysis);
        Fill(_bySite, MarginTab.BySite);
        Fill(_byInstrument, MarginTab.ByInstrument);
        _bar.Heading.Text = CostMarginScreenRules.Heading(r, T);
    }

    private async Task EditAllocationAsync()
    {
        var current = await _costs.GetSettingsAsync();
        if (current.IsFailure) { _dialogs.ShowFailure(current, _allocation.Text); return; }

        // The window title is the action without its access-key mark and its ellipsis.
        using var dialog = new FieldDialog(Localizer, UserDialogs.CaptionOf(_allocation.Text, _allocation.Text), height: 260);
        dialog.Note(T("Steering.AllocationHelp"));
        var amount = dialog.Number(T("Steering.AllocationPerTest"), current.Value.AllocationPerTestMad, 0m,
                                   CostSettings.MaximumAllocationPerTestMad, 2);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var (refusal, settings) = CostMarginScreenRules.Check(amount.Value.ToString(CultureInfo.InvariantCulture));
        if (refusal is not null) { _dialogs.Warn(T(refusal.Key, refusal.Typed), _allocation.Text); return; }

        var saved = await _costs.SaveSettingsAsync(settings!);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, _allocation.Text); return; }
        await LoadAsync();
    }
}
