using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Pricing;

/// <summary>
/// Everything needed to price one act, already resolved from the catalogue and the patient's
/// entitlement. A plain record so the calculation can be unit-tested against worked examples from
/// a real AMO settlement statement, with no database in sight.
/// </summary>
public sealed record ActPricingInput
{
    public required Guid AnalysisTestId { get; init; }
    public required string AnalysisCode { get; init; }
    public required string Description { get; init; }
    public string? DescriptionArabic { get; init; }

    public int Quantity { get; init; } = 1;
    public DateOnly ServiceDate { get; init; }

    /// <summary>Act code in the national nomenclature; carried verbatim onto the claim.</summary>
    public string? NationalActCode { get; init; }

    /// <summary>Letter-key of the act — "B" for a biology act.</summary>
    public string LetterKey { get; init; } = "B";

    /// <summary>Coefficient attached to the act in the nomenclature.</summary>
    public decimal Coefficient { get; init; }

    /// <summary>Value of the letter-key in dirham, as in force on the service date.</summary>
    public decimal LetterKeyValue { get; init; }

    /// <summary>
    /// Price the laboratory charges. For an AMO payer this is normally the reference tariff; for a
    /// self-paying patient it is the laboratory's own price, which may sit above it.
    /// </summary>
    public decimal ChargedUnitPrice { get; init; }

    /// <summary>
    /// The unit price negotiated in the convention the order is priced under, when it has one for
    /// this analysis. Charged instead of <see cref="ChargedUnitPrice"/> unless the order's rate gives
    /// the patient less to pay (<see cref="ActPricingCalculator"/>): the two never add up.
    /// </summary>
    public decimal? NegotiatedUnitPrice { get; init; }

    /// <summary>Biology acts are VAT-exempt in Morocco; non-exempt items carry their own rate.</summary>
    public bool IsVatExempt { get; init; } = true;
    public decimal VatRate { get; init; }

    public Guid? LabOrderId { get; init; }
    public Guid? LabOrderItemId { get; init; }
    public Guid? TestPanelId { get; init; }

    /// <summary>Acts added automatically by a reflex rule that the laboratory absorbs.</summary>
    public bool IsChargeable { get; init; } = true;
}

/// <summary>The coverage in force, snapshotted from the order so a later change cannot reprice it.</summary>
public sealed record CoverageContext
{
    public static readonly CoverageContext SelfPay = new();

    public Guid? PayerId { get; init; }

    /// <summary>Fraction of the reference base the payer reimburses; 0.70 for standard AMO biology.</summary>
    public decimal ReimbursementRate { get; init; }

    /// <summary>
    /// Whether the payer settles on the national reference tariff rather than on the laboratory's
    /// price list. True for every AMO scheme — and the reason a patient can owe money even at a
    /// 100 % reimbursement rate.
    /// </summary>
    public bool UsesReferenceTariff { get; init; } = true;

    /// <summary>Whether the laboratory bills the payer directly instead of the patient.</summary>
    public bool DirectBilling { get; init; }

    public bool IsCovered => PayerId is not null && ReimbursementRate > 0m;
}

/// <summary>One priced act, ready to become an invoice line.</summary>
public sealed record PricedAct
{
    public required ActPricingInput Input { get; init; }

    /// <summary>Quantity × charged unit price.</summary>
    public Money Gross { get; init; }

    /// <summary>Share of an order-level discount allocated to this act, or what its negotiated price takes off.</summary>
    public Money Discount { get; init; }

    /// <summary>What <see cref="Discount"/> is: the negotiated price, the order's rate, or nothing.</summary>
    public ActDiscountKind DiscountKind { get; init; }

    public Money Net { get; init; }
    public Money Vat { get; init; }
    public Money Total { get; init; }

    /// <summary>Quantity × coefficient × letter-key value — the AMO reimbursement base.</summary>
    public Money ReferenceBase { get; init; }

    public Money PayerShare { get; init; }
    public Money PatientShare { get; init; }

    /// <summary>
    /// Amount charged above the reference tariff. Borne entirely by the patient, and the figure a
    /// reception clerk is most often asked to explain.
    /// </summary>
    public Money ExcessOverReferenceTariff { get; init; }

    public decimal? AppliedRate { get; init; }
}

/// <summary>Where an act's discount comes from.</summary>
public enum ActDiscountKind
{
    None = 0,

    /// <summary>The convention's negotiated price, lower than the list price less the order's rate.</summary>
    Negotiated = 1,

    /// <summary>The order's rate (or fixed amount), spread over the acts it applies to.</summary>
    Rate = 2
}

/// <summary>The priced order as a whole.</summary>
public sealed record OrderPricing
{
    public required IReadOnlyList<PricedAct> Acts { get; init; }

    public Money Gross { get; init; }
    public Money Discount { get; init; }

    /// <summary>The part of <see cref="Discount"/> the negotiated prices give.</summary>
    public Money NegotiatedDiscount { get; init; }

    /// <summary>
    /// The part of <see cref="Discount"/> the order's rate gives: what the discount ceiling is
    /// checked against when the rate is the counter's (a profile or a rate typed).
    /// </summary>
    public Money RateDiscount { get; init; }

    public Money Net { get; init; }
    public Money Vat { get; init; }
    public Money Total { get; init; }
    public Money PayerShare { get; init; }
    public Money PatientShare { get; init; }
    public Money ReferenceBaseTotal { get; init; }
    public Money ExcessOverReferenceTariff { get; init; }

    /// <summary>What the patient pays at the counter: their share, unless there is no direct billing.</summary>
    public Money DueAtCounter { get; init; }
}

/// <summary>
/// Prices a set of acts against a coverage.
/// <para>
/// The rules it implements, in the order they apply:
/// </para>
/// <list type="number">
/// <item>Gross is quantity × the laboratory's charged price.</item>
/// <item>An act with a price negotiated in the order's convention costs the lower of that price and
/// its gross less the order's rate — never both (<see cref="DiscountRules"/>).</item>
/// <item>An order-level discount is spread across the other acts in proportion to their gross amount,
/// with the rounding remainder allocated to the last act rather than lost.</item>
/// <item>VAT applies only to non-exempt acts; medical biology is exempt in Morocco.</item>
/// <item>The payer's share is its rate applied to the <em>reference</em> base — coefficient ×
/// letter-key value — not to what the laboratory charged. This is the crux of AMO settlement:
/// a laboratory charging above the reference tariff does not increase the payer's liability, it
/// increases the patient's.</item>
/// <item>The payer's share is capped at the act's own total, so a 100 % rate on a reference base
/// higher than the charged price cannot produce a credit.</item>
/// <item>The patient owes the remainder: the ticket modérateur plus any excess over the tariff.</item>
/// </list>
/// </summary>
public static class ActPricingCalculator
{
    public static OrderPricing Price(IReadOnlyList<ActPricingInput> acts,
                                     CoverageContext coverage,
                                     decimal orderDiscountPercent = 0m,
                                     decimal? fixedDiscountAmount = null)
    {
        var chargeable = acts.Where(a => a.IsChargeable).ToList();

        if (chargeable.Count == 0)
            return new OrderPricing { Acts = [] };

        var grossPerAct = chargeable
            .Select(a => Money.Of(a.ChargedUnitPrice * a.Quantity))
            .ToArray();

        var gross = Money.Of(grossPerAct.Sum(m => m.Amount));

        var rate = Math.Clamp(orderDiscountPercent, 0m, 100m);

        // A negotiated price and the order's rate are never added: each act takes whichever leaves
        // the patient less to pay, the negotiated price when they come to the same (it needs no
        // approval). A negotiated price above the list price is not charged.
        var kinds = new ActDiscountKind[chargeable.Count];
        var negotiatedPerAct = new Money[chargeable.Count];
        for (var i = 0; i < chargeable.Count; i++)
        {
            negotiatedPerAct[i] = Money.Zero;
            if (chargeable[i].NegotiatedUnitPrice is not { } negotiated) continue;

            var negotiatedNet = Money.Round(Math.Max(0m, negotiated) * chargeable[i].Quantity);
            var byRate = fixedDiscountAmount is null ? grossPerAct[i].Amount * (1m - rate / 100m) : grossPerAct[i].Amount;
            if (negotiatedNet < grossPerAct[i].Amount && negotiatedNet <= byRate)
            {
                kinds[i] = ActDiscountKind.Negotiated;
                negotiatedPerAct[i] = Money.Of(grossPerAct[i].Amount - negotiatedNet);
            }
        }

        // The order's rate (or fixed amount) is spread over the acts it applies to only.
        var rateWeights = grossPerAct.Select((g, i) => kinds[i] == ActDiscountKind.Negotiated ? Money.Zero : g).ToArray();
        var rateBase = rateWeights.Sum(w => w.Amount);

        var rateTotal = fixedDiscountAmount is { } fixedAmount
            ? Money.Of(Math.Clamp(fixedAmount, 0m, rateBase))
            : Money.Of(rateBase * rate / 100m);

        var ratePerAct = AllocateProportionally(rateTotal, rateWeights);
        var negotiatedTotal = Money.Of(negotiatedPerAct.Sum(m => m.Amount));
        var discountTotal = rateTotal + negotiatedTotal;

        var priced = new List<PricedAct>(chargeable.Count);

        for (var i = 0; i < chargeable.Count; i++)
        {
            var act = chargeable[i];
            var actGross = grossPerAct[i];
            var actDiscount = kinds[i] == ActDiscountKind.Negotiated ? negotiatedPerAct[i] : ratePerAct[i];
            if (kinds[i] == ActDiscountKind.None && !actDiscount.IsZero) kinds[i] = ActDiscountKind.Rate;
            var net = actGross - actDiscount;
            var vat = act.IsVatExempt ? Money.Zero : Money.Of(net.Amount * act.VatRate);
            // actTotal, not total: the order total below carries that name, and the per-act and
            // per-order figures are the two values in this method it would be most costly to confuse.
            var actTotal = net + vat;

            var referenceBase = Money.Of(act.Coefficient * act.LetterKeyValue * act.Quantity);

            Money payerShare;
            if (!coverage.IsCovered)
            {
                payerShare = Money.Zero;
            }
            else
            {
                var basis = coverage.UsesReferenceTariff ? referenceBase : net;
                var raw = Money.Of(basis.Amount * coverage.ReimbursementRate);
                // Never more than the act itself costs, and never negative.
                payerShare = Money.Of(Math.Clamp(raw.Amount, 0m, actTotal.Amount));
            }

            var excess = Money.Of(Math.Max(0m, net.Amount - referenceBase.Amount));

            priced.Add(new PricedAct
            {
                Input = act,
                Gross = actGross,
                Discount = actDiscount,
                DiscountKind = kinds[i],
                Net = net,
                Vat = vat,
                Total = actTotal,
                ReferenceBase = referenceBase,
                PayerShare = payerShare,
                PatientShare = actTotal - payerShare,
                ExcessOverReferenceTariff = excess,
                AppliedRate = coverage.IsCovered ? coverage.ReimbursementRate : null
            });
        }

        var netTotal = Money.Of(priced.Sum(p => p.Net.Amount));
        var vatTotal = Money.Of(priced.Sum(p => p.Vat.Amount));
        var total = Money.Of(priced.Sum(p => p.Total.Amount));
        var payerTotal = Money.Of(priced.Sum(p => p.PayerShare.Amount));
        var patientTotal = total - payerTotal;

        return new OrderPricing
        {
            Acts = priced,
            Gross = gross,
            Discount = discountTotal,
            NegotiatedDiscount = negotiatedTotal,
            RateDiscount = rateTotal,
            Net = netTotal,
            Vat = vatTotal,
            Total = total,
            PayerShare = payerTotal,
            PatientShare = patientTotal,
            ReferenceBaseTotal = Money.Of(priced.Sum(p => p.ReferenceBase.Amount)),
            ExcessOverReferenceTariff = Money.Of(priced.Sum(p => p.ExcessOverReferenceTariff.Amount)),
            // Without a third-party payment agreement the patient advances the whole amount and
            // claims it back from the payer themselves.
            DueAtCounter = coverage is { IsCovered: true, DirectBilling: true } ? patientTotal : total
        };
    }

    /// <summary>
    /// Splits <paramref name="amount"/> across <paramref name="weights"/> in proportion, giving the
    /// rounding remainder to the last non-zero weight so the parts always add back to the whole.
    /// </summary>
    private static Money[] AllocateProportionally(Money amount, Money[] weights)
    {
        var result = new Money[weights.Length];

        if (amount.IsZero)
        {
            Array.Fill(result, Money.Zero);
            return result;
        }

        var weightTotal = weights.Sum(w => w.Amount);
        if (weightTotal <= 0m)
        {
            Array.Fill(result, Money.Zero);
            return result;
        }

        var lastNonZero = Array.FindLastIndex(weights, w => w.Amount > 0m);
        var allocated = 0m;

        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i].Amount <= 0m)
            {
                result[i] = Money.Zero;
                continue;
            }

            if (i == lastNonZero)
            {
                result[i] = Money.Of(amount.Amount - allocated);
            }
            else
            {
                var share = Money.Of(amount.Amount * weights[i].Amount / weightTotal);
                result[i] = share;
                allocated += share.Amount;
            }
        }

        return result;
    }
}
