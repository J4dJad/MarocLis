using MarocLIS.Application.Administration;
using MarocLIS.Application.Input;
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

        // Several editors open from a menu entry whose label ends with « … »; the window title does not.
        var title = localizer[titleKey].TrimEnd('…');
        Text = subtitle is null ? title : $"{title} — {subtitle}";
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
        ok.Click += (_, _) =>
        {
            if (UnreadableNumber() is { } box)
            {
                Dialogs.Warn(Localizer.Format("Result_NotANumber", box.Text.Trim()), Text);
                box.Focus();
                box.SelectAll();
                return;
            }

            Accept();
        };

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
        AdminDialogFrame.Attach(this, Localizer, AdminDialogFrame.Lead(Localizer, GetType().Name));
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

        // Windows clamps a form taller than the screen before Load, so the client height alone can
        // look as if it fits while the buttons lie below it: measure from the buttons instead.
        var height = Math.Max(ClientSize.Height, Math.Max(_accept.Bottom, _dismiss.Bottom) + 12);
        if (height <= available) return;

        var shift = height - available;
        var footerTop = Math.Min(_accept.Top, _dismiss.Top) - shift;

        if (footerTop <= 0) return;

        SuspendLayout();

        var body = new Panel
        {
            AutoScroll = true,
            AutoScrollMargin = new Size(0, 12),
            BackColor = BackColor,
            Bounds = new Rectangle(0, 0, ClientSize.Width, footerTop - 14)
        };

        // Moved with their coordinates unchanged: the panel sits at the origin, so the layout the
        // editor drew is the layout the operator scrolls through.
        foreach (var control in Controls.Cast<Control>().Where(c => c != _accept && c != _dismiss).ToList())
            body.Controls.Add(control);

        Controls.Add(body);

        // Wider by the scroll bar: the fields were drawn to the dialog's full width, and the bar
        // took its last pixels, which added a horizontal bar and hid the edge of the right column
        // (the start of each row in Arabic).
        var bar = SystemInformation.VerticalScrollBarWidth;
        body.Width += bar;
        _accept.Left += bar;
        _dismiss.Left += bar;

        _accept.Top -= shift;
        _dismiss.Top -= shift;
        var drawn = ClientSize.Width;
        ClientSize = new Size(ClientSize.Width + bar, available);

        // In Arabic the window mirrors its own children, not those of a panel inside it: moved
        // into the body, every field went back to the left-to-right layout (captions on the left,
        // the first field of each row at the left edge). They are mirrored here instead, across
        // the width they were drawn on, which is what the body shows beside its scroll bar.
        if (RightToLeft == RightToLeft.Yes && RightToLeftLayout)
            foreach (Control control in body.Controls) control.Left = drawn - control.Right;

        ResumeLayout(performLayout: true);
    }

    protected abstract void Accept();

    /// <summary>
    /// Says the refusal a shared editor rule returned and puts the cursor back in the box it names.
    /// False when there is none, and the save goes on.
    /// </summary>
    protected bool Refused(FieldRefusal? refusal, params (string Field, Control? Box)[] boxes)
    {
        if (refusal is null) return false;

        Dialogs.Warn(refusal.Typed is { } typed ? Localizer.Format(refusal.Key, typed) : Localizer[refusal.Key], Text);

        var box = boxes.FirstOrDefault(b => b.Field == refusal.Field).Box;
        box?.Focus();
        if (box is TextBox text && refusal.Key == "Result_NotANumber") text.SelectAll();
        return true;
    }

    private readonly List<TextBox> _numbers = [];

    /// <summary>
    /// Marks boxes that hold a number: capped to <paramref name="maxLength"/> characters, and
    /// checked before <see cref="Accept"/>. <see cref="DecimalOrNull"/> reads text it cannot read
    /// as "no value", so "10 %" in a discount, a slip in a critical limit or a figure too large
    /// for its column was saved as an empty limit or a zero — or failed the save — without a word.
    /// </summary>
    protected void Numeric(int maxLength, params TextBox[] boxes)
    {
        foreach (var box in boxes)
        {
            box.MaxLength = maxLength;
            _numbers.Add(box);
        }
    }

    /// <summary>The first enabled number box whose text is no number the editor can save.</summary>
    private TextBox? UnreadableNumber() =>
        _numbers.FirstOrDefault(b => b.Enabled && EditorValues.IsUnreadable(b.Text));

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

        var form = MasterDataEditorRules.FormOf(row, stored);

        _code = TextField("Field.Code", 20, 14, 160, form.Code, latin: true, mono: true);
        _nameFr = TextField("Field.NameFr", 200, 14, 240, form.NameFr);
        _nameAr = TextField("Field.NameAr", 460, 14, 240, form.NameAr, arabic: true);

        _kind = Choice("Equipment.Kind", 20, 68, 200, form.Kind, "EquipmentKind");
        _kind.SelectedIndexChanged += (_, _) => RefreshKindNotice();

        _location = TextField("Equipment.Location", 240, 68, 200, form.Location);
        _serial = TextField("Equipment.SerialNumber", 460, 68, 240, form.SerialNumber,
                        latin: true, mono: true);

        _manufacturer = TextField("Equipment.Manufacturer", 20, 122, 200, form.Manufacturer);
        _model = TextField("Equipment.Model", 240, 122, 200, form.Model);
        _software = TextField("Equipment.SoftwareVersion", 460, 122, 240, form.SoftwareVersion,
                              latin: true);

        _supplier = Lookup("Inventory.Supplier", 20, 176, 300,
                            suppliers.Select(s => (s.SupplierId, $"{s.Code} — {s.Name}")),
                            form.SupplierId);

        _contract = TextField("Equipment.Contract", 340, 176, 200, form.Contract, latin: true);
        _support = TextField("Equipment.SupportPhone", 560, 176, 140, form.SupportPhone,
                             latin: true, mono: true);
        // The lengths of the columns: a longer value was refused by the database with an unexpected
        // error, and the form was lost with it.
        _code.MaxLength = MasterDataEditorRules.CodeMaxLength;
        _nameFr.MaxLength = _nameAr.MaxLength = MasterDataEditorRules.NameMaxLength;
        _location.MaxLength = _manufacturer.MaxLength = _model.MaxLength = MasterDataEditorRules.ManufacturerMaxLength;
        _serial.MaxLength = _contract.MaxLength = MasterDataEditorRules.ReferenceMaxLength;
        _software.MaxLength = MasterDataEditorRules.SoftwareMaxLength;
        _support.MaxLength = MasterDataEditorRules.PhoneMaxLength;

        _contractUntil = OptionalDate("Equipment.ContractValidUntil", 20, 232, form.ContractValidUntil);

        _commissioning = OptionalDate("Equipment.Commissioning", 240, 232,
                                       form.CommissioningDate, max: DateTime.Today);

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
                                       form.MaintenanceIntervalDays, MasterDataEditorRules.IntervalMaxDays);

        _calibrationInterval = Number("Equipment.CalibrationInterval", 240, 366, 180,
                                       form.CalibrationIntervalDays, MasterDataEditorRules.IntervalMaxDays);

        _verificationInterval = Number("Equipment.VerificationInterval", 460, 366, 180,
                                        form.VerificationIntervalDays, MasterDataEditorRules.IntervalMaxDays);

        _lastMaintenance = OptionalDate("Equipment.LastMaintenance", 20, 420,
                                         form.LastMaintenanceOn, max: DateTime.Today);

        _lastCalibration = OptionalDate("Equipment.LastCalibration", 240, 420,
                                         form.LastCalibrationOn, max: DateTime.Today);

        _lastVerification = OptionalDate("Equipment.LastVerification", 460, 420,
                                          form.LastVerificationOn, max: DateTime.Today);

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
        _kindNotice.Visible = MasterDataEditorRules.NeedsCalibrationInterval(Selected<EquipmentKind>(_kind));

    private InstrumentForm Form() => new()
    {
        Code = _code.Text,
        NameFr = _nameFr.Text,
        NameAr = _nameAr.Text,
        Kind = Selected<EquipmentKind>(_kind),
        Location = _location.Text,
        SerialNumber = _serial.Text,
        Manufacturer = _manufacturer.Text,
        Model = _model.Text,
        SoftwareVersion = _software.Text,
        SupplierId = SelectedId(_supplier),
        Contract = _contract.Text,
        SupportPhone = _support.Text,
        ContractValidUntil = DateOrNull(_contractUntil.Toggle, _contractUntil.Picker),
        CommissioningDate = DateOrNull(_commissioning.Toggle, _commissioning.Picker),
        MaintenanceIntervalDays = (int)_maintenanceInterval.Value,
        CalibrationIntervalDays = (int)_calibrationInterval.Value,
        VerificationIntervalDays = (int)_verificationInterval.Value,
        LastMaintenanceOn = DateOrNull(_lastMaintenance.Toggle, _lastMaintenance.Picker),
        LastCalibrationOn = DateOrNull(_lastCalibration.Toggle, _lastCalibration.Picker),
        LastVerificationOn = DateOrNull(_lastVerification.Toggle, _lastVerification.Picker)
    };

    protected override void Accept()
    {
        var form = Form();

        if (Refused(MasterDataEditorRules.Check(form), ("code", _code), ("namefr", _nameFr),
                    ("calibration", _calibrationInterval)))
            return;

        Request = MasterDataEditorRules.ToRequest(form, _id, _stored);

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

        var form = MasterDataEditorRules.FormOf(row, DateOnly.FromDateTime(DateTime.Today));

        _lot = TextField("Quality.QcLot", 20, 14, 200, form.LotNumber, latin: true, mono: true);
        _lot.Enabled = MasterDataEditorRules.KeyEditable(row);   // Lot plus level is the natural key.

        _level = Choice("Quality.QcLevel", 240, 14, 180, form.Level, "QcLevel");
        _level.Enabled = MasterDataEditorRules.KeyEditable(row);

        Label("Inventory.Expiry", 440, 14);
        _expiry = new DateTimePicker
        {
            Bounds = new Rectangle(440, 32, 160, UiTheme.InputHeight),
            Format = DateTimePickerFormat.Short
        };

        // An already-lapsed expiry cannot be shown by a picker whose minimum is today, and
        // clamping it silently would edit the record just by opening it. The minimum is therefore
        // chosen before the value is assigned: assigning a lapsed date first throws.
        var expiryDate = form.ExpiryDate.ToDateTime(TimeOnly.MinValue);

        _expiry.MinDate = expiryDate < DateTime.Today ? new DateTime(2000, 1, 1) : DateTime.Today;
        _expiry.Value = expiryDate;

        Controls.Add(_expiry);

        _nameFr = TextField("Field.NameFr", 20, 68, 260, form.NameFr);
        _nameAr = TextField("Field.NameAr", 300, 68, 300, form.NameAr, arabic: true);

        _manufacturer = TextField("Equipment.Manufacturer", 20, 122, 260, form.Manufacturer);
        _reference = TextField("Inventory.SupplierReference", 300, 122, 300, form.CatalogueReference,
                           latin: true);

        // Each box holds no more than its column: a longer entry otherwise ends in « Erreur inattendue » at the save.
        _lot.MaxLength = MasterDataEditorRules.LotMaxLength;
        _nameFr.MaxLength = _nameAr.MaxLength = MasterDataEditorRules.NameMaxLength;
        _manufacturer.MaxLength = MasterDataEditorRules.ManufacturerMaxLength;
        _reference.MaxLength = MasterDataEditorRules.ReferenceMaxLength;

        _opened = OptionalDate("Inventory.OpenedOn", 20, 180, form.OpenedOn, max: DateTime.Today);

        _stability = Number("Inventory.OpenStabilityDays", 240, 180, 120,
                             form.StabilityAfterOpeningDays, MasterDataEditorRules.IntervalMaxDays);

        _active = Check("Field.Active", 20, 250, form.Active);

        Notice(20, 276, 580, 46, "Quality.MaterialStabilityNotice");

        Buttons(352, 620);
        UseAdminFrame();
    }

    private QcMaterialForm Form() => new()
    {
        LotNumber = _lot.Text,
        Level = Selected<QcLevel>(_level),
        ExpiryDate = DateOnly.FromDateTime(_expiry.Value.Date),
        NameFr = _nameFr.Text,
        NameAr = _nameAr.Text,
        Manufacturer = _manufacturer.Text,
        CatalogueReference = _reference.Text,
        OpenedOn = DateOrNull(_opened.Toggle, _opened.Picker),
        StabilityAfterOpeningDays = (int)_stability.Value,
        Active = _active.Checked
    };

    protected override void Accept()
    {
        var form = Form();

        if (Refused(MasterDataEditorRules.Check(form), ("lot", _lot), ("namefr", _nameFr))) return;

        Request = MasterDataEditorRules.ToRequest(form, _id);

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

        var form = MasterDataEditorRules.FormOf(row);
        _rules = form.Rules;

        _material = Lookup("Quality.QcMaterial", 20, 14, 280,
                            materials.Select(m => (m.QcMaterialId,
                                $"{m.LotNumber} — {localizer[$"QcLevel.{m.Level}"]}")),
                            form.MaterialId, allowNone: false);

        _instrument = Lookup("Result.Instrument", 320, 14, 280,
                              instruments.Select(i => (i.InstrumentId, $"{i.Code} — {i.Name.Fr}")),
                              form.InstrumentId, allowNone: false);

        _parameter = Lookup("Result.Parameter", 20, 68, 580,
                             parameters.Select(p => (p.TestParameterId,
                                 $"{p.AnalysisCode} · {p.ParameterCode} — {p.ParameterName.Fr}"
                                 + (p.UnitSymbol is { Length: > 0 } u ? $" ({u})" : string.Empty))),
                             form.ParameterId, allowNone: false);

        _material.Enabled = _instrument.Enabled = _parameter.Enabled = MasterDataEditorRules.KeyEditable(row);

        Notice(20, 122, 580, 32, "Quality.AssignedLimitsNotice", UiTheme.Accent);

        _mean = TextField("Quality.AssignedMean", 20, 162, 140, form.Mean, latin: true, mono: true, right: true);

        _sd = TextField("Quality.AssignedSd", 180, 162, 140, form.Sd, latin: true, mono: true, right: true);

        _cv = TextField("Quality.MaxCv", 340, 162, 140, form.MaxCv, latin: true, right: true);

        Numeric(EditorValues.MeasureMaxLength, _mean, _sd, _cv);

        _cvEcho = Notice(500, 180, 120, 20, "Quality.CvEcho", UiTheme.TextSecondary);

        _mean.TextChanged += (_, _) => RefreshCv();
        _sd.TextChanged += (_, _) => RefreshCv();

        Label("Quality.Rules", 20, 220);
        _ruleButton = UiTheme.SecondaryButton(string.Empty);
        _ruleButton.Bounds = new Rectangle(20, 238, 580, UiTheme.ButtonHeight);
        _ruleButton.Click += (_, _) => PickRules();
        Controls.Add(_ruleButton);

        _active = Check("Field.Active", 20, 286, form.Active);

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
    private void RefreshCv() => _cvEcho.Text = MasterDataEditorRules.CvEcho(_mean.Text, _sd.Text);

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
        var count = MasterDataScreenRules.CountRules(_rules);

        _ruleButton.Text = MasterDataScreenRules.RuleSummary(_rules, Localizer.Format);

        _ruleButton.ForeColor = count == 0 ? UiTheme.Critical : UiTheme.TextPrimary;
        _ruleButton.Font = count == 0 ? UiTheme.BodyBold : UiTheme.Body;
    }

    private QcTargetForm Form() => new()
    {
        MaterialId = SelectedId(_material),
        InstrumentId = SelectedId(_instrument),
        ParameterId = SelectedId(_parameter),
        Mean = _mean.Text,
        Sd = _sd.Text,
        MaxCv = _cv.Text,
        Rules = _rules,
        Active = _active.Checked
    };

    protected override void Accept()
    {
        var form = Form();

        if (Refused(MasterDataEditorRules.Check(form), ("mean", _mean), ("sd", _sd), ("cv", _cv),
                    ("material", _material), ("rules", _ruleButton)))
            return;

        Request = MasterDataEditorRules.ToRequest(form, _id);

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

        var form = MasterDataEditorRules.FormOf(row, System.Globalization.CultureInfo.CurrentCulture);

        _code = TextField("Field.Code", 20, 14, 160, form.Code, latin: true, mono: true);
        _nameFr = TextField("Field.NameFr", 200, 14, 240, form.NameFr);
        _nameAr = TextField("Field.NameAr", 460, 14, 240, form.NameAr, arabic: true);

        _kind = Choice("Field.Name", 20, 68, 200, form.Kind, "StockItemKind");
        _unit = TextField("Field.Unit", 240, 68, 100, form.Unit);
        _tests = Number("Inventory.TestsPerPack", 360, 68, 100, form.TestsPerPack, MasterDataEditorRules.TestsPerPackMax);

        _storage = Choice("Sample.Storage", 480, 68, 220, form.Storage, "StorageCondition");

        _manufacturer = TextField("Equipment.Manufacturer", 20, 122, 200, form.Manufacturer);
        _supplier = Lookup("Inventory.Supplier", 240, 122, 240,
                            suppliers.Select(s => (s.SupplierId, $"{s.Code} — {s.Name}")),
                            form.SupplierId);
        _reference = TextField("Inventory.SupplierReference", 500, 122, 200, form.SupplierReference,
                               latin: true);

        _analyses = TextField("Inventory.UsedByAnalyses", 20, 176, 460, form.UsedByAnalyses, latin: true);
        _location = TextField("Inventory.DefaultLocation", 500, 176, 200, form.Location);

        Notice(20, 230, 680, 30, "Inventory.ReorderNotice", UiTheme.Accent);

        // The reorder point's caption is the longest: its column is wider so it no longer runs
        // into « Quantité à commander ».
        _minimum = TextField("Inventory.MinimumStock", 20, 266, 130, form.Minimum, latin: true, right: true);
        _reorder = TextField("Inventory.ReorderPoint", 170, 266, 170, form.Reorder, latin: true, right: true);
        _reorderQuantity = TextField("Inventory.ReorderQuantity", 360, 266, 160, form.ReorderQuantity,
                                 latin: true, right: true);
        _maximum = TextField("Inventory.MaximumStock", 540, 266, 160, form.Maximum, latin: true, right: true);

        Numeric(EditorValues.MeasureMaxLength, _minimum, _reorder, _reorderQuantity, _maximum);

        _critical = Check("Inventory.IsCritical", 20, 330, form.Critical, bold: true);
        Notice(20, 354, 680, 30, "Inventory.CriticalNotice");

        _hazardous = Check("Inventory.IsHazardous", 20, 392, form.Hazardous);
        _hazardClass = TextField("Inventory.HazardClass", 240, 386, 200, form.HazardClass, latin: true);
        _hazardClass.Enabled = MasterDataEditorRules.HazardClassEnabled(form);
        _hazardous.CheckedChanged += (_, _) =>
            _hazardClass.Enabled = MasterDataEditorRules.HazardClassEnabled(new StockItemForm { Hazardous = _hazardous.Checked });

        _active = Check("Field.Active", 480, 392, form.Active);

        // GS1: the GTIN printed in the DataMatrix, so a scan at reception picks this article.
        _gtin = TextField("Inventory.Gtin", 20, 446, 200, form.Gtin, latin: true, mono: true);
        _ceMarked = Check("Inventory.IsCeMarked", 240, 470, form.CeMarked);
        Notice(20, 500, 680, 30, "Inventory.GtinNotice");

        // What a result costs in tests and what an opening loses, for the theoretical consumption;
        // the safety margin and the shelf life, for the computed reorder point and the order cap.
        static string N(decimal? v) => v?.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

        Notice(20, 540, 680, 20, "Inventory.ConsumptionNotice", UiTheme.Accent);
        _testsPerResult = TextField("Inventory.TestsPerResult", 20, 562, 300, form.TestsPerResult, latin: true, mono: true);

        // Each box holds no more than its column: a longer entry otherwise ends in « Erreur inattendue » at the save.
        _code.MaxLength = MasterDataEditorRules.StockCodeMaxLength;
        _nameFr.MaxLength = _nameAr.MaxLength = MasterDataEditorRules.NameMaxLength;
        _unit.MaxLength = MasterDataEditorRules.UnitMaxLength;
        _manufacturer.MaxLength = MasterDataEditorRules.ManufacturerMaxLength;
        _reference.MaxLength = _location.MaxLength = MasterDataEditorRules.ReferenceMaxLength;
        _analyses.MaxLength = MasterDataEditorRules.AnalysesMaxLength;
        _hazardClass.MaxLength = MasterDataEditorRules.HazardClassMaxLength;
        _gtin.MaxLength = MasterDataEditorRules.GtinMaxLength;
        _testsPerResult.MaxLength = MasterDataEditorRules.TestsPerResultMaxLength;
        _deadPercent = TextField("Inventory.DeadVolumePercent", 340, 562, 170, form.DeadVolumePercent,
                                 latin: true, right: true);
        _deadTests = TextField("Inventory.DeadVolumeTests", 530, 562, 170, form.DeadVolumeTests,
                               latin: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _deadPercent, _deadTests);

        _calibrationTests = Number("Inventory.TestsPerCalibration", 20, 616, 150, form.TestsPerCalibration,
                                   MasterDataEditorRules.TestsPerCalibrationMax);
        _safetyDays = Number("Inventory.SafetyStockDays", 190, 616, 150, form.SafetyStockDays,
                             MasterDataEditorRules.SafetyDaysMax);
        _shelfLife = Number("Inventory.ShelfLifeDays", 360, 616, 150, form.ShelfLifeDays,
                            MasterDataEditorRules.IntervalMaxDays);

        _computed = Check("Inventory.ReorderPointComputed", 20, 672, form.ReorderPointComputed, bold: true);

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

    private StockItemForm Form() => new()
    {
        Code = _code.Text,
        NameFr = _nameFr.Text,
        NameAr = _nameAr.Text,
        Kind = Selected<StockItemKind>(_kind),
        Unit = _unit.Text,
        TestsPerPack = (int)_tests.Value,
        Storage = Selected<StorageCondition>(_storage),
        Manufacturer = _manufacturer.Text,
        SupplierId = SelectedId(_supplier),
        SupplierReference = _reference.Text,
        UsedByAnalyses = _analyses.Text,
        Location = _location.Text,
        Minimum = _minimum.Text,
        Reorder = _reorder.Text,
        ReorderQuantity = _reorderQuantity.Text,
        Maximum = _maximum.Text,
        Critical = _critical.Checked,
        Hazardous = _hazardous.Checked,
        HazardClass = _hazardClass.Text,
        Active = _active.Checked,
        Gtin = _gtin.Text,
        CeMarked = _ceMarked.Checked,
        TestsPerResult = _testsPerResult.Text,
        DeadVolumePercent = _deadPercent.Text,
        DeadVolumeTests = _deadTests.Text,
        TestsPerCalibration = (int)_calibrationTests.Value,
        SafetyStockDays = (int)_safetyDays.Value,
        ShelfLifeDays = (int)_shelfLife.Value,
        ReorderPointComputed = _computed.Checked
    };

    protected override void Accept()
    {
        var form = Form();

        // The reorder point below the safety stock is caught here as well as in the service: the
        // alert would fire after the laboratory is already below the level it meant to hold.
        if (Refused(MasterDataEditorRules.Check(form), ("minimum", _minimum), ("reorder", _reorder),
                    ("reorderquantity", _reorderQuantity), ("maximum", _maximum), ("code", _code),
                    ("namefr", _nameFr), ("unit", _unit), ("deadpercent", _deadPercent)))
            return;

        if (MasterDataEditorRules.Policy(form).Validate() is { IsFailure: true } invalid)
        {
            Dialogs.Warn(Localizer.Describe(invalid.Errors));
            _testsPerResult.Focus();
            return;
        }

        Request = MasterDataEditorRules.ToRequest(form, _id);

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

        var form = MasterDataEditorRules.FormOf(row);

        _code = TextField("Field.Code", 20, 14, 140, form.Code, latin: true, mono: true);
        _name = TextField("Field.Name", 180, 14, 300, form.Name);
        _ice = TextField("Site.Ice", 500, 14, 180, form.Ice, latin: true, mono: true);

        _fiscalId = TextField("Site.FiscalId", 20, 68, 160, form.FiscalId, latin: true, mono: true);
        _rc = TextField("Site.CommercialRegister", 200, 68, 160, form.CommercialRegister, latin: true);
        _contact = TextField("Inventory.SupplierContact", 380, 68, 300, form.Contact);

        _phone = TextField("Site.Phone", 20, 122, 180, form.Phone, latin: true, mono: true);
        _email = TextField("Field.Email", 220, 122, 240, form.Email, latin: true);
        _city = TextField("Patient.City", 480, 122, 200, form.City);

        _address = TextField("Patient.Address", 20, 176, 440, form.Address);
        _region = Choice("Patient.Region", 480, 176, 200, form.Region, "MoroccanRegion");

        _lead = Number("Inventory.LeadTime", 20, 230, 120, form.LeadTimeDays, MasterDataEditorRules.LeadTimeMax);
        // Loi n° 69-21: 60 days by default, 120 at most by agreement; the field cannot go beyond.
        _terms = Number("Billing.PaymentTerms", 160, 230, 120, form.PaymentTermsDays, PaymentTerms.MaximumDays);
        _account = TextField("SupplierInvoices.SupplierAccount", 300, 230, 160, form.AccountingAccount,
                             latin: true, mono: true);
        Notice(480, 236, 200, 50, "SupplierInvoices.PaymentTermsNotice");

        Notice(20, 294, 660, 30, "Inventory.SupplierEvaluationNotice", UiTheme.Accent);

        _approved = Check("Inventory.Approved", 20, 332, form.Approved, bold: true);
        _approved.CheckedChanged += (_, _) => RefreshApproval();

        _evaluated = OptionalDate("Inventory.LastEvaluation", 240, 330, form.LastEvaluationDate,
                                   max: DateTime.Today);

        _score = Number("Inventory.EvaluationScore", 460, 330, 100, form.EvaluationScore, MasterDataEditorRules.ScoreMax);

        _approvalNotice = Notice(20, 386, 660, 30, "Supplier_ApprovalNeedsEvaluation",
                                  UiTheme.Abnormal);

        _notes = TextField("Field.Notes", 20, 420, 440, form.Notes);
        _active = Check("Field.Active", 480, 434, form.Active);

        // Loi 84-12: the establishment's registration with the AMMPS / DMP to sell IVD devices.
        _registration = TextField("Inventory.RegulatoryRegistration", 20, 478, 300, form.Registration,
                                  latin: true);

        // Each box holds no more than its column: a longer entry otherwise ends in « Erreur inattendue » at the save.
        _code.MaxLength = MasterDataEditorRules.CodeMaxLength;
        _name.MaxLength = _contact.MaxLength = _email.MaxLength = MasterDataEditorRules.LongNameMaxLength;
        _ice.MaxLength = MasterDataEditorRules.IceMaxLength;
        _fiscalId.MaxLength = _account.MaxLength = MasterDataEditorRules.FiscalIdMaxLength;
        _rc.MaxLength = MasterDataEditorRules.RegisterMaxLength;
        _phone.MaxLength = MasterDataEditorRules.PhoneMaxLength;
        _address.MaxLength = MasterDataEditorRules.AddressMaxLength;
        _city.MaxLength = MasterDataEditorRules.CityMaxLength;
        _notes.MaxLength = MasterDataEditorRules.NotesMaxLength;
        _registration.MaxLength = MasterDataEditorRules.RegistrationMaxLength;
        _registrationValidUntil = OptionalDate("Inventory.RegulatoryRegistrationValidUntil", 340, 478,
                                               form.RegistrationValidUntil);
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
        // Built once all the boxes exist: the handlers fire while the dialog is being laid out.
        if (_registrationNotice is null) return;

        var form = Form();
        _approvalNotice.Visible = MasterDataEditorRules.ApprovalNeedsEvaluation(form);

        // Approving a supplier whose registration has lapsed is allowed, but said.
        _registrationNotice.Visible = MasterDataEditorRules.RegistrationExpired(form, DateOnly.FromDateTime(DateTime.Today));
    }

    private SupplierForm Form() => new()
    {
        Code = _code.Text,
        Name = _name.Text,
        Ice = _ice.Text,
        FiscalId = _fiscalId.Text,
        CommercialRegister = _rc.Text,
        Contact = _contact.Text,
        Phone = _phone.Text,
        Email = _email.Text,
        City = _city.Text,
        Address = _address.Text,
        Region = Selected<MoroccanRegion>(_region),
        LeadTimeDays = (int)_lead.Value,
        PaymentTermsDays = (int)_terms.Value,
        AccountingAccount = _account.Text,
        Approved = _approved.Checked,
        LastEvaluationDate = DateOrNull(_evaluated.Toggle, _evaluated.Picker),
        EvaluationScore = (int)_score.Value,
        Notes = _notes.Text,
        Active = _active.Checked,
        Registration = _registration.Text,
        RegistrationValidUntil = DateOrNull(_registrationValidUntil.Toggle, _registrationValidUntil.Picker)
    };

    protected override void Accept()
    {
        var form = Form();

        if (Refused(MasterDataEditorRules.Check(form), ("code", _code), ("name", _name))) return;

        Request = MasterDataEditorRules.ToRequest(form, _id);

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
        var form = MasterDataEditorRules.FormOf(row, stored, typedName, System.Globalization.CultureInfo.CurrentCulture);

        _lastName = TextField("Patient.LastName", 20, 14, 200, form.LastName);
        _firstName = TextField("Patient.FirstName", 240, 14, 200, form.FirstName);
        _nameAr = TextField("Patient.LastNameArabic", 460, 14, 240, form.NameArabic, arabic: true);

        _kind = Choice("Field.Name", 20, 68, 220, form.Kind, "PrescriberKind");
        _kind.SelectedIndexChanged += (_, _) => RefreshNotices();

        _specialityFr = TextField("Order.Speciality", 260, 68, 200, form.Speciality);
        _institution = TextField("Order.Institution", 480, 68, 220, form.Institution);

        _order = TextField("Admin.OrderNumber", 20, 122, 200, form.OrderNumber, latin: true, mono: true);
        _inpe = TextField("Site.Inpe", 240, 122, 170, form.Inpe, latin: true, mono: true);
        // Clear of the INPE caption, which runs past its box in Arabic.
        _ice = TextField("Site.Ice", 470, 122, 170, form.Ice, latin: true, mono: true);

        _inpeNotice = Notice(20, 174, 680, 30, "Order.InpeNotice", UiTheme.Abnormal);

        // Each number in its own box. The row carries one telephone (the mobile, else the
        // landline), which used to be offered as the mobile.
        _phone = TextField("Site.Phone", 20, 210, 180, form.Phone, latin: true, mono: true);
        _mobile = TextField("Patient.MobilePhone", 220, 210, 180, form.Mobile, latin: true, mono: true);
        _fax = TextField("Site.Fax", 420, 210, 160, form.Fax, latin: true, mono: true);
        _email = TextField("Field.Email", 20, 264, 280, form.Email, latin: true);
        _city = TextField("Patient.City", 320, 264, 180, form.City);
        _address = TextField("Patient.Address", 520, 264, 180, form.Address);

        _criticalPhone = TextField("Order.CriticalResultPhone", 20, 318, 220,
                                   form.CriticalPhone, latin: true, mono: true);

        Notice(260, 336, 440, 30, "Order.CriticalPhoneNotice");

        _delivery = Choice("Report.Delivery", 20, 372, 240, form.Delivery, "ReportDeliveryPreference");
        _delivery.SelectedIndexChanged += (_, _) => RefreshNotices();

        _partial = Check("Report.SendPartial", 280, 390, form.SendPartial);

        _deliveryNotice = Notice(20, 424, 680, 30, "Order.DeliveryContactNotice", UiTheme.Abnormal);

        _discount = TextField("Billing.Discount", 20, 458, 120, form.Discount, latin: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _discount);

        _notes = TextField("Field.Notes", 160, 458, 380, form.Notes);

        // Each box holds no more than its column: a longer entry otherwise ends in « Erreur inattendue » at the save.
        _lastName.MaxLength = _firstName.MaxLength = MasterDataEditorRules.PersonNameMaxLength;
        _nameAr.MaxLength = _email.MaxLength = _institution.MaxLength = MasterDataEditorRules.LongNameMaxLength;
        _specialityFr.MaxLength = MasterDataEditorRules.SpecialityMaxLength;
        _order.MaxLength = MasterDataEditorRules.OrderNumberMaxLength;
        _inpe.MaxLength = _mobile.MaxLength = _criticalPhone.MaxLength = MasterDataEditorRules.ShortPhoneMaxLength;
        _ice.MaxLength = MasterDataEditorRules.IceMaxLength;
        _phone.MaxLength = _fax.MaxLength = MasterDataEditorRules.PhoneMaxLength;
        _address.MaxLength = MasterDataEditorRules.AddressMaxLength;
        _city.MaxLength = MasterDataEditorRules.CityMaxLength;
        _notes.MaxLength = MasterDataEditorRules.NotesMaxLength;
        _active = Check("Field.Active", 560, 476, form.Active);

        Buttons(560, 720);
        UseAdminFrame();
        RefreshNotices();
    }

    private void RefreshNotices()
    {
        var form = Form();

        // Only a physician files an AMO claim, so the INPE warning is scoped to those kinds rather
        // than nagging on a dentist or an institution.
        _inpeNotice.Visible = PrescriberEntryRules.WarnsOfMissingInpe(form.Kind, form.Inpe);
        _deliveryNotice.Visible = MasterDataEditorRules.DeliveryContactMissing(form);
    }

    private PrescriberForm Form() => new()
    {
        LastName = _lastName.Text,
        FirstName = _firstName.Text,
        NameArabic = _nameAr.Text,
        Kind = Selected<PrescriberKind>(_kind),
        Speciality = _specialityFr.Text,
        Institution = _institution.Text,
        OrderNumber = _order.Text,
        Inpe = _inpe.Text,
        Ice = _ice.Text,
        Phone = _phone.Text,
        Mobile = _mobile.Text,
        Fax = _fax.Text,
        Email = _email.Text,
        City = _city.Text,
        Address = _address.Text,
        CriticalPhone = _criticalPhone.Text,
        Delivery = Selected<ReportDeliveryPreference>(_delivery),
        SendPartial = _partial.Checked,
        Discount = _discount.Text,
        Notes = _notes.Text,
        Active = _active.Checked
    };

    protected override void Accept()
    {
        var form = Form();

        if (Refused(MasterDataEditorRules.Check(form), ("discount", _discount), ("lastname", _lastName))) return;

        // Built on the stored prescriber, so a field this dialog does not show (the default payer)
        // is saved as it was rather than reset.
        Request = MasterDataEditorRules.ToRequest(form, _id, _stored);

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

        var form = MasterDataEditorRules.FormOf(row);

        _code = TextField("Field.Code", 20, 14, 130, form.Code, latin: true, mono: true);
        _nameFr = TextField("Field.NameFr", 170, 14, 240, form.NameFr);
        _nameAr = TextField("Field.NameAr", 430, 14, 240, form.NameAr, arabic: true);

        _nameEn = TextField("Field.NameEn", 20, 68, 240, form.NameEn);
        _kind = Choice("Coverage.PayerKind", 280, 68, 240, form.Kind, "PayerKind");

        // A column each as wide as its caption: 132 pixels apart, « Taux de remboursement (%) »,
        // « Délai de règlement (jours) » and « ICE » were written over one another.
        _rate = Number("Coverage.ReimbursementRatePercent", 20, 122, 120,
                        form.RatePercent, max: 100m, decimals: 2);

        _terms = Number("Coverage.PaymentTermsDays", 230, 122, 120,
                         form.PaymentTermsDays, max: MasterDataEditorRules.PayerTermsMax);

        _ice = TextField("Site.Ice", 440, 122, 180, form.Ice, latin: true, mono: true);

        _tnr = Check("Coverage.ReferenceTariff", 20, 190, form.UsesReferenceTariff, bold: true);
        _requiresPrescription = Check("Coverage.RequiresPrescription", 284, 190, form.RequiresPrescription);
        _requiresInpe = Check("Coverage.RequiresInpe", 520, 190, form.RequiresInpe);

        _thirdParty = Check("Coverage.ThirdPartyPayment", 20, 218, form.ThirdParty, bold: true);

        _kindNotice = Notice(20, 244, 690, 30, "Coverage.AmoTariffNotice", UiTheme.Abnormal);

        _agreementNumber = TextField("Coverage.AgreementNumber", 20, 282, 220, form.AgreementNumber, latin: true);

        (_validUntilToggle, _validUntil) = OptionalDate("Coverage.AgreementValidUntil", 270, 300,
                                                          form.AgreementValidUntil);

        _accountingAccount = TextField("Coverage.AccountingAccount", 500, 282, 210, form.AccountingAccount,
                                       latin: true, mono: true);

        _contact = TextField("Inventory.SupplierContact", 20, 360, 220, form.Contact);
        _phone = TextField("Site.Phone", 260, 360, 170, form.Phone, latin: true, mono: true);
        _email = TextField("Field.Email", 450, 360, 260, form.Email, latin: true);

        _address = TextField("Patient.Address", 20, 414, 410, form.Address);
        _city = TextField("Patient.City", 450, 414, 260, form.City);

        // Each box holds no more than its column: a longer entry otherwise ends in « Erreur inattendue » at the save.
        _code.MaxLength = MasterDataEditorRules.CodeMaxLength;
        _nameFr.MaxLength = _nameAr.MaxLength = _nameEn.MaxLength = MasterDataEditorRules.NameMaxLength;
        _agreementNumber.MaxLength = MasterDataEditorRules.AgreementMaxLength;
        _ice.MaxLength = MasterDataEditorRules.IceMaxLength;
        _contact.MaxLength = _email.MaxLength = MasterDataEditorRules.LongNameMaxLength;
        _phone.MaxLength = MasterDataEditorRules.PhoneMaxLength;
        _address.MaxLength = MasterDataEditorRules.AddressMaxLength;
        _city.MaxLength = MasterDataEditorRules.CityMaxLength;
        _accountingAccount.MaxLength = MasterDataEditorRules.FiscalIdMaxLength;

        Notice(20, 476, 690, 30, "Coverage.PayerRateNotice");

        _active = Check("Field.Active", 20, 512, form.Active);

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
        var fields = MasterDataEditorRules.Fields(kind);
        var applied = MasterDataEditorRules.WithKind(Form(), kind);

        // Self-pay: the patient settles in cash, nothing is reimbursed and there is no convention.
        // AMO settles against the TNR: not the laboratory's choice, so not the operator's.
        _rate.Value = applied.RatePercent;
        _rate.Enabled = fields.RateEnabled;
        _thirdParty.Checked = applied.ThirdParty;
        _thirdParty.Enabled = fields.ThirdPartyEnabled;
        _tnr.Checked = applied.UsesReferenceTariff;
        _tnr.Enabled = !fields.ReferenceTariffLocked;

        _kindNotice.Visible = MasterDataEditorRules.IsCompulsory(kind);
        ApplyAgreementRules();
    }

    private void ApplyAgreementRules()
    {
        var enabled = MasterDataEditorRules.AgreementEnabled(Form());

        _agreementNumber.Enabled = enabled;
        _validUntilToggle.Enabled = enabled;
        _validUntil.Enabled = enabled && _validUntilToggle.Checked;
    }

    private PayerForm Form() => new()
    {
        Code = _code.Text,
        NameFr = _nameFr.Text,
        NameAr = _nameAr.Text,
        NameEn = _nameEn.Text,
        Kind = Selected<PayerKind>(_kind),
        RatePercent = _rate.Value,
        PaymentTermsDays = (int)_terms.Value,
        Ice = _ice.Text,
        UsesReferenceTariff = _tnr.Checked,
        RequiresPrescription = _requiresPrescription.Checked,
        RequiresInpe = _requiresInpe.Checked,
        ThirdParty = _thirdParty.Checked,
        AgreementNumber = _agreementNumber.Text,
        AgreementValidUntil = DateOrNull(_validUntilToggle, _validUntil),
        AccountingAccount = _accountingAccount.Text,
        Contact = _contact.Text,
        Phone = _phone.Text,
        Email = _email.Text,
        Address = _address.Text,
        City = _city.Text,
        Active = _active.Checked
    };

    protected override void Accept()
    {
        var form = Form();

        if (Refused(MasterDataEditorRules.Check(form), ("code", _code), ("namefr", _nameFr))) return;

        Request = MasterDataEditorRules.ToRequest(form, _id);

        DialogResult = DialogResult.OK;
        Close();
    }
}
