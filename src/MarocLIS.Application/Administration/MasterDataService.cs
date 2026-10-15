using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Administration;

// ---- Requests ----------------------------------------------------------------------------------

public sealed record InstrumentRequest
{
    public Guid? InstrumentId { get; init; }

    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }
    public EquipmentKind Kind { get; init; }

    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? SoftwareVersion { get; init; }
    public string? Location { get; init; }
    public Guid? DepartmentId { get; init; }

    public DateOnly? PurchaseDate { get; init; }
    public DateOnly? CommissioningDate { get; init; }

    public Guid? SupplierId { get; init; }
    public string? MaintenanceContractReference { get; init; }
    public DateOnly? MaintenanceContractValidUntil { get; init; }
    public string? SupportPhone { get; init; }

    /// <summary>
    /// The three intervals that drive every overdue indicator. Left null they never fire — which is
    /// why the calibration guard on <see cref="Instrument.CanProduceResults"/> was inert.
    /// </summary>
    public int? PreventiveMaintenanceIntervalDays { get; init; }
    public DateOnly? LastPreventiveMaintenanceOn { get; init; }
    public int? CalibrationIntervalDays { get; init; }
    public DateOnly? LastCalibrationOn { get; init; }
    public int? VerificationIntervalDays { get; init; }
    public DateOnly? LastVerificationOn { get; init; }
}

public sealed record QcMaterialRequest
{
    public Guid? QcMaterialId { get; init; }

    public required string LotNumber { get; init; }
    public required MultilingualText Name { get; init; }
    public QcLevel Level { get; init; }
    public required DateOnly ExpiryDate { get; init; }

    public string? Manufacturer { get; init; }
    public string? CatalogueReference { get; init; }
    public DateOnly? OpenedOn { get; init; }
    public int? StabilityAfterOpeningDays { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record QcTargetRequest
{
    public Guid? QcTargetId { get; init; }

    public required Guid QcMaterialId { get; init; }
    public required Guid TestParameterId { get; init; }
    public required Guid InstrumentId { get; init; }

    /// <summary>From the manufacturer's insert. The starting limits until the laboratory has its own.</summary>
    public decimal? AssignedMean { get; init; }
    public decimal? AssignedSd { get; init; }

    public decimal? MaxAcceptableCvPercent { get; init; }
    public WestgardRule ActiveRules { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record StockItemRequest
{
    public Guid? StockItemId { get; init; }

    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }
    public StockItemKind Kind { get; init; }
    public required string UnitOfMeasure { get; init; }

    public int? TestsPerPack { get; init; }
    public Guid? PreferredSupplierId { get; init; }
    public string? SupplierCatalogueReference { get; init; }
    public string? Manufacturer { get; init; }
    public Guid? DepartmentId { get; init; }
    public string? UsedByAnalysisCodesCsv { get; init; }

    public decimal ReorderPoint { get; init; }
    public decimal ReorderQuantity { get; init; }
    public decimal MinimumStock { get; init; }
    public decimal? MaximumStock { get; init; }

    public StorageCondition StorageCondition { get; init; }
    public string? DefaultStorageLocation { get; init; }

    public bool IsHazardous { get; init; }
    public string? HazardClass { get; init; }

    /// <summary>A stock-out here stops an analysis being offered, which has to be declared.</summary>
    public bool IsCritical { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>GS1 GTIN; 8, 12, 13 or 14 digits, stored as fourteen.</summary>
    public string? Gtin { get; init; }

    public bool IsCeMarked { get; init; }

    /// <summary>Consumption and computed-reorder settings; left as they are when null.</summary>
    public StockConsumptionPolicy? ConsumptionPolicy { get; init; }
}

public sealed record SupplierRequest
{
    public Guid? SupplierId { get; init; }

    public required string Code { get; init; }
    public required string Name { get; init; }

    public string? Ice { get; init; }
    public string? FiscalId { get; init; }
    public string? CommercialRegisterNumber { get; init; }

    public string? ContactName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? AddressLine { get; init; }
    public string? City { get; init; }
    public MoroccanRegion Region { get; init; }

    public int? LeadTimeDays { get; init; }
    /// <summary>Loi n° 69-21: 60 days when nothing was agreed, at most 120 by agreement.</summary>
    public int PaymentTermsDays { get; init; } = PaymentTerms.DefaultDays;

    /// <summary>Auxiliary account in the accounting export; the supplier code when empty.</summary>
    public string? AccountingAccount { get; init; }

    /// <summary>ISO 15189 §4.6 expects suppliers of critical items to be evaluated and approved.</summary>
    public bool IsApproved { get; init; }
    public DateOnly? LastEvaluationDate { get; init; }
    public int? EvaluationScore { get; init; }

    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>AMMPS / DMP registration (loi 84-12) and its validity date.</summary>
    public string? RegulatoryRegistrationNumber { get; init; }
    public DateOnly? RegulatoryRegistrationValidUntil { get; init; }
}

public sealed record PrescriberRequest
{
    public Guid? PrescriberId { get; init; }

    public required string LastName { get; init; }
    public required string FirstName { get; init; }
    public string? FullNameArabic { get; init; }
    public PrescriberKind Kind { get; init; }
    public MultilingualText? Speciality { get; init; }

    /// <summary>Ordre registration. Required for a physician, and printed on the report.</summary>
    public string? ProfessionalOrderNumber { get; init; }

    /// <summary>INPE — needed on an AMO claim that carries this prescriber's prescription.</summary>
    public string? Inpe { get; init; }
    public string? Ice { get; init; }

    public string? Phone { get; init; }
    public string? MobilePhone { get; init; }
    public string? Email { get; init; }
    public string? Fax { get; init; }
    public string? AddressLine { get; init; }
    public string? City { get; init; }
    public string? InstitutionName { get; init; }

    public ReportDeliveryPreference DeliveryPreference { get; init; }
    public bool SendPartialReports { get; init; }
    public string? CriticalResultPhone { get; init; }

    public decimal? DiscountPercent { get; init; }
    public Guid? DefaultPayerId { get; init; }

    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// A payer to create or amend.
/// <para>
/// <see cref="DefaultReimbursementRate"/> is a fraction — 0.70 for 70 % — matching the domain and
/// <c>ActPricingCalculator</c>. The editor presents it as a percentage and divides, because an
/// administrator typing an AMO rate thinks in percent and the pricing arithmetic does not.
/// </para>
/// </summary>
public sealed record PayerRequest
{
    public Guid? PayerId { get; init; }

    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }
    public PayerKind Kind { get; init; }

    public decimal DefaultReimbursementRate { get; init; } = 0.70m;
    public bool UsesNationalReferenceTariff { get; init; } = true;

    public bool ThirdPartyPaymentAgreement { get; init; }
    public string? AgreementNumber { get; init; }
    public DateOnly? AgreementValidUntil { get; init; }

    public int PaymentTermsDays { get; init; } = 60;
    public bool RequiresPrescription { get; init; } = true;
    public bool RequiresInpeOnClaim { get; init; }

    public string? Ice { get; init; }
    public string? ContactName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? AddressLine { get; init; }
    public string? City { get; init; }

    public bool IsActive { get; init; } = true;
}

// ---- Rows --------------------------------------------------------------------------------------

public sealed record InstrumentMasterRow(
    Guid InstrumentId, string Code, MultilingualText Name, EquipmentKind Kind,
    EquipmentStatus Status, string? Manufacturer, string? Model, string? SerialNumber,
    string? Location, int? PreventiveMaintenanceIntervalDays, int? CalibrationIntervalDays,
    int? VerificationIntervalDays, DateOnly? LastCalibrationOn, DateOnly? ContractValidUntil,
    bool HasInterfaceConfigured, int QcTargetCount, bool IntervalsIncomplete);

public sealed record QcMaterialRow(
    Guid QcMaterialId, string LotNumber, MultilingualText Name, QcLevel Level,
    DateOnly ExpiryDate, int DaysToExpiry, string? Manufacturer, string? CatalogueReference,
    DateOnly? OpenedOn, int? StabilityAfterOpeningDays, bool IsActive, bool IsUsable,
    int TargetCount);

public sealed record QcTargetRow(
    Guid QcTargetId, Guid QcMaterialId, string MaterialLotNumber, QcLevel Level,
    Guid TestParameterId, string ParameterCode, MultilingualText ParameterName,
    Guid InstrumentId, string InstrumentCode, decimal? AssignedMean, decimal? AssignedSd,
    decimal? LaboratoryMean, decimal? LaboratorySd, int LaboratoryPointCount,
    decimal? MaxAcceptableCvPercent, WestgardRule ActiveRules, bool IsActive,
    bool HasUsableLimits);

public sealed record StockItemMasterRow(
    Guid StockItemId, string Code, MultilingualText Name, StockItemKind Kind,
    string UnitOfMeasure, decimal ReorderPoint, decimal MinimumStock, bool IsCritical,
    StorageCondition StorageCondition, string? SupplierName, bool IsHazardous, bool IsActive,
    int LotCount,
    // The editor round-trips every field it shows: a field the row did not carry was blanked on the
    // next save (the analyses list among them, which the reagent-lot tracing reads).
    string? Gtin = null, bool IsCeMarked = false, Guid? PreferredSupplierId = null,
    string? Manufacturer = null, string? SupplierCatalogueReference = null,
    string? UsedByAnalysisCodesCsv = null, string? DefaultStorageLocation = null,
    int? TestsPerPack = null, decimal ReorderQuantity = 0m, decimal? MaximumStock = null,
    string? HazardClass = null, StockConsumptionPolicy? ConsumptionPolicy = null);

public sealed record SupplierRow(
    Guid SupplierId, string Code, string Name, string? Ice, string? ContactName, string? Phone,
    string? Email, string? City, int LeadTimeDays, bool IsApproved,
    DateOnly? LastEvaluationDate, int? EvaluationScore, bool IsEvaluationOverdue, bool IsActive,
    int StockItemCount,
    string? RegistrationNumber = null, DateOnly? RegistrationValidUntil = null,
    // Round-tripped by the editor, which blanked them on every save before.
    string? FiscalId = null, string? CommercialRegisterNumber = null, string? AddressLine = null,
    MoroccanRegion Region = MoroccanRegion.Unspecified, int PaymentTermsDays = PaymentTerms.DefaultDays, string? Notes = null,
    string? AccountingAccount = null);

/// <summary>
/// A payer as the maintenance grid shows it.
/// <para>
/// <see cref="CoveredPatientCount"/> and <see cref="OpenInvoiceCount"/> are what make deactivating
/// one a decision rather than a guess: a payer with live coverages and unsettled invoices is one
/// whose terms are still being applied to real money.
/// </para>
/// </summary>
public sealed record PayerRow(
    Guid PayerId, string Code, MultilingualText Name, PayerKind Kind,
    decimal DefaultReimbursementRate, bool UsesNationalReferenceTariff,
    bool ThirdPartyPaymentAgreement, string? AgreementNumber, DateOnly? AgreementValidUntil,
    bool IsAgreementExpired, int PaymentTermsDays, bool RequiresPrescription,
    bool RequiresInpeOnClaim, string? Ice, string? ContactName, string? Phone, string? Email,
    // AddressLine is projected, not dropped: the editor round-trips every field it shows, and a
    // row that omitted one would blank it on the next save. Same defect as PrescriberRow's missing
    // SendPartialReports.
    string? AddressLine, string? City, bool IsActive, int CoveredPatientCount, int OpenInvoiceCount)
{
    /// <summary>The rate as a percentage, for display. The stored value is a fraction.</summary>
    public decimal ReimbursementPercent => DefaultReimbursementRate * 100m;

    /// <summary>
    /// Resource keys for anything that would make this payer reject or mis-price a claim, so the
    /// grid can flag a row before an invoice is built on it.
    /// </summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = new List<string>();

            if (IsAgreementExpired) problems.Add("Payer.AgreementExpired");

            if (ThirdPartyPaymentAgreement && string.IsNullOrWhiteSpace(AgreementNumber))
                problems.Add("Payer.AgreementNumberMissing");

            if (Kind is PayerKind.AmoCnss or PayerKind.AmoCnops
                     or PayerKind.AmoTadamon or PayerKind.AmoAchamil
                && !UsesNationalReferenceTariff)
                problems.Add("Payer.AmoWithoutReferenceTariff");

            if (Kind != PayerKind.SelfPay && DefaultReimbursementRate == 0m)
                problems.Add("Payer.RateIsZero");

            return problems;
        }
    }
}

public sealed record PrescriberRow(
    Guid PrescriberId, string FullName, string? FullNameArabic, PrescriberKind Kind,
    MultilingualText? Speciality, string? ProfessionalOrderNumber, string? Inpe,
    string? InstitutionName, string? Phone, string? Email, string? City,
    ReportDeliveryPreference DeliveryPreference, bool SendPartialReports,
    decimal? DiscountPercent, bool IsActive, int OrderCount);

/// <summary>A parameter a QC target can be defined against.</summary>
public sealed record ParameterOption(
    Guid TestParameterId, string AnalysisCode, string ParameterCode,
    MultilingualText ParameterName, string? UnitSymbol, int Decimals);

/// <summary>
/// The reference data an installation has to be able to create.
/// <para>
/// Every aggregate here was constructible only by the seeder, or not at all — which meant three
/// finished screens opened onto permanently empty grids on any real installation: the stock screen
/// had no article to receive a lot against, the equipment register had no instrument, and the
/// quality-control entry sheet had no target, so it could never show a single row.
/// </para>
/// <para>
/// One consequence was a silent safety failure rather than an inconvenience.
/// <see cref="Instrument.CanProduceResults"/> refuses to let an analyser produce patient results
/// while its calibration is overdue — but "overdue" is computed from an interval that nothing could
/// set, so the interval was always null and the guard could never fire. Setting those intervals is
/// what arms it.
/// </para>
/// </summary>
public sealed class MasterDataService(
    IRepository<Instrument> instruments,
    IRepository<QcMaterial> qcMaterials,
    IRepository<QcTarget> qcTargets,
    IRepository<StockItem> stockItems,
    IRepository<Supplier> suppliers,
    IRepository<Prescriber> prescribers,
    IRepository<Payer> payers,
    IMasterDataQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    // ---- Instruments ---------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<InstrumentMasterRow>>> GetInstrumentsAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<InstrumentMasterRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetInstrumentsAsync(currentUser.SiteId, includeInactive, ct));
    }

    public async Task<Result<Guid>> SaveInstrumentAsync(InstrumentRequest request,
                                                         CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Equipment.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code))
            errors.Add(Error.Validation("Equipment_CodeRequired"));

        if (string.IsNullOrWhiteSpace(request.Name.Fr))
            errors.Add(Error.Validation("Equipment_NameRequired"));

        foreach (var (interval, key) in new (int?, string)[]
                 {
                     (request.PreventiveMaintenanceIntervalDays, "Equipment_MaintenanceIntervalInvalid"),
                     (request.CalibrationIntervalDays, "Equipment_CalibrationIntervalInvalid"),
                     (request.VerificationIntervalDays, "Equipment_VerificationIntervalInvalid")
                 })
        {
            if (interval is <= 0 or > 3650) errors.Add(Error.Validation(key));
        }

        // An analyser with no calibration interval cannot have an overdue calibration, so the
        // control that stops it producing patient results can never fire. Refused rather than
        // allowed to pass silently — a disarmed guard is worse than an absent one.
        if (request.Kind == EquipmentKind.Analyser && request.CalibrationIntervalDays is null)
            errors.Add(Error.Regulatory("Equipment_AnalyserNeedsCalibrationInterval"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var code = request.Code.Trim().ToUpperInvariant();

        Instrument instrument;

        if (request.InstrumentId is { } id)
        {
            var existing = await instruments.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Equipment_NotFound"));
            instrument = existing;

            if (!string.Equals(instrument.Code, code, StringComparison.Ordinal)
                && await queries.InstrumentCodeExistsAsync(code, id, ct))
                return Result.Failure<Guid>(Error.Conflict("Equipment_CodeAlreadyTaken", code));
        }
        else
        {
            if (await queries.InstrumentCodeExistsAsync(code, null, ct))
                return Result.Failure<Guid>(Error.Conflict("Equipment_CodeAlreadyTaken", code));

            instrument = new Instrument(code, request.Name, request.Kind, currentUser.SiteId);
            await instruments.AddAsync(instrument, ct);
        }

        instrument.Name = request.Name;
        instrument.Kind = request.Kind;
        instrument.Manufacturer = Blank(request.Manufacturer);
        instrument.Model = Blank(request.Model);
        instrument.SerialNumber = Blank(request.SerialNumber);
        instrument.SoftwareVersion = Blank(request.SoftwareVersion);
        instrument.Location = Blank(request.Location);
        instrument.DepartmentId = request.DepartmentId;
        instrument.PurchaseDate = request.PurchaseDate;
        instrument.CommissioningDate = request.CommissioningDate;
        instrument.SupplierId = request.SupplierId;
        instrument.MaintenanceContractReference = Blank(request.MaintenanceContractReference);
        instrument.MaintenanceContractValidUntil = request.MaintenanceContractValidUntil;
        instrument.SupportPhone = MoroccanIdentifiers.TryNormalizePhone(request.SupportPhone)
                                  ?? Blank(request.SupportPhone);

        instrument.PreventiveMaintenanceIntervalDays = request.PreventiveMaintenanceIntervalDays;
        instrument.CalibrationIntervalDays = request.CalibrationIntervalDays;
        instrument.VerificationIntervalDays = request.VerificationIntervalDays;

        // The "last done" dates are normally advanced by recording an intervention. They are
        // settable here only because a laboratory adopting the system has to be able to enter the
        // dates already in its paper equipment file, or every instrument reads as overdue on day one.
        if (request.LastPreventiveMaintenanceOn is { } maintenance)
            instrument.LastPreventiveMaintenanceOn = maintenance;

        if (request.LastCalibrationOn is { } calibration)
            instrument.LastCalibrationOn = calibration;

        if (request.LastVerificationOn is { } verification)
            instrument.LastVerificationOn = verification;

        instruments.Update(instrument);

        unitOfWork.AuditReason = request.InstrumentId is null
            ? $"Création de l'équipement {code}"
            : $"Modification de l'équipement {code}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(instrument.Id);
    }

    // ---- QC materials and targets ---------------------------------------------------------------

    public async Task<Result<IReadOnlyList<QcMaterialRow>>> GetQcMaterialsAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<QcMaterialRow>>(permitted.FirstError);

        return Result.Success(await queries.GetQcMaterialsAsync(includeInactive, clock.Today, ct));
    }

    public async Task<Result<Guid>> SaveQcMaterialAsync(QcMaterialRequest request,
                                                         CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageQcRules);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.LotNumber))
            errors.Add(Error.Validation("QcMaterial_LotRequired"));

        if (string.IsNullOrWhiteSpace(request.Name.Fr))
            errors.Add(Error.Validation("QcMaterial_NameRequired"));

        if (request.ExpiryDate < clock.Today)
            errors.Add(Error.Validation("QcMaterial_AlreadyExpired", request.ExpiryDate));

        if (request.OpenedOn is { } opened && opened > clock.Today)
            errors.Add(Error.Validation("QcMaterial_OpenedInFuture"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var lot = request.LotNumber.Trim();

        QcMaterial material;

        if (request.QcMaterialId is { } id)
        {
            var existing = await qcMaterials.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("QcMaterial_NotFound"));
            material = existing;
        }
        else
        {
            // Lot plus level is the natural key: a control set ships one lot number across two or
            // three levels, and they are separate materials with separate target values.
            if (await queries.QcMaterialExistsAsync(lot, request.Level, ct))
                return Result.Failure<Guid>(Error.Conflict("QcMaterial_LotLevelAlreadyExists",
                                                             lot, request.Level));

            material = new QcMaterial(lot, request.Name, request.Level, request.ExpiryDate);
            await qcMaterials.AddAsync(material, ct);
        }

        material.Name = request.Name;
        material.Manufacturer = Blank(request.Manufacturer);
        material.CatalogueReference = Blank(request.CatalogueReference);
        material.OpenedOn = request.OpenedOn;
        material.StabilityAfterOpeningDays = request.StabilityAfterOpeningDays;
        material.IsActive = request.IsActive;

        qcMaterials.Update(material);

        unitOfWork.AuditReason = request.QcMaterialId is null
            ? $"Création du matériau de contrôle {lot} ({request.Level})"
            : $"Modification du matériau de contrôle {lot}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(material.Id);
    }

    public async Task<Result<IReadOnlyList<QcTargetRow>>> GetQcTargetsAsync(
        Guid? qcMaterialId = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<QcTargetRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetQcTargetsAsync(currentUser.SiteId, qcMaterialId, ct));
    }

    /// <summary>
    /// Defines what a control is expected to read on one instrument for one parameter.
    /// <para>
    /// This is the record the whole QC module hangs from: without a target there is nothing to
    /// enter a control value against, no mean and SD to compute a Z-score from, and therefore no
    /// Levey-Jennings series and no Westgard evaluation. The entry sheet was permanently empty
    /// because nothing could create one.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> SaveQcTargetAsync(QcTargetRequest request,
                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageQcRules);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        // Assigned mean and SD are the manufacturer's insert values. They are the starting limits
        // until the laboratory has enough of its own points, so a target without them can accept
        // values but can never evaluate a rule.
        if (request.AssignedSd is <= 0m)
            errors.Add(Error.Validation("QcTarget_SdMustBePositive"));

        if (request.AssignedMean is null && request.AssignedSd is not null)
            errors.Add(Error.Validation("QcTarget_MeanRequiredWithSd"));

        if (request.MaxAcceptableCvPercent is <= 0m or > 100m)
            errors.Add(Error.Validation("QcTarget_CvOutOfRange"));

        if (request.ActiveRules == WestgardRule.None)
            errors.Add(Error.Regulatory("QcTarget_AtLeastOneRuleRequired"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        QcTarget target;

        if (request.QcTargetId is { } id)
        {
            var existing = await qcTargets.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Qc_TargetNotFound"));
            target = existing;
        }
        else
        {
            if (await queries.QcTargetExistsAsync(request.QcMaterialId, request.TestParameterId,
                                                   request.InstrumentId, ct))
                return Result.Failure<Guid>(Error.Conflict("QcTarget_AlreadyDefined"));

            target = new QcTarget(request.QcMaterialId, request.TestParameterId,
                                   request.InstrumentId);

            await qcTargets.AddAsync(target, ct);
        }

        target.AssignedMean = request.AssignedMean;
        target.AssignedSd = request.AssignedSd;
        target.MaxAcceptableCvPercent = request.MaxAcceptableCvPercent;
        target.ActiveRules = request.ActiveRules;
        target.IsActive = request.IsActive;

        qcTargets.Update(target);

        unitOfWork.AuditReason = request.QcTargetId is null
            ? "Création d'une cible de contrôle interne"
            : "Modification d'une cible de contrôle interne";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(target.Id);
    }

    public async Task<Result<IReadOnlyList<ParameterOption>>> GetParametersAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Quality.View, Permissions.Catalog.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<ParameterOption>>(permitted.FirstError);

        return Result.Success(await queries.GetParametersAsync(ct));
    }

    // ---- Stock items and suppliers ---------------------------------------------------------------

    public async Task<Result<IReadOnlyList<StockItemMasterRow>>> GetStockItemsAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<StockItemMasterRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetStockItemsAsync(currentUser.SiteId, includeInactive, ct));
    }

    public async Task<Result<Guid>> SaveStockItemAsync(StockItemRequest request,
                                                        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code))
            errors.Add(Error.Validation("Stock_CodeRequired"));

        if (string.IsNullOrWhiteSpace(request.Name.Fr))
            errors.Add(Error.Validation("Stock_NameRequired"));

        if (string.IsNullOrWhiteSpace(request.UnitOfMeasure))
            errors.Add(Error.Validation("Stock_UnitRequired"));

        if (request.ReorderPoint < 0m || request.MinimumStock < 0m)
            errors.Add(Error.Validation("Stock_LevelsCannotBeNegative"));

        // The reorder point has to sit at or above the safety stock, otherwise the reorder alert
        // fires after the laboratory is already below the level it promised itself it would hold.
        if (request.ReorderPoint < request.MinimumStock)
            errors.Add(Error.Validation("Stock_ReorderBelowMinimum"));

        if (request.MaximumStock is { } max && max < request.ReorderPoint)
            errors.Add(Error.Validation("Stock_MaximumBelowReorder"));

        if (request.IsHazardous && string.IsNullOrWhiteSpace(request.HazardClass))
            errors.Add(Error.Validation("Stock_HazardClassRequired"));

        var gtin = string.IsNullOrWhiteSpace(request.Gtin) ? null : Gs1Parser.NormaliseGtin(request.Gtin);
        if (!string.IsNullOrWhiteSpace(request.Gtin) && gtin is null)
            errors.Add(Error.Validation("Gs1_GtinInvalid", request.Gtin.Trim()));

        if (request.ConsumptionPolicy?.Validate() is { IsFailure: true } policyInvalid)
            errors.AddRange(policyInvalid.Errors);

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var code = request.Code.Trim().ToUpperInvariant();

        StockItem item;

        if (request.StockItemId is { } id)
        {
            var existing = await stockItems.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Stock_ItemNotFound"));
            item = existing;

            if (!string.Equals(item.Code, code, StringComparison.Ordinal)
                && await queries.StockItemCodeExistsAsync(code, currentUser.SiteId, id, ct))
                return Result.Failure<Guid>(Error.Conflict("Stock_CodeAlreadyTaken", code));
        }
        else
        {
            if (await queries.StockItemCodeExistsAsync(code, currentUser.SiteId, null, ct))
                return Result.Failure<Guid>(Error.Conflict("Stock_CodeAlreadyTaken", code));

            item = new StockItem(code, request.Name, request.Kind,
                                  request.UnitOfMeasure.Trim(), currentUser.SiteId);

            await stockItems.AddAsync(item, ct);
        }

        item.Name = request.Name;
        item.Kind = request.Kind;
        item.UnitOfMeasure = request.UnitOfMeasure.Trim();
        item.TestsPerPack = request.TestsPerPack;
        item.PreferredSupplierId = request.PreferredSupplierId;
        item.SupplierCatalogueReference = Blank(request.SupplierCatalogueReference);
        item.Manufacturer = Blank(request.Manufacturer);
        item.DepartmentId = request.DepartmentId;
        item.UsedByAnalysisCodesCsv = Blank(request.UsedByAnalysisCodesCsv);
        item.ReorderPoint = request.ReorderPoint;
        item.ReorderQuantity = request.ReorderQuantity;
        item.MinimumStock = request.MinimumStock;
        item.MaximumStock = request.MaximumStock;
        item.StorageCondition = request.StorageCondition;
        item.DefaultStorageLocation = Blank(request.DefaultStorageLocation);
        item.IsHazardous = request.IsHazardous;
        item.HazardClass = Blank(request.HazardClass);
        item.IsCritical = request.IsCritical;
        item.IsActive = request.IsActive;
        item.Gtin = gtin;
        item.IsCeMarked = request.IsCeMarked;
        if (request.ConsumptionPolicy is { } policy) item.ApplyConsumptionPolicy(policy);

        stockItems.Update(item);

        unitOfWork.AuditReason = request.StockItemId is null
            ? $"Création de l'article de stock {code}"
            : $"Modification de l'article de stock {code}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(item.Id);
    }

    public async Task<Result<IReadOnlyList<SupplierRow>>> GetSuppliersAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SupplierRow>>(permitted.FirstError);

        return Result.Success(await queries.GetSuppliersAsync(includeInactive, clock.Today, ct));
    }

    public async Task<Result<Guid>> SaveSupplierAsync(SupplierRequest request,
                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.ManagePurchaseOrders);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code))
            errors.Add(Error.Validation("Supplier_CodeRequired"));

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(Error.Validation("Supplier_NameRequired"));

        if (!string.IsNullOrWhiteSpace(request.Ice) && !MoroccanIdentifiers.IsValidIce(request.Ice))
            errors.Add(Error.Validation("Supplier_IceInvalid"));

        if (request.EvaluationScore is < 0 or > 100)
            errors.Add(Error.Validation("Supplier_ScoreOutOfRange"));

        // Loi n° 69-21 caps an agreed payment term at 120 days; a longer one cannot be agreed.
        if (request.PaymentTermsDays is < 0 or > PaymentTerms.MaximumDays)
            errors.Add(Error.Validation("Supplier_PaymentTermsTooLong", PaymentTerms.MaximumDays));

        // An approval with no evaluation behind it is the box-ticking ISO 15189 §4.6 exists to
        // prevent: the point is that the laboratory assessed the supplier, not that it said so.
        if (request.IsApproved && request.LastEvaluationDate is null)
            errors.Add(Error.Regulatory("Supplier_ApprovalNeedsEvaluation"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var code = request.Code.Trim().ToUpperInvariant();

        Supplier supplier;

        if (request.SupplierId is { } id)
        {
            var existing = await suppliers.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Supplier_NotFound"));
            supplier = existing;

            if (!string.Equals(supplier.Code, code, StringComparison.Ordinal)
                && await queries.SupplierCodeExistsAsync(code, id, ct))
                return Result.Failure<Guid>(Error.Conflict("Supplier_CodeAlreadyTaken", code));
        }
        else
        {
            if (await queries.SupplierCodeExistsAsync(code, null, ct))
                return Result.Failure<Guid>(Error.Conflict("Supplier_CodeAlreadyTaken", code));

            supplier = new Supplier(code, request.Name.Trim());
            await suppliers.AddAsync(supplier, ct);
        }

        supplier.Name = request.Name.Trim();
        supplier.Ice = Digits(request.Ice);
        supplier.FiscalId = Digits(request.FiscalId);
        supplier.CommercialRegisterNumber = Blank(request.CommercialRegisterNumber);
        supplier.ContactName = Blank(request.ContactName);
        supplier.Phone = MoroccanIdentifiers.TryNormalizePhone(request.Phone) ?? Blank(request.Phone);
        supplier.Email = Blank(request.Email);
        supplier.AddressLine = Blank(request.AddressLine);
        supplier.City = Blank(request.City);
        supplier.Region = request.Region;
        supplier.LeadTimeDays = request.LeadTimeDays ?? 7;
        supplier.PaymentTermsDays = request.PaymentTermsDays;
        supplier.AccountingAccount = Blank(request.AccountingAccount);
        supplier.IsApproved = request.IsApproved;
        supplier.LastEvaluationDate = request.LastEvaluationDate;
        supplier.EvaluationScore = request.EvaluationScore;
        supplier.Notes = Blank(request.Notes);
        supplier.IsActive = request.IsActive;
        supplier.RegulatoryRegistrationNumber = Blank(request.RegulatoryRegistrationNumber);
        supplier.RegulatoryRegistrationValidUntil = request.RegulatoryRegistrationValidUntil;

        suppliers.Update(supplier);

        unitOfWork.AuditReason = request.SupplierId is null
            ? $"Création du fournisseur {code}"
            : $"Modification du fournisseur {code}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(supplier.Id);
    }

    // ---- Payers -----------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<PayerRow>>> GetPayersAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PayerRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPayersAsync(includeInactive, clock.Today, ct));
    }

    /// <summary>
    /// Creates or amends a payer.
    /// <para>
    /// Guarded by <c>ManageTariffs</c> rather than by a general administration permission, because
    /// that is what this is: the reimbursement rate, the reference-tariff flag and the
    /// third-party-payment agreement together decide how every invoice for every covered patient
    /// divides between the payer and the patient. It belongs with whoever is trusted to set prices,
    /// not with whoever can add a user.
    /// </para>
    /// <para>
    /// Nothing could create or edit a payer before this existed, so the rates shipped by
    /// <c>ReferenceDataSeeder</c> — which its own comment calls "starting points an administrator
    /// confirms against the current convention, not law" — were the rates every installation billed
    /// at, permanently, with a hand-written UPDATE as the only remedy.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> SavePayerAsync(PayerRequest request,
                                                     CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var code = request.Code.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<Guid>(Error.Validation("Payer_CodeRequired"));

        if (await queries.PayerCodeExistsAsync(code, request.PayerId, ct))
            return Result.Failure<Guid>(Error.Conflict("Payer_CodeAlreadyUsed", code));

        Payer payer;

        if (request.PayerId is { } id)
        {
            var existing = await payers.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Payer_NotFound"));
            payer = existing;
        }
        else
        {
            payer = new Payer(code, request.Name, request.Kind);
            await payers.AddAsync(payer, ct);
        }

        payer.Name = request.Name;
        payer.Kind = request.Kind;
        payer.DefaultReimbursementRate = request.DefaultReimbursementRate;
        payer.UsesNationalReferenceTariff = request.UsesNationalReferenceTariff;
        payer.ThirdPartyPaymentAgreement = request.ThirdPartyPaymentAgreement;
        payer.AgreementNumber = Blank(request.AgreementNumber);
        payer.AgreementValidUntil = request.AgreementValidUntil;
        payer.PaymentTermsDays = request.PaymentTermsDays;
        payer.RequiresPrescription = request.RequiresPrescription;
        payer.RequiresInpeOnClaim = request.RequiresInpeOnClaim;
        payer.Ice = Digits(request.Ice);
        payer.ContactName = Blank(request.ContactName);
        payer.Phone = MoroccanIdentifiers.TryNormalizePhone(request.Phone) ?? Blank(request.Phone);
        payer.Email = Blank(request.Email);
        payer.AddressLine = Blank(request.AddressLine);
        payer.City = Blank(request.City);
        payer.IsActive = request.IsActive;

        var valid = payer.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        payers.Update(payer);

        // The rate goes in the audit reason explicitly. "Modified payer CNSS" says nothing an
        // auditor reconciling a disputed settlement can use; the rate it was changed to is the
        // whole question.
        unitOfWork.AuditReason = request.PayerId is null
            ? $"Création de l'organisme payeur {code} ({payer.Name.Fr}), " +
              $"taux {payer.DefaultReimbursementRate * 100m:0.##} %"
            : $"Modification de l'organisme payeur {code} ({payer.Name.Fr}), " +
              $"taux {payer.DefaultReimbursementRate * 100m:0.##} %";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(payer.Id);
    }

    // ---- Prescribers ------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<PrescriberRow>>> GetPrescribersAsync(
        string? search = null, bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Orders.View, Permissions.Orders.Create);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PrescriberRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPrescribersAsync(search, includeInactive, ct));
    }

    /// <summary>
    /// Creates or edits a prescriber.
    /// <para>
    /// Nothing could create one before, so <c>LabOrder.PrescriberId</c> was never populated — which
    /// matters beyond tidiness: an AMO claim needs the prescription behind it, and a critical result
    /// has to be communicated to a named prescriber on a number somebody recorded.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> SavePrescriberAsync(PrescriberRequest request,
                                                         CancellationToken ct = default)
    {
        // Reception creates a doctor it meets for the first time while registering the file;
        // changing an existing one stays with whoever manages the directory.
        var permitted = request.PrescriberId is null
            ? guard.RequireAny(Permissions.Catalog.Manage, Permissions.Orders.Create)
            : guard.Require(Permissions.Catalog.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        Prescriber? existing = null;

        if (request.PrescriberId is { } id)
        {
            existing = await prescribers.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Prescriber_NotFound"));
        }

        // The aggregate's own rules — INPE and ICE format, the discount range, and that a delivery
        // preference of e-mail or SMS actually has an address or a mobile behind it — checked on a
        // copy the context never sees. A refused prescriber used to be added (or an existing one
        // changed) first and left tracked: the order screen's "+" keeps its context, so the next
        // save — the corrected prescriber, or the order itself — committed the refused one too.
        var candidate = new Prescriber(request.LastName.Trim(), request.FirstName.Trim(), request.Kind);
        Apply(candidate);

        var valid = candidate.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        var prescriber = existing ?? candidate;

        if (existing is null) await prescribers.AddAsync(prescriber, ct);
        else Apply(existing);

        prescribers.Update(prescriber);

        unitOfWork.AuditReason = request.PrescriberId is null
            ? $"Création du prescripteur {prescriber.LastName} {prescriber.FirstName}"
            : $"Modification du prescripteur {prescriber.LastName} {prescriber.FirstName}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(prescriber.Id);

        void Apply(Prescriber target)
        {
            target.LastName = request.LastName.Trim();
            target.FirstName = request.FirstName.Trim();
            target.FullNameArabic = Blank(request.FullNameArabic);
            target.SearchKey = PatientSearchKey.Build(request.LastName, request.FirstName);
            target.Kind = request.Kind;
            target.Speciality = request.Speciality;
            target.ProfessionalOrderNumber = Blank(request.ProfessionalOrderNumber);
            target.Inpe = Digits(request.Inpe);
            target.Ice = Digits(request.Ice);
            target.Phone = MoroccanIdentifiers.TryNormalizePhone(request.Phone) ?? Blank(request.Phone);
            target.MobilePhone = MoroccanIdentifiers.TryNormalizePhone(request.MobilePhone);
            target.Email = Blank(request.Email);
            target.Fax = Blank(request.Fax);
            target.AddressLine = Blank(request.AddressLine);
            target.City = Blank(request.City);
            target.InstitutionName = Blank(request.InstitutionName);
            target.DeliveryPreference = request.DeliveryPreference;
            target.SendPartialReports = request.SendPartialReports;
            target.CriticalResultPhone =
                MoroccanIdentifiers.TryNormalizePhone(request.CriticalResultPhone)
                ?? Blank(request.CriticalResultPhone);
            target.DiscountPercent = request.DiscountPercent;
            target.DefaultPayerId = request.DefaultPayerId;
            target.Notes = Blank(request.Notes);
            target.IsActive = request.IsActive;
        }
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Digits(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : new string(value.Where(char.IsDigit).ToArray()) is { Length: > 0 } digits
                ? digits
                : null;
}

/// <summary>Read-side queries for the reference-data screen.</summary>
public interface IMasterDataQueries
{
    Task<IReadOnlyList<InstrumentMasterRow>> GetInstrumentsAsync(
        Guid siteId, bool includeInactive, CancellationToken ct = default);

    Task<bool> InstrumentCodeExistsAsync(string code, Guid? excludingId,
                                          CancellationToken ct = default);

    Task<IReadOnlyList<QcMaterialRow>> GetQcMaterialsAsync(
        bool includeInactive, DateOnly today, CancellationToken ct = default);

    Task<bool> QcMaterialExistsAsync(string lotNumber, QcLevel level,
                                      CancellationToken ct = default);

    Task<IReadOnlyList<QcTargetRow>> GetQcTargetsAsync(
        Guid siteId, Guid? qcMaterialId, CancellationToken ct = default);

    Task<bool> QcTargetExistsAsync(Guid qcMaterialId, Guid testParameterId, Guid instrumentId,
                                    CancellationToken ct = default);

    /// <summary>Parameters a QC target can be attached to, analysis code first.</summary>
    Task<IReadOnlyList<ParameterOption>> GetParametersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StockItemMasterRow>> GetStockItemsAsync(
        Guid siteId, bool includeInactive, CancellationToken ct = default);

    Task<bool> StockItemCodeExistsAsync(string code, Guid siteId, Guid? excludingId,
                                         CancellationToken ct = default);

    Task<IReadOnlyList<SupplierRow>> GetSuppliersAsync(
        bool includeInactive, DateOnly today, CancellationToken ct = default);

    Task<bool> SupplierCodeExistsAsync(string code, Guid? excludingId,
                                        CancellationToken ct = default);

    Task<IReadOnlyList<PrescriberRow>> GetPrescribersAsync(
        string? search, bool includeInactive, CancellationToken ct = default);

    Task<IReadOnlyList<PayerRow>> GetPayersAsync(
        bool includeInactive, DateOnly today, CancellationToken ct = default);

    Task<bool> PayerCodeExistsAsync(string code, Guid? excludingId,
                                     CancellationToken ct = default);
}
