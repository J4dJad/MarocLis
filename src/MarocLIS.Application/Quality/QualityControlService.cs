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

        // The reagent lot of each point: the one named for the run (a new lot being evaluated) or
        // the one in use. Without it the QC that gates a lot's release could never be found.
        var resolvedTargets = new Dictionary<Guid, QcTarget?>();
        foreach (var entry in entries)
            resolvedTargets[entry.QcTargetId] = await targets.GetByIdAsync(entry.QcTargetId, ct);

        // A mistyped value (a key held down) cannot be a measurement: beyond the columns' range it
        // failed the whole save with « Erreur inattendue », and below it a point millions of SD out
        // would still be charted. Refused before anything is stored, like an unreadable value.
        foreach (var entry in entries)
        {
            if (QcRunEvaluator.IsImplausible(resolvedTargets[entry.QcTargetId], entry.Value))
                return Result.Failure<QcSubmissionOutcome>(Error.Validation("Result_TechnicallyImpossible", entry.Value));
        }

        // A lot named for the run applies to the entries that name it, which must be one analyser's:
        // the screen's single lot applied to every analyser's rows turned another analyser's
        // routine QC into an acceptance run that flagged no patient result when rejected.
        var named = entries.Where(e => e.ReagentLotId is not null).ToList();
        var namedLot = named.Select(e => e.ReagentLotId).FirstOrDefault();

        if (named.Select(e => e.ReagentLotId).Distinct().Count() > 1
            || named.Select(e => resolvedTargets[e.QcTargetId]?.InstrumentId).OfType<Guid>().Distinct().Count() > 1)
            return Result.Failure<QcSubmissionOutcome>(Error.Validation("Qc_NamedLotOneAnalyser"));

        IReadOnlyCollection<Guid> ParametersOf(IEnumerable<QcEntry> of) =>
            [.. of.Select(e => resolvedTargets[e.QcTargetId]).OfType<QcTarget>()
                  .Select(t => t.TestParameterId).Distinct()];

        var namedParameters = ParametersOf(named);
        var routineParameters = ParametersOf(entries.Where(e => e.ReagentLotId is null));

        var lotsForNamed = namedParameters.Count == 0
            ? null
            : await queries.GetReagentLotsForQcAsync(namedParameters, namedLot, currentUser.SiteId, today, ct);
        var lotsInUse = routineParameters.Count == 0
            ? null
            : await queries.GetReagentLotsForQcAsync(routineParameters, null, currentUser.SiteId, today, ct);

        // A named lot still awaiting release makes its points an acceptance run for that lot,
        // kept apart from the routine QC of the lot the patient results are produced with.
        var evaluatedLot = namedLot is { } namedId ? await queries.GetReagentLotAsync(namedId, ct) : null;
        if (evaluatedLot is { IsAwaitingRelease: false }) evaluatedLot = null;

        // --- 1. Store the raw points -------------------------------------------------------------

        foreach (var entry in entries)
        {
            var target = resolvedTargets[entry.QcTargetId];

            if (target is null)
            {
                problems.Add(Error.NotFound("Qc_TargetNotFound", entry.QcTargetId));
                continue;
            }

            var material = await materials.GetByIdAsync(target.QcMaterialId, ct);

            if (material is not null && !material.IsUsable(today))
                problems.Add(Error.Validation("Qc_MaterialExpired", material.LotNumber));

            var lotsByParameter = entry.ReagentLotId is null ? lotsInUse : lotsForNamed;

            var result = new QcResult(target.Id, target.TestParameterId, target.InstrumentId,
                                       currentUser.SiteId, entry.Value, now, currentUser.UserId)
            {
                ReagentLotId = lotsByParameter is null
                    ? entry.ReagentLotId
                    : lotsByParameter.TryGetValue(target.TestParameterId, out var lotId) ? lotId : null,
                Comment = entry.Comment
            };

            // Frozen so the chart is reproducible even after the statistics are recomputed.
            QcRunEvaluator.Anchor(result, target, runId);

            if (evaluatedLot is not null && result.ReagentLotId == evaluatedLot.Id)
            {
                result.IsLotEvaluation = true;

                // Records where the lot is loaded. Its transmissions are credited to it only from an
                // explicit start (StartLotEvaluationAsync), not from what was typed here.
                evaluatedLot.StartEvaluationOn(result.InstrumentId);
            }

            await results.AddAsync(result, ct);
            created.Add((result, target, entry));
        }

        // --- 2. Evaluate, accumulate, and flag the patient results the rejected runs cover -------

        // The same judgement as the analyser's controls get (QcRunEvaluator): the run's other
        // levels of the same analyte as peers, each rejected control speaking for its own analyser.
        var evaluated = await QcRunEvaluator.EvaluateAsync(
            queries, [.. created.Select(x => (x.Result, x.Target))], runId, ct);

        var outcomes = new List<QcEntryResult>(created.Count);

        foreach (var (result, target, _) in created)
        {
            var code = await queries.GetParameterCodeAsync(target.TestParameterId, ct) ?? "-";

            outcomes.Add(result.ZScore is null
                ? new QcEntryResult(result.Id, target.Id, code, result.Value, null, QcOutcome.Pending,
                                    WestgardRule.None, ["Qc_NoTargetEstablished"])
                : new QcEntryResult(result.Id, target.Id, code, result.Value, result.ZScore,
                                    evaluated.Evaluations[result.Id].Outcome,
                                    evaluated.Evaluations[result.Id].ViolatedRules,
                                    evaluated.Evaluations[result.Id].ViolationKeys));
        }

        var flagged = evaluated.FlaggedResults.Count;

        if (flagged > 0)
            problems.Add(Error.Regulatory("Qc_PatientResultsFlagged", flagged));

        unitOfWork.AuditReason = $"Saisie du contrôle interne — série {runId:N}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new QcSubmissionOutcome(outcomes, problems));
    }

    /// <summary>
    /// Starts an evaluation run of a lot awaiting release on an analyser: the next control of each
    /// level the analyser transmits today for the lot's analyses is credited to the lot and evaluated
    /// on its own series. Started again for each run (the next day of a multi-day evaluation, a
    /// rerun); routine QC transmitted afterwards stays with the lot in use.
    /// </summary>
    public async Task<Result> StartLotEvaluationAsync(Guid reagentLotId, Guid instrumentId,
                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.EnterQc);
        if (permitted.IsFailure) return permitted;

        var lot = await queries.GetReagentLotAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure(Error.NotFound("Lot_NotFound", reagentLotId));

        if (!lot.StartEvaluationOn(instrumentId, clock.Now))
            return Result.Failure(Error.Conflict("Lot_NotAwaitingRelease", lot.LotNumber));

        unitOfWork.AuditReason = $"Évaluation du lot {lot.LotNumber} démarrée sur l'automate";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Ends a lot's evaluation run: the analyser's transmissions are routine QC again.</summary>
    public async Task<Result> StopLotEvaluationAsync(Guid reagentLotId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.EnterQc);
        if (permitted.IsFailure) return permitted;

        var lot = await queries.GetReagentLotAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure(Error.NotFound("Lot_NotFound", reagentLotId));

        lot.StopEvaluation();

        unitOfWork.AuditReason = $"Évaluation du lot {lot.LotNumber} arrêtée";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
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
                    $"Contrôle interne rejeté — {parameterCode} : {WestgardRuleNames.Describe(result.ViolatedRules)}",
                    $"مراقبة داخلية مرفوضة — {parameterCode}",
                    $"Internal QC rejected — {parameterCode}: {WestgardRuleNames.Describe(result.ViolatedRules)}"),
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

        // Identical values give an SD of zero: every later point off the mean by the smallest
        // amount would be infinitely many SDs away and reject the run. Refused, not saved.
        if (Math.Round(sd, 6) == 0m)
            return Result.Failure<(decimal, decimal, int)>(Error.Validation("Qc_ZeroSdRefused", values.Count));

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

        // The catalogue refuses a control without a rule; changing the rules from the QC screen
        // left that door open, and such a control charts points without ever rejecting a run.
        if (rules == WestgardRule.None)
            return Result.Failure(Error.Regulatory("QcTarget_AtLeastOneRuleRequired"));

        var target = await targets.GetByIdAsync(qcTargetId, ct);
        if (target is null) return Result.Failure(Error.NotFound("Qc_TargetNotFound", qcTargetId));

        target.ActiveRules = rules;

        unitOfWork.AuditReason = $"Modification des règles de Westgard : {rules}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}
