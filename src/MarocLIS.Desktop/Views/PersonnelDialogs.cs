using System.Globalization;
using MarocLIS.Application.Abstractions;
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
                                                    IDocumentOutput output, IUserDialogs dialogs,
                                                    ILocalizer localizer, Guid employeeId)
    {
        using var dialog = new CredentialsDialog(localizer, dialogs, personnel, output, employeeId);
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
        QhDialog.Dress(this, localizer, "QualityHr.Hint.Employee");
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

/// <summary>
/// The documents of one file, with their dates and their scans: what HR shows an inspector or an
/// assessor.
/// </summary>
internal sealed class CredentialsDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly PersonnelService _personnel;
    private readonly IDocumentOutput _output;
    private readonly Guid _employeeId;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _add = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _attach = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _open = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _remove = UiTheme.SecondaryButton(string.Empty);
    private int _pending;

    internal bool Changed { get; private set; }

    internal CredentialsDialog(ILocalizer localizer, IUserDialogs dialogs, PersonnelService personnel,
                               IDocumentOutput output, Guid employeeId)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _personnel = personnel;
        _output = output;
        _employeeId = employeeId;

        Text = localizer["Personnel.Documents"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(1040, 480);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        void Add(string name, string key, int weight, int minimum, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = localizer[key], FillWeight = weight, MinimumWidth = minimum, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        // The dates and the number keep the room they need: "01/07/2015" cut to "01/07/..." is no date.
        Add("kind", "Credential.Kind", 14, 120, UiTheme.Small);
        Add("title", "Credential.Title", 22, 150);
        Add("reference", "Credential.Reference", 13, 120, UiTheme.Monospace);
        Add("issuer", "Credential.IssuedBy", 12, 90, UiTheme.Small);
        Add("obtained", "Credential.ObtainedOn", 10, 100, UiTheme.Monospace);
        Add("expires", "Credential.ExpiresOn", 10, 120, UiTheme.Monospace);
        Add("file", "Credential.Scan", 7, 70, UiTheme.Small);
        Add("note", "Field.Notes", 12, 90, UiTheme.Small);

        var host = new Panel { Bounds = new Rectangle(12, 12, 1016, 400) };
        _grid.Dock = DockStyle.Fill;
        host.Controls.Add(_grid);

        _add.Text = localizer["Credential.Add"];
        _edit.Text = localizer["Credential.Edit"];
        _attach.Text = localizer["Credential.Attach"];
        _open.Text = localizer["Credential.OpenScan"];
        _remove.Text = localizer["Credential.Remove"];

        // Sized to their captions, so a longer translation pushes the next button along instead of
        // being cut.
        var actions = new FlowLayoutPanel
        {
            Location = new Point(12, 426), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false
        };
        foreach (var button in new[] { _add, _edit, _attach, _open, _remove })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowOnly;
            button.MinimumSize = new Size(130, UiTheme.ButtonHeight);
            button.Size = button.MinimumSize;
            button.Margin = new Padding(0, 0, 10, 0);
            actions.Controls.Add(button);
        }

        _add.Click += async (_, _) => await Guarded(AddAsync);
        _edit.Click += async (_, _) => await Guarded(EditAsync);
        _attach.Click += async (_, _) => await Guarded(AttachAsync);
        _open.Click += async (_, _) => await Guarded(OpenAsync);
        _remove.Click += async (_, _) => await Guarded(RemoveAsync);

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Bounds = new Rectangle(888, 426, 140, UiTheme.ButtonHeight);
        close.DialogResult = DialogResult.Cancel;

        Controls.AddRange([host, actions, close]);
        CancelButton = close;

        PermissionGate.Require(Permissions.HumanResources.Manage, _add, _edit, _attach, _remove);

        _grid.SelectionChanged += (_, _) => UpdateButtons();

        // A double-click corrects the document for whoever may, and otherwise shows its scan.
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (!PermissionGate.IsBlocked(_edit)) await Guarded(EditAsync);
            else if (Current()?.FileName is not null) await Guarded(OpenAsync);
        };

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

        UpdateButtons();
        QhDialog.Dress(this, localizer, "QualityHr.Hint.Credentials");
    }

    private CredentialRow? Current() => _grid.CurrentRow?.Tag as CredentialRow;

    private void UpdateButtons()
    {
        var row = Current();
        _edit.Enabled = _attach.Enabled = _remove.Enabled = row is not null && _pending == 0;
        _open.Enabled = row?.FileName is not null && _pending == 0;
        _add.Enabled = _pending == 0;
    }

    /// <summary>One operation at a time: a second click while a scan uploads does nothing.</summary>
    private async Task Guarded(Func<Task> action)
    {
        if (_pending > 0) return;
        _pending++;
        UpdateButtons();
        try { await action(); }
        finally
        {
            _pending--;
            UpdateButtons();
        }
    }

    internal async Task LoadAsync()
    {
        var keep = Current()?.CredentialId;

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
                                       c.ExpiresOn?.ToString("dd/MM/yyyy") ?? "—",
                                       c.FileName is null ? "—" : _localizer["Credential.HasScan"], note);
            _grid.Rows[index].Tag = c;
            if (c.FileName is not null) _grid.Rows[index].Cells["file"].ToolTipText = c.FileName;
            if (c.CredentialId == keep) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        UpdateButtons();
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

    private async Task EditAsync()
    {
        if (Current() is not { } row) return;

        using var editor = new CredentialEditorDialog(_localizer, _dialogs, row);

        while (editor.ShowDialog(this) == DialogResult.OK)
        {
            var updated = await _personnel.UpdateCredentialAsync(_employeeId, row.CredentialId, editor.Request);
            if (updated.IsSuccess)
            {
                Changed = true;
                await LoadAsync();
                return;
            }

            _dialogs.ShowFailure(updated, editor.Text);
        }
    }

    private async Task AttachAsync()
    {
        if (Current() is not { } row) return;

        if (row.FileName is not null
            && !_dialogs.Confirm(_localizer.Format("Credential.ReplaceScanPrompt", row.FileName), _attach.Text))
            return;

        using var picker = new OpenFileDialog
        {
            Title = _attach.Text,
            Filter = $"{_localizer["Credential.ScanFiles"]} (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > CredentialFile.MaximumBytes)
            {
                _dialogs.Warn(_localizer.Format("Credential_FileTooLarge", CredentialFile.MaximumBytes / (1024 * 1024)),
                              _attach.Text);
                return;
            }
            content = await File.ReadAllBytesAsync(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn($"{_localizer["Documents.FileUnreadable"]}\n{ex.Message}", _attach.Text);
            return;
        }

        var attached = await _personnel.AttachCredentialFileAsync(_employeeId, row.CredentialId,
                                                                  Path.GetFileName(picker.FileName), content);
        if (attached.IsFailure) { _dialogs.ShowFailure(attached, _attach.Text); return; }

        Changed = true;
        await LoadAsync();
    }

    private async Task OpenAsync()
    {
        if (Current() is not { FileName: not null } row) return;

        var file = await _personnel.GetCredentialFileAsync(_employeeId, row.CredentialId);
        if (file.IsFailure) { _dialogs.ShowFailure(file, _open.Text); return; }

        var opened = await _output.ViewAsync(file.Value.Content, Path.GetExtension(file.Value.FileName));
        if (opened.IsFailure) _dialogs.ShowFailure(opened, _open.Text);
    }

    private async Task RemoveAsync()
    {
        if (Current() is not { } row) return;

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

    internal CredentialEditorDialog(ILocalizer localizer, IUserDialogs dialogs, CredentialRow? existing = null)
        : base(localizer, dialogs, existing is null ? "Credential.Add" : "Credential.Edit", existing?.Title, 520, 360)
    {
        _kind = Choice("Credential.Kind", 20, 10, 230, existing?.Kind ?? CredentialKind.Diploma, "CredentialKind");
        _fitness = Choice("Credential.Fitness", 270, 10, 230, existing?.Fitness ?? MedicalFitness.Fit, "MedicalFitness");
        _title = TextField("Credential.Title", 20, 62, 480);
        _reference = TextField("Credential.Reference", 20, 114, 230, latin: true, mono: true);
        _issuer = TextField("Credential.IssuedBy", 270, 114, 230);
        _obtained = OptionalDate("Credential.ObtainedOn", 20, 164, DateOnly.FromDateTime(DateTime.Today),
                                 max: DateTime.Today);
        _expires = OptionalDate("Credential.ExpiresOn", 270, 164, null);
        _note = TextField("Field.Notes", 20, 216, 480);
        Notice(20, 266, 480, 34, "Credential.NoDiagnosisNotice");

        if (existing is not null)
        {
            _title.Text = existing.Title;
            _reference.Text = existing.Reference ?? string.Empty;
            _issuer.Text = existing.IssuedBy ?? string.Empty;
            SetDate(_obtained, existing.ObtainedOn);
            SetDate(_expires, existing.ExpiresOn);
            _note.Text = existing.Note ?? string.Empty;
            _suggested = existing.Title;
        }

        _kind.SelectedIndexChanged += (_, _) => OnKindChanged();
        OnKindChanged();

        Buttons(310, 520);
        QhDialog.Dress(this, localizer, null);
    }

    private static void SetDate((CheckBox Toggle, DateTimePicker Picker) field, DateOnly? value)
    {
        field.Toggle.Checked = value is not null;
        if (value is not { } date) return;

        var wanted = date.ToDateTime(TimeOnly.MinValue);
        field.Picker.Value = wanted < field.Picker.MinDate ? field.Picker.MinDate
                           : wanted > field.Picker.MaxDate ? field.Picker.MaxDate
                           : wanted;
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

/// <summary>
/// Recording a departure, or correcting one not reached yet (with <paramref name="current"/>).
/// Habilitations end with it.
/// </summary>
internal sealed class EmployeeExitDialog : MasterDataEditor
{
    private readonly ComboBox _reason;
    private readonly DateTimePicker _date = new();
    private readonly TextBox _comment;

    internal DateOnly ExitDate => DateOnly.FromDateTime(_date.Value.Date);
    internal ExitReason Reason => Selected<ExitReason>(_reason);
    internal string? Comment => Blank(_comment);

    internal EmployeeExitDialog(ILocalizer localizer, IUserDialogs dialogs, string employee,
                                (DateOnly Date, ExitReason Reason, string? Comment)? current = null)
        : base(localizer, dialogs, current is null ? "Personnel.RecordExit" : "Personnel.CorrectExit", employee, 520, 250)
    {
        Label("Personnel.ExitDate", 20, 10);
        _date.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        if (current is { } date) _date.Value = date.Date.ToDateTime(TimeOnly.MinValue);
        Controls.Add(_date);

        _reason = Choice("Personnel.ExitReason", 270, 10, 230, current?.Reason ?? ExitReason.Resignation, "ExitReason");
        _comment = TextField("Field.Comment", 20, 64, 480, current?.Comment);
        Notice(20, 116, 480, 40, "Personnel.ExitNoticeAfterLastDay", UiTheme.Abnormal);

        Buttons(200, 520);
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept() => DialogResult = DialogResult.OK;
}

/// <summary>The date a re-hire starts: the day after the departure at the earliest.</summary>
internal sealed class EmployeeRehireDialog : MasterDataEditor
{
    private readonly DateTimePicker _date = new();

    internal DateOnly RehireDate => DateOnly.FromDateTime(_date.Value.Date);

    internal EmployeeRehireDialog(ILocalizer localizer, IUserDialogs dialogs, string employee, DateOnly exit)
        : base(localizer, dialogs, "Personnel.Rehire", employee, 520, 210)
    {
        Label("Personnel.RehireDate", 20, 10);
        _date.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        var earliest = exit.AddDays(1).ToDateTime(TimeOnly.MinValue);
        _date.MinDate = earliest;
        _date.Value = DateTime.Today > earliest ? DateTime.Today : earliest;
        Controls.Add(_date);

        Notice(20, 70, 480, 70, "Personnel.RehireNotice", UiTheme.Abnormal);
        Buttons(160, 520);
        QhDialog.Dress(this, localizer, null);
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
        QhDialog.Dress(this, localizer, null);
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

/// <summary>
/// The final settlement of an employee who left: the law's figures proposed, every amount
/// adjustable, the total carried onto the payslip of the month of departure.
/// </summary>
internal sealed class SettlementDialog : MasterDataEditor
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private readonly SettlementDetail _detail;
    private readonly TextBox _reference, _leaveDays, _leave, _notice, _severance, _note;
    private readonly CheckBox _noticeNotWorked;
    private readonly Label _total;
    private bool _computing;

    internal SettlementAmounts Amounts { get; private set; } = null!;

    internal SettlementDialog(ILocalizer localizer, IUserDialogs dialogs, SettlementDetail d)
        : base(localizer, dialogs, "Personnel.Settlement", $"{d.Number} {d.FullName}", 720, 470)
    {
        _detail = d;
        var a = d.Saved ?? d.Suggested;
        static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        Controls.Add(new Label
        {
            Font = UiTheme.Body, ForeColor = UiTheme.TextPrimary, AutoSize = false, Bounds = new Rectangle(20, 10, 680, 40),
            Text = localizer.Format("Settlement.Summary", d.HireDate.ToString("dd/MM/yyyy", Fr), d.ExitDate.ToString("dd/MM/yyyy", Fr),
                                    localizer[$"ExitReason.{d.ExitReason}"], d.ServiceYears.ToString("0.##", Fr),
                                    localizer[$"StaffCategory.{d.Category}"])
        });

        _reference = TextField("Settlement.ReferenceSalary", 20, 56, 200, N(a.ReferenceSalary), mono: true, right: true);
        _leaveDays = TextField("Settlement.LeaveDays", 250, 56, 180, N(a.LeaveDays), mono: true, right: true);
        _leave = TextField("Settlement.LeaveIndemnity", 460, 56, 240, N(a.LeaveIndemnity), mono: true, right: true);
        Hint(20, 100, 420, d.ReferenceMonths > 0
            ? localizer.Format("Settlement.ReferenceFromMonths", d.ReferenceMonths)
            : localizer["Settlement.ReferenceFromBase"]);

        _noticeNotWorked = Check("Settlement.NoticeNotWorked", 20, 132, a.NoticeNotWorked);
        _notice = TextField("Settlement.NoticeIndemnity", 460, 124, 240, N(a.NoticeIndemnity), mono: true, right: true);
        var period = d.Notice.Months > 0
            ? localizer.Format("Settlement.NoticeMonths", d.Notice.Months)
            : localizer.Format("Settlement.NoticeDays", d.Notice.Days);
        Hint(20, 156, 420, d.NoticeApplies
            ? localizer.Format("Settlement.NoticeLegal", period)
            : localizer["Settlement.NoticeNotDue"]);

        _severance = TextField("Settlement.Severance", 460, 192, 240, N(a.SeverancePay), mono: true, right: true);
        Hint(20, 210, 420, d.SeveranceDue
            ? localizer.Format("Settlement.SeveranceDue", d.SeveranceHours.ToString("0.##", Fr))
            : localizer["Settlement.SeveranceNotDue"]);

        _note = TextField("Field.Notes", 20, 252, 680, a.Note);

        _total = new Label { Font = UiTheme.BodyBold, ForeColor = UiTheme.TextPrimary, AutoSize = false, Bounds = new Rectangle(20, 306, 680, 22) };
        Controls.Add(_total);

        if (d.SignedOn is { } signed)
        {
            Notice(20, 336, 680, 60, "Settlement.Signed", UiTheme.Abnormal).Text =
                localizer.Format("Settlement.Signed", signed.ToString("dd/MM/yyyy", Fr));
            foreach (var box in new Control[] { _reference, _leaveDays, _leave, _notice, _severance, _note, _noticeNotWorked })
                box.Enabled = false;
        }
        else
            Notice(20, 336, 680, 60, d.Payroll == SettlementPayroll.Closed ? "Settlement.NoticePayrollClosed" : "Settlement.NoticePayroll",
                   d.Payroll == SettlementPayroll.Closed ? UiTheme.Abnormal : null);

        Buttons(410, 720);

        _notice.Enabled = _noticeNotWorked.Checked && d.SignedOn is null;
        _reference.TextChanged += (_, _) => Recompute(leave: true, notice: true, severance: true);
        _leaveDays.TextChanged += (_, _) => Recompute(leave: true);
        _noticeNotWorked.CheckedChanged += (_, _) =>
        {
            _notice.Enabled = _noticeNotWorked.Checked;
            Recompute(notice: true);
        };
        foreach (var box in new[] { _leave, _notice, _severance }) box.TextChanged += (_, _) => ShowTotal();
        ShowTotal();
        QhDialog.Dress(this, localizer, null);
    }

    private void Hint(int x, int y, int width, string text) => Controls.Add(new Label
    {
        Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false, AutoEllipsis = true,
        Bounds = new Rectangle(x, y, width, 20), Text = text
    });

    private static bool Read(TextBox box, out decimal value)
    {
        value = 0m;
        var text = box.Text.Trim().Replace(',', '.').Replace(" ", string.Empty);
        return text.Length == 0 || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static void Put(TextBox box, decimal value) => box.Text = value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Puts the law's figure back in each amount whose basis just changed.</summary>
    private void Recompute(bool leave = false, bool notice = false, bool severance = false)
    {
        if (_computing || !Read(_reference, out var reference)) return;
        _computing = true;
        try
        {
            var d = _detail;
            if (leave && Read(_leaveDays, out var days))
                Put(_leave, SettlementCalculator.LeaveIndemnity(days, reference, d.MonthlyWorkingDays));
            if (notice)
                Put(_notice, _noticeNotWorked.Checked && d.NoticeApplies
                    ? SettlementCalculator.NoticeIndemnity(d.Notice, reference, d.MonthlyWorkingDays)
                    : 0m);
            if (severance && d.SeveranceDue)
                Put(_severance, SettlementCalculator.Severance(d.ServiceYears, reference, d.MonthlyHours));
        }
        finally
        {
            _computing = false;
        }

        ShowTotal();
    }

    private void ShowTotal()
    {
        Read(_leave, out var leave);
        Read(_notice, out var notice);
        Read(_severance, out var severance);
        _total.Text = Localizer.Format("Settlement.Total", (leave + notice + severance).ToString("N2", Fr));
    }

    protected override void Accept()
    {
        if (_detail.SignedOn is not null)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }

        TextBox[] boxes = [_reference, _leaveDays, _leave, _notice, _severance];
        var values = new decimal[boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
            if (!Read(boxes[i], out values[i]) || values[i] < 0)
            {
                Dialogs.Warn(Localizer["Settlement_NegativeAmount"], Text);
                boxes[i].Focus();
                return;
            }

        Amounts = new SettlementAmounts(values[0], values[1], values[2], _noticeNotWorked.Checked,
                                        _noticeNotWorked.Checked ? values[3] : 0m, values[4], Blank(_note));
        DialogResult = DialogResult.OK;
    }
}

/// <summary>The date the employee signed the receipt for the final settlement.</summary>
internal sealed class SettlementSignedDialog : MasterDataEditor
{
    private readonly DateTimePicker _date = new();

    internal DateOnly SignedOn => DateOnly.FromDateTime(_date.Value.Date);

    internal SettlementSignedDialog(ILocalizer localizer, IUserDialogs dialogs, string employee, DateOnly exit)
        : base(localizer, dialogs, "Settlement.SignedTitle", employee, 520, 210)
    {
        Label("Settlement.SignedOn", 20, 10);
        _date.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        _date.MaxDate = DateTime.Today;
        _date.MinDate = exit.ToDateTime(TimeOnly.MinValue) > DateTime.Today ? DateTime.Today : exit.ToDateTime(TimeOnly.MinValue);
        _date.Value = DateTime.Today;
        Controls.Add(_date);

        Notice(20, 70, 480, 70, "Settlement.SignedNotice", UiTheme.Abnormal);
        Buttons(160, 520);
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept() => DialogResult = DialogResult.OK;
}
