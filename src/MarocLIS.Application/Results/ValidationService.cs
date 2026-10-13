using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;

namespace MarocLIS.Application.Results;

/// <summary>One order awaiting biological validation, as the biologist's worklist shows it.</summary>
public sealed record ValidationWorklistEntry
{
    public required Guid LabOrderId { get; init; }
    public required string AccessionNumber { get; init; }
    public required string PatientName { get; init; }
    public int? AgeYears { get; init; }
    public Domain.Patients.Gender Gender { get; init; }
    public OrderPriority Priority { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool IsOverdue { get; init; }

    public int AwaitingCount { get; init; }
    public int AbnormalCount { get; init; }
    public int CriticalCount { get; init; }

    /// <summary>Critical values not yet communicated — these block release and head the list.</summary>
    public int UncommunicatedCriticalCount { get; init; }

    public bool HasQcFailure { get; init; }
    public bool HasDeltaCheckFailure { get; init; }
    public bool HasSampleDefect { get; init; }

    /// <summary>Disciplines represented, so a biologist can filter to their own scope.</summary>
    public IReadOnlyList<string> DepartmentCodes { get; init; } = [];
}

/// <summary>A biologist's decision on one result.</summary>
public sealed record ValidationDecision(
    Guid TestResultId,
    bool Validate,
    MultilingualText? BiologistComment = null,
    string? QcOverrideJustification = null);

public sealed record ValidationOutcome(
    int Validated,
    int Refused,
    IReadOnlyList<Error> Blocked,
    bool OrderFullyValidated);

/// <summary>
/// Biological validation — the act that turns a measurement into a medical statement.
/// <para>
/// Three gates apply, and none of them is a warning the operator can click past:
/// </para>
/// <list type="number">
/// <item><b>Who.</b> Loi n° 12-01 reserves the release of medical biology results to a qualified
/// biologist. Checked by <see cref="IPermissionGuard.RequireBiologicalValidationAsync"/>, which
/// re-reads the user's qualification and its expiry from the database rather than trusting the
/// session — an habilitation can lapse mid-shift.</item>
/// <item><b>Quality control.</b> A result covered by an out-of-control run cannot be released
/// without a recorded, justified override.</item>
/// <item><b>Critical values.</b> A panic value cannot be released until it has been communicated to
/// the prescriber and the recipient has read it back. ISO 15189 §5.9.1 treats an uncommunicated
/// critical result as a failure of the laboratory, not of the clinician.</item>
/// </list>
/// </summary>
public sealed class ValidationService(
    IRepository<LabOrder> orders,
    IRepository<AnalysisTest> analyses,
    IRepository<Instrument> instruments,
    IRepository<CriticalResultNotification> notifications,
    IResultQueries queries,
    IValidationQueries validationQueries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor accessAuditor,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<ValidationWorklistEntry>>> GetWorklistAsync(
        Guid? departmentId = null, bool onlyUrgent = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<ValidationWorklistEntry>>(permitted.FirstError);

        var entries = await validationQueries.GetValidationWorklistAsync(
            currentUser.SiteId,
            departmentId,
            // A biologist with a restricted scope only sees what they may actually release; showing
            // work they cannot action is how a worklist stops being trusted.
            currentUser.AuthorizedDepartmentCodes,
            onlyUrgent,
            clock.Now,
            ct);

        return Result.Success(entries);
    }

    /// <summary>
    /// Applies a batch of validation decisions to one order.
    /// <para>
    /// Batched per order rather than per result, because a biologist validates a file as a whole
    /// and the order's status has to settle once and consistently. A result the gates refuse is
    /// reported back and left untouched; the rest still go through, so one blocked potassium does
    /// not hold up a whole file.
    /// </para>
    /// </summary>
    public async Task<Result<ValidationOutcome>> ValidateAsync(
        Guid orderId, IReadOnlyList<ValidationDecision> decisions, CancellationToken ct = default)
    {
        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure<ValidationOutcome>(Error.NotFound("Order_NotFound"));

        var stored = await queries.GetResultsForOrderAsync(orderId, null, ct);
        var byId = stored.ToDictionary(r => r.Id);

        var catalogue = await LoadDepartmentCodesAsync(order, ct);

        var blocked = new List<Error>();
        var validated = 0;
        var refused = 0;
        var now = clock.Now;
        var today = clock.Today;

        // Cache the per-department authorisation check: a file typically spans two or three
        // disciplines and re-reading the user for each result would be wasteful.
        var departmentChecks = new Dictionary<string, Result>(StringComparer.OrdinalIgnoreCase);

        foreach (var decision in decisions)
        {
            if (!byId.TryGetValue(decision.TestResultId, out var result))
            {
                blocked.Add(Error.NotFound("Result_NotFound", decision.TestResultId));
                continue;
            }

            if (!decision.Validate)
            {
                refused++;
                continue;
            }

            var departmentCode = catalogue.GetValueOrDefault(result.LabOrderItemId) ?? string.Empty;

            if (!departmentChecks.TryGetValue(departmentCode, out var authorised))
            {
                authorised = await guard.RequireBiologicalValidationAsync(departmentCode, ct);
                departmentChecks[departmentCode] = authorised;
            }

            if (authorised.IsFailure)
            {
                blocked.Add(authorised.FirstError);
                continue;
            }

            // The instrument that produced the value must have been fit to do so. An overdue
            // calibration is discovered here rather than at the bench surprisingly often.
            if (result.InstrumentId is { } instrumentId)
            {
                var instrument = await instruments.GetByIdAsync(instrumentId, ct);
                if (instrument is not null)
                {
                    var fit = instrument.CanProduceResults(today);
                    if (fit.IsFailure)
                    {
                        blocked.Add(fit.FirstError);
                        continue;
                    }
                }
            }

            if (decision.BiologistComment is not null)
                result.BiologistComment = decision.BiologistComment;

            // A QC override is an exception the laboratory must be able to account for, so the
            // justification is written onto the covering QC point, not just onto the result.
            var qcOverridden = false;

            if (result.Flags.HasFlag(ResultFlag.QcOutOfControl))
            {
                if (string.IsNullOrWhiteSpace(decision.QcOverrideJustification))
                {
                    blocked.Add(Error.Regulatory("Result_QcOutOfControlBlocksValidation"));
                    continue;
                }

                var recorded = await validationQueries.RecordQcOverrideAsync(
                    result.CoveringQcRunId, decision.QcOverrideJustification, ct);

                if (recorded.IsFailure)
                {
                    blocked.Add(recorded.FirstError);
                    continue;
                }

                qcOverridden = true;
            }

            var criticalCommunicated = !result.IsCritical
                || await validationQueries.IsCriticalCommunicatedAsync(result.Id, ct);

            var applied = result.BiologicallyValidate(now, currentUser.UserId,
                                                       qcOverridden, criticalCommunicated);

            if (applied.IsFailure)
            {
                blocked.Add(applied.FirstError);
                continue;
            }

            validated++;
        }

        // Advance each order item whose results are now all released.
        foreach (var item in order.Items.Where(i => !i.IsCancelled))
        {
            var itemResults = stored.Where(r => r.LabOrderItemId == item.Id).ToList();
            if (itemResults.Count == 0) continue;

            if (itemResults.All(r => r.Status is TestResultStatus.Validated
                                            or TestResultStatus.Reported
                                            or TestResultStatus.NotDone))
                item.MarkBiologicallyValidated(now, currentUser.UserId);
        }

        order.RecomputeStatus();

        unitOfWork.AuditReason =
            $"Validation biologique — dossier {order.AccessionNumber} ({validated} résultat(s))";
        await unitOfWork.SaveChangesAsync(ct);

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.ViewOrder, order.PatientId,
                                     order.Id, "BiologicalValidation", validated, ct: ct);

        return Result.Success(new ValidationOutcome(
            validated, refused, blocked,
            order.Status is LabOrderStatus.Validated or LabOrderStatus.Reported));
    }

    /// <summary>
    /// Withdraws a validation before the report has left the laboratory.
    /// <para>
    /// Once a report has been delivered this path is closed: the corrected value must be issued as
    /// an amended report, because the recipient may already have acted on the figure they hold.
    /// Silently un-validating would leave the laboratory and the clinician with different truths.
    /// </para>
    /// </summary>
    public async Task<Result> UnvalidateAsync(Guid orderId, IReadOnlyList<Guid> resultIds,
                                               string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.Unvalidate);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Item_UnvalidationReasonRequired"));

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound"));

        var stored = await queries.GetResultsForOrderAsync(orderId, null, ct);
        var selected = stored.Where(r => resultIds.Contains(r.Id)).ToList();

        if (selected.Count == 0) return Result.Failure(Error.NotFound("Result_NotFound", orderId));

        var errors = new List<Error>();

        foreach (var result in selected)
        {
            var item = order.Items.FirstOrDefault(i => i.Id == result.LabOrderItemId);

            if (item?.ReportedAt is not null)
            {
                errors.Add(Error.Regulatory("Item_AlreadyReportedRequiresAmendment"));
                continue;
            }

            // Revise with no new value: the figure stands, but the release is withdrawn and the
            // reason recorded, so the result returns to the bench for a second look.
            var revised = result.Revise(result.NumericValue, result.TextValue, result.CodedValue,
                                        clock.Now, currentUser.UserId, reason);

            if (revised.IsFailure)
            {
                errors.Add(revised.FirstError);
                continue;
            }

            item?.Unvalidate(reason);
        }

        order.RecomputeStatus();

        unitOfWork.AuditReason = $"Dévalidation — dossier {order.AccessionNumber} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    // ---- Critical results -----------------------------------------------------------------------

    /// <summary>
    /// Opens the communication record for a critical value. Created when the value is detected
    /// rather than when the call is made, so an uncommunicated panic value is visible on a
    /// worklist instead of depending on somebody remembering.
    /// </summary>
    public async Task<Result<Guid>> RaiseCriticalNotificationAsync(
        Guid testResultId, string parameterName, string reportedValue,
        NotificationChannel channel, string? contactUsed, Guid? prescriberId,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.EnterCritical);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var result = await validationQueries.GetResultAsync(testResultId, ct);
        if (result is null) return Result.Failure<Guid>(Error.NotFound("Result_NotFound", testResultId));

        var attemptNumber = await validationQueries.CountNotificationAttemptsAsync(testResultId, ct) + 1;

        var notification = new CriticalResultNotification(
            testResultId, result.LabOrderId, currentUser.SiteId,
            reportedValue, clock.Now, currentUser.UserId)
        {
            ParameterName = parameterName,
            Channel = channel,
            ContactUsed = contactUsed,
            PrescriberId = prescriberId,
            AttemptNumber = attemptNumber
        };

        await notifications.AddAsync(notification, ct);

        unitOfWork.AuditReason = $"Résultat critique signalé : {parameterName} = {reportedValue}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(notification.Id);
    }

    /// <summary>
    /// Closes a critical-result communication. Requires the recipient's identity and confirmation
    /// that they read the value back; without the read-back the notification stays open and the
    /// result stays unreleasable.
    /// </summary>
    public async Task<Result> AcknowledgeCriticalNotificationAsync(
        Guid notificationId, string recipientName, string? recipientRole,
        bool readBackConfirmed, string? readBackValue, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.EnterCritical);
        if (permitted.IsFailure) return permitted;

        var notification = await notifications.GetByIdAsync(notificationId, ct);
        if (notification is null)
            return Result.Failure(Error.NotFound("Notification_NotFound", notificationId));

        var acknowledged = notification.Acknowledge(clock.Now, recipientName, recipientRole,
                                                     readBackConfirmed, readBackValue);
        if (acknowledged.IsFailure) return acknowledged;

        unitOfWork.AuditReason =
            $"Résultat critique communiqué à {recipientName} avec relecture confirmée";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> MarkCriticalUnreachableAsync(Guid notificationId, string notes,
                                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.EnterCritical);
        if (permitted.IsFailure) return permitted;

        var notification = await notifications.GetByIdAsync(notificationId, ct);
        if (notification is null)
            return Result.Failure(Error.NotFound("Notification_NotFound", notificationId));

        notification.MarkUnreachable(clock.Now, notes);

        unitOfWork.AuditReason = "Résultat critique — interlocuteur injoignable";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Maps each order item to its discipline code, which is what the per-department validation
    /// scope is expressed in.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadDepartmentCodesAsync(LabOrder order,
                                                                          CancellationToken ct)
    {
        var codes = new Dictionary<Guid, string>();
        var byAnalysis = new Dictionary<Guid, string>();

        foreach (var analysisId in order.Items.Select(i => i.AnalysisTestId).Distinct())
        {
            var test = await analyses.GetByIdWithDetailsAsync(analysisId, ct);
            if (test?.Department is not null) byAnalysis[analysisId] = test.Department.Code;
        }

        foreach (var item in order.Items)
            if (byAnalysis.TryGetValue(item.AnalysisTestId, out var code))
                codes[item.Id] = code;

        return codes;
    }
}

/// <summary>
/// Read-side queries the validation workflow needs. Separated from <see cref="IResultQueries"/>
/// because these cross into QC and notifications, and the worklist projection is shaped for one
/// screen rather than for the domain.
/// </summary>
public interface IValidationQueries
{
    Task<IReadOnlyList<ValidationWorklistEntry>> GetValidationWorklistAsync(
        Guid siteId, Guid? departmentId, IReadOnlySet<string> authorizedDepartmentCodes,
        bool onlyUrgent, DateTimeOffset now, CancellationToken ct = default);

    Task<TestResult?> GetResultAsync(Guid testResultId, CancellationToken ct = default);

    /// <summary>
    /// True when a critical value has been communicated <em>and</em> read back. Both conditions
    /// matter: a call nobody confirmed is not a communication as far as ISO 15189 is concerned.
    /// </summary>
    Task<bool> IsCriticalCommunicatedAsync(Guid testResultId, CancellationToken ct = default);

    Task<int> CountNotificationAttemptsAsync(Guid testResultId, CancellationToken ct = default);

    /// <summary>Records a justified override on the QC run covering a result.</summary>
    Task<Result> RecordQcOverrideAsync(Guid? qcRunId, string justification,
                                        CancellationToken ct = default);
}
