using MarocLIS.Application.Input;
using MarocLIS.Domain.Reports;

namespace MarocLIS.Application.Reports;

/// <summary>
/// The rules of the hand-over form (« Enregistrer une remise ») and of the rectification form
/// (« Émettre un rectificatif ») that need no window: which boxes a recipient and a channel call
/// for, what the form fills in itself, and what it refuses before the service is asked. The
/// workstation's dialogs and the web's forms apply the same ones.
/// <para>
/// Handing a report over is a disclosure of health data, so the record has to answer a question
/// somebody may ask months later: who received this, and were they entitled to? A patient
/// collecting their own report needs nothing beyond the fact, a third party needs the patient's
/// written consent on file (loi 08-09), and a bearer collecting on someone's behalf needs their own
/// name and CIN, because "a man came for it" is not a record.
/// </para>
/// </summary>
public static class ReportDeliveryRules
{
    /// <summary>As long as the delivery's columns: a longer entry failed the save of the hand-over.</summary>
    public const int RecipientNameMaxLength = 200;
    public const int RecipientIdentifierMaxLength = 250;
    public const int BearerNameMaxLength = 200;
    public const int BearerCinMaxLength = 20;

    /// <summary>A consent identifier, braces included.</summary>
    public const int ConsentReferenceMaxLength = 38;

    /// <summary>As long as the report's columns.</summary>
    public const int AmendmentReasonMaxLength = 1000;
    public const int ConclusionMaxLength = 4000;

    /// <summary>A bearer is only meaningful for a hand-over: an e-mail has no bearer.</summary>
    public static bool TakesBearer(DeliveryChannel channel) =>
        channel is DeliveryChannel.PrintedAtCounter or DeliveryChannel.Courier;

    /// <summary>A third party is entitled to the report only on the patient's written consent.</summary>
    public static bool NeedsConsent(ReportRecipientKind kind) => kind == ReportRecipientKind.AuthorizedThirdParty;

    /// <summary>
    /// The name the form puts in the recipient box for the recipient chosen: the patient's, the
    /// prescriber's, or nothing. The patient's stayed in the box when the prescriber was picked, and
    /// the hand-over was recorded under the patient's name.
    /// </summary>
    public static string RecipientName(ReportRecipientKind kind, ReportRegisterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return kind switch
        {
            ReportRecipientKind.Patient => row.PatientName,
            ReportRecipientKind.Prescriber => row.PrescriberName ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    /// The address the chosen channel needs: the patient's or the prescriber's e-mail for an
    /// e-mail, the patient's mobile for an SMS or a hand-over at the counter. Null when the file
    /// holds none; whatever the operator typed is kept by the form.
    /// </summary>
    public static string? Contact(DeliveryChannel channel, ReportRecipientKind kind, ReportRegisterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return (channel, kind) switch
        {
            (DeliveryChannel.Email, ReportRecipientKind.Patient) => row.PatientEmail,
            (DeliveryChannel.Email, ReportRecipientKind.Prescriber) => row.PrescriberEmail,
            (DeliveryChannel.SmsLink or DeliveryChannel.PrintedAtCounter, ReportRecipientKind.Patient) => row.PatientPhone,
            _ => null
        };
    }

    /// <summary>What the form says under the channel: what recording it does, and whether e-mail and SMS really go.</summary>
    public static string ChannelNoticeKey(DeliveryChannel channel, bool emailReady, bool smsReady) => channel switch
    {
        DeliveryChannel.PrintedAtCounter => "Report.CounterChannelNotice",
        DeliveryChannel.Email => emailReady ? "Report.EmailChannelLive" : "Report.EmailChannelNotConfigured",
        DeliveryChannel.SmsLink => smsReady ? "Report.SmsChannelLive" : "Report.SmsChannelNotConfigured",
        DeliveryChannel.Fax => "Report.FaxChannelNotice",
        DeliveryChannel.Courier => "Report.CourierChannelNotice",
        _ => "Report.InterfaceChannelNotice"
    };

    /// <summary>
    /// Checks the hand-over before it is recorded, and reads the consent typed for a third party.
    /// An electronic channel with nowhere to send it is a delivery that did not happen; a third
    /// party collecting at the counter is named with their CIN, or the record says a third party
    /// collected it and nothing about who.
    /// </summary>
    public static (FieldRefusal? Refusal, Guid? ConsentId) Check(
        DeliveryChannel channel, ReportRecipientKind kind, string? recipientName, string? recipientIdentifier,
        string? bearerName, string? bearerCin, string? consentReference)
    {
        if (string.IsNullOrWhiteSpace(recipientName))
            return (new FieldRefusal("recipient", "Report_RecipientNameRequired"), null);

        if (channel is DeliveryChannel.Email or DeliveryChannel.SmsLink or DeliveryChannel.Fax
            && string.IsNullOrWhiteSpace(recipientIdentifier))
            return (new FieldRefusal("identifier", "Report_RecipientIdentifierRequired"), null);

        if (!NeedsConsent(kind)) return (null, null);

        if (!Guid.TryParse(consentReference?.Trim(), out var consentId))
            return (new FieldRefusal("consent", "Report_ThirdPartyRequiresConsent"), null);

        if (channel == DeliveryChannel.PrintedAtCounter
            && (string.IsNullOrWhiteSpace(bearerName) || string.IsNullOrWhiteSpace(bearerCin)))
            return (new FieldRefusal("bearer", "Report_BearerRequired"), consentId);

        return (null, consentId);
    }

    // ---- Rectification ----------------------------------------------------------------------------

    /// <summary>
    /// The kinds a rectification may be. None is what a first release is, and offering it would let
    /// an operator issue an amendment that does not say it is one.
    /// </summary>
    public static readonly IReadOnlyList<ReportAmendmentKind> AmendmentKinds =
    [
        ReportAmendmentKind.ResultCorrection, ReportAmendmentKind.Addition,
        ReportAmendmentKind.CommentRevision, ReportAmendmentKind.IdentityCorrection,
        ReportAmendmentKind.Retraction
    ];

    /// <summary>What each kind means to the reader of the new version.</summary>
    public static string AmendmentNoticeKey(ReportAmendmentKind kind) => kind switch
    {
        ReportAmendmentKind.ResultCorrection => "Report.ResultCorrectionNotice",
        ReportAmendmentKind.Addition => "Report.AdditionNotice",
        ReportAmendmentKind.CommentRevision => "Report.CommentRevisionNotice",
        ReportAmendmentKind.Retraction => "Report.RetractionNotice",
        _ => "Report.IdentityCorrectionNotice"
    };

    /// <summary>
    /// A corrected identity or a withdrawn report means the earlier one may have gone to the wrong
    /// person, which is a notifiable data breach under loi 08-09 rather than a clerical fix: the
    /// operator confirms it is really that.
    /// </summary>
    public static bool AmendmentNeedsConfirmation(ReportAmendmentKind kind) =>
        kind is ReportAmendmentKind.IdentityCorrection or ReportAmendmentKind.Retraction;

    /// <summary>The reason is mandatory and printed on the face of the new report.</summary>
    public static FieldRefusal? CheckAmendment(string? reasonFrench) =>
        string.IsNullOrWhiteSpace(reasonFrench) ? new FieldRefusal("reason", "Report_AmendmentReasonRequired") : null;
}
