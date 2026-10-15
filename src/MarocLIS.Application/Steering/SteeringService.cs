using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Statistics;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Steering;

/// <summary>One requested test with the site and the prescriber of its order.</summary>
public sealed record SteeringActivityRow(Guid SiteId, Guid? PrescriberId, ActivityItem Item);

/// <summary>An issued invoice still owed, as the ageing needs it.</summary>
public sealed record ReceivableRow(Guid SiteId, InvoiceKind Kind, DateOnly IssueDate, DateOnly? DueDate,
                                   decimal TotalAmount, decimal PaidAmount, decimal CreditedAmount);

/// <summary>A closed payroll month of a site.</summary>
public sealed record PayrollMonthRow(Guid SiteId, int Year, int Month, int Payslips, decimal Gross, decimal EmployerCost);

public sealed record QualityCounts(int QcResults, int QcRejected, int OpenNonConformities, int NonConformitiesOpened,
                                   IReadOnlyDictionary<EqaOutcome, int> EqaOutcomes);

public sealed record SteeringLabels(
    IReadOnlyDictionary<Guid, string> Sites,
    IReadOnlyDictionary<Guid, MultilingualText> Payers,
    IReadOnlyDictionary<Guid, string> Prescribers);

public enum AgeingBand
{
    NotDue = 0,
    Days1To30 = 1,
    Days31To60 = 2,
    Days61To90 = 3,
    Days91To180 = 4,
    Over180 = 5
}

public sealed record AgeingRow(AgeingBand Band, int Invoices, decimal Amount);

public sealed record RevenueRow(string Key, string Label, int Orders, int Tests, decimal Revenue, double? SharePercent);

public sealed record PayrollFigures(int SiteMonths, decimal Gross, decimal EmployerCost, decimal Revenue,
                                    double? EmployerCostToRevenuePercent);

public sealed record QualityFigures(int QcResults, int QcRejected, double? QcRejectedPercent,
                                    int OpenNonConformities, int NonConformitiesOpened,
                                    int EqaSatisfactory, int EqaQuestionable, int EqaUnsatisfactory, int EqaNotSubmitted);

public sealed record SteeringDashboard(
    DateOnly From,
    DateOnly To,
    int SiteCount,
    int Orders,
    int Tests,
    decimal Revenue,
    decimal Invoiced,
    decimal Credited,
    decimal Collected,
    MarginTotals Margin,
    decimal Outstanding,
    decimal Overdue,
    IReadOnlyList<AgeingRow> Ageing,
    PayrollFigures? Payroll,
    QualityFigures Quality,
    double? AverageTurnaroundHours,
    double? OnTimePercent,
    IReadOnlyList<RevenueRow> ByDay,
    IReadOnlyList<RevenueRow> BySite,
    IReadOnlyList<RevenueRow> ByPayer,
    IReadOnlyList<RevenueRow> ByPrescriber);

/// <summary>
/// The management dashboard ("Pilotage"): revenue for a period across every site the operator may
/// open, by day, site, payer and prescriber; the reagent cost and the margin; what is owed and how
/// late; the payroll against the revenue; and the quality indicators — QC rejections, open
/// non-conformities, external quality assessment and turnaround.
/// <para>
/// The payroll tile needs <see cref="Permissions.HumanResources.Salary"/> as well: salaries are not
/// shown to whoever may see the revenue. It is computed over the payroll months that are closed and
/// fall wholly within the period, against the revenue of the same sites and months, so a month
/// still being prepared does not distort the ratio.
/// </para>
/// </summary>
public sealed class SteeringService(
    ISteeringQueries queries,
    IStatisticsQueries statistics,
    CostMarginService costs,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int MaximumPeriodDays = 366;

    public bool CanSeePayroll => currentUser.Has(Permissions.HumanResources.Salary);

    public async Task<Result<SteeringDashboard>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Steering.View);
        if (permitted.IsFailure) return Result.Failure<SteeringDashboard>(permitted.FirstError);
        if (to < from) return Result.Failure<SteeringDashboard>(Error.Validation("Stats_PeriodReversed"));
        if (to.DayNumber - from.DayNumber + 1 > MaximumPeriodDays)
            return Result.Failure<SteeringDashboard>(Error.Validation("Stats_PeriodTooLong", MaximumPeriodDays));

        var offset = clock.Now.Offset;
        var sites = CostMarginService.Sites(currentUser);
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), offset);

        var activity = await queries.GetActivityAsync(sites, start, end, ct);

        var invoiced = 0m;
        var credited = 0m;
        var collected = 0m;
        foreach (var site in sites)
        {
            var finance = await statistics.GetFinanceAsync(site, from, to, ct);
            invoiced += finance.Invoiced;
            credited += finance.Credited;
            collected += finance.Collected;
        }

        var margin = await costs.ComputeAsync(from, to, ct);
        var receivables = await queries.GetReceivablesAsync(sites, ct);
        var payroll = CanSeePayroll ? await queries.GetClosedPayrollAsync(sites, from, to, ct) : null;
        var quality = await queries.GetQualityAsync(sites, start, end, from, to, ct);
        var labels = await queries.GetLabelsAsync(ct);

        return Result.Success(Compute(from, to, sites.Count, activity, (invoiced, credited, collected), margin.Totals,
                                      receivables, payroll, quality, labels, clock.Today, offset, currentUser.Language));
    }

    public static AgeingBand BandOf(DateOnly? dueDate, DateOnly today)
    {
        if (dueDate is not { } due || due >= today) return AgeingBand.NotDue;
        return (today.DayNumber - due.DayNumber) switch
        {
            <= 30 => AgeingBand.Days1To30,
            <= 60 => AgeingBand.Days31To60,
            <= 90 => AgeingBand.Days61To90,
            <= 180 => AgeingBand.Days91To180,
            _ => AgeingBand.Over180
        };
    }

    public static IReadOnlyList<AgeingRow> Age(IEnumerable<ReceivableRow> receivables, DateOnly today)
    {
        var owed = receivables
            .Select(r => (Row: r, Outstanding: Invoice.ComputeOutstanding(r.Kind, r.TotalAmount, r.PaidAmount, r.CreditedAmount)))
            .Where(r => r.Outstanding > 0m)
            .ToList();

        return Enum.GetValues<AgeingBand>()
            .Select(band =>
            {
                var rows = owed.Where(r => BandOf(r.Row.DueDate, today) == band).ToList();
                return new AgeingRow(band, rows.Count, Money.Round(rows.Sum(r => r.Outstanding)));
            })
            .ToList();
    }

    public static SteeringDashboard Compute(
        DateOnly from, DateOnly to, int siteCount,
        IReadOnlyList<SteeringActivityRow> activity,
        (decimal Invoiced, decimal Credited, decimal Collected) finance,
        MarginTotals margin,
        IReadOnlyList<ReceivableRow> receivables,
        IReadOnlyList<PayrollMonthRow>? payroll,
        QualityCounts quality,
        SteeringLabels labels,
        DateOnly today,
        TimeSpan offset,
        AppLanguage language)
    {
        var performed = activity.Where(a => a.Item.Status != LabOrderItemStatus.Cancelled).ToList();
        var revenue = Money.Round(performed.Sum(a => a.Item.ChargedPrice));

        List<RevenueRow> Breakdown<TKey>(IEnumerable<SteeringActivityRow> rows, Func<SteeringActivityRow, TKey> key,
                                         Func<TKey, (string Key, string Label)> label, bool byRevenue = true)
            where TKey : notnull
        {
            var groups = rows.GroupBy(key)
                .Select(g =>
                {
                    var (k, l) = label(g.Key);
                    var amount = Money.Round(g.Sum(a => a.Item.ChargedPrice));
                    return new RevenueRow(k, l, g.Select(a => a.Item.LabOrderId).Distinct().Count(), g.Count(), amount,
                                          revenue == 0m ? null : Math.Round((double)(amount / revenue * 100m), 1));
                });

            return byRevenue
                ? groups.OrderByDescending(r => r.Revenue).ThenBy(r => r.Label, StringComparer.CurrentCulture).ToList()
                : groups.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        }

        DateOnly Day(SteeringActivityRow a) => DateOnly.FromDateTime(a.Item.RegisteredAt.ToOffset(offset).DateTime);

        // Every day of the period, the empty ones included: a closure is information.
        var byDay = Breakdown(performed, Day, d => (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)), byRevenue: false);
        var days = new List<RevenueRow>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            days.Add(byDay.FirstOrDefault(r => r.Key == key) ?? new RevenueRow(key, day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), 0, 0, 0m, null));
        }

        var bySite = Breakdown(performed, a => a.SiteId,
                               id => (id.ToString(), labels.Sites.GetValueOrDefault(id, "?")));
        var byPayer = Breakdown(performed, a => a.Item.PayerId ?? Guid.Empty,
                                id => (id.ToString(), id == Guid.Empty ? string.Empty
                                                      : labels.Payers.TryGetValue(id, out var n) ? n.For(language) : "?"));
        var byPrescriber = Breakdown(performed, a => a.PrescriberId ?? Guid.Empty,
                                     id => (id.ToString(), id == Guid.Empty ? string.Empty
                                                           : labels.Prescribers.GetValueOrDefault(id, "?")));

        var ageing = Age(receivables, today);

        PayrollFigures? payrollFigures = null;
        if (payroll is not null)
        {
            // Only months wholly inside the period, and the revenue of those same sites and months.
            var months = payroll.Where(p => new DateOnly(p.Year, p.Month, 1) >= from
                                            && new DateOnly(p.Year, p.Month, 1).AddMonths(1).AddDays(-1) <= to)
                                .ToList();
            var keys = months.Select(p => (p.SiteId, p.Year, p.Month)).ToHashSet();
            var monthRevenue = Money.Round(performed
                .Where(a =>
                {
                    var local = a.Item.RegisteredAt.ToOffset(offset);
                    return keys.Contains((a.SiteId, local.Year, local.Month));
                })
                .Sum(a => a.Item.ChargedPrice));
            var gross = Money.Round(months.Sum(p => p.Gross));
            var cost = Money.Round(months.Sum(p => p.EmployerCost));

            payrollFigures = new PayrollFigures(months.Count, gross, cost, monthRevenue,
                                                monthRevenue == 0m ? null : Math.Round((double)(cost / monthRevenue * 100m), 1));
        }

        int Eqa(EqaOutcome outcome) => quality.EqaOutcomes.GetValueOrDefault(outcome);

        var qualityFigures = new QualityFigures(
            quality.QcResults, quality.QcRejected,
            quality.QcResults == 0 ? null : Math.Round(100.0 * quality.QcRejected / quality.QcResults, 1),
            quality.OpenNonConformities, quality.NonConformitiesOpened,
            Eqa(EqaOutcome.Satisfactory), Eqa(EqaOutcome.Questionable), Eqa(EqaOutcome.Unsatisfactory),
            Eqa(EqaOutcome.NotSubmitted));

        // Turnaround as the statistics screen defines it, so the two screens never disagree.
        var tat = StatisticsService.Compute(from, to, [.. activity.Select(a => a.Item)],
                                            new StatisticsLabels(new Dictionary<Guid, (string, MultilingualText)>(),
                                                                 new Dictionary<Guid, (string, MultilingualText)>(),
                                                                 new Dictionary<Guid, MultilingualText>()),
                                            new FinanceTotals(0m, 0m, 0m), offset).Summary;

        return new SteeringDashboard(
            from, to, siteCount,
            performed.Select(a => a.Item.LabOrderId).Distinct().Count(), performed.Count, revenue,
            finance.Invoiced, finance.Credited, finance.Collected,
            margin,
            Money.Round(ageing.Sum(a => a.Amount)),
            Money.Round(ageing.Where(a => a.Band != AgeingBand.NotDue).Sum(a => a.Amount)),
            ageing, payrollFigures, qualityFigures,
            tat.AverageTurnaroundHours, tat.OnTimePercent,
            days, bySite, byPayer, byPrescriber);
    }
}

public interface ISteeringQueries
{
    /// <summary>Every test on orders registered in <c>[start, end)</c> on the given sites.</summary>
    Task<IReadOnlyList<SteeringActivityRow>> GetActivityAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start,
                                                              DateTimeOffset end, CancellationToken ct = default);

    /// <summary>Issued invoices of the sites still open (issued or partly paid), whatever their date.</summary>
    Task<IReadOnlyList<ReceivableRow>> GetReceivablesAsync(IReadOnlyList<Guid> siteIds, CancellationToken ct = default);

    /// <summary>Closed payroll months of the sites overlapping the period, summed per site and month.</summary>
    Task<IReadOnlyList<PayrollMonthRow>> GetClosedPayrollAsync(IReadOnlyList<Guid> siteIds, DateOnly from, DateOnly to,
                                                               CancellationToken ct = default);

    /// <summary>QC results and rejections measured in the period, open NCs now and opened in the period, EQA rounds.</summary>
    Task<QualityCounts> GetQualityAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start, DateTimeOffset end,
                                        DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<SteeringLabels> GetLabelsAsync(CancellationToken ct = default);
}
