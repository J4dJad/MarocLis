using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Orders;

/// <summary>The order as a whole, for the file screen.</summary>
public sealed record OrderDetail(
    Guid OrderId,
    string AccessionNumber,
    LabOrderStatus Status,
    OrderPriority Priority,
    OrderOrigin Origin,
    DateTimeOffset RegisteredAt,
    string RegisteredByName,
    DateTimeOffset? DueAt,
    Guid PatientId,
    string PatientName,
    string? PatientNameArabic,
    DateOnly? DateOfBirth,
    Gender Gender,
    string? NationalId,
    string MedicalRecordNumber,
    string? PhoneNumber,
    Guid? PrescriberId,
    string? PrescriberName,
    string? PrescriberInpe,
    string? PrescriberFreeText,
    DateOnly? PrescriptionDate,
    string? PayerName,
    decimal? ReimbursementRate,
    string? PriorAgreementNumber,
    string? ClinicalInformation,
    string? DiagnosisCode,
    bool? PatientFasted,
    int? DeclaredFastingHours,
    string? WardOrUnit,
    string? RoomOrBed,
    string? InternalNote,
    AppLanguage ReportLanguage,
    Guid? InvoiceId,
    string? InvoiceNumber,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    IReadOnlyList<OrderItemDetail> Items,
    IReadOnlyList<OrderSampleDetail> Samples)
{
    public bool IsCancelled => Status == LabOrderStatus.Cancelled;

    /// <summary>Invoiced orders can still be amended clinically; the invoice is corrected by a
    /// credit note, never by editing the issued document.</summary>
    public bool IsInvoiced => InvoiceId is not null;

    public int LiveItemCount => Items.Count(i => !i.IsCancelled);

    public decimal ChargedTotal => Items.Where(i => !i.IsCancelled).Sum(i => i.ChargedPrice);
    public decimal PatientShareTotal => Items.Where(i => !i.IsCancelled).Sum(i => i.PatientShare);

    /// <summary>
    /// Whether the whole order can still be cancelled. Mirrors <see cref="LabOrder.Cancel"/> so the
    /// button is disabled rather than offered and then refused, but the domain remains the authority.
    /// </summary>
    public bool CanCancel => Status != LabOrderStatus.Cancelled
                             && Items.All(i => i.Status != LabOrderItemStatus.Validated
                                               && i.Status != LabOrderItemStatus.Reported);
}

public sealed record OrderItemDetail(
    Guid ItemId,
    Guid AnalysisTestId,
    string Code,
    MultilingualText Name,
    string? DepartmentCode,
    LabOrderItemStatus Status,
    OrderPriority Priority,
    decimal ChargedPrice,
    decimal PayerShare,
    decimal PatientShare,
    string? BilledLetterKey,
    decimal? BilledCoefficient,
    Guid? SampleId,
    string? SampleBarcode,
    DateTimeOffset? DueAt,
    DateTimeOffset? ValidatedAt,
    string? ValidatedByName,
    int RunCount,
    string? RepeatReason,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    bool IsSubcontracted,
    int ResultCount,
    int EnteredResultCount)
{
    public bool IsCancelled => Status == LabOrderItemStatus.Cancelled;

    /// <summary>Mirrors <see cref="LabOrder.CancelItem"/>: a validated result is amended, not hidden.</summary>
    public bool CanCancel => Status is not (LabOrderItemStatus.Validated
                                            or LabOrderItemStatus.Reported
                                            or LabOrderItemStatus.Cancelled);

    /// <summary>Mirrors <see cref="LabOrderItem.Repeat"/>.</summary>
    public bool CanRepeat => Status is not (LabOrderItemStatus.Validated
                                            or LabOrderItemStatus.Reported
                                            or LabOrderItemStatus.Cancelled);

    /// <summary>Mirrors <see cref="LabOrderItem.IsOverdue"/>.</summary>
    public bool IsOverdueAt(DateTimeOffset now) =>
        DueAt is { } due && now > due
        && Status is not (LabOrderItemStatus.Validated or LabOrderItemStatus.Reported
                                                       or LabOrderItemStatus.Cancelled);
}

public sealed record OrderSampleDetail(
    Guid SampleId,
    string Barcode,
    MultilingualText SpecimenTypeName,
    string? ContainerName,
    string? ContainerColourHex,
    SampleStatus Status,
    int SequenceInOrder,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    string? StorageLocation,
    int AnalysisCount);

/// <summary>
/// One order in the file screen's list: enough to recognise the file and pick it, nothing more.
/// The full file is loaded only for the row the operator selects.
/// </summary>
public sealed record OrderListRow(
    Guid OrderId,
    string AccessionNumber,
    string PatientName,
    DateTimeOffset RegisteredAt,
    LabOrderStatus Status,
    OrderPriority Priority,
    int AnalysisCount);

/// <summary>What the file screen can change about an order's header.</summary>
public sealed record AmendOrderHeaderRequest
{
    public required Guid OrderId { get; init; }

    public Guid? PrescriberId { get; init; }
    public string? PrescriberFreeText { get; init; }
    public DateOnly? PrescriptionDate { get; init; }

    public OrderPriority Priority { get; init; }
    public string? ClinicalInformation { get; init; }
    public string? DiagnosisCode { get; init; }
    public string? WardOrUnit { get; init; }
    public string? RoomOrBed { get; init; }
    public string? InternalNote { get; init; }
    public AppLanguage ReportLanguage { get; init; }
}

/// <summary>Outcome of adding analyses: what was added, what it costs, and what needs printing.</summary>
public sealed record OrderAmendment(
    IReadOnlyList<SampleLabel> Labels,
    OrderPricing Pricing,
    IReadOnlyList<Error> Warnings);

/// <summary>
/// Everything that happens to an order after it is registered: adding an analysis, cancelling one,
/// sending one back to the bench, correcting the header, cancelling the file.
/// <para>
/// This exists because a laboratory order is not written once. The prescriber telephones to add a
/// TSH while the tube is still in the rack; a clerk types the wrong prescriber; an analyser gives an
/// implausible result and the analysis has to be re-run; a patient walks out before the draw.
/// Without a path for each of those, the operator's only recourse is to register a second order —
/// which splits one episode of care across two file numbers, bills the patient twice for the same
/// draw, and makes the turnaround statistics meaningless.
/// </para>
/// <para>
/// Every method here records a reason. Not for tidiness: ISO 15189 requires alterations to a
/// request to be traceable to who made them and why, and an unexplained cancellation is
/// indistinguishable from a concealed error.
/// </para>
/// </summary>
public sealed class OrderAmendmentService(
    IRepository<LabOrder> orders,
    IRepository<Patient> patients,
    IRepository<Sample> samples,
    OrderComposer composer,
    IOrderQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor accessAuditor,
    IClock clock,
    IRepository<TestResult> results)
{
    // ---- Reading -------------------------------------------------------------------------------

    public async Task<Result<OrderDetail>> GetDetailAsync(Guid orderId,
                                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.View);
        if (permitted.IsFailure) return Result.Failure<OrderDetail>(permitted.FirstError);

        var detail = await queries.GetDetailAsync(orderId, currentUser.SiteId, ct);

        if (detail is null)
            return Result.Failure<OrderDetail>(Error.NotFound("Order_NotFound", orderId));

        // Opening a file is a consultation of a medical record, and loi 08-09 makes consultations
        // auditable in their own right — not only the changes made afterwards.
        await accessAuditor.LogAsync(DataAccessKind.ViewOrder, detail.PatientId, orderId,
                                     "OrderDetail", ct: ct);

        return Result.Success(detail);
    }

    /// <summary>
    /// Resolves an accession number or a tube barcode to an order on the current site.
    /// <para>
    /// Both, because what the operator is holding is as often a tube as a paper file, and making
    /// them work out which of the two numbers the box wants is friction for no gain.
    /// </para>
    /// </summary>
    public async Task<Result<Guid?>> ResolveOrderIdAsync(string term,
                                                          CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.View);
        if (permitted.IsFailure) return Result.Failure<Guid?>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(term)) return Result.Success<Guid?>(null);

        return Result.Success(
            await queries.ResolveOrderIdAsync(term.Trim(), currentUser.SiteId, ct));
    }

    /// <summary>
    /// The most recent orders of the current site, newest first, optionally filtered by accession
    /// number, patient name, CIN or file number.
    /// <para>
    /// The file screen opens on this list rather than on an empty search box: an operator who is
    /// asked about "the file from this morning" rarely has its number to hand, and a screen that
    /// shows nothing until an exact number is typed reads as broken. Capped, because the list is
    /// for picking a recent file, not for browsing the archive — a search narrows it to the rest.
    /// </para>
    /// <para>
    /// Not logged as a consultation: the list shows who has a file, not what is in it. Opening a
    /// row goes through <see cref="GetDetailAsync"/>, which is.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<OrderListRow>>> SearchOrdersAsync(
        string? term, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<OrderListRow>>(permitted.FirstError);

        return Result.Success(await queries.SearchAsync(
            string.IsNullOrWhiteSpace(term) ? null : term.Trim(), currentUser.SiteId,
            IOrderQueries.ListPageSize, ct));
    }

    // ---- Adding analyses -----------------------------------------------------------------------

    /// <summary>
    /// Adds analyses to an existing order, priced against the coverage the order was registered
    /// under.
    /// <para>
    /// Priced against the <b>order's</b> coverage rather than the patient's current one: an
    /// addition belongs to the episode of care that was opened at the counter, and re-resolving
    /// coverage would let a mutuelle that lapsed yesterday change the price of an act performed on
    /// a tube drawn last week.
    /// </para>
    /// <para>
    /// A tube already drawn for the same specimen and container is reused rather than replaced. If
    /// that tube has already been received the addition still succeeds, with a warning: whether
    /// there is enough serum left is a question only the person holding the tube can answer, and
    /// refusing here would send the patient for a second needle they may not need.
    /// </para>
    /// </summary>
    public async Task<Result<OrderAmendment>> AddAnalysesAsync(
        Guid orderId, IReadOnlyList<Guid> analysisTestIds, IReadOnlyList<Guid> testPanelIds,
        string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Edit);
        if (permitted.IsFailure) return Result.Failure<OrderAmendment>(permitted.FirstError);

        if (analysisTestIds.Count == 0 && testPanelIds.Count == 0)
            return Result.Failure<OrderAmendment>(
                Error.Validation("Order_AtLeastOneAnalysisRequired"));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<OrderAmendment>(Error.Validation("Order_AmendmentReasonRequired"));

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure<OrderAmendment>(Error.NotFound("Order_NotFound", orderId));

        if (order.Status == LabOrderStatus.Cancelled)
            return Result.Failure<OrderAmendment>(Error.Conflict("Order_CancelledCannotAmend"));

        if (order.IsClosed)
            return Result.Failure<OrderAmendment>(Error.Conflict("Order_ClosedCannotAddItem"));

        // An order carries one invoice. An act added after it was issued would be on none: the
        // order no longer reaches the billing desk, and an issued invoice is never edited. The
        // addition goes on a new order, or the invoice is credited in full first, which frees the
        // order for a new invoice that includes it.
        if (order.InvoiceId is not null)
            return Result.Failure<OrderAmendment>(
                Error.Conflict("Order_InvoicedCannotAddItem", order.AccessionNumber));

        var patient = await patients.GetByIdWithDetailsAsync(order.PatientId, ct);
        if (patient is null)
            return Result.Failure<OrderAmendment>(Error.NotFound("Order_PatientNotFound"));

        var resolved = await composer.ResolveAsync(analysisTestIds, testPanelIds, ct);
        if (resolved.IsFailure) return Result.Failure<OrderAmendment>(resolved.Errors);

        var selected = resolved.Value;

        // Already on the order and not cancelled: rejected here with the code named, rather than
        // letting AddItem refuse it as a warning the operator has to go looking for.
        var duplicates = selected
            .Where(a => order.Items.Any(i => i.AnalysisTestId == a.Test.Id && !i.IsCancelled))
            .Select(a => a.Test.Code)
            .ToList();

        if (duplicates.Count > 0)
            return Result.Failure<OrderAmendment>(
                Error.Conflict("Order_AnalysisAlreadyPresent", string.Join(", ", duplicates)));

        var today = clock.Today;
        var warnings = new List<Error>();

        // The consent gate applies to an addition exactly as it does to a registration. This is the
        // reason the resolution and pricing code was pulled into OrderComposer: an addition that
        // bypassed this check would be a genetic test performed without written consent.
        var consentFailures = OrderComposer.CheckConsents(patient, selected, today);
        if (consentFailures.Count > 0) return Result.Failure<OrderAmendment>(consentFailures);

        warnings.AddRange(OrderComposer.CheckFasting(order.PatientFasted,
                                                      order.DeclaredFastingHours, selected));

        var coverage = await composer.RegisteredCoverageAsync(order, ct);

        var tariffFailures = OrderComposer.CheckTariffs(selected, order.PayerId, today);
        if (tariffFailures.Count > 0) return Result.Failure<OrderAmendment>(tariffFailures);

        var pricing = ActPricingCalculator.Price(
            OrderComposer.BuildPricingInputs(selected, order.PayerId, today),
            coverage,
            order.DiscountPercent ?? 0m);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var added = new List<LabOrderItem>(selected.Count);

        foreach (var priced in pricing.Acts)
        {
            var analysis = selected.First(a => a.Test.Id == priced.Input.AnalysisTestId);

            // An added analysis inherits the order's priority: a file registered as urgent stays
            // urgent, and a routine file does not become urgent because of an afterthought.
            var attached = order.AddItem(OrderComposer.BuildItem(priced, analysis, order.Priority));

            if (attached.IsFailure) { warnings.Add(attached.FirstError); continue; }

            added.Add(attached.Value);
        }

        if (added.Count == 0)
        {
            if (warnings.Count == 0)
                warnings.Add(Error.Validation("Order_AtLeastOneAnalysisRequired"));

            return Result.Failure<OrderAmendment>(warnings);
        }

        var existingSamples = await samples.ListAsync(s => s.LabOrderId == order.Id, ct);

        var reusable = existingSamples
            .Where(s => s.Status is not (SampleStatus.Rejected or SampleStatus.Discarded))
            .OrderBy(s => s.SequenceInOrder)
            .ToList();

        var labels = await composer.CreateSamplesAsync(order, patient, selected, reusable, ct);

        // Warn per reused tube that is already in the laboratory: the operator has to go and look
        // at it, and that is a different job from taking the labels to the draw room.
        foreach (var sample in reusable.Where(s => s.ReceivedAt is not null))
        {
            if (added.Any(i => i.SampleId == sample.Id))
                warnings.Add(Error.Validation("Order_AddedOnReceivedSample", sample.Barcode));
        }

        await composer.CreateAwaitedResultsAsync(order, selected, added, ct);

        // The promise moves out only if the addition is slower than what was already promised —
        // adding a fast analysis must not make an existing urgent one look less late.
        var promised = composer.PromisedCompletion(selected, order.IsUrgent);
        if (order.DueAt is null || promised > order.DueAt) order.DueAt = promised;

        order.RecomputeStatus();

        unitOfWork.AuditReason =
            $"Ajout de {added.Count} analyse(s) au dossier {order.AccessionNumber} : {reason.Trim()}";

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(new OrderAmendment(labels, pricing, warnings));
    }

    // ---- Cancelling ----------------------------------------------------------------------------

    /// <summary>
    /// Cancels one analysis. The order's status is recomputed, so a file whose last live analysis
    /// is cancelled becomes cancelled rather than sitting in a worklist forever.
    /// </summary>
    public async Task<Result> CancelItemAsync(Guid orderId, Guid itemId, string reason,
                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Cancel);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Order_CancellationReasonRequired"));

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound", orderId));

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return Result.Failure(Error.NotFound("Order_ItemNotFound"));

        var code = item.AnalysisTest?.Code ?? item.AnalysisTestId.ToString();

        var cancelled = order.CancelItem(itemId, clock.Now, currentUser.UserId, reason.Trim());
        if (cancelled.IsFailure) return cancelled;

        unitOfWork.AuditReason =
            $"Annulation de l'analyse {code} du dossier {order.AccessionNumber} : {reason.Trim()}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Cancels the whole file.
    /// <para>
    /// Refused once anything has been biologically validated — a validated result has left the
    /// laboratory's sole control and is corrected by an amended report, not by making the file
    /// disappear. An invoiced order can be cancelled clinically; the money is put right by a credit
    /// note, because an issued invoice is immutable under the CGI.
    /// </para>
    /// </summary>
    public async Task<Result> CancelOrderAsync(Guid orderId, string reason,
                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Cancel);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Order_CancellationReasonRequired"));

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound", orderId));

        var cancelled = order.Cancel(clock.Now, currentUser.UserId, reason.Trim());
        if (cancelled.IsFailure) return cancelled;

        // The tubes go with the file. A tube left awaiting collection for a cancelled order is a
        // patient who gets called to the draw room for analyses nobody is going to run.
        var tubes = await samples.ListAsync(s => s.LabOrderId == order.Id, ct);

        foreach (var tube in tubes)
        {
            if (tube.Status is SampleStatus.Discarded or SampleStatus.Rejected) continue;

            var discarded = tube.Discard(clock.Now, currentUser.UserId,
                                          $"Dossier annulé : {reason.Trim()}");

            // A tube that refuses to be discarded — one already in analysis — is left alone rather
            // than forced: the file is cancelled either way, and the tube's own history stays true.
            if (discarded.IsFailure) continue;
        }

        unitOfWork.AuditReason = $"Annulation du dossier {order.AccessionNumber} : {reason.Trim()}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Repeating -----------------------------------------------------------------------------

    /// <summary>
    /// Sends an analysis back to the bench, keeping the run history.
    /// <para>
    /// A repeat is not a correction and not a new order: the same act, on the same tube, run again
    /// because the first run is not trustworthy — a suspect value, a flagged analyser run, a
    /// dilution needed. The run count is what later shows the analysis was run twice, which matters
    /// when a reviewer asks why a result took four hours.
    /// </para>
    /// <para>
    /// Not billed a second time. The laboratory owns the reason it did not trust its own first run,
    /// and a patient charged twice for one act would be right to complain.
    /// </para>
    /// </summary>
    public async Task<Result> RepeatItemAsync(Guid orderId, Guid itemId, string reason,
                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Results.Enter);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Item_RepeatReasonRequired"));

        var order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound", orderId));

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return Result.Failure(Error.NotFound("Order_ItemNotFound"));

        var runResults = await results.ListAsync(r => r.LabOrderItemId == item.Id, ct);

        // A value a biologist released stays released until its validation is withdrawn, which
        // takes the right to withdraw it. Repeating around it left the released figure printing
        // while the new run's values were refused — a technician undoing a biologist's release.
        if (runResults.Any(r => r.IsValidated))
            return Result.Failure(Error.Conflict("Item_RepeatRequiresUnvalidation"));

        // Checked values are being set aside: the same right as changing one.
        if ((item.Status == LabOrderItemStatus.TechnicallyValidated
             || runResults.Any(r => r.Status == TestResultStatus.TechnicallyValidated))
            && !currentUser.Has(Permissions.Results.Modify))
            return Result.Failure(Error.Forbidden("Result_ModifyRequiresPermission",
                                                  item.AnalysisTest?.Code ?? string.Empty));

        var repeated = item.Repeat(reason.Trim(), clock.Now);
        if (repeated.IsFailure) return repeated;

        // The first run's values stay as its record until the new run replaces them, but their
        // technical validation goes: left in place, the biologist could release them one by one,
        // since the analysis-level check only runs when a batch completes the analysis.
        foreach (var result in runResults)
        {
            var reopened = result.ReopenForRepeat();
            if (reopened.IsFailure) return reopened;
        }

        order.RecomputeStatus();

        unitOfWork.AuditReason =
            $"Reprise de l'analyse {item.AnalysisTest?.Code ?? string.Empty} "
            + $"du dossier {order.AccessionNumber} : {reason.Trim()}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Header --------------------------------------------------------------------------------

    /// <summary>
    /// Corrects the order's header: prescriber, prescription date, priority, clinical information,
    /// ward, report language, internal note.
    /// <para>
    /// The prescriber is the field this exists for. It is captured at a counter from a handwritten
    /// prescription, it is wrong often, and it is printed on the report and carried onto the AMO
    /// claim under the prescriber's INPE — so a wrong one is a claim rejected weeks later for a
    /// reason nobody traces back to a typing error at reception.
    /// </para>
    /// <para>
    /// Coverage is deliberately not editable here. Changing the payer after registration would
    /// change the split on acts that are already priced and possibly already invoiced; that is a
    /// credit note and a re-issue, not a field edit.
    /// </para>
    /// </summary>
    public async Task<Result> AmendHeaderAsync(AmendOrderHeaderRequest request, string reason,
                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Edit);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Order_AmendmentReasonRequired"));

        var order = await orders.GetByIdWithDetailsAsync(request.OrderId, ct);
        if (order is null) return Result.Failure(Error.NotFound("Order_NotFound", request.OrderId));

        if (order.Status == LabOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Order_CancelledCannotAmend"));

        // Raising priority is a privileged act for the same reason it is at registration: urgent
        // work jumps the queue, and a queue everybody can jump is not a queue.
        if (request.Priority != order.Priority && request.Priority != OrderPriority.Routine)
        {
            var urgent = guard.Require(Permissions.Orders.AddUrgent);
            if (urgent.IsFailure) return urgent;
        }

        if (request.PrescriberId is null && string.IsNullOrWhiteSpace(request.PrescriberFreeText)
            && LabOrder.RequiresPrescriber(order.Origin))
            return Result.Failure(Error.Validation("Order_PrescriberRequired"));

        order.PrescriberId = request.PrescriberId;
        order.PrescriberFreeText = string.IsNullOrWhiteSpace(request.PrescriberFreeText)
            ? null
            : request.PrescriberFreeText.Trim();
        order.PrescriptionDate = request.PrescriptionDate;
        order.ClinicalInformation = Trimmed(request.ClinicalInformation);
        order.DiagnosisCode = Trimmed(request.DiagnosisCode);
        order.WardOrUnit = Trimmed(request.WardOrUnit);
        order.RoomOrBed = Trimmed(request.RoomOrBed);
        order.InternalNote = Trimmed(request.InternalNote);
        order.ReportLanguage = request.ReportLanguage;

        if (request.Priority != order.Priority)
        {
            order.Priority = request.Priority;

            // Items keep their own priority for the bench worklist, so it moves with the order's.
            foreach (var item in order.Items.Where(i => !i.IsCancelled))
                item.Priority = request.Priority;
        }

        unitOfWork.AuditReason =
            $"Modification du dossier {order.AccessionNumber} : {reason.Trim()}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();

        static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public interface IOrderQueries
{
    /// <summary>
    /// The whole file: header, items with their analysis and sample, and the tubes. Assembled in
    /// the query rather than by walking the aggregate, because the screen needs names the aggregate
    /// does not carry — the prescriber's, the operator's, the payer's.
    /// <para>
    /// Scoped to the site, like every other read in the application. On a multi-site installation
    /// an order belongs to the site that registered it, and an operator at another branch has no
    /// care relationship with that patient — which is the test loi 08-09 applies to access.
    /// </para>
    /// </summary>
    Task<OrderDetail?> GetDetailAsync(Guid orderId, Guid siteId, CancellationToken ct = default);

    /// <summary>
    /// Resolves an accession number or a tube barcode to an order on the given site. Exact match
    /// only; returns null when nothing matches.
    /// </summary>
    Task<Guid?> ResolveOrderIdAsync(string term, Guid siteId, CancellationToken ct = default);

    /// <summary>How many rows the file screen's list shows at most.</summary>
    const int ListPageSize = 200;

    /// <summary>
    /// The site's orders, newest first, at most <paramref name="take"/> of them. With a term, only
    /// those whose accession number, patient name (through the patient's search key, so accents and
    /// case do not matter), CIN or file number contains it.
    /// </summary>
    Task<IReadOnlyList<OrderListRow>> SearchAsync(string? term, Guid siteId,
                                                  int take = ListPageSize,
                                                  CancellationToken ct = default);
}
