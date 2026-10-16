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
/// Fridge, freezer and room temperatures: the daily entry (twice a day is the usual practice), the
/// units without a reading today highlighted, and the monthly sheet per unit printed or exported
/// for the ISO 15189 file. A reading outside the range asks for a comment and opens a
/// non-conformity on its own.
/// </summary>
public sealed class TemperatureView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly TemperatureMonitoringService _temperatures;
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<TemperatureView>? _logger;

    private readonly DataGridView _units = UiTheme.Grid();
    private readonly DataGridView _readings = UiTheme.Grid();
    // Direction A: title and count, search and chip, the units card and the month's readings card.
    private readonly Label _heading = StockPage.Title();
    private readonly Label _count = StockPage.Count();
    private readonly StockSearchBox _search = new();
    private readonly StockCard _readingsCard;
    private readonly Button _record = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _newUnit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _editUnit = UiTheme.SecondaryButton(string.Empty);
    private readonly CheckBox _includeInactive = new StockChip();
    private readonly DateTimePicker _month = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "MM/yyyy", ShowUpDown = true
    };
    private readonly Button _pdf = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _csv = UiTheme.SecondaryButton(string.Empty);
    private readonly StockFooter _status = new();

    private IReadOnlyList<StorageUnitRow> _unitRows = [];
    private IReadOnlyList<TemperatureReadingRow> _readingRows = [];

    public TemperatureView(TemperatureMonitoringService temperatures, IListPdfRenderer lists, IDocumentOutput output,
                           IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                           ILogger<TemperatureView>? logger = null)
        : base(localizer, cultureManager)
    {
        _temperatures = temperatures;
        _lists = lists;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        _readingsCard = new StockCard(_readings);
        BuildLayout();

        _units.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadReadingsAsync());
        _month.ValueChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadReadingsAsync());
        // A unit taken out of service stays reachable: to reactivate it, and to print the sheets of
        // the months it was in use for the ISO 15189 file.
        _includeInactive.CheckedChanged += async (_, _) => await ActivateAsync();
        _record.Click += async (_, _) => await RunBusyAsync(RecordAsync);
        _units.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && CanRecord)
                await RunBusyAsync(RecordAsync);
        };
        _newUnit.Click += async (_, _) => await RunBusyAsync(() => EditUnitAsync(null));
        _editUnit.Click += async (_, _) => await RunBusyAsync(() => EditUnitAsync(CurrentUnit));
        _pdf.Click += async (_, _) => await RunBusyAsync(() => ExportAsync(pdf: true));
        _csv.Click += async (_, _) => await RunBusyAsync(() => ExportAsync(pdf: false));

        PermissionGate.Require(Permissions.Inventory.RecordTemperatures, _record);
        PermissionGate.Require(Permissions.Inventory.ManageStorageUnits, _newUnit, _editUnit);
    }

    public override string Title => T("Nav.Temperatures");

    private StorageUnitRow? CurrentUnit => _units.CurrentRow?.Tag as StorageUnitRow;

    /// <summary>
    /// Readings go only to a unit in service: an inactive one is listed to be reactivated or to
    /// print its past sheets, and the service refuses a reading on it.
    /// </summary>
    private bool CanRecord =>
        CurrentUnit is { IsActive: true } && PermissionGate.Allows(Permissions.Inventory.RecordTemperatures);

    private void RefreshButtons()
    {
        _record.Enabled = CanRecord;
        // The month's sheet is that of the unit selected: with none, the buttons did nothing.
        _pdf.Enabled = _csv.Enabled = CurrentUnit is not null;
        _editUnit.Enabled = CurrentUnit is not null && PermissionGate.Allows(Permissions.Inventory.ManageStorageUnits);
    }

    private void BuildLayout()
    {
        Padding = StockPage.ContentPadding;

        // The reading is the daily act: the primary, on Ctrl+N.
        var header = StockPage.Header(_heading, _count, _newUnit, _editUnit, _record);
        ShortcutHints.Attach(_record, NewKey);
        var filters = StockPage.Filters(_search, _includeInactive);

        foreach (var (name, weight) in new[]
                 {
                     ("name", 16), ("kind", 12), ("location", 12), ("range", 12), ("today", 10), ("last", 10),
                     ("lastat", 12), ("state", 14)
                 })
        {
            _units.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = name is "range" or "today" or "last" or "lastat" ? UiTheme.Monospace
                        : name is "state" ? UiTheme.SmallBold : UiTheme.Body
                }
            });
        }

        _units.Dock = DockStyle.Fill;
        _units.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _units.Rows[e.RowIndex].Tag is not StorageUnitRow row) return;
            if (!row.IsActive)
            {
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
            }
            else if (row.LastOutOfRange)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
            else if (row.MissingToday || row.IsActive && row.ReadingsToday < row.ReadingsPerDay)
            {
                // Not urgent: the readings of the day and the pill carry it, not the whole row.
                if (_units.Columns[e.ColumnIndex].Name is "today")
                {
                    e.CellStyle!.ForeColor = UiTheme.Abnormal;
                    e.CellStyle.Font = UiTheme.MonospaceBold;
                }
            }
        };
        StockGrid.Pills(_units, "state", tag => tag is StorageUnitRow row ? row switch
        {
            { IsActive: false } => StockPill.Waiting,
            { LastOutOfRange: true } => StockPill.Critical,
            { MissingToday: true } => StockPill.Warning,
            _ => StockPill.Done
        } : null);
        _search.Changed += (_, _) => StockGrid.Filter(_units, _search.Query);

        // The month shown, and its sheet printed or exported for the file.
        _month.Width = 100;
        _month.Font = UiTheme.Monospace;
        foreach (var button in new[] { _pdf, _csv }) StockPage.Action(button);
        _readingsCard.Heading.End(_month, _pdf, _csv);

        foreach (var (name, weight) in new[]
                 {
                     ("date", 14), ("value", 9), ("min", 9), ("max", 9), ("state", 10), ("by", 16), ("comment", 24),
                     ("nc", 10)
                 })
        {
            _readings.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = name is "date" or "value" or "min" or "max" or "nc" ? UiTheme.Monospace
                        : name is "state" ? UiTheme.SmallBold : UiTheme.Body
                }
            });
        }

        _readings.Dock = DockStyle.Fill;
        _readings.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _readings.Rows[e.RowIndex].Tag is not TemperatureReadingRow row) return;
            if (row.IsOutOfRange)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };

        StockGrid.Pills(_readings, "state", tag => tag is TemperatureReadingRow row
                            ? row.IsOutOfRange ? StockPill.Critical : StockPill.Done
                            : null);

        Controls.Add(StockPage.Stack(new StockCard(_units, heading: false, footer: _status), _readingsCard, 240));
        Controls.Add(filters);
        Controls.Add(header);
    }

    private const string NewKey = "Ctrl+N";

    /// <summary>Ctrl+N records a reading for the selected unit, as the key drawn on the button says.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N) && _record.Enabled && _record.Visible)
        {
            _record.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private IReadOnlyList<string> ReadingHeaders() =>
    [
        T("Field.Date"), T("Temperatures.Value"), T("Temperatures.PeriodMin"), T("Temperatures.PeriodMax"),
        T("Field.Status"), T("Temperatures.By"), T("Field.Comment"), T("Temperatures.NonConformity")
    ];

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.Temperatures");
        _search.Placeholder = T("StocksUi.SearchUnits");
        _readingsCard.Title.Text = T("Temperatures.Readings");
        _record.Text = T("Temperatures.Record");
        _newUnit.Text = T("Temperatures.NewUnit");
        _editUnit.Text = T("Temperatures.EditUnit");
        _includeInactive.Text = T("Temperatures.IncludeInactive");
        _pdf.Text = T("Temperatures.MonthPdf");
        _csv.Text = T("Temperatures.MonthCsv");

        var unitHeaders = new[]
        {
            T("Field.Name"), T("Temperatures.Kind"), T("Temperatures.Location"), T("Temperatures.Range"),
            T("Temperatures.Today"), T("Temperatures.Last"), T("Field.Date"), T("Field.Status")
        };
        for (var i = 0; i < _units.Columns.Count; i++) _units.Columns[i].HeaderText = unitHeaders[i];

        var readingHeaders = ReadingHeaders();
        for (var i = 0; i < _readings.Columns.Count; i++) _readings.Columns[i].HeaderText = readingHeaders[i];

        if (_unitRows.Count > 0) RenderUnits();
        if (_readingRows.Count > 0) RenderReadings();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _temperatures.GetUnitsAsync(_includeInactive.Checked, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            var selected = CurrentUnit?.StorageUnitId;
            _unitRows = result.Value;
            RenderUnits(selected);
            await LoadReadingsAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the storage units failed.");
        }
    }

    private static string C(decimal? value) =>
        value is { } v ? v.ToString("0.0", CultureInfo.CurrentCulture) + " °C" : string.Empty;

    private void RenderUnits(Guid? select = null)
    {
        _units.Rows.Clear();
        foreach (var u in _unitRows)
        {
            var state = !u.IsActive ? T("Temperatures.Inactive")
                      : u.LastOutOfRange ? T("Temperatures.OutOfRange")
                      : u.MissingToday ? T("Temperatures.MissingToday")
                      : T("Temperatures.InRange");

            var index = _units.Rows.Add(u.Name, T($"StorageUnitKind.{u.Kind}"), u.Location ?? string.Empty,
                                        $"{C(u.MinimumCelsius)} – {C(u.MaximumCelsius)}",
                                        $"{u.ReadingsToday} / {u.ReadingsPerDay}", C(u.LastCelsius),
                                        u.LastReadAt?.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture) ?? string.Empty,
                                        state);
            _units.Rows[index].Tag = u;
            if (u.StorageUnitId == select) _units.CurrentCell = _units.Rows[index].Cells[0];
        }

        StockGrid.Filter(_units, _search.Query);
        _count.Text = T("StocksUi.Units", _unitRows.Count);
        var missing = _unitRows.Count(u => u.MissingToday);
        _status.Text = missing > 0 ? T("Temperatures.MissingCount", missing) : string.Empty;
        _status.ForeColor = missing > 0 ? UiTheme.Abnormal : UiTheme.TextSecondary;
        RefreshButtons();
    }

    private async Task LoadReadingsAsync(CancellationToken ct = default)
    {
        if (CurrentUnit is not { } unit) { _readingRows = []; RenderReadings(); return; }

        var month = await _temperatures.GetMonthAsync(unit.StorageUnitId, _month.Value.Year, _month.Value.Month, ct);
        if (month.IsFailure) { _dialogs.ShowFailure(month); return; }

        _readingRows = month.Value;
        RenderReadings();
    }

    private IReadOnlyList<string> Cells(TemperatureReadingRow r) =>
    [
        r.ReadAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture), C(r.Celsius),
        C(r.PeriodMinimumCelsius), C(r.PeriodMaximumCelsius),
        r.IsOutOfRange ? T("Temperatures.OutOfRange") : T("Temperatures.InRange"),
        r.ByUserName, r.Comment ?? string.Empty, r.NonConformityReference ?? string.Empty
    ];

    private void RenderReadings()
    {
        _readings.Rows.Clear();
        foreach (var r in _readingRows)
        {
            var index = _readings.Rows.Add([.. Cells(r)]);
            _readings.Rows[index].Tag = r;
        }

        _readingsCard.Note.Text = CurrentUnit is { } unit ? $"{unit.Name} · {_readingRows.Count}" : string.Empty;
        RefreshButtons();
    }

    private async Task RecordAsync()
    {
        if (CurrentUnit is not { IsActive: true } unit) return;

        using var dialog = new TemperatureReadingDialog(Localizer, _dialogs, unit);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var recorded = await _temperatures.RecordReadingAsync(dialog.Request);
        if (recorded.IsFailure) { _dialogs.ShowFailure(recorded, _record.Text); return; }

        if (recorded.Value.OutOfRange)
            _dialogs.Warn(T("Temperatures.ExcursionRecorded", unit.Name, recorded.Value.NonConformityReference), _record.Text);

        await ActivateAsync();
    }

    private async Task EditUnitAsync(StorageUnitRow? row)
    {
        using var dialog = new StorageUnitDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Request is null) return;

        var saved = await _temperatures.SaveUnitAsync(dialog.Request);
        if (saved.IsFailure) { _dialogs.ShowFailure(saved, row is null ? _newUnit.Text : _editUnit.Text); return; }

        await ActivateAsync();
    }

    /// <summary>The month's sheet of the unit: every reading, who took it, and the excursions.</summary>
    private async Task ExportAsync(bool pdf)
    {
        if (CurrentUnit is not { } unit) return;

        var month = _month.Value.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var rows = _readingRows.Select(Cells).ToList();
        var excursions = _readingRows.Count(r => r.IsOutOfRange);

        byte[] content = pdf
            ? _lists.Render(new ListDocument(
                T("Temperatures.SheetTitle", unit.Name, month),
                T("Temperatures.SheetCaption", T($"StorageUnitKind.{unit.Kind}"),
                  $"{C(unit.MinimumCelsius)} – {C(unit.MaximumCelsius)}", unit.Location ?? "—",
                  _readingRows.Count, excursions,
                  _readingRows.Count > 0 ? C(_readingRows.Min(r => r.PeriodMinimumCelsius ?? r.Celsius)) : "—",
                  _readingRows.Count > 0 ? C(_readingRows.Max(r => r.PeriodMaximumCelsius ?? r.Celsius)) : "—"),
                ReadingHeaders(), rows, Localizer.CurrentLanguage == Domain.Common.AppLanguage.Arabic))
            : StockCsv.Table(ReadingHeaders(), rows);

        var opened = await _output.OpenAsync(content, pdf ? "pdf" : "csv");
        if (opened.IsFailure) _dialogs.ShowFailure(opened, pdf ? _pdf.Text : _csv.Text);
    }
}

/// <summary>A temperature reading: the value, the min/max of the period when the thermometer gives them.</summary>
internal sealed class TemperatureReadingDialog : MasterDataEditor
{
    private readonly StorageUnitRow _unit;
    private readonly TextBox _value, _min, _max, _comment;

    internal TemperatureReadingRequest? Request { get; private set; }

    internal TemperatureReadingDialog(ILocalizer localizer, IUserDialogs dialogs, StorageUnitRow unit)
        : base(localizer, dialogs, "Temperatures.Record", unit.Name, 520, 260)
    {
        _unit = unit;

        _value = TextField("Temperatures.Value", 20, 14, 150, null, latin: true, mono: true, right: true);
        _min = TextField("Temperatures.PeriodMin", 190, 14, 140, null, latin: true, mono: true, right: true);
        _max = TextField("Temperatures.PeriodMax", 350, 14, 140, null, latin: true, mono: true, right: true);
        _comment = TextField("Field.Comment", 20, 70, 470);
        _comment.MaxLength = 500;   // the column; beyond, the save failed with « Erreur inattendue »

        var notice = Notice(20, 124, 470, 60, "Temperatures.ReadingNotice");
        notice.Text = localizer.Format("Temperatures.ReadingNotice",
                                       unit.MinimumCelsius.ToString("0.0", CultureInfo.CurrentCulture),
                                       unit.MaximumCelsius.ToString("0.0", CultureInfo.CurrentCulture));

        Buttons(200, 520);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.TemperatureReading");
    }

    protected override void Accept()
    {
        if (DecimalOrNull(_value) is not { } value)
        {
            Dialogs.Warn(Localizer["Temperature_ValueRequired"], Text);
            _value.Focus();
            return;
        }

        var min = DecimalOrNull(_min);
        var max = DecimalOrNull(_max);

        var outOfRange = value < _unit.MinimumCelsius || value > _unit.MaximumCelsius
                         || min < _unit.MinimumCelsius || max > _unit.MaximumCelsius;

        if (outOfRange && string.IsNullOrWhiteSpace(_comment.Text))
        {
            Dialogs.Warn(Localizer["Temperature_CommentRequired"], Text);
            _comment.Focus();
            return;
        }

        Request = new TemperatureReadingRequest
        {
            StorageUnitId = _unit.StorageUnitId,
            Celsius = value,
            PeriodMinimumCelsius = min,
            PeriodMaximumCelsius = max,
            Comment = Blank(_comment)
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A monitored fridge, freezer or room, with its acceptable range.</summary>
internal sealed class StorageUnitDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _name, _location, _serial, _min, _max;
    private readonly ComboBox _kind;
    private readonly NumericUpDown _perDay;
    private readonly CheckBox _active;

    internal StorageUnitRequest? Request { get; private set; }

    internal StorageUnitDialog(ILocalizer localizer, IUserDialogs dialogs, StorageUnitRow? row)
        : base(localizer, dialogs, row is null ? "Temperatures.NewUnit" : "Temperatures.EditUnit", row?.Name, 560, 300)
    {
        _id = row?.StorageUnitId;
        static string N(decimal v) => v.ToString("0.##", CultureInfo.CurrentCulture);

        _name = TextField("Field.Name", 20, 14, 250, row?.Name);
        _kind = Choice("Temperatures.Kind", 290, 14, 250, row?.Kind ?? StorageUnitKind.Refrigerator, "StorageUnitKind");

        var (defaultMin, defaultMax) = StorageUnit.DefaultRange(row?.Kind ?? StorageUnitKind.Refrigerator);
        _min = TextField("Temperatures.Minimum", 20, 70, 120, N(row?.MinimumCelsius ?? defaultMin), latin: true, right: true);
        _max = TextField("Temperatures.Maximum", 160, 70, 120, N(row?.MaximumCelsius ?? defaultMax), latin: true, right: true);
        _perDay = Number("Temperatures.ReadingsPerDay", 300, 70, 120, row?.ReadingsPerDay ?? 2, 24m);

        _location = TextField("Temperatures.Location", 20, 126, 250, row?.Location);
        _serial = TextField("Temperatures.SerialNumber", 290, 126, 250, row?.SerialNumber, latin: true);
        // Bounded to the columns: beyond, the save failed with « Erreur inattendue ».
        _name.MaxLength = _location.MaxLength = 100;
        _serial.MaxLength = 60;
        _active = Check("Field.Active", 20, 186, row?.IsActive ?? true);

        // A new unit takes the usual range of its kind; an existing one keeps what was set.
        _kind.SelectedIndexChanged += (_, _) =>
        {
            if (_id is not null) return;
            var (min, max) = StorageUnit.DefaultRange(Selected<StorageUnitKind>(_kind));
            _min.Text = N(min);
            _max.Text = N(max);
        };

        Buttons(240, 560);

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.StorageUnit");
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            Dialogs.Warn(Localizer["StorageUnit_NameRequired"], Text);
            _name.Focus();
            return;
        }

        if (DecimalOrNull(_min) is not { } min || DecimalOrNull(_max) is not { } max || min >= max)
        {
            Dialogs.Warn(Localizer["StorageUnit_RangeInvalid"], Text);
            _min.Focus();
            return;
        }

        Request = new StorageUnitRequest
        {
            StorageUnitId = _id,
            Name = _name.Text.Trim(),
            Kind = Selected<StorageUnitKind>(_kind),
            Location = Blank(_location),
            SerialNumber = Blank(_serial),
            MinimumCelsius = min,
            MaximumCelsius = max,
            ReadingsPerDay = Math.Max(1, (int)_perDay.Value),
            IsActive = _active.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
