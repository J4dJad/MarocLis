using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Quality;

/// <summary>
/// A recorded non-conformity (fiche de non-conformité). ISO 15189 §4.9 requires the laboratory to
/// record every departure from its procedures, assess its clinical significance, act on it and
/// verify that the action worked. Modelled as a small workflow rather than a free-text log so the
/// quality manager can actually report on open items, recurrence and closure delay.
/// </summary>
public sealed class NonConformity : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<CorrectiveAction> _actions = [];

    private NonConformity() { }

    public NonConformity(string reference, Guid siteId, NonConformityCategory category,
                         NonConformitySeverity severity, MultilingualText description,
                         DateTimeOffset detectedAt, Guid detectedByUserId)
    {
        Reference = reference;
        SiteId = siteId;
        Category = category;
        Severity = severity;
        Description = description;
        DetectedAt = detectedAt;
        DetectedByUserId = detectedByUserId;
        // The person whose action raised the file pilots it until someone reassigns it: files
        // opened automatically (QC, EQA, temperature) otherwise had no pilot at all.
        AssignedToUserId = detectedByUserId;
        Status = NonConformityStatus.Open;
    }

    /// <summary>Sequential reference, e.g. "NC-2026-0042".</summary>
    public string Reference { get; private set; } = string.Empty;

    public Guid SiteId { get; set; }

    public NonConformityCategory Category { get; set; }
    public NonConformitySeverity Severity { get; set; }
    public NonConformityStatus Status { get; private set; }

    public MultilingualText Description { get; set; } = new();

    public DateTimeOffset DetectedAt { get; private set; }
    public Guid DetectedByUserId { get; private set; }

    /// <summary>How it came to light: internal audit, QC, a complaint, a staff observation.</summary>
    public DetectionSource DetectionSource { get; set; } = DetectionSource.StaffObservation;

    // ---- What it touched ----------------------------------------------------------------------

    public Guid? DepartmentId { get; set; }
    public Guid? LabOrderId { get; set; }
    public Guid? SampleId { get; set; }
    public Guid? TestResultId { get; set; }
    public Guid? InstrumentId { get; set; }
    public Guid? ReagentLotId { get; set; }
    public Guid? QcResultId { get; set; }

    /// <summary>The supplier at fault, so the file counts in that supplier's evaluation.</summary>
    public Guid? SupplierId { get; set; }

    /// <summary>
    /// Whether results that had already left the laboratory were affected. If so, the clinical
    /// impact must be assessed and recipients informed — this is the field an auditor looks for.
    /// </summary>
    public bool AffectedReleasedResults { get; set; }

    /// <summary>Number of patient files touched, for the impact assessment.</summary>
    public int? AffectedPatientCount { get; set; }

    /// <summary>The biologist's assessment of the clinical consequence.</summary>
    public MultilingualText? ClinicalImpactAssessment { get; set; }

    /// <summary>Set when amended reports had to be issued.</summary>
    public bool AmendedReportsIssued { get; set; }

    // ---- Analysis -----------------------------------------------------------------------------

    public MultilingualText? ImmediateAction { get; set; }

    /// <summary>Root cause. Required before the file can be closed for anything but a minor item.</summary>
    public MultilingualText? RootCauseAnalysis { get; set; }

    public RootCauseCategory? RootCause { get; set; }

    /// <summary>Set when the same cause has produced a non-conformity before — drives escalation.</summary>
    public bool IsRecurrence { get; set; }
    public Guid? PreviousOccurrenceId { get; set; }

    // ---- Ownership and closure ----------------------------------------------------------------

    public Guid? AssignedToUserId { get; set; }
    public DateOnly? DueDate { get; set; }

    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedByUserId { get; private set; }

    /// <summary>
    /// Verification that the corrective action actually worked, checked after the fact. Closing a
    /// non-conformity without it is the most common accreditation finding there is.
    /// </summary>
    public MultilingualText? EffectivenessVerification { get; set; }
    public DateOnly? EffectivenessVerifiedOn { get; set; }
    public Guid? EffectivenessVerifiedByUserId { get; set; }

    public IReadOnlyCollection<CorrectiveAction> Actions => _actions.AsReadOnly();

    public bool IsOpen => Status is NonConformityStatus.Open or NonConformityStatus.InProgress
                                 or NonConformityStatus.AwaitingVerification;

    public int? DaysOpen(DateOnly today) =>
        (ClosedAt is { } closed
            ? DateOnly.FromDateTime(closed.Date).DayNumber
            : today.DayNumber) - DateOnly.FromDateTime(DetectedAt.Date).DayNumber;

    public bool IsOverdue(DateOnly today) => IsOpen && DueDate is { } due && due < today;

    public CorrectiveAction AddAction(CorrectiveAction action)
    {
        action.AttachTo(Id);
        _actions.Add(action);
        // A new action on a file awaiting verification is work to do again: the register must not
        // show it waiting for its effectiveness check while an action is still pending.
        if (Status is NonConformityStatus.Open or NonConformityStatus.AwaitingVerification)
            Status = NonConformityStatus.InProgress;
        return action;
    }

    public void MarkAwaitingVerification() => Status = NonConformityStatus.AwaitingVerification;

    /// <summary>Every action is either completed or cancelled — nothing is left to do.</summary>
    public bool AllActionsFinished =>
        _actions.All(a => a.Status is CorrectiveActionStatus.Completed or CorrectiveActionStatus.Cancelled);

    /// <summary>
    /// Closes the file. A major or critical non-conformity cannot close without a root cause, a
    /// completed action and a documented effectiveness check; a minor one only needs an action.
    /// </summary>
    public Result Close(DateTimeOffset at, Guid byUserId)
    {
        // Closing again would move the closure stamp, and closing a dismissed file would count an
        // unfounded report as a non-conformity that happened.
        if (Status == NonConformityStatus.Closed)
            return Result.Failure(Error.Conflict("Nc_AlreadyClosed"));
        if (Status == NonConformityStatus.Rejected)
            return Result.Failure(Error.Conflict("Nc_AlreadyDismissed"));

        var errors = new List<Error>();

        // A cancelled action is finished too: cancelling is how a wrong or superseded action is
        // taken off the file, so it must not hold the closure back. At least one action must still
        // have been carried out, otherwise cancelling everything would close a file nobody acted on.
        if (_actions.Count == 0)
            errors.Add(Error.Regulatory("Nc_ActionRequiredBeforeClosure"));
        else if (!AllActionsFinished)
            errors.Add(Error.Regulatory("Nc_AllActionsMustBeFinished"));
        else if (_actions.All(a => a.Status == CorrectiveActionStatus.Cancelled))
            errors.Add(Error.Regulatory("Nc_CompletedActionRequiredBeforeClosure"));

        if (Severity is NonConformitySeverity.Major or NonConformitySeverity.Critical)
        {
            if (RootCause is null || RootCauseAnalysis is null ||
                string.IsNullOrWhiteSpace(RootCauseAnalysis.Fr))
                errors.Add(Error.Regulatory("Nc_RootCauseRequiredForMajor"));

            if (EffectivenessVerifiedOn is null ||
                EffectivenessVerification is null ||
                string.IsNullOrWhiteSpace(EffectivenessVerification.Fr))
                errors.Add(Error.Regulatory("Nc_EffectivenessVerificationRequired"));
        }

        if (AffectedReleasedResults &&
            (ClinicalImpactAssessment is null || string.IsNullOrWhiteSpace(ClinicalImpactAssessment.Fr)))
            errors.Add(Error.Regulatory("Nc_ClinicalImpactAssessmentRequired"));

        if (errors.Count > 0) return Result.Failure(errors);

        ClosedAt = at;
        ClosedByUserId = byUserId;
        Status = NonConformityStatus.Closed;
        return Result.Success();
    }

    /// <summary>
    /// Reopens a closed file.
    /// <para>
    /// Needed because closure is not always final in practice: an effectiveness check can fail
    /// later, the same defect can recur within the same cause, or the file can simply have been
    /// closed in error. Without this the only remedy was to open a second non-conformity, which
    /// splits one investigation across two references and loses the link an assessor follows.
    /// </para>
    /// <para>
    /// Returns to <see cref="NonConformityStatus.InProgress"/> rather than <c>Open</c>: the actions
    /// and the analysis already recorded are still there, so the file is resumed, not restarted.
    /// The closure stamp is cleared so <see cref="DaysOpen"/> keeps counting from the original
    /// detection — the time the file was wrongly closed is part of how long it stayed open.
    /// </para>
    /// </summary>
    public Result Reopen(string reason)
    {
        if (Status != NonConformityStatus.Closed)
            return Result.Failure(Error.Conflict("Nc_NotClosed"));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Nc_ReopenReasonRequired"));

        ClosedAt = null;
        ClosedByUserId = null;
        Status = NonConformityStatus.InProgress;

        return Result.Success();
    }

    /// <summary>
    /// Dismisses the file: examined and judged not to be a non-conformity after all.
    /// <para>
    /// The distinction from <see cref="Close"/> matters for the quality indicators. Closing runs
    /// the full ceremony — root cause, completed actions, effectiveness check — and counts as a
    /// non-conformity that happened. Dismissing records that the report was unfounded, and does
    /// not. Without this the only way to clear a mistaken report was to invent a corrective action
    /// for a problem that never existed.
    /// </para>
    /// </summary>
    public Result Dismiss(DateTimeOffset at, Guid byUserId, string justification)
    {
        if (Status == NonConformityStatus.Closed)
            return Result.Failure(Error.Conflict("Nc_AlreadyClosed"));

        if (Status == NonConformityStatus.Rejected)
            return Result.Failure(Error.Conflict("Nc_AlreadyDismissed"));

        if (string.IsNullOrWhiteSpace(justification))
            return Result.Failure(Error.Validation("Nc_DismissJustificationRequired"));

        // Refused when results were affected: something reached a patient, so whatever happened
        // was a non-conformity whether or not the original description was accurate.
        if (AffectedReleasedResults)
            return Result.Failure(Error.Regulatory("Nc_CannotDismissWithAffectedResults"));

        ClosedAt = at;
        ClosedByUserId = byUserId;
        Status = NonConformityStatus.Rejected;

        return Result.Success();
    }
}

public enum NonConformityCategory
{
    /// <summary>Pre-analytical: identification, collection, transport, reception.</summary>
    PreAnalytical = 0,

    /// <summary>Analytical: method, instrument, reagent, QC.</summary>
    Analytical = 1,

    /// <summary>Post-analytical: validation, report, delivery, turnaround.</summary>
    PostAnalytical = 2,

    /// <summary>Equipment failure or maintenance lapse.</summary>
    Equipment = 3,

    /// <summary>Reagent or consumable: stock-out, lot defect, storage.</summary>
    Reagent = 4,

    /// <summary>Personnel: competence, authorisation, training.</summary>
    Personnel = 5,

    /// <summary>Documentation and records.</summary>
    Documentation = 6,

    /// <summary>Information system.</summary>
    InformationSystem = 7,

    /// <summary>Patient or prescriber complaint.</summary>
    Complaint = 8,

    /// <summary>Health, safety or biohazard.</summary>
    Safety = 9,

    /// <summary>Confidentiality or personal-data incident (loi 08-09 / CNDP).</summary>
    DataProtection = 10,

    /// <summary>Subcontractor or supplier.</summary>
    ExternalProvider = 11
}

public enum NonConformitySeverity
{
    /// <summary>Caught internally, no effect on any result.</summary>
    Minor = 0,

    /// <summary>Could have affected a result; contained before release.</summary>
    Major = 1,

    /// <summary>A released result was wrong, or patient safety was at stake.</summary>
    Critical = 2,

    /// <summary>An opportunity for improvement rather than a defect.</summary>
    Observation = 3
}

public enum NonConformityStatus
{
    Open = 0,
    InProgress = 1,
    AwaitingVerification = 2,
    Closed = 3,
    /// <summary>Examined and judged not to be a non-conformity, with the reasoning recorded.</summary>
    Rejected = 4
}

public enum DetectionSource
{
    StaffObservation = 0,
    InternalQualityControl = 1,
    ExternalQualityAssessment = 2,
    InternalAudit = 3,
    ExternalAudit = 4,
    PatientComplaint = 5,
    PrescriberComplaint = 6,
    ManagementReview = 7,
    InstrumentAlarm = 8,
    SupplierNotification = 9
}

public enum RootCauseCategory
{
    HumanError = 0,
    InsufficientTraining = 1,
    ProcedureUnclearOrAbsent = 2,
    ProcedureNotFollowed = 3,
    EquipmentFailure = 4,
    ReagentDefect = 5,
    CalibrationDrift = 6,
    SoftwareDefect = 7,
    Communication = 8,
    ExternalProvider = 9,
    Workload = 10,
    EnvironmentalConditions = 11,
    Undetermined = 99
}

/// <summary>
/// An action taken in response to a non-conformity. Distinguishes correction (fixing this
/// occurrence) from corrective action (removing the cause) and preventive action, because ISO 15189
/// treats them as different obligations and conflating them is how causes survive.
/// </summary>
public sealed class CorrectiveAction : Entity, IAuditable
{
    private CorrectiveAction() { }

    public CorrectiveAction(ActionKind kind, MultilingualText description, Guid responsibleUserId,
                            DateOnly targetDate)
    {
        Kind = kind;
        Description = description;
        ResponsibleUserId = responsibleUserId;
        TargetDate = targetDate;
        Status = CorrectiveActionStatus.Planned;
    }

    public Guid NonConformityId { get; private set; }
    internal void AttachTo(Guid nonConformityId) => NonConformityId = nonConformityId;

    public ActionKind Kind { get; private set; }
    public MultilingualText Description { get; set; } = new();

    public Guid ResponsibleUserId { get; set; }
    public DateOnly TargetDate { get; set; }

    public CorrectiveActionStatus Status { get; private set; }

    public DateOnly? CompletedOn { get; private set; }
    public Guid? CompletedByUserId { get; private set; }
    public MultilingualText? CompletionEvidence { get; private set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public void Start() => Status = CorrectiveActionStatus.InProgress;

    public Result Complete(DateOnly on, Guid byUserId, MultilingualText? evidence)
    {
        if (Status == CorrectiveActionStatus.Cancelled)
            return Result.Failure(Error.Conflict("Action_Cancelled"));
        // The completion date and its evidence are the record; completing twice would rewrite them.
        if (Status == CorrectiveActionStatus.Completed)
            return Result.Failure(Error.Conflict("Nc_ActionAlreadyCompleted"));

        CompletedOn = on;
        CompletedByUserId = byUserId;
        CompletionEvidence = evidence;
        Status = CorrectiveActionStatus.Completed;
        return Result.Success();
    }

    public void Cancel(string reason)
    {
        Status = CorrectiveActionStatus.Cancelled;
        Notes = reason;
    }

    public bool IsOverdue(DateOnly today) =>
        Status is CorrectiveActionStatus.Planned or CorrectiveActionStatus.InProgress &&
        TargetDate < today;
}

public enum ActionKind
{
    /// <summary>Correction — repairs this occurrence only.</summary>
    Correction = 0,

    /// <summary>Corrective action — removes the cause so it does not recur.</summary>
    CorrectiveAction = 1,

    /// <summary>Preventive action — addresses a risk that has not yet materialised.</summary>
    PreventiveAction = 2,

    /// <summary>Improvement action arising from an observation.</summary>
    Improvement = 3
}

public enum CorrectiveActionStatus
{
    Planned = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3
}
