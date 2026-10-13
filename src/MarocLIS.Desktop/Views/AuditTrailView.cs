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

    private readonly TabControl _tabs = new();
    private readonly TabPage _changeTab = new();
    private readonly TabPage _accessTab = new();
    private readonly TabPage _securityTab = new();

    private readonly DataGridView _changes = UiTheme.Grid();
    private readonly DataGridView _access = UiTheme.Grid();
    private readonly DataGridView _security = UiTheme.Grid();

    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly TextBox _user = UiTheme.Input(160);
    private readonly TextBox _search = UiTheme.Input(220);
    private readonly TextBox _recordNumber = UiTheme.Input(150);
    private readonly Button _apply = UiTheme.PrimaryButton(string.Empty);
    private readonly Label _fromLabel = new();
    private readonly Label _toLabel = new();

    private readonly Label _status = new();
    private readonly Label _detail = new();

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
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = UiTheme.Surface };

        foreach (var picker in new[] { _from, _to })
        {
            picker.Format = DateTimePickerFormat.Short;
            picker.Width = 130;
        }

        foreach (var label in new[] { _fromLabel, _toLabel })
        {
            label.Font = UiTheme.Small;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoSize = true;
        }

        _fromLabel.Location = new Point(0, 2);
        _from.Location = new Point(0, 20);
        _toLabel.Location = new Point(142, 2);
        _to.Location = new Point(142, 20);
        _user.Location = new Point(284, 20);
        _search.Location = new Point(456, 20);

        // The file number is a Latin value and must not be mirrored or digit-shaped in Arabic: it
        // gets read back to the person at the counter.
        _recordNumber.Location = new Point(688, 20);
        _recordNumber.Tag = UiTags.LatinValue;
        _recordNumber.Font = UiTheme.Monospace;

        _apply.Bounds = new Rectangle(850, 18, 130, UiTheme.ButtonHeight);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(994, 24, 620, 20);

        bar.Controls.AddRange([_fromLabel, _from, _toLabel, _to, _user, _search, _recordNumber,
                               _apply, _status]);

        // The selected row's full payload. A change entry's before-and-after JSON and a
        // break-glass justification are both too long for a grid cell and are the whole point of
        // opening the row.
        _detail.Font = UiTheme.Monospace;
        _detail.AutoSize = false;
        _detail.Dock = DockStyle.Bottom;
        _detail.Height = 76;
        _detail.BackColor = UiTheme.SurfaceRaised;
        _detail.Padding = new Padding(UiTheme.GutterTight);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = UiTheme.Body;
        _tabs.Padding = new Point(14, 6);

        BuildChangeTab();
        BuildAccessTab();
        BuildSecurityTab();

        _tabs.TabPages.AddRange([_changeTab, _accessTab, _securityTab]);

        Controls.Add(_tabs);
        Controls.Add(_detail);
        Controls.Add(bar);
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
        _changeTab.Padding = new Padding(UiTheme.Gutter);

        Columns(_changes,
            ("when", 12, UiTheme.Monospace, false),
            ("user", 11, null, false),
            ("entity", 12, UiTheme.SmallBold, false),
            ("operation", 8, UiTheme.Small, false),
            ("patient", 10, UiTheme.Monospace, false),
            ("properties", 20, UiTheme.Small, false),
            ("reason", 19, UiTheme.Small, false),
            ("workstation", 8, UiTheme.Small, false));

        _changes.Dock = DockStyle.Fill;
        _changeTab.Controls.Add(_changes);
    }

    private void BuildAccessTab()
    {
        _accessTab.BackColor = UiTheme.Surface;
        _accessTab.Padding = new Padding(UiTheme.Gutter);

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

        _access.Dock = DockStyle.Fill;
        _accessTab.Controls.Add(_access);
    }

    private void BuildSecurityTab()
    {
        _securityTab.BackColor = UiTheme.Surface;
        _securityTab.Padding = new Padding(UiTheme.Gutter);

        Columns(_security,
            ("when", 13, UiTheme.Monospace, false),
            ("user", 14, null, false),
            ("kind", 16, UiTheme.Small, false),
            ("outcome", 9, UiTheme.SmallBold, false),
            ("reason", 18, UiTheme.Small, false),
            ("details", 17, UiTheme.Small, false),
            ("workstation", 13, UiTheme.Small, false));

        _security.Dock = DockStyle.Fill;
        _securityTab.Controls.Add(_security);
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the audit trail failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
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
