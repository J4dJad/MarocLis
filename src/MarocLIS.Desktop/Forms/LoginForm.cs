using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
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
/// <para>
/// Laid out as the direction A mockup: the laboratory's mark, name and workstation on a teal band
/// at the start, the form at the end, with 36 px fields, the password's « Afficher » button and its
/// Caps Lock warning on a line of their own.
/// </para>
/// </summary>
public sealed class LoginForm : LocalizedForm
{
    private readonly IServiceProvider _services;
    private readonly ICultureManager _cultureManager;
    private readonly IUserDialogs _dialogs;

    private const int BandWidth = 340;
    private const int FieldHeight = 36;

    private readonly Panel _band = new();
    private readonly PictureBox _logo = new();
    private readonly Label _subtitle = new();
    private readonly Label _workstation = new();
    private readonly Label _heading = new();
    private readonly Label _prompt = new();
    private readonly Label _userNameLabel = new();
    private readonly Label _passwordLabel = new();
    private readonly Label _siteLabel = new();
    private readonly Label _languageLabel = new();

    private readonly TextBox _userName = new();
    private readonly TextBox _password = new();
    private readonly Button _showPassword = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _capsLock = new();
    private readonly ComboBox _site = new();
    private readonly ComboBox _language = new();
    private readonly CheckBox _rememberUser = new();
    private readonly Button _signIn = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _status = new();
    private readonly Label _forgotten = new();

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
        ClientSize = new Size(880, 508);
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

        // « Afficher »: the password in clear while the operator checks what they typed.
        _showPassword.Click += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_password.UseSystemPasswordChar;
            _showPassword.Text = T(_password.UseSystemPasswordChar ? "Login.ShowPassword" : "Login.HidePassword");
            _password.Focus();
        };

        _language.SelectedIndexChanged += OnLanguageSelected;
        _site.SelectedIndexChanged += (_, _) => ShowLogo();
    }

    private void BuildLayout()
    {
        // --- the band: mark, name, what the application is, workstation and version ------------------

        _band.Bounds = new Rectangle(0, 0, BandWidth, ClientSize.Height);
        _band.BackColor = UiTheme.Accent;
        _band.Tag = UiTags.OwnLayout;

        _logo.Bounds = new Rectangle(32, 36, 56, 56);
        _logo.SizeMode = PictureBoxSizeMode.Normal;
        _logo.BackColor = UiTheme.Accent;
        _logo.Image = Mark(null, 56);

        var title = new Label
        {
            Text = "MarocLIS",
            Font = new Font(UiTheme.PageTitle.FontFamily, 21f, FontStyle.Bold),
            ForeColor = UiTheme.TextOnAccent,
            BackColor = UiTheme.Accent,
            AutoSize = true,
            Location = new Point(28, 106)
        };

        var bandText = Color.FromArgb(0xD6, 0xEE, 0xF0);

        _subtitle.Font = EntryFont(10.5f);
        _subtitle.ForeColor = bandText;
        _subtitle.BackColor = UiTheme.Accent;
        _subtitle.AutoSize = false;
        _subtitle.Bounds = new Rectangle(32, 152, BandWidth - 64, 66);

        _workstation.Font = UiTheme.Small;
        _workstation.ForeColor = bandText;
        _workstation.BackColor = UiTheme.Accent;
        _workstation.AutoSize = false;
        _workstation.Bounds = new Rectangle(32, ClientSize.Height - 36 - 36, BandWidth - 64, 36);
        _workstation.TextAlign = ContentAlignment.BottomLeft;

        _band.Controls.AddRange([_logo, title, _subtitle, _workstation]);

        // --- the form ------------------------------------------------------------------------------

        // Every field, the tick box and the button share one start edge and one width, so their far
        // edges line up as well.
        const int left = BandWidth + 40;
        var width = ClientSize.Width - left - 40;

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(left - 2, 32);

        _prompt.Font = UiTheme.Body;
        _prompt.ForeColor = UiTheme.TextSecondary;
        _prompt.AutoSize = true;
        _prompt.Location = new Point(left, 68);

        // Sized to their text: a label built in code is 100 x 23 by default, which clips a longer
        // caption and lets its blank lower edge paint over the top border of the field below.
        foreach (var label in new[] { _userNameLabel, _passwordLabel, _siteLabel, _languageLabel })
        {
            label.AutoSize = true;
            label.Font = UiTheme.SmallBold;
            label.ForeColor = UiTheme.TextSecondary;
        }

        var y = 100;
        _userNameLabel.Location = new Point(left, y);
        var userHost = FieldHost(_userName, new Rectangle(left, y + 18, width, FieldHeight));

        y += 68;
        _passwordLabel.Location = new Point(left, y);
        const int showWidth = 96;
        var passwordHost = FieldHost(_password, new Rectangle(left, y + 18, width - showWidth - 6, FieldHeight));
        _showPassword.Bounds = new Rectangle(left + width - showWidth, y + 18, showWidth, FieldHeight);
        _showPassword.Tag = UiTags.NoMnemonic;

        // Its own line under the field, in the warning colour, only while Caps Lock is on.
        _capsLock.Font = UiTheme.Small;
        _capsLock.ForeColor = UiTheme.Abnormal;
        _capsLock.AutoSize = false;
        _capsLock.Bounds = new Rectangle(left, y + 18 + FieldHeight + 3, width, 16);
        _capsLock.Visible = false;

        y += 92;
        var siteWidth = (width - 12) * 2 / 3;
        _siteLabel.Location = new Point(left, y);
        _languageLabel.Location = new Point(left + siteWidth + 12, y);
        StyleCombo(_site, new Rectangle(left, y + 18, siteWidth, FieldHeight));
        StyleCombo(_language, new Rectangle(left + siteWidth + 12, y + 18, width - siteWidth - 12, FieldHeight));

        // A flat drop-down list draws no border on Windows until it has the focus, so it looked
        // narrower and shifted next to the boxes above it; these are drawn by the form, framed like
        // the fields.

        y += 68;
        _rememberUser.Bounds = new Rectangle(left, y, width, 22);
        _rememberUser.Font = UiTheme.Body;
        _rememberUser.ForeColor = UiTheme.TextPrimary;

        y += 34;
        _signIn.Bounds = new Rectangle(left, y, width, 40);
        PatientPaint.KeyHint(_signIn, () => KeyboardShortcuts.Format(Keys.Enter, Localizer));

        _status.Bounds = new Rectangle(left, y + 46, width, 32);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.Critical;
        _status.AutoSize = false;

        _forgotten.Font = UiTheme.Small;
        _forgotten.ForeColor = UiTheme.TextSecondary;
        _forgotten.AutoSize = false;
        _forgotten.Bounds = new Rectangle(left, y + 80, width, 18);

        Controls.AddRange([_band, _heading, _prompt, _userNameLabel, userHost, _passwordLabel, passwordHost,
                           _showPassword, _capsLock, _siteLabel, _site, _languageLabel, _language,
                           _rememberUser, _signIn, _status, _forgotten]);
    }

    private static Font EntryFont(float size) => new(UiTheme.Body.FontFamily, size);

    /// <summary>
    /// A 36 px field: a borderless box in a host that draws the line-strong frame with rounded
    /// corners, in the accent and two pixels thick while the box has the focus.
    /// </summary>
    private static Panel FieldHost(TextBox box, Rectangle bounds)
    {
        var host = new Panel { Bounds = bounds, BackColor = UiTheme.SurfaceRaised, Tag = UiTags.OwnLayout };
        box.BorderStyle = BorderStyle.None;
        box.Font = UiTheme.Body;
        box.BackColor = UiTheme.SurfaceRaised;
        box.ForeColor = UiTheme.TextPrimary;
        box.Bounds = new Rectangle(12, (bounds.Height - box.PreferredHeight) / 2 + 1, bounds.Width - 24,
                                   box.PreferredHeight);
        host.Controls.Add(box);

        host.Paint += (_, e) => PaintFrame(e.Graphics, host, box.Focused);
        host.Click += (_, _) => box.Focus();
        box.GotFocus += (_, _) => host.Invalidate();
        box.LostFocus += (_, _) => host.Invalidate();
        return host;
    }

    private static void PaintFrame(Graphics graphics, Control control, bool focused)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(control.Parent?.BackColor ?? UiTheme.SurfaceRaised))
            graphics.FillRectangle(back, control.ClientRectangle);

        var inset = focused ? 1 : 0;
        using var path = UiChrome.RoundedRectangle(
            new Rectangle(inset, inset, control.Width - 1 - 2 * inset, control.Height - 1 - 2 * inset),
            UiTheme.RadiusControl);
        using (var fill = new SolidBrush(UiTheme.SurfaceRaised)) graphics.FillPath(fill, path);
        using var pen = new Pen(focused ? UiTheme.Accent : UiTheme.BorderStrong, focused ? 2 : 1);
        graphics.DrawPath(pen, path);
    }

    /// <summary>A drop-down list as tall as the fields, its items drawn by the form.</summary>
    private static void StyleCombo(ComboBox combo, Rectangle bounds)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Standard;
        combo.Font = UiTheme.Body;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = FieldHeight - 6;
        combo.Bounds = bounds;
        combo.DrawItem += (_, e) =>
        {
            e.DrawBackground();
            if (e.Index < 0) return;

            var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                        | TextFormatFlags.NoPrefix;
            if (combo.RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.Right | TextFormatFlags.RightToLeft;

            var text = combo.GetItemText(combo.Items[e.Index]);
            var area = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, combo.Font, area,
                                  selected ? SystemColors.HighlightText : UiTheme.TextPrimary, flags);
        };
    }

    protected override void ApplyTexts()
    {
        Text = T("Login.Title");
        _heading.Text = T("Login.Title");
        _prompt.Text = T("Login.Prompt");
        _subtitle.Text = T("Login.Subtitle");
        _workstation.Text = $"{T("App.Workstation")} : {Environment.MachineName}{Environment.NewLine}"
                            + $"{T("App.Version")} {Version()}";
        _showPassword.Text = T(_password.UseSystemPasswordChar ? "Login.ShowPassword" : "Login.HidePassword");
        _capsLock.Text = "! " + T("Login.CapsLockOn");
        _forgotten.Text = T("Login.ForgotPassword");
        _userNameLabel.Text = T("Login.UserName");
        _passwordLabel.Text = T("Login.Password");
        _siteLabel.Text = T("Login.Site");
        _languageLabel.Text = T("App.Language");
        _rememberUser.Text = T("Login.RememberUser");
        _signIn.Text = T("Login.SignIn");

        // The language list shows each language in its own language — an Arabic speaker looks for
        // "العربية", not for "Arabe" — and only those the laboratory works in, chosen by value.
        LanguageChoices.Fill(_language, LanguageChoices.OwnName, _cultureManager.CurrentLanguage);

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
        using var logo = site?.LogoPng is { Length: > 0 } png ? LogoImage.Load(png) : null;

        var previous = _logo.Image;
        _logo.Image = Mark(logo, _logo.Width);
        previous?.Dispose();
    }

    /// <summary>
    /// A white rounded square on the band, holding the site's logo, or the application's flask in
    /// the accent colour when the site has none.
    /// </summary>
    private static Bitmap Mark(Image? logo, int size)
    {
        var mark = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(mark);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.Clear(UiTheme.Accent);

        using (var path = UiChrome.RoundedRectangle(new Rectangle(0, 0, size - 1, size - 1), size * 12 / 56))
        using (var fill = new SolidBrush(UiTheme.SurfaceRaised))
            graphics.FillPath(fill, path);

        if (logo is not null)
        {
            // Zoomed into the square, its proportions kept.
            var room = size - 12;
            var scale = Math.Min(room / (float)logo.Width, room / (float)logo.Height);
            var w = (int)(logo.Width * scale);
            var h = (int)(logo.Height * scale);
            graphics.DrawImage(logo, (size - w) / 2, (size - h) / 2, w, h);
            return mark;
        }

        var glyphSize = size * 15 / 28;
        if (Icons.Render(Glyph.FlaskConical, UiTheme.Accent, glyphSize) is { } glyph)
            graphics.DrawImage(glyph, (size - glyph.Width) / 2, (size - glyph.Height) / 2);

        return mark;
    }

    private static string Version()
    {
        var version = typeof(LoginForm).Assembly.GetName().Version;
        return version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void OnLanguageSelected(object? sender, EventArgs e)
    {
        // Nothing selected while the list is refilled: that is not a choice.
        if (_language.SelectedIndex < 0) return;

        var language = LanguageChoices.Selected(_language);
        if (language != _cultureManager.CurrentLanguage) _cultureManager.SetLanguage(language);
    }

    private void ShowCapsLock() => _capsLock.Visible = IsKeyLocked(Keys.CapsLock);

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

            // The signed-in user's own language preference now takes over from the login screen's —
            // unless the laboratory has switched it off since, when the interface stays in French.
            var language = LanguageChoices.Coerce(result.Value.Language);
            if (language != _cultureManager.CurrentLanguage)
                _cultureManager.SetLanguage(language);

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
