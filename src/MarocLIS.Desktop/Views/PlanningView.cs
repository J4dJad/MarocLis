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
    private readonly Label _title = new();
    private readonly Label _issuesHeading = new();
    private readonly Label _issuesCount = new();

    private DateOnly _month = PlanningScreenRules.MonthOf(DateOnly.FromDateTime(DateTime.Today));
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
        Padding = QhPage.PagePadding;

        // Direction A: the title and the count, Refresh and the week copy on the right; the month
        // between its two arrows under them; the month's grid in a card with its legend, the
        // selected cells and their actions at its foot (Assign, the primary, at the far end); the
        // points to check in a card of their own below.
        var header = QhPage.Header(_title, _heading, _refresh, _copyWeek);

        _monthLabel.Font = UiTheme.BodyBold;
        _monthLabel.TextAlign = ContentAlignment.MiddleCenter;
        _monthLabel.AutoSize = false;
        _monthLabel.Size = new Size(160, 34);
        _monthLabel.Margin = new Padding(0, 0, 8, 0);

        var toolbar = QhPage.Toolbar(QhPage.Fit(_previous), _monthLabel, QhPage.Fit(_next));

        _legend.Font = UiTheme.Small;
        _legend.ForeColor = UiTheme.TextSecondary;
        _legend.AutoSize = false;
        _legend.AutoEllipsis = true;
        _legend.Dock = DockStyle.Bottom;
        _legend.Height = 24;
        _legend.Padding = new Padding(14, 0, 14, 0);
        _legend.BackColor = UiTheme.SurfaceRaised;
        _legend.TextAlign = ContentAlignment.MiddleLeft;

        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.MultiSelect = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.Dock = DockStyle.Fill;

        var gridBody = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        gridBody.Controls.Add(_grid);
        gridBody.Controls.Add(_legend);
        gridBody.Controls.Add(new QhSelectionBar(_detail, _clear, _assign));

        var gridCard = QhPage.Card(gridBody);
        gridCard.Dock = DockStyle.Fill;

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

        _issues.Dock = DockStyle.Fill;
        var issuesCard = QhPage.TitledCard(_issuesHeading, _issuesCount, _issues);
        issuesCard.Dock = DockStyle.Bottom;
        issuesCard.Height = 170;

        var main = new Panel { BackColor = UiTheme.Surface, Margin = Padding.Empty };
        main.Controls.Add(gridCard);
        main.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 14, BackColor = UiTheme.Surface });
        main.Controls.Add(issuesCard);

        Controls.Add(QhPage.Frame(2, header, toolbar, main));
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
            e.CellStyle!.ForeColor = PlanningScreenRules.IsCritical(issue) ? UiTheme.Critical : UiTheme.Abnormal;
        };
    }

    protected override void ApplyTexts()
    {
        _title.Text = T("Nav.Planning");
        _issuesHeading.Text = T("Planning.Issue");
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
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the planning failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private void Render()
    {
        _monthLabel.Text = PlanningScreenRules.MonthLabel(_month, CultureInfo.CurrentUICulture);
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

        foreach (var date in PlanningScreenRules.Dates(_data.Year, _data.Month))
        {
            var d = date.Day;
            var (dayName, number) = PlanningScreenRules.DayHeader(date, T);
            var column = Column($"d{d}", $"{dayName}\n{number}", 32, UiTheme.SmallBold);
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            column.MinimumWidth = 28;
            _grid.Columns.Add(column);
        }

        foreach (var line in PlanningScreenRules.Lines(_data, T, CultureInfo.CurrentCulture))
        {
            var values = new object[FixedColumns + days];
            values[0] = line.Name;
            values[1] = line.Hours;
            for (var d = 0; d < days; d++) values[FixedColumns + d] = PlanningScreenRules.Code(line.Days[d], T);

            var index = _grid.Rows.Add(values);
            _grid.Rows[index].Tag = line.Employee;
            for (var d = 0; d < days; d++) _grid.Rows[index].Cells[FixedColumns + d].Tag = line.Days[d];
        }

        _grid.ResumeLayout(performLayout: true);

        if (selected.Item1 >= 0 && selected.Item1 < _grid.RowCount && selected.Item2 < _grid.ColumnCount)
            _grid.CurrentCell = _grid.Rows[selected.Item1].Cells[selected.Item2];
        else if (_grid.RowCount > 0)
        {
            // Opens on today, or on the first of the month, so the next click is already on a day.
            var day = PlanningScreenRules.InitialDay(_data.Year, _data.Month, DateOnly.FromDateTime(DateTime.Today)).Day;
            _grid.CurrentCell = _grid.Rows[0].Cells[FixedColumns + day - 1];
        }

        _issues.Rows.Clear();
        foreach (var issue in _data.Issues)
        {
            var index = _issues.Rows.Add(LeaveScreenRules.Date(issue.Date), issue.EmployeeName ?? "—",
                                         PlanningScreenRules.IssueText(issue, T, CultureInfo.CurrentCulture));
            _issues.Rows[index].Tag = issue;
        }
        _issues.RestoreFillWeights();

        _heading.Text = PlanningScreenRules.Count(_data, T);
        _issuesCount.Text = _data.Issues.Count.ToString(CultureInfo.CurrentCulture);
        ShowDetail();
    }

    private void FormatCell(DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < FixedColumns) return;
        if (_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag is not PlanningDay cell) return;

        var style = e.CellStyle!;
        if (PlanningScreenRules.Shaded(cell)) style.BackColor = UiTheme.SurfaceSunken;

        switch (PlanningScreenRules.Ink(cell))
        {
            case PlanningInk.LeaveApproved:
                style.BackColor = UiTheme.AccentSubtle;
                style.ForeColor = UiTheme.Accent;
                style.Font = UiTheme.SmallBold;
                break;
            case PlanningInk.LeavePending:
                style.BackColor = UiTheme.Surface;
                style.ForeColor = UiTheme.Accent;
                style.Font = UiTheme.Small;
                break;
            case PlanningInk.ShiftFlagged:
                style.ForeColor = UiTheme.Critical;
                style.BackColor = UiTheme.CriticalBackground;
                break;
            case PlanningInk.ShiftUnusual:
                style.ForeColor = UiTheme.Abnormal;
                break;
            case PlanningInk.Shift:
                style.ForeColor = UiTheme.Normal;
                break;
        }
    }

    private IReadOnlyList<PlanningDay> SelectedDays() =>
        PlanningScreenRules.Ordered(_grid.SelectedCells.Cast<DataGridViewCell>()
                                         .Where(c => c.ColumnIndex >= FixedColumns)
                                         .Select(c => c.Tag).OfType<PlanningDay>());

    private void ShowDetail()
    {
        var days = SelectedDays();
        // The right stays PermissionGate's; what the selection allows is the shared rules'.
        var actions = PlanningScreenRules.Actions(days, _ => true);
        _assign.Enabled = actions.Assign;
        _clear.Enabled = actions.Clear;
        _copyWeek.Enabled = actions.CopyWeek;
        _detail.Text = PlanningScreenRules.Detail(days, T);
    }

    private async Task AssignAsync()
    {
        var days = PlanningScreenRules.Assignable(SelectedDays());
        if (days.Count == 0) return;

        // The date first: a long name cut the date off the heading, and the operator could not see
        // which day the shift was for.
        var subtitle = PlanningScreenRules.AssignSubtitle(days, T);

        using var dialog = new ShiftDialog(Localizer, _dialogs, subtitle, PlanningScreenRules.Current(days));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var refused = new List<string>();
        foreach (var day in days)
        {
            var done = await _planning.AssignAsync(PlanningScreenRules.Request(day, dialog.Kind, dialog.Start, dialog.End, dialog.Note));
            if (done.IsFailure)
                refused.Add(PlanningScreenRules.RefusedLine(day, Describe(done)));
        }

        await ActivateAsync();

        if (refused.Count > 0)
            _dialogs.Warn(PlanningScreenRules.SomeRefused(refused, T), _assign.Text);
    }

    private async Task ClearAsync()
    {
        var shifts = PlanningScreenRules.Removable(SelectedDays());
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
        if (SelectedDays() is not { Count: > 0 } selected) return;
        var cell = selected[0];

        var (source, target) = PlanningScreenRules.CopyWeek(cell.Date);
        if (!_dialogs.Confirm(PlanningScreenRules.CopyWeekPrompt(cell.Date, T), _copyWeek.Text)) return;

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
        _note.MaxLength = PlanningScreenRules.NoteMaxLength;
        Notice(20, 138, 480, 56, "Planning.ShiftNotice");

        SetHours(current);
        _kind.SelectedIndexChanged += (_, _) => SetHours(null);
        _start.ValueChanged += (_, _) => ShowHours();
        _end.ValueChanged += (_, _) => ShowHours();

        Buttons(204, 520);
        QhDialog.Dress(this, localizer, null);
    }

    private void SetHours(ShiftCell? current)
    {
        var (s, e) = PlanningScreenRules.HoursOf(Kind, current);
        _start.Value = DateTime.Today.Add(s.ToTimeSpan());
        _end.Value = DateTime.Today.Add(e.ToTimeSpan());
        _start.Enabled = _end.Enabled = PlanningScreenRules.TimesEditable(Kind);
        ShowHours();
    }

    private void ShowHours() =>
        _hours.Text = PlanningScreenRules.HoursCounted(Kind, Start, End, (k, a) => Localizer.Format(k, a), CultureInfo.CurrentCulture);

    protected override void Accept() => DialogResult = DialogResult.OK;
}
