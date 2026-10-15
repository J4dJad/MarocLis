using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Billing;

/// <summary>
/// Cash taken out of a till during a shift to be banked (prélèvement de caisse): one deposit slip,
/// so one line on the bank statement. Part of its <see cref="CashSession"/>.
/// </summary>
public sealed class CashWithdrawal : Entity
{
    public const int ReasonMaxLength = 500;

    private CashWithdrawal() { }

    internal CashWithdrawal(Guid cashSessionId, decimal amount, DateTimeOffset at, string? reason)
    {
        CashSessionId = cashSessionId;
        Amount = Money.Round(amount);
        At = at;
        var text = reason?.Trim();
        Reason = string.IsNullOrEmpty(text) ? null : text.Length > ReasonMaxLength ? text[..ReasonMaxLength] : text;
    }

    public Guid CashSessionId { get; private set; }
    public decimal Amount { get; private set; }
    public DateTimeOffset At { get; private set; }
    public string? Reason { get; private set; }
}
