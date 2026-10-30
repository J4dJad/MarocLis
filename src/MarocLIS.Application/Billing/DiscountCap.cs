using MarocLIS.Domain.Common;
using MarocLIS.Application.Abstractions;

namespace MarocLIS.Application.Billing;

/// <summary>
/// The ceiling on the discount granted when an invoice is issued, held as installation settings
/// beside the invoice print options (« Paramètres › Factures »).
/// <para>
/// A percentage of the gross, and optionally an amount in dirhams: the ceiling in force is the
/// lower of the two. A discount up to it needs only the right to grant discounts and a reason; a
/// discount above it is granted only with the approval of another user holding
/// <c>billing.discount.approve</c>, given on the spot with their user name and password.
/// </para>
/// </summary>
public sealed record DiscountCapSettings
{
    public const string Prefix = "Billing.DiscountCap.";
    public const string MaxPercentKey = Prefix + "MaxPercent";
    public const string MaxAmountKey = Prefix + "MaxAmount";

    /// <summary>The highest amount a ceiling may be given, as for any amount the screens accept.</summary>
    public const decimal AmountLimit = 999_999_999.99m;

    /// <summary>10 % of the gross and no ceiling in dirhams: what the laboratory asked for.</summary>
    public static DiscountCapSettings Default { get; } = new();

    /// <summary>The highest discount, as a percentage of the gross (0 to 100).</summary>
    public decimal MaxPercent { get; init; } = 10m;

    /// <summary>The highest discount in dirhams; null for no ceiling in dirhams.</summary>
    public decimal? MaxAmount { get; init; }

    /// <summary>The ceiling in force on an invoice of <paramref name="gross"/>: the lower of the two.</summary>
    public decimal CapFor(decimal gross)
    {
        var byPercent = Money.Round(gross * MaxPercent / 100m);
        return MaxAmount is { } amount ? Math.Min(byPercent, Money.Round(amount)) : byPercent;
    }

    /// <summary>True when the ceiling in force is the amount in dirhams rather than the percentage.</summary>
    public bool AmountCapApplies(decimal gross) =>
        MaxAmount is { } amount && Money.Round(amount) < Money.Round(gross * MaxPercent / 100m);

    /// <summary>A discount above the ceiling, which needs an approval.</summary>
    public bool Exceeds(decimal discount, decimal gross) => Money.Round(discount) > CapFor(gross);

    public Result Validate()
    {
        if (MaxPercent < 0m || MaxPercent > 100m)
            return Result.Failure(Error.Validation("DiscountCap_PercentOutOfRange"));
        if (MaxAmount is { } amount && (amount < 0m || amount > AmountLimit))
            return Result.Failure(Error.Validation("DiscountCap_AmountOutOfRange"));
        return Result.Success();
    }

    public static DiscountCapSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        static decimal? Read(IReadOnlyDictionary<string, string?> values, string key) =>
            values.TryGetValue(key, out var text)
            && decimal.TryParse(text?.Trim(), System.Globalization.NumberStyles.Number,
                                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        var defaults = Default;

        // A value typed by hand in the settings list that cannot be a ceiling falls back to the
        // default rather than lifting the ceiling: a ceiling is a control, and an unreadable one
        // keeps controlling.
        var percent = Read(values, MaxPercentKey) is { } p and >= 0m and <= 100m ? p : defaults.MaxPercent;
        var amount = Read(values, MaxAmountKey) is { } a and >= 0m and <= AmountLimit ? a : (decimal?)null;

        return new DiscountCapSettings { MaxPercent = percent, MaxAmount = amount };
    }

    public IReadOnlyList<(string Key, string Value)> ToValues() =>
    [
        (MaxPercentKey, MaxPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
        (MaxAmountKey, MaxAmount is { } amount
            ? amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty)
    ];
}

/// <summary>
/// The user name and password of whoever approves a discount above the ceiling, typed on the spot
/// at the counter. Checked like a sign-in, without signing that person in.
/// </summary>
public sealed record DiscountApproval(string UserName, string Password)
{
    // The password stays out of logs and of the audit trail if the record is ever printed.
    public override string ToString() => $"DiscountApproval {{ UserName = {UserName} }}";

    /// <summary>
    /// Whether a refusal is the approval's — the approver's name or password, the approver being the
    /// clerk, their account locked — rather than the invoice's: the same dialog is shown again, the
    /// discount and the reason still filled in, and only the approver's password is asked again.
    /// </summary>
    public static bool IsRefusal(string resourceKey) =>
        resourceKey.StartsWith("Approval_", StringComparison.Ordinal)
        || resourceKey is "Invoice_DiscountApprovalRequired" or "Auth_AccountLockedOut";
}

/// <summary>
/// The approval on the spot of a discount above the ceiling, wherever it is granted: when an
/// invoice is issued, and when a request is registered with a discount profile or a rate typed.
/// Another active user holding <c>billing.discount.approve</c> types their name and password; the
/// operator's own name is refused before anything is checked, whatever their rights.
/// </summary>
public static class DiscountApprovals
{
    /// <summary>The refusal of a discount above the ceiling without a valid approval: the ceiling in dirhams.</summary>
    public static Error Required(DiscountCapSettings cap, decimal gross, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(cap);
        return Error.Forbidden("Invoice_DiscountApprovalRequired", Money.Of(cap.CapFor(gross)).Format(language));
    }

    /// <summary>Checks the approver like a sign-in, without signing them in; a wrong password counts towards their lockout.</summary>
    public static async Task<Result<ApproverIdentity>> VerifyAsync(
        IAuthenticationService? authentication, ICurrentUser currentUser,
        DiscountApproval? approval, Error required, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        if (approval is null || string.IsNullOrWhiteSpace(approval.UserName) || string.IsNullOrEmpty(approval.Password))
            return Result.Failure<ApproverIdentity>(required);

        if (string.Equals(approval.UserName.Trim(), currentUser.UserName, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<ApproverIdentity>(Error.Forbidden("Approval_SelfNotAllowed"));

        if (authentication is null) return Result.Failure<ApproverIdentity>(required);

        var verified = await authentication.VerifyApproverAsync(
            approval.UserName, approval.Password, Domain.Identity.Permissions.Billing.ApproveDiscount, ct);

        if (verified.IsFailure) return verified;

        if (verified.Value.UserId == currentUser.UserId)
            return Result.Failure<ApproverIdentity>(Error.Forbidden("Approval_SelfNotAllowed"));

        return verified;
    }

    /// <summary>What the audit trail says of an approved discount: who, when, how much, under which ceiling.</summary>
    public static string Describe(ApproverIdentity approver, DiscountCapSettings cap, decimal discount, decimal gross,
                                  DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(cap);

        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        var ceiling = cap.MaxAmount is { } max
            ? $"{cap.MaxPercent.ToString("0.##", fr)} % du brut, maximum {max.ToString("N2", fr)} DH"
            : $"{cap.MaxPercent.ToString("0.##", fr)} % du brut";

        return $" ; remise de {discount.ToString("N2", fr)} DH au-delà du plafond de "
               + $"{cap.CapFor(gross).ToString("N2", fr)} DH ({ceiling}) approuvée par "
               + $"{approver.FullName} ({approver.UserName}) le {at.ToString("dd/MM/yyyy HH:mm", fr)}";
    }
}
