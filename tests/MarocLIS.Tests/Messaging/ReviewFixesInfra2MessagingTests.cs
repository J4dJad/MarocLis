using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Infrastructure.Messaging;
using Xunit;

namespace MarocLIS.Tests.Messaging;

/// <summary>
/// The SMTP time limit: SmtpClient.Timeout does not apply to SendMailAsync, so a server that
/// accepts the connection and then says nothing used to hold the send forever.
/// </summary>
public sealed class ReviewFixesInfra2MessagingTests
{
    [Fact]
    public async Task SilentSmtpServer_FailsAtTheConfiguredTimeout()
    {
        // Accepts the connection and never sends its greeting.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        var sender = new MessageSender(new MessagingOptions
        {
            Smtp = new SmtpOptions
            {
                Host = "127.0.0.1", Port = port, EnableSsl = false,
                FromAddress = "labo@example.ma", TimeoutSeconds = 1
            }
        });

        var clock = Stopwatch.StartNew();
        var result = await sender.SendEmailAsync(
            new EmailMessage("patient@example.ma", "Compte rendu", "Bonjour", []))
            .WaitAsync(TimeSpan.FromSeconds(20));

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Messaging_EmailFailed");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

        (await accepted).Dispose();
        listener.Stop();
    }

    [Fact]
    public async Task CallerCancellation_StillSurfacesAsCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        var sender = new MessageSender(new MessagingOptions
        {
            Smtp = new SmtpOptions
            {
                Host = "127.0.0.1", Port = port, EnableSsl = false,
                FromAddress = "labo@example.ma", TimeoutSeconds = 30
            }
        });

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var send = () => sender.SendEmailAsync(
            new EmailMessage("patient@example.ma", "Compte rendu", "Bonjour", []), cancel.Token);

        await send.Should().ThrowAsync<OperationCanceledException>();

        (await accepted).Dispose();
        listener.Stop();
    }
}
