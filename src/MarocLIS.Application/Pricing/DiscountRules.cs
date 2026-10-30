using MarocLIS.Application.Billing;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Pricing;

/// <summary>A discount rate an order may take, and where it comes from.</summary>
/// <param name="Label">The convention's partner, the prescriber's name or the profile's name; empty for a rate typed.</param>
public sealed record DiscountOffer(DiscountSource Source, decimal Percent, string Label,
                                   Guid? PartnershipId = null, Guid? ProfileId = null);

/// <summary>
/// Which discount an order gets. The rules, in the order they apply:
/// <list type="number">
/// <item><b>The convention.</b> The one chosen at the counter when it is in force on the day;
/// otherwise the convention in force on the day whose partner is the order's prescriber (the
/// partner's record in the prescriber file); among several, the highest rate, then the most
/// recent. « Sans convention » takes none.</item>
/// <item><b>The automatic rate.</b> The higher of the convention's rate and the rate on the
/// prescriber's file (an active prescriber); equal, the convention's.</item>
/// <item><b>The order's rate.</b> The higher of the automatic rate and the counter's (a profile
/// or a rate typed): the two are never added. Equal, the automatic one, which needs no
/// approval.</item>
/// <item><b>Negotiated prices.</b> An analysis with a price negotiated in the convention costs the
/// lower of that price and the list price less the order's rate — again never both
/// (<see cref="ActPricingCalculator"/>). An analysis billed inside a profile sold at a package
/// price keeps its share of the package.</item>
/// <item><b>The ceiling.</b> Only the counter's rate is held to the ceiling of Paramètres ›
/// Factures, on the part of the discount it gives; a convention's terms were agreed once, by the
/// laboratory.</item>
/// </list>
/// </summary>
public static class DiscountRules
{
    /// <summary>The convention an order is priced under (rule 1), or null.</summary>
    public static Partnership? ConventionFor(IEnumerable<Partnership> partnerships, Guid siteId, Guid? prescriberId,
                                             Guid? chosenId, bool withoutConvention, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(partnerships);
        if (withoutConvention) return null;

        var inForce = partnerships.Where(p => p.SiteId == siteId && p.IsInForce(day));

        if (chosenId is { } chosen) return inForce.FirstOrDefault(p => p.Id == chosen);

        return prescriberId is { } prescriber
            ? inForce.Where(p => p.PrescriberId == prescriber)
                     .OrderByDescending(p => p.DiscountPercent ?? 0m)
                     .ThenByDescending(p => p.StartDate)
                     .FirstOrDefault()
            : null;
    }

    /// <summary>The automatic rate (rule 2): the convention's or the prescriber's, the higher; null when neither gives one.</summary>
    public static DiscountOffer? Automatic(Partnership? convention, Prescriber? prescriber, DateOnly day)
    {
        DiscountOffer? fromConvention = convention is { DiscountPercent: > 0m } && convention.IsInForce(day)
            ? new DiscountOffer(DiscountSource.Convention, Math.Min(convention.DiscountPercent.Value, 100m),
                                convention.PartnerName, PartnershipId: convention.Id)
            : null;

        DiscountOffer? fromPrescriber = prescriber is { IsActive: true, DiscountPercent: > 0m }
            ? new DiscountOffer(DiscountSource.Prescriber, Math.Min(prescriber.DiscountPercent.Value, 100m), prescriber.FullName)
            : null;

        if (fromConvention is null) return fromPrescriber;
        if (fromPrescriber is null) return fromConvention;
        return fromPrescriber.Percent > fromConvention.Percent ? fromPrescriber : fromConvention;
    }

    /// <summary>The order's rate (rule 3): the higher of the automatic and the counter's, never both.</summary>
    public static DiscountOffer? Applied(DiscountOffer? automatic, DiscountOffer? counter)
    {
        if (automatic is null) return counter is { Percent: > 0m } ? counter : null;
        if (counter is not { Percent: > 0m }) return automatic;
        return counter.Percent > automatic.Percent ? counter : automatic;
    }

    /// <summary>The negotiated price of an analysis on a day: the entry in force, the latest to start.</summary>
    public static PartnershipPrice? NegotiatedFor(IEnumerable<PartnershipPrice> prices, Guid analysisTestId, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return prices.Where(p => p.AnalysisTestId == analysisTestId && !p.IsDeleted && p.IsValidOn(day))
                     .OrderByDescending(p => p.ValidFrom)
                     .FirstOrDefault();
    }

    /// <summary>Whether the order's discount needs another user's approval (rule 5).</summary>
    public static bool NeedsApproval(DiscountOffer? applied, OrderPricing pricing, DiscountCapSettings cap)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(cap);
        return applied is not null && IsDiscretionary(applied.Source)
               && cap.Exceeds(pricing.RateDiscount.Amount, pricing.Gross.Amount);
    }

    /// <summary>The counter's own discount: a profile or a rate typed, which the ceiling and the right to grant discounts govern.</summary>
    public static bool IsDiscretionary(DiscountSource source) => source is DiscountSource.Profile or DiscountSource.Manual;

    /// <summary>
    /// The reason an automatic discount is recorded with on the order, in French like the rest of the
    /// trail: « Convention : Clinique Al Amal », « Prescripteur : Dr ALAMI Karim ».
    /// </summary>
    public static string ReasonFor(DiscountOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return offer.Source switch
        {
            DiscountSource.Convention => $"Convention : {offer.Label}",
            DiscountSource.Prescriber => $"Prescripteur : {offer.Label}",
            DiscountSource.Profile => $"Profil : {offer.Label}",
            _ => offer.Label
        };
    }
}
