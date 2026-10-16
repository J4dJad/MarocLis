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

    private readonly PictureBox _logo = new();
    private readonly Label _subtitle = new();
    private readonly Label _userNameLabel = new();
    private readonly Label _passwordLabel = new();
    private readonly Label _siteLabel = new();
    private readonly Label _languageLabel = new();

    private readonly TextBox _userName = UiTheme.Input();
    private readonly TextBox _password = UiTheme.Input();
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

        // A password typed with Caps Lock on is the commonest "my password is refused" at a shared
        // counter; saying so before the attempt saves a lockout.
        _password.KeyUp += (_, _) => ShowCapsLock();
        _password.Enter += (_, _) => ShowCapsLock();
        AcceptButton = _signIn;

        _language.SelectedIndexChanged += OnLanguageSelected;
        _site.SelectedIndexChanged += (_, _) => ShowLogo();
    }

    private void BuildLayout()
    {
        // Every field, the tick box and the button share one left edge and one width, the full
        // width of the window less its margins, so their right edges line up as well.
        const int left = 28;
        var width = ClientSize.Width - 2 * left;

        _logo.Bounds = new Rectangle(left, 22, 56, 56);
        _logo.SizeMode = PictureBoxSizeMode.Zoom;
        _logo.Image = AppMark(56);

        var title = new Label
        {
            Text = "MarocLIS",
            Font = UiTheme.PageTitle,
            ForeColor = UiTheme.Accent,
            AutoSize = true,
            Location = new Point(left + 68, 22)
        };

        _subtitle.Font = UiTheme.Small;
        _subtitle.ForeColor = UiTheme.TextSecondary;
        _subtitle.AutoSize = false;
        _subtitle.Bounds = new Rectangle(left + 68, 54, width - 68, 32);

        var y = 104;

        // Sized to their text: a label built in code is 100 x 23 by default, which clips a longer
        // caption and lets its blank lower edge paint over the top border of the field below.
        foreach (var label in new[] { _userNameLabel, _passwordLabel, _siteLabel, _languageLabel })
            label.AutoSize = true;

        _userNameLabel.Location = new Point(left, y);
        _userName.Bounds = new Rectangle(left, y + 18, width, UiTheme.InputHeight);

        y += 54;
        _passwordLabel.Location = new Point(left, y);
        _password.Bounds = new Rectangle(left, y + 18, width, UiTheme.InputHeight);

        y += 54;
        _siteLabel.Location = new Point(left, y);
        _site.Bounds = new Rectangle(left, y + 18, width, UiTheme.InputHeight);
        _site.DropDownStyle = ComboBoxStyle.DropDownList;
        _site.Font = UiTheme.Body;
        _site.FlatStyle = FlatStyle.Standard;

        y += 54;
        _languageLabel.Location = new Point(left, y);
        _language.Bounds = new Rectangle(left, y + 18, width, UiTheme.InputHeight);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Font = UiTheme.Body;
        _language.FlatStyle = FlatStyle.Standard;

        // A flat drop-down list draws no border on Windows until it has the focus, so it looked
        // narrower and shifted next to the boxes above it; the standard one is framed like them.

        y += 52;
        _rememberUser.Bounds = new Rectangle(left, y, width, 22);
        _rememberUser.Font = UiTheme.Small;
        _rememberUser.ForeColor = UiTheme.TextSecondary;

        y += 30;
        _signIn.Bounds = new Rectangle(left, y, width, UiTheme.ButtonHeight);

        _status.Bounds = new Rectangle(left, y + 38, width, 32);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.Critical;
        _status.AutoSize = false;

        Controls.AddRange([_logo, title, _subtitle, _userNameLabel, _userName, _passwordLabel, _password,
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
            // which is correct here: the operator has to pick one before they have any scope. A
            // partner laboratory is not offered: samples are sent there, nobody works there.
            using var scope = _services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<LabSite>>();

            _sites = await repository.ListAsync(s => s.IsActive && s.Kind != LabSiteKind.ReferralPartner);
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

    // The selected site's own logo, as set in the settings; the application's mark when it has none.
    private void ShowLogo()
    {
        var site = _site.SelectedItem is SiteChoice choice ? _sites.FirstOrDefault(s => s.Id == choice.Id) : null;
        var logo = site?.LogoPng is { Length: > 0 } png ? LogoImage.Load(png) : null;

        var previous = _logo.Image;
        _logo.Image = logo ?? AppMark(_logo.Width);
        previous?.Dispose();
    }

    // A flask on the accent colour, in a rounded square.
    private static Bitmap AppMark(int size)
    {
        var mark = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(mark);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var radius = size / 4;
        path.AddArc(0, 0, 2 * radius, 2 * radius, 180, 90);
        path.AddArc(size - 1 - 2 * radius, 0, 2 * radius, 2 * radius, 270, 90);
        path.AddArc(size - 1 - 2 * radius, size - 1 - 2 * radius, 2 * radius, 2 * radius, 0, 90);
        path.AddArc(0, size - 1 - 2 * radius, 2 * radius, 2 * radius, 90, 90);
        path.CloseFigure();

        using var fill = new SolidBrush(UiTheme.Accent);
        graphics.FillPath(fill, path);

        var glyphSize = size * 5 / 8;
        if (Icons.Render(Glyph.FlaskConical, UiTheme.TextOnAccent, glyphSize) is { } glyph)
            graphics.DrawImage(glyph, (size - glyph.Width) / 2, (size - glyph.Height) / 2);

        return mark;
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

    private void ShowCapsLock()
    {
        var on = IsKeyLocked(Keys.CapsLock);
        var warning = T("Login.CapsLockOn");

        if (on) _status.Text = warning;
        else if (_status.Text == warning) _status.Text = string.Empty;
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
