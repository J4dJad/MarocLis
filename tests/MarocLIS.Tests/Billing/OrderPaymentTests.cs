using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Orders;
using Xunit;

namespace MarocLIS.Tests.Billing;

/// <summary>
/// The patient's payment status the « Comptes rendus » screen shows and warns on: read from the
/// invoice with the cashier's own patient-payable rule, « non facturé » while no issued invoice
/// bills the order, the payer's share left out, and the amounts withheld from a user without
/// billing.view.
/// </summary>
public sealed class OrderPaymentTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    [Fact]
    public void AnInvoicePaidInFull_IsPaid_AndAsksNothing()
    {
        var payment = OrderPayment.ForInvoice("FAC/2026/000001", InvoiceKind.Invoice, total: 450m, paid: 450m,
                                              credited: 0m, ClaimStatus.NotApplicable, payerShare: 0m,
                                              payerAccepted: null);

        payment.State.Should().Be(OrderPaymentState.Paid);
        payment.PatientDue.Should().Be(0m);
        payment.IsSettled.Should().BeTrue();
    }

    [Fact]
    public void APartPayment_LeavesTheRestDue()
    {
        var payment = OrderPayment.ForInvoice("FAC/2026/000002", InvoiceKind.Invoice, total: 450m, paid: 200m,
                                              credited: 0m, ClaimStatus.NotApplicable, payerShare: 0m,
                                              payerAccepted: null);

        payment.State.Should().Be(OrderPaymentState.PatientShareDue);
        payment.PatientDue.Should().Be(250m);
        payment.IsSettled.Should().BeFalse();
        payment.AuditNote("réimprimé").Should().StartWith("réimprimé avec part patient due : ")
               .And.Contain("FAC/2026/000002");
    }

    [Fact]
    public void ThePayerShareUnderClaim_IsNotThePatients_ToPay()
    {
        // 1 000 DH, 700 claimed from the CNSS, the patient's 300 paid: nothing more at the counter.
        var settled = OrderPayment.ForInvoice("FAC/2026/000003", InvoiceKind.Invoice, total: 1_000m, paid: 300m,
                                              credited: 0m, ClaimStatus.Submitted, payerShare: 700m,
                                              payerAccepted: null);

        settled.State.Should().Be(OrderPaymentState.Paid);
        settled.IsSettled.Should().BeTrue();

        // The payer answered for 500 only: the 200 it refused is the patient's again.
        var shortfall = OrderPayment.ForInvoice("FAC/2026/000003", InvoiceKind.Invoice, total: 1_000m, paid: 800m,
                                                credited: 0m, ClaimStatus.PartiallyAccepted, payerShare: 700m,
                                                payerAccepted: 500m);

        shortfall.PatientDue.Should().Be(200m);
    }

    [Fact]
    public void ACreditNote_ReducesWhatIsDue()
    {
        var payment = OrderPayment.ForInvoice("FAC/2026/000004", InvoiceKind.Invoice, total: 450m, paid: 300m,
                                              credited: 150m, ClaimStatus.NotApplicable, payerShare: 0m,
                                              payerAccepted: null);

        payment.State.Should().Be(OrderPaymentState.Paid);

        var partly = OrderPayment.ForInvoice("FAC/2026/000004", InvoiceKind.Invoice, total: 450m, paid: 100m,
                                             credited: 150m, ClaimStatus.NotApplicable, payerShare: 0m,
                                             payerAccepted: null);

        partly.PatientDue.Should().Be(200m);
    }

    [Fact]
    public void AnInvoiceCoveringSeveralOrders_GivesEachOrderItsBalance_AndSaysItIsShared()
    {
        var payment = OrderPayment.ForInvoice("FAC/2026/000005", InvoiceKind.Invoice, total: 900m, paid: 400m,
                                              credited: 0m, ClaimStatus.NotApplicable, payerShare: 0m,
                                              payerAccepted: null, coveredOrders: 3);

        payment.PatientDue.Should().Be(500m, "the Caisse collects the invoice as a whole");
        payment.CoveredOrders.Should().Be(3);
        payment.InvoiceNumber.Should().Be("FAC/2026/000005");
    }

    [Fact]
    public void TheStatus_AgreesWithTheInvoicesOwnPatientPayable()
    {
        var invoice = new Invoice("BROUILLON", Guid.NewGuid(), InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "ALAOUI Samir"
        };
        invoice.AddLine(new InvoiceLine("Glycémie", 80m)).IsSuccess.Should().BeTrue();
        invoice.AddLine(new InvoiceLine("NFS", 120m)).IsSuccess.Should().BeTrue();
        invoice.Issue("FAC/2026/000006", DateTimeOffset.Now, null).IsSuccess.Should().BeTrue();
        invoice.RecordPayment(new Payment(70m, PaymentMethod.Cash, Today, Guid.NewGuid(), invoice.SiteId))
               .IsSuccess.Should().BeTrue();

        var payment = OrderPayment.ForInvoice(invoice.Number, invoice.Kind, invoice.TotalAmount, invoice.PaidAmount,
                                              invoice.CreditedAmount, invoice.ClaimStatus, invoice.PayerShareAmount,
                                              invoice.PayerAcceptedAmount);

        payment.PatientDue.Should().Be(invoice.PatientPayableAmount).And.Be(130m);
    }

    [Fact]
    public void AnOrderOnNoInvoice_IsNotInvoiced_WithWhatThePatientWillOwe()
    {
        // No payer billed directly: the patient owes the whole charge, less the deposit taken.
        var cash = OrderPayment.ForUninvoiced(total: 450m, patientShare: 450m, payerBilledDirectly: false,
                                              depositHeld: 100m);

        cash.State.Should().Be(OrderPaymentState.NotInvoiced);
        cash.PatientDue.Should().Be(350m);
        cash.IsSettled.Should().BeFalse();
        cash.AuditNote("remis").Should().StartWith("remis avec dossier non facturé");

        // Billed directly to the payer: the patient's share alone.
        OrderPayment.ForUninvoiced(1_000m, 300m, payerBilledDirectly: true, depositHeld: 0m)
                    .PatientDue.Should().Be(300m);

        // The same figure the billing desk caps a deposit at.
        new BillableOrderRow(Guid.NewGuid(), "2610-000001", Guid.NewGuid(), "ALAOUI Samir", DateTimeOffset.Now,
                             "CNSS", Guid.NewGuid(), 1_000m, 700m, 300m, 2, LabOrderStatus.Validated,
                             DepositHeld: 50m, DirectBilling: true)
            .DepositCeiling.Should().Be(OrderPayment.ForUninvoiced(1_000m, 300m, true, 50m).PatientDue!.Value);
    }

    [Fact]
    public void AnOrderWithNothingBillable_AsksNothing()
    {
        var free = OrderPayment.ForUninvoiced(total: 0m, patientShare: 0m, payerBilledDirectly: false, depositHeld: 0m);

        free.State.Should().Be(OrderPaymentState.NothingDue);
        free.IsSettled.Should().BeTrue();
    }

    [Fact]
    public void WithoutBillingView_TheStatusStays_ButNotTheAmounts()
    {
        var due = OrderPayment.ForInvoice("FAC/2026/000007", InvoiceKind.Invoice, total: 900m, paid: 0m,
                                          credited: 0m, ClaimStatus.NotApplicable, payerShare: 0m,
                                          payerAccepted: null, coveredOrders: 2)
                              .WithoutAmounts();

        due.State.Should().Be(OrderPaymentState.PatientShareDue);
        due.IsSettled.Should().BeFalse();
        due.PatientDue.Should().BeNull();
        due.InvoiceNumber.Should().BeNull();
        due.CoveredOrders.Should().Be(1);
        due.AuditNote("réimprimé").Should().Be("réimprimé avec part patient non réglée");
    }
}
