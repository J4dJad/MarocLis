using MarocLIS.Application.Quality;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Quality;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The non-conformity register.
/// <para>
/// ISO 15189 §4.9 is not satisfied by keeping a list. The laboratory has to assess what the
/// departure meant clinically, act, and then verify that the action worked — and the closure
/// button here will refuse until those exist for a major or critical item, naming what is
/// missing rather than reporting a generic failure.
/// </para>
/// <para>
/// A recurrence is marked automatically, because a register whose repeat entries are invisible
/// documents incidents without improving anything.
/// </para>
/// </summary>
public sealed class NonConformityView : LocalizedView
{
    private readonly SelectionLoads _selectionLoads = new();
    private readonly NonConformityService _service;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<NonConformityView>? _logger;

    private readonly DataGridView _register = UiTheme.Grid();
    private readonly DataGridView _actions = UiTheme.Grid();

    private readonly Label _registerHeading = new();
    private readonly Label _detailHeading = new();
    private readonly Label _detail = new();
    private readonly CheckBox _onlyOpen = new();
    private readonly ComboBox _categoryFilter = new();

    private readonly Button _open = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _addAction = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _completeAction = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _analysis = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _close = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _reopen = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _dismiss = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _cancelAction = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _startAction = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _refresh = UiTheme.SecondaryButton(string.Empty);
    private readonly Label _status = new();

    private IReadOnlyList<NonConformityRow> _rows = [];
    private IReadOnlyList<CorrectiveActionRow> _currentActions = [];

    public NonConformityView(NonConformityService service, IUserDialogs dialogs,
                             ILocalizer localizer, ICultureManager cultureManager,
                             ILogger<NonConformityView>? logger = null)
        : base(localizer, cultureManager)
    {
        _service = service;
        _dialogs = dialogs;
        _logger = logger;

        BuildLayout();
        WireBehaviour();
    }

    public override string Title => T("Nav.NonConformities");

    // ---- Layout ------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        Padding = new Padding(UiTheme.Gutter);

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 300,
            SplitterWidth = 6,
            BackColor = UiTheme.Surface
        };

        // --- register --------------------------------------------------------------------------------

        var top = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        var filterBar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UiTheme.Surface };

        _registerHeading.Font = UiTheme.SectionTitle;
        _registerHeading.ForeColor = UiTheme.TextPrimary;
        _registerHeading.AutoSize = true;
        _registerHeading.Location = new Point(0, 2);

        _onlyOpen.Font = UiTheme.Small;
        _onlyOpen.ForeColor = UiTheme.TextSecondary;
        _onlyOpen.AutoSize = true;
        _onlyOpen.Location = new Point(0, 30);
        _onlyOpen.Checked = true;

        _categoryFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _categoryFilter.FlatStyle = FlatStyle.Flat;
        _categoryFilter.Font = UiTheme.Small;
        _categoryFilter.Bounds = new Rectangle(220, 28, 260, UiTheme.InputHeight);

        _open.Bounds = new Rectangle(492, 27, 220, UiTheme.ButtonHeight - 4);
        _refresh.Bounds = new Rectangle(722, 27, 110, UiTheme.ButtonHeight - 4);

        filterBar.Controls.AddRange([_registerHeading, _onlyOpen, _categoryFilter, _open, _refresh]);

        BuildRegisterColumns();

        var registerHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        registerHost.Controls.Add(_register);

        top.Controls.Add(registerHost);
        top.Controls.Add(filterBar);

        // --- detail and actions ------------------------------------------------------------------------

        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

        _detailHeading.Font = UiTheme.SectionTitle;
        _detailHeading.ForeColor = UiTheme.TextPrimary;
        _detailHeading.Dock = DockStyle.Top;
        _detailHeading.Height = 26;

        _detail.Font = UiTheme.Body;
        _detail.ForeColor = UiTheme.TextSecondary;
        _detail.Dock = DockStyle.Top;
        _detail.Height = 62;
        _detail.BackColor = UiTheme.SurfaceRaised;
        _detail.Padding = new Padding(UiTheme.Gutter, UiTheme.GutterTight, UiTheme.Gutter, 0);

        BuildActionColumns();

        var actionHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            Padding = new Padding(0, UiTheme.GutterTight, 0, 0)
        };
        actionHost.Controls.Add(_actions);

        // Two rows now: the four action-level operations, then the four file-level ones. Grouped
        // that way because "cancel this action" and "dismiss this non-conformity" are very
        // different acts and should not sit adjacent.
        var actionBar = new Panel { Dock = DockStyle.Bottom, Height = 74, BackColor = UiTheme.Surface };

        _addAction.Bounds = new Rectangle(0, 4, 170, UiTheme.ButtonHeight);
        _startAction.Bounds = new Rectangle(180, 4, 150, UiTheme.ButtonHeight);
        _completeAction.Bounds = new Rectangle(340, 4, 170, UiTheme.ButtonHeight);
        _cancelAction.Bounds = new Rectangle(520, 4, 170, UiTheme.ButtonHeight);

        _analysis.Bounds = new Rectangle(0, 40, 230, UiTheme.ButtonHeight);
        _close.Bounds = new Rectangle(240, 40, 180, UiTheme.ButtonHeight);
        _reopen.Bounds = new Rectangle(430, 40, 170, UiTheme.ButtonHeight);
        _dismiss.Bounds = new Rectangle(610, 40, 170, UiTheme.ButtonHeight);

        _status.Bounds = new Rectangle(792, 44, 700, 20);
        _status.Font = UiTheme.Small;
        _status.ForeColor = UiTheme.TextSecondary;
        _status.AutoSize = false;

        actionBar.Controls.AddRange([_addAction, _startAction, _completeAction, _cancelAction,
                                     _analysis, _close, _reopen, _dismiss, _status]);

        bottom.Controls.Add(actionHost);
        bottom.Controls.Add(actionBar);
        bottom.Controls.Add(_detail);
        bottom.Controls.Add(_detailHeading);

        split.Panel1.Controls.Add(top);
        split.Panel2.Controls.Add(bottom);

        Controls.Add(split);

        SetDetailEnabled(false);
    }

    private void BuildRegisterColumns()
    {
        _register.Columns.Clear();

        void Add(string name, int weight, Font? font = null,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => _register.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = font ?? UiTheme.Body, Alignment = align
                }
            });

        Add("reference", 10, UiTheme.Monospace);
        Add("severity", 9, UiTheme.SmallBold);
        Add("category", 13, UiTheme.Small);
        Add("description", 26);
        Add("detected", 9, UiTheme.Monospace);
        Add("owner", 12, UiTheme.Small);
        Add("due", 8, UiTheme.Monospace);
        Add("actions", 6, UiTheme.Monospace, DataGridViewContentAlignment.MiddleRight);
        Add("status", 11, UiTheme.Small);
    }

    private void BuildActionColumns()
    {
        _actions.Columns.Clear();

        void Add(string name, int weight, Font? font = null)
            => _actions.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, FillWeight = weight, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Font = font ?? UiTheme.Body }
            });

        Add("kind", 16, UiTheme.Small);
        Add("description", 40);
        Add("owner", 14, UiTheme.Small);
        Add("target", 10, UiTheme.Monospace);
        Add("status", 12, UiTheme.Small);
        Add("completed", 10, UiTheme.Monospace);
    }

    private void WireBehaviour()
    {
        _refresh.Click += async (_, _) => await ActivateAsync();
        _onlyOpen.CheckedChanged += async (_, _) => await ActivateAsync();
        _categoryFilter.SelectedIndexChanged += async (_, _) => await ActivateAsync();

        _register.SelectionChanged += async (_, _) => await _selectionLoads.RunAsync(() => LoadDetailAsync());

        _open.Click += async (_, _) => await OpenNewAsync();
        _addAction.Click += async (_, _) => await AddActionAsync();
        _completeAction.Click += async (_, _) => await CompleteActionAsync();
        _analysis.Click += async (_, _) => await RecordAnalysisAsync();
        _close.Click += async (_, _) => await CloseAsync();
        _reopen.Click += async (_, _) => await ReopenAsync();
        _dismiss.Click += async (_, _) => await DismissAsync();
        _startAction.Click += async (_, _) => await StartActionAsync();
        _cancelAction.Click += async (_, _) => await CancelActionAsync();

        _actions.SelectionChanged += (_, _) => RefreshActionButtons();

        _register.CellFormatting += OnRegisterFormatting;
        _actions.CellFormatting += OnActionFormatting;
    }

    private void OnRegisterFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _register.Rows.Count) return;
        if (_register.Rows[e.RowIndex].Tag is not NonConformityRow row) return;

        // Severity drives the colour; overdue and recurrence add emphasis on top of it.
        var (foreground, background) = row.Severity switch
        {
            NonConformitySeverity.Critical => (UiTheme.Critical, UiTheme.CriticalBackground),
            NonConformitySeverity.Major => (UiTheme.Abnormal, UiTheme.AbnormalBackground),
            _ => (UiTheme.TextPrimary, UiTheme.SurfaceRaised)
        };

        e.CellStyle!.ForeColor = foreground;
        e.CellStyle.BackColor = background;

        if (row.Status == NonConformityStatus.Closed)
        {
            e.CellStyle.ForeColor = UiTheme.TextMuted;
            e.CellStyle.BackColor = UiTheme.SurfaceRaised;
        }
        else if (row.IsOverdue)
        {
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
        }
    }

    private void OnActionFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _actions.Rows.Count) return;
        if (_actions.Rows[e.RowIndex].Tag is not CorrectiveActionRow row) return;

        if (row.Status == CorrectiveActionStatus.Completed)
            e.CellStyle!.ForeColor = UiTheme.Normal;
        else if (row.IsOverdue)
        {
            e.CellStyle!.ForeColor = UiTheme.Critical;
            e.CellStyle.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
        }
    }

    protected override void ApplyTexts()
    {
        _registerHeading.Text = T("NonConformity.Plural");
        _onlyOpen.Text = T("NcStatus.Open");
        _open.Text = T("NonConformity.OpenNew");
        _addAction.Text = T("NonConformity.AddAction");
        _completeAction.Text = T("NonConformity.CompleteAction");
        _analysis.Text = T("NonConformity.RecordAnalysis");
        _close.Text = T("NonConformity.Close");
        _reopen.Text = T("NonConformity.Reopen");
        _dismiss.Text = T("NonConformity.Dismiss");
        _startAction.Text = T("NonConformity.StartAction");
        _cancelAction.Text = T("NonConformity.CancelAction");
        _refresh.Text = T("Action.Refresh");
        _detailHeading.Text = T("NonConformity.Actions");

        var selected = _categoryFilter.SelectedIndex;
        _categoryFilter.BeginUpdate();
        _categoryFilter.Items.Clear();
        _categoryFilter.Items.Add(new CategoryChoice(null, T("Action.SelectAll")));

        foreach (var category in Enum.GetValues<NonConformityCategory>())
            _categoryFilter.Items.Add(new CategoryChoice(category, T($"NcCategory.{category}")));

        _categoryFilter.SelectedIndex = selected >= 0 ? selected : 0;
        _categoryFilter.EndUpdate();

        if (_register.Columns.Count > 0)
        {
            _register.Column("reference").HeaderText = T("NonConformity.Reference");
            _register.Column("severity").HeaderText = T("NonConformity.Severity");
            _register.Column("category").HeaderText = T("NonConformity.Category");
            _register.Column("description").HeaderText = T("Field.Description");
            _register.Column("detected").HeaderText = T("NonConformity.DetectedAt");
            _register.Column("owner").HeaderText = T("NonConformity.AssignedTo");
            _register.Column("due").HeaderText = T("NonConformity.DueDate");
            _register.Column("actions").HeaderText = T("NonConformity.Actions");
            _register.Column("status").HeaderText = T("Field.Status");
        }

        if (_actions.Columns.Count > 0)
        {
            _actions.Column("kind").HeaderText = T("Field.Name");
            _actions.Column("description").HeaderText = T("Field.Description");
            _actions.Column("owner").HeaderText = T("NonConformity.AssignedTo");
            _actions.Column("target").HeaderText = T("NonConformity.DueDate");
            _actions.Column("status").HeaderText = T("Field.Status");
            _actions.Column("completed").HeaderText = T("Field.Date");
        }

        if (_rows.Count > 0) RenderRegister();
    }

    // ---- Loading -------------------------------------------------------------------------------------

    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        try
        {
            var category = (_categoryFilter.SelectedItem as CategoryChoice)?.Value;

            var result = await _service.GetRegisterAsync(_onlyOpen.Checked, category, ct);

            if (result.IsFailure)
            {
                _dialogs.ShowFailure(result);
                return;
            }

            _rows = result.Value;
            RenderRegister();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the non-conformity register failed.");
            _dialogs.Error(T("Message.UnexpectedError"));
        }
    }

    private void RenderRegister()
    {
        _register.SuspendLayout();
        _register.Rows.Clear();

        foreach (var row in _rows)
        {
            var description = row.Description.For(Localizer.CurrentLanguage);

            // Markers appended to the reference, so the most important facts about the file are
            // visible in the narrowest column.
            var reference = row.Reference
                            + (row.IsRecurrence ? " ↻" : string.Empty)
                            + (row.AffectedReleasedResults ? " ⚠" : string.Empty);

            var index = _register.Rows.Add(
                reference,
                T($"NcSeverity.{row.Severity}"),
                T($"NcCategory.{row.Category}"),
                description.Length > 120 ? description[..119] + "…" : description,
                row.DetectedAt.ToString("dd/MM/yy"),
                row.AssignedToName ?? string.Empty,
                row.DueDate?.ToString("dd/MM/yy") ?? string.Empty,
                row.OpenActionCount > 0
                    ? $"{row.ActionCount - row.OpenActionCount}/{row.ActionCount}"
                    : row.ActionCount.ToString(),
                T($"NcStatus.{row.Status}"));

            var gridRow = _register.Rows[index];
            gridRow.Tag = row;

            var tooltip = new List<string> { description };

            if (row.IsRecurrence) tooltip.Add(T("NonConformity.Recurrence"));

            if (row.AffectedReleasedResults)
                tooltip.Add($"{T("NonConformity.AffectedResults")} : " +
                            (row.AffectedPatientCount?.ToString() ?? "?"));

            if (row.DaysOpen is { } days)
                tooltip.Add($"{T("NonConformity.DaysOpen")} : {days}");

            gridRow.Cells["description"].ToolTipText = string.Join(Environment.NewLine, tooltip);
        }

        _register.ResumeLayout(performLayout: true);

        var overdue = _rows.Count(r => r.IsOverdue);

        _registerHeading.Text = $"{T("NonConformity.Plural")} — {_rows.Count}" +
                                (overdue > 0 ? $" · {overdue} {T("Order.Overdue").ToLowerInvariant()}" : string.Empty);

        if (_register.Rows.Count > 0) _register.SelectFirstRow();
        else ClearDetail();
    }

    private async Task LoadDetailAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row)
        {
            ClearDetail();
            return;
        }

        var lines = new List<string>
        {
            row.Description.For(Localizer.CurrentLanguage)
        };

        var facts = new List<string>
        {
            $"{T("NonConformity.DetectionSource")} : {T($"DetectionSource.{row.DetectionSource}")}",
            $"{T("NonConformity.DetectedBy")} : {row.DetectedByName}"
        };

        if (row.DaysOpen is { } days) facts.Add($"{T("NonConformity.DaysOpen")} : {days}");
        if (row.IsRecurrence) facts.Add(T("NonConformity.Recurrence"));

        if (row.AffectedReleasedResults)
            facts.Add($"⚠ {T("NonConformity.AffectedResults")}" +
                      (row.AffectedPatientCount is { } count ? $" : {count}" : string.Empty));

        lines.Add(string.Join("   ·   ", facts));

        _detail.Text = string.Join(Environment.NewLine, lines);
        _detail.ForeColor = row.AffectedReleasedResults ? UiTheme.Critical : UiTheme.TextSecondary;

        _detailHeading.Text = $"{row.Reference} — {T($"NcStatus.{row.Status}")}";

        try
        {
            var actions = await _service.GetActionsAsync(row.Id);

            if (actions.IsFailure) return;

            _currentActions = actions.Value;

            _actions.SuspendLayout();
            _actions.Rows.Clear();

            foreach (var action in _currentActions)
            {
                var index = _actions.Rows.Add(
                    T($"ActionKind.{action.Kind}"),
                    action.Description.For(Localizer.CurrentLanguage),
                    action.ResponsibleName,
                    action.TargetDate.ToString("dd/MM/yy"),
                    T($"ActionStatus.{action.Status}"),
                    action.CompletedOn?.ToString("dd/MM/yy") ?? string.Empty);

                _actions.Rows[index].Tag = action;
            }

            _actions.ResumeLayout(performLayout: true);

            SetDetailEnabled(row.Status != NonConformityStatus.Closed);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the corrective actions failed.");
        }
    }

    private void ClearDetail()
    {
        _currentActions = [];
        _actions.Rows.Clear();
        _detail.Text = string.Empty;
        _detailHeading.Text = T("App.NoData");
        SetDetailEnabled(false);
    }

    private void SetDetailEnabled(bool enabled)
    {
        var row = _register.CurrentRow?.Tag as NonConformityRow;

        // A closed or dismissed file is not editable; a live one is not reopenable. Each operation
        // is offered only where it means something, so the bar states the file's position rather
        // than presenting eight buttons of which six would be refused.
        var closed = row is { Status: NonConformityStatus.Closed
                                    or NonConformityStatus.Rejected };

        _addAction.Enabled = enabled && !closed;
        _analysis.Enabled = enabled && !closed;
        _close.Enabled = enabled && !closed;
        _dismiss.Enabled = enabled && !closed;

        _reopen.Enabled = enabled && row is { Status: NonConformityStatus.Closed };

        RefreshActionButtons();
    }

    /// <summary>Enables the per-action buttons against the selected action's own status.</summary>
    private void RefreshActionButtons()
    {
        var file = _register.CurrentRow?.Tag as NonConformityRow;
        var action = _actions.CurrentRow?.Tag as CorrectiveActionRow;

        var editable = file is not null
                       && file.Status is not (NonConformityStatus.Closed
                                           or NonConformityStatus.Rejected);

        _startAction.Enabled = editable && action is { Status: CorrectiveActionStatus.Planned };

        _completeAction.Enabled = editable
                                  && action is { Status: CorrectiveActionStatus.Planned
                                                       or CorrectiveActionStatus.InProgress };

        // A completed action is evidence and stays; an uncompleted one entered by mistake has to
        // be removable, because Close() requires every action to be completed and one wrong entry
        // would otherwise block the file for ever.
        _cancelAction.Enabled = editable
                                && action is { Status: CorrectiveActionStatus.Planned
                                                     or CorrectiveActionStatus.InProgress };
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task OpenNewAsync()
    {
        using var dialog = new NonConformityDialog(Localizer, _dialogs);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var result = await _service.OpenAsync(dialog.Request);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = $"{T("NonConformity.Reference")} {result.Value}";

        await ActivateAsync();
    }

    private async Task AddActionAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;

        using var dialog = new CorrectiveActionDialog(Localizer, _dialogs);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        var (kind, description, targetDate) = dialog.Result.Value;

        var result = await _service.AddActionAsync(new AddActionRequest(
            row.Id, kind, description, Guid.Empty, targetDate));

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadDetailAsync();
        await ActivateAsync();
    }

    private async Task CompleteActionAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;

        if (_actions.CurrentRow?.Tag is not CorrectiveActionRow action)
        {
            _status.Text = T("Message.NothingSelected");
            return;
        }

        if (action.Status == CorrectiveActionStatus.Completed)
        {
            _dialogs.Info(T("ActionStatus.Completed"));
            return;
        }

        // Evidence rather than a tick: "what shows this was done?" is what an assessor asks.
        var evidence = _dialogs.PromptForText(
            T("NonConformity.EvidencePrompt"), T("NonConformity.CompleteAction"));

        if (evidence is null) return;

        var result = await _service.CompleteActionAsync(
            row.Id, action.Id,
            string.IsNullOrWhiteSpace(evidence) ? null : new MultilingualText(evidence));

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await LoadDetailAsync();
        await ActivateAsync();
    }

    private async Task RecordAnalysisAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;

        using var dialog = new NonConformityAnalysisDialog(Localizer, row);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _service.RecordAnalysisAsync(
            row.Id,
            dialog.RootCause,
            dialog.RootCauseAnalysis,
            dialog.ClinicalImpact,
            dialog.EffectivenessVerification,
            dialog.AmendedReportsIssued);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }

    /// <summary>
    /// Attempts closure. A refusal is shown in full, because each error names a specific ISO 15189
    /// obligation the file has not yet met — the root cause, the effectiveness check, an unfinished
    /// action — and the quality manager needs to know which.
    /// </summary>
    private async Task CloseAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;

        if (!_dialogs.Confirm(
                T("NonConformity.ConfirmClose", row.Reference), T("NonConformity.Close")))
            return;

        var result = await _service.CloseAsync(row.Id);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("NonConformity.Close"));
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = $"{row.Reference} — {T("NcStatus.Closed")}";

        await ActivateAsync();
    }

    /// <summary>
    /// Reopens a closed file. Resumed rather than restarted: the actions and the analysis already
    /// recorded stay, and the days-open count keeps running from the original detection.
    /// </summary>
    private async Task ReopenAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;

        var reason = _dialogs.PromptForReason(
            T("NonConformity.ReopenPrompt", row.Reference), T("NonConformity.Reopen"),
            minimumLength: 10);

        if (reason is null) return;

        var result = await _service.ReopenAsync(row.Id, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("NonConformity.Reopen"));
            return;
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = $"{row.Reference} — {T("NcStatus.InProgress")}";

        await ActivateAsync();
    }

    /// <summary>
    /// Dismisses a report judged not to be a non-conformity. Kept distinct from closure so the
    /// quality indicators stay honest: a closed file counts as something that happened, a dismissed
    /// one records that the report was unfounded.
    /// </summary>
    private async Task DismissAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;

        if (!_dialogs.Confirm(T("NonConformity.ConfirmDismiss", row.Reference),
                               T("NonConformity.Dismiss")))
            return;

        var justification = _dialogs.PromptForReason(
            T("NonConformity.DismissPrompt", row.Reference), T("NonConformity.Dismiss"),
            minimumLength: 15);

        if (justification is null) return;

        var result = await _service.DismissAsync(row.Id, justification);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("NonConformity.Dismiss"));
            return;
        }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = $"{row.Reference} — {T("NcStatus.Rejected")}";

        await ActivateAsync();
    }

    private async Task StartActionAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;
        if (_actions.CurrentRow?.Tag is not CorrectiveActionRow action) return;

        var result = await _service.StartActionAsync(row.Id, action.Id);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        await ActivateAsync();
    }

    /// <summary>
    /// Withdraws an action entered in error. This is the one that unblocks a file: closure requires
    /// every action to be completed, so a single mis-entered action used to hold the file open
    /// permanently with no way to remove it.
    /// </summary>
    private async Task CancelActionAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;
        if (_actions.CurrentRow?.Tag is not CorrectiveActionRow action) return;

        var reason = _dialogs.PromptForReason(
            T("NonConformity.CancelActionPrompt"), T("NonConformity.CancelAction"));

        if (reason is null) return;

        var result = await _service.CancelActionAsync(row.Id, action.Id, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("NonConformity.CancelAction"));
            return;
        }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = T("Message.Saved");

        await ActivateAsync();
    }

    private sealed record CategoryChoice(NonConformityCategory? Value, string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>Opening a non-conformity: what happened, how bad, and did it reach a patient.</summary>
internal sealed class NonConformityDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _category = new();
    private readonly ComboBox _severity = new();
    private readonly ComboBox _source = new();
    private readonly TextBox _description = UiTheme.Input(520);
    private readonly TextBox _immediateAction = UiTheme.Input(520);
    private readonly CheckBox _affectedResults = new();
    private readonly NumericUpDown _patientCount = new();
    private readonly DateTimePicker _dueDate = new();

    internal CreateNonConformityRequest? Request { get; private set; }

    internal NonConformityDialog(ILocalizer localizer, IUserDialogs dialogs)
    {
        _localizer = localizer;
        _dialogs = dialogs;

        Text = localizer["NonConformity.OpenNew"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(580, 470);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        BuildLayout();

        // The patient count only matters if released results were affected.
        _affectedResults.CheckedChanged += (_, _) =>
            _patientCount.Enabled = _affectedResults.Checked;
    }

    private void BuildLayout()
    {
        var y = 16;

        Label Field(string key, int top)
        {
            var label = UiTheme.FieldLabel(_localizer[key]);
            label.Location = new Point(20, top);
            Controls.Add(label);
            return label;
        }

        Field("NonConformity.Category", y);
        _category.DropDownStyle = ComboBoxStyle.DropDownList;
        _category.FlatStyle = FlatStyle.Flat;
        _category.Bounds = new Rectangle(20, y + 18, 260, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<NonConformityCategory>())
            _category.Items.Add(new Choice<NonConformityCategory>(
                value, _localizer[$"NcCategory.{value}"]));

        _category.SelectedIndex = 0;

        var severityLabel = Field("NonConformity.Severity", y);
        severityLabel.Location = new Point(300, y);

        _severity.DropDownStyle = ComboBoxStyle.DropDownList;
        _severity.FlatStyle = FlatStyle.Flat;
        _severity.Bounds = new Rectangle(300, y + 18, 240, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<NonConformitySeverity>())
            _severity.Items.Add(new Choice<NonConformitySeverity>(
                value, _localizer[$"NcSeverity.{value}"]));

        _severity.SelectedIndex = 0;

        y += 54;
        Field("NonConformity.DetectionSource", y);
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        _source.FlatStyle = FlatStyle.Flat;
        _source.Bounds = new Rectangle(20, y + 18, 520, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<DetectionSource>())
            _source.Items.Add(new Choice<DetectionSource>(
                value, _localizer[$"DetectionSource.{value}"]));

        _source.SelectedIndex = 0;

        y += 54;
        Field("Field.Description", y);
        _description.Bounds = new Rectangle(20, y + 18, 520, 66);
        _description.Multiline = true;

        y += 94;
        Field("NonConformity.ImmediateAction", y);
        _immediateAction.Bounds = new Rectangle(20, y + 18, 520, 52);
        _immediateAction.Multiline = true;

        y += 80;
        _affectedResults.Text = _localizer["NonConformity.AffectedResults"];
        _affectedResults.Font = UiTheme.BodyBold;
        _affectedResults.ForeColor = UiTheme.Critical;
        _affectedResults.AutoSize = true;
        _affectedResults.Location = new Point(20, y);

        _patientCount.Bounds = new Rectangle(360, y - 3, 90, UiTheme.InputHeight);
        _patientCount.Maximum = 100000;
        _patientCount.Enabled = false;

        y += 32;
        Field("NonConformity.DueDate", y);
        _dueDate.Bounds = new Rectangle(20, y + 18, 200, UiTheme.InputHeight);
        _dueDate.Format = DateTimePickerFormat.Short;
        _dueDate.Value = DateTime.Today.AddDays(14);

        var ok = UiTheme.PrimaryButton(_localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 420, 260, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(_localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(300, 420, 240, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_category, _severity, _source, _description, _immediateAction,
                           _affectedResults, _patientCount, _dueDate, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_description.Text))
        {
            _dialogs.Warn(_localizer["Nc_DescriptionRequired"]);
            _description.Focus();
            return;
        }

        Request = new CreateNonConformityRequest
        {
            Category = ((Choice<NonConformityCategory>)_category.SelectedItem!).Value,
            Severity = ((Choice<NonConformitySeverity>)_severity.SelectedItem!).Value,
            DetectionSource = ((Choice<DetectionSource>)_source.SelectedItem!).Value,
            Description = new MultilingualText(_description.Text.Trim()),
            ImmediateAction = string.IsNullOrWhiteSpace(_immediateAction.Text)
                ? null
                : new MultilingualText(_immediateAction.Text.Trim()),
            AffectedReleasedResults = _affectedResults.Checked,
            AffectedPatientCount = _affectedResults.Checked ? (int)_patientCount.Value : null,
            DueDate = DateOnly.FromDateTime(_dueDate.Value.Date)
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    internal sealed record Choice<T>(T Value, string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>Adding a corrective action, with the correction/corrective/preventive distinction.</summary>
internal sealed class CorrectiveActionDialog : Form
{
    private readonly ILocalizer _localizer;
    private readonly IUserDialogs _dialogs;

    private readonly ComboBox _kind = new();
    private readonly TextBox _description = UiTheme.Input(460);
    private readonly DateTimePicker _target = new();

    internal (ActionKind Kind, MultilingualText Description, DateOnly TargetDate)? Result
    { get; private set; }

    internal CorrectiveActionDialog(ILocalizer localizer, IUserDialogs dialogs)
    {
        _localizer = localizer;
        _dialogs = dialogs;

        Text = localizer["NonConformity.AddAction"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 300);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var kindLabel = UiTheme.FieldLabel(localizer["Field.Name"]);
        kindLabel.Location = new Point(20, 16);

        _kind.DropDownStyle = ComboBoxStyle.DropDownList;
        _kind.FlatStyle = FlatStyle.Flat;
        _kind.Bounds = new Rectangle(20, 34, 460, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<ActionKind>())
            _kind.Items.Add(new NonConformityDialog.Choice<ActionKind>(
                value, localizer[$"ActionKind.{value}"]));

        _kind.SelectedIndex = 1;   // corrective action, the one ISO 15189 actually asks for

        var descriptionLabel = UiTheme.FieldLabel(localizer["Field.Description"]);
        descriptionLabel.Location = new Point(20, 70);

        _description.Bounds = new Rectangle(20, 88, 460, 90);
        _description.Multiline = true;

        var targetLabel = UiTheme.FieldLabel(localizer["NonConformity.DueDate"]);
        targetLabel.Location = new Point(20, 190);

        _target.Bounds = new Rectangle(20, 208, 200, UiTheme.InputHeight);
        _target.Format = DateTimePickerFormat.Short;
        _target.Value = DateTime.Today.AddDays(14);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 252, 220, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(260, 252, 220, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([kindLabel, _kind, descriptionLabel, _description,
                           targetLabel, _target, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_description.Text))
        {
            _dialogs.Warn(_localizer["Nc_ActionDescriptionRequired"]);
            _description.Focus();
            return;
        }

        Result = (((NonConformityDialog.Choice<ActionKind>)_kind.SelectedItem!).Value,
                  new MultilingualText(_description.Text.Trim()),
                  DateOnly.FromDateTime(_target.Value.Date));

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Root cause, clinical impact and effectiveness verification.
/// <para>
/// One dialog rather than three, because the domain's closure check needs them together for a
/// major or critical item — and splitting them across screens produces a file that cannot close
/// while nobody can see which piece is missing.
/// </para>
/// </summary>
internal sealed class NonConformityAnalysisDialog : Form
{
    private readonly ComboBox _rootCause = new();
    private readonly TextBox _rootCauseAnalysis = UiTheme.Input(520);
    private readonly TextBox _clinicalImpact = UiTheme.Input(520);
    private readonly TextBox _effectiveness = UiTheme.Input(520);
    private readonly CheckBox _amendedReports = new();

    internal RootCauseCategory? RootCause =>
        _rootCause.SelectedItem is NonConformityDialog.Choice<RootCauseCategory> choice
            ? choice.Value
            : null;

    internal MultilingualText? RootCauseAnalysis => Wrap(_rootCauseAnalysis.Text);
    internal MultilingualText? ClinicalImpact => Wrap(_clinicalImpact.Text);
    internal MultilingualText? EffectivenessVerification => Wrap(_effectiveness.Text);
    internal bool AmendedReportsIssued => _amendedReports.Checked;

    private static MultilingualText? Wrap(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new MultilingualText(text.Trim());

    internal NonConformityAnalysisDialog(ILocalizer localizer, NonConformityRow row)
    {
        Text = $"{localizer["NonConformity.RecordAnalysis"]} — {row.Reference}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(580, 520);
        BackColor = UiTheme.SurfaceRaised;
        Font = UiTheme.Body;

        var y = 16;

        Label Field(string key, int top, Color? colour = null)
        {
            var label = UiTheme.FieldLabel(localizer[key]);
            label.Location = new Point(20, top);
            if (colour is { } c) label.ForeColor = c;
            Controls.Add(label);
            return label;
        }

        Field("NonConformity.RootCause", y);
        _rootCause.DropDownStyle = ComboBoxStyle.DropDownList;
        _rootCause.FlatStyle = FlatStyle.Flat;
        _rootCause.Bounds = new Rectangle(20, y + 18, 520, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<RootCauseCategory>())
            _rootCause.Items.Add(new NonConformityDialog.Choice<RootCauseCategory>(
                value, localizer[$"RootCause.{value}"]));

        _rootCause.SelectedIndex = 0;

        y += 54;
        Field("NonConformity.RootCause", y);
        _rootCauseAnalysis.Bounds = new Rectangle(20, y + 18, 520, 70);
        _rootCauseAnalysis.Multiline = true;

        y += 98;
        // Emphasised when released results were affected, because the domain will then require it.
        Field("NonConformity.ClinicalImpact", y,
              row.AffectedReleasedResults ? UiTheme.Critical : null);
        _clinicalImpact.Bounds = new Rectangle(20, y + 18, 520, 70);
        _clinicalImpact.Multiline = true;

        y += 98;
        Field("NonConformity.Effectiveness", y);
        _effectiveness.Bounds = new Rectangle(20, y + 18, 520, 70);
        _effectiveness.Multiline = true;

        y += 98;
        _amendedReports.Text = localizer["NonConformity.AmendedReportsIssued"];
        _amendedReports.AutoSize = true;
        _amendedReports.Location = new Point(20, y);
        _amendedReports.Visible = row.AffectedReleasedResults;

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 470, 260, UiTheme.ButtonHeight);
        ok.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(300, 470, 240, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_rootCause, _rootCauseAnalysis, _clinicalImpact,
                           _effectiveness, _amendedReports, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
