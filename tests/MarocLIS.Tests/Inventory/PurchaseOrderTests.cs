using FluentAssertions;
using MarocLIS.Domain.Inventory;
using Xunit;

namespace MarocLIS.Tests.Inventory;

/// <summary>Tests for the purchase-order life cycle and its totals.</summary>
public sealed class PurchaseOrderTests
{
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.FromHours(1));

    private static PurchaseOrder NewOrder() =>
        new("BC/2026/0001", Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 13), Requester);

    [Fact]
    public void Totals_ApplyDiscountThenVatPerLine()
    {
        var order = NewOrder();
        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 10m, 100m) { DiscountRate = 0.10m });
        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 1m, 50m) { VatRate = 0.07m });

        order.TotalExcludingVat.Should().Be(950m);
        order.TotalVat.Should().Be(183.50m);
        order.TotalIncludingVat.Should().Be(1133.50m);
    }

    [Fact]
    public void Approve_ByTheRequester_IsRefused()
    {
        var order = NewOrder();
        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 1m, 10m));

        order.Approve(Now, Requester).FirstError.ResourceKey.Should().Be("Po_ApproverMustDifferFromRequester");
    }

    [Fact]
    public void Approve_WithoutLines_IsRefused()
    {
        NewOrder().Approve(Now, Manager).FirstError.ResourceKey.Should().Be("Po_NoLines");
    }

    [Fact]
    public void ReceiveLine_PartThenRest_MovesToPartiallyReceivedThenReceived()
    {
        var order = NewOrder();
        var line = new PurchaseOrderLine(Guid.NewGuid(), 10m, 10m);
        order.AddLine(line);
        order.Approve(Now, Manager);
        order.Send(Now);

        order.ReceiveLine(line.Id, 4m).IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.PartiallyReceived);
        line.QuantityOutstanding.Should().Be(6m);

        order.ReceiveLine(line.Id, 6m).IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.Received);
    }

    [Fact]
    public void AddLine_AfterApproval_IsRefused()
    {
        var order = NewOrder();
        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 1m, 10m));
        order.Approve(Now, Manager);

        order.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 1m, 10m))
             .FirstError.ResourceKey.Should().Be("Po_NotEditable");
    }

    [Fact]
    public void Cancel_AfterAPartialDelivery_IsRefused()
    {
        var order = NewOrder();
        var line = new PurchaseOrderLine(Guid.NewGuid(), 10m, 10m);
        order.AddLine(line);
        order.Approve(Now, Manager);
        order.Send(Now);
        order.ReceiveLine(line.Id, 1m);

        order.Cancel("Erreur").FirstError.ResourceKey.Should().Be("Po_PartiallyReceivedCannotCancel");
    }
}
