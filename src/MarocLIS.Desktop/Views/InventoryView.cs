using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
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
    private readonly InventoryService _inventory;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<InventoryView>? _logger;

    private readonly DataGridView _stock = UiTheme.Grid();
    private readonly DataGridView _lots = UiTheme.Grid();

    private readonly Label _stockHeading = new();
    private readonly Label _lotsHeading = new();
    private readonly CheckBox _onlyAlerts = new();
    private readonly CheckBox _onlyQuarantined = new();

    private readonly Button _receive = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _issue = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _release = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _rejectLot = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _clearRejection = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _recall = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _writeOff = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<StockRow> _stockRows = [];
    private IReadOnlyList<LotRow> _lotRows = [];

    public InventoryView(InventoryService inventory, IUserDialogs dialogs,
                         ILocalizer localizer, ICultureManager cultureManager,
                         ILogger<InventoryView>? logger = null)
        : base(localizer, cultureManager)
    {
        _inventory = inventory;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Inventory");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 290,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        // --- stock ------------------------------------------------------------------------------------

        var top = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        var stockBar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = UiTheme.Surface };

        _stockHeading.Font = UiTheme.SectionTitle;
        _stockHeading.ForeColor = UiTheme.TextPrimary;
        _stockHeading.AutoSize = true;
        _stockHeading.Location = new Point(0, 2);

        _onlyAlerts.Font = UiTheme.Small;
        _onlyAlerts.ForeColor = UiTheme.TextSecondary;
        _onlyAlerts.AutoSize = true;
        _onlyAlerts.Location = new Point(0, 28);

        _receive.Bounds = new Rectangle(240, 24, 200, UiTheme.ButtonHeight);
        _issue.Bounds = new Rectangle(450, 24, 170, UiTheme.ButtonHeight);
        _refresh.Bounds = new Rectangle(630, 24, 110, UiTheme.ButtonHeight);

        stockBar.Controls.AddRange([_stockHeading, _onlyAlerts, _receive, _issue, _refresh]);

        BuildStockColumns();

        var stockHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        stockHost.Controls.Add(_stock);

        top.Controls.Add(stockHost);
        top.Controls.Add(stockBar);

        // --- lots -------------------------------------------------------------------------------------

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        var lotBar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = UiTheme.Surface };

        _lotsHeading.Font = UiTheme.SectionTitle;
        _lotsHeading.ForeColor = UiTheme.TextPrimary;
        _lotsHeading.AutoSize = true;
        _lotsHeading.Location = new Point(0, 2);

        _onlyQuarantined.Font = UiTheme.Small;
        _onlyQuarantined.ForeColor = UiTheme.TextSecondary;
        _onlyQuarantined.AutoSize = true;
        _onlyQuarantined.Location = new Point(280, 6);

        lotBar.Controls.AddRange([_lotsHeading, _onlyQuarantined]);

        BuildLotColumns();

        var lotHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        lotHost.Controls.Add(_lots);

        var lotActions = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = UiTheme.Surface };

        _release.Bounds = new Rectangle(0, 10, 200, UiTheme.ButtonHeight);
        _rejectLot.Bounds = new Rectangle(210, 10, 160, UiTheme.ButtonHeight);
        _clearRejection.Bounds = new Rectangle(380, 10, 210, UiTheme.ButtonHeight);
        _recall.Bounds = new Rectangle(600, 10, 190, UiTheme.ButtonHeight);
        _writeOff.Bounds = new Rectangle(800, 10, 180, UiTheme.ButtonHeight);

        _status.Bounds = new Rectangle(992, 16, 600, 20);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.AutoSize = false;

        lotActions.Controls.AddRange([_release, _rejectLot, _clearRejection, _recall, _writeOff,
                                      _status]);

        bottom.Controls.Add(lotHost);
        bottom.Controls.Add(lotActions);
        bottom.Controls.Add(lotBar);

        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);

        Controls.Add(split);
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

        _stock.SelectionChanged += async (_, _) => await LoadLotsAsync();

        _receive.Click += async (_, _) => await ReceiveAsync();
        _issue.Click += async (_, _) => await IssueAsync();
        _release.Click += async (_, _) => await ReleaseAsync();
        _rejectLot.Click += async (_, _) => await RejectAsync();
        _clearRejection.Click += async (_, _) => await ClearRejectionAsync();
        _recall.Click += async (_, _) => await RecallAsync();
        _writeOff.Click += async (_, _) => await WriteOffAsync();

        _stock.CellFormatting += OnStockFormatting;
        _lots.CellFormatting += OnLotFormatting;
    }

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
        else if (row.NeedsReorder || row.DaysToEarliestExpiry is <= 30 || row.QuarantinedLotCount > 0)
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
        else if (row.IsQuarantined || row.IsBeyondOpenStability || row.DaysToExpiry <= 30)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
        else if (row.IsUsable)
        {
            e.CellStyle!.ForeColor = UiTheme.TextPrimary;
        }
    }

    protected override void ApplyTexts()
    {
        _stockHeading.Text = T("Inventory.Title");
        _lotsHeading.Text = T("Inventory.Lots");
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

        if (_stock.Columns.Count > 0)
        {
            _stock.Column("code").HeaderText = T("Field.Code");
            _stock.Column("name").HeaderText = T("Inventory.Item");
            _stock.Column("kind").HeaderText = T("Field.Name");
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
            _lots.Column("days").HeaderText = T("Field.Date");
            _lots.Column("remaining").HeaderText = T("Field.Quantity");
            _lots.Column("received").HeaderText = T("Inventory.Receipt");
            _lots.Column("opened").HeaderText = T("Field.Date");
            _lots.Column("state").HeaderText = T("Field.Status");
            _lots.Column("location").HeaderText = T("Sample.Storage");
        }

        if (_stockRows.Count > 0) RenderStock();
        if (_lotRows.Count > 0) RenderLots();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadStockAsync(ct);
        await LoadLotsAsync(ct);
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading stock failed.");
        }
    }

    private void RenderStock()
    {
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

        _stockHeading.Text = $"{T("Inventory.Title")} — {_stockRows.Count}" +
                             (alertCount > 0 ? $" · {alertCount} {T("Inventory.BelowMinimum").ToLowerInvariant()}" : string.Empty);

        if (_stock.Rows.Count > 0) _stock.Rows[0].Selected = true;
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading lots failed.");
        }
    }

    private void RenderLots()
    {
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

        var quarantined = _lotRows.Count(r => r.IsQuarantined && !r.IsRejected);

        _lotsHeading.Text = $"{T("Inventory.Lots")} — {_lotRows.Count}" +
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
        _rejectLot.Enabled = row is { IsRejected: false };

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
            suppliers.IsSuccess ? suppliers.Value : []);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

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

        if (!decimal.TryParse(answer.Replace(',', '.'),
                              System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out var quantity)
            || quantity <= 0m)
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

        await ActivateAsync();
    }

    private async Task ReleaseAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow lot) return;

        var evidence = _dialogs.PromptForReason(
            T("Inventory.ReleaseEvidencePrompt", lot.LotNumber),
            T("Action.Release"), minimumLength: 10);

        if (evidence is null) return;

        var result = await _inventory.ReleaseLotAsync(lot.ReagentLotId, evidence);

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
            T("Inventory.RejectLotPrompt", lot.LotNumber), T("Action.Reject"));

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
            T("Inventory.RecallReferencePrompt", lot.LotNumber), T("Inventory.Recall"));

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
        }
        else
        {
            _dialogs.Info(T("Inventory.RecallNoResults"), T("Inventory.Recall"));
        }

        await ActivateAsync();
    }

    private async Task WriteOffAsync()
    {
        if (_lots.CurrentRow?.Tag is not LotRow lot) return;

        var kind = lot.IsExpired
            ? StockMovementKind.ExpiryWriteOff
            : StockMovementKind.DamageWriteOff;

        var reason = _dialogs.PromptForReason(
            T("Inventory.WriteOffPrompt", lot.LotNumber, lot.QuantityRemaining.ToString("N2")),
            T("Inventory.WriteOff"));

        if (reason is null) return;

        var result = await _inventory.AdjustAsync(lot.ReagentLotId, lot.QuantityRemaining,
                                                   kind, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await ActivateAsync();
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

    internal ReceiveLotRequest? Request { get; private set; }

    internal ReceiveLotDialog(ILocalizer localizer, IUserDialogs dialogs, StockRow item,
                              IReadOnlyList<Supplier> suppliers)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _item = item;

        Text = $"{localizer["Inventory.ReceiveLot"]} — {item.Code}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 400);
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

        var unitLabel = new Label
        {
            Text = item.UnitOfMeasure,
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(168, y + 24)
        };
        Controls.Add(unitLabel);

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
        ok.Bounds = new Rectangle(20, 350, 230, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(260, 350, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_lotNumber, _expiry, _quantity, _supplier, _deliveryNote,
                           _unitPrice, _storage, _openStability, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
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

        Request = new ReceiveLotRequest
        {
            StockItemId = _item.StockItemId,
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

    private static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(text.Replace(',', '.'),
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out value);

    private sealed record SupplierChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }
}
