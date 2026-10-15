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
    private readonly Label _heading = new();
    private readonly Label _readingsHeading = new();
    private readonly Button _record = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _newUnit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _editUnit = UiTheme.SecondaryButton(string.Empty);
    private readonly DateTimePicker _month = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "MM/yyyy", ShowUpDown = true
    };
    private readonly Button _pdf = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _csv = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

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

        BuildLayout();

        _units.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadReadingsAsync());
        _month.ValueChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadReadingsAsync());
        _record.Click += async (_, _) => await RunBusyAsync(RecordAsync);
        _units.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && PermissionGate.Allows(Permissions.Inventory.RecordTemperatures))
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

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 240,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        var bar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = UiTheme.Surface };
        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 0);

        _record.Bounds = new Rectangle(0, 30, 200, UiTheme.ButtonHeight);
        _newUnit.Bounds = new Rectangle(210, 30, 170, UiTheme.ButtonHeight);
        _editUnit.Bounds = new Rectangle(390, 30, 170, UiTheme.ButtonHeight);
        bar.Controls.AddRange([_heading, _record, _newUnit, _editUnit]);

        foreach (var (name, weight) in new[]
                 {
                     ("name", 16), ("kind", 12), ("location", 12), ("range", 12), ("today", 10), ("last", 10),
                     ("lastat", 12), ("state", 14)
                 })
        {
            _units.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        _units.Dock = DockStyle.Fill;
        _units.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _units.Rows[e.RowIndex].Tag is not StorageUnitRow row) return;
            if (row.LastOutOfRange)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
            else if (row.MissingToday || row.IsActive && row.ReadingsToday < row.ReadingsPerDay)
            {
                e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
                e.CellStyle.ForeColor = UiTheme.Abnormal;
            }
        };

        var top = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        top.Controls.Add(_units);
        top.Controls.Add(bar);

        var readingsBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };
        _readingsHeading.Font = UiTheme.SectionTitle;
        _readingsHeading.ForeColor = UiTheme.TextPrimary;
        // Fixed width: the heading grows with the selection and must not run under the buttons.
        _readingsHeading.AutoSize = false;
        _readingsHeading.AutoEllipsis = true;
        _readingsHeading.Bounds = new Rectangle(0, 10, 370, 26);
        _month.Bounds = new Rectangle(380, 8, 100, UiTheme.InputHeight);
        _pdf.Bounds = new Rectangle(490, 6, 200, UiTheme.ButtonHeight);
        _csv.Bounds = new Rectangle(700, 6, 200, UiTheme.ButtonHeight);
        readingsBar.Controls.AddRange([_readingsHeading, _month, _pdf, _csv]);

        foreach (var (name, weight) in new[]
                 {
                     ("date", 14), ("value", 9), ("min", 9), ("max", 9), ("state", 10), ("by", 16), ("comment", 24),
                     ("nc", 10)
                 })
        {
            _readings.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
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

        _status.Dock = DockStyle.Bottom;
        _status.Height = 30;
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        bottom.Controls.Add(_readings);
        bottom.Controls.Add(readingsBar);
        bottom.Controls.Add(_status);

        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);
        Controls.Add(split);
    }

    private IReadOnlyList<string> ReadingHeaders() =>
    [
        T("Field.Date"), T("Temperatures.Value"), T("Temperatures.PeriodMin"), T("Temperatures.PeriodMax"),
        T("Field.Status"), T("Temperatures.By"), T("Field.Comment"), T("Temperatures.NonConformity")
    ];

    protected override void ApplyTexts()
    {
        _heading.Text = T("Nav.Temperatures");
        _record.Text = T("Temperatures.Record");
        _newUnit.Text = T("Temperatures.NewUnit");
        _editUnit.Text = T("Temperatures.EditUnit");
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
            var result = await _temperatures.GetUnitsAsync(false, ct);
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
            var state = u.LastOutOfRange ? T("Temperatures.OutOfRange")
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

        var missing = _unitRows.Count(u => u.MissingToday);
        _status.Text = missing > 0 ? T("Temperatures.MissingCount", missing) : string.Empty;
        _status.ForeColor = missing > 0 ? UiTheme.Abnormal : UiTheme.TextSecondary;
        _editUnit.Enabled = CurrentUnit is not null && PermissionGate.Allows(Permissions.Inventory.ManageStorageUnits);
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

        _readingsHeading.Text = CurrentUnit is { } unit
            ? $"{T("Temperatures.Readings")} — {unit.Name} · {_readingRows.Count}"
            : T("Temperatures.Readings");
        _editUnit.Enabled = CurrentUnit is not null && PermissionGate.Allows(Permissions.Inventory.ManageStorageUnits);
    }

    private async Task RecordAsync()
    {
        if (CurrentUnit is not { } unit) return;

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

        var notice = Notice(20, 124, 470, 60, "Temperatures.ReadingNotice");
        notice.Text = localizer.Format("Temperatures.ReadingNotice",
                                       unit.MinimumCelsius.ToString("0.0", CultureInfo.CurrentCulture),
                                       unit.MaximumCelsius.ToString("0.0", CultureInfo.CurrentCulture));

        Buttons(200, 520);
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
