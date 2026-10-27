using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Billing;

/// <summary>
/// An advance taken on a request before it is invoiced (acompte).
/// <para>
/// A laboratory asks for money at the counter, when the patient is in front of it, but invoices
/// later — once the request is complete, or at the end of the month for a payer. Until this existed
/// the only way to take that money was to issue the invoice on the spot. A deposit is numbered in
/// the receipt series, enters the till like any payment, and turns into a settlement on the invoice
/// when the invoice is issued; whatever the invoice does not absorb is refunded.
/// </para>
/// </summary>
public sealed class Deposit : AuditableAggregateRoot, ISiteScoped
{
    private Deposit() { }

    public Deposit(string number, Guid siteId, Guid labOrderId, Guid patientId, decimal amount,
                   PaymentMethod method, DateTimeOffset receivedAt, Guid receivedByUserId)
    {
        Number = number;
        SiteId = siteId;
        LabOrderId = labOrderId;
        PatientId = patientId;
        Amount = Money.Round(amount);
        Method = method;
        ReceivedAt = receivedAt;
        ReceivedByUserId = receivedByUserId;
        Status = DepositStatus.Held;
    }

    /// <summary>Receipt number, from the receipt series ("REC/…").</summary>
    public string Number { get; private set; } = string.Empty;

    public Guid SiteId { get; set; }
    public Guid LabOrderId { get; private set; }
    public Guid PatientId { get; private set; }

    public decimal Amount { get; private set; }
    public PaymentMethod Method { get; private set; }
    public DepositStatus Status { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
    public Guid ReceivedByUserId { get; private set; }

    public string? Reference { get; set; }
    public string? BankName { get; set; }
    public DateOnly? MaturityDate { get; set; }

    /// <summary>Till session (shift) the deposit was taken in; required for cash.</summary>
    public Guid? CashSessionId { get; set; }

    /// <summary>The invoice the deposit was turned into a settlement on.</summary>
    public Guid? AppliedInvoiceId { get; private set; }
    public decimal AppliedAmount { get; private set; }
    public DateTimeOffset? AppliedAt { get; private set; }

    public decimal RefundedAmount { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }
    public string? RefundReason { get; private set; }

    /// <summary>
    /// The site whose till paid a cash refund. A user with several sites may refund from another
    /// site's drawer than the one that took the money: that site's cash account pays it.
    /// </summary>
    public Guid? RefundSiteId { get; set; }

    /// <summary>
    /// Set when the deposit was not taken at the counter but carried over from an invoice a credit
    /// note reversed in full: what the patient had already paid on it, held for the invoice that
    /// replaces it. No money came in with it — it is already in the till or the bank from the
    /// original payment — so it is neither takings nor a treasury movement, and the accounts move
    /// it from the patient's account to 4421 instead of from the treasury.
    /// </summary>
    public Guid? CarriedFromInvoiceId { get; private set; }
    public string? CarriedFromInvoiceNumber { get; private set; }

    public bool IsCarriedOver => CarriedFromInvoiceId is not null;

    /// <summary>
    /// What a payment on a wholly credited invoice becomes: a deposit on the order, in the method it
    /// was paid by, applied to the next invoice of that order as any deposit is, or refunded.
    /// </summary>
    public static Deposit CarriedOver(string number, Guid siteId, Guid labOrderId, Guid patientId,
                                      Payment payment, decimal amount, Guid fromInvoiceId,
                                      string fromInvoiceNumber, DateTimeOffset at, Guid byUserId) =>
        new(number, siteId, labOrderId, patientId, amount, payment.Method, at, byUserId)
        {
            Reference = payment.Reference,
            BankName = payment.BankName,
            MaturityDate = payment.MaturityDate,
            CarriedFromInvoiceId = fromInvoiceId,
            CarriedFromInvoiceNumber = fromInvoiceNumber
        };

    /// <summary>What is still held: neither applied to an invoice nor handed back.</summary>
    public decimal Remaining => Money.Round(Amount - AppliedAmount - RefundedAmount);

    public Result Validate()
    {
        var errors = new List<Error>();

        if (Amount <= 0m)
            errors.Add(Error.Validation("Payment_AmountMustBePositive"));
        if (Method is PaymentMethod.ThirdPartyPayer or PaymentMethod.WriteOff)
            errors.Add(Error.Validation("Deposit_MethodNotAllowed"));
        if (Method is PaymentMethod.Cheque or PaymentMethod.BillOfExchange
            && string.IsNullOrWhiteSpace(Reference))
            errors.Add(Error.Validation("Payment_ReferenceRequired"));
        if (Method is PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(Reference))
            errors.Add(Error.Validation("Payment_TransferReferenceRequired"));
        if (Method == PaymentMethod.Cash && CashSessionId is null && !IsCarriedOver)
            errors.Add(Error.Validation("Payment_CashRequiresOpenSession"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>
    /// Records that <paramref name="amount"/> of the deposit settled an invoice. The rest, if any,
    /// stays held until it is refunded.
    /// </summary>
    public Result ApplyTo(Guid invoiceId, decimal amount, DateTimeOffset at)
    {
        if (Status != DepositStatus.Held)
            return Result.Failure(Error.Conflict("Deposit_NotHeld"));

        var value = Money.Round(amount);

        if (value <= 0m || value > Remaining)
            return Result.Failure(Error.Validation("Deposit_ApplyAmountInvalid"));

        AppliedInvoiceId = invoiceId;
        AppliedAmount = Money.Round(AppliedAmount + value);
        AppliedAt = at;

        if (Remaining == 0m) Status = DepositStatus.Applied;

        return Result.Success();
    }

    /// <summary>Hands back what is still held. A deposit is never deleted.</summary>
    public Result Refund(DateTimeOffset at, string reason)
    {
        if (Status != DepositStatus.Held || Remaining <= 0m)
            return Result.Failure(Error.Conflict("Deposit_NotHeld"));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Payment_RefundReasonRequired"));

        RefundedAmount = Remaining;
        RefundedAt = at;
        RefundReason = reason;
        Status = AppliedAmount > 0m ? DepositStatus.Applied : DepositStatus.Refunded;

        return Result.Success();
    }
}

public enum DepositStatus
{
    /// <summary>Taken, not yet on an invoice (or only partly).</summary>
    Held = 0,

    /// <summary>Settled an invoice; any excess was refunded.</summary>
    Applied = 1,

    /// <summary>Handed back in full before any invoice.</summary>
    Refunded = 2
}
