using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Inventory;

/// <summary>
/// Payment terms under loi n° 69-21 (code de commerce, articles 78-1 and following): 60 days when
/// the parties agreed nothing, and never more than 120 days when they did, counted from the
/// delivery of the goods or the completion of the service.
/// </summary>
public static class PaymentTerms
{
    public const int DefaultDays = 60;
    public const int MaximumDays = 120;

    /// <summary>The term the law lets the laboratory use: the default when none was agreed, never above the ceiling.</summary>
    public static int Effective(int agreedDays) =>
        agreedDays <= 0 ? DefaultDays : Math.Min(agreedDays, MaximumDays);

    /// <summary>
    /// The due date: the term after the latest delivery of the invoiced goods, or after the invoice
    /// date when no delivery is known (an invoice entered for goods received before deliveries were
    /// dated).
    /// </summary>
    public static DateOnly DueDate(DateOnly invoiceDate, DateOnly? deliveryDate, int agreedDays) =>
        (deliveryDate ?? invoiceDate).AddDays(Effective(agreedDays));

    /// <summary>
    /// The delivery an invoice line bills: the receipt at which the quantity delivered on the
    /// order line first covered what the earlier invoices billed plus this one. Not simply the
    /// latest receipt — a January delivery invoiced after a February one arrived would otherwise
    /// get its term counted from February. Beyond the receipts on record, the last one; null when
    /// none is.
    /// </summary>
    public static DateOnly? DeliveryCovering(IEnumerable<DatedQuantity> receipts, decimal invoicedBefore,
                                             decimal quantity)
    {
        var needed = invoicedBefore + quantity;
        var delivered = 0m;
        DateOnly? last = null;
        foreach (var receipt in receipts.OrderBy(r => r.On))
        {
            delivered += receipt.Quantity;
            last = receipt.On;
            if (delivered >= needed) return receipt.On;
        }
        return last;
    }
}

/// <summary>A quantity on a date: a receipt on an order line, or a quantity billed on an invoice.</summary>
public readonly record struct DatedQuantity(DateOnly On, decimal Quantity);

/// <summary>What an order line allows an invoice to bill, as the three-way match sees it.</summary>
/// <param name="QuantityInvoicedElsewhere">Already billed on the supplier's other invoices, cancelled ones excepted.</param>
/// <param name="NetUnitPriceMad">The ordered unit price excluding VAT, after the line discount.</param>
public sealed record OrderLineFacts(
    Guid PurchaseOrderLineId,
    decimal QuantityReceived,
    decimal QuantityInvoicedElsewhere,
    decimal NetUnitPriceMad,
    decimal VatRate)
{
    /// <summary>Received and not yet invoiced: the most this invoice may bill.</summary>
    public decimal ReceivedNotInvoiced => Math.Max(0m, QuantityReceived - QuantityInvoicedElsewhere);
}

/// <summary>
/// How far an invoiced unit price may stray from the ordered one before the invoice is blocked: a
/// percentage of the ordered price or a fixed amount, whichever is larger. Zero for both — the
/// default — means the price must match to the centime.
/// </summary>
public readonly record struct PriceTolerance(decimal Percent, decimal AmountMad)
{
    public static PriceTolerance None => new(0m, 0m);

    public decimal AllowedDifference(decimal orderedPrice) =>
        Math.Max(Money.Round(Math.Abs(orderedPrice) * Percent / 100m), AmountMad);

    public bool Accepts(decimal orderedPrice, decimal invoicedPrice) =>
        Math.Abs(Money.Round(invoicedPrice) - Money.Round(orderedPrice)) <= AllowedDifference(orderedPrice);
}

/// <summary>One difference the match found, before it is stored on the invoice.</summary>
public sealed record MatchVariance(int LineNumber, VarianceKind Kind, decimal Expected, decimal Actual);

/// <summary>
/// The three-way match: each invoice line against the order line (price, VAT rate) and against
/// what was received on it and not already invoiced (quantity).
/// </summary>
public static class ThreeWayMatch
{
    public static IReadOnlyList<MatchVariance> Compare(IEnumerable<SupplierInvoiceLine> lines,
                                                       IReadOnlyDictionary<Guid, OrderLineFacts> orderLines,
                                                       PriceTolerance tolerance)
    {
        var variances = new List<MatchVariance>();

        foreach (var line in lines.OrderBy(l => l.LineNumber))
        {
            if (!orderLines.TryGetValue(line.PurchaseOrderLineId, out var facts))
            {
                // A line whose order line has gone is billing something nobody can show was received.
                variances.Add(new MatchVariance(line.LineNumber, VarianceKind.QuantityExceedsReceived,
                                                0m, line.Quantity));
                continue;
            }

            if (line.Quantity > facts.ReceivedNotInvoiced)
                variances.Add(new MatchVariance(line.LineNumber, VarianceKind.QuantityExceedsReceived,
                                                facts.ReceivedNotInvoiced, line.Quantity));

            if (!tolerance.Accepts(facts.NetUnitPriceMad, line.UnitPriceMad))
                variances.Add(new MatchVariance(line.LineNumber, VarianceKind.PriceDiffers,
                                                facts.NetUnitPriceMad, line.UnitPriceMad));

            if (line.VatRate != facts.VatRate)
                variances.Add(new MatchVariance(line.LineNumber, VarianceKind.VatRateDiffers,
                                                facts.VatRate, line.VatRate));
        }

        return variances;
    }
}

/// <summary>An amount paid late, or still unpaid after its due date, over a declaration period.</summary>
/// <param name="PaidOn">Null for an amount still unpaid at the end of the period.</param>
public sealed record LateAmount(decimal Amount, DateOnly? PaidOn, int DaysLate);

/// <summary>
/// Late payment under loi n° 69-21.
/// <para>
/// Two different sums. The <b>penalty</b> is owed to the supplier: the late amount at an annual
/// rate (Bank Al-Maghrib's key rate plus seven points by default) pro rata of the days late. The
/// <b>fine</b> is owed to the Treasury and declared to the tax administration: a percentage for the
/// first month of delay and another for each further month or part of a month. Both rates are
/// settings, because both are fixed by texts that change.
/// </para>
/// </summary>
public static class LatePayment
{
    public static int DaysLate(DateOnly dueDate, DateOnly paidOrAsOf) =>
        Math.Max(0, paidOrAsOf.DayNumber - dueDate.DayNumber);

    /// <summary>Amount × annual rate × days late / 365.</summary>
    public static decimal Penalty(decimal amount, decimal annualRatePercent, int daysLate) =>
        daysLate <= 0 ? 0m : Money.Round(amount * annualRatePercent / 100m * daysLate / 365m);

    /// <summary>Months of delay begun, counting 30 days to a month: 1 to 30 days is one month.</summary>
    public static int MonthsBegun(int daysLate) => daysLate <= 0 ? 0 : (daysLate + 29) / 30;

    public static decimal Fine(decimal amount, int daysLate, decimal firstMonthPercent,
                               decimal additionalMonthPercent)
    {
        var months = MonthsBegun(daysLate);
        if (months == 0) return 0m;

        return Money.Round(amount * (firstMonthPercent + additionalMonthPercent * (months - 1)) / 100m);
    }

    /// <summary>
    /// What of one invoice was late over a period: each payment made in the period after the due
    /// date, and whatever was still unpaid at the end of the period if it was already due.
    /// </summary>
    public static IReadOnlyList<LateAmount> LateAmounts(DateOnly dueDate, decimal totalIncludingVat,
                                                        IEnumerable<(DateOnly PaidOn, decimal Amount)> payments,
                                                        DateOnly from, DateOnly to)
    {
        var list = payments.ToList();
        var late = new List<LateAmount>();

        foreach (var (paidOn, amount) in list.Where(p => p.PaidOn >= from && p.PaidOn <= to)
                                             .OrderBy(p => p.PaidOn))
        {
            var days = DaysLate(dueDate, paidOn);
            if (days > 0) late.Add(new LateAmount(amount, paidOn, days));
        }

        var unpaid = Money.Round(totalIncludingVat - list.Where(p => p.PaidOn <= to).Sum(p => p.Amount));
        var daysAtEnd = DaysLate(dueDate, to);
        if (unpaid > 0m && daysAtEnd > 0) late.Add(new LateAmount(unpaid, null, daysAtEnd));

        return late;
    }
}
