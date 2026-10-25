using System.Globalization;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Administration;

// ---- Forms -------------------------------------------------------------------------------------
// What each editor of « Référentiels » holds while it is typed in: the boxes as text, the lists as
// their ids, the ticks and the spinners as they are. The workstation's dialogs and the web's forms
// fill one, check it and turn it into the service's request with the same rules. The properties can
// be set, so the web's fields write into the form they show.

public sealed record InstrumentForm
{
    public string Code { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public EquipmentKind Kind { get; set; } = EquipmentKind.Analyser;
    public string Location { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SoftwareVersion { get; set; } = string.Empty;
    public Guid? SupplierId { get; set; }
    public string Contract { get; set; } = string.Empty;
    public string SupportPhone { get; set; } = string.Empty;
    public DateOnly? ContractValidUntil { get; set; }
    public DateOnly? CommissioningDate { get; set; }
    public int MaintenanceIntervalDays { get; set; }
    public int CalibrationIntervalDays { get; set; }
    public int VerificationIntervalDays { get; set; }
    public DateOnly? LastMaintenanceOn { get; set; }
    public DateOnly? LastCalibrationOn { get; set; }
    public DateOnly? LastVerificationOn { get; set; }
}

public sealed record QcMaterialForm
{
    public string LotNumber { get; set; } = string.Empty;
    public QcLevel Level { get; set; } = QcLevel.Normal;
    public DateOnly ExpiryDate { get; set; }
    public string NameFr { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string CatalogueReference { get; set; } = string.Empty;
    public DateOnly? OpenedOn { get; set; }
    public int StabilityAfterOpeningDays { get; set; }
    public bool Active { get; set; } = true;
}

public sealed record QcTargetForm
{
    public Guid? MaterialId { get; set; }
    public Guid? InstrumentId { get; set; }
    public Guid? ParameterId { get; set; }
    public string Mean { get; set; } = string.Empty;
    public string Sd { get; set; } = string.Empty;
    public string MaxCv { get; set; } = string.Empty;
    public WestgardRule Rules { get; set; } = MasterDataScreenRules.ClassicMultirule;
    public bool Active { get; set; } = true;
}

public sealed record StockItemForm
{
    public string Code { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public StockItemKind Kind { get; set; } = StockItemKind.Reagent;
    public string Unit { get; set; } = "u";
    public int TestsPerPack { get; set; }
    public StorageCondition Storage { get; set; } = StorageCondition.RoomTemperature;
    public string Manufacturer { get; set; } = string.Empty;
    public Guid? SupplierId { get; set; }
    public string SupplierReference { get; set; } = string.Empty;
    public string UsedByAnalyses { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Minimum { get; set; } = string.Empty;
    public string Reorder { get; set; } = string.Empty;
    public string ReorderQuantity { get; set; } = string.Empty;
    public string Maximum { get; set; } = string.Empty;
    public bool Critical { get; set; }
    public bool Hazardous { get; set; }
    public string HazardClass { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public string Gtin { get; set; } = string.Empty;
    public bool CeMarked { get; set; }
    public string TestsPerResult { get; set; } = string.Empty;
    public string DeadVolumePercent { get; set; } = string.Empty;
    public string DeadVolumeTests { get; set; } = string.Empty;
    public int TestsPerCalibration { get; set; }
    public int SafetyStockDays { get; set; }
    public int ShelfLifeDays { get; set; }
    public bool ReorderPointComputed { get; set; }
}

public sealed record SupplierForm
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Ice { get; set; } = string.Empty;
    public string FiscalId { get; set; } = string.Empty;
    public string CommercialRegister { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public MoroccanRegion Region { get; set; } = MoroccanRegion.Unspecified;
    public int LeadTimeDays { get; set; } = 7;
    public int PaymentTermsDays { get; set; } = PaymentTerms.DefaultDays;
    public string AccountingAccount { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public DateOnly? LastEvaluationDate { get; set; }
    public int EvaluationScore { get; set; }
    public string Notes { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public string Registration { get; set; } = string.Empty;
    public DateOnly? RegistrationValidUntil { get; set; }
}

public sealed record PrescriberForm
{
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string NameArabic { get; set; } = string.Empty;
    public PrescriberKind Kind { get; set; } = PrescriberKind.GeneralPractitioner;
    public string Speciality { get; set; } = string.Empty;
    public string Institution { get; set; } = string.Empty;
    public string OrderNumber { get; set; } = string.Empty;
    public string Inpe { get; set; } = string.Empty;
    public string Ice { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Fax { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string CriticalPhone { get; set; } = string.Empty;
    public ReportDeliveryPreference Delivery { get; set; } = ReportDeliveryPreference.PatientCollects;
    public bool SendPartial { get; set; }
    public string Discount { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
}

public sealed record PayerForm
{
    public string Code { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public PayerKind Kind { get; set; } = PayerKind.AmoCnss;

    /// <summary>The rate as a percentage, the unit a convention is written in.</summary>
    public decimal RatePercent { get; set; } = 70m;

    public int PaymentTermsDays { get; set; } = 60;
    public string Ice { get; set; } = string.Empty;
    public bool UsesReferenceTariff { get; set; } = true;
    public bool RequiresPrescription { get; set; } = true;
    public bool RequiresInpe { get; set; }
    public bool ThirdParty { get; set; }
    public string AgreementNumber { get; set; } = string.Empty;
    public DateOnly? AgreementValidUntil { get; set; }
    public string AccountingAccount { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
}

/// <summary>What a payer's kind does to the form: the boxes it locks and why.</summary>
/// <param name="RateEnabled">Self-pay reimburses nothing: the rate is zero and locked.</param>
/// <param name="ThirdPartyEnabled">Self-pay has no convention: no third-party payment.</param>
/// <param name="ReferenceTariffLocked">An AMO scheme settles against the TNR and nothing else.</param>
public sealed record PayerKindFields(bool RateEnabled, bool ThirdPartyEnabled, bool ReferenceTariffLocked);

/// <summary>
/// The rules of the editors of « Référentiels » that need no window: how each opens on what is
/// stored, what it refuses before the service is asked (on the field it names), what a choice locks
/// or says, and the request it sends. The workstation's dialogs and the web's forms apply the same
/// ones; the refusals that need the database — a code taken, a payer still in use — stay
/// MasterDataService's and the domain's.
/// </summary>
public static class MasterDataEditorRules
{
    // The lengths of the columns: a longer value was refused by the database with an unexpected
    // error, and the form was lost with it.
    public const int CodeMaxLength = 30;
    public const int StockCodeMaxLength = 40;
    public const int NameMaxLength = 300;
    public const int LongNameMaxLength = 200;
    public const int ManufacturerMaxLength = 150;
    public const int ReferenceMaxLength = 100;
    public const int SoftwareMaxLength = 60;
    public const int PhoneMaxLength = 30;
    public const int ShortPhoneMaxLength = 20;
    public const int LotMaxLength = 60;
    public const int UnitMaxLength = 30;
    public const int AnalysesMaxLength = 2000;
    public const int HazardClassMaxLength = 60;
    public const int GtinMaxLength = 14;
    public const int TestsPerResultMaxLength = 500;
    public const int IceMaxLength = 15;
    public const int FiscalIdMaxLength = 20;
    public const int RegisterMaxLength = 40;
    public const int AddressMaxLength = 400;
    public const int CityMaxLength = 100;
    public const int NotesMaxLength = 1000;
    public const int RegistrationMaxLength = 60;
    public const int PersonNameMaxLength = 120;
    public const int SpecialityMaxLength = 150;
    public const int OrderNumberMaxLength = 40;
    public const int AgreementMaxLength = 60;

    // The bounds of the number boxes, as the workstation's spinners hold them.
    public const int IntervalMaxDays = 3650;
    public const int TestsPerPackMax = 100_000;
    public const int TestsPerCalibrationMax = 1000;
    public const int SafetyDaysMax = 365;
    public const int LeadTimeMax = 365;
    public const int ScoreMax = 100;
    public const int PayerTermsMax = 365;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? Positive(int value) => value > 0 ? value : null;

    private static string Fixed(decimal? value, string format, IFormatProvider? culture) =>
        value?.ToString(format, culture) ?? string.Empty;

    private static FieldRefusal? Unreadable(params (string Field, string Text)[] boxes) =>
        boxes.Where(b => TypedNumber.IsUnreadable(b.Text))
             .Select(b => new FieldRefusal(b.Field, "Result_NotANumber", Typed: b.Text.Trim()))
             .FirstOrDefault();

    // ---- Instrument ------------------------------------------------------------------------------

    /// <summary>The instrument opened on the whole stored record when editing — the grid row has no software version, supplier or contract.</summary>
    public static InstrumentForm FormOf(InstrumentMasterRow? row, InstrumentRequest? stored)
    {
        var kept = row is null ? null : stored;

        return new InstrumentForm
        {
            Code = row?.Code ?? string.Empty,
            NameFr = row?.Name.Fr ?? string.Empty,
            NameAr = row?.Name.Ar ?? string.Empty,
            Kind = row?.Kind ?? EquipmentKind.Analyser,
            Location = row?.Location ?? string.Empty,
            SerialNumber = row?.SerialNumber ?? string.Empty,
            Manufacturer = row?.Manufacturer ?? string.Empty,
            Model = row?.Model ?? string.Empty,
            SoftwareVersion = kept?.SoftwareVersion ?? string.Empty,
            SupplierId = kept?.SupplierId,
            Contract = kept?.MaintenanceContractReference ?? string.Empty,
            SupportPhone = kept?.SupportPhone ?? string.Empty,
            ContractValidUntil = row?.ContractValidUntil,
            CommissioningDate = kept?.CommissioningDate,
            MaintenanceIntervalDays = Math.Clamp(row?.PreventiveMaintenanceIntervalDays ?? 0, 0, IntervalMaxDays),
            CalibrationIntervalDays = Math.Clamp(row?.CalibrationIntervalDays ?? 0, 0, IntervalMaxDays),
            VerificationIntervalDays = Math.Clamp(row?.VerificationIntervalDays ?? 0, 0, IntervalMaxDays),
            LastMaintenanceOn = kept?.LastPreventiveMaintenanceOn,
            LastCalibrationOn = row?.LastCalibrationOn,
            LastVerificationOn = kept?.LastVerificationOn
        };
    }

    /// <summary>
    /// An analyser needs a calibration interval: the control that stops it producing results while
    /// its calibration is lapsed computes « lapsed » from it. Said while the kind is chosen.
    /// </summary>
    public static bool NeedsCalibrationInterval(EquipmentKind kind) => kind == EquipmentKind.Analyser;

    public static FieldRefusal? Check(InstrumentForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Equipment_CodeRequired");
        if (string.IsNullOrWhiteSpace(form.NameFr)) return new FieldRefusal("namefr", "Equipment_NameRequired");
        if (NeedsCalibrationInterval(form.Kind) && form.CalibrationIntervalDays <= 0)
            return new FieldRefusal("calibration", "Equipment_AnalyserNeedsCalibrationInterval");
        return null;
    }

    /// <summary>
    /// Built on the stored instrument, so a field the editor does not show (the department, the
    /// purchase date) is saved as it was. No English box: the stored English name is kept while the
    /// French one is unchanged.
    /// </summary>
    public static InstrumentRequest ToRequest(InstrumentForm form, Guid? id, InstrumentRequest? stored)
    {
        ArgumentNullException.ThrowIfNull(form);

        var basis = stored ?? new InstrumentRequest { Code = string.Empty, Name = new MultilingualText() };
        var french = form.NameFr.Trim();

        return basis with
        {
            InstrumentId = id,
            Code = form.Code.Trim(),
            Name = new MultilingualText(french, form.NameAr.Trim(), stored?.Name.Fr == french ? stored.Name.En : null),
            Kind = form.Kind,
            Manufacturer = Blank(form.Manufacturer),
            Model = Blank(form.Model),
            SerialNumber = Blank(form.SerialNumber),
            SoftwareVersion = Blank(form.SoftwareVersion),
            Location = Blank(form.Location),
            SupplierId = form.SupplierId,
            MaintenanceContractReference = Blank(form.Contract),
            MaintenanceContractValidUntil = form.ContractValidUntil,
            SupportPhone = Blank(form.SupportPhone),
            CommissioningDate = form.CommissioningDate,
            PreventiveMaintenanceIntervalDays = Positive(form.MaintenanceIntervalDays),
            CalibrationIntervalDays = Positive(form.CalibrationIntervalDays),
            VerificationIntervalDays = Positive(form.VerificationIntervalDays),
            LastPreventiveMaintenanceOn = form.LastMaintenanceOn,
            LastCalibrationOn = form.LastCalibrationOn,
            LastVerificationOn = form.LastVerificationOn
        };
    }

    // ---- QC material -----------------------------------------------------------------------------

    /// <summary>A material opened on what is stored, or a new lot expiring in a year.</summary>
    public static QcMaterialForm FormOf(QcMaterialRow? row, DateOnly today) => new()
    {
        LotNumber = row?.LotNumber ?? string.Empty,
        Level = row?.Level ?? QcLevel.Normal,
        ExpiryDate = row?.ExpiryDate ?? today.AddMonths(12),
        NameFr = row?.Name.Fr ?? string.Empty,
        NameAr = row?.Name.Ar ?? string.Empty,
        Manufacturer = row?.Manufacturer ?? string.Empty,
        CatalogueReference = row?.CatalogueReference ?? string.Empty,
        OpenedOn = row?.OpenedOn,
        StabilityAfterOpeningDays = Math.Clamp(row?.StabilityAfterOpeningDays ?? 0, 0, IntervalMaxDays),
        Active = row?.IsActive ?? true
    };

    /// <summary>Lot plus level is the natural key: both are set once.</summary>
    public static bool KeyEditable(QcMaterialRow? row) => row is null;

    public static FieldRefusal? Check(QcMaterialForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.LotNumber)) return new FieldRefusal("lot", "QcMaterial_LotRequired");
        if (string.IsNullOrWhiteSpace(form.NameFr)) return new FieldRefusal("namefr", "QcMaterial_NameRequired");
        return null;
    }

    public static QcMaterialRequest ToRequest(QcMaterialForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new QcMaterialRequest
        {
            QcMaterialId = id,
            LotNumber = form.LotNumber.Trim(),
            Name = new MultilingualText(form.NameFr.Trim(), form.NameAr.Trim()),
            Level = form.Level,
            ExpiryDate = form.ExpiryDate,
            Manufacturer = Blank(form.Manufacturer),
            CatalogueReference = Blank(form.CatalogueReference),
            OpenedOn = form.OpenedOn,
            StabilityAfterOpeningDays = Positive(form.StabilityAfterOpeningDays),
            IsActive = form.Active
        };
    }

    // ---- QC target -------------------------------------------------------------------------------

    /// <summary>
    /// What a target needs before one can be entered: a material, an instrument and a parameter to
    /// define it against — the key of the warning, or null when all three exist.
    /// </summary>
    public static string? MissingPrerequisite(int materials, int instruments, int parameters) =>
        materials == 0 ? "Quality.NeedMaterialFirst"
        : instruments == 0 ? "Quality.NeedInstrumentFirst"
        : parameters == 0 ? "Quality.NeedParameterFirst"
        : null;

    /// <summary>A target opened on what is stored, or a new one running the classic multirule set.</summary>
    public static QcTargetForm FormOf(QcTargetRow? row, IFormatProvider? culture = null) => new()
    {
        MaterialId = row?.QcMaterialId,
        InstrumentId = row?.InstrumentId,
        ParameterId = row?.TestParameterId,
        Mean = Fixed(row?.AssignedMean, "0.###", culture),
        Sd = Fixed(row?.AssignedSd, "0.###", culture),
        MaxCv = Fixed(row?.MaxAcceptableCvPercent, "0.##", culture),
        Rules = row?.ActiveRules ?? MasterDataScreenRules.ClassicMultirule,
        Active = row?.IsActive ?? true
    };

    /// <summary>The material, instrument and parameter of a target are its key: set once.</summary>
    public static bool KeyEditable(QcTargetRow? row) => row is null;

    /// <summary>
    /// The coefficient of variation the mean and SD typed imply — the number a biologist judges a
    /// method by — as « CV 2.5 % », or nothing without both.
    /// </summary>
    public static string CvEcho(string? mean, string? sd, IFormatProvider? culture = null) =>
        TypedNumber.Parse(mean) is > 0m and var m && TypedNumber.Parse(sd) is > 0m and var s
            ? $"CV {(s / m * 100m).ToString("0.##", culture)} %"
            : string.Empty;

    public static FieldRefusal? Check(QcTargetForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (Unreadable(("mean", form.Mean), ("sd", form.Sd), ("cv", form.MaxCv)) is { } unreadable) return unreadable;
        if (form.MaterialId is null || form.InstrumentId is null || form.ParameterId is null)
            return new FieldRefusal("material", "Message.NothingSelected");
        if (form.Rules == WestgardRule.None) return new FieldRefusal("rules", "QcTarget_AtLeastOneRuleRequired");
        return null;
    }

    public static QcTargetRequest ToRequest(QcTargetForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new QcTargetRequest
        {
            QcTargetId = id,
            QcMaterialId = form.MaterialId!.Value,
            InstrumentId = form.InstrumentId!.Value,
            TestParameterId = form.ParameterId!.Value,
            AssignedMean = TypedNumber.Parse(form.Mean),
            AssignedSd = TypedNumber.Parse(form.Sd),
            MaxAcceptableCvPercent = TypedNumber.Parse(form.MaxCv),
            ActiveRules = form.Rules,
            IsActive = form.Active
        };
    }

    // ---- Stock item ------------------------------------------------------------------------------

    /// <summary>An article opened on what the row round-trips, or a new reagent counted in units.</summary>
    public static StockItemForm FormOf(StockItemMasterRow? row, IFormatProvider? culture = null)
    {
        var policy = row?.ConsumptionPolicy ?? new StockConsumptionPolicy();

        return new StockItemForm
        {
            Code = row?.Code ?? string.Empty,
            NameFr = row?.Name.Fr ?? string.Empty,
            NameAr = row?.Name.Ar ?? string.Empty,
            Kind = row?.Kind ?? StockItemKind.Reagent,
            Unit = row?.UnitOfMeasure ?? "u",
            TestsPerPack = Math.Clamp(row?.TestsPerPack ?? 0, 0, TestsPerPackMax),
            Storage = row?.StorageCondition ?? StorageCondition.RoomTemperature,
            Manufacturer = row?.Manufacturer ?? string.Empty,
            SupplierId = row?.PreferredSupplierId,
            SupplierReference = row?.SupplierCatalogueReference ?? string.Empty,
            UsedByAnalyses = row?.UsedByAnalysisCodesCsv ?? string.Empty,
            Location = row?.DefaultStorageLocation ?? string.Empty,
            Minimum = Fixed(row?.MinimumStock, "0.##", culture),
            Reorder = Fixed(row?.ReorderPoint, "0.##", culture),
            ReorderQuantity = Fixed(row?.ReorderQuantity, "0.##", culture),
            Maximum = Fixed(row?.MaximumStock, "0.##", culture),
            Critical = row?.IsCritical ?? false,
            Hazardous = row?.IsHazardous ?? false,
            HazardClass = row?.HazardClass ?? string.Empty,
            Active = row?.IsActive ?? true,
            Gtin = row?.Gtin ?? string.Empty,
            CeMarked = row?.IsCeMarked ?? false,
            TestsPerResult = policy.TestsPerResultCsv ?? string.Empty,
            DeadVolumePercent = Fixed(policy.DeadVolumePercent, "0.###", culture),
            DeadVolumeTests = Fixed(policy.DeadVolumeTestsPerOpening, "0.###", culture),
            TestsPerCalibration = Math.Clamp(policy.TestsPerCalibration ?? 0, 0, TestsPerCalibrationMax),
            SafetyStockDays = Math.Clamp(policy.SafetyStockDays ?? 0, 0, SafetyDaysMax),
            ShelfLifeDays = Math.Clamp(policy.TypicalShelfLifeDays ?? 0, 0, IntervalMaxDays),
            ReorderPointComputed = policy.ReorderPointComputed
        };
    }

    /// <summary>The hazard class means something only for a hazardous article.</summary>
    public static bool HazardClassEnabled(StockItemForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.Hazardous;
    }

    /// <summary>
    /// What the article editor refuses before the service is asked: a box holding no number, the
    /// code, the name and the unit, a reorder point below the safety stock — the alert would fire
    /// after the laboratory is already below the level it meant to hold — and a dead volume that is
    /// no number.
    /// </summary>
    public static FieldRefusal? Check(StockItemForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (Unreadable(("minimum", form.Minimum), ("reorder", form.Reorder), ("reorderquantity", form.ReorderQuantity),
                       ("maximum", form.Maximum)) is { } unreadable)
            return unreadable;

        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Stock_CodeRequired");
        if (string.IsNullOrWhiteSpace(form.NameFr)) return new FieldRefusal("namefr", "Stock_NameRequired");
        if (string.IsNullOrWhiteSpace(form.Unit)) return new FieldRefusal("unit", "Stock_UnitRequired");

        if ((TypedNumber.Parse(form.Reorder) ?? 0m) < (TypedNumber.Parse(form.Minimum) ?? 0m))
            return new FieldRefusal("reorder", "Stock_ReorderBelowMinimum");

        if (!string.IsNullOrWhiteSpace(form.DeadVolumePercent) && TypedNumber.Parse(form.DeadVolumePercent) is null
            || !string.IsNullOrWhiteSpace(form.DeadVolumeTests) && TypedNumber.Parse(form.DeadVolumeTests) is null)
            return new FieldRefusal("deadpercent", "Stock_DeadVolumeOutOfRange");

        return null;
    }

    /// <summary>The consumption settings typed, which the domain validates (<see cref="StockConsumptionPolicy.Validate"/>).</summary>
    public static StockConsumptionPolicy Policy(StockItemForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new StockConsumptionPolicy
        {
            TestsPerResultCsv = Blank(form.TestsPerResult),
            DeadVolumePercent = TypedNumber.Parse(form.DeadVolumePercent),
            DeadVolumeTestsPerOpening = TypedNumber.Parse(form.DeadVolumeTests),
            TestsPerCalibration = Positive(form.TestsPerCalibration),
            SafetyStockDays = Positive(form.SafetyStockDays),
            TypicalShelfLifeDays = Positive(form.ShelfLifeDays),
            ReorderPointComputed = form.ReorderPointComputed
        };
    }

    public static StockItemRequest ToRequest(StockItemForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new StockItemRequest
        {
            StockItemId = id,
            Code = form.Code.Trim(),
            Name = new MultilingualText(form.NameFr.Trim(), form.NameAr.Trim()),
            Kind = form.Kind,
            UnitOfMeasure = form.Unit.Trim(),
            TestsPerPack = Positive(form.TestsPerPack),
            PreferredSupplierId = form.SupplierId,
            SupplierCatalogueReference = Blank(form.SupplierReference),
            Manufacturer = Blank(form.Manufacturer),
            UsedByAnalysisCodesCsv = Blank(form.UsedByAnalyses),
            ReorderPoint = TypedNumber.Parse(form.Reorder) ?? 0m,
            ReorderQuantity = TypedNumber.Parse(form.ReorderQuantity) ?? 0m,
            MinimumStock = TypedNumber.Parse(form.Minimum) ?? 0m,
            MaximumStock = TypedNumber.Parse(form.Maximum),
            StorageCondition = form.Storage,
            DefaultStorageLocation = Blank(form.Location),
            IsHazardous = form.Hazardous,
            HazardClass = Blank(form.HazardClass),
            IsCritical = form.Critical,
            IsActive = form.Active,
            Gtin = Blank(form.Gtin),
            IsCeMarked = form.CeMarked,
            ConsumptionPolicy = Policy(form)
        };
    }

    // ---- Supplier --------------------------------------------------------------------------------

    public static SupplierForm FormOf(SupplierRow? row) => new()
    {
        Code = row?.Code ?? string.Empty,
        Name = row?.Name ?? string.Empty,
        Ice = row?.Ice ?? string.Empty,
        FiscalId = row?.FiscalId ?? string.Empty,
        CommercialRegister = row?.CommercialRegisterNumber ?? string.Empty,
        Contact = row?.ContactName ?? string.Empty,
        Phone = row?.Phone ?? string.Empty,
        Email = row?.Email ?? string.Empty,
        City = row?.City ?? string.Empty,
        Address = row?.AddressLine ?? string.Empty,
        Region = row?.Region ?? MoroccanRegion.Unspecified,
        LeadTimeDays = Math.Clamp(row?.LeadTimeDays ?? 7, 0, LeadTimeMax),
        // Loi n° 69-21: 60 days by default, 120 at most by agreement.
        PaymentTermsDays = Math.Clamp(row?.PaymentTermsDays ?? PaymentTerms.DefaultDays, 0, PaymentTerms.MaximumDays),
        AccountingAccount = row?.AccountingAccount ?? string.Empty,
        Approved = row?.IsApproved ?? false,
        LastEvaluationDate = row?.LastEvaluationDate,
        EvaluationScore = Math.Clamp(row?.EvaluationScore ?? 0, 0, ScoreMax),
        Notes = row?.Notes ?? string.Empty,
        Active = row?.IsActive ?? true,
        Registration = row?.RegistrationNumber ?? string.Empty,
        RegistrationValidUntil = row?.RegistrationValidUntil
    };

    /// <summary>
    /// The approval only means something with an evaluation behind it: ISO 15189 §4.6 is about
    /// having assessed the supplier, not about having declared them approved. Said before Save.
    /// </summary>
    public static bool ApprovalNeedsEvaluation(SupplierForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.Approved && form.LastEvaluationDate is null;
    }

    /// <summary>Approving a supplier whose AMMPS registration has lapsed is allowed, but said.</summary>
    public static bool RegistrationExpired(SupplierForm form, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.RegistrationValidUntil is { } until && until < today;
    }

    public static FieldRefusal? Check(SupplierForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Supplier_CodeRequired");
        if (string.IsNullOrWhiteSpace(form.Name)) return new FieldRefusal("name", "Supplier_NameRequired");
        if (ApprovalNeedsEvaluation(form)) return new FieldRefusal("evaluation", "Supplier_ApprovalNeedsEvaluation");
        return null;
    }

    public static SupplierRequest ToRequest(SupplierForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new SupplierRequest
        {
            SupplierId = id,
            Code = form.Code.Trim(),
            Name = form.Name.Trim(),
            Ice = Blank(form.Ice),
            FiscalId = Blank(form.FiscalId),
            CommercialRegisterNumber = Blank(form.CommercialRegister),
            ContactName = Blank(form.Contact),
            Phone = Blank(form.Phone),
            Email = Blank(form.Email),
            AddressLine = Blank(form.Address),
            City = Blank(form.City),
            Region = form.Region,
            LeadTimeDays = Positive(form.LeadTimeDays),
            PaymentTermsDays = form.PaymentTermsDays,
            AccountingAccount = Blank(form.AccountingAccount),
            IsApproved = form.Approved,
            LastEvaluationDate = form.LastEvaluationDate,
            EvaluationScore = Positive(form.EvaluationScore),
            Notes = Blank(form.Notes),
            IsActive = form.Active,
            RegulatoryRegistrationNumber = Blank(form.Registration),
            RegulatoryRegistrationValidUntil = form.RegistrationValidUntil
        };
    }

    // ---- Prescriber ------------------------------------------------------------------------------

    /// <summary>
    /// The prescriber opened on the whole stored record: the stored names as they are — splitting
    /// the displayed full name at its first space moved half of a compound surname into the first
    /// name on every save — and each number in its own box. A new one starts from the name already
    /// typed in the order counter's list.
    /// </summary>
    public static PrescriberForm FormOf(PrescriberRow? row, PrescriberRequest? stored, string? typedName = null,
                                        IFormatProvider? culture = null)
    {
        var kept = row is null ? null : stored;
        var (last, first) = PrescriberEntryRules.SplitTypedName(row?.FullName ?? typedName);

        return new PrescriberForm
        {
            LastName = kept?.LastName ?? last,
            FirstName = kept?.FirstName ?? first ?? string.Empty,
            NameArabic = row?.FullNameArabic ?? string.Empty,
            Kind = row?.Kind ?? PrescriberKind.GeneralPractitioner,
            Speciality = row?.Speciality?.Fr ?? string.Empty,
            Institution = row?.InstitutionName ?? string.Empty,
            OrderNumber = row?.ProfessionalOrderNumber ?? string.Empty,
            Inpe = row?.Inpe ?? string.Empty,
            Ice = kept?.Ice ?? string.Empty,
            Phone = kept?.Phone ?? string.Empty,
            // The row carries one telephone (the mobile, else the landline).
            Mobile = (kept is null ? row?.Phone : kept.MobilePhone) ?? string.Empty,
            Fax = kept?.Fax ?? string.Empty,
            Email = row?.Email ?? string.Empty,
            City = row?.City ?? string.Empty,
            Address = kept?.AddressLine ?? string.Empty,
            CriticalPhone = kept?.CriticalResultPhone ?? string.Empty,
            Delivery = row?.DeliveryPreference ?? ReportDeliveryPreference.PatientCollects,
            SendPartial = row?.SendPartialReports ?? false,
            Discount = Fixed(row?.DiscountPercent, "0.##", culture),
            Notes = kept?.Notes ?? string.Empty,
            Active = row?.IsActive ?? true
        };
    }

    /// <summary>A report sent by e-mail, SMS link or fax needs the address or number it goes to.</summary>
    public static bool DeliveryContactMissing(PrescriberForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return form.Delivery switch
        {
            ReportDeliveryPreference.Email => string.IsNullOrWhiteSpace(form.Email),
            ReportDeliveryPreference.SmsLink => string.IsNullOrWhiteSpace(form.Mobile),
            ReportDeliveryPreference.Fax => string.IsNullOrWhiteSpace(form.Fax),
            _ => false
        };
    }

    public static FieldRefusal? Check(PrescriberForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (Unreadable(("discount", form.Discount)) is { } unreadable) return unreadable;
        return PrescriberEntryRules.Problem(form.LastName) is { } problem ? new FieldRefusal("lastname", problem) : null;
    }

    /// <summary>Built on the stored prescriber, so a field the editor does not show (the default payer) is saved as it was.</summary>
    public static PrescriberRequest ToRequest(PrescriberForm form, Guid? id, PrescriberRequest? stored)
    {
        ArgumentNullException.ThrowIfNull(form);

        var basis = stored ?? new PrescriberRequest { LastName = string.Empty, FirstName = string.Empty };

        return basis with
        {
            PrescriberId = id,
            LastName = form.LastName.Trim(),
            FirstName = form.FirstName.Trim(),
            FullNameArabic = Blank(form.NameArabic),
            Kind = form.Kind,
            Speciality = CatalogEditorRules.FrenchEdited(stored?.Speciality, form.Speciality),
            ProfessionalOrderNumber = Blank(form.OrderNumber),
            Inpe = Blank(form.Inpe),
            Ice = Blank(form.Ice),
            Phone = Blank(form.Phone),
            MobilePhone = Blank(form.Mobile),
            Email = Blank(form.Email),
            Fax = Blank(form.Fax),
            AddressLine = Blank(form.Address),
            City = Blank(form.City),
            InstitutionName = Blank(form.Institution),
            DeliveryPreference = form.Delivery,
            SendPartialReports = form.SendPartial,
            CriticalResultPhone = Blank(form.CriticalPhone),
            DiscountPercent = TypedNumber.Parse(form.Discount),
            Notes = Blank(form.Notes),
            IsActive = form.Active
        };
    }

    // ---- Payer -----------------------------------------------------------------------------------

    public static PayerForm FormOf(PayerRow? row) => new()
    {
        Code = row?.Code ?? string.Empty,
        NameFr = row?.Name.Fr ?? string.Empty,
        NameAr = row?.Name.Ar ?? string.Empty,
        NameEn = row?.Name.En ?? string.Empty,
        Kind = row?.Kind ?? PayerKind.AmoCnss,
        RatePercent = Math.Clamp(row?.ReimbursementPercent ?? 70m, 0m, 100m),
        PaymentTermsDays = Math.Clamp(row?.PaymentTermsDays ?? 60, 0, PayerTermsMax),
        Ice = row?.Ice ?? string.Empty,
        UsesReferenceTariff = row?.UsesNationalReferenceTariff ?? true,
        RequiresPrescription = row?.RequiresPrescription ?? true,
        RequiresInpe = row?.RequiresInpeOnClaim ?? false,
        ThirdParty = row?.ThirdPartyPaymentAgreement ?? false,
        AgreementNumber = row?.AgreementNumber ?? string.Empty,
        AgreementValidUntil = row?.AgreementValidUntil,
        AccountingAccount = row?.AccountingAccount ?? string.Empty,
        Contact = row?.ContactName ?? string.Empty,
        Phone = row?.Phone ?? string.Empty,
        Email = row?.Email ?? string.Empty,
        Address = row?.AddressLine ?? string.Empty,
        City = row?.City ?? string.Empty,
        Active = row?.IsActive ?? true
    };

    /// <summary>The compulsory schemes (AMO), which settle against the TNR: the notice says so.</summary>
    public static bool IsCompulsory(PayerKind kind) =>
        kind is PayerKind.AmoCnss or PayerKind.AmoCnops or PayerKind.AmoTadamon or PayerKind.AmoAchamil;

    /// <summary>What <c>Payer.Validate</c> would otherwise refuse after Save, applied to the boxes as the kind changes.</summary>
    public static PayerKindFields Fields(PayerKind kind) =>
        new(RateEnabled: kind != PayerKind.SelfPay, ThirdPartyEnabled: kind != PayerKind.SelfPay,
            ReferenceTariffLocked: IsCompulsory(kind));

    /// <summary>
    /// A kind chosen: self-pay reimburses nothing and has no convention, so the rate is zeroed and
    /// third-party payment unticked; an AMO scheme settles against the TNR, which is ticked.
    /// </summary>
    public static PayerForm WithKind(PayerForm form, PayerKind kind)
    {
        ArgumentNullException.ThrowIfNull(form);

        var chosen = form with { Kind = kind };
        if (kind == PayerKind.SelfPay) chosen = chosen with { RatePercent = 0m, ThirdParty = false };
        if (IsCompulsory(kind)) chosen = chosen with { UsesReferenceTariff = true };
        return chosen;
    }

    /// <summary>The agreement's number and validity mean something only with third-party payment.</summary>
    public static bool AgreementEnabled(PayerForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.ThirdParty && Fields(form.Kind).ThirdPartyEnabled;
    }

    public static FieldRefusal? Check(PayerForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Code)) return new FieldRefusal("code", "Payer_CodeRequired");
        if (string.IsNullOrWhiteSpace(form.NameFr)) return new FieldRefusal("namefr", "Payer_NameFrenchRequired");
        return null;
    }

    /// <summary>Percentage in, fraction out — the one conversion this editor exists to get right.</summary>
    public static PayerRequest ToRequest(PayerForm form, Guid? id)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new PayerRequest
        {
            PayerId = id,
            Code = form.Code.Trim().ToUpperInvariant(),
            Name = new MultilingualText(form.NameFr.Trim(), Blank(form.NameAr), Blank(form.NameEn)),
            Kind = form.Kind,
            DefaultReimbursementRate = form.RatePercent / 100m,
            UsesNationalReferenceTariff = form.UsesReferenceTariff,
            ThirdPartyPaymentAgreement = form.ThirdParty,
            AgreementNumber = form.ThirdParty ? Blank(form.AgreementNumber) : null,
            AgreementValidUntil = form.ThirdParty ? form.AgreementValidUntil : null,
            PaymentTermsDays = form.PaymentTermsDays,
            RequiresPrescription = form.RequiresPrescription,
            RequiresInpeOnClaim = form.RequiresInpe,
            Ice = Blank(form.Ice),
            ContactName = Blank(form.Contact),
            Phone = Blank(form.Phone),
            Email = Blank(form.Email),
            AddressLine = Blank(form.Address),
            City = Blank(form.City),
            AccountingAccount = Blank(form.AccountingAccount),
            IsActive = form.Active
        };
    }
}
