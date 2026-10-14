using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Abstractions;

/// <summary>
/// How the laboratory sends e-mail and SMS, read from the <c>Messaging</c> section of
/// <c>appsettings.json</c>. Left empty, the channel is simply not offered as a real send.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public SmtpOptions Smtp { get; set; } = new();
    public SmsOptions Sms { get; set; } = new();
}

public sealed class SmtpOptions
{
    /// <summary>Server name, for example <c>smtp.office365.com</c> or <c>smtp.gmail.com</c>.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>587 with STARTTLS is the usual submission port.</summary>
    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>The address the patient sees the report coming from.</summary>
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;

    /// <summary>Copy of every report sent, for the laboratory's own record. Optional.</summary>
    public string BccAddress { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>
/// A provider-neutral HTTP call, because Moroccan SMS gateways each have their own API: the URL,
/// the authorisation header and the body template are copied from the provider's documentation.
/// The template's <c>{to}</c>, <c>{sender}</c> and <c>{message}</c> are replaced, JSON-escaped when
/// the content type is JSON and URL-encoded otherwise.
/// </summary>
public sealed class SmsOptions
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";

    /// <summary>Sent verbatim as the Authorization header, for example <c>Bearer abc123</c>.</summary>
    public string AuthorizationHeader { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/json";
    public string BodyTemplate { get; set; } = "{\"to\":\"{to}\",\"from\":\"{sender}\",\"text\":\"{message}\"}";

    /// <summary>Sender name or number shown on the patient's phone, as registered with the provider.</summary>
    public string Sender { get; set; } = string.Empty;

    /// <summary>Country prefix added to a local number: 0612345678 becomes 212612345678.</summary>
    public string CountryCode { get; set; } = "212";

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

public sealed record EmailMessage(string To, string Subject, string Body,
                                  IReadOnlyList<EmailAttachment> Attachments);

/// <summary>Sends e-mail and SMS. A failure comes back as a result, never as an exception.</summary>
public interface IMessageSender
{
    bool CanSendEmail { get; }
    bool CanSendSms { get; }

    Task<Result> SendEmailAsync(EmailMessage message, CancellationToken ct = default);

    Task<Result> SendSmsAsync(string phoneNumber, string text, CancellationToken ct = default);
}
