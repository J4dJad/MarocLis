using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// Password change, used both voluntarily and as the mandatory step after a reset or an expiry.
/// <para>
/// The policy is shown as a list before the operator types, rather than as a rejection afterwards.
/// A password field that refuses input without saying why is how people end up writing passwords on
/// the monitor.
/// </para>
/// </summary>
public sealed class ChangePasswordForm : LocalizedForm
{
    private readonly IAuthenticationService _authentication;
    private readonly PasswordPolicy _policy;
    private readonly IUserDialogs _dialogs;

    private readonly Label _currentLabel = new();
    private readonly Label _newLabel = new();
    private readonly Label _confirmLabel = new();
    private readonly Label _rules = new();
    private readonly Label _status = new();

    private readonly TextBox _current = UiTheme.Input(300);
    private readonly TextBox _new = UiTheme.Input(300);
    private readonly TextBox _confirm = UiTheme.Input(300);
    private readonly Button _save = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);

    private Guid _userId;
    private bool _mandatory;

    public ChangePasswordForm(IAuthenticationService authentication, PasswordPolicy policy,
                              ILocalizer localizer, ICultureManager cultureManager,
                              IUserDialogs dialogs)
        : base(localizer, cultureManager)
    {
        _authentication = authentication;
        _policy = policy;
        _dialogs = dialogs;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 340);
        BackColor = UiTheme.SurfaceRaised;

        foreach (var box in (TextBox[])[_current, _new, _confirm])
        {
            box.UseSystemPasswordChar = true;
            box.Tag = UiTags.LatinValue;
        }

        BuildLayout();

        _save.Click += async (_, _) => await SaveAsync();
        _cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = _save;
    }

    /// <summary>
    /// Prepares the dialog. <paramref name="currentPasswordKnown"/> pre-fills the current password
    /// when the change follows straight on from a successful sign-in — the operator has just typed
    /// it, and asking again is friction without any security gain.
    /// </summary>
    public void Configure(Guid userId, string? currentPasswordKnown = null, bool mandatory = false)
    {
        _userId = userId;
        _mandatory = mandatory;

        if (!string.IsNullOrEmpty(currentPasswordKnown))
        {
            _current.Text = currentPasswordKnown;
            _current.Enabled = false;
        }

        // A mandatory change cannot be dismissed: the session is unusable until it is done.
        _cancel.Visible = !mandatory;
        ControlBox = !mandatory;
        CancelButton = mandatory ? null : _cancel;
    }

    private void BuildLayout()
    {
        var y = 20;

        _currentLabel.Location = new Point(24, y);
        _current.Location = new Point(24, y + 18);

        y += 54;
        _newLabel.Location = new Point(24, y);
        _new.Location = new Point(24, y + 18);

        y += 54;
        _confirmLabel.Location = new Point(24, y);
        _confirm.Location = new Point(24, y + 18);

        y += 56;
        _rules.Bounds = new Rectangle(24, y, 392, 70);
        _rules.Font = UiTheme.Small;
        _rules.ForeColor = UiTheme.TextSecondary;
        _rules.AutoSize = false;

        y += 78;
        _save.Bounds = new Rectangle(24, y, 180, UiTheme.ButtonHeight);
        _cancel.Bounds = new Rectangle(214, y, 110, UiTheme.ButtonHeight);

        _status.Bounds = new Rectangle(24, y + 36, 392, 30);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.Critical;
        _status.AutoSize = false;

        Controls.AddRange([_currentLabel, _current, _newLabel, _new, _confirmLabel, _confirm,
                           _rules, _save, _cancel, _status]);
    }

    protected override void ApplyTexts()
    {
        Text = T("Action.ChangePassword");
        _currentLabel.Text = T("Admin.CurrentPassword");
        _newLabel.Text = T("Admin.NewPassword");
        _confirmLabel.Text = T("Admin.ConfirmPassword");
        _save.Text = T("Action.Save");
        _cancel.Text = T("Action.Cancel");

        // Stated up front, not as a rejection after the fact.
        _rules.Text = T("Auth_PasswordTooWeak", _policy.MinimumLength);
    }

    private async Task SaveAsync()
    {
        _status.Text = string.Empty;

        if (_new.Text != _confirm.Text)
        {
            _status.Text = T("Auth_PasswordMismatch");
            _confirm.Focus();
            return;
        }

        var strength = _policy.Validate(_new.Text);
        if (strength.IsFailure)
        {
            _status.Text = Describe(strength);
            _new.Focus();
            return;
        }

        _save.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var result = await _authentication.ChangePasswordAsync(_userId, _current.Text, _new.Text);

            if (result.IsFailure)
            {
                _status.Text = Describe(result);
                return;
            }

            _dialogs.Info(T("Message.Saved"));
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            Cursor = Cursors.Default;
            _save.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window is not a way out of a mandatory change.
        if (_mandatory && DialogResult != DialogResult.OK
            && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _status.Text = T("Login.PasswordExpiredMessage");
            return;
        }

        base.OnFormClosing(e);
    }
}
