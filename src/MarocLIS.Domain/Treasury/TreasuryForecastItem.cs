using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Treasury;

/// <summary>
/// A flow the cash forecast cannot read from the application and the accountant enters by hand:
/// the rent, the leasing instalment, the CNSS and the income tax paid over, a loan, a dividend.
/// Positive for money coming in, negative for money going out.
/// </summary>
public sealed class TreasuryForecastItem : AuditableAggregateRoot, ISiteScoped
{
    private TreasuryForecastItem() { }

    public TreasuryForecastItem(Guid siteId, string label, decimal amountMad, ForecastRecurrence recurrence,
                                DateOnly startDate)
    {
        SiteId = siteId;
        Label = label.Trim();
        AmountMad = Money.Round(amountMad);
        Recurrence = recurrence;
        StartDate = startDate;
        IsActive = true;
    }

    public Guid SiteId { get; set; }
    public string Label { get; set; } = string.Empty;

    /// <summary>Signed: positive comes in, negative goes out.</summary>
    public decimal AmountMad { get; private set; }

    public ForecastRecurrence Recurrence { get; set; }
    public DateOnly StartDate { get; set; }

    /// <summary>Last day an occurrence may fall on; none for a flow that goes on.</summary>
    public DateOnly? EndDate { get; set; }

    public bool IsActive { get; set; }

    public void ChangeAmount(decimal amountMad) => AmountMad = Money.Round(amountMad);

    public const int LabelMaxLength = 200;

    public Result Validate()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(Label))
            errors.Add(Error.Validation("TreasuryItem_LabelRequired"));
        else if (Label.Length > LabelMaxLength)
            errors.Add(Error.Validation("Treasury_TextTooLong", LabelMaxLength));
        if (AmountMad == 0m)
            errors.Add(Error.Validation("TreasuryItem_AmountRequired"));
        if (EndDate is { } end && end < StartDate)
            errors.Add(Error.Validation("TreasuryItem_EndBeforeStart"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>
    /// The dates the flow falls on between <paramref name="from"/> and <paramref name="to"/>,
    /// both included. Months are counted from the start date each time, never from the previous
    /// occurrence, so a flow on the 31st falls on the last day of a short month and back on the
    /// 31st after it.
    /// </summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly from, DateOnly to)
    {
        if (!IsActive) yield break;

        var last = EndDate is { } end && end < to ? end : to;

        for (var n = 0; n < 10_000; n++)
        {
            DateOnly? date = Recurrence switch
            {
                ForecastRecurrence.Once => n == 0 ? StartDate : null,
                ForecastRecurrence.Weekly => StartDate.AddDays(7 * n),
                ForecastRecurrence.Monthly => StartDate.AddMonths(n),
                ForecastRecurrence.Quarterly => StartDate.AddMonths(3 * n),
                ForecastRecurrence.Yearly => StartDate.AddYears(n),
                _ => null
            };

            if (date is not { } day || day > last) yield break;
            if (day >= from) yield return day;
        }
    }
}

public enum ForecastRecurrence
{
    Once = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4
}
