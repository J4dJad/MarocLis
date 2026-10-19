using MarocLIS.Application.Licensing;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Licensing;
using MarocLIS.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The screen a new installation opens on, in place of the login, while no licence is installed
/// and the database holds no patient: the server's identifier to send the vendor, the request file
/// that carries it, and the import of the licence received in return.
/// <para>
/// Only on an empty database. One that already holds patients and has lost its licence opens on
/// the login as usual, in read-only mode: a laboratory must never be kept from its results.
/// </para>
/// </summary>
public sealed class LicenceImportForm : LocalizedForm
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IUserDialogs _dialogs;

    private const int BandWidth = 300;

    private readonly Panel _band = new();
    private readonly Label _bandText = new();
    private readonly Label _heading = new();
    private readonly Label _intro = new();
    private readonly Label _serverLabel = new();
    private readonly TextBox _serverId = new();
    private readonly Button _copy = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _steps = new();
    private readonly Button _import = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _export = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _quit = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _keyWarning = new();

    public LicenceImportForm(IServiceScopeFactory scopes, ILocalizer localizer, ICultureManager cultureManager,
                             IUserDialogs dialogs, LicenceStatusHolder licence, LicenceTrust trust)
        : base(localizer, cultureManager)
    {
        _scopes = scopes;
        _dialogs = dialogs;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(1000, 470);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.SurfaceRaised;

        BuildLayout();

        _serverId.Text = licence.Current.ServerId ?? string.Empty;

        // Said here as on the licence tab: a build with the development key accepts licences anyone
        // with the source code can sign, and must not be what a laboratory runs.
        _keyWarning.Visible = trust.IsDevelopmentKey;

        _copy.Click += (_, _) => LicenceFileActions.CopyServerId(_serverId.Text, _dialogs, Localizer);
        _export.Click += async (_, _) =>
            await LicenceFileActions.ExportRequestAsync(this, _scopes, _dialogs, Localizer, _export.Text);
        _import.Click += async (_, _) => await ImportAsync();
        _quit.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        AcceptButton = _import;
        CancelButton = _quit;
    }

    private void BuildLayout()
    {
        _band.Bounds = new Rectangle(0, 0, BandWidth, ClientSize.Height);
        _band.BackColor = UiTheme.Accent;
        _band.Tag = UiTags.OwnLayout;

        var title = new Label
        {
            Text = "MarocLIS",
            Font = new Font(UiTheme.PageTitle.FontFamily, 21f, FontStyle.Bold),
            ForeColor = UiTheme.TextOnAccent,
            BackColor = UiTheme.Accent,
            AutoSize = true,
            Location = new Point(28, 40)
        };

        _bandText.Font = UiTheme.Body;
        _bandText.ForeColor = Color.FromArgb(0xD6, 0xEE, 0xF0);
        _bandText.BackColor = UiTheme.Accent;
        _bandText.AutoSize = false;
        _bandText.Bounds = new Rectangle(32, 90, BandWidth - 64, 120);

        _band.Controls.AddRange([title, _bandText]);

        const int left = BandWidth + 36;
        var width = ClientSize.Width - left - 36;

        _heading.Font = UiTheme.PageTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = false;
        _heading.Bounds = new Rectangle(left - 2, 30, width, 32);

        _intro.Font = UiTheme.Body;
        _intro.ForeColor = UiTheme.TextSecondary;
        _intro.AutoSize = false;
        _intro.Bounds = new Rectangle(left, 70, width, 58);

        _serverLabel.Font = UiTheme.SmallBold;
        _serverLabel.ForeColor = UiTheme.TextSecondary;
        _serverLabel.AutoSize = true;
        _serverLabel.Location = new Point(left, 140);

        // Read-only but selectable: the identifier is dictated over the phone as often as copied.
        _serverId.ReadOnly = true;
        _serverId.BorderStyle = BorderStyle.FixedSingle;
        _serverId.BackColor = UiTheme.SurfaceSunken;
        _serverId.Font = UiTheme.Mono(12f, FontStyle.Bold);
        _serverId.Tag = UiTags.LatinValue;
        const int copyWidth = 110;
        _serverId.Bounds = new Rectangle(left, 160, width - copyWidth - 8, 30);
        _copy.Bounds = new Rectangle(left + width - copyWidth, 159, copyWidth, 32);
        ButtonIcons.Set(_copy, Glyph.CopyPlus);

        _steps.Font = UiTheme.Body;
        _steps.ForeColor = UiTheme.TextPrimary;
        _steps.AutoSize = false;
        _steps.Bounds = new Rectangle(left, 206, width, 110);

        _keyWarning.Font = UiTheme.SmallBold;
        _keyWarning.ForeColor = UiTheme.CriticalInk;
        _keyWarning.BackColor = UiTheme.CriticalBackground;
        _keyWarning.AutoSize = false;
        _keyWarning.Padding = new Padding(8, 4, 8, 4);
        _keyWarning.Bounds = new Rectangle(left, 322, width, 44);

        var buttonTop = ClientSize.Height - 36 - 34;
        // Wide enough for each caption with its icon, side by side: "Quitter" at the start, the
        // two file actions at the end.
        const int quitWidth = 130, exportWidth = 226, importWidth = 236;
        _quit.Bounds = new Rectangle(left, buttonTop, quitWidth, 34);
        _import.Bounds = new Rectangle(left + width - importWidth, buttonTop, importWidth, 34);
        _export.Bounds = new Rectangle(_import.Left - 10 - exportWidth, buttonTop, exportWidth, 34);

        Controls.AddRange([_band, _heading, _intro, _serverLabel, _serverId, _copy, _steps, _keyWarning,
                           _quit, _export, _import]);
    }

    protected override void ApplyTexts()
    {
        Text = $"MarocLIS — {T("Licence.ImportScreen.Title")}";
        _bandText.Text = T("Licence.ImportScreen.Band");
        _heading.Text = T("Licence.ImportScreen.Title");
        _intro.Text = T("Licence.ImportScreen.Intro");
        _serverLabel.Text = T("Licence.ServerId");
        _copy.Text = T("Licence.CopyServerId");
        _steps.Text = T("Licence.ImportScreen.Steps");
        _keyWarning.Text = T("Licence.DevelopmentKey");
        _import.Text = T("Licence.Import");
        _export.Text = T("Licence.ExportRequest");
        _quit.Text = T("Licence.Quit");
    }

    private async Task ImportAsync()
    {
        _import.Enabled = false;
        try
        {
            var status = await LicenceFileActions.ImportAsync(this, _scopes, _dialogs, Localizer, _import.Text);
            if (status is null || status.State is not (LicenceState.Valid or LicenceState.Grace)) return;

            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            _import.Enabled = true;
        }
    }
}
