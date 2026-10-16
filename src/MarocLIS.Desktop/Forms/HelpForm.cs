using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The help window: the user manual's pages, opened on the section of the screen in use.
/// <para>
/// One window, kept and reused, beside the application rather than over it, so the operator can
/// read and work at once. The pages are local HTML files shown in the WebBrowser control that ships
/// with Windows; it reaches nothing but the help folder, and a web link opens in the default
/// browser. Where the control cannot be created, the shell opens the page in the browser instead.
/// </para>
/// </summary>
public sealed class HelpForm : LocalizedForm
{
    private readonly HelpCatalog _catalog;
    private readonly ICultureManager _cultureManager;

    private readonly Button _back = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _forward = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _home = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _print = UiTheme.SecondaryButton(string.Empty);
    private WebBrowser? _browser;
    private string? _screenTitle;
    private string? _screenKey;

    public HelpForm(ILocalizer localizer, ICultureManager cultureManager, HelpCatalog catalog)
        : base(localizer, cultureManager)
    {
        _catalog = catalog;
        _cultureManager = cultureManager;

        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1000, 750);
        MinimumSize = new Size(560, 400);
        ShowInTaskbar = true;
        KeyPreview = true;

        BuildLayout();
    }

    private void BuildLayout()
    {
        BackColor = UiTheme.SurfaceRaised;

        // A white tool bar with the line of the design system under it, as the shell's header.
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(14, 10, 14, 0),
            BackColor = UiTheme.SurfaceRaised,
            WrapContents = false,
            Tag = UiTags.OwnLayout
        };

        foreach (var (button, glyph) in new[] { (_back, Glyph.ChevronLeft), (_forward, Glyph.ChevronRight),
                                                (_home, Glyph.BookOpen), (_print, Glyph.Printer) })
        {
            AdminLayout.Action(button);
            button.Margin = new Padding(0, 0, 8, 0);
            ButtonIcons.Set(button, glyph);
            bar.Controls.Add(button);
        }

        var line = new Label { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Border };

        _back.Enabled = _forward.Enabled = false;
        _back.Click += (_, _) => _browser?.GoBack();
        _forward.Click += (_, _) => _browser?.GoForward();
        _home.Click += (_, _) => GoHome();
        _print.Click += (_, _) => _browser?.ShowPrintDialog();

        try
        {
            _browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                IsWebBrowserContextMenuEnabled = false,
                AllowWebBrowserDrop = false,
                WebBrowserShortcutsEnabled = true
            };
            _browser.Navigating += OnNavigating;
            _browser.NewWindow += (_, e) => e.Cancel = true;
            _browser.CanGoBackChanged += (_, _) => _back.Enabled = _browser.CanGoBack;
            _browser.CanGoForwardChanged += (_, _) => _forward.Enabled = _browser.CanGoForward;
            Controls.Add(_browser);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or InvalidOperationException or PlatformNotSupportedException)
        {
            // No browser control on this workstation: the pages open in the default browser.
            _browser = null;
        }

        Controls.Add(line);
        Controls.Add(bar);
    }

    /// <summary>Shows a page; false when this window cannot, and the browser should.</summary>
    public bool ShowTopic(HelpTopic topic, string? screenTitle)
    {
        if (_browser is null) return false;

        _screenTitle = screenTitle;
        _screenKey = topic.Anchor;
        ApplyTexts();

        try
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            _browser.Navigate(topic.Uri);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Hide();
            return false;
        }
    }

    private void GoHome()
    {
        if (_catalog.Index(_cultureManager.CurrentLanguage) is { } index) _browser?.Navigate(index.Uri);
    }

    // The viewer stays in the help folder. A web link goes to the default browser, anything else
    // (another folder, a network share, a script) is refused.
    private void OnNavigating(object? sender, WebBrowserNavigatingEventArgs e)
    {
        var url = e.Url;
        if (url is null) return;

        if (url.IsFile && _catalog.Contains(url.LocalPath)) return;
        if (url.Scheme == "about" && url.AbsoluteUri == "about:blank") return;

        e.Cancel = true;
        if (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeMailto)
            HelpService.OpenExternally(url);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    // Closing hides: the window and its history are kept for the next F1.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    protected override void ApplyTexts()
    {
        // On a language change the shell has the screen's title in the new language: the caption
        // kept the old one beside translated buttons.
        if (_screenKey is not null && _screenKey == HelpService.CurrentKey) _screenTitle = HelpService.CurrentTitle;
        Text = string.IsNullOrWhiteSpace(_screenTitle) ? T("Help.Title") : $"{T("Help.Title")} — {_screenTitle}";
        _back.Text = T("Help.Back");
        _forward.Text = T("Help.Forward");
        _home.Text = T("Help.Home");
        _print.Text = T("Help.Print");
    }
}
