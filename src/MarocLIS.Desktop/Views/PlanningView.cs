using System.Globalization;
using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The month's shifts and on-call days, one line per person, one column per day, with leave and
/// public holidays shown where they fall, and below it what the planning breaks: short rest, more
/// than 44 hours, no day off, a day without a biologist, a trainee alone on a night.
/// </summary>
public sealed class PlanningView : LocalizedView
{
    private const int FixedColumns = 2;

    private readonly PlanningService _planning;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PlanningView>? _logger;

    private readonly Label _heading = new();
    private readonly Button _previous = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _monthLabel = new();
    private readonly Button _next = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _legend = new();
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _issues = UiTheme.Grid();

    private readonly Button _assign = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _clear = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _copyWeek = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _detail = new();

    private DateOnly _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private PlanningMonth? _data;

    public PlanningView(PlanningService planning, IUserDialogs dialogs, ILocalizer localizer,
                        ICultureManager cultureManager, ILogger<PlanningView>? logger = null)
        : base(localizer, cultureManager)
    {
        _planning = planning;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Planning");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        // Laid out here: the month stays between its two arrows rather than wrapping off on its own.
        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = UiTheme.Surface, Tag = UiTags.OwnLayout };

        _heading.Font = UiTheme.SectionTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 2);

        _previous.Bounds = new Rectangle(0, 27, 175, UiTheme.ButtonHeight - 4);
        _monthLabel.Font = UiTheme.BodyBold;
        _monthLabel.TextAlign = ContentAlignment.MiddleCenter;
        _monthLabel.AutoSize = false;
        _monthLabel.Bounds = new Rectangle(181, 27, 160, UiTheme.ButtonHeight - 4);
        _next.Bounds = new Rectangle(347, 27, 175, UiTheme.ButtonHeight - 4);
        _refresh.Bounds = new Rectangle(534, 27, 130, UiTheme.ButtonHeight - 4);

        bar.Controls.AddRange([_heading, _previous, _monthLabel, _next, _refresh]);

        _legend.Font = UiTheme.Small;
        _legend.ForeColor = UiTheme.TextSecondary;
        _legend.AutoSize = false;
        _legend.AutoEllipsis = true;
        _legend.Dock = DockStyle.Bottom;
        _legend.Height = 22;
        _legend.TextAlign = ContentAlignment.MiddleLeft;

        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.MultiSelect = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_grid);
        host.Controls.Add(_legend);

        void Issue(string name, int weight, Font? font = null)
            => _issues.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, Tag = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Small }
            });

        Issue("date", 10, UiTheme.Monospace);
        Issue("who", 20);
        Issue("what", 70);

        var issuesHost = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = UiTheme.Surface,
                                     Padding = new Padding(0, 6, 0, 0) };
        issuesHost.Controls.Add(_issues);

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = UiTheme.Surface };

        _detail.Font = UiTheme.Small;
        _detail.ForeColor = UiTheme.TextSecondary;
        _detail.AutoSize = false;
        _detail.AutoEllipsis = true;
        _detail.Bounds = new Rectangle(0, 4, 1300, 20);

        _assign.Bounds = new Rectangle(0, 30, 200, UiTheme.ButtonHeight);
        _clear.Bounds = new Rectangle(210, 30, 170, UiTheme.ButtonHeight);
        _copyWeek.Bounds = new Rectangle(390, 30, 300, UiTheme.ButtonHeight);

        actions.Controls.AddRange([_detail, _assign, _clear, _copyWeek]);

        Controls.Add(host);
        Controls.Add(actions);
        Controls.Add(issuesHost);
        Controls.Add(bar);
    }

    private void WireBehaviour()
    {
        _previous.Click += async (_, _) => { _month = _month.AddMonths(-1); await ActivateAsync(); };
        _next.Click += async (_, _) => { _month = _month.AddMonths(1); await ActivateAsync(); };
        _refresh.Click += async (_, _) => await ActivateAsync();
        _assign.Click += async (_, _) => await AssignAsync();
        _clear.Click += async (_, _) => await ClearAsync();
        _copyWeek.Click += async (_, _) => await CopyWeekAsync();
        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= FixedColumns && !PermissionGate.IsBlocked(_assign))
                await AssignAsync();
        };
        _grid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && !PermissionGate.IsBlocked(_clear)) { e.Handled = true; await ClearAsync(); }
        };

        // Clicking an issue takes the grid to that person and day.
        _issues.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _issues.Rows[e.RowIndex].Tag is not PlanningIssue issue) return;
            var column = FixedColumns + issue.Date.Day - 1;
            if (column >= _grid.ColumnCount) return;

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (issue.EmployeeId is { } id && (row.Tag as PlanningEmployee)?.EmployeeId != id) continue;
                _grid.CurrentCell = row.Cells[column];
                return;
            }
        };

        PermissionGate.Require(Permissions.HumanResources.ManagePlanning, _assign, _clear, _copyWeek);

        _grid.CellFormatting += (_, e) => FormatCell(e);
        _issues.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _issues.Rows[e.RowIndex].Tag is not PlanningIssue issue) return;
            e.CellStyle!.ForeColor = issue.Kind is PlanningIssueKind.NoBiologist or PlanningIssueKind.ShiftDuringLeave
                                         or PlanningIssueKind.TraineeAlone
                ? UiTheme.Critical
                : UiTheme.Abnormal;
        };
    }

    protected override void ApplyTexts()
    {
        _refresh.Text = T("Action.Refresh");
        _previous.Text = T("Planning.PreviousMonth");
        _next.Text = T("Planning.NextMonth");
        _assign.Text = T("Planning.Assign");
        _clear.Text = T("Planning.Clear");
        _copyWeek.Text = T("Planning.CopyWeek");
        _legend.Text = T("Planning.Legend");

        _issues.Column("date").HeaderText = T("Field.Date");
        _issues.Column("who").HeaderText = T("Field.Name");
        _issues.Column("what").HeaderText = T("Planning.Issue");

        Render();
    }

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _planning.GetMonthAsync(_month.Year, _month.Month, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _data = result.Value;
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the planning failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private string Code(ShiftCell s) => T($"ShiftKindShort.{s.Kind}");

    private string ShiftText(ShiftCell s) =>
        s.Kind == ShiftKind.OnCall ? T("ShiftKind.OnCall") : $"{T($"ShiftKind.{s.Kind}")} {s.Start:HH\\:mm}-{s.End:HH\\:mm}";

    private sealed record DayCell(PlanningEmployee Employee, DateOnly Date, IReadOnlyList<ShiftCell> Shifts,
                                  LeaveSpan? Leave, HolidayRow? Holiday, bool Employed);

    private void Render()
    {
        var culture = CultureInfo.CurrentUICulture;
        _monthLabel.Text = culture.TextInfo.ToTitleCase(_month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", culture));
        if (_data is null) return;

        var selected = _grid.CurrentCell is { } current ? (current.RowIndex, current.ColumnIndex) : (-1, -1);
        var days = DateTime.DaysInMonth(_data.Year, _data.Month);

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        _grid.Columns.Clear();

        DataGridViewTextBoxColumn Column(string name, string header, int width, Font font) => new()
        {
            Name = name, HeaderText = header, Width = width, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = font }
        };

        _grid.Columns.Add(Column("name", T("Field.Name"), 165, UiTheme.Body));
        _grid.Columns.Add(Column("hours", T("Planning.Hours"), 52, UiTheme.Monospace));
        _grid.Columns[0].Frozen = _grid.Columns[1].Frozen = true;
        _grid.Columns[1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        for (var d = 1; d <= days; d++)
        {
            var date = new DateOnly(_data.Year, _data.Month, d);
            var dayName = T($"WeekdayShort.{date.DayOfWeek}");
            var column = Column($"d{d}", $"{dayName}\n{d:00}", 32, UiTheme.SmallBold);
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            column.MinimumWidth = 28;
            _grid.Columns.Add(column);
        }

        foreach (var employee in _data.Employees)
        {
            var mine = _data.Shifts.Where(s => s.EmployeeId == employee.EmployeeId).ToList();
            var values = new object[FixedColumns + days];
            values[0] = $"{employee.FullName} · {T($"EmployeeJobShort.{employee.Job}")}";
            values[1] = mine.Sum(s => s.Hours).ToString("0.#");

            var cells = new DayCell[days];
            for (var d = 1; d <= days; d++)
            {
                var date = new DateOnly(_data.Year, _data.Month, d);
                var shifts = mine.Where(s => s.Date == date).ToList();
                var leave = _data.Leaves.FirstOrDefault(l => l.EmployeeId == employee.EmployeeId && l.Covers(date));
                var employed = date >= employee.HireDate && (employee.ExitDate is null || date <= employee.ExitDate);
                cells[d - 1] = new DayCell(employee, date, shifts,
                                           leave, _data.Holidays.FirstOrDefault(h => h.Date == date), employed);

                values[FixedColumns + d - 1] =
                    shifts.Count > 0 ? string.Join("+", shifts.Select(Code))
                    : leave is not null ? T($"LeaveTypeShort.{leave.Type}")
                    : string.Empty;
            }

            var index = _grid.Rows.Add(values);
            _grid.Rows[index].Tag = employee;
            for (var d = 0; d < days; d++) _grid.Rows[index].Cells[FixedColumns + d].Tag = cells[d];
        }

        _grid.ResumeLayout(performLayout: true);

        if (selected.Item1 >= 0 && selected.Item1 < _grid.RowCount && selected.Item2 < _grid.ColumnCount)
            _grid.CurrentCell = _grid.Rows[selected.Item1].Cells[selected.Item2];
        else if (_grid.RowCount > 0)
        {
            // Opens on today, or on the first of the month, so the next click is already on a day.
            var today = DateOnly.FromDateTime(DateTime.Today);
            var day = today.Year == _data.Year && today.Month == _data.Month ? today.Day : 1;
            _grid.CurrentCell = _grid.Rows[0].Cells[FixedColumns + day - 1];
        }

        _issues.Rows.Clear();
        foreach (var issue in _data.Issues)
        {
            var index = _issues.Rows.Add(issue.Date.ToString("dd/MM/yyyy"), issue.EmployeeName ?? "—", IssueText(issue));
            _issues.Rows[index].Tag = issue;
        }
        _issues.RestoreFillWeights();

        _heading.Text = T("Planning.Heading", _data.Employees.Count, _data.Shifts.Count, _data.Issues.Count);
        ShowDetail();
    }

    private string IssueText(PlanningIssue issue) => issue.Kind switch
    {
        PlanningIssueKind.RestTooShort => T("PlanningIssue.RestTooShort", (issue.Value ?? 0m).ToString("0.#")),
        PlanningIssueKind.WeeklyHoursExceeded => T("PlanningIssue.WeeklyHoursExceeded", (issue.Value ?? 0m).ToString("0.#"),
                                                   PlanningChecks.WeekStart(issue.Date).ToString("dd/MM")),
        PlanningIssueKind.NoWeeklyRest => T("PlanningIssue.NoWeeklyRest", PlanningChecks.WeekStart(issue.Date).ToString("dd/MM")),
        _ => T($"PlanningIssue.{issue.Kind}")
    };

    private void FormatCell(DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < FixedColumns) return;
        if (_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag is not DayCell cell) return;

        var style = e.CellStyle!;
        if (!cell.Employed)
        {
            style.BackColor = UiTheme.SurfaceSunken;
            return;
        }

        if (cell.Holiday is not null || cell.Date.DayOfWeek == DayOfWeek.Sunday)
            style.BackColor = UiTheme.SurfaceSunken;

        if (cell.Leave is { } leave && cell.Shifts.Count == 0)
        {
            style.BackColor = leave.Status == LeaveStatus.Approved ? UiTheme.AccentSubtle : UiTheme.Surface;
            style.ForeColor = UiTheme.Accent;
            style.Font = leave.Status == LeaveStatus.Approved ? UiTheme.SmallBold : UiTheme.Small;
            return;
        }

        if (cell.Shifts.Count > 0)
        {
            var flagged = _data?.Issues.Any(i => i.EmployeeId == cell.Employee.EmployeeId && i.Date == cell.Date) == true;
            style.ForeColor = flagged ? UiTheme.Critical
                            : cell.Shifts.Any(s => s.Kind is ShiftKind.Night or ShiftKind.SundayHoliday or ShiftKind.OnCall)
                                ? UiTheme.Abnormal
                                : UiTheme.Normal;
            if (flagged) style.BackColor = UiTheme.CriticalBackground;
        }
    }

    private List<DayCell> SelectedDays() =>
    [
        .. _grid.SelectedCells.Cast<DataGridViewCell>()
               .Where(c => c.ColumnIndex >= FixedColumns)
               .Select(c => c.Tag).OfType<DayCell>()
               .OrderBy(c => c.Employee.FullName).ThenBy(c => c.Date)
    ];

    private void ShowDetail()
    {
        var days = SelectedDays();
        _assign.Enabled = days.Count > 0;
        _clear.Enabled = days.Any(d => d.Shifts.Count > 0);
        _copyWeek.Enabled = days.Count > 0;

        if (days.Count != 1)
        {
            _detail.Text = days.Count > 1 ? T("Planning.CellsSelected", days.Count) : T("Planning.Hint");
            return;
        }

        var cell = days[0];
        var parts = new List<string> { $"{cell.Employee.FullName} — {cell.Date.ToDateTime(TimeOnly.MinValue):dddd dd/MM/yyyy}" };
        parts.AddRange(cell.Shifts.Select(s => s.Note is null ? ShiftText(s) : $"{ShiftText(s)} ({s.Note})"));
        if (cell.Leave is { } leave) parts.Add($"{T($"LeaveType.{leave.Type}")} ({T($"LeaveStatus.{leave.Status}")})");
        if (cell.Holiday is { } holiday) parts.Add(holiday.Name);
        _detail.Text = string.Join(" · ", parts);
    }

    private async Task AssignAsync()
    {
        var days = SelectedDays().Where(d => d.Employed).ToList();
        if (days.Count == 0) return;

        var first = days[0];
        var subtitle = days.Count == 1
            ? $"{first.Employee.FullName} — {first.Date:dd/MM/yyyy}"
            : T("Planning.CellsSelected", days.Count);

        using var dialog = new ShiftDialog(Localizer, _dialogs, subtitle, first.Shifts.Count > 0 ? first.Shifts[0] : null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var refused = new List<string>();
        foreach (var day in days)
        {
            var done = await _planning.AssignAsync(new ShiftRequest
            {
                EmployeeId = day.Employee.EmployeeId, Date = day.Date, Kind = dialog.Kind,
                Start = dialog.Start, End = dialog.End, Note = dialog.Note
            });
            if (done.IsFailure)
                refused.Add($"{day.Employee.FullName} {day.Date:dd/MM} : {Describe(done)}");
        }

        await ActivateAsync();

        if (refused.Count > 0)
            _dialogs.Warn(T("Planning.SomeRefused", refused.Count) + "\n\n" + string.Join("\n", refused.Take(12)),
                          _assign.Text);
    }

    private async Task ClearAsync()
    {
        var shifts = SelectedDays().SelectMany(d => d.Shifts).ToList();
        if (shifts.Count == 0) return;
        if (!_dialogs.Confirm(T("Planning.ClearPrompt", shifts.Count), _clear.Text)) return;

        foreach (var shift in shifts)
        {
            var done = await _planning.RemoveAsync(shift.ShiftId);
            if (done.IsFailure) { _dialogs.ShowFailure(done, _clear.Text); break; }
        }

        await ActivateAsync();
    }

    private async Task CopyWeekAsync()
    {
        if (SelectedDays().FirstOrDefault() is not { } cell) return;

        var target = PlanningChecks.WeekStart(cell.Date);
        var source = target.AddDays(-7);
        if (!_dialogs.Confirm(T("Planning.CopyWeekPrompt", source.ToString("dd/MM"), target.ToString("dd/MM")),
                              _copyWeek.Text)) return;

        var done = await _planning.CopyWeekAsync(source, target);
        if (done.IsFailure) { _dialogs.ShowFailure(done, _copyWeek.Text); return; }

        await ActivateAsync();
        _dialogs.Info(T("Planning.CopyWeekDone", done.Value.Copied, done.Value.Skipped), _copyWeek.Text);
    }
}

/// <summary>A post on the planning: the kind sets the usual hours, which can be changed.</summary>
internal sealed class ShiftDialog : MasterDataEditor
{
    private readonly ComboBox _kind;
    private readonly DateTimePicker _start = new(), _end = new();
    private readonly TextBox _note;
    private readonly Label _hours;

    internal ShiftKind Kind => Selected<ShiftKind>(_kind);
    internal TimeOnly Start => TimeOnly.FromDateTime(_start.Value);
    internal TimeOnly End => TimeOnly.FromDateTime(_end.Value);
    internal string? Note => Blank(_note);

    internal ShiftDialog(ILocalizer localizer, IUserDialogs dialogs, string subtitle, ShiftCell? current)
        : base(localizer, dialogs, "Planning.Assign", subtitle, 520, 260)
    {
        _kind = Choice("Planning.Kind", 20, 10, 230, current?.Kind ?? ShiftKind.Day, "ShiftKind");

        Label("Planning.Start", 270, 10);
        _start.Bounds = new Rectangle(270, 28, 100, UiTheme.InputHeight);
        Label("Planning.End", 390, 10);
        _end.Bounds = new Rectangle(390, 28, 100, UiTheme.InputHeight);
        foreach (var picker in new[] { _start, _end })
        {
            picker.Format = DateTimePickerFormat.Custom;
            picker.CustomFormat = "HH:mm";
            picker.ShowUpDown = true;
            Controls.Add(picker);
        }

        _hours = new Label
        {
            Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary, AutoSize = false,
            Bounds = new Rectangle(270, 58, 230, 20)
        };
        Controls.Add(_hours);

        _note = TextField("Field.Notes", 20, 84, 480, current?.Note);
        Notice(20, 138, 480, 56, "Planning.ShiftNotice");

        SetHours(current?.Start, current?.End);
        _kind.SelectedIndexChanged += (_, _) => SetHours(null, null);
        _start.ValueChanged += (_, _) => ShowHours();
        _end.ValueChanged += (_, _) => ShowHours();

        Buttons(204, 520);
    }

    private void SetHours(TimeOnly? start, TimeOnly? end)
    {
        var (s, e) = ShiftRules.DefaultHours(Kind);
        _start.Value = DateTime.Today.Add((start ?? s).ToTimeSpan());
        _end.Value = DateTime.Today.Add((end ?? e).ToTimeSpan());
        _start.Enabled = _end.Enabled = Kind != ShiftKind.OnCall;
        ShowHours();
    }

    private void ShowHours() =>
        _hours.Text = Localizer.Format("Planning.HoursCounted", ShiftRules.Hours(Kind, Start, End).ToString("0.#"));

    protected override void Accept() => DialogResult = DialogResult.OK;
}
