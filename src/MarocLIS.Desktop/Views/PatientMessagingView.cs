using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Relations;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Relations;
using MarocLIS.Localization;
using Microsoft.Extensions.Logging;
using static MarocLIS.Desktop.Views.ReportGrids;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// Patient follow-up by SMS: the bilan reminders due today with the text each patient will receive,
/// the reminder rules, the satisfaction survey (sending, answers keyed in or read from replies,
/// results) and the log of everything sent. Only patients with the follow-up consent appear.
/// </summary>
public sealed class PatientMessagingView : LocalizedView
{
    private readonly PatientMessagingService _messaging;
    private readonly IDocumentOutput _output;
    private readonly IUserDialogs _dialogs;
    private readonly ILogger<PatientMessagingView>? _logger;

    private readonly PeriodBar _bar;
    private readonly Label _notice = new()
    {
        Dock = DockStyle.Top, Height = 44, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly DataGridView _reminders = UiTheme.Grid();
    private readonly DataGridView _rules = UiTheme.Grid();
    private readonly DataGridView _surveys = UiTheme.Grid();
    private readonly DataGridView _comments = UiTheme.Grid();
    private readonly DataGridView _log = UiTheme.Grid();
    private readonly KpiTile[] _resultTiles = Enumerable.Range(0, 4).Select(_ => new KpiTile()).ToArray();
    private readonly TabPage _reminderPage;
    private readonly TabPage _rulePage;
    private readonly TabPage _surveyPage;
    private readonly TabPage _resultPage = new() { BackColor = UiTheme.Surface, Padding = new Padding(0, 10, 0, 0) };
    private readonly TabPage _logPage;

    // The main action of the tab in view is the page's primary button; the survey's occasional
    // actions are under « Plus d'actions ».
    private readonly Button _sendReminders = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _newRule = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _editRule = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _sendSurveys = UiTheme.PrimaryButton(string.Empty);
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly Button _more;
    private readonly ToolStripMenuItem _recordAnswer = Pilotage.MenuItem();
    private readonly ToolStripMenuItem _importReplies = Pilotage.MenuItem();
    private readonly ToolStripMenuItem _settings = Pilotage.MenuItem();

    private IReadOnlyList<ReminderTarget> _targets = [];

    public PatientMessagingView(PatientMessagingService messaging, IDocumentOutput output, IUserDialogs dialogs,
                                ILocalizer localizer, ICultureManager cultureManager,
                                ILogger<PatientMessagingView>? logger = null)
        : base(localizer, cultureManager)
    {
        _messaging = messaging;
        _output = output;
        _dialogs = dialogs;
        _logger = logger;
        _bar = new PeriodBar(localizer, primaryCompute: false);
        _more = Pilotage.MoreButton(_moreMenu);
        Padding = new Padding(24, 16, 24, 16);

        foreach (var grid in new[] { _reminders, _surveys })
        {
            grid.ReadOnly = false;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "send", FillWeight = 4, ReadOnly = false });
        }

        AddColumn(_reminders, "patient", 16);
        AddColumn(_reminders, "phone", 11);
        AddColumn(_reminders, "analysis", 13);
        AddColumn(_reminders, "resultDate", 9);
        AddColumn(_reminders, "dueOn", 9);
        AddColumn(_reminders, "text", 40);
        AddColumn(_reminders, "segments", 5, numeric: true);

        AddColumn(_rules, "code", 8);
        AddColumn(_rules, "analysis", 20);
        AddColumn(_rules, "delay", 8, numeric: true);
        AddColumn(_rules, "window", 8, numeric: true);
        AddColumn(_rules, "template", 50);
        AddColumn(_rules, "active", 6);

        AddColumn(_surveys, "patient", 18);
        AddColumn(_surveys, "phone", 12);
        AddColumn(_surveys, "order", 12);
        AddColumn(_surveys, "reportedAt", 11);
        AddColumn(_surveys, "text", 40);
        AddColumn(_surveys, "segments", 5, numeric: true);

        AddColumn(_comments, "date", 10);
        AddColumn(_comments, "score", 6, numeric: true);
        AddColumn(_comments, "comment", 70);

        AddColumn(_log, "sentAt", 12);
        AddColumn(_log, "kind", 12);
        AddColumn(_log, "patient", 16);
        AddColumn(_log, "phone", 11);
        AddColumn(_log, "status", 9);
        AddColumn(_log, "text", 40);

        foreach (var (grid, column) in new[] { (_reminders, "phone"), (_reminders, "resultDate"), (_reminders, "dueOn"),
                                               (_rules, "code"), (_surveys, "phone"), (_surveys, "order"),
                                               (_surveys, "reportedAt"), (_comments, "date"), (_log, "sentAt"),
                                               (_log, "phone") })
            grid.Column(column).DefaultCellStyle.Font = UiTheme.Monospace;

        // A message refused by the gateway reads as such without the colour of its row.
        Pilotage.Pills(_log, "status", row => row.Tag is PatientMessageStatus status
            ? PatientMessagingScreenRules.Tone(status) == StockTone.Critical
                ? (UiTheme.CriticalBackground, UiTheme.CriticalInk, "!")
                : (UiTheme.NormalBackground, UiTheme.Normal, StockTones.Symbol(StockTone.Done))
            : null);

        _reminderPage = Pilotage.CardPage(_reminders);
        _rulePage = Pilotage.CardPage(_rules);
        _surveyPage = Pilotage.CardPage(_surveys);
        _logPage = Pilotage.CardPage(_log);
        _resultPage.Controls.Add(new ListCard(_comments));
        _resultPage.Controls.Add(Tiles(_resultTiles, 4, 92));
        _tabs.TabPages.AddRange([_reminderPage, _rulePage, _surveyPage, _resultPage, _logPage]);

        _moreMenu.Items.AddRange([_recordAnswer, _importReplies, _settings]);
        _bar.Extra.Controls.AddRange([_editRule, _more]);
        _bar.Lead.Controls.AddRange([_sendReminders, _newRule, _sendSurveys]);

        Controls.Add(_tabs);
        Controls.Add(_notice);
        Controls.Add(_bar);

        // The lists read through the same database context as a send batch: nothing else queries it
        // while one runs (EF refuses two operations at once, which would stop the batch half-way).
        _bar.Compute.Click += async (_, _) => await LoadAsync();
        _bar.Export.Click += async (_, _) => await ExportCurrentAsync();
        _tabs.SelectedIndexChanged += async (_, _) => { UpdateButtons(); await LoadAsync(); };
        // Every write runs as a busy operation: a second click is ignored while a batch is sending,
        // and the shell waits for the batch before closing the screen.
        _sendReminders.Click += async (_, _) => await WriteAsync(SendRemindersAsync);
        _newRule.Click += async (_, _) => await WriteAsync(() => EditRuleAsync(null));
        _editRule.Click += async (_, _) => { if (_rules.CurrentRow?.Tag is ReminderRuleRow r) await WriteAsync(() => EditRuleAsync(r)); };
        _rules.CellDoubleClick += async (_, _) => { if (_rules.CurrentRow?.Tag is ReminderRuleRow r) await WriteAsync(() => EditRuleAsync(r)); };
        _sendSurveys.Click += async (_, _) => await WriteAsync(SendSurveysAsync);
        _recordAnswer.Click += async (_, _) => await WriteAsync(RecordAnswerAsync);
        _importReplies.Click += async (_, _) => await WriteAsync(ImportRepliesAsync);
        _settings.Click += async (_, _) => await WriteAsync(EditSettingsAsync);
    }

    private Task<bool> WriteAsync(Func<Task> operation) =>
        RunBusyAsync(async () =>
        {
            // The period bar (compute, export, dates, actions) and the tabs too: each would query the
            // context the operation is using.
            var controls = new Control[] { _bar, _tabs };
            foreach (var c in controls) c.Enabled = false;
            try { await operation(); }
            finally { foreach (var c in controls) c.Enabled = true; }
        });

    public override string Title => T("Nav.PatientMessaging");

    private void UpdateButtons()
    {
        var tab = _tabs.SelectedTab;
        _sendReminders.Visible = tab == _reminderPage;
        _newRule.Visible = _editRule.Visible = tab == _rulePage;
        _sendSurveys.Visible = tab == _surveyPage;
        _recordAnswer.Available = _importReplies.Available = _settings.Available = tab == _surveyPage || tab == _resultPage;
        Pilotage.SyncMore(_more, _moreMenu);
    }

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        // The menu's short name as the title: the full one, beside it, did not leave room for the
        // actions on a 1366-pixel screen.
        _bar.Heading.Text = T("Nav.PatientMessaging");
        _bar.Subtitle.Text = T("Messaging.Title");
        _more.Text = T("ResultEntry.MoreActions");
        _reminderPage.Text = T(PatientMessagingScreenRules.TabKey(MessagingTab.Reminders));
        _rulePage.Text = T(PatientMessagingScreenRules.TabKey(MessagingTab.Rules));
        _surveyPage.Text = T(PatientMessagingScreenRules.TabKey(MessagingTab.Surveys));
        _resultPage.Text = T(PatientMessagingScreenRules.TabKey(MessagingTab.SurveyResults));
        _logPage.Text = T(PatientMessagingScreenRules.TabKey(MessagingTab.Log));
        _sendReminders.Text = T("Messaging.SendSelected");
        _sendSurveys.Text = T("Messaging.SendSelected");
        _newRule.Text = T("Messaging.NewRule");
        _editRule.Text = T("Action.Edit");
        _recordAnswer.Text = T("Messaging.RecordAnswer");
        _importReplies.Text = T("Messaging.ImportReplies");
        _settings.Text = T("Messaging.SurveySettings");

        _notice.Text = PatientMessagingScreenRules.Notice(_messaging.CanSendSms, T);

        foreach (var grid in new[] { _reminders, _rules, _surveys, _comments, _log })
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderText = T($"Messaging.Column.{column.Name}");

        UpdateButtons();

        // The rules (« Oui »/« Non ») and the log (kind, status) carry translated words that stayed
        // in the previous language until the next refresh. The previews are left as they are: read
        // again they would lose the patients the operator unticked.
        if (_activated && (_tabs.SelectedTab == _rulePage || _tabs.SelectedTab == _logPage)) _ = LoadAsync();
    }

    private bool _activated;

    public override Task ActivateAsync(CancellationToken ct = default)
    {
        _activated = true;
        return LoadAsync(ct);
    }

    // The reminder, survey and log lists name patients, their mobile and the analyses in the texts:
    // their export is in the data-access log, like any patient list that leaves as a file.
    private async Task ExportCurrentAsync()
    {
        if (IsBusy) return;
        var tab = _tabs.SelectedTab;
        var grid = Pilotage.GridOf(tab);
        var context = PatientMessagingScreenRules.ExportContext(TabOf(tab));

        if (context is not null && grid is { Rows.Count: > 0 })
        {
            var logged = await _messaging.LogExportAsync(context, grid.Rows.Count);
            if (logged.IsFailure) { _dialogs.ShowFailure(logged, _bar.Export.Text); return; }
        }

        await ExportAsync(grid, _output, _dialogs, _bar.Export.Text, Localizer);
    }

    private MessagingTab TabOf(TabPage? tab) =>
        tab == _rulePage ? MessagingTab.Rules
        : tab == _surveyPage ? MessagingTab.Surveys
        : tab == _resultPage ? MessagingTab.SurveyResults
        : tab == _logPage ? MessagingTab.Log
        : MessagingTab.Reminders;

    // Nothing reads while a write runs (see WriteAsync); the write refreshes the list itself at its end.
    private Task LoadAsync(CancellationToken ct = default) => IsBusy ? Task.CompletedTask : ReloadAsync(ct);

    private async Task ReloadAsync(CancellationToken ct = default)
    {
        try
        {
            UseWaitCursor = true;
            var tab = _tabs.SelectedTab;

            if (tab == _reminderPage)
            {
                var result = await _messaging.PreviewRemindersAsync(ct);
                if (result.IsFailure) { _dialogs.ShowFailure(result, Title); return; }
                _reminders.Rows.Clear();
                foreach (var c in result.Value)
                {
                    var i = _reminders.Rows.Add([true, .. PatientMessagingScreenRules.Cells(c, CultureInfo.CurrentCulture)]);
                    _reminders.Rows[i].Tag = c;
                }
                LockReadOnlyColumns(_reminders);
            }
            else if (tab == _rulePage)
            {
                var rules = await _messaging.GetRulesAsync(ct);
                if (rules.IsFailure) { _dialogs.ShowFailure(rules, Title); return; }
                // Kept on the rule just edited.
                _rules.RebuildKeeping<ReminderRuleRow, Guid>(r => r.Id, () =>
                {
                    _rules.Rows.Clear();
                    var language = Localizer.CurrentLanguage;
                    foreach (var r in rules.Value)
                    {
                        var i = _rules.Rows.Add([.. PatientMessagingScreenRules.Cells(r, language, T, CultureInfo.CurrentCulture)]);
                        _rules.Rows[i].Tag = r;
                    }
                });
            }
            else if (tab == _surveyPage)
            {
                var result = await _messaging.PreviewSurveysAsync(_bar.From, _bar.To, ct);
                if (result.IsFailure) { _dialogs.ShowFailure(result, Title); return; }
                _surveys.Rows.Clear();
                foreach (var c in result.Value)
                {
                    var i = _surveys.Rows.Add([true, .. PatientMessagingScreenRules.Cells(c, Offset(c.ReportedAt), CultureInfo.CurrentCulture)]);
                    _surveys.Rows[i].Tag = c;
                }
                LockReadOnlyColumns(_surveys);
            }
            else if (tab == _resultPage)
            {
                var result = await _messaging.GetSurveyResultsAsync(_bar.From, _bar.To, ct);
                if (result.IsFailure) { _dialogs.ShowFailure(result, Title); return; }
                var r = result.Value;
                var tiles = PatientMessagingScreenRules.Tiles(r, T, CultureInfo.CurrentCulture);
                for (var t = 0; t < _resultTiles.Length && t < tiles.Count; t++) ReportGrids.Show(_resultTiles[t], tiles[t]);
                _comments.Rows.Clear();
                foreach (var c in r.Comments) _comments.Rows.Add([.. PatientMessagingScreenRules.Cells(c, CultureInfo.CurrentCulture)]);
            }
            else if (tab == _logPage)
            {
                var result = await _messaging.GetLogAsync(_bar.From, _bar.To, ct);
                if (result.IsFailure) { _dialogs.ShowFailure(result, Title); return; }
                _log.Rows.Clear();
                foreach (var m in result.Value)
                {
                    var i = _log.Rows.Add([.. PatientMessagingScreenRules.Cells(m, Offset(m.SentAt), T)]);
                    _log.Rows[i].Tag = m.Status;
                    if (PatientMessagingScreenRules.Tone(m.Status) == StockTone.Critical)
                        _log.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Critical;
                }
            }
        }
        catch (OperationCanceledException) { }
        // Left before the load finished: the screen is gone, there is nothing to report on it.
        catch (Exception) when (IsDisposed || Disposing) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading patient messaging failed.");
            _dialogs.Warn(T(FailureMessages.KeyFor(ex)), Title);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    /// <summary>The workstation's offset at that moment: the times are read in its clock.</summary>
    private static TimeSpan Offset(DateTimeOffset at) => TimeZoneInfo.Local.GetUtcOffset(at);

    private static void LockReadOnlyColumns(DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns) column.ReadOnly = column.Name != "send";
    }

    private static IEnumerable<T> Ticked<T>(DataGridView grid) =>
        grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells["send"].Value is true && r.Tag is T)
            .Select(r => (T)r.Tag!);

    private async Task SendRemindersAsync()
    {
        _reminders.EndEdit();
        var picked = Ticked<ReminderCandidate>(_reminders).ToList();
        if (picked.Count == 0) { _dialogs.Info(T("Message.NothingSelected"), _sendReminders.Text); return; }
        if (!_dialogs.Confirm(PatientMessagingScreenRules.ConfirmSend(picked.Count, T), _sendReminders.Text)) return;

        var result = await _messaging.SendRemindersAsync(
            [.. picked.Select(c => new ReminderSelection(c.RuleId, c.PatientId, c.OrderId))]);
        Report(result, _sendReminders.Text);
        await ReloadAsync();
    }

    private async Task SendSurveysAsync()
    {
        _surveys.EndEdit();
        var picked = Ticked<SurveyCandidate>(_surveys).ToList();
        if (picked.Count == 0) { _dialogs.Info(T("Message.NothingSelected"), _sendSurveys.Text); return; }
        if (!_dialogs.Confirm(PatientMessagingScreenRules.ConfirmSend(picked.Count, T), _sendSurveys.Text)) return;

        var result = await _messaging.SendSurveysAsync(_bar.From, _bar.To, [.. picked.Select(c => c.OrderId)]);
        Report(result, _sendSurveys.Text);
        await ReloadAsync();
    }

    private void Report(Result<CampaignOutcome> result, string title)
    {
        if (result.IsFailure) { _dialogs.ShowFailure(result, title); return; }
        _dialogs.Info(PatientMessagingScreenRules.Outcome(result.Value, T).Text, title);
    }

    private async Task EditRuleAsync(ReminderRuleRow? existing)
    {
        if (_targets.Count == 0)
        {
            var loaded = await _messaging.GetTargetsAsync();
            if (loaded.IsFailure) { _dialogs.ShowFailure(loaded, Title); return; }
            _targets = loaded.Value;
        }

        var language = Localizer.CurrentLanguage;
        var culture = CultureInfo.CurrentCulture;
        var targets = PatientMessagingScreenRules.Targets(_targets, existing);
        var form = PatientMessagingScreenRules.FormFor(existing, culture);
        Func<ReminderRuleForm> read = () => form;
        SaveReminderRuleRequest? request = null;

        using var dialog = new FieldDialog(Localizer, existing is null ? T("Messaging.NewRule") : T("Messaging.EditRule"), () =>
        {
            var (refusal, checkedRequest) = PatientMessagingScreenRules.Check(read(), targets, existing?.Id);
            if (refusal is not null)
            {
                _dialogs.Warn(Say(refusal), Title);
                return false;
            }
            request = checkedRequest;
            return true;
        }, height: 600, intro: T("Messaging.RuleIntro"));
        var target = dialog.Choice(T("Messaging.Column.analysis"),
            targets.Select(t => (t.Id, PatientMessagingScreenRules.Display(t, language, T))), form.TargetId);
        var delay = dialog.Number(T("Messaging.Column.delay"), existing?.DelayMonths ?? PatientMessagingScreenRules.DefaultDelayMonths,
                                  ReminderRule.MinimumDelayMonths, ReminderRule.MaximumDelayMonths);
        var window = dialog.Number(T("Messaging.Column.window"), existing?.WindowDays ?? PatientMessagingScreenRules.DefaultWindowDays,
                                   1, PatientMessagingScreenRules.MaximumWindowDays);
        var fr = dialog.TextField(T("Messaging.TemplateFr"), form.TemplateFr, MessageWording.MaximumLength, multiline: true);
        var ar = dialog.TextField(T("Messaging.TemplateAr"), form.TemplateAr, MessageWording.MaximumLength, multiline: true);
        dialog.Note(PatientMessagingScreenRules.PlaceholdersHelp(T));
        var active = dialog.Check(T("Field.Active"), form.IsActive);
        read = () => new ReminderRuleForm(FieldDialog.Selected<Guid>(target), delay.Value.ToString(culture), window.Value.ToString(culture),
                                          fr.Text, ar.Text, active.Checked);

        if (dialog.ShowDialog(this) != DialogResult.OK || request is null) return;

        var saved = await _messaging.SaveRuleAsync(request);

        if (saved.IsFailure) { _dialogs.ShowFailure(saved, Title); return; }
        await ReloadAsync();
    }

    /// <summary>A refusal of the shared rules, in the operator's language.</summary>
    private string Say(FieldRefusal refusal) =>
        refusal.Field == PatientMessagingScreenRules.RuleDelayField ? PatientMessagingScreenRules.DelayRefusal(T)
        : refusal.Typed is { } typed ? Localizer.Format(refusal.Key, typed)
        : T(refusal.Key);

    private async Task RecordAnswerAsync()
    {
        using var dialog = new FieldDialog(Localizer, T("Messaging.RecordAnswer"), height: 330,
                                           intro: T("Messaging.RecordAnswerIntro"));
        var code = dialog.TextField(T("Messaging.SurveyCode"), null, 12);
        var score = dialog.Number(T("Messaging.Column.score"), 5, 1, 5);
        var comment = dialog.TextField(T("Messaging.Column.comment"), null, SurveyInvitation.CommentMaxLength, multiline: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var (refusal, answer) = PatientMessagingScreenRules.Check(
            new SurveyAnswerForm(code.Text, score.Value.ToString(CultureInfo.CurrentCulture), comment.Text));
        if (refusal is not null || answer is not { } a)
        {
            _dialogs.Warn(Say(refusal!), _recordAnswer.Text);
            return;
        }

        var recorded = await _messaging.RecordAnswerAsync(a.Code, a.Score, a.Comment, SurveyAnswerChannel.Manual);
        if (recorded.IsFailure) { _dialogs.ShowFailure(recorded, _recordAnswer.Text); return; }
        _dialogs.Info(T("Message.Saved"), _recordAnswer.Text);
        await ReloadAsync();
    }

    private async Task ImportRepliesAsync()
    {
        using var dialog = new FieldDialog(Localizer, T("Messaging.ImportReplies"), height: 420,
                                           intro: T("Messaging.ImportRepliesIntro"));
        dialog.Note(T("Messaging.ImportRepliesHelp"));
        var text = dialog.TextField(T("Messaging.Replies"), null, 100_000, multiline: true);
        text.Height = 220;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _messaging.ImportRepliesAsync(text.Text);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _importReplies.Text); return; }

        var (summary, lines) = PatientMessagingScreenRules.ImportOutcome(result.Value, T);
        _dialogs.Info(summary + (lines.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, lines)),
                      _importReplies.Text);
        await ReloadAsync();
    }

    private async Task EditSettingsAsync()
    {
        var current = await _messaging.GetSettingsAsync();
        if (current.IsFailure) { _dialogs.ShowFailure(current, _settings.Text); return; }
        var s = current.Value;

        using var dialog = new FieldDialog(Localizer, T("Messaging.SurveySettings"), height: 560,
                                           intro: T("Messaging.SurveySettingsIntro"));
        var fr = dialog.TextField(T("Messaging.TemplateFr"), s.SurveyTemplateFr, MessageWording.MaximumLength, multiline: true);
        var ar = dialog.TextField(T("Messaging.TemplateAr"), s.SurveyTemplateAr, MessageWording.MaximumLength, multiline: true);
        dialog.Note(PatientMessagingScreenRules.PlaceholdersHelp(T));
        var url = dialog.TextField(T("Messaging.SurveyUrl"), s.SurveyUrl, PatientMessagingScreenRules.SurveyUrlMaxLength);
        var interval = dialog.Number(T("Messaging.SurveyInterval"), s.SurveyIntervalDays, 0, PatientMessagingScreenRules.MaximumIntervalDays);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var (refusal, settings) = PatientMessagingScreenRules.Check(
            new SurveySettingsForm(fr.Text, ar.Text, url.Text, interval.Value.ToString(CultureInfo.CurrentCulture)), s);
        if (refusal is not null || settings is null)
        {
            _dialogs.Warn(Say(refusal!), _settings.Text);
            return;
        }

        var saved = await _messaging.SaveSettingsAsync(settings);
        if (saved.IsFailure) _dialogs.ShowFailure(saved, _settings.Text);
        else await ReloadAsync();
    }
}
