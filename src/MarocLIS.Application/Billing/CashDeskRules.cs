using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Billing;

/// <summary>What « Caisse » offers by right: opening a till takes payments, closing it and banking from it close it.</summary>
public sealed record CashDeskActions(bool Open, bool Close, bool Withdraw);

/// <summary>The difference between the drawer counted and the cash expected, as the operator types the count.</summary>
/// <param name="Variance">Counted less expected, signed: a surplus and a shortfall are different problems.</param>
/// <param name="RequiresExplanation">Beyond the tolerance: the till closes only once it is explained.</param>
public sealed record CashVariance(decimal Variance, bool RequiresExplanation);

/// <summary>
/// The rules of « Caisse » that need no window: the takings the breakdown lists, the variance as
/// the count is typed, and what opening, banking and closing refuse before the service is asked.
/// The workstation's view and the web's screen apply the same ones.
/// <para>
/// The closing figure is the point of the screen. The operator counts the drawer and types what
/// they counted; beyond the domain's tolerance the variance must be explained before the session
/// closes. That is deliberate friction: variance by operator per shift is exactly what an internal
/// audit examines, and a shortfall that can be clicked away is not a control.
/// </para>
/// </summary>
public static class CashDeskRules
{
    /// <summary>The variance the laboratory tolerates without an explanation: the domain's, which enforces it.</summary>
    public const decimal ToleranceMad = CashSession.VarianceToleranceMad;

    /// <summary>An amount, not free text: a run of digits beyond what the amount columns hold failed the save.</summary>
    public const int AmountMaxLength = 12;

    /// <summary>The longest explanation its column holds.</summary>
    public const int ExplanationMaxLength = 1000;

    public static CashDeskActions Actions(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var close = user.Has(Permissions.Billing.CloseCashSession);
        return new CashDeskActions(user.Has(Permissions.Billing.RecordPayment), close, close);
    }

    /// <summary>
    /// The lines of the takings, in the breakdown's order: the float, each method, the cash refunds
    /// and the banking taken out (when there were any, negative), then the cash the drawer should
    /// hold. Only cash is reconciled against the drawer; the other methods are reported for the
    /// shift record but settle through the bank.
    /// </summary>
    public static IReadOnlyList<(string Key, decimal Amount)> Takings(CashSessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var lines = new List<(string, decimal)>
        {
            ("PaymentMethod.Cash", summary.CashReceipts),
            ("PaymentMethod.Cheque", summary.ChequeReceipts),
            ("PaymentMethod.Card", summary.CardReceipts),
            ("PaymentMethod.BankTransfer", summary.TransferReceipts),
            ("PaymentMethod.BillOfExchange", summary.BillOfExchangeReceipts),
            ("PaymentMethod.MobilePayment", summary.MobileReceipts)
        };

        if (summary.CashRefunds > 0m) lines.Add(("Payment.Refunds", -summary.CashRefunds));
        if (summary.CashWithdrawals > 0m) lines.Add(("Payment.Withdrawals", -summary.CashWithdrawals));
        return lines;
    }

    /// <summary>The variance of the count typed, or null while the box reads no number.</summary>
    public static CashVariance? Variance(string? countedText, decimal expected)
    {
        if (TypedNumber.Parse(countedText) is not { } counted) return null;

        var variance = Money.Round(counted - expected);
        return new CashVariance(variance, CashSession.RequiresExplanation(variance));
    }

    /// <summary>The opening float: a number, nothing negative.</summary>
    public static FieldRefusal? CheckOpening(string? floatText) => CheckCount("float", floatText);

    /// <summary>
    /// The closing: a count that reads and is not negative, and the explanation of a variance
    /// beyond the tolerance (the message names the variance).
    /// </summary>
    public static FieldRefusal? CheckClosing(string? countedText, decimal expected, string? explanation)
    {
        if (CheckCount("counted", countedText) is { } refusal) return refusal;

        var variance = Variance(countedText, expected)!;
        return variance.RequiresExplanation && string.IsNullOrWhiteSpace(explanation)
            ? new FieldRefusal("explanation", "CashSession_VarianceExplanationRequired", Amount: variance.Variance)
            : null;
    }

    /// <summary>
    /// Cash taken out of the drawer and banked mid-shift: a positive amount. It comes out of the
    /// expected closing cash, which is the point — money at the bank is not money in the till.
    /// </summary>
    public static FieldRefusal? CheckWithdrawal(string? amountText) =>
        TypedNumber.Parse(amountText) is { } amount && amount > 0m
            ? null
            : new FieldRefusal("amount", "Result_NotANumber", Typed: amountText?.Trim() ?? string.Empty);

    private static FieldRefusal? CheckCount(string field, string? text)
    {
        if (TypedNumber.IsUnreadable(text)) return new FieldRefusal(field, "Result_NotANumber", Typed: text!.Trim());
        if (TypedNumber.Parse(text) is not { } value || value < 0m) return new FieldRefusal(field, "Payment_AmountMustBePositive");
        return null;
    }
}
