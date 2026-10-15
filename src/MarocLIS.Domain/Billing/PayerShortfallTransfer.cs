using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Billing;

/// <summary>
/// The part of a payer's share the payer refused and the patient is left to pay, recorded on the
/// day the payer's answer was entered. In the accounts it moves from the payer's receivable to the
/// patient's on that day, and back again on the day the claim is reopened.
/// <para>
/// The movement is an event of its own: dated on the invoice's last change, it moved with every
/// later payment and was exported again in each period the invoice was touched; computed from the
/// accepted amount alone, it simply vanished when the claim was reopened, with nothing moving the
/// amount back to the payer.
/// </para>
/// </summary>
public sealed class PayerShortfallTransfer : Entity
{
    private PayerShortfallTransfer() { }

    internal PayerShortfallTransfer(Guid invoiceId, decimal amount, DateOnly transferredOn)
    {
        InvoiceId = invoiceId;
        Amount = Money.Round(amount);
        TransferredOn = transferredOn;
    }

    public Guid InvoiceId { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>The day the payer's answer was recorded.</summary>
    public DateOnly TransferredOn { get; private set; }

    /// <summary>The day the claim was reopened and the amount went back to the payer; null while it stands.</summary>
    public DateOnly? ReversedOn { get; private set; }

    internal void Reverse(DateOnly on) => ReversedOn ??= on;
}
