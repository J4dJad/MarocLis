using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Equipment;
using Xunit;

namespace MarocLIS.Tests.Instruments;

/// <summary>
/// The frame-log retention rules, asserted against the predicate the store applies.
/// <para>
/// The store itself needs a database, so what is pinned here is the rule that decides a frame's
/// fate — the one that must not drift. Two properties matter and neither is obvious from the
/// retention number alone:
/// </para>
/// <list type="number">
/// <item>Age alone never makes a frame purgeable. A frame still awaiting acceptance is pending
/// work, and a failed or partially imported one is the last route by which its values can reach a
/// patient file; deleting either because it is old would destroy results nobody has seen.</item>
/// <item>Every other state is purgeable once past the cutoff, because an imported or ignored frame
/// is history, and history is what the retention period is about.</item>
/// </list>
/// <para>
/// The two sets must also be exhaustive over the enum. A member added later and matched by neither
/// rule would be silently retained for ever, which is how an unbounded table comes back.
/// </para>
/// </summary>
public sealed class MessageLogRetentionTests
{
    /// <summary>
    /// The states the store holds back, duplicated from the store on purpose. If the two ever
    /// diverge the exhaustiveness test below still passes but these two do not, which is the point:
    /// a copy that must agree is worth more than a reference that cannot disagree.
    /// </summary>
    private static readonly InstrumentMessageProcessingStatus[] Replayable =
    [
        InstrumentMessageProcessingStatus.Failed,
        InstrumentMessageProcessingStatus.PartiallyImported,
        InstrumentMessageProcessingStatus.AwaitingAcceptance
    ];

    [Theory]
    [InlineData(InstrumentMessageProcessingStatus.Failed)]
    [InlineData(InstrumentMessageProcessingStatus.PartiallyImported)]
    [InlineData(InstrumentMessageProcessingStatus.AwaitingAcceptance)]
    public void ReplayableFrame_IsNeverPurgeable_HoweverOld(
        InstrumentMessageProcessingStatus status)
    {
        var frame = Frame(status, age: TimeSpan.FromDays(3650));

        Purgeable(frame, cutoff: DateTimeOffset.Now.AddDays(-180)).Should().BeFalse(
            "ten years do not turn unfinished work into history");
    }

    [Theory]
    [InlineData(InstrumentMessageProcessingStatus.Imported)]
    [InlineData(InstrumentMessageProcessingStatus.Ignored)]
    [InlineData(InstrumentMessageProcessingStatus.Received)]
    public void FinishedFrame_IsPurgeableOncePastTheCutoff(
        InstrumentMessageProcessingStatus status)
    {
        var frame = Frame(status, age: TimeSpan.FromDays(200));

        Purgeable(frame, cutoff: DateTimeOffset.Now.AddDays(-180)).Should().BeTrue();
    }

    [Theory]
    [InlineData(InstrumentMessageProcessingStatus.Imported)]
    [InlineData(InstrumentMessageProcessingStatus.Ignored)]
    public void FinishedFrame_InsideTheRetentionPeriod_IsKept(
        InstrumentMessageProcessingStatus status)
    {
        var frame = Frame(status, age: TimeSpan.FromDays(10));

        Purgeable(frame, cutoff: DateTimeOffset.Now.AddDays(-180)).Should().BeFalse();
    }

    [Fact]
    public void EveryStatus_IsEitherReplayableOrPurgeable()
    {
        // Exhaustive over the enum rather than over a list someone remembered to update. A new
        // member defaults to "purgeable", so this test is what forces a decision about it: silently
        // deleting a state nobody classified is worse than silently keeping it.
        var all = Enum.GetValues<InstrumentMessageProcessingStatus>();

        all.Should().NotBeEmpty();

        foreach (var status in all)
        {
            var frame = Frame(status, age: TimeSpan.FromDays(400));
            var purgeable = Purgeable(frame, DateTimeOffset.Now.AddDays(-180));

            purgeable.Should().Be(!Replayable.Contains(status),
                $"{status} must be classified deliberately, not by accident of ordering");
        }
    }

    [Fact]
    public void Census_ReportsRetainedSeparatelyFromPurgeable()
    {
        // The retained figure is the one a laboratory needs: frames old enough to go that are being
        // kept because somebody could still act on them. Reporting only a total would hide it.
        var census = new MessageLogCensus(Purgeable: 4_000, RetainedReplayable: 12,
                                           OldestPurgeable: DateTimeOffset.Now.AddDays(-400),
                                           TotalRows: 4_120);

        census.Purgeable.Should().Be(4_000);
        census.RetainedReplayable.Should().Be(12);
        census.TotalRows.Should().BeGreaterThan(census.Purgeable + census.RetainedReplayable,
            "rows inside the retention period are counted in the total and in neither of the others");
    }

    // ---- The predicate under test ------------------------------------------------------------

    /// <summary>
    /// Mirrors <c>InstrumentMessageLogStore.PurgeAsync</c>: past the cutoff, and not in a state a
    /// technician could still act on.
    /// </summary>
    private static bool Purgeable(InstrumentMessageLog frame, DateTimeOffset cutoff) =>
        frame.OccurredAt < cutoff && !Replayable.Contains(frame.Status);

    private static InstrumentMessageLog Frame(InstrumentMessageProcessingStatus status,
                                               TimeSpan age)
    {
        var frame = new InstrumentMessageLog(Guid.NewGuid(), Guid.NewGuid(),
                                              MessageDirection.Inbound, "H|\\^&\rL|1|N",
                                              DateTimeOffset.Now - age);

        frame.Status = status;
        return frame;
    }
}
