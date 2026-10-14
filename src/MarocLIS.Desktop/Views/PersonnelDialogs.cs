using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Opening a personnel file for editing, or its documents, from whichever screen the operator is
/// on: the personnel list, the habilitation matrix and the deadline list all lead to the same file.
/// </summary>
internal static class PersonnelFile
{
    /// <summary>Returns true when something was saved.</summary>
    internal static async Task<bool> EditAsync(IWin32Window owner, PersonnelService personnel, IUserDialogs dialogs,
                                               ILocalizer localizer, Guid? employeeId)
    {
        EmployeeDetail? detail = null;

        if (employeeId is { } id)
        {
            var loaded = await personnel.GetEmployeeAsync(id);
            if (loaded.IsFailure) { dialogs.ShowFailure(loaded); return false; }
            detail = loaded.Value;
        }

        var lookups = await personnel.GetLookupsAsync(employeeId);
        if (lookups.IsFailure) { dialogs.ShowFailure(lookups); return false; }

        var canSeeSalary = detail?.CanSeeSalary ?? PermissionGate.Allows(Permissions.HumanResources.Salary);

        using var dialog = new EmployeeEditorDialog(localizer, dialogs, detail, lookups.Value, canSeeSalary);

        while (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            var saved = await personnel.SaveEmployeeAsync(dialog.Request);
            if (saved.IsSuccess) return true;

            // The form stays open with what was typed: a refused CIN or RIB is corrected in place.
            dialogs.ShowFailure(saved, dialog.Text);
        }

        return false;
    }

    internal static async Task<bool> DocumentsAsync(IWin32Window owner, PersonnelService personnel,
                                                    IUserDialogs dialogs, ILocalizer localizer, Guid employeeId)
    {
        using var dialog = new CredentialsDialog(localizer, dialogs, personnel, employeeId);
        await dialog.LoadAsync();
        dialog.ShowDialog(owner);
        return dialog.Changed;
    }
}

/// <summary>
/// The personnel file: identity, post, contract and, for whoever holds the right, pay.
/// </summary>
internal sealed class EmployeeEditorDialog : MasterDataEditor
{
    private const int FormWidth = 770;

    private readonly EmployeeDetail? _detail;
    private readonly bool _canSeeSalary;

    private readonly TextBox _lastName, _firstName, _arabicName, _cin, _cnss, _phone, _email, _city,
                             _address, _jobTitle;
    private readonly ComboBox _gender, _family, _job, _department, _user, _contract;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _birth, _clause, _contractEnd, _trialEnd;
    private readonly NumericUpDown _children, _weeklyHours;
    private readonly CheckBox _exposed;
    private readonly DateTimePicker _hireDate = new();
    private readonly TextBox? _salary, _rib, _bank;

    internal EmployeeRequest Request { get; private set; } = null!;

    internal EmployeeEditorDialog(ILocalizer localizer, IUserDialogs dialogs, EmployeeDetail? detail,
                                  PersonnelLookups lookups, bool canSeeSalary)
        : base(localizer, dialogs, detail is null ? "Personnel.New" : "Personnel.Edit",
               detail is null ? null : $"{detail.Number} {detail.Request.LastName} {detail.Request.FirstName}",
               FormWidth, canSeeSalary ? 640 : 590)
    {
        _detail = detail;
        _canSeeSalary = canSeeSalary;
        var r = detail?.Request;

        int c1 = 20, c2 = 270, c3 = 520, w = 230;
        var y = 10;

        Section("Personnel.Identity", y); y += 22;
        _lastName = TextField("Personnel.LastName", c1, y, w, r?.LastName);
        _firstName = TextField("Personnel.FirstName", c2, y, w, r?.FirstName);
        _arabicName = TextField("Personnel.ArabicName", c3, y, w, r?.FullNameArabic, arabic: true);

        y += 52;
        _gender = Choice("Field.Gender", c1, y, w, r?.Gender ?? Gender.Unknown, "Gender");
        _birth = OptionalDate("Personnel.BirthDate", c2, y - 2, r?.BirthDate, max: DateTime.Today);
        _cin = TextField("Personnel.Cin", c3, y, w, r?.Cin, latin: true, mono: true);

        y += 52;
        _cnss = TextField("Personnel.Cnss", c1, y, w, r?.CnssNumber, latin: true, mono: true);
        _family = Choice("Personnel.FamilyStatus", c2, y, w, r?.FamilyStatus ?? FamilyStatus.Unmarried, "FamilyStatus");
        _children = Number("Personnel.Children", c3, y, 80, r?.DependentChildren ?? 0, max: 20);

        y += 52;
        _phone = TextField("Field.Phone", c1, y, w, r?.Phone, latin: true);
        _email = TextField("Field.Email", c2, y, w, r?.Email, latin: true);
        _city = TextField("Personnel.City", c3, y, w, r?.City);

        y += 52;
        _address = TextField("Personnel.Address", c1, y, FormWidth - 40, r?.Address);

        y += 58;
        Section("Personnel.Position", y); y += 22;
        _job = Choice("Personnel.Job", c1, y, w, r?.Job ?? EmployeeJob.Technician, "EmployeeJob");
        _jobTitle = TextField("Personnel.JobTitle", c2, y, w, r?.JobTitle);
        _department = Lookup("Field.Department", c3, y, w,
                             lookups.Departments.Select(d => (d.Id, d.Label)), r?.DepartmentId);

        y += 52;
        _user = Lookup("Personnel.LinkedUser", c1, y, w,
                       lookups.Users.Select(u => (u.UserId, $"{u.FullName} ({u.UserName})")), r?.UserId);
        _exposed = Check("Personnel.Exposed", c2, y + 20, r?.IsExposedToBiologicalRisk
                                                          ?? Employee.IsExposedByDefault(EmployeeJob.Technician));
        _clause = OptionalDate("Personnel.ConfidentialityClause", c3, y - 2, r?.ConfidentialityClauseSignedOn,
                               max: DateTime.Today);

        // A new file follows the job: picking "agent d'entretien" ticks the exposure box. An existing
        // file keeps what HR decided.
        if (detail is null)
            _job.SelectedIndexChanged += (_, _) =>
                _exposed.Checked = Employee.IsExposedByDefault(Selected<EmployeeJob>(_job));

        y += 58;
        Section("Personnel.Contract", y); y += 22;
        _contract = Choice("Personnel.ContractType", c1, y, w, r?.ContractType ?? ContractType.Permanent, "ContractType");

        Label("Personnel.HireDate", c2, y);
        _hireDate.Bounds = new Rectangle(c2, y + 18, 180, UiTheme.InputHeight);
        _hireDate.Format = DateTimePickerFormat.Short;
        _hireDate.Value = (r?.HireDate is { } hire && hire != default ? hire : DateOnly.FromDateTime(DateTime.Today))
                          .ToDateTime(TimeOnly.MinValue);
        Controls.Add(_hireDate);

        _contractEnd = OptionalDate("Personnel.ContractEnd", c3, y - 2, r?.ContractEndDate);

        y += 52;
        _trialEnd = OptionalDate("Personnel.TrialEnd", c1, y - 2, r?.TrialPeriodEndDate);
        _weeklyHours = Number("Personnel.WeeklyHours", c2, y, 80, r?.WeeklyHours ?? 44m, max: 60m, decimals: 2);

        var endNotice = Notice(c3, y + 4, w, 34, "Personnel.ContractEndNotice", UiTheme.Abnormal);
        void RefreshEndNotice() => endNotice.Visible = Employee.RequiresEndDate(Selected<ContractType>(_contract));
        _contract.SelectedIndexChanged += (_, _) =>
        {
            RefreshEndNotice();
            if (Employee.RequiresEndDate(Selected<ContractType>(_contract))) _contractEnd.Toggle.Checked = true;
        };
        RefreshEndNotice();

        y += 58;

        if (canSeeSalary)
        {
            Section("Personnel.Pay", y); y += 22;
            _salary = TextField("Personnel.BaseSalary", c1, y, w,
                                r?.BaseSalary?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                                latin: true, right: true);
            _rib = TextField("Personnel.Rib", c2, y, w, r?.Rib, latin: true, mono: true);
            _bank = TextField("Personnel.Bank", c3, y, w, r?.BankName);
            y += 58;
        }
        else
        {
            Notice(c1, y, FormWidth - 40, 20, "Personnel.SalaryHidden");
            y += 28;
        }

        Buttons(y, FormWidth);

        // Sized to what was laid out: the pay section is there or not depending on the right.
        ClientSize = new Size(FormWidth, y + UiTheme.ButtonHeight + 16);
    }

    private void Section(string key, int y)
    {
        var label = UiTheme.FieldLabel(Localizer[key]);
        label.Font = UiTheme.BodyBold;
        label.ForeColor = UiTheme.TextPrimary;
        label.Location = new Point(20, y);
        Controls.Add(label);
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_lastName.Text) || string.IsNullOrWhiteSpace(_firstName.Text))
        {
            Dialogs.Warn(Localizer["Employee_NameRequired"], Text);
            _lastName.Focus();
            return;
        }

        decimal? salary = null;
        if (_salary is not null && !string.IsNullOrWhiteSpace(_salary.Text))
        {
            salary = DecimalOrNull(_salary);
            if (salary is null)
            {
                Dialogs.Warn(Localizer["Employee_SalaryInvalid"], Text);
                _salary.Focus();
                return;
            }
        }

        if (_rib is not null && !string.IsNullOrWhiteSpace(_rib.Text) && !Employee.IsValidRib(_rib.Text))
        {
            Dialogs.Warn(Localizer["Employee_RibInvalid"], Text);
            _rib.Focus();
            return;
        }

        Request = new EmployeeRequest
        {
            EmployeeId = _detail?.Request.EmployeeId,
            LastName = _lastName.Text.Trim(),
            FirstName = _firstName.Text.Trim(),
            FullNameArabic = Blank(_arabicName),
            Gender = Selected<Gender>(_gender),
            BirthDate = DateOrNull(_birth.Toggle, _birth.Picker),
            Cin = Blank(_cin),
            CnssNumber = Blank(_cnss),
            FamilyStatus = Selected<FamilyStatus>(_family),
            DependentChildren = (int)_children.Value,
            Address = Blank(_address),
            City = Blank(_city),
            Phone = Blank(_phone),
            Email = Blank(_email),
            Job = Selected<EmployeeJob>(_job),
            JobTitle = Blank(_jobTitle),
            DepartmentId = SelectedId(_department),
            UserId = SelectedId(_user),
            IsExposedToBiologicalRisk = _exposed.Checked,
            ConfidentialityClauseSignedOn = DateOrNull(_clause.Toggle, _clause.Picker),
            ContractType = Selected<ContractType>(_contract),
            HireDate = DateOnly.FromDateTime(_hireDate.Value.Date),
            ContractEndDate = DateOrNull(_contractEnd.Toggle, _contractEnd.Picker),
            TrialPeriodEndDate = DateOrNull(_trialEnd.Toggle, _trialEnd.Picker),
            WeeklyHours = _weeklyHours.Value,
            BaseSalary = _canSeeSalary ? salary : null,
            Rib = _rib is null ? null : Blank(_rib),
            BankName = _bank is null ? null : Blank(_bank)
        };

        DialogResult = DialogResult.OK;
    }
}

/// <summary>The documents of one file, with their dates: what HR shows an inspector or an assessor.</summary>
internal sealed class CredentialsDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly PersonnelService _personnel;
    private readonly Guid _employeeId;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _add = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _remove = UiTheme.SecondaryButton(string.Empty);

    internal bool Changed { get; private set; }

    internal CredentialsDialog(ILocalizer localizer, IUserDialogs dialogs, PersonnelService personnel, Guid employeeId)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _personnel = personnel;
        _employeeId = employeeId;

        Text = localizer["Personnel.Documents"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 460);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        void Add(string name, string key, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = localizer[key], FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("kind", "Credential.Kind", 16, UiTheme.Small);
        Add("title", "Credential.Title", 24);
        Add("reference", "Credential.Reference", 12, UiTheme.Monospace);
        Add("issuer", "Credential.IssuedBy", 14, UiTheme.Small);
        Add("obtained", "Credential.ObtainedOn", 10, UiTheme.Monospace);
        Add("expires", "Credential.ExpiresOn", 10, UiTheme.Monospace);
        Add("note", "Field.Notes", 14, UiTheme.Small);

        var host = new Panel { Bounds = new Rectangle(12, 12, 876, 380) };
        _grid.Dock = DockStyle.Fill;
        host.Controls.Add(_grid);

        _add.Text = localizer["Credential.Add"];
        _add.Bounds = new Rectangle(12, 408, 200, UiTheme.ButtonHeight);
        _add.Click += async (_, _) => await AddAsync();

        _remove.Text = localizer["Credential.Remove"];
        _remove.Bounds = new Rectangle(222, 408, 200, UiTheme.ButtonHeight);
        _remove.Click += async (_, _) => await RemoveAsync();

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Bounds = new Rectangle(738, 408, 150, UiTheme.ButtonHeight);
        close.DialogResult = DialogResult.Cancel;

        Controls.AddRange([host, _add, _remove, close]);
        CancelButton = close;

        PermissionGate.Require(Permissions.HumanResources.Manage, _add, _remove);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not CredentialRow row) return;
            if (row.ExpiresOn is { } until && until < DateOnly.FromDateTime(DateTime.Today)
                || row.Fitness == MedicalFitness.Unfit)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };
    }

    internal async Task LoadAsync()
    {
        var result = await _personnel.GetEmployeeAsync(_employeeId);
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        var detail = result.Value;
        Text = $"{_localizer["Personnel.Documents"]} — {detail.Number} {detail.Request.LastName} {detail.Request.FirstName}";

        _grid.Rows.Clear();
        foreach (var c in detail.Credentials)
        {
            var note = c.Fitness is { } fitness
                ? $"{_localizer[$"MedicalFitness.{fitness}"]}{(c.Note is null ? string.Empty : " · " + c.Note)}"
                : c.Note ?? string.Empty;

            var index = _grid.Rows.Add(_localizer[$"CredentialKind.{c.Kind}"], c.Title, c.Reference ?? string.Empty,
                                       c.IssuedBy ?? string.Empty, c.ObtainedOn?.ToString("dd/MM/yyyy") ?? "—",
                                       c.ExpiresOn?.ToString("dd/MM/yyyy") ?? "—", note);
            _grid.Rows[index].Tag = c;
        }
    }

    private async Task AddAsync()
    {
        using var editor = new CredentialEditorDialog(_localizer, _dialogs);

        while (editor.ShowDialog(this) == DialogResult.OK)
        {
            var added = await _personnel.AddCredentialAsync(_employeeId, editor.Request);
            if (added.IsSuccess)
            {
                Changed = true;
                await LoadAsync();
                return;
            }

            _dialogs.ShowFailure(added, editor.Text);
        }
    }

    private async Task RemoveAsync()
    {
        if (_grid.CurrentRow?.Tag is not CredentialRow row) return;

        var reason = _dialogs.PromptForReason(_localizer.Format("Credential.RemovePrompt", row.Title), _remove.Text);
        if (reason is null) return;

        var removed = await _personnel.RemoveCredentialAsync(_employeeId, row.CredentialId, reason);
        if (removed.IsFailure) { _dialogs.ShowFailure(removed); return; }

        Changed = true;
        await LoadAsync();
    }
}

/// <summary>One document: a diploma, a vaccination, a fitness decision.</summary>
internal sealed class CredentialEditorDialog : MasterDataEditor
{
    private readonly ComboBox _kind, _fitness;
    private readonly TextBox _title, _reference, _issuer, _note;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _obtained, _expires;
    private string _suggested = string.Empty;

    internal CredentialRequest Request { get; private set; } = null!;

    internal CredentialEditorDialog(ILocalizer localizer, IUserDialogs dialogs)
        : base(localizer, dialogs, "Credential.Add", null, 520, 360)
    {
        _kind = Choice("Credential.Kind", 20, 10, 230, CredentialKind.Diploma, "CredentialKind");
        _fitness = Choice("Credential.Fitness", 270, 10, 230, MedicalFitness.Fit, "MedicalFitness");
        _title = TextField("Credential.Title", 20, 62, 480);
        _reference = TextField("Credential.Reference", 20, 114, 230, latin: true, mono: true);
        _issuer = TextField("Credential.IssuedBy", 270, 114, 230);
        _obtained = OptionalDate("Credential.ObtainedOn", 20, 164, DateOnly.FromDateTime(DateTime.Today),
                                 max: DateTime.Today);
        _expires = OptionalDate("Credential.ExpiresOn", 270, 164, null);
        _note = TextField("Field.Notes", 20, 216, 480);
        Notice(20, 266, 480, 34, "Credential.NoDiagnosisNotice");

        _kind.SelectedIndexChanged += (_, _) => OnKindChanged();
        OnKindChanged();

        Buttons(310, 520);
    }

    /// <summary>
    /// Proposes a title for the kind, without overwriting one the operator typed, and offers the
    /// fitness decision only for a medical visit.
    /// </summary>
    private void OnKindChanged()
    {
        var kind = Selected<CredentialKind>(_kind);
        _fitness.Enabled = kind == CredentialKind.MedicalVisit;

        var suggestion = kind is CredentialKind.Other or CredentialKind.Diploma or CredentialKind.ContinuingEducation
            ? string.Empty
            : Localizer[$"CredentialKind.{kind}"];

        if (_title.Text.Length == 0 || _title.Text == _suggested) _title.Text = suggestion;
        _suggested = suggestion;
    }

    protected override void Accept()
    {
        if (string.IsNullOrWhiteSpace(_title.Text))
        {
            Dialogs.Warn(Localizer["Credential_TitleRequired"], Text);
            _title.Focus();
            return;
        }

        var kind = Selected<CredentialKind>(_kind);

        Request = new CredentialRequest
        {
            Kind = kind,
            Title = _title.Text.Trim(),
            Reference = Blank(_reference),
            IssuedBy = Blank(_issuer),
            ObtainedOn = DateOrNull(_obtained.Toggle, _obtained.Picker),
            ExpiresOn = DateOrNull(_expires.Toggle, _expires.Picker),
            Fitness = kind == CredentialKind.MedicalVisit ? Selected<MedicalFitness>(_fitness) : null,
            Note = Blank(_note)
        };

        DialogResult = DialogResult.OK;
    }
}

/// <summary>Recording a departure. Habilitations end with it.</summary>
internal sealed class EmployeeExitDialog : MasterDataEditor
{
    private readonly ComboBox _reason;
    private readonly DateTimePicker _date = new();
    private readonly TextBox _comment;

    internal DateOnly ExitDate => DateOnly.FromDateTime(_date.Value.Date);
    internal ExitReason Reason => Selected<ExitReason>(_reason);
    internal string? Comment => Blank(_comment);

    internal EmployeeExitDialog(ILocalizer localizer, IUserDialogs dialogs, string employee)
        : base(localizer, dialogs, "Personnel.RecordExit", employee, 520, 250)
    {
        Label("Personnel.ExitDate", 20, 10);
        _date.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        Controls.Add(_date);

        _reason = Choice("Personnel.ExitReason", 270, 10, 230, ExitReason.Resignation, "ExitReason");
        _comment = TextField("Field.Comment", 20, 64, 480);
        Notice(20, 116, 480, 40, "Personnel.ExitNotice", UiTheme.Abnormal);

        Buttons(200, 520);
    }

    protected override void Accept() => DialogResult = DialogResult.OK;
}

/// <summary>
/// A habilitation decision: what, on which analyser or discipline, at what level, reviewed when.
/// </summary>
internal sealed class HabilitationGrantDialog : MasterDataEditor
{
    private readonly PersonnelLookups _lookups;
    private readonly ComboBox _activity, _level;
    private readonly ComboBox _subject = new();
    private readonly DateTimePicker _granted = new();
    private readonly (CheckBox Toggle, DateTimePicker Picker) _review;
    private readonly TextBox _evidence;

    internal HabilitationActivity Activity => Selected<HabilitationActivity>(_activity);
    internal Guid? SubjectId => _subject.Enabled ? SelectedId(_subject) : null;
    internal HabilitationLevel Level => Selected<HabilitationLevel>(_level);
    internal DateOnly GrantedOn => DateOnly.FromDateTime(_granted.Value.Date);
    internal DateOnly? ReviewDueOn => DateOrNull(_review.Toggle, _review.Picker);
    internal string? Evidence => Blank(_evidence);

    internal HabilitationGrantDialog(ILocalizer localizer, IUserDialogs dialogs, string employee,
                                     PersonnelLookups lookups, HabilitationActivity activity, Guid? subjectId)
        : base(localizer, dialogs, "Habilitation.Grant", employee, 560, 350)
    {
        _lookups = lookups;

        _activity = Choice("Habilitation.Activity", 20, 10, 250, activity, "HabilitationActivity");

        Label("Habilitation.Subject", 290, 10);
        _subject.DropDownStyle = ComboBoxStyle.DropDownList;
        _subject.FlatStyle = FlatStyle.Flat;
        _subject.Bounds = new Rectangle(290, 28, 250, UiTheme.InputHeight);
        Controls.Add(_subject);

        _level = Choice("Habilitation.Level", 20, 62, 250, HabilitationLevel.Authorized, "HabilitationLevel");

        Label("Habilitation.GrantedOn", 290, 62);
        _granted.Bounds = new Rectangle(290, 80, 180, UiTheme.InputHeight);
        _granted.Format = DateTimePickerFormat.Short;
        _granted.MaxDate = DateTime.Today;
        _granted.Value = DateTime.Today;
        Controls.Add(_granted);

        _review = OptionalDate("Habilitation.ReviewDue", 20, 112,
                               DateOnly.FromDateTime(DateTime.Today.AddYears(1)), min: DateTime.Today);

        _evidence = TextField("Habilitation.Evidence", 20, 166, 520);
        Notice(20, 216, 520, 70, "Habilitation.SignatureNotice");

        _activity.SelectedIndexChanged += (_, _) => FillSubjects(null);
        FillSubjects(subjectId);

        Buttons(296, 560);
    }

    private void FillSubjects(Guid? current)
    {
        var activity = Activity;
        _subject.Items.Clear();
        _subject.Enabled = EmployeeHabilitation.RequiresSubject(activity);
        if (!_subject.Enabled) return;

        var options = EmployeeHabilitation.SubjectIsInstrument(activity) ? _lookups.Instruments : _lookups.Departments;
        foreach (var option in options)
        {
            var index = _subject.Items.Add(new LookupChoice(option.Id, option.Label));
            if (option.Id == current) _subject.SelectedIndex = index;
        }

        if (_subject.SelectedIndex < 0 && _subject.Items.Count > 0) _subject.SelectedIndex = 0;
    }

    protected override void Accept()
    {
        if (_subject.Enabled && SelectedId(_subject) is null)
        {
            Dialogs.Warn(Localizer["Habilitation_SubjectRequired"], Text);
            return;
        }

        DialogResult = DialogResult.OK;
    }
}
