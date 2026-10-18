using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Billing;

/// <summary>Where the patient stands on paying for an order, as the report counter needs to know it.</summary>
public enum OrderPaymentState
{
    /// <summary>Nothing billable on the order: no charge, nothing to collect.</summary>
    NothingDue = 0,

    /// <summary>On an issued invoice the patient owes nothing more on.</summary>
    Paid = 1,

    /// <summary>On an issued invoice the patient still owes part of.</summary>
    PatientShareDue = 2,

    /// <summary>Billable acts on no issued invoice: never invoiced, or a credit note reversed it.</summary>
    NotInvoiced = 3
}

/// <summary>
/// The patient's payment status of one order, read from the invoice that bills it.
/// <para>
/// What the patient still owes is the cashier's own figure — <see cref="Invoice.PatientPayableAmount"/>,
/// the outstanding less the payer's share while that share is claimed from the payer — so the
/// counter never says « payé » about an invoice the Caisse still collects on. An order carries one
/// invoice at a time (<c>LabOrder.InvoiceId</c>), and a credit note that reverses it wholly frees the
/// order, so an order back to « non facturé » is the billing desk's own reading as well. An invoice
/// covering several orders is collected as a whole: its balance is the order's balance.
/// </para>
/// </summary>
/// <param name="PatientDue">
/// What is still due from the patient: the invoice's patient payable, or for an order not invoiced
/// what the patient will owe less the deposits held. Null when the user may not see amounts.
/// </param>
/// <param name="InvoiceNumber">The invoice billing the order; null when there is none or it is hidden.</param>
/// <param name="CoveredOrders">The orders that invoice covers; above one, its balance is shared.</param>
public sealed record OrderPayment(
    OrderPaymentState State,
    decimal? PatientDue,
    string? InvoiceNumber = null,
    int CoveredOrders = 1)
{
    public static readonly OrderPayment NothingDue = new(OrderPaymentState.NothingDue, 0m);

    /// <summary>Whether the counter may hand the report over without a word about payment.</summary>
    public bool IsSettled => State is OrderPaymentState.NothingDue or OrderPaymentState.Paid;

    /// <summary>The order's status from the issued invoice that bills it, read from its stored columns.</summary>
    public static OrderPayment ForInvoice(string number, InvoiceKind kind, decimal total, decimal paid,
                                          decimal credited, ClaimStatus claimStatus, decimal payerShare,
                                          decimal? payerAccepted, int coveredOrders = 1)
    {
        var due = Invoice.ComputePatientPayable(Invoice.ComputeOutstanding(kind, total, paid, credited),
                                                claimStatus, payerShare, payerAccepted);

        return new OrderPayment(due > 0m ? OrderPaymentState.PatientShareDue : OrderPaymentState.Paid,
                                due, number, Math.Max(1, coveredOrders));
    }

    /// <summary>
    /// The order's status while no issued invoice bills it. What the patient will owe is the
    /// billing desk's deposit ceiling (<see cref="BillableOrderRow.ComputeDepositCeiling"/>): the
    /// patient's share alone when the payer is billed directly, the whole charge otherwise, less
    /// the deposits already held.
    /// </summary>
    public static OrderPayment ForUninvoiced(decimal total, decimal patientShare, bool payerBilledDirectly,
                                             decimal depositHeld) =>
        total <= 0m
            ? NothingDue
            : new OrderPayment(OrderPaymentState.NotInvoiced,
                               BillableOrderRow.ComputeDepositCeiling(total, patientShare, payerBilledDirectly,
                                                                      depositHeld));

    /// <summary>
    /// The status without its amounts, for a user without <c>billing.view</c>: the counter still
    /// learns that the order is not paid, but not how much is owed or on which invoice.
    /// </summary>
    public OrderPayment WithoutAmounts() => this with { PatientDue = null, InvoiceNumber = null, CoveredOrders = 1 };

    /// <summary>
    /// The note the access log carries when a report goes out on an order not settled: what the
    /// operator was told and chose to go past. <paramref name="act"/> is « remis » or « réimprimé ».
    /// </summary>
    public string AuditNote(string act) => State switch
    {
        OrderPaymentState.NotInvoiced => PatientDue is { } estimate
            ? $"{act} avec dossier non facturé, part patient estimée : {Money.Of(estimate)}"
            : $"{act} avec dossier non facturé",
        OrderPaymentState.PatientShareDue => PatientDue is { } due
            ? $"{act} avec part patient due : {Money.Of(due)}"
              + (InvoiceNumber is { } number ? $" (facture {number})" : string.Empty)
            : $"{act} avec part patient non réglée",
        _ => act
    };
}
