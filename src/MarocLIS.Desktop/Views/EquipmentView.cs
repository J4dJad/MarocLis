using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The equipment register: status, maintenance and calibration.
/// <para>
/// ISO 15189 §5.3 wants an inventory of every item of equipment that affects a result, with its
/// maintenance and calibration history. The screen is built around the one question an assessor
/// asks first — <em>is anything overdue?</em> — so overdue calibrations sort to the top and the
/// panel that opens with them is the intervention history, not the nameplate details.
/// </para>
/// <para>
/// Returning an instrument to service is offered, but the domain refuses it while a calibration is
/// lapsed. That refusal surfaces here as a message, not as a disabled button: an operator who is
/// told "not available" learns nothing, whereas one who is told the calibration is overdue knows
/// what to do next.
/// </para>
/// </summary>
public sealed class EquipmentView : LocalizedView
{
    private readonly EquipmentService _equipment;
    private readonly NonConformityService _nonConformities;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<EquipmentView>? _logger;

    private readonly DataGridView _grid = UiTheme.Grid();
    // Direction A: title and count, search and chip, the register card and, beside it, the
    // selected instrument's card with its dates and what can be recorded on it.
    private readonly Label _heading = StockPage.Title();
    private readonly Label _count = StockPage.Count();
    private readonly StockSearchBox _search = new();
    private readonly CheckBox _onlyDue = new StockChip();
    private readonly StockCard _asideCard;
    private readonly StockFacts _facts = new() { Dock = DockStyle.Top };
    private readonly PillLabel _statusPill = new();

    private readonly Button _maintenance = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _calibration = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _status = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);

    private readonly Label _detail = new();
    private readonly Label _message = new();

    private IReadOnlyList<EquipmentRow> _rows = [];

    public EquipmentView(EquipmentService equipment, NonConformityService nonConformities,
                         IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                         ILogger<EquipmentView>? logger = null)
        : base(localizer, cultureManager)
    {
        _equipment = equipment;
        _nonConformities = nonConformities;
        _dialogs = dialogs;
        _logger = logger;

        _asideCard = new StockCard(new Panel { BackColor = UiTheme.SurfaceRaised });
        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Equipment");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = StockPage.ContentPadding;

        var header = StockPage.Header(_heading, _count, _refresh);
        _onlyDue.Checked = true;   // The overdue list is the reason the screen exists.
        var filters = StockPage.Filters(_search, _onlyDue);

        BuildColumns();
        var registerCard = new StockCard(_grid, heading: false);

        // The selected instrument: its status, what is wrong with it, its facts and its actions,
        // the maintenance being the everyday one.
        _asideCard.Heading.End(_statusPill);

        _detail.Font = UiTheme.Body;
        _detail.ForeColor = UiTheme.TextSecondary;
        _detail.AutoSize = false;
        _detail.Dock = DockStyle.Top;
        _detail.Height = 22;
        _detail.AutoEllipsis = true;

        _message.Font = UiTheme.SmallBold;
        _message.ForeColor = UiTheme.TextSecondary;
        _message.AutoSize = false;
        _message.Dock = DockStyle.Top;
        _message.Height = 56;

        var body = (Panel)_asideCard.Controls[0];
        body.Padding = new Padding(15, 0, 15, 4);
        body.AutoScroll = true;
        body.Controls.Add(_facts);
        body.Controls.Add(_message);
        body.Controls.Add(_detail);

        foreach (var button in new[] { _status, _calibration, _maintenance }) StockPage.Action(button);
        var actions = StockPage.ActionBar(_status, _calibration, _maintenance);
        _asideCard.Controls.Add(actions);
        actions.BringToFront();
        body.BringToFront();

        // The aside keeps its width; the register takes the rest.
        _asideCard.Dock = DockStyle.Right;
        _asideCard.Width = 380;
        var gap = new Panel { Dock = DockStyle.Right, Width = 12, BackColor = UiTheme.Surface };
        Controls.Add(registerCard);
        Controls.Add(gap);
        Controls.Add(_asideCard);

        // Arabic: the aside goes to the left, the side the reading ends on.
        _asideCard.RightToLeftChanged += (_, _) =>
            gap.Dock = _asideCard.Dock = _asideCard.RightToLeft == RightToLeft.Yes ? DockStyle.Left : DockStyle.Right;
        Controls.Add(filters);
        Controls.Add(header);
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("code", 9, UiTheme.MonospaceBold);
        Add("name", 20);
        Add("kind", 10, UiTheme.Small);
        Add("status", 11, UiTheme.SmallBold);
        Add("serial", 11, UiTheme.Monospace);
        Add("location", 10, UiTheme.Small);
        Add("maintenance", 10, UiTheme.Monospace);
        Add("calibration", 10, UiTheme.Monospace);
        Add("contract", 9, UiTheme.Monospace);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyDue.CheckedChanged += async (_, _) => await LoadAsync();

        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.CellFormatting += OnCellFormatting;
        StockGrid.Pills(_grid, "status", tag => tag is EquipmentRow row ? StatusPill(row.Status) : null);
        _search.Changed += (_, _) => StockGrid.Filter(_grid, _search.Query);

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _maintenance.Click += async (_, _) => await RunBusyAsync(RecordMaintenanceAsync);
        _calibration.Click += async (_, _) => await RunBusyAsync(RecordCalibrationAsync);
        _status.Click += async (_, _) => await RunBusyAsync(ChangeStatusAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Equipment.RecordMaintenance, _maintenance);
        PermissionGate.Require(Permissions.Equipment.Calibrate, _calibration);
        PermissionGate.Require(Permissions.Equipment.Manage, _status);
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        if (_grid.Rows[e.RowIndex].Tag is not EquipmentRow row) return;

        // A lapsed calibration is the serious one: results produced on that instrument are not
        // defensible, so it is coloured as critical rather than as a reminder (EquipmentScreenRules).
        switch (EquipmentScreenRules.RowInk(row))
        {
            case StockInk.CriticalBold:
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
                e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
                return;

            case StockInk.Muted:
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
                return;
        }

        // A reminder, not an emergency: the late date and the status pill carry it, not the row.
        var column = _grid.Columns[e.ColumnIndex].Name;
        var ink = column switch
        {
            "maintenance" => EquipmentScreenRules.MaintenanceInk(row),
            "contract" => EquipmentScreenRules.ContractInk(row),
            _ => StockInk.Plain
        };
        if (ink == StockInk.AbnormalBold)
        {
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
            e.CellStyle.Font = UiTheme.MonospaceBold;
        }
    }

    private static StockPill StatusPill(EquipmentStatus status) => StockPill.Of(EquipmentScreenRules.StatusTone(status));

    protected override void ApplyTexts()
    {
        _heading.Text = T("Equipment.Title");
        _search.Placeholder = T("StocksUi.SearchEquipment");
        _onlyDue.Text = T("Equipment.OnlyDue");
        _maintenance.Text = T("Equipment.RecordMaintenance");
        _calibration.Text = T("Equipment.RecordCalibration");
        _status.Text = T("Equipment.ChangeStatus");
        _refresh.Text = T("Action.Refresh");

        if (_grid.Columns.Count > 0)
        {
            _grid.Column("code").HeaderText = T("Field.Code");
            _grid.Column("name").HeaderText = T("Field.Name");
            _grid.Column("kind").HeaderText = T("Equipment.Kind");
            _grid.Column("status").HeaderText = T("Field.Status");
            _grid.Column("serial").HeaderText = T("Equipment.SerialNumber");
            _grid.Column("location").HeaderText = T("Equipment.Location");
            _grid.Column("maintenance").HeaderText = T("Equipment.NextMaintenance");
            _grid.Column("calibration").HeaderText = T("Equipment.NextCalibration");
            _grid.Column("contract").HeaderText = T("Equipment.Contract");
        }

        if (_rows.Count > 0) Render((_grid.CurrentRow?.Tag as EquipmentRow)?.InstrumentId);
        ShowDetail();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _equipment.GetEquipmentAsync(_onlyDue.Checked, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            // The instrument just serviced stays current, with its detail beside it.
            var keep = (_grid.CurrentRow?.Tag as EquipmentRow)?.InstrumentId;

            _rows = result.Value;
            Render(keep);
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the equipment register failed.");
        }
    }

    private void Render(Guid? keep = null)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var index = _grid.Rows.Add(
                row.Code,
                row.Name.For(Localizer.CurrentLanguage),
                T(EquipmentScreenRules.KindKey(row.Kind)),
                T(EquipmentScreenRules.StatusKey(row.Status)),
                row.SerialNumber ?? string.Empty,
                row.Location ?? string.Empty,
                EquipmentScreenRules.MaintenanceDue(row),
                EquipmentScreenRules.CalibrationDue(row),
                EquipmentScreenRules.ContractDue(row));

            _grid.Rows[index].Tag = row;
        }

        _grid.ResumeLayout(performLayout: true);

        _count.Text = EquipmentScreenRules.Count(_rows, T);

        var kept = _grid.Rows.Cast<DataGridViewRow>()
                        .FirstOrDefault(r => keep is not null && r.Tag is EquipmentRow row && row.InstrumentId == keep);
        if (kept is { Index: > 0 }) _grid.MakeCurrent(kept);
        else if (_grid.Rows.Count > 0) _grid.SelectFirstRow();
        StockGrid.Filter(_grid, _search.Query);

        ShowDetail();
    }

    private void ShowDetail()
    {
        var row = _grid.CurrentRow?.Tag as EquipmentRow;

        // Decommissioning is final: nothing more is recorded on the instrument, and its status
        // no longer changes (the service refuses it too).
        var actions = EquipmentScreenRules.Actions(p => PermissionGate.Allows(p), row);
        _maintenance.Enabled = actions.Maintenance;
        _calibration.Enabled = actions.Calibration;
        _status.Enabled = actions.Status;

        if (row is null)
        {
            _detail.Text = string.Empty;
            _message.Text = string.Empty;
            _asideCard.Title.Text = T("StocksUi.NoInstrument");
            _asideCard.Note.Text = string.Empty;
            _statusPill.Show(string.Empty, EntryPaint.Waiting);
            _facts.Show([]);
            return;
        }

        var pill = StatusPill(row.Status);
        _asideCard.Title.Text = row.Name.For(Localizer.CurrentLanguage);
        _asideCard.Note.Text = row.Code;
        _statusPill.Show($"{pill.Symbol} {T(EquipmentScreenRules.StatusKey(row.Status))}", pill.Tone);
        _facts.Show([.. EquipmentScreenRules.Facts(row, T).Select(f => (f.Label, f.Value, f.Mono))]);

        _detail.Text = EquipmentScreenRules.Detail(row, T);

        // Says what is wrong, rather than leaving the operator to compare two dates in a row.
        if (EquipmentScreenRules.Warning(row) is { } warning)
        {
            _message.ForeColor = warning.Ink switch
            {
                StockInk.Muted => UiTheme.TextMuted,
                StockInk.Critical => UiTheme.Critical,
                _ => UiTheme.Abnormal
            };
            _message.Text = T(warning.Key);
        }
        else
        {
            _message.ForeColor = UiTheme.Normal;
            _message.Text = string.Empty;
        }
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task RecordMaintenanceAsync()
    {
        if (_grid.CurrentRow?.Tag is not EquipmentRow row) return;

        using var dialog = new MaintenanceDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (dialog.Entry is not { } entry) return;

        var result = await _equipment.RecordMaintenanceAsync(
            row.InstrumentId, entry.Kind, entry.Description, entry.DowntimeHours,
            entry.PartsReplaced, entry.PostCheckPassed, entry.ExternalTechnician);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadAsync();

        if (!entry.PostCheckPassed) await OfferNonConformityAsync(row);
    }

    /// <summary>
    /// A failed check after an intervention: the service has taken an operational instrument out
    /// of service; the operator is told so and offered the non-conformity, opened on the instrument.
    /// </summary>
    private async Task OfferNonConformityAsync(EquipmentRow row)
    {
        var question = EquipmentScreenRules.PostCheckQuestion(row, T);

        if (!EquipmentScreenRules.MayReportNonConformity(p => PermissionGate.Allows(p)))
        {
            if (EquipmentScreenRules.TakenOutOfService(row)) _dialogs.Info(T("Equipment.PostCheckFailedOutOfService"));
            return;
        }

        if (!_dialogs.Confirm(question, T("Equipment.RecordMaintenance"))) return;

        using var dialog = new NonConformityDialog(Localizer, _dialogs);
        dialog.Prefill(NonConformityCategory.Equipment, NonConformitySeverity.Major, DetectionSource.StaffObservation,
                       EquipmentScreenRules.NonConformityDescription(row, Localizer.CurrentLanguage, T),
                       row.InstrumentId);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var opened = await _nonConformities.OpenAsync(dialog.Request);
        if (opened.IsFailure)
        {
            _dialogs.ShowFailure(opened);
            return;
        }

        _dialogs.Info($"{T("NonConformity.Reference")} {opened.Value}");
    }

    private async Task RecordCalibrationAsync()
    {
        if (_grid.CurrentRow?.Tag is not EquipmentRow row) return;

        using var dialog = new CalibrationDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (dialog.Entry is not { } entry) return;

        var result = await _equipment.RecordCalibrationAsync(
            row.InstrumentId, testParameterId: null, entry.Outcome, entry.CalibratorLotNumber,
            entry.ValidUntil, entry.Slope, entry.Intercept, entry.Comment);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadAsync();
    }

    private async Task ChangeStatusAsync()
    {
        if (_grid.CurrentRow?.Tag is not EquipmentRow row) return;

        using var dialog = new EquipmentStatusDialog(Localizer, _dialogs, row);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _equipment.ChangeStatusAsync(row.InstrumentId, dialog.Status,
                                                         dialog.Reason);

        if (result.IsFailure)
        {
            // The domain refuses a return to service while a calibration is lapsed. Shown as the
            // refusal it is, so the operator knows the next step is to recalibrate.
            _dialogs.ShowFailure(result, T("Equipment.ChangeStatus"));
            return;
        }

        await LoadAsync();
    }
}

/// <summary>Recording an intervention. The description is trilingual because it goes in the file.</summary>
internal sealed class MaintenanceDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _kind = new();
    private readonly TextBox _descriptionFr = UiTheme.Input(440);
    private readonly TextBox _descriptionAr = UiTheme.Input(440);
    private readonly TextBox _descriptionEn = UiTheme.Input(440);
    private readonly TextBox _downtime = UiTheme.Input(100);
    private readonly TextBox _parts = UiTheme.Input(200);
    private readonly TextBox _technician = UiTheme.Input(200);
    private readonly CheckBox _postCheck = new();

    private MaintenanceKind Kind => (MaintenanceKind)(_kind.SelectedItem as EnumChoice)!.Value;

    /// <summary>The intervention read and checked by EquipmentScreenRules, once « Enregistrer » is accepted.</summary>
    internal MaintenanceEntry? Entry { get; private set; }

    internal MaintenanceDialog(ILocalizer localizer, IUserDialogs dialogs, EquipmentRow row)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        // Bounded to the columns: beyond, the save failed with « Erreur inattendue » after the dialog closed.
        _descriptionFr.MaxLength = _descriptionAr.MaxLength = _descriptionEn.MaxLength = EquipmentScreenRules.DescriptionMaxLength;
        _parts.MaxLength = EquipmentScreenRules.PartsMaxLength;
        _technician.MaxLength = EquipmentScreenRules.TechnicianMaxLength;
        _downtime.MaxLength = EditorValues.MeasureMaxLength;

        Text = $"{localizer["Equipment.RecordMaintenance"]} — {row.Code}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 420);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var y = 14;

        Label Field(string key, int top, int left = 20)
        {
            var label = UiTheme.FieldLabel(localizer[key]);
            label.Location = new Point(left, top);
            Controls.Add(label);
            return label;
        }

        Field("Equipment.MaintenanceKind", y);
        _kind.DropDownStyle = ComboBoxStyle.DropDownList;
        _kind.FlatStyle = FlatStyle.Flat;
        _kind.Bounds = new Rectangle(20, y + 18, 200, UiTheme.InputHeight);

        foreach (var kind in Enum.GetValues<MaintenanceKind>())
            _kind.Items.Add(new EnumChoice((int)kind, localizer[$"MaintenanceKind.{kind}"]));

        // A breakdown repair is the commonest thing recorded off the cuff; preventive work is
        // usually recorded from a plan, so corrective is the safer default to land on.
        _kind.SelectedIndex = (int)EquipmentScreenRules.NewMaintenance().Kind;

        Field("Equipment.DowntimeHours", y, 250);
        _downtime.Location = new Point(250, y + 18);
        _downtime.Tag = UiTags.LatinValue;
        _downtime.TextAlign = HorizontalAlignment.Right;

        y += 54;
        Field("Field.DescriptionFr", y);
        _descriptionFr.Location = new Point(20, y + 18);

        y += 54;
        Field("Field.DescriptionAr", y);
        _descriptionAr.Location = new Point(20, y + 18);
        _descriptionAr.RightToLeft = RightToLeft.Yes;

        y += 54;
        Field("Field.DescriptionEn", y);
        _descriptionEn.Location = new Point(20, y + 18);

        y += 54;
        Field("Equipment.PartsReplaced", y);
        _parts.Location = new Point(20, y + 18);

        Field("Equipment.ExternalTechnician", y, 250);
        _technician.Location = new Point(250, y + 18);

        y += 56;

        _postCheck.Text = localizer["Equipment.PostCheckPassed"];
        _postCheck.Checked = EquipmentScreenRules.NewMaintenance().PostCheckPassed;
        _postCheck.AutoSize = true;
        _postCheck.Font = UiTheme.Body;
        _postCheck.Location = new Point(20, y);
        Controls.Add(_postCheck);

        // Spelt out: an intervention with a failed post-check has not returned the instrument to
        // service, and unticking this is the record of that.
        var note = new Label
        {
            Text = localizer["Equipment.PostCheckExplanation"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, y + 22, 460, 32)
        };
        Controls.Add(note);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 376, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 376, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_kind, _downtime, _descriptionFr, _descriptionAr, _descriptionEn,
                           _parts, _technician, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.Maintenance");
    }

    private void Accept()
    {
        // A description is required, and « 2 h » or « 1e20 » in the downtime is no number
        // (EquipmentScreenRules.Check, as the web's form).
        var (refusal, entry) = EquipmentScreenRules.Check(new MaintenanceForm
        {
            Kind = Kind,
            DescriptionFr = _descriptionFr.Text,
            DescriptionAr = _descriptionAr.Text,
            DescriptionEn = _descriptionEn.Text,
            Downtime = _downtime.Text,
            PartsReplaced = _parts.Text,
            Technician = _technician.Text,
            PostCheckPassed = _postCheck.Checked
        });

        if (refusal is not null)
        {
            _dialogs.Warn(_localizer.Format(refusal.Key, refusal.Typed ?? string.Empty));
            (refusal.Field == EquipmentScreenRules.DowntimeField ? _downtime : _descriptionFr).Focus();
            return;
        }

        Entry = entry;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Recording a calibration. The calibrator lot is the traceability link to a reference material,
/// which is why the domain refuses a passing calibration without one.
/// </summary>
internal sealed class CalibrationDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _outcome = new();
    private readonly TextBox _calibratorLot = UiTheme.Input(200);
    private readonly DateTimePicker _validUntil = new();
    private readonly CheckBox _hasValidity = new();
    private readonly TextBox _slope = UiTheme.Input(120);
    private readonly TextBox _intercept = UiTheme.Input(120);
    private readonly TextBox _comment = UiTheme.Input(440);
    private readonly Label _lotNotice = new();

    private CalibrationOutcome Outcome =>
        (CalibrationOutcome)(_outcome.SelectedItem as EnumChoice)!.Value;

    /// <summary>The calibration read and checked by EquipmentScreenRules, once « Enregistrer » is accepted.</summary>
    internal CalibrationEntry? Entry { get; private set; }

    internal CalibrationDialog(ILocalizer localizer, IUserDialogs dialogs, EquipmentRow row)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _calibratorLot.MaxLength = EquipmentScreenRules.CalibratorLotMaxLength;
        _comment.MaxLength = EquipmentScreenRules.CommentMaxLength;
        _slope.MaxLength = _intercept.MaxLength = EditorValues.MeasureMaxLength;

        Text = $"{localizer["Equipment.RecordCalibration"]} — {row.Code}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 330);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var y = 14;

        void Field(string key, int top, int left = 20)
        {
            var label = UiTheme.FieldLabel(localizer[key]);
            label.Location = new Point(left, top);
            Controls.Add(label);
        }

        Field("Calibration.Outcome", y);
        _outcome.DropDownStyle = ComboBoxStyle.DropDownList;
        _outcome.FlatStyle = FlatStyle.Flat;
        _outcome.Bounds = new Rectangle(20, y + 18, 200, UiTheme.InputHeight);

        foreach (var outcome in Enum.GetValues<CalibrationOutcome>())
            _outcome.Items.Add(new EnumChoice((int)outcome, localizer[$"CalibrationOutcome.{outcome}"]));

        _outcome.SelectedIndex = 0;
        _outcome.SelectedIndexChanged += (_, _) => RefreshLotRequirement();

        Field("Calibration.CalibratorLot", y, 250);
        _calibratorLot.Location = new Point(250, y + 18);
        _calibratorLot.Tag = UiTags.LatinValue;
        _calibratorLot.Font = UiTheme.MonospaceBold;

        y += 54;

        _lotNotice.Font = UiTheme.Small;
        _lotNotice.ForeColor = UiTheme.Abnormal;
        _lotNotice.AutoSize = false;
        _lotNotice.Bounds = new Rectangle(20, y, 460, 30);
        Controls.Add(_lotNotice);

        y += 36;

        _hasValidity.Text = localizer["Calibration.ValidUntil"];
        _hasValidity.AutoSize = true;
        _hasValidity.Checked = true;
        _hasValidity.Location = new Point(20, y + 20);
        _hasValidity.CheckedChanged += (_, _) => _validUntil.Enabled = _hasValidity.Checked;
        Controls.Add(_hasValidity);

        _validUntil.Bounds = new Rectangle(250, y + 18, 200, UiTheme.InputHeight);
        _validUntil.Format = DateTimePickerFormat.Short;
        _validUntil.MinDate = DateTime.Today;
        _validUntil.Value = EquipmentScreenRules.NewCalibration(DateOnly.FromDateTime(DateTime.Today)).ValidUntil.ToDateTime(TimeOnly.MinValue);

        y += 56;
        Field("Calibration.Slope", y);
        _slope.Location = new Point(20, y + 18);
        _slope.Tag = UiTags.LatinValue;
        _slope.TextAlign = HorizontalAlignment.Right;

        Field("Calibration.Intercept", y, 250);
        _intercept.Location = new Point(250, y + 18);
        _intercept.Tag = UiTags.LatinValue;
        _intercept.TextAlign = HorizontalAlignment.Right;

        y += 54;
        Field("Field.Comment", y);
        _comment.Location = new Point(20, y + 18);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 288, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 288, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_outcome, _calibratorLot, _validUntil, _slope, _intercept, _comment,
                           ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        RefreshLotRequirement();

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.Calibration");
    }

    /// <summary>
    /// Shows the calibrator-lot requirement before the operator hits Save rather than after: the
    /// domain rejects a passing calibration with no lot, and a rejection they could have foreseen
    /// is a rejection that should have been a label.
    /// </summary>
    private void RefreshLotRequirement()
    {
        var required = EquipmentScreenRules.LotRequired(Outcome);

        _lotNotice.Text = required ? _localizer["Calibration.CalibratorLotRequiredNotice"] : string.Empty;
    }

    private void Accept()
    {
        // A passing calibration names its calibrator lot, and a slip in the slope or the intercept
        // is no figure (EquipmentScreenRules.Check, as the web's form).
        var (refusal, entry) = EquipmentScreenRules.Check(new CalibrationForm
        {
            Outcome = Outcome,
            CalibratorLot = _calibratorLot.Text,
            HasValidity = _hasValidity.Checked,
            ValidUntil = DateOnly.FromDateTime(_validUntil.Value.Date),
            Slope = _slope.Text,
            Intercept = _intercept.Text,
            Comment = _comment.Text
        }, DateOnly.FromDateTime(DateTime.Today));

        if (refusal is not null)
        {
            _dialogs.Warn(_localizer.Format(refusal.Key, refusal.Typed ?? string.Empty));
            Control box = refusal.Field switch
            {
                EquipmentScreenRules.SlopeField => _slope,
                EquipmentScreenRules.InterceptField => _intercept,
                EquipmentScreenRules.ValidUntilField => _validUntil,
                _ => _calibratorLot
            };
            box.Focus();
            return;
        }

        Entry = entry;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Changing an instrument's status. The reason is mandatory and ends up in the audit trail.</summary>
internal sealed class EquipmentStatusDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _statusChoice = new();
    private readonly TextBox _reason = UiTheme.Input(440);

    internal EquipmentStatus Status =>
        (EquipmentStatus)(_statusChoice.SelectedItem as EnumChoice)!.Value;

    internal string Reason { get; private set; } = string.Empty;

    internal EquipmentStatusDialog(ILocalizer localizer, IUserDialogs dialogs, EquipmentRow row)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _reason.MaxLength = EquipmentScreenRules.ReasonMaxLength;

        Text = $"{localizer["Equipment.ChangeStatus"]} — {row.Code}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 250);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var current = UiTheme.FieldLabel(EquipmentScreenRules.CurrentStatus(row, localizer.Format));
        current.Location = new Point(20, 14);
        current.Font = UiTheme.BodyBold;
        current.ForeColor = UiTheme.TextPrimary;
        Controls.Add(current);

        var statusLabel = UiTheme.FieldLabel(localizer["Equipment.NewStatus"]);
        statusLabel.Location = new Point(20, 46);
        Controls.Add(statusLabel);

        _statusChoice.DropDownStyle = ComboBoxStyle.DropDownList;
        _statusChoice.FlatStyle = FlatStyle.Flat;
        _statusChoice.Bounds = new Rectangle(20, 64, 240, UiTheme.InputHeight);

        // Only the four statuses the service actually applies. Commissioning and Backup are set
        // when the instrument record is created, and offering them here would produce a refusal.
        foreach (var status in EquipmentScreenRules.StatusChoices)
        {
            _statusChoice.Items.Add(new EnumChoice((int)status,
                                                    localizer[EquipmentScreenRules.StatusKey(status)]));
        }

        _statusChoice.SelectedIndex = 0;
        _statusChoice.SelectedIndexChanged += (_, _) => RefreshNotice();

        var notice = new Label
        {
            Name = "notice",
            Font = UiTheme.Small,
            ForeColor = UiTheme.Abnormal,
            AutoSize = false,
            Bounds = new Rectangle(20, 98, 460, 32)
        };
        Controls.Add(notice);

        var reasonLabel = UiTheme.FieldLabel(localizer["Field.Reason"]);
        reasonLabel.Location = new Point(20, 138);
        Controls.Add(reasonLabel);

        _reason.Location = new Point(20, 156);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 206, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 206, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_statusChoice, _reason, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        RefreshNotice();

        StockDialog.Frame(this, localizer, "StocksUi.Dialog.EquipmentStatus");
    }

    private void RefreshNotice()
    {
        if (Controls["notice"] is not Label notice) return;

        // A return to service and a decommissioning are warned about before the operator commits.
        notice.Text = EquipmentScreenRules.StatusNoticeKey(Status) is { } key ? _localizer[key] : string.Empty;
    }

    private void Accept()
    {
        var (refusal, reason) = EquipmentScreenRules.Check(new EquipmentStatusForm { Status = Status, Reason = _reason.Text });
        if (refusal is not null)
        {
            _dialogs.Warn(_localizer[refusal.Key]);
            _reason.Focus();
            return;
        }

        Reason = reason!;

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>An enum value paired with its localised label, for use in a combo box.</summary>
internal sealed record EnumChoice(int Value, string Display)
{
    public override string ToString() => Display;
}
