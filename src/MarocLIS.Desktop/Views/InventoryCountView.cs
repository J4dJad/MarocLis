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
/// expects tends to find it. It has a column for the time each shelf was counted: a sheet keyed in
/// later is entered with that time ("Feuille comptée le"), and each lot is measured against the
/// stock at that time rather than the stock when it is typed in.
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
    private readonly Label _sheetTimeLabel = new();
    private readonly DateTimePicker _sheetTime = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "dd/MM/yyyy HH:mm", ShowCheckBox = true, Checked = false
    };

    private IReadOnlyList<InventoryCountRow> _rows = [];
    private InventoryCountDetail? _detail;
    private bool _rendering;

    // Counts typed while an earlier one is still being saved wait their turn instead of being
    // dropped: a counter goes down the sheet faster than the database answers.
    private readonly Queue<PendingCount> _pending = new();
    private bool _saving;
    private Guid? _renderedCountId;

    private sealed record PendingCount(Guid InventoryCountId, Guid LineId, decimal Counted, string? Comment,
                                       DateTimeOffset? ShelfCountedAt);

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
        _lines.CellEndEdit += async (_, e) => await QueueLineAsync(e.RowIndex);

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

        // Unticked, a figure is the shelf as it is now; ticked, the time the paper sheet was filled in.
        _sheetTimeLabel.Font = UiTheme.Small;
        _sheetTimeLabel.ForeColor = UiTheme.TextSecondary;
        _sheetTimeLabel.AutoSize = true;
        _sheetTimeLabel.Location = new Point(1020, 0);
        _sheetTime.Bounds = new Rectangle(1020, 16, 170, UiTheme.InputHeight);
        _sheetTime.Value = DateTime.Now;
        linesBar.Controls.AddRange([_linesHeading, _sheet, _sheetCsv, _variances, _sheetTimeLabel, _sheetTime]);

        foreach (var (name, weight, editable) in new[]
                 {
                     ("line", 4, false), ("location", 11, false), ("code", 9, false), ("item", 16, false),
                     ("lot", 10, false), ("expiry", 8, false), ("expected", 7, false), ("stockAtCount", 8, false),
                     ("counted", 8, true), ("countedAt", 9, false), ("variance", 7, false), ("comment", 14, true)
                 })
        {
            _lines.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = !editable,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = name is "expected" or "stockAtCount" or "counted" or "variance"
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

    // The variance is counted less the stock when the shelf was counted: that is the column next
    // to it. The quantity frozen at the start stays for information, labelled as such.
    private IReadOnlyList<string> SheetHeaders(bool withExpected) =>
    [
        "#", T("Sample.Storage"), T("Field.Code"), T("Inventory.Item"), T("Inventory.LotNumber"), T("Inventory.Expiry"),
        .. withExpected ? new[] { T("InventoryCount.ExpectedAtStart"), T("InventoryCount.StockAtCount") } : [],
        T("InventoryCount.Counted"),
        T("InventoryCount.CountedAt"),
        .. withExpected ? new[] { T("InventoryCount.Variance") } : [],
        T("Field.Comment")
    ];

    private static string When(DateTimeOffset? at) =>
        at?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;

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
        _sheetTimeLabel.Text = T("InventoryCount.SheetTime");

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
        // The cursor stays where the counter was: back on line 1 after every reload, a 250-line
        // sheet meant scrolling down again for each lot.
        var (row, column) = _lines.CurrentCell is { } cell && _detail?.Count.InventoryCountId == _renderedCountId
            ? (cell.RowIndex, cell.ColumnIndex)
            : (-1, -1);
        _renderedCountId = _detail?.Count.InventoryCountId;

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
                                            N(l.ExpectedQuantity), N(l.StockAtCount), N(l.CountedQuantity),
                                            When(l.CountedAt), N(l.Variance), l.Comment ?? string.Empty);
                _lines.Rows[index].Tag = l;
                _lines.Rows[index].ReadOnly = !counting || !PermissionGate.Allows(Permissions.Inventory.Receive)
                                                           && !PermissionGate.Allows(Permissions.Inventory.CloseCounts);
            }

            _linesHeading.Text = $"{T("InventoryCount.Lines")} — {_detail.Count.Number} · {_detail.Count.Counted}/{_detail.Count.Lines}";
            _status.Text = counting ? $"{T("InventoryCount.CountingNotice")} {T("InventoryCount.SheetTimeNotice")}" : string.Empty;

            if (row >= 0 && row < _lines.Rows.Count && column >= 0 && column < _lines.Columns.Count)
                _lines.CurrentCell = _lines.Rows[row].Cells[column];
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

    /// <summary>
    /// Saves a count as it is entered. Only the saved line and the progress are refreshed: reloading
    /// the whole sheet threw away the value being typed on the next line and sent the cursor back
    /// to the top.
    /// </summary>
    private async Task QueueLineAsync(int rowIndex)
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

        // The sheet's time goes with a figure; a comment alone leaves the line's count where it was.
        DateTimeOffset? shelf = _sheetTime.Checked && counted != line.CountedQuantity
            ? new DateTimeOffset(_sheetTime.Value)
            : null;
        _pending.Enqueue(new PendingCount(_detail.Count.InventoryCountId, line.LineId, counted, comment, shelf));
        if (_saving) return;

        _saving = true;
        BeginOperation();
        try
        {
            while (_pending.TryDequeue(out var next)) await SaveLineAsync(next);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving an inventory count line failed.");
            _pending.Clear();
            await ActivateAsync();
        }
        finally
        {
            _saving = false;
            EndOperation();
        }
    }

    private async Task SaveLineAsync(PendingCount count)
    {
        var saved = await _counts.RecordCountAsync(count.InventoryCountId, count.LineId, count.Counted, count.Comment,
                                                   count.ShelfCountedAt);
        if (saved.IsFailure) _dialogs.ShowFailure(saved, T("InventoryCount.Counted"));

        // The saved line as recorded — or as it still is, after a refusal — with its variance.
        var detail = await _counts.GetDetailAsync(count.InventoryCountId);
        if (detail.IsFailure || _detail?.Count.InventoryCountId != count.InventoryCountId) return;

        _detail = detail.Value;
        RefreshLine(count.LineId);
        RefreshProgress();
    }

    private void RefreshLine(Guid lineId)
    {
        if (_detail?.Lines.FirstOrDefault(l => l.LineId == lineId) is not { } fresh) return;
        static string N(decimal? v) => v?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;

        foreach (DataGridViewRow row in _lines.Rows)
        {
            if (row.Tag is not InventoryCountLineRow line || line.LineId != lineId) continue;

            row.Tag = fresh;
            _rendering = true;
            try
            {
                // A cell being typed in again keeps what is being typed.
                var editing = _lines.IsCurrentCellInEditMode && _lines.CurrentCell is { } cell && cell.RowIndex == row.Index
                    ? cell.OwningColumn?.Name
                    : null;
                if (editing != "counted") row.Cells["counted"].Value = N(fresh.CountedQuantity);
                if (editing != "comment") row.Cells["comment"].Value = fresh.Comment ?? string.Empty;
                row.Cells["stockAtCount"].Value = N(fresh.StockAtCount);
                row.Cells["countedAt"].Value = When(fresh.CountedAt);
                row.Cells["variance"].Value = N(fresh.Variance);
            }
            finally
            {
                _rendering = false;
            }

            _lines.InvalidateRow(row.Index);
            return;
        }
    }

    private void RefreshProgress()
    {
        if (_detail is null) return;
        var count = _detail.Count;

        _linesHeading.Text = $"{T("InventoryCount.Lines")} — {count.Number} · {count.Counted}/{count.Lines}";
        _rows = [.. _rows.Select(r => r.InventoryCountId == count.InventoryCountId ? count : r)];

        foreach (DataGridViewRow row in _list.Rows)
        {
            if (row.Tag is not InventoryCountRow r || r.InventoryCountId != count.InventoryCountId) continue;
            row.Tag = count;
            row.Cells["counted"].Value = count.Counted;
            row.Cells["variances"].Value = count.WithVariance;
        }
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
            string.Empty, string.Empty, string.Empty
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
            N(l.ExpectedQuantity), N(l.StockAtCount), N(l.CountedQuantity), When(l.CountedAt), N(l.Variance),
            l.Comment ?? string.Empty
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
