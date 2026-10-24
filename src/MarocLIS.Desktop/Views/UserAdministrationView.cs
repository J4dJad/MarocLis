using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Users, roles and the security log.
/// <para>
/// Three things on this screen exist because of the regulation rather than for convenience:
/// </para>
/// <list type="bullet">
/// <item>There is no delete button. A personnel record is the actor on every audit entry it ever
/// signed and the signature on released reports, so withdrawal is deactivation and nothing
/// else.</item>
/// <item>The list says whether each biologist actually satisfies loi n° 12-01 for biological
/// validation — qualification, Ordre registration, in date. An administrator should discover a
/// mis-recorded biologist here, not when the biologist is standing at the bench with a report to
/// sign.</item>
/// <item>The security log sits on the same screen as the accounts. The administrator looking at
/// users is the person who needs to notice a run of failed sign-ins against one name.</item>
/// </list>
/// </summary>
public sealed class UserAdministrationView : LocalizedView
{
    private readonly UserAdministrationService _administration;
    private readonly ICurrentUser _currentUser;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<UserAdministrationView>? _logger;

    private readonly TabControl _tabs = new FlatTabControl();
    private readonly TabPage _usersTab = new();
    private readonly TabPage _rolesTab = new();
    private readonly TabPage _securityTab = new();

    // Users tab
    private readonly TextBox _search = new();
    private readonly CheckBox _includeInactive = new();
    private readonly DataGridView _users = UiTheme.Grid();
    private readonly Label _userCount = new();
    private readonly Button _newUser = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editUser = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _assignRoles = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _siteAccess = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _resetPassword = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _unlock = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _toggleActive = UiTheme.SecondaryButton(string.Empty);
    private readonly ToolTip _tips = new();
    private readonly Label _userNotice = new();

    // Page and selected account
    private readonly Label _pageTitle = new();
    private readonly Label _pageCaption = new();
    private readonly Label _asideName = new();
    private readonly Label _asideSub = new();
    private readonly PillLabel _asidePill = new();
    private readonly TableLayoutPanel _facts = new();
    private readonly (Label Caption, Label Value)[] _factLabels =
        [(new(), new()), (new(), new()), (new(), new()), (new(), new()), (new(), new())];
    private readonly Label _rolesHeading = new();
    private readonly Label _permissionsHeading = new();
    private readonly Label _eventCount = new();

    // Roles tab
    private readonly DataGridView _roles = UiTheme.Grid();
    private readonly TreeView _permissions = new();
    private readonly Button _newRole = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _toggleRole = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _savePermissions = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _roleNotice = new();

    // Security tab
    private readonly DataGridView _events = UiTheme.Grid();
    private readonly CheckBox _onlySelectedUser = new();
    private readonly Button _refreshEvents = UiTheme.SecondaryButton(string.Empty);

    private IReadOnlyList<UserRow> _userRows = [];
    private IReadOnlyList<RoleRow> _roleRows = [];
    private IReadOnlyList<SiteRow> _siteRows = [];
    private IReadOnlyList<SecurityEventRow> _eventRows = [];

    public UserAdministrationView(UserAdministrationService administration, IUserDialogs dialogs,
                                  ILocalizer localizer, ICultureManager cultureManager, ICurrentUser currentUser,
                                  ILogger<UserAdministrationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _administration = administration;
        _dialogs = dialogs;
        _currentUser = currentUser;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Administration");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = AdminLayout.Page;

        BuildUsersTab();
        BuildRolesTab();
        BuildSecurityTab();

        _tabs.TabPages.AddRange([_usersTab, _rolesTab, _securityTab]);

        // The security log is the audit trail's, and opened with an error for whoever may manage
        // accounts without reading it.
        if (PermissionGate.User is not { } viewer || !UserAdministrationRules.SecurityLogOffered(viewer))
            _tabs.TabPages.Remove(_securityTab);

        Controls.Add(AdminLayout.FlatTabs(_tabs));
        Controls.Add(AdminLayout.PageHeader(_pageTitle, _pageCaption));
    }

    /// <summary>
    /// The accounts: search and filter above, the list in a card, and beside it the selected
    /// account's facts with every action that applies to it — the list pattern of the patients
    /// screen. Creating and editing stay at the head, where the list's own actions are.
    /// </summary>
    private void BuildUsersTab()
    {
        _usersTab.BackColor = UiTheme.Surface;

        var filters = AdminLayout.Row(AdminLayout.RowHeight,
            [new AdminSearchBox(_search, 320), AdminLayout.Filter(_includeInactive), AdminLayout.Muted(_userCount)],
            AdminLayout.Actions(_newUser, _editUser));

        BuildUserColumns();
        AdminLayout.ListGrid(_users);

        var list = AdminLayout.Card(_users);

        var aside = BuildUserAside();
        aside.Dock = DockStyle.Right;
        aside.Width = 356;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(0, 4, 0, 0) };
        body.Controls.Add(AdminLayout.Spaced(list, new Padding(0, 0, AdminLayout.Gap, 0)));
        body.Controls.Add(aside);

        _usersTab.Controls.Add(body);
        _usersTab.Controls.Add(filters);
    }

    /// <summary>The selected account: who, in what state, and what can be done with it.</summary>
    private Panel BuildUserAside()
    {
        var aside = new Panel { BackColor = UiTheme.SurfaceRaised, Padding = new Padding(16, 14, 16, 12) };

        _asideName.Font = new Font(UiTheme.SectionTitle.FontFamily, 12.75f, UiTheme.SectionTitle.Style);
        _asideName.ForeColor = UiTheme.TextPrimary;
        _asideName.AutoEllipsis = true;
        _asideName.UseMnemonic = false;
        _asideName.Dock = DockStyle.Top;
        _asideName.Height = 26;

        _asideSub.Font = UiTheme.Body;
        _asideSub.ForeColor = UiTheme.TextSecondary;
        _asideSub.AutoEllipsis = true;
        _asideSub.UseMnemonic = false;
        _asideSub.Dock = DockStyle.Top;
        _asideSub.Height = 22;

        var pillRow = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = UiTheme.SurfaceRaised, Tag = UiTags.OwnLayout };
        _asidePill.Location = new Point(0, 7);
        pillRow.Controls.Add(_asidePill);

        _facts.Dock = DockStyle.Top;
        _facts.AutoSize = true;
        _facts.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _facts.ColumnCount = 2;
        _facts.BackColor = UiTheme.SurfaceRaised;
        _facts.Margin = Padding.Empty;
        _facts.Padding = new Padding(0, 4, 0, 4);
        _facts.Tag = UiTags.OwnLayout;
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        _facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (caption, value) in _factLabels)
        {
            caption.Font = UiTheme.Body;
            caption.ForeColor = UiTheme.TextSecondary;
            caption.AutoSize = true;
            caption.MaximumSize = new Size(112, 0);
            caption.Margin = new Padding(0, 3, 4, 3);
            caption.UseMnemonic = false;

            value.Font = UiTheme.Body;
            value.ForeColor = UiTheme.TextPrimary;
            value.AutoSize = true;
            value.MaximumSize = new Size(178, 0);
            value.Margin = new Padding(0, 3, 0, 3);
            value.UseMnemonic = false;

            _facts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _facts.Controls.Add(caption);
            _facts.Controls.Add(value);
        }

        _userNotice.Font = UiTheme.SmallBold;
        _userNotice.AutoSize = false;
        _userNotice.Dock = DockStyle.Top;
        _userNotice.Height = 40;
        _userNotice.Padding = new Padding(0, 4, 0, 0);

        // Spelt out on the screen, not only in the code: an administrator looking for a delete
        // button should learn immediately why there is not one.
        var retention = new Label
        {
            Name = "retention",
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextMuted,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 6, 0, 0)
        };

        // Two columns so the five actions stay visible at 1366x768: the long one (password reset)
        // takes a whole row, the others go in pairs.
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = UiTheme.SurfaceRaised,
            Padding = new Padding(0, 6, 0, 0),
            Tag = UiTags.OwnLayout
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 3; i++) buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        void Put(Button button, int column, int row, int span = 1)
        {
            UiTheme.StyleSecondary(button);
            button.Dock = DockStyle.Fill;
            button.Height = 32;
            button.Margin = new Padding(column == 0 && span == 1 ? 0 : column == 1 ? 3 : 0, 0, column == 0 && span == 1 ? 3 : 0, 6);
            button.TextAlign = ContentAlignment.MiddleLeft;
            buttons.Controls.Add(button, column, row);
            if (span > 1) buttons.SetColumnSpan(button, span);
        }

        Put(_assignRoles, 0, 0);
        Put(_siteAccess, 1, 0);
        Put(_resetPassword, 0, 1, 2);
        Put(_unlock, 0, 2);
        Put(_toggleActive, 1, 2);

        var line = new Label { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Border, Margin = Padding.Empty };
        var gap = new Label { Dock = DockStyle.Top, Height = 8, BackColor = UiTheme.SurfaceRaised };

        // Docked from the top in reverse: the last added sits highest.
        aside.Controls.Add(retention);
        aside.Controls.Add(buttons);
        aside.Controls.Add(gap);
        aside.Controls.Add(line);
        aside.Controls.Add(_userNotice);
        aside.Controls.Add(_facts);
        aside.Controls.Add(pillRow);
        aside.Controls.Add(_asideSub);
        aside.Controls.Add(_asideName);

        // Scrolls rather than clipping when the window is short or the captions wrap (Arabic):
        // the docked stack lays out inside the display rectangle, which this minimum enlarges.
        aside.AutoScroll = true;
        void Fit()
        {
            var need = aside.Padding.Vertical + 56;
            foreach (Control child in aside.Controls)
                if (child.Dock == DockStyle.Top) need += child.Height;
            var min = new Size(0, need);
            if (aside.AutoScrollMinSize != min) aside.AutoScrollMinSize = min;
        }
        aside.Resize += (_, _) => Fit();
        _facts.SizeChanged += (_, _) => Fit();

        UiChrome.AsCard(aside);
        return aside;
    }

    private void BuildUserColumns()
    {
        _users.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _users.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("username", 11, UiTheme.Monospace);
        Add("fullname", 18);
        Add("qualification", 13, UiTheme.Small);
        Add("order", 9, UiTheme.Monospace);
        Add("roles", 16, UiTheme.Small);
        Add("departments", 9, UiTheme.Small);
        Add("state", 10, UiTheme.SmallBold);
        Add("lastlogin", 9, UiTheme.Monospace);
        Add("password", 5, UiTheme.SmallBold, DataGridViewContentAlignment.MiddleCenter);
    }

    private void BuildRolesTab()
    {
        _rolesTab.BackColor = UiTheme.Surface;

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 460,
            SplitterWidth = AdminLayout.Gap,
            BackColor = UiTheme.Surface
        };

        BuildRoleColumns();
        AdminLayout.ListGrid(_roles);

        _permissions.CheckBoxes = true;
        _permissions.Font = UiTheme.Body;
        _permissions.BorderStyle = BorderStyle.None;
        _permissions.BackColor = UiTheme.SurfaceRaised;
        _permissions.HideSelection = false;
        _permissions.ItemHeight = 24;

        var tree = new Panel { BackColor = UiTheme.SurfaceRaised, Padding = new Padding(12, 0, 4, 0) };
        tree.Controls.Add(_permissions);
        _permissions.Dock = DockStyle.Fill;

        _roleNotice.Font = UiTheme.Small;
        _roleNotice.ForeColor = UiTheme.Abnormal;

        split.Panel1.Controls.Add(AdminLayout.CardSection(_rolesHeading, _roles,
                                                          AdminLayout.Actions(null, _toggleRole, _newRole), footer: true));

        var right = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        right.Controls.Add(AdminLayout.CardSection(_permissionsHeading, tree,
                                                   AdminLayout.Actions(_savePermissions)));
        right.Controls.Add(AdminLayout.Note(_roleNotice, 30));

        split.Panel2.Controls.Add(right);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(0, 4, 0, 0) };
        host.Controls.Add(split);
        _rolesTab.Controls.Add(host);
    }

    private void BuildRoleColumns()
    {
        _roles.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _roles.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("code", 26, UiTheme.Monospace);
        Add("name", 34);
        Add("users", 12, UiTheme.MonospaceBold, DataGridViewContentAlignment.MiddleRight);
        Add("rights", 12, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("builtin", 16, UiTheme.Small);
    }

    private void BuildSecurityTab()
    {
        _securityTab.BackColor = UiTheme.Surface;

        var filters = AdminLayout.Row(AdminLayout.RowHeight,
            [AdminLayout.Filter(_onlySelectedUser), AdminLayout.Muted(_eventCount)],
            AdminLayout.Actions(null, _refreshEvents));
        _onlySelectedUser.Margin = Padding.Empty;

        _events.Columns.Clear();

        void Add(string name, int weight, Font? font = null)
            => _events.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("when", 14, UiTheme.Monospace);
        Add("kind", 20, UiTheme.SmallBold);
        Add("outcome", 8, UiTheme.SmallBold);
        Add("user", 14, UiTheme.Monospace);
        Add("workstation", 12, UiTheme.Monospace);
        Add("reason", 32, UiTheme.Small);
        AdminLayout.ListGrid(_events);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(0, 4, 0, 0) };
        body.Controls.Add(AdminLayout.Card(_events));

        _securityTab.Controls.Add(body);
        _securityTab.Controls.Add(filters);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadUsersAsync(); }
        };

        _includeInactive.CheckedChanged += async (_, _) => await LoadUsersAsync();

        _users.SelectionChanged += (_, _) => RefreshUserActions();

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _newUser.Click += async (_, _) => await RunBusyAsync(CreateUserAsync);
        _editUser.Click += async (_, _) => await RunBusyAsync(EditUserAsync);
        _assignRoles.Click += async (_, _) => await RunBusyAsync(AssignRolesAsync);
        _siteAccess.Click += async (_, _) => await RunBusyAsync(EditSiteAccessAsync);
        _resetPassword.Click += async (_, _) => await RunBusyAsync(ResetPasswordAsync);
        _unlock.Click += async (_, _) => await RunBusyAsync(UnlockAsync);
        _toggleActive.Click += async (_, _) => await RunBusyAsync(ToggleActiveAsync);

        _users.CellFormatting += OnUserFormatting;
        AdminLayout.PillColumn(_users, "state", r => r.Tag is UserRow row ? StateColours(row) : null);
        AdminLayout.PillColumn(_events, "outcome", r => r.Tag is SecurityEventRow row
            ? row.Succeeded ? AdminLayout.Ok : AdminLayout.Crit
            : null);

        _roles.SelectionChanged += (_, _) => ShowRolePermissions();
        _newRole.Click += async (_, _) => await RunBusyAsync(CreateRoleAsync);
        _toggleRole.Click += async (_, _) => await RunBusyAsync(ToggleRoleActiveAsync);
        _savePermissions.Click += async (_, _) => await RunBusyAsync(SaveRolePermissionsAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require(Permissions.Administration.ManageUsers, _newUser, _editUser, _siteAccess, _resetPassword, _unlock, _toggleActive);
        PermissionGate.Require(Permissions.Administration.ManageRoles, _assignRoles, _newRole, _toggleRole, _savePermissions);

        // The list of a role's rights stays readable, and scrollable, for whoever may only look.
        _permissions.BeforeCheck += (_, e) =>
        {
            if (!PermissionGate.Allows(Permissions.Administration.ManageRoles)) e.Cancel = true;
        };

        _refreshEvents.Click += async (_, _) => await LoadEventsAsync();
        _onlySelectedUser.CheckedChanged += async (_, _) => await LoadEventsAsync();

        _events.CellFormatting += OnEventFormatting;

        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_tabs.SelectedTab == _securityTab) await LoadEventsAsync();
        };
    }

    private void OnUserFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _users.Rows.Count) return;
        if (_users.Rows[e.RowIndex].Tag is not UserRow row) return;

        if (!row.IsActive)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
            return;
        }

        if (row.IsLockedOut)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
            return;
        }

        // A validating role on an unqualified record is the regulatory defect this list exists to
        // expose, so it is flagged as strongly as a lockout (the rules', shared with the web).
        var tone = UserAdministrationRules.Tone(row);
        if (tone == AccountTone.Critical)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (tone == AccountTone.Warning)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnEventFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _events.Rows.Count) return;
        if (_events.Rows[e.RowIndex].Tag is not SecurityEventRow row) return;

        var emphasis = UserAdministrationRules.Emphasis(row);
        if (emphasis == SecurityEventEmphasis.Failed)
        {
            e.CellStyle!.ForeColor = UiTheme.Critical;
        }
        else if (emphasis == SecurityEventEmphasis.Privilege)
        {
            // Privilege changes are the entries a security review is looking for; they should not
            // read the same as four hundred routine sign-ins.
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    protected override void ApplyTexts()
    {
        _pageTitle.Text = T("Nav.Users");
        _pageCaption.Text = T("AdminA.UsersLead");
        _rolesHeading.Text = T("Nav.Roles");

        var captions = new[] { "Admin.Qualification", "Admin.OrderNumber", "Nav.Roles", "Admin.Departments", "Admin.LastLogin" };
        for (var i = 0; i < _factLabels.Length; i++) _factLabels[i].Caption.Text = T(captions[i]);

        _usersTab.Text = T("Nav.Users");
        _rolesTab.Text = T("Nav.Roles");
        _securityTab.Text = T("Admin.SecurityLog");

        _search.PlaceholderText = T("App.SearchPlaceholder");
        _includeInactive.Text = T("Admin.IncludeInactive");

        _newUser.Text = T("Admin.NewUser");
        _editUser.Text = T("Action.Edit");
        // Captions of their own: in Arabic the screen titles did not fit the half-width buttons.
        _assignRoles.Text = T("Admin.RolesButton");
        _siteAccess.Text = T("Admin.SiteAccessButton");
        _resetPassword.Text = T("Admin.ResetPassword");
        _unlock.Text = T("Admin.Unlock");

        _newRole.Text = T("Admin.NewRole");
        _toggleRole.Text = T("Admin.DeactivateRole");
        _savePermissions.Text = T("Admin.SavePermissions");

        _onlySelectedUser.Text = T("Admin.OnlySelectedUser");
        _refreshEvents.Text = T("Action.Refresh");

        if (_usersTab.Controls.Find("retention", searchAllChildren: true)
                is [Label retention, ..])
            retention.Text = T("Admin.NoDeleteExplanation");

        if (_users.Columns.Count > 0)
        {
            _users.Column("username").HeaderText = T("Admin.UserName");
            _users.Column("fullname").HeaderText = T("Admin.FullName");
            _users.Column("qualification").HeaderText = T("Admin.Qualification");
            _users.Column("order").HeaderText = T("Admin.OrderNumber");
            _users.Column("roles").HeaderText = T("Nav.Roles");
            _users.Column("departments").HeaderText = T("Admin.Departments");
            _users.Column("state").HeaderText = T("Field.Status");
            _users.Column("lastlogin").HeaderText = T("Admin.LastLogin");
            _users.Column("password").HeaderText = string.Empty;
        }

        if (_roles.Columns.Count > 0)
        {
            _roles.Column("code").HeaderText = T("Field.Code");
            _roles.Column("name").HeaderText = T("Field.Name");
            _roles.Column("users").HeaderText = T("Nav.Users");
            _roles.Column("rights").HeaderText = T("Admin.Permissions");
            _roles.Column("builtin").HeaderText = T("Admin.BuiltIn");
        }

        if (_events.Columns.Count > 0)
        {
            _events.Column("when").HeaderText = T("Field.Date");
            _events.Column("kind").HeaderText = T("Admin.EventKind");
            _events.Column("outcome").HeaderText = T("Field.Status");
            _events.Column("user").HeaderText = T("Admin.UserName");
            _events.Column("workstation").HeaderText = T("App.Workstation");
            _events.Column("reason").HeaderText = T("Field.Reason");
        }

        if (_userRows.Count > 0) RenderUsers();
        if (_roleRows.Count > 0) RenderRoles();
        if (_eventRows.Count > 0) RenderEvents();

        RefreshUserActions();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await LoadSitesAsync(ct);
        await LoadUsersAsync(ct);
        await LoadRolesAsync(ct);
    }

    private async Task LoadSitesAsync(CancellationToken ct = default)
    {
        var result = await _administration.GetSitesAsync(ct);
        if (result.IsSuccess) _siteRows = result.Value;
    }

    private async Task LoadUsersAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _administration.GetUsersAsync(
                _includeInactive.Checked,
                string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim(), ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _userRows = result.Value;
            RenderUsers();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the user list failed.");
        }
    }

    private void RenderUsers()
    {
        var selected = (_users.CurrentRow?.Tag as UserRow)?.UserId;

        _users.SuspendLayout();
        _users.Rows.Clear();

        foreach (var row in _userRows)
        {
            var state = StateText(row);

            // A short glyph column rather than three more text columns (the rules').
            var flags = UserAdministrationRules.Flags(row);

            var index = _users.Rows.Add(
                row.UserName,
                row.FullName,
                T($"Qualification.{row.Qualification}"),
                row.ProfessionalOrderNumber ?? string.Empty,
                string.Join(", ", row.RoleCodes),
                row.AuthorizedDepartmentCodes.Count == 0
                    ? T("Admin.AllDepartments")
                    : string.Join(", ", row.AuthorizedDepartmentCodes),
                state,
                row.LastLoginAt?.ToLocalTime().ToString("dd/MM/yy HH:mm") ?? "—",
                string.Join(" ", flags));

            _users.Rows[index].Tag = row;
        }

        _users.ResumeLayout(performLayout: true);

        // « 7 · 7 actif »: the word did not agree with its count.
        _userCount.Text = T("Admin.UserCount", _userRows.Count, _userRows.Count(u => u.IsActive));

        // Keeps the administrator on the record they were working with across a reload, which they
        // will do repeatedly while fixing up a personnel file.
        var restored = selected is { } id
            ? _users.Rows.Cast<DataGridViewRow>()
                         .FirstOrDefault(r => (r.Tag as UserRow)?.UserId == id)
            : null;

        // Made current, not only highlighted: the first user stayed current and the actions
        // followed it rather than the record kept on screen.
        if (restored is not null) _users.MakeCurrent(restored);
        else if (_users.Rows.Count > 0) _users.SelectFirstRow();

        RefreshUserActions();
    }

    private async Task LoadRolesAsync(CancellationToken ct = default)
    {
        var result = await _administration.GetRolesAsync(includeInactive: true, ct);

        if (result.IsFailure) return;

        _roleRows = result.Value;
        RenderRoles();
    }

    private void RenderRoles()
    {
        var hadRole = _roles.CurrentRow?.Tag is RoleRow;

        _roles.SuspendLayout();
        // Kept on the role whose permissions were just saved or which was just switched off: the tree
        // beside the list follows the current row, and showed the first role's permissions instead.
        _roles.RebuildKeeping<RoleRow, Guid>(r => r.RoleId, () =>
        {
            _roles.Rows.Clear();

            foreach (var row in _roleRows)
            {
                var index = _roles.Rows.Add(
                    row.Code,
                    row.Name.For(Localizer.CurrentLanguage),
                    row.ActiveUserCount.ToString(),
                    row.Permissions.Count.ToString(),
                    row.IsBuiltIn ? T("Admin.BuiltIn") : string.Empty);

                _roles.Rows[index].Tag = row;

                if (!row.IsActive)
                    _roles.Rows[index].DefaultCellStyle.ForeColor = UiTheme.TextMuted;
            }
        });

        _roles.ResumeLayout(performLayout: true);

        if (!hadRole && _roles.Rows.Count > 0) _roles.SelectFirstRow();

        ShowRolePermissions();
    }

    /// <summary>
    /// Fills the permission tree for the selected role, grouped by module. Built from the
    /// reflection-discovered catalogue so a module added later appears here without a code change.
    /// </summary>
    private void ShowRolePermissions()
    {
        var role = _roles.CurrentRow?.Tag as RoleRow;

        _permissions.BeginUpdate();
        _permissions.Nodes.Clear();

        var granted = role?.Permissions.ToHashSet(StringComparer.Ordinal)
                      ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var (module, permissions) in UserAdministrationRules.Catalogue())
        {
            var moduleNode = new TreeNode(T($"PermissionModule.{module}")) { Tag = null };

            foreach (var permission in permissions)
            {
                moduleNode.Nodes.Add(new TreeNode(permission)
                {
                    Tag = permission,
                    Checked = granted.Contains(permission),
                    // Latin identifier: the permission string is the thing a role is defined by and
                    // is quoted verbatim in the laboratory's own procedures.
                    NodeFont = UiTheme.Monospace
                });
            }

            moduleNode.Expand();
            _permissions.Nodes.Add(moduleNode);
        }

        _permissions.EndUpdate();

        _permissionsHeading.Text = role is null
            ? T("Admin.Permissions")
            : $"{T("Admin.Permissions")} — {role.Code}";

        _permissions.Enabled = role is not null;
        _savePermissions.Enabled = role is not null;

        _roleNotice.Text = role is null
            ? string.Empty
            : role.IsBuiltIn
                ? T("Admin.BuiltInRoleNotice")
                : string.Empty;

        // A built-in role cannot be deactivated — the seeded role that holds admin.* has to stay
        // identifiable — so the button is only offered for a role the laboratory created itself.
        var actions = UserAdministrationRules.RoleActions(_currentUser, role);
        _toggleRole.Enabled = role is { IsBuiltIn: false };

        _toggleRole.Text = actions.Activates
            ? T("Admin.ActivateRole")
            : T("Admin.DeactivateRole");
    }

    private async Task LoadEventsAsync(CancellationToken ct = default)
    {
        try
        {
            var userId = _onlySelectedUser.Checked
                ? (_users.CurrentRow?.Tag as UserRow)?.UserId
                : null;

            var result = await _administration.GetRecentSecurityEventsAsync(userId, UserAdministrationRules.RecentEventCount, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _eventRows = result.Value;
            RenderEvents();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the security log failed.");
        }
    }

    private void RenderEvents()
    {
        _events.SuspendLayout();
        _events.Rows.Clear();

        foreach (var row in _eventRows)
        {
            var index = _events.Rows.Add(
                row.OccurredAt.ToLocalTime().ToString("dd/MM/yy HH:mm:ss"),
                T($"SecurityEventKind.{row.Kind}"),
                row.Succeeded ? T("Admin.Succeeded") : T("Admin.Failed"),
                row.UserName ?? "—",
                row.Workstation ?? string.Empty,
                // The failure key is resolved through the catalogue: the log stores a key rather
                // than a sentence precisely so it can be read in the operator's language.
                string.IsNullOrWhiteSpace(row.FailureReasonKey)
                    ? row.Details ?? string.Empty
                    : T(row.FailureReasonKey));

            _events.Rows[index].Tag = row;
        }

        _events.ResumeLayout(performLayout: true);

        _eventCount.Text = T("Audit.RowCount", _eventRows.Count);
    }

    /// <summary>Offers only the operations that make sense for the selected account.</summary>
    private void RefreshUserActions()
    {
        var row = _users.CurrentRow?.Tag as UserRow;

        // What is offered on the account, by its state: the rules' (shared with the web). The
        // rights still grey the buttons through the gate; an inactive account is reactivated, not
        // unlocked; your own account cannot be deactivated (the service refuses it): greyed out on
        // it, rather than refused once the reason has been typed.
        var actions = UserAdministrationRules.Actions(_currentUser, row);
        _editUser.Enabled = row is not null;
        _assignRoles.Enabled = row is not null;
        _siteAccess.Enabled = row is not null;
        _resetPassword.Enabled = row is not null;
        _unlock.Enabled = row is { IsActive: true } and ({ IsLockedOut: true } or { FailedLoginAttempts: > 0 });
        _toggleActive.Enabled = row is not null && !actions.OwnAccount;
        _toggleActive.Text = actions.Reactivates ? T("Admin.Reactivate") : T("Admin.Deactivate");
        _tips.SetToolTip(_toggleActive, actions.OwnAccount ? T("User_CannotDeactivateSelf") : null);

        ShowSelectedUser(row);

        // Names the specific defect. "Invalid" tells an administrator nothing they can act on.
        if (row is not null && UserAdministrationRules.Notice(row) is { } notice)
        {
            _userNotice.ForeColor = notice.Critical ? UiTheme.Critical : UiTheme.Abnormal;
            _userNotice.Text = notice.Argument is { } argument ? T(notice.Key, argument) : T(notice.Key);
        }
        else
        {
            _userNotice.ForeColor = UiTheme.TextSecondary;
            _userNotice.Text = string.Empty;
        }
    }

    /// <summary>The selected account's facts in the panel beside the list.</summary>
    private void ShowSelectedUser(UserRow? row)
    {
        if (row is null)
        {
            _asideName.Text = T("AdminA.NoUserSelected");
            _asideSub.Text = string.Empty;
            _asidePill.Show(string.Empty, EntryPaint.Waiting);
            foreach (var (_, value) in _factLabels) value.Text = "—";
            return;
        }

        _asideName.Text = row.FullName;
        _asideSub.Text = row.UserName;
        _asidePill.Show(StateText(row), StateColours(row));

        _factLabels[0].Value.Text = T($"Qualification.{row.Qualification}");
        _factLabels[1].Value.Text = row.ProfessionalOrderNumber ?? "—";
        _factLabels[2].Value.Text = row.RoleCodes.Count == 0 ? "—" : string.Join(", ", row.RoleCodes);
        _factLabels[3].Value.Text = row.AuthorizedDepartmentCodes.Count == 0
            ? T("Admin.AllDepartments")
            : string.Join(", ", row.AuthorizedDepartmentCodes);
        _factLabels[4].Value.Text = row.LastLoginAt?.ToLocalTime().ToString("dd/MM/yy HH:mm") ?? "—";

        _factLabels[1].Value.Font = _factLabels[4].Value.Font = UiTheme.Monospace;
    }

    private string StateText(UserRow row) =>
        UserAdministrationRules.State(row) is var (key, failed) && failed is { } count ? $"{T(key)} ({count})" : T(key);

    /// <summary>
    /// The pill of an account's state: the same reading as the row colours — a lockout or a
    /// biologist who may not sign is critical, an expired credential a warning.
    /// </summary>
    private static (Color Back, Color Fore) StateColours(UserRow row) => UserAdministrationRules.Tone(row) switch
    {
        AccountTone.Inactive => AdminLayout.Wait,
        AccountTone.Critical => AdminLayout.Crit,
        AccountTone.Warning => AdminLayout.Warn,
        _ => AdminLayout.Ok
    };

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task CreateUserAsync()
    {
        using var dialog = new UserDialog(Localizer, _dialogs, existing: null, await LoadPrescriberOptionsAsync());

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.CreateRequest is null) return;

        var result = await _administration.CreateUserAsync(dialog.CreateRequest);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await ApplySignatureAsync(result.Value, dialog);

        // Says what the operator must do next: the account cannot be used until the holder changes
        // the password the administrator just typed.
        _dialogs.Info(T("Admin.UserCreatedMustChangePassword", dialog.CreateRequest.UserName),
                      T("Admin.NewUser"));

        await LoadUsersAsync();
    }

    private async Task EditUserAsync()
    {
        if (_users.CurrentRow?.Tag is not UserRow row) return;

        byte[]? signature = null;

        if (row.HasSignature && await _administration.GetSignatureAsync(row.UserId) is { IsSuccess: true } stored)
            signature = stored.Value;

        using var dialog = new UserDialog(Localizer, _dialogs, row, await LoadPrescriberOptionsAsync(), signature);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.UpdateRequest is null) return;

        var result = await _administration.UpdateUserAsync(dialog.UpdateRequest);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await ApplySignatureAsync(row.UserId, dialog);

        await LoadUsersAsync();
    }

    /// <summary>
    /// Stores or removes the signature chosen in the editor, once the record it belongs to is saved:
    /// the service checks the qualification as recorded, so a biologist's qualification entered in
    /// the same edit must be saved first.
    /// </summary>
    private async Task ApplySignatureAsync(Guid userId, UserDialog dialog)
    {
        Result? outcome = dialog.NewSignature is { } png
            ? await _administration.SetSignatureAsync(userId, png)
            : dialog.SignatureRemoved
                ? await _administration.RemoveSignatureAsync(userId)
                : null;

        if (outcome is { IsFailure: true })
            _dialogs.ShowFailure(outcome, T("Admin.Signature"));
    }

    /// <summary>The prescribers for the editor's picker; an empty list if they cannot be read.</summary>
    private async Task<IReadOnlyList<PrescriberOption>> LoadPrescriberOptionsAsync()
    {
        var result = await _administration.GetPrescriberOptionsAsync();
        return result.IsSuccess ? result.Value : [];
    }

    private async Task AssignRolesAsync()
    {
        if (_users.CurrentRow?.Tag is not UserRow row) return;

        using var dialog = new RoleAssignmentDialog(Localizer, row, _roleRows);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _administration.SetRolesAsync(row.UserId, dialog.SelectedRoleIds);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Nav.Roles"));
            return;
        }

        await LoadUsersAsync();
        await LoadRolesAsync();
    }

    private async Task EditSiteAccessAsync()
    {
        if (_users.CurrentRow?.Tag is not UserRow row) return;

        using var dialog = new SiteAccessDialog(Localizer, _dialogs, row, _siteRows);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _administration.SetSiteAccessAsync(
            row.UserId, dialog.SelectedSiteIds, dialog.DefaultSiteId);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadUsersAsync();
    }

    private async Task ResetPasswordAsync()
    {
        if (_users.CurrentRow?.Tag is not UserRow row) return;

        // The administrator types the new password and tells the holder out of band. Generating one
        // and showing it here would put a live credential in a screenshot and in the window title
        // of a shared reception workstation.
        var password = _dialogs.PromptForText(
            T("Admin.ResetPasswordPrompt", row.UserName), T("Admin.ResetPassword"));

        if (string.IsNullOrWhiteSpace(password)) return;

        var result = await _administration.ResetPasswordAsync(row.UserId, password);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _dialogs.Info(T("Admin.PasswordResetMustChange", row.UserName), T("Admin.ResetPassword"));

        await LoadUsersAsync();
    }

    private async Task UnlockAsync()
    {
        if (_users.CurrentRow?.Tag is not UserRow row) return;

        var result = await _administration.UnlockAsync(row.UserId);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadUsersAsync();
    }

    private async Task ToggleActiveAsync()
    {
        if (_users.CurrentRow?.Tag is not UserRow row) return;

        var activating = !row.IsActive;

        var reason = _dialogs.PromptForReason(
            activating
                ? T("Admin.ReactivateReasonPrompt", row.UserName)
                : T("Admin.DeactivateReasonPrompt", row.UserName),
            activating ? T("Admin.Reactivate") : T("Admin.Deactivate"));

        if (reason is null) return;

        var result = await _administration.SetActiveAsync(row.UserId, activating, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadUsersAsync();
    }

    private async Task CreateRoleAsync()
    {
        // Capped to their columns (code 40, name 300): a longer one failed the save with an
        // unexpected error.
        var code = _dialogs.PromptForText(T("Admin.NewRoleCodePrompt"), T("Admin.NewRole"),
                                          maxLength: UserAdministrationRules.RoleCodeMaxLength);
        if (string.IsNullOrWhiteSpace(code)) return;

        var name = _dialogs.PromptForText(T("Admin.NewRoleNamePrompt"), T("Admin.NewRole"),
                                          maxLength: UserAdministrationRules.RoleNameMaxLength);
        if (string.IsNullOrWhiteSpace(name)) return;

        // Created with no permissions at all, then granted in the tree. A role that arrives with a
        // starter set of rights nobody chose is how an over-privileged role gets into production.
        var result = await _administration.CreateRoleAsync(
            code, new MultilingualText(name.Trim()), []);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadRolesAsync();

        // The new role made current: the notice sends the administrator to the tree to grant it
        // its permissions, and the tree showed the role that was current before.
        if (_roles.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as RoleRow)?.RoleId == result.Value) is { } created)
        {
            _roles.MakeCurrent(created);
            ShowRolePermissions();
        }

        _dialogs.Info(T("Admin.RoleCreatedNoPermissions"), T("Admin.NewRole"));
    }

    /// <summary>
    /// Activates or deactivates a role the laboratory created. A deactivated role stops granting
    /// its permissions but is not deleted: it is referenced by the audit entries of every change
    /// made under it.
    /// </summary>
    private async Task ToggleRoleActiveAsync()
    {
        if (_roles.CurrentRow?.Tag is not RoleRow role) return;

        var activating = !role.IsActive;

        if (UserAdministrationRules.DeactivationNeedsConfirmation(role))
        {
            if (!_dialogs.Confirm(T("Admin.ConfirmDeactivateRole", role.Code, role.ActiveUserCount),
                                   T("Admin.DeactivateRole")))
                return;
        }

        var result = await _administration.SetRoleActiveAsync(role.RoleId, activating);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Admin.DeactivateRole"));
            return;
        }

        await LoadRolesAsync();
        await LoadUsersAsync();
    }

    private async Task SaveRolePermissionsAsync()
    {
        if (_roles.CurrentRow?.Tag is not RoleRow role) return;

        var selected = _permissions.Nodes.Cast<TreeNode>()
            .SelectMany(module => module.Nodes.Cast<TreeNode>())
            .Where(node => node.Checked && node.Tag is string)
            .Select(node => (string)node.Tag!)
            .ToList();

        var (added, removed) = UserAdministrationRules.Change(role, selected);

        if (added == 0 && removed == 0)
        {
            _roleNotice.ForeColor = UiTheme.TextSecondary;
            _roleNotice.Text = T("Message.NoChanges");
            return;
        }

        if (!_dialogs.Confirm(T("Admin.ConfirmPermissionChange", role.Code, added, removed,
                                 role.ActiveUserCount),
                               T("Admin.SavePermissions")))
            return;

        var result = await _administration.SetRolePermissionsAsync(role.RoleId, selected);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("Admin.SavePermissions"));
            return;
        }

        await LoadRolesAsync();
        await LoadUsersAsync();

        _roleNotice.ForeColor = UiTheme.Normal;
        _roleNotice.Text = T("Message.Saved");
    }
}

/// <summary>
/// Creating or editing a personnel record. The qualification block is the ISO 15189 §5.1 competence
/// record and the loi 12-01 authorisation in one, which is why it is not optional detail hidden
/// behind a second tab.
/// </summary>
internal sealed class UserDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly UserRow? _existing;

    private readonly TextBox _userName = UiTheme.Input(200);
    private readonly TextBox _password = UiTheme.Input(200);
    private readonly TextBox _fullName = UiTheme.Input(200);
    private readonly TextBox _fullNameArabic = UiTheme.Input(200);
    private readonly TextBox _email = UiTheme.Input(200);
    private readonly TextBox _phone = UiTheme.Input(200);

    private readonly ComboBox _qualification = new();
    private readonly TextBox _orderNumber = UiTheme.Input(200);
    private readonly TextBox _inpe = UiTheme.Input(200);
    private readonly DateTimePicker _validUntil = new();
    private readonly CheckBox _hasExpiry = new();
    private readonly TextBox _departments = UiTheme.Input(440);
    private readonly ComboBox _language = new();
    private readonly Label _qualificationNotice = new();
    private readonly ComboBox _prescriber = new();

    private readonly PictureBox _signature = new();
    private readonly Label _noSignature = new();
    private readonly Button _loadSignature = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _removeSignature = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _signatureHint = new();

    internal CreateUserRequest? CreateRequest { get; private set; }
    internal UpdateUserRequest? UpdateRequest { get; private set; }

    /// <summary>A signature scan chosen in this editor, as a PNG; null when none was chosen.</summary>
    internal byte[]? NewSignature { get; private set; }

    /// <summary>Whether the stored signature was removed in this editor.</summary>
    internal bool SignatureRemoved { get; private set; }

    internal UserDialog(ILocalizer localizer, IUserDialogs dialogs, UserRow? existing,
                        IReadOnlyList<PrescriberOption> prescribers, byte[]? signature = null)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _existing = existing;

        Text = existing is null
            ? localizer["Admin.NewUser"]
            : $"{localizer["Action.Edit"]} — {existing.UserName}";

        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 640);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var y = 12;

        void Field(string key, int top, int left = 20)
        {
            var label = UiTheme.FieldLabel(localizer[key]);
            label.Location = new Point(left, top);
            Controls.Add(label);
        }

        Field("Admin.UserName", y);
        _userName.Location = new Point(20, y + 18);
        _userName.Tag = UiTags.LatinValue;
        _userName.Font = UiTheme.Monospace;
        _userName.Enabled = existing is null;   // A renamed account breaks its own audit history.

        Field(existing is null ? "Admin.InitialPassword" : "Admin.PasswordUnchanged", y, 250);
        _password.Location = new Point(250, y + 18);
        _password.Tag = UiTags.LatinValue;
        _password.UseSystemPasswordChar = true;
        _password.Enabled = existing is null;

        y += 54;
        Field("Admin.FullName", y);
        _fullName.Location = new Point(20, y + 18);

        Field("Admin.FullNameArabic", y, 250);
        _fullNameArabic.Location = new Point(250, y + 18);
        _fullNameArabic.RightToLeft = RightToLeft.Yes;

        y += 54;
        Field("Field.Email", y);
        _email.Location = new Point(20, y + 18);
        _email.Tag = UiTags.LatinValue;

        Field("Field.Phone", y, 250);
        _phone.Location = new Point(250, y + 18);
        _phone.Tag = UiTags.LatinValue;

        y += 54;
        Field("Admin.Qualification", y);
        _qualification.DropDownStyle = ComboBoxStyle.DropDownList;
        _qualification.FlatStyle = FlatStyle.Flat;
        _qualification.Bounds = new Rectangle(20, y + 18, 200, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<ProfessionalQualification>())
        {
            _qualification.Items.Add(
                new EnumChoice((int)value, localizer[$"Qualification.{value}"]));
        }

        _qualification.SelectedIndexChanged += (_, _) =>
        {
            RefreshQualificationNotice();
            RefreshSignature();
        };

        Field("Admin.Language", y, 250);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.FlatStyle = FlatStyle.Flat;
        _language.Bounds = new Rectangle(250, y + 18, 200, UiTheme.InputHeight);

        // Only the languages the laboratory works in.
        LanguageChoices.Fill(_language, l => localizer[$"AppLanguage.{l}"], AppLanguage.French);

        y += 54;
        Field("Admin.OrderNumber", y);
        _orderNumber.Location = new Point(20, y + 18);
        _orderNumber.Tag = UiTags.LatinValue;
        _orderNumber.Font = UiTheme.Monospace;

        Field("Admin.Inpe", y, 250);
        _inpe.Location = new Point(250, y + 18);
        _inpe.Tag = UiTags.LatinValue;
        _inpe.Font = UiTheme.Monospace;

        y += 54;

        _hasExpiry.Text = localizer["Admin.QualificationValidUntil"];
        _hasExpiry.AutoSize = true;
        _hasExpiry.Location = new Point(20, y + 20);
        _hasExpiry.CheckedChanged += (_, _) => _validUntil.Enabled = _hasExpiry.Checked;
        Controls.Add(_hasExpiry);

        _validUntil.Bounds = new Rectangle(250, y + 18, 200, UiTheme.InputHeight);
        _validUntil.Format = DateTimePickerFormat.Short;
        _validUntil.MinDate = DateTime.Today;
        _validUntil.Value = DateTime.Today.AddYears(1);
        _validUntil.Enabled = false;

        // The lengths of the columns: a longer value was refused by the database with an unexpected
        // error, and the form was lost with it.
        _userName.MaxLength = UserAdministrationRules.UserNameMaxLength;
        _fullName.MaxLength = _fullNameArabic.MaxLength = _email.MaxLength = UserAdministrationRules.NameMaxLength;
        _phone.MaxLength = UserAdministrationRules.PhoneMaxLength;
        _orderNumber.MaxLength = UserAdministrationRules.OrderNumberMaxLength;
        _inpe.MaxLength = UserAdministrationRules.InpeMaxLength;
        _departments.MaxLength = UserAdministrationRules.DepartmentsMaxLength;

        y += 54;

        _qualificationNotice.Font = UiTheme.Small;
        _qualificationNotice.ForeColor = UiTheme.Abnormal;
        _qualificationNotice.AutoSize = false;
        _qualificationNotice.Bounds = new Rectangle(20, y, 460, 32);
        Controls.Add(_qualificationNotice);

        y += 38;
        Field("Admin.Departments", y);
        _departments.Location = new Point(20, y + 18);
        _departments.Tag = UiTags.LatinValue;

        var departmentsHint = new Label
        {
            Text = localizer["Admin.DepartmentsHint"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, y + 48, 460, 30)
        };
        Controls.Add(departmentsHint);

        // The outside doctor this account belongs to. Only the prescriber role uses it: such an
        // account then sees that prescriber's orders and nothing else, and without a link it sees
        // nothing at all. Staff accounts ignore it.
        y += 84;
        Field("Admin.Prescriber", y);
        _prescriber.DropDownStyle = ComboBoxStyle.DropDownList;
        _prescriber.FlatStyle = FlatStyle.Flat;
        _prescriber.Bounds = new Rectangle(20, y + 18, 460, UiTheme.InputHeight);
        _prescriber.Items.Add(new PrescriberChoice(null, localizer["Admin.NoPrescriber"]));

        // A retired prescriber stays pickable only for the account already linked to it.
        foreach (var option in UserAdministrationRules.PickablePrescribers(prescribers, existing?.PrescriberId))
            _prescriber.Items.Add(new PrescriberChoice(option.PrescriberId, UserAdministrationRules.PrescriberDisplay(option)));

        var prescriberHint = new Label
        {
            Text = localizer["Admin.PrescriberHint"],
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            Bounds = new Rectangle(20, y + 48, 460, 30)
        };
        Controls.Add(prescriberHint);

        // The handwritten signature printed under the biologist's name on the reports they release.
        // A column of its own: the form is already as tall as a 768-pixel screen allows.
        Field("Admin.Signature", 12, 520);
        _signature.Bounds = new Rectangle(520, 30, 220, 100);
        _signature.SizeMode = PictureBoxSizeMode.Zoom;
        _signature.BorderStyle = BorderStyle.FixedSingle;
        _signature.BackColor = Color.White;

        _noSignature.Text = localizer["Admin.NoSignature"];
        _noSignature.Dock = DockStyle.Fill;
        _noSignature.TextAlign = ContentAlignment.MiddleCenter;
        _noSignature.ForeColor = UiTheme.TextSecondary;
        _noSignature.BackColor = Color.Transparent;
        _signature.Controls.Add(_noSignature);

        _loadSignature.Text = localizer["Admin.LoadSignature"];
        _loadSignature.Bounds = new Rectangle(520, 140, 220, UiTheme.ButtonHeight);
        _loadSignature.Click += (_, _) => LoadSignature();

        _removeSignature.Text = localizer["Admin.RemoveSignature"];
        _removeSignature.Bounds = new Rectangle(520, 140 + UiTheme.ButtonHeight + 8, 220, UiTheme.ButtonHeight);
        _removeSignature.Click += (_, _) =>
        {
            NewSignature = null;
            SignatureRemoved = true;
            ShowSignature(null);
        };

        _signatureHint.Font = UiTheme.Small;
        _signatureHint.AutoSize = false;
        _signatureHint.Bounds = new Rectangle(520, 140 + 2 * (UiTheme.ButtonHeight + 8), 220, 96);
        Controls.Add(_signatureHint);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 592, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 592, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_userName, _password, _fullName, _fullNameArabic, _email, _phone,
                           _qualification, _language, _orderNumber, _inpe, _validUntil,
                           _departments, _prescriber, _signature, _loadSignature, _removeSignature,
                           ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(UserDialog)));

        Populate(existing);
        RefreshQualificationNotice();
        ShowSignature(signature);
    }

    private bool IsBiologist =>
        UserAdministrationRules.SignatureAllowed((ProfessionalQualification)((_qualification.SelectedItem as EnumChoice)?.Value ?? 0));

    private void LoadSignature()
    {
        using var dialog = new OpenFileDialog
        {
            Title = _localizer["Admin.Signature"],
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Scaled to what a signature block holds; a JPEG comes out as the PNG the service stores.
        var png = LogoImage.ToPng(dialog.FileName, UserAdministrationRules.SignatureMaxWidth, UserAdministrationRules.SignatureMaxHeight);

        if (png is null)
        {
            _dialogs.Warn(_localizer["Admin.SignatureInvalid"], _localizer["Admin.Signature"]);
            return;
        }

        NewSignature = png;
        SignatureRemoved = false;
        ShowSignature(png);
    }

    private void ShowSignature(byte[]? png)
    {
        var previous = _signature.Image;
        _signature.Image = png is { Length: > 0 } ? LogoImage.Load(png) : null;
        previous?.Dispose();
        _noSignature.Visible = _signature.Image is null;
        RefreshSignature();
    }

    /// <summary>
    /// Only a biologist's record carries a signature: it attests the biological validation the
    /// reports it is printed on went through.
    /// </summary>
    private void RefreshSignature()
    {
        var biologist = IsBiologist;

        _loadSignature.Enabled = biologist;
        _removeSignature.Enabled = biologist && _signature.Image is not null;

        _signatureHint.ForeColor = biologist ? UiTheme.TextSecondary : UiTheme.Abnormal;
        _signatureHint.Text = biologist
            ? _localizer["Admin.SignatureHint"]
            : _localizer["Admin.SignatureBiologistOnly"];
    }

    private void Populate(UserRow? existing)
    {
        _qualification.SelectedIndex = 0;
        _language.SelectedIndex = 0;
        _prescriber.SelectedIndex = 0;

        if (existing is null) return;

        if (existing.PrescriberId is { } linked)
        {
            var index = _prescriber.Items.Cast<object>().ToList()
                .FindIndex(item => item is PrescriberChoice { Id: { } id } && id == linked);

            // Not in the list (it could not be read): kept as it is rather than shown as "none",
            // which saving would turn into a silent unlink.
            if (index < 0)
                index = _prescriber.Items.Add(new PrescriberChoice(linked, UserAdministrationRules.UnlistedPrescriberDisplay(linked)));

            _prescriber.SelectedIndex = index;
        }

        _userName.Text = existing.UserName;
        _fullName.Text = existing.FullName;
        _fullNameArabic.Text = existing.FullNameArabic ?? string.Empty;
        _email.Text = existing.Email ?? string.Empty;
        _phone.Text = existing.Phone ?? string.Empty;
        _orderNumber.Text = existing.ProfessionalOrderNumber ?? string.Empty;
        _inpe.Text = existing.Inpe ?? string.Empty;
        _departments.Text = string.Join(", ", existing.AuthorizedDepartmentCodes);

        Select(_qualification, (int)existing.Qualification);
        // French when the account's language has since been switched off.
        LanguageChoices.Select(_language, existing.PreferredLanguage);

        if (existing.QualificationValidUntil is { } until)
        {
            _hasExpiry.Checked = true;
            _validUntil.Enabled = true;

            // An already-lapsed date cannot be shown by a picker whose minimum is today, and
            // clamping it silently would edit the record just by opening it.
            _validUntil.MinDate = new DateTime(2000, 1, 1);
            _validUntil.Value = until.ToDateTime(TimeOnly.MinValue);
        }
    }

    private static void Select(ComboBox combo, int value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is EnumChoice choice && choice.Value == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>
    /// States the loi 12-01 requirement at the moment the qualification is chosen, so the refusal
    /// the service would return is never a surprise.
    /// </summary>
    private void RefreshQualificationNotice()
    {
        var qualification = (ProfessionalQualification)
            ((_qualification.SelectedItem as EnumChoice)?.Value ?? 0);

        _qualificationNotice.Text = UserAdministrationRules.QualificationNoticeKey(qualification) is { } key
            ? _localizer[key]
            : string.Empty;
    }

    private void Accept()
    {
        var form = new UserForm
        {
            UserName = _userName.Text,
            Password = _password.Text,
            FullName = _fullName.Text,
            FullNameArabic = _fullNameArabic.Text,
            Email = _email.Text,
            Phone = _phone.Text,
            Qualification = (ProfessionalQualification)((_qualification.SelectedItem as EnumChoice)?.Value ?? 0),
            OrderNumber = _orderNumber.Text,
            Inpe = _inpe.Text,
            QualificationValidUntil = _hasExpiry.Checked ? DateOnly.FromDateTime(_validUntil.Value.Date) : null,
            Language = (AppLanguage)((_language.SelectedItem as EnumChoice)?.Value ?? 0),
            Departments = _departments.Text,
            PrescriberId = (_prescriber.SelectedItem as PrescriberChoice)?.Id
        };

        // What the editor refuses before the service is asked, and the request it sends: the
        // rules' (shared with the web).
        if (UserAdministrationRules.Check(form, creating: _existing is null) is { } refusal)
        {
            _dialogs.Warn(refusal.Typed is { } typed ? _localizer.Format(refusal.Key, typed) : _localizer[refusal.Key]);
            (refusal.Field switch { "username" => _userName, "password" => _password, _ => _fullName }).Focus();
            return;
        }

        if (_existing is null) CreateRequest = UserAdministrationRules.ToCreateRequest(form);
        else UpdateRequest = UserAdministrationRules.ToUpdateRequest(_existing.UserId, form);

        // A scan chosen before the qualification was changed away from biologist is not sent.
        if (!IsBiologist) NewSignature = null;

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record PrescriberChoice(Guid? Id, string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>
/// Assigning roles. Shows what each role actually grants, because a role code alone tells an
/// administrator nothing about the rights they are handing out.
/// </summary>
internal sealed class RoleAssignmentDialog : Form
{
    private readonly CheckedListBox _list = new();
    private readonly Label _summary = new();
    private readonly IReadOnlyList<RoleRow> _roles;
    private readonly ILocalizer _localizer;

    internal IReadOnlyList<Guid> SelectedRoleIds =>
    [
        .. _list.CheckedIndices.Cast<int>().Select(i => _roles[i].RoleId)
    ];

    internal RoleAssignmentDialog(ILocalizer localizer, UserRow user, IReadOnlyList<RoleRow> roles)
    {
        _localizer = localizer;
        _roles = UserAdministrationRules.AssignableRoles(roles);

        Text = $"{localizer["Nav.Roles"]} — {user.UserName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 420);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        _list.Bounds = new Rectangle(20, 16, 440, 250);
        _list.CheckOnClick = true;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Font = UiTheme.Body;

        var held = user.RoleIds.ToHashSet();

        foreach (var role in _roles)
        {
            var index = _list.Items.Add($"{role.Code} — {role.Name.For(localizer.CurrentLanguage)}");
            _list.SetItemChecked(index, held.Contains(role.RoleId));
        }

        _list.SelectedIndexChanged += (_, _) => ShowSummary();
        _list.ItemCheck += (_, _) => BeginInvoke(ShowSummary);

        _summary.Bounds = new Rectangle(20, 276, 440, 90);
        _summary.Font = UiTheme.Small;
        _summary.ForeColor = UiTheme.TextSecondary;
        _summary.AutoSize = false;

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 376, 210, UiTheme.ButtonHeight);
        ok.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 376, 210, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_list, _summary, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(RoleAssignmentDialog)));

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        ShowSummary();
    }

    private void ShowSummary()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _roles.Count)
        {
            _summary.Text = string.Empty;
            return;
        }

        var role = _roles[_list.SelectedIndex];

        var (count, validates, administers) = UserAdministrationRules.Grants(role);

        var lines = new List<string>(3)
        {
            _localizer.Format("Admin.RolePermissionCount", count)
        };

        // The two grants with consequences beyond the application: one is reserved by law, the
        // other is the one that can lock everybody out.
        if (validates) lines.Add(_localizer["Admin.RoleGrantsBiologicalValidation"]);
        if (administers) lines.Add(_localizer["Admin.RoleGrantsUserManagement"]);

        _summary.Text = string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Which sites a user may work on, and which one they land on at sign-in.</summary>
internal sealed class SiteAccessDialog : Form
{
    private readonly CheckedListBox _list = new();
    private readonly ComboBox _default = new();
    private readonly IReadOnlyList<SiteRow> _sites;
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    internal IReadOnlyList<Guid> SelectedSiteIds =>
    [
        .. _list.CheckedIndices.Cast<int>().Select(i => _sites[i].SiteId)
    ];

    internal Guid? DefaultSiteId => (_default.SelectedItem as SiteChoice)?.SiteId;

    internal SiteAccessDialog(ILocalizer localizer, IUserDialogs dialogs, UserRow user,
                              IReadOnlyList<SiteRow> sites)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _sites = UserAdministrationRules.AssignableSites(sites);

        Text = $"{localizer["Admin.SiteAccess"]} — {user.UserName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 340);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        _list.Bounds = new Rectangle(20, 16, 400, 200);
        _list.CheckOnClick = true;
        _list.BorderStyle = BorderStyle.FixedSingle;

        var held = user.SiteIds.ToHashSet();

        foreach (var site in _sites)
        {
            var index = _list.Items.Add(
                $"{site.Code} — {site.Name.For(localizer.CurrentLanguage)}");

            _list.SetItemChecked(index, held.Contains(site.SiteId));
        }

        _list.ItemCheck += (_, _) => BeginInvoke(() => RefreshDefaults(DefaultSiteId));

        var label = UiTheme.FieldLabel(localizer["Admin.DefaultSite"]);
        label.Location = new Point(20, 226);
        Controls.Add(label);

        _default.DropDownStyle = ComboBoxStyle.DropDownList;
        _default.FlatStyle = FlatStyle.Flat;
        _default.Bounds = new Rectangle(20, 246, 400, UiTheme.InputHeight);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 296, 190, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(230, 296, 190, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_list, _default, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
        AdminDialogFrame.Attach(this, localizer, AdminDialogFrame.Lead(localizer, nameof(SiteAccessDialog)));

        RefreshDefaults(user.DefaultSiteId);
    }

    /// <summary>Keeps the default-site list to the sites actually ticked.</summary>
    private void RefreshDefaults(Guid? preferred)
    {
        _default.Items.Clear();

        foreach (var index in _list.CheckedIndices.Cast<int>())
        {
            var site = _sites[index];
            _default.Items.Add(new SiteChoice(site.SiteId,
                                               $"{site.Code} — {site.Name.For(_localizer.CurrentLanguage)}"));
        }

        if (_default.Items.Count == 0) return;

        var match = _default.Items.Cast<SiteChoice>()
                            .Select((choice, i) => (choice, i))
                            .FirstOrDefault(x => x.choice.SiteId == preferred);

        _default.SelectedIndex = match.choice is null ? 0 : match.i;
    }

    private void Accept()
    {
        if (UserAdministrationRules.CheckSites(SelectedSiteIds) is { } refusal)
        {
            _dialogs.Warn(_localizer[refusal]);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record SiteChoice(Guid SiteId, string Display)
    {
        public override string ToString() => Display;
    }
}
