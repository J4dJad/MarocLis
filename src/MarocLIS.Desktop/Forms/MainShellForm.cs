using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Desktop.Views;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
public sealed partial class MainShellForm : LocalizedForm
{
    private readonly ICurrentUser _currentUser;
    private readonly INavigationService _navigation;
    private readonly ICultureManager _cultureManager;
    private readonly IUserDialogs _dialogs;

    private readonly Panel _sidebar = new();
    private readonly Panel _header = new();
    private readonly Panel _content = new();
    private readonly Label _viewTitle = new();
    private readonly Label _breadcrumb = new();
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _statusSession = new();
    private readonly ToolStripStatusLabel _statusShortcuts = new();
    private readonly Label _userInfo = new();
    private readonly Label _siteInfo = new();
    private readonly ComboBox _languagePicker = new();
    private readonly Button _print = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _help = UiTheme.SecondaryButton(string.Empty);
    private readonly HelpService.F1Filter _helpKey = new();
    private readonly IListPdfRenderer _lists;
    private readonly IDocumentOutput _documents;
    private readonly Label _clock = new();
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _idleTimer = new();

    // Re-checks the account behind the session: deactivated, password reset, roles changed.
    private readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 60_000 };
    private readonly ISessionValidator _sessionValidator;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MainShellForm>? _logger;
    private bool _checkingSession;
    private bool _endingSession;

    private readonly List<NavigationEntry> _entries = [];
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.Ordinal);
    private string _activeKey = string.Empty;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
    private readonly ActivityFilter _activity;

    // Screens with a shortcut out of the box: the counter and bench workflow, in the order a file
    // goes through the laboratory. Every other screen can be given one in the shortcuts dialog.
    private static readonly Dictionary<string, Keys> DefaultScreenKeys = new(StringComparer.Ordinal)
    {
        ["patients"] = Keys.Control | Keys.D1,
        ["orders"] = Keys.Control | Keys.D2,
        ["orderdetail"] = Keys.Control | Keys.D3,
        ["collection"] = Keys.Control | Keys.D4,
        ["reception"] = Keys.Control | Keys.D5,
        ["entry"] = Keys.Control | Keys.D6,
        ["validation"] = Keys.Control | Keys.D7,
        ["reports"] = Keys.Control | Keys.D8,
        ["billing"] = Keys.Control | Keys.D9,
        ["cash"] = Keys.Control | Keys.D0
    };

    private const string ActionSave = "action.save";
    private const string ActionSearch = "action.search";
    private const string ActionPrint = "action.print";
    private const string ActionRefresh = "action.refresh";
    private const string ActionHelp = "action.help";
    private const string ActionShortcuts = "action.shortcuts";

    private readonly KeyboardShortcuts _shortcuts;

    // The menu stays at 9 pt: its column is fixed, and at the body size "Réception des
    // échantillons" no longer fitted beside its icon.
    private static readonly Font NavigationFont = new(UiTheme.Body.FontFamily, 9f);
    private static readonly Font NavigationFontActive = new(UiTheme.BodyBold.FontFamily, 9f, UiTheme.BodyBold.Style);
    private static readonly Font ShortcutFont = new(UiTheme.Body.FontFamily, 8.25f);
    private readonly CollapsedNavigationGroups _collapsedGroups;
    private readonly ToolTip _navTips = new();

    public MainShellForm(ICurrentUser currentUser, INavigationService navigation,
                         ILocalizer localizer, ICultureManager cultureManager,
                         IUserDialogs dialogs, PasswordPolicy policy,
                         IListPdfRenderer lists, IDocumentOutput documents,
                         ISessionValidator sessionValidator, IServiceScopeFactory scopes,
                         ILogger<MainShellForm>? logger = null)
        : base(localizer, cultureManager)
    {
        _sessionValidator = sessionValidator;
        _scopes = scopes;
        _logger = logger;
        _lists = lists;
        _documents = documents;
        _currentUser = currentUser;
        PermissionGate.User = currentUser;
        _navigation = navigation;
        _cultureManager = cultureManager;
        _dialogs = dialogs;
        _shortcuts = new KeyboardShortcuts(currentUser.UserId);
        _collapsedGroups = new CollapsedNavigationGroups(currentUser.UserId);

        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildLayout();
        BuildNavigation();
        DefineShortcuts();
        RenderNavigation();   // again, now that the shortcuts it shows are known

        _navigation.Attach(_content, view =>
        {
            _viewTitle.Text = HelpService.CurrentTitle = view.Title;
            ShowBreadcrumb();
        });

        // F1 opens the help of the screen in use, here through the shortcuts and in the dialogs
        // a screen opens through the filter.
        HelpService.Configure(localizer, cultureManager);
        System.Windows.Forms.Application.AddMessageFilter(_helpKey);

        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        // Idle lock: a shared laboratory counter left unattended with a session open exposes every
        // patient file on the site. The interval comes from the password policy so the security
        // procedure and the software agree on one number.
        _idleTimer.Interval = (int)Math.Max(TimeSpan.FromMinutes(1).TotalMilliseconds,
                                            policy.IdleTimeout.TotalMilliseconds / 4);
        _idleTimer.Tick += (_, _) => CheckIdle(policy.IdleTimeout);
        _idleTimer.Start();

        // A session is built once, at sign-in; this is what makes deactivating a dismissed
        // employee, resetting a password or withdrawing a role reach a workstation already open.
        _sessionTimer.Tick += async (_, _) => await CheckSessionAsync();
        _sessionTimer.Start();

        // Activity anywhere counts: a click in a grid or typing in a dialog never reaches this
        // form's own mouse and key handlers, and an operator working with the mouse alone was
        // signed out mid-task.
        _activity = new ActivityFilter(() => _lastActivity = DateTimeOffset.Now);
        System.Windows.Forms.Application.AddMessageFilter(_activity);

        _languagePicker.SelectedIndexChanged += OnLanguagePicked;
    }

    // ---- Layout -----------------------------------------------------------------------------------

    private void BuildLayout()
    {
        _header.Dock = DockStyle.Top;
        _header.Height = 52;
        _header.BackColor = UiTheme.SurfaceRaised;
        _header.Padding = new Padding(UiTheme.Gutter, 0, UiTheme.Gutter, 0);

        // The brand in the accent on the white header, then a thin rule and the breadcrumb.
        var brand = new Label
        {
            Text = "MarocLIS",
            Font = new Font(UiTheme.SectionTitle.FontFamily, 13f, FontStyle.Bold),
            ForeColor = UiTheme.Accent,
            AutoSize = true,
            Location = new Point(UiTheme.Gutter, 14)
        };

        var rule = new Panel { BackColor = UiTheme.Border, Bounds = new Rectangle(126, 16, 1, 20) };

        _breadcrumb.Font = UiTheme.Body;
        _breadcrumb.ForeColor = UiTheme.TextSecondary;
        _breadcrumb.AutoSize = true;
        _breadcrumb.Location = new Point(140, 17);
        _breadcrumb.SizeChanged += (_, _) => PlaceViewTitle();

        _viewTitle.Font = UiTheme.SectionTitle;
        _viewTitle.ForeColor = UiTheme.TextPrimary;
        _viewTitle.AutoSize = true;
        _viewTitle.Location = new Point(140, 15);

        _userInfo.Font = UiTheme.SmallBold;
        _userInfo.ForeColor = UiTheme.TextPrimary;
        _userInfo.AutoSize = true;
        _userInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _siteInfo.Font = UiTheme.Small;
        _siteInfo.ForeColor = UiTheme.TextSecondary;
        _siteInfo.AutoSize = true;
        _siteInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _clock.Font = UiTheme.Small;
        _clock.ForeColor = UiTheme.TextSecondary;
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

        // Help of the current screen, left of printing: the same window F1 opens.
        _help.Width = 120;
        _help.Height = 28;
        _help.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _help.Click += (_, _) => HelpService.ShowCurrent();

        _header.Controls.AddRange([brand, rule, _breadcrumb, _viewTitle, _userInfo, _siteInfo, _clock, _help, _print,
                                   _languagePicker]);
        _header.Resize += (_, _) => PositionHeaderItems();
        // The clock, the print button and the user's name size themselves to their text: the clock
        // was placed while still empty and then grew rightwards under the print button. Each
        // change of size places the header items again.
        foreach (var item in new Control[] { _clock, _help, _print, _languagePicker, _userInfo, _siteInfo })
            item.SizeChanged += (_, _) => PositionHeaderItems();

        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = UiTheme.SidebarWidth;
        _sidebar.BackColor = UiTheme.SurfaceRaised;
        _sidebar.Padding = new Padding(0, UiTheme.GutterTight, 0, 0);
        _sidebar.AutoScroll = true;

        _content.Dock = DockStyle.Fill;
        _content.BackColor = UiTheme.Surface;
        _content.Padding = new Padding(0);

        // Hairlines in the line colour between the header, the menu and the screen.
        var headerLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Border };
        var sidebarLine = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = UiTheme.Border };

        BuildStatusBar();

        // Content before sidebar before header, so docking resolves in the right order; the status
        // bar runs the whole width under the menu, as the header does above it.
        Controls.Add(_content);
        Controls.Add(sidebarLine);
        Controls.Add(_sidebar);
        Controls.Add(headerLine);
        Controls.Add(_statusBar);
        Controls.Add(_header);
    }

    /// <summary>
    /// The status bar, in the accent: who is connected on which site at the left, the main
    /// keyboard shortcuts at the right, as the user has them.
    /// </summary>
    private void BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.SizingGrip = false;
        _statusBar.AutoSize = false;
        _statusBar.Height = 26;
        _statusBar.BackColor = UiTheme.Accent;
        _statusBar.ForeColor = UiTheme.TextOnAccent;
        _statusBar.Font = UiTheme.Small;
        _statusBar.Renderer = new UiChrome.FlatStripRenderer();
        _statusBar.Padding = new Padding(UiTheme.GutterTight, 0, UiTheme.Gutter, 0);

        _statusSession.ForeColor = UiTheme.TextOnAccent;
        _statusSession.TextAlign = ContentAlignment.MiddleLeft;
        _statusSession.Spring = true;

        _statusShortcuts.ForeColor = UiTheme.TextOnAccent;
        _statusShortcuts.TextAlign = ContentAlignment.MiddleRight;

        _statusBar.Items.AddRange([_statusSession, _statusShortcuts]);
    }

    private void ShowStatusBar()
    {
        _statusSession.Text = $"●  {T("App.ConnectedAs")} {_currentUser.FullName}     " +
                              $"{T("App.Site")} : {_currentUser.SiteCode}";

        // Only shortcuts that exist, written with the keys this user has.
        var shortcuts = new (string Action, string Label)[]
        {
            (ActionSearch, T("Action.Search")),
            (ActionSave, T("Action.Save")),
            (ActionPrint, T("Action.Print")),
            (ActionRefresh, T("Action.Refresh")),
            (ActionHelp, T("Help.Title"))
        };

        _statusShortcuts.Text = string.Join("  ·  ", shortcuts
            .Select(s => (Keys: _shortcuts.For(s.Action), s.Label))
            .Where(s => s.Keys != Keys.None)
            .Select(s => $"{KeyboardShortcuts.Format(s.Keys, Localizer)} {s.Label}"));
    }

    // "Group ›" before the screen's title, for a screen listed under a group of the menu.
    private void ShowBreadcrumb()
    {
        var group = GroupOf(_activeKey) is { } key ? _entries.FirstOrDefault(e => e.Key == key) : null;
        var rtl = RightToLeft == RightToLeft.Yes;

        _breadcrumb.Text = group is null ? string.Empty : $"{T(group.LabelKey)}  {(rtl ? "‹" : "›")}";
        _breadcrumb.Visible = group is not null;
        PlaceViewTitle();
    }

    private void PlaceViewTitle() =>
        _viewTitle.Left = _breadcrumb.Visible && _breadcrumb.Text.Length > 0
            ? _breadcrumb.Right + UiTheme.GutterTight
            : _breadcrumb.Left;

    private void PositionHeaderItems()
    {
        var right = _header.ClientSize.Width - UiTheme.Gutter;

        _languagePicker.Location = new Point(right - _languagePicker.Width, 14);
        right -= _languagePicker.Width + UiTheme.Gutter;

        _print.Location = new Point(right - _print.Width, 12);
        right -= _print.Width + UiTheme.Gutter;

        _help.Location = new Point(right - _help.Width, 12);
        right -= _help.Width + UiTheme.Gutter;

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

            // The director's dashboard sits with the dashboard and the statistics: it reads every
            // module, and belongs to none of the groups below.
            new("steering", "Nav.Steering", [Permissions.Steering.View],
                n => n.NavigateToAsync<SteeringView>()),

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

            new("recollections", "Nav.Recollections", [Permissions.Orders.Edit, Permissions.Samples.Reject],
                n => n.NavigateToAsync<RecollectionView>()),

            new("serumbank", "Nav.SerumBank", [Permissions.Samples.Receive],
                n => n.NavigateToAsync<SampleStorageView>()),

            new("subcontracting", "Nav.Subcontracting", [Permissions.Samples.Subcontract],
                n => n.NavigateToAsync<SubcontractingView>()),

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
                                                      Permissions.Equipment.View,
                                                      Permissions.Inventory.EnterInvoices,
                                                      Permissions.Inventory.RecordTemperatures],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("inventory", "Nav.Inventory", [Permissions.Inventory.View],
                n => n.NavigateToAsync<InventoryView>()),

            new("purchaseorders", "Nav.PurchaseOrders", [Permissions.Inventory.View],
                n => n.NavigateToAsync<PurchaseOrderView>()),

            new("quotes", "Nav.Quotes", [Permissions.Inventory.ManageQuotes],
                n => n.NavigateToAsync<QuoteRequestView>()),
            new("supplierinvoices", "Nav.SupplierInvoices", [Permissions.Inventory.EnterInvoices,
                                                              Permissions.Inventory.ApprovePayments],
                n => n.NavigateToAsync<SupplierInvoiceView>()),
            new("supplierevaluation", "Nav.SupplierEvaluation", [Permissions.Inventory.ManagePurchaseOrders],
                n => n.NavigateToAsync<SupplierEvaluationView>()),
            new("consumption", "Nav.Consumption", [Permissions.Inventory.View],
                n => n.NavigateToAsync<ConsumptionView>()),
            new("inventorycounts", "Nav.InventoryCounts", [Permissions.Inventory.Receive,
                                                            Permissions.Inventory.CloseCounts],
                n => n.NavigateToAsync<InventoryCountView>()),
            new("temperatures", "Nav.Temperatures", [Permissions.Inventory.RecordTemperatures,
                                                      Permissions.Inventory.ManageStorageUnits],
                n => n.NavigateToAsync<TemperatureView>()),

            new("equipment", "Nav.Equipment", [Permissions.Equipment.View],
                n => n.NavigateToAsync<EquipmentView>()),

            new("contracts", "Nav.Contracts", [Permissions.Equipment.ViewContracts],
                n => n.NavigateToAsync<InstrumentContractView>()),

            new("group.personnel", "Nav.HumanResources",
                [Permissions.HumanResources.View, Permissions.HumanResources.RequestLeave,
                 Permissions.HumanResources.ApproveLeave, Permissions.HumanResources.ManagePlanning,
                 Permissions.HumanResources.Salary, Permissions.HumanResources.Accidents],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("personnel", "Nav.Personnel", [Permissions.HumanResources.View],
                n => n.NavigateToAsync<PersonnelView>()),

            new("habilitations", "Nav.Habilitations", [Permissions.HumanResources.View],
                n => n.NavigateToAsync<HabilitationView>()),

            new("hrdeadlines", "Nav.PersonnelDeadlines", [Permissions.HumanResources.View],
                n => n.NavigateToAsync<PersonnelDeadlinesView>()),

            // Every employee asks for their own leave here; the list shows them their own only.
            new("leave", "Nav.Leave",
                [Permissions.HumanResources.RequestLeave, Permissions.HumanResources.ApproveLeave,
                 Permissions.HumanResources.View],
                n => n.NavigateToAsync<LeaveView>()),

            new("planning", "Nav.Planning",
                [Permissions.HumanResources.View, Permissions.HumanResources.ManagePlanning],
                n => n.NavigateToAsync<PlanningView>()),

            new("payroll", "Nav.Payroll", [Permissions.HumanResources.Salary],
                n => n.NavigateToAsync<PayrollView>()),

            // A blood exposure is declared by whoever is on duty with the right, HR or biologist.
            new("accidents", "Nav.Accidents", [Permissions.HumanResources.Accidents, Permissions.HumanResources.View],
                n => n.NavigateToAsync<AccidentView>()),

            new("group.relations", "Nav.RelationsGroup", [Permissions.Relations.View, Permissions.Messaging.Campaign],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("prescriberstats", "Nav.PrescriberStatistics", [Permissions.Relations.View],
                n => n.NavigateToAsync<PrescriberStatisticsView>()),

            new("relations", "Nav.Relations", [Permissions.Relations.View],
                n => n.NavigateToAsync<RelationsView>()),

            new("patientmessaging", "Nav.PatientMessaging", [Permissions.Messaging.Campaign],
                n => n.NavigateToAsync<PatientMessagingView>()),

            new("group.finance", "Nav.Finance", [Permissions.Accounting.Export],
                _ => Task.CompletedTask, IsGroupHeader: true),

            new("accountingexport", "Nav.AccountingExport", [Permissions.Accounting.Export],
                n => n.NavigateToAsync<AccountingExportView>()),

            new("accountingsettings", "Nav.AccountingSettings", [Permissions.Accounting.Export],
                n => n.NavigateToAsync<AccountingSettingsView>()),

            new("fixedassets", "Nav.FixedAssets", [Permissions.Assets.View],
                n => n.NavigateToAsync<FixedAssetView>()),

            new("bankreconciliation", "Nav.BankReconciliation", [Permissions.Treasury.View, Permissions.Treasury.Manage],
                n => n.NavigateToAsync<BankReconciliationView>()),

            new("cashforecast", "Nav.CashForecast", [Permissions.Treasury.View, Permissions.Treasury.Manage],
                n => n.NavigateToAsync<CashForecastView>()),

            new("cashreview", "Nav.CashSessionReview", [Permissions.Billing.ReconcileCashSession],
                n => n.NavigateToAsync<CashSessionReviewView>()),

            new("costmargin", "Nav.CostMargin", [Permissions.Steering.View],
                n => n.NavigateToAsync<CostMarginView>()),

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
                                              Permissions.Inventory.ManagePurchaseOrders,
                                              Permissions.Catalog.Manage,
                                              Permissions.Billing.ManageTariffs],
                n => n.NavigateToAsync<MasterDataView>()),

            new("migration", "Nav.Migration", [Permissions.Administration.Migration],
                n => n.NavigateToAsync<DataMigrationView>()),

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
        var scroll = _sidebar.AutoScrollPosition;
        _sidebar.SuspendLayout();
        _sidebar.Controls.Clear();
        _navButtons.Clear();
        _navTips.RemoveAll();

        // Bounds of a scrolled panel are taken from where it is scrolled to, not from its top: the
        // menu is laid out from the top, then put back where the user had scrolled it. Laid out
        // scrolled, a fold left an empty band above the first entry as tall as the scroll.
        _sidebar.AutoScrollPosition = Point.Empty;
        var y = UiTheme.GutterTight;
        string? group = null;

        foreach (var entry in _entries)
        {
            if (entry.IsGroupHeader)
            {
                group = entry.Key;

                // A group is shown when the user can open at least one of its screens, whatever
                // the header itself was declared with: a phlebotomist has Collection without
                // Results.View, and the technical header used to vanish above the entry it held.
                if (!GroupEntries(entry.Key).Any(IsPermitted)) continue;

                var collapsed = _collapsedGroups.IsCollapsed(entry.Key);
                var header = new Label
                {
                    Text = T(entry.LabelKey).ToUpperInvariant(),
                    Font = UiTheme.Overline,
                    ForeColor = UiTheme.TextSecondary,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = Cursors.Hand,
                    Bounds = new Rectangle(UiTheme.GutterTight, y + 6, NavigationWidth, 24),
                    Padding = new Padding(UiTheme.Gutter + 8, 0, 0, 0)
                };

                // The chevron is drawn rather than typed: the arrow characters are missing from
                // the fonts of some workstations and came out as empty boxes.
                header.Paint += (_, e) => PaintChevron(e.Graphics, header, collapsed);

                var key = entry.Key;
                header.Click += (_, _) =>
                {
                    _collapsedGroups.Toggle(key);
                    RenderNavigation();
                };
                header.MouseEnter += (_, _) => header.ForeColor = UiTheme.Accent;
                header.MouseLeave += (_, _) => header.ForeColor = UiTheme.TextSecondary;
                _navTips.SetToolTip(header, T(collapsed ? "Nav.ExpandGroup" : "Nav.CollapseGroup"));

                _sidebar.Controls.Add(header);
                y += 32;
                continue;
            }

            // An entry the user has no permission for is not rendered at all. A disabled button
            // would still tell them the feature exists and that they are not trusted with it,
            // which is noise on a screen that has to stay scannable.
            if (!IsPermitted(entry)) continue;

            // A folded group keeps its buttons, hidden: the keyboard shortcuts and the shortcut
            // list go by the buttons the user was given.
            var folded = group is not null && _collapsedGroups.IsCollapsed(group);

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
                Font = NavigationFont,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };

            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = UiTheme.SurfaceSunken;

            if (IconCatalog.Navigation.TryGetValue(entry.Key, out var glyph))
                ButtonIcons.Set(button, glyph, always: true);

            var captured = entry;
            button.Click += async (_, _) => await ActivateAsync(captured);
            button.Paint += (_, e) => PaintShortcut(button, captured.Key, e);
            button.Paint += (_, e) => PaintActiveBar(button, e);
            if (_shortcuts.For(ScreenAction(entry.Key)) is var navKeys and not Keys.None)
                _navTips.SetToolTip(button, $"{T(entry.LabelKey)} ({KeyboardShortcuts.Format(navKeys, Localizer)})");

            button.Visible = !folded;
            _sidebar.Controls.Add(button);
            _navButtons[entry.Key] = button;

            if (!folded) y += 34;
        }

        // The shortcuts list is one key away (Maj+F1), and also here for whoever does not know that.
        var shortcuts = UiTheme.SecondaryButton(ShortcutsButtonText());
        shortcuts.Bounds = new Rectangle(UiTheme.GutterTight, y + UiTheme.Gutter, NavigationWidth,
                                         UiTheme.ButtonHeight);
        shortcuts.FlatAppearance.BorderColor = UiTheme.BorderStrong;
        shortcuts.Click += (_, _) => ShowShortcuts();
        _sidebar.Controls.Add(shortcuts);
        y = shortcuts.Bottom;

        // Sign-out sits at the bottom, away from the working entries.
        var signOut = UiTheme.SecondaryButton(T("Action.Logout"));
        signOut.Bounds = new Rectangle(UiTheme.GutterTight, y + UiTheme.GutterTight, NavigationWidth,
                                       UiTheme.ButtonHeight);
        signOut.FlatAppearance.BorderColor = UiTheme.BorderStrong;
        signOut.Click += (_, _) => SignOut();
        _sidebar.Controls.Add(signOut);

        _sidebar.ResumeLayout(performLayout: true);

        // Folding a group at the bottom of a scrolled menu must not throw the user back to the top.
        _sidebar.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);

        HighlightActive();
    }

    // A small triangle left of a group's name: pointing right when folded, down when open.
    private static void PaintChevron(Graphics graphics, Label header, bool collapsed)
    {
        var x = UiTheme.Gutter - 2;
        var y = header.Height / 2;

        Point[] points = collapsed
            ? [new(x, y - 4), new(x + 5, y), new(x, y + 4)]
            : [new(x - 1, y - 2), new(x + 7, y - 2), new(x + 3, y + 3)];

        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(header.ForeColor);
        graphics.FillPolygon(brush, points);
    }

    private bool IsPermitted(NavigationEntry entry) =>
        entry.RequiredPermissions.Length == 0 || entry.RequiredPermissions.Any(_currentUser.Has);

    // The entries listed under a group header, up to the next header.
    private IEnumerable<NavigationEntry> GroupEntries(string groupKey) =>
        _entries.SkipWhile(e => e.Key != groupKey).Skip(1).TakeWhile(e => !e.IsGroupHeader);

    // The group an entry is listed under, or null for the entries above the first header.
    private string? GroupOf(string key)
    {
        string? group = null;
        foreach (var entry in _entries)
        {
            if (entry.IsGroupHeader) group = entry.Key;
            else if (entry.Key == key) return group;
        }
        return null;
    }

    private async Task ActivateAsync(NavigationEntry entry)
    {
        // A screen opened by its shortcut from a folded group unfolds it, so the highlighted
        // entry is in sight.
        if (GroupOf(entry.Key) is { } group && _collapsedGroups.Expand(group))
            RenderNavigation();

        var previousKey = _activeKey;
        var previousView = _navigation.Current;

        _activeKey = entry.Key;
        HighlightActive();
        await entry.Navigate(_navigation);

        // The screen being left may refuse to go (results typed and not saved): the operator is
        // still on it, so its entry stays highlighted.
        if (previousView is not null && ReferenceEquals(_navigation.Current, previousView))
        {
            _activeKey = previousKey;
            HighlightActive();
        }

        HelpService.CurrentKey = _activeKey;
    }

    private void HighlightActive()
    {
        foreach (var (key, button) in _navButtons)
        {
            var active = key == _activeKey;
            button.BackColor = active ? UiTheme.AccentSubtle : UiTheme.SurfaceRaised;
            button.Font = active ? NavigationFontActive : NavigationFont;
            // The frame Windows draws round the focused entry takes the entry's own colour.
            button.FlatAppearance.BorderColor = button.BackColor;
            button.ForeColor = active ? UiTheme.AccentInk : UiTheme.TextPrimary;
            button.FlatAppearance.MouseOverBackColor = active ? UiTheme.AccentSubtle : UiTheme.SurfaceSunken;
            button.Invalidate();
        }

        if (IsHandleCreated) ShowBreadcrumb();
    }

    // The active entry carries a 3 px accent bar on its leading edge, as in the design system.
    private void PaintActiveBar(Button button, PaintEventArgs e)
    {
        if (!string.Equals(button.Tag as string, _activeKey, StringComparison.Ordinal)) return;

        const int bar = 3;
        var x = button.RightToLeft == RightToLeft.Yes ? button.Width - bar : 0;
        using var brush = new SolidBrush(UiTheme.Accent);
        e.Graphics.FillRectangle(brush, x, 3, bar, button.Height - 6);
    }

    // ---- Lifecycle --------------------------------------------------------------------------------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await ActivateAsync(_entries[0]);
        await CheckBackupAgeAsync();
    }

    protected override void ApplyTexts()
    {
        Text = $"{T("App.Title")} — {_currentUser.FullName}";

        _userInfo.Text = _currentUser.FullName;
        // The screen's title in the header, set at each navigation, follows a language change too:
        // it stayed in the previous language above a screen already translated.
        if (_navigation.Current is { } view) _viewTitle.Text = HelpService.CurrentTitle = view.Title;
        _print.Text = WithShortcut(T("Action.Print"), ActionPrint);
        _help.Text = HelpButtonText();
        _siteInfo.Text = $"{T("App.Site")} : {_currentUser.SiteCode}   ·   " +
                         $"{T("App.Workstation")} : {_currentUser.Workstation}";
        ShowStatusBar();
        ShowBackupWarning();
        ShowBreadcrumb();

        // Each language in its own name, only those the laboratory works in, chosen by value.
        LanguageChoices.Fill(_languagePicker, LanguageChoices.OwnName, _cultureManager.CurrentLanguage);

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
        // Nothing selected while the list is refilled: that is not a choice.
        if (_languagePicker.SelectedIndex < 0) return;

        var language = LanguageChoices.Selected(_languagePicker);
        if (language != _cultureManager.CurrentLanguage) _cultureManager.SetLanguage(language);
    }

    // ---- Idle lock --------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _lastActivity = DateTimeOffset.Now;

        if (_shortcuts.Find(e.KeyData) is { } action && RunShortcut(action))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        base.OnKeyDown(e);
    }

    // ---- Keyboard shortcuts -----------------------------------------------------------------------

    private void DefineShortcuts()
    {
        foreach (var entry in _entries.Where(e => !e.IsGroupHeader))
            _shortcuts.Define(ScreenAction(entry.Key), DefaultScreenKeys.GetValueOrDefault(entry.Key));

        _shortcuts.Define(ActionSave, Keys.Control | Keys.S);
        _shortcuts.Define(ActionSearch, Keys.Control | Keys.F);
        _shortcuts.Define(ActionPrint, Keys.Control | Keys.P);
        _shortcuts.Define(ActionRefresh, Keys.F5);
        _shortcuts.Define(ActionHelp, Keys.F1);
        _shortcuts.Define(ActionShortcuts, Keys.Shift | Keys.F1);
    }

    private static string ScreenAction(string key) => "screen." + key;

    // The actions this user can see: a screen they have no permission for is not offered.
    private List<ShortcutAction> ShortcutActions()
    {
        var actions = new List<ShortcutAction>
        {
            new(ActionHelp, T("Help.Screen"), _shortcuts.Default(ActionHelp)),
            new(ActionShortcuts, T("Shortcut.Help"), _shortcuts.Default(ActionShortcuts)),
            new(ActionSave, T("Action.Save"), _shortcuts.Default(ActionSave)),
            new(ActionSearch, T("Shortcut.Search"), _shortcuts.Default(ActionSearch)),
            new(ActionPrint, T("Shortcut.Print"), _shortcuts.Default(ActionPrint)),
            new(ActionRefresh, T("Shortcut.Refresh"), _shortcuts.Default(ActionRefresh))
        };

        actions.AddRange(_entries
            .Where(e => !e.IsGroupHeader && _navButtons.ContainsKey(e.Key))
            .Select(e => new ShortcutAction(ScreenAction(e.Key), T("Shortcut.Screen", T(e.LabelKey)),
                                            _shortcuts.Default(ScreenAction(e.Key)))));
        return actions;
    }

    private bool RunShortcut(string action)
    {
        switch (action)
        {
            case ActionHelp:
                HelpService.ShowCurrent();
                return true;
            case ActionShortcuts:
                ShowShortcuts();
                return true;
            case ActionRefresh:
                _ = _navigation.RefreshCurrentAsync();
                return true;
            case ActionPrint:
                _ = PrintCurrentListAsync();
                return true;
            case ActionSearch:
                return FocusSearch();
            case ActionSave:
                return ClickSave();
        }

        if (!action.StartsWith("screen.", StringComparison.Ordinal)) return false;

        var key = action["screen.".Length..];
        if (!_navButtons.ContainsKey(key)) return false;   // not permitted for this user

        var entry = _entries.First(e => e.Key == key);
        _ = ActivateAsync(entry);
        return true;
    }

    // The first box the operator can type in, in tab order: on every screen that is its search.
    private bool FocusSearch()
    {
        if (_navigation.Current is not { } view) return false;

        var box = Ordered(view).OfType<TextBoxBase>()
            .FirstOrDefault(b => b is { Visible: true, Enabled: true, ReadOnly: false } && IsShowing(b));
        if (box is null) return false;

        box.Focus();
        box.SelectAll();
        return true;
    }

    // The screen's own Save button, when it has one and it can be pressed.
    private bool ClickSave()
    {
        if (_navigation.Current is not { } view) return false;

        var save = Mnemonics.Plain(T("Action.Save"));
        var button = Ordered(view).OfType<Button>()
            .FirstOrDefault(b => b.Enabled && IsShowing(b)
                                 && string.Equals(Mnemonics.Plain(b.Text).Trim(), save,
                                                  StringComparison.CurrentCultureIgnoreCase));
        if (button is null) return false;

        button.PerformClick();
        return true;
    }

    private static bool IsShowing(Control control)
    {
        for (var c = control; c is not null; c = c.Parent)
            if (!c.Visible) return false;
        return true;
    }

    private static IEnumerable<Control> Ordered(Control parent)
    {
        foreach (var child in parent.Controls.Cast<Control>().OrderBy(c => c.TabIndex))
        {
            yield return child;
            foreach (var nested in Ordered(child)) yield return nested;
        }
    }

    private void ShowShortcuts()
    {
        using var dialog = new ShortcutsDialog(_shortcuts, ShortcutActions(), Localizer, _cultureManager);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // The labels that show a shortcut follow the new choice.
        _print.Text = WithShortcut(T("Action.Print"), ActionPrint);
        _help.Text = HelpButtonText();
        ShowStatusBar();
        RenderNavigation();
    }

    private string WithShortcut(string text, string action) =>
        _shortcuts.For(action) is var keys and not Keys.None
            ? $"{text}  ({KeyboardShortcuts.Format(keys, Localizer)})"
            : text;

    private string ShortcutsButtonText() => WithShortcut(T("Shortcut.Title"), ActionShortcuts);

    // "Aide (F1)" as written, unless the user moved help to another key.
    private string HelpButtonText() =>
        _shortcuts.For(ActionHelp) == Keys.F1 ? T("Help.Button") : WithShortcut(T("Help.Title"), ActionHelp);

    private void PaintShortcut(Button button, string key, PaintEventArgs e)
    {
        var keys = _shortcuts.For(ScreenAction(key));
        if (keys == Keys.None) return;

        // A long screen name keeps the whole width: the key then shows in the tooltip only.
        var shortcut = KeyboardShortcuts.Format(keys, Localizer);
        var used = button.Padding.Left + (button.Image?.Width ?? 0) + 8
                   + TextRenderer.MeasureText(button.Text, button.Font).Width;
        if (used + TextRenderer.MeasureText(shortcut, ShortcutFont).Width + UiTheme.Gutter > button.Width)
            return;

        var bounds = new Rectangle(0, 0, button.Width - UiTheme.GutterTight, button.Height);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | (button.RightToLeft == RightToLeft.Yes ? TextFormatFlags.Left : TextFormatFlags.Right);
        if (button.RightToLeft == RightToLeft.Yes) bounds.X += UiTheme.GutterTight;

        TextRenderer.DrawText(e.Graphics, shortcut, ShortcutFont,
                              bounds, UiTheme.TextMuted, flags);
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

        // Everything happens before the notice, which waits for a click: the screen and every
        // dialog open over it are cleared, and the session is ended and logged at the moment it
        // timed out — not whenever somebody comes to the counter and dismisses the notice.
        _ = EndSessionAsync(SecurityEventKind.SessionTimeout, $"Idle for {timeout.TotalMinutes:0} min",
                            notice: T("Message.SessionExpired"));
    }

    /// <summary>
    /// Ends the session when the account behind it changed since sign-in. A check that cannot
    /// reach the database leaves the session alone: the next operation fails on its own, and a
    /// network blip must not sign out every workstation at once.
    /// </summary>
    private async Task CheckSessionAsync()
    {
        if (_checkingSession || _endingSession) return;
        _checkingSession = true;

        bool valid;
        try
        {
            valid = await _sessionValidator.IsStillValidAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The session could not be re-checked against the account.");
            return;
        }
        finally
        {
            _checkingSession = false;
        }

        if (valid || _endingSession) return;

        // As for the idle lock: cleared and ended before the notice, which waits for a click.
        await EndSessionAsync(SecurityEventKind.Logout, "Session ended: account changed since sign-in",
                              notice: T("Auth_SessionRevoked"));
    }

    /// <summary>Notes keyboard and mouse input in every window of the application, dialogs included.</summary>
    private sealed class ActivityFilter(Action onActivity) : IMessageFilter
    {
        private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmMouseMove = 0x0200, WmLeftDown = 0x0201,
                          WmRightDown = 0x0204, WmMouseWheel = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is WmKeyDown or WmSysKeyDown or WmMouseMove or WmLeftDown or WmRightDown or WmMouseWheel)
                onActivity();
            return false;
        }
    }

    private void SignOut()
    {
        if (!_dialogs.Confirm(T("Action.Logout"))) return;

        _ = EndSessionAsync(SecurityEventKind.Logout, details: null);
    }

    /// <summary>
    /// Records the end of the session, then leaves the message loop so the process restarts.
    /// <para>
    /// Restarting the process is the cleanest way to guarantee no scope, cached query or tracked
    /// entity survives into the next operator's session on a shared workstation. It used to be
    /// done here with <c>Environment.Exit</c>, which skipped everything after the message loop:
    /// no sign-out event was recorded, the folder of patient PDFs viewed in the session was left
    /// on disk, and the log was not flushed. The restart is now <see cref="Program"/>'s, once the
    /// host — and with it that folder — is disposed and the log closed.
    /// </para>
    /// <para>
    /// A <paramref name="notice"/> is shown only once the screen is concealed and the end of the
    /// session recorded: it waits for a click, possibly for hours on an abandoned workstation.
    /// </para>
    /// </summary>
    private async Task EndSessionAsync(SecurityEventKind reason, string? details, string? notice = null)
    {
        if (_endingSession) return;
        _endingSession = true;

        _idleTimer.Stop();
        _sessionTimer.Stop();
        ConcealOpenWindows();

        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
                       .SignOutAsync(reason, details);
        }
        catch (Exception ex)
        {
            // The session ends regardless; a missing record is logged, not a reason to stay open.
            _logger?.LogError(ex, "The end of the session could not be recorded.");
        }

        if (notice is not null) _dialogs.Warn(notice);

        Program.RestartAfterExit = true;

        // Exit rather than Close: it also ends the loop of any dialog still open over the shell.
        System.Windows.Forms.Application.Exit();
    }

    /// <summary>
    /// Clears the shell's screen and hides every other window of the application. A dialog opened
    /// over the shell — the patient editor, an order file, a result entry — is a window of its
    /// own, and hiding only the shell's content left it readable behind the notice. Hiding a modal
    /// dialog ends its loop as a cancel would; nothing it held is saved.
    /// </summary>
    private void ConcealOpenWindows()
    {
        _content.Visible = false;

        foreach (var form in System.Windows.Forms.Application.OpenForms.Cast<Form>()
                     .Where(f => f != this && f.Visible).ToList())
        {
            try
            {
                form.Hide();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not hide {Form} when ending the session.", form.Name);
            }
        }
    }

    /// <summary>
    /// Records the end of the session when the window itself is closed — the title-bar cross,
    /// Alt+F4, a Windows log-off — which is how most working days end. The other ways out go
    /// through <see cref="EndSessionAsync"/>, which has recorded it already.
    /// <para>
    /// Waited for here, off the UI thread so the awaits inside cannot deadlock on it, and for a
    /// bounded time: a database that does not answer must not keep the window from closing.
    /// </para>
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!_endingSession)
        {
            _endingSession = true;
            _idleTimer.Stop();
            _sessionTimer.Stop();

            var details = $"Application window closed ({e.CloseReason})";
            try
            {
                var recorded = Task.Run(async () =>
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
                               .SignOutAsync(SecurityEventKind.Logout, details);
                });

                if (!recorded.Wait(TimeSpan.FromSeconds(10)))
                    _logger?.LogWarning("The end of the session was not recorded in time when the window closed.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "The end of the session could not be recorded.");
            }
        }

        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            System.Windows.Forms.Application.RemoveMessageFilter(_activity);
            System.Windows.Forms.Application.RemoveMessageFilter(_helpKey);
            _clockTimer.Dispose();
            _idleTimer.Dispose();
            _sessionTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
