using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Quality;

/// <summary>One control to be run, as the QC entry sheet presents it.</summary>
public sealed record QcEntryRow
{
    public required Guid QcTargetId { get; init; }
    public required Guid TestParameterId { get; init; }
    public required Guid QcMaterialId { get; init; }
    public required Guid InstrumentId { get; init; }

    public required string ParameterCode { get; init; }
    public required MultilingualText ParameterName { get; init; }
    public string? UnitSymbol { get; init; }
    public int Decimals { get; init; } = 2;

    public required string MaterialLotNumber { get; init; }
    public QcLevel Level { get; init; }
    public required MultilingualText MaterialName { get; init; }
    public DateOnly MaterialExpiry { get; init; }

    /// <summary>
    /// False when the lot is expired or past its on-board stability. Entry is still permitted —
    /// the laboratory may have a documented reason — but the reason will have to be recorded.
    /// </summary>
    public bool MaterialUsable { get; init; }

    public required string InstrumentCode { get; init; }

    public decimal? TargetMean { get; init; }
    public decimal? TargetSd { get; init; }
    public decimal? CvPercent { get; init; }
    public decimal? MaxAcceptableCvPercent { get; init; }
    public int PointCount { get; init; }

    /// <summary>
    /// True while the laboratory's own statistics are not yet established, so the screen can say
    /// the limits are the manufacturer's rather than its own. ISO 15189 §5.6 expects the laboratory
    /// to establish its own.
    /// </summary>
    public bool UsingAssignedStatistics { get; init; }

    public WestgardRule ActiveRules { get; init; }

    public decimal? LastValue { get; init; }
    public DateTimeOffset? LastMeasuredAt { get; init; }
    public QcOutcome? LastOutcome { get; init; }
}

/// <summary>A value the technician submits for one control.</summary>
public sealed record QcEntry(Guid QcTargetId, decimal Value, Guid? ReagentLotId = null,
                             string? Comment = null);

/// <summary>What happened to one submitted control point.</summary>
public sealed record QcEntryResult(
    Guid QcResultId,
    Guid QcTargetId,
    string ParameterCode,
    decimal Value,
    decimal? ZScore,
    QcOutcome Outcome,
    WestgardRule ViolatedRules,
    IReadOnlyList<string> ViolationKeys);

/// <summary>
/// The result of submitting a control run.
/// <para>
/// Carries warnings alongside the outcomes rather than failing: the points <em>were</em> stored,
/// and an expired control lot or a batch of newly flagged patient results are things the technician
/// must be told about, not reasons to discard the entry they just made.
/// </para>
/// </summary>
public sealed record QcSubmissionOutcome(
    IReadOnlyList<QcEntryResult> Results,
    IReadOnlyList<Error> Warnings)
{
    public bool AnyRejected => Results.Any(r => r.Outcome == QcOutcome.Rejected);
}

/// <summary>One point on a Levey-Jennings chart.</summary>
public sealed record LeveyJenningsPoint(
    Guid QcResultId,
    DateTimeOffset MeasuredAt,
    decimal Value,
    decimal? ZScore,
    QcOutcome Outcome,
    WestgardRule ViolatedRules,
    bool WasOverridden,
    bool Reviewed,
    string? Comment);

/// <summary>A Levey-Jennings chart: the control limits plus the series.</summary>
public sealed record LeveyJenningsSeries(
    Guid QcTargetId,
    string ParameterCode,
    MultilingualText ParameterName,
    string? UnitSymbol,
    int Decimals,
    string MaterialLotNumber,
    QcLevel Level,
    string InstrumentCode,
    decimal? Mean,
    decimal? Sd,
    decimal? CvPercent,
    bool UsingAssignedStatistics,
    IReadOnlyList<LeveyJenningsPoint> Points);

/// <summary>A rejected control awaiting review.</summary>
public sealed record QcReviewRow(
    Guid QcResultId,
    Guid QcTargetId,
    string ParameterCode,
    MultilingualText ParameterName,
    string InstrumentCode,
    string MaterialLotNumber,
    QcLevel Level,
    decimal Value,
    decimal? ZScore,
    DateTimeOffset MeasuredAt,
    WestgardRule ViolatedRules,
    bool WasOverridden,
    int AffectedResultCount);

/// <summary>
/// Internal quality control.
/// <para>
/// The evaluation itself is <see cref="WestgardEvaluator"/> — a pure function this service feeds
/// and records. What lives here is the sequencing that makes the verdict trustworthy:
/// </para>
/// <list type="bullet">
/// <item>Points are grouped into a <b>run</b>, so the multi-level rules (2₂ₛ within run, R₄ₛ) see
/// the other levels measured alongside. Evaluating each level in isolation would silently miss
/// the two rules most sensitive to a calibration shift.</item>
/// <item>The target's mean and SD are frozen onto each point, so the chart never shifts
/// retroactively when the statistics are recomputed.</item>
/// <item>An accepted point is folded into the laboratory's running statistics; a rejected one is
/// not, because a point the laboratory rejected is not evidence of its own performance.</item>
/// </list>
/// </summary>
public sealed class QualityControlService(
    IRepository<QcTarget> targets,
    IRepository<QcResult> results,
    IRepository<QcMaterial> materials,
    IRepository<NonConformity> nonConformities,
    IQualityQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<QcEntryRow>>> GetEntrySheetAsync(
        Guid? instrumentId = null, Guid? departmentId = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<QcEntryRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetQcEntrySheetAsync(currentUser.SiteId, instrumentId, departmentId,
                                                clock.Today, ct));
    }

    /// <summary>
    /// Records a batch of control values as one run and evaluates each against the active rules.
    /// <para>
    /// Submitted as a batch on purpose: the multi-level Westgard rules compare the levels measured
    /// together, so they are only correct if the run is known. A screen that saved one level at a
    /// time could not evaluate R₄ₛ at all.
    /// </para>
    /// </summary>
    public async Task<Result<QcSubmissionOutcome>> SubmitAsync(
        IReadOnlyList<QcEntry> entries, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.EnterQc);
        if (permitted.IsFailure)
            return Result.Failure<QcSubmissionOutcome>(permitted.FirstError);

        if (entries.Count == 0)
            return Result.Failure<QcSubmissionOutcome>(
                Error.Validation("Message.NothingSelected"));

        var now = clock.Now;
        var today = clock.Today;

        // One identifier for the whole run, which is what lets the multi-level rules find their peers.
        var runId = Guid.CreateVersion7();

        var created = new List<(QcResult Result, QcTarget Target, QcEntry Entry)>(entries.Count);
        var problems = new List<Error>();

        // --- 1. Store the raw points -------------------------------------------------------------

        foreach (var entry in entries)
        {
            var target = await targets.GetByIdAsync(entry.QcTargetId, ct);

            if (target is null)
            {
                problems.Add(Error.NotFound("Qc_TargetNotFound", entry.QcTargetId));
                continue;
            }

            var material = await materials.GetByIdAsync(target.QcMaterialId, ct);

            if (material is not null && !material.IsUsable(today))
                problems.Add(Error.Validation("Qc_MaterialExpired", material.LotNumber));

            var result = new QcResult(target.Id, target.TestParameterId, target.InstrumentId,
                                       currentUser.SiteId, entry.Value, now, currentUser.UserId)
            {
                QcRunId = runId,
                // Frozen so the chart is reproducible even after the statistics are recomputed.
                TargetMean = target.EffectiveMean,
                TargetSd = target.EffectiveSd,
                ZScore = target.ZScore(entry.Value),
                ReagentLotId = entry.ReagentLotId,
                Comment = entry.Comment
            };

            await results.AddAsync(result, ct);
            created.Add((result, target, entry));
        }

        // --- 2. Evaluate, with the run's other levels available as peers --------------------------

        var outcomes = new List<QcEntryResult>(created.Count);

        foreach (var (result, target, _) in created)
        {
            if (result.ZScore is null)
            {
                // No target established: the point is stored but cannot be interpreted. Silently
                // treating that as in control would be the worst of the options.
                result.ApplyEvaluation(WestgardEvaluation.NotEvaluable);

                outcomes.Add(new QcEntryResult(result.Id, target.Id,
                    await queries.GetParameterCodeAsync(target.TestParameterId, ct) ?? "-",
                    result.Value, null, QcOutcome.Pending, WestgardRule.None,
                    ["Qc_NoTargetEstablished"]));

                continue;
            }

            var history = await queries.GetZScoreHistoryAsync(target.Id, 12, ct);

            // The point just stored is the newest element of the history the rules evaluate.
            var series = new List<QcPoint>(history)
            {
                new(result.ZScore.Value, result.MeasuredAt, runId,
                    await queries.GetMaterialLevelAsync(target.QcMaterialId, ct))
            };

            var peers = created
                .Where(x => x.Result.Id != result.Id && x.Result.ZScore is not null)
                .Select(x => new QcPoint(x.Result.ZScore!.Value, x.Result.MeasuredAt, runId,
                                          QcLevel.Normal))
                .ToList();

            var evaluation = WestgardEvaluator.Evaluate(target.ActiveRules, series, peers);

            result.ApplyEvaluation(evaluation);

            // Only accepted points shape the laboratory's own mean and SD. Folding in a rejected
            // point would widen the limits to accommodate the very error that was detected.
            if (evaluation.Outcome is QcOutcome.Accepted or QcOutcome.Warning)
                target.AccumulatePoint(result.Value);

            outcomes.Add(new QcEntryResult(result.Id, target.Id,
                await queries.GetParameterCodeAsync(target.TestParameterId, ct) ?? "-",
                result.Value, result.ZScore, evaluation.Outcome,
                evaluation.ViolatedRules, evaluation.ViolationKeys));
        }

        // --- 3. Flag the patient results the rejected runs cover -----------------------------------

        var rejectedTargets = created
            .Where(x => x.Result.IsRejected)
            .Select(x => x.Target.TestParameterId)
            .Distinct()
            .ToList();

        if (rejectedTargets.Count > 0 && created.Count > 0)
        {
            // Patient results produced since the last accepted control for the same parameter and
            // instrument are flagged, so a biologist cannot release them without noticing.
            var flagged = await queries.FlagResultsCoveredByRejectedQcAsync(
                rejectedTargets, created[0].Result.InstrumentId, runId, ct);

            if (flagged > 0)
                problems.Add(Error.Regulatory("Qc_PatientResultsFlagged", flagged));
        }

        unitOfWork.AuditReason = $"Saisie du contrôle interne — série {runId:N}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new QcSubmissionOutcome(outcomes, problems));
    }

    public async Task<Result<LeveyJenningsSeries>> GetChartAsync(
        Guid qcTargetId, int days = 60, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure) return Result.Failure<LeveyJenningsSeries>(permitted.FirstError);

        var series = await queries.GetLeveyJenningsAsync(
            qcTargetId, clock.Now.AddDays(-days), ct);

        return series is null
            ? Result.Failure<LeveyJenningsSeries>(Error.NotFound("Qc_TargetNotFound", qcTargetId))
            : Result.Success(series);
    }

    public async Task<Result<IReadOnlyList<QcReviewRow>>> GetPendingReviewAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<QcReviewRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPendingQcReviewAsync(currentUser.SiteId, ct));
    }

    /// <summary>
    /// Records the review of a rejected control.
    /// <para>
    /// A corrective action is mandatory — the domain refuses without one. When the rejection
    /// affected results that had already been released, a non-conformity is opened automatically
    /// rather than left to the quality manager to remember: ISO 15189 §4.9 requires the departure
    /// to be recorded, and the laboratory has already established that one occurred.
    /// </para>
    /// </summary>
    public async Task<Result> ReviewAsync(Guid qcResultId, string correctiveAction,
                                           bool openNonConformity, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ReviewQc);
        if (permitted.IsFailure) return permitted;

        var result = await results.GetByIdAsync(qcResultId, ct);
        if (result is null) return Result.Failure(Error.NotFound("Qc_ResultNotFound", qcResultId));

        var reviewed = result.Review(clock.Now, currentUser.UserId, correctiveAction);
        if (reviewed.IsFailure) return reviewed;

        if (openNonConformity && result.NonConformityId is null)
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

            var reference = await sequences.NextAsync(
                SequencePurpose.NonConformity, null, clock.Today.Year, ct);

            if (reference.IsFailure) return Result.Failure(reference.FirstError);

            var parameterCode = await queries.GetParameterCodeAsync(result.TestParameterId, ct);

            var nonConformity = new NonConformity(
                reference.Value,
                currentUser.SiteId,
                NonConformityCategory.Analytical,
                NonConformitySeverity.Major,
                new MultilingualText(
                    $"Contrôle interne rejeté — {parameterCode} : {result.ViolatedRules}",
                    $"مراقبة داخلية مرفوضة — {parameterCode}",
                    $"Internal QC rejected — {parameterCode}: {result.ViolatedRules}"),
                clock.Now,
                currentUser.UserId)
            {
                DetectionSource = DetectionSource.InternalQualityControl,
                QcResultId = result.Id,
                InstrumentId = result.InstrumentId,
                ReagentLotId = result.ReagentLotId,
                ImmediateAction = new MultilingualText(correctiveAction),
                DueDate = clock.Today.AddDays(14)
            };

            await nonConformities.AddAsync(nonConformity, ct);
            result.NonConformityId = nonConformity.Id;

            unitOfWork.AuditReason =
                $"Revue du contrôle rejeté et ouverture de la NC {reference.Value}";

            await unitOfWork.SaveChangesAsync(ct);
            await unitOfWork.CommitAsync(ct);

            return Result.Success();
        }

        unitOfWork.AuditReason = "Revue du contrôle interne";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Establishes the laboratory's own mean and SD from an accepted period.
    /// <para>
    /// ISO 15189 §5.6 expects the laboratory to derive its limits from its own data rather than
    /// rely indefinitely on the manufacturer's insert. Recomputing from a chosen window, rather
    /// than from all history, is what lets a laboratory exclude a period it has already explained.
    /// </para>
    /// </summary>
    public async Task<Result<(decimal Mean, decimal Sd, int Points)>> RecomputeStatisticsAsync(
        Guid qcTargetId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageQcRules);
        if (permitted.IsFailure)
            return Result.Failure<(decimal, decimal, int)>(permitted.FirstError);

        if (to < from)
            return Result.Failure<(decimal, decimal, int)>(Error.Validation("Coverage_InvalidPeriod"));

        var target = await targets.GetByIdAsync(qcTargetId, ct);
        if (target is null)
            return Result.Failure<(decimal, decimal, int)>(
                Error.NotFound("Qc_TargetNotFound", qcTargetId));

        var values = await queries.GetAcceptedValuesAsync(qcTargetId, from, to, ct);

        if (values.Count < QcTarget.MinimumPointsForLaboratoryStatistics)
            return Result.Failure<(decimal, decimal, int)>(
                Error.Validation("Qc_NotEnoughPointsForStatistics",
                                  QcTarget.MinimumPointsForLaboratoryStatistics, values.Count));

        // Two-pass mean and sample SD over the chosen window. The running accumulation on the
        // entity is for day-to-day upkeep; establishing limits deliberately recomputes from
        // scratch so an excluded period really is excluded.
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        var sd = (decimal)Math.Sqrt((double)variance);

        target.LaboratoryMean = Math.Round(mean, 6);
        target.LaboratorySd = Math.Round(sd, 6);
        target.LaboratoryPointCount = values.Count;
        target.StatisticsFrom = from;
        target.StatisticsTo = to;

        unitOfWork.AuditReason =
            $"Établissement des limites du laboratoire sur {values.Count} points ({from:dd/MM/yyyy}–{to:dd/MM/yyyy})";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success((target.LaboratoryMean!.Value, target.LaboratorySd!.Value, values.Count));
    }

    /// <summary>Changes which Westgard rules apply to a target.</summary>
    public async Task<Result> SetActiveRulesAsync(Guid qcTargetId, WestgardRule rules,
                                                   CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageQcRules);
        if (permitted.IsFailure) return permitted;

        var target = await targets.GetByIdAsync(qcTargetId, ct);
        if (target is null) return Result.Failure(Error.NotFound("Qc_TargetNotFound", qcTargetId));

        target.ActiveRules = rules;

        unitOfWork.AuditReason = $"Modification des règles de Westgard : {rules}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}
