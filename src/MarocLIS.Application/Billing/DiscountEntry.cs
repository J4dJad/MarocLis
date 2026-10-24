using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Billing;

/// <summary>What the discount box of the issue dialog comes to as it is typed.</summary>
/// <param name="Discount">The reduction in dirhams; null while the box is neither an amount nor a percentage.</param>
/// <param name="IsValid">Read, and between nothing and the gross.</param>
/// <param name="AboveCap">Beyond the ceiling in force: another user's approval is asked.</param>
/// <param name="Ceiling">The ceiling in force on this invoice, in dirhams.</param>
/// <param name="AmountCapApplies">The ceiling is the amount in dirhams rather than the percentage.</param>
/// <param name="IsPercent">The box holds a percentage, shown with the dirhams it comes to.</param>
/// <param name="Net">What the invoice will carry once the reduction is taken off.</param>
public sealed record DiscountReading(decimal? Discount, bool IsValid, bool AboveCap, decimal Ceiling,
                                     bool AmountCapApplies, bool IsPercent, decimal Net);

/// <summary>
/// The discount granted as an invoice is issued, as the operator types it: an amount in dirhams
/// (« 50 », « 74,70 ») or a percentage of the gross (« 10 % », or with the Arabic sign « 10٪ »). A
/// discount is almost always agreed as a final amount — "make it 300" — so the net is shown live.
/// The workstation's issue dialog and the web's read the box the same way.
/// </summary>
public static class DiscountEntry
{
    /// <summary>The longest approver name and password the boxes take.</summary>
    public const int ApproverMaxLength = 100;
    public const int ApproverPasswordMaxLength = 128;

    /// <summary>
    /// Reads the discount box. A percentage becomes the amount it stands for, rounded to the
    /// centime, since the invoice records the reduction itself. Empty means no discount (zero);
    /// null is text that is neither, or a percentage outside 0–100. Both separators are taken: the
    /// keypad of a counter terminal produces a full stop and the French keyboard a comma.
    /// </summary>
    public static decimal? Parse(string? text, decimal gross)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;

        var trimmed = text.Trim();
        var last = trimmed[^1];

        if (last is not ('%' or '٪')) return TypedNumber.Parse(trimmed);

        var percent = TypedNumber.Parse(trimmed[..^1]);
        if (percent is not { } rate || rate < 0m || rate > 100m) return null;

        return Math.Round(gross * rate / 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>True when the discount box holds a percentage rather than an amount.</summary>
    public static bool IsPercent(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim()[^1] is '%' or '٪';

    /// <summary>The box as the dialog shows it under itself: the net, or why it is not one, or the ceiling passed.</summary>
    public static DiscountReading Read(string? text, decimal gross, DiscountCapSettings cap)
    {
        ArgumentNullException.ThrowIfNull(cap);

        var discount = Parse(text, gross);
        var valid = discount is { } d && d >= 0m && d <= gross;
        var above = valid && discount > 0m && cap.Exceeds(discount!.Value, gross);

        return new DiscountReading(discount, valid, above, cap.CapFor(gross), cap.AmountCapApplies(gross),
                                   IsPercent(text), valid ? gross - discount!.Value : gross);
    }

    /// <summary>
    /// Checks the discount before the invoice is issued: a reduction that reads, is not negative
    /// and not above the gross, carries its reason, and — beyond the ceiling — the approver's name
    /// and password. Said next to the field, before a transaction is opened; the domain and the
    /// service check the same.
    /// </summary>
    public static FieldRefusal? Check(string? text, decimal gross, DiscountCapSettings cap, string? reason,
                                      string? approver, string? approverPassword)
    {
        ArgumentNullException.ThrowIfNull(cap);

        if (Parse(text, gross) is not { } discount) return new FieldRefusal("discount", "Billing.DiscountInvalid");
        if (discount < 0m) return new FieldRefusal("discount", "Invoice_DiscountNegative");
        if (discount > gross) return new FieldRefusal("discount", "Invoice_DiscountExceedsGross");

        if (discount > 0m && string.IsNullOrWhiteSpace(reason))
            return new FieldRefusal("reason", "Invoice_DiscountReasonRequired");

        if (Read(text, gross, cap).AboveCap
            && (string.IsNullOrWhiteSpace(approver) || string.IsNullOrEmpty(approverPassword)))
            return new FieldRefusal(string.IsNullOrWhiteSpace(approver) ? "approver" : "password",
                                    "Billing.DiscountApproverRequired");

        return null;
    }

    /// <summary>The discount the service is given: null rather than zero when none is granted, so it skips the check.</summary>
    public static decimal? Granted(string? text, decimal gross) => Parse(text, gross) is { } value and > 0m ? value : null;

    /// <summary>The approver typed in, when the discount is beyond the ceiling; null otherwise.</summary>
    public static DiscountApproval? Approval(string? text, decimal gross, DiscountCapSettings cap, string? approver,
                                             string? approverPassword) =>
        Read(text, gross, cap).AboveCap ? new DiscountApproval(approver?.Trim() ?? string.Empty, approverPassword ?? string.Empty) : null;

    /// <summary>The net the invoice will carry, rounded as money.</summary>
    public static Money Net(decimal gross, decimal discount) => Money.Of(gross - discount);
}
