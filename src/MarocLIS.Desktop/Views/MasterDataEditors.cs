using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Shared chrome and field helpers for the reference-data editors.
/// <para>
/// Five editors with the same shape would otherwise be five copies of the same two hundred lines of
/// control positioning, and the fifth copy is where the RTL tag or the Latin-value marker gets
/// forgotten.
/// </para>
/// </summary>
internal abstract class MasterDataEditor : Form
{
    protected ILocalizer Localizer { get; }
    protected IUserDialogs Dialogs { get; }

    protected MasterDataEditor(ILocalizer localizer, IUserDialogs dialogs, string titleKey,
                               string? subtitle, int width, int height)
    {
        Localizer = localizer;
        Dialogs = dialogs;

        Text = subtitle is null ? localizer[titleKey] : $"{localizer[titleKey]} — {subtitle}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(width, height);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;
    }

    protected void Label(string key, int x, int y)
    {
        var label = UiTheme.FieldLabel(Localizer[key]);
        label.Location = new Point(x, y);
        Controls.Add(label);
    }

    /// <summary>
    /// A labelled text box.
    /// <para>
    /// <c>TextField</c> and not <c>Text</c>: a member named <c>Text</c> hides
    /// <see cref="Control.Text"/> on every editor that inherits this class, which turned the form's
    /// own caption into a method group — so <c>Text = ...</c> in the constructor and every
    /// <c>Dialogs.Warn(message, Text)</c> in a validation path stopped compiling. The caption is
    /// what a warning box needs to say which dialog is complaining, so the property has to win.
    /// </para>
    /// </summary>
    protected TextBox TextField(string key, int x, int y, int width, string? value = null,
                                bool latin = false, bool mono = false, bool arabic = false,
                                bool right = false)
    {
        Label(key, x, y);

        var box = UiTheme.Input(width);
        box.Location = new Point(x, y + 18);
        box.Text = value ?? string.Empty;

        if (latin) box.Tag = UiTags.LatinValue;
        if (mono) box.Font = UiTheme.Monospace;
        if (arabic) box.RightToLeft = RightToLeft.Yes;
        if (right) box.TextAlign = HorizontalAlignment.Right;

        Controls.Add(box);
        return box;
    }

    protected ComboBox Choice<TEnum>(string key, int x, int y, int width, TEnum current,
                                      string prefix, IEnumerable<TEnum>? only = null)
        where TEnum : struct, Enum
    {
        Label(key, x, y);

        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(x, y + 18, width, UiTheme.InputHeight)
        };

        foreach (var value in only ?? Enum.GetValues<TEnum>())
        {
            var index = combo.Items.Add(
                new EnumChoice(Convert.ToInt32(value), Localizer[$"{prefix}.{value}"]));

            if (Equals(value, current)) combo.SelectedIndex = index;
        }

        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;

        Controls.Add(combo);
        return combo;
    }

    protected ComboBox Lookup(string key, int x, int y, int width,
                              IEnumerable<(Guid Id, string Display)> options, Guid? current,
                              bool allowNone = true)
    {
        Label(key, x, y);

        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(x, y + 18, width, UiTheme.InputHeight)
        };

        if (allowNone) combo.Items.Add(new LookupChoice(null, "—"));

        foreach (var (id, display) in options)
        {
            var index = combo.Items.Add(new LookupChoice(id, display));
            if (current == id) combo.SelectedIndex = index;
        }

        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;

        Controls.Add(combo);
        return combo;
    }

    protected NumericUpDown Number(string key, int x, int y, int width, decimal? value,
                                    decimal max = 1_000_000m, int decimals = 0)
    {
        Label(key, x, y);

        var box = new NumericUpDown
        {
            Bounds = new Rectangle(x, y + 18, width, UiTheme.InputHeight),
            Maximum = max,
            DecimalPlaces = decimals,
            Value = value is { } v ? Math.Clamp(v, 0m, max) : 0m
        };

        Controls.Add(box);
        return box;
    }

    protected CheckBox Check(string key, int x, int y, bool value, bool bold = false)
    {
        var box = new CheckBox
        {
            Text = Localizer[key],
            AutoSize = true,
            Checked = value,
            Location = new Point(x, y),
            Font = bold ? UiTheme.BodyBold : UiTheme.Body
        };

        Controls.Add(box);
        return box;
    }

    protected (CheckBox Toggle, DateTimePicker Picker) OptionalDate(
        string key, int x, int y, DateOnly? value, DateTime? min = null, DateTime? max = null)
    {
        // DateOnly's default (01/01/0001) is what an unset non-nullable column reads back as, and
        // DateTimePicker throws on anything before 1753. It means "no date", so it is shown as one.
        if (value == default(DateOnly)) value = null;

        var toggle = Check(key, x, y, value is not null);

        var picker = new DateTimePicker
        {
            Bounds = new Rectangle(x, y + 20, 180, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short,
            Enabled = value is not null
        };

        if (min is { } lower) picker.MinDate = lower;
        if (max is { } upper) picker.MaxDate = upper;

        if (value is { } date)
        {
            var wanted = date.ToDateTime(TimeOnly.MinValue);
            picker.Value = wanted < picker.MinDate ? picker.MinDate
                         : wanted > picker.MaxDate ? picker.MaxDate
                         : wanted;
        }

        toggle.CheckedChanged += (_, _) => picker.Enabled = toggle.Checked;

        Controls.Add(picker);
        return (toggle, picker);
    }

    protected Label Notice(int x, int y, int width, int height, string key, Color? colour = null)
    {
        var label = new Label
        {
            Text = Localizer[key],
            Font = UiTheme.Small,
            ForeColor = colour ?? UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(x, y, width, height)
        };

        // A notice that does not fit its box ends in "…" with the full text on hover, rather than
        // spilling under the field below it: the space was sized for French, and the Arabic or
        // English sentence can run a line longer.
        label.AutoEllipsis = true;
        label.TextChanged += (_, _) => _tips.SetToolTip(label, label.Text);
        _tips.SetToolTip(label, label.Text);

        Controls.Add(label);
        return label;
    }

    private readonly ToolTip _tips = new() { AutoPopDelay = 20_000 };

    /// <param name="acceptKey">
    /// The confirming button's caption. "Enregistrer" by default, which is what nearly every editor
    /// does; a dialog that prints rather than saves says so, because an operator who reads "Save"
    /// on a print dialog reasonably wonders what is being stored.
    /// </param>
    protected void Buttons(int y, int width, string acceptKey = "Action.Save")
    {
        var ok = UiTheme.PrimaryButton(Localizer[acceptKey]);
        ok.Bounds = new Rectangle(20, y, (width - 60) / 2, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(Localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(20 + (width - 60) / 2 + 20, y, (width - 60) / 2,
                                       UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        _accept = ok;
        _dismiss = cancel;
    }

    private Button? _accept, _dismiss;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_framed) FitToScreen();
    }

    private bool _framed;

    /// <summary>
    /// Gives this editor the frame of the administration dialogs — a title with one sentence, and
    /// the buttons in a footer bar — which also keeps a tall editor within the screen. Called by
    /// the editors of the administration screens at the end of their constructor; the other
    /// editors keep the plain layout and <see cref="FitToScreen"/>.
    /// </summary>
    protected void UseAdminFrame()
    {
        if (_framed) return;
        _framed = true;
        DialogFrame.Attach(this, Localizer, DialogFrame.Lead(Localizer, GetType().Name));
    }

    /// <summary>
    /// Keeps a tall editor usable on a small screen. The analysis editor is 764 pixels high, and
    /// at 1366×768 its Save and Cancel buttons sat below the taskbar: the only way out was Enter or
    /// Esc. When the dialog is taller than the screen's working area, every field moves into a
    /// scrolling body and the buttons stay pinned at the bottom, always in view.
    /// </summary>
    private void FitToScreen()
    {
        if (_accept is null || _dismiss is null) return;

        var chrome = Height - ClientSize.Height;
        var available = Screen.FromControl(Owner ?? (Control)this).WorkingArea.Height - chrome;

        if (ClientSize.Height <= available) return;

        var shift = ClientSize.Height - available;
        var footerTop = Math.Min(_accept.Top, _dismiss.Top) - shift;

        if (footerTop <= 0) return;

        SuspendLayout();

        var body = new Panel
        {
            AutoScroll = true,
            AutoScrollMargin = new Size(0, 12),
            BackColor = BackColor,
            Bounds = new Rectangle(0, 0, ClientSize.Width, footerTop - 4)
        };

        // Moved with their coordinates unchanged: the panel sits at the origin, so the layout the
        // editor drew is the layout the operator scrolls through.
        foreach (var control in Controls.Cast<Control>().Where(c => c != _accept && c != _dismiss).ToList())
            body.Controls.Add(control);

        Controls.Add(body);

        _accept.Top -= shift;
        _dismiss.Top -= shift;
        ClientSize = new Size(ClientSize.Width, available);

        ResumeLayout(performLayout: true);
    }

    protected abstract void Accept();

    protected static string? Blank(TextBox box) =>
        string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    protected static int? IntOrNull(NumericUpDown box) =>
        box.Value > 0 ? (int)box.Value : null;

    /// <summary>
    /// <see cref="IntOrNull"/>, except that a box left at the stored value returns that value: a
    /// stored zero shows as 0 and would otherwise come back as "not set".
    /// </summary>
    protected static int? IntOrStored(NumericUpDown box, int? stored) =>
        stored is { } value && box.Value == Math.Clamp(value, box.Minimum, box.Maximum)
            ? stored
            : IntOrNull(box);

    /// <summary>
    /// A text the dialog edits in French only. While the French is unchanged the stored text is
    /// kept whole, translations included; rebuilding it from the one box dropped the Arabic and
    /// English on every save, even when nothing was touched.
    /// </summary>
    protected static MultilingualText? FrenchEdited(MultilingualText? stored, string? french) =>
        french is null ? null
        : stored is not null && string.Equals(stored.Fr, french, StringComparison.Ordinal) ? stored
        : new MultilingualText(french);

    /// <summary>A stored decimal written for an editable box, or empty when there is none.</summary>
    protected static string? Editable(decimal? value) =>
        value is { } number ? EditorValues.FormatForEditing(number, 0) : null;

    protected static decimal? DecimalOrNull(TextBox box) => EditorValues.ParseDecimal(box.Text);

    protected static TEnum Selected<TEnum>(ComboBox combo) where TEnum : struct, Enum =>
        (TEnum)Enum.ToObject(typeof(TEnum), (combo.SelectedItem as EnumChoice)?.Value ?? 0);

    protected static Guid? SelectedId(ComboBox combo) => (combo.SelectedItem as LookupChoice)?.Id;

    protected static DateOnly? DateOrNull(CheckBox toggle, DateTimePicker picker) =>
        toggle.Checked ? DateOnly.FromDateTime(picker.Value.Date) : null;

    protected sealed record LookupChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>
/// Creating or editing an instrument.
/// <para>
/// The intervals are the point of this dialog. Every overdue indicator, and the control that stops
/// an analyser producing patient results while its calibration is lapsed, is computed from them —
/// so a null interval is not an omission, it silently disarms the guard. The service refuses an
/// analyser without a calibration interval for that reason, and the notice here says so before the
/// refusal arrives.
/// </para>
/// </summary>
internal sealed class InstrumentEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _code, _nameFr, _nameAr, _manufacturer, _model, _serial, _software,
                             _location, _support, _contract;
    private readonly ComboBox _kind, _supplier;
    private readonly NumericUpDown _maintenanceInterval, _calibrationInterval, _verificationInterval;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _lastMaintenance, _lastCalibration,
                                                               _lastVerification, _contractUntil,
                                                               _commissioning;
    private readonly Label _kindNotice;
    private readonly InstrumentRequest? _stored;

    internal InstrumentRequest? Request { get; private set; }

    /// <param name="stored">
    /// The whole stored instrument when editing. The fields are filled from it, and the request
    /// saved is built on it, so whatever the dialog does not show is carried through as stored.
    /// </param>
    internal InstrumentEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                    InstrumentMasterRow? row, IReadOnlyList<SupplierRow> suppliers,
                                    InstrumentRequest? stored = null)
        : base(localizer, dialogs,
               row is null ? "Equipment.NewInstrument" : "Action.Edit", row?.Code, 720, 600)
    {
        _id = row?.InstrumentId;
        _stored = row is null ? null : stored;

        _code = TextField("Field.Code", 20, 14, 160, row?.Code, latin: true, mono: true);
        _nameFr = TextField("Field.NameFr", 200, 14, 240, row?.Name.Fr);
        _nameAr = TextField("Field.NameAr", 460, 14, 240, row?.Name.Ar, arabic: true);

        _kind = Choice("Equipment.Kind", 20, 68, 200, row?.Kind ?? EquipmentKind.Analyser,
                        "EquipmentKind");
        _kind.SelectedIndexChanged += (_, _) => RefreshKindNotice();

        _location = TextField("Equipment.Location", 240, 68, 200, row?.Location);
        _serial = TextField("Equipment.SerialNumber", 460, 68, 240, row?.SerialNumber,
                        latin: true, mono: true);

        _manufacturer = TextField("Equipment.Manufacturer", 20, 122, 200, row?.Manufacturer);
        _model = TextField("Equipment.Model", 240, 122, 200, row?.Model);
        _software = TextField("Equipment.SoftwareVersion", 460, 122, 240, _stored?.SoftwareVersion,
                              latin: true);

        _supplier = Lookup("Inventory.Supplier", 20, 176, 300,
                            suppliers.Select(s => (s.SupplierId, $"{s.Code} — {s.Name}")),
                            _stored?.SupplierId);

        _contract = TextField("Equipment.Contract", 340, 176, 200,
                              _stored?.MaintenanceContractReference, latin: true);
        _support = TextField("Equipment.SupportPhone", 560, 176, 140, _stored?.SupportPhone,
                             latin: true, mono: true);

        _contractUntil = OptionalDate("Equipment.ContractValidUntil", 20, 232,
                                       row?.ContractValidUntil);

        _commissioning = OptionalDate("Equipment.Commissioning", 240, 232,
                                       _stored?.CommissioningDate, max: DateTime.Today);

        // Interval block, set apart because it is the part that matters.
        var intervalHeading = new Label
        {
            Text = localizer["Equipment.Intervals"],
            Font = UiTheme.SectionTitle,
            ForeColor = UiTheme.TextPrimary,
            AutoSize = false,
            Bounds = new Rectangle(20, 300, 400, 22)
        };
        Controls.Add(intervalHeading);

        _kindNotice = Notice(20, 326, 680, 32, "Equipment.CalibrationIntervalNotice",
                              UiTheme.Abnormal);

        _maintenanceInterval = Number("Equipment.MaintenanceInterval", 20, 366, 180,
                                       row?.PreventiveMaintenanceIntervalDays, 3650);

        _calibrationInterval = Number("Equipment.CalibrationInterval", 240, 366, 180,
                                       row?.CalibrationIntervalDays, 3650);

        _verificationInterval = Number("Equipment.VerificationInterval", 460, 366, 180,
                                        row?.VerificationIntervalDays, 3650);

        _lastMaintenance = OptionalDate("Equipment.LastMaintenance", 20, 420,
                                         _stored?.LastPreventiveMaintenanceOn, max: DateTime.Today);

        _lastCalibration = OptionalDate("Equipment.LastCalibration", 240, 420,
                                         row?.LastCalibrationOn, max: DateTime.Today);

        _lastVerification = OptionalDate("Equipment.LastVerification", 460, 420,
                                          _stored?.LastVerificationOn, max: DateTime.Today);

        Notice(20, 486, 680, 46, "Equipment.LastDatesNotice");

        Buttons(552, 720);
        UseAdminFrame();
        RefreshKindNotice();
    }

    /// <summary>
    /// Says the calibration interval is mandatory for an analyser while the kind is being chosen,
    /// rather than letting the service refuse after Save.
    /// </summary>
    private void RefreshKindNotice() =>
        _kindNotice.Visible = Selected<EquipmentKind>(_kind) == EquipmentKind.Analyser;

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Equipment_CodeRequired"]);
            _code.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["Equipment_NameRequired"]);
            _nameFr.Focus();
            return;
        }

        if (Selected<EquipmentKind>(_kind) == EquipmentKind.Analyser
            && _calibrationInterval.Value <= 0)
        {
            Dialogs.Warn(Localizer["Equipment_AnalyserNeedsCalibrationInterval"]);
            _calibrationInterval.Focus();
            return;
        }

        // Built on the stored instrument, so a field this dialog does not show (the department, the
        // purchase date) is saved as it was rather than reset.
        var basis = _stored ?? new InstrumentRequest { Code = string.Empty, Name = new MultilingualText() };

        Request = basis with
        {
            InstrumentId = _id,
            Code = _code.Text.Trim(),
            // No English box: the stored English name is kept while the French one is unchanged.
            Name = new MultilingualText(
                _nameFr.Text.Trim(), _nameAr.Text.Trim(),
                _stored?.Name.Fr == _nameFr.Text.Trim() ? _stored.Name.En : null),
            Kind = Selected<EquipmentKind>(_kind),
            Manufacturer = Blank(_manufacturer),
            Model = Blank(_model),
            SerialNumber = Blank(_serial),
            SoftwareVersion = Blank(_software),
            Location = Blank(_location),
            SupplierId = SelectedId(_supplier),
            MaintenanceContractReference = Blank(_contract),
            MaintenanceContractValidUntil = DateOrNull(_contractUntil.Toggle, _contractUntil.Picker),
            SupportPhone = Blank(_support),
            CommissioningDate = DateOrNull(_commissioning.Toggle, _commissioning.Picker),
            PreventiveMaintenanceIntervalDays = IntOrNull(_maintenanceInterval),
            CalibrationIntervalDays = IntOrNull(_calibrationInterval),
            VerificationIntervalDays = IntOrNull(_verificationInterval),
            LastPreventiveMaintenanceOn = DateOrNull(_lastMaintenance.Toggle, _lastMaintenance.Picker),
            LastCalibrationOn = DateOrNull(_lastCalibration.Toggle, _lastCalibration.Picker),
            LastVerificationOn = DateOrNull(_lastVerification.Toggle, _lastVerification.Picker)
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A control material: one lot at one level, with its expiry and open stability.</summary>
internal sealed class QcMaterialEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _lot, _nameFr, _nameAr, _manufacturer, _reference;
    private readonly ComboBox _level;
    private readonly DateTimePicker _expiry;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _opened;
    private readonly NumericUpDown _stability;
    private readonly CheckBox _active;

    internal QcMaterialRequest? Request { get; private set; }

    internal QcMaterialEditorDialog(ILocalizer localizer, IUserDialogs dialogs, QcMaterialRow? row)
        : base(localizer, dialogs,
               row is null ? "Quality.NewQcMaterial" : "Action.Edit", row?.LotNumber, 620, 400)
    {
        _id = row?.QcMaterialId;

        _lot = TextField("Quality.QcLot", 20, 14, 200, row?.LotNumber, latin: true, mono: true);
        _lot.Enabled = row is null;   // Lot plus level is the natural key.

        _level = Choice("Quality.QcLevel", 240, 14, 180, row?.Level ?? QcLevel.Normal, "QcLevel");
        _level.Enabled = row is null;

        Label("Inventory.Expiry", 440, 14);
        _expiry = new DateTimePicker
        {
            Bounds = new Rectangle(440, 32, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short
        };

        // An already-lapsed expiry cannot be shown by a picker whose minimum is today, and
        // clamping it silently would edit the record just by opening it. The minimum is therefore
        // chosen before the value is assigned: assigning a lapsed date first throws.
        var expiryDate = row?.ExpiryDate.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today.AddMonths(12);

        _expiry.MinDate = expiryDate < DateTime.Today ? new DateTime(2000, 1, 1) : DateTime.Today;
        _expiry.Value = expiryDate;

        Controls.Add(_expiry);

        _nameFr = TextField("Field.NameFr", 20, 68, 260, row?.Name.Fr);
        _nameAr = TextField("Field.NameAr", 300, 68, 300, row?.Name.Ar, arabic: true);

        _manufacturer = TextField("Equipment.Manufacturer", 20, 122, 260, row?.Manufacturer);
        _reference = TextField("Inventory.SupplierReference", 300, 122, 300, row?.CatalogueReference,
                           latin: true);

        _opened = OptionalDate("Inventory.OpenedOn", 20, 180, row?.OpenedOn, max: DateTime.Today);

        _stability = Number("Inventory.OpenStabilityDays", 240, 180, 120,
                             row?.StabilityAfterOpeningDays, 3650);

        _active = Check("Field.Active", 20, 250, row?.IsActive ?? true);

        Notice(20, 276, 580, 46, "Quality.MaterialStabilityNotice");

        Buttons(352, 620);
        UseAdminFrame();
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_lot.Text))
        {
            Dialogs.Warn(Localizer["QcMaterial_LotRequired"]);
            _lot.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["QcMaterial_NameRequired"]);
            _nameFr.Focus();
            return;
        }

        Request = new QcMaterialRequest
        {
            QcMaterialId = _id,
            LotNumber = _lot.Text.Trim(),
            Name = new MultilingualText(_nameFr.Text.Trim(), _nameAr.Text.Trim()),
            Level = Selected<QcLevel>(_level),
            ExpiryDate = DateOnly.FromDateTime(_expiry.Value.Date),
            Manufacturer = Blank(_manufacturer),
            CatalogueReference = Blank(_reference),
            OpenedOn = DateOrNull(_opened.Toggle, _opened.Picker),
            StabilityAfterOpeningDays = IntOrNull(_stability),
            IsActive = _active.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// A QC target: what one control reads on one instrument for one parameter.
/// <para>
/// The record the whole quality-control module hangs from. Without it there is nothing to enter a
/// control value against, no mean and SD to compute a Z-score from, and so no Levey-Jennings series
/// and no Westgard evaluation — which is why the entry sheet was permanently empty.
/// </para>
/// </summary>
internal sealed class QcTargetEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly ComboBox _material, _instrument, _parameter;
    private readonly TextBox _mean, _sd, _cv;
    private readonly CheckBox _active;
    private readonly Label _cvEcho;
    private WestgardRule _rules;
    private readonly Button _ruleButton;

    internal QcTargetRequest? Request { get; private set; }

    internal QcTargetEditorDialog(ILocalizer localizer, IUserDialogs dialogs, QcTargetRow? row,
                                  IReadOnlyList<QcMaterialRow> materials,
                                  IReadOnlyList<InstrumentMasterRow> instruments,
                                  IReadOnlyList<ParameterOption> parameters)
        : base(localizer, dialogs,
               row is null ? "Quality.NewQcTarget" : "Action.Edit", row?.ParameterCode, 640, 470)
    {
        _id = row?.QcTargetId;

        _rules = row?.ActiveRules
                 ?? (WestgardRule.OneTwoS | WestgardRule.OneThreeS | WestgardRule.TwoTwoS
                     | WestgardRule.R4S | WestgardRule.FourOneS | WestgardRule.TenX);

        _material = Lookup("Quality.QcMaterial", 20, 14, 280,
                            materials.Select(m => (m.QcMaterialId,
                                $"{m.LotNumber} — {localizer[$"QcLevel.{m.Level}"]}")),
                            row?.QcMaterialId, allowNone: false);

        _instrument = Lookup("Result.Instrument", 320, 14, 280,
                              instruments.Select(i => (i.InstrumentId, $"{i.Code} — {i.Name.Fr}")),
                              row?.InstrumentId, allowNone: false);

        _parameter = Lookup("Result.Parameter", 20, 68, 580,
                             parameters.Select(p => (p.TestParameterId,
                                 $"{p.AnalysisCode} · {p.ParameterCode} — {p.ParameterName.Fr}"
                                 + (p.UnitSymbol is { Length: > 0 } u ? $" ({u})" : string.Empty))),
                             row?.TestParameterId, allowNone: false);

        _material.Enabled = row is null;
        _instrument.Enabled = row is null;
        _parameter.Enabled = row is null;

        Notice(20, 122, 580, 32, "Quality.AssignedLimitsNotice", UiTheme.Accent);

        _mean = TextField("Quality.AssignedMean", 20, 162, 140,
                      row?.AssignedMean?.ToString("0.###"), latin: true, mono: true, right: true);

        _sd = TextField("Quality.AssignedSd", 180, 162, 140,
                    row?.AssignedSd?.ToString("0.###"), latin: true, mono: true, right: true);

        _cv = TextField("Quality.MaxCv", 340, 162, 140,
                    row?.MaxAcceptableCvPercent?.ToString("0.##"), latin: true, right: true);

        _cvEcho = Notice(500, 180, 120, 20, "Quality.CvEcho", UiTheme.TextSecondary);

        _mean.TextChanged += (_, _) => RefreshCv();
        _sd.TextChanged += (_, _) => RefreshCv();

        Label("Quality.Rules", 20, 220);
        _ruleButton = UiTheme.SecondaryButton(string.Empty);
        _ruleButton.Bounds = new Rectangle(20, 238, 580, UiTheme.ButtonHeight);
        _ruleButton.Click += (_, _) => PickRules();
        Controls.Add(_ruleButton);

        _active = Check("Field.Active", 20, 286, row?.IsActive ?? true);

        Notice(20, 316, 580, 46, "Quality.TargetLifecycleNotice");

        Buttons(410, 640);
        UseAdminFrame();

        RefreshCv();
        RefreshRuleButton();
    }

    /// <summary>
    /// Echoes the coefficient of variation the entered mean and SD imply. It is the number a
    /// biologist judges a method by, and computing it in their head from two boxes is friction.
    /// </summary>
    private void RefreshCv()
    {
        var mean = DecimalOrNull(_mean);
        var sd = DecimalOrNull(_sd);

        _cvEcho.Text = mean is > 0m && sd is > 0m
            ? $"CV {sd.Value / mean.Value * 100m:0.##} %"
            : string.Empty;
    }

    private void PickRules()
    {
        using var dialog = new WestgardRuleDialog(
            Localizer,
            (_parameter.SelectedItem as LookupChoice)?.Display ?? "—",
            (_material.SelectedItem as LookupChoice)?.Display ?? "—",
            _rules);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _rules = dialog.Rules;
        RefreshRuleButton();
    }

    private void RefreshRuleButton()
    {
        var count = Enum.GetValues<WestgardRule>()
                        .Count(r => r != WestgardRule.None && _rules.HasFlag(r));

        _ruleButton.Text = count == 0
            ? Localizer["Quality.NoRulesWarning"]
            : Localizer.Format("Quality.RuleCount", count);

        _ruleButton.ForeColor = count == 0 ? UiTheme.Critical : UiTheme.TextPrimary;
        _ruleButton.Font = count == 0 ? UiTheme.BodyBold : UiTheme.Body;
    }

    protected override void Accept()
    {
        var material = SelectedId(_material);
        var instrument = SelectedId(_instrument);
        var parameter = SelectedId(_parameter);

        if (material is null || instrument is null || parameter is null)
        {
            Dialogs.Warn(Localizer["Message.NothingSelected"]);
            return;
        }

        if (_rules == WestgardRule.None)
        {
            Dialogs.Warn(Localizer["QcTarget_AtLeastOneRuleRequired"]);
            return;
        }

        Request = new QcTargetRequest
        {
            QcTargetId = _id,
            QcMaterialId = material.Value,
            InstrumentId = instrument.Value,
            TestParameterId = parameter.Value,
            AssignedMean = DecimalOrNull(_mean),
            AssignedSd = DecimalOrNull(_sd),
            MaxAcceptableCvPercent = DecimalOrNull(_cv),
            ActiveRules = _rules,
            IsActive = _active.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A stock article, with the reorder policy that drives the shortage alerts.</summary>
internal sealed class StockItemEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _code, _nameFr, _nameAr, _unit, _manufacturer, _reference,
                             _analyses, _location, _hazardClass, _gtin;
    private readonly ComboBox _kind, _storage, _supplier;
    private readonly NumericUpDown _tests;
    private readonly TextBox _reorder, _reorderQuantity, _minimum, _maximum;
    private readonly CheckBox _critical, _hazardous, _active, _ceMarked;

    // Consumption and computed reorder point (purchasing lot B).
    private readonly TextBox _testsPerResult, _deadPercent, _deadTests;
    private readonly NumericUpDown _calibrationTests, _safetyDays, _shelfLife;
    private readonly CheckBox _computed;

    internal StockItemRequest? Request { get; private set; }

    internal StockItemEditorDialog(ILocalizer localizer, IUserDialogs dialogs,
                                   StockItemMasterRow? row, IReadOnlyList<SupplierRow> suppliers,
                                   Application.Inventory.ReorderPointProposal? proposal = null)
        : base(localizer, dialogs,
               row is null ? "Inventory.NewItem" : "Action.Edit", row?.Code, 720, 800)
    {
        _id = row?.StockItemId;

        _code = TextField("Field.Code", 20, 14, 160, row?.Code, latin: true, mono: true);
        _nameFr = TextField("Field.NameFr", 200, 14, 240, row?.Name.Fr);
        _nameAr = TextField("Field.NameAr", 460, 14, 240, row?.Name.Ar, arabic: true);

        _kind = Choice("Field.Name", 20, 68, 200, row?.Kind ?? StockItemKind.Reagent,
                        "StockItemKind");
        _unit = TextField("Field.Unit", 240, 68, 100, row?.UnitOfMeasure ?? "u");
        _tests = Number("Inventory.TestsPerPack", 360, 68, 100, row?.TestsPerPack, 100_000);

        _storage = Choice("Sample.Storage", 480, 68, 220,
                           row?.StorageCondition ?? StorageCondition.RoomTemperature,
                           "StorageCondition");

        _manufacturer = TextField("Equipment.Manufacturer", 20, 122, 200, row?.Manufacturer);
        _supplier = Lookup("Inventory.Supplier", 240, 122, 240,
                            suppliers.Select(s => (s.SupplierId, $"{s.Code} — {s.Name}")),
                            row?.PreferredSupplierId);
        _reference = TextField("Inventory.SupplierReference", 500, 122, 200, row?.SupplierCatalogueReference,
                               latin: true);

        _analyses = TextField("Inventory.UsedByAnalyses", 20, 176, 460, row?.UsedByAnalysisCodesCsv, latin: true);
        _location = TextField("Inventory.DefaultLocation", 500, 176, 200, row?.DefaultStorageLocation);

        Notice(20, 230, 680, 30, "Inventory.ReorderNotice", UiTheme.Accent);

        _minimum = TextField("Inventory.MinimumStock", 20, 266, 140,
                         row?.MinimumStock.ToString("0.##"), latin: true, right: true);
        _reorder = TextField("Inventory.ReorderPoint", 180, 266, 140,
                         row?.ReorderPoint.ToString("0.##"), latin: true, right: true);
        _reorderQuantity = TextField("Inventory.ReorderQuantity", 340, 266, 140,
                                 row is null ? null : row.ReorderQuantity.ToString("0.##"),
                                 latin: true, right: true);
        _maximum = TextField("Inventory.MaximumStock", 500, 266, 140, row?.MaximumStock?.ToString("0.##"),
                             latin: true, right: true);

        _critical = Check("Inventory.IsCritical", 20, 330, row?.IsCritical ?? false, bold: true);
        Notice(20, 354, 680, 30, "Inventory.CriticalNotice");

        _hazardous = Check("Inventory.IsHazardous", 20, 392, row?.IsHazardous ?? false);
        _hazardClass = TextField("Inventory.HazardClass", 240, 386, 200, row?.HazardClass, latin: true);
        _hazardClass.Enabled = row?.IsHazardous ?? false;
        _hazardous.CheckedChanged += (_, _) => _hazardClass.Enabled = _hazardous.Checked;

        _active = Check("Field.Active", 480, 392, row?.IsActive ?? true);

        // GS1: the GTIN printed in the DataMatrix, so a scan at reception picks this article.
        _gtin = TextField("Inventory.Gtin", 20, 446, 200, row?.Gtin, latin: true, mono: true);
        _ceMarked = Check("Inventory.IsCeMarked", 240, 470, row?.IsCeMarked ?? false);
        Notice(20, 500, 680, 30, "Inventory.GtinNotice");

        // What a result costs in tests and what an opening loses, for the theoretical consumption;
        // the safety margin and the shelf life, for the computed reorder point and the order cap.
        var policy = row?.ConsumptionPolicy ?? new StockConsumptionPolicy();
        static string N(decimal? v) => v?.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

        Notice(20, 540, 680, 20, "Inventory.ConsumptionNotice", UiTheme.Accent);
        _testsPerResult = TextField("Inventory.TestsPerResult", 20, 562, 300, policy.TestsPerResultCsv, latin: true, mono: true);
        _deadPercent = TextField("Inventory.DeadVolumePercent", 340, 562, 170, N(policy.DeadVolumePercent),
                                 latin: true, right: true);
        _deadTests = TextField("Inventory.DeadVolumeTests", 530, 562, 170, N(policy.DeadVolumeTestsPerOpening),
                               latin: true, right: true);

        _calibrationTests = Number("Inventory.TestsPerCalibration", 20, 616, 150, policy.TestsPerCalibration, 1000m);
        _safetyDays = Number("Inventory.SafetyStockDays", 190, 616, 150, policy.SafetyStockDays, 365m);
        _shelfLife = Number("Inventory.ShelfLifeDays", 360, 616, 150, policy.TypicalShelfLifeDays, 3650m);

        _computed = Check("Inventory.ReorderPointComputed", 20, 672, policy.ReorderPointComputed, bold: true);

        var suggestion = Notice(20, 696, 520, 40, "Inventory.NoReorderProposal");
        var apply = UiTheme.SecondaryButton(Localizer["Inventory.ApplyReorderProposal"]);
        apply.Bounds = new Rectangle(560, 696, 140, UiTheme.ButtonHeight);
        apply.Enabled = proposal is not null;
        Controls.Add(apply);

        if (proposal is { Suggestion: var s })
        {
            suggestion.Text = Localizer.Format("Inventory.ReorderProposal", N(s.ReorderPoint), N(s.AverageDailyConsumption),
                                               s.LeadTimeDays, N(s.SafetyStock), proposal.WindowDays,
                                               Localizer[s.FromTheoretical ? "Inventory.FromTheoretical" : "Inventory.FromIssues"]);
            apply.Click += (_, _) => _reorder.Text = N(s.ReorderPoint);
        }

        Buttons(750, 720);
        UseAdminFrame();
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Stock_CodeRequired"]);
            _code.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["Stock_NameRequired"]);
            _nameFr.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_unit.Text))
        {
            Dialogs.Warn(Localizer["Stock_UnitRequired"]);
            _unit.Focus();
            return;
        }

        var minimum = DecimalOrNull(_minimum) ?? 0m;
        var reorder = DecimalOrNull(_reorder) ?? 0m;

        // Caught here as well as in the service: a reorder point below the safety stock means the
        // alert fires after the laboratory is already below the level it meant to hold.
        if (reorder < minimum)
        {
            Dialogs.Warn(Localizer["Stock_ReorderBelowMinimum"]);
            _reorder.Focus();
            return;
        }

        var deadPercent = DecimalOrNull(_deadPercent);
        var deadTests = DecimalOrNull(_deadTests);
        if (!string.IsNullOrWhiteSpace(_deadPercent.Text) && deadPercent is null
            || !string.IsNullOrWhiteSpace(_deadTests.Text) && deadTests is null)
        {
            Dialogs.Warn(Localizer["Stock_DeadVolumeOutOfRange"]);
            _deadPercent.Focus();
            return;
        }

        var policy = new StockConsumptionPolicy
        {
            TestsPerResultCsv = Blank(_testsPerResult),
            DeadVolumePercent = deadPercent,
            DeadVolumeTestsPerOpening = deadTests,
            TestsPerCalibration = IntOrNull(_calibrationTests),
            SafetyStockDays = IntOrNull(_safetyDays),
            TypicalShelfLifeDays = IntOrNull(_shelfLife),
            ReorderPointComputed = _computed.Checked
        };

        if (policy.Validate() is { IsFailure: true } invalid)
        {
            Dialogs.Warn(Localizer.Describe(invalid.Errors));
            _testsPerResult.Focus();
            return;
        }

        Request = new StockItemRequest
        {
            StockItemId = _id,
            Code = _code.Text.Trim(),
            Name = new MultilingualText(_nameFr.Text.Trim(), _nameAr.Text.Trim()),
            Kind = Selected<StockItemKind>(_kind),
            UnitOfMeasure = _unit.Text.Trim(),
            TestsPerPack = IntOrNull(_tests),
            PreferredSupplierId = SelectedId(_supplier),
            SupplierCatalogueReference = Blank(_reference),
            Manufacturer = Blank(_manufacturer),
            UsedByAnalysisCodesCsv = Blank(_analyses),
            ReorderPoint = reorder,
            ReorderQuantity = DecimalOrNull(_reorderQuantity) ?? 0m,
            MinimumStock = minimum,
            MaximumStock = DecimalOrNull(_maximum),
            StorageCondition = Selected<StorageCondition>(_storage),
            DefaultStorageLocation = Blank(_location),
            IsHazardous = _hazardous.Checked,
            HazardClass = Blank(_hazardClass),
            IsCritical = _critical.Checked,
            IsActive = _active.Checked,
            Gtin = Blank(_gtin),
            IsCeMarked = _ceMarked.Checked,
            ConsumptionPolicy = policy
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>A supplier, with the ISO 15189 §4.6 evaluation that approval has to rest on.</summary>
internal sealed class SupplierEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _code, _name, _ice, _fiscalId, _rc, _contact, _phone, _email,
                             _address, _city, _notes, _account, _registration;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _registrationValidUntil;
    private readonly Label _registrationNotice;
    private readonly ComboBox _region;
    private readonly NumericUpDown _lead, _terms, _score;
    private readonly CheckBox _approved, _active;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _evaluated;
    private readonly Label _approvalNotice;

    internal SupplierRequest? Request { get; private set; }

    internal SupplierEditorDialog(ILocalizer localizer, IUserDialogs dialogs, SupplierRow? row)
        : base(localizer, dialogs,
               row is null ? "Inventory.NewSupplier" : "Action.Edit", row?.Code, 700, 640)
    {
        _id = row?.SupplierId;

        _code = TextField("Field.Code", 20, 14, 140, row?.Code, latin: true, mono: true);
        _name = TextField("Field.Name", 180, 14, 300, row?.Name);
        _ice = TextField("Site.Ice", 500, 14, 180, row?.Ice, latin: true, mono: true);

        _fiscalId = TextField("Site.FiscalId", 20, 68, 160, row?.FiscalId, latin: true, mono: true);
        _rc = TextField("Site.CommercialRegister", 200, 68, 160, row?.CommercialRegisterNumber, latin: true);
        _contact = TextField("Inventory.SupplierContact", 380, 68, 300, row?.ContactName);

        _phone = TextField("Site.Phone", 20, 122, 180, row?.Phone, latin: true, mono: true);
        _email = TextField("Field.Email", 220, 122, 240, row?.Email, latin: true);
        _city = TextField("Patient.City", 480, 122, 200, row?.City);

        _address = TextField("Patient.Address", 20, 176, 440, row?.AddressLine);
        _region = Choice("Patient.Region", 480, 176, 200, row?.Region ?? MoroccanRegion.Unspecified,
                          "MoroccanRegion");

        _lead = Number("Inventory.LeadTime", 20, 230, 120, row?.LeadTimeDays ?? 7, 365);
        // Loi n° 69-21: 60 days by default, 120 at most by agreement; the field cannot go beyond.
        _terms = Number("Billing.PaymentTerms", 160, 230, 120, row?.PaymentTermsDays ?? PaymentTerms.DefaultDays,
                        PaymentTerms.MaximumDays);
        _account = TextField("SupplierInvoices.SupplierAccount", 300, 230, 160, row?.AccountingAccount,
                             latin: true, mono: true);
        Notice(480, 236, 200, 50, "SupplierInvoices.PaymentTermsNotice");

        Notice(20, 294, 660, 30, "Inventory.SupplierEvaluationNotice", UiTheme.Accent);

        _approved = Check("Inventory.Approved", 20, 332, row?.IsApproved ?? false, bold: true);
        _approved.CheckedChanged += (_, _) => RefreshApproval();

        _evaluated = OptionalDate("Inventory.LastEvaluation", 240, 330, row?.LastEvaluationDate,
                                   max: DateTime.Today);

        _score = Number("Inventory.EvaluationScore", 460, 330, 100, row?.EvaluationScore, 100);

        _approvalNotice = Notice(20, 386, 660, 30, "Supplier_ApprovalNeedsEvaluation",
                                  UiTheme.Abnormal);

        _notes = TextField("Field.Notes", 20, 420, 440, row?.Notes);
        _active = Check("Field.Active", 480, 434, row?.IsActive ?? true);

        // Loi 84-12: the establishment's registration with the AMMPS / DMP to sell IVD devices.
        _registration = TextField("Inventory.RegulatoryRegistration", 20, 478, 300, row?.RegistrationNumber,
                                  latin: true);
        _registrationValidUntil = OptionalDate("Inventory.RegulatoryRegistrationValidUntil", 340, 478,
                                               row?.RegistrationValidUntil);
        _registrationNotice = Notice(20, 530, 660, 30, "Supplier_RegistrationExpiredNotice", UiTheme.Abnormal);
        _registrationValidUntil.Toggle.CheckedChanged += (_, _) => RefreshApproval();
        _registrationValidUntil.Picker.ValueChanged += (_, _) => RefreshApproval();

        Buttons(580, 700);
        UseAdminFrame();
        RefreshApproval();
    }

    /// <summary>
    /// The approval box only means something with an evaluation behind it. Said before Save,
    /// because the service refuses it — ISO 15189 §4.6 is about having assessed the supplier, not
    /// about having declared them approved.
    /// </summary>
    private void RefreshApproval()
    {
        _approvalNotice.Visible = _approved.Checked && !_evaluated.Toggle.Checked;

        // Approving a supplier whose registration has lapsed is allowed, but said.
        if (_registrationNotice is not null)
            _registrationNotice.Visible = _registrationValidUntil.Toggle.Checked
                                          && _registrationValidUntil.Picker.Value.Date < DateTime.Today;
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Supplier_CodeRequired"]);
            _code.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            Dialogs.Warn(Localizer["Supplier_NameRequired"]);
            _name.Focus();
            return;
        }

        if (_approved.Checked && !_evaluated.Toggle.Checked)
        {
            Dialogs.Warn(Localizer["Supplier_ApprovalNeedsEvaluation"]);
            return;
        }

        Request = new SupplierRequest
        {
            SupplierId = _id,
            Code = _code.Text.Trim(),
            Name = _name.Text.Trim(),
            Ice = Blank(_ice),
            FiscalId = Blank(_fiscalId),
            CommercialRegisterNumber = Blank(_rc),
            ContactName = Blank(_contact),
            Phone = Blank(_phone),
            Email = Blank(_email),
            AddressLine = Blank(_address),
            City = Blank(_city),
            Region = Selected<MoroccanRegion>(_region),
            LeadTimeDays = IntOrNull(_lead),
            PaymentTermsDays = (int)_terms.Value,
            AccountingAccount = Blank(_account),
            IsApproved = _approved.Checked,
            LastEvaluationDate = DateOrNull(_evaluated.Toggle, _evaluated.Picker),
            EvaluationScore = IntOrNull(_score),
            Notes = Blank(_notes),
            IsActive = _active.Checked,
            RegulatoryRegistrationNumber = Blank(_registration),
            RegulatoryRegistrationValidUntil = DateOrNull(_registrationValidUntil.Toggle, _registrationValidUntil.Picker)
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// A prescriber.
/// <para>
/// The INPE is the field that matters commercially: an AMO claim carrying this prescriber's
/// prescription cannot be filed without it. The critical-result telephone number is the one that
/// matters clinically — a panic value has to reach a named person on a number somebody recorded.
/// </para>
/// </summary>
internal sealed class PrescriberEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _lastName, _firstName, _nameAr, _specialityFr, _order, _inpe, _ice,
                             _phone, _mobile, _email, _fax, _address, _city, _institution,
                             _criticalPhone, _discount, _notes;
    private readonly ComboBox _kind, _delivery;
    private readonly CheckBox _partial, _active;
    private readonly Label _inpeNotice, _deliveryNotice;
    private readonly PrescriberRequest? _stored;

    internal PrescriberRequest? Request { get; private set; }

    /// <param name="typedName">
    /// For a new prescriber created from the order screen: the name already typed in the list, so
    /// it is not typed twice.
    /// </param>
    /// <param name="stored">
    /// The whole stored prescriber when editing. The fields are filled from it, and the request
    /// saved is built on it, so whatever the dialog does not show is carried through as stored.
    /// </param>
    internal PrescriberEditorDialog(ILocalizer localizer, IUserDialogs dialogs, PrescriberRow? row,
                                    string? typedName = null, PrescriberRequest? stored = null)
        : base(localizer, dialogs,
               row is null ? "Order.NewPrescriber" : "Action.Edit", row?.FullName, 720, 620)
    {
        _id = row?.PrescriberId;
        _stored = row is null ? null : stored;

        // The stored names as they are: splitting the displayed full name at its first space moved
        // half of a compound surname ("El Amrani") into the first name on every save.
        var parts = (row?.FullName ?? typedName?.Trim() ?? string.Empty).Split(' ', 2);

        _lastName = TextField("Patient.LastName", 20, 14, 200,
                              _stored?.LastName ?? parts.ElementAtOrDefault(0));
        _firstName = TextField("Patient.FirstName", 240, 14, 200,
                               _stored?.FirstName ?? parts.ElementAtOrDefault(1));
        _nameAr = TextField("Patient.LastNameArabic", 460, 14, 240, row?.FullNameArabic, arabic: true);

        _kind = Choice("Field.Name", 20, 68, 220, row?.Kind ?? PrescriberKind.GeneralPractitioner,
                        "PrescriberKind");
        _kind.SelectedIndexChanged += (_, _) => RefreshNotices();

        _specialityFr = TextField("Order.Speciality", 260, 68, 200, row?.Speciality?.Fr);
        _institution = TextField("Order.Institution", 480, 68, 220, row?.InstitutionName);

        _order = TextField("Admin.OrderNumber", 20, 122, 200, row?.ProfessionalOrderNumber,
                       latin: true, mono: true);
        _inpe = TextField("Site.Inpe", 240, 122, 170, row?.Inpe, latin: true, mono: true);
        _ice = TextField("Site.Ice", 430, 122, 170, _stored?.Ice, latin: true, mono: true);

        _inpeNotice = Notice(20, 174, 680, 30, "Order.InpeNotice", UiTheme.Abnormal);

        // Each number in its own box. The row carries one telephone (the mobile, else the
        // landline), which used to be offered as the mobile.
        _phone = TextField("Site.Phone", 20, 210, 180, _stored?.Phone, latin: true, mono: true);
        _mobile = TextField("Patient.MobilePhone", 220, 210, 180,
                            _stored is null ? row?.Phone : _stored.MobilePhone,
                            latin: true, mono: true);
        _fax = TextField("Site.Fax", 420, 210, 160, _stored?.Fax, latin: true, mono: true);
        _email = TextField("Field.Email", 20, 264, 280, row?.Email, latin: true);
        _city = TextField("Patient.City", 320, 264, 180, row?.City);
        _address = TextField("Patient.Address", 520, 264, 180, _stored?.AddressLine);

        _criticalPhone = TextField("Order.CriticalResultPhone", 20, 318, 220,
                                   _stored?.CriticalResultPhone, latin: true, mono: true);

        Notice(260, 336, 440, 30, "Order.CriticalPhoneNotice");

        _delivery = Choice("Report.Delivery", 20, 372, 240,
                            row?.DeliveryPreference ?? ReportDeliveryPreference.PatientCollects,
                            "ReportDeliveryPreference");
        _delivery.SelectedIndexChanged += (_, _) => RefreshNotices();

        _partial = Check("Report.SendPartial", 280, 390, row?.SendPartialReports ?? false);

        _deliveryNotice = Notice(20, 424, 680, 30, "Order.DeliveryContactNotice", UiTheme.Abnormal);

        _discount = TextField("Billing.Discount", 20, 458, 120,
                          row?.DiscountPercent?.ToString("0.##"), latin: true, right: true);

        _notes = TextField("Field.Notes", 160, 458, 380, _stored?.Notes);
        _active = Check("Field.Active", 560, 476, row?.IsActive ?? true);

        Buttons(560, 720);
        UseAdminFrame();
        RefreshNotices();
    }

    private void RefreshNotices()
    {
        var kind = Selected<PrescriberKind>(_kind);

        // Only a physician files an AMO claim, so the INPE warning is scoped to those kinds rather
        // than nagging on a dentist or an institution.
        _inpeNotice.Visible = kind is PrescriberKind.GeneralPractitioner
                                   or PrescriberKind.Specialist
                              && string.IsNullOrWhiteSpace(_inpe.Text);

        var delivery = Selected<ReportDeliveryPreference>(_delivery);

        _deliveryNotice.Visible =
            (delivery == ReportDeliveryPreference.Email && string.IsNullOrWhiteSpace(_email.Text))
            || (delivery == ReportDeliveryPreference.SmsLink
                && string.IsNullOrWhiteSpace(_mobile.Text))
            || (delivery == ReportDeliveryPreference.Fax && string.IsNullOrWhiteSpace(_fax.Text));
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_lastName.Text))
        {
            Dialogs.Warn(Localizer["Prescriber_LastNameRequired"]);
            _lastName.Focus();
            return;
        }

        // Built on the stored prescriber, so a field this dialog does not show (the default payer)
        // is saved as it was rather than reset.
        var basis = _stored ?? new PrescriberRequest { LastName = string.Empty, FirstName = string.Empty };

        Request = basis with
        {
            PrescriberId = _id,
            LastName = _lastName.Text.Trim(),
            FirstName = _firstName.Text.Trim(),
            FullNameArabic = Blank(_nameAr),
            Kind = Selected<PrescriberKind>(_kind),
            Speciality = FrenchEdited(_stored?.Speciality, Blank(_specialityFr)),
            ProfessionalOrderNumber = Blank(_order),
            Inpe = Blank(_inpe),
            Ice = Blank(_ice),
            Phone = Blank(_phone),
            MobilePhone = Blank(_mobile),
            Email = Blank(_email),
            Fax = Blank(_fax),
            AddressLine = Blank(_address),
            City = Blank(_city),
            InstitutionName = Blank(_institution),
            DeliveryPreference = Selected<ReportDeliveryPreference>(_delivery),
            SendPartialReports = _partial.Checked,
            CriticalResultPhone = Blank(_criticalPhone),
            DiscountPercent = DecimalOrNull(_discount),
            Notes = Blank(_notes),
            IsActive = _active.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Creating or amending a payer.
/// <para>
/// This dialog decides how every covered invoice divides between the payer and the patient, so two
/// things in it are deliberate.
/// </para>
/// <para>
/// First, the rate is entered as a <em>percentage</em> and stored as a fraction. A convention is
/// written as "70 %", <c>ActPricingCalculator</c> multiplies by 0.70, and the gap between those two
/// is a factor of a hundred applied to real money. The conversion happens here, once, rather than
/// being left to whoever types the field.
/// </para>
/// <para>
/// Second, the rules the domain enforces are applied to the controls as the kind changes, instead
/// of being discovered as a refusal after Save. An AMO scheme settles on the Tarif National de
/// Référence and nothing else, so that flag is ticked and locked; self-pay reimburses nothing and
/// has no convention, so the rate is zeroed and the agreement fields disabled.
/// </para>
/// </summary>
internal sealed class PayerEditorDialog : MasterDataEditor
{
    private readonly Guid? _id;
    private readonly TextBox _code, _nameFr, _nameAr, _nameEn, _agreementNumber, _ice,
                             _contact, _phone, _email, _address, _city, _accountingAccount;
    private readonly ComboBox _kind;
    private readonly NumericUpDown _rate, _terms;
    private readonly CheckBox _tnr, _thirdParty, _requiresPrescription, _requiresInpe, _active;
    private readonly CheckBox _validUntilToggle;
    private readonly DateTimePicker _validUntil;
    private readonly Label _kindNotice;

    internal PayerRequest? Request { get; private set; }

    internal PayerEditorDialog(ILocalizer localizer, IUserDialogs dialogs, PayerRow? row)
        : base(localizer, dialogs,
               row is null ? "Coverage.NewPayer" : "Coverage.EditPayer",
               row?.Code, 740, 600)
    {
        _id = row?.PayerId;

        _code = TextField("Field.Code", 20, 14, 130, row?.Code, latin: true, mono: true);
        _nameFr = TextField("Field.NameFr", 170, 14, 240, row?.Name.Fr);
        _nameAr = TextField("Field.NameAr", 430, 14, 240, row?.Name.Ar, arabic: true);

        _nameEn = TextField("Field.NameEn", 20, 68, 240, row?.Name.En);
        _kind = Choice("Coverage.PayerKind", 280, 68, 240, row?.Kind ?? PayerKind.AmoCnss,
                        "PayerKind");

        _rate = Number("Coverage.ReimbursementRatePercent", 20, 122, 120,
                        row?.ReimbursementPercent ?? 70m, max: 100m, decimals: 2);

        _terms = Number("Coverage.PaymentTermsDays", 152, 122, 120,
                         row?.PaymentTermsDays ?? 60, max: 365m);

        _ice = TextField("Site.Ice", 284, 122, 180, row?.Ice, latin: true, mono: true);

        _tnr = Check("Coverage.ReferenceTariff", 20, 190,
                      row?.UsesNationalReferenceTariff ?? true, bold: true);
        _requiresPrescription = Check("Coverage.RequiresPrescription", 284, 190,
                                       row?.RequiresPrescription ?? true);
        _requiresInpe = Check("Coverage.RequiresInpe", 520, 190,
                               row?.RequiresInpeOnClaim ?? false);

        _thirdParty = Check("Coverage.ThirdPartyPayment", 20, 218,
                             row?.ThirdPartyPaymentAgreement ?? false, bold: true);

        _kindNotice = Notice(20, 244, 690, 30, "Coverage.AmoTariffNotice", UiTheme.Abnormal);

        _agreementNumber = TextField("Coverage.AgreementNumber", 20, 282, 220,
                                      row?.AgreementNumber, latin: true);

        (_validUntilToggle, _validUntil) = OptionalDate("Coverage.AgreementValidUntil", 270, 300,
                                                          row?.AgreementValidUntil);

        _accountingAccount = TextField("Coverage.AccountingAccount", 500, 282, 210, row?.AccountingAccount,
                                       latin: true, mono: true);

        _contact = TextField("Inventory.SupplierContact", 20, 360, 220, row?.ContactName);
        _phone = TextField("Site.Phone", 260, 360, 170, row?.Phone, latin: true, mono: true);
        _email = TextField("Field.Email", 450, 360, 260, row?.Email, latin: true);

        _address = TextField("Patient.Address", 20, 414, 410, row?.AddressLine);
        _city = TextField("Patient.City", 450, 414, 260, row?.City);

        Notice(20, 476, 690, 30, "Coverage.PayerRateNotice");

        _active = Check("Field.Active", 20, 512, row?.IsActive ?? true);

        _kind.SelectedIndexChanged += (_, _) => ApplyKindRules();
        _thirdParty.CheckedChanged += (_, _) => ApplyAgreementRules();

        ApplyKindRules();

        Buttons(542, 740);
        UseAdminFrame();
    }

    /// <summary>
    /// Reflects in the controls what <c>Payer.Validate</c> would otherwise refuse after Save.
    /// </summary>
    private void ApplyKindRules()
    {
        var kind = Selected<PayerKind>(_kind);

        var compulsory = kind is PayerKind.AmoCnss or PayerKind.AmoCnops
                              or PayerKind.AmoTadamon or PayerKind.AmoAchamil;

        if (kind == PayerKind.SelfPay)
        {
            // The patient settles in cash: nothing is reimbursed and there is no convention.
            _rate.Value = 0m;
            _rate.Enabled = false;
            _thirdParty.Checked = false;
            _thirdParty.Enabled = false;
        }
        else
        {
            _rate.Enabled = true;
            _thirdParty.Enabled = true;
        }

        if (compulsory)
        {
            // AMO settles against the TNR. Not the laboratory's choice, so not the operator's.
            _tnr.Checked = true;
            _tnr.Enabled = false;
        }
        else
        {
            _tnr.Enabled = true;
        }

        _kindNotice.Visible = compulsory;
        ApplyAgreementRules();
    }

    private void ApplyAgreementRules()
    {
        var enabled = _thirdParty.Checked && _thirdParty.Enabled;

        _agreementNumber.Enabled = enabled;
        _validUntilToggle.Enabled = enabled;
        _validUntil.Enabled = enabled && _validUntilToggle.Checked;
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_code.Text))
        {
            Dialogs.Warn(Localizer["Payer_CodeRequired"], Text);
            _code.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameFr.Text))
        {
            Dialogs.Warn(Localizer["Payer_NameFrenchRequired"], Text);
            _nameFr.Focus();
            return;
        }

        Request = new PayerRequest
        {
            PayerId = _id,
            Code = _code.Text.Trim().ToUpperInvariant(),
            Name = new MultilingualText(_nameFr.Text.Trim(), Blank(_nameAr), Blank(_nameEn)),
            Kind = Selected<PayerKind>(_kind),

            // Percentage in, fraction out — the one conversion this dialog exists to get right.
            DefaultReimbursementRate = _rate.Value / 100m,

            UsesNationalReferenceTariff = _tnr.Checked,
            ThirdPartyPaymentAgreement = _thirdParty.Checked,
            AgreementNumber = _thirdParty.Checked ? Blank(_agreementNumber) : null,
            AgreementValidUntil = _thirdParty.Checked
                ? DateOrNull(_validUntilToggle, _validUntil)
                : null,
            PaymentTermsDays = (int)_terms.Value,
            RequiresPrescription = _requiresPrescription.Checked,
            RequiresInpeOnClaim = _requiresInpe.Checked,
            Ice = Blank(_ice),
            ContactName = Blank(_contact),
            Phone = Blank(_phone),
            Email = Blank(_email),
            AddressLine = Blank(_address),
            City = Blank(_city),
            AccountingAccount = Blank(_accountingAccount),
            IsActive = _active.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
