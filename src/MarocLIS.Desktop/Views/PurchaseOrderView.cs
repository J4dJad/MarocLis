using System.Globalization;
using MarocLIS.Domain.Common;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Purchase orders: raised, approved by someone else, sent, received line by line.
/// <para>
/// Receiving a line asks for the lot number and expiry, because the delivery goes straight into
/// stock as a quarantined lot. The stock screen then shows the same delivery without anyone
/// keying it a second time.
/// </para>
/// </summary>
public sealed class PurchaseOrderView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly PurchaseOrderService _orders;
    private readonly PurchaseOrderDocumentService _documents;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PurchaseOrderView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _lines = UiTheme.Grid();
    // Direction A: title and count, search and chip, the orders card and the lines card.
    private readonly Label _title = StockPage.Title();
    private readonly Label _count = StockPage.Count();
    private readonly StockSearchBox _search = new();
    private readonly CheckBox _onlyOpen = new StockChip();
    private readonly StockCard _linesCard;
    private readonly ContextMenuStrip _headerMore = new();
    private readonly ContextMenuStrip _linesMore = new();
    private readonly Button _headerMoreButton;
    private readonly Button _linesMoreButton;

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newOrder = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _printPdf = UiTheme.SecondaryButton(string.Empty);
    private readonly StockMenuButton _savePdf = new();
    private readonly Button _addLine = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _approve = UiTheme.SecondaryButton(string.Empty);
    private readonly ToolTip _tips = new();
    private readonly Button _send = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _receive = UiTheme.PrimaryButton(string.Empty);
    private readonly StockMenuButton _cancel = new();

    private readonly StockFooter _totals = new();

    private IReadOnlyList<PurchaseOrderRow> _rows = [];
    private IReadOnlyList<PurchaseOrderLineRow> _lineRows = [];

    // Reception checks, refusal of a delivery and the supplier's registration warning.
    private readonly ReceptionService? _reception;

    public PurchaseOrderView(PurchaseOrderService orders, PurchaseOrderDocumentService documents,
                             IDocumentOutput output, IUserDialogs dialogs, ILocalizer localizer,
                             ICultureManager cultureManager, ILogger<PurchaseOrderView>? logger = null,
                             ReceptionService? reception = null)
        : base(localizer, cultureManager)
    {
        _orders = orders;
        _documents = documents;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;
        _reception = reception;

        _linesCard = new StockCard(_lines, footer: _totals);
        _headerMoreButton = StockMenu.More(_headerMore, localizer);
        _linesMoreButton = StockMenu.More(_linesMore, localizer);

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.PurchaseOrders");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = StockPage.ContentPadding;

        _onlyOpen.Checked = true;
        _headerMore.Items.Add(StockMenu.Entry(_savePdf));
        var header = StockPage.Header(_title, _count, _refresh, _printPdf, _headerMoreButton, _newOrder);
        ShortcutHints.Attach(_newOrder, NewKey);

        var filters = StockPage.Filters(_search, _onlyOpen);

        BuildOrderColumns();
        BuildLineColumns();

        var ordersCard = new StockCard(_grid, heading: false);

        // The selected order's lines and what can be done with it, the receipt being the main one.
        _linesMore.Items.Add(StockMenu.Entry(_cancel));
        foreach (var button in new[] { _addLine, _approve, _send, _receive }) StockPage.Action(button);
        _linesCard.Heading.End(_addLine, _approve, _send, _linesMoreButton, _receive);

        Controls.Add(StockPage.Stack(ordersCard, _linesCard, 270));
        Controls.Add(filters);
        Controls.Add(header);
    }

    private const string NewKey = "Ctrl+N";

    /// <summary>Ctrl+N raises a new order, as the key drawn on the button says.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N) && _newOrder.Enabled && _newOrder.Visible)
        {
            _newOrder.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static void AddColumn(DataGridView grid, string name, int weight, Font? font = null,
                                  DataGridViewContentAlignment align =
                                      DataGridViewContentAlignment.MiddleLeft) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body, Alignment = align }
        });

    private void BuildOrderColumns()
    {
        const DataGridViewContentAlignment right = DataGridViewContentAlignment.MiddleRight;

        _grid.Columns.Clear();
        AddColumn(_grid, "number", 11, UiTheme.MonospaceBold);
        AddColumn(_grid, "priority", 12, UiTheme.SmallBold);
        AddColumn(_grid, "supplier", 17);
        AddColumn(_grid, "date", 8, UiTheme.Monospace);
        AddColumn(_grid, "expected", 9, UiTheme.Monospace);
        AddColumn(_grid, "status", 11, UiTheme.SmallBold);
        AddColumn(_grid, "lines", 5, UiTheme.Monospace, right);
        AddColumn(_grid, "ht", 10, UiTheme.Monospace, right);
        AddColumn(_grid, "ttc", 10, UiTheme.Monospace, right);
        AddColumn(_grid, "raisedBy", 10, UiTheme.Small);
        AddColumn(_grid, "approvedBy", 10, UiTheme.Small);
    }

    private void BuildLineColumns()
    {
        const DataGridViewContentAlignment right = DataGridViewContentAlignment.MiddleRight;

        _lines.Columns.Clear();
        AddColumn(_lines, "line", 4, UiTheme.Monospace, right);
        AddColumn(_lines, "item", 9, UiTheme.Monospace);
        AddColumn(_lines, "description", 26);
        AddColumn(_lines, "ordered", 8, UiTheme.Monospace, right);
        AddColumn(_lines, "received", 8, UiTheme.Monospace, right);
        AddColumn(_lines, "outstanding", 8, UiTheme.MonospaceBold, right);
        AddColumn(_lines, "unit", 6, UiTheme.Small);
        AddColumn(_lines, "price", 9, UiTheme.Monospace, right);
        AddColumn(_lines, "discount", 6, UiTheme.Monospace, right);
        AddColumn(_lines, "vat", 5, UiTheme.Monospace, right);
        AddColumn(_lines, "total", 10, UiTheme.Monospace, right);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyOpen.CheckedChanged += async (_, _) => await LoadAsync();

        _grid.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadLinesAsync());
        _grid.CellFormatting += OnOrderFormatting;
        StockGrid.Pills(_grid, "status", tag => tag is PurchaseOrderRow row
                            ? StockPill.Of(PurchaseOrderScreenRules.StatusTone(row.Status)) : null);
        StockGrid.Pills(_grid, "priority", tag => tag is PurchaseOrderRow row
                            ? StockPill.Of(PurchaseOrderScreenRules.PriorityTone(row)) : null);
        _search.Changed += (_, _) => StockGrid.Filter(_grid, _search.Query);
        _lines.SelectionChanged += (_, _) => RefreshButtons();

        _newOrder.Click += async (_, _) => await RunActionAsync(CreateOrderAsync);
        _addLine.Click += async (_, _) => await RunActionAsync(() => AddLineAsync(SelectedOrder?.SupplierId));
        _printPdf.Click += async (_, _) => await PrintPdfAsync();
        _savePdf.Click += async (_, _) => await SavePdfAsync();
        _approve.Click += async (_, _) => await RunActionAsync(ApproveAsync);
        _send.Click += async (_, _) => await RunActionAsync(SendAsync);
        _receive.Click += async (_, _) => await RunActionAsync(ReceiveAsync);
        _cancel.Click += async (_, _) => await RunActionAsync(CancelAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Inventory.ManagePurchaseOrders, _newOrder, _addLine, _send, _cancel);
        PermissionGate.Require(Permissions.Inventory.ApprovePurchaseOrders, _approve);
        PermissionGate.Require(Permissions.Inventory.Receive, _receive);
    }

    private void OnOrderFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not PurchaseOrderRow row) return;

        // The priority shows on its pill; a late delivery on its date. A whole row in colour is
        // kept for the critical cases, as the design system has it.
        switch (PurchaseOrderScreenRules.Ink(row, _grid.Columns[e.ColumnIndex].Name))
        {
            case StockInk.CriticalBold:
                e.CellStyle!.ForeColor = UiTheme.Critical;
                break;
            case StockInk.AbnormalBold:
                e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
                e.CellStyle.ForeColor = UiTheme.Abnormal;
                break;
            case StockInk.Muted:
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
                break;
        }
    }

    protected override void ApplyTexts()
    {
        _title.Text = T("PurchaseOrders.Title");
        _search.Placeholder = T("StocksUi.SearchOrders");
        _headerMoreButton.Text = _linesMoreButton.Text = T("ResultEntry.MoreActions");
        _onlyOpen.Text = T("PurchaseOrders.OnlyOpen");
        _refresh.Text = T("Action.Refresh");
        _newOrder.Text = T("PurchaseOrders.NewOrder");
        _addLine.Text = T("PurchaseOrders.AddLine");
        _approve.Text = T("PurchaseOrders.Approve");
        _send.Text = T("PurchaseOrders.SendToSupplier");
        _printPdf.Text = T("PurchaseOrders.PrintPdf");
        _savePdf.Text = T("PurchaseOrders.SavePdf");
        _receive.Text = T("PurchaseOrders.ReceiveLine");
        _cancel.Text = T("PurchaseOrders.CancelOrder");

        if (_grid.Columns.Count > 0)
        {
            _grid.Column("number").HeaderText = T("PurchaseOrders.Number");
            _grid.Column("priority").HeaderText = T("PurchaseOrders.Priority");
            _grid.Column("supplier").HeaderText = T("Inventory.Supplier");
            _grid.Column("date").HeaderText = T("Field.Date");
            _grid.Column("expected").HeaderText = T("PurchaseOrders.ExpectedDelivery");
            _grid.Column("status").HeaderText = T("Field.Status");
            _grid.Column("lines").HeaderText = T("PurchaseOrders.Lines");
            _grid.Column("ht").HeaderText = T("PurchaseOrders.TotalExcludingVat");
            _grid.Column("ttc").HeaderText = T("PurchaseOrders.TotalIncludingVat");
            _grid.Column("raisedBy").HeaderText = T("PurchaseOrders.RaisedBy");
            _grid.Column("approvedBy").HeaderText = T("PurchaseOrders.ApprovedBy");
        }

        if (_lines.Columns.Count > 0)
        {
            _lines.Column("line").HeaderText = "#";
            _lines.Column("item").HeaderText = T("Field.Code");
            _lines.Column("description").HeaderText = T("Inventory.Item");
            _lines.Column("ordered").HeaderText = T("PurchaseOrders.Ordered");
            _lines.Column("received").HeaderText = T("PurchaseOrders.Received");
            _lines.Column("outstanding").HeaderText = T("PurchaseOrders.Outstanding");
            _lines.Column("unit").HeaderText = T("Field.Unit");
            _lines.Column("price").HeaderText = T("PurchaseOrders.UnitPrice");
            _lines.Column("discount").HeaderText = T("PurchaseOrders.Discount");
            _lines.Column("vat").HeaderText = T("PurchaseOrders.Vat");
            _lines.Column("total").HeaderText = T("PurchaseOrders.TotalExcludingVat");
        }

        RenderHeading();
        if (_rows.Count > 0) Render();
        if (_lineRows.Count > 0) RenderLines();
        RefreshButtons();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(null, ct);

    private PurchaseOrderRow? SelectedOrder => _grid.CurrentRow?.Tag as PurchaseOrderRow;
    private PurchaseOrderLineRow? SelectedLine => _lines.CurrentRow?.Tag as PurchaseOrderLineRow;

    private static string Amount(decimal value) => PurchaseOrderScreenRules.Amount(value);
    // Shown, never read back: written with the decimal comma of the language, not « 2.5 ».
    private static string Quantity(decimal value) => PurchaseOrderScreenRules.Quantity(value);

    private async Task LoadAsync(Guid? keep = null, CancellationToken ct = default)
    {
        try
        {
            keep ??= SelectedOrder?.Id;

            var result = await _orders.GetOrdersAsync(_onlyOpen.Checked, ct);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _rows = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading purchase orders failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)));
        }
    }

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        // The priority orders still to be dealt with come first: they are the ones that stop a
        // consumable from running out.
        foreach (var row in PurchaseOrderScreenRules.Ordered(_rows))
        {
            var index = _grid.Rows.Add(
                row.Number,
                PurchaseOrderScreenRules.PriorityKey(row) is { } priority ? T(priority) : string.Empty,
                row.SupplierName,
                PurchaseOrderScreenRules.Date(row.OrderDate),
                PurchaseOrderScreenRules.Expected(row),
                T(PurchaseOrderScreenRules.StatusKey(row.Status)),
                row.LineCount,
                Amount(row.TotalExcludingVat),
                Amount(row.TotalIncludingVat),
                row.RaisedByName,
                row.ApprovedByName ?? string.Empty);

            _grid.Rows[index].Tag = row;
            if (row.Id == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        RenderHeading();

        if (keep is null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
        StockGrid.Filter(_grid, _search.Query);

        // The grid makes its first row current as it is added, before the row carries its
        // order: that selection loaded nothing, and setting the same row current again raises
        // no event. The lines of the order now shown are loaded here, once the rows are filled.
        _ = _selectionLoads.RunAsync(() => LoadLinesAsync());
    }

    private void RenderHeading()
    {
        _count.Text = PurchaseOrderScreenRules.Count(_rows, T);
    }

    private async Task LoadLinesAsync()
    {
        try
        {
            if (SelectedOrder is not { } order)
            {
                _lineRows = [];
                RenderLines();
                return;
            }

            var result = await _orders.GetLinesAsync(order.Id);
            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            if (SelectedOrder?.Id != order.Id) return;

            _lineRows = result.Value;
            RenderLines();
        }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading purchase order lines failed.");
        }
    }

    private void RenderLines()
    {
        _lines.SuspendLayout();
        _lines.Rows.Clear();

        foreach (var line in _lineRows)
        {
            var index = _lines.Rows.Add(
                line.LineNumber,
                line.ItemCode,
                line.Description,
                Quantity(line.QuantityOrdered),
                Quantity(line.QuantityReceived),
                Quantity(line.QuantityOutstanding),
                line.UnitOfMeasure,
                Amount(line.UnitPriceMad),
                PurchaseOrderScreenRules.Rate(line.DiscountRate, blankWhenZero: true),
                PurchaseOrderScreenRules.Rate(line.VatRate),
                Amount(line.LineTotalExcludingVat));

            _lines.Rows[index].Tag = line;
        }

        _lines.ResumeLayout(performLayout: true);

        // The first line still awaiting goods is the one a receipt most likely concerns.
        var firstOutstanding = _lines.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(r => r.Tag is PurchaseOrderLineRow { QuantityOutstanding: > 0m });
        if (firstOutstanding is not null) _lines.CurrentCell = firstOutstanding.Cells[0];

        var order = SelectedOrder;
        _linesCard.Title.Text = T("PurchaseOrders.Lines");
        _linesCard.Note.Text = PurchaseOrderScreenRules.LinesNote(order, T);

        var totals = order is null ? default : PurchaseOrderScreenRules.Totals(order);
        _totals.Text = order is null
            ? string.Empty
            : T("PurchaseOrders.TotalsLine", Amount(totals.ExcludingVat), Amount(totals.Vat), Amount(totals.IncludingVat));

        RefreshButtons();
    }

    /// <summary>
    /// Runs one write with every write button held disabled until it ends, and the view flagged busy
    /// so that leaving the screen does not dispose its context under it. The order is e-mailed before
    /// it is saved as sent: a second click during a slow send mailed the supplier twice, and closing
    /// the screen mid-send left the order approved, to be sent again later.
    /// </summary>
    private async Task RunActionAsync(Func<Task> action)
    {
        var ran = await RunBusyAsync(async () =>
        {
            RefreshButtons();
            await action();
        });

        if (ran && !IsDisposed) RefreshButtons();
    }

    private void RefreshButtons()
    {
        // Nothing that writes is offered while a write is in flight (see RunActionAsync).
        var order = IsBusy ? null : SelectedOrder;

        var actions = PurchaseOrderScreenRules.Actions(p => PermissionGate.Allows(p), PermissionGate.User?.UserId ?? Guid.Empty, order, SelectedLine);

        _newOrder.Enabled = !IsBusy;
        _addLine.Enabled = actions.AddLine;
        // The author of an order cannot approve it (the service refuses it): greyed out for them,
        // with the reason on the button rather than a refusal after the click.
        _approve.Enabled = actions.Approve;
        _tips.SetToolTip(_approve, actions.OwnOrder ? T("PurchaseOrders.ApproveOwnOrderTip") : null);
        _send.Enabled = actions.Send;
        _receive.Enabled = actions.Receive;
        _cancel.Enabled = actions.Cancel;
        _printPdf.Enabled = _savePdf.Enabled = actions.Print;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task CreateOrderAsync()
    {
        try
        {
            var suppliers = await _orders.GetSuppliersAsync();
            if (suppliers.IsFailure) { _dialogs.ShowFailure(suppliers, _newOrder.Text); return; }

            if (suppliers.Value.Count == 0)
            {
                _dialogs.Warn(T("PurchaseOrders.NoApprovedSupplier"), _newOrder.Text);
                return;
            }

            using var dialog = new PurchaseOrderDialog(Localizer, _dialogs, suppliers.Value);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

            if (_reception is not null
                && await _reception.RegistrationWarningAsync(dialog.Request.SupplierId) is { } warning
                && !_dialogs.Confirm($"{Localizer.Describe(warning)}\n\n{T("Supplier.ContinueAnyway")}", _newOrder.Text))
                return;

            var result = await _orders.CreateAsync(dialog.Request);
            if (result.IsFailure) { _dialogs.ShowFailure(result, _newOrder.Text); return; }

            await LoadAsync();
            var created = _grid.Rows.Cast<DataGridViewRow>()
                               .FirstOrDefault(r => r.Tag is PurchaseOrderRow o && o.Number == result.Value);
            if (created is not null) _grid.CurrentCell = created.Cells[0];

            // An order without lines can do nothing; the next step is obvious, so it is offered.
            await AddLineAsync(dialog.Request.SupplierId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Creating a purchase order failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _newOrder.Text);
        }
    }

    private async Task AddLineAsync(Guid? supplierId = null)
    {
        if (SelectedOrder is not { Status: PurchaseOrderStatus.Draft } order) return;

        var items = await _orders.GetOrderableItemsAsync();
        if (items.IsFailure) { _dialogs.ShowFailure(items, _addLine.Text); return; }

        using var dialog = new PurchaseOrderLineDialog(Localizer, _dialogs, order.Number, items.Value,
                                                       supplierId);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var result = await _orders.AddLineAsync(order.Id, dialog.Request);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _addLine.Text); return; }

        await LoadAsync(order.Id);
    }

    private async Task StepAsync(Func<PurchaseOrderRow, Task<Result>> step, string title)
    {
        if (SelectedOrder is not { } order) return;

        var result = await step(order);
        if (result.IsFailure) { _dialogs.ShowFailure(result, title); return; }

        await LoadAsync(order.Id);
    }

    /// <summary>Approval, with a warning when the supplier's AMMPS / DMP registration has lapsed.</summary>
    private async Task ApproveAsync()
    {
        if (SelectedOrder is not { } order) return;

        if (_reception is not null
            && await _reception.RegistrationWarningForOrderAsync(order.Id) is { } warning
            && !_dialogs.Confirm($"{Localizer.Describe(warning)}\n\n{T("Supplier.ContinueAnyway")}", _approve.Text))
            return;

        await StepAsync(o => _orders.ApproveAsync(o.Id), _approve.Text);
    }

    /// <summary>
    /// Sends the approved order to the supplier: by e-mail with the PDF attached when the laboratory
    /// has a mail account and the supplier an address, otherwise by hand. In the second case the
    /// order is only marked sent once the operator confirms having sent it themselves.
    /// </summary>
    private async Task SendAsync()
    {
        if (SelectedOrder is not { Status: PurchaseOrderStatus.Approved } order) return;

        try
        {
            string manualReason;

            if (_documents.CanEmail)
            {
                var pdf = await _documents.RenderAsync(order.Id);
                if (pdf.IsFailure) { _dialogs.ShowFailure(pdf, _send.Text); return; }

                if (PurchaseOrderScreenRules.SendRoute(_documents.CanEmail, pdf.Value.SupplierEmail) == PurchaseOrderSendRoute.Email
                    && pdf.Value.SupplierEmail is { } address)
                {
                    if (!_dialogs.Confirm(T("PurchaseOrders.EmailQuestion", order.Number, order.SupplierName, address),
                                          _send.Text))
                        return;

                    var emailed = await _documents.EmailAsync(order.Id);
                    if (emailed.IsSuccess)
                    {
                        _dialogs.Info(T("PurchaseOrders.EmailSent", order.Number, address), _send.Text);
                        await LoadAsync(order.Id);
                        return;
                    }

                    // The mail did not leave: the order stays approved, and the operator may still
                    // send it by hand.
                    manualReason = Describe(emailed);
                }
                else
                {
                    manualReason = PurchaseOrderScreenRules.ManualReason(canEmail: true, order.SupplierName, T);
                }
            }
            else
            {
                manualReason = PurchaseOrderScreenRules.ManualReason(canEmail: false, order.SupplierName, T);
            }

            if (!_dialogs.Confirm(PurchaseOrderScreenRules.ManualQuestion(manualReason, order, T), _send.Text))
                return;

            await StepAsync(o => _orders.SendAsync(o.Id), _send.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sending purchase order {Number} failed.", order.Number);
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _send.Text);
        }
    }

    private async Task PrintPdfAsync()
    {
        if (SelectedOrder is not { } order) return;

        try
        {
            var pdf = await _documents.RenderAsync(order.Id);
            if (pdf.IsFailure) { _dialogs.ShowFailure(pdf, _printPdf.Text); return; }

            var opened = await _output.OpenAsync(pdf.Value.Content, "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _printPdf.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Rendering purchase order {Number} failed.", order.Number);
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _printPdf.Text);
        }
    }

    /// <summary>Saves the PDF where the operator chooses, to attach it to a mail of their own.</summary>
    private async Task SavePdfAsync()
    {
        if (SelectedOrder is not { } order) return;

        try
        {
            var pdf = await _documents.RenderAsync(order.Id);
            if (pdf.IsFailure) { _dialogs.ShowFailure(pdf, _savePdf.Text); return; }

            using var dialog = new SaveFileDialog
            {
                Title = UserDialogs.CaptionOf(_savePdf.Text, _savePdf.Text),
                FileName = pdf.Value.FileName,
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            await File.WriteAllBytesAsync(dialog.FileName, pdf.Value.Content);
            _dialogs.Info(T("PurchaseOrders.PdfSaved", dialog.FileName), _savePdf.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{T(FailureMessages.KeyFor(ex))}\n{ex.Message}", _savePdf.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving purchase order {Number} failed.", order.Number);
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), _savePdf.Text);
        }
    }

    private async Task ReceiveAsync()
    {
        if (SelectedOrder is not { } order || SelectedLine is not { } line) return;

        var condition = _reception is null
            ? StorageCondition.RoomTemperature
            : await _reception.StorageConditionOfAsync(line.StockItemId);

        using var dialog = new PurchaseOrderReceiptDialog(Localizer, _dialogs, order.Number, line, condition,
            _reception is null ? null : gtin => _reception.FindByGtinAsync(gtin));
        var answer = dialog.ShowDialog(this);

        // Refused at the door: nothing is received on the order, the refusal and its NC are kept.
        if (answer == DialogResult.Abort && dialog.RefusalReason is { } reason && _reception is not null)
        {
            var refused = await _reception.RefuseDeliveryAsync(new RefuseDeliveryRequest
            {
                PurchaseOrderId = order.Id,
                StockItemId = line.StockItemId,
                Reason = reason,
                ArrivalTemperatureCelsius = dialog.Temperature,
                DeliveryNoteReference = dialog.DeliveryNote,
                LotNumber = dialog.LotNumber
            });
            if (refused.IsFailure) { _dialogs.ShowFailure(refused, T("Reception.Refuse")); return; }

            _dialogs.Info(T("Reception.Refused", refused.Value), T("Reception.Refuse"));
            return;
        }

        if (answer != DialogResult.OK || dialog.Request is null) return;

        var result = await _orders.ReceiveLineAsync(order.Id, dialog.Request);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _receive.Text); return; }

        _dialogs.Info(T("Inventory.ReceivedQuarantined"), _receive.Text);
        await LoadAsync(order.Id);
    }

    private async Task CancelAsync()
    {
        if (SelectedOrder is not { } order) return;

        var reason = _dialogs.PromptForReason(T("PurchaseOrders.CancelPrompt", order.Number), _cancel.Text, maxLength: 1000);
        if (reason is null) return;

        await StepAsync(o => _orders.CancelAsync(o.Id, reason), _cancel.Text);
    }
}

/// <summary>A new order: who it goes to and when the goods are wanted. Lines come next.</summary>
internal sealed class PurchaseOrderDialog : MasterDataEditor
{
    private readonly ComboBox _supplier;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _expected;
    private readonly TextBox _supplierReference, _notes;

    internal CreatePurchaseOrderRequest? Request { get; private set; }

    internal PurchaseOrderDialog(ILocalizer localizer, IUserDialogs dialogs,
                                 IReadOnlyList<SupplierOption> suppliers)
        : base(localizer, dialogs, "PurchaseOrders.NewOrder", null, 520, 300)
    {
        _supplier = Lookup("Inventory.Supplier", 20, 14, 480,
                           suppliers.Select(s => (s.Id, $"{s.Code} — {s.Name}")), null,
                           allowNone: false);

        _expected = OptionalDate("PurchaseOrders.ExpectedDelivery", 20, 68,
                                 DateOnly.FromDateTime(DateTime.Today.AddDays(7)), min: DateTime.Today);

        _supplierReference = TextField("PurchaseOrders.SupplierReference", 240, 68, 260, null, latin: true);
        _notes = TextField("Field.Notes", 20, 128, 480);
        // Bounded to the columns: beyond, the save failed with « Erreur inattendue ».
        _supplierReference.MaxLength = PurchaseOrderScreenRules.SupplierReferenceMaxLength;
        _notes.MaxLength = PurchaseOrderScreenRules.NotesMaxLength;

        Notice(20, 182, 480, 46, "PurchaseOrders.ApprovalNotice");

        Buttons(248, 520);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.PurchaseOrder");
    }

    protected override void Accept()
    {
        var expected = DateOrNull(_expected.Toggle, _expected.Picker);
        var (refusal, request) = PurchaseOrderScreenRules.CheckOrder(new PurchaseOrderForm
        {
            SupplierId = SelectedId(_supplier),
            HasExpectedDelivery = expected is not null,
            ExpectedDelivery = expected ?? default,
            SupplierReference = _supplierReference.Text,
            Notes = _notes.Text
        }, DateOnly.FromDateTime(DateTime.Today));
        if (refusal is not null)
        {
            Dialogs.Warn(Localizer.Format(refusal.Key, refusal.Value ?? string.Empty), Text);
            return;
        }

        Request = request;

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>One line: the article, how many, at what price.</summary>
internal sealed class PurchaseOrderLineDialog : MasterDataEditor
{
    private readonly IReadOnlyList<OrderableItem> _items;
    private readonly Guid? _supplierId;
    private readonly ComboBox _item;
    private readonly TextBox _quantity, _price, _discount, _vat;
    private readonly Label _unit = new();

    internal AddPurchaseOrderLineRequest? Request { get; private set; }

    internal PurchaseOrderLineDialog(ILocalizer localizer, IUserDialogs dialogs, string number,
                                     IReadOnlyList<OrderableItem> items, Guid? supplierId)
        : base(localizer, dialogs, "PurchaseOrders.AddLine", number, 560, 260)
    {
        _supplierId = supplierId;
        // Articles normally bought from this supplier first: the usual case, and the short list.
        _items = PurchaseOrderScreenRules.ItemsFor(items, supplierId);

        var language = localizer.CurrentLanguage;
        _item = Lookup("Inventory.Item", 20, 14, 520,
                       _items.Select(i => (i.Id, PurchaseOrderScreenRules.ItemLabel(i, language))), null,
                       allowNone: false);

        _quantity = TextField("Field.Quantity", 20, 68, 110, null, latin: true, mono: true, right: true);

        _unit.Font = UiTheme.Small;
        _unit.ForeColor = UiTheme.TextSecondary;
        _unit.AutoSize = true;
        _unit.Location = new Point(134, 92);
        Controls.Add(_unit);

        // The price box takes the room the two rates do not need: its caption, in Arabic, ran into
        // the discount's over 120 pixels.
        _price = TextField("PurchaseOrders.UnitPrice", 200, 68, 160, null, latin: true, mono: true, right: true);
        _discount = TextField("PurchaseOrders.DiscountPercent", 380, 68, 70, "0", latin: true, mono: true, right: true);
        _vat = TextField("PurchaseOrders.VatPercent", 470, 68, 70, "20", latin: true, mono: true, right: true);
        // « 10 % » in the discount was ordered with no discount, and a slip in the VAT at 20 %.
        Numeric(EditorValues.MeasureMaxLength, _quantity, _discount, _vat);
        Numeric(EditorValues.AmountMaxLength, _price);

        Notice(20, 124, 520, 46, "PurchaseOrders.LineNotice");

        _item.SelectedIndexChanged += (_, _) => FillDefaults();
        FillDefaults();

        Buttons(208, 560);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.PurchaseOrderLine");
    }

    private OrderableItem? Current =>
        SelectedId(_item) is { } id ? _items.FirstOrDefault(i => i.Id == id) : null;

    /// <summary>
    /// The reorder quantity, the last list price and, from the item's usual supplier, its usual
    /// discount, as starting values.
    /// </summary>
    private void FillDefaults()
    {
        if (Current is not { } item) return;

        _unit.Text = item.UnitOfMeasure;
        // The discount was agreed with the preferred supplier: another supplier starts from none; a
        // captive reagent ordered from its contract's supplier is bought at the contracted price.
        var form = new PurchaseOrderLineForm();
        PurchaseOrderScreenRules.Defaults(form, item, _supplierId);
        _quantity.Text = form.Quantity;
        _price.Text = form.UnitPrice;
        _discount.Text = form.DiscountPercent;
    }

    protected override void Accept()
    {
        if (Current is not { } item) return;

        var (refusal, request) = PurchaseOrderScreenRules.CheckLine(new PurchaseOrderLineForm
        {
            StockItemId = item.Id,
            Quantity = _quantity.Text,
            UnitPrice = _price.Text,
            DiscountPercent = _discount.Text,
            VatPercent = _vat.Text
        });
        if (refusal is not null)
        {
            Dialogs.Warn(Localizer.Format(refusal.Key, refusal.Value ?? string.Empty), Text);
            (refusal.Field switch
            {
                PurchaseOrderScreenRules.PriceField => _price,
                PurchaseOrderScreenRules.DiscountField => _discount,
                PurchaseOrderScreenRules.VatField => _vat,
                _ => _quantity
            }).Focus();
            return;
        }

        Request = request;

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A delivery against one line, received straight into stock as a lot.</summary>
internal sealed class PurchaseOrderReceiptDialog : MasterDataEditor
{
    private readonly TextBox _quantity, _lot, _deliveryNote, _location;
    private readonly DateTimePicker _expiry;

    // Reception checks and GS1 scan.
    private readonly PurchaseOrderLineRow _line;
    private readonly StorageCondition _condition;
    private readonly ReceptionChecksPanel _checks;
    private readonly Func<string, Task<GtinMatch?>>? _findByGtin;

    internal ReceivePurchaseOrderLineRequest? Request { get; private set; }

    /// <summary>Set when the whole delivery was refused; the dialog then closes with Abort.</summary>
    internal string? RefusalReason { get; private set; }
    internal decimal? Temperature => _checks.Temperature;
    internal string? DeliveryNote => Blank(_deliveryNote);
    internal string? LotNumber => Blank(_lot);

    internal PurchaseOrderReceiptDialog(ILocalizer localizer, IUserDialogs dialogs, string number,
                                        PurchaseOrderLineRow line,
                                        StorageCondition condition = StorageCondition.RoomTemperature,
                                        Func<string, Task<GtinMatch?>>? findByGtin = null)
        : base(localizer, dialogs, "PurchaseOrders.ReceiveLine", $"{number} · {line.ItemCode}", 520, 520)
    {
        _line = line;
        _condition = condition;
        _findByGtin = findByGtin;

        _quantity = TextField("Field.Quantity", 20, 14, 120,
                              line.QuantityOutstanding.ToString("0.##", CultureInfo.InvariantCulture),
                              latin: true, mono: true, right: true);
        _lot = TextField("Inventory.LotNumber", 160, 14, 160, null, latin: true, mono: true);

        Label("Inventory.Expiry", 340, 14);
        _expiry = new DateTimePicker
        {
            Bounds = new Rectangle(340, 32, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MinDate = DateTime.Today,
            Value = DateTime.Today.AddMonths(12)
        };
        Controls.Add(_expiry);

        _deliveryNote = TextField("Inventory.DeliveryNote", 20, 68, 230, null, latin: true);
        _location = TextField("Inventory.DefaultLocation", 270, 68, 230);
        _lot.MaxLength = InventoryScreenRules.LotNumberMaxLength;
        _deliveryNote.MaxLength = InventoryScreenRules.DeliveryNoteMaxLength;
        _location.MaxLength = InventoryScreenRules.StorageMaxLength;
        Numeric(EditorValues.MeasureMaxLength, _quantity);

        _checks = new ReceptionChecksPanel(localizer, condition) { Location = new Point(20, 122) };
        _checks.Scanned += async data => await OnScannedAsync(data);
        Controls.Add(_checks);

        Notice(20, 124 + ReceptionChecksPanel.PanelHeight, 480, 40, "Inventory.ReceivedQuarantined", UiTheme.Abnormal);

        Buttons(468, 520);

        var refuse = UiTheme.SecondaryButton(localizer["Reception.Refuse"]);
        refuse.Bounds = new Rectangle(20, 420, 480, UiTheme.ButtonHeight);
        refuse.Enabled = findByGtin is not null;
        refuse.Click += (_, _) =>
        {
            var reason = Dialogs.PromptForReason(Localizer["Reception.RefusePrompt"],
                                                 Localizer["Reception.Refuse"], minimumLength: InventoryScreenRules.RefusalMinLength,
                                                 maxLength: InventoryScreenRules.RefusalMaxLength);
            if (reason is null) return;
            RefusalReason = reason;
            DialogResult = DialogResult.Abort;
            Close();
        };
        Controls.Add(refuse);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.PurchaseOrderReceipt", refuse);
    }

    /// <summary>A scan fills the lot and expiry, and says so when the box is not the line's article.</summary>
    private async Task OnScannedAsync(Gs1Data data)
    {
        if (data.LotNumber is { } lot) _lot.Text = lot;

        if (data.ExpiryDate is { } expiry)
        {
            var date = expiry.ToDateTime(TimeOnly.MinValue);
            if (date < _expiry.MinDate) Dialogs.Warn(Localizer.Format("Lot_AlreadyExpired", expiry), Text);
            else _expiry.Value = date;
        }

        if (data.Gtin is not { } gtin || _findByGtin is null) return;

        var match = await _findByGtin(gtin);

        if (PurchaseOrderScreenRules.ScanMismatch(match, _line, gtin, Localizer.Format) is not { } said) return;
        if (match is null) Dialogs.Info(said, Text);
        else Dialogs.Warn(said, Text);
    }

    protected override void Accept()
    {
        var form = new ReceiveLotForm
        {
            Quantity = _quantity.Text,
            LotNumber = _lot.Text,
            ExpiryDate = DateOnly.FromDateTime(_expiry.Value.Date),
            DeliveryNote = _deliveryNote.Text,
            StorageLocation = _location.Text
        };
        _checks.Fill(form);

        var verdict = PurchaseOrderScreenRules.CheckReceipt(form, _line, _condition, DateOnly.FromDateTime(DateTime.Today));
        if (verdict.Refusal is { } refusal)
        {
            if (refusal.Field == InventoryScreenRules.TemperatureField)
            {
                _checks.Confirm(new ArrivalVerdict(refusal, null, false, false), Dialogs, Text);
                return;
            }

            Dialogs.Warn(Localizer.Format(refusal.Key, refusal.Value ?? string.Empty), Text);
            (refusal.Field == InventoryScreenRules.LotNumberField ? _lot : _quantity).Focus();
            return;
        }

        // The ambient warning, then the non-conformity a failed check opens, asked about first.
        var check = _checks.Confirm(new ArrivalVerdict(null, verdict.Request!.Reception, verdict.AmbientWarning,
                                                       verdict.NeedsNonConformity), Dialogs, Text);
        if (check is null) return;

        Request = verdict.Request;

        DialogResult = DialogResult.OK;
        Close();
    }
}
