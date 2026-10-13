using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Organization;
using MarocLIS.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// Sign-in.
/// <para>
/// The language selector is on this screen deliberately: an operator whose Windows locale is French
/// may still need the Arabic interface, and making them sign in before they can read the interface
/// is the wrong order. Changing it here applies immediately, before authentication.
/// </para>
/// </summary>
public sealed class LoginForm : LocalizedForm
{
    private readonly IServiceProvider _services;
    private readonly ICultureManager _cultureManager;
    private readonly IUserDialogs _dialogs;

    private readonly Label _subtitle = new();
    private readonly Label _userNameLabel = new();
    private readonly Label _passwordLabel = new();
    private readonly Label _siteLabel = new();
    private readonly Label _languageLabel = new();

    private readonly TextBox _userName = UiTheme.Input(280);
    private readonly TextBox _password = UiTheme.Input(280);
    private readonly ComboBox _site = new();
    private readonly ComboBox _language = new();
    private readonly CheckBox _rememberUser = new();
    private readonly Button _signIn = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<LabSite> _sites = [];

    public LoginForm(IServiceProvider services, ILocalizer localizer,
                     ICultureManager cultureManager, IUserDialogs dialogs)
        : base(localizer, cultureManager)
    {
        _services = services;
        _cultureManager = cultureManager;
        _dialogs = dialogs;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(440, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.SurfaceRaised;

        _password.UseSystemPasswordChar = true;
        // The user name is a Latin identifier; it stays left-to-right even in the Arabic interface.
        _userName.Tag = UiTags.LatinValue;
        _password.Tag = UiTags.LatinValue;

        BuildLayout();

        _signIn.Click += async (_, _) => await SignInAsync();
        AcceptButton = _signIn;

        _language.SelectedIndexChanged += OnLanguageSelected;
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "MarocLIS",
            Font = UiTheme.PageTitle,
            ForeColor = UiTheme.Accent,
            AutoSize = true,
            Location = new Point(28, 26)
        };

        _subtitle.Font = UiTheme.Small;
        _subtitle.ForeColor = UiTheme.TextSecondary;
        _subtitle.AutoSize = false;
        _subtitle.Bounds = new Rectangle(28, 58, 384, 32);

        var y = 104;

        _userNameLabel.Location = new Point(28, y);
        _userName.Location = new Point(28, y + 18);

        y += 54;
        _passwordLabel.Location = new Point(28, y);
        _password.Location = new Point(28, y + 18);

        y += 54;
        _siteLabel.Location = new Point(28, y);
        _site.Bounds = new Rectangle(28, y + 18, 280, UiTheme.InputHeight);
        _site.DropDownStyle = ComboBoxStyle.DropDownList;
        _site.Font = UiTheme.Body;
        _site.FlatStyle = FlatStyle.Flat;

        y += 54;
        _languageLabel.Location = new Point(28, y);
        _language.Bounds = new Rectangle(28, y + 18, 280, UiTheme.InputHeight);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Font = UiTheme.Body;
        _language.FlatStyle = FlatStyle.Flat;

        y += 52;
        _rememberUser.Bounds = new Rectangle(28, y, 280, 22);
        _rememberUser.Font = UiTheme.Small;
        _rememberUser.ForeColor = UiTheme.TextSecondary;

        y += 30;
        _signIn.Bounds = new Rectangle(28, y, 280, UiTheme.ButtonHeight);

        _status.Bounds = new Rectangle(28, y + 38, 384, 32);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.Critical;
        _status.AutoSize = false;

        Controls.AddRange([title, _subtitle, _userNameLabel, _userName, _passwordLabel, _password,
                           _siteLabel, _site, _languageLabel, _language, _rememberUser,
                           _signIn, _status]);
    }

    protected override void ApplyTexts()
    {
        Text = T("Login.Title");
        _subtitle.Text = T("Login.Subtitle");
        _userNameLabel.Text = T("Login.UserName");
        _passwordLabel.Text = T("Login.Password");
        _siteLabel.Text = T("Login.Site");
        _languageLabel.Text = T("App.Language");
        _rememberUser.Text = T("Login.RememberUser");
        _signIn.Text = T("Login.SignIn");

        // The language list shows each language in its own language — an Arabic speaker looks for
        // "العربية", not for "Arabe".
        var selected = _language.SelectedIndex;
        _language.BeginUpdate();
        _language.Items.Clear();
        _language.Items.AddRange(["Français", "العربية", "English"]);
        _language.SelectedIndex = selected >= 0 ? selected : (int)_cultureManager.CurrentLanguage;
        _language.EndUpdate();

        RefreshSiteList();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        await LoadSitesAsync();

        var remembered = Properties.LoginSettings.LastUserName;
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            _userName.Text = remembered;
            _rememberUser.Checked = true;
            _password.Focus();
        }
        else
        {
            _userName.Focus();
        }
    }

    private async Task LoadSitesAsync()
    {
        try
        {
            // Pre-authentication the site query filter is inert, so every active site is visible —
            // which is correct here: the operator has to pick one before they have any scope.
            using var scope = _services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<LabSite>>();

            _sites = await repository.ListAsync(s => s.IsActive);
            RefreshSiteList();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void RefreshSiteList()
    {
        if (_sites.Count == 0) return;

        var selectedId = _site.SelectedItem is SiteChoice current ? current.Id : (Guid?)null;

        _site.BeginUpdate();
        _site.Items.Clear();

        foreach (var site in _sites.OrderBy(s => s.Code, StringComparer.Ordinal))
            _site.Items.Add(new SiteChoice(site.Id,
                                           $"{site.Code} — {site.Name.For(_cultureManager.CurrentLanguage)}"));

        _site.SelectedIndex = 0;

        if (selectedId is { } id)
        {
            for (var i = 0; i < _site.Items.Count; i++)
                if (_site.Items[i] is SiteChoice choice && choice.Id == id) _site.SelectedIndex = i;
        }

        _site.EndUpdate();
    }

    private void OnLanguageSelected(object? sender, EventArgs e)
    {
        var language = _language.SelectedIndex switch
        {
            1 => AppLanguage.Arabic,
            2 => AppLanguage.English,
            _ => AppLanguage.French
        };

        if (language != _cultureManager.CurrentLanguage) _cultureManager.SetLanguage(language);
    }

    private async Task SignInAsync()
    {
        _status.Text = string.Empty;
        _signIn.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var siteId = _site.SelectedItem is SiteChoice choice ? choice.Id : (Guid?)null;

            // The sign-in scope is separate and short-lived; the session it establishes lives on
            // the singleton CurrentUserAccessor, not in this scope.
            using var scope = _services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

            var result = await auth.SignInAsync(_userName.Text.Trim(), _password.Text, siteId);

            if (result.IsFailure)
            {
                _status.Text = Localizer.Describe(result.Errors);
                _password.Clear();
                _password.Focus();
                return;
            }

            Properties.LoginSettings.LastUserName = _rememberUser.Checked ? _userName.Text.Trim() : null;

            // The signed-in user's own language preference now takes over from the login screen's.
            if (result.Value.Language != _cultureManager.CurrentLanguage)
                _cultureManager.SetLanguage(result.Value.Language);

            if (result.Value.MustChangePassword && !await ForcePasswordChangeAsync(result.Value.UserId))
            {
                // They declined: the session is not usable, so it is ended rather than left open.
                await auth.SignOutAsync();
                _status.Text = T("Login.PasswordExpiredMessage");
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            Cursor = Cursors.Default;
            _signIn.Enabled = true;
        }
    }

    /// <summary>
    /// Forces a password change before the shell opens. The seeded administrator and every
    /// administrative reset land here, so a shared initial credential can never be used for real work.
    /// </summary>
    private async Task<bool> ForcePasswordChangeAsync(Guid userId)
    {
        _dialogs.Info(T("Login.PasswordExpiredMessage"), T("Login.PasswordExpiredTitle"));

        using var scope = _services.CreateScope();
        var form = scope.ServiceProvider.GetRequiredService<ChangePasswordForm>();
        form.Configure(userId, currentPasswordKnown: _password.Text, mandatory: true);

        return await Task.FromResult(form.ShowDialog(this) == DialogResult.OK);
    }

    private sealed record SiteChoice(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }
}
