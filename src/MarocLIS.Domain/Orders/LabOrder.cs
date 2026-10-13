using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Domain.Orders;

/// <summary>
/// A request for analyses (un dossier / une demande). The aggregate that the whole workflow turns
/// around: reception registers it, the phlebotomist collects against it, technicians produce
/// results for its items, a biologist validates it and only then may a report be released.
/// </summary>
public sealed class LabOrder : AuditableAggregateRoot, ISoftDeletable, ISiteScoped
{
    private readonly List<LabOrderItem> _items = [];

    private LabOrder() { }

    public LabOrder(string accessionNumber, Guid patientId, Guid siteId,
                    DateTimeOffset registeredAt, Guid registeredByUserId)
    {
        AccessionNumber = accessionNumber;
        PatientId = patientId;
        SiteId = siteId;
        RegisteredAt = registeredAt;
        RegisteredByUserId = registeredByUserId;
        Status = LabOrderStatus.Registered;
    }

    /// <summary>
    /// Unique, human-dictatable file number printed on labels and reports, e.g. "26-09-004317".
    /// Allocated from a per-site, per-year sequence so two workstations never collide.
    /// </summary>
    public string AccessionNumber { get; private set; } = string.Empty;

    public Guid PatientId { get; private set; }
    public Patient? Patient { get; private set; }

    public Guid SiteId { get; set; }

    public Guid? PrescriberId { get; set; }
    public Prescriber? Prescriber { get; private set; }

    /// <summary>
    /// Kept as text alongside the link: prescribers are often written down before the record
    /// exists, and losing the handwritten name would break traceability of the prescription.
    /// </summary>
    public string? PrescriberFreeText { get; set; }

    public DateTimeOffset RegisteredAt { get; private set; }
    public Guid RegisteredByUserId { get; private set; }

    /// <summary>Date of the prescription itself, which may predate registration.</summary>
    public DateOnly? PrescriptionDate { get; set; }

    /// <summary>Scan of the prescription — required in the file for AMO claims.</summary>
    public byte[]? PrescriptionScan { get; set; }
    public string? PrescriptionScanContentType { get; set; }

    public OrderPriority Priority { get; set; } = OrderPriority.Routine;

    public LabOrderStatus Status { get; private set; }

    public OrderOrigin Origin { get; set; } = OrderOrigin.Walkin;

    /// <summary>Ward or department, when the order comes from a clinic or hospital.</summary>
    public string? WardOrUnit { get; set; }
    public string? RoomOrBed { get; set; }

    // ---- Clinical context --------------------------------------------------------------------

    /// <summary>Clinical indication supplied by the prescriber; drives interpretation.</summary>
    public string? ClinicalInformation { get; set; }

    /// <summary>ICD-10 code of the suspected or known condition, when supplied.</summary>
    public string? DiagnosisCode { get; set; }

    /// <summary>Treatment in progress at the time of sampling, copied from the patient file and editable.</summary>
    public string? CurrentTreatment { get; set; }

    /// <summary>Whether the patient declared having fasted, and for how long.</summary>
    public bool? PatientFasted { get; set; }
    public int? DeclaredFastingHours { get; set; }

    // ---- Billing snapshot --------------------------------------------------------------------

    /// <summary>
    /// The payer and entitlement as they stood at registration. Snapshotted because a later change
    /// of insurance must not silently reprice an invoice that has already been issued.
    /// </summary>
    public Guid? PayerId { get; set; }
    public Guid? PatientCoverageId { get; set; }
    public decimal? ReimbursementRateAtRegistration { get; set; }
    public string? PriorAgreementNumber { get; set; }

    /// <summary>Discount granted at reception, in percent, with the reason it was granted.</summary>
    public decimal? DiscountPercent { get; set; }
    public string? DiscountReason { get; set; }

    public Guid? InvoiceId { get; set; }

    // ---- Lifecycle timestamps used for turnaround reporting -----------------------------------

    public DateTimeOffset? FirstCollectedAt { get; private set; }
    public DateTimeOffset? AllReceivedAt { get; private set; }
    public DateTimeOffset? FullyValidatedAt { get; private set; }
    public DateTimeOffset? FirstReportedAt { get; private set; }

    /// <summary>Promised completion, the latest expected completion among the items.</summary>
    public DateTimeOffset? DueAt { get; set; }

    // ---- Cancellation -------------------------------------------------------------------------

    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>Report language for this order; defaults to the patient's preference.</summary>
    public AppLanguage ReportLanguage { get; set; } = AppLanguage.French;

    /// <summary>Operator note visible on worklists but never printed.</summary>
    public string? InternalNote { get; set; }

    public IReadOnlyCollection<LabOrderItem> Items => _items.AsReadOnly();

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    // ---- Composition --------------------------------------------------------------------------

    public bool IsEditable => Status is LabOrderStatus.Draft or LabOrderStatus.Registered
                                        or LabOrderStatus.AwaitingCollection;

    public bool IsClosed => Status is LabOrderStatus.Reported or LabOrderStatus.Cancelled;

    public Result<LabOrderItem> AddItem(LabOrderItem item)
    {
        if (IsClosed)
            return Result.Failure<LabOrderItem>(Error.Conflict("Order_ClosedCannotAddItem"));

        if (_items.Any(i => i.AnalysisTestId == item.AnalysisTestId && !i.IsCancelled))
            return Result.Failure<LabOrderItem>(
                Error.Conflict("Order_AnalysisAlreadyPresent", item.AnalysisTestId));

        item.AttachTo(Id);
        _items.Add(item);
        RecomputeStatus();
        return Result.Success(item);
    }

    /// <summary>
    /// Cancels one analysis. Allowed up to biological validation; afterwards the result exists and
    /// must be amended through the report-amendment path instead of being made to disappear.
    /// </summary>
    public Result CancelItem(Guid itemId, DateTimeOffset now, Guid byUserId, string reason)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
            return Result.Failure(Error.NotFound("Order_ItemNotFound"));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Order_CancellationReasonRequired"));
        if (item.Status == LabOrderItemStatus.Validated)
            return Result.Failure(Error.Conflict("Order_ItemValidatedCannotCancel"));

        item.Cancel(now, byUserId, reason);
        RecomputeStatus();
        return Result.Success();
    }

    /// <summary>
    /// Cancels the whole order. Refused once anything has been biologically validated, because a
    /// validated result has already left the laboratory's sole control.
    /// </summary>
    public Result Cancel(DateTimeOffset now, Guid byUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Order_CancellationReasonRequired"));
        if (Status == LabOrderStatus.Cancelled)
            return Result.Success();
        if (_items.Any(i => i.Status == LabOrderItemStatus.Validated))
            return Result.Failure(Error.Conflict("Order_HasValidatedItemsCannotCancel"));

        foreach (var item in _items.Where(i => !i.IsCancelled))
            item.Cancel(now, byUserId, reason);

        Status = LabOrderStatus.Cancelled;
        CancelledAt = now;
        CancelledByUserId = byUserId;
        CancellationReason = reason;
        return Result.Success();
    }

    // ---- Workflow transitions -----------------------------------------------------------------

    public void MarkFirstCollection(DateTimeOffset at)
    {
        FirstCollectedAt ??= at;
        RecomputeStatus();
    }

    public void MarkAllReceived(DateTimeOffset at)
    {
        AllReceivedAt ??= at;
        RecomputeStatus();
    }

    public void MarkFirstReported(DateTimeOffset at) => FirstReportedAt ??= at;

    /// <summary>
    /// Recomputes the order status from its items. The order never carries a status that its items
    /// contradict, so a worklist filter and the file header can never disagree.
    /// </summary>
    public void RecomputeStatus()
    {
        if (Status == LabOrderStatus.Cancelled) return;

        var live = _items.Where(i => !i.IsCancelled).ToList();

        if (live.Count == 0)
        {
            Status = _items.Count > 0 ? LabOrderStatus.Cancelled : LabOrderStatus.Registered;
            return;
        }

        if (live.All(i => i.Status == LabOrderItemStatus.Reported))
        {
            Status = LabOrderStatus.Reported;
            FullyValidatedAt ??= live.Max(i => i.ValidatedAt);
            return;
        }

        if (live.All(i => i.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported))
        {
            Status = LabOrderStatus.Validated;
            FullyValidatedAt ??= live.Max(i => i.ValidatedAt);
            return;
        }

        if (live.Any(i => i.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported))
        {
            Status = LabOrderStatus.PartiallyValidated;
            return;
        }

        if (live.Any(i => i.Status is LabOrderItemStatus.InProgress
                                   or LabOrderItemStatus.ResultEntered
                                   or LabOrderItemStatus.TechnicallyValidated))
        {
            Status = LabOrderStatus.InProgress;
            return;
        }

        if (live.All(i => i.Status >= LabOrderItemStatus.Received))
        {
            Status = LabOrderStatus.Received;
            return;
        }

        if (live.Any(i => i.Status >= LabOrderItemStatus.Collected))
        {
            Status = LabOrderStatus.PartiallyCollected;
            return;
        }

        Status = FirstCollectedAt is null ? LabOrderStatus.AwaitingCollection : LabOrderStatus.PartiallyCollected;
    }

    // ---- Derived views ------------------------------------------------------------------------

    public int LiveItemCount => _items.Count(i => !i.IsCancelled);

    public int ValidatedItemCount =>
        _items.Count(i => !i.IsCancelled && i.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported);

    public decimal CompletionRatio =>
        LiveItemCount == 0 ? 0m : Math.Round((decimal)ValidatedItemCount / LiveItemCount, 3);

    public bool IsUrgent => Priority is OrderPriority.Urgent or OrderPriority.VitalEmergency;

    public bool IsOverdue(DateTimeOffset now) =>
        DueAt is { } due && now > due && Status is not (LabOrderStatus.Validated or LabOrderStatus.Reported
                                                        or LabOrderStatus.Cancelled);

    /// <summary>Turnaround actually achieved, from sample reception to full validation.</summary>
    public TimeSpan? AchievedTurnaround =>
        AllReceivedAt is { } received && FullyValidatedAt is { } validated ? validated - received : null;

    /// <summary>
    /// Registration-time checks. Deliberately returns every problem at once so the reception clerk
    /// fixes the whole form in one pass instead of discovering issues one at a time.
    /// </summary>
    public Result ValidateForRegistration()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(AccessionNumber))
            errors.Add(Error.Validation("Order_AccessionNumberRequired"));
        if (LiveItemCount == 0)
            errors.Add(Error.Validation("Order_AtLeastOneAnalysisRequired"));
        if (PrescriberId is null && string.IsNullOrWhiteSpace(PrescriberFreeText)
            && Origin != OrderOrigin.PatientRequest)
            errors.Add(Error.Validation("Order_PrescriberRequired"));
        if (DiscountPercent is { } discount)
        {
            if (discount is < 0m or > 100m)
                errors.Add(Error.Validation("Order_DiscountOutOfRange"));
            else if (discount > 0m && string.IsNullOrWhiteSpace(DiscountReason))
                errors.Add(Error.Validation("Order_DiscountReasonRequired"));
        }
        if (PrescriptionDate is { } prescribed && prescribed > DateOnly.FromDateTime(RegisteredAt.Date))
            errors.Add(Error.Validation("Order_PrescriptionDateInFuture"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

/// <summary>Status of the order as a whole, derived from its items.</summary>
public enum LabOrderStatus
{
    /// <summary>Being composed at reception, not yet committed.</summary>
    Draft = 0,

    /// <summary>Registered; nothing collected yet.</summary>
    Registered = 1,

    /// <summary>Waiting for the phlebotomist.</summary>
    AwaitingCollection = 2,

    /// <summary>Some samples drawn, others still pending.</summary>
    PartiallyCollected = 3,

    /// <summary>All samples received and accepted by the technical platform.</summary>
    Received = 4,

    /// <summary>At least one analysis is running or has an unvalidated result.</summary>
    InProgress = 5,

    /// <summary>Some analyses are biologically validated, others are not.</summary>
    PartiallyValidated = 6,

    /// <summary>Everything is biologically validated; the report can be released.</summary>
    Validated = 7,

    /// <summary>A report has been released for every analysis.</summary>
    Reported = 8,

    Cancelled = 99
}

public enum OrderPriority
{
    Routine = 0,

    /// <summary>Urgent — jumps the queue, turnaround measured in minutes.</summary>
    Urgent = 1,

    /// <summary>Vital emergency — the technician is interrupted, the biologist is called.</summary>
    VitalEmergency = 2
}

public enum OrderOrigin
{
    /// <summary>Patient presenting at the laboratory counter.</summary>
    Walkin = 0,

    /// <summary>Sample drawn at the patient's home by a laboratory phlebotomist.</summary>
    HomeCollection = 1,

    /// <summary>Sent by a clinic or hospital ward.</summary>
    Institution = 2,

    /// <summary>Received from a collection centre belonging to the laboratory.</summary>
    CollectionCentre = 3,

    /// <summary>Subcontracted to us by another laboratory.</summary>
    Subcontracting = 4,

    /// <summary>Occupational medicine / company campaign.</summary>
    OccupationalHealth = 5,

    /// <summary>The patient asked for the analysis without a prescription.</summary>
    PatientRequest = 6
}
