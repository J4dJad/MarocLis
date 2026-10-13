using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Catalog;

/// <summary>
/// A technical discipline of the laboratory (secteur technique). Drives report sectioning, the
/// validation scope of each biologist, and the ISO 15189 accreditation scope.
/// </summary>
public sealed class Department : AuditableAggregateRoot
{
    private Department() { }

    public Department(string code, MultilingualText name)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
    }

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();

    /// <summary>Order of the section on the printed report.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Whether this discipline is inside the laboratory's accreditation scope.</summary>
    public bool IsInAccreditationScope { get; set; }

    /// <summary>Biologist responsible for the discipline.</summary>
    public Guid? SupervisorUserId { get; set; }

    public bool IsActive { get; set; } = true;

    // Not ISoftDeletable, for the same reason as AnalysisTest — which is also how this one was
    // found. AnalysisTest requires a Department, and while both were filtered the pair matched, so
    // EF said nothing; the moment AnalysisTest stopped being soft-deletable this became an
    // unmatched filtered principal and the escalated warning threw on the next model build.
    //
    // A discipline is retired with IsActive. It has to keep resolving after that: it sections the
    // printed report and defines the ISO 15189 accreditation scope a past report was issued under,
    // so a report reprinted years later must still group its analyses the way it did originally.

    /// <summary>Discipline codes seeded on first run; they match Moroccan laboratory practice.</summary>
    public static class Codes
    {
        public const string Biochemistry = "BIOCH";
        public const string Haematology = "HEMA";
        public const string Haemostasis = "HEMOST";
        public const string Immunology = "IMMUNO";
        public const string Serology = "SERO";
        public const string Hormonology = "HORMO";
        public const string Bacteriology = "BACTE";
        public const string Parasitology = "PARASITO";
        public const string Mycology = "MYCO";
        public const string Virology = "VIRO";
        public const string Genetics = "GENET";
        public const string Toxicology = "TOXICO";
        public const string Cytology = "CYTO";
        public const string UrineAnalysis = "URINE";
    }
}

/// <summary>
/// A kind of biological material (nature du prélèvement): venous blood, urine, CSF, stool, swab…
/// </summary>
public sealed class SpecimenType : AuditableAggregateRoot
{
    private SpecimenType() { }

    public SpecimenType(string code, MultilingualText name)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
    }

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();

    /// <summary>Instructions shown to the phlebotomist, e.g. "8 h de jeûne".</summary>
    public MultilingualText? CollectionInstructions { get; set; }

    /// <summary>Instructions handed to the patient for self-collected material (urine, stool).</summary>
    public MultilingualText? PatientInstructions { get; set; }

    /// <summary>Fasting requirement in hours, 0 when none.</summary>
    public int FastingHours { get; set; }

    public bool IsActive { get; set; } = true;

    public static class Codes
    {
        public const string WholeBloodEdta = "SANG_EDTA";
        public const string SerumDryTube = "SERUM";
        public const string PlasmaCitrate = "PLASMA_CIT";
        public const string PlasmaHeparin = "PLASMA_HEP";
        public const string PlasmaFluoride = "PLASMA_FLU";
        public const string Urine = "URINE";
        public const string Urine24H = "URINE_24H";
        public const string Stool = "SELLES";
        public const string Csf = "LCR";
        public const string Sputum = "EXPECTO";
        public const string ThroatSwab = "FROTTIS_GORGE";
        public const string VaginalSwab = "PRELEV_VAGINAL";
        public const string UrethralSwab = "PRELEV_URETRAL";
        public const string WoundSwab = "PRELEV_PLAIE";
        public const string BloodCulture = "HEMOCULTURE";
        public const string Tissue = "TISSU";
    }
}

/// <summary>
/// A physical tube or container, with its additive and the colour of its cap — this is what the
/// phlebotomist actually recognises, and what the barcode label must state.
/// </summary>
public sealed class ContainerType : AuditableAggregateRoot
{
    private ContainerType() { }

    public ContainerType(string code, MultilingualText name, string capColourHex)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
        CapColourHex = capColourHex;
    }

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();

    /// <summary>Cap colour as #RRGGBB, rendered on labels and on the collection worklist.</summary>
    public string CapColourHex { get; set; } = "#FFFFFF";

    /// <summary>Anticoagulant or additive, e.g. "EDTA K2", "Citrate 3.2 %", "Héparine de lithium".</summary>
    public MultilingualText? Additive { get; set; }

    /// <summary>Nominal draw volume in millilitres.</summary>
    public decimal? NominalVolumeMl { get; set; }

    /// <summary>Minimum acceptable volume; below this the sample is rejected as insufficient.</summary>
    public decimal? MinimumVolumeMl { get; set; }

    /// <summary>Order of draw, so multi-tube collections avoid additive carry-over.</summary>
    public int DrawOrder { get; set; }

    public bool RequiresProtectionFromLight { get; set; }
    public bool RequiresIceTransport { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A unit of measurement. UCUM codes are stored so exports and instrument mappings stay
/// unambiguous, while the displayed symbol stays what Moroccan clinicians expect (mg/dl, g/l).
/// </summary>
public sealed class MeasurementUnit : AuditableAggregateRoot
{
    private MeasurementUnit() { }

    public MeasurementUnit(string code, string displaySymbol)
    {
        Code = code;
        DisplaySymbol = displaySymbol;
    }

    public string Code { get; private set; } = string.Empty;

    /// <summary>What appears on the report, e.g. "mg/dL".</summary>
    public string DisplaySymbol { get; set; } = string.Empty;

    /// <summary>Arabic rendering where one is customary; falls back to the Latin symbol.</summary>
    public string? DisplaySymbolArabic { get; set; }

    /// <summary>UCUM case-sensitive code, e.g. "mg/dL".</summary>
    public string? UcumCode { get; set; }

    /// <summary>
    /// Factor to the SI unit of the same quantity, when a conversion is defined. Lets the report
    /// print conventional units while the database keeps one canonical value.
    /// </summary>
    public decimal? FactorToSi { get; set; }
    public string? SiUnitCode { get; set; }

    public bool IsActive { get; set; } = true;
}
