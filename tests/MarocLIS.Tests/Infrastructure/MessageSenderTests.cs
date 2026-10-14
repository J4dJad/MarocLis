using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Infrastructure.Messaging;
using Xunit;

namespace MarocLIS.Tests.Infrastructure;

public sealed class MessageSenderTests
{
    [Theory]
    [InlineData("06 12 34 56 78", "212612345678")]
    [InlineData("+212 6 12 34 56 78", "212612345678")]
    [InlineData("00212612345678", "212612345678")]
    [InlineData("0522-00-00-00", "212522000000")]
    public void Moroccan_numbers_are_normalised_to_the_international_form(string input, string expected) =>
        MessageSender.NormalisePhone(input, "212").Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    public void Unusable_numbers_are_refused(string? input) =>
        MessageSender.NormalisePhone(input, "212").Should().BeNull();

    [Fact]
    public async Task Unconfigured_channels_refuse_without_trying()
    {
        var sender = new MessageSender(new MessagingOptions());

        sender.CanSendEmail.Should().BeFalse();
        sender.CanSendSms.Should().BeFalse();

        (await sender.SendEmailAsync(new EmailMessage("a@b.ma", "s", "b", []))).Errors
            .Should().Contain(e => e.ResourceKey == "Messaging_EmailNotConfigured");
        (await sender.SendSmsAsync("0612345678", "x")).Errors
            .Should().Contain(e => e.ResourceKey == "Messaging_SmsNotConfigured");
    }
}
