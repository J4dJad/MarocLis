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

        foreach (var grid in new[] { _days, _sites, _payers, _prescribers })
        {
            AddColumn(grid, "label", 30);
            AddColumn(grid, "orders", 10, numeric: true);
            AddColumn(grid, "tests", 10, numeric: true);
            AddColumn(grid, "revenue", 14, numeric: true);
            AddColumn(grid, "share", 9, numeric: true);
        }

        AddColumn(_ageing, "band", 30);
        AddColumn(_ageing, "invoices", 12, numeric: true);
        AddColumn(_ageing, "amount", 16, numeric: true);

        AddColumn(_quality, "indicator", 40);
        AddColumn(_quality, "value", 20, numeric: true);

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

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Steering.Title");
        _pages[0].Text = T("Steering.ByDay");
        _pages[1].Text = T("Steering.BySite");
        _pages[2].Text = T("Steering.ByPayer");
        _pages[3].Text = T("Steering.ByPrescriber");
        _pages[4].Text = T("Steering.Receivables");
        _pages[5].Text = T("Steering.Quality");

        foreach (var grid in new[] { _days, _sites, _payers, _prescribers, _ageing, _quality })
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
            _dialogs.Warn(T("Message.UnexpectedError"), _bar.Compute.Text);
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

        string Tile(string key, string value) => $"{T(key)}\n{value}";
        string Mad(decimal value) => $"{Amount(value)} MAD";

        _tiles[0].Text = Tile("Steering.Revenue", $"{Mad(d.Revenue)} · {Count(d.Orders)} {T("Steering.OrdersUnit")}");
        _tiles[1].Text = Tile("Steering.InvoicedCollected", $"{Mad(d.Invoiced - d.Credited)} / {Mad(d.Collected)}");
        _tiles[2].Text = Tile("Steering.Column.reagentCost", Mad(d.Margin.ReagentCost));
        _tiles[3].Text = Tile("Steering.Column.margin", $"{Mad(d.Margin.Margin)} · {Share(d.Margin.MarginPercent)}");
        _tiles[4].Text = Tile("Steering.OverdueReceivables", $"{Mad(d.Overdue)} / {Mad(d.Outstanding)}");
        _tiles[4].Emphasis = d.Overdue > 0m ? UiTheme.Overdue : null;

        // Without the salary right the payroll tile is not shown at all, not shown empty.
        _tiles[5].Visible = d.Payroll is not null;
        if (d.Payroll is { } p)
            _tiles[5].Text = Tile("Steering.PayrollRatio",
                p.SiteMonths == 0 ? T("Steering.NoClosedPayroll")
                                  : $"{Mad(p.EmployerCost)} · {Share(p.EmployerCostToRevenuePercent)}");

        _tiles[6].Text = Tile("Steering.Turnaround",
            $"{(d.AverageTurnaroundHours is { } h ? $"{h:0.0} h" : "—")} · {Share(d.OnTimePercent)}");
        _tiles[7].Text = Tile("Steering.QcRejections",
            $"{Count(d.Quality.QcRejected)} / {Count(d.Quality.QcResults)} · {Share(d.Quality.QcRejectedPercent)}");

        void Fill(DataGridView grid, IEnumerable<RevenueRow> rows, string emptyKey)
        {
            foreach (var r in rows)
                grid.Rows.Add(r.Label.Length == 0 ? T(emptyKey) : r.Label, Count(r.Orders), Count(r.Tests),
                              Amount(r.Revenue), Share(r.SharePercent));
        }

        Fill(_days, d.ByDay, "Steering.NoLabel");
        Fill(_sites, d.BySite, "Steering.NoLabel");
        Fill(_payers, d.ByPayer, "Statistics.SelfPaying");
        Fill(_prescribers, d.ByPrescriber, "Relations.NoPrescriber");

        foreach (var a in d.Ageing)
        {
            var i = _ageing.Rows.Add(T($"AgeingBand.{a.Band}"), Count(a.Invoices), Amount(a.Amount));
            if (a.Band >= AgeingBand.Days61To90 && a.Amount > 0m) _ageing.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Overdue;
        }

        var q = d.Quality;
        _quality.Rows.Add(T("Steering.QcResults"), Count(q.QcResults));
        _quality.Rows.Add(T("Steering.QcRejections"), $"{Count(q.QcRejected)} ({Share(q.QcRejectedPercent)})");
        _quality.Rows.Add(T("Steering.OpenNonConformities"), Count(q.OpenNonConformities));
        _quality.Rows.Add(T("Steering.NonConformitiesOpened"), Count(q.NonConformitiesOpened));
        _quality.Rows.Add(T("Steering.EqaSatisfactory"), Count(q.EqaSatisfactory));
        _quality.Rows.Add(T("Steering.EqaQuestionable"), Count(q.EqaQuestionable));
        _quality.Rows.Add(T("Steering.EqaUnsatisfactory"), Count(q.EqaUnsatisfactory));
        _quality.Rows.Add(T("Steering.EqaNotSubmitted"), Count(q.EqaNotSubmitted));
        _quality.Rows.Add(T("Steering.Turnaround"),
                          d.AverageTurnaroundHours is { } tat ? $"{tat:0.0} h" : "—");
        _quality.Rows.Add(T("Statistics.OnTime"), Share(d.OnTimePercent));

        _bar.Subtitle.Text = $"{Date(d.From)} → {Date(d.To)} · {T("Steering.Sites", d.SiteCount)}";
    }
}
