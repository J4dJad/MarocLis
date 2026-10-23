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
    // A plain box: the search host draws its frame (an Input box keeps painting its own).
    private readonly TextBox _search = new() { Width = 260, BorderStyle = BorderStyle.None };
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
    private readonly ContextMenuStrip _exitMenu = new();
    private readonly ToolStripMenuItem _correctExitItem = new(), _cancelExitItem = new();
    private readonly ToolStripMenuItem _employmentItem = new(), _salaryItem = new(), _certificateItem = new();
    private readonly ToolStripMenuItem _computeItem = new(), _receiptItem = new(), _signedItem = new();
    private readonly Label _status = new();
    private readonly Label _title = new();
    private readonly QhAside _aside = new();

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
        Padding = QhPage.PagePadding;

        // Direction A, a list screen: the title and the count, Refresh and the one primary action
        // on the right; the search and the chip under them; the list in a card and the selected
        // file in an aside with its facts and the actions on it.
        var header = QhPage.Header(_title, _heading, _refresh, _new);
        var toolbar = QhPage.Toolbar(QhPage.Search(_search, 340), QhPage.Chip(_includeExited), _includeExited);

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

        _lettersMenu.Items.AddRange([_employmentItem, _salaryItem, _certificateItem]);
        _settlementMenu.Items.AddRange([_computeItem, _receiptItem, _signedItem]);
        _exitMenu.Items.AddRange([_correctExitItem, _cancelExitItem]);

        _aside.SetActions(_edit, _documents, _exit, _letters, _settlement);
        _aside.AddBelow(QhPage.StatusLine(_status));
        // The width of the panel and room for two lines: the account message names the account.
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _status.Height = 48;
        _status.TextAlign = ContentAlignment.TopLeft;

        Controls.Add(QhPage.Frame(2, header, toolbar, QhPage.WithAside(QhPage.Card(_grid), _aside)));
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
        _correctExitItem.Click += async (_, _) => await Guarded(CorrectExitAsync);
        _cancelExitItem.Click += async (_, _) => await Guarded(CancelExitAsync);
        _letters.Click += (_, _) =>
        {
            // Serving a notice is still working here: the attestation de travail holds until the
            // exit date, and the certificate can be prepared from the day the departure is known.
            var row = Current();
            _employmentItem.Enabled = row is not null && OnStaff(row) && PermissionGate.Allows(Permissions.HumanResources.Manage);
            _salaryItem.Enabled = PermissionGate.Allows(Permissions.HumanResources.Salary);
            _certificateItem.Enabled = row?.ExitDate is not null && PermissionGate.Allows(Permissions.HumanResources.Manage);
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
        _grid.SelectionChanged += (_, _) => ShowFile();

        PermissionGate.Require(Permissions.HumanResources.Manage, _new, _edit, _exit);
        PermissionGate.Require([Permissions.HumanResources.Manage, Permissions.HumanResources.Salary], _letters);
        PermissionGate.Require(Permissions.HumanResources.Salary, _settlement);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not EmployeeRow row) return;

            if (!OnStaff(row))
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
        _title.Text = T("Nav.Personnel");
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
        _correctExitItem.Text = T("Personnel.CorrectExit");
        _cancelExitItem.Text = T("Personnel.CancelExit");

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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the personnel list failed.");
            _status.ForeColor = UiTheme.Critical;
            _status.Text = T(FailureMessages.KeyFor(ex));
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
        // Kept on the file just edited, its departure just recorded or the language just switched.
        _grid.RebuildKeeping<EmployeeRow, Guid>(r => r.EmployeeId, () =>
        {
            _grid.Rows.Clear();

            foreach (var row in rows)
            {
                var index = _grid.Rows.Add(
                    row.Number,
                    row.ExitDate is { } left
                        ? $"{row.FullName}  ({T(OnStaff(row) ? "Personnel.LeavingOn" : "Personnel.ExitedOn", left.ToString("dd/MM/yyyy"))})"
                        : row.FullName,
                    T($"EmployeeJob.{row.Job}"),
                    row.JobTitle ?? string.Empty,
                    row.DepartmentName ?? string.Empty,
                    T($"ContractType.{row.ContractType}"),
                    row.HireDate.ToString("dd/MM/yyyy"),
                    row.ContractEndDate?.ToString("dd/MM/yyyy") ?? "—",
                    row.UserName ?? string.Empty);

                _grid.Rows[index].Tag = row;
            }
        });

        _grid.ResumeLayout(performLayout: true);
        _grid.RestoreFillWeights();

        _heading.Text = T("QualityHr.Personnel.Count", _rows.Count(OnStaff), _grid.Rows.Count);
        RefreshExitCaption();
        ShowFile();
    }

    /// <summary>The selected file in the aside: its name, post, where it stands and its key dates.</summary>
    private void ShowFile()
    {
        if (Current() is not { } row)
        {
            _aside.Clear(T("Nav.Personnel"));
            return;
        }

        (string, (Color, Color))? pill = row.ExitDate switch
        {
            { } exit when exit >= Today => (T("Personnel.LeavingOn", exit.ToString("dd/MM/yyyy")), QhTone.Warn),
            { } exit => (T("Personnel.ExitedOn", exit.ToString("dd/MM/yyyy")), QhTone.Muted),
            _ when row.ContractEndDate is { } end && end < Today => (T("Personnel.ContractEnd") + " " + end.ToString("dd/MM/yyyy"), QhTone.Crit),
            _ => (T($"ContractType.{row.ContractType}"), QhTone.Ok)
        };

        var subtitle = string.Join(" · ", new[] { row.JobTitle, row.DepartmentName }.Where(v => !string.IsNullOrWhiteSpace(v)));

        _aside.Show(row.FullName, subtitle, pill,
        [
            (T("Personnel.Number"), row.Number, true),
            (T("Personnel.Job"), T($"EmployeeJob.{row.Job}"), false),
            (T("Personnel.ContractType"), T($"ContractType.{row.ContractType}"), false),
            (T("Personnel.HireDate"), row.HireDate.ToString("dd/MM/yyyy"), true),
            (T("Personnel.ContractEnd"), row.ContractEndDate?.ToString("dd/MM/yyyy") ?? "—", true),
            (T("Personnel.LinkedUser"), row.UserName ?? "—", true)
        ]);
    }

    private EmployeeRow? Current() => _grid.CurrentRow?.Tag as EmployeeRow;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>On the staff today: no departure, or one not reached yet (the notice being served).</summary>
    private static bool OnStaff(EmployeeRow row) => PersonnelRules.OnStaff(row, Today);

    /// <summary>
    /// The departure button follows the file: record an exit; correct or withdraw one not reached
    /// yet; re-hire once it has taken effect, the only time a re-hire can succeed.
    /// </summary>
    private void RefreshExitCaption()
    {
        var row = Current();
        _exit.Text = row?.ExitDate switch
        {
            null => T("Personnel.RecordExit"),
            { } exit when exit >= Today => T("Personnel.ChangeExit"),
            _ => T("Personnel.Rehire")
        };
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
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
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
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
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
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
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

        switch (row.ExitDate)
        {
            case null:
            {
                using var dialog = new EmployeeExitDialog(Localizer, _dialogs, $"{row.Number} {row.FullName}",
                                                          linkedUserName: row.UserName);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var recorded = await _personnel.RecordExitAsync(row.EmployeeId, dialog.ExitDate, dialog.Reason, dialog.Comment);
                if (recorded.IsFailure) { _dialogs.ShowFailure(recorded); return; }
                await ActivateAsync();
                ShowAccountOutcome(recorded.Value);
                return;
            }
            case { } exit when exit >= Today:
                // Not reached yet: corrected or withdrawn, not re-hired.
                _exitMenu.Show(_exit, new Point(0, _exit.Height));
                return;
            case { } exit:
            {
                using var dialog = new EmployeeRehireDialog(Localizer, _dialogs, $"{row.Number} {row.FullName}", exit);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                result = await _personnel.RehireAsync(row.EmployeeId, dialog.RehireDate);
                break;
            }
        }

        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
        await ActivateAsync();
    }

    /// <summary>
    /// Says on the status line what the departure did to the linked LIS account: deactivated now,
    /// to be on the exit date, left active as the last administrator, or still inactive.
    /// </summary>
    private void ShowAccountOutcome(DepartedAccountOutcome? outcome)
    {
        if (outcome is null) return;

        var date = outcome.DeactivationDate?.ToString("dd/MM/yyyy") ?? string.Empty;
        (_status.ForeColor, _status.Text) = outcome.State switch
        {
            DepartedAccountState.Deactivated => (UiTheme.Normal, T("Personnel.AccountDeactivated", outcome.UserName)),
            DepartedAccountState.Scheduled => (UiTheme.Normal, T("Personnel.AccountDeactivationScheduled", outcome.UserName, date)),
            DepartedAccountState.KeptLastAdministrator => (UiTheme.Critical, T("Personnel.AccountKeptLastAdministrator", outcome.UserName)),
            _ => (UiTheme.Abnormal, T("Personnel.AccountStillInactive", outcome.UserName))
        };
    }

    private async Task CorrectExitAsync()
    {
        if (Current() is not { ExitDate: { } exit, ExitReason: { } reason } row) return;
        try
        {
            var detail = await _personnel.GetEmployeeAsync(row.EmployeeId);
            if (detail.IsFailure) { _dialogs.ShowFailure(detail); return; }

            using var dialog = new EmployeeExitDialog(Localizer, _dialogs, $"{row.Number} {row.FullName}",
                                                      (exit, reason, detail.Value.ExitComment), row.UserName);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var result = await _personnel.CorrectExitAsync(row.EmployeeId, dialog.ExitDate, dialog.Reason, dialog.Comment);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
            await ActivateAsync();
            ShowAccountOutcome(result.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Correcting a departure failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task CancelExitAsync()
    {
        if (Current() is not { ExitDate: { } exit } row) return;
        try
        {
            var reason = _dialogs.PromptForReason(T("Personnel.CancelExitPrompt", row.FullName, exit.ToString("dd/MM/yyyy")),
                                                  T("Personnel.CancelExit"));
            if (reason is null) return;

            var result = await _personnel.CancelExitAsync(row.EmployeeId, reason);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }
            await ActivateAsync();
            ShowAccountOutcome(result.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Withdrawing a departure failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
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
    private readonly Label _title = new();

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
        Padding = QhPage.PagePadding;

        // Direction A: the title and the count, Refresh and the export on the right; the matrix in a
        // card; under it the selected cell — what it says, Withdraw, and Grant at the far end.
        var header = QhPage.Header(_title, _heading, _refresh, _export);

        // Cells, not rows: each cell is one decision.
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Dock = DockStyle.Fill;

        var body = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        body.Controls.Add(_grid);
        body.Controls.Add(new QhSelectionBar(_detail, _withdraw, _grant));

        Controls.Add(QhPage.Frame(1, header, QhPage.Card(body)));
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
        _title.Text = T("Nav.Habilitations");
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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the habilitation matrix failed.");
            _detail.ForeColor = UiTheme.Critical;
            _detail.Text = T(FailureMessages.KeyFor(ex));
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

        // The cell the operator was on, put back after the rebuild: a grant or a withdrawal used to
        // send the matrix back to its first employee, with the detail and the actions beside it.
        var keptEmployee = _grid.CurrentCell is { } before ? (_grid.Rows[before.RowIndex].Tag as EmployeeRow)?.EmployeeId : null;
        (HabilitationActivity, Guid?)? keptColumn = _grid.CurrentCell is { ColumnIndex: >= 3 } c && c.ColumnIndex - 3 < _columns.Count
            ? (_columns[c.ColumnIndex - 3].Activity, _columns[c.ColumnIndex - 3].SubjectId)
            : null;

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

        if (keptEmployee is { } employeeId
            && _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as EmployeeRow)?.EmployeeId == employeeId) is { } kept)
        {
            var column = keptColumn is { } key ? _columns.FindIndex(x => x.Activity == key.Item1 && x.SubjectId == key.Item2) : -1;
            try
            {
                _grid.CurrentCell = kept.Cells[column >= 0 ? 3 + column : 0];
            }
            catch (InvalidOperationException)
            {
                // Mid-layout: the first row stays current.
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        _heading.Text = T("QualityHr.Habilitations.Count", _matrix.Employees.Count, _matrix.Habilitations.Count,
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
            _withdraw.Text, maxLength: 500);
        if (reason is null) return;

        var result = await _personnel.WithdrawAsync(employee.EmployeeId, h.HabilitationId, reason);
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        await ActivateAsync();
    }

    /// <summary>The matrix as CSV, one line per habilitation, for the accreditation file.</summary>
    private async Task ExportAsync()
    {
        if (_matrix is null) return;

        // Names and evidence are typed by users: the shared cell neutralises formulas for Excel.
        static string Csv(string? value) => MarocLIS.Application.Inventory.CsvCell.Of(value);

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
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
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
    private readonly Label _title = new();

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
        Padding = QhPage.PagePadding;

        // Direction A, a work queue: the title and the count, Refresh on the right; the horizon
        // under them; the list in a card, its state as a pill; the two ways into the file in the
        // bar under it, the documents (the usual answer) at the far end.
        var header = QhPage.Header(_title, _heading, _refresh);

        _horizon.DropDownStyle = ComboBoxStyle.DropDownList;
        _horizon.FlatStyle = FlatStyle.Flat;
        var toolbar = QhPage.Toolbar(QhPage.Filter(_horizon, 200));

        void Add(string name, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, Tag = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("state", 9, UiTheme.SmallBold);
        Add("due", 9, UiTheme.Monospace);
        Add("number", 7, UiTheme.Monospace);
        Add("name", 16);
        Add("job", 12, UiTheme.Small);
        Add("what", 20);
        Add("subject", 24, UiTheme.Small);

        _grid.Dock = DockStyle.Fill;
        var body = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        body.Controls.Add(_grid);
        body.Controls.Add(new QhSelectionBar(null, _edit, _documents));

        Controls.Add(QhPage.Frame(2, header, toolbar, QhPage.Card(body)));
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _horizon.SelectedIndexChanged += async (_, _) => { if (_horizon.Focused) await ActivateAsync(); };
        _documents.Click += async (_, _) => await OpenAsync(documents: true);
        _edit.Click += async (_, _) => await OpenAsync(documents: false);

        PermissionGate.Require(Permissions.HumanResources.Manage, _edit);

        // The state as a pill (overdue or missing in red, within 30 days in amber), the due date in
        // the same ink: the row itself stays white, as in every list of direction A.
        QhGridPills.Attach(_grid, "state", r => r.Tag is DeadlineRow row ? Tone(row) : null);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not DeadlineRow row) return;
            if (_grid.Columns[e.ColumnIndex].Name != "due") return;

            if (row.IsOverdue || row.DueOn is null) e.CellStyle!.ForeColor = UiTheme.Critical;
            else if (row.DueOn is { } due && due <= DateOnly.FromDateTime(DateTime.Today).AddDays(30))
                e.CellStyle!.ForeColor = UiTheme.Abnormal;
        };
    }

    private static (Color Back, Color Fore) Tone(DeadlineRow row) =>
        row.IsOverdue || row.DueOn is null ? QhTone.Crit
        : row.DueOn.Value <= DateOnly.FromDateTime(DateTime.Today).AddDays(30) ? QhTone.Warn
        : QhTone.Info;

    protected override void ApplyTexts()
    {
        _title.Text = T("Nav.PersonnelDeadlines");
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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the personnel deadlines failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private void Render()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        _grid.SuspendLayout();
        // Kept on the deadline just dealt with: its file edited, it moves or leaves the list.
        _grid.RebuildKeeping<DeadlineRow, (Guid, DeadlineKind, HabilitationActivity?, string?)>(
            r => (r.EmployeeId, r.Kind, r.Activity, r.Subject), () =>
        {
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
        });

        _grid.ResumeLayout(performLayout: true);
        _grid.RestoreFillWeights();

        _heading.Text = T("QualityHr.Deadlines.Count", _rows.Count, _rows.Count(r => r.IsOverdue));
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
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }
}
