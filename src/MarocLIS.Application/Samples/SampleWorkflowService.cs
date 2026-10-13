using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Samples;

/// <summary>One tube on the collection worklist, with everything the phlebotomist needs to draw it.</summary>
public sealed record CollectionWorklistEntry
{
    public required Guid SampleId { get; init; }
    public required Guid LabOrderId { get; init; }
    public required string Barcode { get; init; }
    public required string AccessionNumber { get; init; }

    public required string PatientName { get; init; }
    public string? PatientNameArabic { get; init; }
    public Gender Gender { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public int? AgeYears { get; init; }

    public OrderPriority Priority { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }

    public int SequenceInOrder { get; init; }
    public int TotalSamplesInOrder { get; init; }

    public required MultilingualText SpecimenTypeName { get; init; }

    /// <summary>When the tube was drawn; null while it is still waiting to be.</summary>
    public DateTimeOffset? CollectedAt { get; init; }

    public bool IsCollected => CollectedAt is not null;
    public MultilingualText? ContainerName { get; init; }
    public string ContainerColourHex { get; init; } = "#FFFFFF";

    /// <summary>Order of draw, so a multi-tube collection avoids additive carry-over.</summary>
    public int DrawOrder { get; init; }

    public decimal? NominalVolumeMl { get; init; }
    public decimal? MinimumVolumeMl { get; init; }

    /// <summary>Hours of fasting the strictest analysis on this tube requires; 0 when none.</summary>
    public int RequiredFastingHours { get; init; }

    public bool? PatientDeclaredFasted { get; init; }

    /// <summary>Instructions the phlebotomist must follow, in their language.</summary>
    public MultilingualText? CollectionInstructions { get; init; }

    /// <summary>Analyses carried by this tube, for the label and for the draw check.</summary>
    public IReadOnlyList<string> AnalysisCodes { get; init; } = [];

    /// <summary>Clinical or logistic flags on the patient file — biohazard, difficult draw.</summary>
    public IReadOnlyList<MultilingualText> PatientAlerts { get; init; } = [];

    public bool RequiresLightProtection { get; init; }
    public bool RequiresIceTransport { get; init; }
    public CollectionLocation CollectionLocation { get; init; }
}

/// <summary>What the phlebotomist records at the chair.</summary>
public sealed record RecordCollectionRequest
{
    public required Guid SampleId { get; init; }
    public decimal? VolumeMl { get; init; }
    public int CollectionAttempts { get; init; } = 1;
    public string? CollectionSite { get; init; }
    public string? Note { get; init; }

    /// <summary>Null keeps whatever the order's origin implied.</summary>
    public CollectionLocation? Location { get; init; }

    /// <summary>Declared at the chair, which is the only place it can honestly be asked.</summary>
    public bool? PatientFasted { get; init; }
    public int? DeclaredFastingHours { get; init; }
}

/// <summary>One tube at the reception bench, with the pre-analytical facts needed to accept it.</summary>
public sealed record SampleReceptionCandidate
{
    public required Guid SampleId { get; init; }
    public required Guid LabOrderId { get; init; }
    public required string Barcode { get; init; }
    public required string AccessionNumber { get; init; }
    public required string PatientName { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public Gender Gender { get; init; }
    public OrderPriority Priority { get; init; }

    public SampleStatus Status { get; init; }
    public DateTimeOffset? CollectedAt { get; init; }
    public DateTimeOffset? DispatchedAt { get; init; }

    /// <summary>Time since the draw. The figure the bench judges acceptability on.</summary>
    public TimeSpan? Elapsed { get; init; }

    public required MultilingualText SpecimenTypeName { get; init; }
    public MultilingualText? ContainerName { get; init; }
    public decimal? VolumeMl { get; init; }
    public decimal? MinimumVolumeMl { get; init; }

    public SampleDefect ExistingDefects { get; init; }

    public IReadOnlyList<string> AnalysisCodes { get; init; } = [];

    /// <summary>
    /// The shortest stability window among the analyses on this tube, in hours. Null when none of
    /// them declares one.
    /// </summary>
    public int? ShortestStabilityHours { get; init; }

    /// <summary>
    /// True when the elapsed transport time has already exceeded the shortest stability window.
    /// The bench is told, rather than left to work it out from two timestamps.
    /// </summary>
    public bool StabilityExceeded { get; init; }

    public bool RequiresColdChain { get; init; }
}

public sealed record ReceiveSampleRequest
{
    public required Guid SampleId { get; init; }
    public SampleDefect ObservedDefects { get; init; } = SampleDefect.None;

    /// <summary>
    /// Must be set explicitly when defects were observed. The domain refuses a silent acceptance,
    /// so this is a decision the operator makes rather than a flag the UI can default.
    /// </summary>
    public bool AcceptWithReservation { get; init; }

    public decimal? TemperatureCelsius { get; init; }
    public bool? ColdChainRespected { get; init; }
    public string? Note { get; init; }

    /// <summary>Where the tube goes after reception, when it is not going straight to the bench.</summary>
    public string? StorageLocation { get; init; }
    public string? StoragePosition { get; init; }
}

public sealed record RejectSampleRequest(
    Guid SampleId,
    SampleRejectionReason Reason,
    string? Comment,
    bool RequestRecollection);

public sealed record ReceptionOutcome(
    int Received,
    int Rejected,
    IReadOnlyList<Error> Problems,
    IReadOnlyList<SampleLabel> ReplacementLabels);

/// <summary>
/// The pre-analytical phase: collection, dispatch, reception and rejection.
/// <para>
/// This is the part of a laboratory an ISO 15189 assessor examines most closely, because most
/// avoidable errors happen here rather than on the analyser. Three behaviours follow from that and
/// are not negotiable in the code below:
/// </para>
/// <list type="bullet">
/// <item>Every movement produces a <see cref="SampleEvent"/> — who, when, from which workstation,
/// at what temperature. The chain of custody is reconstructable for any tube.</item>
/// <item>A defective sample cannot be received silently. The operator either rejects it or accepts
/// it with a recorded reservation that the report then discloses.</item>
/// <item>A rejection always carries a reason from a closed list, because the rejection rate by
/// reason is a mandatory quality indicator and free text cannot be counted.</item>
/// </list>
/// </summary>
public sealed class SampleWorkflowService(
    IRepository<Sample> samples,
    IRepository<LabOrder> orders,
    IRepository<AnalysisTest> analyses,
    IRepository<Patient> patients,
    ISampleQueries queries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor accessAuditor,
    IClock clock,
    OrderRegistrationOptions options)
{
    // ---- Collection ----------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<CollectionWorklistEntry>>> GetCollectionWorklistAsync(
        bool onlyUrgent = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<CollectionWorklistEntry>>(permitted.FirstError);

        var entries = await queries.GetCollectionWorklistAsync(
            currentUser.SiteId, onlyUrgent, clock.Today, ct);

        return Result.Success(entries);
    }

    /// <summary>
    /// Records a draw.
    /// <para>
    /// The declared fasting is written back onto the order as well as the sample: the phlebotomist
    /// at the chair is the only person who can honestly ask, and the biologist interpreting a
    /// glucose later needs the answer.
    /// </para>
    /// </summary>
    public async Task<Result> RecordCollectionAsync(IReadOnlyList<RecordCollectionRequest> requests,
                                                     CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Collect);
        if (permitted.IsFailure) return permitted;

        if (requests.Count == 0) return Result.Failure(Error.Validation("Message.NothingSelected"));

        var problems = new List<Error>();
        var now = clock.Now;
        var touchedOrders = new HashSet<Guid>();

        foreach (var request in requests)
        {
            var sample = await samples.GetByIdWithDetailsAsync(request.SampleId, ct);

            if (sample is null)
            {
                problems.Add(Error.NotFound("Sample_NotFound", request.SampleId));
                continue;
            }

            var minimumVolume = await queries.GetMinimumVolumeAsync(sample.Id, ct);

            var collected = sample.Collect(now, currentUser.UserId, request.VolumeMl, minimumVolume);

            if (collected.IsFailure)
            {
                problems.Add(collected.FirstError);
                continue;
            }

            sample.CollectionAttempts = Math.Max(1, request.CollectionAttempts);
            sample.CollectionSite = request.CollectionSite;
            sample.CollectionNote = request.Note;
            if (request.Location is { } location) sample.CollectionLocation = location;

            // Advance the order items bound to this tube.
            var order = await orders.GetByIdWithDetailsAsync(sample.LabOrderId, ct);

            if (order is not null)
            {
                foreach (var item in order.Items.Where(i => i.SampleId == sample.Id && !i.IsCancelled))
                {
                    var marked = item.MarkCollected(now);
                    if (marked.IsFailure) problems.Add(marked.FirstError);
                }

                if (request.PatientFasted is { } fasted)
                {
                    order.PatientFasted = fasted;
                    order.DeclaredFastingHours = request.DeclaredFastingHours;
                }

                order.MarkFirstCollection(now);
                touchedOrders.Add(order.Id);
            }
        }

        unitOfWork.AuditReason = $"Prélèvement de {requests.Count} tube(s)";
        await unitOfWork.SaveChangesAsync(ct);

        return problems.Count == 0 ? Result.Success() : Result.Failure(problems);
    }

    /// <summary>
    /// Marks tubes as dispatched to the laboratory, which is what starts the transport-delay clock
    /// that the reception bench then judges acceptability against.
    /// </summary>
    public async Task<Result> DispatchAsync(IReadOnlyList<Guid> sampleIds, Guid? transportRunId = null,
                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Dispatch);
        if (permitted.IsFailure) return permitted;

        var problems = new List<Error>();
        var now = clock.Now;

        foreach (var sampleId in sampleIds)
        {
            var sample = await samples.GetByIdWithDetailsAsync(sampleId, ct);

            if (sample is null)
            {
                problems.Add(Error.NotFound("Sample_NotFound", sampleId));
                continue;
            }

            var dispatched = sample.Dispatch(now, currentUser.UserId, transportRunId);
            if (dispatched.IsFailure) problems.Add(dispatched.FirstError);
        }

        unitOfWork.AuditReason = $"Expédition de {sampleIds.Count} tube(s)";
        await unitOfWork.SaveChangesAsync(ct);

        return problems.Count == 0 ? Result.Success() : Result.Failure(problems);
    }

    /// <summary>
    /// Records a label reprint.
    /// <para>
    /// Logged as its own event because a duplicate label in circulation is a real safety risk: two
    /// tubes bearing one barcode is the mechanism by which one patient's sample is reported under
    /// another patient's name.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<SampleLabel>>> ReprintLabelsAsync(
        Guid labOrderId, string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Reprint);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SampleLabel>>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<IReadOnlyList<SampleLabel>>(
                Error.Validation("Sample_ReprintReasonRequired"));

        var labels = await queries.GetLabelsForOrderAsync(labOrderId, clock.Now, ct);

        if (labels.Count == 0)
            return Result.Failure<IReadOnlyList<SampleLabel>>(Error.NotFound("Sample_NotFound", labOrderId));

        foreach (var label in labels)
        {
            var sample = await samples.GetByIdWithDetailsAsync(label.SampleId, ct);

            sample?.AddEvent(SampleEventKind.LabelReprinted, clock.Now, currentUser.UserId,
                             reason, workstation: currentUser.Workstation);
        }

        unitOfWork.AuditReason = $"Réimpression d'étiquettes : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(labels);
    }

    // ---- Reception -----------------------------------------------------------------------------

    /// <summary>
    /// Resolves a scanned barcode to the tube in front of the operator.
    /// <para>
    /// Exact, case-sensitive match. A partial or fuzzy barcode match is capable of binding one
    /// patient's tube to another patient's order, which is the worst defect this software could
    /// have — so the lookup is deliberately unforgiving and an unknown barcode is an error the
    /// operator has to resolve rather than something the system guesses at.
    /// </para>
    /// </summary>
    public async Task<Result<SampleReceptionCandidate>> ScanAsync(string barcode,
                                                                   CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Receive);
        if (permitted.IsFailure) return Result.Failure<SampleReceptionCandidate>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(barcode))
            return Result.Failure<SampleReceptionCandidate>(Error.Validation("Sample.Barcode"));

        var candidate = await queries.GetReceptionCandidateAsync(barcode.Trim(), clock.Now, ct);

        if (candidate is null)
            return Result.Failure<SampleReceptionCandidate>(
                Error.NotFound("Sample_BarcodeUnknown", barcode.Trim()));

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.ViewOrder, null,
                                     candidate.LabOrderId, "SampleReception", ct: ct);

        return Result.Success(candidate);
    }

    /// <summary>
    /// Tubes drawn or in transit and not yet received.
    /// <para>
    /// The bench needs its own list as well as the scanner: a tube that was drawn at a collection
    /// centre and never arrived is invisible to a scanning loop, and this is where it shows up.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<SampleReceptionCandidate>>> GetAwaitingReceptionAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<SampleReceptionCandidate>>(permitted.FirstError);

        var awaiting = await queries.GetAwaitingReceptionAsync(currentUser.SiteId, clock.Now, ct);

        return Result.Success(awaiting);
    }

    public async Task<Result<ReceptionOutcome>> ReceiveAsync(
        IReadOnlyList<ReceiveSampleRequest> requests, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Receive);
        if (permitted.IsFailure) return Result.Failure<ReceptionOutcome>(permitted.FirstError);

        var problems = new List<Error>();
        var received = 0;
        var now = clock.Now;

        foreach (var request in requests)
        {
            var sample = await samples.GetByIdWithDetailsAsync(request.SampleId, ct);

            if (sample is null)
            {
                problems.Add(Error.NotFound("Sample_NotFound", request.SampleId));
                continue;
            }

            var accepted = sample.Receive(now, currentUser.UserId, request.ObservedDefects,
                                           request.AcceptWithReservation,
                                           request.TemperatureCelsius, request.Note);

            if (accepted.IsFailure)
            {
                problems.Add(accepted.FirstError);
                continue;
            }

            sample.ColdChainRespected = request.ColdChainRespected;

            if (!string.IsNullOrWhiteSpace(request.StorageLocation))
            {
                sample.MoveToStorage(now, currentUser.UserId, request.StorageLocation,
                                      request.StoragePosition,
                                      clock.Today.AddDays(options.SampleRetentionDays));
            }

            // Advance the order items and set their promised completion, which only becomes
            // meaningful once the sample is actually on the platform.
            var order = await orders.GetByIdWithDetailsAsync(sample.LabOrderId, ct);

            if (order is not null)
            {
                foreach (var item in order.Items.Where(i => i.SampleId == sample.Id && !i.IsCancelled))
                {
                    var test = await analyses.GetByIdAsync(item.AnalysisTestId, ct);

                    var dueAt = test?.ExpectedCompletion(now, order.IsUrgent);

                    var marked = item.MarkReceived(now, dueAt);
                    if (marked.IsFailure) problems.Add(marked.FirstError);
                }

                // The order's promise is the latest of its items'.
                var latest = order.Items.Where(i => !i.IsCancelled && i.DueAt is not null)
                                        .Select(i => i.DueAt!.Value)
                                        .DefaultIfEmpty(order.DueAt ?? now)
                                        .Max();

                order.DueAt = latest;
                order.MarkAllReceived(now);
            }

            received++;
        }

        unitOfWork.AuditReason = $"Réception de {received} tube(s)";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new ReceptionOutcome(received, 0, problems, []));
    }

    /// <summary>
    /// Refuses a tube.
    /// <para>
    /// The analyses bound to it are cancelled with the rejection as their reason, so the order does
    /// not sit indefinitely waiting for work that will never be done — and when a recollection is
    /// requested, a fresh tube with a new barcode is created and its label returned. Reusing the
    /// rejected tube's barcode is never an option.
    /// </para>
    /// </summary>
    public async Task<Result<ReceptionOutcome>> RejectAsync(
        IReadOnlyList<RejectSampleRequest> requests, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Reject);
        if (permitted.IsFailure) return Result.Failure<ReceptionOutcome>(permitted.FirstError);

        var problems = new List<Error>();
        var replacementLabels = new List<SampleLabel>();
        var rejected = 0;
        var now = clock.Now;

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        foreach (var request in requests)
        {
            var sample = await samples.GetByIdWithDetailsAsync(request.SampleId, ct);

            if (sample is null)
            {
                problems.Add(Error.NotFound("Sample_NotFound", request.SampleId));
                continue;
            }

            var refused = sample.Reject(now, currentUser.UserId, request.Reason,
                                         request.Comment, request.RequestRecollection);

            if (refused.IsFailure)
            {
                problems.Add(refused.FirstError);
                continue;
            }

            var order = await orders.GetByIdWithDetailsAsync(sample.LabOrderId, ct);

            if (order is not null)
            {
                var reasonText = $"Échantillon refusé : {request.Reason}" +
                                 (string.IsNullOrWhiteSpace(request.Comment)
                                     ? string.Empty
                                     : $" — {request.Comment}");

                var affected = order.Items
                    .Where(i => i.SampleId == sample.Id && !i.IsCancelled
                                && i.Status != LabOrderItemStatus.Validated)
                    .Select(i => i.Id)
                    .ToList();

                foreach (var itemId in affected)
                {
                    var cancelled = order.CancelItem(itemId, now, currentUser.UserId, reasonText);
                    if (cancelled.IsFailure) problems.Add(cancelled.FirstError);
                }

                if (request.RequestRecollection)
                {
                    var replacement = await CreateReplacementAsync(order, sample, affected, ct);

                    if (replacement.IsSuccess) replacementLabels.Add(replacement.Value);
                    else problems.Add(replacement.FirstError);
                }
            }

            rejected++;
        }

        unitOfWork.AuditReason = $"Refus de {rejected} échantillon(s)";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(new ReceptionOutcome(0, rejected, problems, replacementLabels));
    }

    /// <summary>
    /// Creates a fresh tube for a recollection, with a new barcode, and re-opens the analyses the
    /// rejection cancelled.
    /// </summary>
    private async Task<Result<SampleLabel>> CreateReplacementAsync(
        LabOrder order, Sample rejected, IReadOnlyList<Guid> cancelledItemIds, CancellationToken ct)
    {
        var barcode = await sequences.NextAsync(SequencePurpose.SampleBarcode, null, null, ct);
        if (barcode.IsFailure) return Result.Failure<SampleLabel>(barcode.FirstError);

        var replacement = new Sample(barcode.Value, order.Id, rejected.SpecimenTypeId,
                                      currentUser.SiteId)
        {
            ContainerTypeId = rejected.ContainerTypeId,
            SequenceInOrder = rejected.SequenceInOrder,
            CollectionLocation = rejected.CollectionLocation,
            KeepUntil = clock.Today.AddDays(options.SampleRetentionDays)
        };

        replacement.AddEvent(SampleEventKind.LabelPrinted, clock.Now, currentUser.UserId,
                             $"Remplacement de {rejected.Barcode}",
                             workstation: currentUser.Workstation);

        await samples.AddAsync(replacement, ct);

        rejected.LinkReplacement(replacement.Id);

        // Re-request the cancelled analyses against the new tube. A cancelled item is not revived —
        // its cancellation is part of the record — so a new item is added instead.
        var analysisIds = order.Items
            .Where(i => cancelledItemIds.Contains(i.Id))
            .Select(i => (i.AnalysisTestId, i.DepartmentId, i.ChargedPrice, i.ReferenceTariffBase,
                          i.PayerShare, i.PatientShare, i.BilledLetterKey, i.BilledCoefficient,
                          i.BilledActCode))
            .ToList();

        foreach (var source in analysisIds)
        {
            var reopened = new LabOrderItem(source.AnalysisTestId, order.Priority)
            {
                SampleId = replacement.Id,
                DepartmentId = source.DepartmentId,
                // The patient is not charged twice for a tube the laboratory refused.
                ChargedPrice = 0m,
                ReferenceTariffBase = source.ReferenceTariffBase,
                PayerShare = 0m,
                PatientShare = 0m,
                BilledLetterKey = source.BilledLetterKey,
                BilledCoefficient = source.BilledCoefficient,
                BilledActCode = source.BilledActCode
            };

            order.AddItem(reopened);
        }

        order.RecomputeStatus();

        var patient = await patients.GetByIdAsync(order.PatientId, ct);
        var container = await queries.GetContainerDescriptionAsync(replacement.ContainerTypeId, ct);

        return Result.Success(new SampleLabel(
            replacement.Id,
            replacement.Barcode,
            patient?.FullName ?? string.Empty,
            patient?.FullNameArabic,
            patient?.DateOfBirth,
            order.AccessionNumber,
            container?.Name ?? string.Empty,
            container?.ColourHex ?? "#FFFFFF",
            replacement.SequenceInOrder,
            replacement.SequenceInOrder,
            [.. analysisIds.Select(a => a.BilledActCode ?? string.Empty).Where(c => c.Length > 0)],
            order.IsUrgent,
            clock.Now));
    }

    // ---- Storage and disposal ------------------------------------------------------------------

    /// <summary>
    /// The serum bank: everything received and not yet discarded.
    /// <para>
    /// Reading a tube's location is a personal-data access — the row carries a patient name — so it
    /// is logged like any other consultation under loi n° 08-09.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<StoredSampleRow>>> GetStoredSamplesAsync(
        string? search = null, bool onlyExpired = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<StoredSampleRow>>(permitted.FirstError);

        var rows = await queries.GetStoredSamplesAsync(currentUser.SiteId, search, onlyExpired,
                                                        clock.Today, ct);

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.Search, recordCount: rows.Count,
                                      queryDescription: "Sérothèque", ct: ct);

        return Result.Success(rows);
    }

    /// <summary>
    /// Re-shelves a tube, or sets its serum-bank retention.
    /// <para>
    /// Storage could previously only be set as a by-product of reception, so a tube moved from the
    /// bench to the serum bank — which is what happens to every tube at the end of a run — had no
    /// way to say where it went. A sample whose recorded position is wrong is a sample that cannot
    /// be retrieved for an add-on request, which is the whole reason a laboratory keeps it.
    /// </para>
    /// </summary>
    public async Task<Result> MoveToStorageAsync(Guid sampleId, string location, string? position,
                                                  DateOnly? keepUntil,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Receive);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(location))
            return Result.Failure(Error.Validation("Sample_StorageLocationRequired"));

        var sample = await samples.GetByIdWithDetailsAsync(sampleId, ct);
        if (sample is null) return Result.Failure(Error.NotFound("Sample_NotFound", sampleId));

        var moved = sample.MoveToStorage(clock.Now, currentUser.UserId, location.Trim(),
                                          string.IsNullOrWhiteSpace(position) ? null : position.Trim(),
                                          keepUntil);
        if (moved.IsFailure) return moved;

        unitOfWork.AuditReason =
            $"Échantillon {sample.Barcode} rangé en {location.Trim()}"
            + (position is null ? string.Empty : $" / {position.Trim()}");

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Discards a tube once its retention has run out.
    /// <para>
    /// A serum bank that is never purged fills up, and a tube that is physically in the bin but
    /// still recorded as stored sends a technician hunting for it when an add-on request arrives.
    /// The note is kept because discarding a tube ends the possibility of ever repeating an
    /// analysis on it.
    /// </para>
    /// </summary>
    public async Task<Result> DiscardAsync(Guid sampleId, string note,
                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Receive);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(note))
            return Result.Failure(Error.Validation("Sample_DiscardNoteRequired"));

        var sample = await samples.GetByIdWithDetailsAsync(sampleId, ct);
        if (sample is null) return Result.Failure(Error.NotFound("Sample_NotFound", sampleId));

        var discarded = sample.Discard(clock.Now, currentUser.UserId, note.Trim());
        if (discarded.IsFailure) return discarded;

        unitOfWork.AuditReason = $"Élimination de l'échantillon {sample.Barcode} : {note.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Splits an aliquot off a received tube, with its own barcode from the sample series.
    /// <para>
    /// A laboratory aliquots constantly — one draw feeding a local analyser, a subcontracted send
    /// and a serum-bank retain. Each fraction needs its own barcode, because they end up in
    /// different places and the traceability has to follow the fraction, not the parent draw.
    /// </para>
    /// </summary>
    public async Task<Result<string>> CreateAliquotAsync(Guid sampleId, decimal? volumeMl,
                                                          string? purpose, string? storageLocation,
                                                          CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Samples.Receive);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        var sample = await samples.GetByIdWithDetailsAsync(sampleId, ct);
        if (sample is null) return Result.Failure<string>(Error.NotFound("Sample_NotFound", sampleId));

        if (sample.Status is SampleStatus.Rejected or SampleStatus.Discarded)
            return Result.Failure<string>(Error.Conflict("Sample_CannotAliquotFromStatus",
                                                           sample.Status));

        if (sample.CollectedAt is null)
            return Result.Failure<string>(Error.Validation("Sample_NotCollected"));

        // The barcode comes from the same gapless series as the parent tube, inside a transaction,
        // so an abandoned aliquot does not burn a barcode that a real tube then never carries.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var barcode = await sequences.NextAsync(SequencePurpose.SampleBarcode, currentUser.SiteId,
                                                 ct: ct);

        if (barcode.IsFailure) return Result.Failure<string>(barcode.FirstError);

        var aliquot = sample.CreateAliquot(barcode.Value, volumeMl, clock.Now, currentUser.UserId);

        aliquot.Purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();
        aliquot.StorageLocation = string.IsNullOrWhiteSpace(storageLocation)
            ? sample.StorageLocation
            : storageLocation.Trim();

        unitOfWork.AuditReason =
            $"Aliquot {aliquot.Barcode} créé depuis l'échantillon {sample.Barcode}";

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(aliquot.Barcode);
    }
}

/// <summary>Read-side queries for the pre-analytical screens.</summary>
public interface ISampleQueries
{
    Task<IReadOnlyList<CollectionWorklistEntry>> GetCollectionWorklistAsync(
        Guid siteId, bool onlyUrgent, DateOnly today, CancellationToken ct = default);

    Task<SampleReceptionCandidate?> GetReceptionCandidateAsync(
        string barcode, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Tubes drawn or in transit and not yet received, for the reception bench's own list.</summary>
    Task<IReadOnlyList<SampleReceptionCandidate>> GetAwaitingReceptionAsync(
        Guid siteId, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<SampleLabel>> GetLabelsForOrderAsync(
        Guid labOrderId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Minimum acceptable volume for the tube's container, for the under-fill check.</summary>
    Task<decimal?> GetMinimumVolumeAsync(Guid sampleId, CancellationToken ct = default);

    Task<ContainerDescription?> GetContainerDescriptionAsync(Guid? containerTypeId,
                                                              CancellationToken ct = default);

    /// <summary>
    /// Tubes on the platform or in the serum bank — everything received and not yet discarded.
    /// <para>
    /// This is the list the bench needs to answer two questions a paper rack cannot: where is a
    /// given tube, and which ones are past their retention and can be purged.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<StoredSampleRow>> GetStoredSamplesAsync(
        Guid siteId, string? search, bool onlyExpired, DateOnly today,
        CancellationToken ct = default);
}

/// <summary>One tube as the storage screen lists it.</summary>
public sealed record StoredSampleRow(
    Guid SampleId,
    string Barcode,
    string AccessionNumber,
    string PatientName,
    string SpecimenTypeName,
    SampleStatus Status,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    string? StorageLocation,
    string? StoragePosition,
    DateOnly? KeepUntil,
    int? DaysToKeepUntil,
    decimal? VolumeMl,
    int AliquotCount,
    bool HasDefects,
    bool IsAcceptedWithReservation)
{
    /// <summary>Past its retention: eligible for purging, which is why the screen exists.</summary>
    public bool IsPastRetention => DaysToKeepUntil is < 0;

    /// <summary>Received but never shelved — the tube nobody can find.</summary>
    public bool IsUnshelved =>
        Status == SampleStatus.Received && string.IsNullOrWhiteSpace(StorageLocation);
}

public sealed record ContainerDescription(string Name, string ColourHex);
