using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class QualityQueries(LisDbContext db) : IQualityQueries
{
    public Task<IReadOnlyDictionary<Guid, Guid>> GetReagentLotsForQcAsync(
        IReadOnlyCollection<Guid> testParameterIds, Guid? namedLotId, Guid siteId, DateOnly today,
        CancellationToken ct = default) =>
        ReagentLotLookup.ResolveAsync(db, testParameterIds, namedLotId, siteId, today, ct);

    public Task<MarocLIS.Domain.Inventory.ReagentLot?> GetReagentLotAsync(Guid reagentLotId,
                                                                           CancellationToken ct = default) =>
        db.ReagentLots.FirstOrDefaultAsync(l => l.Id == reagentLotId, ct);

    // ---- Internal quality control ---------------------------------------------------------------

    public async Task<IReadOnlyList<QcEntryRow>> GetQcEntrySheetAsync(
        Guid siteId, Guid? instrumentId, Guid? departmentId, DateOnly today,
        CancellationToken ct = default)
    {
        var query = db.QcTargets.AsNoTracking().Where(t => t.IsActive);

        if (instrumentId is { } instrument)
            query = query.Where(t => t.InstrumentId == instrument);

        var rows = await query
            .Join(db.Instruments.AsNoTracking().Where(i => i.SiteId == siteId),
                  t => t.InstrumentId, i => i.Id, (t, i) => new { Target = t, Instrument = i })
            .Join(db.TestParameters.AsNoTracking(), x => x.Target.TestParameterId, p => p.Id,
                  (x, p) => new { x.Target, x.Instrument, Parameter = p })
            .Join(db.QcMaterials.AsNoTracking(), x => x.Target.QcMaterialId, m => m.Id,
                  (x, m) => new { x.Target, x.Instrument, x.Parameter, Material = m })
            .Join(db.AnalysisTests.AsNoTracking(), x => x.Parameter.AnalysisTestId, a => a.Id,
                  (x, a) => new
                  {
                      x.Target,
                      x.Instrument,
                      x.Parameter,
                      x.Material,
                      a.DepartmentId,
                      UnitSymbol = x.Parameter.UnitId == null
                          ? null
                          : db.Units.Where(u => u.Id == x.Parameter.UnitId)
                                    .Select(u => u.DisplaySymbol).FirstOrDefault()
                  })
            .Where(x => departmentId == null || x.DepartmentId == departmentId)
            .ToListAsync(ct);

        var targetIds = rows.Select(r => r.Target.Id).ToList();

        // The previous point per target, so the technician sees where the series stood. The routine
        // series only: a new lot's acceptance point says nothing about the lot in use.
        var lastPoints = await db.QcResults.AsNoTracking()
            .Where(r => targetIds.Contains(r.QcTargetId) && !r.IsLotEvaluation)
            .GroupBy(r => r.QcTargetId)
            .Select(g => g.OrderByDescending(r => r.MeasuredAt)
                          .Select(r => new { r.QcTargetId, r.Value, r.MeasuredAt, r.Outcome })
                          .First())
            .ToListAsync(ct);

        var lastLookup = lastPoints.ToDictionary(p => p.QcTargetId);

        return
        [
            .. rows.Select(r =>
            {
                var established =
                    r.Target.LaboratoryPointCount >= QcTarget.MinimumPointsForLaboratoryStatistics;

                var last = lastLookup.GetValueOrDefault(r.Target.Id);

                return new QcEntryRow
                {
                    QcTargetId = r.Target.Id,
                    TestParameterId = r.Parameter.Id,
                    QcMaterialId = r.Material.Id,
                    InstrumentId = r.Instrument.Id,
                    ParameterCode = r.Parameter.Code,
                    ParameterName = r.Parameter.Name,
                    UnitSymbol = r.UnitSymbol,
                    Decimals = r.Parameter.Decimals,
                    MaterialLotNumber = r.Material.LotNumber,
                    Level = r.Material.Level,
                    MaterialName = r.Material.Name,
                    MaterialExpiry = r.Material.ExpiryDate,
                    MaterialUsable = r.Material.IsUsable(today),
                    InstrumentCode = r.Instrument.Code,
                    TargetMean = r.Target.EffectiveMean,
                    TargetSd = r.Target.EffectiveSd,
                    CvPercent = r.Target.CvPercent,
                    MaxAcceptableCvPercent = r.Target.MaxAcceptableCvPercent,
                    PointCount = r.Target.LaboratoryPointCount,
                    UsingAssignedStatistics = !established,
                    ActiveRules = r.Target.ActiveRules,
                    LastValue = last?.Value,
                    LastMeasuredAt = last?.MeasuredAt,
                    LastOutcome = last?.Outcome
                };
            })
            .OrderBy(r => r.InstrumentCode)
            .ThenBy(r => r.ParameterCode)
            .ThenBy(r => r.Level)
        ];
    }

    /// <summary>
    /// The trailing Z-score history, oldest first.
    /// <para>
    /// Only points that carry a Z-score are returned: one measured before a target existed cannot
    /// contribute to a rule, and including it as a zero would read as perfectly in control.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<QcPoint>> GetZScoreHistoryAsync(
        Guid qcTargetId, int count, Guid? evaluationLotId = null, CancellationToken ct = default)
    {
        var level = await db.QcTargets.AsNoTracking()
            .Where(t => t.Id == qcTargetId)
            .Join(db.QcMaterials.AsNoTracking(), t => t.QcMaterialId, m => m.Id, (t, m) => m.Level)
            .FirstOrDefaultAsync(ct);

        var rows = await db.QcResults.AsNoTracking()
            .Where(r => r.QcTargetId == qcTargetId && r.ZScore != null)
            // A new lot's acceptance points are a series of their own: they say nothing about
            // the lot in use, and the routine rules must not run across the two.
            .Where(r => evaluationLotId == null
                ? !r.IsLotEvaluation
                : r.IsLotEvaluation && r.ReagentLotId == evaluationLotId)
            .OrderByDescending(r => r.MeasuredAt)
            .Take(Math.Clamp(count, 1, 100))
            .Select(r => new { r.ZScore, r.MeasuredAt, r.QcRunId })
            .ToListAsync(ct);

        rows.Reverse();

        return [.. rows.Select(r => new QcPoint(r.ZScore!.Value, r.MeasuredAt, r.QcRunId, level))];
    }

    public async Task<LeveyJenningsSeries?> GetLeveyJenningsAsync(
        Guid qcTargetId, DateTimeOffset from, CancellationToken ct = default)
    {
        var header = await db.QcTargets.AsNoTracking()
            .Where(t => t.Id == qcTargetId)
            .Join(db.TestParameters.AsNoTracking(), t => t.TestParameterId, p => p.Id,
                  (t, p) => new { Target = t, Parameter = p })
            .Join(db.QcMaterials.AsNoTracking(), x => x.Target.QcMaterialId, m => m.Id,
                  (x, m) => new { x.Target, x.Parameter, Material = m })
            .Join(db.Instruments.AsNoTracking(), x => x.Target.InstrumentId, i => i.Id,
                  (x, i) => new
                  {
                      x.Target,
                      x.Parameter,
                      x.Material,
                      InstrumentCode = i.Code,
                      UnitSymbol = x.Parameter.UnitId == null
                          ? null
                          : db.Units.Where(u => u.Id == x.Parameter.UnitId)
                                    .Select(u => u.DisplaySymbol).FirstOrDefault()
                  })
            .FirstOrDefaultAsync(ct);

        if (header is null) return null;

        // The routine chart: a new lot's acceptance points are judged on their own series, and
        // drawn among the points of the lot in use they read as its own excursions.
        var points = await db.QcResults.AsNoTracking()
            .Where(r => r.QcTargetId == qcTargetId && r.MeasuredAt >= from && !r.IsLotEvaluation)
            .OrderBy(r => r.MeasuredAt)
            .Select(r => new LeveyJenningsPoint(
                r.Id, r.MeasuredAt, r.Value, r.ZScore, r.Outcome, r.ViolatedRules,
                r.WasOverridden, r.ReviewedAt != null, r.Comment))
            .ToListAsync(ct);

        var established =
            header.Target.LaboratoryPointCount >= QcTarget.MinimumPointsForLaboratoryStatistics;

        return new LeveyJenningsSeries(
            header.Target.Id,
            header.Parameter.Code,
            header.Parameter.Name,
            header.UnitSymbol,
            header.Parameter.Decimals,
            header.Material.LotNumber,
            header.Material.Level,
            header.InstrumentCode,
            header.Target.EffectiveMean,
            header.Target.EffectiveSd,
            header.Target.CvPercent,
            !established,
            points);
    }

    public async Task<IReadOnlyList<QcReviewRow>> GetPendingQcReviewAsync(
        Guid siteId, CancellationToken ct = default) =>
        await db.QcResults.AsNoTracking()
            .Where(r => r.SiteId == siteId
                        && r.Outcome == QcOutcome.Rejected
                        && r.ReviewedAt == null)
            .Join(db.TestParameters.AsNoTracking(), r => r.TestParameterId, p => p.Id,
                  (r, p) => new { Result = r, Parameter = p })
            .Join(db.Instruments.AsNoTracking(), x => x.Result.InstrumentId, i => i.Id,
                  (x, i) => new { x.Result, x.Parameter, Instrument = i })
            .Join(db.QcTargets.AsNoTracking(), x => x.Result.QcTargetId, t => t.Id,
                  (x, t) => new { x.Result, x.Parameter, x.Instrument, Target = t })
            .Join(db.QcMaterials.AsNoTracking(), x => x.Target.QcMaterialId, m => m.Id,
                  (x, m) => new { x.Result, x.Parameter, x.Instrument, x.Target, Material = m })
            // Ordered and capped before the projection: EF cannot sort on a member of a row built
            // through a constructor.
            .OrderBy(x => x.Result.MeasuredAt)
            .Take(300)
            .Select(x => new QcReviewRow(
                      x.Result.Id,
                      x.Target.Id,
                      x.Parameter.Code,
                      x.Parameter.Name,
                      x.Instrument.Code,
                      x.Material.LotNumber,
                      x.Material.Level,
                      x.Result.Value,
                      x.Result.ZScore,
                      x.Result.MeasuredAt,
                      x.Result.ViolatedRules,
                      x.Result.WasOverridden,
                      // How many patient results this rejection has already flagged — the figure
                      // that tells the reviewer how urgent the file is.
                      db.TestResults.Count(tr => tr.CoveringQcRunId == x.Result.QcRunId),
                      db.TestResults.Count(tr => tr.CoveringQcRunId == x.Result.QcRunId && tr.ValidatedAt != null)))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<decimal>> GetAcceptedValuesAsync(
        Guid qcTargetId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue),
                                        TimeZoneInfo.Local.GetUtcOffset(
                                            from.ToDateTime(TimeOnly.MinValue)));

        var end = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue),
                                      TimeZoneInfo.Local.GetUtcOffset(
                                          to.ToDateTime(TimeOnly.MaxValue)));

        // Warnings count towards the statistics; rejections do not. A 1-2s warning is an expected
        // feature of an in-control series, whereas a rejected point is the error itself and would
        // widen the limits to accommodate it.
        return await db.QcResults.AsNoTracking()
            .Where(r => r.QcTargetId == qcTargetId && !r.IsLotEvaluation
                        && r.MeasuredAt >= start && r.MeasuredAt <= end
                        && (r.Outcome == QcOutcome.Accepted || r.Outcome == QcOutcome.Warning))
            .OrderBy(r => r.MeasuredAt)
            .Select(r => r.Value)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Flags the patient results that a rejected control run covers.
    /// <para>
    /// "Covers" means: the same parameter, the same instrument, produced since the last accepted
    /// control for that pair. Those are the results whose validity the rejected control calls into
    /// question, and flagging them is what makes
    /// <see cref="TestResult.BiologicallyValidate"/> refuse them without a justified override.
    /// </para>
    /// <para>
    /// Results already biologically validated or reported are flagged too, and stay as they are:
    /// they are not withdrawn — a released figure is corrected through the amended report — but
    /// they are counted apart on the review (<see cref="QcReviewRow.AffectedReleasedCount"/>), and
    /// their review opens a non-conformity for the look-back. Leaving them out made a rejection
    /// after a morning's validations count nothing, and the review asked an optional question.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> FlagResultsCoveredByRejectedQcAsync(
        IReadOnlyList<Guid> testParameterIds, Guid instrumentId, Guid qcRunId,
        CancellationToken ct = default)
    {
        if (testParameterIds.Count == 0) return [];

        var flagged = new List<TestResult>();

        // One window per parameter. The latest accepted control across all the rejected parameters
        // bounded them all: with glucose last accepted yesterday and creatinine this morning, both
        // rejected now, the glucose results of the night escaped the flag.
        foreach (var parameterId in testParameterIds.Distinct())
        {
            // The last accepted control for this parameter on this instrument bounds the affected
            // window. With none, the window is open-ended and every unreleased result for the
            // parameter is in question. A new lot's acceptance run does not bound it: the
            // patient results were produced with the lot in use, whatever the new lot did.
            var lastAccepted = await db.QcResults.AsNoTracking()
                .Where(r => r.InstrumentId == instrumentId
                            && r.TestParameterId == parameterId
                            && !r.IsLotEvaluation
                            && (r.Outcome == QcOutcome.Accepted || r.Outcome == QcOutcome.Warning))
                .OrderByDescending(r => r.MeasuredAt)
                .Select(r => (DateTimeOffset?)r.MeasuredAt)
                .FirstOrDefaultAsync(ct);

            var affected = await db.TestResults
                .Where(r => r.InstrumentId == instrumentId
                            && r.TestParameterId == parameterId
                            && (lastAccepted == null || r.MeasuredAt >= lastAccepted))
                .ToListAsync(ct);

            foreach (var result in affected)
            {
                result.FlagQcFailure();
                result.CoveringQcRunId = qcRunId;
            }

            flagged.AddRange(affected);
        }

        return flagged;
    }

    public Task<int> CountReleasedResultsCoveredAsync(Guid qcRunId, CancellationToken ct = default) =>
        db.TestResults.AsNoTracking().CountAsync(r => r.CoveringQcRunId == qcRunId && r.ValidatedAt != null, ct);

    public Task<string?> GetParameterCodeAsync(Guid testParameterId, CancellationToken ct = default) =>
        db.TestParameters.AsNoTracking()
            .Where(p => p.Id == testParameterId)
            .Select(p => p.Code)
            .FirstOrDefaultAsync(ct);

    public async Task<QcLevel> GetMaterialLevelAsync(Guid qcMaterialId,
                                                      CancellationToken ct = default) =>
        await db.QcMaterials.AsNoTracking()
            .Where(m => m.Id == qcMaterialId)
            .Select(m => m.Level)
            .FirstOrDefaultAsync(ct);

    // ---- Non-conformities -------------------------------------------------------------------------

    public async Task<IReadOnlyList<NonConformityRow>> GetNonConformitiesAsync(
        Guid siteId, bool onlyOpen, NonConformityCategory? category, DateOnly today,
        CancellationToken ct = default)
    {
        var query = db.NonConformities.AsNoTracking().Where(n => n.SiteId == siteId);

        if (onlyOpen)
            query = query.Where(n => n.Status == NonConformityStatus.Open
                                     || n.Status == NonConformityStatus.InProgress
                                     || n.Status == NonConformityStatus.AwaitingVerification);

        if (category is { } wanted) query = query.Where(n => n.Category == wanted);

        var rows = await query
            .OrderByDescending(n => n.Severity)
            .ThenBy(n => n.DueDate)
            .ThenByDescending(n => n.DetectedAt)
            .Take(500)
            .Select(n => new
            {
                n.Id,
                n.Reference,
                n.Category,
                n.Severity,
                n.Status,
                n.Description,
                n.DetectedAt,
                n.DetectedByUserId,
                n.DetectionSource,
                n.AffectedReleasedResults,
                n.AffectedPatientCount,
                n.AssignedToUserId,
                n.DueDate,
                n.ClosedAt,
                n.IsRecurrence,
                SampleBarcode = n.SampleId == null
                    ? null
                    : db.Samples.IgnoreQueryFilters().Where(s => s.Id == n.SampleId).Select(s => s.Barcode).FirstOrDefault(),
                AccessionNumber = n.LabOrderId == null
                    ? null
                    : db.LabOrders.IgnoreQueryFilters().Where(o => o.Id == n.LabOrderId).Select(o => o.AccessionNumber).FirstOrDefault(),
                ActionCount = n.Actions.Count,
                OpenActionCount = n.Actions.Count(a => a.Status == CorrectiveActionStatus.Planned
                                                       || a.Status == CorrectiveActionStatus.InProgress),
                DetectedByName = db.Users.Where(u => u.Id == n.DetectedByUserId)
                                         .Select(u => u.FullName).FirstOrDefault(),
                AssignedToName = n.AssignedToUserId == null
                    ? null
                    : db.Users.Where(u => u.Id == n.AssignedToUserId)
                              .Select(u => u.FullName).FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r =>
            {
                var isOpen = r.Status is NonConformityStatus.Open
                                      or NonConformityStatus.InProgress
                                      or NonConformityStatus.AwaitingVerification;

                var daysOpen = (r.ClosedAt is { } closed
                    ? DateOnly.FromDateTime(closed.Date).DayNumber
                    : today.DayNumber) - DateOnly.FromDateTime(r.DetectedAt.Date).DayNumber;

                return new NonConformityRow(
                    r.Id, r.Reference, r.Category, r.Severity, r.Status, r.Description,
                    r.DetectedAt, r.DetectedByName ?? string.Empty, r.DetectionSource,
                    r.AffectedReleasedResults, r.AffectedPatientCount, r.AssignedToName,
                    r.DueDate,
                    isOpen && r.DueDate is { } due && due < today,
                    daysOpen,
                    r.ActionCount, r.OpenActionCount, r.IsRecurrence, r.SampleBarcode, r.AccessionNumber);
            })
        ];
    }

    public async Task<IReadOnlyList<CorrectiveActionRow>> GetActionsAsync(
        Guid nonConformityId, DateOnly today, CancellationToken ct = default) =>
        await db.CorrectiveActions.AsNoTracking()
            .Where(a => a.NonConformityId == nonConformityId)
            .OrderBy(a => a.Kind).ThenBy(a => a.TargetDate)
            .Select(a => new CorrectiveActionRow(
                a.Id,
                a.Kind,
                a.Description,
                db.Users.Where(u => u.Id == a.ResponsibleUserId)
                        .Select(u => u.FullName).FirstOrDefault() ?? string.Empty,
                a.TargetDate,
                a.Status,
                a.CompletedOn,
                (a.Status == CorrectiveActionStatus.Planned
                 || a.Status == CorrectiveActionStatus.InProgress)
                && a.TargetDate < today))
            .ToListAsync(ct);

    public async Task<Guid?> FindPreviousOccurrenceAsync(
        Guid siteId, NonConformityCategory category, Guid? departmentId, DateOnly since,
        CancellationToken ct = default)
    {
        var start = new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue),
                                        TimeZoneInfo.Local.GetUtcOffset(
                                            since.ToDateTime(TimeOnly.MinValue)));

        return await db.NonConformities.AsNoTracking()
            .Where(n => n.SiteId == siteId
                        && n.Category == category
                        && n.DetectedAt >= start
                        && (departmentId == null || n.DepartmentId == departmentId))
            .OrderByDescending(n => n.DetectedAt)
            .Select(n => (Guid?)n.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A tube by its barcode, else the tubes of a file by its number, on the site: what a
    /// non-conformity can be linked to. The barcode first, as a tube is labelled with it.
    /// </summary>
    public async Task<IReadOnlyList<NonConformitySampleChoice>> FindSamplesForNonConformityAsync(
        Guid siteId, string term, CancellationToken ct = default)
    {
        var tubes = db.Samples.AsNoTracking().Where(s => s.SiteId == siteId);

        var orderIds = await tubes.Where(s => s.Barcode == term).Select(s => s.LabOrderId).ToListAsync(ct);
        var byBarcode = orderIds.Count > 0;
        if (!byBarcode)
            orderIds = await db.LabOrders.AsNoTracking()
                .Where(o => o.SiteId == siteId && o.AccessionNumber == term)
                .Select(o => o.Id)
                .ToListAsync(ct);
        if (orderIds.Count == 0) return [];

        var rows = await tubes
            .Where(s => byBarcode ? s.Barcode == term : orderIds.Contains(s.LabOrderId))
            .Join(db.LabOrders.AsNoTracking(), s => s.LabOrderId, o => o.Id, (s, o) => new { Sample = s, o.AccessionNumber })
            .Join(db.SpecimenTypes.AsNoTracking().IgnoreQueryFilters(), x => x.Sample.SpecimenTypeId, t => t.Id,
                  (x, t) => new { x.Sample.Id, x.Sample.LabOrderId, x.Sample.Barcode, x.AccessionNumber, t.Name, x.Sample.Status,
                                  x.Sample.SequenceInOrder })
            .OrderBy(x => x.AccessionNumber).ThenBy(x => x.SequenceInOrder).ThenBy(x => x.Barcode)
            .Take(50)
            .ToListAsync(ct);

        return [.. rows.Select(r => new NonConformitySampleChoice(r.Id, r.LabOrderId, r.Barcode, r.AccessionNumber, r.Name, r.Status))];
    }

    public async Task<bool> AreLinksOnSiteAsync(Guid siteId, Guid? sampleId, Guid? labOrderId, CancellationToken ct = default)
    {
        if (sampleId is { } tubeId)
        {
            var tube = await db.Samples.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.Id == tubeId && s.SiteId == siteId)
                .Select(s => new { s.LabOrderId })
                .FirstOrDefaultAsync(ct);
            if (tube is null || (labOrderId is { } fileOfTube && tube.LabOrderId != fileOfTube)) return false;
        }

        return labOrderId is not { } fileId
               || await db.LabOrders.AsNoTracking().IgnoreQueryFilters().AnyAsync(o => o.Id == fileId && o.SiteId == siteId, ct);
    }
}
