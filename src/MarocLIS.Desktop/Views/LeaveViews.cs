using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Leave and absences: the requests of the year, waiting ones first, with who else in the same
/// department is away on those days, and the decisions. An employee without an HR right sees and
/// asks for their own leave only.
/// </summary>
public sealed class LeaveView : LocalizedView
{
    private readonly LeaveService _leave;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<LeaveView>? _logger;

    private readonly Label _heading = new();
    private readonly ComboBox _year = new();
    private readonly CheckBox _pendingOnly = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();

    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _approve = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refuse = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _cancel = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _balances = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _holidays = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _detail = new();
    private readonly Label _title = new();
    private readonly Label _yearCaption = new();

    private IReadOnlyList<LeaveRow> _rows = [];

    public LeaveView(LeaveService leave, IUserDialogs dialogs, ILocalizer localizer, ICultureManager cultureManager,
                     ILogger<LeaveView>? logger = null)
        : base(localizer, cultureManager)
    {
        _leave = leave;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Leave");

    private int Year => _year.SelectedItem is int y ? y : DateTime.Today.Year;

    private void BuildLayout()
    {
        Padding = QhPage.PagePadding;

        // Direction A, a work queue: the title and the count; Refresh and the one primary action
        // (a new request) on the right; the year, the chip, the balances and the holidays under
        // them; the requests in a card, their status as a pill; under the list the selected request
        // and the decisions on it, Approve at the far end.
        var header = QhPage.Header(_title, _heading, _refresh, _new);

        _year.DropDownStyle = ComboBoxStyle.DropDownList;
        _year.FlatStyle = FlatStyle.Flat;
        var thisYear = DateTime.Today.Year;
        for (var y = thisYear - 2; y <= thisYear + 1; y++) _year.Items.Add(y);
        _year.SelectedItem = thisYear;

        var toolbar = QhPage.Toolbar(QhPage.Caption(_yearCaption), QhPage.Filter(_year, 90),
                                     QhPage.Chip(_pendingOnly), _pendingOnly,
                                     QhPage.Fit(_balances), QhPage.Fit(_holidays));

        void Add(string name, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, Tag = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("status", 9, UiTheme.SmallBold);
        Add("name", 16);
        Add("department", 10, UiTheme.Small);
        Add("type", 14, UiTheme.Small);
        Add("from", 10, UiTheme.Monospace);
        Add("to", 10, UiTheme.Monospace);
        Add("days", 6, UiTheme.Monospace);
        Add("proof", 7, UiTheme.Small);
        Add("away", 7, UiTheme.Small);
        Add("decision", 16, UiTheme.Small);

        _grid.Dock = DockStyle.Fill;
        var body = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        body.Controls.Add(_grid);
        body.Controls.Add(new QhSelectionBar(_detail, _cancel, _refuse, _approve));

        Controls.Add(QhPage.Frame(2, header, toolbar, QhPage.Card(body)));
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _year.SelectedIndexChanged += async (_, _) => { if (_year.Focused) await ActivateAsync(); };
        _pendingOnly.CheckedChanged += async (_, _) => await ActivateAsync();

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _new.Click += async (_, _) => await RunBusyAsync(NewAsync);
        _approve.Click += async (_, _) => await RunBusyAsync(ApproveAsync);
        _refuse.Click += async (_, _) => await RunBusyAsync(RefuseAsync);
        _cancel.Click += async (_, _) => await RunBusyAsync(CancelAsync);
        _balances.Click += async (_, _) => await RunBusyAsync(BalancesAsync);
        _holidays.Click += async (_, _) => await RunBusyAsync(HolidaysAsync);
        _grid.SelectionChanged += (_, _) => ShowDetail();

        PermissionGate.Require([Permissions.HumanResources.RequestLeave, Permissions.HumanResources.Manage],
                               _new, _cancel);
        PermissionGate.Require(Permissions.HumanResources.ApproveLeave, _approve, _refuse);

        // The status as a pill (waiting in amber, approved in green, refused or cancelled muted);
        // a closed request keeps its muted ink across the row.
        QhGridPills.Attach(_grid, "status", r => r.Tag is LeaveRow row ? StatusTone(row.Status) : null);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not LeaveRow row) return;

            if (row.Status is LeaveStatus.Refused or LeaveStatus.Cancelled)
                e.CellStyle!.ForeColor = UiTheme.TextMuted;
            else if (row.Status == LeaveStatus.Pending && e.ColumnIndex == _grid.Column("name").Index)
                e.CellStyle!.Font = UiTheme.BodyBold;

            if (e.ColumnIndex == _grid.Column("proof").Index && !row.JustificationProvided
                && LeaveRules.RequiresJustification(row.Type) && row.Status == LeaveStatus.Pending)
                e.CellStyle!.ForeColor = UiTheme.Critical;
        };
    }

    private static (Color Back, Color Fore)? StatusTone(LeaveStatus status) => status switch
    {
        LeaveStatus.Pending => QhTone.Warn,
        LeaveStatus.Approved => QhTone.Ok,
        _ => QhTone.Muted
    };

    protected override void ApplyTexts()
    {
        _title.Text = T("Nav.Leave");
        _yearCaption.Text = T("Eqa.Year");
        _pendingOnly.Text = T("Leave.PendingOnly");
        _refresh.Text = T("Action.Refresh");
        _new.Text = T("Leave.New");
        _approve.Text = T("Leave.Approve");
        _refuse.Text = T("Leave.Refuse");
        _cancel.Text = T("Leave.Cancel");
        _balances.Text = T("Leave.Balances");
        _holidays.Text = T("Leave.Holidays");

        _grid.Column("status").HeaderText = T("Field.Status");
        _grid.Column("name").HeaderText = T("Field.Name");
        _grid.Column("department").HeaderText = T("Field.Department");
        _grid.Column("type").HeaderText = T("Leave.Type");
        _grid.Column("from").HeaderText = T("Leave.From");
        _grid.Column("to").HeaderText = T("Leave.To");
        _grid.Column("days").HeaderText = T("Leave.Days");
        _grid.Column("proof").HeaderText = T("Leave.Proof");
        _grid.Column("away").HeaderText = T("Leave.ColleaguesAway");
        _grid.Column("decision").HeaderText = T("Leave.Decision");

        Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _leave.GetRequestsAsync(_pendingOnly.Checked, Year, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the leave requests failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private static string Half(DateOnly date, bool half, string halfText) =>
        half ? $"{date:dd/MM/yyyy} {halfText}" : date.ToString("dd/MM/yyyy");

    private void Render()
    {
        var selected = Current()?.LeaveRequestId;

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var proof = !LeaveRules.RequiresJustification(row.Type) ? string.Empty
                      : row.JustificationProvided ? T("Leave.ProofGiven") : T("Leave.ProofMissing");

            var decision = row.Status switch
            {
                LeaveStatus.Pending => T("Leave.SubmittedOn", row.SubmittedAt.LocalDateTime.ToString("dd/MM/yyyy")),
                _ => string.Join(" · ", new[] { row.DecidedBy, row.DecisionComment }.Where(s => !string.IsNullOrEmpty(s)))
            };

            var index = _grid.Rows.Add(T($"LeaveStatus.{row.Status}"), $"{row.EmployeeNumber} {row.EmployeeName}",
                                       row.DepartmentName ?? "—", T($"LeaveType.{row.Type}"),
                                       Half(row.StartDate, row.StartsAfternoon, T("Leave.AfternoonShort")),
                                       Half(row.EndDate, row.EndsMorning, T("Leave.MorningShort")),
                                       row.Days.ToString("0.#"), proof,
                                       row.ColleaguesAway == 0 ? "—" : row.ColleaguesAway.ToString(), decision);
            _grid.Rows[index].Tag = row;
            if (row.LeaveRequestId == selected) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        _grid.RestoreFillWeights();

        _heading.Text = T("QualityHr.Leave.Count", _rows.Count(r => r.Status == LeaveStatus.Pending),
                          _rows.Count(r => r.Status == LeaveStatus.Approved));
        ShowDetail();
    }

    private LeaveRow? Current() => _grid.CurrentRow?.Tag as LeaveRow;

    private void ShowDetail()
    {
        var row = Current();
        var pending = row?.Status == LeaveStatus.Pending;

        _approve.Enabled = _refuse.Enabled = pending;
        _cancel.Enabled = row is { IsLive: true } && (row.Status == LeaveStatus.Pending
                                                      || row.StartDate > DateOnly.FromDateTime(DateTime.Today));

        _detail.Text = row is null ? string.Empty
            : string.Join(" · ", new[]
              {
                  T($"LeaveType.{row.Type}"),
                  T("Leave.DaysCounted", row.Days.ToString("0.#")),
                  row.Reason,
                  row.ColleaguesAway > 0 ? T("Leave.AwayWarning", row.ColleaguesAway, row.DepartmentName) : null
              }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private async Task NewAsync()
    {
        try
        {
            var balances = await _leave.GetBalancesAsync();
            if (balances.IsFailure) { _dialogs.ShowFailure(balances); return; }

            var own = await _leave.OwnEmployeeIdAsync();
            var people = _leave.CanActForOthers ? balances.Value : balances.Value.Where(b => b.EmployeeId == own).ToList();
            if (people.Count == 0)
            {
                // HR acting for others with nobody on file needs another hint than an unlinked account.
                _dialogs.Warn(T(_leave.CanActForOthers ? "Leave_NoEmployees" : "Leave_NoPersonnelFile"), _new.Text);
                return;
            }

            using var dialog = new LeaveRequestDialog(Localizer, _dialogs, _leave, people, own,
                                                      PermissionGate.Allows(Permissions.HumanResources.Manage));

            while (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var submitted = await _leave.SubmitAsync(dialog.Submission);
                if (submitted.IsSuccess)
                {
                    await ActivateAsync();
                    return;
                }

                _dialogs.ShowFailure(submitted, dialog.Text);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Submitting a leave request failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private async Task ApproveAsync()
    {
        if (Current() is not { Status: LeaveStatus.Pending } row) return;

        var question = T("Leave.ApprovePrompt", T($"LeaveType.{row.Type}"), row.EmployeeName,
                         row.StartDate.ToString("dd/MM/yyyy"), row.EndDate.ToString("dd/MM/yyyy"), row.Days.ToString("0.#"));
        if (row.ColleaguesAway > 0) question += "\n\n" + T("Leave.AwayWarning", row.ColleaguesAway, row.DepartmentName);
        if (!_dialogs.Confirm(question, _approve.Text)) return;

        var done = await _leave.ApproveAsync(row.LeaveRequestId, null);
        if (done.IsFailure) { _dialogs.ShowFailure(done, _approve.Text); return; }
        await ActivateAsync();
    }

    private async Task RefuseAsync()
    {
        if (Current() is not { Status: LeaveStatus.Pending } row) return;

        var reason = _dialogs.PromptForReason(T("Leave.RefusePrompt", row.EmployeeName), _refuse.Text, maxLength: 500);
        if (reason is null) return;

        var done = await _leave.RefuseAsync(row.LeaveRequestId, reason);
        if (done.IsFailure) { _dialogs.ShowFailure(done, _refuse.Text); return; }
        await ActivateAsync();
    }

    private async Task CancelAsync()
    {
        if (Current() is not { IsLive: true } row) return;

        var reason = _dialogs.PromptForText(T("Leave.CancelPrompt", T($"LeaveType.{row.Type}"),
                                              row.StartDate.ToString("dd/MM/yyyy")), _cancel.Text, maxLength: 500);
        if (reason is null) return;

        var done = await _leave.CancelAsync(row.LeaveRequestId, reason);
        if (done.IsFailure) { _dialogs.ShowFailure(done, _cancel.Text); return; }
        await ActivateAsync();
    }

    private async Task BalancesAsync()
    {
        using var dialog = new LeaveBalancesDialog(Localizer, _dialogs, _leave);
        await dialog.LoadAsync();
        dialog.ShowDialog(this);
    }

    private async Task HolidaysAsync()
    {
        using var dialog = new HolidaysDialog(Localizer, _dialogs, _leave, Year);
        await dialog.LoadAsync();
        dialog.ShowDialog(this);
        if (dialog.Changed) await ActivateAsync();
    }
}

/// <summary>
/// A leave request: who, which kind, from when to when, half days at either end. The days the
/// request will count are shown as the dates change, Sundays and public holidays left out.
/// </summary>
internal sealed class LeaveRequestDialog : MasterDataEditor
{
    private readonly LeaveService _leave;
    private readonly IReadOnlyList<LeaveBalanceRow> _people;
    private readonly ComboBox _employee;
    private readonly ComboBox _type;
    private readonly DateTimePicker _start = new(), _end = new();
    private readonly CheckBox _startsAfternoon, _endsMorning, _proof;
    private readonly TextBox _reason;
    private readonly Label _count;
    private readonly Label _balance;
    private int _previewVersion;

    internal LeaveSubmission Submission { get; private set; } = null!;

    internal LeaveRequestDialog(ILocalizer localizer, IUserDialogs dialogs, LeaveService leave,
                                IReadOnlyList<LeaveBalanceRow> people, Guid? own, bool hrTypes)
        : base(localizer, dialogs, "Leave.New", null, 600, 420)
    {
        _leave = leave;
        _people = people;

        _employee = Lookup("Leave.Employee", 20, 10, 560,
                           people.Select(p => (p.EmployeeId, $"{p.Number} — {p.FullName}")),
                           own ?? people[0].EmployeeId, allowNone: false);
        _employee.Enabled = people.Count > 1;

        var types = Enum.GetValues<LeaveType>().Where(t => hrTypes || !LeaveRules.IsHrOnly(t));
        _type = Choice("Leave.Type", 20, 62, 270, LeaveType.Annual, "LeaveType", types);

        Label("Leave.From", 20, 114);
        _start.Bounds = new Rectangle(20, 132, 180, UiTheme.InputHeight);
        _start.Format = DateTimePickerFormat.Short;
        _start.Value = DateTime.Today.AddDays(1);
        Controls.Add(_start);
        _startsAfternoon = Check("Leave.StartsAfternoon", 210, 136, false);

        Label("Leave.To", 20, 166);
        _end.Bounds = new Rectangle(20, 184, 180, UiTheme.InputHeight);
        _end.Format = DateTimePickerFormat.Short;
        _end.Value = DateTime.Today.AddDays(1);
        Controls.Add(_end);
        _endsMorning = Check("Leave.EndsMorning", 210, 188, false);

        _reason = TextField("Leave.Reason", 20, 218, 560);
        _reason.MaxLength = 500;
        _proof = Check("Leave.ProofProvided", 20, 274, false);

        _count = new Label
        {
            Font = UiTheme.BodyBold, ForeColor = UiTheme.Accent, AutoSize = false,
            Bounds = new Rectangle(310, 80, 270, 22)
        };
        _balance = new Label
        {
            Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false,
            Bounds = new Rectangle(310, 102, 270, 20)
        };
        Controls.AddRange([_count, _balance]);

        Notice(20, 302, 560, 54, "Leave.RulesNotice");
        Buttons(366, 600, "Leave.Submit");

        _type.SelectedIndexChanged += async (_, _) => await PreviewAsync();
        _employee.SelectedIndexChanged += async (_, _) => await PreviewAsync();
        _start.ValueChanged += async (_, _) =>
        {
            if (_end.Value < _start.Value) _end.Value = _start.Value;
            await PreviewAsync();
        };
        _end.ValueChanged += async (_, _) => await PreviewAsync();
        _startsAfternoon.CheckedChanged += async (_, _) => await PreviewAsync();
        _endsMorning.CheckedChanged += async (_, _) => await PreviewAsync();

        Shown += async (_, _) => await PreviewAsync();
        QhDialog.Dress(this, localizer, null);
    }

    private LeaveType Type => Selected<LeaveType>(_type);

    private async Task PreviewAsync()
    {
        var version = ++_previewVersion;
        var start = DateOnly.FromDateTime(_start.Value.Date);
        var end = DateOnly.FromDateTime(_end.Value.Date);

        var days = await _leave.PreviewDaysAsync(Type, start, end, _startsAfternoon.Checked, _endsMorning.Checked);
        if (version != _previewVersion || IsDisposed) return;

        var count = days.IsSuccess ? days.Value : 0m;
        _count.Text = Localizer.Format(LeaveRules.CountsCalendarDays(Type) ? "Leave.CalendarDays" : "Leave.WorkingDays",
                                       count.ToString("0.#"));

        var person = _people.FirstOrDefault(p => p.EmployeeId == SelectedId(_employee));
        if (LeaveRules.DebitsBalance(Type) && person is not null)
        {
            _balance.ForeColor = count > person.Available ? UiTheme.Critical : UiTheme.TextSecondary;
            _balance.Text = Localizer.Format("Leave.AvailableBalance", person.Available.ToString("0.#"));
        }
        else
        {
            _balance.ForeColor = UiTheme.TextSecondary;
            _balance.Text = LeaveRules.Entitlement(Type) is { } allowed
                ? Localizer.Format("Leave.Entitlement", allowed.ToString("0.#"))
                : string.Empty;
        }
    }

    protected override void Accept()
    {
        Submission = new LeaveSubmission
        {
            EmployeeId = SelectedId(_employee),
            Type = Type,
            StartDate = DateOnly.FromDateTime(_start.Value.Date),
            EndDate = DateOnly.FromDateTime(_end.Value.Date),
            StartsAfternoon = _startsAfternoon.Checked,
            EndsMorning = _endsMorning.Checked,
            Reason = Blank(_reason),
            JustificationProvided = _proof.Checked
        };
        DialogResult = DialogResult.OK;
    }
}

/// <summary>The annual-leave accounts: acquired, taken, waiting, left; HR corrects them here.</summary>
internal sealed class LeaveBalancesDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly LeaveService _leave;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _adjust = UiTheme.PrimaryButton(string.Empty);

    internal LeaveBalancesDialog(ILocalizer localizer, IUserDialogs dialogs, LeaveService leave)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _leave = leave;

        Text = localizer["Leave.Balances"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(900, 500);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        void Add(string name, string key, int weight, Font? font = null, bool right = false)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = localizer[key], FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add("number", "Personnel.Number", 9, UiTheme.Monospace);
        Add("name", "Field.Name", 22);
        Add("yearly", "Leave.YearlyEntitlement", 11, UiTheme.Monospace, true);
        Add("credited", "Leave.Credited", 10, UiTheme.Monospace, true);
        Add("taken", "Leave.Taken", 10, UiTheme.Monospace, true);
        Add("pending", "Leave.PendingDays", 10, UiTheme.Monospace, true);
        Add("balance", "Leave.Balance", 10, UiTheme.Monospace, true);
        Add("available", "Leave.Available", 10, UiTheme.Monospace, true);

        var host = new Panel { Bounds = new Rectangle(12, 12, 876, 380) };
        _grid.Dock = DockStyle.Fill;
        host.Controls.Add(_grid);

        var notice = new Label
        {
            Text = localizer["Leave.BalancesNotice"], Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
            AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(12, 398, 876, 36)
        };

        _adjust.Text = localizer["Leave.Adjust"];
        _adjust.Bounds = new Rectangle(12, 448, 220, UiTheme.ButtonHeight);
        _adjust.Click += async (_, _) => await AdjustAsync();

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Bounds = new Rectangle(738, 448, 150, UiTheme.ButtonHeight);
        close.DialogResult = DialogResult.Cancel;

        Controls.AddRange([host, notice, _adjust, close]);
        CancelButton = close;

        PermissionGate.Require(Permissions.HumanResources.Manage, _adjust);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not LeaveBalanceRow row) return;
            if (e.ColumnIndex == _grid.Columns["available"]!.Index && row.Available < 0)
                e.CellStyle!.ForeColor = UiTheme.Critical;
        };
        QhDialog.Dress(this, localizer, null);
    }

    internal async Task LoadAsync()
    {
        var result = await _leave.GetBalancesAsync();
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _grid.Rows.Clear();
        foreach (var b in result.Value)
        {
            var index = _grid.Rows.Add(b.Number, b.FullName, b.AnnualEntitlement.ToString("0.#"),
                                       b.Credited.ToString("0.##"), b.Taken.ToString("0.#"),
                                       b.Pending.ToString("0.#"), b.Balance.ToString("0.##"),
                                       b.Available.ToString("0.##"));
            _grid.Rows[index].Tag = b;
        }

        _adjust.Enabled = result.Value.Count > 0;
    }

    private async Task AdjustAsync()
    {
        if (_grid.CurrentRow?.Tag is not LeaveBalanceRow row) return;

        using var editor = new LeaveAdjustmentDialog(_localizer, _dialogs, $"{row.Number} {row.FullName}");
        while (editor.ShowDialog(this) == DialogResult.OK)
        {
            var done = await _leave.AdjustAsync(row.EmployeeId, editor.Days, editor.Comment);
            if (done.IsSuccess)
            {
                await LoadAsync();
                return;
            }

            _dialogs.ShowFailure(done, editor.Text);
        }
    }
}

internal sealed class LeaveAdjustmentDialog : MasterDataEditor
{
    private readonly TextBox _days, _comment;

    internal decimal Days { get; private set; }
    internal string Comment => _comment.Text.Trim();

    internal LeaveAdjustmentDialog(ILocalizer localizer, IUserDialogs dialogs, string employee)
        : base(localizer, dialogs, "Leave.Adjust", employee, 520, 230)
    {
        _days = TextField("Leave.AdjustDays", 20, 10, 140, mono: true, right: true);
        Numeric(EditorValues.MeasureMaxLength, _days);
        _comment = TextField("Leave.AdjustReason", 180, 10, 320);
        _comment.MaxLength = 500;
        Notice(20, 66, 480, 90, "Leave.AdjustNotice");
        Buttons(172, 520);
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept()
    {
        if (DecimalOrNull(_days) is not { } days || days == 0m)
        {
            Dialogs.Warn(Localizer["Leave_AdjustmentInvalid"], Text);
            return;
        }

        Days = days;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>
/// The public holidays of a year. The civil ones are filled in; the religious ones (Aïd al-Fitr,
/// Aïd al-Adha, 1er Moharram, Aïd al-Mawlid) follow the moon and are entered when announced.
/// </summary>
internal sealed class HolidaysDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;
    private readonly LeaveService _leave;
    private readonly int _year;
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly Button _add = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _remove = UiTheme.SecondaryButton(string.Empty);

    internal bool Changed { get; private set; }

    internal HolidaysDialog(ILocalizer localizer, IUserDialogs dialogs, LeaveService leave, int year)
    {
        _localizer = localizer;
        _dialogs = dialogs;
        _leave = leave;
        _year = year;

        Text = $"{localizer["Leave.Holidays"]} {year}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 520);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        void Add(string name, string key, int weight, Font? font = null)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = localizer[key], FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("date", "Field.Date", 14, UiTheme.Monospace);
        Add("day", "Holiday.Weekday", 12, UiTheme.Small);
        Add("name", "Field.Name", 40);
        Add("kind", "Holiday.Kind", 14, UiTheme.Small);

        var host = new Panel { Bounds = new Rectangle(12, 12, 616, 400) };
        _grid.Dock = DockStyle.Fill;
        host.Controls.Add(_grid);

        var notice = new Label
        {
            Text = localizer["Holiday.Notice"], Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
            AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(12, 418, 616, 40)
        };

        _add.Text = localizer["Holiday.Add"];
        _add.Bounds = new Rectangle(12, 468, 210, UiTheme.ButtonHeight);
        _add.Click += async (_, _) => await AddAsync();

        _remove.Text = localizer["Holiday.Remove"];
        _remove.Bounds = new Rectangle(232, 468, 170, UiTheme.ButtonHeight);
        _remove.Click += async (_, _) => await RemoveAsync();

        var close = UiTheme.SecondaryButton(localizer["Action.Close"]);
        close.Bounds = new Rectangle(478, 468, 150, UiTheme.ButtonHeight);
        close.DialogResult = DialogResult.Cancel;

        Controls.AddRange([host, notice, _add, _remove, close]);
        CancelButton = close;

        PermissionGate.Require(Permissions.HumanResources.Manage, _add, _remove);
        QhDialog.Dress(this, localizer, null);
    }

    internal async Task LoadAsync()
    {
        var result = await _leave.GetHolidaysAsync(_year);
        if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

        _grid.Rows.Clear();
        foreach (var h in result.Value)
        {
            var index = _grid.Rows.Add(h.Date.ToString("dd/MM/yyyy"),
                                       _localizer[$"Weekday.{h.Date.DayOfWeek}"], h.Name,
                                       _localizer[h.IsReligious ? "Holiday.Religious" : "Holiday.Civil"]);
            _grid.Rows[index].Tag = h;
        }
    }

    private async Task AddAsync()
    {
        using var editor = new HolidayEditorDialog(_localizer, _dialogs, _year);
        while (editor.ShowDialog(this) == DialogResult.OK)
        {
            var done = await _leave.AddHolidayAsync(editor.Date, editor.HolidayName, editor.IsReligious);
            if (done.IsSuccess)
            {
                Changed = true;
                await LoadAsync();
                // The leaves the new holiday falls in were recounted: named, so HR knows whose
                // balance moved.
                if (done.Value.Count > 0)
                    _dialogs.Info(_localizer["Holiday.Recounted"] + Environment.NewLine + string.Join(Environment.NewLine,
                        done.Value.Select(r => _localizer.Format("Holiday.RecountedLine", r.EmployeeNumber, r.EmployeeName,
                                                                 r.StartDate.ToString("dd/MM/yyyy"), r.EndDate.ToString("dd/MM/yyyy"),
                                                                 r.Status, r.DaysBefore.ToString("0.#"), r.DaysAfter.ToString("0.#")))),
                        editor.Text);
                return;
            }

            _dialogs.ShowFailure(done, editor.Text);
        }
    }

    private async Task RemoveAsync()
    {
        if (_grid.CurrentRow?.Tag is not HolidayRow row) return;
        if (!_dialogs.Confirm(_localizer.Format("Holiday.RemovePrompt", row.Name, row.Date.ToString("dd/MM/yyyy")),
                              _remove.Text)) return;

        var done = await _leave.RemoveHolidayAsync(row.HolidayId);
        if (done.IsFailure) { _dialogs.ShowFailure(done); return; }

        Changed = true;
        await LoadAsync();
    }
}

internal sealed class HolidayEditorDialog : MasterDataEditor
{
    private readonly DateTimePicker _date = new();
    private readonly TextBox _name;
    private readonly CheckBox _religious;

    internal DateOnly Date => DateOnly.FromDateTime(_date.Value.Date);
    internal string HolidayName => _name.Text.Trim();
    internal bool IsReligious => _religious.Checked;

    internal HolidayEditorDialog(ILocalizer localizer, IUserDialogs dialogs, int year)
        : base(localizer, dialogs, "Holiday.Add", year.ToString(), 520, 190)
    {
        Label("Field.Date", 20, 10);
        _date.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        _date.MinDate = new DateTime(year, 1, 1);
        _date.MaxDate = new DateTime(year, 12, 31);
        _date.Value = DateTime.Today.Year == year ? DateTime.Today : new DateTime(year, 1, 1);
        Controls.Add(_date);

        _name = TextField("Field.Name", 220, 10, 280);
        _name.MaxLength = 150;
        _religious = Check("Holiday.Religious", 20, 76, true);
        Buttons(132, 520);
        QhDialog.Dress(this, localizer, null);
    }

    protected override void Accept() => DialogResult = DialogResult.OK;
}
