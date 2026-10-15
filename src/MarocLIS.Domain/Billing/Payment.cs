using System.Linq.Expressions;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Billing;

/// <summary>
/// A settlement against an invoice. Cash movements are additionally tied to a
/// <see cref="CashSession"/> so the till can be reconciled at the end of each shift.
/// </summary>
public sealed class Payment : AuditableAggregateRoot, ISiteScoped
{
    private Payment() { }

    public Payment(decimal amount, PaymentMethod method, DateOnly paymentDate,
                   Guid receivedByUserId, Guid siteId)
    {
        Amount = Money.Round(amount);
        Method = method;
        PaymentDate = paymentDate;
        ReceivedByUserId = receivedByUserId;
        SiteId = siteId;
        Status = PaymentStatus.Settled;
    }

    public Guid InvoiceId { get; private set; }
    internal void AttachTo(Guid invoiceId) => InvoiceId = invoiceId;

    public Guid SiteId { get; set; }

    public decimal Amount { get; private set; }
    public PaymentMethod Method { get; private set; }
    public PaymentStatus Status { get; private set; }

    public DateOnly PaymentDate { get; private set; }
    public Guid ReceivedByUserId { get; private set; }

    /// <summary>Cheque number, transfer reference, card authorisation code.</summary>
    public string? Reference { get; set; }

    /// <summary>Bank the cheque or transfer came from.</summary>
    public string? BankName { get; set; }

    /// <summary>Due date of a post-dated cheque or a bill of exchange.</summary>
    public DateOnly? MaturityDate { get; set; }

    /// <summary>Till session this cash movement belongs to.</summary>
    public Guid? CashSessionId { get; set; }

    /// <summary>
    /// Set when this settlement is a deposit taken before the invoice. The money already entered
    /// the till when the deposit was taken, so this payment must not be counted into it again.
    /// </summary>
    public Guid? DepositId { get; set; }

    /// <summary>Set when the payer settled several invoices in one remittance.</summary>
    public string? RemittanceReference { get; set; }

    public DateTimeOffset? BouncedAt { get; private set; }
    public string? BounceReason { get; private set; }

    public DateTimeOffset? RefundedAt { get; private set; }
    public string? RefundReason { get; private set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Cash settlements above this amount attract a tax penalty in Morocco and must be steered
    /// towards a traceable method. The application warns at the till rather than after the fact.
    /// </summary>
    public const decimal CashPaymentWarningThresholdMad = 20_000m;

    /// <summary>
    /// Money that actually came in: a settled payment that is not a write-off. A write-off is
    /// recorded as a settlement so the receivable closes, but nobody paid it. An expression so the
    /// statistics query and the domain share one definition.
    /// </summary>
    public static readonly Expression<Func<Payment, bool>> IsMoneyCollected =
        p => p.Status == PaymentStatus.Settled && p.Method != PaymentMethod.WriteOff;

    /// <summary>
    /// What the third-party payer itself paid, as opposed to the patient at the counter or a
    /// write-off of the refused share.
    /// </summary>
    public static readonly Expression<Func<Payment, bool>> IsPayerSettlement =
        p => p.Status == PaymentStatus.Settled && p.Method == PaymentMethod.ThirdPartyPayer;

    public bool RequiresTraceableMethodWarning =>
        Method == PaymentMethod.Cash && Amount > CashPaymentWarningThresholdMad;

    public Result MarkBounced(DateTimeOffset at, string reason)
    {
        if (Method is not (PaymentMethod.Cheque or PaymentMethod.BillOfExchange))
            return Result.Failure(Error.Validation("Payment_OnlyChequesCanBounce"));
        if (Status != PaymentStatus.Settled)
            return Result.Failure(Error.Conflict("Payment_NotSettled"));

        Status = PaymentStatus.Bounced;
        BouncedAt = at;
        BounceReason = reason;
        return Result.Success();
    }

    public Result Refund(DateTimeOffset at, string reason)
    {
        if (Status != PaymentStatus.Settled)
            return Result.Failure(Error.Conflict("Payment_NotSettled"));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Payment_RefundReasonRequired"));

        Status = PaymentStatus.Refunded;
        RefundedAt = at;
        RefundReason = reason;
        return Result.Success();
    }

    /// <summary>
    /// Cancels a write-off whose reason no longer holds — the claim it closed was resubmitted.
    /// The line stays on the invoice, cancelled, with the reason in its notes.
    /// </summary>
    public Result CancelWriteOff(string reason)
    {
        if (Method != PaymentMethod.WriteOff)
            return Result.Failure(Error.Conflict("Payment_NotSettled"));
        if (Status != PaymentStatus.Settled)
            return Result.Failure(Error.Conflict("Payment_NotSettled"));

        Status = PaymentStatus.Cancelled;
        Notes = string.IsNullOrWhiteSpace(Notes) ? $"Annulé : {reason}" : $"{Notes} — annulé : {reason}";
        return Result.Success();
    }

    public Result Validate()
    {
        var errors = new List<Error>();

        if (Amount <= 0m)
            errors.Add(Error.Validation("Payment_AmountMustBePositive"));
        if (Method is PaymentMethod.Cheque or PaymentMethod.BillOfExchange
            && string.IsNullOrWhiteSpace(Reference))
            errors.Add(Error.Validation("Payment_ReferenceRequired"));
        if (Method is PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(Reference))
            errors.Add(Error.Validation("Payment_TransferReferenceRequired"));
        if (Method == PaymentMethod.Cash && CashSessionId is null)
            errors.Add(Error.Validation("Payment_CashRequiresOpenSession"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum PaymentMethod
{
    /// <summary>Espèces.</summary>
    Cash = 0,

    /// <summary>Chèque.</summary>
    Cheque = 1,

    /// <summary>Carte bancaire.</summary>
    Card = 2,

    /// <summary>Virement bancaire.</summary>
    BankTransfer = 3,

    /// <summary>Effet / traite.</summary>
    BillOfExchange = 4,

    /// <summary>Paiement mobile.</summary>
    MobilePayment = 5,

    /// <summary>Settled directly by the payer under a third-party agreement.</summary>
    ThirdPartyPayer = 6,

    /// <summary>Written off; recorded as a settlement so the receivable closes.</summary>
    WriteOff = 9
}

public enum PaymentStatus
{
    Pending = 0,
    Settled = 1,
    /// <summary>Cheque returned unpaid; the receivable reopens.</summary>
    Bounced = 2,
    Refunded = 3,
    Cancelled = 4
}

/// <summary>
/// A till shift (session de caisse). Cash handled at a laboratory counter has to be reconciled per
/// operator per shift; the variance between what the system expects and what was counted is the
/// figure an internal audit looks at.
/// </summary>
public sealed class CashSession : AuditableAggregateRoot, ISiteScoped
{
    private CashSession() { }

    public CashSession(Guid siteId, Guid operatorUserId, DateTimeOffset openedAt, decimal openingFloat)
    {
        SiteId = siteId;
        OperatorUserId = operatorUserId;
        OpenedAt = openedAt;
        OpeningFloat = Money.Round(openingFloat);
        Status = CashSessionStatus.Open;
    }

    public Guid SiteId { get; set; }
    public Guid OperatorUserId { get; private set; }

    /// <summary>Workstation or counter the session is bound to.</summary>
    public string? Workstation { get; set; }

    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedByUserId { get; private set; }

    public CashSessionStatus Status { get; private set; }

    /// <summary>Fond de caisse at opening.</summary>
    public decimal OpeningFloat { get; private set; }

    /// <summary>Cash taken in, computed from the settled cash payments of the session.</summary>
    public decimal ExpectedCashReceipts { get; private set; }

    /// <summary>Refunds paid out in cash.</summary>
    public decimal CashRefunds { get; private set; }

    /// <summary>Cash removed mid-shift and banked.</summary>
    public decimal CashWithdrawals { get; private set; }

    /// <summary>What the till should physically hold at closing.</summary>
    public decimal ExpectedClosingCash =>
        Money.Round(OpeningFloat + ExpectedCashReceipts - CashRefunds - CashWithdrawals);

    /// <summary>What the operator actually counted.</summary>
    public decimal? CountedCash { get; private set; }

    /// <summary>Counted minus expected: negative is a shortfall.</summary>
    public decimal? Variance =>
        CountedCash is { } counted ? Money.Round(counted - ExpectedClosingCash) : null;

    public string? VarianceExplanation { get; private set; }

    /// <summary>Non-cash takings, recorded for the shift report rather than for reconciliation.</summary>
    public decimal ChequeReceipts { get; private set; }
    public decimal CardReceipts { get; private set; }
    public decimal TransferReceipts { get; private set; }

    public decimal TotalReceipts =>
        Money.Round(ExpectedCashReceipts + ChequeReceipts + CardReceipts + TransferReceipts);

    /// <summary>Accumulates a settlement into the shift totals.</summary>
    public void Accumulate(PaymentMethod method, decimal amount)
    {
        var value = Money.Round(amount);
        switch (method)
        {
            case PaymentMethod.Cash: ExpectedCashReceipts = Money.Round(ExpectedCashReceipts + value); break;
            case PaymentMethod.Cheque: ChequeReceipts = Money.Round(ChequeReceipts + value); break;
            case PaymentMethod.Card: CardReceipts = Money.Round(CardReceipts + value); break;
            case PaymentMethod.BankTransfer: TransferReceipts = Money.Round(TransferReceipts + value); break;
        }
    }

    public void RecordCashRefund(decimal amount) => CashRefunds = Money.Round(CashRefunds + amount);

    public void RecordWithdrawal(decimal amount) => CashWithdrawals = Money.Round(CashWithdrawals + amount);

    /// <summary>
    /// Closes the shift. A variance beyond the tolerance must be explained before the session can
    /// close, so an unexplained shortfall cannot simply be clicked away.
    /// </summary>
    public Result Close(DateTimeOffset at, Guid byUserId, decimal countedCash,
                        string? explanation, decimal toleranceMad = 10m)
    {
        if (Status != CashSessionStatus.Open)
            return Result.Failure(Error.Conflict("CashSession_NotOpen"));

        CountedCash = Money.Round(countedCash);

        if (Math.Abs(Variance!.Value) > toleranceMad && string.IsNullOrWhiteSpace(explanation))
            return Result.Failure(Error.Validation("CashSession_VarianceExplanationRequired", Variance));

        VarianceExplanation = explanation;
        ClosedAt = at;
        ClosedByUserId = byUserId;
        Status = CashSessionStatus.Closed;
        return Result.Success();
    }
}

public enum CashSessionStatus
{
    Open = 0,
    Closed = 1,
    /// <summary>Reviewed and signed off by a supervisor.</summary>
    Reconciled = 2
}
