using System.Globalization;
using MarocLIS.Application.Abstractions;
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
        Dock = DockStyle.Top, Height = 40, Font = UiTheme.Small, ForeColor = UiTheme.TextSecondary
    };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly DataGridView _reminders = UiTheme.Grid();
    private readonly DataGridView _rules = UiTheme.Grid();
    private readonly DataGridView _surveys = UiTheme.Grid();
    private readonly DataGridView _comments = UiTheme.Grid();
    private readonly DataGridView _log = UiTheme.Grid();
    private readonly Label[] _resultTiles = Enumerable.Range(0, 4).Select(_ => new Label()).ToArray();
    private readonly TabPage _reminderPage;
    private readonly TabPage _rulePage;
    private readonly TabPage _surveyPage;
    private readonly TabPage _resultPage = new() { BackColor = UiTheme.Surface };
    private readonly TabPage _logPage;

    private readonly Button _sendReminders = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _newRule = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _editRule = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _sendSurveys = UiTheme.PrimaryButton(string.Empty);
    private readonly Button _recordAnswer = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _importReplies = UiTheme.SecondaryButton(string.Empty);
    private readonly Button _settings = UiTheme.SecondaryButton(string.Empty);

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
        _bar = new PeriodBar(localizer);
        Padding = new Padding(UiTheme.Gutter);

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

        _reminderPage = Page(_reminders);
        _rulePage = Page(_rules);
        _surveyPage = Page(_surveys);
        _logPage = Page(_log);
        _resultPage.Controls.Add(_comments);
        _resultPage.Controls.Add(Tiles(_resultTiles, 4, 80));
        _tabs.TabPages.AddRange([_reminderPage, _rulePage, _surveyPage, _resultPage, _logPage]);

        foreach (var b in new[] { _sendReminders, _newRule, _editRule, _sendSurveys, _recordAnswer, _importReplies, _settings })
            b.AutoSize = true;
        _bar.Extra.Controls.AddRange([_sendReminders, _newRule, _editRule, _sendSurveys, _recordAnswer, _importReplies, _settings]);

        Controls.Add(_tabs);
        Controls.Add(_notice);
        Controls.Add(_bar);

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
            var buttons = new[] { _sendReminders, _newRule, _editRule, _sendSurveys, _recordAnswer, _importReplies, _settings };
            foreach (var b in buttons) b.Enabled = false;
            try { await operation(); }
            finally { foreach (var b in buttons) b.Enabled = true; }
        });

    public override string Title => T("Nav.PatientMessaging");

    private void UpdateButtons()
    {
        var tab = _tabs.SelectedTab;
        _sendReminders.Visible = tab == _reminderPage;
        _newRule.Visible = _editRule.Visible = tab == _rulePage;
        _sendSurveys.Visible = tab == _surveyPage;
        _recordAnswer.Visible = _importReplies.Visible = _settings.Visible = tab == _surveyPage || tab == _resultPage;
    }

    protected override void ApplyTexts()
    {
        _bar.ApplyTexts();
        _bar.Heading.Text = T("Messaging.Title");
        _reminderPage.Text = T("Messaging.Reminders");
        _rulePage.Text = T("Messaging.Rules");
        _surveyPage.Text = T("Messaging.Surveys");
        _resultPage.Text = T("Messaging.SurveyResults");
        _logPage.Text = T("Messaging.Log");
        _sendReminders.Text = T("Messaging.SendSelected");
        _sendSurveys.Text = T("Messaging.SendSelected");
        _newRule.Text = T("Messaging.NewRule");
        _editRule.Text = T("Action.Edit");
        _recordAnswer.Text = T("Messaging.RecordAnswer");
        _importReplies.Text = T("Messaging.ImportReplies");
        _settings.Text = T("Messaging.SurveySettings");

        _notice.Text = T("Messaging.ConsentNotice") + " " +
                       (_messaging.CanSendSms ? string.Empty : T("Messaging.SmsNotConfiguredNotice") + " ") +
                       T("Messaging.WhatsAppNotice");

        foreach (var grid in new[] { _reminders, _rules, _surveys, _comments, _log })
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderText = T($"Messaging.Column.{column.Name}");

        UpdateButtons();
    }

    public override Task ActivateAsync(CancellationToken ct = default) => LoadAsync(ct);

    // The reminder, survey and log lists name patients, their mobile and the analyses in the texts:
    // their export is in the data-access log, like any patient list that leaves as a file.
    private async Task ExportCurrentAsync()
    {
        var tab = _tabs.SelectedTab;
        var grid = tab?.Controls.OfType<DataGridView>().FirstOrDefault();
        var context = tab == _reminderPage ? "Relations/ReminderPreview"
            : tab == _surveyPage ? "Relations/SurveyPreview"
            : tab == _logPage ? "Relations/MessageLog"
            : null;

        if (context is not null && grid is { Rows.Count: > 0 })
        {
            var logged = await _messaging.LogExportAsync(context, grid.Rows.Count);
            if (logged.IsFailure) { _dialogs.ShowFailure(logged, _bar.Export.Text); return; }
        }

        await ExportAsync(grid, _output, _dialogs, _bar.Export.Text, Localizer);
    }

    private async Task LoadAsync(CancellationToken ct = default)
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
                    var i = _reminders.Rows.Add(true, c.PatientName, c.Phone, c.Analysis, Date(c.ResultDate), Date(c.DueOn),
                                                c.Text, c.Segments);
                    _reminders.Rows[i].Tag = c;
                }
                LockReadOnlyColumns(_reminders);
            }
            else if (tab == _rulePage)
            {
                var rules = await _messaging.GetRulesAsync(ct);
                if (rules.IsFailure) { _dialogs.ShowFailure(rules, Title); return; }
                _rules.Rows.Clear();
                var language = Localizer.CurrentLanguage;
                foreach (var r in rules.Value)
                {
                    var i = _rules.Rows.Add(r.Code, r.Name.For(language), r.DelayMonths, r.WindowDays,
                                            r.Template.For(language), r.IsActive ? T("Action.Yes") : T("Action.No"));
                    _rules.Rows[i].Tag = r;
                }
            }
            else if (tab == _surveyPage)
            {
                var result = await _messaging.PreviewSurveysAsync(_bar.From, _bar.To, ct);
                if (result.IsFailure) { _dialogs.ShowFailure(result, Title); return; }
                _surveys.Rows.Clear();
                foreach (var c in result.Value)
                {
                    var i = _surveys.Rows.Add(true, c.PatientName, c.Phone, c.AccessionNumber,
                                              c.ReportedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                                              c.Text, c.Segments);
                    _surveys.Rows[i].Tag = c;
                }
                LockReadOnlyColumns(_surveys);
            }
            else if (tab == _resultPage)
            {
                var result = await _messaging.GetSurveyResultsAsync(_bar.From, _bar.To, ct);
                if (result.IsFailure) { _dialogs.ShowFailure(result, Title); return; }
                var r = result.Value;
                string Tile(string key, string value) => $"{T(key)}\n{value}";
                _resultTiles[0].Text = Tile("Messaging.Invited", $"{Count(r.Invited)} ({Count(r.Delivered)} {T("Messaging.Delivered")})");
                _resultTiles[1].Text = Tile("Messaging.Answered", $"{Count(r.Answered)} · {Share(r.ResponseRatePercent)}");
                _resultTiles[2].Text = Tile("Messaging.AverageScore", r.AverageScore is { } a ? $"{a:0.00} / 5" : "—");
                _resultTiles[3].Text = Tile("Messaging.Distribution",
                    string.Join("  ", r.Distribution.Select((n, i) => $"{i + 1}★ {n}")));
                _comments.Rows.Clear();
                foreach (var c in r.Comments) _comments.Rows.Add(Date(c.Date), c.Score, c.Comment);
            }
            else if (tab == _logPage)
            {
                var result = await _messaging.GetLogAsync(_bar.From, _bar.To, ct);
                if (result.IsFailure) { _dialogs.ShowFailure(result, Title); return; }
                _log.Rows.Clear();
                foreach (var m in result.Value)
                {
                    var i = _log.Rows.Add(m.SentAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                                          T($"PatientMessageKind.{m.Kind}"), m.PatientName, m.Phone,
                                          T($"PatientMessageStatus.{m.Status}"),
                                          m.Failure is null ? m.Text : $"{m.Text}  [{m.Failure}]");
                    if (m.Status == PatientMessageStatus.Failed) _log.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Critical;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading patient messaging failed.");
            _dialogs.Warn(T("Message.UnexpectedError"), Title);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

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
        if (!_dialogs.Confirm(T("Messaging.ConfirmSend", picked.Count), _sendReminders.Text)) return;

        var result = await _messaging.SendRemindersAsync(
            [.. picked.Select(c => new ReminderSelection(c.RuleId, c.PatientId, c.OrderId))]);
        Report(result, _sendReminders.Text);
        await LoadAsync();
    }

    private async Task SendSurveysAsync()
    {
        _surveys.EndEdit();
        var picked = Ticked<SurveyCandidate>(_surveys).ToList();
        if (picked.Count == 0) { _dialogs.Info(T("Message.NothingSelected"), _sendSurveys.Text); return; }
        if (!_dialogs.Confirm(T("Messaging.ConfirmSend", picked.Count), _sendSurveys.Text)) return;

        var result = await _messaging.SendSurveysAsync(_bar.From, _bar.To, [.. picked.Select(c => c.OrderId)]);
        Report(result, _sendSurveys.Text);
        await LoadAsync();
    }

    private void Report(Result<CampaignOutcome> result, string title)
    {
        if (result.IsFailure) { _dialogs.ShowFailure(result, title); return; }
        var o = result.Value;
        _dialogs.Info(T("Messaging.CampaignOutcome", o.Sent, o.Failed, o.Skipped), title);
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
        var current = existing?.AnalysisTestId ?? existing?.TestPanelId ?? Guid.Empty;

        // A rule whose analysis is no longer offered (inactive, or under a specific consent) keeps
        // it, so that the rule can still be switched off; the service refuses to keep it active.
        var targets = _targets.ToList();
        if (existing is not null && targets.All(t => t.Id != current))
            targets.Add(new ReminderTarget(current, existing.TestPanelId is not null, existing.Code, existing.Name));

        using var dialog = new FieldDialog(Localizer, existing is null ? T("Messaging.NewRule") : T("Messaging.EditRule"),
                                           height: 600);
        var target = dialog.Choice(T("Messaging.Column.analysis"),
            targets.Select(t => (t.Id, $"{t.Code} — {t.Name.For(language)}{(t.IsPanel ? " (" + T("Messaging.Panel") + ")" : string.Empty)}")),
            current);
        var delay = dialog.Number(T("Messaging.Column.delay"), existing?.DelayMonths ?? 3,
                                  ReminderRule.MinimumDelayMonths, ReminderRule.MaximumDelayMonths);
        var window = dialog.Number(T("Messaging.Column.window"), existing?.WindowDays ?? 60, 1, 365);
        var fr = dialog.TextField(T("Messaging.TemplateFr"), existing?.Template.Fr ?? MessagingSettings.DefaultReminderFr,
                                  MessageWording.MaximumLength, multiline: true);
        var ar = dialog.TextField(T("Messaging.TemplateAr"), existing?.Template.Ar ?? MessagingSettings.DefaultReminderAr,
                                  MessageWording.MaximumLength, multiline: true);
        dialog.Note(T("Messaging.PlaceholdersHelp", string.Join(" ", MessageWording.Placeholders)));
        var active = dialog.Check(T("Field.Active"), existing?.IsActive ?? true);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var id = FieldDialog.Selected<Guid>(target);
        var chosen = targets.FirstOrDefault(t => t.Id == id);
        var saved = await _messaging.SaveRuleAsync(new SaveReminderRuleRequest
        {
            Id = existing?.Id,
            AnalysisTestId = chosen is { IsPanel: false } ? chosen.Id : null,
            TestPanelId = chosen is { IsPanel: true } ? chosen.Id : null,
            DelayMonths = (int)delay.Value,
            WindowDays = (int)window.Value,
            Template = new MultilingualText(fr.Text, FieldDialog.Value(ar)),
            IsActive = active.Checked
        });

        if (saved.IsFailure) { _dialogs.ShowFailure(saved, Title); return; }
        await LoadAsync();
    }

    private async Task RecordAnswerAsync()
    {
        using var dialog = new FieldDialog(Localizer, T("Messaging.RecordAnswer"), height: 330);
        var code = dialog.TextField(T("Messaging.SurveyCode"), null, 12);
        var score = dialog.Number(T("Messaging.Column.score"), 5, 1, 5);
        var comment = dialog.TextField(T("Messaging.Column.comment"), null, SurveyInvitation.CommentMaxLength, multiline: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var recorded = await _messaging.RecordAnswerAsync(code.Text, (int)score.Value, FieldDialog.Value(comment),
                                                          SurveyAnswerChannel.Manual);
        if (recorded.IsFailure) { _dialogs.ShowFailure(recorded, _recordAnswer.Text); return; }
        _dialogs.Info(T("Message.Saved"), _recordAnswer.Text);
        await LoadAsync();
    }

    private async Task ImportRepliesAsync()
    {
        using var dialog = new FieldDialog(Localizer, T("Messaging.ImportReplies"), height: 420);
        dialog.Note(T("Messaging.ImportRepliesHelp"));
        var text = dialog.TextField(T("Messaging.Replies"), null, 100_000, multiline: true);
        text.Height = 220;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var result = await _messaging.ImportRepliesAsync(text.Text);
        if (result.IsFailure) { _dialogs.ShowFailure(result, _importReplies.Text); return; }

        var o = result.Value;
        var details = string.Join(Environment.NewLine, o.Refused.Concat(o.Unrecognised.Select(l => $"? {l}")).Take(20));
        _dialogs.Info(T("Messaging.ImportOutcome", o.Recorded, o.Refused.Count, o.Unrecognised.Count)
                      + (details.Length == 0 ? string.Empty : Environment.NewLine + details), _importReplies.Text);
        await LoadAsync();
    }

    private async Task EditSettingsAsync()
    {
        var current = await _messaging.GetSettingsAsync();
        if (current.IsFailure) { _dialogs.ShowFailure(current, _settings.Text); return; }
        var s = current.Value;

        using var dialog = new FieldDialog(Localizer, T("Messaging.SurveySettings"), height: 560);
        var fr = dialog.TextField(T("Messaging.TemplateFr"), s.SurveyTemplateFr, MessageWording.MaximumLength, multiline: true);
        var ar = dialog.TextField(T("Messaging.TemplateAr"), s.SurveyTemplateAr, MessageWording.MaximumLength, multiline: true);
        dialog.Note(T("Messaging.PlaceholdersHelp", string.Join(" ", MessageWording.Placeholders)));
        var url = dialog.TextField(T("Messaging.SurveyUrl"), s.SurveyUrl, 300);
        var interval = dialog.Number(T("Messaging.SurveyInterval"), s.SurveyIntervalDays, 0, 3650);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var saved = await _messaging.SaveSettingsAsync(s with
        {
            SurveyTemplateFr = fr.Text.Trim(),
            SurveyTemplateAr = ar.Text.Trim(),
            SurveyUrl = url.Text.Trim(),
            SurveyIntervalDays = (int)interval.Value
        });
        if (saved.IsFailure) _dialogs.ShowFailure(saved, _settings.Text);
        else await LoadAsync();
    }
}
