using MarocLIS.Desktop.Forms;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Opens the help of the screen the operator is on.
/// <para>
/// Static because F1 has to work in every dialog a screen opens, and those are built in many
/// places that have no reason to be handed a help service. The shell sets the current screen as it
/// navigates and installs <see cref="F1Filter"/>, which answers F1 in any window of the application
/// other than the shell itself: there F1 goes through the shell's keyboard shortcuts, so a user who
/// moved help to another key keeps their choice.
/// </para>
/// </summary>
public static class HelpService
{
    private static ILocalizer? _localizer;
    private static ICultureManager? _cultureManager;
    private static HelpCatalog? _catalog;
    private static HelpForm? _form;

    /// <summary>The navigation key of the screen on show, null before the first one opens.</summary>
    public static string? CurrentKey { get; set; }

    /// <summary>The title of the screen on show, for the help window's caption.</summary>
    public static string? CurrentTitle { get; set; }

    public static void Configure(ILocalizer localizer, ICultureManager cultureManager)
    {
        _localizer = localizer;
        _cultureManager = cultureManager;
    }

    /// <summary>Shows the help of the current screen.</summary>
    public static void ShowCurrent() => Show(CurrentKey, CurrentTitle);

    /// <summary>Shows the section of a screen, or the table of contents when the key is unknown.</summary>
    public static void Show(string? navigationKey, string? screenTitle)
    {
        if (_localizer is null || _cultureManager is null) return;

        _catalog ??= new HelpCatalog(HelpCatalog.DefaultRoot);
        var topic = _catalog.Resolve(navigationKey, _cultureManager.CurrentLanguage);

        if (topic is null)
        {
            var rtl = _cultureManager.CurrentLanguage == Domain.Common.AppLanguage.Arabic;
            MessageBox.Show(_localizer["Help.NotFound"], _localizer["Help.Title"], MessageBoxButtons.OK,
                            MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                            rtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
            return;
        }

        // A help window already open when a dialog was shown is disabled with the rest of the
        // application until the dialog closes: F1 in that dialog opens a fresh one it can use.
        if (_form is { IsDisposed: false, Visible: true, CanFocus: false })
        {
            _form.Dispose();
            _form = null;
        }

        if (_form is null || _form.IsDisposed)
            _form = new HelpForm(_localizer, _cultureManager, _catalog);

        if (!_form.ShowTopic(topic, screenTitle)) OpenExternally(topic.Uri);
    }

    /// <summary>Opens an address in the workstation's default browser.</summary>
    public static void OpenExternally(Uri uri)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered for the address: nothing more can be done from here.
        }
    }

    /// <summary>F1 in any window but the shell (handled by its shortcuts) and the shortcuts list.</summary>
    public sealed class F1Filter : IMessageFilter
    {
        private const int WmKeyDown = 0x0100;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmKeyDown || (Keys)(int)m.WParam != Keys.F1 || Control.ModifierKeys != Keys.None)
                return false;

            // The shortcuts dialog takes F1 as a key to assign; the shell has its own binding.
            if (Form.ActiveForm is null or MainShellForm or HelpForm or ShortcutsDialog) return false;

            ShowCurrent();
            return true;
        }
    }
}
