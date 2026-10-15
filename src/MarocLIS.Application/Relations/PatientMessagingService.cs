using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Relations;

public sealed record ReminderRuleRow(
    Guid Id,
    Guid? AnalysisTestId,
    Guid? TestPanelId,
    string Code,
    MultilingualText Name,
    int DelayMonths,
    int WindowDays,
    MultilingualText Template,
    bool IsActive);

public sealed record SaveReminderRuleRequest
{
    public Guid? Id { get; init; }
    public Guid? AnalysisTestId { get; init; }
    public Guid? TestPanelId { get; init; }
    public int DelayMonths { get; init; } = 3;
    public int WindowDays { get; init; } = 60;
    public required MultilingualText Template { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>An analysis or a panel a rule can name.</summary>
public sealed record ReminderTarget(Guid Id, bool IsPanel, string Code, MultilingualText Name);

/// <summary>One occurrence of a rule's analysis in a patient's orders.</summary>
public sealed record ReminderSourceRow(Guid PatientId, Guid OrderId, DateTimeOffset RegisteredAt,
                                       DateTimeOffset? ValidatedAt);

/// <summary>The contact details and consents a message needs.</summary>
public sealed record MessageRecipient(
    Guid PatientId,
    string FullName,
    string? MobilePhone,
    AppLanguage Language,
    bool HasFollowUpConsent,
    bool OptedOutOfSecondaryUse);

/// <summary>A reminder the planner found due, before the patient's consent and number are checked.</summary>
public sealed record PlannedReminder(Guid RuleId, Guid PatientId, Guid OrderId, DateOnly ResultDate, DateOnly DueOn);

public sealed record ReminderCandidate(
    Guid RuleId,
    Guid PatientId,
    Guid OrderId,
    string PatientName,
    string Phone,
    string Analysis,
    DateOnly ResultDate,
    DateOnly DueOn,
    string Text,
    int Segments);

public sealed record ReminderSelection(Guid RuleId, Guid PatientId, Guid OrderId);

public sealed record SurveyCandidate(Guid OrderId, Guid PatientId, string PatientName, string Phone,
                                     string AccessionNumber, DateTimeOffset ReportedAt, string Text, int Segments);

/// <summary>A reported order the survey could follow, before consent and frequency are checked.</summary>
public sealed record SurveySourceRow(Guid OrderId, Guid PatientId, string AccessionNumber, DateTimeOffset ReportedAt);

public sealed record CampaignOutcome(int Sent, int Failed, int Skipped);

public sealed record ReplyImportOutcome(int Recorded, IReadOnlyList<string> Unrecognised, IReadOnlyList<string> Refused);

public sealed record SurveyComment(DateOnly Date, int Score, string Comment);

public sealed record SurveyResults(
    DateOnly From,
    DateOnly To,
    int Invited,
    int Delivered,
    int Answered,
    double? ResponseRatePercent,
    double? AverageScore,
    IReadOnlyList<int> Distribution,
    IReadOnlyList<SurveyComment> Comments);

/// <summary>What the survey statistics read: one invitation, anonymised.</summary>
public sealed record SurveyAnswerRow(DateTimeOffset InvitedAt, bool Delivered, int? Score, string? Comment,
                                     DateTimeOffset? AnsweredAt);

public sealed record PatientMessageRow(
    DateTimeOffset SentAt,
    PatientMessageKind Kind,
    string PatientName,
    string Phone,
    PatientMessageStatus Status,
    string? Failure,
    string Text);

/// <summary>The laboratory's name and phone as the messages sign them.</summary>
public sealed record MessageSignature(string Laboratory, string? Phone);

/// <summary>
/// The survey wording and link, held as installation settings (<c>AppSetting</c>).
/// </summary>
public sealed record MessagingSettings
{
    public const string Prefix = "Relations.Messaging.";
    public const string SurveyTemplateFrKey = "Relations.Messaging.SurveyTemplate.Fr";
    public const string SurveyTemplateArKey = "Relations.Messaging.SurveyTemplate.Ar";
    public const string SurveyUrlKey = "Relations.Messaging.SurveyUrl";
    public const string SurveyIntervalDaysKey = "Relations.Messaging.SurveyIntervalDays";

    public const string DefaultReminderFr =
        "Bonjour {patient}, votre dernier dosage {analyse} date du {date}. Si votre médecin vous a demandé " +
        "un contrôle régulier, il est peut-être temps de le refaire. {labo} {telephone}";

    public const string DefaultReminderAr =
        "مرحبا {patient}، آخر تحليل {analyse} أجريتموه بتاريخ {date}. إذا طلب منكم طبيبكم مراقبة منتظمة، " +
        "فقد يكون الوقت قد حان لإعادته. {labo} {telephone}";

    public string SurveyTemplateFr { get; init; } =
        "{labo} : merci de votre visite. Votre avis nous aide à améliorer notre accueil. Répondez par SMS " +
        "{code} suivi d'une note de 1 à 5 et, si vous le souhaitez, d'un commentaire. {lien}";

    public string SurveyTemplateAr { get; init; } =
        "{labo}: شكرا على زيارتكم. رأيكم يساعدنا على تحسين استقبالنا. أجيبوا برسالة تتضمن الرمز {code} " +
        "ثم نقطة من 1 إلى 5 وتعليقا إن شئتم. {lien}";

    /// <summary>
    /// The survey page, when the laboratory has one: <c>{code}</c> in it is replaced, otherwise
    /// <c>?code=…</c> is appended. Empty: patients answer by replying to the SMS.
    /// </summary>
    public string SurveyUrl { get; init; } = string.Empty;

    /// <summary>A patient is not surveyed again within this many days.</summary>
    public int SurveyIntervalDays { get; init; } = 90;

    public static MessagingSettings Default { get; } = new();

    public string? SurveyLink(string code)
    {
        if (string.IsNullOrWhiteSpace(SurveyUrl)) return null;
        var url = SurveyUrl.Trim();
        return url.Contains("{code}", StringComparison.OrdinalIgnoreCase)
            ? url.Replace("{code}", code, StringComparison.OrdinalIgnoreCase)
            : url + (url.Contains('?') ? "&" : "?") + "code=" + code;
    }

    public static MessagingSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        var d = Default;
        string Text(string key, string fallback) =>
            values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

        return new MessagingSettings
        {
            SurveyTemplateFr = Text(SurveyTemplateFrKey, d.SurveyTemplateFr),
            SurveyTemplateAr = Text(SurveyTemplateArKey, d.SurveyTemplateAr),
            SurveyUrl = values.TryGetValue(SurveyUrlKey, out var url) ? url?.Trim() ?? string.Empty : string.Empty,
            SurveyIntervalDays = values.TryGetValue(SurveyIntervalDaysKey, out var days)
                                 && int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                                 && n is >= 0 and <= 3650
                ? n
                : d.SurveyIntervalDays
        };
    }

    public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>
    {
        [SurveyTemplateFrKey] = SurveyTemplateFr,
        [SurveyTemplateArKey] = SurveyTemplateAr,
        [SurveyUrlKey] = SurveyUrl,
        [SurveyIntervalDaysKey] = SurveyIntervalDays.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>Which results make a reminder due today, from plain rows, so it can be tested without a database.</summary>
public static class ReminderPlanner
{
    /// <summary>
    /// For each patient, the latest validated result of the rule's analysis starts the delay. No
    /// reminder when the patient has a later order for it (registered, even not yet validated), when
    /// the window after the due date has passed, or when this result was already reminded.
    /// </summary>
    public static IReadOnlyList<PlannedReminder> Plan(ReminderRule rule, IEnumerable<ReminderSourceRow> sources,
                                                      IReadOnlySet<(Guid RuleId, Guid OrderId)> alreadySent,
                                                      DateOnly today, TimeSpan offset)
    {
        var planned = new List<PlannedReminder>();

        foreach (var patient in sources.GroupBy(s => s.PatientId))
        {
            var latestValidated = patient.Where(s => s.ValidatedAt is not null)
                                         .OrderByDescending(s => s.ValidatedAt).FirstOrDefault();
            if (latestValidated is null) continue;

            // A later order for the same analysis means the patient is already back.
            if (patient.Any(s => s.OrderId != latestValidated.OrderId && s.RegisteredAt > latestValidated.RegisteredAt))
                continue;

            var resultDate = DateOnly.FromDateTime(latestValidated.ValidatedAt!.Value.ToOffset(offset).DateTime);
            if (!rule.IsInWindow(resultDate, today)) continue;
            if (alreadySent.Contains((rule.Id, latestValidated.OrderId))) continue;

            planned.Add(new PlannedReminder(rule.Id, patient.Key, latestValidated.OrderId, resultDate,
                                            rule.DueOn(resultDate)));
        }

        return planned;
    }

    /// <summary>Oldest result the rule can still remind, to bound the query.</summary>
    public static DateOnly EarliestResultDate(ReminderRule rule, DateOnly today) =>
        today.AddDays(-rule.WindowDays).AddMonths(-rule.DelayMonths).AddDays(-1);
}

/// <summary>
/// Bilan reminders and satisfaction surveys by SMS, through the laboratory's configured gateway
/// (<see cref="IMessageSender"/>). Only patients who recorded the follow-up messages consent are
/// written to, the wording is checked against promotional terms, every send is logged, and every
/// transmission is in the data-access log. WhatsApp is not offered: it needs an approved WhatsApp
/// Business provider under contract, which the laboratory does not have.
/// </summary>
public sealed class PatientMessagingService(
    IRepository<ReminderRule> rules,
    IRepository<PatientMessage> messages,
    IRepository<SurveyInvitation> invitations,
    IPatientMessagingQueries queries,
    IConfigurationWriter settingsWriter,
    IMessageSender sender,
    IDataAccessAuditor accessAuditor,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Most messages one batch sends: a gateway refusing a burst should not take the afternoon.</summary>
    public const int MaximumBatch = 500;

    public bool CanSendSms => sender.CanSendSms;

    private Result Require() => guard.Require(Permissions.Messaging.Campaign);

    // ---- Rules ---------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<ReminderRuleRow>>> GetRulesAsync(CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ReminderRuleRow>>(permitted.FirstError);
        return Result.Success(await queries.GetRulesAsync(ct));
    }

    public async Task<Result<IReadOnlyList<ReminderTarget>>> GetTargetsAsync(CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ReminderTarget>>(permitted.FirstError);
        return Result.Success(await queries.GetTargetsAsync(ct));
    }

    public async Task<Result<Guid>> SaveRuleAsync(SaveReminderRuleRequest request, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var template = new MultilingualText(request.Template.Fr?.Trim() ?? string.Empty,
                                            Blank(request.Template.Ar), Blank(request.Template.En));

        ReminderRule rule;
        if (request.Id is { } id)
        {
            var existing = await rules.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Reminder_NotFound"));
            rule = existing;
        }
        else
        {
            rule = new ReminderRule(request.AnalysisTestId, request.TestPanelId, request.DelayMonths, template);
        }

        rule.AnalysisTestId = request.AnalysisTestId;
        rule.TestPanelId = request.TestPanelId;
        rule.DelayMonths = request.DelayMonths;
        rule.WindowDays = request.WindowDays;
        rule.Template = template;
        rule.IsActive = request.IsActive;

        var valid = rule.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (await queries.RuleExistsAsync(rule.AnalysisTestId, rule.TestPanelId, request.Id, ct))
            return Result.Failure<Guid>(Error.Conflict("Reminder_AlreadyExists"));

        if (request.Id is null) await rules.AddAsync(rule, ct);
        else rules.Update(rule);

        unitOfWork.AuditReason = $"Règle de rappel de bilan ({rule.DelayMonths} mois) enregistrée";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(rule.Id);
    }

    // ---- Reminders -----------------------------------------------------------------------------------

    /// <summary>The reminders due today at the operator's site, with the text each patient would receive.</summary>
    public async Task<Result<IReadOnlyList<ReminderCandidate>>> PreviewRemindersAsync(CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ReminderCandidate>>(permitted.FirstError);

        var candidates = await ComputeRemindersAsync(ct);
        await accessAuditor.LogAsync(DataAccessKind.Search, context: "Relations/ReminderPreview",
                                     recordCount: candidates.Count, ct: ct);
        return Result.Success(candidates);
    }

    private async Task<IReadOnlyList<ReminderCandidate>> ComputeRemindersAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var offset = clock.Now.Offset;
        var active = (await rules.ListAsync(r => r.IsActive, ct)).ToList();
        if (active.Count == 0) return [];

        var sent = await queries.GetRemindedAsync(currentUser.SiteId, ct);
        var targets = (await queries.GetTargetsAsync(ct)).ToDictionary(t => t.Id);
        var signature = await queries.GetSignatureAsync(currentUser.SiteId, ct);

        var planned = new List<PlannedReminder>();
        foreach (var rule in active)
        {
            var since = new DateTimeOffset(ReminderPlanner.EarliestResultDate(rule, today).ToDateTime(TimeOnly.MinValue), offset);
            var sources = await queries.GetReminderSourcesAsync(currentUser.SiteId, rule.AnalysisTestId,
                                                                rule.TestPanelId, since, ct);
            planned.AddRange(ReminderPlanner.Plan(rule, sources, sent, today, offset));
        }

        var recipients = await queries.GetRecipientsAsync([.. planned.Select(p => p.PatientId).Distinct()], today, ct);
        var byRule = active.ToDictionary(r => r.Id);

        var candidates = new List<ReminderCandidate>();
        foreach (var plan in planned)
        {
            // The consent and a usable mobile number are required; nothing else will do.
            if (!recipients.TryGetValue(plan.PatientId, out var recipient) || !recipient.HasFollowUpConsent
                || string.IsNullOrWhiteSpace(recipient.MobilePhone))
                continue;

            var rule = byRule[plan.RuleId];
            var targetId = rule.AnalysisTestId ?? rule.TestPanelId!.Value;
            var analysis = targets.TryGetValue(targetId, out var target) ? target.Name.For(recipient.Language) : "?";
            var template = rule.Template.For(recipient.Language);

            var text = MessageWording.Render(template, new Dictionary<string, string?>
            {
                [MessageWording.Patient] = recipient.FullName,
                [MessageWording.Analysis] = analysis,
                [MessageWording.Date] = plan.ResultDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                [MessageWording.Laboratory] = signature.Laboratory,
                [MessageWording.Phone] = signature.Phone
            });

            candidates.Add(new ReminderCandidate(plan.RuleId, plan.PatientId, plan.OrderId, recipient.FullName,
                                                 recipient.MobilePhone!, analysis, plan.ResultDate, plan.DueOn,
                                                 text, MessageWording.Segments(text)));
        }

        return candidates.OrderBy(c => c.DueOn).ThenBy(c => c.PatientName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// Sends the selected reminders. The list is computed again first, so a patient whose consent
    /// was withdrawn since the preview, or who came back in the meantime, is skipped.
    /// </summary>
    public async Task<Result<CampaignOutcome>> SendRemindersAsync(IReadOnlyList<ReminderSelection> selection,
                                                                  CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<CampaignOutcome>(permitted.FirstError);
        if (!sender.CanSendSms) return Result.Failure<CampaignOutcome>(Error.Validation("Messaging_SmsNotConfigured"));
        if (selection.Count == 0) return Result.Failure<CampaignOutcome>(Error.Validation("Messaging_NothingSelected"));
        if (selection.Count > MaximumBatch)
            return Result.Failure<CampaignOutcome>(Error.Validation("Messaging_BatchTooLarge", MaximumBatch));

        var current = (await ComputeRemindersAsync(ct)).ToDictionary(c => (c.RuleId, c.PatientId, c.OrderId));
        int sent = 0, failed = 0, skipped = 0;

        foreach (var pick in selection.Distinct())
        {
            if (!current.TryGetValue((pick.RuleId, pick.PatientId, pick.OrderId), out var candidate))
            {
                skipped++;
                continue;
            }

            var message = new PatientMessage(currentUser.SiteId, candidate.PatientId, PatientMessageKind.BilanReminder,
                                             candidate.Phone, candidate.Text, clock.Now, currentUser.UserId)
            {
                ReminderRuleId = candidate.RuleId,
                SourceOrderId = candidate.OrderId,
                SourceResultDate = candidate.ResultDate
            };

            if (await SendAsync(message, candidate.OrderId, "Relations/Reminder", ct)) sent++;
            else failed++;
        }

        return Result.Success(new CampaignOutcome(sent, failed, skipped));
    }

    // Sends one message and saves its log row at once: a batch interrupted half-way must not send
    // the first half again.
    private async Task<bool> SendAsync(PatientMessage message, Guid? orderId, string context, CancellationToken ct)
    {
        var result = await sender.SendSmsAsync(message.PhoneNumber, message.Text, ct);
        if (result.IsFailure)
            message.MarkFailed(string.Join(" ", new[] { result.FirstError.ResourceKey }
                .Concat(result.FirstError.Args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? string.Empty))));

        await messages.AddAsync(message, ct);
        unitOfWork.AuditReason = message.Kind == PatientMessageKind.BilanReminder
            ? "Rappel de bilan envoyé par SMS"
            : "Enquête de satisfaction envoyée par SMS";
        await unitOfWork.SaveChangesAsync(ct);

        if (result.IsSuccess)
            await accessAuditor.LogAsync(DataAccessKind.Transmit, message.PatientId, orderId, context, ct: ct);

        return result.IsSuccess;
    }

    // ---- Surveys -------------------------------------------------------------------------------------

    public async Task<Result<MessagingSettings>> GetSettingsAsync(CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<MessagingSettings>(permitted.FirstError);
        return Result.Success(MessagingSettings.FromValues(await queries.GetSettingValuesAsync(MessagingSettings.Prefix, ct)));
    }

    public async Task<Result> SaveSettingsAsync(MessagingSettings settings, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        foreach (var template in new[] { settings.SurveyTemplateFr, settings.SurveyTemplateAr })
        {
            var valid = MessageWording.Validate(template);
            if (valid.IsFailure) return valid;
        }

        if (!HasCode(settings.SurveyTemplateFr) || !HasCode(settings.SurveyTemplateAr))
            return Result.Failure(Error.Validation("Survey_TemplateNeedsCode"));
        if (settings.SurveyIntervalDays is < 0 or > 3650)
            return Result.Failure(Error.Validation("Survey_IntervalOutOfRange"));
        if (!string.IsNullOrWhiteSpace(settings.SurveyUrl)
            && !(Uri.TryCreate(settings.SurveyUrl.Replace("{code}", "X", StringComparison.OrdinalIgnoreCase),
                               UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            return Result.Failure(Error.Validation("Survey_UrlInvalid"));

        var changed = new List<string>();
        foreach (var (key, value) in settings.ToValues())
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                await settingsWriter.AddSettingAsync(new AppSetting(key, value)
                {
                    Description = "Relations patients : enquête de satisfaction par SMS"
                }, ct);
                changed.Add(key);
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(key);
            }
        }

        if (changed.Count == 0) return Result.Success();

        unitOfWork.AuditReason = $"Paramètres des messages patients modifiés : {string.Join(", ", changed)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();

        static bool HasCode(string template) =>
            template.Contains(MessageWording.Code, StringComparison.OrdinalIgnoreCase)
            || template.Contains(MessageWording.Link, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Orders first reported in the period whose patient may be surveyed.</summary>
    public async Task<Result<IReadOnlyList<SurveyCandidate>>> PreviewSurveysAsync(DateOnly from, DateOnly to,
                                                                                 CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SurveyCandidate>>(permitted.FirstError);
        if (to < from) return Result.Failure<IReadOnlyList<SurveyCandidate>>(Error.Validation("Stats_PeriodReversed"));

        var candidates = await ComputeSurveysAsync(from, to, ct);
        await accessAuditor.LogAsync(DataAccessKind.Search, context: "Relations/SurveyPreview",
                                     recordCount: candidates.Count, ct: ct);
        return Result.Success(candidates);
    }

    private async Task<IReadOnlyList<SurveyCandidate>> ComputeSurveysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var offset = clock.Now.Offset;
        var settings = MessagingSettings.FromValues(await queries.GetSettingValuesAsync(MessagingSettings.Prefix, ct));
        var signature = await queries.GetSignatureAsync(currentUser.SiteId, ct);

        var sources = await queries.GetSurveySourcesAsync(
            currentUser.SiteId,
            new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset),
            new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), offset), ct);

        var patients = sources.Select(s => s.PatientId).Distinct().ToList();
        var recipients = await queries.GetRecipientsAsync(patients, clock.Today, ct);
        var lastSurveyed = await queries.GetLastSurveyedAsync(patients, ct);
        var surveyedOrders = await queries.GetSurveyedOrdersAsync([.. sources.Select(s => s.OrderId)], ct);

        var candidates = new List<SurveyCandidate>();
        var chosen = new HashSet<Guid>();

        foreach (var source in sources.OrderByDescending(s => s.ReportedAt))
        {
            if (surveyedOrders.Contains(source.OrderId) || !chosen.Add(source.PatientId)) continue;
            if (!recipients.TryGetValue(source.PatientId, out var r) || !r.HasFollowUpConsent || r.OptedOutOfSecondaryUse
                || string.IsNullOrWhiteSpace(r.MobilePhone))
                continue;
            if (lastSurveyed.TryGetValue(source.PatientId, out var last)
                && (clock.Now - last).TotalDays < settings.SurveyIntervalDays)
                continue;

            // The code shown in the preview is a placeholder: the real one is drawn at sending.
            var text = SurveyText(settings, r.Language, signature, "XXXXXX");
            candidates.Add(new SurveyCandidate(source.OrderId, source.PatientId, r.FullName, r.MobilePhone!,
                                               source.AccessionNumber, source.ReportedAt, text,
                                               MessageWording.Segments(text)));
        }

        return candidates;
    }

    private static string SurveyText(MessagingSettings settings, AppLanguage language, MessageSignature signature,
                                     string code) =>
        MessageWording.Render(language == AppLanguage.Arabic ? settings.SurveyTemplateAr : settings.SurveyTemplateFr,
            new Dictionary<string, string?>
            {
                [MessageWording.Laboratory] = signature.Laboratory,
                [MessageWording.Phone] = signature.Phone,
                [MessageWording.Code] = code,
                [MessageWording.Link] = settings.SurveyLink(code)
            });

    public async Task<Result<CampaignOutcome>> SendSurveysAsync(DateOnly from, DateOnly to,
                                                                IReadOnlyList<Guid> orderIds,
                                                                CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<CampaignOutcome>(permitted.FirstError);
        if (!sender.CanSendSms) return Result.Failure<CampaignOutcome>(Error.Validation("Messaging_SmsNotConfigured"));
        if (orderIds.Count == 0) return Result.Failure<CampaignOutcome>(Error.Validation("Messaging_NothingSelected"));
        if (orderIds.Count > MaximumBatch)
            return Result.Failure<CampaignOutcome>(Error.Validation("Messaging_BatchTooLarge", MaximumBatch));

        var settings = MessagingSettings.FromValues(await queries.GetSettingValuesAsync(MessagingSettings.Prefix, ct));
        var signature = await queries.GetSignatureAsync(currentUser.SiteId, ct);
        var current = (await ComputeSurveysAsync(from, to, ct)).ToDictionary(c => c.OrderId);
        var recipients = await queries.GetRecipientsAsync([.. current.Values.Select(c => c.PatientId).Distinct()],
                                                          clock.Today, ct);
        int sent = 0, failed = 0, skipped = 0;

        foreach (var orderId in orderIds.Distinct())
        {
            if (!current.TryGetValue(orderId, out var candidate))
            {
                skipped++;
                continue;
            }

            var code = await NewCodeAsync(ct);
            var invitation = new SurveyInvitation(currentUser.SiteId, candidate.PatientId, orderId, code, clock.Now);
            await invitations.AddAsync(invitation, ct);

            var text = SurveyText(settings, recipients[candidate.PatientId].Language, signature, code);
            var message = new PatientMessage(currentUser.SiteId, candidate.PatientId, PatientMessageKind.SatisfactionSurvey,
                                             candidate.Phone, text, clock.Now, currentUser.UserId)
            {
                SourceOrderId = orderId,
                SurveyInvitationId = invitation.Id
            };

            var ok = await SendAsync(message, orderId, "Relations/Survey", ct);
            if (ok)
            {
                invitation.Delivered = true;
                await unitOfWork.SaveChangesAsync(ct);
                sent++;
            }
            else failed++;
        }

        return Result.Success(new CampaignOutcome(sent, failed, skipped));
    }

    private async Task<string> NewCodeAsync(CancellationToken ct)
    {
        // 31^6 codes: a collision is rare, and checked anyway because the code is how a reply is matched.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = SurveyCode.New();
            if (!await queries.SurveyCodeExistsAsync(code, ct)) return code;
        }
        throw new InvalidOperationException("No free survey code after ten draws.");
    }

    /// <summary>Records an answer keyed in by the desk, or read from a reply.</summary>
    public async Task<Result> RecordAnswerAsync(string code, int score, string? comment, SurveyAnswerChannel channel,
                                                CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return permitted;

        var normalised = SurveyCode.Normalise(code);
        if (normalised is null) return Result.Failure(Error.Validation("Survey_CodeInvalid", code));

        var id = await queries.FindSurveyAsync(normalised, ct);
        if (id is null) return Result.Failure(Error.NotFound("Survey_CodeUnknown", normalised));

        var invitation = await invitations.GetByIdAsync(id.Value, ct);
        if (invitation is null) return Result.Failure(Error.NotFound("Survey_CodeUnknown", normalised));

        var recorded = invitation.RecordAnswer(score, comment, channel, clock.Now, currentUser.UserId);
        if (recorded.IsFailure) return recorded;

        invitations.Update(invitation);
        unitOfWork.AuditReason = $"Réponse à l'enquête de satisfaction {normalised}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Records the replies pasted from the gateway's inbox or the phone, one per line
    /// ("CODE note commentaire"). Lines that are not replies are returned untouched.
    /// </summary>
    public async Task<Result<ReplyImportOutcome>> ImportRepliesAsync(string text, CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<ReplyImportOutcome>(permitted.FirstError);

        var recorded = 0;
        var unrecognised = new List<string>();
        var refused = new List<string>();

        foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var reply = SurveyReplyParser.Parse(line);
            if (reply is null)
            {
                unrecognised.Add(line);
                continue;
            }

            var result = await RecordAnswerAsync(reply.Code, reply.Score, reply.Comment, SurveyAnswerChannel.ReplyCode, ct);
            if (result.IsSuccess) recorded++;
            else refused.Add($"{reply.Code}: {result.FirstError.ResourceKey}");
        }

        return Result.Success(new ReplyImportOutcome(recorded, unrecognised, refused));
    }

    public async Task<Result<SurveyResults>> GetSurveyResultsAsync(DateOnly from, DateOnly to,
                                                                   CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Messaging.Campaign, Permissions.Steering.View);
        if (permitted.IsFailure) return Result.Failure<SurveyResults>(permitted.FirstError);
        if (to < from) return Result.Failure<SurveyResults>(Error.Validation("Stats_PeriodReversed"));

        var offset = clock.Now.Offset;
        var rows = await queries.GetSurveyAnswersAsync(
            currentUser.SiteId,
            new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset),
            new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), offset), ct);

        return Result.Success(SummariseSurveys(from, to, rows, offset));
    }

    public static SurveyResults SummariseSurveys(DateOnly from, DateOnly to, IReadOnlyList<SurveyAnswerRow> rows,
                                                 TimeSpan offset)
    {
        var delivered = rows.Count(r => r.Delivered);
        var answered = rows.Where(r => r.Score is not null).ToList();
        var distribution = Enumerable.Range(1, 5).Select(s => answered.Count(r => r.Score == s)).ToList();

        return new SurveyResults(
            from, to, rows.Count, delivered, answered.Count,
            delivered == 0 ? null : Math.Round(100.0 * answered.Count / delivered, 1),
            answered.Count == 0 ? null : Math.Round(answered.Average(r => r.Score!.Value), 2),
            distribution,
            answered.Where(r => !string.IsNullOrWhiteSpace(r.Comment))
                    .OrderByDescending(r => r.AnsweredAt)
                    .Select(r => new SurveyComment(DateOnly.FromDateTime((r.AnsweredAt ?? r.InvitedAt).ToOffset(offset).DateTime),
                                                   r.Score!.Value, r.Comment!))
                    .ToList());
    }

    public async Task<Result<IReadOnlyList<PatientMessageRow>>> GetLogAsync(DateOnly from, DateOnly to,
                                                                           CancellationToken ct = default)
    {
        var permitted = Require();
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PatientMessageRow>>(permitted.FirstError);
        if (to < from) return Result.Failure<IReadOnlyList<PatientMessageRow>>(Error.Validation("Stats_PeriodReversed"));

        var offset = clock.Now.Offset;
        var rows = await queries.GetMessagesAsync(
            currentUser.SiteId,
            new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset),
            new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), offset), ct);

        await accessAuditor.LogAsync(DataAccessKind.Search, context: "Relations/MessageLog", recordCount: rows.Count, ct: ct);
        return Result.Success(rows);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IPatientMessagingQueries
{
    Task<IReadOnlyList<ReminderRuleRow>> GetRulesAsync(CancellationToken ct = default);

    /// <summary>Active analyses and panels a rule can name.</summary>
    Task<IReadOnlyList<ReminderTarget>> GetTargetsAsync(CancellationToken ct = default);

    Task<bool> RuleExistsAsync(Guid? analysisTestId, Guid? testPanelId, Guid? exceptId, CancellationToken ct = default);

    /// <summary>
    /// Non-cancelled occurrences of the analysis (or of the panel) at the site in orders registered
    /// since <paramref name="since"/>, with their validation time when validated.
    /// </summary>
    Task<IReadOnlyList<ReminderSourceRow>> GetReminderSourcesAsync(Guid siteId, Guid? analysisTestId, Guid? testPanelId,
                                                                   DateTimeOffset since, CancellationToken ct = default);

    /// <summary>The (rule, order) pairs a reminder was already sent for at the site, failed sends excluded.</summary>
    Task<IReadOnlySet<(Guid RuleId, Guid OrderId)>> GetRemindedAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>Name, mobile, language and the follow-up consent in force on <paramref name="today"/>.</summary>
    Task<IReadOnlyDictionary<Guid, MessageRecipient>> GetRecipientsAsync(IReadOnlyList<Guid> patientIds, DateOnly today,
                                                                        CancellationToken ct = default);

    Task<MessageSignature> GetSignatureAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>Orders of the site first reported in <c>[start, end)</c>.</summary>
    Task<IReadOnlyList<SurveySourceRow>> GetSurveySourcesAsync(Guid siteId, DateTimeOffset start, DateTimeOffset end,
                                                               CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastSurveyedAsync(IReadOnlyList<Guid> patientIds,
                                                                        CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetSurveyedOrdersAsync(IReadOnlyList<Guid> orderIds, CancellationToken ct = default);

    Task<bool> SurveyCodeExistsAsync(string code, CancellationToken ct = default);

    Task<Guid?> FindSurveyAsync(string code, CancellationToken ct = default);

    Task<IReadOnlyList<SurveyAnswerRow>> GetSurveyAnswersAsync(Guid siteId, DateTimeOffset start, DateTimeOffset end,
                                                               CancellationToken ct = default);

    Task<IReadOnlyList<PatientMessageRow>> GetMessagesAsync(Guid siteId, DateTimeOffset start, DateTimeOffset end,
                                                            CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);
}
