using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The owner's decisions on the billing items (cycles 1 to 4), at the domain and row level: the
/// shift report's mobile and bill-of-exchange lines, the payer-due rule shared with the ledger row,
/// the refundable payments, and an analysis repeated only once it has a value.
/// </summary>
public sealed class RecoRbBillingTests
{
    [Fact]
    public void MobilePayments_AndBillsOfExchange_AreCountedOnTheirOwnLines_AndReversedThere()
    {
        var session = new CashSession(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now, 100m);

        session.Accumulate(PaymentMethod.MobilePayment, 30m);
        session.Accumulate(PaymentMethod.BillOfExchange, 15m);
        session.Accumulate(PaymentMethod.Cash, 20m);
        session.ReverseNonCash(PaymentMethod.MobilePayment, 10m);

        session.MobileReceipts.Should().Be(20m);
        session.BillOfExchangeReceipts.Should().Be(15m);
        session.TotalReceipts.Should().Be(55m);
        session.ExpectedClosingCash.Should().Be(120m);

        var summary = CashSessionSummary.From(session, "Caissière", 3);
        summary.MobileReceipts.Should().Be(20m);
        summary.BillOfExchangeReceipts.Should().Be(15m);
    }

    [Theory]
    [InlineData(1000, 700, 0, 700)]     // no credit note
    [InlineData(1000, 700, 100, 700)]   // within the patient's share
    [InlineData(1000, 700, 500, 500)]   // 300 off the patient, 200 off the payer
    [InlineData(1000, 700, 1000, 0)]    // reversed in full
    public void ThePayerDue_OfTheLedgerRow_IsTheInvoicesRule(decimal total, decimal payerShare, decimal credited, decimal due)
    {
        Invoice.ComputePayerDue(total, payerShare, credited).Should().Be(due);

        var row = new InvoiceRow(Guid.NewGuid(), "FAC/1", InvoiceKind.Invoice, InvoiceStatus.Issued,
                                 new DateOnly(2026, 10, 1), null, "CNSS", total, 0m, total - credited, 0,
                                 ClaimStatus.Submitted, null, payerShare, null, credited);
        row.PayerDue.Should().Be(due);
    }

    [Theory]
    [InlineData(PaymentMethod.Cash, true)]
    [InlineData(PaymentMethod.Cheque, true)]
    [InlineData(PaymentMethod.Card, true)]
    [InlineData(PaymentMethod.MobilePayment, true)]
    [InlineData(PaymentMethod.BillOfExchange, true)]
    [InlineData(PaymentMethod.ThirdPartyPayer, false)]
    [InlineData(PaymentMethod.WriteOff, false)]
    public void OnlyWhatThePatientPaid_IsOfferedForARefund(PaymentMethod method, bool offered)
    {
        Row(method, PaymentStatus.Settled).CanRefund.Should().Be(offered);
        Row(method, PaymentStatus.Refunded).CanRefund.Should().BeFalse();
    }

    private static PaymentRow Row(PaymentMethod method, PaymentStatus status) =>
        new(Guid.NewGuid(), 50m, method, status, new DateOnly(2026, 10, 1), "Caissière", "REF", null, null);

    [Fact]
    public void AnAnalysisOnTheBench_IsOfferedForARepeat_OnlyOnceAValueIsEntered()
    {
        static OrderItemDetail Item(int entered) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "GLU", new MultilingualText("Glycémie"), "BIOCH",
                LabOrderItemStatus.Received, OrderPriority.Routine, 100m, 70m, 30m, null, null, null, null, null,
                null, null, 0, null, null, null, false, 1, entered);

        Item(0).CanRepeat.Should().BeFalse();
        Item(1).CanRepeat.Should().BeTrue();
    }
}
