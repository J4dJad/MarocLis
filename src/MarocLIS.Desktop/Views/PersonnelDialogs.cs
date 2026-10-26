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
        // The lengths of the columns: a longer value was refused by the database with an unexpected
        // error, and the form was lost with it.
        _lastName.MaxLength = _firstName.MaxLength = EmployeeFileRules.NameMaxLength;
        _arabicName.MaxLength = EmployeeFileRules.ArabicNameMaxLength;

        y += 52;
        _gender = Choice("Field.Gender", c1, y, w, r?.Gender ?? Gender.Unknown, "Gender");
        _birth = OptionalDate("Personnel.BirthDate", c2, y - 2, r?.BirthDate, max: DateTime.Today);
        _cin = TextField("Personnel.Cin", c3, y, w, r?.Cin, latin: true, mono: true);
        _cin.MaxLength = EmployeeFileRules.CinMaxLength;

        y += 52;
        _cnss = TextField("Personnel.Cnss", c1, y, w, r?.CnssNumber, latin: true, mono: true);
        _cnss.MaxLength = EmployeeFileRules.CnssMaxLength;
        _family = Choice("Personnel.FamilyStatus", c2, y, w, r?.FamilyStatus ?? FamilyStatus.Unmarried, "FamilyStatus");
        _children = Number("Personnel.Children", c3, y, 120, r?.DependentChildren ?? 0, max: EmployeeFileRules.MaxChildren);

        y += 52;
        _phone = TextField("Field.Phone", c1, y, w, r?.Phone, latin: true);
        _email = TextField("Field.Email", c2, y, w, r?.Email, latin: true);
        _city = TextField("Personnel.City", c3, y, w, r?.City);
        _phone.MaxLength = EmployeeFileRules.PhoneMaxLength;
        _email.MaxLength = EmployeeFileRules.EmailMaxLength;
        _city.MaxLength = EmployeeFileRules.CityMaxLength;

        y += 52;
        _address = TextField("Personnel.Address", c1, y, FormWidth - 40, r?.Address);
        _address.MaxLength = EmployeeFileRules.AddressMaxLength;

        y += 58;
        Section("Personnel.Position", y); y += 22;
        _job = Choice("Personnel.Job", c1, y, w, r?.Job ?? EmployeeJob.Technician, "EmployeeJob");
        _jobTitle = TextField("Personnel.JobTitle", c2, y, w, r?.JobTitle);
        _jobTitle.MaxLength = EmployeeFileRules.JobTitleMaxLength;
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
        // file keeps what HR decided (EmployeeFileRules.JobChanged).
        _job.SelectedIndexChanged += (_, _) =>
        {
            var form = new EmployeeForm { Job = Selected<EmployeeJob>(_job), Exposed = _exposed.Checked };
            EmployeeFileRules.JobChanged(form, isNew: detail is null);
            _exposed.Checked = form.Exposed;
        };

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
        _weeklyHours = Number("Personnel.WeeklyHours", c2, y, 120, r?.WeeklyHours ?? EmployeeFileRules.DefaultWeeklyHours,
                              max: EmployeeFileRules.MaxWeeklyHours, decimals: 2);

        var endNotice = Notice(c3, y + 4, w, 34, "Personnel.ContractEndNotice", UiTheme.Abnormal);
        void RefreshEndNotice() => endNotice.Visible = EmployeeFileRules.ShowsEndNotice(Selected<ContractType>(_contract));
        _contract.SelectedIndexChanged += (_, _) =>
        {
            RefreshEndNotice();
            // A contract that must end on a date gets one (EmployeeFileRules.ContractChanged).
            var form = new EmployeeForm
            {
                ContractType = Selected<ContractType>(_contract),
                ContractEndDate = DateOrNull(_contractEnd.Toggle, _contractEnd.Picker)
            };
            EmployeeFileRules.ContractChanged(form, DateOnly.FromDateTime(_contractEnd.Picker.Value.Date));
            if (form.ContractEndDate is not null) _contractEnd.Toggle.Checked = true;
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
            Numeric(EmployeeFileRules.SalaryMaxLength, _salary);
            _rib.MaxLength = EmployeeFileRules.RibMaxLength;
            _bank.MaxLength = EmployeeFileRules.BankMaxLength;
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
        // The shared check (EmployeeFileRules.Check): the name, the CIN and the CNSS number in their
        // form, the salary a number and the RIB with its key, each refused on its box.
        var form = new EmployeeForm
        {
            LastName = _lastName.Text,
            FirstName = _firstName.Text,
            ArabicName = _arabicName.Text,
            Gender = Selected<Gender>(_gender),
            BirthDate = DateOrNull(_birth.Toggle, _birth.Picker),
            Cin = _cin.Text,
            Cnss = _cnss.Text,
            FamilyStatus = Selected<FamilyStatus>(_family),
            Children = (int)_children.Value,
            Phone = _phone.Text,
            Email = _email.Text,
            City = _city.Text,
            Address = _address.Text,
            Job = Selected<EmployeeJob>(_job),
            JobTitle = _jobTitle.Text,
            DepartmentId = SelectedId(_department),
            UserId = SelectedId(_user),
            Exposed = _exposed.Checked,
            ConfidentialityClauseSignedOn = DateOrNull(_clause.Toggle, _clause.Picker),
            ContractType = Selected<ContractType>(_contract),
            HireDate = DateOnly.FromDateTime(_hireDate.Value.Date),
            ContractEndDate = DateOrNull(_contractEnd.Toggle, _contractEnd.Picker),
            TrialPeriodEndDate = DateOrNull(_trialEnd.Toggle, _trialEnd.Picker),
            WeeklyHours = _weeklyHours.Value,
            Salary = _salary?.Text,
            Rib = _rib?.Text,
            Bank = _bank?.Text
        };

        var (refusal, request) = EmployeeFileRules.Check(form, _detail?.Request.EmployeeId, _canSeeSalary);
        if (Refused(refusal,
                    (EmployeeFileRules.LastNameField, _lastName), (EmployeeFileRules.FirstNameField, _firstName),
                    (EmployeeFileRules.CinField, _cin), (EmployeeFileRules.CnssField, _cnss),
                    (EmployeeFileRules.ChildrenField, _children), (EmployeeFileRules.WeeklyHoursField, _weeklyHours),
                    (EmployeeFileRules.SalaryField, _salary), (EmployeeFileRules.RibField, _rib)))
            return;

        Request = request!;
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
        Add("obtained", "Credential.ObtainedOn", 10, 120, UiTheme.Monospace);
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
            if (CredentialRules.IsAlarm(row, DateOnly.FromDateTime(DateTime.Today)))
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };

        // Wide enough for the actions and « Fermer » side by side: the Arabic captions are longer,
        // and « Fermer » slid under the last of them, out of reach.
        var needed = actions.PreferredSize.Width + 12 + 24 + close.Width + 12;
        if (needed > ClientSize.Width)
        {
            var grow = needed - ClientSize.Width;
            ClientSize = new Size(needed, ClientSize.Height);
            host.Width += grow;
            close.Left += grow;
        }

        // Opened read-only from the personnel and deadline screens: the changes were offered to a
        // user who may only consult the file, then refused by the service.
        PermissionGate.Require(Permissions.HumanResources.Manage, _add, _edit, _attach, _remove);
        UpdateButtons();
        QhDialog.Dress(this, localizer, "QualityHr.Hint.Credentials");
    }

    private CredentialRow? Current() => _grid.CurrentRow?.Tag as CredentialRow;

    private string T(string key, params object?[] args) => args.Length == 0 ? _localizer[key] : _localizer.Format(key, args);

    private void UpdateButtons()
    {
        // By document and one operation at a time (CredentialRules.Actions); the right is the gate's.
        var actions = CredentialRules.Actions(Current(), _pending > 0, _ => true);
        _edit.Enabled = actions.Edit;
        _attach.Enabled = actions.Attach;
        _remove.Enabled = actions.Remove;
        _open.Enabled = actions.Open;
        _add.Enabled = actions.Add;
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
            var index = _grid.Rows.Add(CredentialRules.Kind(c, T), c.Title, c.Reference ?? string.Empty,
                                       c.IssuedBy ?? string.Empty, PersonnelScreenRules.DateOrDash(c.ObtainedOn),
                                       PersonnelScreenRules.DateOrDash(c.ExpiresOn), CredentialRules.Scan(c, T),
                                       CredentialRules.Note(c, T));
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
            Title = UserDialogs.CaptionOf(_attach.Text, _attach.Text),
            Filter = $"{_localizer["Credential.ScanFiles"]} (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        byte[] content;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (CredentialRules.CheckScan(info.Name, info.Length) is { } refused)
            {
                _dialogs.Warn(refused.Typed is { } typed ? _localizer.Format(refused.Key, typed) : _localizer[refused.Key],
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

        var reason = _dialogs.PromptForReason(_localizer.Format("Credential.RemovePrompt", row.Title), _remove.Text,
                                              maxLength: CredentialRules.RemoveReasonMaxLength);
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
    private readonly CredentialForm _form;

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
        _title.MaxLength = CredentialRules.TitleMaxLength;
        _issuer.MaxLength = CredentialRules.IssuedByMaxLength;
        _reference.MaxLength = CredentialRules.ReferenceMaxLength;
        _note.MaxLength = CredentialRules.NoteMaxLength;
        Notice(20, 266, 480, 34, "Credential.NoDiagnosisNotice");

        // The form the shared rules propose (CredentialRules.FormFor): the document as it is.
        _form = CredentialRules.FormFor(existing, DateOnly.FromDateTime(DateTime.Today), (k, a) => a.Length == 0 ? localizer[k] : localizer.Format(k, a));
        if (existing is not null)
        {
            _title.Text = _form.Title;
            _reference.Text = _form.Reference ?? string.Empty;
            _issuer.Text = _form.IssuedBy ?? string.Empty;
            SetDate(_obtained, _form.ObtainedOn);
            SetDate(_expires, _form.ExpiresOn);
            _note.Text = _form.Note ?? string.Empty;
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
        _form.Kind = Selected<CredentialKind>(_kind);
        _fitness.Enabled = CredentialRules.AsksFitness(_form.Kind);

        _form.Title = _title.Text;
        CredentialRules.KindChanged(_form, (k, a) => a.Length == 0 ? Localizer[k] : Localizer.Format(k, a));
        _title.Text = _form.Title;
    }

    protected override void Accept()
    {
        _form.Kind = Selected<CredentialKind>(_kind);
        _form.Fitness = Selected<MedicalFitness>(_fitness);
        _form.Title = _title.Text;
        _form.Reference = _reference.Text;
        _form.IssuedBy = _issuer.Text;
        _form.ObtainedOn = DateOrNull(_obtained.Toggle, _obtained.Picker);
        _form.ExpiresOn = DateOrNull(_expires.Toggle, _expires.Picker);
        _form.Note = _note.Text;

        var (refusal, request) = CredentialRules.Check(_form);
        if (Refused(refusal, (CredentialRules.TitleField, _title), (CredentialRules.ExpiresField, _expires.Picker))) return;

        Request = request!;
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
                                (DateOnly Date, ExitReason Reason, string? Comment)? current = null,
                                string? linkedUserName = null)
        : base(localizer, dialogs, DepartureRules.TitleKey(correcting: current is not null), employee, 520,
               linkedUserName is null ? 250 : 300)
    {
        Label("Personnel.ExitDate", 20, 10);
        _date.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        if (current is { } date) _date.Value = date.Date.ToDateTime(TimeOnly.MinValue);
        Controls.Add(_date);

        _reason = Choice("Personnel.ExitReason", 270, 10, 230, current?.Reason ?? ExitReason.Resignation, "ExitReason");
        _comment = TextField("Field.Comment", 20, 64, 480, current?.Comment);
        _comment.MaxLength = DepartureRules.CommentMaxLength;
        Notice(20, 116, 480, 40, "Personnel.ExitNoticeAfterLastDay", UiTheme.Abnormal);

        // The linked LIS account stops with the departure (owner decision, cycle 5): said before
        // the exit is saved, with the account's name.
        if (DepartureRules.AccountNotice(linkedUserName, (k, a) => Localizer.Format(k, a)) is { } notice)
            Notice(20, 160, 480, 40, "Personnel.ExitDeactivatesAccount", UiTheme.Abnormal).Text = notice;

        Buttons(linkedUserName is null ? 200 : 250, 520);
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
        // The day after the departure at the earliest (DepartureRules.EarliestRehire).
        _date.MinDate = DepartureRules.EarliestRehire(exit).ToDateTime(TimeOnly.MinValue);
        _date.Value = DepartureRules.RehireDefault(exit, DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
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
    private readonly HabilitationForm _form;

    /// <summary>The decision checked by the shared rules (HabilitationScreenRules.Check).</summary>
    internal HabilitationGrant Grant { get; private set; } = null!;

    internal HabilitationGrantDialog(ILocalizer localizer, IUserDialogs dialogs, string employee,
                                     PersonnelLookups lookups, HabilitationColumn? column)
        : base(localizer, dialogs, "Habilitation.Grant", employee, 560, 350)
    {
        _lookups = lookups;
        // Opened on the column selected, habilitated from today, reviewed in a year (HabilitationScreenRules.FormFor).
        _form = HabilitationScreenRules.FormFor(column, lookups, DateOnly.FromDateTime(DateTime.Today));

        _activity = Choice("Habilitation.Activity", 20, 10, 250, _form.Activity, "HabilitationActivity");

        Label("Habilitation.Subject", 290, 10);
        _subject.DropDownStyle = ComboBoxStyle.DropDownList;
        _subject.FlatStyle = FlatStyle.Flat;
        _subject.Bounds = new Rectangle(290, 28, 250, UiTheme.InputHeight);
        Controls.Add(_subject);

        _level = Choice("Habilitation.Level", 20, 62, 250, _form.Level, "HabilitationLevel");

        Label("Habilitation.GrantedOn", 290, 62);
        _granted.Bounds = new Rectangle(290, 80, 180, UiTheme.InputHeight);
        _granted.Format = DateTimePickerFormat.Short;
        _granted.MaxDate = DateTime.Today;
        _granted.Value = _form.GrantedOn.ToDateTime(TimeOnly.MinValue);
        Controls.Add(_granted);

        _review = OptionalDate("Habilitation.ReviewDue", 20, 112, _form.ReviewDueOn, min: DateTime.Today);

        _evidence = TextField("Habilitation.Evidence", 20, 166, 520);
        _evidence.MaxLength = HabilitationScreenRules.EvidenceMaxLength;
        Notice(20, 216, 520, 70, "Habilitation.SignatureNotice");

        _activity.SelectedIndexChanged += (_, _) =>
        {
            _form.Activity = Selected<HabilitationActivity>(_activity);
            HabilitationScreenRules.ActivityChanged(_form, _lookups);
            FillSubjects();
        };
        FillSubjects();

        Buttons(296, 560);
        QhDialog.Dress(this, localizer, null);
    }

    private void FillSubjects()
    {
        _subject.Items.Clear();
        _subject.Enabled = HabilitationScreenRules.AsksSubject(_form.Activity);
        if (!_subject.Enabled) return;

        foreach (var option in HabilitationScreenRules.Subjects(_form.Activity, _lookups))
        {
            var index = _subject.Items.Add(new LookupChoice(option.Id, option.Label));
            if (option.Id == _form.SubjectId) _subject.SelectedIndex = index;
        }

        if (_subject.SelectedIndex < 0 && _subject.Items.Count > 0) _subject.SelectedIndex = 0;
    }

    protected override void Accept()
    {
        _form.Activity = Selected<HabilitationActivity>(_activity);
        _form.SubjectId = _subject.Enabled ? SelectedId(_subject) : null;
        _form.Level = Selected<HabilitationLevel>(_level);
        _form.GrantedOn = DateOnly.FromDateTime(_granted.Value.Date);
        _form.ReviewDueOn = DateOrNull(_review.Toggle, _review.Picker);
        _form.Evidence = _evidence.Text;

        var (refusal, grant) = HabilitationScreenRules.Check(_form, DateOnly.FromDateTime(DateTime.Today));
        if (Refused(refusal, (HabilitationScreenRules.SubjectField, _subject), (HabilitationScreenRules.GrantedOnField, _granted),
                    (HabilitationScreenRules.ReviewField, _review.Picker)))
            return;

        Grant = grant!;
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
        // The amounts saved, or the law's (SettlementScreenRules.FormFor), and the sentences that explain them.
        var a = SettlementScreenRules.FormFor(d, Fr);

        Controls.Add(new Label
        {
            Font = UiTheme.Body, ForeColor = UiTheme.TextPrimary, AutoSize = false, Bounds = new Rectangle(20, 10, 680, 40),
            Text = SettlementScreenRules.Summary(d, T, Fr)
        });

        _reference = TextField("Settlement.ReferenceSalary", 20, 56, 200, a.ReferenceSalary, mono: true, right: true);
        _leaveDays = TextField("Settlement.LeaveDays", 250, 56, 180, a.LeaveDays, mono: true, right: true);
        _leave = TextField("Settlement.LeaveIndemnity", 460, 56, 240, a.LeaveIndemnity, mono: true, right: true);
        Hint(20, 100, 420, SettlementScreenRules.ReferenceHint(d, T));

        _noticeNotWorked = Check("Settlement.NoticeNotWorked", 20, 132, a.NoticeNotWorked);
        _notice = TextField("Settlement.NoticeIndemnity", 460, 124, 240, a.NoticeIndemnity, mono: true, right: true);
        Hint(20, 156, 420, SettlementScreenRules.NoticeHint(d, T));

        _severance = TextField("Settlement.Severance", 460, 192, 240, a.Severance, mono: true, right: true);
        Hint(20, 210, 420, SettlementScreenRules.SeveranceHint(d, T, Fr));

        _note = TextField("Field.Notes", 20, 252, 680, a.Note);
        _note.MaxLength = SettlementScreenRules.NoteMaxLength;
        Numeric(SettlementScreenRules.AmountMaxLength, _reference, _leave, _notice, _severance);
        // A decimal(9,2) column: seven figures before the comma. Checked as a number like the
        // amounts: "abc" in the days was refused as a negative amount.
        Numeric(SettlementScreenRules.LeaveDaysMaxLength, _leaveDays);

        _total = new Label { Font = UiTheme.BodyBold, ForeColor = UiTheme.TextPrimary, AutoSize = false, Bounds = new Rectangle(20, 306, 680, 22) };
        Controls.Add(_total);

        var (notice, warning) = SettlementScreenRules.Notice(d, T);
        Notice(20, 336, 680, 60, "Settlement.NoticePayroll", warning ? UiTheme.Abnormal : null).Text = notice;
        if (SettlementScreenRules.IsReadOnly(d))
            foreach (var box in new Control[] { _reference, _leaveDays, _leave, _notice, _severance, _note, _noticeNotWorked })
                box.Enabled = false;

        Buttons(410, 720);

        _notice.Enabled = SettlementScreenRules.NoticeEditable(Form(), d);
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

    private string T(string key, params object?[] args) => args.Length == 0 ? Localizer[key] : Localizer.Format(key, args);

    private void Hint(int x, int y, int width, string text) => Controls.Add(new Label
    {
        Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false, AutoEllipsis = true,
        Bounds = new Rectangle(x, y, width, 20), Text = text
    });

    private SettlementForm Form() => new()
    {
        ReferenceSalary = _reference.Text,
        LeaveDays = _leaveDays.Text,
        LeaveIndemnity = _leave.Text,
        NoticeNotWorked = _noticeNotWorked.Checked,
        NoticeIndemnity = _notice.Text,
        Severance = _severance.Text,
        Note = _note.Text
    };

    /// <summary>Puts the law's figure back in each amount whose basis just changed (SettlementScreenRules.Recompute).</summary>
    private void Recompute(bool leave = false, bool notice = false, bool severance = false)
    {
        if (_computing) return;
        _computing = true;
        try
        {
            var form = Form();
            SettlementScreenRules.Recompute(form, _detail, leave, notice, severance, Fr);
            if (_leave.Text != form.LeaveIndemnity) _leave.Text = form.LeaveIndemnity;
            if (_notice.Text != form.NoticeIndemnity) _notice.Text = form.NoticeIndemnity;
            if (_severance.Text != form.Severance) _severance.Text = form.Severance;
        }
        finally
        {
            _computing = false;
        }

        ShowTotal();
    }

    private void ShowTotal() => _total.Text = SettlementScreenRules.TotalText(Form(), T, Fr);

    protected override void Accept()
    {
        if (SettlementScreenRules.IsReadOnly(_detail))
        {
            DialogResult = DialogResult.Cancel;
            return;
        }

        var (refusal, amounts) = SettlementScreenRules.Check(Form());
        if (Refused(refusal, (SettlementScreenRules.ReferenceField, _reference), (SettlementScreenRules.LeaveDaysField, _leaveDays),
                    (SettlementScreenRules.LeaveField, _leave), (SettlementScreenRules.NoticeField, _notice),
                    (SettlementScreenRules.SeveranceField, _severance)))
            return;

        Amounts = amounts!;
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
        // Not before the exit date, not in the future (SettlementScreenRules.EarliestSigning).
        _date.MinDate = SettlementScreenRules.EarliestSigning(exit, DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
        _date.Value = DateTime.Today;
        Controls.Add(_date);

        Notice(20, 70, 480, 70, "Settlement.SignedNotice", UiTheme.Abnormal);
        Buttons(160, 520);
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept() => DialogResult = DialogResult.OK;
}
