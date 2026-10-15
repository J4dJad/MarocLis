using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Relations;

/// <summary>One order as the prescriber statistics count it: performed tests and their charged price.</summary>
public sealed record PrescriberOrderRow(
    Guid OrderId,
    Guid SiteId,
    Guid? PrescriberId,
    DateTimeOffset RegisteredAt,
    int Tests,
    decimal Revenue);

/// <summary>A prescriber's name and the institution it is counted under.</summary>
public sealed record PrescriberLabel(Guid Id, string Name, string? Institution, PrescriberKind Kind, string? City);

/// <summary>Orders, tests and revenue over one window.</summary>
public sealed record VolumeFigures(int Orders, int Tests, decimal Revenue)
{
    public static readonly VolumeFigures Zero = new(0, 0, 0m);

    public VolumeFigures Add(PrescriberOrderRow row) => new(Orders + 1, Tests + row.Tests, Revenue + row.Revenue);
}

/// <summary>
/// A prescriber's (or an institution's) volumes in the period, the period before and the same
/// period a year earlier, with the change in percent; null where there is nothing to compare with.
/// </summary>
public sealed record PrescriberVolumeRow(
    Guid? PrescriberId,
    string? Name,
    string? Institution,
    VolumeFigures Current,
    VolumeFigures Previous,
    VolumeFigures LastYear,
    double? OrdersVsPrevious,
    double? OrdersVsLastYear,
    double? RevenueVsPrevious,
    double? RevenueVsLastYear,
    bool IsNew);

/// <summary>One month of the period, against the same month a year earlier.</summary>
public sealed record MonthlyVolumeRow(
    DateOnly Month,
    VolumeFigures Current,
    VolumeFigures LastYear,
    int ActivePrescribers,
    int NewPrescribers,
    double? OrdersVsLastYear);

/// <summary>One prescriber in one month, for the detailed export.</summary>
public sealed record PrescriberMonthRow(Guid? PrescriberId, string? Name, string? Institution, DateOnly Month,
                                        VolumeFigures Figures);

public sealed record PrescriberStatistics(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    VolumeFigures Total,
    VolumeFigures TotalPrevious,
    VolumeFigures TotalLastYear,
    IReadOnlyList<PrescriberVolumeRow> Prescribers,
    IReadOnlyList<PrescriberVolumeRow> Institutions,
    IReadOnlyList<MonthlyVolumeRow> Months,
    IReadOnlyList<PrescriberVolumeRow> NewPrescribers,
    IReadOnlyList<PrescriberVolumeRow> LostPrescribers,
    IReadOnlyList<PrescriberMonthRow> Detail);

/// <summary>
/// What each prescriber and each clinic sends the laboratory, and how it moves: per month, against
/// the period just before and against the same period last year, with the prescribers who started
/// sending and those who stopped.
/// <para>
/// Definitions, kept here and covered by tests: an order counts when it is not cancelled; its tests
/// and revenue are its analyses that were not cancelled, at the price charged. A prescriber is
/// <b>new</b> when they have orders in the period and none in the <see cref="NewLookbackDays"/>
/// days before it; <b>lost</b> when they had orders in the previous period and none in this one.
/// Every site the operator may open is counted together.
/// </para>
/// </summary>
public sealed class PrescriberStatisticsService(
    IPrescriberStatisticsQueries queries,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int MaximumPeriodDays = 366;
    public const int NewLookbackDays = 365;

    public async Task<Result<PrescriberStatistics>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Relations.View, Permissions.Steering.View);
        if (permitted.IsFailure) return Result.Failure<PrescriberStatistics>(permitted.FirstError);

        if (to < from) return Result.Failure<PrescriberStatistics>(Error.Validation("Stats_PeriodReversed"));
        if (to.DayNumber - from.DayNumber + 1 > MaximumPeriodDays)
            return Result.Failure<PrescriberStatistics>(Error.Validation("Stats_PeriodTooLong", MaximumPeriodDays));

        var offset = clock.Now.Offset;
        var sites = currentUser.AccessibleSiteIds.Count > 0 ? currentUser.AccessibleSiteIds : [currentUser.SiteId];
        var (previousFrom, previousTo) = PreviousPeriod(from, to);
        var (lastYearFrom, lastYearTo) = (from.AddYears(-1), to.AddYears(-1));

        DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), offset);

        var current = await queries.GetOrdersAsync(sites, Start(from), Start(to.AddDays(1)), ct);
        var previous = await queries.GetOrdersAsync(sites, Start(previousFrom), Start(previousTo.AddDays(1)), ct);
        var lastYear = await queries.GetOrdersAsync(sites, Start(lastYearFrom), Start(lastYearTo.AddDays(1)), ct);
        var before = await queries.GetActivePrescriberIdsAsync(sites, Start(from.AddDays(-NewLookbackDays)),
                                                               Start(from), ct);

        var labels = await queries.GetLabelsAsync(ct);

        return Result.Success(Compute(from, to, current, previous, lastYear, before, labels, offset));
    }

    /// <summary>The period of the same length that ends the day before <paramref name="from"/>.</summary>
    public static (DateOnly From, DateOnly To) PreviousPeriod(DateOnly from, DateOnly to)
    {
        // Whole calendar months compare to the same number of whole months before them, so that
        // February is not compared with 28 days straddling January and December.
        if (from.Day == 1 && to.AddDays(1).Day == 1)
        {
            var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
            return (from.AddMonths(-months), from.AddDays(-1));
        }

        var length = to.DayNumber - from.DayNumber + 1;
        return (from.AddDays(-length), from.AddDays(-1));
    }

    /// <summary>Change in percent from <paramref name="before"/> to <paramref name="now"/>; null when there was nothing before.</summary>
    public static double? Change(decimal now, decimal before) =>
        before == 0m ? null : Math.Round((double)((now - before) / before * 100m), 1);

    public static PrescriberStatistics Compute(
        DateOnly from, DateOnly to,
        IReadOnlyList<PrescriberOrderRow> current,
        IReadOnlyList<PrescriberOrderRow> previous,
        IReadOnlyList<PrescriberOrderRow> lastYear,
        IReadOnlySet<Guid> activeBefore,
        IReadOnlyDictionary<Guid, PrescriberLabel> labels,
        TimeSpan offset)
    {
        var (previousFrom, previousTo) = PreviousPeriod(from, to);

        static Dictionary<TKey, VolumeFigures> Sum<TKey>(IEnumerable<PrescriberOrderRow> rows,
                                                         Func<PrescriberOrderRow, TKey> key) where TKey : notnull
        {
            var sums = new Dictionary<TKey, VolumeFigures>();
            foreach (var row in rows)
                sums[key(row)] = (sums.TryGetValue(key(row), out var s) ? s : VolumeFigures.Zero).Add(row);
            return sums;
        }

        static VolumeFigures Total(IEnumerable<PrescriberOrderRow> rows) =>
            rows.Aggregate(VolumeFigures.Zero, (s, r) => s.Add(r));

        // Prescriber key: Guid.Empty stands for "no prescriber recorded" (the patient came on their own).
        static Guid PrescriberKey(PrescriberOrderRow r) => r.PrescriberId ?? Guid.Empty;

        string? NameOf(Guid id) => id == Guid.Empty ? null : labels.TryGetValue(id, out var l) ? l.Name : "?";
        string? InstitutionOf(Guid id) => id != Guid.Empty && labels.TryGetValue(id, out var l) ? l.Institution : null;

        // Institution key: its name, trimmed and upper-cased so "Clinique Al Amal" and "CLINIQUE AL AMAL "
        // add up; the empty key holds the prescribers in private practice and the walk-ins.
        string InstitutionKey(PrescriberOrderRow r) =>
            (InstitutionOf(PrescriberKey(r)) ?? string.Empty).Trim().ToUpperInvariant();

        var byPrescriber = Sum(current, PrescriberKey);
        var byPrescriberPrevious = Sum(previous, PrescriberKey);
        var byPrescriberLastYear = Sum(lastYear, PrescriberKey);

        PrescriberVolumeRow Row(Guid? id, string? name, string? institution, VolumeFigures now,
                                VolumeFigures before, VolumeFigures yearAgo, bool isNew) =>
            new(id, name, institution, now, before, yearAgo,
                Change(now.Orders, before.Orders), Change(now.Orders, yearAgo.Orders),
                Change(now.Revenue, before.Revenue), Change(now.Revenue, yearAgo.Revenue), isNew);

        var prescribers = byPrescriber.Keys.Union(byPrescriberPrevious.Keys)
            .Select(id => Row(id == Guid.Empty ? null : id, NameOf(id), InstitutionOf(id),
                              byPrescriber.GetValueOrDefault(id, VolumeFigures.Zero),
                              byPrescriberPrevious.GetValueOrDefault(id, VolumeFigures.Zero),
                              byPrescriberLastYear.GetValueOrDefault(id, VolumeFigures.Zero),
                              id != Guid.Empty && byPrescriber.ContainsKey(id) && !activeBefore.Contains(id)))
            .OrderByDescending(r => r.Current.Revenue).ThenByDescending(r => r.Current.Orders)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

        var byInstitution = Sum(current, InstitutionKey);
        var byInstitutionPrevious = Sum(previous, InstitutionKey);
        var byInstitutionLastYear = Sum(lastYear, InstitutionKey);

        // The display name of an institution is the spelling of its first prescriber met.
        var institutionNames = current.Concat(previous).Concat(lastYear)
            .Select(r => InstitutionOf(PrescriberKey(r)))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .GroupBy(n => n!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First()!.Trim());

        var institutions = byInstitution.Keys.Union(byInstitutionPrevious.Keys)
            .Select(key => Row(null, null, key.Length == 0 ? null : institutionNames.GetValueOrDefault(key, key),
                               byInstitution.GetValueOrDefault(key, VolumeFigures.Zero),
                               byInstitutionPrevious.GetValueOrDefault(key, VolumeFigures.Zero),
                               byInstitutionLastYear.GetValueOrDefault(key, VolumeFigures.Zero), false))
            .OrderByDescending(r => r.Current.Revenue).ThenBy(r => r.Institution, StringComparer.CurrentCulture)
            .ToList();

        // ---- Months -------------------------------------------------------------------------------

        static DateOnly MonthOf(DateTimeOffset at, TimeSpan offset)
        {
            var local = at.ToOffset(offset);
            return new DateOnly(local.Year, local.Month, 1);
        }

        var currentByMonth = current.GroupBy(r => MonthOf(r.RegisteredAt, offset)).ToDictionary(g => g.Key, g => g.ToList());
        var lastYearByMonth = lastYear.GroupBy(r => MonthOf(r.RegisteredAt, offset).AddYears(1))
                                      .ToDictionary(g => g.Key, g => g.ToList());

        var months = new List<MonthlyVolumeRow>();
        var seen = new HashSet<Guid>(activeBefore);
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var rows = currentByMonth.GetValueOrDefault(month) ?? [];
            var yearAgo = lastYearByMonth.GetValueOrDefault(month) ?? [];
            var active = rows.Where(r => r.PrescriberId is not null).Select(r => r.PrescriberId!.Value).ToHashSet();
            var newOnes = active.Count(id => !seen.Contains(id));
            seen.UnionWith(active);

            var now = Total(rows);
            var before = Total(yearAgo);
            months.Add(new MonthlyVolumeRow(month, now, before, active.Count, newOnes, Change(now.Orders, before.Orders)));
        }

        var detail = current
            .GroupBy(r => (Prescriber: PrescriberKey(r), Month: MonthOf(r.RegisteredAt, offset)))
            .Select(g => new PrescriberMonthRow(g.Key.Prescriber == Guid.Empty ? null : g.Key.Prescriber,
                                                NameOf(g.Key.Prescriber), InstitutionOf(g.Key.Prescriber),
                                                g.Key.Month, Total(g)))
            .OrderBy(r => r.Name ?? string.Empty, StringComparer.CurrentCulture).ThenBy(r => r.Month)
            .ToList();

        var newPrescribers = prescribers.Where(p => p.IsNew).ToList();
        var lost = prescribers
            .Where(p => p.PrescriberId is not null && p.Current.Orders == 0 && p.Previous.Orders > 0)
            .OrderByDescending(p => p.Previous.Revenue)
            .ToList();

        return new PrescriberStatistics(from, to, previousFrom, previousTo,
                                        Total(current), Total(previous), Total(lastYear),
                                        prescribers.Where(p => p.Current.Orders > 0 || p.Previous.Orders > 0).ToList(),
                                        institutions, months, newPrescribers, lost, detail);
    }
}

public interface IPrescriberStatisticsQueries
{
    /// <summary>Orders registered in <c>[start, end)</c> on the given sites, cancelled orders left out.</summary>
    Task<IReadOnlyList<PrescriberOrderRow>> GetOrdersAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start,
                                                           DateTimeOffset end, CancellationToken ct = default);

    /// <summary>Prescribers with at least one order registered in <c>[start, end)</c>.</summary>
    Task<IReadOnlySet<Guid>> GetActivePrescriberIdsAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start,
                                                         DateTimeOffset end, CancellationToken ct = default);

    /// <summary>Every prescriber, deleted ones included: last year's figures still name them.</summary>
    Task<IReadOnlyDictionary<Guid, PrescriberLabel>> GetLabelsAsync(CancellationToken ct = default);
}
