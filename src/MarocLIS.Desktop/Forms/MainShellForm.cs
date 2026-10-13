using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;

using MarocLIS.Reporting;

namespace MarocLIS.Desktop.Forms;

/// <summary>
/// The application shell: header, permission-driven sidebar, and the panel views are hosted in.
/// <para>
/// The sidebar is built from the signed-in user's permissions, so a phlebotomist simply does not
/// see a billing entry. That is a usability measure and not the security control — the control is
/// the application-layer <see cref="IPermissionGuard"/>, which refuses the operation even if the
/// screen were reached another way. Hiding a button an auditor can still click is theatre; hiding
/// it *and* refusing the call is defence in depth.
/// </para>
/// </summary>
public sealed class MainShellForm : LocalizedForm
{
    private readonly ICurrentUser _currentUser;
    private readonly INavigationService _navigation;
    private readonly ICultureManager _cultureManager;
    private readonly IUserDialogs _dialogs;

    private readonly Panel _sidebar = new();
    private readonly Panel _header = new();
    private readonly Panel _content = new();
    private readonly Label _viewTitle = new();
    private readonly Label _userInfo = new();
    private readonly Label _siteInfo = new();
    private readonly ComboBox _languagePicker = new();
    private readonly Button _print = UiTheme.SecondaryButton(string.Empty);
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _documents;
    private readonly Label _clock = new();
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _idleTimer = new();

    private readonly List<NavigationEntry> _entries = [];
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.Ordinal);
    private string _activeKey = string.Empty;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;

    public MainShellForm(ICurrentUser currentUser, INavigationService navigation,
                         ILocalizer localizer, ICultureManager cultureManager,
                         IUserDialogs dialogs, PasswordPolicy policy,
                         IListPdfRenderer lists, IDocumentOutput documents)
        : base(localizer, cultureManager)
    {
        _lists = lists;
        _documents = documents;
        _currentUser = currentUser;
        _navigation = navigation;
        _cultureManager = cultureManager;
        _dialogs = dialogs;

        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildLayout();
        BuildNavigation();

        _navigation.Attach(_content, view => _viewTitle.Text = view.Title);

        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        // Idle lock: a shared laboratory counter left unattended with a session open exposes every
        // patient file on the site. The interval comes from the password policy so the security
        // procedure and the software agree on one number.
        _idleTimer.Interval = (int)Math.Max(TimeSpan.FromMinutes(1).TotalMilliseconds,
                                            policy.IdleTimeout.TotalMilliseconds / 4);
        _idleTimer.Tick += (_, _) => CheckIdle(policy.IdleTimeout);
        _idleTimer.Start();

        _languagePicker.SelectedIndexChanged += OnLanguagePicked;
    }

    // ---- Layout -----------------------------------------------------------------------------------

    private void BuildLayout()
    {
        _header.Dock = DockStyle.Top;
        _header.Height = 52;
        _header.BackColor = UiTheme.Accent;
        _header.Padding = new Padding(UiTheme.Gutter, 0, UiTheme.Gutter, 0);

        var brand = new Label
        {
            Text = "MarocLIS",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = UiTheme.TextOnAccent,
            AutoSize = true,
            Location = new Point(UiTheme.Gutter, 14)
        };

        _viewTitle.Font = UiTheme.SectionTitle;
        _viewTitle.ForeColor = UiTheme.TextOnAccent;
        _viewTitle.AutoSize = true;
        _viewTitle.Location = new Point(140, 16);

        _userInfo.Font = UiTheme.Small;
        _userInfo.ForeColor = UiTheme.TextOnAccent;
        _userInfo.AutoSize = true;
        _userInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _siteInfo.Font = UiTheme.Small;
        _siteInfo.ForeColor = Color.FromArgb(0xCC, 0xDD, 0xEE);
        _siteInfo.AutoSize = true;
        _siteInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _clock.Font = UiTheme.Small;
        _clock.ForeColor = Color.FromArgb(0xCC, 0xDD, 0xEE);
        _clock.AutoSize = true;
        _clock.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _languagePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _languagePicker.FlatStyle = FlatStyle.Flat;
        _languagePicker.Font = UiTheme.Small;
        _languagePicker.Width = 104;
        _languagePicker.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        // Printing lives in the header, beside the language, so it is in the same place on every
        // screen: it prints whatever list the current screen is showing.
        _print.Width = 160;
        _print.Height = 28;
        _print.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _print.Click += async (_, _) => await PrintCurrentListAsync();
        ButtonIcons.Set(_print, Glyph.Printer);

        _header.Controls.AddRange([brand, _viewTitle, _userInfo, _siteInfo, _clock, _print,
                                   _languagePicker]);
        _header.Resize += (_, _) => PositionHeaderItems();

        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = UiTheme.SidebarWidth;
        _sidebar.BackColor = UiTheme.SurfaceRaised;
        _sidebar.Padding = new Padding(0, UiTheme.GutterTight, 0, 0);
        _sidebar.AutoScroll = true;

        _content.Dock = DockStyle.Fill;
        _content.BackColor = UiTheme.Surface;
        _content.Padding = new Padding(0);

        // Content before sidebar before header, so docking resolves in the right order.
        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(_header);
    }

    private void PositionHeaderItems()
    {
        var right = _header.ClientSize.Width - UiTheme.Gutter;

        _languagePicker.Location = new Point(right - _languagePicker.Width, 14);
        right -= _languagePicker.Width + UiTheme.Gutter;

        _print.Location = new Point(right - _print.Width, 12);
        right -= _print.Width + UiTheme.Gutter;

        _clock.Location = new Point(right - _clock.Width, 18);
        right -= _clock.Width + UiTheme.Gutter * 2;

        _userInfo.Location = new Point(right - _userInfo.Width, 8);
        _siteInfo.Location = new Point(right - _siteInfo.Width, 26);
    }

    // ---- Navigation -------------------------------------------------------------------------------

    private sealed record NavigationEntry(
        string Key,
        string LabelKey,
        string[] RequiredPermissions,
        Func<INavigationService, Task> Navigate,
        bool IsGroupHeader = false);

    private void BuildNavigation()
    {
        _entries.Clear();

        _entries.AddRange(
        [
            new("dashboard", "Nav.Dashboard", [],
                n => n.NavigateToAsync<DashboardView>()),

            new("statistics", "Nav.Statistics", [Permissions.Statistics.View],
                n => n.NavigateToAsync<StatisticsView>()),

            new("group.reception", "Nav.Reception", [Permissions.Patients.View],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("patients", "Nav.Patients", [Permissions.Patients.View],
                n => n.NavigateToAsync<PatientSearchView>()),

            new("orders", "Nav.Orders", [Permissions.Orders.Create],
                n => n.NavigateToAsync<OrderRegistrationView>()),

            new("orderdetail", "Nav.OrderFile", [Permissions.Orders.View],
                n => n.NavigateToAsync<OrderDetailView>()),

            new("group.technical", "Nav.Worklist", [Permissions.Results.View],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("collection", "Nav.Collection", [Permissions.Samples.Collect],
                n => n.NavigateToAsync<CollectionView>()),

            new("reception", "Nav.SampleReception", [Permissions.Samples.Receive],
                n => n.NavigateToAsync<SampleReceptionView>()),

            new("serumbank", "Nav.SerumBank", [Permissions.Samples.Receive],
                n => n.NavigateToAsync<SampleStorageView>()),

            new("entry", "Nav.ResultEntry", [Permissions.Results.Enter],
                n => n.NavigateToAsync<ResultEntryView>()),

            new("validation", "Nav.Validation", [Permissions.Results.BiologicalValidate],
                n => n.NavigateToAsync<ValidationView>()),

            new("reports", "Nav.Reports", [Permissions.Reports.View],
                n => n.NavigateToAsync<ReportRegisterView>()),

            new("group.billing", "Nav.Billing", [Permissions.Billing.View],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("billing", "Nav.Billing", [Permissions.Billing.View],
                n => n.NavigateToAsync<BillingView>()),

            new("cash", "Nav.CashDesk", [Permissions.Billing.RecordPayment],
                n => n.NavigateToAsync<CashSessionView>()),

            new("claims", "Nav.Claims", [Permissions.Billing.View],
                n => n.NavigateToAsync<ClaimBatchView>()),

            new("group.quality", "Nav.Quality", [Permissions.Quality.View],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("qc", "Nav.QualityControl", [Permissions.Quality.EnterQc,
                                              Permissions.Quality.ReviewQc],
                n => n.NavigateToAsync<QualityControlView>()),

            new("nonconformities", "Nav.NonConformities", [Permissions.Quality.ManageNonConformity,
                                                            Permissions.Quality.View],
                n => n.NavigateToAsync<NonConformityView>()),

            new("eqa", "Nav.Eqa", [Permissions.Quality.View],
                n => n.NavigateToAsync<EqaView>()),

            new("documents", "Nav.Documents", [Permissions.Quality.View],
                n => n.NavigateToAsync<QualityDocumentsView>()),

            new("group.logistics", "Nav.Inventory", [Permissions.Inventory.View,
                                                      Permissions.Equipment.View],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("inventory", "Nav.Inventory", [Permissions.Inventory.View],
                n => n.NavigateToAsync<InventoryView>()),

            new("purchaseorders", "Nav.PurchaseOrders", [Permissions.Inventory.View],
                n => n.NavigateToAsync<PurchaseOrderView>()),

            new("equipment", "Nav.Equipment", [Permissions.Equipment.View],
                n => n.NavigateToAsync<EquipmentView>()),

            new("group.administration", "Nav.Administration", [Permissions.Administration.ViewUsers],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("users", "Nav.Users", [Permissions.Administration.ViewUsers],
                n => n.NavigateToAsync<UserAdministrationView>()),

            new("audit", "Nav.AuditTrail", [Permissions.Administration.ViewAuditTrail],
                n => n.NavigateToAsync<AuditTrailView>()),

            new("catalog", "Nav.Catalog", [Permissions.Catalog.View],
                n => n.NavigateToAsync<CatalogView>()),

            new("interfaces", "Nav.Instruments", [Permissions.Instruments.View],
                n => n.NavigateToAsync<InstrumentInterfaceView>()),

            new("masterdata", "Nav.MasterData", [Permissions.Equipment.Manage,
                                              Permissions.Quality.ManageQcRules,
                                              Permissions.Inventory.Receive],
                n => n.NavigateToAsync<MasterDataView>()),

            new("settings", "Nav.Settings", [Permissions.Administration.ManageSites,
                                              Permissions.Administration.ManageSettings],
                n => n.NavigateToAsync<SettingsView>())
        ]);

        RenderNavigation();
    }

    // Narrower than the column by the width of its scroll bar. The entries are taller than a
    // laptop screen, so the vertical bar appears, and entries as wide as the whole column then
    // overflowed it and brought up a horizontal bar under the menu as well.
    private static int NavigationWidth =>
        UiTheme.SidebarWidth - UiTheme.GutterTight - SystemInformation.VerticalScrollBarWidth - 2;

    private void RenderNavigation()
    {
        _sidebar.SuspendLayout();
        _sidebar.Controls.Clear();
        _navButtons.Clear();

        var y = UiTheme.GutterTight;

        foreach (var entry in _entries)
        {
            // An entry the user has no permission for is not rendered at all. A disabled button
            // would still tell them the feature exists and that they are not trusted with it,
            // which is noise on a screen that has to stay scannable.
            if (entry.RequiredPermissions.Length > 0
                && !entry.RequiredPermissions.Any(_currentUser.Has))
                continue;

            if (entry.IsGroupHeader)
            {
                var header = new Label
                {
                    Text = T(entry.LabelKey).ToUpperInvariant(),
                    Font = UiTheme.SmallBold,
                    ForeColor = UiTheme.TextMuted,
                    AutoSize = false,
                    Bounds = new Rectangle(UiTheme.Gutter, y + 10, NavigationWidth - UiTheme.GutterTight, 18)
                };
                _sidebar.Controls.Add(header);
                y += 32;
                continue;
            }

            var button = new Button
            {
                Text = T(entry.LabelKey),
                Tag = entry.Key,
                Bounds = new Rectangle(UiTheme.GutterTight, y, NavigationWidth, 32),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(UiTheme.Gutter, 0, 0, 0),
                BackColor = UiTheme.SurfaceRaised,
                ForeColor = UiTheme.TextPrimary,
                Font = UiTheme.Body,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };

            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = UiTheme.AccentSubtle;

            if (IconCatalog.Navigation.TryGetValue(entry.Key, out var glyph))
                ButtonIcons.Set(button, glyph, always: true);

            var captured = entry;
            button.Click += async (_, _) => await ActivateAsync(captured);

            _sidebar.Controls.Add(button);
            _navButtons[entry.Key] = button;

            y += 34;
        }

        // Sign-out sits at the bottom, away from the working entries.
        var signOut = UiTheme.SecondaryButton(T("Action.Logout"));
        signOut.Bounds = new Rectangle(UiTheme.GutterTight, y + UiTheme.Gutter, NavigationWidth,
                                       UiTheme.ButtonHeight);
        signOut.FlatAppearance.BorderColor = UiTheme.Border;
        signOut.Click += (_, _) => SignOut();
        _sidebar.Controls.Add(signOut);

        _sidebar.ResumeLayout(performLayout: true);

        HighlightActive();
    }

    private async Task ActivateAsync(NavigationEntry entry)
    {
        _activeKey = entry.Key;
        HighlightActive();
        await entry.Navigate(_navigation);
    }

    private void HighlightActive()
    {
        foreach (var (key, button) in _navButtons)
        {
            var active = key == _activeKey;
            button.BackColor = active ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised;
            button.Font = active ? UiTheme.BodyBold : UiTheme.Body;
            button.ForeColor = active ? UiTheme.Accent : UiTheme.TextPrimary;
        }
    }

    // ---- Lifecycle --------------------------------------------------------------------------------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await ActivateAsync(_entries[0]);
    }

    protected override void ApplyTexts()
    {
        Text = $"{T("App.Title")} — {_currentUser.FullName}";

        _userInfo.Text = _currentUser.FullName;
        _print.Text = T("Action.Print") + "  (Ctrl+P)";
        _siteInfo.Text = $"{T("App.Site")} : {_currentUser.SiteCode}   ·   " +
                         $"{T("App.Workstation")} : {_currentUser.Workstation}";

        var selected = _languagePicker.SelectedIndex;
        _languagePicker.BeginUpdate();
        _languagePicker.Items.Clear();
        _languagePicker.Items.AddRange(["Français", "العربية", "English"]);
        _languagePicker.SelectedIndex = selected >= 0
            ? selected
            : (int)_cultureManager.CurrentLanguage;
        _languagePicker.EndUpdate();

        UpdateClock();

        // The sidebar carries translated labels, so it is rebuilt on a language change.
        if (_navButtons.Count > 0) RenderNavigation();

        PositionHeaderItems();
    }

    /// <summary>Prints the list the current screen is showing, as it is shown.</summary>
    private async Task PrintCurrentListAsync()
    {
        if (_navigation.Current is not { } view) return;

        var grid = ListPrinting.MainGrid(view);

        if (grid is null || grid.Rows.Count == 0)
        {
            _dialogs.Info(T("Print.NothingToPrint"), T("Action.Print"));
            return;
        }

        try
        {
            var caption = $"{T("App.Site")} : {_currentUser.SiteCode}   ·   {_currentUser.FullName}   ·   " +
                          $"{DateTime.Now:g}   ·   {T("Print.RowCount", grid.Rows.Count)}";

            var document = ListPrinting.Capture(grid, view.Title, caption,
                                                _cultureManager.CurrentLanguage.IsRightToLeft());

            var opened = await _documents.OpenAsync(_lists.Render(document), "pdf");

            if (opened.IsFailure) _dialogs.ShowFailure(opened, T("Action.Print"));
        }
        catch (Exception ex)
        {
            _dialogs.Error(ex.Message, T("Action.Print"));
        }
    }

    private void UpdateClock() => _clock.Text = DateTime.Now.ToString("dddd d MMMM yyyy   HH:mm");

    private void OnLanguagePicked(object? sender, EventArgs e)
    {
        var language = _languagePicker.SelectedIndex switch
        {
            1 => AppLanguage.Arabic,
            2 => AppLanguage.English,
            _ => AppLanguage.French
        };

        if (language != _cultureManager.CurrentLanguage) _cultureManager.SetLanguage(language);
    }

    // ---- Idle lock --------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _lastActivity = DateTimeOffset.Now;

        // F5 refreshes the active view — the shortcut every operator tries first.
        if (e.KeyCode == Keys.F5)
        {
            _ = _navigation.RefreshCurrentAsync();
            e.Handled = true;
        }

        if (e is { Control: true, KeyCode: Keys.P })
        {
            _ = PrintCurrentListAsync();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _lastActivity = DateTimeOffset.Now;
        base.OnMouseMove(e);
    }

    private void CheckIdle(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return;
        if (DateTimeOffset.Now - _lastActivity < timeout) return;

        _idleTimer.Stop();
        _dialogs.Warn(T("Message.SessionExpired"));
        SignOut(confirm: false);
    }

    private void SignOut(bool confirm = true)
    {
        if (confirm && !_dialogs.Confirm(T("Action.Logout"))) return;

        // Restarting the process is the cleanest way to guarantee no scope, cached query or
        // tracked entity survives into the next operator's session on a shared workstation.
        System.Windows.Forms.Application.Restart();
        Environment.Exit(0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockTimer.Dispose();
            _idleTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
