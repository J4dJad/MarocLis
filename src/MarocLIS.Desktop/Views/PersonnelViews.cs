using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The personnel list: every file, its post and contract, with the documents and the departure one
/// click away.
/// </summary>
public sealed class PersonnelView : LocalizedView
{
    private readonly PersonnelService _personnel;
    private readonly HrDocumentService _documentsService;
    private readonly SettlementService _settlements;
    private readonly IHrDocumentRenderer _renderer;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PersonnelView>? _logger;

    private readonly Label _heading = new();
    private readonly TextBox _search = UiTheme.Input(260);
    private readonly CheckBox _includeExited = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();

    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _documents = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _exit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _letters = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _settlement = UiTheme.SecondaryButton(string.Empty);
    private readonly ContextMenuStrip _lettersMenu = new();
    private readonly ContextMenuStrip _settlementMenu = new();
    private readonly ToolStripMenuItem _employmentItem = new(), _salaryItem = new(), _certificateItem = new();
    private readonly ToolStripMenuItem _computeItem = new(), _receiptItem = new(), _signedItem = new();
    private readonly Label _status = new();

    private IReadOnlyList<EmployeeRow> _rows = [];

    /// <summary>
    /// Database work under way. The view's services share one context, which takes one operation
    /// at a time: a click that lands while the list is reloading after a save is ignored.
    /// </summary>
    private int _pending;

    private async Task Guarded(Func<Task> action)
    {
        if (_pending > 0) return;
        _pending++;
        try { await action(); }
        finally { _pending--; }
    }

    public PersonnelView(PersonnelService personnel, HrDocumentService documents, SettlementService settlements,
                         IHrDocumentRenderer renderer, IDocumentOutput output, IUserDialogs dialogs, ILocalizer localizer,
                         ICultureManager cultureManager, ILogger<PersonnelView>? logger = null)
        : base(localizer, cultureManager)
    {
        _personnel = personnel;
        _documentsService = documents;
        _settlements = settlements;
        _renderer = renderer;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Personnel");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.SectionTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 2);

        _search.Location = new Point(0, 28);
        _refresh.Bounds = new Rectangle(272, 27, 110, UiTheme.ButtonHeight - 4);

        _includeExited.Font = UiTheme.Small;
        _includeExited.ForeColor = UiTheme.TextSecondary;
        _includeExited.AutoSize = true;
        _includeExited.Location = new Point(396, 32);

        bar.Controls.AddRange([_heading, _search, _refresh, _includeExited]);

        void Add(string name, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, Tag = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("number", 7, UiTheme.Monospace);
        Add("name", 16);
        Add("job", 11, UiTheme.Small);
        Add("title", 12, UiTheme.Small);
        Add("department", 11, UiTheme.Small);
        Add("contract", 9, UiTheme.Small);
        Add("hired", 8, UiTheme.Monospace);
        Add("end", 8, UiTheme.Monospace);
        Add("user", 10, UiTheme.Monospace);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_grid);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.Surface };

        _new.Bounds = new Rectangle(0, 10, 190, UiTheme.ButtonHeight);
        _edit.Bounds = new Rectangle(200, 10, 170, UiTheme.ButtonHeight);
        _documents.Bounds = new Rectangle(380, 10, 190, UiTheme.ButtonHeight);
        _exit.Bounds = new Rectangle(580, 10, 190, UiTheme.ButtonHeight);
        _letters.Bounds = new Rectangle(780, 10, 170, UiTheme.ButtonHeight);
        _settlement.Bounds = new Rectangle(960, 10, 210, UiTheme.ButtonHeight);

        _status.Font = UiTheme.Small;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(1182, 16, 400, 20);

        _lettersMenu.Items.AddRange([_employmentItem, _salaryItem, _certificateItem]);
        _settlementMenu.Items.AddRange([_computeItem, _receiptItem, _signedItem]);

        actions.Controls.AddRange([_new, _edit, _documents, _exit, _letters, _settlement, _status]);

        Controls.Add(host);
        Controls.Add(actions);
        Controls.Add(bar);
    }

    private void WireBehaviour()
    {
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Render();
        };

        _refresh.Click += async (_, _) => await Guarded(() => ActivateAsync());
        _includeExited.CheckedChanged += async (_, _) => await Guarded(() => ActivateAsync());

        _new.Click += async (_, _) => await Guarded(() => EditAsync(null));
        _edit.Click += async (_, _) => { if (Current() is { } row) await Guarded(() => EditAsync(row.EmployeeId)); };
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && !PermissionGate.IsBlocked(_edit) && Current() is { } row)
                await Guarded(() => EditAsync(row.EmployeeId));
        };
        _documents.Click += async (_, _) => await Guarded(DocumentsAsync);
        _exit.Click += async (_, _) => await Guarded(ExitOrRehireAsync);
        _letters.Click += (_, _) =>
        {
            var active = Current()?.ExitDate is null;
            _employmentItem.Enabled = active && PermissionGate.Allows(Permissions.HumanResources.Manage);
            _salaryItem.Enabled = PermissionGate.Allows(Permissions.HumanResources.Salary);
            _certificateItem.Enabled = !active && PermissionGate.Allows(Permissions.HumanResources.Manage);
            _lettersMenu.Show(_letters, new Point(0, _letters.Height));
        };
        _employmentItem.Click += async (_, _) => await Guarded(() => LetterAsync(HrDocumentKind.EmploymentAttestation));
        _salaryItem.Click += async (_, _) => await Guarded(() => LetterAsync(HrDocumentKind.SalaryAttestation));
        _certificateItem.Click += async (_, _) => await Guarded(() => LetterAsync(HrDocumentKind.WorkCertificate));
        _settlement.Click += (_, _) => _settlementMenu.Show(_settlement, new Point(0, _settlement.Height));
        _computeItem.Click += async (_, _) => await Guarded(SettlementAsync);
        _receiptItem.Click += async (_, _) => await Guarded(ReceiptAsync);
        _signedItem.Click += async (_, _) => await Guarded(ReceiptSignedAsync);
        _grid.SelectionChanged += (_, _) => RefreshExitCaption();

        PermissionGate.Require(Permissions.HumanResources.Manage, _new, _edit, _exit);
        PermissionGate.Require([Permissions.HumanResources.Manage, Permissions.HumanResources.Salary], _letters);
        PermissionGate.Require(Permissions.HumanResources.Salary, _settlement);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not EmployeeRow row) return;

            if (row.ExitDate is not null)
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
            else if (row.ContractEndDate is { } end && end < DateOnly.FromDateTime(DateTime.Today))
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };
    }

    protected override void ApplyTexts()
    {
        _search.PlaceholderText = T("Personnel.SearchPlaceholder");
        _refresh.Text = T("Action.Refresh");
        _includeExited.Text = T("Personnel.IncludeExited");
        _new.Text = T("Personnel.New");
        _edit.Text = T("Personnel.Edit");
        _documents.Text = T("Personnel.Documents");
        _letters.Text = T("Personnel.Letters");
        _settlement.Text = T("Personnel.Settlement");
        _employmentItem.Text = T("HrDocument.EmploymentAttestation");
        _salaryItem.Text = T("HrDocument.SalaryAttestation");
        _certificateItem.Text = T("HrDocument.WorkCertificate");
        _computeItem.Text = T("Settlement.Compute");
        _receiptItem.Text = T("Settlement.PrintReceipt");
        _signedItem.Text = T("Settlement.RecordSigned");

        _grid.Column("number").HeaderText = T("Personnel.Number");
        _grid.Column("name").HeaderText = T("Field.Name");
        _grid.Column("job").HeaderText = T("Personnel.Job");
        _grid.Column("title").HeaderText = T("Personnel.JobTitle");
        _grid.Column("department").HeaderText = T("Field.Department");
        _grid.Column("contract").HeaderText = T("Personnel.ContractType");
        _grid.Column("hired").HeaderText = T("Personnel.HireDate");
        _grid.Column("end").HeaderText = T("Personnel.ContractEnd");
        _grid.Column("user").HeaderText = T("Personnel.LinkedUser");

        RefreshExitCaption();
        if (_rows.Count > 0) Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        _pending++;
        try
        {
            var result = await _personnel.GetEmployeesAsync(_includeExited.Checked, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the personnel list failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T("Message.UnexpectedError");
        }
        finally
        {
            _pending--;
        }
    }

    private void Render()
    {
        var term = _search.Text.Trim();
        var rows = term.Length == 0
            ? _rows
            : _rows.Where(r => r.FullName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                               || r.Number.Contains(term, StringComparison.OrdinalIgnoreCase)
                               || (r.JobTitle?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false));

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in rows)
        {
            var index = _grid.Rows.Add(
                row.Number,
                row.ExitDate is { } left ? $"{row.FullName}  ({T("Personnel.ExitedOn", left.ToString("dd/MM/yyyy"))})" : row.FullName,
                T($"EmployeeJob.{row.Job}"),
                row.JobTitle ?? string.Empty,
                row.DepartmentName ?? string.Empty,
                T($"ContractType.{row.ContractType}"),
                row.HireDate.ToString("dd/MM/yyyy"),
                row.ContractEndDate?.ToString("dd/MM/yyyy") ?? "—",
                row.UserName ?? string.Empty);

            _grid.Rows[index].Tag = row;
        }

        _grid.ResumeLayout(performLayout: true);
        _grid.RestoreFillWeights();

        _heading.Text = T("Personnel.Heading", _rows.Count(r => r.ExitDate is null));
        RefreshExitCaption();
    }

    private EmployeeRow? Current() => _grid.CurrentRow?.Tag as EmployeeRow;

    private void RefreshExitCaption()
    {
        var row = Current();
        _exit.Text = row?.ExitDate is not null ? T("Personnel.Rehire") : T("Personnel.RecordExit");
        _edit.Enabled = _documents.Enabled = _exit.Enabled = _letters.Enabled = row is not null;
        _settlement.Enabled = row?.ExitDate is not null;
    }

    private async Task OpenPdfAsync(Func<Task<Domain.Common.Result<HrDocument>>> load, string caption)
    {
        try
        {
            var document = await load();
            if (document.IsFailure) { _dialogs.ShowFailure(document); return; }

            var opened = await _output.OpenAsync(_renderer.Render(document.Value), "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, caption);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Printing an HR document failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private async Task LetterAsync(HrDocumentKind kind)
    {
        if (Current() is not { } row) return;
        await OpenPdfAsync(() => _documentsService.GetAsync(kind, row.EmployeeId), T($"HrDocument.{kind}"));
    }

    private async Task SettlementAsync()
    {
        if (Current() is not { ExitDate: not null } row) return;
        try
        {
            var detail = await _settlements.GetAsync(row.EmployeeId);
            if (detail.IsFailure) { _dialogs.ShowFailure(detail); return; }

            using var dialog = new SettlementDialog(Localizer, _dialogs, detail.Value);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var saved = await _settlements.SaveAsync(row.EmployeeId, dialog.Amounts);
            if (saved.IsFailure) { _dialogs.ShowFailure(saved); return; }

            _status.ForeColor = saved.Value == SettlementPayroll.Closed ? UiTheme.Abnormal : UiTheme.Normal;
            _status.Text = T($"Settlement.Saved.{saved.Value}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Computing a final settlement failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private async Task ReceiptAsync()
    {
        if (Current() is not { ExitDate: not null } row) return;
        await OpenPdfAsync(() => _settlements.ReceiptAsync(row.EmployeeId), T("Settlement.PrintReceipt"));
    }

    private async Task ReceiptSignedAsync()
    {
        if (Current() is not { ExitDate: { } exit } row) return;

        using var dialog = new SettlementSignedDialog(Localizer, _dialogs, $"{row.Number} {row.FullName}", exit);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var signed = await _settlements.SignAsync(row.EmployeeId, dialog.SignedOn);
        if (signed.IsFailure) { _dialogs.ShowFailure(signed); return; }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Settlement.SignedRecorded");
    }

    private async Task EditAsync(Guid? employeeId)
    {
        try
        {
            if (await PersonnelFile.EditAsync(this, _personnel, _dialogs, Localizer, employeeId))
            {
                _status.ForeColor = UiTheme.Normal;
                _status.Text = T("Personnel.Saved");
                await ActivateAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Editing a personnel file failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private async Task DocumentsAsync()
    {
        if (Current() is not { } row) return;
        await PersonnelFile.DocumentsAsync(this, _personnel, _output, _dialogs, Localizer, row.EmployeeId);
    }

    private async Task ExitOrRehireAsync()
    {
        if (Current() is not { } row) return;

        Domain.Common.Result result;

        if (row.ExitDate is null)
        {
            using var dialog = new EmployeeExitDialog(Localizer, _dialogs, $"{row.Number} {row.FullName}");
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            result = await _personnel.RecordExitAsync(row.EmployeeId, dialog.ExitDate, dialog.Reason, dialog.Comment);
        }
        else
        {
            if (!_dialogs.Confirm(T("Personnel.RehirePrompt", row.FullName), _exit.Text)) return;
            result = await _personnel.RehireAsync(row.EmployeeId, DateOnly.FromDateTime(DateTime.Today));
        }

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
        await ActivateAsync();
    }
}

/// <summary>
/// The habilitation matrix: employees down the side, activities across, the level and the review
/// date in each cell. The document an ISO 15189 assessor asks for first, exportable as it stands.
/// </summary>
public sealed class HabilitationView : LocalizedView
{
    private readonly PersonnelService _personnel;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<HabilitationView>? _logger;

    private readonly Label _heading = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _export = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();

    private readonly Button _grant = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _withdraw = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _detail = new();

    private HabilitationMatrix? _matrix;
    private List<(HabilitationActivity Activity, Guid? SubjectId, string? Label)> _columns = [];

    public HabilitationView(PersonnelService personnel, IDocumentOutput output, IUserDialogs dialogs,
                            ILocalizer localizer, ICultureManager cultureManager,
                            ILogger<HabilitationView>? logger = null)
        : base(localizer, cultureManager)
    {
        _personnel = personnel;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Habilitations");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.SectionTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 2);

        _refresh.Bounds = new Rectangle(0, 27, 110, UiTheme.ButtonHeight - 4);
        _export.Bounds = new Rectangle(120, 27, 190, UiTheme.ButtonHeight - 4);

        bar.Controls.AddRange([_heading, _refresh, _export]);

        // Cells, not rows: each cell is one decision.
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_grid);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = UiTheme.Surface };

        _detail.Font = UiTheme.Small;
        _detail.ForeColor = UiTheme.TextSecondary;
        _detail.AutoSize = false;
        _detail.Bounds = new Rectangle(0, 4, 1300, 20);

        _grant.Bounds = new Rectangle(0, 30, 230, UiTheme.ButtonHeight);
        _withdraw.Bounds = new Rectangle(240, 30, 230, UiTheme.ButtonHeight);

        actions.Controls.AddRange([_detail, _grant, _withdraw]);

        Controls.Add(host);
        Controls.Add(actions);
        Controls.Add(bar);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _export.Click += async (_, _) => await ExportAsync();
        _grant.Click += async (_, _) => await GrantAsync();
        _withdraw.Click += async (_, _) => await WithdrawAsync();
        _grid.CurrentCellChanged += (_, _) => ShowDetail();
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && !PermissionGate.IsBlocked(_grant)) await GrantAsync();
        };

        PermissionGate.Require(Permissions.HumanResources.ManageHabilitations, _grant, _withdraw);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 3) return;
            if (_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag is not HabilitationRow h) return;

            if (h.ReviewDueOn is { } due && due < DateOnly.FromDateTime(DateTime.Today))
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
            else if (h.Level == HabilitationLevel.InTraining)
            {
                e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
                e.CellStyle.ForeColor = UiTheme.Abnormal;
            }
            else
            {
                e.CellStyle!.ForeColor = UiTheme.Normal;
                e.CellStyle.Font = UiTheme.SmallBold;
            }
        };
    }

    protected override void ApplyTexts()
    {
        _refresh.Text = T("Action.Refresh");
        _export.Text = T("Habilitation.Export");
        _grant.Text = T("Habilitation.Grant");
        _withdraw.Text = T("Habilitation.Withdraw");
        if (_matrix is not null) Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _personnel.GetMatrixAsync(ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _matrix = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the habilitation matrix failed.");
            _detail.ForeColor = UiTheme.Critical;
            _detail.Text = T("Message.UnexpectedError");
        }
    }

    private string ColumnHeader((HabilitationActivity Activity, Guid? SubjectId, string? Label) column) =>
        column.Label is null ? T($"HabilitationActivity.{column.Activity}")
                             : $"{T($"HabilitationActivity.{column.Activity}")}\n{column.Label}";

    private string Cell(HabilitationRow h) =>
        $"{T($"HabilitationLevelShort.{h.Level}")}{(h.ReviewDueOn is { } due ? $" · {due:MM/yy}" : string.Empty)}";

    private void Render()
    {
        if (_matrix is null) return;

        // Every activity anyone holds gets a column; the collection activities are always there, as
        // they are the ones a new préleveur is placed on first.
        _columns =
        [
            .. _matrix.Habilitations.Select(h => (h.Activity, h.SubjectId, h.SubjectLabel))
                .Concat(new[] { HabilitationActivity.AdultCollection, HabilitationActivity.PaediatricCollection }
                            .Select(a => (Activity: a, SubjectId: (Guid?)null, SubjectLabel: (string?)null)))
                .Distinct()
                .OrderBy(c => c.Activity).ThenBy(c => c.Item3, StringComparer.CurrentCulture)
        ];

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        _grid.Columns.Clear();

        DataGridViewTextBoxColumn Column(string name, string header, int width, Font font) => new()
        {
            Name = name, HeaderText = header, Width = width, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = font }
        };

        _grid.Columns.Add(Column("number", T("Personnel.Number"), 70, UiTheme.Monospace));
        _grid.Columns.Add(Column("name", T("Field.Name"), 190, UiTheme.Body));
        _grid.Columns.Add(Column("job", T("Personnel.Job"), 110, UiTheme.Small));
        _grid.Columns[0].Frozen = _grid.Columns[1].Frozen = _grid.Columns[2].Frozen = true;

        for (var i = 0; i < _columns.Count; i++)
        {
            var column = Column($"a{i}", ColumnHeader(_columns[i]), 120, UiTheme.Small);
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(column);
        }

        foreach (var employee in _matrix.Employees)
        {
            var values = new object[3 + _columns.Count];
            values[0] = employee.Number;
            values[1] = employee.FullName;
            values[2] = T($"EmployeeJob.{employee.Job}");

            var held = _matrix.Habilitations.Where(h => h.EmployeeId == employee.EmployeeId).ToList();
            for (var i = 0; i < _columns.Count; i++)
            {
                var h = held.FirstOrDefault(x => x.Activity == _columns[i].Activity && x.SubjectId == _columns[i].SubjectId);
                values[3 + i] = h is null ? string.Empty : Cell(h);
            }

            var index = _grid.Rows.Add(values);
            _grid.Rows[index].Tag = employee;

            for (var i = 0; i < _columns.Count; i++)
                _grid.Rows[index].Cells[3 + i].Tag =
                    held.FirstOrDefault(x => x.Activity == _columns[i].Activity && x.SubjectId == _columns[i].SubjectId);
        }

        _grid.ResumeLayout(performLayout: true);

        var today = DateOnly.FromDateTime(DateTime.Today);
        _heading.Text = T("Habilitation.Heading", _matrix.Employees.Count, _matrix.Habilitations.Count,
                          _matrix.Habilitations.Count(h => h.ReviewDueOn is { } d && d < today));
        _export.Enabled = _matrix.Employees.Count > 0;
        ShowDetail();
    }

    private void ShowDetail()
    {
        var cell = _grid.CurrentCell;
        var employee = cell is null ? null : _grid.Rows[cell.RowIndex].Tag as EmployeeRow;

        _grant.Enabled = employee is not null;

        if (cell?.Tag is HabilitationRow h)
        {
            _withdraw.Enabled = true;
            _detail.ForeColor = UiTheme.TextSecondary;
            _detail.Text = T("Habilitation.Detail", T($"HabilitationLevel.{h.Level}"), h.GrantedOn.ToString("dd/MM/yyyy"),
                             h.GrantedBy, h.ReviewDueOn?.ToString("dd/MM/yyyy") ?? "—", h.Evidence ?? "—");
        }
        else
        {
            _withdraw.Enabled = false;
            _detail.Text = _matrix is { Habilitations.Count: 0 } ? T("Habilitation.Empty") : string.Empty;
        }
    }

    private async Task GrantAsync()
    {
        if (_grid.CurrentCell is not { } cell || _grid.Rows[cell.RowIndex].Tag is not EmployeeRow employee) return;

        var (activity, subject) = cell.ColumnIndex >= 3
            ? (_columns[cell.ColumnIndex - 3].Activity, _columns[cell.ColumnIndex - 3].SubjectId)
            : (HabilitationActivity.AdultCollection, (Guid?)null);

        var lookups = await _personnel.GetLookupsAsync(employee.EmployeeId);
        if (lookups.IsFailure) { _dialogs.ShowFailure(lookups); return; }

        using var dialog = new HabilitationGrantDialog(Localizer, _dialogs, $"{employee.Number} {employee.FullName}",
                                                       lookups.Value, activity, subject);

        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var granted = await _personnel.GrantAsync(employee.EmployeeId, dialog.Activity, dialog.SubjectId,
                                                      dialog.Level, dialog.GrantedOn, dialog.ReviewDueOn,
                                                      dialog.Evidence);
            if (granted.IsSuccess)
            {
                await ActivateAsync();
                return;
            }

            _dialogs.ShowFailure(granted, dialog.Text);
        }
    }

    private async Task WithdrawAsync()
    {
        if (_grid.CurrentCell is not { Tag: HabilitationRow h } cell) return;
        if (_grid.Rows[cell.RowIndex].Tag is not EmployeeRow employee) return;

        var reason = _dialogs.PromptForReason(
            T("Habilitation.WithdrawPrompt", T($"HabilitationActivity.{h.Activity}"), employee.FullName),
            _withdraw.Text);
        if (reason is null) return;

        var result = await _personnel.WithdrawAsync(employee.EmployeeId, h.HabilitationId, reason);
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        await ActivateAsync();
    }

    /// <summary>The matrix as CSV, one line per habilitation, for the accreditation file.</summary>
    private async Task ExportAsync()
    {
        if (_matrix is null) return;

        static string Csv(string? value) =>
            value is null ? string.Empty
            : value.IndexOfAny([';', '"', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', new[]
        {
            T("Personnel.Number"), T("Field.Name"), T("Personnel.Job"), T("Habilitation.Activity"),
            T("Habilitation.Subject"), T("Habilitation.Level"), T("Habilitation.GrantedOn"),
            T("Habilitation.GrantedBy"), T("Habilitation.ReviewDue"), T("Habilitation.Evidence")
        }.Select(Csv)));

        foreach (var employee in _matrix.Employees)
        foreach (var h in _matrix.Habilitations.Where(x => x.EmployeeId == employee.EmployeeId)
                                               .OrderBy(x => x.Activity))
        {
            csv.AppendLine(string.Join(';', new[]
            {
                employee.Number, employee.FullName, T($"EmployeeJob.{employee.Job}"),
                T($"HabilitationActivity.{h.Activity}"), h.SubjectLabel, T($"HabilitationLevel.{h.Level}"),
                h.GrantedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), h.GrantedBy,
                h.ReviewDueOn?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), h.Evidence
            }.Select(Csv)));
        }

        try
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            var opened = await _output.OpenAsync(bytes, "csv");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _export.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting the habilitation matrix failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }
}

/// <summary>
/// What HR must act on: documents and habilitations coming due, contracts ending, and the things
/// missing from a file altogether (hepatitis B for an exposed post, the secrecy clause, the
/// hiring medical visit, a biologist's registration with the Order).
/// </summary>
public sealed class PersonnelDeadlinesView : LocalizedView
{
    private static readonly int[] Horizons = [30, 60, 90, 180];

    private readonly PersonnelService _personnel;
    private readonly IUserDialogs _dialogs;
    private readonly IDocumentOutput _output;
    private readonly ILogger<PersonnelDeadlinesView>? _logger;

    private readonly Label _heading = new();
    private readonly ComboBox _horizon = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _documents = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);

    private IReadOnlyList<DeadlineRow> _rows = [];

    public PersonnelDeadlinesView(PersonnelService personnel, IDocumentOutput output, IUserDialogs dialogs,
                                  ILocalizer localizer, ICultureManager cultureManager,
                                  ILogger<PersonnelDeadlinesView>? logger = null)
        : base(localizer, cultureManager)
    {
        _personnel = personnel;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.PersonnelDeadlines");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.SectionTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 2);

        _horizon.DropDownStyle = ComboBoxStyle.DropDownList;
        _horizon.FlatStyle = FlatStyle.Flat;
        _horizon.Bounds = new Rectangle(0, 28, 200, UiTheme.InputHeight);
        _refresh.Bounds = new Rectangle(212, 27, 110, UiTheme.ButtonHeight - 4);

        bar.Controls.AddRange([_heading, _horizon, _refresh]);

        void Add(string name, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, Tag = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("state", 8, UiTheme.SmallBold);
        Add("due", 9, UiTheme.Monospace);
        Add("number", 7, UiTheme.Monospace);
        Add("name", 16);
        Add("job", 12, UiTheme.Small);
        Add("what", 20);
        Add("subject", 24, UiTheme.Small);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_grid);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UiTheme.Surface };
        _documents.Bounds = new Rectangle(0, 10, 220, UiTheme.ButtonHeight);
        _edit.Bounds = new Rectangle(230, 10, 220, UiTheme.ButtonHeight);
        actions.Controls.AddRange([_documents, _edit]);

        Controls.Add(host);
        Controls.Add(actions);
        Controls.Add(bar);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _horizon.SelectedIndexChanged += async (_, _) => { if (_horizon.Focused) await ActivateAsync(); };
        _documents.Click += async (_, _) => await OpenAsync(documents: true);
        _edit.Click += async (_, _) => await OpenAsync(documents: false);

        PermissionGate.Require(Permissions.HumanResources.Manage, _edit);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeadlineRow row) return;

            if (row.IsOverdue)
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
            else if (row.DueOn is { } due && due <= DateOnly.FromDateTime(DateTime.Today).AddDays(30))
            {
                e.CellStyle!.BackColor = UiTheme.AbnormalBackground;
                e.CellStyle.ForeColor = UiTheme.Abnormal;
            }
        };
    }

    protected override void ApplyTexts()
    {
        var selected = Math.Max(_horizon.SelectedIndex, 1);
        _horizon.Items.Clear();
        foreach (var days in Horizons) _horizon.Items.Add(T("Deadline.Horizon", days));
        _horizon.SelectedIndex = selected;

        _refresh.Text = T("Action.Refresh");
        _documents.Text = T("Personnel.Documents");
        _edit.Text = T("Personnel.Edit");

        _grid.Column("state").HeaderText = T("Field.Status");
        _grid.Column("due").HeaderText = T("Deadline.DueOn");
        _grid.Column("number").HeaderText = T("Personnel.Number");
        _grid.Column("name").HeaderText = T("Field.Name");
        _grid.Column("job").HeaderText = T("Personnel.Job");
        _grid.Column("what").HeaderText = T("Deadline.What");
        _grid.Column("subject").HeaderText = T("Deadline.Subject");

        if (_rows.Count > 0) Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var horizon = Horizons[Math.Max(_horizon.SelectedIndex, 0)];
            var result = await _personnel.GetDeadlinesAsync(horizon, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the personnel deadlines failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void Render()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var state = row.DueOn is null ? T("Deadline.Missing")
                      : row.IsOverdue ? T("Deadline.Overdue")
                      : T("Deadline.InDays", row.DueOn.Value.DayNumber - today.DayNumber);

            var subject = row.Kind == DeadlineKind.HabilitationReview && row.Activity is { } activity
                ? T($"HabilitationActivity.{activity}") + (row.Subject is null ? string.Empty : $" — {row.Subject}")
                : row.Subject ?? string.Empty;

            var index = _grid.Rows.Add(state, row.DueOn?.ToString("dd/MM/yyyy") ?? "—", row.Number, row.FullName,
                                       T($"EmployeeJob.{row.Job}"), T($"DeadlineKind.{row.Kind}"), subject);
            _grid.Rows[index].Tag = row;
        }

        _grid.ResumeLayout(performLayout: true);
        _grid.RestoreFillWeights();

        _heading.Text = T("Deadline.Heading", _rows.Count, _rows.Count(r => r.IsOverdue));
        _documents.Enabled = _edit.Enabled = _rows.Count > 0;
    }

    private async Task OpenAsync(bool documents)
    {
        if (_grid.CurrentRow?.Tag is not DeadlineRow row) return;

        try
        {
            var changed = documents
                ? await PersonnelFile.DocumentsAsync(this, _personnel, _output, _dialogs, Localizer, row.EmployeeId)
                : await PersonnelFile.EditAsync(this, _personnel, _dialogs, Localizer, row.EmployeeId);

            if (changed) await ActivateAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Opening a personnel file from the deadlines failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }
}
