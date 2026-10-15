using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// The consumption data of the stock items: the activity of the analyses they serve, their
/// movements and their stock at both ends of a period. A fixed handful of set-based queries for the
/// whole site, whatever the number of items, so the dashboard can afford it.
/// </summary>
public sealed class StockPlanningQueries(LisDbContext db) : IStockPlanningQueries
{
    /// <summary>Results that count as a test run: a value entered, whatever validation stage it reached.</summary>
    private static readonly TestResultStatus[] Produced =
    [
        TestResultStatus.Entered, TestResultStatus.TechnicallyValidated, TestResultStatus.Validated,
        TestResultStatus.Reported
    ];

    private static readonly StockMovementKind[] IssueKinds = [StockMovementKind.Issue, StockMovementKind.Consumption];

    private static readonly StockMovementKind[] InKinds = [StockMovementKind.Receipt, StockMovementKind.TransferIn];

    public async Task<IReadOnlyList<ItemConsumptionData>> GetConsumptionDataAsync(
        Guid siteId, DateOnly from, DateOnly to, DateOnly today, IReadOnlyCollection<Guid>? stockItemIds,
        CancellationToken ct = default)
    {
        var wanted = stockItemIds?.ToList();

        var items = await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive && (wanted == null || wanted.Contains(i.Id)))
            .Select(i => new
            {
                i.Id, i.Code, i.Name, i.Kind, i.UnitOfMeasure, i.TestsPerPack, i.UsedByAnalysisCodesCsv,
                i.DeadVolumePercent, i.DeadVolumeTestsPerOpening, i.TestsPerResultCsv, i.TestsPerCalibration,
                i.SafetyStockDays, i.TypicalShelfLifeDays, i.ReorderPointComputed, i.MinimumStock, i.ReorderPoint,
                i.IsCritical,
                LeadTimeDays = db.Suppliers.Where(s => s.Id == i.PreferredSupplierId)
                                           .Select(s => (int?)s.LeadTimeDays).FirstOrDefault(),
                Physical = i.Lots.Sum(l => (decimal?)l.QuantityRemaining) ?? 0m,
                Usable = i.Lots
                    .Where(l => !l.IsQuarantined && !l.IsRejected && !l.IsRecalled
                                && l.ExpiryDate >= today
                                && (l.OpenedOn == null
                                    || l.StabilityAfterOpeningDays == null
                                    || l.OpenedOn.Value.AddDays(l.StabilityAfterOpeningDays.Value) >= today))
                    .Sum(l => (decimal?)l.QuantityRemaining) ?? 0m
            })
            .ToListAsync(ct);

        if (items.Count == 0) return [];

        var ids = items.Select(i => i.Id).ToList();
        var start = Start(from);
        var end = Start(to.AddDays(1));

        var codesOf = items.ToDictionary(i => i.Id, i => ReagentLotSelector.AnalysisCodes(i.UsedByAnalysisCodesCsv));
        var activity = await ActivityAsync(siteId, [.. codesOf.Values.SelectMany(c => c).Distinct()], start, end, ct);

        // Movements from the start of the period on: the stock at a date is today's lots less what
        // moved since, which keeps the figures consistent with the lots whatever the ledger's age.
        var moves = (await db.StockMovements.AsNoTracking()
                .Where(m => m.SiteId == siteId && ids.Contains(m.StockItemId) && m.OccurredAt >= start)
                .Select(m => new { m.StockItemId, m.Kind, m.OccurredAt, m.Quantity })
                .ToListAsync(ct))
            .Select(m => new { m.StockItemId, m.Kind, After = m.OccurredAt >= end, m.Quantity })
            .ToList();

        var counted = await (
                from c in db.InventoryCounts.AsNoTracking()
                where c.SiteId == siteId && c.Status == InventoryCountStatus.Closed
                      && c.CountDate >= @from && c.CountDate <= to
                from l in c.Lines
                where ids.Contains(l.StockItemId)
                select new { l.StockItemId, c.Number, c.CountDate })
            .ToListAsync(ct);

        var lastCount = counted.GroupBy(c => c.StockItemId)
                               .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CountDate)
                                                               .ThenByDescending(c => c.Number).First().Number);

        // As StockMovement.SignedQuantity: what comes in adds, everything else takes away.
        static decimal Signed(StockMovementKind kind, decimal quantity) =>
            kind is StockMovementKind.Receipt or StockMovementKind.Return or StockMovementKind.PositiveAdjustment
                or StockMovementKind.TransferIn
                ? quantity
                : -quantity;

        var result = new List<ItemConsumptionData>(items.Count);

        foreach (var i in items)
        {
            var mine = moves.Where(m => m.StockItemId == i.Id).ToList();
            var since = mine.Sum(m => Signed(m.Kind, m.Quantity));
            var after = mine.Where(m => m.After).Sum(m => Signed(m.Kind, m.Quantity));
            var inPeriod = mine.Where(m => !m.After).ToList();

            var issued = inPeriod.Where(m => IssueKinds.Contains(m.Kind)).Sum(m => m.Quantity)
                         - inPeriod.Where(m => m.Kind == StockMovementKind.Return).Sum(m => m.Quantity);
            var receipts = inPeriod.Where(m => InKinds.Contains(m.Kind)).Sum(m => m.Quantity);

            var codes = codesOf[i.Id];

            result.Add(new ItemConsumptionData(
                i.Id, i.Code, i.Name, i.Kind, i.UnitOfMeasure, i.TestsPerPack,
                new StockConsumptionPolicy
                {
                    DeadVolumePercent = i.DeadVolumePercent,
                    DeadVolumeTestsPerOpening = i.DeadVolumeTestsPerOpening,
                    TestsPerResultCsv = i.TestsPerResultCsv,
                    TestsPerCalibration = i.TestsPerCalibration,
                    SafetyStockDays = i.SafetyStockDays,
                    TypicalShelfLifeDays = i.TypicalShelfLifeDays,
                    ReorderPointComputed = i.ReorderPointComputed
                },
                codes,
                [.. codes.Select(c => activity.GetValueOrDefault(c) ?? new AnalysisActivity(c, 0, 0, 0, 0))],
                Math.Max(0m, issued), receipts,
                i.Physical - since, i.Physical - after,
                lastCount.GetValueOrDefault(i.Id),
                i.LeadTimeDays ?? 0, i.MinimumStock, i.ReorderPoint, i.Usable, i.IsCritical));
        }

        return result;
    }

    /// <summary>
    /// What each analysis ran in the period: patient analyses with a value, control levels, calibrations
    /// and repeats. Counted once per ordered analysis (a blood count is one test, not twenty
    /// parameters), once per control level and run, once per calibration of an analyser.
    /// </summary>
    private async Task<Dictionary<string, AnalysisActivity>> ActivityAsync(
        Guid siteId, IReadOnlyList<string> codes, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (codes.Count == 0) return [];

        var wanted = codes.ToList();

        var parameters = await (
                from p in db.TestParameters.IgnoreQueryFilters().AsNoTracking()
                join a in db.AnalysisTests.IgnoreQueryFilters().AsNoTracking() on p.AnalysisTestId equals a.Id
                where wanted.Contains(a.Code)
                select new { p.Id, a.Code, AnalysisTestId = a.Id })
            .ToListAsync(ct);

        if (parameters.Count == 0) return [];

        var parameterIds = parameters.Select(p => p.Id).ToList();
        var codeOfParameter = parameters.ToDictionary(p => p.Id, p => p.Code);
        var analysisIds = parameters.Select(p => p.AnalysisTestId).Distinct().ToList();
        var codeOfAnalysis = parameters.GroupBy(p => p.AnalysisTestId).ToDictionary(g => g.Key, g => g.First().Code);

        var patients = await db.TestResults.AsNoTracking()
            .Where(r => r.SiteId == siteId && Produced.Contains(r.Status)
                        && parameterIds.Contains(r.TestParameterId)
                        && (r.MeasuredAt ?? r.EnteredAt) >= start && (r.MeasuredAt ?? r.EnteredAt) < end)
            .Select(r => new { r.LabOrderItemId, r.TestParameterId })
            .Distinct()
            .ToListAsync(ct);

        var patientCounts = patients
            .Select(r => (r.LabOrderItemId, Code: codeOfParameter[r.TestParameterId]))
            .Distinct()
            .GroupBy(r => r.Code)
            .ToDictionary(g => g.Key, g => g.Count());

        // An analysis sent back to the bench in the period: its extra runs, at least one.
        var repeats = await (
                from i in db.LabOrderItems.AsNoTracking()
                join o in db.LabOrders.AsNoTracking() on i.LabOrderId equals o.Id
                where o.SiteId == siteId && analysisIds.Contains(i.AnalysisTestId)
                      && i.RepeatedAt != null && i.RepeatedAt >= start && i.RepeatedAt < end
                select new { i.AnalysisTestId, i.RunCount })
            .ToListAsync(ct);

        var rerunCounts = repeats.GroupBy(r => codeOfAnalysis[r.AnalysisTestId])
                                 .ToDictionary(g => g.Key, g => g.Sum(r => Math.Max(1, r.RunCount - 1)));

        var controls = await (
                from q in db.QcResults.AsNoTracking()
                join t in db.QcTargets.AsNoTracking() on q.QcTargetId equals t.Id
                where q.SiteId == siteId && parameterIds.Contains(q.TestParameterId)
                      && q.MeasuredAt >= start && q.MeasuredAt < end
                select new { q.TestParameterId, t.QcMaterialId, q.InstrumentId, q.MeasuredAt })
            .ToListAsync(ct);

        var qcCounts = controls
            .Select(q => (Code: codeOfParameter[q.TestParameterId], q.QcMaterialId, q.InstrumentId, q.MeasuredAt))
            .Distinct()
            .GroupBy(q => q.Code)
            .ToDictionary(g => g.Key, g => g.Count());

        var calibrations = await (
                from c in db.CalibrationRecords.AsNoTracking()
                join n in db.Instruments.IgnoreQueryFilters().AsNoTracking() on c.InstrumentId equals n.Id
                where n.SiteId == siteId && c.TestParameterId != null && parameterIds.Contains(c.TestParameterId.Value)
                      && c.PerformedAt >= start && c.PerformedAt < end
                      && c.Outcome != Domain.Equipment.CalibrationOutcome.Aborted
                select new { TestParameterId = c.TestParameterId!.Value, c.InstrumentId, c.PerformedAt })
            .ToListAsync(ct);

        var calibrationCounts = calibrations
            .Select(c => (Code: codeOfParameter[c.TestParameterId], c.InstrumentId, c.PerformedAt))
            .Distinct()
            .GroupBy(c => c.Code)
            .ToDictionary(g => g.Key, g => g.Count());

        return codes.ToDictionary(c => c, c => new AnalysisActivity(
            c, patientCounts.GetValueOrDefault(c), qcCounts.GetValueOrDefault(c),
            calibrationCounts.GetValueOrDefault(c), rerunCounts.GetValueOrDefault(c)));
    }

    public async Task<IReadOnlyList<ExpiringLotRow>> GetExpiringLotsAsync(Guid siteId, DateOnly today, int horizonDays,
                                                                         CancellationToken ct = default)
    {
        var horizon = today.AddDays(horizonDays);

        return await (
                from l in db.ReagentLots.AsNoTracking()
                join i in db.StockItems.AsNoTracking() on l.StockItemId equals i.Id
                where i.SiteId == siteId && l.QuantityRemaining > 0m && !l.IsRejected && !l.IsRecalled
                      && l.ExpiryDate <= horizon
                orderby l.ExpiryDate
                select new ExpiringLotRow(l.Id, i.Code, i.Name, l.LotNumber, l.ExpiryDate, l.QuantityRemaining,
                                          i.UnitOfMeasure, ExpiryAlertLevel.None))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetComputedReorderItemsAsync(Guid siteId, CancellationToken ct = default) =>
        await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive && i.ReorderPointComputed)
            .Select(i => i.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix,
                                                                                  CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.SiteId == null && s.Key.StartsWith(prefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    /// <summary>Midnight of a day, laboratory time.</summary>
    internal static DateTimeOffset Start(DateOnly day)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
    }
}
