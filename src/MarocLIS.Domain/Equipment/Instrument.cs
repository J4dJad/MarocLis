using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Equipment;

/// <summary>
/// A piece of laboratory equipment — usually an analyser, but the same record covers centrifuges,
/// fridges and pipettes, all of which ISO 15189 §5.3 requires to be inventoried, maintained and
/// calibrated with records kept. The communication settings for analysers that talk to the LIS live
/// on <see cref="InstrumentInterface"/> so a fridge does not carry a serial port configuration.
/// </summary>
public sealed class Instrument : AuditableAggregateRoot, ISoftDeletable, ISiteScoped
{
    // Named after the navigation on purpose: EF Core finds a collection's backing field by name,
    // and under any other name it materialises the records into the read-only wrapper and throws,
    // which made every instrument with a maintenance entry impossible to load for an update.
    private readonly List<MaintenanceRecord> _maintenanceRecords = [];
    private readonly List<CalibrationRecord> _calibrations = [];

    private Instrument() { }

    public Instrument(string code, MultilingualText name, EquipmentKind kind, Guid siteId)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
        Kind = kind;
        SiteId = siteId;
    }

    /// <summary>Internal asset code, printed on the equipment label and on worklists.</summary>
    public string Code { get; private set; } = string.Empty;

    public MultilingualText Name { get; set; } = new();
    public EquipmentKind Kind { get; set; }
    public Guid SiteId { get; set; }

    public Guid? DepartmentId { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>Firmware or software version in use — relevant when a supplier reports a defect.</summary>
    public string? SoftwareVersion { get; set; }

    public DateOnly? PurchaseDate { get; set; }
    public DateOnly? CommissioningDate { get; set; }
    public DateOnly? DecommissioningDate { get; set; }

    /// <summary>Supplier and contract, so a breakdown call does not need a paper file.</summary>
    public Guid? SupplierId { get; set; }
    public string? MaintenanceContractReference { get; set; }
    public DateOnly? MaintenanceContractValidUntil { get; set; }
    public string? SupportPhone { get; set; }

    public EquipmentStatus Status { get; private set; } = EquipmentStatus.Operational;

    /// <summary>Physical location, for the equipment inventory.</summary>
    public string? Location { get; set; }

    // ---- Planning ------------------------------------------------------------------------------

    /// <summary>Interval between preventive maintenance operations, in days.</summary>
    public int? PreventiveMaintenanceIntervalDays { get; set; }
    public DateOnly? LastPreventiveMaintenanceOn { get; set; }

    /// <summary>Interval between calibrations, in days.</summary>
    public int? CalibrationIntervalDays { get; set; }
    public DateOnly? LastCalibrationOn { get; set; }

    /// <summary>
    /// For equipment whose measurement is the point (fridges, thermometers, pipettes): interval
    /// between metrological verifications.
    /// </summary>
    public int? VerificationIntervalDays { get; set; }
    public DateOnly? LastVerificationOn { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public IReadOnlyCollection<MaintenanceRecord> MaintenanceRecords => _maintenanceRecords.AsReadOnly();
    public IReadOnlyCollection<CalibrationRecord> Calibrations => _calibrations.AsReadOnly();

    // ---- Derived -------------------------------------------------------------------------------

    public DateOnly? NextPreventiveMaintenanceDue =>
        LastPreventiveMaintenanceOn is { } last && PreventiveMaintenanceIntervalDays is { } interval
            ? last.AddDays(interval) : null;

    public DateOnly? NextCalibrationDue =>
        LastCalibrationOn is { } last && CalibrationIntervalDays is { } interval
            ? last.AddDays(interval) : null;

    public DateOnly? NextVerificationDue =>
        LastVerificationOn is { } last && VerificationIntervalDays is { } interval
            ? last.AddDays(interval) : null;

    public bool IsMaintenanceOverdue(DateOnly today) =>
        NextPreventiveMaintenanceDue is { } due && due < today;

    public bool IsCalibrationOverdue(DateOnly today) =>
        NextCalibrationDue is { } due && due < today;

    public bool IsContractExpired(DateOnly today) =>
        MaintenanceContractValidUntil is { } until && until < today;

    /// <summary>
    /// Whether the instrument may be used to produce patient results. An overdue calibration is
    /// not a warning to be dismissed: results produced on an uncalibrated analyser are not
    /// defensible, so the domain refuses rather than letting the UI decide.
    /// </summary>
    public Result CanProduceResults(DateOnly today)
    {
        if (Status != EquipmentStatus.Operational)
            return Result.Failure(Error.Regulatory("Instrument_NotOperational", Status));
        if (DecommissioningDate is { } decommissioned && decommissioned <= today)
            return Result.Failure(Error.Regulatory("Instrument_Decommissioned"));
        if (IsCalibrationOverdue(today))
            return Result.Failure(Error.Regulatory("Instrument_CalibrationOverdue", NextCalibrationDue!));

        return Result.Success();
    }

    /// <summary>
    /// The same rule as <see cref="CanProduceResults"/>, from the bare facts, as the resource key an
    /// analyser link reports when it holds results back — or null when the instrument may produce
    /// them. Static so a screen reading a projection can say it without loading the entity.
    /// </summary>
    public static string? ResultBlockKey(EquipmentStatus status, DateOnly? decommissionedOn,
                                         DateOnly? calibrationDue, DateOnly today) =>
        status != EquipmentStatus.Operational ? $"Interface_InstrumentNotOperational_{status}"
        : decommissionedOn is { } gone && gone <= today ? "Interface_InstrumentDecommissioned"
        : calibrationDue is { } due && due < today ? "Interface_InstrumentCalibrationOverdue"
        : null;

    public string? ResultBlockKey(DateOnly today) =>
        ResultBlockKey(Status, DecommissioningDate, NextCalibrationDue, today);

    // ---- Transitions ---------------------------------------------------------------------------

    public void PlaceOutOfService(string reason)
    {
        Status = EquipmentStatus.OutOfService;
        LastStatusReason = reason;
    }

    public void PlaceUnderMaintenance(string reason)
    {
        Status = EquipmentStatus.UnderMaintenance;
        LastStatusReason = reason;
    }

    /// <summary>
    /// Returns the instrument to service. Refused while a calibration is overdue — bringing an
    /// analyser back without recalibrating is precisely the shortcut this guard exists to block.
    /// </summary>
    public Result ReturnToService(DateOnly today)
    {
        if (IsCalibrationOverdue(today))
            return Result.Failure(Error.Regulatory("Instrument_CalibrationRequiredBeforeReturn"));

        Status = EquipmentStatus.Operational;
        LastStatusReason = null;
        return Result.Success();
    }

    public void Decommission(DateOnly on, string reason)
    {
        DecommissioningDate = on;
        Status = EquipmentStatus.Decommissioned;
        LastStatusReason = reason;
    }

    public string? LastStatusReason { get; private set; }

    public MaintenanceRecord AddMaintenance(MaintenanceRecord record)
    {
        record.AttachTo(Id);
        _maintenanceRecords.Add(record);

        if (record.Kind == MaintenanceKind.Preventive &&
            (LastPreventiveMaintenanceOn is null || record.PerformedOn > LastPreventiveMaintenanceOn))
            LastPreventiveMaintenanceOn = record.PerformedOn;

        return record;
    }

    public CalibrationRecord AddCalibration(CalibrationRecord record)
    {
        record.AttachTo(Id);
        _calibrations.Add(record);

        if (record.Outcome == CalibrationOutcome.Passed &&
            (LastCalibrationOn is null || record.PerformedOn > LastCalibrationOn))
            LastCalibrationOn = record.PerformedOn;

        return record;
    }
}

public enum EquipmentKind
{
    /// <summary>An analyser that produces results, usually interfaced to the LIS.</summary>
    Analyser = 0,
    Centrifuge = 1,
    Refrigerator = 2,
    Freezer = 3,
    Incubator = 4,
    Microscope = 5,
    Pipette = 6,
    Balance = 7,
    WaterBath = 8,
    Autoclave = 9,
    Thermometer = 10,
    BarcodePrinter = 11,
    Other = 99
}

public enum EquipmentStatus
{
    Operational = 0,
    UnderMaintenance = 1,
    /// <summary>Broken down or withdrawn pending investigation.</summary>
    OutOfService = 2,
    /// <summary>In qualification before first use.</summary>
    Commissioning = 3,
    Decommissioned = 4,
    /// <summary>On loan or standing in for another instrument.</summary>
    Backup = 5
}

/// <summary>A maintenance, repair or verification operation, with who did it and what was done.</summary>
public sealed class MaintenanceRecord : Entity, IAuditable
{
    private MaintenanceRecord() { }

    public MaintenanceRecord(MaintenanceKind kind, DateOnly performedOn, MultilingualText description)
    {
        Kind = kind;
        PerformedOn = performedOn;
        Description = description;
    }

    public Guid InstrumentId { get; private set; }
    internal void AttachTo(Guid instrumentId) => InstrumentId = instrumentId;

    public MaintenanceKind Kind { get; private set; }
    public DateOnly PerformedOn { get; private set; }
    public MultilingualText Description { get; set; } = new();

    /// <summary>Laboratory member who performed or witnessed it.</summary>
    public Guid? PerformedByUserId { get; set; }

    /// <summary>External technician, when the supplier did the work.</summary>
    public string? ExternalTechnicianName { get; set; }
    public string? SupplierInterventionReference { get; set; }

    /// <summary>Downtime caused, in hours — feeds the availability indicator.</summary>
    public decimal? DowntimeHours { get; set; }

    public decimal? CostMad { get; set; }
    public string? PartsReplaced { get; set; }

    /// <summary>Whether the instrument was verified as fit for use afterwards.</summary>
    public bool PostInterventionCheckPassed { get; set; }
    public string? PostInterventionCheckNotes { get; set; }

    /// <summary>Non-conformity raised when the intervention followed a failure with patient impact.</summary>
    public Guid? NonConformityId { get; set; }

    public byte[]? AttachedReport { get; set; }
    public string? AttachedReportContentType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}

public enum MaintenanceKind
{
    /// <summary>Planned preventive maintenance.</summary>
    Preventive = 0,
    /// <summary>Repair after a breakdown.</summary>
    Corrective = 1,
    /// <summary>Daily or weekly operator upkeep.</summary>
    Routine = 2,
    /// <summary>Metrological verification against a reference.</summary>
    Verification = 3,
    /// <summary>Qualification before first use or after a major change.</summary>
    Qualification = 4,
    /// <summary>Software or firmware update.</summary>
    SoftwareUpdate = 5,
    Cleaning = 6,
    Relocation = 7
}

/// <summary>
/// A calibration of one measurement channel. Kept per parameter because an analyser is calibrated
/// assay by assay, and a result must be traceable to the calibration in force when it was produced.
/// </summary>
public sealed class CalibrationRecord : Entity, IAuditable
{
    private CalibrationRecord() { }

    public CalibrationRecord(DateOnly performedOn, DateTimeOffset performedAt, Guid performedByUserId)
    {
        PerformedOn = performedOn;
        PerformedAt = performedAt;
        PerformedByUserId = performedByUserId;
    }

    public Guid InstrumentId { get; private set; }
    internal void AttachTo(Guid instrumentId) => InstrumentId = instrumentId;

    /// <summary>Null when the calibration covers the whole instrument rather than one assay.</summary>
    public Guid? TestParameterId { get; set; }

    public DateOnly PerformedOn { get; private set; }
    public DateTimeOffset PerformedAt { get; private set; }
    public Guid PerformedByUserId { get; private set; }

    /// <summary>Calibrator lot — the traceability link to a reference material.</summary>
    public string? CalibratorLotNumber { get; set; }
    public DateOnly? CalibratorExpiryDate { get; set; }
    public string? CalibratorManufacturer { get; set; }

    /// <summary>Reagent lot the calibration is bound to; a lot change normally invalidates it.</summary>
    public Guid? ReagentLotId { get; set; }

    public CalibrationOutcome Outcome { get; set; } = CalibrationOutcome.Passed;

    /// <summary>Slope, intercept and correlation, where the method reports them.</summary>
    public decimal? Slope { get; set; }
    public decimal? Intercept { get; set; }
    public decimal? CorrelationCoefficient { get; set; }

    /// <summary>Raw calibration curve or instrument printout.</summary>
    public string? CurveData { get; set; }

    /// <summary>When the calibration stops being valid.</summary>
    public DateOnly? ValidUntil { get; set; }

    public string? Comment { get; set; }

    /// <summary>Verification that the calibration holds, typically a control run straight after.</summary>
    public bool VerifiedByQc { get; set; }
    public Guid? VerifyingQcResultId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public bool IsValidOn(DateOnly date) =>
        Outcome == CalibrationOutcome.Passed && (ValidUntil is null || ValidUntil >= date);
}

public enum CalibrationOutcome
{
    Passed = 0,
    Failed = 1,
    /// <summary>Accepted with a documented reservation.</summary>
    PassedWithReservation = 2,
    /// <summary>Abandoned before completion.</summary>
    Aborted = 3
}
