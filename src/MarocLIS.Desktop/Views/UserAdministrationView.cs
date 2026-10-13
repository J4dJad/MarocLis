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
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<UserAdministrationView>? _logger;

    private readonly TabControl _tabs = new();
    private readonly TabPage _usersTab = new();
    private readonly TabPage _rolesTab = new();
    private readonly TabPage _securityTab = new();

    // Users tab
    private readonly TextBox _search = UiTheme.Input(260);
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
    private readonly Label _userNotice = new();

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
                                  ILocalizer localizer, ICultureManager cultureManager,
                                  ILogger<UserAdministrationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _administration = administration;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Administration");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = UiTheme.Body;
        _tabs.Padding = new Point(14, 6);

        BuildUsersTab();
        BuildRolesTab();
        BuildSecurityTab();

        _tabs.TabPages.AddRange([_usersTab, _rolesTab, _securityTab]);

        // The security log is the audit trail's, and opened with an error for whoever may manage
        // accounts without reading it.
        if (!PermissionGate.Allows(Permissions.Administration.ViewAuditTrail))
            _tabs.TabPages.Remove(_securityTab);

        Controls.Add(_tabs);
    }

    private void BuildUsersTab()
    {
        _usersTab.BackColor = UiTheme.Surface;
        _usersTab.Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = UiTheme.Surface };

        _search.Location = new Point(0, 6);

        _includeInactive.Font = UiTheme.Small;
        _includeInactive.ForeColor = UiTheme.TextSecondary;
        _includeInactive.AutoSize = true;
        _includeInactive.Location = new Point(280, 10);

        _userCount.Font = UiTheme.SmallBold;
        _userCount.ForeColor = UiTheme.TextSecondary;
        _userCount.AutoSize = true;
        _userCount.Location = new Point(470, 10);

        bar.Controls.AddRange([_search, _includeInactive, _userCount]);

        BuildUserColumns();

        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        gridHost.Controls.Add(_users);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = UiTheme.Surface };

        _userNotice.Font = UiTheme.SmallBold;
        _userNotice.AutoSize = false;
        _userNotice.Bounds = new Rectangle(0, 2, 1200, 18);

        _newUser.Bounds = new Rectangle(0, 26, 170, UiTheme.ButtonHeight);
        _editUser.Bounds = new Rectangle(180, 26, 140, UiTheme.ButtonHeight);
        _assignRoles.Bounds = new Rectangle(330, 26, 140, UiTheme.ButtonHeight);
        _siteAccess.Bounds = new Rectangle(480, 26, 140, UiTheme.ButtonHeight);
        _resetPassword.Bounds = new Rectangle(630, 26, 190, UiTheme.ButtonHeight);
        _unlock.Bounds = new Rectangle(830, 26, 150, UiTheme.ButtonHeight);
        _toggleActive.Bounds = new Rectangle(990, 26, 170, UiTheme.ButtonHeight);

        // Spelt out on the screen, not only in the code: an administrator looking for a delete
        // button should learn immediately why there is not one.
        var retention = new Label
        {
            Name = "retention",
            Font = UiTheme.Small,
            ForeColor = UiTheme.TextMuted,
            AutoSize = false,
            Bounds = new Rectangle(0, 62, 1200, 18)
        };

        actions.Controls.AddRange([_userNotice, _newUser, _editUser, _assignRoles, _siteAccess,
                                   _resetPassword, _unlock, _toggleActive, retention]);

        _usersTab.Controls.Add(gridHost);
        _usersTab.Controls.Add(actions);
        _usersTab.Controls.Add(bar);
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
        _rolesTab.Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 460,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        BuildRoleColumns();

        var left = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        var leftActions = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = UiTheme.Surface };

        _newRole.Bounds = new Rectangle(0, 8, 180, UiTheme.ButtonHeight);
        _toggleRole.Bounds = new Rectangle(190, 8, 190, UiTheme.ButtonHeight);
        leftActions.Controls.AddRange([_newRole, _toggleRole]);

        left.Controls.Add(_roles);
        left.Controls.Add(leftActions);

        var right = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        _permissions.Dock = DockStyle.Fill;
        _permissions.CheckBoxes = true;
        _permissions.Font = UiTheme.Body;
        _permissions.BorderStyle = BorderStyle.FixedSingle;
        _permissions.BackColor = UiTheme.SurfaceRaised;
        _permissions.HideSelection = false;

        var rightActions = new Panel { Dock = DockStyle.Bottom, Height = 66, BackColor = UiTheme.Surface };

        _roleNotice.Font = UiTheme.Small;
        _roleNotice.ForeColor = UiTheme.Abnormal;
        _roleNotice.AutoSize = false;
        _roleNotice.Bounds = new Rectangle(0, 2, 700, 24);

        _savePermissions.Bounds = new Rectangle(0, 30, 230, UiTheme.ButtonHeight);

        rightActions.Controls.AddRange([_roleNotice, _savePermissions]);

        right.Controls.Add(_permissions);
        right.Controls.Add(rightActions);

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        _rolesTab.Controls.Add(split);
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
        _securityTab.Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = UiTheme.Surface };

        _onlySelectedUser.Font = UiTheme.Small;
        _onlySelectedUser.ForeColor = UiTheme.TextSecondary;
        _onlySelectedUser.AutoSize = true;
        _onlySelectedUser.Location = new Point(0, 10);

        _refreshEvents.Bounds = new Rectangle(320, 5, 120, UiTheme.ButtonHeight);

        bar.Controls.AddRange([_onlySelectedUser, _refreshEvents]);

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

        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        gridHost.Controls.Add(_events);

        _securityTab.Controls.Add(gridHost);
        _securityTab.Controls.Add(bar);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadUsersAsync(); }
        };

        _includeInactive.CheckedChanged += async (_, _) => await LoadUsersAsync();

        _users.SelectionChanged += (_, _) => RefreshUserActions();

        _newUser.Click += async (_, _) => await CreateUserAsync();
        _editUser.Click += async (_, _) => await EditUserAsync();
        _assignRoles.Click += async (_, _) => await AssignRolesAsync();
        _siteAccess.Click += async (_, _) => await EditSiteAccessAsync();
        _resetPassword.Click += async (_, _) => await ResetPasswordAsync();
        _unlock.Click += async (_, _) => await UnlockAsync();
        _toggleActive.Click += async (_, _) => await ToggleActiveAsync();

        _users.CellFormatting += OnUserFormatting;

        _roles.SelectionChanged += (_, _) => ShowRolePermissions();
        _newRole.Click += async (_, _) => await CreateRoleAsync();
        _toggleRole.Click += async (_, _) => await ToggleRoleActiveAsync();
        _savePermissions.Click += async (_, _) => await SaveRolePermissionsAsync();

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
        // expose, so it is flagged as strongly as a lockout.
        if (row.Qualification is ProfessionalQualification.MedicalBiologist
                              or ProfessionalQualification.PharmacistBiologist
            && !row.QualifiedToValidate)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.PasswordExpired || row.MustChangePassword || row.QualificationExpired)
        {
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
            e.CellStyle.ForeColor = UiTheme.Abnormal;
        }
    }

    private void OnEventFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _events.Rows.Count) return;
        if (_events.Rows[e.RowIndex].Tag is not SecurityEventRow row) return;

        if (!row.Succeeded)
        {
            e.CellStyle!.ForeColor = UiTheme.Critical;
        }
        else if (row.Kind is SecurityEventKind.RoleAssigned or SecurityEventKind.RoleRemoved
                          or SecurityEventKind.PermissionsChanged
                          or SecurityEventKind.PasswordReset
                          or SecurityEventKind.UserDeactivated
                          or SecurityEventKind.SecuritySettingChanged)
        {
            // Privilege changes are the entries a security review is looking for; they should not
            // read the same as four hundred routine sign-ins.
            e.CellStyle!.ForeColor = UiTheme.Abnormal;
        }
    }

    protected override void ApplyTexts()
    {
        _usersTab.Text = T("Nav.Users");
        _rolesTab.Text = T("Nav.Roles");
        _securityTab.Text = T("Admin.SecurityLog");

        _search.PlaceholderText = T("App.SearchPlaceholder");
        _includeInactive.Text = T("Admin.IncludeInactive");

        _newUser.Text = T("Admin.NewUser");
        _editUser.Text = T("Action.Edit");
        _assignRoles.Text = T("Nav.Roles");
        _siteAccess.Text = T("Admin.SiteAccess");
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
            var state = !row.IsActive ? T("Admin.Inactive")
                : row.IsLockedOut ? T("Admin.LockedOut")
                : row.FailedLoginAttempts > 0
                    ? $"{T("Admin.Active")} ({row.FailedLoginAttempts})"
                    : T("Admin.Active");

            // A short glyph column rather than three more text columns: the administrator needs to
            // see that something is wrong with the credential, then open the record to find out.
            var flags = new List<string>(3);
            if (row.MustChangePassword) flags.Add("⚿");
            if (row.PasswordExpired) flags.Add("!");
            if (row.QualificationExpired) flags.Add("⌛");
            if (row.QualifiedToValidate) flags.Add("✓");

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

        _userCount.Text = $"{_userRows.Count} · {_userRows.Count(u => u.IsActive)} {T("Admin.Active").ToLowerInvariant()}";

        // Keeps the administrator on the record they were working with across a reload, which they
        // will do repeatedly while fixing up a personnel file.
        var restored = selected is { } id
            ? _users.Rows.Cast<DataGridViewRow>()
                         .FirstOrDefault(r => (r.Tag as UserRow)?.UserId == id)
            : null;

        if (restored is not null) restored.Selected = true;
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
        _roles.SuspendLayout();
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

        _roles.ResumeLayout(performLayout: true);

        if (_roles.Rows.Count > 0) _roles.SelectFirstRow();

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

        foreach (var (module, permissions) in UserAdministrationService.GetPermissionCatalogue()
                                                             .OrderBy(g => g.Key, StringComparer.Ordinal))
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

        _permissions.Enabled = role is not null;
        _savePermissions.Enabled = role is not null;

        _roleNotice.Text = role is null
            ? string.Empty
            : role.IsBuiltIn
                ? T("Admin.BuiltInRoleNotice")
                : string.Empty;

        // A built-in role cannot be deactivated — the seeded role that holds admin.* has to stay
        // identifiable — so the button is only offered for a role the laboratory created itself.
        _toggleRole.Enabled = role is { IsBuiltIn: false };

        _toggleRole.Text = role is { IsActive: false }
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

            var result = await _administration.GetRecentSecurityEventsAsync(userId, 300, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _eventRows = result.Value;
            RenderEvents();
        }
        catch (OperationCanceledException) { }
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
    }

    /// <summary>Offers only the operations that make sense for the selected account.</summary>
    private void RefreshUserActions()
    {
        var row = _users.CurrentRow?.Tag as UserRow;

        _editUser.Enabled = row is not null;
        _assignRoles.Enabled = row is not null;
        _siteAccess.Enabled = row is not null;
        _resetPassword.Enabled = row is not null;
        _unlock.Enabled = row is { IsLockedOut: true } or { FailedLoginAttempts: > 0 };

        _toggleActive.Enabled = row is not null;
        _toggleActive.Text = row is { IsActive: false } ? T("Admin.Reactivate") : T("Admin.Deactivate");

        if (row is null)
        {
            _userNotice.Text = string.Empty;
            return;
        }

        // Names the specific defect. "Invalid" tells an administrator nothing they can act on.
        if (row.Qualification is ProfessionalQualification.MedicalBiologist
                              or ProfessionalQualification.PharmacistBiologist
            && !row.QualifiedToValidate)
        {
            _userNotice.ForeColor = UiTheme.Critical;
            _userNotice.Text = row.QualificationExpired
                ? T("Admin.QualificationExpiredNotice", row.QualificationValidUntil!.Value)
                : T("Admin.OrderNumberMissingNotice");
        }
        else if (row.IsLockedOut)
        {
            _userNotice.ForeColor = UiTheme.Critical;
            _userNotice.Text = T("Admin.LockedOutNotice", row.LockedOutUntil!.Value.ToLocalTime());
        }
        else if (row.PasswordExpired)
        {
            _userNotice.ForeColor = UiTheme.Abnormal;
            _userNotice.Text = row.PasswordAgeDays is { } age
                ? T("Admin.PasswordExpiredNotice", age)
                : T("Admin.PasswordNeverChangedNotice");
        }
        else
        {
            _userNotice.ForeColor = UiTheme.TextSecondary;
            _userNotice.Text = string.Empty;
        }
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task CreateUserAsync()
    {
        using var dialog = new UserDialog(Localizer, _dialogs, existing: null);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.CreateRequest is null) return;

        var result = await _administration.CreateUserAsync(dialog.CreateRequest);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        // Says what the operator must do next: the account cannot be used until the holder changes
        // the password the administrator just typed.
        _dialogs.Info(T("Admin.UserCreatedMustChangePassword", dialog.CreateRequest.UserName),
                      T("Admin.NewUser"));

        await LoadUsersAsync();
    }

    private async Task EditUserAsync()
    {
        if (_users.CurrentRow?.Tag is not UserRow row) return;

        using var dialog = new UserDialog(Localizer, _dialogs, row);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.UpdateRequest is null) return;

        var result = await _administration.UpdateUserAsync(dialog.UpdateRequest);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadUsersAsync();
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
        var code = _dialogs.PromptForText(T("Admin.NewRoleCodePrompt"), T("Admin.NewRole"));
        if (string.IsNullOrWhiteSpace(code)) return;

        var name = _dialogs.PromptForText(T("Admin.NewRoleNamePrompt"), T("Admin.NewRole"));
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

        if (!activating && role.ActiveUserCount > 0)
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

        var removed = role.Permissions.Except(selected, StringComparer.Ordinal).Count();
        var added = selected.Except(role.Permissions, StringComparer.Ordinal).Count();

        if (removed == 0 && added == 0)
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

    internal CreateUserRequest? CreateRequest { get; private set; }
    internal UpdateUserRequest? UpdateRequest { get; private set; }

    internal UserDialog(ILocalizer localizer, IUserDialogs dialogs, UserRow? existing)
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
        ClientSize = new Size(500, 560);
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

        _qualification.SelectedIndexChanged += (_, _) => RefreshQualificationNotice();

        Field("Admin.Language", y, 250);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.FlatStyle = FlatStyle.Flat;
        _language.Bounds = new Rectangle(250, y + 18, 200, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<AppLanguage>())
            _language.Items.Add(new EnumChoice((int)value, localizer[$"AppLanguage.{value}"]));

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

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 512, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(250, 512, 230, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_userName, _password, _fullName, _fullNameArabic, _email, _phone,
                           _qualification, _language, _orderNumber, _inpe, _validUntil,
                           _departments, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        Populate(existing);
        RefreshQualificationNotice();
    }

    private void Populate(UserRow? existing)
    {
        _qualification.SelectedIndex = 0;
        _language.SelectedIndex = 0;

        if (existing is null) return;

        _userName.Text = existing.UserName;
        _fullName.Text = existing.FullName;
        _fullNameArabic.Text = existing.FullNameArabic ?? string.Empty;
        _email.Text = existing.Email ?? string.Empty;
        _phone.Text = existing.Phone ?? string.Empty;
        _orderNumber.Text = existing.ProfessionalOrderNumber ?? string.Empty;
        _inpe.Text = existing.Inpe ?? string.Empty;
        _departments.Text = string.Join(", ", existing.AuthorizedDepartmentCodes);

        Select(_qualification, (int)existing.Qualification);
        Select(_language, (int)existing.PreferredLanguage);

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

        _qualificationNotice.Text = qualification switch
        {
            ProfessionalQualification.MedicalBiologist or
            ProfessionalQualification.PharmacistBiologist =>
                _localizer["Admin.BiologistRequirementNotice"],

            ProfessionalQualification.PrescribingPhysician =>
                _localizer["Admin.PhysicianRequirementNotice"],

            _ => string.Empty
        };
    }

    private void Accept()
    {
        var qualification = (ProfessionalQualification)
            ((_qualification.SelectedItem as EnumChoice)?.Value ?? 0);

        var language = (AppLanguage)((_language.SelectedItem as EnumChoice)?.Value ?? 0);

        if (string.IsNullOrWhiteSpace(_fullName.Text))
        {
            _dialogs.Warn(_localizer["User_FullNameRequired"]);
            _fullName.Focus();
            return;
        }

        var departments = _departments.Text
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        DateOnly? validUntil = _hasExpiry.Checked
            ? DateOnly.FromDateTime(_validUntil.Value.Date)
            : null;

        if (_existing is null)
        {
            if (_userName.Text.Trim().Length < 3)
            {
                _dialogs.Warn(_localizer.Format("User_NameTooShort", 3));
                _userName.Focus();
                return;
            }

            if (string.IsNullOrEmpty(_password.Text))
            {
                _dialogs.Warn(_localizer["Admin.InitialPasswordRequired"]);
                _password.Focus();
                return;
            }

            CreateRequest = new CreateUserRequest
            {
                UserName = _userName.Text.Trim(),
                FullName = _fullName.Text.Trim(),
                InitialPassword = _password.Text,
                FullNameArabic = Blank(_fullNameArabic.Text),
                Email = Blank(_email.Text),
                Phone = Blank(_phone.Text),
                Qualification = qualification,
                ProfessionalOrderNumber = Blank(_orderNumber.Text),
                Inpe = Blank(_inpe.Text),
                QualificationValidUntil = validUntil,
                PreferredLanguage = language,
                AuthorizedDepartmentCodes = departments
            };
        }
        else
        {
            UpdateRequest = new UpdateUserRequest
            {
                UserId = _existing.UserId,
                FullName = _fullName.Text.Trim(),
                FullNameArabic = Blank(_fullNameArabic.Text),
                Email = Blank(_email.Text),
                Phone = Blank(_phone.Text),
                Qualification = qualification,
                ProfessionalOrderNumber = Blank(_orderNumber.Text),
                Inpe = Blank(_inpe.Text),
                QualificationValidUntil = validUntil,
                PreferredLanguage = language,
                AuthorizedDepartmentCodes = departments
            };
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
        _roles = [.. roles.Where(r => r.IsActive)];

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

        var validates = role.Permissions.Contains(Permissions.Results.BiologicalValidate,
                                                   StringComparer.Ordinal);

        var administers = role.Permissions.Contains(Permissions.Administration.ManageUsers,
                                                     StringComparer.Ordinal);

        var lines = new List<string>(3)
        {
            _localizer.Format("Admin.RolePermissionCount", role.Permissions.Count)
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
        _sites = [.. sites.Where(s => s.IsActive)];

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
        if (_list.CheckedIndices.Count == 0)
        {
            _dialogs.Warn(_localizer["User_AtLeastOneSiteRequired"]);
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
