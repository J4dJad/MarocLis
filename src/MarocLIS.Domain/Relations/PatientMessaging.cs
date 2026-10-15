using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Relations;

/// <summary>
/// A bilan reminder rule: "remind the patient N months after a validated result of this analysis
/// (or of this panel)", e.g. HbA1c after 3 months. Only patients who recorded the
/// <see cref="Patients.ConsentPurpose.FollowUpMessages"/> consent are reminded, and the message says
/// that the check is due, never what to buy.
/// </summary>
public sealed class ReminderRule : AuditableAggregateRoot
{
    public const int MinimumDelayMonths = 1;
    public const int MaximumDelayMonths = 60;

    private ReminderRule() { }

    public ReminderRule(Guid? analysisTestId, Guid? testPanelId, int delayMonths, MultilingualText template)
    {
        AnalysisTestId = analysisTestId;
        TestPanelId = testPanelId;
        DelayMonths = delayMonths;
        Template = template;
    }

    /// <summary>The analysis whose result starts the delay; exclusive with <see cref="TestPanelId"/>.</summary>
    public Guid? AnalysisTestId { get; set; }

    /// <summary>The panel whose result starts the delay; exclusive with <see cref="AnalysisTestId"/>.</summary>
    public Guid? TestPanelId { get; set; }

    public int DelayMonths { get; set; }

    /// <summary>
    /// Days after the due date during which the reminder is still offered. Past it the patient has
    /// most likely been seen elsewhere, and a late message is noise.
    /// </summary>
    public int WindowDays { get; set; } = 60;

    /// <summary>The SMS text, with the placeholders of <see cref="MessageWording"/>.</summary>
    public MultilingualText Template { get; set; } = new();

    public bool IsActive { get; set; } = true;

    /// <summary>Day the reminder falls due for a result validated on <paramref name="resultDate"/>.</summary>
    public DateOnly DueOn(DateOnly resultDate) => resultDate.AddMonths(DelayMonths);

    /// <summary>Whether a reminder for that result may be sent on <paramref name="today"/>.</summary>
    public bool IsInWindow(DateOnly resultDate, DateOnly today)
    {
        var due = DueOn(resultDate);
        return today >= due && today.DayNumber - due.DayNumber <= WindowDays;
    }

    public Result Validate()
    {
        var errors = new List<Error>();

        if (AnalysisTestId is null == TestPanelId is null)
            errors.Add(Error.Validation("Reminder_AnalysisOrPanel"));
        if (DelayMonths is < MinimumDelayMonths or > MaximumDelayMonths)
            errors.Add(Error.Validation("Reminder_DelayOutOfRange", MinimumDelayMonths, MaximumDelayMonths));
        if (WindowDays is < 1 or > 365)
            errors.Add(Error.Validation("Reminder_WindowOutOfRange"));

        var wording = MessageWording.Validate(Template.Fr);
        if (wording.IsFailure) errors.AddRange(wording.Errors);

        foreach (var other in new[] { Template.Ar, Template.En })
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            var checkedOther = MessageWording.Validate(other);
            if (checkedOther.IsFailure) errors.AddRange(checkedOther.Errors);
        }

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors.Distinct().ToList());
    }
}

/// <summary>One SMS sent to a patient by a campaign (reminder or survey), kept as the send log.</summary>
public sealed class PatientMessage : AggregateRoot, IAuditable, ISiteScoped
{
    private PatientMessage() { }

    public PatientMessage(Guid siteId, Guid patientId, PatientMessageKind kind, string phoneNumber, string text,
                          DateTimeOffset sentAt, Guid sentByUserId)
    {
        SiteId = siteId;
        PatientId = patientId;
        Kind = kind;
        PhoneNumber = phoneNumber;
        Text = text;
        SentAt = sentAt;
        SentByUserId = sentByUserId;
    }

    public Guid SiteId { get; set; }
    public Guid PatientId { get; private set; }
    public PatientMessageKind Kind { get; private set; }

    /// <summary>The rule a reminder applied, and the result it was computed from.</summary>
    public Guid? ReminderRuleId { get; set; }
    public Guid? SourceOrderId { get; set; }
    public DateOnly? SourceResultDate { get; set; }

    public Guid? SurveyInvitationId { get; set; }

    public string PhoneNumber { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public DateTimeOffset SentAt { get; private set; }
    public Guid SentByUserId { get; private set; }

    public PatientMessageStatus Status { get; private set; } = PatientMessageStatus.Sent;

    /// <summary>The gateway's refusal, when the send failed.</summary>
    public string? Failure { get; private set; }

    public void MarkFailed(string failure)
    {
        Status = PatientMessageStatus.Failed;
        Failure = failure.Length > 500 ? failure[..500] : failure;
    }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}

public enum PatientMessageKind
{
    BilanReminder = 0,
    SatisfactionSurvey = 1
}

public enum PatientMessageStatus
{
    /// <summary>Accepted by the SMS gateway.</summary>
    Sent = 0,
    /// <summary>Refused by the gateway, or the gateway was unreachable.</summary>
    Failed = 1
}

/// <summary>
/// A satisfaction survey sent for one order: a short code the patient quotes in a reply (or enters
/// on the survey page), and the answer — a score from 1 to 5 and a comment — recorded from that
/// reply or keyed in by the reception desk.
/// </summary>
public sealed class SurveyInvitation : AuditableAggregateRoot, ISiteScoped
{
    public const int CommentMaxLength = 1000;

    private SurveyInvitation() { }

    public SurveyInvitation(Guid siteId, Guid patientId, Guid labOrderId, string code, DateTimeOffset createdAt)
    {
        SiteId = siteId;
        PatientId = patientId;
        LabOrderId = labOrderId;
        Code = code;
        InvitedAt = createdAt;
    }

    public Guid SiteId { get; set; }
    public Guid PatientId { get; private set; }
    public Guid LabOrderId { get; private set; }

    /// <summary>Six characters, unambiguous when read on a phone (no 0/O, no 1/I).</summary>
    public string Code { get; private set; } = string.Empty;

    public DateTimeOffset InvitedAt { get; private set; }

    /// <summary>The SMS reached the gateway; false when the send failed.</summary>
    public bool Delivered { get; set; }

    public int? Score { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public SurveyAnswerChannel? AnswerChannel { get; private set; }
    public Guid? AnswerRecordedByUserId { get; private set; }

    public bool IsAnswered => Score is not null;

    public Result RecordAnswer(int score, string? comment, SurveyAnswerChannel channel, DateTimeOffset at,
                               Guid byUserId)
    {
        if (IsAnswered) return Result.Failure(Error.Conflict("Survey_AlreadyAnswered", Code));
        if (score is < 1 or > 5) return Result.Failure(Error.Validation("Survey_ScoreOutOfRange"));

        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (text is { Length: > CommentMaxLength }) text = text[..CommentMaxLength];

        Score = score;
        Comment = text;
        AnsweredAt = at;
        AnswerChannel = channel;
        AnswerRecordedByUserId = byUserId;
        return Result.Success();
    }
}

public enum SurveyAnswerChannel
{
    /// <summary>Keyed in from a paper form or a phone call.</summary>
    Manual = 0,
    /// <summary>Taken from the patient's SMS reply ("code score comment").</summary>
    ReplyCode = 1,
    /// <summary>Entered by the patient on the survey page the link opens.</summary>
    Web = 2
}

/// <summary>The survey codes: short, random, readable over the phone.</summary>
public static class SurveyCode
{
    public const int Length = 6;

    // No 0/O, 1/I/L: a code read aloud or retyped from a phone must not be mistaken.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string New()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static string? Normalise(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var compact = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length == Length && compact.All(c => Alphabet.Contains(c)) ? compact : null;
    }
}

/// <summary>A patient's reply as read from the SMS: "K7PQ2M 4 accueil très correct".</summary>
public sealed record SurveyReply(string Code, int Score, string? Comment);

public static partial class SurveyReplyParser
{
    [GeneratedRegex(@"^\s*([A-Za-z0-9]{6})\s*[:;,\-]?\s*([1-5])(?:\s*/\s*5)?(?!\d)\s*[:;,\-]?\s*(.*)$",
                    RegexOptions.Singleline)]
    private static partial Regex Pattern();

    /// <summary>The code, the score and the rest of the line as the comment; null when the line is not a reply.</summary>
    public static SurveyReply? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = Pattern().Match(line);
        if (!match.Success) return null;

        var code = SurveyCode.Normalise(match.Groups[1].Value);
        if (code is null) return null;

        var score = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var comment = match.Groups[3].Value.Trim();
        return new SurveyReply(code, score, comment.Length == 0 ? null : comment);
    }
}

/// <summary>
/// The wording rules of patient messages. A follow-up message tells the patient a check is due or
/// asks how the visit went; it does not sell. The words of a promotion are refused in a template,
/// so a campaign cannot become advertising for analyses — which the profession's code of ethics
/// forbids a laboratory — by a later edit.
/// </summary>
public static partial class MessageWording
{
    public const int MaximumLength = 480;

    public const string Patient = "{patient}";
    public const string Analysis = "{analyse}";
    public const string Date = "{date}";
    public const string Laboratory = "{labo}";
    public const string Phone = "{telephone}";
    public const string Code = "{code}";
    public const string Link = "{lien}";

    public static readonly IReadOnlyList<string> Placeholders = [Patient, Analysis, Date, Laboratory, Phone, Code, Link];

    /// <summary>
    /// Words that make a message a promotion, compared without accents or case. Kept short and
    /// unambiguous: "offre" or "remise" in a reminder can only be commercial.
    /// </summary>
    private static readonly string[] AdvertisingTerms =
    [
        "promo", "reduction", "remise", "offre", "offert", "gratuit", "solde", "rabais", "prix", "tarif",
        "discount", "free", "offer", "sale", "%",
        "تخفيض", "خصم", "عرض", "مجان", "تنزيل"
    ];

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex PlaceholderPattern();

    public static Result Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return Result.Failure(Error.Validation("Messaging_TemplateRequired"));
        if (template.Length > MaximumLength)
            return Result.Failure(Error.Validation("Messaging_TemplateTooLong", MaximumLength));

        if (AdvertisingTermIn(template) is { } term)
            return Result.Failure(Error.Validation("Messaging_AdvertisingWording", term));

        foreach (Match match in PlaceholderPattern().Matches(template))
            if (!Placeholders.Contains(match.Value, StringComparer.OrdinalIgnoreCase))
                return Result.Failure(Error.Validation("Messaging_UnknownPlaceholder", match.Value));

        return Result.Success();
    }

    /// <summary>The first promotional word found, or null.</summary>
    public static string? AdvertisingTermIn(string text)
    {
        var folded = Fold(text);
        foreach (var term in AdvertisingTerms)
        {
            var foldedTerm = Fold(term);
            var index = folded.IndexOf(foldedTerm, StringComparison.Ordinal);
            while (index >= 0)
            {
                // Whole-word start for Latin terms: "prix" must not fire inside "prixe" nor
                // "sale" inside "salle"; Arabic and "%" are matched anywhere.
                var latin = foldedTerm.All(c => c is >= 'a' and <= 'z');
                var startOk = !latin || index == 0 || !char.IsLetter(folded[index - 1]);
                var end = index + foldedTerm.Length;
                var endOk = !latin || foldedTerm.Length > 4 || end >= folded.Length || !char.IsLetter(folded[end]);
                if (startOk && endOk) return term;
                index = folded.IndexOf(foldedTerm, index + 1, StringComparison.Ordinal);
            }
        }
        return null;
    }

    /// <summary>Replaces the placeholders; a missing value leaves an empty string, never the brace.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var text = PlaceholderPattern().Replace(template, m =>
            values.TryGetValue(m.Value.ToLowerInvariant(), out var value) ? value ?? string.Empty : string.Empty);

        // Collapse the double spaces an empty placeholder leaves behind.
        return Regex.Replace(text, @"[ \t]{2,}", " ").Trim();
    }

    /// <summary>
    /// The SMS parts the text takes: 160 characters (153 per part when split) in the GSM alphabet,
    /// 70 (67) as soon as one character is outside it — any Arabic letter, or a "œ".
    /// </summary>
    public static int Segments(string text)
    {
        if (text.Length == 0) return 0;

        var gsm = text.All(IsGsm);
        var single = gsm ? 160 : 70;
        var part = gsm ? 153 : 67;
        var length = gsm ? text.Sum(c => GsmExtended.Contains(c) ? 2 : 1) : text.Length;

        return length <= single ? 1 : (int)Math.Ceiling(length / (double)part);
    }

    private const string GsmBasic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    private const string GsmExtended = "^{}\\[~]|€";

    private static bool IsGsm(char c) => GsmBasic.Contains(c) || GsmExtended.Contains(c);

    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
