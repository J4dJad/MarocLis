using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Supplier invoices (factures fournisseurs): entry against the purchase orders, the three-way
/// match, the loi 69-21 due dates, payments, the late-payment declaration and the accounting export.
/// <para>
/// The heading carries the due-date dashboard — overdue, due within 7 and 15 days, blocked — so the
/// payment run starts from the screen rather than from a spreadsheet.
/// </para>
/// </summary>
public sealed class SupplierInvoiceView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly SupplierInvoiceService _invoices;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<SupplierInvoiceView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _lines = UiTheme.Grid();
    private readonly DataGridView _variances = UiTheme.Grid();
    private readonly DataGridView _payments = UiTheme.Grid();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _linesTab = new(), _variancesTab = new(), _paymentsTab = new();

    // Direction A: title and count, search and the due-date chips, the invoices card and the
    // selected invoice's card. The filter list stays as the state the chips choose.
    private readonly Label _heading = StockPage.Title();
    private readonly Label _count = StockPage.Count();
    private readonly StockSearchBox _search = new();
    private readonly List<StockChip> _chips = [];
    private readonly StockFooter _summary = new();
    private readonly StockCard _detailCard;
    private readonly Label _detailInfo = new();
    private readonly ContextMenuStrip _headerMore = new();
    private readonly ContextMenuStrip _detailMore = new();
    private readonly Button _headerMoreButton;
    private readonly Button _detailMoreButton;
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true };

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newInvoice = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _rematch = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _unblock = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _pay = UiTheme.PrimaryButton(string.Empty);
    private readonly StockMenuButton _attach = new();
    private readonly Button _viewScan = UiTheme.SecondaryButton(string.Empty);
    private readonly StockMenuButton _cancel = new();
    private readonly StockMenuButton _late = new();
    private readonly StockMenuButton _export = new();
    private readonly StockMenuButton _settings = new();

    private IReadOnlyList<SupplierInvoiceRow> _rows = [];
    private SupplierInvoiceDetail? _detail;
    private SupplierDueSummary? _due;

    public SupplierInvoiceView(SupplierInvoiceService invoices, IDocumentOutput output, IUserDialogs dialogs,
                               ILocalizer localizer, ICultureManager cultureManager,
                               ILogger<SupplierInvoiceView>? logger = null)
        : base(localizer, cultureManager)
    {
        _invoices = invoices;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        var detailBody = new Panel { BackColor = UiTheme.SurfaceRaised };
        _detailCard = new StockCard(detailBody);
        _headerMoreButton = StockMenu.More(_headerMore, localizer);
        _detailMoreButton = StockMenu.More(_detailMore, localizer);

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.SupplierInvoices");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = StockPage.ContentPadding;

        // The rare tools of the payment run go behind « Plus d'actions ».
        foreach (var button in new[] { _late, _export, _settings }) _headerMore.Items.Add(StockMenu.Entry(button));
        var header = StockPage.Header(_heading, _count, _refresh, _headerMoreButton, _newInvoice);
        ShortcutHints.Attach(_newInvoice, NewKey);

        // One chip per filter; the due ones carry their count from the due-date dashboard.
        foreach (var filter in Enum.GetValues<SupplierInvoiceFilter>())
            _chips.Add(new StockChip { Tag = filter, Checked = filter == SupplierInvoiceFilter.Open });
        StockChip.Exclusive(_chips, chip => _filter.SelectedItem = chip.Tag);
        var filters = StockPage.Filters(_search, [.. _chips]);

        BuildColumns();
        var invoicesCard = new StockCard(_grid, heading: false, footer: _summary);

        _detailInfo.Font = UiTheme.Small;
        _detailInfo.ForeColor = UiTheme.TextSecondary;
        _detailInfo.BackColor = UiTheme.SurfaceRaised;
        _detailInfo.Dock = DockStyle.Top;
        _detailInfo.Height = 44;
        _detailInfo.Padding = new Padding(15, 0, 15, 4);
        _detailInfo.AutoSize = false;

        _linesTab.Controls.Add(_lines);
        _variancesTab.Controls.Add(_variances);
        _paymentsTab.Controls.Add(_payments);
        _tabs.TabPages.AddRange([_linesTab, _variancesTab, _paymentsTab]);

        var body = (Panel)_detailCard.Controls[0];
        body.Padding = new Padding(15, 0, 15, 4);
        body.Controls.Add(_tabs);
        body.Controls.Add(_detailInfo);

        // The selected invoice's actions, the payment being the main one.
        foreach (var button in new[] { _attach, _cancel }) _detailMore.Items.Add(StockMenu.Entry(button));
        foreach (var button in new[] { _rematch, _unblock, _viewScan, _pay }) StockPage.Action(button);
        _detailCard.Heading.End(_rematch, _unblock, _viewScan, _detailMoreButton, _pay);

        Controls.Add(StockPage.Stack(invoicesCard, _detailCard, 250));
        Controls.Add(filters);
        Controls.Add(header);
    }

    private const string NewKey = "Ctrl+N";

    /// <summary>Ctrl+N enters a new invoice, as the key drawn on the button says.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N) && _newInvoice.Enabled && _newInvoice.Visible)
        {
            _newInvoice.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static void AddColumn(DataGridView grid, string name, int weight, Font? font = null, bool right = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = font ?? UiTheme.Body,
                Alignment = right ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft
            }
        });

    private void BuildColumns()
    {
        AddColumn(_grid, "supplier", 16);
        AddColumn(_grid, "number", 11, UiTheme.MonospaceBold);
        AddColumn(_grid, "date", 8, UiTheme.Monospace);
        AddColumn(_grid, "delivery", 8, UiTheme.Monospace);
        AddColumn(_grid, "due", 8, UiTheme.Monospace);
        AddColumn(_grid, "days", 7, UiTheme.MonospaceBold, right: true);
        AddColumn(_grid, "status", 11, UiTheme.SmallBold);
        AddColumn(_grid, "ttc", 9, UiTheme.Monospace, right: true);
        AddColumn(_grid, "paid", 9, UiTheme.Monospace, right: true);
        AddColumn(_grid, "outstanding", 9, UiTheme.MonospaceBold, right: true);
        AddColumn(_grid, "scan", 4, UiTheme.Small);

        AddColumn(_lines, "line", 4, UiTheme.Monospace, right: true);
        AddColumn(_lines, "order", 11, UiTheme.Monospace);
        AddColumn(_lines, "item", 9, UiTheme.Monospace);
        AddColumn(_lines, "description", 26);
        AddColumn(_lines, "quantity", 8, UiTheme.Monospace, right: true);
        AddColumn(_lines, "price", 9, UiTheme.Monospace, right: true);
        AddColumn(_lines, "vat", 6, UiTheme.Monospace, right: true);
        AddColumn(_lines, "total", 10, UiTheme.Monospace, right: true);

        AddColumn(_variances, "line", 4, UiTheme.Monospace, right: true);
        AddColumn(_variances, "variance", 40);

        AddColumn(_payments, "date", 8, UiTheme.Monospace);
        AddColumn(_payments, "amount", 10, UiTheme.Monospace, right: true);
        AddColumn(_payments, "method", 10);
        AddColumn(_payments, "reference", 12, UiTheme.Monospace);
        AddColumn(_payments, "by", 12, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        // The formatter goes first: items added before it keep their raw enum name.
        _filter.Format += (_, e) => e.Value = e.ListItem is SupplierInvoiceFilter f ? T($"SupplierInvoiceFilter.{f}") : e.Value;
        foreach (var filter in Enum.GetValues<SupplierInvoiceFilter>()) _filter.Items.Add(filter);
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += async (_, _) => await LoadAsync();

        _refresh.Click += async (_, _) => await ActivateAsync();
        _grid.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadDetailAsync());
        _grid.CellFormatting += OnFormatting;
        StockGrid.Pills(_grid, "status", tag => tag is SupplierInvoiceRow row ? StatusPill(row) : null);
        _search.Changed += (_, _) => StockGrid.Filter(_grid, _search.Query);

        _newInvoice.Click += async (_, _) => await RunBusyAsync(NewInvoiceAsync);
        _rematch.Click += async (_, _) => await RunBusyAsync(RematchAsync);
        _unblock.Click += async (_, _) => await RunBusyAsync(UnblockAsync);
        _pay.Click += async (_, _) => await RunBusyAsync(PayAsync);
        _attach.Click += async (_, _) => await RunBusyAsync(AttachAsync);
        _viewScan.Click += async (_, _) => await ViewScanAsync();
        _cancel.Click += async (_, _) => await RunBusyAsync(CancelAsync);
        _late.Click += async (_, _) => await LatePaymentsAsync();
        _export.Click += async (_, _) => await ExportAsync();
        _settings.Click += async (_, _) => await SettingsAsync();

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Inventory.EnterInvoices, _newInvoice, _attach, _cancel);
        PermissionGate.Require(Permissions.Inventory.ApprovePayments, _unblock, _pay);
        PermissionGate.Require(Permissions.Billing.ExportAccounting, _export);
        PermissionGate.Require(Permissions.Administration.ManageSettings, _settings);
    }

    private void OnFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not SupplierInvoiceRow row) return;

        if (row.IsOverdue)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.Status == SupplierInvoiceStatus.Blocked)
        {
            // A blocked invoice is not urgent: its pill and its number say it, not the whole row.
            if (_grid.Columns[e.ColumnIndex].Name is "number")
                e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
        else if (!row.IsOpen)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
    }

    private static StockPill StatusPill(SupplierInvoiceRow row) => row switch
    {
        { IsOverdue: true } => StockPill.Critical,
        { Status: SupplierInvoiceStatus.Blocked } => StockPill.Warning,
        { Status: SupplierInvoiceStatus.Cancelled } => StockPill.Waiting,
        { IsOpen: false } => StockPill.Done,
        { Status: SupplierInvoiceStatus.PartiallyPaid } => StockPill.Running,
        _ => StockPill.Waiting
    };

    protected override void ApplyTexts()
    {
        _search.Placeholder = T("StocksUi.SearchInvoices");
        _headerMoreButton.Text = _detailMoreButton.Text = T("ResultEntry.MoreActions");
        _refresh.Text = T("Action.Refresh");
        _newInvoice.Text = T("SupplierInvoices.New");
        _rematch.Text = T("SupplierInvoices.Rematch");
        _unblock.Text = T("SupplierInvoices.Unblock");
        _pay.Text = T("SupplierInvoices.RecordPayment");
        _attach.Text = T("SupplierInvoices.AttachScan");
        _viewScan.Text = T("SupplierInvoices.ViewScan");
        _cancel.Text = T("SupplierInvoices.Cancel");
        _late.Text = T("SupplierInvoices.LatePayments");
        _export.Text = T("SupplierInvoices.AccountingExport");
        _settings.Text = T("SupplierInvoices.Settings");

        _linesTab.Text = T("PurchaseOrders.Lines");
        _variancesTab.Text = T("SupplierInvoices.Variances");
        _paymentsTab.Text = T("SupplierInvoices.Payments");

        _grid.Column("supplier").HeaderText = T("Inventory.Supplier");
        _grid.Column("number").HeaderText = T("SupplierInvoices.Number");
        _grid.Column("date").HeaderText = T("SupplierInvoices.InvoiceDate");
        _grid.Column("delivery").HeaderText = T("SupplierInvoices.DeliveryDate");
        _grid.Column("due").HeaderText = T("SupplierInvoices.DueDate");
        _grid.Column("days").HeaderText = T("SupplierInvoices.DaysToDue");
        _grid.Column("status").HeaderText = T("Field.Status");
        _grid.Column("ttc").HeaderText = T("PurchaseOrders.TotalIncludingVat");
        _grid.Column("paid").HeaderText = T("SupplierInvoices.Paid");
        _grid.Column("outstanding").HeaderText = T("SupplierInvoices.Outstanding");
        _grid.Column("scan").HeaderText = T("SupplierInvoices.Scan");

        _lines.Column("line").HeaderText = "#";
        _lines.Column("order").HeaderText = T("PurchaseOrders.Number");
        _lines.Column("item").HeaderText = T("Field.Code");
        _lines.Column("description").HeaderText = T("Inventory.Item");
        _lines.Column("quantity").HeaderText = T("Field.Quantity");
        _lines.Column("price").HeaderText = T("PurchaseOrders.UnitPrice");
        _lines.Column("vat").HeaderText = T("PurchaseOrders.Vat");
        _lines.Column("total").HeaderText = T("PurchaseOrders.TotalExcludingVat");

        _variances.Column("line").HeaderText = "#";
        _variances.Column("variance").HeaderText = T("SupplierInvoices.Variance");

        _payments.Column("date").HeaderText = T("Field.Date");
        _payments.Column("amount").HeaderText = T("SupplierInvoices.Amount");
        _payments.Column("method").HeaderText = T("SupplierInvoices.Method");
        _payments.Column("reference").HeaderText = T("SupplierInvoices.Reference");
        _payments.Column("by").HeaderText = T("Field.CreatedBy");

        _filter.Refresh();
        _detailCard.Title.Text = T("StocksUi.SelectedInvoice");
        RenderHeading();
        if (_rows.Count > 0) Render();
        RenderDetail();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(null, ct);

    private SupplierInvoiceRow? Selected => _grid.CurrentRow?.Tag as SupplierInvoiceRow;

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Date(DateOnly? date) => date?.ToString("dd/MM/yy") ?? string.Empty;

    private SupplierInvoiceFilter Filter =>
        _filter.SelectedItem is SupplierInvoiceFilter f ? f : SupplierInvoiceFilter.Open;

    private async Task LoadAsync(Guid? keep = null, CancellationToken ct = default)
    {
        try
        {
            keep ??= Selected?.Id;

            var due = await _invoices.GetDueSummaryAsync(ct);
            if (due.IsSuccess) _due = due.Value;

            var result = await _invoices.GetInvoicesAsync(Filter, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading supplier invoices failed.");
            _dialogs.Warn(T("Message.UnexpectedError"));
        }
    }

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var days = row.IsOpen
                ? row.DaysUntilDue < 0 ? $"+{-row.DaysUntilDue}" : $"J-{row.DaysUntilDue}"
                : string.Empty;

            var index = _grid.Rows.Add(
                row.SupplierName, row.InvoiceNumber, Date(row.InvoiceDate), Date(row.DeliveryDate),
                Date(row.DueDate), days, T($"SupplierInvoiceStatus.{row.Status}"),
                Amount(row.TotalIncludingVat), Amount(row.AmountPaid), Amount(row.AmountOutstanding),
                row.HasScan ? "PDF" : string.Empty);

            _grid.Rows[index].Tag = row;
            if (row.Id == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        StockGrid.Filter(_grid, _search.Query);
        RenderHeading();

        if (keep is null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
        if (_grid.Rows.Count == 0)
        {
            _detail = null;
            RenderDetail();
        }
    }

    private void RenderHeading()
    {
        _heading.Text = T("SupplierInvoices.Title");
        _count.Text = T("StocksUi.Invoices", _rows.Count);
        foreach (var chip in _chips)
        {
            var filter = (SupplierInvoiceFilter)chip.Tag!;
            int? count = _due is not { } due ? null : filter switch
            {
                SupplierInvoiceFilter.Overdue => due.Overdue,
                SupplierInvoiceFilter.DueWithin7Days => due.DueWithin7Days,
                SupplierInvoiceFilter.DueWithin15Days => due.DueWithin15Days,
                SupplierInvoiceFilter.Blocked => due.Blocked,
                _ => null
            };
            chip.Text = count is { } n ? $"{T($"SupplierInvoiceFilter.{filter}")} · {n}" : T($"SupplierInvoiceFilter.{filter}");
        }

        _summary.Text = _due is { } d
            ? T("SupplierInvoices.DueSummary", d.Overdue, Amount(d.OverdueAmount), d.DueWithin7Days,
                Amount(d.DueWithin7DaysAmount), d.DueWithin15Days, Amount(d.DueWithin15DaysAmount), d.Blocked)
            : string.Empty;
        _summary.ForeColor = _due is { Overdue: > 0 } ? UiTheme.Critical : UiTheme.TextSecondary;
    }

    private async Task LoadDetailAsync()
    {
        try
        {
            if (Selected is not { } row)
            {
                _detail = null;
                RenderDetail();
                return;
            }

            var result = await _invoices.GetDetailAsync(row.Id);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
            if (Selected?.Id != row.Id) return;

            _detail = result.Value;
            RenderDetail();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading a supplier invoice failed.");
        }
    }

    private void RenderDetail()
    {
        _lines.Rows.Clear();
        _variances.Rows.Clear();
        _payments.Rows.Clear();

        if (_detail is not { } d)
        {
            _detailCard.Note.Text = string.Empty;
            _detailInfo.Text = string.Empty;
            RefreshButtons();
            return;
        }

        foreach (var l in d.Lines)
            _lines.Rows.Add(l.LineNumber, l.OrderNumber, l.ItemCode, l.Description, Quantity(l.Quantity),
                            Amount(l.UnitPriceMad), $"{l.VatRate * 100m:0.#}%", Amount(l.TotalExcludingVat));

        foreach (var v in d.Variances)
            _variances.Rows.Add(v.LineNumber, DescribeVariance(v));

        foreach (var p in d.Payments)
            _payments.Rows.Add(Date(p.PaidOn), Amount(p.AmountMad), T($"SupplierPaymentMethod.{p.Method}"),
                               p.Reference ?? string.Empty, p.RecordedByName);

        var h = d.Header;
        _detailCard.Note.Text = $"{h.InvoiceNumber} · {h.SupplierName} · {T($"SupplierInvoiceStatus.{h.Status}")}";

        var info = T("SupplierInvoices.DetailInfo", Amount(h.TotalExcludingVat),
                     Amount(h.TotalIncludingVat - h.TotalExcludingVat), Amount(h.TotalIncludingVat),
                     d.PaymentTermsDays, Date(h.DueDate), h.EnteredByName);
        if (d.UnblockJustification is not null)
            info += "\n" + T("SupplierInvoices.UnblockedInfo", d.UnblockedByName ?? "?",
                             d.UnblockedAt?.ToString("dd/MM/yy HH:mm") ?? string.Empty, d.UnblockJustification);
        if (d.CancellationReason is not null)
            info += "\n" + T("SupplierInvoices.CancelledInfo", d.CancellationReason);
        _detailInfo.Text = info;

        _variancesTab.Text = d.Variances.Count > 0
            ? $"{T("SupplierInvoices.Variances")} ({d.Variances.Count})"
            : T("SupplierInvoices.Variances");

        RefreshButtons();
    }

    private string DescribeVariance(SupplierInvoiceVarianceRow v) => v.Kind switch
    {
        VarianceKind.PriceDiffers => T("VarianceKind.PriceDiffers", Amount(v.Expected), Amount(v.Actual)),
        VarianceKind.VatRateDiffers => T("VarianceKind.VatRateDiffers", $"{v.Expected * 100m:0.#}%",
                                         $"{v.Actual * 100m:0.#}%"),
        _ => T("VarianceKind.QuantityExceedsReceived", Quantity(v.Expected), Quantity(v.Actual))
    };

    private void RefreshButtons()
    {
        var row = Selected;
        _rematch.Enabled = row?.Status is SupplierInvoiceStatus.Blocked;
        _unblock.Enabled = row?.Status is SupplierInvoiceStatus.Blocked;
        _pay.Enabled = row?.Status is SupplierInvoiceStatus.ApprovedForPayment or SupplierInvoiceStatus.PartiallyPaid;
        _attach.Enabled = row is not null && row.Status != SupplierInvoiceStatus.Cancelled;
        _viewScan.Enabled = row is { HasScan: true };
        _cancel.Enabled = row is not null && row.IsOpen && row.AmountPaid == 0m;
    }

    // ---- Actions -------------------------------------------------------------------------------------

    private async Task NewInvoiceAsync()
    {
        try
        {
            var suppliers = await _invoices.GetSuppliersAsync();
            if (suppliers.IsFailure) { _dialogs.ShowFailure(suppliers, _newInvoice.Text); return; }
            if (suppliers.Value.Count == 0)
            {
                _dialogs.Warn(T("SupplierInvoices.NoSupplierWithOrders"), _newInvoice.Text);
                return;
            }

            using var dialog = new SupplierInvoiceEntryDialog(Localizer, _dialogs, suppliers.Value,
                                                              id => _invoices.GetInvoiceableLinesAsync(id));
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

            var result = await _invoices.CreateAsync(dialog.Request);
            if (result.IsFailure) { _dialogs.ShowFailure(result, _newInvoice.Text); return; }

            var outcome = result.Value;
            if (outcome.Status == SupplierInvoiceStatus.Blocked)
                _dialogs.Warn(T("SupplierInvoices.CreatedBlocked", outcome.VarianceCount, Date(outcome.DueDate)),
                              _newInvoice.Text);
            else
                _dialogs.Info(T("SupplierInvoices.CreatedApproved", Date(outcome.DueDate)), _newInvoice.Text);

            if (dialog.ScanPath is { } path) await AttachFileAsync(outcome.InvoiceId, path);

            await LoadAsync(outcome.InvoiceId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Entering a supplier invoice failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _newInvoice.Text);
        }
    }

    private async Task RematchAsync()
    {
        if (Selected is not { } row) return;

        var result = await _invoices.RematchAsync(row.Id);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _rematch.Text); return; }

        if (result.Value.Status == SupplierInvoiceStatus.Blocked)
            _dialogs.Warn(T("SupplierInvoices.StillBlocked", result.Value.VarianceCount), _rematch.Text);
        else
            _dialogs.Info(T("SupplierInvoices.CreatedApproved", Date(result.Value.DueDate)), _rematch.Text);

        await LoadAsync(row.Id);
    }

    private async Task UnblockAsync()
    {
        if (Selected is not { } row) return;

        var reason = _dialogs.PromptForReason(T("SupplierInvoices.UnblockPrompt", row.InvoiceNumber, row.VarianceCount),
                                              _unblock.Text, 10);
        if (reason is null) return;

        var result = await _invoices.UnblockAsync(row.Id, reason);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _unblock.Text); return; }

        await LoadAsync(row.Id);
    }

    private async Task PayAsync()
    {
        if (Selected is not { } row) return;

        using var dialog = new SupplierPaymentDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var result = await _invoices.RecordPaymentAsync(row.Id, dialog.Request);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _pay.Text); return; }

        await LoadAsync(row.Id);
    }

    private async Task AttachAsync()
    {
        if (Selected is not { } row) return;

        if (row.HasScan && !_dialogs.Confirm(T("SupplierInvoices.ReplaceScan"), _attach.Text)) return;

        using var picker = new OpenFileDialog
        {
            Title = _attach.Text,
            Filter = $"{T("Credential.ScanFiles")} (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        await AttachFileAsync(row.Id, picker.FileName);
        await LoadAsync(row.Id);
    }

    private async Task AttachFileAsync(Guid invoiceId, string path)
    {
        byte[] content;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > SupplierInvoiceFile.MaximumBytes)
            {
                _dialogs.Warn(T("SupplierInvoice_ScanTooLarge", SupplierInvoiceFile.MaximumBytes / (1024 * 1024)),
                              _attach.Text);
                return;
            }
            content = await File.ReadAllBytesAsync(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T("Documents.FileUnreadable")}\n{ex.Message}", _attach.Text);
            return;
        }

        var attached = await _invoices.AttachScanAsync(invoiceId, Path.GetFileName(path), content);
        if (attached.IsFailure) _dialogs.ShowFailure(attached, _attach.Text);
    }

    private async Task ViewScanAsync()
    {
        if (Selected is not { HasScan: true } row) return;

        var file = await _invoices.GetScanAsync(row.Id);
        if (file.IsFailure) { _dialogs.ShowFailure(file, _viewScan.Text); return; }

        var opened = await _output.ViewAsync(file.Value.Content, Path.GetExtension(file.Value.FileName));
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _viewScan.Text);
    }

    private async Task CancelAsync()
    {
        if (Selected is not { } row) return;

        var reason = _dialogs.PromptForReason(T("SupplierInvoices.CancelPrompt", row.InvoiceNumber), _cancel.Text);
        if (reason is null) return;

        var result = await _invoices.CancelAsync(row.Id, reason);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _cancel.Text); return; }

        await LoadAsync(row.Id);
    }

    private async Task LatePaymentsAsync()
    {
        try
        {
            using var dialog = new PurchasingPeriodDialog(Localizer, _dialogs, "SupplierInvoices.LatePayments",
                                                          withLayout: false);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var rows = await _invoices.GetLatePaymentsAsync(dialog.From, dialog.To);
            if (rows.IsFailure) { _dialogs.ShowFailure(rows, _late.Text); return; }

            if (rows.Value.Count == 0)
            {
                _dialogs.Info(T("SupplierInvoices.NoLatePayments"), _late.Text);
                return;
            }

            var file = await _invoices.ExportLatePaymentsAsync(dialog.From, dialog.To);
            if (file.IsFailure) { _dialogs.ShowFailure(file, _late.Text); return; }

            var opened = await _output.OpenAsync(file.Value, "csv");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _late.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Listing late supplier payments failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _late.Text);
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            using var dialog = new PurchasingPeriodDialog(Localizer, _dialogs, "SupplierInvoices.AccountingExport",
                                                          withLayout: true);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var file = await _invoices.ExportAccountingAsync(dialog.From, dialog.To, dialog.ExportLayout);
            if (file.IsFailure) { _dialogs.ShowFailure(file, _export.Text); return; }

            var opened = await _output.OpenAsync(file.Value, "csv");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _export.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting supplier accounting entries failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _export.Text);
        }
    }

    private async Task SettingsAsync()
    {
        try
        {
            var current = await _invoices.GetSettingsAsync();
            if (current.IsFailure) { _dialogs.ShowFailure(current, _settings.Text); return; }

            using var dialog = new PurchasingSettingsDialog(Localizer, _dialogs, current.Value);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Settings is null) return;

            var saved = await _invoices.SaveSettingsAsync(dialog.Settings);
            if (saved.IsFailure) _dialogs.ShowFailure(saved, _settings.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving the purchasing settings failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _settings.Text);
        }
    }
}

/// <summary>
/// A new supplier invoice: the supplier, its number and dates, then the order lines it bills, each
/// pre-filled with what was received and not yet invoiced, at the ordered net price.
/// </summary>
internal sealed class SupplierInvoiceEntryDialog : MasterDataEditor
{
    private const string ColInclude = "include", ColQuantity = "quantity", ColPrice = "price", ColVat = "vat";

    private readonly Func<Guid, Task<Result<IReadOnlyList<InvoiceableOrderLine>>>> _loadLines;
    private readonly ComboBox _supplier;
    private readonly TextBox _number, _notes;
    private readonly DateTimePicker _invoiceDate, _receivedOn;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Label _total = new();
    private readonly CheckBox _scan;

    internal CreateSupplierInvoiceRequest? Request { get; private set; }

    /// <summary>A scan chosen in the dialog, attached once the invoice is saved.</summary>
    internal string? ScanPath { get; private set; }

    internal SupplierInvoiceEntryDialog(ILocalizer localizer, IUserDialogs dialogs,
                                        IReadOnlyList<SupplierOption> suppliers,
                                        Func<Guid, Task<Result<IReadOnlyList<InvoiceableOrderLine>>>> loadLines)
        : base(localizer, dialogs, "SupplierInvoices.New", null, 980, 600)
    {
        _loadLines = loadLines;

        _supplier = Lookup("Inventory.Supplier", 20, 14, 320,
                           suppliers.Select(s => (s.Id, $"{s.Code} — {s.Name}")), null, allowNone: false);
        _number = TextField("SupplierInvoices.Number", 360, 14, 180, null, latin: true, mono: true);

        Label("SupplierInvoices.InvoiceDate", 560, 14);
        _invoiceDate = new DateTimePicker
        {
            Bounds = new Rectangle(560, 32, 170, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MaxDate = DateTime.Today,
            Value = DateTime.Today
        };

        Label("SupplierInvoices.ReceivedOn", 750, 14);
        _receivedOn = new DateTimePicker
        {
            Bounds = new Rectangle(750, 32, 170, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MaxDate = DateTime.Today,
            Value = DateTime.Today
        };
        Controls.AddRange([_invoiceDate, _receivedOn]);

        _notes = TextField("Field.Notes", 20, 68, 700);
        _scan = Check("SupplierInvoices.AttachScanAfter", 740, 90, false);

        Notice(20, 122, 940, 34, "SupplierInvoices.EntryNotice");

        _grid.Bounds = new Rectangle(20, 160, 940, 340);
        _grid.Dock = DockStyle.None;
        _grid.ReadOnly = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        BuildColumns();
        Controls.Add(_grid);

        _total.Font = UiTheme.SmallBold;
        _total.ForeColor = UiTheme.TextPrimary;
        _total.AutoSize = false;
        _total.Bounds = new Rectangle(20, 506, 940, 20);
        Controls.Add(_total);

        _supplier.SelectedIndexChanged += async (_, _) => await LoadLinesAsync();
        _grid.CellValueChanged += (_, _) => RefreshTotal();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        Buttons(540, 980);
        Shown += async (_, _) => await LoadLinesAsync();

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.SupplierInvoiceEntry");
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = ColInclude, FillWeight = 4, HeaderText = string.Empty,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        void Add(string name, string headerKey, int weight, bool editable = false, bool right = false) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = Localizer[headerKey], FillWeight = weight, ReadOnly = !editable,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = editable ? UiTheme.MonospaceBold : UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft,
                    BackColor = editable ? UiTheme.SurfaceRaised : UiTheme.SurfaceSunken
                }
            });

        Add("order", "PurchaseOrders.Number", 11);
        Add("item", "Inventory.Item", 24);
        Add("ordered", "PurchaseOrders.Ordered", 7, right: true);
        Add("received", "PurchaseOrders.Received", 7, right: true);
        Add("invoiced", "SupplierInvoices.AlreadyInvoiced", 7, right: true);
        Add(ColQuantity, "SupplierInvoices.QuantityInvoiced", 8, editable: true, right: true);
        Add(ColPrice, "SupplierInvoices.InvoicedUnitPrice", 9, editable: true, right: true);
        Add(ColVat, "PurchaseOrders.VatPercent", 6, editable: true, right: true);
    }

    private async Task LoadLinesAsync()
    {
        _grid.Rows.Clear();
        if (SelectedId(_supplier) is not { } supplierId) return;

        var lines = await _loadLines(supplierId);
        if (lines.IsFailure) { Dialogs.ShowFailure(lines, Text); return; }
        if (SelectedId(_supplier) != supplierId) return;

        static string N(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        foreach (var line in lines.Value)
        {
            var index = _grid.Rows.Add(
                line.ReceivedNotInvoiced > 0m, line.OrderNumber, $"{line.ItemCode} — {line.Description}",
                N(line.QuantityOrdered), N(line.QuantityReceived), N(line.QuantityInvoiced),
                N(line.ReceivedNotInvoiced > 0m ? line.ReceivedNotInvoiced : line.QuantityOrdered - line.QuantityInvoiced),
                line.NetUnitPriceMad.ToString("0.00", CultureInfo.InvariantCulture),
                N(line.VatRate * 100m));
            _grid.Rows[index].Tag = line;
        }

        RefreshTotal();
    }

    private IEnumerable<(InvoiceableOrderLine Line, decimal? Quantity, decimal? Price, decimal? Vat)> Included() =>
        _grid.Rows.Cast<DataGridViewRow>()
             .Where(r => r.Tag is InvoiceableOrderLine && r.Cells[ColInclude].Value is true)
             .Select(r => ((InvoiceableOrderLine)r.Tag!,
                           EditorValues.ParseDecimal(r.Cells[ColQuantity].Value?.ToString()),
                           EditorValues.ParseDecimal(r.Cells[ColPrice].Value?.ToString()),
                           EditorValues.ParseDecimal(r.Cells[ColVat].Value?.ToString())));

    private void RefreshTotal()
    {
        decimal ht = 0m, vat = 0m;
        foreach (var (_, quantity, price, rate) in Included())
        {
            var lineHt = Money.Round((quantity ?? 0m) * Money.Round(price ?? 0m));
            ht += lineHt;
            vat += Money.Round(lineHt * (rate ?? 0m) / 100m);
        }

        _total.Text = Localizer.Format("PurchaseOrders.TotalsLine", ht.ToString("N2", CultureInfo.CurrentCulture),
                                       vat.ToString("N2", CultureInfo.CurrentCulture),
                                       (ht + vat).ToString("N2", CultureInfo.CurrentCulture));
    }

    protected override void Accept()
    {
        _grid.EndEdit();

        if (SelectedId(_supplier) is not { } supplierId) return;

        if (string.IsNullOrWhiteSpace(_number.Text))
        {
            Dialogs.Warn(Localizer["SupplierInvoice_NumberRequired"], Text);
            _number.Focus();
            return;
        }

        var lines = new List<SupplierInvoiceLineRequest>();
        foreach (var (line, quantity, price, vat) in Included())
        {
            if (quantity is not > 0m)
            {
                Dialogs.Warn(Localizer["SupplierInvoice_QuantityMustBePositive"], Text);
                return;
            }
            if (price is not >= 0m || vat is not (>= 0m and <= 100m))
            {
                Dialogs.Warn(Localizer["SupplierInvoice_PriceMustNotBeNegative"], Text);
                return;
            }

            lines.Add(new SupplierInvoiceLineRequest(line.PurchaseOrderLineId, quantity.Value, price.Value,
                                                     vat.Value / 100m));
        }

        if (lines.Count == 0)
        {
            Dialogs.Warn(Localizer["SupplierInvoice_NoLines"], Text);
            return;
        }

        if (_scan.Checked)
        {
            using var picker = new OpenFileDialog
            {
                Filter = $"{Localizer["Credential.ScanFiles"]} (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff",
                CheckFileExists = true
            };
            if (picker.ShowDialog(this) == DialogResult.OK) ScanPath = picker.FileName;
        }

        Request = new CreateSupplierInvoiceRequest
        {
            SupplierId = supplierId,
            InvoiceNumber = _number.Text.Trim(),
            InvoiceDate = DateOnly.FromDateTime(_invoiceDate.Value.Date),
            ReceivedOn = DateOnly.FromDateTime(_receivedOn.Value.Date),
            Notes = Blank(_notes),
            Lines = lines
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A payment against an invoice approved for payment, the outstanding amount by default.</summary>
internal sealed class SupplierPaymentDialog : MasterDataEditor
{
    private readonly DateTimePicker _date, _maturity;
    private readonly TextBox _amount, _reference;
    private readonly ComboBox _method;

    internal RecordSupplierPaymentRequest? Request { get; private set; }

    internal SupplierPaymentDialog(ILocalizer localizer, IUserDialogs dialogs, SupplierInvoiceRow row)
        : base(localizer, dialogs, "SupplierInvoices.RecordPayment", $"{row.InvoiceNumber} · {row.SupplierName}", 520, 270)
    {
        Label("Field.Date", 20, 14);
        _date = new DateTimePicker
        {
            Bounds = new Rectangle(20, 32, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MaxDate = DateTime.Today,
            Value = DateTime.Today
        };
        Controls.Add(_date);

        _amount = TextField("SupplierInvoices.Amount", 200, 14, 140,
                            row.AmountOutstanding.ToString("0.00", CultureInfo.InvariantCulture),
                            latin: true, mono: true, right: true);
        _method = Choice("SupplierInvoices.Method", 360, 14, 140, SupplierPaymentMethod.Transfer,
                         "SupplierPaymentMethod");
        _reference = TextField("SupplierInvoices.Reference", 20, 68, 320, null, latin: true, mono: true);
        _reference.MaxLength = 60;

        // An LCN or a post-dated cheque reaches the bank on its due date, which the bank
        // reconciliation looks for it on; the payment date by default.
        Label("SupplierInvoices.MaturityDate", 360, 68);
        _maturity = new DateTimePicker
        {
            Bounds = new Rectangle(360, 86, 140, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = DateTime.Today
        };
        Controls.Add(_maturity);

        void Refresh()
        {
            var method = Selected<SupplierPaymentMethod>(_method);
            _maturity.Enabled = method is SupplierPaymentMethod.Cheque or SupplierPaymentMethod.BillOfExchange;
            if (_maturity.Value.Date < _date.Value.Date) _maturity.Value = _date.Value.Date;
        }

        _method.SelectedIndexChanged += (_, _) => Refresh();
        _date.ValueChanged += (_, _) => Refresh();
        Refresh();

        Notice(20, 124, 480, 60, "SupplierInvoices.PaymentNotice");

        Buttons(210, 520);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.SupplierPayment");
    }

    protected override void Accept()
    {
        if (DecimalOrNull(_amount) is not { } amount || amount <= 0m)
        {
            Dialogs.Warn(Localizer["SupplierPayment_AmountMustBePositive"], Text);
            _amount.Focus();
            return;
        }

        var method = Selected<SupplierPaymentMethod>(_method);
        if (method != SupplierPaymentMethod.Cash && string.IsNullOrWhiteSpace(_reference.Text))
        {
            Dialogs.Warn(Localizer["SupplierPayment_ReferenceRequired"], Text);
            _reference.Focus();
            return;
        }

        var paidOn = DateOnly.FromDateTime(_date.Value.Date);
        var maturity = DateOnly.FromDateTime(_maturity.Value.Date);
        if (_maturity.Enabled && maturity < paidOn)
        {
            Dialogs.Warn(Localizer["SupplierPayment_MaturityBeforePayment"], Text);
            _maturity.Focus();
            return;
        }

        // An LCN always falls due after it is issued: left on the payment date, its due date was
        // not entered, and the bank reconciliation and the forecast would look for it on the wrong day.
        var bill = method == SupplierPaymentMethod.BillOfExchange;
        if (bill && maturity <= paidOn)
        {
            Dialogs.Warn(Localizer["SupplierPayment_MaturityRequired"], Text);
            _maturity.Focus();
            return;
        }

        Request = new RecordSupplierPaymentRequest(paidOn, amount, method, Blank(_reference),
                                                   _maturity.Enabled && (bill || maturity > paidOn) ? maturity : null);
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A period, and for the accounting export the file layout.</summary>
internal sealed class PurchasingPeriodDialog : MasterDataEditor
{
    private readonly DateTimePicker _from, _to;
    private readonly ComboBox? _layout;

    internal DateOnly From { get; private set; }
    internal DateOnly To { get; private set; }
    internal AccountingExportLayout ExportLayout { get; private set; }

    internal PurchasingPeriodDialog(ILocalizer localizer, IUserDialogs dialogs, string titleKey, bool withLayout)
        : base(localizer, dialogs, titleKey, null, 520, withLayout ? 250 : 200)
    {
        // The last calendar quarter by default: the declaration of late payments is quarterly.
        var today = DateTime.Today;
        var quarterStart = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(-3);

        Label("Field.From", 20, 14);
        _from = new DateTimePicker
        {
            Bounds = new Rectangle(20, 32, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = quarterStart
        };
        Label("Field.To", 220, 14);
        _to = new DateTimePicker
        {
            Bounds = new Rectangle(220, 32, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Value = quarterStart.AddMonths(3).AddDays(-1)
        };
        Controls.AddRange([_from, _to]);

        if (withLayout)
            _layout = Choice("SupplierInvoices.ExportLayout", 20, 70, 380, AccountingExportLayout.Sage,
                             "AccountingExportLayout");
        else
            Notice(20, 70, 480, 50, "SupplierInvoices.LatePaymentsNotice");

        Buttons(withLayout ? 190 : 140, 520, "Action.Export");

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.PurchasingPeriod");
    }

    protected override void Accept()
    {
        From = DateOnly.FromDateTime(_from.Value.Date);
        To = DateOnly.FromDateTime(_to.Value.Date);
        if (From > To)
        {
            Dialogs.Warn(Localizer["Purchasing_PeriodReversed"], Text);
            return;
        }

        ExportLayout = _layout is null ? AccountingExportLayout.Sage : Selected<AccountingExportLayout>(_layout);
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>The match tolerance, the loi 69-21 rates and the accounts of the export.</summary>
internal sealed class PurchasingSettingsDialog : MasterDataEditor
{
    private readonly PurchasingSettings _current;
    private readonly TextBox _tolPercent, _tolMad, _keyRate, _margin, _fineFirst, _fineNext;
    private readonly TextBox _purchaseJournal, _bankJournal, _cashJournal, _supplierAccount, _vatAccount,
                             _bankAccount, _cashAccount;
    private readonly Dictionary<StockItemKind, TextBox> _charges = [];

    internal PurchasingSettings? Settings { get; private set; }

    internal PurchasingSettingsDialog(ILocalizer localizer, IUserDialogs dialogs, PurchasingSettings current)
        : base(localizer, dialogs, "SupplierInvoices.Settings", null, 760, 620)
    {
        _current = current;
        static string N(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        _tolPercent = TextField("SupplierInvoices.TolerancePercent", 20, 14, 160, N(current.PriceTolerancePercent),
                                latin: true, mono: true, right: true);
        _tolMad = TextField("SupplierInvoices.ToleranceMad", 200, 14, 160, N(current.PriceToleranceMad),
                            latin: true, mono: true, right: true);

        _keyRate = TextField("SupplierInvoices.KeyRate", 20, 68, 160, N(current.KeyRatePercent),
                             latin: true, mono: true, right: true);
        _margin = TextField("SupplierInvoices.PenaltyMargin", 200, 68, 160, N(current.PenaltyMarginPoints),
                            latin: true, mono: true, right: true);
        _fineFirst = TextField("SupplierInvoices.FineFirstMonth", 380, 68, 160, N(current.FineFirstMonthPercent),
                               latin: true, mono: true, right: true);
        _fineNext = TextField("SupplierInvoices.FineNextMonths", 560, 68, 160, N(current.FineAdditionalMonthPercent),
                              latin: true, mono: true, right: true);
        Notice(20, 122, 720, 46, "SupplierInvoices.RatesNotice", UiTheme.Abnormal);

        _purchaseJournal = TextField("SupplierInvoices.PurchaseJournal", 20, 176, 160, current.PurchaseJournal, latin: true, mono: true);
        _bankJournal = TextField("SupplierInvoices.BankJournal", 200, 176, 160, current.BankJournal, latin: true, mono: true);
        _cashJournal = TextField("SupplierInvoices.CashJournal", 380, 176, 160, current.CashJournal, latin: true, mono: true);

        _supplierAccount = TextField("SupplierInvoices.SuppliersAccount", 20, 230, 160, current.SupplierAccount, latin: true, mono: true);
        _vatAccount = TextField("SupplierInvoices.VatAccount", 200, 230, 160, current.VatAccount, latin: true, mono: true);
        _bankAccount = TextField("SupplierInvoices.BankAccount", 380, 230, 160, current.BankAccount, latin: true, mono: true);
        _cashAccount = TextField("SupplierInvoices.CashAccount", 560, 230, 160, current.CashAccount, latin: true, mono: true);

        Label("SupplierInvoices.ChargeAccounts", 20, 288);
        var i = 0;
        foreach (var kind in Enum.GetValues<StockItemKind>())
        {
            var x = 20 + i % 4 * 180;
            var y = 310 + i / 4 * 54;
            _charges[kind] = TextField($"StockItemKind.{kind}", x, y, 160, current.ChargeAccountFor(kind),
                                       latin: true, mono: true);
            i++;
        }

        Buttons(560, 760);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.PurchasingSettings");
    }

    protected override void Accept()
    {
        decimal? Rate(TextBox box) => DecimalOrNull(box) is { } v && v >= 0m ? v : null;

        if (Rate(_tolPercent) is not { } tolPercent || Rate(_tolMad) is not { } tolMad
            || Rate(_keyRate) is not { } keyRate || Rate(_margin) is not { } margin
            || Rate(_fineFirst) is not { } fineFirst || Rate(_fineNext) is not { } fineNext)
        {
            Dialogs.Warn(Localizer["Purchasing_SettingOutOfRange"], Text);
            return;
        }

        string Code(TextBox box, string fallback) => Blank(box) ?? fallback;

        Settings = _current with
        {
            PriceTolerancePercent = tolPercent,
            PriceToleranceMad = tolMad,
            KeyRatePercent = keyRate,
            PenaltyMarginPoints = margin,
            FineFirstMonthPercent = fineFirst,
            FineAdditionalMonthPercent = fineNext,
            PurchaseJournal = Code(_purchaseJournal, _current.PurchaseJournal),
            BankJournal = Code(_bankJournal, _current.BankJournal),
            CashJournal = Code(_cashJournal, _current.CashJournal),
            SupplierAccount = Code(_supplierAccount, _current.SupplierAccount),
            VatAccount = Code(_vatAccount, _current.VatAccount),
            BankAccount = Code(_bankAccount, _current.BankAccount),
            CashAccount = Code(_cashAccount, _current.CashAccount),
            ChargeAccounts = _charges.ToDictionary(c => c.Key, c => Code(c.Value, _current.ChargeAccountFor(c.Key)))
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
