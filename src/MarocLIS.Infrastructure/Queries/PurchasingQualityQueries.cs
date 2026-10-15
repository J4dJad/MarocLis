using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// Finds the reagent lot in use for analyses, shared by the result entry, the analyser interface
/// and the QC entry so all three answer the same way. The reagent articles are few, so they are
/// loaded with their lots and matched in memory with <see cref="ReagentLotSelector"/>.
/// </summary>
internal static class ReagentLotLookup
{
    public static async Task<IReadOnlyDictionary<Guid, Guid>> ResolveAsync(
        LisDbContext db, IReadOnlyCollection<Guid> testParameterIds, Guid? namedLotId, Guid siteId,
        DateOnly today, CancellationToken ct)
    {
        if (testParameterIds.Count == 0) return new Dictionary<Guid, Guid>();

        var analysisOf = await AnalysisCodesAsync(db, testParameterIds, ct);

        // Only the lots that can answer: opened and still in service, plus the one named for the
        // run. The exhausted, expired and withdrawn lots of past years are not read on every tube.
        var reagents = await db.StockItems.AsNoTracking()
            .Include(i => i.Lots.Where(l => l.Id == namedLotId
                                            || (l.OpenedOn != null && !l.IsRejected && !l.IsRecalled
                                                && l.ExpiryDate >= today)))
            .Where(i => i.SiteId == siteId && i.IsActive && i.Kind == StockItemKind.Reagent
                        && i.UsedByAnalysisCodesCsv != null)
            .ToListAsync(ct);

        var named = namedLotId is { } lotId
            ? reagents.FirstOrDefault(i => i.Lots.Any(l => l.Id == lotId))
            : null;

        var lots = new Dictionary<Guid, Guid>();

        foreach (var parameter in analysisOf.Where(p => p.Code is not null))
        {
            // A lot named for a QC run counts for the analyses of its own article only.
            if (named is not null && ReagentLotSelector.IsUsedBy(named, parameter.Code!))
            {
                lots[parameter.Id] = namedLotId!.Value;
                continue;
            }

            if (ReagentLotSelector.LotInUse(reagents, parameter.Code!, today) is { } lot)
                lots[parameter.Id] = lot.Id;
        }

        return lots;
    }

    /// <summary>
    /// For each parameter, the lot awaiting release loaded on the analyser for its acceptance QC.
    /// Absent when there is none, or when two articles' lots would both claim the parameter.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> ResolveUnderEvaluationAsync(
        LisDbContext db, IReadOnlyCollection<Guid> testParameterIds, Guid siteId, Guid instrumentId,
        DateOnly today, CancellationToken ct)
    {
        if (testParameterIds.Count == 0) return new Dictionary<Guid, Guid>();

        var evaluated = await (
            from l in db.ReagentLots.AsNoTracking()
            join i in db.StockItems.AsNoTracking() on l.StockItemId equals i.Id
            where i.SiteId == siteId && i.IsActive && i.Kind == StockItemKind.Reagent
                  && i.UsedByAnalysisCodesCsv != null
                  && l.EvaluationInstrumentId == instrumentId
                  && l.IsQuarantined && !l.IsRejected && !l.IsRecalled && l.ReleasedOn == null
                  && l.ExpiryDate >= today
            select new { l.Id, i.UsedByAnalysisCodesCsv })
            .ToListAsync(ct);

        var lots = new Dictionary<Guid, Guid>();
        if (evaluated.Count == 0) return lots;

        foreach (var parameter in (await AnalysisCodesAsync(db, testParameterIds, ct)).Where(p => p.Code is not null))
        {
            var code = parameter.Code!.Trim().ToUpperInvariant();
            var matching = evaluated
                .Where(e => ReagentLotSelector.AnalysisCodes(e.UsedByAnalysisCodesCsv).Contains(code))
                .ToList();

            if (matching.Count == 1) lots[parameter.Id] = matching[0].Id;
        }

        return lots;
    }

    private static async Task<List<(Guid Id, string? Code)>> AnalysisCodesAsync(
        LisDbContext db, IReadOnlyCollection<Guid> testParameterIds, CancellationToken ct)
    {
        var ids = testParameterIds.Distinct().ToList();

        var rows = await db.TestParameters.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                Code = db.AnalysisTests.IgnoreQueryFilters()
                         .Where(a => a.Id == p.AnalysisTestId).Select(a => a.Code).FirstOrDefault()
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => (r.Id, r.Code))];
    }
}

public sealed class PurchasingQualityQueries(LisDbContext db) : IPurchasingQualityQueries
{
    public async Task<IReadOnlyList<GtinMatch>> FindItemsByGtinAsync(string gtin, Guid siteId,
                                                                    CancellationToken ct = default) =>
        await db.StockItems.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.IsActive && i.Gtin == gtin)
            .Select(i => new GtinMatch(i.Id, i.Code, i.Name))
            .ToListAsync(ct);

    public async Task<LotQcEvidence?> GetLotQcEvidenceAsync(Guid reagentLotId, DateOnly today,
                                                           CancellationToken ct = default)
    {
        var lot = await db.ReagentLots.AsNoTracking()
            .Where(l => l.Id == reagentLotId)
            .Select(l => new { l.Id, l.LotNumber, l.StockItemId })
            .FirstOrDefaultAsync(ct);
        if (lot is null) return null;

        var item = await db.StockItems.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.Id == lot.StockItemId)
            .Select(i => new { i.Code, i.Kind, i.UsedByAnalysisCodesCsv, i.SiteId })
            .FirstOrDefaultAsync(ct);
        if (item is null) return null;

        var codes = ReagentLotSelector.AnalysisCodes(item.UsedByAnalysisCodesCsv);
        var levels = await LevelsAsync(codes, item.SiteId, today, ct);

        var points = await db.QcResults.AsNoTracking()
            .Where(r => r.ReagentLotId == reagentLotId)
            .Select(r => new LotQcPoint(r.Id, r.QcTargetId, r.QcRunId, r.Outcome, r.MeasuredAt))
            .ToListAsync(ct);

        return new LotQcEvidence(lot.Id, lot.LotNumber, item.Code, item.Kind, codes,
                                 [.. levels.Select(l => new RequiredQcLevel(l.Key, l.Value))], points);
    }

    public async Task<LotComparisonData?> GetLotComparisonDataAsync(Guid reagentLotId,
                                                                   CancellationToken ct = default)
    {
        var lot = await db.ReagentLots.AsNoTracking()
            .Where(l => l.Id == reagentLotId)
            .Select(l => new { l.Id, l.LotNumber, l.StockItemId, l.ReceivedOn, l.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (lot is null) return null;

        var itemCode = await db.StockItems.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.Id == lot.StockItemId).Select(i => i.Code).FirstOrDefaultAsync(ct) ?? "—";

        // The lot it replaces: the last one of the same article put into service before it.
        var previous = await db.ReagentLots.AsNoTracking()
            .Where(l => l.StockItemId == lot.StockItemId && l.Id != lot.Id && !l.IsRejected
                        && l.ReleasedOn != null
                        && (l.ReceivedOn < lot.ReceivedOn
                            || (l.ReceivedOn == lot.ReceivedOn && l.CreatedAt < lot.CreatedAt)))
            .OrderByDescending(l => l.ReceivedOn).ThenByDescending(l => l.CreatedAt)
            .Select(l => new { l.Id, l.LotNumber })
            .FirstOrDefaultAsync(ct);

        var lotIds = previous is null ? new[] { lot.Id } : [lot.Id, previous.Id];

        var values = await db.QcResults.AsNoTracking()
            .Where(r => r.ReagentLotId != null && lotIds.Contains(r.ReagentLotId.Value)
                        && (r.Outcome == QcOutcome.Accepted || r.Outcome == QcOutcome.Warning))
            .Select(r => new { r.ReagentLotId, r.QcTargetId, r.Value })
            .ToListAsync(ct);

        var labels = await LabelsAsync([.. values.Select(v => v.QcTargetId).Distinct()], ct);

        QcValueOnLot Value(Guid target, decimal value) =>
            new(target, labels.GetValueOrDefault(target, "—"), value);

        return new LotComparisonData(
            itemCode, lot.LotNumber, previous?.LotNumber,
            [.. values.Where(v => v.ReagentLotId == lot.Id).Select(v => Value(v.QcTargetId, v.Value))],
            [.. values.Where(v => previous is not null && v.ReagentLotId == previous.Id)
                      .Select(v => Value(v.QcTargetId, v.Value))]);
    }

    public async Task<IReadOnlyList<RecallResultRow>> GetResultsProducedWithLotAsync(
        Guid reagentLotId, CancellationToken ct = default)
    {
        var rows = await (
            from r in db.TestResults.AsNoTracking()
            where r.ReagentLotId == reagentLotId
            join o in db.LabOrders.AsNoTracking() on r.LabOrderId equals o.Id
            join p in db.Patients.AsNoTracking() on o.PatientId equals p.Id
            join tp in db.TestParameters.AsNoTracking() on r.TestParameterId equals tp.Id
            join a in db.AnalysisTests.AsNoTracking().IgnoreQueryFilters() on tp.AnalysisTestId equals a.Id
            orderby o.AccessionNumber, a.Code, tp.Code
            select new
            {
                r.Id, PatientId = p.Id, p.LastName, p.FirstName, p.MedicalRecordNumber,
                OrderId = o.Id, o.AccessionNumber, AnalysisCode = a.Code, ParameterCode = tp.Code,
                r.MeasuredAt, r.EnteredAt, r.Status
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new RecallResultRow(
                r.Id, r.PatientId, $"{r.LastName} {r.FirstName}".Trim(), r.MedicalRecordNumber,
                r.OrderId, r.AccessionNumber, r.AnalysisCode, r.ParameterCode,
                r.MeasuredAt ?? r.EnteredAt,
                r.Status is TestResultStatus.Validated or TestResultStatus.Reported))
        ];
    }

    public async Task<IReadOnlyList<LotCertificateRow>> GetLotCertificatesAsync(Guid reagentLotId,
                                                                               CancellationToken ct = default) =>
        await db.Set<LotCertificateFile>().AsNoTracking()
            .Where(f => f.ReagentLotId == reagentLotId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new LotCertificateRow(f.Id, f.FileName, f.CreatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// The year's deliveries, refusals and non-conformities of every supplier that is active or had
    /// any of them. A delivery is one receipt date on one purchase order (several lines or lots
    /// received together count once); a receipt without an order is one per date and delivery note.
    /// </summary>
    public async Task<IReadOnlyList<SupplierEvaluationInput>> GetSupplierEvaluationInputsAsync(
        int year, CancellationToken ct = default)
    {
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var end = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2);

        var suppliers = await db.Suppliers.AsNoTracking()
            .Select(s => new
            {
                s.Id, s.Code, s.Name, s.IsActive, s.IsApproved, s.EvaluationScore, s.LastEvaluationDate,
                s.LeadTimeDays, s.RegulatoryRegistrationNumber, s.RegulatoryRegistrationValidUntil
            })
            .ToListAsync(ct);

        var lots = await db.ReagentLots.AsNoTracking()
            .Where(l => l.SupplierId != null && l.ReceivedOn >= from && l.ReceivedOn <= to)
            .Select(l => new { SupplierId = l.SupplierId!.Value, l.PurchaseOrderId, l.ReceivedOn, l.DeliveryNoteReference })
            .ToListAsync(ct);

        var orderIds = lots.Where(l => l.PurchaseOrderId != null).Select(l => l.PurchaseOrderId!.Value).Distinct().ToList();

        var orders = await db.PurchaseOrders.AsNoTracking().IgnoreQueryFilters()
            .Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.OrderDate, o.ExpectedDeliveryDate })
            .ToDictionaryAsync(o => o.Id, ct);

        var refusals = await db.Set<RefusedDelivery>().AsNoTracking()
            .Where(r => r.SupplierId != null && r.RefusedOn >= from && r.RefusedOn <= to)
            .GroupBy(r => r.SupplierId!.Value)
            .Select(g => new { SupplierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.SupplierId, g => g.Count, ct);

        // Dismissed files (judged not to be non-conformities) do not count against the supplier.
        var ncs = await db.NonConformities.AsNoTracking()
            .Where(n => n.SupplierId != null && n.DetectedAt >= start && n.DetectedAt < end
                        && n.Status != NonConformityStatus.Rejected)
            .Select(n => new { SupplierId = n.SupplierId!.Value, n.DetectedAt })
            .ToListAsync(ct);

        var ncCounts = ncs.Where(n => n.DetectedAt.Year == year)
                          .GroupBy(n => n.SupplierId)
                          .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<SupplierEvaluationInput>();

        foreach (var s in suppliers)
        {
            var deliveries = lots
                .Where(l => l.SupplierId == s.Id)
                .GroupBy(l => l.PurchaseOrderId is { } po
                    ? $"{po:N}|{l.ReceivedOn:yyyyMMdd}"
                    : $"-|{l.ReceivedOn:yyyyMMdd}|{l.DeliveryNoteReference}")
                .Select(g =>
                {
                    var first = g.First();
                    DateOnly? due = first.PurchaseOrderId is { } po && orders.TryGetValue(po, out var order)
                        ? order.ExpectedDeliveryDate ?? order.OrderDate.AddDays(s.LeadTimeDays)
                        : null;
                    return new SupplierDelivery(first.ReceivedOn, due);
                })
                .ToList();

            var refused = refusals.GetValueOrDefault(s.Id);
            var nc = ncCounts.GetValueOrDefault(s.Id);

            if (!s.IsActive && deliveries.Count == 0 && refused == 0 && nc == 0) continue;

            result.Add(new SupplierEvaluationInput(
                s.Id, s.Code, s.Name, s.IsApproved, s.EvaluationScore, s.LastEvaluationDate,
                s.RegulatoryRegistrationNumber, s.RegulatoryRegistrationValidUntil,
                deliveries, refused, nc));
        }

        return result;
    }

    public Task<SupplierRegistration?> GetSupplierRegistrationAsync(Guid supplierId,
                                                                    CancellationToken ct = default) =>
        db.Suppliers.AsNoTracking()
          .Where(s => s.Id == supplierId)
          .Select(s => new SupplierRegistration(s.Name, s.RegulatoryRegistrationNumber,
                                                s.RegulatoryRegistrationValidUntil))
          .FirstOrDefaultAsync(ct);

    public Task<StorageCondition?> GetStorageConditionAsync(Guid stockItemId, CancellationToken ct = default) =>
        db.StockItems.AsNoTracking().IgnoreQueryFilters()
          .Where(i => i.Id == stockItemId)
          .Select(i => (StorageCondition?)i.StorageCondition)
          .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ReagentLotOption>> GetReagentLotsForQcAsync(Guid siteId, DateOnly today,
                                                                               CancellationToken ct = default) =>
        await (
            from l in db.ReagentLots.AsNoTracking()
            join i in db.StockItems.AsNoTracking() on l.StockItemId equals i.Id
            where i.SiteId == siteId && i.Kind == StockItemKind.Reagent && i.UsedByAnalysisCodesCsv != null
                  && !l.IsRejected && !l.IsRecalled && l.ExpiryDate >= today
                  && (l.IsQuarantined || l.OpenedOn != null)
            orderby l.IsQuarantined descending, i.Code, l.ExpiryDate
            select new ReagentLotOption(l.Id, i.Code, l.LotNumber, l.ExpiryDate, l.IsQuarantined))
            .ToListAsync(ct);

    // The active control levels of the analyses on the lot's site, with a label naming parameter,
    // level and analyser. Only the site's analysers still in service: a lot can only get QC points
    // there, so another site's levels, or a decommissioned analyser's, could never be satisfied.
    private async Task<Dictionary<Guid, string>> LevelsAsync(IReadOnlyList<string> codes, Guid siteId,
                                                             DateOnly today, CancellationToken ct)
    {
        if (codes.Count == 0) return [];

        var parameterIds = await (
            from p in db.TestParameters.AsNoTracking()
            join a in db.AnalysisTests.AsNoTracking() on p.AnalysisTestId equals a.Id
            where codes.Contains(a.Code)
            select p.Id).ToListAsync(ct);

        var targetIds = await (
            from t in db.QcTargets.AsNoTracking()
            join m in db.QcMaterials.AsNoTracking() on t.QcMaterialId equals m.Id
            join i in db.Instruments.AsNoTracking() on t.InstrumentId equals i.Id
            where t.IsActive && m.IsActive && m.ExpiryDate >= today && parameterIds.Contains(t.TestParameterId)
                  && i.SiteId == siteId && i.Status != EquipmentStatus.Decommissioned
                  && (i.DecommissioningDate == null || i.DecommissioningDate > today)
            select t.Id).ToListAsync(ct);

        return await LabelsAsync(targetIds, ct);
    }

    private async Task<Dictionary<Guid, string>> LabelsAsync(IReadOnlyList<Guid> targetIds, CancellationToken ct)
    {
        if (targetIds.Count == 0) return [];

        var rows = await (
            from t in db.QcTargets.AsNoTracking().IgnoreQueryFilters()
            where targetIds.Contains(t.Id)
            join p in db.TestParameters.AsNoTracking() on t.TestParameterId equals p.Id
            join m in db.QcMaterials.AsNoTracking().IgnoreQueryFilters() on t.QcMaterialId equals m.Id
            select new
            {
                t.Id, Parameter = p.Code, m.Level, Material = m.LotNumber,
                Instrument = db.Instruments.IgnoreQueryFilters()
                               .Where(x => x.Id == t.InstrumentId).Select(x => x.Code).FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id,
                                 r => $"{r.Parameter} · {LevelFr(r.Level)} ({r.Material}) · {r.Instrument ?? "—"}");
    }

    private static string LevelFr(QcLevel level) => level switch
    {
        QcLevel.Low => "niveau bas",
        QcLevel.Normal => "niveau normal",
        QcLevel.High => "niveau haut",
        _ => "niveau supplémentaire"
    };
}
