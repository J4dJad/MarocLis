using FluentAssertions;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;
using Xunit;

namespace MarocLIS.Tests.Domain;

public sealed class RecollectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(1));

    private static (LabOrder Order, LabOrderItem Item) SingleTubeOrder()
    {
        var order = new LabOrder("2610-000099", Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(-2), Guid.NewGuid());
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine) { ChargedPrice = 80m, SampleId = Guid.NewGuid() };
        order.AddItem(item);
        item.MarkCollected(Now.AddHours(-1));
        item.MarkReceived(Now.AddMinutes(-30), Now.AddDays(1));
        order.RecomputeStatus();
        return (order, item);
    }

    [Fact]
    public void Replacing_the_only_tube_keeps_the_order_open()
    {
        var (order, item) = SingleTubeOrder();
        var replacement = new LabOrderItem(item.AnalysisTestId, OrderPriority.Routine)
        {
            ChargedPrice = item.ChargedPrice,
            SampleId = Guid.NewGuid()
        };

        order.ReplaceItems([(item.Id, replacement)], Now, Guid.NewGuid(), "Échantillon refusé : Clotted")
             .IsSuccess.Should().BeTrue();

        item.IsCancelled.Should().BeTrue();
        order.Status.Should().NotBe(LabOrderStatus.Cancelled);
        order.Items.Should().Contain(replacement);
        replacement.LabOrderId.Should().Be(order.Id);

        // Billed once: the cancelled item drops out, the replacement carries the price.
        order.Items.Where(i => !i.IsCancelled).Sum(i => i.ChargedPrice).Should().Be(80m);
    }

    [Fact]
    public void Plain_cancellation_of_the_only_item_still_closes_the_order()
    {
        var (order, item) = SingleTubeOrder();

        order.CancelItem(item.Id, Now, Guid.NewGuid(), "Échantillon refusé").IsSuccess.Should().BeTrue();

        order.Status.Should().Be(LabOrderStatus.Cancelled);
    }

    [Fact]
    public void A_tube_cannot_be_refused_twice()
    {
        var sample = new Sample("0000000002", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        sample.Collect(Now, Guid.NewGuid(), 5m, 2m);

        sample.Reject(Now, Guid.NewGuid(), SampleRejectionReason.Clotted, null, true).IsSuccess.Should().BeTrue();

        sample.Reject(Now.AddMinutes(5), Guid.NewGuid(), SampleRejectionReason.Clotted, null, true)
              .FirstError.ResourceKey.Should().Be("Sample_AlreadyRejected");
    }

    [Fact]
    public void A_received_tube_is_not_received_again_but_can_still_be_refused()
    {
        var sample = new Sample("0000000003", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var user = Guid.NewGuid();
        sample.Collect(Now, user, 5m, 2m);
        sample.Receive(Now.AddMinutes(30), user, SampleDefect.None, false, null, null).IsSuccess.Should().BeTrue();

        sample.Receive(Now.AddMinutes(40), user, SampleDefect.None, false, null, null)
              .FirstError.ResourceKey.Should().Be("Sample_CannotReceiveFromStatus");

        sample.Reject(Now.AddHours(1), user, SampleRejectionReason.Clotted, "Caillot après centrifugation", true)
              .IsSuccess.Should().BeTrue();
    }
}
