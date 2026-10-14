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

    /// <summary>Till session the cash went into.</summary>
    public Guid? CashSessionId { get; set; }

    /// <summary>The invoice the deposit was turned into a settlement on.</summary>
    public Guid? AppliedInvoiceId { get; private set; }
    public decimal AppliedAmount { get; private set; }
    public DateTimeOffset? AppliedAt { get; private set; }

    public decimal RefundedAmount { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }
    public string? RefundReason { get; private set; }

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
        if (Method == PaymentMethod.Cash && CashSessionId is null)
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
