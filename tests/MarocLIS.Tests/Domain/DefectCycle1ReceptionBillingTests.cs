using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesBilling2Tests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Defect-hunt cycle 1, reception and billing (lot c1a): the till, the claims and their
/// resubmission, and the amendments of a file after registration.
/// </summary>
public sealed class DefectCycle1ReceptionBillingTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(1));

    // ---- An analysis is repeated only once its tube reached the bench ------------------------------

    [Theory]
    [InlineData(LabOrderItemStatus.Pending)]
    [InlineData(LabOrderItemStatus.Collected)]
    public void AnAnalysisWhoseTubeNeverReachedTheBench_CannotBeRepeated(LabOrderItemStatus status)
    {
        var item = ItemAt(status);

        var repeated = item.Repeat("Valeur douteuse", Now);

        repeated.FirstError.ResourceKey.Should().Be("Item_CannotRepeatFromStatus");
        item.Status.Should().Be(status);
        item.RepeatedAt.Should().BeNull();
    }

    [Fact]
    public void AnAnalysisOnTheBench_IsRepeated_AndTheScreenOffersIt()
    {
        var item = ItemAt(LabOrderItemStatus.ResultEntered);

        item.Repeat("Valeur douteuse", Now).IsSuccess.Should().BeTrue();
        item.Status.Should().Be(LabOrderItemStatus.Received);

        static bool Offered(LabOrderItemStatus status) =>
            new Application.Orders.OrderItemDetail(
                Guid.NewGuid(), Guid.NewGuid(), "GLU", new MultilingualText("Glycémie"), "BIOCH", status,
                OrderPriority.Routine, 100m, 70m, 30m, null, null, null, null, null, null, null, 0, null, null,
                null, false, 1, 1).CanRepeat;

        Offered(LabOrderItemStatus.Pending).Should().BeFalse();
        Offered(LabOrderItemStatus.Collected).Should().BeFalse();
        Offered(LabOrderItemStatus.InProgress).Should().BeTrue();
        Offered(LabOrderItemStatus.TechnicallyValidated).Should().BeTrue();
    }

    private static LabOrderItem ItemAt(LabOrderItemStatus status)
    {
        var item = new LabOrderItem(Guid.NewGuid(), OrderPriority.Routine);

        if (status >= LabOrderItemStatus.Collected) item.MarkCollected(Now).IsSuccess.Should().BeTrue();
        if (status >= LabOrderItemStatus.Received) item.MarkReceived(Now, null).IsSuccess.Should().BeTrue();
        if (status >= LabOrderItemStatus.InProgress) item.MarkInProgress(Now).IsSuccess.Should().BeTrue();
        if (status >= LabOrderItemStatus.ResultEntered) item.MarkResultEntered(Now).IsSuccess.Should().BeTrue();

        item.Status.Should().Be(status);
        return item;
    }

    // ---- A refused share the patient paid is not claimed from the payer again -----------------------

    [Fact]
    public async Task ARejectedClaim_WhoseShortfallThePatientPaid_IsNotResubmitted_UntilThePatientIsRefunded()
    {
        var invoice = IssuedInvoice(1_000m, payerRate: 0.70m);
        var harness = new BillingHarness(invoice);

        // The patient pays their 300 at the counter; the claim goes out.
        (await harness.Service.RecordPaymentAsync(
            new RecordPaymentRequest(invoice.Id, 300m, PaymentMethod.Card, "AUT-1", null, null, null)))
            .IsSuccess.Should().BeTrue();
        invoice.ClaimStatus = ClaimStatus.Submitted;

        // The payer refuses everything; the refusal is left to the patient, who pays it too.
        (await harness.Service.RecordPayerSettlementAsync(
                invoice.Id, 0m, PayerShortfallHandling.PatientPays, "Ordonnance illisible", null))
            .IsSuccess.Should().BeTrue();
        invoice.ClaimStatus.Should().Be(ClaimStatus.Rejected);

        (await harness.Service.RecordPaymentAsync(
            new RecordPaymentRequest(invoice.Id, 700m, PaymentMethod.Card, "AUT-2", null, null, null)))
            .IsSuccess.Should().BeTrue();
        invoice.OutstandingAmount.Should().Be(0m);

        // Sent again, the payer's 700 could never be entered: the invoice is already paid in full.
        var resubmitted = await harness.Service.SubmitClaimBatchAsync([invoice.Id]);

        resubmitted.IsFailure.Should().BeTrue();
        resubmitted.FirstError.ResourceKey.Should().Be("Claim_ShortfallPaidByPatient");
        invoice.ClaimStatus.Should().Be(ClaimStatus.Rejected);
        invoice.PayerAcceptedAmount.Should().Be(0m);
        invoice.PatientPayableAmount.Should().Be(0m);

        // Once the patient is refunded, the claim goes out again and the payer's payment fits.
        var paid = invoice.Payments.Single(p => p.Amount == 700m);
        (await harness.Service.RefundPaymentAsync(invoice.Id, paid.Id, "Renvoi du dossier à la CNSS"))
            .IsSuccess.Should().BeTrue();

        (await harness.Service.SubmitClaimBatchAsync([invoice.Id])).IsSuccess.Should().BeTrue();
        invoice.ClaimStatus.Should().Be(ClaimStatus.Submitted);
        invoice.PatientPayableAmount.Should().Be(0m);

        (await harness.Service.RecordPayerSettlementAsync(
                invoice.Id, 700m, PayerShortfallHandling.PatientPays, null, "VIR-2026-12"))
            .IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task ARejectedClaim_WrittenOff_IsStillResubmitted()
    {
        var invoice = IssuedInvoice(400m, payerRate: 0.75m);
        var harness = new BillingHarness(invoice);
        invoice.ClaimStatus = ClaimStatus.Submitted;

        (await harness.Service.RecordPayerSettlementAsync(
                invoice.Id, 0m, PayerShortfallHandling.WriteOff, "Prescription illisible", null))
            .IsSuccess.Should().BeTrue();

        (await harness.Service.SubmitClaimBatchAsync([invoice.Id])).IsSuccess.Should().BeTrue();
        invoice.ClaimStatus.Should().Be(ClaimStatus.Submitted);
        invoice.OutstandingAmount.Should().Be(400m);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static Invoice IssuedInvoice(decimal price, decimal payerRate)
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), InvoiceKind.Invoice, Today, Guid.NewGuid())
        {
            BilledToName = "CNSS",
            PayerId = Guid.NewGuid()
        };

        var line = new InvoiceLine("Analyse", price);
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        line.SetCoverageSplit(payerRate, payerUsesReferenceTariff: false);
        invoice.Issue("FAC/PRINCIPAL/2026/000300", Now, null).IsSuccess.Should().BeTrue();
        invoice.ClaimStatus = ClaimStatus.Prepared;
        return invoice;
    }

    /// <summary>A billing service over one invoice, an open till, and everything else stubbed.</summary>
    private sealed class BillingHarness
    {
        public BillingHarness(Invoice invoice)
        {
            var user = Guid.NewGuid();
            var till = new CashSession(invoice.SiteId, user, Now.AddHours(-2), 200m);

            Service = new BillingService(
                Stub<IRepository<Invoice>>.With(
                    ("GetByIdWithDetailsAsync", _ => Task.FromResult<Invoice?>(invoice)),
                    ("GetByIdAsync", _ => Task.FromResult<Invoice?>(invoice))),
                Stub<IRepository<LabOrder>>.Create(),
                Stub<IRepository<Patient>>.Create(),
                Stub<IRepository<Payer>>.Create(),
                Stub<IRepository<LabSite>>.Create(),
                Stub<IRepository<AppUser>>.Create(),
                Stub<IRepository<CashSession>>.With(
                    ("GetByIdAsync", _ => Task.FromResult<CashSession?>(till))),
                Stub<IRepository<Deposit>>.Create(),
                Stub<IBillingQueries>.With(
                    ("GetOpenCashSessionAsync", _ => Task.FromResult<CashSession?>(till))),
                Stub<ISequenceAllocator>.With(
                    ("NextAsync", _ => Task.FromResult(Result.Success("TP/PRINCIPAL/2026/000004")))),
                Stub<IUnitOfWork>.Create(),
                Stub<IPermissionGuard>.Create(),
                Stub<ICurrentUser>.With(
                    ("get_UserId", _ => user),
                    ("get_SiteId", _ => invoice.SiteId)),
                Stub<IClock>.With(
                    ("get_Now", _ => Now),
                    ("get_Today", _ => Today)));
        }

        public BillingService Service { get; }
    }
}
