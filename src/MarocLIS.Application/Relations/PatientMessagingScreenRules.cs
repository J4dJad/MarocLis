using System.Globalization;
using System.Text.RegularExpressions;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Relations;

/// <summary>The five tabs of « Rappels et enquêtes ».</summary>
public enum MessagingTab
{
    Reminders,
    Rules,
    Surveys,
    SurveyResults,
    Log
}

/// <summary>
/// What « Rappels et enquêtes » offers, all of it to whom may run the patient campaigns: on the
/// reminders and the surveys, sending the patients ticked (none ticked, nothing to send); on the
/// rules, a new rule and the change of the one selected; on the surveys and their results, an answer
/// keyed in, the replies pasted from the phone and the survey's settings.
/// </summary>
public sealed record MessagingActions(
    bool Send,
    bool SendEnabled,
    bool Rules,
    bool EditRule,
    bool SurveyTools,
    bool Export);

/// <summary>A reminder rule as its editor holds it: the delay and the window as typed.</summary>
public sealed record ReminderRuleForm(Guid TargetId, string? Delay, string? Window, string? TemplateFr, string? TemplateAr, bool IsActive);

/// <summary>The survey's settings as their editor holds them: the interval as typed.</summary>
public sealed record SurveySettingsForm(string? TemplateFr, string? TemplateAr, string? Url, string? Interval);

/// <summary>An answer keyed in by the desk: the score as typed.</summary>
public sealed record SurveyAnswerForm(string? Code, string? Score, string? Comment);

/// <summary>
/// The rules of « Rappels et enquêtes » that need no window: what is offered by which right, the
/// notice of consent above the lists, the lists' columns and cells (a message the gateway refused
/// in red, with the reason in the reader's language), the survey results' key figures, the editors
/// of a rule, of the survey's settings and of an answer with each refusal beside its box, the
/// outcomes of a send and of an import as the screen says them, the access-log context of a list
/// that leaves as a file, and its CSV and file name. The workstation's PatientMessagingView and the
/// web's screen apply the same ones; what is sent and recorded is PatientMessagingService's, through
/// the laboratory's configured gateway, to the patients with the follow-up consent only.
/// </summary>
public static class PatientMessagingScreenRules
{
    public const string PeriodField = "messaging-period";
    public const string RuleTargetField = "rule-target";
    public const string RuleDelayField = "rule-delay";
    public const string RuleWindowField = "rule-window";
    public const string RuleTemplateFrField = "rule-template-fr";
    public const string RuleTemplateArField = "rule-template-ar";
    public const string SettingsTemplateFrField = "survey-template-fr";
    public const string SettingsTemplateArField = "survey-template-ar";
    public const string SettingsUrlField = "survey-url";
    public const string SettingsIntervalField = "survey-interval";
    public const string AnswerCodeField = "answer-code";
    public const string AnswerScoreField = "answer-score";
    public const string AnswerCommentField = "answer-comment";
    public const string RepliesField = "replies";

    public const int DefaultDelayMonths = 3;
    public const int DefaultWindowDays = 60;
    public const int MaximumWindowDays = 365;
    public const int MaximumIntervalDays = 3650;
    public const int SurveyUrlMaxLength = 300;
    public const int RepliesMaxLength = 100_000;

    /// <summary>The import's refused and unreadable lines shown with its outcome, at most.</summary>
    public const int ImportDetailLines = 20;

    public static MessagingActions Actions(Func<string, bool> has, MessagingTab tab, int ticked, ReminderRuleRow? rule)
    {
        ArgumentNullException.ThrowIfNull(has);
        var campaign = has(Permissions.Messaging.Campaign);
        var sends = tab is MessagingTab.Reminders or MessagingTab.Surveys;
        return new MessagingActions(
            Send: campaign && sends,
            SendEnabled: campaign && sends && ticked > 0,
            Rules: campaign && tab == MessagingTab.Rules,
            EditRule: campaign && tab == MessagingTab.Rules && rule is not null,
            SurveyTools: campaign && tab is MessagingTab.Surveys or MessagingTab.SurveyResults,
            Export: campaign);
    }

    public static string TabKey(MessagingTab tab) => $"Messaging.{tab}";

    /// <summary>Whether a tab's list depends on the period chosen (the reminders are those due today, the rules have none).</summary>
    public static bool UsesPeriod(MessagingTab tab) => tab is MessagingTab.Surveys or MessagingTab.SurveyResults or MessagingTab.Log;

    /// <summary>
    /// The consent notice above the lists, then that without a gateway the lists can be previewed but
    /// not sent, then why WhatsApp is not offered.
    /// </summary>
    public static string Notice(bool canSendSms, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Messaging.ConsentNotice") + " "
               + (canSendSms ? string.Empty : text("Messaging.SmsNotConfiguredNotice") + " ")
               + text("Messaging.WhatsAppNotice");
    }

    public static string ColumnKey(string column) => $"Messaging.Column.{column}";

    private static ReportColumn Column(string name, bool numeric = false) => new(name, ColumnKey(name), numeric);

    /// <summary>The columns of a list (the survey results' comments on their tab), in order.</summary>
    public static IReadOnlyList<ReportColumn> Columns(MessagingTab tab) => tab switch
    {
        MessagingTab.Rules =>
            [Column("code"), Column("analysis"), Column("delay", true), Column("window", true), Column("template"), Column("active")],
        MessagingTab.Surveys =>
            [Column("patient"), Column("phone"), Column("order"), Column("reportedAt"), Column("text"), Column("segments", true)],
        MessagingTab.SurveyResults => [Column("date"), Column("score", true), Column("comment")],
        MessagingTab.Log => [Column("sentAt"), Column("kind"), Column("patient"), Column("phone"), Column("status"), Column("text")],
        _ => [Column("patient"), Column("phone"), Column("analysis"), Column("resultDate"), Column("dueOn"), Column("text"), Column("segments", true)]
    };

    /// <summary>The columns read as identifiers, in the fixed-pitch face and left to right: phones, codes, numbers and dates.</summary>
    public static bool IsIdentifier(string column) =>
        column is "phone" or "resultDate" or "dueOn" or "code" or "order" or "reportedAt" or "date" or "sentAt";

    /// <summary>« 18/10/2026 09:30 », in the laboratory's time.</summary>
    public static string At(DateTimeOffset at, TimeSpan offset) =>
        at.ToOffset(offset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => PeriodReport.Date(date);

    public static IReadOnlyList<string> Cells(ReminderCandidate c, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(c);
        return [c.PatientName, c.Phone, c.Analysis, Date(c.ResultDate), Date(c.DueOn), c.Text, c.Segments.ToString(culture)];
    }

    public static IReadOnlyList<string> Cells(SurveyCandidate c, TimeSpan offset, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(c);
        return [c.PatientName, c.Phone, c.AccessionNumber, At(c.ReportedAt, offset), c.Text, c.Segments.ToString(culture)];
    }

    /// <summary>A rule's cells: its analysis and wording in the reader's language, active « Oui » or « Non ».</summary>
    public static IReadOnlyList<string> Cells(ReminderRuleRow r, AppLanguage language, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            r.Code, r.Name.For(language), r.DelayMonths.ToString(culture), r.WindowDays.ToString(culture), r.Template.For(language),
            r.IsActive ? text("Action.Yes") : text("Action.No")
        ];
    }

    public static IReadOnlyList<string> Cells(SurveyComment c, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(c);
        return [Date(c.Date), c.Score.ToString(culture), c.Comment];
    }

    /// <summary>A message sent: its kind and status in the reader's language, a refusal's reason after its text.</summary>
    public static IReadOnlyList<string> Cells(PatientMessageRow m, TimeSpan offset, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            At(m.SentAt, offset), text($"PatientMessageKind.{m.Kind}"), m.PatientName, m.Phone, text($"PatientMessageStatus.{m.Status}"),
            m.Failure is null ? m.Text : $"{m.Text}  [{Failure(m.Failure, text)}]"
        ];
    }

    /// <summary>A refused message: in red, with a ! pill; a message sent, a ✓ pill.</summary>
    public static StockTone Tone(PatientMessageStatus status) =>
        status == PatientMessageStatus.Failed ? StockTone.Critical : StockTone.Done;

    private static readonly Regex KeyThenArguments = new(@"^(?<key>[A-Z][A-Za-z]*_[A-Za-z]+)(?:\s+(?<rest>.*))?$",
                                                         RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// The gateway's refusal as the log keeps it (its message's key, then what the gateway said),
    /// in the reader's language: « le fournisseur SMS a refusé l'envoi (HTTP 500 …) ».
    /// </summary>
    public static string Failure(string failure, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(text);
        var match = KeyThenArguments.Match(failure.Trim());
        if (!match.Success) return failure;
        var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value : null;
        return rest is null ? text(match.Groups["key"].Value) : text(match.Groups["key"].Value, rest);
    }

    /// <summary>
    /// The survey results' key figures: invited (and how many reached), answered and the rate, the
    /// average score out of five, the scores' distribution.
    /// </summary>
    public static IReadOnlyList<ReportTile> Tiles(SurveyResults r, ScreenText text, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(culture);
        return
        [
            new("invited", text("Messaging.Invited"), PeriodReport.Count(r.Invited, culture),
                $"{PeriodReport.Count(r.Delivered, culture)} {text("Messaging.Delivered")}"),
            new("answered", text("Messaging.Answered"), PeriodReport.Count(r.Answered, culture), PeriodReport.Share(r.ResponseRatePercent, culture)),
            new("average", text("Messaging.AverageScore"), r.AverageScore is { } a ? string.Format(culture, "{0:0.00} / 5", a) : "—"),
            new("distribution", text("Messaging.Distribution"),
                string.Join("  ", r.Distribution.Select((n, i) => string.Format(culture, "{0}★ {1}", i + 1, n))))
        ];
    }

    /// <summary>The access-log context of a list leaving as a file: those naming patients, their mobile and their analyses.</summary>
    public static string? ExportContext(MessagingTab tab) => tab switch
    {
        MessagingTab.Reminders => "Relations/ReminderPreview",
        MessagingTab.Surveys => "Relations/SurveyPreview",
        MessagingTab.Log => "Relations/MessageLog",
        _ => null
    };

    /// <summary>A list as CSV, its captions first, or nothing when it is empty (« Aucune donnée »).</summary>
    public static byte[]? Csv(MessagingTab tab, IEnumerable<IReadOnlyList<string>> rows, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        return PeriodReport.Csv([.. Columns(tab).Select(c => text(c.CaptionKey))], rows);
    }

    /// <summary>« rappels-20261026.csv », « enquetes-20261001-20261026.csv », « journal-sms-20261001-20261026.csv ».</summary>
    public static string FileName(MessagingTab tab, DateOnly from, DateOnly to, DateOnly today)
    {
        var day = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var period = PeriodReport.FileStamp(from, to);
        return tab switch
        {
            MessagingTab.Rules => $"regles-de-rappel-{day}.csv",
            MessagingTab.Surveys => $"enquetes-{period}.csv",
            MessagingTab.SurveyResults => $"avis-patients-{period}.csv",
            MessagingTab.Log => $"journal-sms-{period}.csv",
            _ => $"rappels-{day}.csv"
        };
    }

    // ---- Sending -------------------------------------------------------------------------------------

    /// <summary>« Envoyer 3 SMS maintenant ? »</summary>
    public static string ConfirmSend(int count, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Messaging.ConfirmSend", count);
    }

    /// <summary>« 2 envoyé(s), 1 en échec, 0 ignoré(s). », in orange when one failed or was skipped.</summary>
    public static (string Text, StockInk Ink) Outcome(CampaignOutcome outcome, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(text);
        return (text("Messaging.CampaignOutcome", outcome.Sent, outcome.Failed, outcome.Skipped),
                outcome.Failed > 0 || outcome.Skipped > 0 ? StockInk.Abnormal : StockInk.Plain);
    }

    /// <summary>
    /// What an import recorded, then up to <see cref="ImportDetailLines"/> of the replies refused
    /// (each with its reason in the reader's language) and of the lines not read as replies (« ? »).
    /// </summary>
    public static (string Summary, IReadOnlyList<string> Details) ImportOutcome(ReplyImportOutcome outcome, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(text);
        var refused = outcome.Refused.Select(r =>
        {
            var colon = r.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 ? text(r[(colon + 2)..].Trim(), r[..colon]) : r;
        });
        return (text("Messaging.ImportOutcome", outcome.Recorded, outcome.Refused.Count, outcome.Unrecognised.Count),
                [.. refused.Concat(outcome.Unrecognised.Select(l => $"? {l}")).Take(ImportDetailLines)]);
    }

    // ---- Rules ---------------------------------------------------------------------------------------

    /// <summary>
    /// The analyses and panels a rule may name. A rule whose analysis is no longer offered (switched
    /// off, or put under a specific consent) keeps it, so that the rule can still be switched off;
    /// the service refuses to keep it active.
    /// </summary>
    public static IReadOnlyList<ReminderTarget> Targets(IReadOnlyList<ReminderTarget> offered, ReminderRuleRow? rule)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var targets = offered.ToList();
        if (rule is not null && TargetOf(rule) is var current && targets.All(t => t.Id != current))
            targets.Add(new ReminderTarget(current, rule.TestPanelId is not null, rule.Code, rule.Name));
        return targets;
    }

    private static Guid TargetOf(ReminderRuleRow rule) => rule.AnalysisTestId ?? rule.TestPanelId ?? Guid.Empty;

    /// <summary>« GLY — Glycémie à jeun », a panel followed by « (Bilan (panel)) ».</summary>
    public static string Display(ReminderTarget target, AppLanguage language, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(text);
        return $"{target.Code} — {target.Name.For(language)}{(target.IsPanel ? " (" + text("Messaging.Panel") + ")" : string.Empty)}";
    }

    /// <summary>« Champs disponibles : {patient} {analyse} … », under the wording.</summary>
    public static string PlaceholdersHelp(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Messaging.PlaceholdersHelp", string.Join(" ", MessageWording.Placeholders));
    }

    /// <summary>A new rule follows three months on, sent within sixty days, in the default wording; an existing one as saved.</summary>
    public static ReminderRuleForm FormFor(ReminderRuleRow? rule, IFormatProvider culture) =>
        rule is null
            ? new ReminderRuleForm(Guid.Empty, DefaultDelayMonths.ToString(culture), DefaultWindowDays.ToString(culture),
                                   MessagingSettings.DefaultReminderFr, MessagingSettings.DefaultReminderAr, true)
            : new ReminderRuleForm(TargetOf(rule), rule.DelayMonths.ToString(culture), rule.WindowDays.ToString(culture),
                                   rule.Template.Fr, rule.Template.Ar, rule.IsActive);

    /// <summary>
    /// The rule typed, checked: an analysis or a panel chosen, a delay of whole months within the
    /// limits, a window of whole days up to a year, each wording informative, within its length and
    /// with known fields only (the French one required). A refusal names its box.
    /// </summary>
    public static (FieldRefusal? Refusal, SaveReminderRuleRequest? Request) Check(ReminderRuleForm form, IReadOnlyList<ReminderTarget> targets,
                                                                               Guid? ruleId)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.FirstOrDefault(t => t.Id == form.TargetId && t.Id != Guid.Empty) is not { } target)
            return (new FieldRefusal(RuleTargetField, "Reminder_AnalysisOrPanel"), null);
        if (WholeNumber(form.Delay) is not { } delay || delay is < ReminderRule.MinimumDelayMonths or > ReminderRule.MaximumDelayMonths)
            return (new FieldRefusal(RuleDelayField, "Reminder_DelayOutOfRange"), null);
        if (WholeNumber(form.Window) is not { } window || window is < 1 or > MaximumWindowDays)
            return (new FieldRefusal(RuleWindowField, "Reminder_WindowOutOfRange"), null);
        if (Wording(form.TemplateFr, RuleTemplateFrField) is { } fr) return (fr, null);
        if (!string.IsNullOrWhiteSpace(form.TemplateAr) && Wording(form.TemplateAr, RuleTemplateArField) is { } ar) return (ar, null);

        return (null, new SaveReminderRuleRequest
        {
            Id = ruleId,
            AnalysisTestId = target.IsPanel ? null : target.Id,
            TestPanelId = target.IsPanel ? target.Id : null,
            DelayMonths = delay,
            WindowDays = window,
            Template = new MultilingualText(form.TemplateFr!.Trim(), Blank(form.TemplateAr)),
            IsActive = form.IsActive
        });
    }

    /// <summary>The delay's refusal names both limits: « entre 1 et 60 mois ».</summary>
    public static string DelayRefusal(ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Reminder_DelayOutOfRange", ReminderRule.MinimumDelayMonths, ReminderRule.MaximumDelayMonths);
    }

    /// <summary>A service's refusal of a rule beside the box it names (the French wording for a wording's).</summary>
    public static string? RuleFieldOf(string resourceKey) => resourceKey switch
    {
        "Reminder_AnalysisOrPanel" or "Reminder_AlreadyExists" or "Reminder_SpecificConsentAnalysis" => RuleTargetField,
        "Reminder_DelayOutOfRange" => RuleDelayField,
        "Reminder_WindowOutOfRange" => RuleWindowField,
        "Messaging_TemplateRequired" or "Messaging_TemplateTooLong" or "Messaging_AdvertisingWording"
            or "Messaging_UnknownPlaceholder" => RuleTemplateFrField,
        _ => null
    };

    // ---- Survey settings -----------------------------------------------------------------------------

    public static SurveySettingsForm FormFor(MessagingSettings settings, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SurveySettingsForm(settings.SurveyTemplateFr, settings.SurveyTemplateAr, settings.SurveyUrl,
                                      settings.SurveyIntervalDays.ToString(culture));
    }

    /// <summary>
    /// The survey's settings typed, checked: each wording informative, within its length, with known
    /// fields and carrying the code (or the link, with an address to it); an https address or none; an
    /// interval of whole days up to ten years. A refusal names its box.
    /// </summary>
    public static (FieldRefusal? Refusal, MessagingSettings? Settings) Check(SurveySettingsForm form, MessagingSettings current)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(current);
        if (Wording(form.TemplateFr, SettingsTemplateFrField) is { } fr) return (fr, null);
        if (Wording(form.TemplateAr, SettingsTemplateArField) is { } ar) return (ar, null);

        var url = form.Url?.Trim() ?? string.Empty;
        if (!MessagingSettings.IsValidSurveyUrl(url)) return (new FieldRefusal(SettingsUrlField, "Survey_UrlInvalid"), null);
        foreach (var (template, field) in new[] { (form.TemplateFr!, SettingsTemplateFrField), (form.TemplateAr!, SettingsTemplateArField) })
        {
            if (MessagingSettings.HasCode(template, url)) continue;
            return (new FieldRefusal(field, template.Contains(MessageWording.Link, StringComparison.OrdinalIgnoreCase)
                                                ? "Survey_LinkNeedsUrl"
                                                : "Survey_TemplateNeedsCode"), null);
        }

        if (WholeNumber(form.Interval) is not { } interval || interval is < 0 or > MaximumIntervalDays)
            return (new FieldRefusal(SettingsIntervalField, "Survey_IntervalOutOfRange"), null);

        return (null, current with
        {
            SurveyTemplateFr = form.TemplateFr!.Trim(),
            SurveyTemplateAr = form.TemplateAr!.Trim(),
            SurveyUrl = url,
            SurveyIntervalDays = interval
        });
    }

    /// <summary>A service's refusal of the settings beside the box it names (the French wording for a wording's).</summary>
    public static string? SettingsFieldOf(string resourceKey) => resourceKey switch
    {
        "Survey_UrlInvalid" => SettingsUrlField,
        "Survey_IntervalOutOfRange" => SettingsIntervalField,
        "Messaging_TemplateRequired" or "Messaging_TemplateTooLong" or "Messaging_AdvertisingWording"
            or "Messaging_UnknownPlaceholder" or "Survey_LinkNeedsUrl" or "Survey_TemplateNeedsCode" => SettingsTemplateFrField,
        _ => null
    };

    // ---- Answers -------------------------------------------------------------------------------------

    /// <summary>
    /// The answer typed, checked: a survey code as the SMS gave it, a score from 1 to 5, a comment
    /// within its column. A refusal names its box.
    /// </summary>
    public static (FieldRefusal? Refusal, (string Code, int Score, string? Comment)? Answer) Check(SurveyAnswerForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (SurveyCode.Normalise(form.Code) is not { } code)
            return (new FieldRefusal(AnswerCodeField, "Survey_CodeInvalid", form.Code?.Trim() ?? string.Empty), null);
        if (WholeNumber(form.Score) is not { } score || score is < 1 or > 5)
            return (new FieldRefusal(AnswerScoreField, "Survey_ScoreOutOfRange"), null);
        if (form.Comment is { } comment && comment.Trim().Length > SurveyInvitation.CommentMaxLength)
            return (new FieldRefusal(AnswerCommentField, "Relations_TextTooLong",
                                     SurveyInvitation.CommentMaxLength.ToString(CultureInfo.InvariantCulture)), null);
        return (null, (code, score, Blank(form.Comment)));
    }

    /// <summary>A service's refusal of an answer beside the box it names.</summary>
    public static string? AnswerFieldOf(string resourceKey) => resourceKey switch
    {
        "Survey_CodeInvalid" or "Survey_CodeUnknown" or "Survey_AlreadyAnswered" => AnswerCodeField,
        "Survey_ScoreOutOfRange" => AnswerScoreField,
        _ => null
    };

    /// <summary>The box a refusal of a list's period names: reversed.</summary>
    public static string? FieldOf(Error? error) => PeriodReport.IsPeriodRefusal(error) ? PeriodField : null;

    // ---- Helpers -------------------------------------------------------------------------------------

    private static FieldRefusal? Wording(string? template, string field)
    {
        var valid = MessageWording.Validate(template ?? string.Empty);
        return valid.IsFailure ? FieldRefusal.Of(field, valid.FirstError) : null;
    }

    private static int? WholeNumber(string? typed) =>
        TypedNumber.Parse(typed) is { } value && value == decimal.Truncate(value) && Math.Abs(value) <= int.MaxValue
            ? (int)value
            : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
