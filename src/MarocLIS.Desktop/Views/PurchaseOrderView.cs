using System.Globalization;
using MarocLIS.Domain.Common;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
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
    private readonly PurchaseOrderService _orders;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PurchaseOrderView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _lines = UiTheme.Grid();
    private readonly Label _heading = new();
    private readonly Label _linesHeading = new();
    private readonly CheckBox _onlyOpen = new();

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _newOrder = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _addLine = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _approve = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _send = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _receive = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    private readonly Label _totals = new();

    private IReadOnlyList<PurchaseOrderRow> _rows = [];
    private IReadOnlyList<PurchaseOrderLineRow> _lineRows = [];

    public PurchaseOrderView(PurchaseOrderService orders, IUserDialogs dialogs, ILocalizer localizer,
                             ICultureManager cultureManager, ILogger<PurchaseOrderView>? logger = null)
        : base(localizer, cultureManager)
    {
        _orders = orders;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.PurchaseOrders");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        _onlyOpen.Font = UiTheme.Small;
        _onlyOpen.ForeColor = UiTheme.TextSecondary;
        _onlyOpen.AutoSize = true;
        _onlyOpen.Location = new Point(0, 32);
        _onlyOpen.Checked = true;

        _refresh.Bounds = new Rectangle(300, 26, 110, UiTheme.ButtonHeight);
        _newOrder.Bounds = new Rectangle(420, 26, 190, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _onlyOpen, _refresh, _newOrder]);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 280,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        BuildOrderColumns();
        BuildLineColumns();

        split.Panel1.Controls.Add(_grid);

        _linesHeading.Font = UiTheme.SectionTitle;
        _linesHeading.ForeColor = UiTheme.TextPrimary;
        _linesHeading.Dock = DockStyle.Top;
        _linesHeading.Height = 26;

        split.Panel2.Controls.Add(_lines);
        split.Panel2.Controls.Add(_linesHeading);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = UiTheme.Surface };

        _totals.Font = UiTheme.SmallBold;
        _totals.ForeColor = UiTheme.TextSecondary;
        _totals.AutoSize = false;
        _totals.Bounds = new Rectangle(0, 4, 1200, 20);

        var x = 0;
        foreach (var button in new[] { _addLine, _approve, _send, _receive, _cancel })
        {
            button.Bounds = new Rectangle(x, 36, 170, UiTheme.ButtonHeight);
            x += 180;
        }

        footer.Controls.AddRange([_totals, _addLine, _approve, _send, _receive, _cancel]);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(bar);
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
        AddColumn(_grid, "supplier", 20);
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

        _grid.SelectionChanged += async (_, _) => await LoadLinesAsync();
        _grid.CellFormatting += OnOrderFormatting;
        _lines.SelectionChanged += (_, _) => RefreshButtons();

        _newOrder.Click += async (_, _) => await CreateOrderAsync();
        _addLine.Click += async (_, _) => await AddLineAsync();
        _approve.Click += async (_, _) => await StepAsync(o => _orders.ApproveAsync(o.Id), _approve.Text);
        _send.Click += async (_, _) => await SendAsync();
        _receive.Click += async (_, _) => await ReceiveAsync();
        _cancel.Click += async (_, _) => await CancelAsync();
    }

    private void OnOrderFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not PurchaseOrderRow row) return;

        if (row.IsOverdue)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
        else if (row.Status is PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Received)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
    }

    protected override void ApplyTexts()
    {
        _onlyOpen.Text = T("PurchaseOrders.OnlyOpen");
        _refresh.Text = T("Action.Refresh");
        _newOrder.Text = T("PurchaseOrders.NewOrder");
        _addLine.Text = T("PurchaseOrders.AddLine");
        _approve.Text = T("PurchaseOrders.Approve");
        _send.Text = T("PurchaseOrders.Send");
        _receive.Text = T("PurchaseOrders.ReceiveLine");
        _cancel.Text = T("PurchaseOrders.CancelOrder");

        if (_grid.Columns.Count > 0)
        {
            _grid.Column("number").HeaderText = T("PurchaseOrders.Number");
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

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private static string Quantity(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading purchase orders failed.");
            _dialogs.Warn(T("Message.UnexpectedError"));
        }
    }

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var index = _grid.Rows.Add(
                row.Number,
                row.SupplierName,
                row.OrderDate.ToString("dd/MM/yy"),
                row.ExpectedDeliveryDate is { } expected
                    ? row.IsOverdue ? $"! {expected:dd/MM/yy}" : expected.ToString("dd/MM/yy")
                    : string.Empty,
                T($"PurchaseOrderStatus.{row.Status}"),
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
        if (_grid.Rows.Count == 0)
        {
            _lineRows = [];
            RenderLines();
        }
    }

    private void RenderHeading()
    {
        var overdue = _rows.Count(r => r.IsOverdue);
        var toApprove = _rows.Count(r => r.Status == PurchaseOrderStatus.Draft && r.LineCount > 0);

        var text = $"{T("PurchaseOrders.Title")} — {_rows.Count}";
        if (toApprove > 0) text += $" · {T("PurchaseOrders.ToApprove")} {toApprove}";
        if (overdue > 0) text += $" · {T("PurchaseOrders.Late")} {overdue}";
        _heading.Text = text;
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
                line.DiscountRate == 0m ? string.Empty : $"{line.DiscountRate * 100m:0.#}%",
                $"{line.VatRate * 100m:0.#}%",
                Amount(line.LineTotalExcludingVat));

            _lines.Rows[index].Tag = line;
        }

        _lines.ResumeLayout(performLayout: true);

        // The first line still awaiting goods is the one a receipt most likely concerns.
        var firstOutstanding = _lines.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(r => r.Tag is PurchaseOrderLineRow { QuantityOutstanding: > 0m });
        if (firstOutstanding is not null) _lines.CurrentCell = firstOutstanding.Cells[0];

        var order = SelectedOrder;
        _linesHeading.Text = order is null
            ? T("PurchaseOrders.Lines")
            : $"{T("PurchaseOrders.Lines")} — {order.Number} · {order.SupplierName}";

        _totals.Text = order is null
            ? string.Empty
            : T("PurchaseOrders.TotalsLine", Amount(order.TotalExcludingVat),
                Amount(order.TotalIncludingVat - order.TotalExcludingVat),
                Amount(order.TotalIncludingVat));

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var order = SelectedOrder;

        _addLine.Enabled = order?.Status == PurchaseOrderStatus.Draft;
        _approve.Enabled = order is { Status: PurchaseOrderStatus.Draft, LineCount: > 0 };
        _send.Enabled = order?.Status == PurchaseOrderStatus.Approved;
        _receive.Enabled = order?.Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent
                                          or PurchaseOrderStatus.PartiallyReceived
                           && SelectedLine is { QuantityOutstanding: > 0m };
        _cancel.Enabled = order?.Status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved
                                         or PurchaseOrderStatus.Sent;
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
            _dialogs.Warn(T("Message.UnexpectedError"), _newOrder.Text);
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

    private async Task SendAsync()
    {
        if (SelectedOrder is not { } order) return;

        if (!_dialogs.Confirm(T("PurchaseOrders.SendQuestion", order.Number, order.SupplierName),
                              _send.Text))
            return;

        await StepAsync(o => _orders.SendAsync(o.Id), _send.Text);
    }

    private async Task ReceiveAsync()
    {
        if (SelectedOrder is not { } order || SelectedLine is not { } line) return;

        using var dialog = new PurchaseOrderReceiptDialog(Localizer, _dialogs, order.Number, line);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var result = await _orders.ReceiveLineAsync(order.Id, dialog.Request);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _receive.Text); return; }

        _dialogs.Info(T("Inventory.ReceivedQuarantined"), _receive.Text);
        await LoadAsync(order.Id);
    }

    private async Task CancelAsync()
    {
        if (SelectedOrder is not { } order) return;

        var reason = _dialogs.PromptForReason(T("PurchaseOrders.CancelPrompt", order.Number), _cancel.Text);
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

        Notice(20, 182, 480, 46, "PurchaseOrders.ApprovalNotice");

        Buttons(248, 520);
    }

    protected override void Accept()
    {
        if (SelectedId(_supplier) is not { } supplierId) return;

        Request = new CreatePurchaseOrderRequest
        {
            SupplierId = supplierId,
            ExpectedDeliveryDate = DateOrNull(_expected.Toggle, _expected.Picker),
            SupplierReference = Blank(_supplierReference),
            Notes = Blank(_notes)
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>One line: the article, how many, at what price.</summary>
internal sealed class PurchaseOrderLineDialog : MasterDataEditor
{
    private readonly IReadOnlyList<OrderableItem> _items;
    private readonly ComboBox _item;
    private readonly TextBox _quantity, _price, _discount, _vat;
    private readonly Label _unit = new();

    internal AddPurchaseOrderLineRequest? Request { get; private set; }

    internal PurchaseOrderLineDialog(ILocalizer localizer, IUserDialogs dialogs, string number,
                                     IReadOnlyList<OrderableItem> items, Guid? supplierId)
        : base(localizer, dialogs, "PurchaseOrders.AddLine", number, 560, 260)
    {
        // Articles normally bought from this supplier first: the usual case, and the short list.
        _items = items.OrderBy(i => supplierId is not null && i.PreferredSupplierId == supplierId ? 0 : 1)
                      .ThenBy(i => i.Code, StringComparer.Ordinal).ToList();

        var language = localizer.CurrentLanguage;
        _item = Lookup("Inventory.Item", 20, 14, 520,
                       _items.Select(i => (i.Id, $"{i.Code} — {i.Name.For(language)}")), null,
                       allowNone: false);

        _quantity = TextField("Field.Quantity", 20, 68, 110, null, latin: true, mono: true, right: true);

        _unit.Font = UiTheme.Small;
        _unit.ForeColor = UiTheme.TextSecondary;
        _unit.AutoSize = true;
        _unit.Location = new Point(134, 92);
        Controls.Add(_unit);

        _price = TextField("PurchaseOrders.UnitPrice", 200, 68, 120, null, latin: true, mono: true, right: true);
        _discount = TextField("PurchaseOrders.DiscountPercent", 340, 68, 90, "0", latin: true, mono: true, right: true);
        _vat = TextField("PurchaseOrders.VatPercent", 450, 68, 90, "20", latin: true, mono: true, right: true);

        Notice(20, 124, 520, 46, "PurchaseOrders.LineNotice");

        _item.SelectedIndexChanged += (_, _) => FillDefaults();
        FillDefaults();

        Buttons(208, 560);
    }

    private OrderableItem? Current =>
        SelectedId(_item) is { } id ? _items.FirstOrDefault(i => i.Id == id) : null;

    /// <summary>The reorder quantity and the last price paid, as starting values.</summary>
    private void FillDefaults()
    {
        if (Current is not { } item) return;

        _unit.Text = item.UnitOfMeasure;
        _quantity.Text = item.ReorderQuantity > 0m
            ? item.ReorderQuantity.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        _price.Text = item.LastUnitPriceMad?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    protected override void Accept()
    {
        if (Current is not { } item) return;

        if (DecimalOrNull(_quantity) is not { } quantity || quantity <= 0m)
        {
            Dialogs.Warn(Localizer["Po_QuantityMustBePositive"], Text);
            _quantity.Focus();
            return;
        }

        if (DecimalOrNull(_price) is not { } price || price < 0m)
        {
            Dialogs.Warn(Localizer["Po_PriceMustNotBeNegative"], Text);
            _price.Focus();
            return;
        }

        var discount = DecimalOrNull(_discount) ?? 0m;
        var vat = DecimalOrNull(_vat) ?? 20m;

        Request = new AddPurchaseOrderLineRequest
        {
            StockItemId = item.Id,
            Quantity = quantity,
            UnitPriceMad = price,
            DiscountRate = discount / 100m,
            VatRate = vat / 100m
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A delivery against one line, received straight into stock as a lot.</summary>
internal sealed class PurchaseOrderReceiptDialog : MasterDataEditor
{
    private readonly Guid _lineId;
    private readonly TextBox _quantity, _lot, _deliveryNote, _location;
    private readonly DateTimePicker _expiry;

    internal ReceivePurchaseOrderLineRequest? Request { get; private set; }

    internal PurchaseOrderReceiptDialog(ILocalizer localizer, IUserDialogs dialogs, string number,
                                        PurchaseOrderLineRow line)
        : base(localizer, dialogs, "PurchaseOrders.ReceiveLine", $"{number} · {line.ItemCode}", 520, 300)
    {
        _lineId = line.Id;

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

        Notice(20, 128, 480, 60, "Inventory.ReceivedQuarantined", UiTheme.Abnormal);

        Buttons(248, 520);
    }

    protected override void Accept()
    {
        if (DecimalOrNull(_quantity) is not { } quantity || quantity <= 0m)
        {
            Dialogs.Warn(Localizer["Po_QuantityMustBePositive"], Text);
            _quantity.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_lot.Text))
        {
            Dialogs.Warn(Localizer["Lot_NumberRequired"], Text);
            _lot.Focus();
            return;
        }

        Request = new ReceivePurchaseOrderLineRequest
        {
            LineId = _lineId,
            Quantity = quantity,
            LotNumber = _lot.Text.Trim(),
            ExpiryDate = DateOnly.FromDateTime(_expiry.Value.Date),
            DeliveryNoteReference = Blank(_deliveryNote),
            StorageLocation = Blank(_location)
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
