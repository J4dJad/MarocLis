using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Periodic inventories. A count is opened on a scope (all, a storage location, a category),
/// started — which freezes the expected quantities and prints the count sheet — counted line by
/// line in the grid, and closed, which posts the differences as adjustments "Inventaire n°" and
/// prints the variance report.
/// <para>
/// The printed count sheet leaves the expected quantity out: a counter who sees what the system
/// expects tends to find it.
/// </para>
/// </summary>
public sealed class InventoryCountView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly InventoryCountService _counts;
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<InventoryCountView>? _logger;

    private readonly DataGridView _list = UiTheme.Grid();
    private readonly DataGridView _lines = UiTheme.Grid();
    private readonly Label _heading = new();
    private readonly Label _linesHeading = new();
    private readonly CheckBox _includeClosed = new();
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _start = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _close = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _sheet = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _sheetCsv = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _variances = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<InventoryCountRow> _rows = [];
    private InventoryCountDetail? _detail;
    private bool _rendering;

    public InventoryCountView(InventoryCountService counts, IListPdfRenderer lists, IDocumentOutput output,
                              IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                              ILogger<InventoryCountView>? logger = null)
        : base(localizer, cultureManager)
    {
        _counts = counts;
        _lists = lists;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();

        _includeClosed.CheckedChanged += async (_, _) => await ActivateAsync();
        _list.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadDetailAsync());
        _new.Click += async (_, _) => await RunBusyAsync(CreateAsync);
        _start.Click += async (_, _) => await RunBusyAsync(StartAsync);
        _close.Click += async (_, _) => await RunBusyAsync(CloseAsync);
        _cancel.Click += async (_, _) => await RunBusyAsync(CancelAsync);
        _sheet.Click += async (_, _) => await RunBusyAsync(() => PrintSheetAsync(pdf: true));
        _sheetCsv.Click += async (_, _) => await RunBusyAsync(() => PrintSheetAsync(pdf: false));
        _variances.Click += async (_, _) => await RunBusyAsync(PrintVariancesAsync);
        _lines.CellEndEdit += async (_, e) => await RunBusyAsync(() => SaveLineAsync(e.RowIndex));

        // Opening, starting and closing a count is the manager's; counting needs the reception right.
        PermissionGate.Require(Permissions.Inventory.CloseCounts, _new, _start, _close, _cancel);
    }

    public override string Title => T("Nav.InventoryCounts");

    private InventoryCountRow? Current => _list.CurrentRow?.Tag as InventoryCountRow;

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 220,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        var bar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        _includeClosed.Font = UiTheme.Small;
        _includeClosed.ForeColor = UiTheme.TextSecondary;
        _includeClosed.AutoSize = true;
        _includeClosed.Location = new Point(0, 34);

        _new.Bounds = new Rectangle(220, 26, 170, UiTheme.ButtonHeight);
        _start.Bounds = new Rectangle(400, 26, 170, UiTheme.ButtonHeight);
        _close.Bounds = new Rectangle(580, 26, 170, UiTheme.ButtonHeight);
        _cancel.Bounds = new Rectangle(760, 26, 130, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_heading, _includeClosed, _new, _start, _close, _cancel]);

        foreach (var (name, weight) in new[]
                 {
                     ("number", 12), ("date", 9), ("scope", 20), ("status", 10), ("lines", 7), ("counted", 7),
                     ("variances", 7), ("closed", 12)
                 })
        {
            _list.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        _list.Dock = DockStyle.Fill;

        var top = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        top.Controls.Add(_list);
        top.Controls.Add(bar);

        var linesBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };
        _linesHeading.Font = UiTheme.SectionTitle;
        _linesHeading.ForeColor = UiTheme.TextPrimary;
        // Fixed width: the heading grows with the selection and must not run under the buttons.
        _linesHeading.AutoSize = false;
        _linesHeading.AutoEllipsis = true;
        _linesHeading.Bounds = new Rectangle(0, 10, 370, 26);

        _sheet.Bounds = new Rectangle(380, 6, 220, UiTheme.ButtonHeight);
        _sheetCsv.Bounds = new Rectangle(610, 6, 170, UiTheme.ButtonHeight);
        _variances.Bounds = new Rectangle(790, 6, 220, UiTheme.ButtonHeight);
        linesBar.Controls.AddRange([_linesHeading, _sheet, _sheetCsv, _variances]);

        foreach (var (name, weight, editable) in new[]
                 {
                     ("line", 4, false), ("location", 12, false), ("code", 10, false), ("item", 18, false),
                     ("lot", 11, false), ("expiry", 8, false), ("expected", 8, false), ("counted", 8, true),
                     ("variance", 7, false), ("comment", 16, true)
                 })
        {
            _lines.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = !editable,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = name is "expected" or "counted" or "variance"
                        ? DataGridViewContentAlignment.MiddleRight
                        : DataGridViewContentAlignment.MiddleLeft,
                    BackColor = editable ? UiTheme.SurfaceRaised : Color.Empty
                }
            });
        }

        _lines.Dock = DockStyle.Fill;
        _lines.ReadOnly = false;
        _lines.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _lines.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _lines.Rows[e.RowIndex].Tag is not InventoryCountLineRow row) return;
            if (row.Variance is { } v && v != 0m)
            {
                e.CellStyle!.ForeColor = UiTheme.Critical;
            }
        };

        _status.Dock = DockStyle.Bottom;
        _status.Height = 30;
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        bottom.Controls.Add(_lines);
        bottom.Controls.Add(linesBar);
        bottom.Controls.Add(_status);

        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);
        Controls.Add(split);
    }

    private IReadOnlyList<string> SheetHeaders(bool withExpected) =>
    [
        "#", T("Sample.Storage"), T("Field.Code"), T("Inventory.Item"), T("Inventory.LotNumber"), T("Inventory.Expiry"),
        .. withExpected ? new[] { T("InventoryCount.Expected") } : [],
        T("InventoryCount.Counted"),
        .. withExpected ? new[] { T("InventoryCount.Variance") } : [],
        T("Field.Comment")
    ];

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.InventoryCounts");
        _includeClosed.Text = T("InventoryCount.IncludeClosed");
        _new.Text = T("InventoryCount.New");
        _start.Text = T("InventoryCount.Start");
        _close.Text = T("InventoryCount.Close");
        _cancel.Text = T("Action.Cancel");
        _sheet.Text = T("InventoryCount.Sheet");
        _sheetCsv.Text = T("InventoryCount.SheetCsv");
        _variances.Text = T("InventoryCount.VarianceReport");
        _linesHeading.Text = T("InventoryCount.Lines");

        var listHeaders = new[]
        {
            T("InventoryCount.Number"), T("Field.Date"), T("InventoryCount.Scope"), T("Field.Status"),
            T("InventoryCount.Lines"), T("InventoryCount.Counted"), T("InventoryCount.Variance"),
            T("InventoryCount.ClosedAt")
        };
        for (var i = 0; i < _list.Columns.Count; i++) _list.Columns[i].HeaderText = listHeaders[i];

        var lineHeaders = SheetHeaders(withExpected: true);
        for (var i = 0; i < _lines.Columns.Count; i++) _lines.Columns[i].HeaderText = lineHeaders[i];

        if (_rows.Count > 0) RenderList();
        if (_detail is not null) RenderLines();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _counts.GetCountsAsync(_includeClosed.Checked, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            var selected = Current?.InventoryCountId;
            _rows = result.Value;
            RenderList(selected);
            await LoadDetailAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the inventories failed.");
        }
    }

    private string ScopeText(InventoryCountRow r) => r.Scope switch
    {
        InventoryCountScope.StorageLocation => $"{T("InventoryCountScope.StorageLocation")} : {r.StorageLocation}",
        InventoryCountScope.Category => $"{T("InventoryCountScope.Category")} : {T($"StockItemKind.{r.Category}")}",
        _ => T("InventoryCountScope.All")
    };

    private void RenderList(Guid? select = null)
    {
        _list.Rows.Clear();
        foreach (var r in _rows)
        {
            var index = _list.Rows.Add(r.Number, r.CountDate.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture),
                                       ScopeText(r), T($"InventoryCountStatus.{r.Status}"), r.Lines, r.Counted,
                                       r.WithVariance,
                                       r.ClosedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) ?? string.Empty);
            _list.Rows[index].Tag = r;
            if (r.InventoryCountId == select) _list.CurrentCell = _list.Rows[index].Cells[0];
        }

        UpdateButtons();
    }

    private async Task LoadDetailAsync(CancellationToken ct = default)
    {
        if (Current is not { } row) { _detail = null; RenderLines(); return; }

        var detail = await _counts.GetDetailAsync(row.InventoryCountId, ct);
        if (detail.IsFailure) { _dialogs.ShowFailure(detail); return; }

        _detail = detail.Value;
        RenderLines();
    }

    private void RenderLines()
    {
        _rendering = true;
        try
        {
            _lines.Rows.Clear();
            if (_detail is null) { UpdateButtons(); return; }

            var counting = _detail.Count.Status == InventoryCountStatus.Counting;
            static string N(decimal? v) => v?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;

            foreach (var l in _detail.Lines)
            {
                var index = _lines.Rows.Add(l.LineNumber, l.StorageLocation ?? string.Empty, l.ItemCode, l.ItemName,
                                            l.LotNumber, l.ExpiryDate.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture),
                                            N(l.ExpectedQuantity), N(l.CountedQuantity), N(l.Variance),
                                            l.Comment ?? string.Empty);
                _lines.Rows[index].Tag = l;
                _lines.Rows[index].ReadOnly = !counting || !PermissionGate.Allows(Permissions.Inventory.Receive)
                                                           && !PermissionGate.Allows(Permissions.Inventory.CloseCounts);
            }

            _linesHeading.Text = $"{T("InventoryCount.Lines")} — {_detail.Count.Number} · {_detail.Count.Counted}/{_detail.Count.Lines}";
            _status.Text = counting ? T("InventoryCount.CountingNotice") : string.Empty;
        }
        finally
        {
            _rendering = false;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var status = Current?.Status;
        _start.Enabled = status == InventoryCountStatus.Draft && PermissionGate.Allows(Permissions.Inventory.CloseCounts);
        _close.Enabled = status == InventoryCountStatus.Counting && PermissionGate.Allows(Permissions.Inventory.CloseCounts);
        _cancel.Enabled = status is InventoryCountStatus.Draft or InventoryCountStatus.Counting
                          && PermissionGate.Allows(Permissions.Inventory.CloseCounts);
        _sheet.Enabled = _sheetCsv.Enabled = _detail is { Lines.Count: > 0 };
        _variances.Enabled = _detail is { Lines.Count: > 0 };
    }

    private async Task CreateAsync()
    {
        var locations = await _counts.GetStorageLocationsAsync();
        if (locations.IsFailure) { _dialogs.ShowFailure(locations, _new.Text); return; }

        using var dialog = new InventoryCountDialog(Localizer, _dialogs, locations.Value);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var created = await _counts.CreateAsync(dialog.Request);
        if (created.IsFailure) { _dialogs.ShowFailure(created, _new.Text); return; }

        _status.Text = T("InventoryCount.Created", created.Value);
        await ActivateAsync();
    }

    private async Task StartAsync()
    {
        if (Current is not { } row) return;

        var started = await _counts.StartCountingAsync(row.InventoryCountId);
        if (started.IsFailure) { _dialogs.ShowFailure(started, _start.Text); return; }

        await ActivateAsync();
        _status.Text = T("InventoryCount.Started", started.Value);
    }

    private async Task SaveLineAsync(int rowIndex)
    {
        if (_rendering || rowIndex < 0 || _detail is null) return;
        if (_lines.Rows[rowIndex].Tag is not InventoryCountLineRow line) return;

        var countedText = Convert.ToString(_lines.Rows[rowIndex].Cells["counted"].Value, CultureInfo.CurrentCulture)?.Trim();
        var comment = Convert.ToString(_lines.Rows[rowIndex].Cells["comment"].Value, CultureInfo.CurrentCulture);

        if (string.IsNullOrEmpty(countedText)) return;

        if (EditorValues.ParseDecimal(countedText) is not { } counted)
        {
            _dialogs.Warn(Localizer.Format("Result_NotANumber", countedText), T("InventoryCount.Counted"));
            return;
        }

        if (counted == line.CountedQuantity && (comment ?? string.Empty) == (line.Comment ?? string.Empty)) return;

        var saved = await _counts.RecordCountAsync(_detail.Count.InventoryCountId, line.LineId, counted, comment);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, T("InventoryCount.Counted")); }

        // Re-read after the grid has finished its edit, so the variance and the progress follow.
        BeginInvoke(new Action(() => _ = ActivateAsync()));
    }

    private async Task CloseAsync()
    {
        if (Current is not { } row) return;
        if (!_dialogs.Confirm(T("InventoryCount.CloseConfirm", row.Number), _close.Text)) return;

        var closed = await _counts.CloseAsync(row.InventoryCountId);
        if (closed.IsFailure) { _dialogs.ShowFailure(closed, _close.Text); return; }

        await ActivateAsync();
        _status.Text = T("InventoryCount.Closed", row.Number, closed.Value);
    }

    private async Task CancelAsync()
    {
        if (Current is not { } row) return;

        var reason = _dialogs.PromptForReason(T("InventoryCount.CancelPrompt", row.Number), _cancel.Text);
        if (reason is null) return;

        var cancelled = await _counts.CancelAsync(row.InventoryCountId, reason);
        if (cancelled.IsFailure) { _dialogs.ShowFailure(cancelled, _cancel.Text); return; }

        await ActivateAsync();
    }

    /// <summary>The count sheet, without the expected quantities, to fill in on paper.</summary>
    private async Task PrintSheetAsync(bool pdf)
    {
        if (_detail is null) return;

        var rows = _detail.Lines.Select(l => (IReadOnlyList<string>)
        [
            l.LineNumber.ToString(CultureInfo.InvariantCulture), l.StorageLocation ?? string.Empty, l.ItemCode,
            l.ItemName, l.LotNumber, l.ExpiryDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            string.Empty, string.Empty
        ]).ToList();

        var headers = SheetHeaders(withExpected: false);

        byte[] content = pdf
            ? _lists.Render(new ListDocument(
                T("InventoryCount.SheetTitle", _detail.Count.Number,
                  _detail.Count.CountDate.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)),
                $"{ScopeText(_detail.Count)} — {T("InventoryCount.SheetCaption")}",
                headers, rows, Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic))
            : StockCsv.Table(headers, rows);

        var opened = await _output.OpenAsync(content, pdf ? "pdf" : "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, pdf ? _sheet.Text : _sheetCsv.Text);
    }

    /// <summary>The lots whose count differs from the stock, with the difference.</summary>
    private async Task PrintVariancesAsync()
    {
        if (_detail is null) return;

        static string N(decimal? v) => v?.ToString("0.###", CultureInfo.GetCultureInfo("fr-FR")) ?? string.Empty;

        var rows = _detail.Lines.Where(l => l.Variance is not null and not 0m).Select(l => (IReadOnlyList<string>)
        [
            l.LineNumber.ToString(CultureInfo.InvariantCulture), l.StorageLocation ?? string.Empty, l.ItemCode,
            l.ItemName, l.LotNumber, l.ExpiryDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            N(l.ExpectedQuantity), N(l.CountedQuantity), N(l.Variance), l.Comment ?? string.Empty
        ]).ToList();

        var document = new ListDocument(
            T("InventoryCount.VarianceTitle", _detail.Count.Number,
              _detail.Count.CountDate.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture)),
            $"{ScopeText(_detail.Count)} — {T($"InventoryCountStatus.{_detail.Count.Status}")} — "
            + T("InventoryCount.VarianceCaption", rows.Count, _detail.Lines.Count),
            SheetHeaders(withExpected: true), rows, Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic);

        var opened = await _output.OpenAsync(_lists.Render(document), "pdf");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _variances.Text);
    }
}

/// <summary>Opens an inventory: its date and what it covers.</summary>
internal sealed class InventoryCountDialog : MasterDataEditor
{
    private readonly DateTimePicker _date;
    private readonly ComboBox _scope, _category;
    private readonly ComboBox _location;
    private readonly TextBox _notes;

    internal CreateInventoryCountRequest? Request { get; private set; }

    internal InventoryCountDialog(ILocalizer localizer, IUserDialogs dialogs, IReadOnlyList<string> locations)
        : base(localizer, dialogs, "InventoryCount.New", null, 560, 300)
    {
        Label("Field.Date", 20, 14);
        _date = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Bounds = new Rectangle(20, 32, 160, UiTheme.InputHeight),
            Value = DateTime.Today
        };
        Controls.Add(_date);

        _scope = Choice("InventoryCount.Scope", 200, 14, 330, InventoryCountScope.All, "InventoryCountScope");

        Label("InventoryCountScope.StorageLocation", 20, 70);
        _location = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(20, 88, 240, UiTheme.InputHeight)
        };
        _location.Items.AddRange([.. locations]);
        Controls.Add(_location);

        _category = Choice("InventoryCountScope.Category", 290, 70, 240, StockItemKind.Reagent, "StockItemKind");

        _notes = TextField("Field.Comment", 20, 126, 510);
        Notice(20, 180, 510, 40, "InventoryCount.ScopeNotice");

        void Sync()
        {
            var scope = Selected<InventoryCountScope>(_scope);
            _location.Enabled = scope == InventoryCountScope.StorageLocation;
            _category.Enabled = scope == InventoryCountScope.Category;
        }

        _scope.SelectedIndexChanged += (_, _) => Sync();
        Sync();

        Buttons(240, 560);
    }

    protected override void Accept()
    {
        var scope = Selected<InventoryCountScope>(_scope);

        if (scope == InventoryCountScope.StorageLocation && string.IsNullOrWhiteSpace(_location.Text))
        {
            Dialogs.Warn(Localizer["InventoryCount_LocationRequired"], Text);
            _location.Focus();
            return;
        }

        Request = new CreateInventoryCountRequest
        {
            CountDate = DateOnly.FromDateTime(_date.Value),
            Scope = scope,
            StorageLocation = scope == InventoryCountScope.StorageLocation ? _location.Text.Trim() : null,
            Category = scope == InventoryCountScope.Category ? Selected<StockItemKind>(_category) : null,
            Notes = Blank(_notes)
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
