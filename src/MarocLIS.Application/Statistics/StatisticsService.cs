using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Statistics;

/// <summary>One requested test in the period, as the statistics need it. Read, never tracked.</summary>
public sealed record ActivityItem(
    Guid LabOrderId,
    Guid PatientId,
    DateTimeOffset RegisteredAt,
    Guid? PayerId,
    Guid AnalysisTestId,
    Guid DepartmentId,
    LabOrderItemStatus Status,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? DueAt,
    decimal ChargedPrice,
    decimal PayerShare,
    decimal PatientShare);

/// <summary>Names for the identifiers carried by <see cref="ActivityItem"/>.</summary>
public sealed record StatisticsLabels(
    IReadOnlyDictionary<Guid, (string Code, MultilingualText Name)> Tests,
    IReadOnlyDictionary<Guid, (string Code, MultilingualText Name)> Departments,
    IReadOnlyDictionary<Guid, MultilingualText> Payers);

/// <summary>Money issued and collected in the period, from the accounting documents themselves.</summary>
public sealed record FinanceTotals(decimal Invoiced, decimal Credited, decimal Collected);

public sealed record StatisticsSummary(
    int OrderCount,
    int PatientCount,
    int TestCount,
    int CancelledTestCount,
    int ValidatedTestCount,
    double? AverageTurnaroundHours,
    double? OnTimePercent,
    decimal ChargedAmount,
    decimal InvoicedAmount,
    decimal CreditedAmount,
    decimal CollectedAmount);

public sealed record DepartmentStatistic(
    string Code,
    MultilingualText Name,
    int TestCount,
    int ValidatedCount,
    double? AverageTurnaroundHours,
    double? OnTimePercent,
    decimal ChargedAmount);

public sealed record TestStatistic(
    string Code,
    MultilingualText Name,
    int TestCount,
    double? AverageTurnaroundHours,
    decimal ChargedAmount);

public sealed record PayerStatistic(
    MultilingualText? Name,
    int OrderCount,
    int TestCount,
    decimal ChargedAmount,
    decimal PayerShare,
    decimal PatientShare);

public sealed record DailyVolume(DateOnly Date, int OrderCount, int TestCount);

public sealed record ActivityStatistics(
    DateOnly From,
    DateOnly To,
    StatisticsSummary Summary,
    IReadOnlyList<DepartmentStatistic> Departments,
    IReadOnlyList<TestStatistic> Tests,
    IReadOnlyList<PayerStatistic> Payers,
    IReadOnlyList<DailyVolume> Days);

/// <summary>
/// Activity statistics for the laboratory's management review (ISO 15189 §8.9): volumes,
/// turnaround against the promised delay, and money charged, issued and collected.
/// <para>
/// The figures are computed here from plain rows rather than in SQL, so the definitions —
/// what counts as a test, what "on time" means — live in one readable place and are covered by
/// unit tests. A year of activity for a private laboratory is a few hundred thousand rows,
/// which is why the period is capped.
/// </para>
/// </summary>
public sealed class StatisticsService(
    IStatisticsQueries queries,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Longest period one request may cover.</summary>
    public const int MaximumPeriodDays = 366;

    public async Task<Result<ActivityStatistics>> GetActivityAsync(DateOnly from, DateOnly to,
                                                                   CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Statistics.View);
        if (permitted.IsFailure) return Result.Failure<ActivityStatistics>(permitted.FirstError);

        if (to < from)
            return Result.Failure<ActivityStatistics>(Error.Validation("Stats_PeriodReversed"));
        if (to.DayNumber - from.DayNumber + 1 > MaximumPeriodDays)
            return Result.Failure<ActivityStatistics>(
                Error.Validation("Stats_PeriodTooLong", MaximumPeriodDays));

        // The period is in the laboratory's local calendar; registration times are compared in
        // the same time zone so that an order registered at 23:30 lands on the right day. Each day
        // takes its own offset: Morocco moves to UTC+0 for Ramadan, and a period crossing the
        // change, read with today's offset, slid an hour of orders a day across midnight.
        var zone = LaboratoryZone(clock.Now);
        var start = LocalMidnight(from, zone);
        var end = LocalMidnight(to.AddDays(1), zone);

        var items = await queries.GetActivityAsync(currentUser.SiteId, start, end, ct);
        var labels = await queries.GetLabelsAsync(ct);
        var finance = await queries.GetFinanceAsync(currentUser.SiteId, from, to, ct);

        return Result.Success(Compute(from, to, items, labels, finance, zone));
    }

    /// <summary>
    /// The machine's time zone, which carries the change of hour, when it agrees with the clock;
    /// otherwise (a clock set apart from the machine) the clock's own offset, as before.
    /// </summary>
    private static TimeZoneInfo LaboratoryZone(DateTimeOffset now) =>
        TimeZoneInfo.Local.GetUtcOffset(now) == now.Offset ? TimeZoneInfo.Local : Fixed(now.Offset);

    private static TimeZoneInfo Fixed(TimeSpan offset) =>
        TimeZoneInfo.CreateCustomTimeZone("UTC" + offset, offset, "UTC" + offset, "UTC" + offset);

    /// <summary>Midnight of <paramref name="day"/> in <paramref name="zone"/>, with that day's offset.</summary>
    public static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    /// <summary>The aggregation itself, separate so it can be tested without a database.</summary>
    public static ActivityStatistics Compute(DateOnly from, DateOnly to,
                                             IReadOnlyList<ActivityItem> items,
                                             StatisticsLabels labels, FinanceTotals finance,
                                             TimeSpan offset) =>
        Compute(from, to, items, labels, finance, Fixed(offset));

    /// <summary>As above, each registration placed on its day in the laboratory's time zone.</summary>
    public static ActivityStatistics Compute(DateOnly from, DateOnly to,
                                             IReadOnlyList<ActivityItem> items,
                                             StatisticsLabels labels, FinanceTotals finance,
                                             TimeZoneInfo zone)
    {
        // A cancelled test was not performed and was not charged; it is counted once, in the
        // summary, and nowhere else.
        var performed = items.Where(i => i.Status != LabOrderItemStatus.Cancelled).ToList();

        // Orders and patients are those with at least one test performed, as the payer breakdown,
        // the steering dashboard and the prescriber statistics count them: an order cancelled
        // whole shows only in the cancelled tests.
        var summary = new StatisticsSummary(
            OrderCount: performed.Select(i => i.LabOrderId).Distinct().Count(),
            PatientCount: performed.Select(i => i.PatientId).Distinct().Count(),
            TestCount: performed.Count,
            CancelledTestCount: items.Count - performed.Count,
            ValidatedTestCount: performed.Count(IsValidated),
            AverageTurnaroundHours: AverageTurnaround(performed),
            OnTimePercent: OnTimePercent(performed),
            ChargedAmount: Money.Round(performed.Sum(i => i.ChargedPrice)),
            InvoicedAmount: finance.Invoiced,
            CreditedAmount: finance.Credited,
            CollectedAmount: finance.Collected);

        var departments = performed
            .GroupBy(i => i.DepartmentId)
            .Select(g =>
            {
                var label = labels.Departments.TryGetValue(g.Key, out var d)
                    ? d : ("?", new MultilingualText("?", "?", "?"));
                var rows = g.ToList();
                return new DepartmentStatistic(label.Item1, label.Item2, rows.Count,
                                               rows.Count(IsValidated), AverageTurnaround(rows),
                                               OnTimePercent(rows),
                                               Money.Round(rows.Sum(i => i.ChargedPrice)));
            })
            .OrderByDescending(d => d.TestCount).ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList();

        var tests = performed
            .GroupBy(i => i.AnalysisTestId)
            .Select(g =>
            {
                var label = labels.Tests.TryGetValue(g.Key, out var t)
                    ? t : ("?", new MultilingualText("?", "?", "?"));
                var rows = g.ToList();
                return new TestStatistic(label.Item1, label.Item2, rows.Count,
                                         AverageTurnaround(rows),
                                         Money.Round(rows.Sum(i => i.ChargedPrice)));
            })
            .OrderByDescending(t => t.TestCount).ThenBy(t => t.Code, StringComparer.Ordinal)
            .ToList();

        var payers = performed
            .GroupBy(i => i.PayerId)
            .Select(g => new PayerStatistic(
                g.Key is { } id && labels.Payers.TryGetValue(id, out var name) ? name : null,
                g.Select(i => i.LabOrderId).Distinct().Count(),
                g.Count(),
                Money.Round(g.Sum(i => i.ChargedPrice)),
                Money.Round(g.Sum(i => i.PayerShare)),
                Money.Round(g.Sum(i => i.PatientShare))))
            .OrderByDescending(p => p.ChargedAmount)
            .ToList();

        // Every day of the period appears, including the empty ones: a gap in a volume curve is
        // information (a closure, an outage) and should not be smoothed away.
        var byDay = items
            .GroupBy(i => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(i.RegisteredAt, zone).DateTime))
            .ToDictionary(g => g.Key, g => g.ToList());

        var days = new List<DailyVolume>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var rows = byDay.TryGetValue(day, out var found) ? found : [];
            days.Add(new DailyVolume(day, rows.Where(i => i.Status != LabOrderItemStatus.Cancelled)
                                              .Select(i => i.LabOrderId).Distinct().Count(),
                                     rows.Count(i => i.Status != LabOrderItemStatus.Cancelled)));
        }

        return new ActivityStatistics(from, to, summary, departments, tests, payers, days);
    }

    private static bool IsValidated(ActivityItem item) =>
        item.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported;

    /// <summary>Reception to biological validation, in hours, over the tests that have both.</summary>
    private static double? AverageTurnaround(IReadOnlyCollection<ActivityItem> items)
    {
        var hours = items
            .Where(i => i.ReceivedAt is not null && i.ValidatedAt is not null)
            .Select(i => (i.ValidatedAt!.Value - i.ReceivedAt!.Value).TotalHours)
            .Where(h => h >= 0)
            .ToList();

        return hours.Count == 0 ? null : Math.Round(hours.Average(), 1);
    }

    /// <summary>Share of validated tests released by their due time, among those that had one.</summary>
    private static double? OnTimePercent(IReadOnlyCollection<ActivityItem> items)
    {
        var measured = items.Where(i => i.DueAt is not null && i.ValidatedAt is not null).ToList();
        if (measured.Count == 0) return null;

        var onTime = measured.Count(i => i.ValidatedAt <= i.DueAt);
        return Math.Round(100.0 * onTime / measured.Count, 1);
    }
}

/// <summary>Read-side queries behind the statistics screen.</summary>
public interface IStatisticsQueries
{
    /// <summary>Every test on an order registered in <c>[start, end)</c> at the site.</summary>
    Task<IReadOnlyList<ActivityItem>> GetActivityAsync(Guid siteId, DateTimeOffset start,
                                                       DateTimeOffset end,
                                                       CancellationToken ct = default);

    Task<StatisticsLabels> GetLabelsAsync(CancellationToken ct = default);

    /// <summary>Issued invoices and credit notes, and settled payments, dated within the period.</summary>
    Task<FinanceTotals> GetFinanceAsync(Guid siteId, DateOnly from, DateOnly to,
                                        CancellationToken ct = default);
}
