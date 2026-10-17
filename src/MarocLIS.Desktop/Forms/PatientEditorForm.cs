using MarocLIS.Application.Patients;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The patient file editor — the entry point to the whole workflow, since nothing downstream
/// exists without a patient.
/// <para>
/// One form serves creation and editing, and is opened both from the patient list and from the
/// order screen. That matters operationally: a walk-in patient is registered by the same clerk, at
/// the same counter, in the same minute as their order, and forcing them to two screens is how a
/// half-registered file gets left behind.
/// </para>
/// <para>
/// The coverage and consent tabs are not optional detail. The coverage decides what the patient
/// pays at the counter, and the consent is what makes a genetic or HIV analysis legally
/// performable — <c>OrderRegistrationService</c> refuses those analyses outright without one.
/// </para>
/// </summary>
public sealed class PatientEditorForm : LocalizedForm
{
    private readonly PatientRegistrationService _service;
    private readonly IPatientQueries _queries;
    private readonly IUserDialogs _dialogs;

    private readonly Guid? _patientId;

    private readonly TabControl _tabs = new();
    private readonly TabPage _identityTab = new();
    private readonly TabPage _contactTab = new();
    private readonly TabPage _clinicalTab = new();
    private readonly TabPage _coverageTab = new();
    private readonly TabPage _consentTab = new();
    private readonly TabPage _alertTab = new();

    // Identity
    private readonly TextBox _lastName = UiTheme.Input(220);
    private readonly TextBox _firstName = UiTheme.Input(220);
    private readonly TextBox _lastNameAr = UiTheme.Input(220);
    private readonly TextBox _firstNameAr = UiTheme.Input(220);
    private readonly ComboBox _gender = new();
    private readonly DateTimePicker _dateOfBirth = new();
    private readonly CheckBox _hasDateOfBirth = new();
    private readonly NumericUpDown _declaredAge = new();
    private readonly TextBox _placeOfBirth = UiTheme.Input(220);
    private readonly TextBox _cin = UiTheme.Input(160);
    private readonly TextBox _passport = UiTheme.Input(160);
    private readonly TextBox _nationality = UiTheme.Input(80);
    private readonly Label _ageEcho = new();

    private readonly Panel _guardianBox = new();
    private readonly TextBox _guardianName = UiTheme.Input(220);
    private readonly TextBox _guardianCin = UiTheme.Input(160);
    private readonly TextBox _guardianRelationship = UiTheme.Input(160);
    private readonly Label _guardianNotice = new();

    // Contact
    private readonly TextBox _address = UiTheme.Input(440);
    private readonly TextBox _city = UiTheme.Input(220);
    private readonly ComboBox _region = new();
    private readonly TextBox _postalCode = UiTheme.Input(100);
    private readonly TextBox _mobile = UiTheme.Input(180);
    private readonly TextBox _altPhone = UiTheme.Input(180);
    private readonly TextBox _email = UiTheme.Input(280);
    private readonly ComboBox _language = new();
    private readonly CheckBox _optOut = new();
    private readonly Label _phoneNotice = new();

    // Clinical
    private readonly CheckBox _pregnant = new();
    private readonly DateTimePicker _lmp = new();
    private readonly CheckBox _hasLmp = new();
    private readonly TextBox _weight = UiTheme.Input(100);
    private readonly TextBox _height = UiTheme.Input(100);
    private readonly ComboBox _bloodGroup = new();
    private readonly TextBox _treatment = UiTheme.Input(440);
    private readonly TextBox _allergies = UiTheme.Input(440);

    // Coverage
    private readonly DataGridView _coverages = UiTheme.Grid();
    private readonly Button _addCoverage = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _endCoverage = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _coverageNotice = new();

    // Consent
    private readonly DataGridView _consents = UiTheme.Grid();
    private readonly Button _addConsent = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _withdrawConsent = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _consentNotice = new();

    // Alerts
    private readonly DataGridView _alerts = UiTheme.Grid();
    private readonly Button _addAlert = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _alertNotice = new();

    private readonly Button _save = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    /// <summary>True while a create or update is awaited; the dialog cannot be dismissed then.</summary>
    private bool _saving;
    private readonly Label _status = new();
    private readonly Label _header = new();
    private PatientDialogFrame? _frame;

    /// <summary>
    /// Whether the status line currently carries a duplicate warning, so clearing it cannot wipe
    /// an unrelated message somebody else put there.
    /// </summary>
    private bool _duplicateWarningShown;

    private IReadOnlyList<PayerOption> _payers = [];

    /// <summary>Pending coverages, held here until the file exists to attach them to.</summary>
    private readonly List<CoverageRequest> _pendingCoverages = [];
    private readonly List<ConsentRequest> _pendingConsents = [];

    private IReadOnlyList<CoverageRow> _coverageRows = [];
    private IReadOnlyList<ConsentRow> _consentRows = [];
    private IReadOnlyList<AlertRow> _alertRows = [];

    /// <summary>Alerts entered before the file exists, held until there is a file to attach them to.</summary>
    private readonly List<(PatientAlertSeverity Severity, MultilingualText Message, DateOnly? ExpiresOn)>
        _pendingAlerts = [];

    /// <summary>The file that was created or edited. Null when the operator backed out.</summary>
    public Guid? ResultPatientId { get; private set; }

    public PatientEditorForm(PatientRegistrationService service, IPatientQueries queries,
                             IUserDialogs dialogs, ILocalizer localizer,
                             ICultureManager cultureManager, Guid? patientId = null)
        : base(localizer, cultureManager)
    {
        _service = service;
        _queries = queries;
        _dialogs = dialogs;
        _patientId = patientId;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(780, 640);
        BackColor = UiTheme.SurfaceRaised;

        BuildLayout();
        WireBehaviour();

        // Title, then the file's number and name (or what to do first) as the muted sentence; the
        // status line takes the footer's start, « Annuler » then « Enregistrer » its end.
        _frame = PatientDialogFrame.Apply(this, string.Empty,
                                          _patientId is null ? T("PatientDialog.EditorNewHint") : string.Empty, _header,
                                          _status, _cancel, _save);
    }

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        _header.Font = UiTheme.SectionTitle;
        _header.ForeColor = UiTheme.Accent;
        _header.AutoSize = false;
        _header.Bounds = new Rectangle(12, 10, 756, 24);

        _tabs.Bounds = new Rectangle(12, 40, 756, 528);
        _tabs.Font = UiTheme.Body;
        _tabs.Padding = new Point(14, 6);

        BuildIdentityTab();
        BuildContactTab();
        BuildClinicalTab();
        BuildCoverageTab();
        BuildConsentTab();
        BuildAlertTab();

        _tabs.TabPages.AddRange([_identityTab, _contactTab, _clinicalTab, _coverageTab,
                                 _consentTab, _alertTab]);

        // The lengths of the patient's columns: a longer name, address or treatment pasted in
        // failed the save with an unexpected error and lost the whole form.
        foreach (var (box, length) in new[]
                 {
                     (_lastName, 120), (_firstName, 120), (_lastNameAr, 120), (_firstNameAr, 120),
                     (_placeOfBirth, 120), (_cin, 20), (_passport, 30), (_nationality, 2),
                     (_guardianName, 200), (_guardianCin, 20), (_guardianRelationship, 60),
                     (_address, 400), (_city, 100), (_postalCode, 10), (_mobile, 20), (_altPhone, 20),
                     (_email, 200), (_treatment, 1000), (_allergies, 500), (_weight, 8), (_height, 8)
                 })
            box.MaxLength = length;

        _save.Bounds = new Rectangle(12, 600, 240, UiTheme.ButtonHeight);
        _cancel.Bounds = new Rectangle(262, 600, 140, UiTheme.ButtonHeight);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(412, 576, 356, 56);

        Controls.AddRange([_header, _tabs, _save, _cancel, _status]);

        AcceptButton = _save;
        CancelButton = _cancel;
    }

    private void BuildIdentityTab()
    {
        _identityTab.BackColor = UiTheme.SurfaceRaised;
        var t = _identityTab;

        Field(t, "Patient.LastName", 16, 20);
        _lastName.Location = new Point(20, 34);

        Field(t, "Patient.FirstName", 16, 260);
        _firstName.Location = new Point(260, 34);

        Field(t, "Patient.Gender", 16, 500);
        _gender.DropDownStyle = ComboBoxStyle.DropDownList;
        _gender.FlatStyle = FlatStyle.Flat;
        _gender.Bounds = new Rectangle(500, 34, 200, UiTheme.InputHeight);
        foreach (var g in Enum.GetValues<Gender>())
            _gender.Items.Add(new EnumChoice((int)g, T($"Gender.{g}")));
        _gender.SelectedIndex = 0;

        // The Arabic name is not a nicety: it is what the CIN says, and it is what an Arabic
        // report and signature block must print.
        Field(t, "Patient.LastNameArabic", 70, 20);
        _lastNameAr.Location = new Point(20, 88);
        _lastNameAr.RightToLeft = RightToLeft.Yes;

        Field(t, "Patient.FirstNameArabic", 70, 260);
        _firstNameAr.Location = new Point(260, 88);
        _firstNameAr.RightToLeft = RightToLeft.Yes;

        _hasDateOfBirth.AutoSize = true;
        _hasDateOfBirth.Checked = true;
        _hasDateOfBirth.Location = new Point(20, 140);
        t.Controls.Add(_hasDateOfBirth);

        _dateOfBirth.Bounds = new Rectangle(20, 162, 200, UiTheme.InputHeight);
        _dateOfBirth.Format = DateTimePickerFormat.Short;
        _dateOfBirth.MinDate = new DateTime(1900, 1, 1);
        _dateOfBirth.MaxDate = DateTime.Today;
        _dateOfBirth.Value = new DateTime(1990, 1, 1);

        // An age with no birth date is normal here, not an edge case: older patients frequently
        // hold no birth record, and the reference ranges still need an age band.
        Field(t, "Patient.DeclaredAge", 140, 260);
        _declaredAge.Bounds = new Rectangle(260, 162, 100, UiTheme.InputHeight);
        _declaredAge.Maximum = 130;
        _declaredAge.Enabled = false;

        _ageEcho.Font = UiTheme.SmallBold;
        _ageEcho.ForeColor = UiTheme.Accent;
        _ageEcho.AutoSize = false;
        _ageEcho.Bounds = new Rectangle(380, 166, 320, 20);
        t.Controls.Add(_ageEcho);

        Field(t, "Patient.PlaceOfBirth", 196, 20);
        _placeOfBirth.Location = new Point(20, 214);

        Field(t, "Patient.Cin", 196, 260);
        _cin.Location = new Point(260, 214);
        _cin.Tag = UiTags.LatinValue;
        _cin.Font = UiTheme.Monospace;

        Field(t, "Patient.Passport", 196, 440);
        _passport.Location = new Point(440, 214);
        _passport.Tag = UiTags.LatinValue;
        _passport.Font = UiTheme.Monospace;

        Field(t, "Patient.Nationality", 196, 620);
        _nationality.Location = new Point(620, 214);
        _nationality.Tag = UiTags.LatinValue;
        _nationality.Text = "MA";

        // The guardian block appears only for a minor, and it is a hard gate: the consent for any
        // analysis on a minor is the guardian's to give (Patient.Validate refuses without it).
        _guardianBox.Bounds = new Rectangle(20, 256, 700, 130);
        _guardianBox.BackColor = UiTheme.AbnormalBackground;
        _guardianBox.Visible = false;

        _guardianNotice.Font = UiTheme.SmallBold;
        _guardianNotice.ForeColor = UiTheme.Abnormal;
        _guardianNotice.AutoSize = false;
        _guardianNotice.Bounds = new Rectangle(12, 8, 676, 20);

        Field(_guardianBox, "Patient.Guardian", 34, 12);
        _guardianName.Location = new Point(12, 52);

        Field(_guardianBox, "Patient.GuardianCin", 34, 252);
        _guardianCin.Location = new Point(252, 52);
        _guardianCin.Tag = UiTags.LatinValue;
        _guardianCin.Font = UiTheme.Monospace;

        Field(_guardianBox, "Patient.GuardianRelationship", 34, 432);
        _guardianRelationship.Location = new Point(432, 52);

        _guardianBox.Controls.AddRange([_guardianNotice, _guardianName, _guardianCin,
                                        _guardianRelationship]);

        t.Controls.AddRange([_lastName, _firstName, _gender, _lastNameAr, _firstNameAr,
                             _dateOfBirth, _declaredAge, _placeOfBirth, _cin, _passport,
                             _nationality, _guardianBox]);
    }

    private void BuildContactTab()
    {
        _contactTab.BackColor = UiTheme.SurfaceRaised;
        var t = _contactTab;

        Field(t, "Patient.Address", 16, 20);
        _address.Location = new Point(20, 34);

        Field(t, "Patient.City", 70, 20);
        _city.Location = new Point(20, 88);

        Field(t, "Patient.Region", 70, 260);
        _region.DropDownStyle = ComboBoxStyle.DropDownList;
        _region.FlatStyle = FlatStyle.Flat;
        _region.Bounds = new Rectangle(260, 88, 300, UiTheme.InputHeight);
        foreach (var r in Enum.GetValues<MoroccanRegion>())
            _region.Items.Add(new EnumChoice((int)r, T($"MoroccanRegion.{r}")));
        _region.SelectedIndex = 0;

        Field(t, "Patient.PostalCode", 70, 580);
        _postalCode.Location = new Point(580, 88);
        _postalCode.Tag = UiTags.LatinValue;

        Field(t, "Patient.MobilePhone", 124, 20);
        _mobile.Location = new Point(20, 142);
        _mobile.Tag = UiTags.LatinValue;
        _mobile.Font = UiTheme.Monospace;

        Field(t, "Patient.AlternatePhone", 124, 220);
        _altPhone.Location = new Point(220, 142);
        _altPhone.Tag = UiTags.LatinValue;
        _altPhone.Font = UiTheme.Monospace;

        Field(t, "Field.Email", 124, 420);
        _email.Location = new Point(420, 142);
        _email.Tag = UiTags.LatinValue;

        // Stated because the number is silently rewritten on save and the clerk should not think
        // the application lost what they typed.
        _phoneNotice.Font = UiTheme.Small;
        _phoneNotice.ForeColor = UiTheme.TextSecondary;
        _phoneNotice.AutoSize = false;
        _phoneNotice.Bounds = new Rectangle(20, 176, 700, 24);
        t.Controls.Add(_phoneNotice);

        Field(t, "Patient.PreferredLanguage", 206, 20);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.FlatStyle = FlatStyle.Flat;
        _language.Bounds = new Rectangle(20, 224, 200, UiTheme.InputHeight);
        foreach (var l in Enum.GetValues<AppLanguage>())
            _language.Items.Add(new EnumChoice((int)l, T($"AppLanguage.{l}")));
        _language.SelectedIndex = 0;

        _optOut.AutoSize = true;
        _optOut.Location = new Point(20, 274);
        t.Controls.Add(_optOut);

        var optOutNote = new Label
        {
            Name = "optOutNote",
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 296, 700, 34)
        };
        t.Controls.Add(optOutNote);

        t.Controls.AddRange([_address, _city, _region, _postalCode, _mobile, _altPhone, _email,
                             _language]);
    }

    private void BuildClinicalTab()
    {
        _clinicalTab.BackColor = UiTheme.SurfaceRaised;
        var t = _clinicalTab;

        _pregnant.AutoSize = true;
        _pregnant.Location = new Point(20, 20);
        t.Controls.Add(_pregnant);

        _hasLmp.AutoSize = true;
        _hasLmp.Location = new Point(20, 50);
        t.Controls.Add(_hasLmp);

        _lmp.Bounds = new Rectangle(260, 48, 200, UiTheme.InputHeight);
        _lmp.Format = DateTimePickerFormat.Short;
        _lmp.MaxDate = DateTime.Today;
        _lmp.Enabled = false;

        var pregnancyNote = new Label
        {
            Name = "pregnancyNote",
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 82, 700, 34)
        };
        t.Controls.Add(pregnancyNote);

        Field(t, "Patient.Weight", 126, 20);
        _weight.Location = new Point(20, 144);
        _weight.Tag = UiTags.LatinValue;
        _weight.TextAlign = HorizontalAlignment.Right;

        Field(t, "Patient.Height", 126, 140);
        _height.Location = new Point(140, 144);
        _height.Tag = UiTags.LatinValue;
        _height.TextAlign = HorizontalAlignment.Right;

        Field(t, "Patient.BloodGroup", 126, 260);
        _bloodGroup.DropDownStyle = ComboBoxStyle.DropDownList;
        _bloodGroup.FlatStyle = FlatStyle.Flat;
        _bloodGroup.Bounds = new Rectangle(260, 144, 140, UiTheme.InputHeight);
        foreach (var b in Enum.GetValues<BloodGroup>())
            _bloodGroup.Items.Add(new EnumChoice((int)b, T($"BloodGroup.{b}")));
        _bloodGroup.SelectedIndex = 0;

        var bsaNote = new Label
        {
            Name = "bsaNote",
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(420, 148, 300, 20)
        };
        t.Controls.Add(bsaNote);

        Field(t, "Patient.CurrentTreatment", 186, 20);
        _treatment.Location = new Point(20, 204);

        Field(t, "Patient.Allergies", 240, 20);
        _allergies.Location = new Point(20, 258);

        var interferenceNote = new Label
        {
            Name = "interferenceNote",
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 292, 700, 34)
        };
        t.Controls.Add(interferenceNote);

        t.Controls.AddRange([_lmp, _weight, _height, _bloodGroup, _treatment, _allergies]);
    }

    private void BuildCoverageTab()
    {
        _coverageTab.BackColor = UiTheme.SurfaceRaised;
        _coverageTab.Padding = new Padding(12);

        _coverages.Columns.Clear();
        void Add(string name, int w, Font? f = null, string align = "")
            => _coverages.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = w, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = f ?? UiTheme.Body,
                    Alignment = align == "r" ? DataGridViewContentAlignment.MiddleRight
                                             : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("payer", 24);
        Add("membership", 16, UiTheme.Monospace);
        Add("relationship", 12, UiTheme.Small);
        Add("insured", 16, UiTheme.Small);
        Add("rate", 8, UiTheme.MonospaceBold, "r");
        Add("ald", 8, UiTheme.SmallBold);
        Add("validity", 16, UiTheme.Monospace);

        _coverages.Dock = DockStyle.Fill;

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 74,
                                  BackColor = UiTheme.SurfaceRaised };

        _addCoverage.Bounds = new Rectangle(0, 8, 220, UiTheme.ButtonHeight);
        _endCoverage.Bounds = new Rectangle(230, 8, 200, UiTheme.ButtonHeight);

        _coverageNotice.Font = UiTheme.Small;
        _coverageNotice.ForeColor = UiTheme.TextSecondary;
        _coverageNotice.AutoSize = false;
        _coverageNotice.Bounds = new Rectangle(0, 44, 720, 28);

        actions.Controls.AddRange([_addCoverage, _endCoverage, _coverageNotice]);

        _coverageTab.Controls.Add(_coverages);
        _coverageTab.Controls.Add(actions);
    }

    private void BuildConsentTab()
    {
        _consentTab.BackColor = UiTheme.SurfaceRaised;
        _consentTab.Padding = new Padding(12);

        _consents.Columns.Clear();
        void Add(string name, int w, Font? f = null)
            => _consents.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = w, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = f ?? UiTheme.Body }
            });

        Add("purpose", 28);
        Add("channel", 14, UiTheme.Small);
        Add("given", 13, UiTheme.Monospace);
        Add("signedby", 20, UiTheme.Small);
        Add("expires", 11, UiTheme.Monospace);
        Add("state", 14, UiTheme.SmallBold);

        _consents.Dock = DockStyle.Fill;

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 74,
                                  BackColor = UiTheme.SurfaceRaised };

        _addConsent.Bounds = new Rectangle(0, 8, 220, UiTheme.ButtonHeight);
        _withdrawConsent.Bounds = new Rectangle(230, 8, 200, UiTheme.ButtonHeight);

        _consentNotice.Font = UiTheme.Small;
        _consentNotice.ForeColor = UiTheme.TextSecondary;
        _consentNotice.AutoSize = false;
        _consentNotice.Bounds = new Rectangle(0, 44, 720, 28);

        actions.Controls.AddRange([_addConsent, _withdrawConsent, _consentNotice]);

        _consentTab.Controls.Add(_consents);
        _consentTab.Controls.Add(actions);
    }

    /// <summary>
    /// The file's alerts: the flags shown to whoever opens it next.
    /// <para>
    /// A patient alert is what carries "difficult venous access", "left arm only — lymphoedema",
    /// "biohazard" and "unpaid balance" from the person who learnt it to the person who needs it. A
    /// critical one blocks registration until it is acknowledged, which is the whole point: a
    /// warning nobody has to read is a warning nobody reads.
    /// </para>
    /// <para>
    /// Alerts are added and allowed to expire, never deleted. "Pregnant, third trimester" stops
    /// applying; it does not stop having been true, and a deleted alert takes with it the reason
    /// somebody drew from the other arm for six months.
    /// </para>
    /// </summary>
    private void BuildAlertTab()
    {
        _alertTab.BackColor = UiTheme.SurfaceRaised;
        _alertTab.Padding = new Padding(12);

        _alerts.Columns.Clear();

        void Add(string name, int w, Font? f = null)
            => _alerts.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = w, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = f ?? UiTheme.Body }
            });

        Add("severity", 16, UiTheme.SmallBold);
        Add("message", 54);
        Add("expires", 15, UiTheme.Monospace);
        Add("state", 15, UiTheme.SmallBold);

        _alerts.Dock = DockStyle.Fill;
        _alerts.CellFormatting += OnAlertFormatting;

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 74,
                                  BackColor = UiTheme.SurfaceRaised };

        _addAlert.Bounds = new Rectangle(0, 8, 220, UiTheme.ButtonHeight);

        _alertNotice.Font = UiTheme.Small;
        _alertNotice.ForeColor = UiTheme.TextSecondary;
        _alertNotice.AutoSize = false;
        _alertNotice.Bounds = new Rectangle(230, 4, 490, 44);

        actions.Controls.AddRange([_addAlert, _alertNotice]);

        _alertTab.Controls.Add(_alerts);
        _alertTab.Controls.Add(actions);
    }

    /// <summary>
    /// Widens a tab's two action buttons to their captions, the second one after the first: at a
    /// fixed 220 and 200, « Recueillir un consentement » and « Retirer le consentement » lost their
    /// last word. The floors cover the captions in the bold button font, which the caption alone
    /// under-measures.
    /// </summary>
    private static void FitPair(Button first, Button second)
    {
        static int Need(Button b) =>
            TextRenderer.MeasureText(b.Text, b.Font).Width + b.Padding.Horizontal + 24
            + (b.Image is { } image ? image.Width + 8 : 0);

        first.Width = Math.Max(250, Need(first));
        second.Left = first.Right + 10;
        second.Width = Math.Max(230, Need(second));
    }

    private void OnAlertFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _alerts.Rows.Count) return;
        if (_alerts.Rows[e.RowIndex].Tag is not AlertRow alert) return;

        if (!alert.IsActive(DateOnly.FromDateTime(DateTime.Today)))
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        e.CellStyle!.ForeColor = alert.Severity switch
        {
            PatientAlertSeverity.Critical => UiTheme.Critical,
            PatientAlertSeverity.Warning => UiTheme.Abnormal,
            _ => UiTheme.TextPrimary
        };

        if (alert.IsBlocking) e.CellStyle.BackColor = UiTheme.CriticalBackground;
    }

    private void Field(Control parent, string key, int top, int left)
    {
        var label = UiTheme.FieldLabel(T(key));
        label.Name = "lbl_" + key;
        label.Location = new Point(left, top);
        parent.Controls.Add(label);
    }

    private void WireBehaviour()
    {
        _hasDateOfBirth.CheckedChanged += (_, _) =>
        {
            _dateOfBirth.Enabled = _hasDateOfBirth.Checked;
            _declaredAge.Enabled = !_hasDateOfBirth.Checked;
            RefreshAgeDependentFields();
        };

        _dateOfBirth.ValueChanged += (_, _) => RefreshAgeDependentFields();
        _declaredAge.ValueChanged += (_, _) => RefreshAgeDependentFields();
        _gender.SelectedIndexChanged += (_, _) => RefreshPregnancyFields();

        _pregnant.CheckedChanged += (_, _) => RefreshPregnancyFields();
        _hasLmp.CheckedChanged += (_, _) => _lmp.Enabled = _hasLmp.Checked;

        _weight.TextChanged += (_, _) => RefreshBodySurface();
        _height.TextChanged += (_, _) => RefreshBodySurface();

        _mobile.Leave += (_, _) => NormalisePhone(_mobile);
        _altPhone.Leave += (_, _) => NormalisePhone(_altPhone);

        // Duplicate detection while the clerk is still looking at the patient. The service has
        // always had FindDuplicatesAsync for exactly this — its own comment says it exists so the
        // screen can warn "rather than after they have pressed save and turned away" — and nothing
        // called it, so the only warning came from the post-save DuplicateWarningForm, by which
        // time the person has left the counter.
        foreach (var field in new Control[] { _lastName, _firstName, _cin, _dateOfBirth })
            field.Leave += async (_, _) => await CheckForDuplicatesAsync();

        _addCoverage.Click += async (_, _) => await AddCoverageAsync();
        _endCoverage.Click += async (_, _) => await EndCoverageAsync();
        _addConsent.Click += async (_, _) => await AddConsentAsync();
        _withdrawConsent.Click += async (_, _) => await WithdrawConsentAsync();

        _addAlert.Click += async (_, _) => await AddAlertAsync();

        _coverages.SelectionChanged += (_, _) => RefreshCoverageActions();
        _consents.SelectionChanged += (_, _) => RefreshConsentActions();

        _save.Click += async (_, _) => await SaveAsync();
        _cancel.DialogResult = DialogResult.Cancel;

        // A file opened by someone who may only read it shows everything and changes nothing.
        PermissionGate.Require(_patientId is null ? Permissions.Patients.Create : Permissions.Patients.Edit, _save);
        PermissionGate.Require(Permissions.Patients.Edit, _addCoverage, _endCoverage, _addConsent,
                               _withdrawConsent, _addAlert);
    }

    protected override void ApplyTexts()
    {
        Text = _patientId is null ? T("Patient.NewFile") : T("Patient.EditFile");
        _frame?.SetTexts(Text, _patientId is null ? T("PatientDialog.EditorNewHint") : null);

        _identityTab.Text = T("Patient.Identity");
        _contactTab.Text = T("Patient.Contact");
        _clinicalTab.Text = T("Patient.ClinicalContext");
        _coverageTab.Text = T("Coverage.Plural");
        _consentTab.Text = T("Consent.Plural");
        _alertTab.Text = T("Patient.Alerts");

        _hasDateOfBirth.Text = T("Patient.DateOfBirth");
        _hasLmp.Text = T("Patient.LastMenstrualPeriod");
        _pregnant.Text = T("Patient.IsPregnant");
        _optOut.Text = T("Patient.OptOutOfSecondaryUse");

        _guardianNotice.Text = T("Patient_MinorRequiresGuardian");
        _phoneNotice.Text = T("Patient.PhoneNormalisedNotice");

        _addCoverage.Text = T("Coverage.Add");
        _endCoverage.Text = T("Coverage.End");
        _addConsent.Text = T("Consent.Record");
        _withdrawConsent.Text = T("Consent.Withdraw");
        FitPair(_addCoverage, _endCoverage);
        FitPair(_addConsent, _withdrawConsent);
        _coverageNotice.Text = T("Coverage.PricingNotice");
        _consentNotice.Text = T("Consent.WrittenEvidenceNotice");

        _save.Text = T("Action.Save");
        _cancel.Text = T("Action.Cancel");

        SetText(_contactTab, "optOutNote", T("Patient.OptOutExplanation"));
        SetText(_clinicalTab, "pregnancyNote", T("Patient.PregnancyExplanation"));
        SetText(_clinicalTab, "interferenceNote", T("Patient.InterferenceExplanation"));

        foreach (var (parent, key) in new (Control, string)[]
                 {
                     (_identityTab, "Patient.LastName"), (_identityTab, "Patient.FirstName"),
                     (_identityTab, "Patient.Gender"), (_identityTab, "Patient.LastNameArabic"),
                     (_identityTab, "Patient.FirstNameArabic"),
                     (_identityTab, "Patient.DeclaredAge"), (_identityTab, "Patient.PlaceOfBirth"),
                     (_identityTab, "Patient.Cin"), (_identityTab, "Patient.Passport"),
                     (_identityTab, "Patient.Nationality"),
                     (_guardianBox, "Patient.Guardian"),
                     (_guardianBox, "Patient.GuardianCin"),
                     (_guardianBox, "Patient.GuardianRelationship"),
                     (_contactTab, "Patient.Address"), (_contactTab, "Patient.City"),
                     (_contactTab, "Patient.Region"), (_contactTab, "Patient.PostalCode"),
                     (_contactTab, "Patient.MobilePhone"),
                     (_contactTab, "Patient.AlternatePhone"), (_contactTab, "Field.Email"),
                     (_contactTab, "Patient.PreferredLanguage"),
                     (_clinicalTab, "Patient.Weight"), (_clinicalTab, "Patient.Height"),
                     (_clinicalTab, "Patient.BloodGroup"),
                     (_clinicalTab, "Patient.CurrentTreatment"),
                     (_clinicalTab, "Patient.Allergies")
                 })
        {
            SetText(parent, "lbl_" + key, T(key));
        }

        if (_coverages.Columns.Count > 0)
        {
            _coverages.Column("payer").HeaderText = T("Coverage.Payer");
            _coverages.Column("membership").HeaderText = T("Coverage.MembershipNumber");
            // Short headers: with the long ones the validity column was behind a sideways scroll.
            _coverages.Column("relationship").HeaderText = T("Coverage.RelationshipShort");
            _coverages.Column("insured").HeaderText = T("Coverage.InsuredPerson");
            _coverages.Column("rate").HeaderText = T("Coverage.RateShort");
            _coverages.Column("ald").HeaderText = T("Coverage.ChronicScheme");
            _coverages.Column("validity").HeaderText = T("Coverage.Validity");
        }

        if (_consents.Columns.Count > 0)
        {
            _consents.Column("purpose").HeaderText = T("Consent.Purpose");
            _consents.Column("channel").HeaderText = T("Consent.Channel");
            _consents.Column("given").HeaderText = T("Consent.GivenAt");
            _consents.Column("signedby").HeaderText = T("Consent.SignedBy");
            _consents.Column("expires").HeaderText = T("Field.Date");
            _consents.Column("state").HeaderText = T("Field.Status");
        }

        RefreshAgeDependentFields();
        RefreshPregnancyFields();
        RefreshBodySurface();
        RenderCoverages();
        RenderConsents();
    }

    private static void SetText(Control parent, string name, string text)
    {
        var found = parent.Controls.Find(name, searchAllChildren: true);
        if (found.Length > 0) found[0].Text = text;
    }

    // ---- Loading ------------------------------------------------------------------------------------

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            var payers = await _queries.GetPayersAsync();
            _payers = payers;

            if (_patientId is { } id) await LoadExistingAsync(id);
        }
        catch (Exception ex)
        {
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
            _dialogs.Error(ex.Message, T("Message.UnexpectedError"));
        }
    }

    private async Task LoadExistingAsync(Guid id)
    {
        var detail = await _queries.GetDetailAsync(id);
        if (detail is null) return;

        _header.Text = $"{detail.MedicalRecordNumber} — {detail.LastName} {detail.FirstName}";

        _lastName.Text = detail.LastName;
        _firstName.Text = detail.FirstName;
        _lastNameAr.Text = detail.LastNameArabic ?? string.Empty;
        _firstNameAr.Text = detail.FirstNameArabic ?? string.Empty;
        Select(_gender, (int)detail.Gender);

        if (detail.DateOfBirth is { } dob)
        {
            _hasDateOfBirth.Checked = true;
            _dateOfBirth.Value = dob.ToDateTime(TimeOnly.MinValue);
        }
        else
        {
            _hasDateOfBirth.Checked = false;
            _declaredAge.Value = Math.Clamp(detail.DeclaredAgeYears ?? 0, 0, 130);
        }

        _placeOfBirth.Text = detail.PlaceOfBirth ?? string.Empty;
        _cin.Text = detail.Cin ?? string.Empty;
        _passport.Text = detail.PassportNumber ?? string.Empty;
        _nationality.Text = detail.NationalityCode;
        _guardianName.Text = detail.GuardianFullName ?? string.Empty;
        _guardianCin.Text = detail.GuardianCin ?? string.Empty;
        _guardianRelationship.Text = detail.GuardianRelationship ?? string.Empty;

        _address.Text = detail.AddressLine ?? string.Empty;
        _city.Text = detail.City ?? string.Empty;
        Select(_region, (int)detail.Region);
        _postalCode.Text = detail.PostalCode ?? string.Empty;
        _mobile.Text = detail.MobilePhone ?? string.Empty;
        _altPhone.Text = detail.AlternatePhone ?? string.Empty;
        _email.Text = detail.Email ?? string.Empty;
        Select(_language, (int)detail.PreferredLanguage);
        _optOut.Checked = detail.OptedOutOfSecondaryUse;

        _pregnant.Checked = detail.IsPregnant;
        if (detail.LastMenstrualPeriod is { } lmp)
        {
            _hasLmp.Checked = true;
            _lmp.Value = lmp.ToDateTime(TimeOnly.MinValue);
        }

        _weight.Text = detail.WeightKg?.ToString("0.##") ?? string.Empty;
        _height.Text = detail.HeightCm?.ToString("0.##") ?? string.Empty;
        Select(_bloodGroup, (int)detail.BloodGroup);
        _treatment.Text = detail.CurrentTreatment ?? string.Empty;
        _allergies.Text = detail.KnownAllergies ?? string.Empty;

        // An anonymised file is a record of an erasure, not an editable patient.
        if (detail.IsAnonymized)
        {
            _save.Enabled = false;
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = T("Patient_AnonymizedIsReadOnly");
            foreach (Control c in _identityTab.Controls) c.Enabled = false;
        }

        await ReloadCoveragesAsync();
        await ReloadConsentsAsync();
        await ReloadAlertsAsync();
    }

    private static void Select(ComboBox combo, int value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is EnumChoice c && c.Value == value) { combo.SelectedIndex = i; return; }
        }
    }

    // ---- Live field behaviour -----------------------------------------------------------------------

    /// <summary>
    /// Echoes the age the system computed and reveals the guardian block for a minor. Shown live
    /// because the age drives the reference ranges and the consent authority, and a clerk who
    /// mistypes a year should see it immediately rather than at save.
    /// </summary>
    private void RefreshAgeDependentFields()
    {
        var age = CurrentAgeYears();

        _ageEcho.Text = age is { } years
            ? $"{years} {T("Report.Years")}"
            : T("Patient_AgeOrBirthDateRequired");

        _ageEcho.ForeColor = age is null ? UiTheme.Abnormal : UiTheme.Accent;

        var minor = age is { } a && a < 18;
        _guardianBox.Visible = minor;
    }

    private int? CurrentAgeYears()
    {
        if (!_hasDateOfBirth.Checked)
            return _declaredAge.Value > 0 ? (int)_declaredAge.Value : null;

        var dob = DateOnly.FromDateTime(_dateOfBirth.Value.Date);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var age = today.Year - dob.Year;
        if (today < dob.AddYears(age)) age--;
        return age < 0 ? null : age;
    }

    /// <summary>
    /// Warns, before the file is saved, that one may already exist for this person.
    /// <para>
    /// Two files for one patient split a clinical history in half, and merging them afterwards is
    /// the one correction in this application that cannot be made cleanly — which is why the
    /// warning belongs here, while the person is still at the counter and can be asked, rather than
    /// only in the dialog that appears after Save.
    /// </para>
    /// <para>
    /// Suppressed when editing: a saved patient matches itself on name, CIN and date of birth, so
    /// every edit would raise a warning about the very record being edited.
    /// </para>
    /// </summary>
    private async Task CheckForDuplicatesAsync()
    {
        if (_patientId is not null) return;

        if (string.IsNullOrWhiteSpace(_lastName.Text) || string.IsNullOrWhiteSpace(_firstName.Text))
            return;

        var result = await _service.FindDuplicatesAsync(
            _lastName.Text.Trim(),
            _firstName.Text.Trim(),
            string.IsNullOrWhiteSpace(_cin.Text) ? null : _cin.Text.Trim(),
            _hasDateOfBirth.Checked ? DateOnly.FromDateTime(_dateOfBirth.Value.Date) : null);

        if (result.IsFailure)
        {
            // Silent. A clerk who lacks Patients.View cannot be shown other patients' files, and
            // refusing the keystroke would make the form unusable for them; the service still
            // refuses the registration itself.
            return;
        }

        if (result.Value.Count == 0)
        {
            if (_duplicateWarningShown)
            {
                _status.Text = string.Empty;
                _duplicateWarningShown = false;
            }

            return;
        }

        _duplicateWarningShown = true;
        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = "⚠ " + T("Patient.PossibleDuplicateNotice",
                                 result.Value.Count,
                                 string.Join(", ", result.Value.Take(3)
                                                        .Select(d => d.MedicalRecordNumber)));
    }

    private void RefreshPregnancyFields()
    {
        var female = (_gender.SelectedItem as EnumChoice)?.Value == (int)Gender.Female;

        _pregnant.Enabled = female;
        if (!female) _pregnant.Checked = false;

        _hasLmp.Visible = _pregnant.Checked || female;
        _lmp.Visible = _hasLmp.Visible;
        _lmp.Enabled = _hasLmp.Visible && _hasLmp.Checked;
    }

    private void RefreshBodySurface()
    {
        var note = _clinicalTab.Controls.Find("bsaNote", true);
        if (note.Length == 0) return;

        if (TryParseDecimal(_weight.Text, out var w) && TryParseDecimal(_height.Text, out var h)
            && w > 0 && h > 0)
        {
            var bsa = Math.Round((decimal)Math.Sqrt((double)(w * h) / 3600d), 3);
            note[0].Text = $"{T("Patient.BodySurface")} {bsa:0.000} m²";
        }
        else
        {
            note[0].Text = string.Empty;
        }
    }

    /// <summary>
    /// Rewrites the number to E.164 as the field is left. Done visibly rather than silently at
    /// save, so the clerk can see that +212 replaced the leading zero and confirm it is right.
    /// </summary>
    private static void NormalisePhone(TextBox box)
    {
        var normalised = MoroccanIdentifiers.TryNormalizePhone(box.Text);
        if (!string.IsNullOrWhiteSpace(normalised)) box.Text = normalised;
    }

    private static bool TryParseDecimal(string text, out decimal value)
    {
        var parsed = EditorValues.ParseDecimal(text);
        value = parsed ?? 0m;
        return parsed is not null;
    }

    // ---- Coverage ----------------------------------------------------------------------------------

    private async Task ReloadCoveragesAsync()
    {
        if (_patientId is not { } id) return;
        _coverageRows = await _queries.GetCoveragesAsync(id);
        RenderCoverages();
    }

    private void RenderCoverages()
    {
        if (_coverages.Columns.Count == 0) return;

        _coverages.Rows.Clear();

        // Existing rows for a saved file; pending rows for one still being created.
        foreach (var row in _coverageRows)
        {
            var index = _coverages.Rows.Add(
                row.PayerName,
                row.MembershipNumber,
                T($"BeneficiaryRelationship.{row.Relationship}"),
                row.InsuredPersonName ?? string.Empty,
                $"{row.EffectiveRate * 100m:0.#} %",
                row.IsChronicDiseaseScheme ? T("Coverage.ChronicScheme") : string.Empty,
                row.ValidUntil is { } until
                    ? $"{row.ValidFrom:dd/MM/yy} – {until:dd/MM/yy}"
                    : $"{row.ValidFrom:dd/MM/yy} – …");

            _coverages.Rows[index].Tag = row;

            if (!row.IsValidToday)
                _coverages.Rows[index].DefaultCellStyle.ForeColor = UiTheme.TextMuted;
            else if (row.IsPrimary)
                _coverages.Rows[index].DefaultCellStyle.Font = UiTheme.BodyBold;
        }

        foreach (var pending in _pendingCoverages)
        {
            var payer = _payers.FirstOrDefault(p => p.Id == pending.PayerId);

            var index = _coverages.Rows.Add(
                payer?.Name ?? "—",
                pending.MembershipNumber,
                T($"BeneficiaryRelationship.{pending.Relationship}"),
                pending.InsuredPersonName ?? string.Empty,
                pending.IsChronicDiseaseScheme
                    ? "100 %"
                    : $"{(pending.ReimbursementRateOverride ?? payer?.DefaultReimbursementRate ?? 0m) * 100m:0.#} %",
                pending.IsChronicDiseaseScheme ? T("Coverage.ChronicScheme") : string.Empty,
                T("Coverage.PendingSave"));

            _coverages.Rows[index].DefaultCellStyle.ForeColor = UiTheme.Accent;
            _coverages.Rows[index].DefaultCellStyle.Font = UiTheme.BodyBold;
        }

        RefreshCoverageActions();
    }

    private void RefreshCoverageActions()
    {
        _endCoverage.Enabled = _coverages.CurrentRow?.Tag is CoverageRow { ValidUntil: null };
        _addCoverage.Enabled = _save.Enabled;
    }

    private async Task AddCoverageAsync()
    {
        if (_payers.Count == 0)
        {
            _dialogs.Warn(T("Coverage.NoPayersConfigured"), T("Coverage.Add"));
            return;
        }

        using var dialog = new CoverageDialog(Localizer, _dialogs, _payers);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        if (_patientId is { } id)
        {
            var result = await _service.AddCoverageAsync(id, dialog.Request);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
            await ReloadCoveragesAsync();
        }
        else
        {
            // Held until the file exists: the coverage needs a patient id to hang from, and the
            // patient is not created until the operator saves.
            _pendingCoverages.Add(dialog.Request);
            RenderCoverages();
        }
    }

    private async Task EndCoverageAsync()
    {
        if (_patientId is not { } id) return;
        if (_coverages.CurrentRow?.Tag is not CoverageRow row) return;

        var answer = _dialogs.PromptForText(
            T("Coverage.EndDatePrompt", row.PayerName),
            T("Coverage.End"), DateTime.Today.ToString("dd/MM/yyyy"));

        if (string.IsNullOrWhiteSpace(answer)) return;

        if (!DateTime.TryParse(answer, out var parsed))
        {
            // Said as a date: the message read « … n'est pas une valeur numérique valide ».
            _dialogs.Warn(Localizer.Format("Coverage_EndDateUnreadable", answer.Trim()));
            return;
        }

        var result = await _service.EndCoverageAsync(id, row.CoverageId,
                                                      DateOnly.FromDateTime(parsed.Date));

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        await ReloadCoveragesAsync();
    }

    // ---- Consent -----------------------------------------------------------------------------------

    private async Task ReloadConsentsAsync()
    {
        if (_patientId is not { } id) return;
        _consentRows = await _queries.GetConsentsAsync(id);
        RenderConsents();
    }

    private void RenderConsents()
    {
        if (_consents.Columns.Count == 0) return;

        _consents.Rows.Clear();

        foreach (var row in _consentRows)
        {
            var state = row.WithdrawnAt is not null
                ? T("Consent.Withdrawn")
                : row.RequiresWrittenEvidence && !row.HasEvidenceDocument
                    ? T("Consent.EvidenceMissing")
                    : T("Consent.Active");

            var index = _consents.Rows.Add(
                T($"ConsentPurpose.{row.Purpose}"),
                T($"ConsentChannel.{row.Channel}"),
                row.GivenAt.ToLocalTime().ToString("dd/MM/yy HH:mm"),
                row.SignedByName ?? string.Empty,
                row.ExpiresOn?.ToString("dd/MM/yy") ?? string.Empty,
                state);

            _consents.Rows[index].Tag = row;

            if (row.WithdrawnAt is not null)
                _consents.Rows[index].DefaultCellStyle.ForeColor = UiTheme.TextMuted;
            else if (row.RequiresWrittenEvidence && !row.HasEvidenceDocument)
            {
                // A purpose the law requires in writing, with no document attached, is the defect
                // an assessor looks for.
                _consents.Rows[index].DefaultCellStyle.BackColor = UiTheme.AbnormalBackground;
                _consents.Rows[index].DefaultCellStyle.ForeColor = UiTheme.Abnormal;
            }
        }

        foreach (var pending in _pendingConsents)
        {
            var index = _consents.Rows.Add(
                T($"ConsentPurpose.{pending.Purpose}"),
                T($"ConsentChannel.{pending.Channel}"),
                T("Coverage.PendingSave"), pending.SignedByName ?? string.Empty,
                pending.ExpiresOn?.ToString("dd/MM/yy") ?? string.Empty,
                T("Coverage.PendingSave"));

            _consents.Rows[index].DefaultCellStyle.ForeColor = UiTheme.Accent;
            _consents.Rows[index].DefaultCellStyle.Font = UiTheme.BodyBold;
        }

        RefreshConsentActions();
    }

    private void RefreshConsentActions()
    {
        _withdrawConsent.Enabled = _consents.CurrentRow?.Tag is ConsentRow { WithdrawnAt: null };
        _addConsent.Enabled = _save.Enabled;
    }

    private async Task AddConsentAsync()
    {
        using var dialog = new ConsentDialog(Localizer, _dialogs,
                                              CurrentAgeYears() is { } a && a < 18,
                                              _guardianName.Text);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        if (_patientId is { } id)
        {
            var result = await _service.RecordConsentAsync(id, dialog.Request);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
            await ReloadConsentsAsync();
        }
        else
        {
            _pendingConsents.Add(dialog.Request);
            RenderConsents();
        }
    }

    private async Task WithdrawConsentAsync()
    {
        if (_patientId is not { } id) return;
        if (_consents.CurrentRow?.Tag is not ConsentRow row) return;

        var reason = _dialogs.PromptForReason(
            T("Consent.WithdrawPrompt", T($"ConsentPurpose.{row.Purpose}")),
            T("Consent.Withdraw"), maxLength: 500);

        if (reason is null) return;

        var result = await _service.WithdrawConsentAsync(id, row.ConsentId, reason);
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        await ReloadConsentsAsync();
    }

    // ---- Alerts -------------------------------------------------------------------------------------

    private async Task ReloadAlertsAsync()
    {
        if (_patientId is not { } id) return;

        _alertRows = await _queries.GetAlertsAsync(id);
        RenderAlerts();
    }

    private void RenderAlerts()
    {
        if (_alerts.Columns.Count == 0) return;

        _alerts.Rows.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var language = Localizer.CurrentLanguage;

        foreach (var row in _alertRows)
        {
            var index = _alerts.Rows.Add(
                T($"PatientAlertSeverity.{row.Severity}"),
                row.Message.For(language),
                row.ExpiresOn?.ToString("dd/MM/yy") ?? T("Patient.AlertNoExpiry"),
                row.IsActive(today) ? T("Patient.AlertActive") : T("Patient.AlertExpired"));

            _alerts.Rows[index].Tag = row;
        }

        foreach (var pending in _pendingAlerts)
        {
            var index = _alerts.Rows.Add(
                T($"PatientAlertSeverity.{pending.Severity}"),
                pending.Message.For(language),
                pending.ExpiresOn?.ToString("dd/MM/yy") ?? T("Patient.AlertNoExpiry"),
                T("Coverage.PendingSave"));

            _alerts.Rows[index].DefaultCellStyle.ForeColor = UiTheme.Accent;
            _alerts.Rows[index].DefaultCellStyle.Font = UiTheme.BodyBold;
        }

        _addAlert.Enabled = _save.Enabled;
    }

    private async Task AddAlertAsync()
    {
        using var dialog = new PatientAlertDialog(Localizer, _dialogs);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (_patientId is { } id)
        {
            var result = await _service.AddAlertAsync(id, dialog.Severity, dialog.Message,
                                                       dialog.ExpiresOn);

            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            await ReloadAlertsAsync();
        }
        else
        {
            // Held until the file exists, like the coverages and consents: an alert cannot be
            // attached to a patient who has not been created yet.
            _pendingAlerts.Add((dialog.Severity, dialog.Message, dialog.ExpiresOn));
            RenderAlerts();
        }
    }

    // ---- Save ---------------------------------------------------------------------------------------

    private PatientIdentityRequest BuildIdentity(string? duplicateOverrideReason = null) => new()
    {
        LastName = _lastName.Text.Trim(),
        FirstName = _firstName.Text.Trim(),
        LastNameArabic = _lastNameAr.Text,
        FirstNameArabic = _firstNameAr.Text,
        Gender = (Gender)((_gender.SelectedItem as EnumChoice)?.Value ?? 0),
        DateOfBirth = _hasDateOfBirth.Checked
            ? DateOnly.FromDateTime(_dateOfBirth.Value.Date) : null,
        DeclaredAgeYears = !_hasDateOfBirth.Checked && _declaredAge.Value > 0
            ? (int)_declaredAge.Value : null,
        PlaceOfBirth = _placeOfBirth.Text,
        Cin = _cin.Text,
        PassportNumber = _passport.Text,
        NationalityCode = _nationality.Text,
        GuardianFullName = _guardianName.Text,
        GuardianCin = _guardianCin.Text,
        GuardianRelationship = _guardianRelationship.Text,
        AddressLine = _address.Text,
        City = _city.Text,
        Region = (MoroccanRegion)((_region.SelectedItem as EnumChoice)?.Value ?? 0),
        PostalCode = _postalCode.Text,
        MobilePhone = _mobile.Text,
        AlternatePhone = _altPhone.Text,
        Email = _email.Text,
        PreferredLanguage = (AppLanguage)((_language.SelectedItem as EnumChoice)?.Value ?? 0),
        IsPregnant = _pregnant.Checked,
        LastMenstrualPeriod = _hasLmp.Checked && _hasLmp.Visible
            ? DateOnly.FromDateTime(_lmp.Value.Date) : null,
        WeightKg = TryParseDecimal(_weight.Text, out var w) && w > 0 ? w : null,
        HeightCm = TryParseDecimal(_height.Text, out var h) && h > 0 ? h : null,
        BloodGroup = (BloodGroup)((_bloodGroup.SelectedItem as EnumChoice)?.Value ?? 0),
        CurrentTreatment = _treatment.Text,
        KnownAllergies = _allergies.Text,
        OptedOutOfSecondaryUse = _optOut.Checked,
        DuplicateOverrideReason = duplicateOverrideReason
    };

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastName.Text))
        {
            _tabs.SelectedTab = _identityTab;
            _dialogs.Warn(T("Patient_LastNameRequired"));
            _lastName.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_firstName.Text))
        {
            _tabs.SelectedTab = _identityTab;
            _dialogs.Warn(T("Patient_FirstNameRequired"));
            _firstName.Focus();
            return;
        }

        // A weight or height the form cannot read (« 72 kg », « 1m70 ») or below zero was saved
        // as no value without a word, and the calculations that use it (clearance, body surface)
        // then had none.
        foreach (var box in new[] { _weight, _height })
        {
            if (!EditorValues.IsUnreadable(box.Text) && !(TryParseDecimal(box.Text, out var measure) && measure < 0m))
                continue;

            _tabs.SelectedTab = _clinicalTab;
            _dialogs.Warn(EditorValues.IsUnreadable(box.Text)
                ? Localizer.Format("Result_NotANumber", box.Text.Trim())
                : Localizer.Format("Patient_BodyMeasureOutOfRange", PatientRegistrationService.MaxWeightKg,
                                   PatientRegistrationService.MaxHeightCm));
            box.Focus();
            box.SelectAll();
            return;
        }

        _save.Enabled = false;
        _cancel.Enabled = false;
        _saving = true;

        try
        {
            if (_patientId is { } id)
            {
                var updated = await _service.UpdateAsync(id, BuildIdentity());

                if (updated.IsFailure) { _dialogs.ShowFailure(updated); return; }

                ResultPatientId = id;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            await CreateAsync(null);
        }
        finally
        {
            _saving = false;
            if (!IsDisposed && DialogResult != DialogResult.OK)
            {
                _save.Enabled = true;
                _cancel.Enabled = true;
            }
        }
    }

    /// <summary>
    /// Refuses to close while a save is in flight. Esc or the close box used to dismiss the dialog
    /// while the registration still completed behind it: the caller got "cancelled", the patient
    /// was created anyway, and the clerk — seeing no file — registered the same person again.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_saving && DialogResult != DialogResult.OK && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>
    /// Creates the file, handling the duplicate question the service may come back with.
    /// <para>
    /// The service returns candidates rather than an error, because the operator did nothing
    /// wrong — the system is asking whether this is the same person. Only two answers are offered:
    /// use the existing file, or state why it is somebody else.
    /// </para>
    /// </summary>
    private async Task CreateAsync(string? overrideReason)
    {
        var result = await _service.RegisterAsync(BuildIdentity(overrideReason),
                                                   _pendingCoverages, _pendingConsents);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        var outcome = result.Value;

        if (outcome.NeedsDuplicateDecision)
        {
            using var dialog = new DuplicateWarningForm(Localizer, _dialogs, outcome.Duplicates);

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            if (dialog.SelectedExistingPatientId is { } existing)
            {
                // The clerk recognised the patient. Nothing was written; the caller receives the
                // file that already existed, which is the whole point of the check.
                ResultPatientId = existing;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (!string.IsNullOrWhiteSpace(dialog.OverrideReason))
            {
                await CreateAsync(dialog.OverrideReason);
            }

            return;
        }

        // Alerts are attached after the file exists, not passed into registration: the registration
        // request takes the identity, the coverages and the consents, which are what the service's
        // own rules need. An alert is a later annotation on an existing file — and attaching one
        // must not be able to fail the registration it was added alongside.
        if (outcome.PatientId is { } created && _pendingAlerts.Count > 0)
        {
            foreach (var (severity, message, expiresOn) in _pendingAlerts)
            {
                var attached = await _service.AddAlertAsync(created, severity, message, expiresOn);

                if (attached.IsFailure)
                {
                    // The file was created; only the flag was not. Said plainly rather than
                    // swallowed, because the operator will otherwise assume the alert is on record.
                    _dialogs.Warn(Localizer.Describe(attached.Errors), T("Patient.Alerts"));
                    break;
                }
            }
        }

        ResultPatientId = outcome.PatientId;
        DialogResult = DialogResult.OK;
        Close();
    }
}
