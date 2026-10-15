using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Inventory;

// ---- Read models ----------------------------------------------------------------------------------

/// <summary>A stock item a GTIN designates.</summary>
public sealed record GtinMatch(Guid StockItemId, string Code, MultilingualText Name);

/// <summary>Everything the QC gate needs about one lot.</summary>
public sealed record LotQcEvidence(
    Guid ReagentLotId,
    string LotNumber,
    string ItemCode,
    StockItemKind Kind,
    IReadOnlyList<string> AnalysisCodes,
    IReadOnlyList<RequiredQcLevel> RequiredLevels,
    IReadOnlyList<LotQcPoint> Points);

/// <summary>Accepted control values on a lot and on the lot it replaces.</summary>
public sealed record LotComparisonData(
    string ItemCode,
    string NewLotNumber,
    string? PreviousLotNumber,
    IReadOnlyList<QcValueOnLot> NewLotValues,
    IReadOnlyList<QcValueOnLot> PreviousLotValues);

/// <summary>The comparison as the dialog shows it.</summary>
public sealed record LotComparisonView(
    string ItemCode,
    string NewLotNumber,
    string? PreviousLotNumber,
    IReadOnlyList<LotComparisonRow> Rows);

/// <summary>A patient result produced with a lot: one line of a recall list.</summary>
public sealed record RecallResultRow(
    Guid TestResultId,
    Guid PatientId,
    string PatientName,
    string MedicalRecordNumber,
    Guid LabOrderId,
    string AccessionNumber,
    string AnalysisCode,
    string ParameterCode,
    DateTimeOffset? ResultDate,
    bool IsValidated);

public sealed record LotCertificateRow(Guid Id, string FileName, DateTimeOffset AddedAt);

public sealed record LotCertificateContent(string FileName, string ContentType, byte[] Content);

/// <summary>A supplier's year, as read from the database, before the indicators are computed.</summary>
public sealed record SupplierEvaluationInput(
    Guid SupplierId,
    string Code,
    string Name,
    bool IsApproved,
    int? CurrentScore,
    DateOnly? LastEvaluationDate,
    string? RegistrationNumber,
    DateOnly? RegistrationValidUntil,
    IReadOnlyList<SupplierDelivery> Deliveries,
    int RefusedDeliveries,
    int NonConformities);

/// <summary>One supplier with the year's computed indicators.</summary>
public sealed record SupplierEvaluationRow(
    Guid SupplierId,
    string Code,
    string Name,
    bool IsApproved,
    int? CurrentScore,
    DateOnly? LastEvaluationDate,
    string? RegistrationNumber,
    DateOnly? RegistrationValidUntil,
    bool RegistrationExpired,
    SupplierKpis Kpis);

/// <summary>A lot a control run can be recorded against: one under evaluation, or one in use.</summary>
public sealed record ReagentLotOption(Guid ReagentLotId, string ItemCode, string LotNumber,
                                      DateOnly ExpiryDate, bool IsQuarantined);

public sealed record SupplierRegistration(string Name, string? Number, DateOnly? ValidUntil);

public sealed record RefuseDeliveryRequest
{
    public Guid? SupplierId { get; init; }
    public Guid? PurchaseOrderId { get; init; }
    public Guid? StockItemId { get; init; }
    public required string Reason { get; init; }
    public decimal? ArrivalTemperatureCelsius { get; init; }
    public string? DeliveryNoteReference { get; init; }
    public string? LotNumber { get; init; }
}

/// <summary>Read-side queries of the purchasing-quality features.</summary>
public interface IPurchasingQualityQueries
{
    Task<IReadOnlyList<GtinMatch>> FindItemsByGtinAsync(string gtin, Guid siteId, CancellationToken ct = default);

    Task<LotQcEvidence?> GetLotQcEvidenceAsync(Guid reagentLotId, DateOnly today, CancellationToken ct = default);

    Task<LotComparisonData?> GetLotComparisonDataAsync(Guid reagentLotId, CancellationToken ct = default);

    Task<IReadOnlyList<RecallResultRow>> GetResultsProducedWithLotAsync(Guid reagentLotId,
                                                                         CancellationToken ct = default);

    Task<IReadOnlyList<LotCertificateRow>> GetLotCertificatesAsync(Guid reagentLotId, CancellationToken ct = default);

    Task<IReadOnlyList<SupplierEvaluationInput>> GetSupplierEvaluationInputsAsync(
        int year, CancellationToken ct = default);

    Task<SupplierRegistration?> GetSupplierRegistrationAsync(Guid supplierId, CancellationToken ct = default);

    /// <summary>The storage condition of an article, which decides the temperature check at reception.</summary>
    Task<StorageCondition?> GetStorageConditionAsync(Guid stockItemId, CancellationToken ct = default);

    /// <summary>Quarantined (not rejected) reagent lots and opened usable ones, for the QC entry.</summary>
    Task<IReadOnlyList<ReagentLotOption>> GetReagentLotsForQcAsync(Guid siteId, DateOnly today,
                                                                   CancellationToken ct = default);
}

// ---- Reception ------------------------------------------------------------------------------------

/// <summary>
/// The reception side of purchasing quality: the checks made at the door, the non-conformity a
/// failed check opens against the supplier, the refusal of a whole delivery, the GS1 scan and the
/// certificates kept with a lot.
/// </summary>
public sealed class ReceptionService(
    IRepository<NonConformity> nonConformities,
    IRepository<RefusedDelivery> refusals,
    IRepository<PurchaseOrder> orders,
    IRepository<LotCertificateFile> certificates,
    IPurchasingQualityQueries queries,
    IInventoryQueries inventoryQueries,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// Reads a scanned DataMatrix and, when its GTIN is known, the article it designates. An
    /// unknown GTIN is not an error: the lot and expiry are still worth filling in.
    /// </summary>
    public async Task<Result<(Gs1Data Data, GtinMatch? Item)>> ReadScanAsync(string raw,
                                                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Receive);
        if (permitted.IsFailure) return Result.Failure<(Gs1Data, GtinMatch?)>(permitted.FirstError);

        var parsed = Gs1Parser.Parse(raw);
        if (parsed.IsFailure) return Result.Failure<(Gs1Data, GtinMatch?)>(parsed.Errors);

        GtinMatch? item = null;

        if (parsed.Value.Gtin is { } gtin)
        {
            var matches = await queries.FindItemsByGtinAsync(gtin, currentUser.SiteId, ct);
            // Two articles sharing a GTIN is a catalogue fault; neither is picked.
            if (matches.Count == 1) item = matches[0];
        }

        return Result.Success((parsed.Value, item));
    }

    /// <summary>The storage condition the reception dialog checks the temperature against.</summary>
    public async Task<StorageCondition> StorageConditionOfAsync(Guid stockItemId, CancellationToken ct = default) =>
        await queries.GetStorageConditionAsync(stockItemId, ct) ?? StorageCondition.RoomTemperature;

    /// <summary>The registration warning for the supplier of a purchase order, or null.</summary>
    public async Task<Error?> RegistrationWarningForOrderAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        var order = await orders.GetByIdAsync(purchaseOrderId, ct);
        return order is null ? null : await RegistrationWarningAsync(order.SupplierId, ct);
    }

    /// <summary>The article a GTIN designates, when exactly one does.</summary>
    public async Task<GtinMatch?> FindByGtinAsync(string gtin, CancellationToken ct = default)
    {
        if (guard.Require(Permissions.Inventory.Receive).IsFailure) return null;
        var matches = await queries.FindItemsByGtinAsync(gtin, currentUser.SiteId, ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Opens the non-conformity a failed reception check calls for. Allocates the reference and
    /// adds the file; the caller saves, inside the transaction it opened for the number.
    /// </summary>
    public async Task<Result<NonConformity>> OpenReceptionNonConformityAsync(
        StockItem item, ReagentLot? lot, Guid? supplierId, ReceptionAssessment assessment,
        decimal? temperature, CancellationToken ct = default)
    {
        var details = assessment.DescribeFr(item.StorageCondition, temperature);
        var subject = lot is null ? item.Code : $"{item.Code}, lot {lot.LotNumber}";

        return await OpenAsync(
            assessment.Severity,
            new MultilingualText(
                $"Réception non conforme — {subject} : {details}",
                $"استلام غير مطابق — {subject}",
                $"Non-conforming reception — {subject}: {details}"),
            supplierId, lot?.Id,
            "Lot maintenu en quarantaine ; fournisseur à informer.", ct);
    }

    /// <summary>
    /// Refuses a delivery as a whole: nothing enters the stock, the refusal is recorded with the
    /// supplier, the order, the person, the reason and the temperature, and a non-conformity is
    /// opened against the supplier. Returns the non-conformity's reference.
    /// </summary>
    public async Task<Result<string>> RefuseDeliveryAsync(RefuseDeliveryRequest request,
                                                          CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Receive);
        if (permitted.IsFailure) return Result.Failure<string>(permitted.FirstError);

        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Failure<string>(Error.Validation("Reception_RefusalReasonRequired"));

        var supplierId = request.SupplierId;
        string? orderNumber = null;

        if (request.PurchaseOrderId is { } orderId)
        {
            var order = await orders.GetByIdAsync(orderId, ct);
            if (order is null) return Result.Failure<string>(Error.NotFound("Po_NotFound"));
            supplierId ??= order.SupplierId;
            orderNumber = order.Number;
        }

        var reason = request.Reason.Trim();

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var refusal = new RefusedDelivery(currentUser.SiteId, supplierId, request.PurchaseOrderId,
                                          clock.Today, clock.Now, currentUser.UserId, reason)
        {
            StockItemId = request.StockItemId,
            ArrivalTemperatureCelsius = request.ArrivalTemperatureCelsius,
            DeliveryNoteReference = Blank(request.DeliveryNoteReference),
            LotNumber = Blank(request.LotNumber)
        };

        var subject = orderNumber ?? request.DeliveryNoteReference ?? "—";
        var temperature = request.ArrivalTemperatureCelsius is { } t
            ? $" (température à l'arrivée {ReceptionAssessment.Format(t)} °C)"
            : string.Empty;

        var opened = await OpenAsync(
            NonConformitySeverity.Major,
            new MultilingualText(
                $"Livraison refusée à la réception — {subject} : {reason}{temperature}",
                $"تسليم مرفوض عند الاستلام — {subject}",
                $"Delivery refused at reception — {subject}: {reason}"),
            supplierId, null, "Livraison refusée, rien n'est entré en stock.", ct);

        if (opened.IsFailure) return Result.Failure<string>(opened.Errors);

        refusal.NonConformityId = opened.Value.Id;
        await refusals.AddAsync(refusal, ct);

        unitOfWork.AuditReason = $"Refus de la livraison {subject} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(opened.Value.Reference);
    }

    public async Task<Result<IReadOnlyList<LotCertificateRow>>> GetCertificatesAsync(
        Guid reagentLotId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<LotCertificateRow>>(permitted.FirstError);

        return Result.Success(await queries.GetLotCertificatesAsync(reagentLotId, ct));
    }

    /// <summary>Keeps a certificate (analysis, CE declaration) with the lot. Same rules as the HR scans.</summary>
    public async Task<Result> AttachCertificateAsync(Guid reagentLotId, string fileName, byte[] content,
                                                     CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.Receive);
        if (permitted.IsFailure) return permitted;

        // Only the name is kept: the folder says something about the workstation, not the lot.
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        if (name.Length == 0 || content is not { Length: > 0 })
            return Result.Failure(Error.Validation("Credential_FileEmpty"));
        if (content.Length > LotCertificateFile.MaximumBytes)
            return Result.Failure(Error.Validation("Credential_FileTooLarge", LotCertificateFile.MaximumBytes / (1024 * 1024)));
        if (LotCertificateFile.ContentTypeOf(name) is not { } contentType)
            return Result.Failure(Error.Validation("Credential_FileType"));
        if (name.Length > 200) name = name[..(200 - Path.GetExtension(name).Length)] + Path.GetExtension(name);

        var lot = await inventoryQueries.GetLotEntityAsync(reagentLotId, ct);
        if (lot is null) return Result.Failure(Error.NotFound("Lot_NotFound", reagentLotId));

        await certificates.AddAsync(new LotCertificateFile(lot.Id, name, contentType, content), ct);

        unitOfWork.AuditReason = $"Certificat joint au lot {lot.LotNumber} : {name}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<LotCertificateContent>> GetCertificateAsync(Guid certificateId,
                                                                         CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<LotCertificateContent>(permitted.FirstError);

        var file = await certificates.GetByIdAsync(certificateId, ct);
        return file is null
            ? Result.Failure<LotCertificateContent>(Error.NotFound("Lot_CertificateNotFound"))
            : Result.Success(new LotCertificateContent(file.FileName, file.ContentType, file.Content));
    }

    /// <summary>
    /// A warning when the supplier's AMMPS / DMP registration has lapsed, or null. A warning and
    /// not a refusal: the registration may have been renewed and not yet re-entered, and an urgent
    /// reagent order should not wait on the paperwork — but the person ordering has to know.
    /// </summary>
    public async Task<Error?> RegistrationWarningAsync(Guid supplierId, CancellationToken ct = default)
    {
        var registration = await queries.GetSupplierRegistrationAsync(supplierId, ct);

        return registration is { ValidUntil: { } until } && until < clock.Today
            ? Error.Regulatory("Supplier_RegistrationExpired", registration.Name,
                               until.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
            : null;
    }

    private async Task<Result<NonConformity>> OpenAsync(NonConformitySeverity severity,
                                                        MultilingualText description, Guid? supplierId,
                                                        Guid? lotId, string immediateAction,
                                                        CancellationToken ct)
    {
        var reference = await sequences.NextAsync(SequencePurpose.NonConformity, null, clock.Today.Year, ct);
        if (reference.IsFailure) return Result.Failure<NonConformity>(reference.Errors);

        var nonConformity = new NonConformity(reference.Value, currentUser.SiteId,
                                              NonConformityCategory.ExternalProvider, severity,
                                              description, clock.Now, currentUser.UserId)
        {
            DetectionSource = DetectionSource.StaffObservation,
            SupplierId = supplierId,
            ReagentLotId = lotId,
            ImmediateAction = new MultilingualText(immediateAction),
            DueDate = clock.Today.AddDays(14)
        };

        await nonConformities.AddAsync(nonConformity, ct);
        return Result.Success(nonConformity);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// ---- Lot quality: QC gate, comparison, recall ------------------------------------------------------

/// <summary>
/// The quality side of a reagent lot after it is received: whether its QC allows the release, how
/// it compares with the lot before it, and which patient results it produced.
/// </summary>
public sealed class LotQualityService(
    IPurchasingQualityQueries queries,
    IDataAccessAuditor accessAuditor,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<Result<LotQcVerdict>> GetReleaseVerdictAsync(Guid reagentLotId,
                                                                   CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<LotQcVerdict>(permitted.FirstError);

        var evidence = await queries.GetLotQcEvidenceAsync(reagentLotId, clock.Today, ct);
        return evidence is null
            ? Result.Failure<LotQcVerdict>(Error.NotFound("Lot_NotFound", reagentLotId))
            : Result.Success(Verdict(evidence));
    }

    public static LotQcVerdict Verdict(LotQcEvidence evidence) =>
        LotQcGate.Evaluate(LotQcGate.RequiresQc(evidence.Kind, evidence.AnalysisCodes),
                           evidence.RequiredLevels, evidence.Points);

    public async Task<Result<LotComparisonView>> CompareWithPreviousLotAsync(Guid reagentLotId,
                                                                            CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<LotComparisonView>(permitted.FirstError);

        var data = await queries.GetLotComparisonDataAsync(reagentLotId, ct);
        if (data is null) return Result.Failure<LotComparisonView>(Error.NotFound("Lot_NotFound", reagentLotId));

        return Result.Success(new LotComparisonView(data.ItemCode, data.NewLotNumber, data.PreviousLotNumber,
                                                    LotComparison.Compare(data.NewLotValues, data.PreviousLotValues)));
    }

    /// <summary>
    /// The patient results produced with a lot — the list a recall actually needs. Patient data,
    /// so it takes the results permission too, and the access is logged like any patient search.
    /// </summary>
    public async Task<Result<IReadOnlyList<RecallResultRow>>> GetRecallListAsync(Guid reagentLotId,
                                                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<RecallResultRow>>(permitted.FirstError);

        var results = guard.Require(Permissions.Results.View);
        if (results.IsFailure) return Result.Failure<IReadOnlyList<RecallResultRow>>(results.FirstError);

        var rows = await queries.GetResultsProducedWithLotAsync(reagentLotId, ct);

        await accessAuditor.LogAsync(DataAccessKind.Search, context: "LotRecall", recordCount: rows.Count,
                                     queryDescription: $"Résultats produits avec le lot {reagentLotId}", ct: ct);

        return Result.Success(rows);
    }

    /// <summary>Records that the recall list left the application as a file.</summary>
    public Task LogRecallExportAsync(Guid reagentLotId, int count, CancellationToken ct = default) =>
        accessAuditor.LogAsync(DataAccessKind.Export, context: "LotRecall", recordCount: count,
                               queryDescription: $"Export CSV des résultats du lot {reagentLotId}", ct: ct);

    /// <summary>The lots a control run can be recorded against, for the QC entry sheet.</summary>
    public async Task<Result<IReadOnlyList<ReagentLotOption>>> GetLotsForQcAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.EnterQc);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ReagentLotOption>>(permitted.FirstError);

        return Result.Success(await queries.GetReagentLotsForQcAsync(currentUser.SiteId, clock.Today, ct));
    }
}

// ---- Supplier evaluation ---------------------------------------------------------------------------

/// <summary>
/// The computed supplier evaluation: per supplier and per year, deliveries, punctuality, refusals,
/// non-conformities and a proposed score (<see cref="SupplierEvaluation"/>) that the person managing
/// suppliers may apply to the supplier's record.
/// </summary>
public sealed class SupplierEvaluationService(
    IRepository<Supplier> suppliers,
    IPurchasingQualityQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<SupplierEvaluationRow>>> EvaluateAsync(int year,
                                                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SupplierEvaluationRow>>(permitted.FirstError);

        var inputs = await queries.GetSupplierEvaluationInputsAsync(year, ct);
        return Result.Success(Evaluate(inputs, clock.Today));
    }

    public static IReadOnlyList<SupplierEvaluationRow> Evaluate(IEnumerable<SupplierEvaluationInput> inputs,
                                                                DateOnly today) =>
    [
        .. inputs.Select(i => new SupplierEvaluationRow(
                i.SupplierId, i.Code, i.Name, i.IsApproved, i.CurrentScore, i.LastEvaluationDate,
                i.RegistrationNumber, i.RegistrationValidUntil,
                i.RegistrationValidUntil is { } until && until < today,
                SupplierEvaluation.Compute(i.Deliveries, i.RefusedDeliveries, i.NonConformities)))
                 .OrderBy(r => r.Name, StringComparer.CurrentCulture)
    ];

    /// <summary>
    /// Applies a score to the supplier as its evaluation of today. The score is the proposed one or
    /// the evaluator's own judgement; the audit reason keeps both.
    /// </summary>
    public async Task<Result> ApplyScoreAsync(Guid supplierId, int score, int? proposed, int year,
                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return permitted;

        if (score is < 0 or > 100) return Result.Failure(Error.Validation("Supplier_ScoreOutOfRange"));

        var supplier = await suppliers.GetByIdAsync(supplierId, ct);
        if (supplier is null) return Result.Failure(Error.NotFound("Supplier_NotFound"));

        supplier.EvaluationScore = score;
        supplier.LastEvaluationDate = clock.Today;
        suppliers.Update(supplier);

        unitOfWork.AuditReason =
            $"Évaluation {year} du fournisseur {supplier.Code} : note {score}/100" +
            (proposed is { } p ? $" (note calculée {p}/100 — {SupplierEvaluation.FormulaFr})" : string.Empty);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>CSV for the recall list and the annual supplier report: semicolon-separated, with a BOM.</summary>
public static class PurchasingCsv
{
    public static byte[] Recall(IReadOnlyList<RecallResultRow> rows, IReadOnlyList<string> headers)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', headers.Select(Cell)));

        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(';', new[]
            {
                r.PatientName, r.MedicalRecordNumber, r.AccessionNumber, r.AnalysisCode, r.ParameterCode,
                r.ResultDate?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                r.IsValidated ? "oui" : "non"
            }.Select(Cell)));
        }

        return Bytes(csv);
    }

    public static byte[] SupplierReport(IReadOnlyList<SupplierEvaluationRow> rows, IReadOnlyList<string> headers)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', headers.Select(Cell)));

        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(';', SupplierReportCells(r).Select(Cell)));
        }

        return Bytes(csv);
    }

    /// <summary>The cells of one supplier line, shared by the CSV and the PDF.</summary>
    public static IReadOnlyList<string> SupplierReportCells(SupplierEvaluationRow r)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        return
        [
            r.Code, r.Name,
            r.Kpis.Deliveries.ToString(fr),
            r.Kpis.OnTimeRatePercent is { } rate ? rate.ToString("0.#", fr) + " %" : "—",
            r.Kpis.AverageDelayDays is { } delay ? delay.ToString("0.#", fr) : "—",
            r.Kpis.RefusedDeliveries.ToString(fr),
            r.Kpis.NonConformities.ToString(fr),
            r.Kpis.ProposedScore?.ToString(fr) ?? "—",
            r.CurrentScore?.ToString(fr) ?? "—",
            r.RegistrationNumber ?? "—",
            r.RegistrationValidUntil?.ToString("dd/MM/yyyy", fr) ?? "—"
        ];
    }

    private static string Cell(string? value) => CsvCell.Of(value);

    private static byte[] Bytes(StringBuilder csv) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
}
