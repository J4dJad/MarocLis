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
    bool IsRecurrence,
    string? SampleBarcode = null,
    string? AccessionNumber = null);

/// <summary>
/// A tube a non-conformity can be about, found by its barcode or its file's number: the tube, its
/// file and what it holds, and where it stands (a refused tube is the commonest case).
/// </summary>
public sealed record NonConformitySampleChoice(
    Guid SampleId,
    Guid LabOrderId,
    string Barcode,
    string AccessionNumber,
    MultilingualText Specimen,
    MarocLIS.Domain.Samples.SampleStatus Status);

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

/// <summary>What has been recorded of a file's analysis, for the dialog that completes it.</summary>
public sealed record NonConformityAnalysis(
    RootCauseCategory? RootCause,
    MultilingualText? RootCauseAnalysis,
    MultilingualText? ClinicalImpact,
    MultilingualText? EffectivenessVerification,
    DateOnly? EffectivenessVerifiedOn,
    bool AmendedReportsIssued);

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
    /// The analysis recorded so far. The dialog opens on it: opened blank, it showed nothing of
    /// the analysis already done and saved its first root-cause category over the one recorded.
    /// </summary>
    public async Task<Result<NonConformityAnalysis>> GetAnalysisAsync(Guid nonConformityId,
                                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure) return Result.Failure<NonConformityAnalysis>(permitted.FirstError);

        var nonConformity = await nonConformities.GetByIdAsync(nonConformityId, ct);
        if (nonConformity is null)
            return Result.Failure<NonConformityAnalysis>(Error.NotFound("Nc_NotFound", nonConformityId));

        return Result.Success(new NonConformityAnalysis(
            nonConformity.RootCause, nonConformity.RootCauseAnalysis, nonConformity.ClinicalImpactAssessment,
            nonConformity.EffectivenessVerification, nonConformity.EffectivenessVerifiedOn,
            nonConformity.AmendedReportsIssued));
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
    /// <summary>
    /// The tubes a file can be linked to: the tube with that barcode, or every tube of the file with
    /// that number, on the user's site. Open to whoever may declare, like the declaration itself.
    /// </summary>
    public async Task<Result<IReadOnlyList<NonConformitySampleChoice>>> FindSamplesAsync(string? term,
                                                                                         CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Quality.ReportNonConformity,
                                         Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<NonConformitySampleChoice>>(permitted.FirstError);

        var wanted = term?.Trim() ?? string.Empty;
        if (wanted.Length == 0) return Result.Success<IReadOnlyList<NonConformitySampleChoice>>([]);

        return Result.Success(await queries.FindSamplesForNonConformityAsync(currentUser.SiteId, wanted, ct));
    }

    public async Task<Result<string>> OpenAsync(CreateNonConformityRequest request,
                                                 CancellationToken ct = default)
    {
        // Declaring is open to whoever reports (a technician at the bench); everything after the
        // declaration keeps requiring the management right.
        var permitted = guard.RequireAny(Permissions.Quality.ReportNonConformity,
                                         Permissions.Quality.ManageNonConformity);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(request.Description.Fr))
            return Result.Failure<string>(Error.Validation("Nc_DescriptionRequired"));

        // The tube and the file come from the request: one of another site, unknown, or a tube of
        // another file was linked as sent, and the register then pointed at someone else's patient.
        if ((request.SampleId is not null || request.LabOrderId is not null)
            && !await queries.AreLinksOnSiteAsync(currentUser.SiteId, request.SampleId, request.LabOrderId, ct))
            return Result.Failure<string>(Error.Validation("Nc_LinkNotFound"));

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

        var open = EnsureOpen(nonConformity);
        if (open.IsFailure) return open;

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

        var open = EnsureOpen(nonConformity);
        if (open.IsFailure) return open;

        var action = nonConformity.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null) return Result.Failure(Error.NotFound("Nc_ActionNotFound", actionId));

        var completed = action.Complete(clock.Today, currentUser.UserId, evidence);
        if (completed.IsFailure) return completed;

        // Once every action is done the file moves to awaiting-verification rather than closing
        // itself: the effectiveness check is a separate, later judgement.
        if (nonConformity.AllActionsFinished)
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

        var open = EnsureOpen(nonConformity);
        if (open.IsFailure) return open;

        if (rootCause is { } cause) nonConformity.RootCause = cause;
        if (rootCauseAnalysis is not null) nonConformity.RootCauseAnalysis = rootCauseAnalysis;
        if (clinicalImpact is not null) nonConformity.ClinicalImpactAssessment = clinicalImpact;

        // The check is dated and signed when it is written or changed, not each time the rest of
        // the analysis is saved again with it.
        if (effectivenessVerification is not null
            && effectivenessVerification.Fr.Trim() != nonConformity.EffectivenessVerification?.Fr.Trim())
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

        var open = EnsureOpen(nonConformity);
        if (open.IsFailure) return open;

        var action = nonConformity.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null) return Result.Failure(Error.NotFound("Nc_ActionNotFound"));

        if (action.Status == CorrectiveActionStatus.Completed)
            return Result.Failure(Error.Conflict("Nc_CompletedActionCannotBeCancelled"));
        if (action.Status == CorrectiveActionStatus.Cancelled)
            return Result.Failure(Error.Conflict("Action_Cancelled"));

        action.Cancel(reason);

        // Cancelling the last pending action finishes the file's actions just as completing it
        // would, provided something was actually carried out.
        if (nonConformity.IsOpen && nonConformity.AllActionsFinished &&
            nonConformity.Actions.Any(a => a.Status == CorrectiveActionStatus.Completed))
            nonConformity.MarkAwaitingVerification();

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

        var open = EnsureOpen(nonConformity);
        if (open.IsFailure) return open;

        var action = nonConformity.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null) return Result.Failure(Error.NotFound("Nc_ActionNotFound"));

        if (action.Status != CorrectiveActionStatus.Planned)
            return Result.Failure(Error.Conflict("Nc_ActionNotPlanned"));

        action.Start();

        unitOfWork.AuditReason = $"Action démarrée sur {nonConformity.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// A closed or dismissed file is read-only until reopened. The screen greys these operations
    /// out, but a register loaded before someone else closed the file still offers them.
    /// </summary>
    private static Result EnsureOpen(NonConformity nonConformity) => nonConformity.Status switch
    {
        NonConformityStatus.Closed => Result.Failure(Error.Conflict("Nc_AlreadyClosed")),
        NonConformityStatus.Rejected => Result.Failure(Error.Conflict("Nc_AlreadyDismissed")),
        _ => Result.Success()
    };
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

    /// <summary>The tube with this barcode, or the tubes of the file with this number, on the site; none when neither.</summary>
    Task<IReadOnlyList<NonConformitySampleChoice>> FindSamplesForNonConformityAsync(Guid siteId, string term,
                                                                                   CancellationToken ct = default);

    /// <summary>
    /// Whether the tube and the file a non-conformity is linked to exist on the site, and the tube is
    /// of that file when both are given; true when neither is.
    /// </summary>
    Task<bool> AreLinksOnSiteAsync(Guid siteId, Guid? sampleId, Guid? labOrderId, CancellationToken ct = default);
}
