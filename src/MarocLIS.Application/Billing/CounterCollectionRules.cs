using MarocLIS.Application.Input;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Billing;

/// <summary>
/// The rules of « Encaissement » and « À encaisser » that need no window, shared by the
/// workstation's dialog and the web's panel: who sees the panel, when it opens by itself, the method
/// it opens on, what it refuses before the service is asked, the change to give back on cash, and
/// the till to open before the first patient pays.
/// <para>
/// The right is the one that guards payments and deposits everywhere else
/// (<c>billing.payment.record</c>). Without it the counter still registers: the panel never opens,
/// the « Encaisser » buttons are hidden on the web and greyed at the workstation, and the money is
/// taken later at the billing desk (« Plus tard »).
/// </para>
/// </summary>
public static class CounterCollectionRules
{
    /// <summary>« Tout payer », as the workstation's key and the web's.</summary>
    public const string FullShortcut = "F9";

    /// <summary>« Acompte ».</summary>
    public const string DepositShortcut = "F10";

    /// <summary>Whether the user takes money at the counter.</summary>
    public static bool CanCollect(Func<string, bool> has)
    {
        ArgumentNullException.ThrowIfNull(has);
        return has(Permissions.Billing.RecordPayment);
    }

    /// <summary>
    /// Whether « Encaisser » is offered on a file whose payment status is known (the report counter,
    /// « À encaisser »): to whoever takes payments, while the patient owes something on it.
    /// </summary>
    public static bool Offers(OrderPayment? payment, Func<string, bool> has) =>
        CanCollect(has) && payment is { IsSettled: false, PatientDue: > 0m };

    /// <summary>
    /// Whether the panel opens by itself after « Enregistrer »: for whoever takes payments, when the
    /// patient has something to pay. Otherwise the registration ends as it always has.
    /// </summary>
    public static bool OpensAfterRegistration(Func<string, bool> has, Money dueAtCounter) =>
        CanCollect(has) && dueAtCounter.Amount > 0m;

    /// <summary>The method the panel opens on: the user's last one when it is a counter method, else cash.</summary>
    public static PaymentMethod DefaultMethod(PaymentMethod? last) =>
        last is { } method && BillingScreenRules.CounterMethods.Contains(method) ? method : PaymentMethod.Cash;

    /// <summary>The reference a method cannot do without, as its message key; null when none is missing.</summary>
    public static string? MissingReference(PaymentMethod method, string? reference)
    {
        if (!string.IsNullOrWhiteSpace(reference)) return null;

        return method switch
        {
            PaymentMethod.Cheque or PaymentMethod.BillOfExchange => "Payment_ReferenceRequired",
            PaymentMethod.BankTransfer => "Payment_TransferReferenceRequired",
            _ => null
        };
    }

    /// <summary>
    /// Checks the panel before the money is taken: for « Acompte » an amount that reads, is positive
    /// and within what is due; for either the reference the method needs; for cash, an amount
    /// tendered that reads and covers what is taken (left empty, the exact sum is assumed).
    /// </summary>
    public static FieldRefusal? Check(CollectionKind kind, string? amountText, decimal due, bool invoiced,
                                      PaymentMethod method, string? reference, string? tenderedText)
    {
        if (kind == CollectionKind.Deposit
            && BillingScreenRules.CheckPayment(amountText, due, deposit: !invoiced, method, reference) is { } refusal)
            return refusal;

        if (MissingReference(method, reference) is { } missing) return new FieldRefusal("reference", missing);

        if (method != PaymentMethod.Cash || string.IsNullOrWhiteSpace(tenderedText)) return null;

        if (TypedNumber.Parse(tenderedText) is not { } tendered)
            return new FieldRefusal("tendered", "Result_NotANumber", Typed: tenderedText.Trim());

        var amount = Amount(kind, amountText, due) ?? 0m;
        return tendered < amount ? new FieldRefusal("tendered", "Collect_TenderedShort", Amount: amount) : null;
    }

    /// <summary>The amount the panel takes: the whole due for « Tout payer », the amount typed for « Acompte ».</summary>
    public static decimal? Amount(CollectionKind kind, string? amountText, decimal due) =>
        kind == CollectionKind.Full
            ? due
            : TypedNumber.Parse(amountText) is { } typed ? Money.Round(typed) : null;

    /// <summary>The change to hand back on cash, or null when it does not apply or the tendered sum does not read or cover it.</summary>
    public static decimal? Change(PaymentMethod method, string? tenderedText, decimal amount) =>
        method == PaymentMethod.Cash && TypedNumber.Parse(tenderedText) is { } tendered && tendered >= amount
            ? Money.Round(tendered - amount)
            : null;

    /// <summary>Whether the counter offers to open the till: for whoever takes payments, while none is open.</summary>
    public static bool OffersTillOpening(TillState state, Func<string, bool> has) =>
        state == TillState.NotOpen && CanCollect(has);

    /// <summary>The status line's message key for what was done.</summary>
    public static string DoneKey(CollectionDone done)
    {
        ArgumentNullException.ThrowIfNull(done);

        if (done.InvoiceIssued) return "Collect.DoneInvoiced";
        if (done.InvoiceId is null) return "Collect.DoneDeposit";
        return done.RemainingDue > 0m ? "Collect.DonePartly" : "Collect.DonePaid";
    }

    /// <summary>The caption of a queue line's state: not invoiced yet, or the balance of its invoice.</summary>
    public static string StateKey(OrderPaymentState state) =>
        state == OrderPaymentState.NotInvoiced ? "Collect.StateNotInvoiced" : "Collect.StateDue";

    /// <summary>What the queue still has to collect, in all.</summary>
    public static decimal QueueTotal(IEnumerable<CollectionQueueRow> rows) => Money.Round(rows.Sum(r => r.Due));
}
