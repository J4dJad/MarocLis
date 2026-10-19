using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The « Langues » tab: which of Arabic and English the laboratory works in, French always. Saved
/// as an installation setting; every language list offers only the languages ticked here, and the
/// services assign no other.
/// </summary>
public sealed partial class SettingsView
{
    private readonly ActiveLanguagesService? _languages;
    private readonly ICultureManager _cultureManager;
    private readonly TabPage _languagesTab = new();
    private readonly Label _languagesHeading = new();
    private readonly Label _languagesIntro = new();
    private readonly CheckBox _languageFrench = new();
    private readonly CheckBox _languageArabic = new();
    private readonly CheckBox _languageEnglish = new();
    private readonly Label _languagesFrenchNote = new();
    private readonly Label _languagesNotice = new();
    private readonly Button _saveLanguages = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _reloadLanguages = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _languagesStatus = new();

    private void BuildLanguagesTab()
    {
        _languagesTab.BackColor = UiTheme.Surface;

        static Label Text(Label label, Font font, Color colour, Rectangle bounds)
        {
            label.Font = font;
            label.ForeColor = colour;
            label.AutoSize = false;
            label.Bounds = bounds;
            return label;
        }

        static CheckBox Option(CheckBox box, int y)
        {
            box.Font = UiTheme.Body;
            box.AutoSize = false;
            box.Bounds = new Rectangle(0, y, 490, 26);
            return box;
        }

        Text(_languagesHeading, UiTheme.BodyBold, UiTheme.TextPrimary, new Rectangle(0, 0, 760, 22));
        Text(_languagesIntro, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(0, 26, 760, 56));

        Option(_languageFrench, 92);
        Option(_languageArabic, 120);
        Option(_languageEnglish, 148);

        // French cannot be switched off: the working language, and the one a record in a language
        // switched off is shown and printed in.
        _languageFrench.Checked = true;
        _languageFrench.Enabled = false;

        Text(_languagesFrenchNote, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(0, 182, 760, 22));
        Text(_languagesNotice, UiTheme.Small, UiTheme.TextSecondary, new Rectangle(0, 212, 760, 56));

        // The same column on the reading side as the « Factures » tab.
        var column = new Panel { Dock = DockStyle.Left, Width = 760, BackColor = UiTheme.SurfaceRaised };
        column.Controls.AddRange(
        [
            _languagesHeading, _languagesIntro, _languageFrench, _languageArabic, _languageEnglish,
            _languagesFrenchNote, _languagesNotice
        ]);
        var leftToRight = column.Controls.Cast<Control>().ToDictionary(c => c, c => c.Left);
        column.RightToLeftChanged += (_, _) =>
        {
            var rtl = column.RightToLeft == RightToLeft.Yes;
            column.Dock = rtl ? DockStyle.Right : DockStyle.Left;
            foreach (var (control, left) in leftToRight)
                control.Left = rtl ? column.Width - left - control.Width : left;
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised, AutoScroll = true };
        body.Controls.Add(column);

        var card = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.SurfaceRaised,
                               Padding = new Padding(20, 16, 2, 1) };
        card.Controls.Add(body);
        card.Controls.Add(ActionBar(_languagesStatus, _saveLanguages, _reloadLanguages));
        UiChrome.AsCard(card);

        _languagesTab.Controls.Add(card);

        _saveLanguages.Click += async (_, _) => await SaveLanguagesAsync();
        _reloadLanguages.Click += async (_, _) => await LoadLanguagesAsync();

        // Read by anyone who may open the screen, changed only with the right to manage settings;
        // the service refuses the others in any case.
        PermissionGate.Require(Permissions.Administration.ManageSettings, _saveLanguages, _languageArabic,
                               _languageEnglish);
    }

    private void ApplyLanguagesTexts()
    {
        _languagesTab.Text = T("Languages.SettingsTab");
        _languagesHeading.Text = T("Languages.Heading");
        _languagesIntro.Text = T("Languages.Intro");
        _languageFrench.Text = T("AppLanguage.French");
        _languageArabic.Text = T("AppLanguage.Arabic");
        _languageEnglish.Text = T("AppLanguage.English");
        _languagesFrenchNote.Text = T("Languages.FrenchAlways");
        _languagesNotice.Text = T("Languages.Notice");
        _saveLanguages.Text = T("Action.Save");
        _reloadLanguages.Text = T("Action.Refresh");
    }

    private async Task LoadLanguagesAsync(CancellationToken ct = default)
    {
        if (_languages is null) return;

        ShowLanguages(await _languages.GetAsync(ct));
        _languagesStatus.Text = string.Empty;
    }

    private void ShowLanguages(LanguageSettings settings)
    {
        _languageArabic.Checked = settings.IsActive(AppLanguage.Arabic);
        _languageEnglish.Checked = settings.IsActive(AppLanguage.English);

        // Arabic is a module of the licence: without it the box stays off, and says why.
        var arabicLicensed = _licence?.Current.HasModule(LicenceModules.Arabic) ?? true;
        _languageArabic.Enabled = arabicLicensed;
        _languageArabic.Text = arabicLicensed
            ? T("AppLanguage.Arabic")
            : $"{T("AppLanguage.Arabic")} — {T("Licence.ModuleNotLicensed")}";
    }

    private async Task SaveLanguagesAsync()
    {
        if (_languages is null) return;

        List<AppLanguage> active = [AppLanguage.French];
        if (_languageArabic.Checked) active.Add(AppLanguage.Arabic);
        if (_languageEnglish.Checked) active.Add(AppLanguage.English);

        var saved = await _languages.SaveAsync(active);

        if (saved.IsFailure)
        {
            _dialogs.ShowFailure(saved, T("Languages.SettingsTab"));
            return;
        }

        ShowLanguages(_languages.Current);
        _languagesStatus.ForeColor = UiTheme.Normal;
        _languagesStatus.Text = T("Languages.Saved");

        // The interface in a language just switched off goes back to French, which refills the
        // header's list as well.
        var language = LanguageChoices.Coerce(_cultureManager.CurrentLanguage);
        if (language != _cultureManager.CurrentLanguage) _cultureManager.SetLanguage(language);
    }
}
