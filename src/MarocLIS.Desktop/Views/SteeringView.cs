using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Steering;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// "Pilotage": the director's view of a period across every site the operator may open — revenue,
/// margin and reagent cost, receivables by age, payroll against revenue (with the salary right
/// only), quality indicators and turnaround — with the revenue broken down by day, site, payer and
/// prescriber.
/// </summary>
public sealed class SteeringView : LocalizedView
{
    private readonly SteeringService _steering;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SteeringView>? _logger;

    private readonly PeriodBar _bar;
    private readonly KpiTile[] _tiles = Enumerable.Range(0, 8).Select(_ => new KpiTile()).ToArray();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _days = UiTheme.Grid();
    private readonly DataGridView _sites = UiTheme.Grid();
    private readonly DataGridView _payers = UiTheme.Grid();
    private readonly DataGridView _prescribers = UiTheme.Grid();
    private readonly DataGridView _ageing = UiTheme.Grid();
    private readonly DataGridView _quality = UiTheme.Grid();
    private readonly TabPage[] _pages;

    private SteeringDashboard? _result;

    public SteeringView(SteeringService steering, IDocumentOutput output, IUserDialogs dialogs, ILocalizer localizer,
                        ICultureManager cultureManager, ILogger<SteeringView>? logger = null)
        : base(localizer, cultureManager)
    {
        _steering = steering;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;
        _bar = new PeriodBar(localizer);
        Padding = new Padding(24, 16, 24, 16);

        foreach (var tab in Enum.GetValues<SteeringTab>())
            foreach (var column in SteeringScreenRules.Columns(tab))
                AddColumn(GridOf(tab), column.Name, Weight(column.Name), column.Numeric);

        _pages = [.. new[] { _days, _sites, _payers, _prescribers, _ageing, _quality }.Select(Pilotage.CardPage)];
        _tabs.TabPages.AddRange(_pages);

        Controls.Add(_tabs);
        Controls.Add(Tiles(_tiles, 4, 172));
        Controls.Add(Pilotage.Gap(14));
        Controls.Add(_bar);

        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) =>
            await ExportAsync(Pilotage.GridOf(_tabs.SelectedTab), _output, _dialogs,
                              _bar.Export.Text, Localizer);
    }

    public override string Title => T("Nav.Steering");

    private DataGridView GridOf(SteeringTab tab) => tab switch
    {
        SteeringTab.BySite => _sites,
        SteeringTab.ByPayer => _payers,
        SteeringTab.ByPrescriber => _prescribers,
        SteeringTab.Receivables => _ageing,
        SteeringTab.Quality => _quality,
        _ => _days
    };

    // The share of each column of the tables.
    private static int Weight(string column) => column switch
    {
        "label" or "band" => 30,
        "orders" or "tests" => 10,
        "revenue" => 14,
        "share" => 9,
        "invoices" => 12,
        "owed" => 16,
        "indicator" => 40,
        "value" => 20,
        _ => 10
    };

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Steering.Title");
        foreach (var tab in Enum.GetValues<SteeringTab>())
        {
            _pages[(int)tab].Text = T(SteeringScreenRules.TabKey(tab));
            foreach (var column in SteeringScreenRules.Columns(tab))
                GridOf(tab).Column(column.Name).HeaderText = T(column.CaptionKey);
        }

        Render();
    }

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            UseWaitCursor = true;
            var result = await _steering.GetAsync(_bar.From, _bar.To, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result, _bar.Compute.Text); return; }
            _result = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing the steering dashboard failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _bar.Compute.Text);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Render()
    {
        foreach (var grid in new[] { _days, _sites, _payers, _prescribers, _ageing, _quality }) grid.Rows.Clear();
        if (_result is not { } d)
        {
            foreach (var tile in _tiles) tile.Text = string.Empty;
            _bar.Subtitle.Text = string.Empty;
            return;
        }

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string Money(decimal value) => Domain.Common.Money.Of(value).Format(Localizer.CurrentLanguage);

        // Without the salary right the payroll tile is not shown at all, not shown empty: the rules
        // leave it out, and the last tile of the row stays free.
        var tiles = SteeringScreenRules.Tiles(d, T, Money, culture);
        for (var i = 0; i < _tiles.Length; i++)
        {
            _tiles[i].Visible = i < tiles.Count;
            if (i < tiles.Count) ReportGrids.Show(_tiles[i], tiles[i]);
        }

        foreach (var tab in Enum.GetValues<SteeringTab>())
            ReportGrids.Fill(GridOf(tab), SteeringScreenRules.Rows(d, tab, T, Amount, culture));

        _bar.Subtitle.Text = SteeringScreenRules.Subtitle(d, T);
    }
}
