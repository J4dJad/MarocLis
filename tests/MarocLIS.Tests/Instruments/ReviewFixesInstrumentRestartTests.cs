using System.Text;
using FluentAssertions;
using MarocLIS.Domain.Equipment;
using MarocLIS.Instruments.Hosting;
using MarocLIS.Instruments.Transport;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// U6: a link whose driver failed is restarted after a growing wait, and the file-drop transport
/// treats an unreachable outbound folder as a dropped line instead of ending the driver.
/// </summary>
public sealed class ReviewFixesInstrumentRestartTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(5, 480)]
    [InlineData(6, 600)]
    [InlineData(50, 600)]
    public void Wait_doubles_up_to_ten_minutes(int failures, int seconds) =>
        LinkRestartPolicy.DelayAfter(failures).Should().Be(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Failed_link_is_due_again_once_its_wait_has_run_out()
    {
        var policy = new LinkRestartPolicy();
        var link = Guid.NewGuid();

        policy.IsDue(link, T0).Should().BeTrue("a link that never failed has nothing to wait for");

        policy.RecordFailure(link, startedAt: T0, endedAt: T0.AddSeconds(5));

        policy.IsDue(link, T0.AddSeconds(20)).Should().BeFalse();
        policy.IsDue(link, T0.AddSeconds(35)).Should().BeTrue("it was left dead for good before");
    }

    [Fact]
    public void Failures_in_a_row_wait_longer_and_a_long_run_starts_again_from_the_first_wait()
    {
        var policy = new LinkRestartPolicy();
        var link = Guid.NewGuid();

        policy.RecordFailure(link, T0, T0.AddSeconds(1));
        policy.RecordFailure(link, T0.AddSeconds(31), T0.AddSeconds(32));

        policy.IsDue(link, T0.AddSeconds(32 + 59)).Should().BeFalse();
        policy.IsDue(link, T0.AddSeconds(32 + 60)).Should().BeTrue();

        // Ran an hour before failing again: the link was working, the slate is clean.
        var later = T0.AddHours(2);
        policy.RecordFailure(link, later.AddHours(-1), later);
        policy.IsDue(link, later.AddSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void Forgotten_link_is_due_at_once()
    {
        var policy = new LinkRestartPolicy();
        var link = Guid.NewGuid();

        policy.RecordFailure(link, T0, T0);
        policy.Forget(link);

        policy.IsDue(link, T0).Should().BeTrue();
    }

    [Fact]
    public async Task Unreachable_outbound_folder_drops_the_line_without_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), "maroclis-u6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // A file where the folder should be: creating the folder fails as an unmounted share does.
            var blocker = Path.Combine(root, "share");
            await File.WriteAllTextAsync(blocker, "x");

            var link = new InstrumentInterface(Guid.NewGuid(), Guid.NewGuid(), InstrumentProtocol.Hl7V2,
                                               InstrumentTransportKind.FileDrop)
            {
                InboundDirectory = root,
                OutboundDirectory = Path.Combine(blocker, "out")
            };

            var transport = new FileDropInstrumentTransport(link);
            (await transport.ConnectAsync()).Should().BeTrue();

            string? dropped = null;
            transport.Disconnected += (_, reason) => dropped = reason;

            await transport.Invoking(t => t.WriteAsync(Encoding.ASCII.GetBytes("MSH|^~\\&|ACK")))
                           .Should().NotThrowAsync("an exception here ended the driver and the link");

            transport.IsConnected.Should().BeFalse("the driver reconnects a dropped line on its own");
            dropped.Should().NotBeNull();

            (await transport.ConnectAsync()).Should().BeTrue("the inbound folder is still there");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
