using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Billing;

/// <summary>What « Facturation » offers, by right and by the invoice the print buttons act on.</summary>
public sealed record BillingActions(
    bool Issue, bool Deposit, bool RefundDeposit, bool Pay, bool CreditNote, bool Refund, bool Bounce,
    bool SubmitClaim, bool ClaimOutcome, bool PayerSettlement, bool Print, bool PrintCotation);

/// <summary>
/// Why an action is not taken on the selection, said before any dialog is filled in for nothing:
/// the message's key and the values it names. An information (« entièrement couvert ») is said as
/// one, not as a warning.
/// </summary>
public sealed record BillingRefusal(string Key, bool IsInformation = false, params object?[] Args);

/// <summary>Whether a payment can be taken at the counter on an invoice.</summary>
public enum PaymentBlock
{
    None = 0,

    /// <summary>Nothing outstanding: the invoice is settled.</summary>
    Settled = 1,

    /// <summary>What is left is the payer's share, settled through the claim.</summary>
    PayerShareOnly = 2
}

/// <summary>Which boxes of the payment form a method calls for.</summary>
public sealed record PaymentFields(bool Reference, bool Bank, bool Maturity);

/// <summary>
/// The rules of « Facturation » that need no window: what each button is offered on, what the
/// selection must be before an invoice, a deposit, a payment, a credit note or a claim is asked for,
/// and what the payment and settlement forms refuse. The services check the same and refuse in any
/// case; these say it before the operator types a reason for nothing. The workstation's view and
/// dialogs and the web's screen apply the same ones.
/// <para>
/// Issuing is one action, not a draft-then-issue pair. A number is allocated from a gapless series
/// at the moment of issue, and holding one across two user actions is precisely how a gap appears
/// in a series that Moroccan tax law requires to be unbroken.
/// </para>
/// </summary>
public static class BillingScreenRules
{
    /// <summary>The ledger's status chips, in order; « Tous » before them.</summary>
    public static readonly IReadOnlyList<InvoiceStatus> LedgerStatuses =
        [InvoiceStatus.Issued, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid, InvoiceStatus.WrittenOff];

    /// <summary>The methods taken at the counter, in the order the form lists them.</summary>
    public static readonly IReadOnlyList<PaymentMethod> CounterMethods =
    [
        PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Cheque,
        PaymentMethod.BankTransfer, PaymentMethod.BillOfExchange, PaymentMethod.MobilePayment
    ];

    /// <summary>A credit note's reason is at least this long: it is printed on the document.</summary>
    public const int CreditNoteReasonMinimumLength = 10;

    /// <summary>The longest reason the corrections' columns hold.</summary>
    public const int CorrectionReasonMaxLength = 500;

    /// <summary>The longest texts the payment's columns hold: a longer one failed the save.</summary>
    public const int ReferenceMaxLength = 60;
    public const int BankMaxLength = 120;

    /// <summary>The longest discount reason the invoice holds.</summary>
    public const int DiscountReasonMaxLength = 300;

    /// <summary>The longest claim refusal or shortfall reason, and remittance reference, the columns hold.</summary>
    public const int ClaimReasonMaxLength = 500;
    public const int RemittanceMaxLength = 60;

    /// <summary>
    /// The buttons a user's rights allow, and the print buttons by the invoice they act on. The
    /// coded (cotation B) format is that of an invoice: a credit note prints as it always has.
    /// Declaring a cheque unpaid is the accounts office's (Comptabilité, Administrateur).
    /// </summary>
    public static BillingActions Actions(ICurrentUser user, InvoiceRow? printTarget)
    {
        ArgumentNullException.ThrowIfNull(user);

        var credit = user.Has(Permissions.Billing.CreditNote);
        var payment = user.Has(Permissions.Billing.RecordPayment);
        var claims = user.Has(Permissions.Billing.ExportAccounting);

        return new BillingActions(
            Issue: user.Has(Permissions.Billing.CreateInvoice),
            Deposit: payment,
            RefundDeposit: credit,
            Pay: payment,
            CreditNote: credit,
            Refund: credit,
            Bounce: user.Has(Permissions.Billing.DeclareUnpaid),
            SubmitClaim: claims,
            ClaimOutcome: claims,
            PayerSettlement: claims,
            Print: printTarget is not null,
            PrintCotation: printTarget is { Kind: InvoiceKind.Invoice });
    }

    /// <summary>
    /// The invoice the print buttons act on: the single ticked row when exactly one is ticked, or the
    /// highlighted row when none is. Several ticked rows print nothing — which of them was meant is
    /// not something to guess with an accounting document.
    /// </summary>
    public static InvoiceRow? PrintTarget(IReadOnlyList<InvoiceRow> ticked, InvoiceRow? current)
    {
        ArgumentNullException.ThrowIfNull(ticked);

        return ticked.Count switch
        {
            1 => ticked[0],
            0 => current,
            _ => null
        };
    }

    // ---- The invoice -------------------------------------------------------------------------------

    /// <summary>
    /// Why the ticked requests cannot go on one invoice, or null when they can. Several orders can
    /// share one invoice only if they belong to one patient — a single document addressed to two
    /// people is not a document anybody can settle — and to one payer, each having been split with
    /// its own at registration. A request listed for the deposit it still holds, invoiced or
    /// cancelled, is refunded, not invoiced: named, rather than the whole issue failing on one row
    /// nobody can spot.
    /// </summary>
    public static BillingRefusal? IssueRefusal(IReadOnlyList<BillableOrderRow> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        if (selected.Count == 0) return new BillingRefusal("Message.NothingSelected", IsInformation: true);

        if (selected.Select(s => s.PatientId).Distinct().Count() > 1)
            return new BillingRefusal("Invoice_MultiplePatients");

        var invoiced = selected.Where(s => s.InvoiceId is not null).ToList();
        if (invoiced.Count > 0)
            return new BillingRefusal("Invoice_OrderAlreadyInvoiced", false, Accessions(invoiced));

        var cancelled = selected.Where(s => s.Status == LabOrderStatus.Cancelled).ToList();
        if (cancelled.Count > 0)
            return new BillingRefusal("Invoice_OrderCancelled", false, Accessions(cancelled));

        if (selected.Select(s => (s.PayerId, s.DirectBilling)).Distinct().Count() > 1)
            return new BillingRefusal("Invoice_MultiplePayers", false, Accessions(selected));

        return null;
    }

    /// <summary>
    /// The payer an invoice may be addressed to instead of the patient, or null. Invoiced again
    /// after a credit note reversed the first invoice, which went to the patient although the order
    /// had a payer: the usual correction is a third-party box left unticked at registration, so the
    /// operator is offered to address this one to the payer — when the payer has an agreement in
    /// force, without which its share cannot be claimed.
    /// </summary>
    public static Guid? ReinvoicePayer(IReadOnlyList<BillableOrderRow> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        return selected.Count > 0
               && selected[0] is { PayerId: { } payerId, DirectBilling: false, PayerAgreementValid: true }
               && selected.Any(s => s.PreviouslyInvoiced)
            ? payerId
            : null;
    }

    /// <summary>The total the issue dialog confirms: the ticked requests' charges.</summary>
    public static Money Gross(IReadOnlyList<BillableOrderRow> selected) => Money.Of(selected.Sum(s => s.Total));

    // ---- Deposits ----------------------------------------------------------------------------------

    /// <summary>
    /// Why no deposit can be taken on the request, or null. A cancelled request is refunded, an
    /// invoiced one is paid on its invoice, and under direct billing only the patient's share can be
    /// taken in advance — once the deposits held cover it, there is nothing left to take.
    /// </summary>
    public static BillingRefusal? DepositRefusal(BillableOrderRow order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.Status == LabOrderStatus.Cancelled)
            return new BillingRefusal("Deposit_OrderCancelled", false, order.AccessionNumber);
        if (order.InvoiceId is not null)
            return new BillingRefusal("Invoice_OrderAlreadyInvoiced", false, order.AccessionNumber);
        if (order.DepositCeiling <= 0m)
            return new BillingRefusal("Deposit.FullyCovered", IsInformation: true);
        return null;
    }

    /// <summary>The deposits a refund hands back: those still held, with money left on them.</summary>
    public static IReadOnlyList<DepositRow> HeldDeposits(IEnumerable<DepositRow> deposits) =>
        [.. deposits.Where(d => d.Status == DepositStatus.Held && d.Remaining > 0m)];

    // ---- Payments and their corrections ------------------------------------------------------------

    /// <summary>Whether the counter can take a payment on the invoice.</summary>
    public static PaymentBlock PaymentBlockOf(InvoiceRow invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (invoice.Outstanding <= 0m) return PaymentBlock.Settled;
        if (invoice.PatientPayable <= 0m) return PaymentBlock.PayerShareOnly;
        return PaymentBlock.None;
    }

    /// <summary>
    /// The caption of what a payment may take: on an invoice whose payer share is claimed from the
    /// payer, the patient's part only — asked for the whole balance, the patient paid the payer's
    /// share too, and it was claimed again.
    /// </summary>
    public static string PaymentCeilingKey(InvoiceRow invoice) =>
        invoice.PatientPayable < invoice.Outstanding ? "Payment.PatientShareDue" : "Billing.Outstanding";

    /// <summary>
    /// The settlements a correction can apply to: a bounce is meaningless for cash, a refund only
    /// gives back what the patient paid, and neither applies to a payment already refunded.
    /// </summary>
    public static IReadOnlyList<PaymentRow> CorrectionCandidates(IEnumerable<PaymentRow> payments, bool bounce) =>
        [.. payments.Where(p => bounce ? p.CanBounce : p.CanRefund)];

    /// <summary>Which boxes the method calls for: a cheque without its number is not traceable, a transfer without a reference cannot be reconciled.</summary>
    public static PaymentFields FieldsFor(PaymentMethod method) => new(
        Reference: method is PaymentMethod.Cheque or PaymentMethod.BankTransfer or PaymentMethod.BillOfExchange
                          or PaymentMethod.Card,
        Bank: method is PaymentMethod.Cheque or PaymentMethod.BankTransfer or PaymentMethod.BillOfExchange,
        Maturity: method is PaymentMethod.Cheque or PaymentMethod.BillOfExchange);

    /// <summary>
    /// Moroccan tax law penalises cash settlement above a threshold: warned before the money is
    /// taken, not after the receipt is printed.
    /// </summary>
    public static bool WarnsCash(PaymentMethod method, string? amountText) =>
        method == PaymentMethod.Cash && TypedNumber.Parse(amountText) is { } amount
        && amount > Payment.CashPaymentWarningThresholdMad;

    /// <summary>
    /// Checks the payment (or deposit) form: an amount that reads, is positive and within what may
    /// be taken, and the reference a cheque, a bill of exchange or a transfer needs.
    /// </summary>
    public static FieldRefusal? CheckPayment(string? amountText, decimal ceiling, bool deposit, PaymentMethod method,
                                             string? reference)
    {
        if (TypedNumber.IsUnreadable(amountText))
            return new FieldRefusal("amount", "Result_NotANumber", Typed: amountText!.Trim());

        if (TypedNumber.Parse(amountText) is not { } amount || amount <= 0m)
            return new FieldRefusal("amount", "Payment_AmountMustBePositive");

        if (amount > ceiling)
            return new FieldRefusal("amount", deposit ? "Deposit_ExceedsOrderTotal" : "Payment_ExceedsOutstanding",
                                    Amount: ceiling);

        if (FieldsFor(method).Reference && string.IsNullOrWhiteSpace(reference)
            && method is PaymentMethod.Cheque or PaymentMethod.BillOfExchange)
            return new FieldRefusal("reference", "Payment_ReferenceRequired");

        if (method == PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(reference))
            return new FieldRefusal("reference", "Payment_TransferReferenceRequired");

        return null;
    }

    // ---- Credit notes ------------------------------------------------------------------------------

    /// <summary>
    /// Why no credit note can be issued on the invoice, or null. Only an invoice is credited; one
    /// reversed in full would be credited twice; one sent to the payer, or answered by it, is
    /// corrected through the claim. The service refuses all three as well.
    /// </summary>
    public static BillingRefusal? CreditNoteRefusal(InvoiceRow invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (invoice.Kind == InvoiceKind.CreditNote) return new BillingRefusal("Invoice_CannotCreditACreditNote");
        if (invoice.Creditable <= 0m) return new BillingRefusal("Invoice_AlreadyFullyCredited", false, invoice.Number);
        if (Invoice.ClaimBlocksCreditNote(invoice.ClaimStatus))
            return new BillingRefusal("Invoice_CreditNoteClaimSent", false, invoice.Number);
        return null;
    }

    // ---- Third-party payer -------------------------------------------------------------------------

    /// <summary>The ticked invoices a claim batch may take: prepared, or rejected and resent.</summary>
    public static IReadOnlyList<InvoiceRow> Claimable(IEnumerable<InvoiceRow> ticked) =>
        [.. ticked.Where(i => i.ClaimStatus is ClaimStatus.Prepared or ClaimStatus.Rejected)];

    /// <summary>The ticked invoices whose claim has been sent, which a payer's answer applies to.</summary>
    public static IReadOnlyList<InvoiceRow> Submitted(IEnumerable<InvoiceRow> ticked) =>
        [.. ticked.Where(i => i.ClaimStatus is not (ClaimStatus.NotApplicable or ClaimStatus.Prepared))];

    /// <summary>
    /// The answers a payer can return on a batch. No « Settled »: a batch answer records no money,
    /// so « Settled » left the payer's share owing while reading as paid. Settlement goes through
    /// the per-invoice accepted amount.
    /// </summary>
    public static readonly IReadOnlyList<ClaimStatus> ClaimOutcomes =
        [ClaimStatus.Accepted, ClaimStatus.PartiallyAccepted, ClaimStatus.Rejected, ClaimStatus.Resubmitted];

    /// <summary>
    /// The reason is mandatory for a refusal or a part-payment: the payer states it once, and it is
    /// what decides whether the claim is corrected and resent or written off.
    /// </summary>
    public static bool ClaimOutcomeNeedsReason(ClaimStatus outcome) =>
        outcome is ClaimStatus.Rejected or ClaimStatus.PartiallyAccepted;

    public static FieldRefusal? CheckClaimOutcome(ClaimStatus outcome, string? reason) =>
        ClaimOutcomeNeedsReason(outcome) && string.IsNullOrWhiteSpace(reason)
            ? new FieldRefusal("reason", "Claim_RejectionReasonRequired")
            : null;

    /// <summary>
    /// Why the payer's settlement cannot be entered on the invoice, or null: it needs a payer's
    /// share whose claim was sent, and is entered once.
    /// </summary>
    public static BillingRefusal? SettlementRefusal(InvoiceRow invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (invoice.PayerShare <= 0m || invoice.ClaimStatus is ClaimStatus.NotApplicable or ClaimStatus.Prepared)
            return new BillingRefusal("Billing.NoSubmittedClaims");
        if (invoice.PayerAcceptedAmount is not null)
            return new BillingRefusal("Claim_SettlementAlreadyRecorded", false, invoice.Number);
        return null;
    }

    /// <summary>
    /// The caption over the payer's settlement: what the payer still owes — after a credit note,
    /// the original share overstated it and the shortfall was computed on money no longer owed.
    /// </summary>
    public static string SettlementCeilingKey(InvoiceRow invoice) =>
        invoice.PayerDue < invoice.PayerShare ? "Claim.PayerDue" : "Billing.PayerShare";

    /// <summary>What the payer did not accept of what it owes, as typed; zero while nothing reads.</summary>
    public static decimal Shortfall(string? acceptedText, decimal payerDue) =>
        TypedNumber.Parse(acceptedText) is { } accepted ? Money.Round(payerDue - accepted) : 0m;

    /// <summary>
    /// Checks the payer's settlement: an amount between nothing and what the payer owes, and the
    /// reason of a shortfall — an unexplained one is exactly what the accountant asks about.
    /// </summary>
    public static FieldRefusal? CheckSettlement(string? acceptedText, decimal payerDue, string? reason)
    {
        if (TypedNumber.Parse(acceptedText) is not { } amount || amount < 0m || amount > payerDue)
            return new FieldRefusal("accepted", "Claim_AcceptedAmountInvalid", Amount: payerDue);

        if (Shortfall(acceptedText, payerDue) > 0m && string.IsNullOrWhiteSpace(reason))
            return new FieldRefusal("reason", "Claim_RejectionReasonRequired");

        return null;
    }

    private static string Accessions(IEnumerable<BillableOrderRow> rows) =>
        string.Join(", ", rows.Select(r => r.AccessionNumber));
}
