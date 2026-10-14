using FluentAssertions;
using MarocLIS.Domain.Orders;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class SubcontractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(1));

    private static LabOrderItem ReceivedSubcontracted()
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine)
        {
            IsSubcontracted = true,
            SubcontractorSiteId = Guid.NewGuid()
        };
        item.MarkCollected(Now.AddHours(-2));
        item.MarkReceived(Now.AddHours(-1), Now.AddDays(3));
        return item;
    }

    [Fact]
    public void Received_act_is_sent_once_on_a_slip_and_leaves_the_queue()
    {
        var item = ReceivedSubcontracted();
        var partner = Guid.NewGuid();
        var user = Guid.NewGuid();

        item.IsAwaitingSubcontractDispatch.Should().BeTrue();

        item.SendToSubcontractor(partner, "ST/2026/00001", Now, user).IsSuccess.Should().BeTrue();

        item.SubcontractorSiteId.Should().Be(partner);
        item.SubcontractDispatchNumber.Should().Be("ST/2026/00001");
        item.SentToSubcontractorAt.Should().Be(Now);
        item.SubcontractSentByUserId.Should().Be(user);
        item.IsAwaitingSubcontractDispatch.Should().BeFalse();

        item.SendToSubcontractor(partner, "ST/2026/00002", Now, user).Errors
            .Should().Contain(e => e.ResourceKey == "Subcontract_AlreadySent");
    }

    [Fact]
    public void Tube_not_received_or_act_not_subcontracted_cannot_be_sent()
    {
        var pending = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { IsSubcontracted = true };
        pending.SendToSubcontractor(Guid.NewGuid(), "ST/1", Now, Guid.NewGuid()).Errors
            .Should().Contain(e => e.ResourceKey == "Subcontract_NotReceived");

        var local = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        local.SendToSubcontractor(Guid.NewGuid(), "ST/1", Now, Guid.NewGuid()).Errors
            .Should().Contain(e => e.ResourceKey == "Subcontract_NotSubcontracted");
    }

    [Fact]
    public void Return_is_stamped_once_and_only_on_subcontracted_acts()
    {
        var item = ReceivedSubcontracted();
        item.MarkSubcontractReturned(Now);
        item.MarkSubcontractReturned(Now.AddHours(5));
        item.SubcontractReturnedAt.Should().Be(Now);

        var local = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);
        local.MarkSubcontractReturned(Now);
        local.SubcontractReturnedAt.Should().BeNull();
    }
}
