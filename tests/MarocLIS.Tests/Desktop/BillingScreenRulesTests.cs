using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Tests.Licensing;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The rules of « Facturation », « Caisse » and « Tiers payant », moved out of the workstation's
/// views and dialogs, now shared with the web.
/// </summary>
public sealed class BillingScreenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(1));
    private static readonly Guid Patient = Guid.NewGuid();
    private static readonly Guid Cnops = Guid.NewGuid();

    internal static BillableOrderRow Order(string accession = "2610-000021", Guid? patient = null, Guid? payer = null,
                                           bool direct = true, LabOrderStatus status = LabOrderStatus.Validated,
                                           Guid? invoice = null, decimal deposit = 0m, bool again = false, bool agreement = true) =>
        new(Guid.NewGuid(), accession, patient ?? Patient, "ALAOUI Fatima Zahra", Now, payer is null ? null : "CNOPS", payer,
            400m, payer is null ? 0m : 320m, payer is null ? 400m : 80m, 3, status, deposit, direct, invoice,
            invoice is null ? null : "FA-2026-000031", again, agreement);

    internal static InvoiceRow Invoice(InvoiceKind kind = InvoiceKind.Invoice, decimal total = 400m, decimal paid = 0m,
                                       decimal outstanding = 400m, ClaimStatus claim = ClaimStatus.NotApplicable,
                                       decimal payerShare = 0m, decimal? accepted = null, decimal credited = 0m) =>
        new(Guid.NewGuid(), "FA-2026-000031", kind, InvoiceStatus.Issued, new DateOnly(2026, 10, 20), null, "ALAOUI Fatima Zahra",
            total, paid, outstanding, 0, claim, "2610-000021", payerShare, accepted, credited);

    [Fact]
    public void The_buttons_follow_the_rights_and_the_coded_print_is_an_invoices()
    {
        var counter = new LicenceTestKit.User(true, Permissions.Billing.View, Permissions.Billing.RecordPayment);
        var accounts = new LicenceTestKit.User(true, Permissions.Billing.View, Permissions.Billing.CreateInvoice,
                                               Permissions.Billing.CreditNote, Permissions.Billing.DeclareUnpaid,
                                               Permissions.Billing.ExportAccounting);

        BillingScreenRules.Actions(counter, null).Should().Be(
            new BillingActions(false, true, false, true, false, false, false, false, false, false, false, false));
        BillingScreenRules.Actions(accounts, Invoice()).Should().Be(
            new BillingActions(true, false, true, false, true, true, true, true, true, true, true, true));
        BillingScreenRules.Actions(accounts, Invoice(InvoiceKind.CreditNote)).PrintCotation.Should().BeFalse();

        var one = Invoice();
        var current = Invoice();
        BillingScreenRules.PrintTarget([one], current).Should().Be(one);
        BillingScreenRules.PrintTarget([], current).Should().Be(current);
        BillingScreenRules.PrintTarget([one, current], current).Should().BeNull("which of two ticked invoices was meant is not guessed");
    }

    [Fact]
    public void An_invoice_takes_one_patients_requests_with_one_payer_none_invoiced_or_cancelled()
    {
        BillingScreenRules.IssueRefusal([])!.IsInformation.Should().BeTrue();
        BillingScreenRules.IssueRefusal([Order(), Order(patient: Guid.NewGuid())])!.Key.Should().Be("Invoice_MultiplePatients");
        BillingScreenRules.IssueRefusal([Order(), Order("2610-000022", invoice: Guid.NewGuid())])
                          .Should().BeEquivalentTo(new BillingRefusal("Invoice_OrderAlreadyInvoiced", false, "2610-000022"));
        BillingScreenRules.IssueRefusal([Order(status: LabOrderStatus.Cancelled)])!.Key.Should().Be("Invoice_OrderCancelled");
        BillingScreenRules.IssueRefusal([Order(payer: Cnops), Order("2610-000022")])!
                          .Should().BeEquivalentTo(new BillingRefusal("Invoice_MultiplePayers", false, "2610-000021, 2610-000022"));
        BillingScreenRules.IssueRefusal([Order(payer: Cnops), Order("2610-000022", payer: Cnops)]).Should().BeNull();
        BillingScreenRules.Gross([Order(), Order()]).Amount.Should().Be(800m);
    }

    [Fact]
    public void An_order_invoiced_again_to_the_patient_is_offered_to_its_payer_under_an_agreement()
    {
        BillingScreenRules.ReinvoicePayer([Order(payer: Cnops, direct: false, again: true)]).Should().Be(Cnops);
        BillingScreenRules.ReinvoicePayer([Order(payer: Cnops, direct: false)]).Should().BeNull();
        BillingScreenRules.ReinvoicePayer([Order(payer: Cnops, direct: false, again: true, agreement: false)]).Should().BeNull();
        BillingScreenRules.ReinvoicePayer([Order(payer: Cnops, direct: true, again: true)]).Should().BeNull();
    }

    [Fact]
    public void A_deposit_is_refused_on_a_cancelled_invoiced_or_covered_request()
    {
        BillingScreenRules.DepositRefusal(Order(status: LabOrderStatus.Cancelled))!.Key.Should().Be("Deposit_OrderCancelled");
        BillingScreenRules.DepositRefusal(Order(invoice: Guid.NewGuid()))!.Key.Should().Be("Invoice_OrderAlreadyInvoiced");
        BillingScreenRules.DepositRefusal(Order(payer: Cnops, deposit: 80m))
                          .Should().BeEquivalentTo(new BillingRefusal("Deposit.FullyCovered", IsInformation: true));
        BillingScreenRules.DepositRefusal(Order(payer: Cnops, deposit: 30m)).Should().BeNull();

        BillingScreenRules.HeldDeposits(
        [
            new DepositRow(Guid.NewGuid(), "AC-1", Now, 50m, 50m, PaymentMethod.Cash, DepositStatus.Held),
            new DepositRow(Guid.NewGuid(), "AC-2", Now, 50m, 0m, PaymentMethod.Cash, DepositStatus.Held),
            new DepositRow(Guid.NewGuid(), "AC-3", Now, 50m, 50m, PaymentMethod.Cash, DepositStatus.Refunded)
        ]).Select(d => d.Number).Should().Equal("AC-1");
    }

    [Fact]
    public void A_payment_takes_the_patients_part_and_the_reference_its_method_needs()
    {
        BillingScreenRules.PaymentBlockOf(Invoice(outstanding: 0m, paid: 400m)).Should().Be(PaymentBlock.Settled);
        BillingScreenRules.PaymentBlockOf(Invoice(outstanding: 320m, paid: 80m, claim: ClaimStatus.Submitted, payerShare: 320m))
                          .Should().Be(PaymentBlock.PayerShareOnly);
        BillingScreenRules.PaymentBlockOf(Invoice()).Should().Be(PaymentBlock.None);
        BillingScreenRules.PaymentCeilingKey(Invoice(claim: ClaimStatus.Submitted, payerShare: 320m)).Should().Be("Payment.PatientShareDue");

        BillingScreenRules.FieldsFor(PaymentMethod.Cash).Should().Be(new PaymentFields(false, false, false));
        BillingScreenRules.FieldsFor(PaymentMethod.Card).Should().Be(new PaymentFields(true, false, false));
        BillingScreenRules.FieldsFor(PaymentMethod.Cheque).Should().Be(new PaymentFields(true, true, true));
        BillingScreenRules.WarnsCash(PaymentMethod.Cash, "25 000,00").Should().BeTrue();
        BillingScreenRules.WarnsCash(PaymentMethod.Card, "25 000,00").Should().BeFalse();

        BillingScreenRules.CheckPayment("12a", 400m, false, PaymentMethod.Cash, null)
                          .Should().Be(new FieldRefusal("amount", "Result_NotANumber", "12a"));
        BillingScreenRules.CheckPayment("0", 400m, false, PaymentMethod.Cash, null)!.Key.Should().Be("Payment_AmountMustBePositive");
        BillingScreenRules.CheckPayment("1 500,00", 400m, true, PaymentMethod.Cash, null)
                          .Should().Be(new FieldRefusal("amount", "Deposit_ExceedsOrderTotal", Amount: 400m));
        BillingScreenRules.CheckPayment("400", 400m, false, PaymentMethod.Cheque, " ")!.Key.Should().Be("Payment_ReferenceRequired");
        BillingScreenRules.CheckPayment("400", 400m, false, PaymentMethod.BankTransfer, null)!.Key
                          .Should().Be("Payment_TransferReferenceRequired");
        BillingScreenRules.CheckPayment("400", 400m, false, PaymentMethod.Card, null).Should().BeNull();
    }

    [Fact]
    public void Corrections_pick_what_they_apply_to_and_a_credit_note_is_refused_where_it_cannot_lawfully_go()
    {
        PaymentRow Paid(PaymentMethod method, PaymentStatus status = PaymentStatus.Settled) =>
            new(Guid.NewGuid(), 100m, method, status, new DateOnly(2026, 10, 20), "Nadia", null, null, null);
        PaymentRow[] payments = [Paid(PaymentMethod.Cash), Paid(PaymentMethod.Cheque), Paid(PaymentMethod.ThirdPartyPayer),
                                 Paid(PaymentMethod.Cheque, PaymentStatus.Refunded)];

        BillingScreenRules.CorrectionCandidates(payments, bounce: true).Select(p => p.Method).Should().Equal(PaymentMethod.Cheque);
        BillingScreenRules.CorrectionCandidates(payments, bounce: false).Select(p => p.Method)
                          .Should().Equal(PaymentMethod.Cash, PaymentMethod.Cheque);

        BillingScreenRules.CreditNoteRefusal(Invoice(InvoiceKind.CreditNote))!.Key.Should().Be("Invoice_CannotCreditACreditNote");
        BillingScreenRules.CreditNoteRefusal(Invoice(credited: 400m))!.Key.Should().Be("Invoice_AlreadyFullyCredited");
        BillingScreenRules.CreditNoteRefusal(Invoice(claim: ClaimStatus.Submitted, payerShare: 320m))!.Key
                          .Should().Be("Invoice_CreditNoteClaimSent");
        BillingScreenRules.CreditNoteRefusal(Invoice(claim: ClaimStatus.Prepared, payerShare: 320m)).Should().BeNull();
    }

    [Fact]
    public void The_payers_answer_and_settlement_are_checked_before_they_are_recorded()
    {
        BillingScreenRules.Claimable([Invoice(claim: ClaimStatus.Prepared), Invoice(claim: ClaimStatus.Submitted),
                                      Invoice(claim: ClaimStatus.Rejected)]).Should().HaveCount(2);
        BillingScreenRules.Submitted([Invoice(claim: ClaimStatus.Prepared), Invoice(claim: ClaimStatus.Submitted)])
                          .Should().ContainSingle();
        BillingScreenRules.ClaimOutcomes.Should().NotContain(ClaimStatus.Settled);
        BillingScreenRules.CheckClaimOutcome(ClaimStatus.Rejected, "")!.Key.Should().Be("Claim_RejectionReasonRequired");
        BillingScreenRules.CheckClaimOutcome(ClaimStatus.Accepted, "").Should().BeNull();

        var sent = Invoice(claim: ClaimStatus.Submitted, payerShare: 320m);
        BillingScreenRules.SettlementRefusal(Invoice())!.Key.Should().Be("Billing.NoSubmittedClaims");
        BillingScreenRules.SettlementRefusal(sent with { PayerAcceptedAmount = 320m })!.Key.Should().Be("Claim_SettlementAlreadyRecorded");
        BillingScreenRules.SettlementRefusal(sent).Should().BeNull();
        BillingScreenRules.Shortfall("300", 320m).Should().Be(20m);
        BillingScreenRules.CheckSettlement("330", 320m, null).Should().Be(new FieldRefusal("accepted", "Claim_AcceptedAmountInvalid", Amount: 320m));
        BillingScreenRules.CheckSettlement("300", 320m, " ")!.Field.Should().Be("reason");
        BillingScreenRules.CheckSettlement("300", 320m, "Acte hors nomenclature").Should().BeNull();
    }

    [Fact]
    public void The_discount_reads_the_net_and_asks_an_approval_beyond_the_ceiling()
    {
        var cap = DiscountCapSettings.Default; // 10 % of the gross

        DiscountEntry.Read("10 %", 400m, cap).Should().Be(new DiscountReading(40m, true, false, 40m, false, true, 360m));
        DiscountEntry.Read("60", 400m, cap).AboveCap.Should().BeTrue();
        DiscountEntry.Read("500", 400m, cap).IsValid.Should().BeFalse();
        DiscountEntry.Granted("", 400m).Should().BeNull();

        DiscountEntry.Check("abc", 400m, cap, null, null, null)!.Key.Should().Be("Billing.DiscountInvalid");
        DiscountEntry.Check("-5", 400m, cap, null, null, null)!.Key.Should().Be("Invoice_DiscountNegative");
        DiscountEntry.Check("500", 400m, cap, null, null, null)!.Key.Should().Be("Invoice_DiscountExceedsGross");
        DiscountEntry.Check("20", 400m, cap, " ", null, null)!.Field.Should().Be("reason");
        DiscountEntry.Check("60", 400m, cap, "Personnel", "dr.berrada", "")!.Field.Should().Be("password");
        DiscountEntry.Check("60", 400m, cap, "Personnel", "dr.berrada", "secret").Should().BeNull();
        DiscountEntry.Approval("60", 400m, cap, " dr.berrada ", "secret")!.UserName.Should().Be("dr.berrada");
        DiscountEntry.Approval("20", 400m, cap, "dr.berrada", "secret").Should().BeNull();
    }

    [Fact]
    public void The_till_lists_its_takings_and_closes_only_once_a_variance_beyond_tolerance_is_explained()
    {
        var summary = new CashSessionSummary(Guid.NewGuid(), Guid.NewGuid(), "Nadia", Now, 500m, 1200m, 300m, 450m, 0m, 0m, 0m,
                                             100m, 0m, 1600m, 9);

        CashDeskRules.Takings(summary).Select(l => l.Key).Should().Equal(
            "PaymentMethod.Cash", "PaymentMethod.Cheque", "PaymentMethod.Card", "PaymentMethod.BankTransfer",
            "PaymentMethod.BillOfExchange", "PaymentMethod.MobilePayment", "Payment.Refunds");
        CashDeskRules.Takings(summary)[^1].Amount.Should().Be(-100m);

        CashDeskRules.Variance("1 595,00", 1600m).Should().Be(new CashVariance(-5m, false));
        CashDeskRules.Variance("1550", 1600m).Should().Be(new CashVariance(-50m, true));
        CashDeskRules.Variance("abc", 1600m).Should().BeNull();

        CashDeskRules.CheckOpening("-1")!.Key.Should().Be("Payment_AmountMustBePositive");
        CashDeskRules.CheckOpening("2 00x")!.Key.Should().Be("Result_NotANumber");
        CashDeskRules.CheckOpening("500").Should().BeNull();
        CashDeskRules.CheckClosing("1550", 1600m, " ")
                     .Should().Be(new FieldRefusal("explanation", "CashSession_VarianceExplanationRequired", Amount: -50m));
        CashDeskRules.CheckClosing("1550", 1600m, "Rendu de monnaie erroné").Should().BeNull();
        CashDeskRules.CheckWithdrawal("0")!.Key.Should().Be("Result_NotANumber");
        CashDeskRules.CheckWithdrawal("1000").Should().BeNull();

        var cashier = new LicenceTestKit.User(true, Permissions.Billing.RecordPayment);
        CashDeskRules.Actions(cashier).Should().Be(new CashDeskActions(true, false, false));
    }

    [Fact]
    public void A_claim_batch_takes_one_payers_ready_invoices()
    {
        var amo = Guid.NewGuid();
        ClaimCandidateRow Candidate(Guid payer, bool ready = true, int days = 5) =>
            new(Guid.NewGuid(), "FA-1", new DateOnly(2026, 10, 1), payer, payer == amo ? "CNSS (AMO)" : "CNOPS", "ALAMI Mohammed",
                400m, 320m, false, true, !ready, ready, days);

        var blocked = Candidate(Cnops, ready: false);
        var cnops = Candidate(Cnops);
        var cnss = Candidate(amo);

        ClaimScreenRules.PayerToSelect(blocked, [blocked, cnss, cnops]).Should().Be(amo);
        ClaimScreenRules.PayerToSelect(cnops, [blocked, cnss, cnops]).Should().Be(Cnops);
        ClaimScreenRules.ClaimableOf([blocked, cnss, cnops], Cnops).Should().Equal(cnops);

        var accounts = new LicenceTestKit.User(true, Permissions.Billing.ExportAccounting);
        ClaimScreenRules.Actions([cnops], [blocked, cnops], null, accounts).Should().Be(new ClaimActions(true, true, false));
        ClaimScreenRules.Actions([cnops, cnss], [cnops, cnss], null, accounts).Submit.Should().BeFalse("one payer per batch");
        ClaimScreenRules.Actions([cnops], [cnops], null, new LicenceTestKit.User(true, Permissions.Billing.View))
                        .Should().Be(new ClaimActions(false, false, false));

        var today = new DateOnly(2026, 10, 20);
        var silent = new ClaimBatchRow("BT-1", Cnops, "CNOPS", today.AddDays(-70), 3, 1200m, 960m, 0m, 0, 0, 3, null);
        ClaimScreenRules.State(silent, today).Should().Be(ClaimBatchState.NeedsChasing);
        ClaimScreenRules.State(silent with { SubmittedOn = today.AddDays(-10) }, today).Should().Be(ClaimBatchState.AwaitingOutcome);
        ClaimScreenRules.State(silent with { PendingCount = 0, RejectedCount = 1 }, today).Should().Be(ClaimBatchState.PartlyRejected);
        ClaimScreenRules.IsLate(Candidate(Cnops, days: 75)).Should().BeTrue();
    }
}
