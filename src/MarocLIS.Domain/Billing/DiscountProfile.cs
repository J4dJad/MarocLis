using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Billing;

/// <summary>
/// A discount the front desk may grant by name rather than by a figure typed on the spot:
/// « Personnel », « Famille », « Médecin », « Campagne », « Cas social ». Each carries its rate, can
/// be switched off, and may hold only between two dates (a screening campaign). Chosen at
/// registration with a reason; above the ceiling of Paramètres › Factures it still needs another
/// user's approval, like any discount.
/// </summary>
public sealed class DiscountProfile : AuditableAggregateRoot
{
    public const int NameMaxLength = 80;

    private DiscountProfile() { }

    public DiscountProfile(string name, decimal percent)
    {
        Name = name;
        Percent = percent;
    }

    public string Name { get; set; } = string.Empty;

    /// <summary>The rate granted on the laboratory's price, in percent (above 0, at most 100).</summary>
    public decimal Percent { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>First day the profile may be chosen; open when empty.</summary>
    public DateOnly? ValidFrom { get; set; }

    /// <summary>Last day the profile may be chosen; open when empty.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>Whether the counter may choose it on <paramref name="day"/>: active and within its dates.</summary>
    public bool IsAvailableOn(DateOnly day) =>
        IsActive && (ValidFrom is null || day >= ValidFrom) && (ValidUntil is null || day <= ValidUntil);

    public Result Validate()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(Name)) errors.Add(Error.Validation("DiscountProfile_NameRequired"));
        else if (Name.Trim().Length > NameMaxLength) errors.Add(Error.Validation("Relations_TextTooLong", NameMaxLength));
        if (Percent is <= 0m or > 100m) errors.Add(Error.Validation("DiscountProfile_PercentOutOfRange"));
        if (ValidFrom is { } from && ValidUntil is { } until && until < from)
            errors.Add(Error.Validation("DiscountProfile_UntilBeforeFrom"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}
