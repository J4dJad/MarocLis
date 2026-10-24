using MarocLIS.Application.Input;
using MarocLIS.Application.Patients;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// Attaching an insurance entitlement.
/// <para>
/// The fields here are the ones that decide money: the payer sets the reimbursement base, the
/// membership number is what the claim is filed under, and an ALD agreement takes the file to
/// 100 % — which is why the prior-agreement reference becomes mandatory the moment it is ticked.
/// </para>
/// </summary>
internal sealed class CoverageDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly IReadOnlyList<PayerOption> _payers;

    private readonly ComboBox _payer = new();
    private readonly TextBox _membership = UiTheme.Input(200);
    private readonly ComboBox _relationship = new();
    private readonly TextBox _insuredName = UiTheme.Input(200);
    private readonly TextBox _insuredCin = UiTheme.Input(160);
    private readonly TextBox _employer = UiTheme.Input(200);
    private readonly DateTimePicker _validFrom = new();
    private readonly DateTimePicker _validUntil = new();
    private readonly CheckBox _hasValidUntil = new();
    private readonly CheckBox _isPrimary = new();
    private readonly CheckBox _isAld = new();
    private readonly TextBox _priorAgreement = UiTheme.Input(200);
    private readonly DateTimePicker _priorAgreementUntil = new();
    private readonly TextBox _rateOverride = UiTheme.Input(100);
    private readonly CheckBox _directBilling = new();
    private readonly Label _rateNotice = new();

    internal CoverageRequest? Request { get; private set; }

    internal CoverageDialog(ILocalizer localizer, IUserDialogs dialogs,
                            IReadOnlyList<PayerOption> payers)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _payers = payers;

        // The lengths of the coverage's columns: a longer text failed the patient's save with an
        // unexpected error, and on a new patient the whole form was lost with it.
        foreach (var (box, length) in new[]
                 {
                     (_membership, 40), (_insuredName, 200), (_insuredCin, 20), (_employer, 200),
                     (_priorAgreement, 60), (_rateOverride, 10)
                 })
            box.MaxLength = length;

        Text = localizer["Coverage.Add"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 470);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        void Field(string key, int top, int left = 20)
        {
            var label = UiTheme.FieldLabel(localizer[key]);
            label.Location = new Point(left, top);
            Controls.Add(label);
        }

        Field("Coverage.Payer", 14);
        _payer.DropDownStyle = ComboBoxStyle.DropDownList;
        _payer.FlatStyle = FlatStyle.Flat;
        _payer.Bounds = new Rectangle(20, 32, 470, UiTheme.InputHeight);

        foreach (var p in payers)
            _payer.Items.Add(new PayerChoice(p, $"{p.Code} — {p.Name}"));

        if (_payer.Items.Count > 0) _payer.SelectedIndex = 0;
        _payer.SelectedIndexChanged += (_, _) => RefreshRateNotice();

        _rateNotice.Font = UiTheme.SmallBold;
        _rateNotice.ForeColor = UiTheme.Accent;
        _rateNotice.AutoSize = false;
        _rateNotice.Bounds = new Rectangle(20, 64, 470, 20);
        Controls.Add(_rateNotice);

        Field("Coverage.MembershipNumber", 90);
        _membership.Location = new Point(20, 108);
        _membership.Tag = UiTags.LatinValue;
        _membership.Font = UiTheme.Monospace;

        Field("Coverage.Relationship", 90, 240);
        _relationship.DropDownStyle = ComboBoxStyle.DropDownList;
        _relationship.FlatStyle = FlatStyle.Flat;
        _relationship.Bounds = new Rectangle(240, 108, 250, UiTheme.InputHeight);

        foreach (var r in Enum.GetValues<BeneficiaryRelationship>())
            _relationship.Items.Add(new EnumChoice((int)r,
                                                    localizer[$"BeneficiaryRelationship.{r}"]));

        _relationship.SelectedIndex = 0;
        _relationship.SelectedIndexChanged += (_, _) => RefreshInsuredFields();

        Field("Coverage.InsuredPerson", 146);
        _insuredName.Location = new Point(20, 164);

        Field("Coverage.InsuredPersonCin", 146, 240);
        _insuredCin.Location = new Point(240, 164);
        _insuredCin.Tag = UiTags.LatinValue;
        _insuredCin.Font = UiTheme.Monospace;

        Field("Coverage.Employer", 202);
        _employer.Location = new Point(20, 220);

        Field("Coverage.RateOverride", 202, 240);
        _rateOverride.Location = new Point(240, 220);
        _rateOverride.Tag = UiTags.LatinValue;
        _rateOverride.TextAlign = HorizontalAlignment.Right;

        Field("Coverage.ValidFrom", 258);
        _validFrom.Bounds = new Rectangle(20, 276, 200, UiTheme.InputHeight);
        _validFrom.Format = DateTimePickerFormat.Short;

        _hasValidUntil.Text = localizer["Coverage.ValidUntil"];
        _hasValidUntil.AutoSize = true;
        _hasValidUntil.Location = new Point(240, 258);
        _hasValidUntil.CheckedChanged += (_, _) => _validUntil.Enabled = _hasValidUntil.Checked;
        Controls.Add(_hasValidUntil);

        _validUntil.Bounds = new Rectangle(240, 276, 200, UiTheme.InputHeight);
        _validUntil.Format = DateTimePickerFormat.Short;
        _validUntil.Value = DateTime.Today.AddYears(1);
        _validUntil.Enabled = false;

        _isPrimary.Text = localizer["Coverage.Primary"];
        _isPrimary.Checked = true;
        _isPrimary.AutoSize = true;
        _isPrimary.Location = new Point(20, 316);
        Controls.Add(_isPrimary);

        _directBilling.Text = localizer["Coverage.DirectBilling"];
        _directBilling.Checked = true;
        _directBilling.AutoSize = true;
        _directBilling.Location = new Point(240, 316);
        Controls.Add(_directBilling);

        _isAld.Text = localizer["Coverage.ChronicScheme"];
        _isAld.AutoSize = true;
        _isAld.Font = UiTheme.BodyBold;
        _isAld.Location = new Point(20, 346);
        _isAld.CheckedChanged += (_, _) => RefreshAldFields();
        Controls.Add(_isAld);

        Field("Coverage.PriorAgreement", 372);
        _priorAgreement.Location = new Point(20, 390);
        _priorAgreement.Tag = UiTags.LatinValue;
        _priorAgreement.Enabled = false;

        Field("Coverage.PriorAgreementValidUntil", 372, 240);
        _priorAgreementUntil.Bounds = new Rectangle(240, 390, 200, UiTheme.InputHeight);
        _priorAgreementUntil.Format = DateTimePickerFormat.Short;
        _priorAgreementUntil.Value = DateTime.Today.AddYears(1);
        _priorAgreementUntil.Enabled = false;

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 428, 230, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(260, 428, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_payer, _membership, _relationship, _insuredName, _insuredCin,
                           _employer, _rateOverride, _validFrom, _validUntil, _priorAgreement,
                           _priorAgreementUntil, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        PatientDialogFrame.Apply(this, Text, localizer["PatientDialog.CoverageHint"], null, null, cancel, ok);

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);

        RefreshRateNotice();
        RefreshInsuredFields();
        RefreshAldFields();
    }

    /// <summary>
    /// States the base and rate the chosen payer implies, before anything is saved. AMO schemes
    /// price on the national reference tariff rather than the laboratory's list, and the
    /// difference is what the patient is left to pay — so it should not be a surprise at the till.
    /// </summary>
    private void RefreshRateNotice()
    {
        if (_payer.SelectedItem is not PayerChoice choice) { _rateNotice.Text = string.Empty; return; }

        var payer = choice.Option;

        var parts = new List<string>(3)
        {
            $"{_localizer["Coverage.ReimbursementRate"]} {payer.DefaultReimbursementRate * 100m:0.#} %"
        };

        parts.AddRange(PatientFileRules.PayerNoticeKeys(payer.UsesNationalReferenceTariff, payer.RequiresPrescription)
                                       .Select(key => _localizer[key]));

        _rateNotice.Text = string.Join("   ·   ", parts);
    }

    /// <summary>The insured person is only meaningful for a dependent (ayant droit).</summary>
    private void RefreshInsuredFields()
    {
        var dependent = PatientFileRules.AsksInsuredPerson(
            (BeneficiaryRelationship)((_relationship.SelectedItem as EnumChoice)?.Value ?? 0));

        _insuredName.Enabled = dependent;
        _insuredCin.Enabled = dependent;

        if (!dependent) { _insuredName.Text = string.Empty; _insuredCin.Text = string.Empty; }
    }

    private void RefreshAldFields()
    {
        _priorAgreement.Enabled = _isAld.Checked;
        _priorAgreementUntil.Enabled = _isAld.Checked;
    }

    private void Accept()
    {
        if (_payer.SelectedItem is not PayerChoice choice)
        {
            _dialogs.Warn(_localizer["Coverage.NoPayersConfigured"]);
            return;
        }

        switch (PatientFileRules.CoverageProblem(_membership.Text, _isAld.Checked, _priorAgreement.Text))
        {
            case "Coverage_MembershipNumberRequired":
                _dialogs.Warn(_localizer["Coverage_MembershipNumberRequired"]);
                _membership.Focus();
                return;

            case { } problem:
                _dialogs.Warn(_localizer[problem]);
                _priorAgreement.Focus();
                return;
        }

        // Accepts either "70" or "0.70": a clerk types the percentage they read off the card.
        var typed = PatientFileRules.ReadRate(_rateOverride.Text);

        if (typed.ProblemKey is { } refused)
        {
            _dialogs.Warn(refused == "Result_NotANumber"
                ? _localizer.Format(refused, _rateOverride.Text)
                : _localizer[refused]);
            _rateOverride.Focus();
            return;
        }

        var rate = typed.Rate;

        // « 1 » or « 0,7 » is read as a fraction (100 %, 70 %), not as 1 % or 0,7 %: said back
        // before it is saved, since a clerk who meant a small percentage would never notice.
        if (typed.NeedsFractionConfirmation
            && !_dialogs.Confirm(_localizer.Format("Coverage.ConfirmFractionRate",
                                                   _rateOverride.Text.Trim(), $"{rate * 100m:0.#}"),
                                 _localizer["Coverage.RateOverride"]))
        {
            _rateOverride.Focus();
            return;
        }

        Request = new CoverageRequest
        {
            PayerId = choice.Option.Id,
            MembershipNumber = _membership.Text.Trim(),
            Relationship = (BeneficiaryRelationship)
                ((_relationship.SelectedItem as EnumChoice)?.Value ?? 0),
            InsuredPersonName = Blank(_insuredName.Text),
            InsuredPersonCin = Blank(_insuredCin.Text),
            EmployerName = Blank(_employer.Text),
            ValidFrom = DateOnly.FromDateTime(_validFrom.Value.Date),
            ValidUntil = _hasValidUntil.Checked
                ? DateOnly.FromDateTime(_validUntil.Value.Date) : null,
            IsPrimary = _isPrimary.Checked,
            ReimbursementRateOverride = rate,
            IsChronicDiseaseScheme = _isAld.Checked,
            PriorAgreementNumber = Blank(_priorAgreement.Text),
            PriorAgreementValidUntil = _isAld.Checked
                ? DateOnly.FromDateTime(_priorAgreementUntil.Value.Date) : null,
            DirectBillingEnabled = _directBilling.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PayerChoice(PayerOption Option, string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>
/// Recording a consent.
/// <para>
/// Not a formality. <c>OrderRegistrationService</c> refuses to register a genetic test, an HIV
/// serology or a prenatal screen without one, so this dialog is the only thing that makes those
/// analyses performable at all. The channel matters as much as the purpose: for those purposes the
/// law wants writing, and a verbal tick is refused.
/// </para>
/// </summary>
internal sealed class ConsentDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly bool _isMinor;

    private readonly ComboBox _purpose = new();
    private readonly ComboBox _channel = new();
    private readonly ComboBox _language = new();
    private readonly TextBox _signedBy = UiTheme.Input(220);
    private readonly TextBox _signedByCin = UiTheme.Input(160);
    private readonly TextBox _signedByRelationship = UiTheme.Input(160);
    private readonly TextBox _formVersion = UiTheme.Input(140);
    private readonly DateTimePicker _expires = new();
    private readonly CheckBox _hasExpiry = new();
    private readonly TextBox _notes = UiTheme.Input(440);
    private readonly Label _writtenNotice = new();

    internal ConsentRequest? Request { get; private set; }

    internal ConsentDialog(ILocalizer localizer, IUserDialogs dialogs, bool isMinor,
                           string? guardianName)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _isMinor = isMinor;

        // The lengths of the consent's columns: a longer text failed the save with an unexpected error.
        foreach (var (box, length) in new[]
                 {
                     (_signedBy, 200), (_signedByCin, 20), (_signedByRelationship, 60), (_formVersion, 40),
                     (_notes, 1000)
                 })
            box.MaxLength = length;

        Text = localizer["Consent.Record"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 400);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        void Field(string key, int top, int left = 20)
        {
            var label = UiTheme.FieldLabel(localizer[key]);
            label.Location = new Point(left, top);
            Controls.Add(label);
        }

        Field("Consent.Purpose", 14);
        _purpose.DropDownStyle = ComboBoxStyle.DropDownList;
        _purpose.FlatStyle = FlatStyle.Flat;
        _purpose.Bounds = new Rectangle(20, 32, 440, UiTheme.InputHeight);

        foreach (var p in Enum.GetValues<ConsentPurpose>())
            _purpose.Items.Add(new EnumChoice((int)p, localizer[$"ConsentPurpose.{p}"]));

        _purpose.SelectedIndex = 0;
        _purpose.SelectedIndexChanged += (_, _) => RefreshWrittenRequirement();

        _writtenNotice.Font = UiTheme.SmallBold;
        _writtenNotice.ForeColor = UiTheme.Abnormal;
        _writtenNotice.AutoSize = false;
        _writtenNotice.Bounds = new Rectangle(20, 64, 440, 32);
        Controls.Add(_writtenNotice);

        Field("Consent.Channel", 102);
        _channel.DropDownStyle = ComboBoxStyle.DropDownList;
        _channel.FlatStyle = FlatStyle.Flat;
        _channel.Bounds = new Rectangle(20, 120, 200, UiTheme.InputHeight);

        foreach (var c in Enum.GetValues<ConsentChannel>())
            _channel.Items.Add(new EnumChoice((int)c, localizer[$"ConsentChannel.{c}"]));

        _channel.SelectedIndex = 0;
        _channel.SelectedIndexChanged += (_, _) => RefreshWrittenRequirement();

        // The language the form was presented in is evidence: consent given on a document the
        // patient could not read is not consent.
        Field("Consent.PresentedIn", 102, 240);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.FlatStyle = FlatStyle.Flat;
        _language.Bounds = new Rectangle(240, 120, 220, UiTheme.InputHeight);

        // Only the languages the laboratory works in: no consent form is presented in another.
        LanguageChoices.Fill(_language, l => localizer[$"AppLanguage.{l}"], AppLanguage.French);

        Field("Consent.SignedBy", 158);
        _signedBy.Location = new Point(20, 176);
        _signedBy.Text = isMinor ? guardianName ?? string.Empty : string.Empty;

        Field("Consent.SignedByCin", 158, 260);
        _signedByCin.Location = new Point(260, 176);
        _signedByCin.Tag = UiTags.LatinValue;
        _signedByCin.Font = UiTheme.Monospace;

        Field("Consent.SignedByRelationship", 214);
        _signedByRelationship.Location = new Point(20, 232);
        _signedByRelationship.Text = isMinor ? localizer["Patient.Guardian"] : string.Empty;

        Field("Consent.FormVersion", 214, 260);
        _formVersion.Location = new Point(260, 232);
        _formVersion.Tag = UiTags.LatinValue;

        _hasExpiry.Text = localizer["Consent.ExpiresOn"];
        _hasExpiry.AutoSize = true;
        _hasExpiry.Location = new Point(20, 272);
        _hasExpiry.CheckedChanged += (_, _) => _expires.Enabled = _hasExpiry.Checked;
        Controls.Add(_hasExpiry);

        _expires.Bounds = new Rectangle(260, 270, 200, UiTheme.InputHeight);
        _expires.Format = DateTimePickerFormat.Short;
        _expires.MinDate = DateTime.Today;
        _expires.Value = DateTime.Today.AddYears(1);
        _expires.Enabled = false;

        Field("Field.Notes", 302);
        _notes.Location = new Point(20, 320);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 358, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 358, 210, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_purpose, _channel, _language, _signedBy, _signedByCin,
                           _signedByRelationship, _formVersion, _expires, _notes, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        PatientDialogFrame.Apply(this, Text, localizer["PatientDialog.ConsentHint"], null, null, cancel, ok);

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);

        RefreshWrittenRequirement();
    }

    /// <summary>
    /// Warns while the purpose is being chosen, not after Save. The service refuses a verbal
    /// consent for a purpose the law wants in writing, and a refusal the operator could have
    /// foreseen should have been a label.
    /// </summary>
    private void RefreshWrittenRequirement()
    {
        var purpose = (ConsentPurpose)((_purpose.SelectedItem as EnumChoice)?.Value ?? 0);
        var channel = (ConsentChannel)((_channel.SelectedItem as EnumChoice)?.Value ?? 0);

        _writtenNotice.Text = PatientFileRules.ConsentNoticeKey(purpose, channel, _isMinor) switch
        {
            null => string.Empty,
            "Order_ConsentMustBeWritten" => _localizer.Format("Order_ConsentMustBeWritten",
                                                              _localizer[$"ConsentPurpose.{purpose}"]),
            var key => _localizer[key]
        };
    }

    private void Accept()
    {
        var purpose = (ConsentPurpose)((_purpose.SelectedItem as EnumChoice)?.Value ?? 0);
        var channel = (ConsentChannel)((_channel.SelectedItem as EnumChoice)?.Value ?? 0);

        switch (PatientFileRules.ConsentProblem(purpose, channel, _isMinor, _signedBy.Text))
        {
            case "Order_ConsentMustBeWritten":
                _dialogs.Warn(_localizer.Format("Order_ConsentMustBeWritten",
                                                 _localizer[$"ConsentPurpose.{purpose}"]));
                return;

            case { } problem:
                _dialogs.Warn(_localizer[problem]);
                _signedBy.Focus();
                return;
        }

        Request = new ConsentRequest
        {
            Purpose = purpose,
            Channel = channel,
            PresentedInLanguage = (AppLanguage)((_language.SelectedItem as EnumChoice)?.Value ?? 0),
            SignedByName = Blank(_signedBy.Text),
            SignedByCin = Blank(_signedByCin.Text),
            SignedByRelationship = Blank(_signedByRelationship.Text),
            FormVersion = Blank(_formVersion.Text),
            ExpiresOn = _hasExpiry.Checked ? DateOnly.FromDateTime(_expires.Value.Date) : null,
            Notes = Blank(_notes.Text)
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The duplicate question.
/// <para>
/// Deliberately hard to dismiss. A duplicate file splits one patient's history across two records,
/// and the cost lands on the next clinician, who loses the previous-result column that makes a
/// delta check mean anything. So the dialog offers exactly two ways out: open the file that
/// already exists, or state in writing why this is a different person.
/// </para>
/// </summary>
internal sealed class DuplicateWarningForm : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly IReadOnlyList<DuplicateCandidate> _candidates;

    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly TextBox _reason = UiTheme.Input(720);
    private readonly Button _useExisting;
    private readonly Button _createAnyway;

    internal Guid? SelectedExistingPatientId { get; private set; }
    internal string? OverrideReason { get; private set; }

    internal DuplicateWarningForm(ILocalizer localizer, IUserDialogs dialogs,
                                  IReadOnlyList<DuplicateCandidate> candidates)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _candidates = candidates;

        Text = localizer["Patient.DuplicateTitle"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        // 760 wide: at 620 every cell of the candidates was cut, and which file matched could not be read.
        ClientSize = new Size(760, 400);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var warning = new Label
        {
            Text = localizer.Format("Patient_DuplicateSuspected", candidates.Count),
            Font = UiTheme.BodyBold,
            ForeColor = UiTheme.Abnormal,
            AutoSize = false,
            Bounds = new Rectangle(20, 14, 720, 40)
        };

        var explanation = new Label
        {
            Text = localizer["Patient.DuplicateExplanation"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 54, 720, 32)
        };

        void Add(string name, int w, Font? f = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = w, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = f ?? UiTheme.Body }
            });

        // Weighted for the 720 px of the grid: the record number, the telephone and « Correspondance »
        // whole, the name taking what is left.
        Add("record", 17, UiTheme.Monospace);
        Add("name", 21);
        Add("gender", 10, UiTheme.Small);
        Add("age", 7, UiTheme.Monospace);
        Add("cin", 12, UiTheme.Monospace);
        Add("phone", 15, UiTheme.Monospace);
        Add("matched", 18, UiTheme.SmallBold);

        _grid.Dock = DockStyle.None;
        _grid.Bounds = new Rectangle(20, 92, 720, 140);

        foreach (var c in candidates)
        {
            var index = _grid.Rows.Add(
                c.MedicalRecordNumber,
                c.FullName,
                localizer[$"Gender.{c.Gender}"],
                c.AgeYears?.ToString() ?? "—",
                c.Cin ?? string.Empty,
                c.MobilePhone ?? string.Empty,
                localizer[c.MatchedOn]);

            _grid.Rows[index].Tag = c;

            // A CIN match is near-certain identity; a name match is a coincidence worth checking.
            if (PatientFileRules.IsCertainMatch(c))
            {
                _grid.Rows[index].DefaultCellStyle.BackColor = UiTheme.CriticalBackground;
                _grid.Rows[index].DefaultCellStyle.ForeColor = UiTheme.Critical;
                _grid.Rows[index].DefaultCellStyle.Font = UiTheme.BodyBold;
            }
        }

        _grid.Column("record").HeaderText = localizer["Patient.RecordNumber"];
        _grid.Column("name").HeaderText = localizer["Patient.Title"];
        _grid.Column("gender").HeaderText = localizer["Patient.Gender"];
        _grid.Column("age").HeaderText = localizer["Patient.Age"];
        _grid.Column("cin").HeaderText = localizer["Patient.Cin"];
        _grid.Column("phone").HeaderText = localizer["Patient.MobilePhone"];
        _grid.Column("matched").HeaderText = localizer["Patient.MatchedOn"];

        var reasonLabel = UiTheme.FieldLabel(localizer["Patient.DuplicateOverrideReason"]);
        reasonLabel.Location = new Point(20, 242);

        _reason.Location = new Point(20, 262);

        _useExisting = UiTheme.PrimaryButton(localizer["Patient.UseExistingFile"]);
        _useExisting.Bounds = new Rectangle(20, 306, 280, 34);
        _useExisting.Click += (_, _) => UseExisting();

        _createAnyway = UiTheme.SecondaryButton(localizer["Patient.CreateAnyway"]);
        _createAnyway.Bounds = new Rectangle(310, 306, 290, 34);
        _createAnyway.Click += (_, _) => CreateAnyway();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(20, 350, 140, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([warning, explanation, _grid, reasonLabel, _reason, _useExisting,
                           _createAnyway, cancel]);

        // No AcceptButton: neither choice should be reachable by a reflexive Enter.
        CancelButton = cancel;

        PatientDialogFrame.Apply(this, Text, explanation.Text, explanation, null,
                                 cancel, _createAnyway, _useExisting);

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        RtlLayout.Apply(this, localizer.CurrentLanguage);

        if (_grid.Rows.Count > 0) _grid.SelectFirstRow();

        _reason.TextChanged += (_, _) => RefreshButtons();
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        _useExisting.Enabled = _grid.CurrentRow?.Tag is DuplicateCandidate;

        // The override is gated on a real justification, not merely a non-empty box.
        var justified = ReasonRules.IsSufficient(_reason.Text, ReasonRules.JustificationMinimumLength);

        _createAnyway.Enabled = justified;
        _createAnyway.ForeColor = justified ? UiTheme.Abnormal : UiTheme.TextMuted;
        _createAnyway.Font = justified ? UiTheme.BodyBold : UiTheme.Body;
    }

    private void UseExisting()
    {
        if (_grid.CurrentRow?.Tag is not DuplicateCandidate candidate) return;

        SelectedExistingPatientId = candidate.PatientId;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CreateAnyway()
    {
        var reason = _reason.Text.Trim();

        if (!ReasonRules.IsSufficient(reason, ReasonRules.JustificationMinimumLength))
        {
            _dialogs.Warn(_localizer["Message.ReasonRequiredPrompt"],
                          _localizer["Message.ReasonRequiredTitle"]);
            _reason.Focus();
            return;
        }

        if (!_dialogs.Confirm(_localizer.Format("Patient.ConfirmCreateDuplicate",
                                                 _candidates.Count),
                               _localizer["Patient.DuplicateTitle"]))
            return;

        OverrideReason = reason;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Adding a clinical or logistic flag to a file.
/// <para>
/// This is what carries "difficult venous access", "left arm only — lymphoedema", "biohazard" and
/// "unpaid balance" from the person who learnt it to the person who needs it. Severity is not
/// decoration: a critical alert blocks registration until it is acknowledged, so choosing it is
/// choosing to stop somebody mid-task, and the form says so before the choice is made.
/// </para>
/// <para>
/// The expiry is optional and usually wanted. "Pregnant, third trimester" is true for three months
/// and misleading for ever after, whereas "known difficult draw" has no end date — so the field is
/// offered rather than demanded, and a flag with no expiry is shown as permanent rather than as a
/// blank nobody filled in.
/// </para>
/// </summary>
internal sealed class PatientAlertDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _severity = new();
    private readonly TextBox _messageFr = UiTheme.Input(440);
    private readonly TextBox _messageAr = UiTheme.Input(440);
    private readonly CheckBox _hasExpiry = new();
    private readonly DateTimePicker _expiry = new();
    private readonly Label _severityNotice = new();
    private readonly Label _messageLabel = new();
    private readonly Label _messageArLabel = new();
    private readonly Label _notice = new();
    private readonly Button _ok;
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    internal PatientAlertSeverity Severity =>
        (PatientAlertSeverity)((_severity.SelectedItem as EnumChoice)?.Value ?? 0);

    internal MultilingualText Message =>
        new(_messageFr.Text.Trim(),
            string.IsNullOrWhiteSpace(_messageAr.Text) ? null : _messageAr.Text.Trim());

    internal DateOnly? ExpiresOn =>
        _hasExpiry.Checked ? DateOnly.FromDateTime(_expiry.Value.Date) : null;

    internal PatientAlertDialog(ILocalizer localizer, IUserDialogs dialogs)
    {
        _localizer = localizer;
        _dialogs = dialogs;

        // The length of the alert's message columns: a longer one failed the save with an unexpected error.
        _messageFr.MaxLength = _messageAr.MaxLength = 500;

        Text = localizer["Patient.AddAlert"];
        StartPosition = FormStartPosition.CenterParent;
        Font = UiTheme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 330);
        BackColor = UiTheme.SurfaceRaised;

        var severityLabel = UiTheme.FieldLabel(string.Empty);
        severityLabel.Name = "severityLabel";
        severityLabel.Location = new Point(20, 16);

        _severity.DropDownStyle = ComboBoxStyle.DropDownList;
        _severity.FlatStyle = FlatStyle.Flat;
        _severity.Bounds = new Rectangle(20, 34, 220, UiTheme.InputHeight);

        _severityNotice.Font = UiTheme.Small;
        _severityNotice.AutoSize = false;
        _severityNotice.Bounds = new Rectangle(20, 66, 460, 30);

        _messageLabel.Location = new Point(20, 102);
        _messageFr.Location = new Point(20, 120);

        _messageArLabel.Location = new Point(20, 156);
        _messageAr.Location = new Point(20, 174);
        _messageAr.RightToLeft = RightToLeft.Yes;

        _hasExpiry.AutoSize = true;
        _hasExpiry.Location = new Point(20, 212);

        _expiry.Bounds = new Rectangle(20, 234, 200, UiTheme.InputHeight);
        _expiry.Format = DateTimePickerFormat.Short;
        _expiry.MinDate = DateTime.Today;
        _expiry.Value = DateTime.Today.AddMonths(6);
        _expiry.Enabled = false;

        _hasExpiry.CheckedChanged += (_, _) => _expiry.Enabled = _hasExpiry.Checked;

        _notice.Font = UiTheme.Small;
        _notice.ForeColor = UiTheme.TextSecondary;
        _notice.AutoSize = false;
        _notice.Bounds = new Rectangle(240, 228, 240, 52);

        _ok = UiTheme.PrimaryButton(string.Empty);
        _ok.Bounds = new Rectangle(20, 284, 230, UiTheme.ButtonHeight);
        _ok.Click += (_, _) => Accept();

        _cancel.Bounds = new Rectangle(260, 284, 220, UiTheme.ButtonHeight);
        _cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([severityLabel, _severity, _severityNotice, _messageLabel, _messageFr,
                           _messageArLabel, _messageAr, _hasExpiry, _expiry, _notice,
                           _ok, _cancel]);

        _severity.SelectedIndexChanged += (_, _) => RefreshSeverity();

        // Enter adds the alert, as in the other dialogs; Accept still requires the message.
        AcceptButton = _ok;
        CancelButton = _cancel;

        ApplyTexts();

        PatientDialogFrame.Apply(this, Text, _localizer["PatientDialog.AlertHint"], null, null, _cancel, _ok);

        // Mirrored in Arabic like the other dialogs: it opened left to right, labels and buttons
        // on the wrong side.
        // The French message stays left to right, the Arabic one right to left, whatever the language.
        RtlLayout.Apply(this, localizer.CurrentLanguage);
        _messageFr.RightToLeft = RightToLeft.No;
        _messageAr.RightToLeft = RightToLeft.Yes;
    }

    private void ApplyTexts()
    {
        if (Controls["severityLabel"] is Label label) label.Text = _localizer["Patient.AlertSeverity"];

        var selected = _severity.SelectedIndex;

        _severity.Items.Clear();

        foreach (var severity in Enum.GetValues<PatientAlertSeverity>())
            _severity.Items.Add(new EnumChoice((int)severity,
                                                _localizer[$"PatientAlertSeverity.{severity}"]));

        _severity.SelectedIndex = selected >= 0 ? selected : 0;

        _messageLabel.Text = _localizer["Patient.AlertMessageFr"];
        _messageLabel.Font = UiTheme.Small;
        _messageLabel.ForeColor = UiTheme.TextSecondary;
        _messageLabel.AutoSize = true;

        _messageArLabel.Text = _localizer["Patient.AlertMessageAr"];
        _messageArLabel.Font = UiTheme.Small;
        _messageArLabel.ForeColor = UiTheme.TextSecondary;
        _messageArLabel.AutoSize = true;

        _hasExpiry.Text = _localizer["Patient.AlertExpiresOn"];
        _notice.Text = _localizer["Patient.AlertExpiryNotice"];

        _ok.Text = _localizer["Action.Add"];
        _cancel.Text = _localizer["Action.Cancel"];

        RefreshSeverity();
    }

    private void RefreshSeverity()
    {
        var severity = Severity;

        _severityNotice.ForeColor = severity switch
        {
            PatientAlertSeverity.Critical => UiTheme.Critical,
            PatientAlertSeverity.Warning => UiTheme.Abnormal,
            _ => UiTheme.TextSecondary
        };

        _severityNotice.Text = _localizer[PatientFileRules.AlertNoticeKey(severity)];
    }

    private void Accept()
    {
        if (PatientFileRules.AlertProblem(_messageFr.Text) is { } problem)
        {
            _dialogs.Warn(_localizer[problem], Text);
            _messageFr.Focus();
            return;
        }

        // A critical alert stops the next operator mid-registration, so choosing it is confirmed.
        // Not friction for its own sake: an alert that interrupts everybody gets switched off, and
        // then the one that mattered is off too.
        if (PatientFileRules.AlertNeedsConfirmation(Severity)
            && !_dialogs.Confirm(_localizer["Patient.ConfirmCriticalAlert"], Text))
            return;

        DialogResult = DialogResult.OK;
        Close();
    }
}
