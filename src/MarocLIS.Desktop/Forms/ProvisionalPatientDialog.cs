using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Patients;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// "Patient X urgence": the two things known about a patient nobody can name yet — sex and an
/// estimated age, both of which the reference ranges need — and nothing else.
/// </summary>
internal sealed class ProvisionalPatientDialog : Form
{
    private readonly ComboBox _gender = new();
    private readonly NumericUpDown _age = new();

    internal Gender Gender => (Gender)((_gender.SelectedItem as EnumChoice)?.Value ?? (int)Gender.Unknown);
    internal int EstimatedAge => (int)_age.Value;

    internal ProvisionalPatientDialog(ILocalizer localizer)
    {
        Text = localizer["Patient.Provisional"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 230);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var help = new Label
        {
            Text = localizer["Patient.ProvisionalHelp"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, 14, 420, 54)
        };

        var genderLabel = UiTheme.FieldLabel(localizer["Patient.Gender"]);
        genderLabel.Location = new Point(20, 76);
        _gender.DropDownStyle = ComboBoxStyle.DropDownList;
        _gender.FlatStyle = FlatStyle.Flat;
        _gender.Bounds = new Rectangle(20, 94, 200, UiTheme.InputHeight);

        foreach (var gender in new[] { Gender.Male, Gender.Female, Gender.Unknown })
            _gender.Items.Add(new EnumChoice((int)gender, localizer[$"Gender.{gender}"]));

        _gender.SelectedIndex = 0;

        var ageLabel = UiTheme.FieldLabel(localizer["Patient.EstimatedAge"]);
        ageLabel.Location = new Point(240, 76);
        _age.Bounds = new Rectangle(240, 94, 120, UiTheme.InputHeight);
        _age.Minimum = 0;
        _age.Maximum = 120;
        _age.Value = 40;

        var create = UiTheme.PrimaryButton(localizer["Patient.ProvisionalCreate"]);
        create.Bounds = new Rectangle(20, 170, 240, UiTheme.ButtonHeight);
        create.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(280, 170, 160, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([help, genderLabel, _gender, ageLabel, _age, create, cancel]);

        AcceptButton = create;
        CancelButton = cancel;
    }
}
