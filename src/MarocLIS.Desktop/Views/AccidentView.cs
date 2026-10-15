using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Work accidents and blood exposures: the list of the year, and for the selected file its history
/// and its serological checks. A declaration not sent within 48 hours is shown in red.
/// </summary>
public sealed class AccidentView : LocalizedView
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private readonly AccidentService _accidents;
    private readonly IAccidentRenderer _renderer;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly IClock _clock;
    private readonly ILogger<AccidentView>? _logger;

    private readonly Label _heading = new();
    private readonly ComboBox _year = new();
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _new = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _register = UiTheme.SecondaryButton(string.Empty);
    private readonly DataGridView _grid = UiTheme.Grid();
    private readonly DataGridView _events = UiTheme.Grid();
    private readonly DataGridView _checks = UiTheme.Grid();
    private readonly Label _detail = new();
    private readonly Button _edit = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _stoppage = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _return = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _log = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _checkDone = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _declaration = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _close = UiTheme.SecondaryButton(string.Empty);

    private IReadOnlyList<AccidentRow> _rows = [];
    private AccidentDetail? _current;
    private int _pending;

    public AccidentView(AccidentService accidents, IAccidentRenderer renderer, IDocumentOutput output,
                        IUserDialogs dialogs, IClock clock, ILocalizer localizer, ICultureManager cultureManager,
                        ILogger<AccidentView>? logger = null)
        : base(localizer, cultureManager)
    {
        _accidents = accidents;
        _renderer = renderer;
        _output = output;
        _dialogs = dialogs;
        _clock = clock;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.Accidents");

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = UiTheme.Surface };

        _heading.Font = UiTheme.SectionTitle;
        _heading.ForeColor = UiTheme.TextPrimary;
        _heading.AutoSize = true;
        _heading.Location = new Point(0, 2);

        _year.DropDownStyle = ComboBoxStyle.DropDownList;
        _year.FlatStyle = FlatStyle.Flat;
        _year.Bounds = new Rectangle(0, 28, 110, UiTheme.InputHeight);
        var thisYear = DateTime.Today.Year;
        for (var y = thisYear; y >= thisYear - 5; y--) _year.Items.Add(y);
        _year.SelectedIndex = 0;

        _refresh.Bounds = new Rectangle(122, 27, 110, UiTheme.ButtonHeight - 4);
        _new.Bounds = new Rectangle(244, 27, 220, UiTheme.ButtonHeight - 4);
        _register.Bounds = new Rectangle(476, 27, 200, UiTheme.ButtonHeight - 4);
        bar.Controls.AddRange([_heading, _year, _refresh, _new, _register]);

        static void Add(DataGridView grid, string name, int weight, Font? font = null, bool right = false)
            => grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, Tag = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body,
                    Alignment = right ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft
                }
            });

        Add(_grid, "number", 9, UiTheme.Monospace);
        Add(_grid, "date", 10, UiTheme.Monospace);
        Add(_grid, "employee", 16);
        Add(_grid, "department", 10, UiTheme.Small);
        Add(_grid, "kind", 13);
        Add(_grid, "declared", 14, UiTheme.Small);
        Add(_grid, "days", 6, UiTheme.Monospace, true);
        Add(_grid, "back", 9, UiTheme.Monospace);
        Add(_grid, "check", 9, UiTheme.Monospace);
        Add(_grid, "state", 7, UiTheme.SmallBold);

        Add(_events, "on", 8, UiTheme.Monospace);
        Add(_events, "what", 12);
        Add(_events, "text", 28, UiTheme.Small);
        Add(_events, "by", 12, UiTheme.Small);

        Add(_checks, "label", 16);
        Add(_checks, "due", 9, UiTheme.Monospace);
        Add(_checks, "done", 9, UiTheme.Monospace);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_grid);

        var lower = new Panel { Dock = DockStyle.Bottom, Height = 250, BackColor = UiTheme.Surface, Padding = new Padding(0, 8, 0, 0) };

        _detail.Font = UiTheme.Small;
        _detail.ForeColor = UiTheme.TextSecondary;
        _detail.AutoSize = false;
        _detail.AutoEllipsis = true;
        _detail.Dock = DockStyle.Top;
        _detail.Height = 38;

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Surface };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        _events.Dock = DockStyle.Fill;
        _checks.Dock = DockStyle.Fill;
        _events.Margin = new Padding(0, 0, 8, 0);
        _checks.Margin = new Padding(0);
        split.Controls.Add(_events, 0, 0);
        split.Controls.Add(_checks, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.Surface, WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        // Sized to their caption, so the row fits at 1366 pixels in each language.
        foreach (var button in new[] { _edit, _stoppage, _return, _log, _checkDone, _declaration, _close })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(96, UiTheme.ButtonHeight);
            button.Padding = new Padding(4, 0, 4, 0);
            button.Margin = new Padding(0, 0, 8, 0);
            actions.Controls.Add(button);
        }

        lower.Controls.Add(split);
        lower.Controls.Add(_detail);
        lower.Controls.Add(actions);

        Controls.Add(host);
        Controls.Add(lower);
        Controls.Add(bar);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await Guarded(() => ActivateAsync());
        _year.SelectedIndexChanged += async (_, _) => { if (_year.Focused) await Guarded(() => ActivateAsync()); };
        _new.Click += async (_, _) => await Guarded(() => EditAsync(null));
        _edit.Click += async (_, _) => await Guarded(() => EditAsync(_current));
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && !PermissionGate.IsBlocked(_edit)) await Guarded(() => EditAsync(_current));
        };
        _grid.SelectionChanged += async (_, _) => { if (_pending == 0) await Guarded(LoadDetailAsync); };
        _stoppage.Click += async (_, _) => await Guarded(StoppageAsync);
        _return.Click += async (_, _) => await Guarded(ReturnAsync);
        _log.Click += async (_, _) => await Guarded(LogAsync);
        _checkDone.Click += async (_, _) => await Guarded(CheckDoneAsync);
        _declaration.Click += async (_, _) => await Guarded(DeclarationAsync);
        _register.Click += async (_, _) => await Guarded(RegisterAsync);
        _close.Click += async (_, _) => await Guarded(CloseAsync);

        PermissionGate.Require(Permissions.HumanResources.Accidents,
                               _new, _edit, _stoppage, _return, _log, _checkDone, _close);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not AccidentRow row) return;
            if (row.DeclarationLate(_clock.Now.LocalDateTime) && _grid.Columns[e.ColumnIndex].Name is "declared" or "number")
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
            else if (row.NextCheckDue is { } due && due < _clock.Today && _grid.Columns[e.ColumnIndex].Name == "check")
            {
                e.CellStyle!.BackColor = UiTheme.CriticalBackground;
                e.CellStyle.ForeColor = UiTheme.Critical;
            }
        };
    }

    /// <summary>One operation at a time: a second click while the first still works is ignored.</summary>
    private async Task Guarded(Func<Task> action)
    {
        if (_pending > 0) return;
        _pending++;
        // Reported to the navigation service too, so leaving the screen waits for the write.
        BeginOperation();
        try { await action(); }
        finally { _pending--; EndOperation(); }
    }

    protected override void ApplyTexts()
    {
        _refresh.Text = T("Action.Refresh");
        _new.Text = T("Accident.New");
        _register.Text = T("Accident.Register");
        _edit.Text = T("Accident.Edit");
        _stoppage.Text = T("Accident.Stoppage");
        _return.Text = T("Accident.Return");
        _log.Text = T("Accident.Log");
        _checkDone.Text = T("Accident.CheckDone");
        _declaration.Text = T("Accident.Declaration");
        _close.Text = T("Accident.Close");

        _grid.Column("number").HeaderText = T("Accident.Number");
        _grid.Column("date").HeaderText = T("Accident.OccurredAt");
        _grid.Column("employee").HeaderText = T("Accident.Employee");
        _grid.Column("department").HeaderText = T("Field.Department");
        _grid.Column("kind").HeaderText = T("Accident.Kind");
        _grid.Column("declared").HeaderText = T("Accident.DeclaredOn");
        _grid.Column("days").HeaderText = T("Accident.StoppageDays");
        _grid.Column("back").HeaderText = T("Accident.ReturnedOn");
        _grid.Column("check").HeaderText = T("Accident.NextCheck");
        _grid.Column("state").HeaderText = T("Field.Status");

        _events.Column("on").HeaderText = T("Field.Date");
        _events.Column("what").HeaderText = T("Accident.Event");
        _events.Column("text").HeaderText = T("Accident.EventText");
        _events.Column("by").HeaderText = T("Accident.EventBy");
        _checks.Column("label").HeaderText = T("Accident.Serology");
        _checks.Column("due").HeaderText = T("Deadline.DueOn");
        _checks.Column("done").HeaderText = T("Accident.DoneOn");

        if (_rows.Count > 0) Render(_current?.Request.AccidentId);
    }

    private int Year => _year.SelectedItem is int y ? y : DateTime.Today.Year;

    public override async Task ActivateAsync(CancellationToken ct = default) => await ReloadAsync(_current?.Request.AccidentId, ct);

    private async Task ReloadAsync(Guid? select, CancellationToken ct = default)
    {
        _pending++;
        try
        {
            var result = await _accidents.GetAccidentsAsync(Year, ct);
            if (result.IsFailure) { _dialogs.ShowFailure(result); return; }

            _rows = result.Value;
            Render(select);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the work accidents failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
        finally
        {
            _pending--;
        }

        await LoadDetailAsync();
    }

    private void Render(Guid? select)
    {
        var now = _clock.Now.LocalDateTime;
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (var r in _rows)
        {
            var declared = r.DeclaredToInsurerOn is { } d ? d.ToString("dd/MM/yyyy", Fr)
                         : r.DeclarationLate(now) ? T("Accident.DeclarationLate")
                         : T("Accident.DeclareBefore", (r.OccurredAt + WorkAccident.DeclarationDelay).ToString("dd/MM HH:mm", Fr));
            var index = _grid.Rows.Add(r.Number, r.OccurredAt.ToString("dd/MM/yyyy HH:mm", Fr),
                                       $"{r.EmployeeNumber} {r.EmployeeName}", r.DepartmentName ?? "—",
                                       T($"AccidentKind.{r.Kind}"), declared, r.StoppageDays.ToString("0.#", Fr),
                                       r.ReturnedToWorkOn?.ToString("dd/MM/yyyy", Fr) ?? string.Empty,
                                       r.NextCheckDue?.ToString("dd/MM/yyyy", Fr) ?? string.Empty,
                                       r.IsClosed ? T("Accident.Closed") : T("Accident.Open"));
            _grid.Rows[index].Tag = r;
            if (r.AccidentId == select) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }

        _grid.ResumeLayout(performLayout: true);
        _grid.RestoreFillWeights();

        var late = _rows.Count(r => r.DeclarationLate(now));
        _heading.Text = T("Accident.Heading", _rows.Count(r => r.OccurredAt.Year == Year),
                          _rows.Count(r => !r.IsClosed), late);
    }

    private AccidentRow? Selected() => _grid.CurrentRow?.Tag as AccidentRow;

    private async Task LoadDetailAsync()
    {
        _current = null;
        _events.Rows.Clear();
        _checks.Rows.Clear();
        _detail.Text = string.Empty;

        if (Selected() is { } row)
        {
            try
            {
                var detail = await _accidents.GetAsync(row.AccidentId);
                if (detail.IsFailure) { _dialogs.ShowFailure(detail); return; }
                _current = detail.Value;
                RenderDetail();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Loading a work accident failed.");
                _dialogs.Error(T("Message.UnexpectedError"));
            }
        }

        RefreshButtons();
    }

    private void RenderDetail()
    {
        if (_current is not { } a) return;
        var r = a.Request;

        foreach (var e in a.Events)
            _events.Rows.Add(e.On.ToString("dd/MM/yyyy", Fr), T($"AccidentEventKind.{e.Kind}"), e.Text ?? string.Empty, e.By ?? string.Empty);
        foreach (var c in a.Checks)
        {
            var index = _checks.Rows.Add(c.Label, c.DueOn.ToString("dd/MM/yyyy", Fr), c.DoneOn?.ToString("dd/MM/yyyy", Fr) ?? "—");
            _checks.Rows[index].Tag = c;
        }
        _events.RestoreFillWeights();
        _checks.RestoreFillWeights();

        var parts = new List<string> { $"{a.Number} · {a.EmployeeName} · {T($"AccidentKind.{r.Kind}")}" };
        if (r.Kind == AccidentKind.BloodExposure && r.Exposure is { } exposure)
            parts.Add(T($"ExposureType.{exposure}") + (r.SourceIdentified ? " · " + T("Accident.SourceIdentified") : string.Empty));
        if (!string.IsNullOrWhiteSpace(r.InsurerName))
            parts.Add($"{r.InsurerName}{(r.InsurerClaimNumber is { } claim ? " n° " + claim : string.Empty)}");
        if (r.Subrogation) parts.Add(T("Accident.Subrogation"));
        if (r.IncapacityRate is { } rate) parts.Add(T("Accident.IncapacityShort", rate.ToString("0.##", Fr)));
        _detail.Text = string.Join("   ·   ", parts) + Environment.NewLine + r.Circumstances;
    }

    private void RefreshButtons()
    {
        var has = _current is not null;
        var open = _current is { IsClosed: false };
        _edit.Enabled = _declaration.Enabled = has;
        _stoppage.Enabled = has;
        _return.Enabled = open && _current!.Stoppages.Any(s => s.Status == LeaveStatus.Approved);
        _log.Enabled = has;
        _checkDone.Enabled = open && _current!.Checks.Any(c => c.DoneOn is null);
        _close.Enabled = open;
    }

    private async Task EditAsync(AccidentDetail? detail)
    {
        try
        {
            var people = await _accidents.GetEmployeesAsync();
            if (people.IsFailure) { _dialogs.ShowFailure(people); return; }

            // Whoever serves a notice is still on the staff: a departure recorded ahead does not hide them.
            var today = DateOnly.FromDateTime(_clock.Now.LocalDateTime);
            var choices = people.Value.Where(p => AccidentService.IsDeclarableFor(p.ExitDate, today)
                                                  || detail?.Request.EmployeeId == p.EmployeeId).ToList();
            if (choices.Count == 0) { _dialogs.Warn(T("Leave_NoEmployees"), _new.Text); return; }

            var insurer = detail is null ? await _accidents.LastInsurerAsync() : null;
            using var dialog = new AccidentDialog(Localizer, _dialogs, choices, detail, insurer, _clock.Now.LocalDateTime);
            while (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var saved = await _accidents.SaveAsync(dialog.Request!);
                if (saved.IsSuccess)
                {
                    await ReloadAsync(saved.Value);
                    return;
                }
                _dialogs.ShowFailure(saved, dialog.Text);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving a work accident failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private async Task Run(Func<Task<Domain.Common.Result>> action, string caption)
    {
        try
        {
            var done = await action();
            if (done.IsFailure) { _dialogs.ShowFailure(done, caption); return; }
            await ReloadAsync(_current?.Request.AccidentId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "A work-accident step failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private async Task StoppageAsync()
    {
        if (_current is not { } a || a.Request.AccidentId is not { } id) return;
        using var dialog = new AccidentStoppageDialog(Localizer, _dialogs, a);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(() => _accidents.AddStoppageAsync(id, dialog.From, dialog.To, dialog.Relapse), dialog.Text);
    }

    private async Task ReturnAsync()
    {
        if (_current is not { } a || a.Request.AccidentId is not { } id) return;
        var last = a.Stoppages.Where(s => s.Status == LeaveStatus.Approved).Max(s => s.EndDate);
        using var dialog = new AccidentDateDialog(Localizer, _dialogs, "Accident.Return", a.Number, "Accident.ReturnedOn",
                                                  last.AddDays(1), DateOnly.FromDateTime(a.Request.OccurredAt), null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(() => _accidents.RecordReturnAsync(id, dialog.Date), dialog.Text);
    }

    private async Task LogAsync()
    {
        if (_current is not { } a || a.Request.AccidentId is not { } id) return;
        using var dialog = new AccidentLogDialog(Localizer, _dialogs, a.Number);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(() => _accidents.LogAsync(id, dialog.Kind, dialog.Note), dialog.Text);
    }

    private async Task CheckDoneAsync()
    {
        if (_current is not { } a || a.Request.AccidentId is not { } id) return;
        var check = (_checks.CurrentRow?.Tag as SerologyCheckRow) is { DoneOn: null } chosen
            ? chosen
            : a.Checks.Where(c => c.DoneOn is null).OrderBy(c => c.DueOn).FirstOrDefault();
        if (check is null) return;

        using var dialog = new AccidentDateDialog(Localizer, _dialogs, "Accident.CheckDone", $"{a.Number} — {check.Label}",
                                                  "Accident.DoneOn", _clock.Today, DateOnly.FromDateTime(a.Request.OccurredAt),
                                                  _clock.Today, "Accident.CheckNotice");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(() => _accidents.MarkCheckDoneAsync(id, check.CheckId, dialog.Date), dialog.Text);
    }

    private async Task CloseAsync()
    {
        if (_current is not { } a || a.Request.AccidentId is not { } id) return;
        var note = _dialogs.PromptForText(T("Accident.ClosePrompt"), $"{T("Accident.Close")} — {a.Number}");
        if (note is null) return;
        await Run(() => _accidents.CloseAsync(id, note), _close.Text);
    }

    private async Task DeclarationAsync()
    {
        if (_current?.Request.AccidentId is not { } id) return;
        try
        {
            var document = await _accidents.GetDeclarationAsync(id);
            if (document.IsFailure) { _dialogs.ShowFailure(document); return; }
            var opened = await _output.OpenAsync(_renderer.RenderDeclaration(document.Value), "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _declaration.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Printing an accident declaration failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private async Task RegisterAsync()
    {
        try
        {
            var document = await _accidents.GetReportAsync(Year);
            if (document.IsFailure) { _dialogs.ShowFailure(document); return; }
            var opened = await _output.OpenAsync(_renderer.RenderReport(document.Value), "pdf");
            if (opened.IsFailure) _dialogs.ShowFailure(opened, _register.Text);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Printing the accident register failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }
}

/// <summary>Declaring an accident, or completing its file once the insurer has answered.</summary>
internal sealed class AccidentDialog : MasterDataEditor
{
    private readonly ComboBox _employee, _kind, _exposure;
    private readonly DateTimePicker _date = new(), _time = new();
    private readonly TextBox _place, _circumstances, _injuries, _witnesses, _insurer, _claim, _rate, _note;
    private readonly CheckBox _firstAid, _source, _subrogation;
    private readonly (CheckBox Toggle, DateTimePicker Picker) _consulted, _declared;
    private readonly AccidentDetail? _detail;

    internal AccidentRequest? Request { get; private set; }

    internal AccidentDialog(ILocalizer localizer, IUserDialogs dialogs, IReadOnlyList<EmployeeRow> people,
                            AccidentDetail? detail, string? lastInsurer, DateTime now)
        : base(localizer, dialogs, detail is null ? "Accident.New" : "Accident.Edit", detail?.Number, 760, 640)
    {
        _detail = detail;
        var r = detail?.Request;

        _employee = Lookup("Accident.Employee", 20, 10, 340, people.Select(p => (p.EmployeeId, $"{p.Number} {p.FullName}")),
                           r?.EmployeeId, allowNone: false);
        _employee.Enabled = detail is null;
        _kind = Choice("Accident.Kind", 380, 10, 360, r?.Kind ?? AccidentKind.WorkAccident, "AccidentKind");

        Label("Accident.OccurredAt", 20, 62);
        _date.Bounds = new Rectangle(20, 80, 160, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        _date.MaxDate = now.Date;
        _date.Value = (r?.OccurredAt ?? now).Date;
        _time.Bounds = new Rectangle(190, 80, 100, UiTheme.InputHeight);
        _time.Format = DateTimePickerFormat.Custom;
        _time.CustomFormat = "HH:mm";
        _time.ShowUpDown = true;
        _time.Value = DateTime.Today + (r?.OccurredAt ?? now).TimeOfDay;
        Controls.AddRange([_date, _time]);
        _place = TextField("Accident.Place", 380, 62, 360, r?.Place);

        _circumstances = TextField("Accident.Circumstances", 20, 114, 720, r?.Circumstances);
        _circumstances.Multiline = true;
        _circumstances.Height = 60;
        _circumstances.ScrollBars = ScrollBars.Vertical;
        _injuries = TextField("Accident.Injuries", 20, 196, 450, r?.Injuries);
        _witnesses = TextField("Accident.Witnesses", 490, 196, 250, r?.Witnesses);
        _firstAid = Check("Accident.FirstAid", 20, 250, r?.FirstAidGiven ?? false);

        _exposure = Choice("Accident.Exposure", 20, 280, 340, r?.Exposure ?? ExposureType.Needlestick, "ExposureType");
        _source = Check("Accident.SourceIdentified", 380, 302, r?.SourceIdentified ?? false);
        _consulted = OptionalDate("Accident.ConsultedAt", 560, 280, r?.ConsultedAt is { } c ? DateOnly.FromDateTime(c) : null,
                                  max: now.Date);

        _insurer = TextField("Accident.Insurer", 20, 340, 340, r?.InsurerName ?? lastInsurer);
        _claim = TextField("Accident.ClaimNumber", 380, 340, 170, r?.InsurerClaimNumber);
        _declared = OptionalDate("Accident.DeclaredOn", 560, 340, r?.DeclaredToInsurerOn, max: now.Date);
        _subrogation = Check("Accident.SubrogationAsk", 20, 396, r?.Subrogation ?? false);
        _rate = TextField("Accident.Incapacity", 560, 392, 180,
                          r?.IncapacityRate?.ToString("0.##", CultureInfo.InvariantCulture), mono: true, right: true);
        _note = TextField("Field.Notes", 20, 444, 720, r?.Note);

        Notice(20, 498, 720, 64, "Accident.Notice");
        Buttons(578, 760);

        _kind.SelectedIndexChanged += (_, _) => Toggle();
        Toggle();
    }

    private void Toggle()
    {
        var blood = Selected<AccidentKind>(_kind) == AccidentKind.BloodExposure;
        _exposure.Enabled = _source.Enabled = blood;
    }

    protected override void Accept()
    {
        if (SelectedId(_employee) is not { } employee) return;
        if (string.IsNullOrWhiteSpace(_circumstances.Text))
        {
            Dialogs.Warn(Localizer["Accident_CircumstancesRequired"], Text);
            _circumstances.Focus();
            return;
        }

        decimal? rate = null;
        if (!string.IsNullOrWhiteSpace(_rate.Text))
        {
            rate = DecimalOrNull(_rate);
            if (rate is null or < 0 or > 100)
            {
                Dialogs.Warn(Localizer["Accident_IncapacityInvalid"], Text);
                _rate.Focus();
                return;
            }
        }

        var kind = Selected<AccidentKind>(_kind);
        var occurred = _date.Value.Date + _time.Value.TimeOfDay;
        var consulted = DateOrNull(_consulted.Toggle, _consulted.Picker);
        Request = new AccidentRequest
        {
            AccidentId = _detail?.Request.AccidentId,
            EmployeeId = employee,
            Kind = kind,
            OccurredAt = new DateTime(occurred.Year, occurred.Month, occurred.Day, occurred.Hour, occurred.Minute, 0),
            Place = Blank(_place),
            Circumstances = _circumstances.Text.Trim(),
            Injuries = Blank(_injuries),
            Witnesses = Blank(_witnesses),
            FirstAidGiven = _firstAid.Checked,
            Exposure = kind == AccidentKind.BloodExposure ? Selected<ExposureType>(_exposure) : null,
            SourceIdentified = kind == AccidentKind.BloodExposure && _source.Checked,
            // The day of the consultation; the same day as the accident keeps its hour.
            ConsultedAt = consulted is { } day
                ? (day == DateOnly.FromDateTime(occurred) ? occurred : day.ToDateTime(new TimeOnly(9, 0)))
                : null,
            InsurerName = Blank(_insurer),
            InsurerClaimNumber = Blank(_claim),
            DeclaredToInsurerOn = DateOrNull(_declared.Toggle, _declared.Picker),
            Subrogation = _subrogation.Checked,
            IncapacityRate = rate,
            Note = Blank(_note)
        };
        DialogResult = DialogResult.OK;
    }
}

/// <summary>A stoppage or its extension, from the medical certificate.</summary>
internal sealed class AccidentStoppageDialog : MasterDataEditor
{
    private readonly DateTimePicker _from = new(), _to = new();
    private readonly CheckBox _relapse;

    internal DateOnly From => DateOnly.FromDateTime(_from.Value.Date);
    internal DateOnly To => DateOnly.FromDateTime(_to.Value.Date);
    internal bool Relapse => _relapse.Checked;

    internal AccidentStoppageDialog(ILocalizer localizer, IUserDialogs dialogs, AccidentDetail accident)
        : base(localizer, dialogs, "Accident.Stoppage", accident.Number, 520, 280)
    {
        var day = DateOnly.FromDateTime(accident.Request.OccurredAt);
        var last = accident.Stoppages.Where(s => s.Status == LeaveStatus.Approved).Select(s => (DateOnly?)s.EndDate).Max();
        var start = (last ?? day).AddDays(1);

        Label("Leave.From", 20, 10);
        _from.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _from.Format = DateTimePickerFormat.Short;
        _from.MinDate = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        _from.Value = start.ToDateTime(TimeOnly.MinValue);
        Label("Leave.To", 240, 10);
        _to.Bounds = new Rectangle(240, 28, 180, UiTheme.InputHeight);
        _to.Format = DateTimePickerFormat.Short;
        _to.Value = start.AddDays(6).ToDateTime(TimeOnly.MinValue);
        Controls.AddRange([_from, _to]);

        _relapse = Check("Accident.Relapse", 20, 72, accident.IsClosed);
        Notice(20, 104, 480, 96, "Accident.StoppageNotice");
        Buttons(218, 520);
    }

    protected override void Accept()
    {
        if (To < From)
        {
            Dialogs.Warn(Localizer["Leave_EndBeforeStart"], Text);
            return;
        }
        DialogResult = DialogResult.OK;
    }
}

/// <summary>One date: the return to work, or the day a serological check was done.</summary>
internal sealed class AccidentDateDialog : MasterDataEditor
{
    private readonly DateTimePicker _date = new();

    internal DateOnly Date => DateOnly.FromDateTime(_date.Value.Date);

    internal AccidentDateDialog(ILocalizer localizer, IUserDialogs dialogs, string titleKey, string subtitle,
                                string labelKey, DateOnly value, DateOnly min, DateOnly? max, string? noticeKey = null)
        : base(localizer, dialogs, titleKey, subtitle, 520, noticeKey is null ? 160 : 230)
    {
        Label(labelKey, 20, 10);
        _date.Bounds = new Rectangle(20, 28, 180, UiTheme.InputHeight);
        _date.Format = DateTimePickerFormat.Short;
        _date.MinDate = min.ToDateTime(TimeOnly.MinValue);
        if (max is { } upper) _date.MaxDate = upper.ToDateTime(TimeOnly.MinValue);
        var wanted = value.ToDateTime(TimeOnly.MinValue);
        _date.Value = wanted < _date.MinDate ? _date.MinDate : wanted > _date.MaxDate ? _date.MaxDate : wanted;
        Controls.Add(_date);

        if (noticeKey is not null) Notice(20, 70, 480, 70, noticeKey);
        Buttons(noticeKey is null ? 96 : 166, 520);
    }

    protected override void Accept() => DialogResult = DialogResult.OK;
}

/// <summary>A line of the insurer follow-up: a reminder, the insurer's answer, or a note.</summary>
internal sealed class AccidentLogDialog : MasterDataEditor
{
    private readonly ComboBox _kind;
    private readonly TextBox _text;

    internal AccidentEventKind Kind => Selected<AccidentEventKind>(_kind);
    internal string? Note => Blank(_text);

    internal AccidentLogDialog(ILocalizer localizer, IUserDialogs dialogs, string number)
        : base(localizer, dialogs, "Accident.Log", number, 560, 250)
    {
        _kind = Choice("Accident.Event", 20, 10, 300, AccidentEventKind.InsurerReminder, "AccidentEventKind",
                       [AccidentEventKind.InsurerReminder, AccidentEventKind.InsurerResponse, AccidentEventKind.Note]);
        _text = TextField("Accident.EventText", 20, 62, 520);
        _text.Multiline = true;
        _text.Height = 70;
        Buttons(186, 560);
    }

    protected override void Accept()
    {
        if (Kind != AccidentEventKind.InsurerReminder && Note is null)
        {
            Dialogs.Warn(Localizer["Accident_EventTextRequired"], Text);
            _text.Focus();
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
