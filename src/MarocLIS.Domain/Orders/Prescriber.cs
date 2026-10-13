using MarocLIS.Domain.Common;
using MarocLIS.Domain.Morocco;

namespace MarocLIS.Domain.Orders;

/// <summary>
/// A prescribing healthcare professional or institution. Kept as a first-class record rather than
/// free text because AMO claims must carry the prescriber's identifier, critical results must be
/// phoned to a traceable person, and prescriber activity drives the laboratory's commercial view.
/// </summary>
public sealed class Prescriber : AuditableAggregateRoot, ISoftDeletable
{
    private Prescriber() { }

    public Prescriber(string lastName, string firstName, PrescriberKind kind)
    {
        LastName = lastName;
        FirstName = firstName;
        Kind = kind;
    }

    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? FullNameArabic { get; set; }

    /// <summary>Normalised search key maintained by the application layer.</summary>
    public string SearchKey { get; set; } = string.Empty;

    public PrescriberKind Kind { get; set; }

    /// <summary>Medical speciality, printed on the report header.</summary>
    public MultilingualText? Speciality { get; set; }

    /// <summary>Registration number with the Ordre National des Médecins.</summary>
    public string? ProfessionalOrderNumber { get; set; }

    /// <summary>INPE — required on AMO claims to identify the prescriber.</summary>
    public string? Inpe { get; set; }

    /// <summary>ICE, when the prescriber is an institution that we invoice directly.</summary>
    public string? Ice { get; set; }

    public string? Phone { get; set; }
    public string? MobilePhone { get; set; }
    public string? Email { get; set; }
    public string? Fax { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public MoroccanRegion Region { get; set; }

    /// <summary>Institution the prescriber practises in (clinique, CHU, cabinet).</summary>
    public string? InstitutionName { get; set; }

    // ---- Result delivery preferences ---------------------------------------------------------

    /// <summary>How validated reports reach this prescriber.</summary>
    public ReportDeliveryPreference DeliveryPreference { get; set; } = ReportDeliveryPreference.PatientCollects;

    /// <summary>Language the prescriber's copy of the report is produced in.</summary>
    public AppLanguage ReportLanguage { get; set; } = AppLanguage.French;

    /// <summary>Whether a copy is sent as soon as each discipline is validated, or only when complete.</summary>
    public bool SendPartialReports { get; set; }

    /// <summary>
    /// Number to call for a critical result, when it differs from the main one. Whoever takes the
    /// call is recorded on the result; ISO 15189 §5.9.1 requires the read-back to be logged.
    /// </summary>
    public string? CriticalResultPhone { get; set; }

    // ---- Commercial --------------------------------------------------------------------------

    /// <summary>Discount granted on the laboratory price list for this prescriber's patients, in percent.</summary>
    public decimal? DiscountPercent { get; set; }

    /// <summary>Convention linking this prescriber to a payer, when applicable.</summary>
    public Guid? DefaultPayerId { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public string FullName => Kind == PrescriberKind.Institution
        ? InstitutionName ?? LastName
        : $"Dr {LastName} {FirstName}".Trim();

    public string DisplayName(AppLanguage language) =>
        language == AppLanguage.Arabic && !string.IsNullOrWhiteSpace(FullNameArabic)
            ? FullNameArabic
            : FullName;

    public Result Validate()
    {
        var errors = new List<Error>();

        if (Kind == PrescriberKind.Institution)
        {
            if (string.IsNullOrWhiteSpace(InstitutionName))
                errors.Add(Error.Validation("Prescriber_InstitutionNameRequired"));
        }
        else if (string.IsNullOrWhiteSpace(LastName))
        {
            errors.Add(Error.Validation("Prescriber_LastNameRequired"));
        }

        if (Inpe is not null && !MoroccanIdentifiers.IsValidInpe(Inpe))
            errors.Add(Error.Validation("Prescriber_InpeInvalid"));

        if (Ice is not null && !MoroccanIdentifiers.IsValidIce(Ice))
            errors.Add(Error.Validation("Prescriber_IceInvalid"));

        if (DiscountPercent is < 0m or > 100m)
            errors.Add(Error.Validation("Prescriber_DiscountOutOfRange"));

        if (DeliveryPreference is ReportDeliveryPreference.Email && string.IsNullOrWhiteSpace(Email))
            errors.Add(Error.Validation("Prescriber_EmailRequiredForDelivery"));

        if (DeliveryPreference is ReportDeliveryPreference.SmsLink && string.IsNullOrWhiteSpace(MobilePhone))
            errors.Add(Error.Validation("Prescriber_MobileRequiredForDelivery"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum PrescriberKind
{
    /// <summary>Médecin généraliste.</summary>
    GeneralPractitioner = 0,

    /// <summary>Médecin spécialiste.</summary>
    Specialist = 1,

    /// <summary>Chirurgien-dentiste.</summary>
    Dentist = 2,

    /// <summary>Sage-femme.</summary>
    Midwife = 3,

    /// <summary>Clinique, hôpital, centre de santé — the order comes from the institution.</summary>
    Institution = 4,

    /// <summary>Another laboratory subcontracting to us.</summary>
    Laboratory = 5,

    /// <summary>Médecin du travail.</summary>
    OccupationalPhysician = 6,

    /// <summary>No prescriber: the patient requested the analysis directly.</summary>
    SelfReferral = 9
}

public enum ReportDeliveryPreference
{
    /// <summary>The patient collects the printed report at the laboratory.</summary>
    PatientCollects = 0,
    Email = 1,
    Fax = 2,
    /// <summary>Delivered by a courier round.</summary>
    Courier = 3,
    /// <summary>Pushed to the prescriber's own software over the integration interface.</summary>
    ElectronicInterface = 4,
    /// <summary>A download link sent by SMS.</summary>
    SmsLink = 5
}
