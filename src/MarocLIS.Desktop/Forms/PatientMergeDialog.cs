using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// Merging two files that turned out to be the same person.
/// <para>
/// The dialog states the direction explicitly and in one sentence, because getting it backwards is
/// the expensive mistake: the losing file stops appearing in searches, and if the clerk picks the
/// wrong survivor the richer history is the one that disappears from view. The number of orders on
/// each side is shown for exactly that reason — the file with more history is almost always the one
/// to keep.
/// </para>
/// </summary>
internal sealed class PatientMergeDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly PatientSearchRow _duplicate;
    private readonly IReadOnlyList<PatientSearchRow> _candidates;

    private readonly ComboBox _survivor = new();
    private readonly Label _direction = new();
    private readonly Label _warning = new();
    private readonly TextBox _reason = UiTheme.Input(520);
    private readonly Button _ok;

    internal Guid SurvivingPatientId { get; private set; }
    internal string Reason { get; private set; } = string.Empty;

    internal PatientMergeDialog(ILocalizer localizer, IUserDialogs dialogs,
                                PatientSearchRow duplicate,
                                IReadOnlyList<PatientSearchRow> candidates)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _duplicate = duplicate;
        _candidates = candidates;

        Text = localizer["Patient.Merge"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(580, 320);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var losing = new Label
        {
            Text = $"{localizer["Patient.FileToMerge"]} : {duplicate.MedicalRecordNumber} — " +
                   $"{duplicate.FullName} ({duplicate.OrderCount} {localizer["Order.Plural"]})",
            Font = UiTheme.BodyBold,
            AutoSize = false,
            Bounds = new Rectangle(20, 16, 540, 22)
        };

        var survivorLabel = UiTheme.FieldLabel(localizer["Patient.SurvivingFile"]);
        survivorLabel.Location = new Point(20, 50);

        _survivor.DropDownStyle = ComboBoxStyle.DropDownList;
        _survivor.FlatStyle = FlatStyle.Flat;
        _survivor.Bounds = new Rectangle(20, 68, 540, UiTheme.InputHeight);

        // Ordered by history depth: the file that has seen more orders is the one worth keeping.
        foreach (var c in candidates.OrderByDescending(c => c.OrderCount))
        {
            _survivor.Items.Add(new Choice(c.Id,
                $"{c.MedicalRecordNumber} — {c.FullName} ({c.OrderCount} {localizer["Order.Plural"]})"));
        }

        if (_survivor.Items.Count > 0) _survivor.SelectedIndex = 0;
        _survivor.SelectedIndexChanged += (_, _) => RefreshDirection();

        _direction.Font = UiTheme.SmallBold;
        _direction.ForeColor = UiTheme.Accent;
        _direction.AutoSize = false;
        _direction.Bounds = new Rectangle(20, 102, 540, 34);

        _warning.Text = localizer["Patient.MergeExplanation"];
        _warning.Font = UiTheme.Small;
        _warning.ForeColor = UiTheme.TextSecondary;
        _warning.AutoSize = false;
        _warning.Bounds = new Rectangle(20, 140, 540, 46);

        var reasonLabel = UiTheme.FieldLabel(localizer["Field.Reason"]);
        reasonLabel.Location = new Point(20, 194);

        _reason.Location = new Point(20, 212);
        _reason.TextChanged += (_, _) => RefreshOk();

        _ok = UiTheme.PrimaryButton(localizer["Patient.Merge"]);
        _ok.Bounds = new Rectangle(20, 264, 260, UiTheme.ButtonHeight);
        _ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(290, 264, 270, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([losing, survivorLabel, _survivor, _direction, _warning, reasonLabel,
                           _reason, _ok, cancel]);

        CancelButton = cancel;

        RefreshDirection();
        RefreshOk();
    }

    /// <summary>Spells the direction out as a sentence, so it cannot be misread from two dropdowns.</summary>
    private void RefreshDirection()
    {
        if (_survivor.SelectedItem is not Choice choice) { _direction.Text = string.Empty; return; }

        _direction.Text = _localizer.Format("Patient.MergeDirection",
                                             _duplicate.MedicalRecordNumber,
                                             choice.Display.Split(" — ")[0]);
    }

    private void RefreshOk() => _ok.Enabled = _reason.Text.Trim().Length >= 5
                                              && _survivor.SelectedItem is Choice;

    private void Accept()
    {
        if (_survivor.SelectedItem is not Choice choice) return;

        if (_reason.Text.Trim().Length < 5)
        {
            _dialogs.Warn(_localizer["Message.ReasonRequiredPrompt"],
                          _localizer["Message.ReasonRequiredTitle"]);
            return;
        }

        if (!_dialogs.Confirm(_localizer.Format("Patient.ConfirmMerge",
                                                 _duplicate.MedicalRecordNumber,
                                                 choice.Display.Split(" — ")[0]),
                               _localizer["Patient.Merge"]))
            return;

        SurvivingPatientId = choice.Id;
        Reason = _reason.Text.Trim();

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record Choice(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }
}
