using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Relations;

/// <summary>
/// A price negotiated in a convention for one analysis: either a fixed price in dirhams or a rate
/// off the laboratory's own price, between two dates. The patients the convention covers are
/// charged it in place of the price list.
/// <para>
/// Kept apart from the payers' tariffs (<c>AnalysisTestTariff</c> with a payer): a payer's tariff
/// decides who pays and on which reference base the claim is split, while a convention partner —
/// a clinic, a company, an association — is not the patient's payer. Its price only lowers what the
/// laboratory charges, whoever pays it.
/// </para>
/// </summary>
public sealed class PartnershipPrice : AuditableAggregateRoot, ISoftDeletable
{
    /// <summary>The highest price the screens accept, as for any amount.</summary>
    public const decimal PriceLimit = 999_999.99m;

    private PartnershipPrice() { }

    public PartnershipPrice(Guid partnershipId, Guid analysisTestId, DateOnly validFrom)
    {
        PartnershipId = partnershipId;
        AnalysisTestId = analysisTestId;
        ValidFrom = validFrom;
    }

    public Guid PartnershipId { get; set; }
    public Guid AnalysisTestId { get; set; }

    /// <summary>The price charged, in dirhams. Exclusive with <see cref="DiscountPercent"/>.</summary>
    public decimal? FixedPrice { get; set; }

    /// <summary>The rate off the laboratory's price, in percent. Exclusive with <see cref="FixedPrice"/>.</summary>
    public decimal? DiscountPercent { get; set; }

    public DateOnly ValidFrom { get; set; }

    /// <summary>Open-ended when empty.</summary>
    public DateOnly? ValidUntil { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public bool IsValidOn(DateOnly day) => day >= ValidFrom && (ValidUntil is null || day <= ValidUntil);

    /// <summary>
    /// The negotiated unit price for an analysis listed at <paramref name="listPrice"/>: the fixed price,
    /// or the list price less the rate, rounded to the centime.
    /// </summary>
    public decimal PriceFor(decimal listPrice) =>
        FixedPrice is { } price
            ? Money.Round(price)
            : Money.Round(listPrice * (1m - Math.Clamp(DiscountPercent ?? 0m, 0m, 100m) / 100m));

    /// <summary>Whether this entry and <paramref name="other"/> price the same analysis on a common day.</summary>
    public bool Overlaps(PartnershipPrice other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Id == Id || other.AnalysisTestId != AnalysisTestId || other.PartnershipId != PartnershipId) return false;

        var thisEnd = ValidUntil ?? DateOnly.MaxValue;
        var otherEnd = other.ValidUntil ?? DateOnly.MaxValue;
        return ValidFrom <= otherEnd && other.ValidFrom <= thisEnd;
    }

    public Result Validate()
    {
        var errors = new List<Error>();

        if (AnalysisTestId == Guid.Empty) errors.Add(Error.Validation("PartnershipPrice_AnalysisRequired"));
        if (FixedPrice is null == DiscountPercent is null) errors.Add(Error.Validation("PartnershipPrice_PriceOrPercent"));
        if (FixedPrice is < 0m or > PriceLimit) errors.Add(Error.Validation("PartnershipPrice_PriceOutOfRange"));
        if (DiscountPercent is <= 0m or > 100m) errors.Add(Error.Validation("PartnershipPrice_PercentOutOfRange"));
        if (ValidUntil is { } until && until < ValidFrom) errors.Add(Error.Validation("PartnershipPrice_UntilBeforeFrom"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}
