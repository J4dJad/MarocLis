using System.Globalization;
using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Audit;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Reads the traceability record: changes, consultations and security events.
/// <para>
/// The application wrote all three tables meticulously and could not show any of them. An auditor
/// asking who opened a given patient's file, or a biologist asking what a result was before it was
/// corrected, had no answer short of a DBA with SSMS — which is not an answer a laboratory can give
/// under loi n° 08-09, and not the retrievable change history ISO 15189 §5.8 asks for.
/// </para>
/// <para>
/// Read-only, and nothing here offers an edit. That is not restraint in the UI: the four
/// traceability tables carry <c>INSTEAD OF UPDATE, DELETE</c> triggers installed by the initial
/// migration, so the application's own login cannot alter what this screen shows.
/// </para>
/// </summary>
public sealed class AuditTrailView : LocalizedView
{
    private readonly AuditTrailService _audit;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<AuditTrailView>? _logger;

    private readonly TabControl _tabs = new FlatTabControl();
    private readonly TabPage _changeTab = new();
    private readonly TabPage _accessTab = new();
    private readonly TabPage _securityTab = new();

    private readonly DataGridView _changes = UiTheme.Grid();
    private readonly DataGridView _access = UiTheme.Grid();
    private readonly DataGridView _security = UiTheme.Grid();

    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly TextBox _user = UiTheme.Input(160);
    private readonly TextBox _search = new();
    private readonly TextBox _recordNumber = UiTheme.Input(150);
    private readonly Button _apply = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _fromLabel = new();
    private readonly Label _toLabel = new();

    private readonly Label _status = new();
    private readonly Label _detail = new();
    private readonly Label _pageTitle = new();
    private readonly Label _detailHeading = new();

    private IReadOnlyList<AuditEntryRow> _changeRows = [];
    private IReadOnlyList<DataAccessRow> _accessRows = [];
    private IReadOnlyList<SecurityEventRow> _securityRows = [];

    private bool _changesTruncated, _accessTruncated, _securityTruncated;

    public AuditTrailView(AuditTrailService audit, IUserDialogs dialogs, ILocalizer localizer,
                          ICultureManager cultureManager,
                          ILogger<AuditTrailView>? logger = null)
        : base(localizer, cultureManager)
    {
        _audit = audit;
        _dialogs = dialogs;
        _logger = logger;

        var defaults = audit.DefaultFilter();
        _from.Value = defaults.From.ToDateTime(TimeOnly.MinValue);
        _to.Value = defaults.To.ToDateTime(TimeOnly.MinValue);

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.AuditTrail");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = AdminLayout.Page;

        foreach (var picker in new[] { _from, _to })
        {
            picker.Format = DateTimePickerFormat.Short;
            picker.Width = 130;
            picker.Anchor = AnchorStyles.Left;
            picker.Margin = new Padding(6, 0, 12, 0);
        }

        foreach (var label in new[] { _fromLabel, _toLabel })
        {
            AdminLayout.Muted(label);
            label.Margin = Padding.Empty;
        }

        foreach (var box in new[] { _user, _recordNumber })
        {
            box.Anchor = AnchorStyles.Left;
            box.Margin = new Padding(0, 0, 8, 0);
        }

        // The file number is a Latin value and must not be mirrored or digit-shaped in Arabic: it
        // gets read back to the person at the counter.
        _recordNumber.Tag = UiTags.LatinValue;
        _recordNumber.Font = UiTheme.Monospace;

        var search = new AdminSearchBox(_search, 240) { Margin = new Padding(0, 0, 8, 0) };

        var filters = AdminLayout.Row(AdminLayout.RowHeight,
            [_fromLabel, _from, _toLabel, _to, _user, search, _recordNumber],
            AdminLayout.Actions(_apply));

        // The selected row's full payload. A change entry's before-and-after JSON and a
        // break-glass justification are both too long for a grid cell and are the whole point of
        // opening the row.
        _detail.Font = UiTheme.Monospace;
        _detail.ForeColor = UiTheme.TextSecondary;
        _detail.BackColor = UiTheme.SurfaceRaised;
        _detail.AutoSize = false;
        _detail.Padding = new Padding(15, 0, 15, 6);
        _detail.UseMnemonic = false;

        var detail = AdminLayout.CardSection(_detailHeading, _detail);
        detail.Dock = DockStyle.Bottom;
        detail.Height = 118;

        BuildChangeTab();
        BuildAccessTab();
        BuildSecurityTab();

        _tabs.TabPages.AddRange([_changeTab, _accessTab, _securityTab]);

        var tabs = AdminLayout.FlatTabs(_tabs);

        var gap = new Label { Dock = DockStyle.Bottom, Height = AdminLayout.Gap, BackColor = UiTheme.Surface };

        Controls.Add(tabs);
        Controls.Add(gap);
        Controls.Add(detail);
        Controls.Add(filters);
        Controls.Add(AdminLayout.PageHeader(_pageTitle, _status));
    }

    private static void Columns(DataGridView grid,
                                 params (string Name, int Weight, Font? Font, bool Right)[] columns)
    {
        grid.Columns.Clear();

        foreach (var (name, weight, font, right) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight
                                      : DataGridViewContentAlignment.MiddleLeft
                }
            });
        }
    }

    private void BuildChangeTab()
    {
        _changeTab.BackColor = UiTheme.Surface;

        Columns(_changes,
            ("when", 12, UiTheme.Monospace, false),
            ("user", 11, null, false),
            ("entity", 12, UiTheme.SmallBold, false),
            ("operation", 8, UiTheme.Small, false),
            ("patient", 10, UiTheme.Monospace, false),
            ("properties", 20, UiTheme.Small, false),
            ("reason", 19, UiTheme.Small, false),
            ("workstation", 8, UiTheme.Small, false));

        AdminLayout.ListGrid(_changes);
        _changeTab.Controls.Add(AdminLayout.Card(_changes));
    }

    private void BuildAccessTab()
    {
        _accessTab.BackColor = UiTheme.Surface;

        Columns(_access,
            ("when", 12, UiTheme.Monospace, false),
            ("user", 11, null, false),
            ("kind", 11, UiTheme.Small, false),
            ("patient", 10, UiTheme.Monospace, false),
            ("records", 6, UiTheme.Monospace, true),
            ("query", 22, UiTheme.Small, false),
            ("context", 11, UiTheme.Small, false),
            ("breakglass", 9, UiTheme.SmallBold, false),
            ("workstation", 8, UiTheme.Small, false));

        AdminLayout.ListGrid(_access);
        _accessTab.Controls.Add(AdminLayout.Card(_access));
    }

    private void BuildSecurityTab()
    {
        _securityTab.BackColor = UiTheme.Surface;

        Columns(_security,
            ("when", 13, UiTheme.Monospace, false),
            ("user", 14, null, false),
            ("kind", 16, UiTheme.Small, false),
            ("outcome", 9, UiTheme.SmallBold, false),
            ("reason", 18, UiTheme.Small, false),
            ("details", 17, UiTheme.Small, false),
            ("workstation", 13, UiTheme.Small, false));

        AdminLayout.ListGrid(_security);
        _securityTab.Controls.Add(AdminLayout.Card(_security));
    }

    private void WireBehaviour()
    {
        _apply.Click += async (_, _) => await ActivateAsync();

        foreach (var box in new[] { _user, _search, _recordNumber })
        {
            box.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ActivateAsync(); }
            };
        }

        _tabs.SelectedIndexChanged += (_, _) => { ShowStatus(); ShowDetail(); };

        foreach (var grid in new[] { _changes, _access, _security })
            grid.SelectionChanged += (_, _) => ShowDetail();

        _changes.CellFormatting += OnChangeFormatting;
        _access.CellFormatting += OnAccessFormatting;
        _security.CellFormatting += OnSecurityFormatting;

        AdminLayout.PillColumn(_security, "outcome", r => r.Tag is SecurityEventRow row
            ? row.Succeeded ? AdminLayout.Ok : AdminLayout.Crit
            : null);
        AdminLayout.PillColumn(_access, "breakglass", r => r.Tag is DataAccessRow { IsBreakGlass: true }
            ? AdminLayout.Crit
            : null);
        AdminLayout.PillColumn(_changes, "operation", r => r.Tag is AuditEntryRow row
            ? row.Operation is AuditOperation.SoftDelete or AuditOperation.Delete ? AdminLayout.Crit : AdminLayout.Wait
            : null);
    }

    // ---- Conditional formatting ---------------------------------------------------------------------

    private void OnChangeFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _changes.Rows.Count) return;
        if (_changes.Rows[e.RowIndex].Tag is not AuditEntryRow row) return;

        // A deletion of clinical data is the entry a review looks for first.
        if (row.Operation is AuditOperation.SoftDelete or AuditOperation.Delete)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.Reason is { Length: > 0 })
        {
            // A change that needed a justification is a change somebody had to account for.
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
        }
    }

    private void OnAccessFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _access.Rows.Count) return;
        if (_access.Rows[e.RowIndex].Tag is not DataAccessRow row) return;

        if (row.IsBreakGlass)
        {
            e.CellStyle!.BackColor = UiTheme.CriticalBackground;
            e.CellStyle.ForeColor = UiTheme.Critical;
        }
        else if (row.Kind is DataAccessKind.Export or DataAccessKind.Transmit
                          or DataAccessKind.SubjectAccessRequest)
        {
            // Data leaving the laboratory, as distinct from being looked at inside it.
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
        }
    }

    private void OnSecurityFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _security.Rows.Count) return;
        if (_security.Rows[e.RowIndex].Tag is not SecurityEventRow row) return;

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
            e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
        }
    }

    // ---- Texts -------------------------------------------------------------------------------------

    protected override void ApplyTexts()
    {
        _pageTitle.Text = T("Nav.AuditTrail");
        _detailHeading.Text = T("AdminA.RowDetail");

        _changeTab.Text = T("Audit.Changes");
        _accessTab.Text = T("Audit.Consultations");
        _securityTab.Text = T("Audit.SecurityEvents");

        _fromLabel.Text = T("Field.From");
        _toLabel.Text = T("Field.To");
        _user.PlaceholderText = T("Admin.UserName");
        _search.PlaceholderText = T("App.SearchPlaceholder");
        _recordNumber.PlaceholderText = T("Patient.RecordNumber");
        _apply.Text = T("Action.Apply");

        Header(_changes, ("when", "Field.Date"), ("user", "Admin.UserName"),
               ("entity", "Audit.Entity"), ("operation", "Audit.Operation"),
               ("patient", "Patient.RecordNumber"), ("properties", "Audit.ChangedProperties"),
               ("reason", "Field.Reason"), ("workstation", "Audit.Workstation"));

        Header(_access, ("when", "Field.Date"), ("user", "Admin.UserName"),
               ("kind", "Audit.AccessKind"), ("patient", "Patient.RecordNumber"),
               ("records", "Audit.RecordCount"), ("query", "Audit.Query"),
               ("context", "Audit.Context"), ("breakglass", "Audit.BreakGlass"),
               ("workstation", "Audit.Workstation"));

        Header(_security, ("when", "Field.Date"), ("user", "Admin.UserName"),
               ("kind", "Audit.EventKind"), ("outcome", "Field.Status"),
               ("reason", "Field.Reason"), ("details", "Field.Notes"),
               ("workstation", "Audit.Workstation"));

        RenderAll();
        ShowStatus();

        // Re-rendering the lists for the new language makes each first row current before it
        // carries its entry, so the detail line went blank: it is shown again for the row now current.
        ShowDetail();
    }

    private void Header(DataGridView grid, params (string Column, string Key)[] headers)
    {
        if (grid.Columns.Count == 0) return;

        foreach (var (column, key) in headers)
            if (grid.Columns.Contains(column)) grid.Column(column).HeaderText = T(key);
    }

    // ---- Loading -----------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var filter = CurrentFilter();

            var changes = await _audit.GetChangesAsync(filter, ct);
            if (changes.IsFailure) { _dialogs.ShowFailure(changes, Title); return; }

            _changeRows = changes.Value.Rows;
            _changesTruncated = changes.Value.Truncated;

            // A file number turns the consultation tab into a subject-access answer: scoped to that
            // patient and complete rather than capped, because a right of access under loi n° 08-09
            // is not satisfied by the first five hundred rows.
            var access = string.IsNullOrWhiteSpace(_recordNumber.Text)
                ? await _audit.GetDataAccessAsync(filter, ct)
                : await _audit.GetPatientAccessHistoryAsync(_recordNumber.Text.Trim(),
                                                              filter.From, filter.To, ct);

            if (access.IsFailure) { _dialogs.ShowFailure(access, Title); return; }

            _accessRows = access.Value.Rows;
            _accessTruncated = access.Value.Truncated;

            var security = await _audit.GetSecurityEventsAsync(filter, ct);
            if (security.IsSuccess)
            {
                _securityRows = security.Value.Rows;
                _securityTruncated = security.Value.Truncated;
            }

            RenderAll();
            ShowStatus();
            ShowDetail();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the audit trail failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T(FailureMessages.KeyFor(ex));
        }
    }

    private AuditTrailFilter CurrentFilter() => new()
    {
        From = DateOnly.FromDateTime(_from.Value.Date),
        To = DateOnly.FromDateTime(_to.Value.Date),
        UserName = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim(),
        Search = string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim()
    };

    /// <summary>
    /// Row count for the visible tab, and whether the ceiling cut it short.
    /// <para>
    /// Said out loud on purpose. An audit extract that stops at five hundred rows while looking
    /// complete is the one failure mode of this screen that would mislead rather than merely
    /// inconvenience — somebody would conclude an access did not happen.
    /// </para>
    /// </summary>
    private void ShowStatus()
    {
        var (count, truncated) = _tabs.SelectedIndex switch
        {
            1 => (_accessRows.Count, _accessTruncated),
            2 => (_securityRows.Count, _securityTruncated),
            _ => (_changeRows.Count, _changesTruncated)
        };

        if (truncated)
        {
            _status.ForeColor = UiTheme.Abnormal;
            _status.Text = "⚠ " + T("Audit.Truncated", count);
            return;
        }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = T("Audit.RowCount", count);
    }

    private void ShowDetail()
    {
        _detail.ForeColor = UiTheme.TextSecondary;

        _detail.Text = _tabs.SelectedIndex switch
        {
            1 => (_access.CurrentRow?.Tag as DataAccessRow) is { } access
                ? Join(access.QueryDescription, access.BreakGlassJustification)
                : string.Empty,

            2 => (_security.CurrentRow?.Tag as SecurityEventRow) is { } security
                ? Join(security.Details, security.OsUser)
                : string.Empty,

            _ => (_changes.CurrentRow?.Tag as AuditEntryRow) is { } change
                ? Join(change.Changes, change.Reason)
                : string.Empty
        };
    }

    private static string Join(params string?[] parts) =>
        string.Join("   ·   ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    // ---- Rendering ---------------------------------------------------------------------------------

    private void RenderAll()
    {
        RenderChanges();
        RenderAccess();
        RenderSecurity();
    }

    private void RenderChanges()
    {
        if (_changes.Columns.Count == 0) return;

        _changes.Rows.Clear();

        foreach (var row in _changeRows)
        {
            var index = _changes.Rows.Add(
                row.OccurredAt.ToString("dd/MM/yy HH:mm:ss", CultureInfo.CurrentCulture),
                row.UserName ?? "—",
                row.AggregateName is { Length: > 0 } && row.AggregateName != row.EntityName
                    ? $"{row.EntityName} ({row.AggregateName})"
                    : row.EntityName,
                T($"AuditOperation.{row.Operation}"),
                row.PatientRecordNumber ?? string.Empty,
                row.ChangedProperties ?? string.Empty,
                row.Reason ?? string.Empty,
                row.Workstation ?? string.Empty);

            _changes.Rows[index].Tag = row;
        }
    }

    private void RenderAccess()
    {
        if (_access.Columns.Count == 0) return;

        _access.Rows.Clear();

        foreach (var row in _accessRows)
        {
            var index = _access.Rows.Add(
                row.OccurredAt.ToString("dd/MM/yy HH:mm:ss", CultureInfo.CurrentCulture),
                row.UserName ?? "—",
                T($"DataAccessKind.{row.Kind}"),
                row.PatientRecordNumber ?? string.Empty,
                row.RecordCount?.ToString(CultureInfo.CurrentCulture) ?? string.Empty,
                row.QueryDescription ?? string.Empty,
                row.Context ?? string.Empty,
                row.IsBreakGlass ? T("Audit.BreakGlass") : string.Empty,
                row.Workstation ?? string.Empty);

            _access.Rows[index].Tag = row;
        }
    }

    private void RenderSecurity()
    {
        if (_security.Columns.Count == 0) return;

        _security.Rows.Clear();

        foreach (var row in _securityRows)
        {
            var index = _security.Rows.Add(
                row.OccurredAt.ToString("dd/MM/yy HH:mm:ss", CultureInfo.CurrentCulture),
                row.UserName ?? "—",
                T($"SecurityEventKind.{row.Kind}"),
                row.Succeeded ? T("Action.Yes") : T("Audit.Refused"),
                // The failure reason is itself a resource key, so it renders in the operator's
                // language rather than as an internal token.
                row.FailureReasonKey is { Length: > 0 } ? T(row.FailureReasonKey) : string.Empty,
                row.Details ?? string.Empty,
                row.Workstation ?? string.Empty);

            _security.Rows[index].Tag = row;
        }
    }
}
