using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Orders;

/// <summary>
/// One analysis inside an order. Carries its own status because the disciplines of a single order
/// progress independently — the NFS is validated within the hour while the culture needs three
/// days — and because billing and turnaround are measured per act.
/// </summary>
public sealed class LabOrderItem : Entity, IAuditable
{
    private LabOrderItem() { }

    public LabOrderItem(Guid analysisTestId, OrderPriority priority)
    {
        AnalysisTestId = analysisTestId;
        Priority = priority;
        Status = LabOrderItemStatus.Pending;
    }

    public Guid LabOrderId { get; private set; }
    public LabOrder? LabOrder { get; private set; }

    internal void AttachTo(Guid orderId) => LabOrderId = orderId;

    public Guid AnalysisTestId { get; private set; }
    public AnalysisTest? AnalysisTest { get; private set; }

    /// <summary>Set when the analysis was added as part of a panel, for reporting and pricing.</summary>
    public Guid? TestPanelId { get; set; }

    /// <summary>Set when the analysis was added automatically by a reflex rule.</summary>
    public Guid? AddedByReflexRuleId { get; set; }

    /// <summary>The sample the analysis is run on. Assigned when samples are created for the order.</summary>
    public Guid? SampleId { get; set; }

    public OrderPriority Priority { get; set; }

    public LabOrderItemStatus Status { get; private set; }

    /// <summary>Copied from the analysis so worklists can filter by discipline without a join.</summary>
    public Guid DepartmentId { get; set; }

    // ---- Pricing snapshot ---------------------------------------------------------------------

    /// <summary>
    /// Price as charged for this act, frozen at registration. Never recomputed: a tariff revision
    /// must not alter what a patient was told they owed.
    /// </summary>
    public decimal ChargedPrice { get; set; }

    /// <summary>Reimbursement base under the national reference tariff at registration.</summary>
    public decimal ReferenceTariffBase { get; set; }

    /// <summary>Letter-key and coefficient as billed, needed verbatim on the AMO claim.</summary>
    public string? BilledLetterKey { get; set; }
    public decimal? BilledCoefficient { get; set; }
    public string? BilledActCode { get; set; }

    /// <summary>Share borne by the payer, derived from the coverage rate at registration.</summary>
    public decimal PayerShare { get; set; }

    /// <summary>Share borne by the patient (ticket modérateur plus any excess over the TNR).</summary>
    public decimal PatientShare { get; set; }

    // ---- Subcontracting -----------------------------------------------------------------------

    /// <summary>Set when the act is sent to a partner laboratory; the report must say so.</summary>
    public bool IsSubcontracted { get; set; }
    public Guid? SubcontractorSiteId { get; set; }
    public DateTimeOffset? SentToSubcontractorAt { get; set; }

    /// <summary>The partner laboratory's own number for the request, entered when they give it.</summary>
    public string? SubcontractorReference { get; set; }

    /// <summary>Number of the dispatch slip (bordereau d'envoi) the tube left with.</summary>
    public string? SubcontractDispatchNumber { get; private set; }
    public Guid? SubcontractSentByUserId { get; private set; }

    /// <summary>When the partner's results were first entered here.</summary>
    public DateTimeOffset? SubcontractReturnedAt { get; private set; }

    public bool IsAwaitingSubcontractDispatch =>
        IsSubcontracted && !IsCancelled && SentToSubcontractorAt is null && ResultEnteredAt is null;

    /// <summary>
    /// Records that the act left for the partner laboratory on a dispatch slip. The partner may be
    /// changed at the time of sending, for instance when the usual one cannot take the sample.
    /// </summary>
    public Result SendToSubcontractor(Guid partnerSiteId, string dispatchNumber, DateTimeOffset at,
                                      Guid byUserId)
    {
        if (!IsSubcontracted)
            return Result.Failure(Error.Conflict("Subcontract_NotSubcontracted"));
        if (IsCancelled)
            return Result.Failure(Error.Conflict("Subcontract_ItemCancelled"));
        if (SentToSubcontractorAt is not null)
            return Result.Failure(Error.Conflict("Subcontract_AlreadySent"));
        if (Status is LabOrderItemStatus.Pending or LabOrderItemStatus.Collected)
            return Result.Failure(Error.Conflict("Subcontract_NotReceived"));

        SubcontractorSiteId = partnerSiteId;
        SubcontractDispatchNumber = dispatchNumber;
        SentToSubcontractorAt = at;
        SubcontractSentByUserId = byUserId;
        return Result.Success();
    }

    /// <summary>Called when results are entered on a subcontracted act: the partner has answered.</summary>
    public void MarkSubcontractReturned(DateTimeOffset at)
    {
        if (IsSubcontracted) SubcontractReturnedAt ??= at;
    }

    // ---- Lifecycle ----------------------------------------------------------------------------

    public DateTimeOffset? CollectedAt { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? ResultEnteredAt { get; private set; }
    public DateTimeOffset? TechnicallyValidatedAt { get; private set; }
    public Guid? TechnicallyValidatedByUserId { get; private set; }
    public DateTimeOffset? ValidatedAt { get; private set; }
    public Guid? ValidatedByUserId { get; private set; }
    public DateTimeOffset? ReportedAt { get; private set; }

    /// <summary>Expected completion, computed from the analysis turnaround at reception.</summary>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>Number of times the analysis has been repeated, whatever the reason.</summary>
    public int RunCount { get; private set; }

    /// <summary>Why the analysis was repeated — quoted in the QC review and non-conformity file.</summary>
    public string? RepeatReason { get; private set; }

    /// <summary>
    /// When the analysis was last sent back to the bench. A value stored before it belongs to the
    /// run that was set aside: until one is stored after it, the analysis is not complete again.
    /// </summary>
    public DateTimeOffset? RepeatedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public string? CancellationReason { get; private set; }

    public bool IsCancelled => Status == LabOrderItemStatus.Cancelled;

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    // ---- Transitions --------------------------------------------------------------------------
    // Each transition is guarded rather than a free setter, so no code path can mark an item
    // validated without it having carried a result first.

    public Result MarkCollected(DateTimeOffset at)
    {
        if (Status is not (LabOrderItemStatus.Pending or LabOrderItemStatus.Collected))
            return Result.Failure(Error.Conflict("Item_CannotCollectFromStatus", Status));
        CollectedAt ??= at;
        Status = LabOrderItemStatus.Collected;
        return Result.Success();
    }

    public Result MarkReceived(DateTimeOffset at, DateTimeOffset? dueAt)
    {
        if (Status is not (LabOrderItemStatus.Collected or LabOrderItemStatus.Received))
            return Result.Failure(Error.Conflict("Item_CannotReceiveFromStatus", Status));
        ReceivedAt ??= at;
        DueAt = dueAt;
        Status = LabOrderItemStatus.Received;
        return Result.Success();
    }

    public Result MarkInProgress(DateTimeOffset at)
    {
        if (Status is not (LabOrderItemStatus.Received or LabOrderItemStatus.InProgress))
            return Result.Failure(Error.Conflict("Item_CannotStartFromStatus", Status));
        StartedAt ??= at;
        RunCount++;
        Status = LabOrderItemStatus.InProgress;
        return Result.Success();
    }

    public Result MarkResultEntered(DateTimeOffset at)
    {
        if (Status is not (LabOrderItemStatus.Received or LabOrderItemStatus.InProgress
                                                      or LabOrderItemStatus.ResultEntered))
            return Result.Failure(Error.Conflict("Item_CannotEnterResultFromStatus", Status));
        ResultEnteredAt = at;
        Status = LabOrderItemStatus.ResultEntered;
        return Result.Success();
    }

    /// <summary>Technical validation by a technician: plausibility, QC status, delta check.</summary>
    public Result MarkTechnicallyValidated(DateTimeOffset at, Guid byUserId)
    {
        if (Status != LabOrderItemStatus.ResultEntered)
            return Result.Failure(Error.Conflict("Item_RequiresResultBeforeTechnicalValidation"));
        TechnicallyValidatedAt = at;
        TechnicallyValidatedByUserId = byUserId;
        Status = LabOrderItemStatus.TechnicallyValidated;
        return Result.Success();
    }

    /// <summary>
    /// Biological validation — the act that makes the result a medical statement. Loi n° 12-01
    /// reserves it to a biologist; the caller must have checked
    /// <see cref="Identity.AppUser.CanBiologicallyValidate"/> first, and this guard makes sure a
    /// result exists and was technically checked.
    /// </summary>
    public Result MarkBiologicallyValidated(DateTimeOffset at, Guid byBiologistUserId,
                                            bool allowWithoutTechnicalValidation = false)
    {
        if (Status == LabOrderItemStatus.Validated)
            return Result.Success();

        var allowed = CanBeBiologicallyValidated(allowWithoutTechnicalValidation);
        if (allowed.IsFailure) return allowed;

        ValidatedAt = at;
        ValidatedByUserId = byBiologistUserId;
        Status = LabOrderItemStatus.Validated;
        return Result.Success();
    }

    /// <summary>
    /// The preconditions of <see cref="MarkBiologicallyValidated"/>, checked without changing
    /// anything, so the validation of a batch can be refused before any of its results is released.
    /// </summary>
    public Result CanBeBiologicallyValidated(bool allowWithoutTechnicalValidation = false)
    {
        if (Status == LabOrderItemStatus.Validated)
            return Result.Success();

        if (Status == LabOrderItemStatus.ResultEntered && !allowWithoutTechnicalValidation)
            return Result.Failure(Error.Regulatory("Item_TechnicalValidationRequiredFirst"));

        if (Status is not (LabOrderItemStatus.TechnicallyValidated or LabOrderItemStatus.ResultEntered))
            return Result.Failure(Error.Conflict("Item_CannotValidateFromStatus", Status));

        return Result.Success();
    }

    /// <summary>
    /// Withdraws a validation. Only meaningful before the report left the laboratory; afterwards
    /// the corrected result must be issued as an amended report so the recipient sees the change.
    /// </summary>
    public Result Unvalidate(string reason)
    {
        // The reported check comes first. Reporting moves the status off Validated, so the generic
        // guard below would otherwise catch every reported analysis and answer "not validated" —
        // true, but it sends the biologist looking for a validation that is right there, instead of
        // telling them the one thing they need to know: the result has left the laboratory and the
        // correction has to go out as an amended report.
        if (Status == LabOrderItemStatus.Reported)
            return Result.Failure(Error.Regulatory("Item_AlreadyReportedRequiresAmendment"));
        if (Status != LabOrderItemStatus.Validated)
            return Result.Failure(Error.Conflict("Item_NotValidated"));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Item_UnvalidationReasonRequired"));

        ValidatedAt = null;
        ValidatedByUserId = null;
        RepeatReason = reason;
        Status = LabOrderItemStatus.TechnicallyValidated;
        return Result.Success();
    }

    /// <summary>
    /// Sends the analysis back to result entry because one of its checked or released values is
    /// being corrected (<see cref="Results.TestResult.Revise"/>). The corrected figure has been
    /// checked by nobody: it goes through technical and then biological validation again, like any
    /// new value, before a report can carry it.
    /// <para>
    /// Unlike <see cref="Unvalidate"/>, allowed once the analysis has been reported: correcting a
    /// printed figure is exactly what an amended report is for. <see cref="ReportedAt"/> is kept,
    /// so the analysis stays known as printed — it cannot be cancelled, and the next release is
    /// recognised as a rectification of the version the recipient holds.
    /// </para>
    /// </summary>
    public Result ReopenForCorrection(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Result_RevisionReasonRequired"));
        if (Status is not (LabOrderItemStatus.ResultEntered or LabOrderItemStatus.TechnicallyValidated
                           or LabOrderItemStatus.Validated or LabOrderItemStatus.Reported))
            return Result.Failure(Error.Conflict("Item_CannotCorrectFromStatus", Status));

        TechnicallyValidatedAt = null;
        TechnicallyValidatedByUserId = null;
        ValidatedAt = null;
        ValidatedByUserId = null;
        RepeatReason = reason;
        Status = LabOrderItemStatus.ResultEntered;
        return Result.Success();
    }

    /// <summary>
    /// Records that some of the analysis's results went out on a report while the analysis itself
    /// is not validated yet — the biologist released the haemoglobin of a blood count and not the
    /// rest. The analysis keeps its status; <see cref="ReportedAt"/> is what then stops it being
    /// cancelled, since cancelling would drop a printed figure from every later version.
    /// </summary>
    public void RecordPartlyReported(DateTimeOffset at)
    {
        if (Status != LabOrderItemStatus.Cancelled) ReportedAt ??= at;
    }

    /// <summary>Some of the analysis's results are on a report that has left the laboratory.</summary>
    public bool HasReportedResults => ReportedAt is not null;

    public Result MarkReported(DateTimeOffset at)
    {
        if (Status is not (LabOrderItemStatus.Validated or LabOrderItemStatus.Reported))
            return Result.Failure(Error.Regulatory("Item_MustBeValidatedBeforeReporting"));
        ReportedAt ??= at;
        Status = LabOrderItemStatus.Reported;
        return Result.Success();
    }

    /// <summary>
    /// Sends the analysis back to the bench, keeping the run history.
    /// <para>
    /// Not once a biologist has validated it. Repeating reset the analysis and left its released
    /// results standing: they went on printing, the new run's values were refused, and the analysis
    /// could never be validated again. The validation is withdrawn first, by somebody entitled to
    /// withdraw it, and the analysis can then be repeated.
    /// </para>
    /// </summary>
    public Result Repeat(string reason, DateTimeOffset at)
    {
        if (Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported
                   or LabOrderItemStatus.Cancelled)
            return Result.Failure(Error.Conflict("Item_CannotRepeatFromStatus", Status));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Item_RepeatReasonRequired"));

        RepeatReason = reason;
        RepeatedAt = at;
        TechnicallyValidatedAt = null;
        TechnicallyValidatedByUserId = null;
        ValidatedAt = null;
        ValidatedByUserId = null;
        Status = LabOrderItemStatus.Received;
        return Result.Success();
    }

    internal void Cancel(DateTimeOffset at, Guid byUserId, string reason)
    {
        Status = LabOrderItemStatus.Cancelled;
        CancelledAt = at;
        CancelledByUserId = byUserId;
        CancellationReason = reason;
    }

    public bool IsOverdue(DateTimeOffset now) =>
        DueAt is { } due && now > due &&
        Status is not (LabOrderItemStatus.Validated or LabOrderItemStatus.Reported
                                                    or LabOrderItemStatus.Cancelled);

    /// <summary>Total price for this act, patient share plus payer share.</summary>
    public Money Total => Money.Of(ChargedPrice);
}

/// <summary>
/// Status of one analysis. The numeric order matters: comparisons such as
/// <c>Status &gt;= Received</c> are used to decide whether a whole order has reached a stage.
/// </summary>
public enum LabOrderItemStatus
{
    /// <summary>Registered, sample not drawn.</summary>
    Pending = 0,

    /// <summary>Sample drawn.</summary>
    Collected = 1,

    /// <summary>Sample received and accepted by the technical platform.</summary>
    Received = 2,

    /// <summary>On the analyser or on the bench.</summary>
    InProgress = 3,

    /// <summary>A result has been captured but nobody has checked it.</summary>
    ResultEntered = 4,

    /// <summary>Checked by a technician: plausible, QC in control, delta acceptable.</summary>
    TechnicallyValidated = 5,

    /// <summary>Released by a biologist — legally reportable.</summary>
    Validated = 6,

    /// <summary>Included in a report that has been delivered.</summary>
    Reported = 7,

    Cancelled = 99
}
