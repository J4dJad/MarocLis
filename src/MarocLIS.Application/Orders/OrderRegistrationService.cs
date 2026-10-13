using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Pricing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Orders;

/// <summary>What reception captures to register a request.</summary>
public sealed record RegisterOrderRequest
{
    public required Guid PatientId { get; init; }
    public Guid? PrescriberId { get; init; }
    public string? PrescriberFreeText { get; init; }
    public DateOnly? PrescriptionDate { get; init; }

    /// <summary>Analyses chosen individually.</summary>
    public IReadOnlyList<Guid> AnalysisTestIds { get; init; } = [];

    /// <summary>Panels chosen; expanded to their analyses, duplicates collapsed.</summary>
    public IReadOnlyList<Guid> TestPanelIds { get; init; } = [];

    public OrderPriority Priority { get; init; } = OrderPriority.Routine;
    public OrderOrigin Origin { get; init; } = OrderOrigin.Walkin;

    public string? ClinicalInformation { get; init; }
    public string? DiagnosisCode { get; init; }
    public bool? PatientFasted { get; init; }
    public int? DeclaredFastingHours { get; init; }
    public string? WardOrUnit { get; init; }
    public string? RoomOrBed { get; init; }
    public string? InternalNote { get; init; }

    /// <summary>Null lets the patient's primary coverage on the registration date decide.</summary>
    public Guid? PatientCoverageId { get; init; }

    /// <summary>Forces self-pay even when the patient holds a coverage.</summary>
    public bool BillDirectlyToPatient { get; init; }

    public decimal? DiscountPercent { get; init; }
    public string? DiscountReason { get; init; }

    /// <summary>Null takes the patient's preferred language.</summary>
    public AppLanguage? ReportLanguage { get; init; }

    public byte[]? PrescriptionScan { get; init; }
    public string? PrescriptionScanContentType { get; init; }
}

public sealed record RegisteredOrder(
    Guid OrderId,
    string AccessionNumber,
    IReadOnlyList<SampleLabel> Labels,
    OrderPricing Pricing,
    IReadOnlyList<Error> Warnings);

/// <summary>What has to be printed on one barcode label.</summary>
public sealed record SampleLabel(
    Guid SampleId,
    string Barcode,
    string PatientName,
    string? PatientNameArabic,
    DateOnly? DateOfBirth,
    string AccessionNumber,
    string ContainerName,
    string ContainerColourHex,
    int SequenceInOrder,
    int TotalSamples,
    IReadOnlyList<string> AnalysisCodes,
    bool IsUrgent,
    DateTimeOffset PrintedAt);

/// <summary>
/// Registers a request: validates it, prices it, allocates the file number, creates the order and
/// its samples, and pre-creates the awaited result rows.
/// <para>
/// The whole thing runs in one transaction with the number allocation, because a Moroccan
/// laboratory's file and invoice numbering must stay gapless — an order that fails halfway must
/// give its number back.
/// </para>
/// </summary>
public sealed class OrderRegistrationService(
    IRepository<Patient> patients,
    IRepository<LabOrder> orders,
    OrderComposer composer,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor accessAuditor,
    IClock clock,
    OrderRegistrationOptions options)
{
    public async Task<Result<RegisteredOrder>> RegisterAsync(RegisterOrderRequest request,
                                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Orders.Create);
        if (permitted.IsFailure) return Result.Failure<RegisteredOrder>(permitted.FirstError);

        if (request.Priority != OrderPriority.Routine)
        {
            var urgent = guard.Require(Permissions.Orders.AddUrgent);
            if (urgent.IsFailure) return Result.Failure<RegisteredOrder>(urgent.FirstError);
        }

        var patient = await patients.GetByIdWithDetailsAsync(request.PatientId, ct);
        if (patient is null)
            return Result.Failure<RegisteredOrder>(Error.NotFound("Order_PatientNotFound"));

        // Resolve the analyses: explicit choices plus every panel's mandatory members, de-duplicated.
        var resolved = await composer.ResolveAsync(request.AnalysisTestIds, request.TestPanelIds, ct);
        if (resolved.IsFailure) return Result.Failure<RegisteredOrder>(resolved.FirstError);

        var selected = resolved.Value;
        if (selected.Count == 0)
            return Result.Failure<RegisteredOrder>(Error.Validation("Order_AtLeastOneAnalysisRequired"));

        var today = clock.Today;
        var warnings = new List<Error>();

        var consentFailures = OrderComposer.CheckConsents(patient, selected, today);
        if (consentFailures.Count > 0)
            return Result.Failure<RegisteredOrder>(consentFailures);

        warnings.AddRange(OrderComposer.CheckFasting(request.PatientFasted,
                                                      request.DeclaredFastingHours, selected));

        var coverage = await composer.ResolveCoverageAsync(
            patient, request.PatientCoverageId, request.BillDirectlyToPatient, today, ct);

        var tariffFailures = OrderComposer.CheckTariffs(selected, coverage.PayerId, today);
        if (tariffFailures.Count > 0) return Result.Failure<RegisteredOrder>(tariffFailures);

        var pricing = ActPricingCalculator.Price(
            OrderComposer.BuildPricingInputs(selected, coverage.PayerId, today),
            coverage.Context,
            request.DiscountPercent ?? 0m);

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var accessionNumber = await sequences.NextAsync(
            SequencePurpose.Accession, currentUser.SiteId, today.Year, ct);

        if (accessionNumber.IsFailure)
            return Result.Failure<RegisteredOrder>(accessionNumber.FirstError);

        var order = new LabOrder(accessionNumber.Value, patient.Id, currentUser.SiteId,
                                 clock.Now, currentUser.UserId)
        {
            PrescriberId = request.PrescriberId,
            PrescriberFreeText = request.PrescriberFreeText,
            PrescriptionDate = request.PrescriptionDate,
            PrescriptionScan = request.PrescriptionScan,
            PrescriptionScanContentType = request.PrescriptionScanContentType,
            Priority = request.Priority,
            Origin = request.Origin,
            WardOrUnit = request.WardOrUnit,
            RoomOrBed = request.RoomOrBed,
            ClinicalInformation = request.ClinicalInformation,
            DiagnosisCode = request.DiagnosisCode,
            CurrentTreatment = patient.CurrentTreatment,
            PatientFasted = request.PatientFasted,
            DeclaredFastingHours = request.DeclaredFastingHours,
            InternalNote = request.InternalNote,
            ReportLanguage = request.ReportLanguage ?? patient.PreferredLanguage,
            PayerId = coverage.PayerId,
            PatientCoverageId = coverage.CoverageId,
            ReimbursementRateAtRegistration = coverage.Context.IsCovered
                ? coverage.Context.ReimbursementRate
                : null,
            PriorAgreementNumber = coverage.PriorAgreementNumber,
            DiscountPercent = request.DiscountPercent,
            DiscountReason = request.DiscountReason
        };

        // Attach the priced acts as order items, carrying the frozen price and coverage split.
        foreach (var priced in pricing.Acts)
        {
            var analysis = selected.First(a => a.Test.Id == priced.Input.AnalysisTestId);
            var added = order.AddItem(OrderComposer.BuildItem(priced, analysis, request.Priority));
            if (added.IsFailure) warnings.Add(added.FirstError);
        }

        var registrationValid = order.ValidateForRegistration();
        if (registrationValid.IsFailure)
            return Result.Failure<RegisteredOrder>(registrationValid.Errors);

        // Promised completion, from now rather than from reception, because reception has not
        // happened yet at registration.
        order.DueAt = composer.PromisedCompletion(selected, order.IsUrgent);

        await orders.AddAsync(order, ct);

        var labels = await composer.CreateSamplesAsync(order, patient, selected, ct: ct);

        await composer.CreateAwaitedResultsAsync(order, selected, order.Items, ct);

        // Restart the retention clock on the last act, per the medical-record retention obligation.
        patient.ExtendRetention(today, options.MedicalRecordRetentionYears);

        unitOfWork.AuditReason = $"Enregistrement du dossier {order.AccessionNumber}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        await accessAuditor.LogAsync(Domain.Audit.DataAccessKind.ViewPatient, patient.Id, order.Id,
                                     "OrderRegistration", ct: ct);

        return Result.Success(new RegisteredOrder(order.Id, order.AccessionNumber, labels,
                                                   pricing, warnings));
    }
}

/// <summary>
/// Registration policy a laboratory sets once. Retention in particular is a legal minimum rather
/// than a preference, so it is configuration with a documented default rather than a constant.
/// </summary>
public sealed class OrderRegistrationOptions
{
    public const string SectionName = "OrderRegistration";

    /// <summary>
    /// Years a patient file is retained after the last act. Moroccan practice for medical records
    /// is a minimum of ten years; a laboratory may keep longer but not shorter.
    /// </summary>
    public int MedicalRecordRetentionYears { get; set; } = 10;

    /// <summary>Days a primary sample is kept in the serum bank for possible re-analysis.</summary>
    public int SampleRetentionDays { get; set; } = 7;

    /// <summary>Whether a possible duplicate patient blocks registration or only warns.</summary>
    public bool BlockOnSuspectedDuplicate { get; set; }
}
