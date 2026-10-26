using MarocLIS.Application.Quality;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
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
    private readonly QhAside _aside = new();
    private readonly Label _count = new();
    private readonly CheckBox _onlyOpen = new();
    private readonly ComboBox _categoryFilter = new();

    private readonly Button _open = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _addAction = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _completeAction = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _analysis = UiTheme.SecondaryButton(string.Empty);
    // Secondary: opening a file is the screen's one primary action.
    private readonly Button _close = UiTheme.SecondaryButton(string.Empty);
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
        Padding = QhPage.PagePadding;

        _categoryFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _categoryFilter.FlatStyle = FlatStyle.Flat;
        _onlyOpen.Checked = true;

        BuildRegisterColumns();
        BuildActionColumns();

        var header = QhPage.Header(_registerHeading, _count, _refresh, _open);
        var toolbar = QhPage.Toolbar(QhPage.Chip(_onlyOpen), QhPage.Filter(_categoryFilter, 260), _onlyOpen);

        // --- the register over the selected file's actions ---------------------------------------------

        var split = new ProportionalSplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 290,
            SplitterWidth = 14,
            BackColor = UiTheme.Surface
        };

        var registerCard = QhPage.Card(_register);
        registerCard.Dock = DockStyle.Fill;

        // The four action-level operations at the foot of the actions, away from the file-level ones
        // in the aside: "cancel this action" and "dismiss this non-conformity" are very different
        // acts and should not sit adjacent.
        var actionBody = new Panel { BackColor = UiTheme.SurfaceRaised, Margin = Padding.Empty };
        _actions.Dock = DockStyle.Fill;
        actionBody.Controls.Add(_actions);
        actionBody.Controls.Add(new QhSelectionBar(null, _addAction, _startAction, _completeAction, _cancelAction));

        var actionsCard = QhPage.TitledCard(_detailHeading, null, actionBody);
        actionsCard.Dock = DockStyle.Fill;

        split.Panel1.Controls.Add(registerCard);
        split.Panel2.Controls.Add(actionsCard);

        // --- the selected file: its facts and the file-level operations ----------------------------------

        _aside.SetActions(_analysis, _close, _reopen, _dismiss);
        _aside.AddBelow(QhPage.StatusLine(_status));

        Controls.Add(QhPage.Frame(2, header, toolbar, QhPage.WithAside(split, _aside, 320)));

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

        // Writes run through RunBusyAsync, as on the billing screens: a double-click records one
        // action, and leaving the screen mid-write no longer disposes its context under it.
        _open.Click += async (_, _) => await RunBusyAsync(OpenNewAsync);
        _addAction.Click += async (_, _) => await RunBusyAsync(AddActionAsync);
        _completeAction.Click += async (_, _) => await RunBusyAsync(CompleteActionAsync);
        _analysis.Click += async (_, _) => await RunBusyAsync(RecordAnalysisAsync);
        _close.Click += async (_, _) => await RunBusyAsync(CloseAsync);
        _reopen.Click += async (_, _) => await RunBusyAsync(ReopenAsync);
        _dismiss.Click += async (_, _) => await RunBusyAsync(DismissAsync);
        _startAction.Click += async (_, _) => await RunBusyAsync(StartActionAsync);
        _cancelAction.Click += async (_, _) => await RunBusyAsync(CancelActionAsync);

        // Greyed out for a user without the right; the service refuses them in any case.
        PermissionGate.Require([Permissions.Quality.ReportNonConformity, Permissions.Quality.ManageNonConformity], _open);
        PermissionGate.Require(Permissions.Quality.ManageNonConformity, _addAction, _completeAction, _analysis, _close, _reopen, _dismiss, _startAction, _cancelAction);

        _actions.SelectionChanged += (_, _) => RefreshActionButtons();

        _register.CellFormatting += OnRegisterFormatting;
        QhGridPills.Attach(_register, "severity",
                           r => r.Tag is NonConformityRow row ? QhTone.Of(NonConformityScreenRules.SeverityTone(row.Severity)) : null);
        QhGridPills.Attach(_register, "status",
                           r => r.Tag is NonConformityRow row ? QhTone.Of(NonConformityScreenRules.StatusTone(row.Status)) : null);
        _actions.CellFormatting += OnActionFormatting;
        QhGridPills.Attach(_actions, "status", r => r.Tag is CorrectiveActionRow action
            ? QhTone.Of(NonConformityScreenRules.ActionTone(action))
            : null);
    }

    private void OnRegisterFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _register.Rows.Count) return;
        if (_register.Rows[e.RowIndex].Tag is not NonConformityRow row) return;

        // Severity and status are pills; a closed file is muted, an overdue one bold, and its due
        // date in the critical ink.
        var emphasis = NonConformityScreenRules.Emphasis(row);
        if (emphasis == NonConformityEmphasis.Closed)
        {
            e.CellStyle!.ForeColor = UiTheme.TextMuted;
        }
        else if (emphasis == NonConformityEmphasis.Overdue)
        {
            e.CellStyle!.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
            if (_register.Columns[e.ColumnIndex].Name == "due") e.CellStyle.ForeColor = UiTheme.Critical;
        }
    }

    private void OnActionFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _actions.Rows.Count) return;
        if (_actions.Rows[e.RowIndex].Tag is not CorrectiveActionRow row) return;

        var ink = NonConformityScreenRules.ActionInk(row);
        if (QhTone.InkOf(ink) is { } colour) e.CellStyle!.ForeColor = colour;
        if (ink == QualityInk.Critical)
            e.CellStyle!.Font = new Font(e.CellStyle.Font ?? UiTheme.Body, FontStyle.Bold);
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
        _categoryFilter.Items.Add(new CategoryChoice(null, T("NonConformity.AllCategories")));

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
            _actions.Column("kind").HeaderText = T("NonConformity.ActionKind");
            _actions.Column("description").HeaderText = T("Field.Description");
            _actions.Column("owner").HeaderText = T("NonConformity.AssignedTo");
            _actions.Column("target").HeaderText = T("NonConformity.DueDate");
            _actions.Column("status").HeaderText = T("Field.Status");
            _actions.Column("completed").HeaderText = T("Field.Date");
        }

        if (_rows.Count > 0) RenderRegister((_register.CurrentRow?.Tag as NonConformityRow)?.Id);
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

            // The file being worked on stays current: after each action the register went back to
            // its first file, and the next action was added to another non-conformity's list.
            var keep = (_register.CurrentRow?.Tag as NonConformityRow)?.Id;

            _rows = result.Value;
            RenderRegister(keep);
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the non-conformity register failed.");
            _dialogs.Error(T(FailureMessages.KeyFor(ex)));
        }
    }

    private void RenderRegister(Guid? keep = null)
    {
        _register.SuspendLayout();
        _register.Rows.Clear();

        foreach (var row in _rows)
        {
            var description = row.Description.For(Localizer.CurrentLanguage);

            // Markers appended to the reference, so the most important facts about the file are
            // visible in the narrowest column.
            var index = _register.Rows.Add(
                NonConformityScreenRules.Reference(row),
                T($"NcSeverity.{row.Severity}"),
                T($"NcCategory.{row.Category}"),
                NonConformityScreenRules.ShortDescription(description),
                NonConformityScreenRules.ShortDate(row.DetectedAt),
                row.AssignedToName ?? string.Empty,
                NonConformityScreenRules.ShortDate(row.DueDate),
                NonConformityScreenRules.ActionsCell(row),
                T($"NcStatus.{row.Status}"));

            var gridRow = _register.Rows[index];
            gridRow.Tag = row;

            gridRow.Cells["description"].ToolTipText = string.Join(
                Environment.NewLine, NonConformityScreenRules.Tooltip(row, Localizer.CurrentLanguage, T));
        }

        _register.ResumeLayout(performLayout: true);

        _registerHeading.Text = T("NonConformity.Plural");
        _count.Text = NonConformityScreenRules.Count(_rows, T);

        var kept = _register.Rows.Cast<DataGridViewRow>()
                            .FirstOrDefault(r => keep is not null && r.Tag is NonConformityRow row && row.Id == keep);
        if (kept is { Index: > 0 }) _register.MakeCurrent(kept);
        else if (_register.Rows.Count > 0) _register.SelectFirstRow();
        else ClearDetail();
    }

    private async Task LoadDetailAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row)
        {
            ClearDetail();
            return;
        }

        ShowFile(row);

        _detailHeading.Text = NonConformityScreenRules.ActionsHeading(row, T);

        try
        {
            var actions = await _service.GetActionsAsync(row.Id);

            if (actions.IsFailure) return;

            // The action just started or completed stays current when it is still the same file's.
            var keepAction = (_actions.CurrentRow?.Tag as CorrectiveActionRow)?.Id;

            _currentActions = actions.Value;

            _actions.SuspendLayout();
            _actions.Rows.Clear();

            foreach (var action in _currentActions)
            {
                var index = _actions.Rows.Add(
                    T($"ActionKind.{action.Kind}"),
                    action.Description.For(Localizer.CurrentLanguage),
                    action.ResponsibleName,
                    NonConformityScreenRules.ShortDate(action.TargetDate),
                    T($"ActionStatus.{action.Status}"),
                    NonConformityScreenRules.ShortDate(action.CompletedOn));

                _actions.Rows[index].Tag = action;
            }

            _actions.ResumeLayout(performLayout: true);

            var keptAction = _actions.Rows.Cast<DataGridViewRow>()
                                    .FirstOrDefault(r => keepAction is not null && r.Tag is CorrectiveActionRow a && a.Id == keepAction);
            if (keptAction is not null) _actions.MakeCurrent(keptAction);

            // A file is selected: SetDetailEnabled itself narrows the bar to the file's status, so
            // a closed file keeps only Reopen (passing false here left Reopen unreachable).
            SetDetailEnabled(true);
        }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading the corrective actions failed.");
        }
    }

    /// <summary>
    /// The selected file in the aside: its reference and status, then how bad, where it came from,
    /// who found it, how long it has been open, and whether it reached a patient.
    /// </summary>
    private void ShowFile(NonConformityRow row)
    {
        _aside.Show(NonConformityScreenRules.Reference(row),
                    NonConformityScreenRules.Subtitle(row, T),
                    (T($"NcStatus.{row.Status}"), QhTone.Of(NonConformityScreenRules.StatusTone(row.Status))),
                    NonConformityScreenRules.Facts(row, T).Select(f => (f.Label, f.Value, f.Mono)),
                    row.Description.For(Localizer.CurrentLanguage),
                    row.AffectedReleasedResults ? UiTheme.Critical : null);
    }

    private void ClearDetail()
    {
        _currentActions = [];
        _actions.Rows.Clear();
        _aside.Clear(T("App.NoData"));
        _detailHeading.Text = T("App.NoData");
        SetDetailEnabled(false);
    }

    private void SetDetailEnabled(bool enabled)
    {
        // A closed or dismissed file is not editable; a live one is not reopenable. Each operation
        // is offered only where it means something, so the bar states the file's position rather
        // than presenting eight buttons of which six would be refused.
        var actions = CurrentActions(enabled);

        _addAction.Enabled = actions.AddAction;
        _analysis.Enabled = actions.RecordAnalysis;
        _close.Enabled = actions.Close;
        _dismiss.Enabled = actions.Dismiss;
        _reopen.Enabled = actions.Reopen;

        RefreshActionButtons();
    }

    /// <summary>What the bar offers, by right and by the file's and the action's state (NonConformityScreenRules).</summary>
    private NonConformityScreenActions CurrentActions(bool enabled = true) =>
        NonConformityScreenRules.Actions(p => PermissionGate.Allows(p),
                                         enabled ? _register.CurrentRow?.Tag as NonConformityRow : null,
                                         _actions.CurrentRow?.Tag as CorrectiveActionRow);

    /// <summary>Enables the per-action buttons against the selected action's own status.</summary>
    private void RefreshActionButtons()
    {
        // A completed action is evidence and stays; an uncompleted one entered by mistake has to
        // be removable, because Close() requires every action to be completed or cancelled and one
        // wrong entry would otherwise block the file for ever.
        var actions = CurrentActions();
        _startAction.Enabled = actions.StartAction;
        _completeAction.Enabled = actions.CompleteAction;
        _cancelAction.Enabled = actions.CancelAction;
    }

    // ---- Actions ---------------------------------------------------------------------------------------

    private async Task OpenNewAsync()
    {
        using var dialog = new NonConformityDialog(Localizer, _dialogs)
        {
            FindSamples = term => _service.FindSamplesAsync(term)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;

        var result = await _service.OpenAsync(dialog.Request);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result);
            return;
        }

        _status.ForeColor = UiTheme.Normal;
        _status.Text = NonConformityScreenRules.Opened(result.Value, T);

        await ActivateAsync();
    }

    private async Task AddActionAsync()
    {
        if (_register.CurrentRow?.Tag is not NonConformityRow row) return;

        using var dialog = new CorrectiveActionDialog(Localizer, _dialogs);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        var result = await _service.AddActionAsync(NonConformityScreenRules.ToRequest(dialog.Result, row.Id));

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

        if (NonConformityScreenRules.AlreadyCompleted(action))
        {
            _dialogs.Info(T("ActionStatus.Completed"));
            return;
        }

        // Evidence rather than a tick: "what shows this was done?" is what an assessor asks.
        var evidence = _dialogs.PromptForText(
            T("NonConformity.EvidencePrompt"), T("NonConformity.CompleteAction"),
            maxLength: NonConformityScreenRules.TextMaxLength);

        if (evidence is null) return;

        var result = await _service.CompleteActionAsync(
            row.Id, action.Id, NonConformityScreenRules.Evidence(evidence));

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

        var recorded = await _service.GetAnalysisAsync(row.Id);
        if (recorded.IsFailure)
        {
            _dialogs.ShowFailure(recorded);
            return;
        }

        using var dialog = new NonConformityAnalysisDialog(Localizer, row, recorded.Value);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var analysis = dialog.Analysis;
        var result = await _service.RecordAnalysisAsync(
            row.Id,
            analysis.RootCause,
            analysis.RootCauseAnalysis,
            analysis.ClinicalImpact,
            analysis.EffectivenessVerification,
            analysis.AmendedReportsIssued);

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
        _status.Text = NonConformityScreenRules.StatusLine(row, NonConformityStatus.Closed, T);

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
            minimumLength: NonConformityScreenRules.ReopenReasonMinimumLength);

        if (reason is null) return;

        var result = await _service.ReopenAsync(row.Id, reason);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("NonConformity.Reopen"));
            return;
        }

        _status.ForeColor = UiTheme.Abnormal;
        _status.Text = NonConformityScreenRules.StatusLine(row, NonConformityStatus.InProgress, T);

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
            minimumLength: NonConformityScreenRules.DismissReasonMinimumLength);

        if (justification is null) return;

        var result = await _service.DismissAsync(row.Id, justification);

        if (result.IsFailure)
        {
            _dialogs.ShowFailure(result, T("NonConformity.Dismiss"));
            return;
        }

        _status.ForeColor = UiTheme.TextSecondary;
        _status.Text = NonConformityScreenRules.StatusLine(row, NonConformityStatus.Rejected, T);

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
            T("NonConformity.CancelActionPrompt"), T("NonConformity.CancelAction"),
            maxLength: NonConformityScreenRules.CancelReasonMaxLength);

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
    private readonly TextBox _sampleTerm = UiTheme.Input(200);
    private readonly Button _findSample = UiTheme.SecondaryButton(string.Empty);
    private readonly ComboBox _sample = new();
    private readonly NonConformityForm _sampleForm = new();

    internal CreateNonConformityRequest? Request { get; private set; }

    /// <summary>
    /// Finds the tubes for a barcode or a file's number (NonConformityService.FindSamplesAsync); the
    /// tube box stays off when the calling screen gives none.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, Task<Result<IReadOnlyList<NonConformitySampleChoice>>>>? FindSamples { get; init; }

    /// <summary>The instrument the file is about, when another screen opens it (a failed maintenance check).</summary>
    private Guid? _instrumentId;

    internal NonConformityDialog(ILocalizer localizer, IUserDialogs dialogs)
    {
        _localizer = localizer;
        _dialogs = dialogs;

        Text = localizer["NonConformity.OpenNew"];
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(580, 524);
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

        // The tube the file is about, found by its barcode or its file's number, as on the web.
        y += 54;
        Field("NonConformity.Sample", y);
        _sampleTerm.Bounds = new Rectangle(20, y + 18, 150, UiTheme.InputHeight);
        _sampleTerm.MaxLength = NonConformityScreenRules.SampleTermMaxLength;
        _sampleTerm.Tag = UiTags.LatinValue;
        _sampleTerm.PlaceholderText = _localizer["NonConformity.SamplePlaceholder"];
        _findSample.Text = _localizer["Action.Search"];
        _findSample.Bounds = new Rectangle(176, y + 18, 110, UiTheme.InputHeight);
        _findSample.Click += async (_, _) => await FindSamplesAsync();
        _sampleTerm.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await FindSamplesAsync();
        };
        _sample.DropDownStyle = ComboBoxStyle.DropDownList;
        _sample.FlatStyle = FlatStyle.Flat;
        _sample.Bounds = new Rectangle(292, y + 18, 248, UiTheme.InputHeight);
        _sample.Enabled = false;
        _sample.SelectedIndexChanged += (_, _) =>
            NonConformityScreenRules.PickSample(_sampleForm, (_sample.SelectedItem as Choice<Guid?>)?.Value);

        y += 54;
        Field("Field.Description", y);
        _description.Bounds = new Rectangle(20, y + 18, 520, 66);
        _description.Multiline = true;
        _description.MaxLength = NonConformityScreenRules.TextMaxLength;   // the column; beyond, the save failed with « Erreur inattendue »

        y += 94;
        Field("NonConformity.ImmediateAction", y);
        _immediateAction.Bounds = new Rectangle(20, y + 18, 520, 52);
        _immediateAction.Multiline = true;
        _immediateAction.MaxLength = NonConformityScreenRules.TextMaxLength;

        y += 80;
        _affectedResults.Text = _localizer["NonConformity.AffectedResults"];
        _affectedResults.Font = UiTheme.BodyBold;
        _affectedResults.ForeColor = UiTheme.Critical;
        _affectedResults.AutoSize = true;
        _affectedResults.Location = new Point(20, y);

        _patientCount.Bounds = new Rectangle(360, y - 3, 90, UiTheme.InputHeight);
        _patientCount.Maximum = NonConformityScreenRules.PatientCountMax;
        _patientCount.Enabled = false;

        y += 32;
        Field("NonConformity.DueDate", y);
        _dueDate.Bounds = new Rectangle(20, y + 18, 200, UiTheme.InputHeight);
        _dueDate.Format = DateTimePickerFormat.Short;
        _dueDate.Value = NonConformityScreenRules.NewForm(DateOnly.FromDateTime(DateTime.Today)).DueDate.ToDateTime(TimeOnly.MinValue);

        var ok = UiTheme.PrimaryButton(_localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 474, 260, UiTheme.ButtonHeight);
        ok.Click += (_, _) => Accept();

        var cancel = UiTheme.SecondaryButton(_localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(300, 474, 240, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_category, _severity, _source, _sampleTerm, _findSample, _sample, _description, _immediateAction,
                           _affectedResults, _patientCount, _dueDate, ok, cancel]);

        AcceptButton = ok;
        CancelButton = cancel;

        QhDialog.Dress(this, _localizer, "QualityHr.Hint.NonConformity");
        Shown += (_, _) => _findSample.Enabled = FindSamples is not null;
    }

    /// <summary>Opens on what the calling screen already knows; every field stays editable.</summary>
    internal void Prefill(NonConformityCategory category, NonConformitySeverity severity,
                          DetectionSource source, string description, Guid? instrumentId)
    {
        static void Select<T>(ComboBox box, T value)
        {
            foreach (var item in box.Items)
            {
                if (item is not Choice<T> choice || !EqualityComparer<T>.Default.Equals(choice.Value, value)) continue;
                box.SelectedItem = item;
                return;
            }
        }

        Select(_category, category);
        Select(_severity, severity);
        Select(_source, source);
        _description.Text = description;
        _instrumentId = instrumentId;
    }

    /// <summary>The tubes for what was typed, picked as the rules pick them (NonConformityScreenRules.SamplesFound).</summary>
    private async Task FindSamplesAsync()
    {
        if (FindSamples is null || string.IsNullOrWhiteSpace(_sampleTerm.Text)) return;

        _findSample.Enabled = false;
        try
        {
            _sampleForm.SampleTerm = _sampleTerm.Text;
            var found = await FindSamples(_sampleTerm.Text.Trim());
            if (found.IsFailure) { _dialogs.ShowFailure(found); return; }

            NonConformityScreenRules.SamplesFound(_sampleForm, found.Value);
            var picked = _sampleForm.SampleId;

            _sample.Items.Clear();
            _sample.Items.Add(new Choice<Guid?>(null, _localizer["NonConformity.NoSample"]));
            foreach (var choice in _sampleForm.SampleChoices)
                _sample.Items.Add(new Choice<Guid?>(choice.SampleId,
                    NonConformityScreenRules.SampleText(choice, _localizer.CurrentLanguage, _localizer.Format)));
            _sample.SelectedIndex = Math.Max(0, _sampleForm.SampleChoices.ToList().FindIndex(c => c.SampleId == picked) + 1);
            _sample.Enabled = _sampleForm.SampleChoices.Count > 0;

            if (NonConformityScreenRules.SampleSearchOutcome(_sampleForm, _localizer.Format) is { } notFound)
                _dialogs.Warn(notFound);
        }
        finally
        {
            _findSample.Enabled = FindSamples is not null;
        }
    }

    private void Accept()
    {
        var form = new NonConformityForm
        {
            Category = ((Choice<NonConformityCategory>)_category.SelectedItem!).Value,
            Severity = ((Choice<NonConformitySeverity>)_severity.SelectedItem!).Value,
            Source = ((Choice<DetectionSource>)_source.SelectedItem!).Value,
            Description = _description.Text,
            ImmediateAction = _immediateAction.Text,
            AffectedResults = _affectedResults.Checked,
            PatientCount = (int)_patientCount.Value,
            DueDate = DateOnly.FromDateTime(_dueDate.Value.Date),
            InstrumentId = _instrumentId,
            SampleId = _sampleForm.SampleId,
            LabOrderId = _sampleForm.LabOrderId
        };

        if (NonConformityScreenRules.Check(form) is { } refusal)
        {
            _dialogs.Warn(_localizer[refusal.Key]);
            _description.Focus();
            return;
        }

        Request = NonConformityScreenRules.ToRequest(form);

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

    internal CorrectiveActionForm? Result { get; private set; }

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

        // A corrective action, the one ISO 15189 actually asks for.
        var offered = NonConformityScreenRules.NewAction(DateOnly.FromDateTime(DateTime.Today));
        _kind.SelectedIndex = Array.IndexOf(Enum.GetValues<ActionKind>(), offered.Kind);

        var descriptionLabel = UiTheme.FieldLabel(localizer["Field.Description"]);
        descriptionLabel.Location = new Point(20, 70);

        _description.Bounds = new Rectangle(20, 88, 460, 90);
        _description.Multiline = true;
        _description.MaxLength = NonConformityScreenRules.TextMaxLength;   // the column; beyond, the save failed with « Erreur inattendue »

        var targetLabel = UiTheme.FieldLabel(localizer["NonConformity.DueDate"]);
        targetLabel.Location = new Point(20, 190);

        _target.Bounds = new Rectangle(20, 208, 200, UiTheme.InputHeight);
        _target.Format = DateTimePickerFormat.Short;
        _target.Value = offered.TargetDate.ToDateTime(TimeOnly.MinValue);

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

        QhDialog.Dress(this, localizer, "QualityHr.Hint.CorrectiveAction");
    }

    private void Accept()
    {
        var form = new CorrectiveActionForm
        {
            Kind = ((NonConformityDialog.Choice<ActionKind>)_kind.SelectedItem!).Value,
            Description = _description.Text,
            TargetDate = DateOnly.FromDateTime(_target.Value.Date)
        };

        if (NonConformityScreenRules.Check(form) is { } refusal)
        {
            _dialogs.Warn(_localizer[refusal.Key]);
            _description.Focus();
            return;
        }

        Result = form;

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

    /// <summary>The analysis as typed, read by NonConformityScreenRules.</summary>
    internal NonConformityAnalysisInput Analysis => NonConformityScreenRules.Read(new NonConformityAnalysisForm
    {
        RootCause = _rootCause.SelectedItem is NonConformityDialog.Choice<RootCauseCategory> choice
            ? choice.Value
            : Enum.GetValues<RootCauseCategory>()[0],
        RootCauseAnalysis = _rootCauseAnalysis.Text,
        ClinicalImpact = _clinicalImpact.Text,
        Effectiveness = _effectiveness.Text,
        AmendedReportsIssued = _amendedReports.Checked
    });

    internal NonConformityAnalysisDialog(ILocalizer localizer, NonConformityRow row,
                                         NonConformityAnalysis? recorded = null)
    {
        Text = NonConformityScreenRules.AnalysisTitle(row, localizer.Format);
        var form = NonConformityScreenRules.FormOf(recorded);
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

        Field("NonConformity.RootCauseCategory", y);
        _rootCause.DropDownStyle = ComboBoxStyle.DropDownList;
        _rootCause.FlatStyle = FlatStyle.Flat;
        _rootCause.Bounds = new Rectangle(20, y + 18, 520, UiTheme.InputHeight);

        foreach (var value in Enum.GetValues<RootCauseCategory>())
            _rootCause.Items.Add(new NonConformityDialog.Choice<RootCauseCategory>(
                value, localizer[$"RootCause.{value}"]));

        _rootCause.SelectedIndex = Math.Max(0, Array.IndexOf(Enum.GetValues<RootCauseCategory>(), form.RootCause));

        y += 54;
        Field("NonConformity.RootCause", y);
        _rootCauseAnalysis.Bounds = new Rectangle(20, y + 18, 520, 70);
        _rootCauseAnalysis.Multiline = true;
        _rootCauseAnalysis.MaxLength = NonConformityScreenRules.TextMaxLength;   // the column; beyond, the save failed with « Erreur inattendue »

        y += 98;
        // Emphasised when released results were affected, because the domain will then require it.
        Field("NonConformity.ClinicalImpact", y,
              NonConformityScreenRules.ClinicalImpactRequired(row) ? UiTheme.Critical : null);
        _clinicalImpact.Bounds = new Rectangle(20, y + 18, 520, 70);
        _clinicalImpact.Multiline = true;
        _clinicalImpact.MaxLength = NonConformityScreenRules.TextMaxLength;

        y += 98;
        Field("NonConformity.Effectiveness", y);
        _effectiveness.Bounds = new Rectangle(20, y + 18, 520, 70);
        _effectiveness.Multiline = true;
        _effectiveness.MaxLength = NonConformityScreenRules.TextMaxLength;

        y += 98;
        _amendedReports.Text = localizer["NonConformity.AmendedReportsIssued"];
        _amendedReports.AutoSize = true;
        _amendedReports.Location = new Point(20, y);
        _amendedReports.Visible = NonConformityScreenRules.AmendedReportsAsked(row);

        var ok = UiTheme.PrimaryButton(localizer["Action.Save"]);
        ok.Bounds = new Rectangle(20, 470, 260, UiTheme.ButtonHeight);
        ok.DialogResult = DialogResult.OK;

        var cancel = UiTheme.SecondaryButton(localizer["Action.Cancel"]);
        cancel.Bounds = new Rectangle(300, 470, 240, UiTheme.ButtonHeight);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([_rootCause, _rootCauseAnalysis, _clinicalImpact,
                           _effectiveness, _amendedReports, ok, cancel]);

        // What was already recorded, so completing the file does not mean typing it all again.
        _rootCauseAnalysis.Text = form.RootCauseAnalysis;
        _clinicalImpact.Text = form.ClinicalImpact;
        _effectiveness.Text = form.Effectiveness;
        _amendedReports.Checked = form.AmendedReportsIssued;

        AcceptButton = ok;
        CancelButton = cancel;

        QhDialog.Dress(this, localizer, "QualityHr.Hint.NcAnalysis");
    }
}
