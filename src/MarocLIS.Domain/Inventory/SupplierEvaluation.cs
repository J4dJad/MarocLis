namespace MarocLIS.Domain.Inventory;

/// <summary>
/// One delivery from a supplier. <see cref="DueOn"/> is the date the order asked for (the expected
/// delivery date, or the order date plus the supplier's lead time); null for a receipt made without
/// a purchase order, which counts as a delivery but cannot be on time or late.
/// </summary>
public sealed record SupplierDelivery(DateOnly ReceivedOn, DateOnly? DueOn)
{
    public int DelayDays => DueOn is { } due ? Math.Max(0, ReceivedOn.DayNumber - due.DayNumber) : 0;
    public bool IsOnTime => DueOn is { } due && ReceivedOn <= due;
}

/// <summary>The year's indicators for one supplier, and the score they suggest.</summary>
public sealed record SupplierKpis(
    int Deliveries,
    int DatedDeliveries,
    int OnTimeDeliveries,
    decimal? OnTimeRatePercent,
    decimal? AverageDelayDays,
    int RefusedDeliveries,
    int NonConformities,
    int? ProposedScore);

/// <summary>
/// The computed supplier evaluation (ISO 15189 §6.8, external products and services).
/// <para>
/// Proposed score, out of 100:
/// <c>50 × on-time rate + 30 × (1 − refused deliveries ÷ (deliveries + refused)) + max(0, 20 − 5 × NC)</c>.
/// Punctuality weighs most because a late reagent stops an analysis; a refused delivery costs the
/// conformity share; each non-conformity attributed to the supplier costs 5 points, down to 0. With
/// no dated delivery the punctuality share is given in full (nothing shows the supplier late); with
/// no delivery and no refusal at all there is no evidence, and no score is proposed.
/// </para>
/// </summary>
public static class SupplierEvaluation
{
    public const string FormulaFr =
        "Note = 50 × taux de livraison à l'heure + 30 × (1 − livraisons refusées ÷ (livraisons + refusées)) + max(0 ; 20 − 5 × NC)";

    public static SupplierKpis Compute(IReadOnlyCollection<SupplierDelivery> deliveries, int refused,
                                       int nonConformities)
    {
        var dated = deliveries.Where(d => d.DueOn is not null).ToList();
        var onTime = dated.Count(d => d.IsOnTime);

        decimal? onTimeRate = dated.Count == 0 ? null : Math.Round(100m * onTime / dated.Count, 1);
        decimal? averageDelay = dated.Count == 0 ? null : Math.Round((decimal)dated.Average(d => d.DelayDays), 1);

        int? score = null;

        if (deliveries.Count + refused > 0)
        {
            var punctuality = dated.Count == 0 ? 1m : (decimal)onTime / dated.Count;
            var conformity = 1m - (decimal)refused / (deliveries.Count + refused);
            var ncShare = Math.Max(0, 20 - 5 * nonConformities);

            score = (int)Math.Round(50m * punctuality + 30m * conformity + ncShare,
                                    MidpointRounding.AwayFromZero);
        }

        return new SupplierKpis(deliveries.Count, dated.Count, onTime, onTimeRate, averageDelay,
                                refused, nonConformities, score);
    }
}
