using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Infrastructure.Messaging;

/// <summary>
/// E-mail through the laboratory's own SMTP account, SMS through its gateway's HTTP API.
/// <para>
/// System.Net.Mail rather than a third-party client: it handles the submission port with STARTTLS,
/// which is what Office 365, Gmail and the Moroccan hosting providers offer, and it adds no
/// dependency to an application installed on laboratory workstations.
/// </para>
/// </summary>
public sealed class MessageSender(MessagingOptions options, ILogger<MessageSender>? logger = null)
    : IMessageSender
{
    private static readonly HttpClient Http = new();

    // Accents and apostrophes kept as they are: some gateways count \u00E9 as six characters.
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool CanSendEmail => options.Smtp.IsConfigured;
    public bool CanSendSms => options.Sms.IsConfigured;

    public async Task<Result> SendEmailAsync(EmailMessage message, CancellationToken ct = default)
    {
        var smtp = options.Smtp;
        if (!smtp.IsConfigured) return Result.Failure(Error.Validation("Messaging_EmailNotConfigured"));

        MailAddress to;
        try { to = new MailAddress(message.To.Trim()); }
        catch (FormatException) { return Result.Failure(Error.Validation("Messaging_InvalidEmail", message.To)); }

        using var mail = new MailMessage
        {
            From = new MailAddress(smtp.FromAddress, string.IsNullOrWhiteSpace(smtp.FromName) ? null : smtp.FromName),
            Subject = message.Subject,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        mail.To.Add(to);
        if (!string.IsNullOrWhiteSpace(smtp.BccAddress)) mail.Bcc.Add(smtp.BccAddress);

        var streams = new List<MemoryStream>();

        try
        {
            foreach (var attachment in message.Attachments)
            {
                var stream = new MemoryStream(attachment.Content);
                streams.Add(stream);
                mail.Attachments.Add(new Attachment(stream, attachment.FileName, attachment.ContentType));
            }

            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = smtp.TimeoutSeconds * 1000
            };

            if (!string.IsNullOrWhiteSpace(smtp.UserName))
                client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);

            // SmtpClient.Timeout applies to Send only, not to SendMailAsync: a server that accepted
            // the connection and then stopped answering held the send — and the screen waiting on
            // it — indefinitely. The limit is enforced with a token instead.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, smtp.TimeoutSeconds)));

            await client.SendMailAsync(mail, timeout.Token);
            return Result.Success();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("Sending e-mail to {Recipient} through {Host} timed out.", to.Address, smtp.Host);
            return Result.Failure(Error.Validation("Messaging_EmailFailed", "timeout"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Sending e-mail to {Recipient} through {Host} failed.", to.Address, smtp.Host);
            return Result.Failure(Error.Validation("Messaging_EmailFailed", Describe(ex)));
        }
        finally
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }

    public async Task<Result> SendSmsAsync(string phoneNumber, string text, CancellationToken ct = default)
    {
        var sms = options.Sms;
        if (!sms.IsConfigured) return Result.Failure(Error.Validation("Messaging_SmsNotConfigured"));

        var number = NormalisePhone(phoneNumber, sms.CountryCode);
        if (number is null) return Result.Failure(Error.Validation("Messaging_InvalidPhone", phoneNumber));

        var json = sms.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase);

        string Encode(string value) => json
            ? JsonSerializer.Serialize(value, Json)[1..^1]
            : Uri.EscapeDataString(value);

        var body = sms.BodyTemplate
            .Replace("{to}", Encode(number))
            .Replace("{sender}", Encode(sms.Sender))
            .Replace("{message}", Encode(text));

        var url = sms.Url
            .Replace("{to}", Uri.EscapeDataString(number))
            .Replace("{sender}", Uri.EscapeDataString(sms.Sender))
            .Replace("{message}", Uri.EscapeDataString(text));

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(sms.Method.ToUpperInvariant()), url);

            if (!string.IsNullOrWhiteSpace(sms.AuthorizationHeader))
                request.Headers.TryAddWithoutValidation("Authorization", sms.AuthorizationHeader);

            if (request.Method != HttpMethod.Get)
                request.Content = new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse(sms.ContentType).MediaType!);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(sms.TimeoutSeconds));

            using var response = await Http.SendAsync(request, timeout.Token);

            if (response.IsSuccessStatusCode) return Result.Success();

            var answer = await response.Content.ReadAsStringAsync(ct);
            var detail = $"HTTP {(int)response.StatusCode} {answer[..Math.Min(answer.Length, 200)]}".Trim();

            logger?.LogWarning("SMS gateway refused the message: {Detail}", detail);
            return Result.Failure(Error.Validation("Messaging_SmsFailed", detail));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Failure(Error.Validation("Messaging_SmsFailed", "timeout"));
        }
        catch (HttpRequestException ex)
        {
            logger?.LogWarning(ex, "SMS gateway unreachable.");
            return Result.Failure(Error.Validation("Messaging_SmsFailed", ex.Message));
        }
    }

    /// <summary>
    /// Digits only, with the country prefix: <c>06 12 34 56 78</c>, <c>+212612345678</c> and
    /// <c>00212612345678</c> all become <c>212612345678</c>.
    /// </summary>
    public static string? NormalisePhone(string? phone, string countryCode)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());

        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        else if (digits.StartsWith('0') && !string.IsNullOrEmpty(countryCode)) digits = countryCode + digits[1..];

        return digits.Length is >= 9 and <= 15 ? digits : null;
    }

    private static string Describe(Exception ex) =>
        ex is SmtpException { InnerException: { } inner } ? $"{ex.Message} ({inner.Message})" : ex.Message;
}
