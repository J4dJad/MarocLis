using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Reagent and consumable stock, with lot-level detail.
/// <para>
/// The quantity shown counts only <b>usable</b> lots — released, in date, within open stability.
/// A shelf holding six quarantined boxes and nothing released is out of stock for every practical
/// purpose, and a screen that says otherwise lets a run be started that cannot be finished.
/// </para>
/// <para>
/// The lot panel is where the ISO 15189 work happens: a newly received lot arrives quarantined and
/// takes an explicit release with recorded evidence before anything can be drawn from it.
/// </para>
/// </summary>
public sealed class InventoryView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly InventoryService _inventory;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<InventoryView>? _logger;

    private readonly DataGridView _stock = UiTheme.Grid();
    private readonly DataGridView _lots = UiTheme.Grid();

    // Direction A: the title and its count, the search, the filter chips and the two cards.
    private readonly Label _title = StockPage.Title();
    private readonly Label _count = StockPage.Count();
    private readonly StockSearchBox _search = new();
    private readonly CheckBox _onlyAlerts = new StockChip();
    private readonly CheckBox _onlyQuarantined = new StockChip();
    private readonly StockCard _lotCard;
    private readonly ContextMenuStrip _lotMore = new();
    private readonly Button _lotMoreButton;

    private readonly Button _receive = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _issue = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _release = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _rejectLot = UiTheme.SecondaryButton(string.Empty);
    // Rarer lot operations, run from « Plus d'actions ▾ ».
    private readonly StockMenuButton _clearRejection = new();
    private readonly StockMenuButton _recall = new();
    private readonly StockMenuButton _writeOff = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly StockFooter _status = new();

    private IReadOnlyList<StockRow> _stockRows = [];
    private IReadOnlyList<LotRow> _lotRows = [];

    // Purchasing quality: reception checks, lot certificates, recall list, lot comparison.
    private readonly ReceptionService? _reception;
    private readonly LotQualityService? _lotQuality;
    private readonly IDocumentOutput? _output;
    private readonly ContextMenuStrip _lotMenu = new();

    // Stock alerts: expiry at both levels and the stock-out forecast.
    private readonly StockPlanningService? _planning;
    private readonly Button _alerts = UiTheme.SecondaryButton(string.Empty);

    public InventoryView(InventoryService inventory, IUserDialogs dialogs,
                         ILocalizer localizer, ICultureManager cultureManager,
                         ILogger<InventoryView>? logger = null,
                         ReceptionService? reception = null, LotQualityService? lotQuality = null,
                         IDocumentOutput? output = null, StockPlanningService? planning = null)
        : base(localizer, cultureManager)
    {
        _planning = planning;
        _inventory = inventory;
        _dialogs = dialogs;
        _logger = logger;
        _reception = reception;
        _lotQuality = lotQuality;
        _output = output;

        _lotCard = new StockCard(_lots, footer: _status);
        _lotMoreButton = StockMenu.More(_lotMore, localizer);

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Inventory");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = StockPage.ContentPadding;

        // --- title, actions and filters ----------------------------------------------------------------

        // One primary action, the receipt, with its key; the issue is a secondary action now.
        UiTheme.StyleSecondary(_issue);
        _alerts.Visible = _planning is not null;
        var header = StockPage.Header(_title, _count, _refresh, _alerts, _issue, _receive);
        ShortcutHints.Attach(_receive, NewKey);

        var filters = StockPage.Filters(_search, _onlyAlerts);

        // --- stock -------------------------------------------------------------------------------------

        BuildStockColumns();
        var stockCard = new StockCard(_stock, heading: false);

        // --- lots --------------------------------------------------------------------------------------

        BuildLotColumns();

        StockPage.Action(_rejectLot);
        StockPage.Action(_release);
        _lotCard.Heading.Start(_onlyQuarantined);
        _lotCard.Heading.End(_rejectLot, _lotMoreButton, _release);

        Controls.Add(StockPage.Stack(stockCard, _lotCard, 280));
        Controls.Add(filters);
        Controls.Add(header);
    }

    private const string NewKey = "Ctrl+N";

    /// <summary>Ctrl+N receives a lot of the selected article, as the key drawn on the button says.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N) && _receive.Enabled && _receive.Visible)
        {
            _receive.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void BuildStockColumns()
    {
        _stock.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _stock.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("code", 11, UiTheme.Monospace);
        Add("name", 24);
        Add("kind", 11, UiTheme.Small);
        Add("onhand", 10, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("reorder", 8, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("lots", 7, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("expiry", 10, UiTheme.Monospace);
        Add("supplier", 13, UiTheme.Small);
        Add("alert", 6, UiTheme.SmallBold, DataGridViewContentAlignment.MiddleCenter);
    }

    private void BuildLotColumns()
    {
        _lots.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _lots.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("item", 16, UiTheme.Monospace);
        Add("lot", 15, UiTheme.MonospaceBold);
        Add("expiry", 10, UiTheme.Monospace);
        Add("days", 7, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("remaining", 11, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("received", 9, UiTheme.Monospace);
        Add("opened", 9, UiTheme.Monospace);
        Add("state", 13, UiTheme.SmallBold);
        Add("location", 10, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyAlerts.CheckedChanged += async (_, _) => await LoadStockAsync();
        _onlyQuarantined.CheckedChanged += async (_, _) => await LoadLotsAsync();
        _search.Changed += (_, _) => StockGrid.Filter(_stock, _search.Query);

        _stock.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadLotsAsync());

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _receive.Click += async (_, _) => await RunBusyAsync(ReceiveAsync);
        _issue.Click += async (_, _) => await RunBusyAsync(IssueAsync);
        _release.Click += async (_, _) => await RunBusyAsync(ReleaseAsync);
        _rejectLot.Click += async (_, _) => await RunBusyAsync(RejectAsync);
        _clearRejection.Click += async (_, _) => await RunBusyAsync(ClearRejectionAsync);
        _recall.Click += async (_, _) => await RunBusyAsync(RecallAsync);
        _writeOff.Click += async (_, _) => await RunBusyAsync(WriteOffAsync);
        _alerts.Click += async (_, _) => await RunBusyAsync(ShowStockAlertsAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Inventory.Receive, _receive);
        PermissionGate.Require([Permissions.Inventory.Receive, Permissions.Quality.ReviewQc], _rejectLot);
        PermissionGate.Require(Permissions.Quality.ReviewQc, _release);
        PermissionGate.Require(Permissions.Inventory.Issue, _issue);
        PermissionGate.Require(Permissions.Inventory.Adjust, _clearRejection, _recall, _writeOff);

        _stock.CellFormatting += OnStockFormatting;
        _lots.CellFormatting += OnLotFormatting;
        StockGrid.Pills(_lots, "state", tag => tag is LotRow lot ? LotPill(lot) : null);

        WireLotMenu();
    }

    /// <summary>
    /// The lot's quality records, on a right click: the patient results it produced (the recall
    /// list), its QC against the previous lot, and the certificates kept with it.
    /// </summary>
    private void WireLotMenu()
    {
        var results = new ToolStripMenuItem { Tag = "Recall.Menu" };
        results.Click += async (_, _) => await RunBusyAsync(() => ShowRecallListAsync());
        var compare = new ToolStripMenuItem { Tag = "LotComparison.Menu" };
        compare.Click += async (_, _) => await RunBusyAsync(CompareLotsAsync);
        var certificates = new ToolStripMenuItem { Tag = "Certificates.Menu" };
        certificates.Click += (_, _) => ShowCertificates();

        _lotMenu.Items.AddRange([results, compare, certificates]);
        _lotMenu.Opening += (_, e) =>
        {
            foreach (ToolStripItem item in _lotMenu.Items) item.Text = T((string)item.Tag!);
            var row = _lots.CurrentRow?.Tag as LotRow;
            results.Enabled = row is not null && _lotQuality is not null
                              && PermissionGate.Allows(Permissions.Results.View);
            compare.Enabled = row is not null && _lotQuality is not null;
            certificates.Enabled = row is not null && _reception is not null && _output is not null;
            e.Cancel = row is null;
        };

        _lots.ContextMenuStrip = _lotMenu;

        // The lot card's « Plus d'actions ▾ »: the rarer lot operations, then the same quality
        // records as the right click.
        var moreResults = new ToolStripMenuItem { Tag = "Recall.Menu" };
        moreResults.Click += async (_, _) => await RunBusyAsync(() => ShowRecallListAsync());
        var moreCompare = new ToolStripMenuItem { Tag = "LotComparison.Menu" };
        moreCompare.Click += async (_, _) => await RunBusyAsync(CompareLotsAsync);
        var moreCertificates = new ToolStripMenuItem { Tag = "Certificates.Menu" };
        moreCertificates.Click += (_, _) => ShowCertificates();

        _lotMore.Items.AddRange([StockMenu.Entry(_clearRejection), StockMenu.Entry(_recall),
                                 StockMenu.Entry(_writeOff), new ToolStripSeparator(),
                                 moreResults, moreCompare, moreCertificates]);
        _lotMore.Opening += (_, _) =>
        {
            var row = _lots.CurrentRow?.Tag as LotRow;
            foreach (var item in new[] { moreResults, moreCompare, moreCertificates }) item.Text = T((string)item.Tag!);
            moreResults.Enabled = row is not null && _lotQuality is not null && PermissionGate.Allows(Permissions.Results.View);
            moreCompare.Enabled = row is not null && _lotQuality is not null;
            moreCertificates.Enabled = row is not null && _reception is not null && _output is not null;
        };
        _lots.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.RowIndex < _lots.Rows.Count)
                _lots.CurrentCell = _lots.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
        };
    }

    private async Task ShowRecallListAsync(LotRow? lot = null)
    {
        if ((lot ?? _lots.CurrentRow?.Tag as LotRow) is not { } row || _lotQuality is null || _output is null) return;

        var list = await _lotQuality.GetRecallListAsync(row.ReagentLotId);
        if (list.IsFailure) { _dialogs.ShowFailure(list, T("Recall.Menu")); return; }

        using var dialog = new RecallListDialog(Localizer, _dialogs, _output, _lotQuality, row.ReagentLotId,
                                                row.LotNumber, list.Value);
        dialog.ShowDialog(this);
    }

    private async Task ShowStockAlertsAsync()
    {
        if (_planning is null) return;

        var alerts = await _planning.GetAlertsAsync();
        if (alerts.IsFailure) { _dialogs.ShowFailure(alerts, _alerts.Text); return; }

        using var dialog = new StockAlertsDialog(Localizer, alerts.Value);
        dialog.ShowDialog(this);
    }

    private async Task CompareLotsAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow row || _lotQuality is null) return;

        var comparison = await _lotQuality.CompareWithPreviousLotAsync(row.ReagentLotId);
        if (comparison.IsFailure) { _dialogs.ShowFailure(comparison, T("LotComparison.Menu")); return; }

        using var dialog = new LotComparisonDialog(Localizer, comparison.Value);
        dialog.ShowDialog(this);
    }

    private void ShowCertificates()
    {
        if (_lots.CurrentRow?.Tag is not LotRow row || _reception is null || _output is null) return;

        using var dialog = new LotCertificatesDialog(Localizer, _dialogs, _output, _reception,
                                                     row.ReagentLotId, row.LotNumber);
        dialog.ShowDialog(this);
    }

    // The cells that carry a warning, coloured on their own: a whole row in colour is kept for the
    // critical cases, as the design system has it.
    private static readonly HashSet<string> StockWarningCells = ["onhand", "lots", "expiry", "alert"];

    private void OnStockFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _stock.Rows.Count) return;
        if (_stock.Rows[e.RowIndex].Tag is not StockRow row) return;

        // A critical item below its safety stock is the one that stops an analysis being offered
        // at all — which has to be declared to prescribers, so it leads the colouring.
        if (row.IsCritical && row.BelowMinimum)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
        }
        else if (row.BelowMinimum || row.DaysToEarliestExpiry is < 0)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if ((row.NeedsReorder || row.DaysToEarliestExpiry is <= 30 || row.QuarantinedLotCount > 0)
                 && StockWarningCells.Contains(_stock.Columns[e.ColumnIndex].Name))
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnLotFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _lots.Rows.Count) return;
        if (_lots.Rows[e.RowIndex].Tag is not LotRow row) return;

        if (row.IsRejected || row.IsRecalled || row.IsExpired)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if ((row.IsQuarantined || row.IsBeyondOpenStability || row.DaysToExpiry <= 30)
                 && _lots.Columns[e.ColumnIndex].Name is "expiry" or "days")
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
            e.CellStyle.Font = UiTheme.MonospaceBold;
        }
        else if (row.IsUsable)
        {
            e.CellStyle!.ForeColor = UiTheme.TextPrimary;
        }
    }

    /// <summary>The lot's state as a pill: usable, waiting for a decision, or out of use.</summary>
    private static StockPill LotPill(LotRow row) =>
        row.IsRejected || row.IsRecalled || row.IsExpired ? StockPill.Critical
        : row.IsBeyondOpenStability || row.IsQuarantined ? StockPill.Warning
        : StockPill.Done;

    protected override void ApplyTexts()
    {
        _title.Text = T("Inventory.Title");
        _lotCard.Title.Text = T("Inventory.Lots");
        _lotMoreButton.Text = T("ResultEntry.MoreActions");
        _search.Placeholder = T("StocksUi.SearchStock");
        _onlyAlerts.Text = T("Inventory.OnlyAlerts");
        _onlyQuarantined.Text = T("Inventory.Quarantined");
        _receive.Text = T("Inventory.ReceiveLot");
        _issue.Text = T("Inventory.Issue");
        _release.Text = T("Action.Release");
        _rejectLot.Text = T("Action.Reject");
        _clearRejection.Text = T("Inventory.ClearRejection");
        _recall.Text = T("Inventory.Recall");
        _writeOff.Text = T("Inventory.WriteOff");
        _refresh.Text = T("Action.Refresh");
        _alerts.Text = T("Stock.Alerts");

        if (_stock.Columns.Count > 0)
        {
            _stock.Column("code").HeaderText = T("Field.Code");
            _stock.Column("name").HeaderText = T("Inventory.Item");
            _stock.Column("kind").HeaderText = T("Inventory.ItemKind");
            _stock.Column("onhand").HeaderText = T("Inventory.QuantityOnHand");
            _stock.Column("reorder").HeaderText = T("Inventory.ReorderPoint");
            _stock.Column("lots").HeaderText = T("Inventory.Lot");
            _stock.Column("expiry").HeaderText = T("Inventory.Expiry");
            _stock.Column("supplier").HeaderText = T("Inventory.Supplier");
            _stock.Column("alert").HeaderText = string.Empty;
        }

        if (_lots.Columns.Count > 0)
        {
            _lots.Column("item").HeaderText = T("Inventory.Item");
            _lots.Column("lot").HeaderText = T("Inventory.LotNumber");
            _lots.Column("expiry").HeaderText = T("Inventory.Expiry");
            _lots.Column("days").HeaderText = T("Inventory.DaysToExpiry");
            _lots.Column("remaining").HeaderText = T("Field.Quantity");
            _lots.Column("received").HeaderText = T("Inventory.Receipt");
            _lots.Column("opened").HeaderText = T("Inventory.OpenedOn");
            _lots.Column("state").HeaderText = T("Field.Status");
            _lots.Column("location").HeaderText = T("Sample.Storage");
        }

        if (_stockRows.Count > 0) RenderStock();
        if (_lotRows.Count > 0) RenderLots();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        // Lots that expired since the last pass lower the usable stock without any operation, so
        // the reorder check also runs whenever the screen is shown.
        ShowReorder(await _inventory.RaiseReorderDraftsAsync(ct), append: false);

        await LoadStockAsync(ct);
        await LoadLotsAsync(ct);
    }

    // Says which priority purchase order the items that reached their reorder point went on.
    private void ShowReorder(ReorderOutcome outcome, bool append)
    {
        if (!outcome.Any) return;

        var message = T("Inventory.ReorderDrafted", string.Join(", ", outcome.PurchaseOrders),
                        string.Join(", ", outcome.DraftedItems));

        _status.ForeColor = UiTheme.Critical;
        _status.Text = append && !string.IsNullOrEmpty(_status.Text) ? $"{_status.Text}  ·  {message}" : message;
    }

    // After an operation that lowered the stock: the drafting it triggered, shown once the screen
    // has reloaded.
    private async Task ReloadAfterStockOutAsync()
    {
        var drafted = _inventory.LastReorder;
        await ActivateAsync();
        ShowReorder(drafted, append: true);
    }

    private async Task LoadStockAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _inventory.GetStockAsync(_onlyAlerts.Checked, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _stockRows = result.Value;
            RenderStock();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading stock failed.");
        }
    }

    private void RenderStock()
    {
        // The article worked on stays current after a reception, a release or a write-off: the
        // list went back to its first article, and the lots shown were another article's.
        var keep = (_stock.CurrentRow?.Tag as StockRow)?.StockItemId;

        _stock.SuspendLayout();
        _stock.Rows.Clear();

        foreach (var row in _stockRows)
        {
            // Glyphs rather than colour alone: the alert column says what is wrong in one or two
            // characters that survive a monochrome print.
            var alerts = new List<string>();
            if (row.BelowMinimum) alerts.Add("!!");
            else if (row.NeedsReorder) alerts.Add("!");
            if (row.QuarantinedLotCount > 0) alerts.Add("Q");
            if (row.DaysToEarliestExpiry is <= 30) alerts.Add("⏳");
            if (row.IsCritical) alerts.Add("★");

            var index = _stock.Rows.Add(
                row.Code,
                row.Name.For(Localizer.CurrentLanguage),
                T($"StockItemKind.{row.Kind}"),
                $"{row.QuantityOnHand:N2} {row.UnitOfMeasure}",
                row.ReorderPoint.ToString("N2"),
                row.QuarantinedLotCount > 0
                    ? $"{row.LotCount} ({row.QuarantinedLotCount}Q)"
                    : row.LotCount.ToString(),
                row.EarliestExpiry is { } expiry
                    ? $"{expiry:dd/MM/yy}" +
                      (row.DaysToEarliestExpiry is { } days ? $" ({days}j)" : string.Empty)
                    : string.Empty,
                row.SupplierName ?? string.Empty,
                string.Join(" ", alerts));

            _stock.Rows[index].Tag = row;
        }

        _stock.ResumeLayout(performLayout: true);

        var alertCount = _stockRows.Count(r => r.BelowMinimum || r.NeedsReorder);

        _count.Text = $"{T("StocksUi.Items", _stockRows.Count)}" +
                      (alertCount > 0 ? $" · {alertCount} {T("Inventory.BelowMinimum").ToLowerInvariant()}" : string.Empty);

        var kept = _stock.Rows.Cast<DataGridViewRow>()
                         .FirstOrDefault(r => keep is not null && r.Tag is StockRow row && row.StockItemId == keep);
        if (kept is { Index: > 0 }) _stock.MakeCurrent(kept);
        else if (_stock.Rows.Count > 0) _stock.SelectFirstRow();
        StockGrid.Filter(_stock, _search.Query);
    }

    private async Task LoadLotsAsync(CancellationToken ct = default)
    {
        try
        {
            var stockItemId = (_stock.CurrentRow?.Tag as StockRow)?.StockItemId;

            var result = await _inventory.GetLotsAsync(
                _onlyQuarantined.Checked ? null : stockItemId, _onlyQuarantined.Checked, ct);

            if (result.IsFailure) return;

            _lotRows = result.Value;
            RenderLots();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading lots failed.");
        }
    }

    private void RenderLots()
    {
        var keep = (_lots.CurrentRow?.Tag as LotRow)?.ReagentLotId;

        _lots.SuspendLayout();
        _lots.Rows.Clear();

        foreach (var row in _lotRows)
        {
            var state = row.IsRejected ? T("Lot_Rejected")
                : row.IsRecalled ? T("Inventory.Recalled")
                : row.IsExpired ? T("Inventory.Expired")
                : row.IsBeyondOpenStability ? T("Inventory.BeyondOpenStability")
                : row.IsQuarantined ? T("Inventory.Quarantined")
                : T("Inventory.Usable");

            var index = _lots.Rows.Add(
                row.ItemCode,
                row.LotNumber,
                row.ExpiryDate.ToString("dd/MM/yy"),
                row.DaysToExpiry.ToString(),
                row.QuantityRemaining.ToString("N2"),
                row.ReceivedOn.ToString("dd/MM/yy"),
                row.OpenedOn?.ToString("dd/MM/yy") ?? string.Empty,
                state,
                row.StorageLocation ?? string.Empty);

            _lots.Rows[index].Tag = row;
        }

        _lots.ResumeLayout(performLayout: true);

        // The lot just released, refused or recalled stays the current one, its new state in view.
        var keptLot = _lots.Rows.Cast<DataGridViewRow>()
                          .FirstOrDefault(r => keep is not null && r.Tag is LotRow lot && lot.ReagentLotId == keep);
        if (keptLot is not null) _lots.MakeCurrent(keptLot);

        var quarantined = _lotRows.Count(r => r.IsQuarantined && !r.IsRejected);

        var item = _onlyQuarantined.Checked ? null : (_stock.CurrentRow?.Tag as StockRow)?.Code;
        _lotCard.Note.Text = (item is null ? string.Empty : $"{item} · ") + $"{_lotRows.Count}" +
                             (quarantined > 0 ? $" · {quarantined} {T("Inventory.Quarantined").ToLowerInvariant()}" : string.Empty);

        RefreshLotActions();
        _lots.SelectionChanged -= OnLotSelectionChanged;
        _lots.SelectionChanged += OnLotSelectionChanged;
    }

    private void OnLotSelectionChanged(object? sender, EventArgs e) => RefreshLotActions();

    /// <summary>Only offers the operations that make sense for the selected lot's state.</summary>
    private void RefreshLotActions()
    {
        var row = _lots.CurrentRow?.Tag as LotRow;

        _release.Enabled = row is { IsQuarantined: true, IsRejected: false, IsRecalled: false };
        // A lot still in quarantine may be refused at reception; a released one only by whoever
        // may release lots.
        _rejectLot.Enabled = row is { IsRejected: false }
                             && (row.IsQuarantined || PermissionGate.Allows(Permissions.Quality.ReviewQc));

        // Only offered for a lot that is actually rejected, and never for a recalled one: a
        // manufacturer recall is not a clerical error that can be withdrawn.
        _clearRejection.Enabled = row is { IsRejected: true, IsRecalled: false };
        _recall.Enabled = row is { IsRecalled: false };
        _writeOff.Enabled = row is { QuantityRemaining: > 0m };

        _release.ForeColor = _release.Enabled ? UiTheme.TextOnAccent : UiTheme.TextMuted;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task ReceiveAsync()
    {
        if (_stock.CurrentRow?.Tag is not StockRow item)
        {
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var suppliers = await _inventory.GetSuppliersAsync();

        using var dialog = new ReceiveLotDialog(Localizer, _dialogs, item,
            suppliers.IsSuccess ? suppliers.Value : [], _stockRows,
            _reception is null ? null : gtin => _reception.FindByGtinAsync(gtin));

        var answer = dialog.ShowDialog(this);

        // Refused at the door: nothing goes into stock, the refusal and its non-conformity are kept.
        if (answer == DialogResult.Abort && dialog.Refusal is { } refusal && _reception is not null)
        {
            var refused = await _reception.RefuseDeliveryAsync(refusal);
            if (refused.IsFailure) { _dialogs.ShowFailure(refused, T("Reception.Refuse")); return; }

            _dialogs.Info(T("Reception.Refused", refused.Value), T("Reception.Refuse"));
            return;
        }

        if (answer != DialogResult.OK || dialog.Request is null) return;

        var result = await _inventory.ReceiveLotAsync(dialog.Request);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        // Stated explicitly, because a storeman who does not know the lot is quarantined will
        // wonder why the bench cannot draw from it.
        _dialogs.Info(T("Inventory.ReceivedQuarantined"), T("Inventory.ReceiveLot"));

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }

    private async Task IssueAsync()
    {
        if (_stock.CurrentRow?.Tag is not StockRow item)
        {
            _status.Text = T("Message.NothingSelected");
            return;
        }

        var answer = _dialogs.PromptForText(
            T("Inventory.IssueQuantityPrompt", item.Code, item.UnitOfMeasure,
              item.QuantityOnHand.ToString("N2")),
            T("Inventory.Issue"));

        if (answer is null) return;

        if (EditorValues.ParseDecimal(answer) is not { } quantity || quantity <= 0m)
        {
            _dialogs.Warn(Localizer.Format("Result_NotANumber", answer));
            return;
        }

        var result = await _inventory.IssueAsync(item.StockItemId, quantity);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        // Names the lots drawn from: FEFO means the operator did not choose them, and lot
        // traceability means they need to know which went to the bench.
        _status.ForeColor = UiTheme.Normal;
        _status.Text = $"{T("Inventory.Issue")} — {T("Inventory.Lot").ToLowerInvariant()} {result.Value}";

        await ReloadAfterStockOutAsync();
    }

    private async Task ReleaseAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow lot) return;

        var evidence = _dialogs.PromptForReason(
            T("Inventory.ReleaseEvidencePrompt", lot.LotNumber),
            T("Action.Release"), minimumLength: 10, maxLength: 1000);

        if (evidence is null) return;

        var result = await _inventory.ReleaseLotAsync(lot.ReagentLotId, evidence);

        // Refused for want of conforming QC on the lot: a biologist may release it by derogation,
        // with a justification that stays on the lot and in the audit trail.
        var refusedForQc = result.IsFailure
                           && result.FirstError.ResourceKey is "Lot_ReleaseNoQcRun" or "Lot_ReleaseQcRejected"
                                                               or "Lot_ReleaseQcLevelsMissing";

        if (refusedForQc && PermissionGate.Allows(Permissions.Results.BiologicalValidate))
        {
            var justification = _dialogs.PromptForReason(
                $"{Describe(result)}\n\n" +
                T("Lot.QcOverrideJustificationPrompt", lot.LotNumber, ReagentLot.QcOverrideJustificationMinLength),
                T("Lot.QcOverrideTitle"), minimumLength: ReagentLot.QcOverrideJustificationMinLength, maxLength: 1000);

            if (justification is null) return;

            result = await _inventory.ReleaseLotOverridingQcAsync(lot.ReagentLotId, evidence, justification);
        }

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = $"{lot.LotNumber} — {T("Inventory.Usable")}";

        await ActivateAsync();
    }

    private async Task RejectAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow lot) return;

        var reason = _dialogs.PromptForReason(
            T("Inventory.RejectLotPrompt", lot.LotNumber), T("Action.Reject"), maxLength: 500);

        if (reason is null) return;

        var result = await _inventory.RejectLotAsync(lot.ReagentLotId, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await ActivateAsync();
    }

    /// <summary>
    /// Records a manufacturer recall and reports how many patient results were produced with the
    /// lot — the question a recall actually raises, and the reason results carry a lot reference.
    /// </summary>
    /// <summary>
    /// Withdraws a rejection recorded against the wrong lot. The lot returns to quarantine, not to
    /// service — the acceptance check it never had is still owed.
    /// </summary>
    private async Task ClearRejectionAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow lot) return;

        var reason = _dialogs.PromptForReason(
            T("Inventory.ClearRejectionPrompt", lot.LotNumber),
            T("Inventory.ClearRejection"), minimumLength: 10);

        if (reason is null) return;

        var result = await _inventory.ClearLotRejectionAsync(lot.ReagentLotId, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Inventory.ClearRejection"));
            return;
        }

        // Stated, because the lot is not usable yet and a storeman who thinks it is will wonder
        // why the bench still cannot draw from it.
        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = $"{lot.LotNumber} — {T("Inventory.Quarantined")}";

        await ActivateAsync();
    }

    private async Task RecallAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow lot) return;

        var reference = _dialogs.PromptForText(
            T("Inventory.RecallReferencePrompt", lot.LotNumber), T("Inventory.Recall"), maxLength: 100);

        if (string.IsNullOrWhiteSpace(reference)) return;

        var result = await _inventory.RecallLotAsync(lot.ReagentLotId, reference.Trim());

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        if (result.Value > 0)
        {
            _dialogs.Warn(
                Localizer.Format("Inventory.RecallAffectedResults", result.Value, lot.LotNumber),
                T("Inventory.Recall"));

            // The count says how many; the list says who, which is what the recall needs.
            if (_lotQuality is not null && PermissionGate.Allows(Permissions.Results.View))
                await ShowRecallListAsync(lot);
        }
        else
        {
            _dialogs.Info(T("Inventory.RecallNoResults"), T("Inventory.Recall"));
        }

        await ReloadAfterStockOutAsync();
    }

    private async Task WriteOffAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow lot) return;

        var kind = lot.IsExpired
            ? StockMovementKind.ExpiryWriteOff
            : StockMovementKind.DamageWriteOff;

        var reason = _dialogs.PromptForReason(
            T("Inventory.WriteOffPrompt", lot.LotNumber, lot.QuantityRemaining.ToString("N2")),
            T("Inventory.WriteOff"), maxLength: 500);

        if (reason is null) return;

        var result = await _inventory.AdjustAsync(lot.ReagentLotId, lot.QuantityRemaining,
                                                   kind, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.Text = string.Empty;
        await ReloadAfterStockOutAsync();
    }
}

/// <summary>Recording a delivery: the lot number and expiry are what make it traceable.</summary>
internal sealed class ReceiveLotDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly StockRow _item;

    private readonly TextBox _lotNumber = UiTheme.Input(200);
    private readonly DateTimePicker _expiry = new();
    private readonly TextBox _quantity = UiTheme.Input(140);
    private readonly ComboBox _supplier = new();
    private readonly TextBox _deliveryNote = UiTheme.Input(200);
    private readonly TextBox _unitPrice = UiTheme.Input(140);
    private readonly TextBox _storage = UiTheme.Input(200);
    private readonly NumericUpDown _openStability = new();

    // Reception checks and GS1 scan.
    private StockRow _current;
    private readonly IReadOnlyList<StockRow> _items;
    private readonly Func<string, Task<GtinMatch?>>? _findByGtin;
    private readonly ReceptionChecksPanel _checks;
    private readonly Label _unitLabel = new();

    internal ReceiveLotRequest? Request { get; private set; }

    /// <summary>Set when the delivery was refused as a whole; the dialog then closes with Abort.</summary>
    internal RefuseDeliveryRequest? Refusal { get; private set; }

    internal ReceiveLotDialog(ILocalizer localizer, IUserDialogs dialogs, StockRow item,
                              IReadOnlyList<Supplier> suppliers, IReadOnlyList<StockRow>? items = null,
                              Func<string, Task<GtinMatch?>>? findByGtin = null)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _item = item;
        _current = item;
        _items = items ?? [];
        _findByGtin = findByGtin;
        _checks = new ReceptionChecksPanel(localizer, item.StorageCondition);

        // Bounded to the columns: beyond, the save failed with « Erreur inattendue ».
        _lotNumber.MaxLength = _deliveryNote.MaxLength = 60;
        _storage.MaxLength = 100;
        _quantity.MaxLength = EditorValues.MeasureMaxLength;
        _unitPrice.MaxLength = EditorValues.AmountMaxLength;

        Text = $"{localizer["Inventory.ReceiveLot"]} — {item.Code}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 620);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var y = 16;

        void Field(string key, int top, int left = 20)
        {
            var label = UiTheme.FieldLabel(localizer[key]);
            label.Location = new Point(left, top);
            Controls.Add(label);
        }

        Field("Inventory.LotNumber", y);
        _lotNumber.Location = new Point(20, y + 18);
        _lotNumber.Tag = UiTags.LatinValue;
        _lotNumber.Font = UiTheme.MonospaceBold;

        Field("Inventory.Expiry", y, 240);
        _expiry.Bounds = new Rectangle(240, y + 18, 200, UiTheme.InputHeight);
        _expiry.Format = DateTimePickerFormat.Short;
        _expiry.MinDate = DateTime.Today;
        _expiry.Value = DateTime.Today.AddMonths(12);

        y += 54;
        Field("Field.Quantity", y);
        _quantity.Location = new Point(20, y + 18);
        _quantity.Tag = UiTags.LatinValue;
        _quantity.TextAlign = HorizontalAlignment.Right;
        _quantity.Font = UiTheme.MonospaceBold;

        _unitLabel.Text = item.UnitOfMeasure;
        _unitLabel.Font = UiTheme.Small;
        _unitLabel.ForeColor = UiTheme.TextSecondary;
        _unitLabel.AutoSize = true;
        _unitLabel.Location = new Point(168, y + 24);
        Controls.Add(_unitLabel);

        Field("Inventory.Supplier", y, 240);
        _supplier.DropDownStyle = ComboBoxStyle.DropDownList;
        _supplier.FlatStyle = FlatStyle.Flat;
        _supplier.Bounds = new Rectangle(240, y + 18, 200, UiTheme.InputHeight);

        _supplier.Items.Add(new SupplierChoice(null, "—"));
        foreach (var supplier in suppliers.OrderBy(s => s.Name))
            _supplier.Items.Add(new SupplierChoice(supplier.Id, supplier.Name));

        _supplier.SelectedIndex = 0;

        y += 54;
        Field("Inventory.DeliveryNote", y);
        _deliveryNote.Location = new Point(20, y + 18);
        _deliveryNote.Tag = UiTags.LatinValue;

        Field("Field.Price", y, 240);
        _unitPrice.Location = new Point(240, y + 18);
        _unitPrice.Tag = UiTags.LatinValue;
        _unitPrice.TextAlign = HorizontalAlignment.Right;

        y += 54;
        Field("Sample.Storage", y);
        _storage.Location = new Point(20, y + 18);

        Field("Inventory.OpenStabilityDays", y, 240);
        _openStability.Bounds = new Rectangle(240, y + 18, 100, UiTheme.InputHeight);
        _openStability.Maximum = 3650;

        y += 66;

        _checks.Location = new Point(20, y);
        _checks.Scanned += async data => await OnScannedAsync(data);
        Controls.Add(_checks);

        y += ReceptionChecksPanel.PanelHeight + 6;

        // Stated up front: the storeman needs to know the lot will not be usable yet.
        var notice = new Label
        {
            Text = localizer["Inventory.ReceivedQuarantined"],
            Font = UiTheme.SmallBold,
            ForeColor = UiTheme.Abnormal,
            AutoSize = false,
            Bounds = new Rectangle(20, y, 470, 36)
        };
        Controls.Add(notice);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 570, 150, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var refuse = UiTheme.SecondaryButton(localizer["Reception.Refuse"]);
        refuse.Bounds = new Rectangle(180, 570, 150, UiTheme.ButtonHeight);
        refuse.Click += (_, _) => Refuse();
        refuse.Enabled = findByGtin is not null;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(340, 570, 150, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_lotNumber, _expiry, _quantity, _supplier, _deliveryNote,
                           _unitPrice, _storage, _openStability, ok, refuse, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.ReceiveLot", refuse);
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_lotNumber.Text))
        {
            _dialogs.Warn(_localizer["Lot_NumberRequired"]);
            _lotNumber.Focus();
            return;
        }

        if (!TryParse(_quantity.Text, out var quantity) || quantity <= 0m)
        {
            _dialogs.Warn(_localizer["Lot_QuantityMustBePositive"]);
            _quantity.Focus();
            return;
        }

        // A price typed with its currency was saved as no price, and the lot valued at nothing.
        if (EditorValues.IsUnreadable(_unitPrice.Text))
        {
            _dialogs.Warn(_localizer.Format("Result_NotANumber", _unitPrice.Text.Trim()));
            _unitPrice.Focus();
            return;
        }

        var check = _checks.Build(_dialogs, Text);
        if (check is null) return;

        Request = new ReceiveLotRequest
        {
            Reception = check,
            StockItemId = _current.StockItemId,
            LotNumber = _lotNumber.Text.Trim(),
            ExpiryDate = DateOnly.FromDateTime(_expiry.Value.Date),
            Quantity = quantity,
            SupplierId = (_supplier.SelectedItem as SupplierChoice)?.Id,
            DeliveryNoteReference = string.IsNullOrWhiteSpace(_deliveryNote.Text)
                ? null : _deliveryNote.Text.Trim(),
            UnitPriceMad = TryParse(_unitPrice.Text, out var price) ? price : null,
            StorageLocation = string.IsNullOrWhiteSpace(_storage.Text) ? null : _storage.Text.Trim(),
            StabilityAfterOpeningDays = _openStability.Value > 0 ? (int)_openStability.Value : null
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// A DataMatrix read: the lot and expiry go into their fields, and a GTIN that designates
    /// another article offers to receive that one instead — the box in hand decides.
    /// </summary>
    private async Task OnScannedAsync(Gs1Data data)
    {
        if (data.LotNumber is { } lot) _lotNumber.Text = lot;

        if (data.ExpiryDate is { } expiry)
        {
            var date = expiry.ToDateTime(TimeOnly.MinValue);
            if (date < _expiry.MinDate)
                _dialogs.Warn(_localizer.Format("Lot_AlreadyExpired", expiry), Text);
            else
                _expiry.Value = date;
        }

        if (data.Gtin is not { } gtin || _findByGtin is null) return;

        var match = await _findByGtin(gtin);

        if (match is null)
        {
            _dialogs.Info(_localizer.Format("Reception.GtinUnknown", gtin), Text);
            return;
        }

        if (match.StockItemId == _current.StockItemId) return;

        var other = _items.FirstOrDefault(i => i.StockItemId == match.StockItemId);

        if (other is not null
            && _dialogs.Confirm(_localizer.Format("Reception.GtinOtherItem", match.Code, _current.Code), Text))
        {
            _current = other;
            Text = $"{_localizer["Inventory.ReceiveLot"]} — {other.Code}";
            _unitLabel.Text = other.UnitOfMeasure;
            _checks.SetCondition(other.StorageCondition);
        }
        else if (other is null)
        {
            _dialogs.Warn(_localizer.Format("Reception.GtinOtherItem", match.Code, _current.Code), Text);
        }
    }

    private void Refuse()
    {
        var reason = _dialogs.PromptForReason(_localizer["Reception.RefusePrompt"],
                                              _localizer["Reception.Refuse"], minimumLength: 10, maxLength: 1000);
        if (reason is null) return;

        Refusal = new RefuseDeliveryRequest
        {
            SupplierId = (_supplier.SelectedItem as SupplierChoice)?.Id,
            StockItemId = _current.StockItemId,
            Reason = reason,
            ArrivalTemperatureCelsius = _checks.Temperature,
            DeliveryNoteReference = string.IsNullOrWhiteSpace(_deliveryNote.Text) ? null : _deliveryNote.Text.Trim(),
            LotNumber = string.IsNullOrWhiteSpace(_lotNumber.Text) ? null : _lotNumber.Text.Trim()
        };

        DialogResult = DialogResult.Abort;
        Close();
    }

    // Read as the editors read numbers: « 1e20 » was taken, and the save then failed with
    // « Erreur inattendue »; « 1 000 », as the French culture writes it, was refused.
    private static bool TryParse(string text, out decimal value)
    {
        var parsed = EditorValues.ParseDecimal(text);
        value = parsed ?? 0m;
        return parsed is not null;
    }

    private sealed record SupplierChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }
}
