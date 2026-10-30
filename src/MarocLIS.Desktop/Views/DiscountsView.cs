using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// « Remises »: every discount granted over a period — on a request at registration (convention,
/// prescriber, profile or rate typed) or on an invoice when it was issued — with who granted it,
/// the request and the patient, its rate and amount, its source, its reason and, beyond the
/// ceiling, who approved it; filtered by source and by a text, exported as CSV. The rules are
/// <see cref="DiscountScreenRules"/>', the same as the web's.
/// </summary>
public sealed class DiscountsView : LocalizedView
{
    private readonly DiscountService _discounts;
    private readonly ICurrentUser _currentUser;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<DiscountsView>? _logger;

    private readonly PeriodBar _bar;
    private readonly KpiTile[] _tiles = Enumerable.Range(0, 5).Select(_ => new KpiTile()).ToArray();
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 190, Margin = new Padding(0, 5, 0, 0) };
    private readonly TextBox _search = UiTheme.Input(220);

    private IReadOnlyList<DiscountReportRow>? _rows;
    private (DateOnly From, DateOnly To)? _computed;

    public DiscountsView(DiscountService discounts, ICurrentUser currentUser, IDocumentOutput output, IUserDialogs dialogs,
                         ILocalizer localizer, ICultureManager cultureManager, ILogger<DiscountsView>? logger = null)
        : base(localizer, cultureManager)
    {
        _discounts = discounts;
        _currentUser = currentUser;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;
        _bar = new PeriodBar(localizer);
        Padding = new Padding(24, 16, 24, 16);

        foreach (var column in DiscountScreenRules.Columns)
            AddColumn(_grid, column.Name, column.Name is "reason" or "patient" ? 16 : column.Name is "detail" ? 12 : 9, column.Numeric);
        _grid.Column("date").DefaultCellStyle.Font = UiTheme.Monospace;
        _grid.Column("reference").DefaultCellStyle.Font = UiTheme.Monospace;

        _search.Margin = new Padding(12, 5, 0, 0);
        _bar.Filters.Controls.AddRange([_source, _search]);

        var card = Pilotage.CardPage(_grid);
        var host = new TabControl { Dock = DockStyle.Fill };
        host.TabPages.Add(card);

        Controls.Add(host);
        Controls.Add(Tiles(_tiles, 5, 92));
        Controls.Add(Pilotage.Gap(14));
        Controls.Add(_bar);

        // Greyed, not hidden, for whoever neither approves discounts nor steers the laboratory.
        PermissionGate.Require([Permissions.Billing.ApproveDiscount, Permissions.Steering.View], _bar.Compute, _bar.Export);

        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) => await ExportAsync(_grid, _output, _dialogs, _bar.Export.Text, Localizer);
        _source.SelectedIndexChanged += (_, _) => Render();
        _search.TextChanged += (_, _) => Render();
    }

    public override string Title => T("Nav.Discounts");

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Discounts.Title");
        foreach (var column in DiscountScreenRules.Columns) _grid.Columns[column.Name]!.HeaderText = T(column.CaptionKey);
        _search.PlaceholderText = T("Discounts.SearchPlaceholder");

        var selected = _source.SelectedIndex;
        _source.Items.Clear();
        foreach (var source in DiscountScreenRules.SourceFilters) _source.Items.Add(new DialogChoice(source, T(DiscountScreenRules.FilterKey(source))));
        _source.SelectedIndex = Math.Max(0, selected);
        Render();
    }

    public override Task ActivateAsync(CancellationToken ct = default) =>
        DiscountScreenRules.Actions(_currentUser.Has, null).Compute ? LoadAsync(ct) : Task.CompletedTask;

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            UseWaitCursor = true;
            var (from, to) = (_bar.From, _bar.To);
            var result = await _discounts.GetReportAsync(from, to, ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result, _bar.Compute.Text);
                return;
            }
            _rows = result.Value;
            _computed = (from, to);
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reading the discounts granted failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _bar.Compute.Text);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Render()
    {
        _grid.Rows.Clear();
        if (_rows is null)
        {
            foreach (var tile in _tiles) tile.Text = string.Empty;
            _bar.Subtitle.Text = string.Empty;
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        string Money(decimal value) => Domain.Common.Money.Of(value).Format(Localizer.CurrentLanguage);
        var source = _source.SelectedItem is DialogChoice { Value: DiscountSource chosen } ? chosen : (DiscountSource?)null;
        var shown = DiscountScreenRules.Filter(_rows, source, _search.Text);

        var tiles = DiscountScreenRules.Tiles(shown, T, Money, culture);
        for (var i = 0; i < _tiles.Length && i < tiles.Count; i++) ReportGrids.Show(_tiles[i], tiles[i]);
        Fill(_grid, DiscountScreenRules.Rows(shown, T, Money, culture));
        _bar.Subtitle.Text = _computed is { } period ? Application.Steering.PeriodReport.Period(period.From, period.To) : string.Empty;
    }
}
