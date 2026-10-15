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
/// Requests for quotation: the articles wanted, the suppliers asked, their answers side by side
/// (cheapest and fastest marked per line) and the offer chosen, which becomes a draft purchase order.
/// <para>
/// Captive reagents of an instrument contract in force cannot be put out to quotation: the request
/// editor shows them greyed with their contract, and the service refuses them in any case.
/// </para>
/// </summary>
public sealed class QuoteRequestView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly QuoteService _quotes;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<QuoteRequestView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _comparison = UiTheme.Grid();
    private readonly DataGridView _suppliers = UiTheme.Grid();
    private readonly Label _heading = new();
    private readonly Label _comparisonHeading = new();
    private readonly Label _suppliersHeading = new();
    private readonly Label _legend = new();
    private readonly CheckBox _onlyOpen = new();

    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _sendEmail = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _markSent = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _addSupplier = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _recordOffer = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _openFile = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _award = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    private IReadOnlyList<QuoteRequestRow> _rows = [];
    private QuoteRequestDetail? _detail;

    public QuoteRequestView(QuoteService quotes, IDocumentOutput output, IUserDialogs dialogs, ILocalizer localizer,
                            ICultureManager cultureManager, ILogger<QuoteRequestView>? logger = null)
        : base(localizer, cultureManager)
    {
        _quotes = quotes;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Quotes");

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
        _new.Bounds = new Rectangle(420, 26, 220, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _onlyOpen, _refresh, _new]);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 220,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        split.Panel1.Controls.Add(_grid);

        var lower = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 640,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        foreach (var heading in new[] { _comparisonHeading, _suppliersHeading })
        {
            heading.Font = UiTheme.SectionTitle;
            heading.ForeColor = UiTheme.TextPrimary;
            heading.Dock = DockStyle.Top;
            heading.Height = 26;
        }

        _legend.Font = UiTheme.Small;
        _legend.ForeColor = UiTheme.TextSecondary;
        _legend.Dock = DockStyle.Bottom;
        _legend.Height = 20;

        lower.Panel1.Controls.Add(_comparison);
        lower.Panel1.Controls.Add(_legend);
        lower.Panel1.Controls.Add(_comparisonHeading);
        lower.Panel2.Controls.Add(_suppliers);
        lower.Panel2.Controls.Add(_suppliersHeading);

        split.Panel2.Controls.Add(lower);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.Surface };

        var x = 0;
        foreach (var button in new[] { _sendEmail, _markSent, _addSupplier, _recordOffer, _openFile, _award, _cancel })
        {
            button.Bounds = new Rectangle(x, 10, 160, UiTheme.ButtonHeight);
            x += 168;
        }

        footer.Controls.AddRange([_sendEmail, _markSent, _addSupplier, _recordOffer, _openFile, _award, _cancel]);

        BuildColumns();

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(bar);
    }

    private static void AddColumn(DataGridView grid, string name, int weight, Font? font = null,
                                  DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, FillWeight = weight, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body, Alignment = align }
        });

    private void BuildColumns()
    {
        const DataGridViewContentAlignment right = DataGridViewContentAlignment.MiddleRight;

        AddColumn(_grid, "number", 11, UiTheme.MonospaceBold);
        AddColumn(_grid, "date", 8, UiTheme.Monospace);
        AddColumn(_grid, "subject", 22);
        AddColumn(_grid, "status", 10, UiTheme.SmallBold);
        AddColumn(_grid, "due", 8, UiTheme.Monospace);
        AddColumn(_grid, "items", 6, UiTheme.Monospace, right);
        AddColumn(_grid, "answers", 8, UiTheme.Monospace, right);
        AddColumn(_grid, "raisedBy", 11, UiTheme.Small);
        AddColumn(_grid, "awarded", 14);
        AddColumn(_grid, "order", 11, UiTheme.Monospace);

        AddColumn(_suppliers, "supplier", 24);
        AddColumn(_suppliers, "sent", 10, UiTheme.Small);
        AddColumn(_suppliers, "total", 12, UiTheme.Monospace, right);
        AddColumn(_suppliers, "delay", 8, UiTheme.Monospace, right);
        AddColumn(_suppliers, "valid", 10, UiTheme.Monospace);
        AddColumn(_suppliers, "missing", 8, UiTheme.Monospace, right);
        AddColumn(_suppliers, "file", 6, UiTheme.Small);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyOpen.CheckedChanged += async (_, _) => await LoadAsync();
        _grid.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadDetailAsync());
        _grid.CellFormatting += OnRequestFormatting;
        _suppliers.SelectionChanged += (_, _) => RefreshButtons();
        _suppliers.CellFormatting += OnSupplierFormatting;
        _comparison.CellFormatting += OnComparisonFormatting;

        _new.Click += async (_, _) => await RunActionAsync(CreateAsync);
        _sendEmail.Click += async (_, _) => await RunActionAsync(SendByEmailAsync);
        _markSent.Click += async (_, _) => await RunActionAsync(MarkSentAsync);
        _addSupplier.Click += async (_, _) => await RunActionAsync(AddSupplierAsync);
        _recordOffer.Click += async (_, _) => await RunActionAsync(RecordOfferAsync);
        _openFile.Click += async (_, _) => await OpenFileAsync();
        _award.Click += async (_, _) => await RunActionAsync(AwardAsync);
        _cancel.Click += async (_, _) => await RunActionAsync(CancelAsync);

        PermissionGate.Require(Permissions.Inventory.ManageQuotes, _new, _sendEmail, _markSent, _addSupplier,
                               _recordOffer, _cancel);
        // Choosing the offer drafts a purchase order: both rights are needed.
        PermissionGate.Require(Permissions.Inventory.ManagePurchaseOrders, _award);
    }

    protected override void ApplyTexts()
    {
        _onlyOpen.Text = T("Quotes.OnlyOpen");
        _refresh.Text = T("Action.Refresh");
        _new.Text = T("Quotes.New");
        _sendEmail.Text = T("Quotes.SendByEmail");
        _markSent.Text = T("Quotes.MarkSent");
        _addSupplier.Text = T("Quotes.AddSupplier");
        _recordOffer.Text = T("Quotes.RecordOffer");
        _openFile.Text = T("Quotes.OpenFile");
        _award.Text = T("Quotes.Award");
        _cancel.Text = T("Quotes.Cancel");
        _legend.Text = T("Quotes.Legend");

        _grid.Column("number").HeaderText = T("Quotes.Number");
        _grid.Column("date").HeaderText = T("Field.Date");
        _grid.Column("subject").HeaderText = T("Quotes.Subject");
        _grid.Column("status").HeaderText = T("Field.Status");
        _grid.Column("due").HeaderText = T("Quotes.ResponseDue");
        _grid.Column("items").HeaderText = T("Quotes.Items");
        _grid.Column("answers").HeaderText = T("Quotes.Answers");
        _grid.Column("raisedBy").HeaderText = T("PurchaseOrders.RaisedBy");
        _grid.Column("awarded").HeaderText = T("Quotes.AwardedTo");
        _grid.Column("order").HeaderText = T("PurchaseOrders.Number");

        _suppliers.Column("supplier").HeaderText = T("Inventory.Supplier");
        _suppliers.Column("sent").HeaderText = T("Quotes.Sent");
        _suppliers.Column("total").HeaderText = T("PurchaseOrders.TotalExcludingVat");
        _suppliers.Column("delay").HeaderText = T("Quotes.DelayDays");
        _suppliers.Column("valid").HeaderText = T("Quotes.ValidUntil");
        _suppliers.Column("missing").HeaderText = T("Quotes.MissingItems");
        _suppliers.Column("file").HeaderText = T("Quotes.File");

        RenderHeading();
        if (_rows.Count > 0) Render();
        RenderDetail();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(null, ct);

    private QuoteRequestRow? SelectedRequest => _grid.CurrentRow?.Tag as QuoteRequestRow;
    private QuoteSupplierRow? SelectedSupplier => _suppliers.CurrentRow?.Tag as QuoteSupplierRow;

    private QuoteOfferRow? SelectedOffer =>
        SelectedSupplier is { } supplier ? _detail?.Offers.FirstOrDefault(o => o.SupplierId == supplier.SupplierId) : null;

    private static string Amount(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

    private async Task LoadAsync(Guid? keep = null, CancellationToken ct = default)
    {
        try
        {
            keep ??= SelectedRequest?.Id;

            var result = await _quotes.GetRequestsAsync(_onlyOpen.Checked, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading quotation requests failed.");
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
                row.RequestedOn.ToString("dd/MM/yy"),
                row.Subject ?? string.Empty,
                T($"QuoteRequestStatus.{row.Status}"),
                row.ResponseDueDate?.ToString("dd/MM/yy") ?? string.Empty,
                row.ItemCount,
                $"{row.OfferCount} / {row.SupplierCount}",
                row.RaisedByName,
                row.AwardedSupplierName ?? string.Empty,
                row.PurchaseOrderNumber ?? string.Empty);

            _grid.Rows[index].Tag = row;
            if (row.Id == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
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
        var open = _rows.Count(r => r.Status == QuoteRequestStatus.Open);
        _heading.Text = $"{T("Quotes.Title")} — {_rows.Count}" + (open > 0 ? $" · {T("Quotes.OpenCount")} {open}" : string.Empty);
    }

    private async Task LoadDetailAsync()
    {
        try
        {
            if (SelectedRequest is not { } request)
            {
                _detail = null;
                RenderDetail();
                return;
            }

            var result = await _quotes.GetDetailAsync(request.Id);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
            if (SelectedRequest?.Id != request.Id) return;

            _detail = result.Value;
            RenderDetail();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading a quotation request failed.");
        }
    }

    /// <summary>
    /// The comparison grid: one row per requested article, one column per supplier that answered,
    /// each cell the net unit price and the delay. Rebuilt per request because the suppliers differ.
    /// </summary>
    private void RenderDetail()
    {
        var keepSupplier = SelectedSupplier?.SupplierId;

        _comparison.SuspendLayout();
        _comparison.Rows.Clear();
        _comparison.Columns.Clear();

        _suppliers.SuspendLayout();
        _suppliers.Rows.Clear();

        if (_detail is { } detail)
        {
            AddColumn(_comparison, "line", 4, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
            AddColumn(_comparison, "item", 24);
            AddColumn(_comparison, "quantity", 8, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
            _comparison.Column("line").HeaderText = "#";
            _comparison.Column("item").HeaderText = T("Inventory.Item");
            _comparison.Column("quantity").HeaderText = T("Field.Quantity");

            foreach (var offer in detail.Offers)
            {
                AddColumn(_comparison, $"o{offer.Id:N}", 14, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
                _comparison.Columns[$"o{offer.Id:N}"]!.HeaderText = offer.IsExpired
                    ? $"{offer.SupplierName} ({T("Quotes.Expired")})"
                    : offer.SupplierName;
                _comparison.Columns[$"o{offer.Id:N}"]!.Tag = offer;
            }

            foreach (var line in detail.Comparison)
            {
                var item = detail.Items.FirstOrDefault(i => i.Id == line.QuoteRequestItemId);
                var values = new List<object>
                {
                    line.LineNumber,
                    item is null ? "?" : $"{item.Code} — {item.Designation}",
                    $"{line.Quantity:0.##} {item?.Unit}"
                };

                foreach (var offer in detail.Offers)
                {
                    var cell = line.Cells.FirstOrDefault(c => c.SupplierQuoteId == offer.Id);
                    values.Add(cell is null
                        ? "—"
                        : cell.DeliveryDelayDays is { } days
                            ? $"{Amount(cell.NetUnitPriceMad)} · {days} {T("Quotes.DaysShort")}"
                            : Amount(cell.NetUnitPriceMad));
                }

                var index = _comparison.Rows.Add([.. values]);
                _comparison.Rows[index].Tag = line;
            }

            foreach (var supplier in detail.Suppliers)
            {
                var offer = detail.Offers.FirstOrDefault(o => o.SupplierId == supplier.SupplierId);
                var index = _suppliers.Rows.Add(
                    supplier.IsApproved ? supplier.Name : $"{supplier.Name} ({T("Quotes.NotApproved")})",
                    supplier.SentAt is { } sent ? sent.ToString("dd/MM/yy") : T("Quotes.NotSent"),
                    offer is null ? T("Quotes.Awaiting") : Amount(offer.TotalExcludingVat),
                    offer?.DeliveryDelayDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    offer?.ValidUntil?.ToString("dd/MM/yy") ?? string.Empty,
                    offer is { MissingItems: > 0 } ? offer.MissingItems : string.Empty,
                    offer?.FileName is null ? string.Empty : T("Action.Yes"));

                _suppliers.Rows[index].Tag = supplier;
                if (supplier.SupplierId == keepSupplier) _suppliers.CurrentCell = _suppliers.Rows[index].Cells[0];
            }

            _comparisonHeading.Text = $"{T("Quotes.Comparison")} — {detail.Number}" +
                                      (detail.Subject is { Length: > 0 } subject ? $" · {subject}" : string.Empty);
            _suppliersHeading.Text = T("Quotes.SuppliersAsked");
        }
        else
        {
            _comparisonHeading.Text = T("Quotes.Comparison");
            _suppliersHeading.Text = T("Quotes.SuppliersAsked");
        }

        _comparison.ResumeLayout(performLayout: true);
        _suppliers.ResumeLayout(performLayout: true);
        RefreshButtons();
    }

    private void OnRequestFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not QuoteRequestRow row) return;
        if (row.Status != QuoteRequestStatus.Open) e.CellStyle!.ForeColor = UiTheme.TextMuted;
    }

    private void OnSupplierFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _suppliers.Rows[e.RowIndex].Tag is not QuoteSupplierRow row || _detail is null) return;

        if (_detail.Offers.FirstOrDefault(o => o.SupplierId == row.SupplierId) is not { } offer)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        if (offer.Id == _detail.AwardedQuoteId)
        {
            e.CellStyle!.BackColor = UiTheme.AccentSubtle;
            e.CellStyle.Font = UiTheme.BodyBold;
        }
        else if (offer.IsExpired)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
    }

    /// <summary>Cheapest in green, fastest in bold, both together for an offer that is both.</summary>
    private void OnComparisonFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _comparison.Rows[e.RowIndex].Tag is not QuoteComparisonLine line) return;
        if (_comparison.Columns[e.ColumnIndex].Tag is not QuoteOfferRow offer) return;

        var cell = line.Cells.FirstOrDefault(c => c.SupplierQuoteId == offer.Id);
        if (cell is null) return;

        if (cell.IsExpired)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        if (cell.IsCheapest)
        {
            e.CellStyle!.BackColor = UiTheme.NormalBackground;
            e.CellStyle.ForeColor = UiTheme.Normal;
        }

        if (cell.IsFastest) e.CellStyle!.Font = UiTheme.MonospaceBold;
    }

    /// <summary>
    /// Runs one write with every write button held disabled until it ends, and the view flagged busy
    /// so that leaving the screen does not dispose its context under it. The e-mails go out before
    /// the "sent" dates are saved: a second click during a slow send mailed every supplier again,
    /// and closing the screen mid-send lost the dates, so the request was sent again later.
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
        _new.Enabled = !IsBusy;
        var open = !IsBusy && _detail is { Status: QuoteRequestStatus.Open };
        var supplier = SelectedSupplier;
        var offer = SelectedOffer;

        _sendEmail.Enabled = open && _detail!.Suppliers.Any(s => s.SentAt is null);
        _markSent.Enabled = open && supplier is { SentAt: null };
        _addSupplier.Enabled = open;
        _recordOffer.Enabled = open && supplier is not null;
        _openFile.Enabled = offer?.FileName is not null;
        _award.Enabled = open && offer is { IsExpired: false };
        _cancel.Enabled = open;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task CreateAsync()
    {
        try
        {
            var items = await _quotes.GetQuotableItemsAsync();
            if (items.IsFailure) { _dialogs.ShowFailure(items, _new.Text); return; }

            var suppliers = await _quotes.GetSuppliersAsync();
            if (suppliers.IsFailure) { _dialogs.ShowFailure(suppliers, _new.Text); return; }

            using var dialog = new QuoteRequestDialog(Localizer, _dialogs, items.Value, suppliers.Value);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

            var created = await _quotes.CreateAsync(dialog.Request);
            if (created.IsFailure) { _dialogs.ShowFailure(created, _new.Text); return; }

            await LoadAsync(created.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Creating a quotation request failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), _new.Text);
        }
    }

    private async Task SendByEmailAsync()
    {
        if (_detail is not { } detail) return;

        if (!_quotes.CanEmail)
        {
            _dialogs.Warn($"{T("Purchasing_EmailNotConfigured")}\n\n{T("Quotes.SendManually")}", _sendEmail.Text);
            return;
        }

        if (!_dialogs.Confirm(T("Quotes.SendQuestion", detail.Number), _sendEmail.Text)) return;

        var sent = await _quotes.SendByEmailAsync(detail.Id);
        if (sent.IsFailure) { _dialogs.ShowFailure(sent, _sendEmail.Text); return; }

        var message = T("Quotes.SentCount", sent.Value.Sent);
        if (sent.Value.WithoutEmail.Count > 0)
            message += "\n\n" + T("Quotes.WithoutEmail", string.Join(", ", sent.Value.WithoutEmail));
        if (sent.Value.Failed.Count > 0)
            message += "\n\n" + T("Quotes.SendFailed", string.Join(", ", sent.Value.Failed));

        _dialogs.Info(message, _sendEmail.Text);
        await LoadAsync(detail.Id);
    }

    private async Task MarkSentAsync()
    {
        if (_detail is not { } detail || SelectedSupplier is not { } supplier) return;

        if (!_dialogs.Confirm(T("Quotes.MarkSentQuestion", detail.Number, supplier.Name), _markSent.Text)) return;

        await StepAsync(() => _quotes.MarkSentAsync(detail.Id, supplier.SupplierId), _markSent.Text);
    }

    private async Task AddSupplierAsync()
    {
        if (_detail is not { } detail) return;

        var suppliers = await _quotes.GetSuppliersAsync();
        if (suppliers.IsFailure) { _dialogs.ShowFailure(suppliers, _addSupplier.Text); return; }

        var choices = suppliers.Value.Where(s => detail.Suppliers.All(a => a.SupplierId != s.Id)).ToList();
        if (choices.Count == 0) { _dialogs.Info(T("Quotes.AllSuppliersAsked"), _addSupplier.Text); return; }

        using var dialog = new QuoteSupplierPickDialog(Localizer, _dialogs, detail.Number, choices);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SupplierId is not { } supplierId) return;

        await StepAsync(() => _quotes.AddSupplierAsync(detail.Id, supplierId), _addSupplier.Text);
    }

    private async Task RecordOfferAsync()
    {
        if (_detail is not { } detail || SelectedSupplier is not { } supplier) return;

        if (SelectedOffer is not null
            && !_dialogs.Confirm(T("Quotes.ReplaceOfferQuestion", supplier.Name), _recordOffer.Text))
            return;

        using var dialog = new QuoteOfferDialog(Localizer, _dialogs, detail, supplier);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        await StepAsync(() => _quotes.RecordOfferAsync(detail.Id, dialog.Request), _recordOffer.Text);
    }

    private async Task OpenFileAsync()
    {
        if (SelectedOffer is not { FileName: not null } offer) return;

        var file = await _quotes.GetOfferFileAsync(offer.Id);
        if (file.IsFailure) { _dialogs.ShowFailure(file, _openFile.Text); return; }

        var opened = await _output.ViewAsync(file.Value.Content, Path.GetExtension(file.Value.FileName));
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _openFile.Text);
    }

    private async Task AwardAsync()
    {
        if (_detail is not { } detail || SelectedOffer is not { } offer) return;

        var question = T("Quotes.AwardQuestion", offer.SupplierName, Amount(offer.TotalExcludingVat));
        if (offer.MissingItems > 0) question += "\n\n" + T("Quotes.AwardMissingItems", offer.MissingItems);

        if (!_dialogs.Confirm(question, _award.Text)) return;

        var awarded = await _quotes.AwardAsync(detail.Id, offer.Id);
        if (awarded.IsFailure) { _dialogs.ShowFailure(awarded, _award.Text); return; }

        _dialogs.Info(T("Quotes.Awarded", awarded.Value), _award.Text);
        await LoadAsync(detail.Id);
    }

    private async Task CancelAsync()
    {
        if (_detail is not { } detail) return;

        var reason = _dialogs.PromptForReason(T("Quotes.CancelPrompt", detail.Number), _cancel.Text);
        if (reason is null) return;

        await StepAsync(() => _quotes.CancelAsync(detail.Id, reason), _cancel.Text);
    }

    private async Task StepAsync(Func<Task<Result>> step, string title)
    {
        try
        {
            var keep = SelectedRequest?.Id;
            var result = await step();
            if (result.IsFailure) { _dialogs.ShowFailure(result, title); return; }

            await LoadAsync(keep);
            await LoadDetailAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "A quotation step failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), title);
        }
    }
}

/// <summary>A new request: the articles and quantities, and the suppliers to ask.</summary>
internal sealed class QuoteRequestDialog : MasterDataEditor
{
    private readonly TextBox _subject, _notes;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _due;
    private readonly DataGridView _items = UiTheme.Grid();
    private readonly CheckedListBox _suppliers = new();
    private readonly IReadOnlyList<QuoteSupplierOption> _supplierOptions;

    internal CreateQuoteRequest? Request { get; private set; }

    internal QuoteRequestDialog(ILocalizer localizer, IUserDialogs dialogs, IReadOnlyList<QuotableItem> items,
                                IReadOnlyList<QuoteSupplierOption> suppliers)
        : base(localizer, dialogs, "Quotes.New", null, 900, 620)
    {
        _supplierOptions = suppliers;

        _subject = TextField("Quotes.Subject", 20, 14, 520);
        _due = OptionalDate("Quotes.ResponseDue", 560, 14, DateOnly.FromDateTime(DateTime.Today.AddDays(7)),
                            min: DateTime.Today);

        Label("Quotes.ItemsToQuote", 20, 70);
        _items.Dock = DockStyle.None;
        _items.Bounds = new Rectangle(20, 90, 560, 400);
        _items.ReadOnly = false;
        _items.Columns.Add(new DataGridViewCheckBoxColumn { Name = "include", HeaderText = "", FillWeight = 6 });
        _items.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "item", HeaderText = localizer["Inventory.Item"], FillWeight = 44, ReadOnly = true
        });
        _items.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "quantity", HeaderText = localizer["Field.Quantity"], FillWeight = 14,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        });
        _items.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "contract", HeaderText = localizer["Quotes.OnContract"], FillWeight = 20, ReadOnly = true
        });

        var language = localizer.CurrentLanguage;
        foreach (var item in items)
        {
            var index = _items.Rows.Add(false, $"{item.Code} — {item.Name.For(language)} ({item.UnitOfMeasure})",
                                        item.ReorderQuantity > 0m
                                            ? item.ReorderQuantity.ToString("0.##", CultureInfo.InvariantCulture)
                                            : string.Empty,
                                        item.ContractReference ?? string.Empty);
            var row = _items.Rows[index];
            row.Tag = item;

            // On contract: bought at the contracted price from the contract's supplier, never quoted.
            if (item.ContractReference is not null)
            {
                row.ReadOnly = true;
                row.DefaultCellStyle.ForeColor = UiTheme.TextMuted;
            }
        }
        Controls.Add(_items);

        Label("Quotes.SuppliersToAsk", 600, 70);
        _suppliers.Bounds = new Rectangle(600, 90, 280, 400);
        _suppliers.CheckOnClick = true;
        foreach (var supplier in suppliers)
            _suppliers.Items.Add(supplier.IsApproved
                ? string.IsNullOrWhiteSpace(supplier.Email) ? $"{supplier.Name} ({localizer["Quotes.NoEmail"]})" : supplier.Name
                : $"{supplier.Name} ({localizer["Quotes.NotApproved"]})");
        Controls.Add(_suppliers);

        _notes = TextField("Field.Notes", 20, 500, 860);
        Notice(20, 552, 860, 20, "Quotes.OnContractNotice");

        Buttons(580, 900);
    }

    protected override void Accept()
    {
        _items.EndEdit();

        var chosen = new List<QuoteItemInput>();
        foreach (DataGridViewRow row in _items.Rows)
        {
            if (row.Tag is not QuotableItem item || item.ContractReference is not null) continue;
            if (row.Cells["include"].Value is not true) continue;

            if (EditorValues.ParseDecimal(row.Cells["quantity"].Value?.ToString()) is not { } quantity || quantity <= 0m)
            {
                Dialogs.Warn(Localizer["Quote_QuantityMustBePositive"], Text);
                _items.CurrentCell = row.Cells["quantity"];
                return;
            }

            chosen.Add(new QuoteItemInput(item.Id, quantity));
        }

        if (chosen.Count == 0) { Dialogs.Warn(Localizer["Quote_NoItems"], Text); return; }

        var suppliers = _suppliers.CheckedIndices.Cast<int>().Select(i => _supplierOptions[i].Id).ToList();
        if (suppliers.Count == 0) { Dialogs.Warn(Localizer["Quote_NoSuppliers"], Text); return; }

        Request = new CreateQuoteRequest
        {
            Subject = Blank(_subject),
            ResponseDueDate = DateOrNull(_due.Toggle, _due.Picker),
            Notes = Blank(_notes),
            Items = chosen,
            SupplierIds = suppliers
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>One more supplier to ask.</summary>
internal sealed class QuoteSupplierPickDialog : MasterDataEditor
{
    private readonly ComboBox _supplier;

    internal Guid? SupplierId { get; private set; }

    internal QuoteSupplierPickDialog(ILocalizer localizer, IUserDialogs dialogs, string number,
                                     IReadOnlyList<QuoteSupplierOption> suppliers)
        : base(localizer, dialogs, "Quotes.AddSupplier", number, 480, 130)
    {
        _supplier = Lookup("Inventory.Supplier", 20, 14, 440,
                           suppliers.Select(s => (s.Id, $"{s.Code} — {s.Name}")), null, allowNone: false);
        Buttons(76, 480);
    }

    protected override void Accept()
    {
        SupplierId = SelectedId(_supplier);
        if (SupplierId is null) return;

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A supplier's answer: price, VAT and discount per article, delay, validity and conditions.</summary>
internal sealed class QuoteOfferDialog : MasterDataEditor
{
    private readonly Guid _supplierId;
    private readonly DateTimePicker _received;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _valid;
    private readonly TextBox _delay, _reference, _conditions;
    private readonly DataGridView _lines = UiTheme.Grid();
    private readonly Label _file = new();
    private string? _fileName;
    private byte[]? _fileContent;

    internal RecordQuoteOfferRequest? Request { get; private set; }

    internal QuoteOfferDialog(ILocalizer localizer, IUserDialogs dialogs, QuoteRequestDetail detail,
                              QuoteSupplierRow supplier)
        : base(localizer, dialogs, "Quotes.RecordOffer", $"{detail.Number} · {supplier.Name}", 760, 600)
    {
        _supplierId = supplier.SupplierId;

        Label("Quotes.ReceivedOn", 20, 14);
        _received = new DateTimePicker
        {
            Bounds = new Rectangle(20, 32, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            MaxDate = DateTime.Today,
            Value = DateTime.Today
        };
        Controls.Add(_received);

        _valid = OptionalDate("Quotes.ValidUntil", 200, 14, DateOnly.FromDateTime(DateTime.Today.AddMonths(1)));
        _delay = TextField("Quotes.DelayDays", 400, 14, 120, null, latin: true, mono: true, right: true);
        _reference = TextField("Quotes.SupplierReference", 540, 14, 200, null, latin: true);

        Label("Quotes.Prices", 20, 70);
        _lines.Dock = DockStyle.None;
        _lines.Bounds = new Rectangle(20, 90, 720, 300);
        _lines.ReadOnly = false;
        _lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "item", HeaderText = localizer["Inventory.Item"], FillWeight = 46, ReadOnly = true });
        _lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "quantity", HeaderText = localizer["Field.Quantity"], FillWeight = 12, ReadOnly = true });
        _lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "price", HeaderText = localizer["PurchaseOrders.UnitPrice"], FillWeight = 16 });
        _lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "discount", HeaderText = localizer["PurchaseOrders.DiscountPercent"], FillWeight = 12 });
        _lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "vat", HeaderText = localizer["PurchaseOrders.VatPercent"], FillWeight = 10 });

        foreach (var item in detail.Items)
        {
            var index = _lines.Rows.Add($"{item.Code} — {item.Designation}", $"{item.Quantity:0.##} {item.Unit}",
                                        string.Empty, "0", "20");
            _lines.Rows[index].Tag = item;
        }
        Controls.Add(_lines);

        Notice(20, 396, 720, 34, "Quotes.PriceNotice");

        _conditions = TextField("Quotes.Conditions", 20, 436, 720);

        var attach = UiTheme.SecondaryButton(localizer["Quotes.AttachFile"]);
        attach.Bounds = new Rectangle(20, 492, 200, UiTheme.ButtonHeight);
        attach.Click += (_, _) => PickFile();
        Controls.Add(attach);

        _file.Font = UiTheme.Small;
        _file.ForeColor = UiTheme.TextSecondary;
        _file.AutoSize = true;
        _file.Location = new Point(232, 500);
        Controls.Add(_file);

        Buttons(546, 760);
    }

    private void PickFile()
    {
        using var picker = new OpenFileDialog
        {
            Title = Localizer["Quotes.AttachFile"],
            Filter = "PDF, images, Office (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.doc;*.docx;*.xls;*.xlsx)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.doc;*.docx;*.xls;*.xlsx",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > PurchasingDocument.MaximumBytes)
            {
                Dialogs.Warn(Localizer.Format("Purchasing_FileTooLarge", PurchasingDocument.MaximumBytes / (1024 * 1024)), Text);
                return;
            }

            _fileContent = File.ReadAllBytes(info.FullName);
            _fileName = info.Name;
            _file.Text = info.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Warn($"{Localizer["Documents.FileUnreadable"]}\n{ex.Message}", Text);
        }
    }

    protected override void Accept()
    {
        _lines.EndEdit();

        var lines = new List<SupplierQuoteLineInput>();
        foreach (DataGridViewRow row in _lines.Rows)
        {
            if (row.Tag is not QuoteItemRow item) continue;

            // A blank price is an article the supplier did not quote.
            if (string.IsNullOrWhiteSpace(row.Cells["price"].Value?.ToString())) continue;

            if (EditorValues.ParseDecimal(row.Cells["price"].Value?.ToString()) is not { } price || price < 0m)
            {
                Dialogs.Warn(Localizer["Po_PriceMustNotBeNegative"], Text);
                _lines.CurrentCell = row.Cells["price"];
                return;
            }

            var discount = EditorValues.ParseDecimal(row.Cells["discount"].Value?.ToString()) ?? 0m;
            var vat = EditorValues.ParseDecimal(row.Cells["vat"].Value?.ToString()) ?? 20m;
            if (discount is < 0m or >= 100m || vat is < 0m or > 100m)
            {
                Dialogs.Warn(Localizer["Po_RateOutOfRange"], Text);
                _lines.CurrentCell = row.Cells["discount"];
                return;
            }

            lines.Add(new SupplierQuoteLineInput(item.Id, price, vat / 100m, discount / 100m));
        }

        if (lines.Count == 0) { Dialogs.Warn(Localizer["Quote_OfferHasNoPrice"], Text); return; }

        int? delay = null;
        if (!string.IsNullOrWhiteSpace(_delay.Text))
        {
            if (!int.TryParse(_delay.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days))
            {
                Dialogs.Warn(Localizer["Quote_DelayMustNotBeNegative"], Text);
                _delay.Focus();
                return;
            }
            delay = days;
        }

        Request = new RecordQuoteOfferRequest
        {
            SupplierId = _supplierId,
            ReceivedOn = DateOnly.FromDateTime(_received.Value.Date),
            ValidUntil = DateOrNull(_valid.Toggle, _valid.Picker),
            DeliveryDelayDays = delay,
            SupplierReference = Blank(_reference),
            Conditions = Blank(_conditions),
            Lines = lines,
            FileName = _fileName,
            FileContent = _fileContent
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
