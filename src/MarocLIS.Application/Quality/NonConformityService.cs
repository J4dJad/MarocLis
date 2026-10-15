using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Quality;

/// <summary>One non-conformity in the register.</summary>
public sealed record NonConformityRow(
    Guid Id,
    string Reference,
    NonConformityCategory Category,
    NonConformitySeverity Severity,
    NonConformityStatus Status,
    MultilingualText Description,
    DateTimeOffset DetectedAt,
    string DetectedByName,
    DetectionSource DetectionSource,
    bool AffectedReleasedResults,
    int? AffectedPatientCount,
    string? AssignedToName,
    DateOnly? DueDate,
    bool IsOverdue,
    int? DaysOpen,
    int ActionCount,
    int OpenActionCount,
    bool IsRecurrence);

public sealed record CorrectiveActionRow(
    Guid Id,
    ActionKind Kind,
    MultilingualText Description,
    string ResponsibleName,
    DateOnly TargetDate,
    CorrectiveActionStatus Status,
    DateOnly? CompletedOn,
    bool IsOverdue);

public sealed record CreateNonConformityRequest
{
    public required NonConformityCategory Category { get; init; }
    public required NonConformitySeverity Severity { get; init; }
    public required MultilingualText Description { get; init; }
    public DetectionSource DetectionSource { get; init; } = DetectionSource.StaffObservation;

    public Guid? DepartmentId { get; init; }
    public Guid? LabOrderId { get; init; }
    public Guid? SampleId { get; init; }
    public Guid? InstrumentId { get; init; }
    public Guid? ReagentLotId { get; init; }

    public bool AffectedReleasedResults { get; init; }
    public int? AffectedPatientCount { get; init; }
    public MultilingualText? ImmediateAction { get; init; }

    public Guid? AssignedToUserId { get; init; }
    public DateOnly? DueDate { get; init; }
}

public sealed record AddActionRequest(
    Guid NonConformityId,
    ActionKind Kind,
    MultilingualText Description,
    Guid ResponsibleUserId,
    DateOnly TargetDate);

/// <summary>
/// The non-conformity register.
/// <para>
/// ISO 15189 §4.9 is not satisfied by a list of incidents: the laboratory has to assess the
/// clinical significance, act, and <em>verify that the action worked</em>. The domain enforces the
/// closure preconditions; this service supplies the sequencing and the two conveniences that make
/// the register useful rather than a chore — a gapless reference, and automatic detection of a
/// recurrence, which is what turns a list of incidents into an improvement process.
/// </para>
/// </summary>
public sealed class NonConformityService(
    IRepository<NonConformity> nonConformities,
    IQualityQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<NonConformityRow>>> GetRegisterAsync(
        bool onlyOpen = true, NonConformityCategory? category = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<NonConformityRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetNonConformitiesAsync(currentUser.SiteId, onlyOpen, category,
                                                   clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<CorrectiveActionRow>>> GetActionsAsync(
        Guid nonConformityId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<CorrectiveActionRow>>(permitted.FirstError);

        return Result.Success(await queries.GetActionsAsync(nonConformityId, clock.Today, ct));
    }

    /// <summary>
    /// Opens a non-conformity.
    /// <para>
    /// Recurrence is detected rather than declared: if the same category and severity have been
    /// recorded before in the same discipline, the new record is linked to the previous occurrence.
    /// A laboratory that cannot see it is repeating itself cannot improve, and asking the operator
    /// to remember is asking the wrong person.
    /// </para>
    /// </summary>
    public async Task<Result<string>> OpenAsync(CreateNonConformityRequest request,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(request.Description.Fr))
            return Result.Failure<string>(Error.Validation("Nc_DescriptionRequired"));

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var reference = await sequences.NextAsync(
            SequencePurpose.NonConformity, null, clock.Today.Year, ct);

        if (reference.IsFailure) return Result.Failure<string>(reference.FirstError);

        var nonConformity = new NonConformity(
            reference.Value, currentUser.SiteId, request.Category, request.Severity,
            request.Description, clock.Now, currentUser.UserId)
        {
            DetectionSource = request.DetectionSource,
            DepartmentId = request.DepartmentId,
            LabOrderId = request.LabOrderId,
            SampleId = request.SampleId,
            InstrumentId = request.InstrumentId,
            ReagentLotId = request.ReagentLotId,
            AffectedReleasedResults = request.AffectedReleasedResults,
            AffectedPatientCount = request.AffectedPatientCount,
            ImmediateAction = request.ImmediateAction,
            AssignedToUserId = request.AssignedToUserId ?? currentUser.UserId,
            // A major or critical item gets a shorter default: the severity should drive the pace.
            DueDate = request.DueDate ?? clock.Today.AddDays(
                request.Severity is NonConformitySeverity.Critical ? 7
                : request.Severity is NonConformitySeverity.Major ? 14
                : 30)
        };

        var previous = await queries.FindPreviousOccurrenceAsync(
            currentUser.SiteId, request.Category, request.DepartmentId, clock.Today.AddMonths(-12), ct);

        if (previous is { } earlier)
        {
            nonConformity.IsRecurrence = true;
            nonConformity.PreviousOccurrenceId = earlier;
        }

        await nonConformities.AddAsync(nonConformity, ct);

        unitOfWork.AuditReason = $"Ouverture de la non-conformité {reference.Value}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(reference.Value);
    }

    public async Task<Result> AddActionAsync(AddActionRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(request.NonConformityId, ct);

        if (nonConformity is null)
            return Result.Failure(Error.NotFound("Nc_NotFound", request.NonConformityId));

        if (string.IsNullOrWhiteSpace(request.Description.Fr))
            return Result.Failure(Error.Validation("Nc_ActionDescriptionRequired"));

        // Unspecified means the person recording it owns it. An action with no owner is an action
        // nobody does, and ISO 15189 expects a responsible person against each one.
        var responsible = request.ResponsibleUserId == Guid.Empty
            ? currentUser.UserId
            : request.ResponsibleUserId;

        nonConformity.AddAction(new CorrectiveAction(
            request.Kind, request.Description, responsible, request.TargetDate));

        unitOfWork.AuditReason = $"Action ajoutée à la NC {nonConformity.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> CompleteActionAsync(Guid nonConformityId, Guid actionId,
                                                   MultilingualText? evidence,
                                                   CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(nonConformityId, ct);
        if (nonConformity is null) return Result.Failure(Error.NotFound("Nc_NotFound", nonConformityId));

        var action = nonConformity.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null) return Result.Failure(Error.NotFound("Nc_ActionNotFound", actionId));

        var completed = action.Complete(clock.Today, currentUser.UserId, evidence);
        if (completed.IsFailure) return completed;

        // Once every action is done the file moves to awaiting-verification rather than closing
        // itself: the effectiveness check is a separate, later judgement.
        if (nonConformity.Actions.All(a => a.Status is CorrectiveActionStatus.Completed
                                                    or CorrectiveActionStatus.Cancelled))
            nonConformity.MarkAwaitingVerification();

        unitOfWork.AuditReason = $"Action soldée — NC {nonConformity.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Records the root cause analysis, the clinical impact assessment and the effectiveness
    /// verification. Kept as one method because the domain's closure check requires them together
    /// for a major or critical item, and filling them in over three screens invites a half-complete
    /// file that cannot close and nobody can see why.
    /// </summary>
    public async Task<Result> RecordAnalysisAsync(
        Guid nonConformityId,
        RootCauseCategory? rootCause = null,
        MultilingualText? rootCauseAnalysis = null,
        MultilingualText? clinicalImpact = null,
        MultilingualText? effectivenessVerification = null,
        bool amendedReportsIssued = false,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(nonConformityId, ct);
        if (nonConformity is null) return Result.Failure(Error.NotFound("Nc_NotFound", nonConformityId));

        if (rootCause is { } cause) nonConformity.RootCause = cause;
        if (rootCauseAnalysis is not null) nonConformity.RootCauseAnalysis = rootCauseAnalysis;
        if (clinicalImpact is not null) nonConformity.ClinicalImpactAssessment = clinicalImpact;

        if (effectivenessVerification is not null)
        {
            nonConformity.EffectivenessVerification = effectivenessVerification;
            nonConformity.EffectivenessVerifiedOn = clock.Today;
            nonConformity.EffectivenessVerifiedByUserId = currentUser.UserId;
        }

        if (amendedReportsIssued) nonConformity.AmendedReportsIssued = true;

        unitOfWork.AuditReason = $"Analyse de la NC {nonConformity.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Closes a non-conformity. Every precondition is the domain's, and a refusal names exactly
    /// what is missing — the root cause, the effectiveness check, an unfinished action — rather
    /// than reporting a generic failure.
    /// </summary>
    public async Task<Result> CloseAsync(Guid nonConformityId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(nonConformityId, ct);
        if (nonConformity is null) return Result.Failure(Error.NotFound("Nc_NotFound", nonConformityId));

        var closed = nonConformity.Close(clock.Now, currentUser.UserId);
        if (closed.IsFailure) return closed;

        unitOfWork.AuditReason = $"Clôture de la non-conformité {nonConformity.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Reopens a closed non-conformity.
    /// <para>
    /// Closure is not always final: an effectiveness check can fail after the fact, or the file can
    /// have been closed in error. Without this the only remedy was to open a second file, which
    /// splits one investigation across two references and breaks the recurrence link an assessor
    /// follows.
    /// </para>
    /// </summary>
    public async Task<Result> ReopenAsync(Guid nonConformityId, string reason,
                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(nonConformityId, ct);
        if (nonConformity is null) return Result.Failure(Error.NotFound("Nc_NotFound", nonConformityId));

        var reopened = nonConformity.Reopen(reason);
        if (reopened.IsFailure) return reopened;

        unitOfWork.AuditReason =
            $"Réouverture de la non-conformité {nonConformity.Reference} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Dismisses a report judged, after examination, not to be a non-conformity.
    /// <para>
    /// Distinct from closing, and the distinction is what keeps the quality indicators honest: a
    /// closed file counts as a non-conformity that happened, a dismissed one records that the
    /// report was unfounded. Without it, clearing a mistaken report meant inventing a corrective
    /// action for a problem that never existed.
    /// </para>
    /// </summary>
    public async Task<Result> DismissAsync(Guid nonConformityId, string justification,
                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(nonConformityId, ct);
        if (nonConformity is null) return Result.Failure(Error.NotFound("Nc_NotFound", nonConformityId));

        var dismissed = nonConformity.Dismiss(clock.Now, currentUser.UserId, justification);
        if (dismissed.IsFailure) return dismissed;

        unitOfWork.AuditReason =
            $"Non-conformité {nonConformity.Reference} écartée : {justification}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Withdraws a corrective action entered in error.
    /// <para>
    /// This was the sharpest of the gaps: <see cref="NonConformity.Close"/> requires <em>every</em>
    /// action to be completed, so a single mis-entered action permanently blocked the file from
    /// ever closing, with no way to remove it.
    /// </para>
    /// </summary>
    public async Task<Result> CancelActionAsync(Guid nonConformityId, Guid actionId, string reason,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Nc_ActionCancelReasonRequired"));

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(nonConformityId, ct);
        if (nonConformity is null) return Result.Failure(Error.NotFound("Nc_NotFound", nonConformityId));

        var action = nonConformity.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null) return Result.Failure(Error.NotFound("Nc_ActionNotFound"));

        if (action.Status == CorrectiveActionStatus.Completed)
            return Result.Failure(Error.Conflict("Nc_CompletedActionCannotBeCancelled"));

        action.Cancel(reason);

        unitOfWork.AuditReason =
            $"Action annulée sur {nonConformity.Reference} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Marks an action as started. Small, but it is what makes the register readable: without it
    /// every action sits at <c>Planned</c> until the moment it is completed, so nobody can tell
    /// which of an overdue file's actions are actually in hand.
    /// </summary>
    public async Task<Result> StartActionAsync(Guid nonConformityId, Guid actionId,
                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return permitted;

        var nonConformity = await nonConformities.GetByIdWithDetailsAsync(nonConformityId, ct);
        if (nonConformity is null) return Result.Failure(Error.NotFound("Nc_NotFound", nonConformityId));

        var action = nonConformity.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null) return Result.Failure(Error.NotFound("Nc_ActionNotFound"));

        if (action.Status != CorrectiveActionStatus.Planned)
            return Result.Failure(Error.Conflict("Nc_ActionNotPlanned"));

        action.Start();

        unitOfWork.AuditReason = $"Action démarrée sur {nonConformity.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}

/// <summary>Read-side queries for the quality screens.</summary>
public interface IQualityQueries
{
    // --- Internal quality control -------------------------------------------------------------

    Task<IReadOnlyList<QcEntryRow>> GetQcEntrySheetAsync(
        Guid siteId, Guid? instrumentId, Guid? departmentId, DateOnly today,
        CancellationToken ct = default);

    /// <summary>
    /// The trailing Z-score history for a target, oldest first — the shape
    /// <see cref="WestgardEvaluator"/> expects. Routine points only, or, with
    /// <paramref name="evaluationLotId"/>, only the acceptance points of that reagent lot.
    /// </summary>
    Task<IReadOnlyList<QcPoint>> GetZScoreHistoryAsync(Guid qcTargetId, int count,
                                                        Guid? evaluationLotId = null,
                                                        CancellationToken ct = default);

    Task<LeveyJenningsSeries?> GetLeveyJenningsAsync(Guid qcTargetId, DateTimeOffset from,
                                                      CancellationToken ct = default);

    Task<IReadOnlyList<QcReviewRow>> GetPendingQcReviewAsync(Guid siteId,
                                                              CancellationToken ct = default);

    /// <summary>Accepted values in a window, for establishing the laboratory's own limits.</summary>
    Task<IReadOnlyList<decimal>> GetAcceptedValuesAsync(Guid qcTargetId, DateOnly from, DateOnly to,
                                                         CancellationToken ct = default);

    /// <summary>
    /// Flags the unreleased patient results that a rejected run covers, and returns how many.
    /// Those results then cannot be validated without a justified override.
    /// </summary>
    Task<int> FlagResultsCoveredByRejectedQcAsync(
        IReadOnlyList<Guid> testParameterIds, Guid instrumentId, Guid qcRunId,
        CancellationToken ct = default);

    Task<string?> GetParameterCodeAsync(Guid testParameterId, CancellationToken ct = default);

    Task<QcLevel> GetMaterialLevelAsync(Guid qcMaterialId, CancellationToken ct = default);

    /// <summary>
    /// The reagent lot each control point was measured with: the lot named for the run when the
    /// parameter's analysis uses it (a new lot under evaluation), otherwise the lot in use on the
    /// site. Parameters without an unambiguous lot are absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetReagentLotsForQcAsync(
        IReadOnlyCollection<Guid> testParameterIds, Guid? namedLotId, Guid siteId, DateOnly today,
        CancellationToken ct = default);

    /// <summary>The reagent lot, tracked, so a QC run can record that it is under evaluation.</summary>
    Task<MarocLIS.Domain.Inventory.ReagentLot?> GetReagentLotAsync(Guid reagentLotId,
                                                                    CancellationToken ct = default);

    // --- Non-conformities ----------------------------------------------------------------------

    Task<IReadOnlyList<NonConformityRow>> GetNonConformitiesAsync(
        Guid siteId, bool onlyOpen, NonConformityCategory? category, DateOnly today,
        CancellationToken ct = default);

    Task<IReadOnlyList<CorrectiveActionRow>> GetActionsAsync(Guid nonConformityId, DateOnly today,
                                                              CancellationToken ct = default);

    /// <summary>
    /// The most recent closed non-conformity of the same category and discipline within a window,
    /// so a repeat can be linked to what came before it.
    /// </summary>
    Task<Guid?> FindPreviousOccurrenceAsync(Guid siteId, NonConformityCategory category,
                                             Guid? departmentId, DateOnly since,
                                             CancellationToken ct = default);
}
