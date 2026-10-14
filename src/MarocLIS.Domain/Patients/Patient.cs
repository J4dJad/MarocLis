using MarocLIS.Domain.Common;
using MarocLIS.Domain.Morocco;

namespace MarocLIS.Domain.Patients;

/// <summary>
/// A patient file (dossier patient). Personal data here falls under loi n° 08-09 on the protection
/// of individuals with regard to the processing of personal data: collection must be justified,
/// access is logged, and the record carries an explicit retention horizon.
/// </summary>
public sealed class Patient : AuditableAggregateRoot, ISoftDeletable, ISiteScoped
{
    private readonly List<PatientCoverage> _coverages = [];
    private readonly List<PatientConsent> _consents = [];
    private readonly List<PatientAlert> _alerts = [];

    private Patient() { }

    public Patient(string medicalRecordNumber, string lastName, string firstName,
                   DateOnly? dateOfBirth, Gender gender, Guid siteId)
    {
        MedicalRecordNumber = medicalRecordNumber;
        LastName = lastName;
        FirstName = firstName;
        DateOfBirth = dateOfBirth;
        Gender = gender;
        SiteId = siteId;
    }

    /// <summary>Surname given to a file opened before the patient could be identified.</summary>
    public const string ProvisionalLastName = "X-URG";

    /// <summary>
    /// Opens a file for a patient who cannot be identified yet — unconscious, brought in by the
    /// emergency services, unable to give a name — so the sample can be drawn and analysed now.
    /// <para>
    /// The file number doubles as the provisional first name: "X-URG 000431" is unique, is printed
    /// on every tube label, and is what the ward quotes on the telephone until somebody knows who
    /// the patient is.
    /// </para>
    /// </summary>
    public static Patient Provisional(string medicalRecordNumber, Gender gender, int estimatedAgeYears,
                                      Guid siteId)
    {
        var digits = new string(medicalRecordNumber.Where(char.IsAsciiDigit).ToArray());
        var suffix = digits.Length > 6 ? digits[^6..] : digits;

        return new Patient(medicalRecordNumber, ProvisionalLastName,
                           suffix.Length > 0 ? suffix : medicalRecordNumber, null, gender, siteId)
        {
            DeclaredAgeYears = estimatedAgeYears,
            IsProvisionalIdentity = true
        };
    }

    /// <summary>
    /// True while the file carries a provisional identity. Cleared once the real identity is
    /// entered; the change is audited like any identity correction.
    /// </summary>
    public bool IsProvisionalIdentity { get; private set; }

    /// <summary>The real identity has been entered on a provisional file.</summary>
    public void ConfirmIdentity() => IsProvisionalIdentity = false;

    /// <summary>Human-readable file number, unique per installation, e.g. "P-2026-000431".</summary>
    public string MedicalRecordNumber { get; private set; } = string.Empty;

    public Guid SiteId { get; set; }

    // ---- Civil identity ---------------------------------------------------------------------

    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Name in Arabic script — as written on the CIN, and required for Arabic reports.</summary>
    public string? LastNameArabic { get; set; }
    public string? FirstNameArabic { get; set; }

    /// <summary>
    /// Normalised search key (accent- and case-insensitive) maintained by the application layer so
    /// the reception desk can find "Bénani" by typing "benani".
    /// </summary>
    public string SearchKey { get; set; } = string.Empty;

    public Gender Gender { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// Set when only an age was declared (common with elderly patients holding no birth record).
    /// Reference ranges then use this instead of a computed age.
    /// </summary>
    public int? DeclaredAgeYears { get; set; }

    public string? PlaceOfBirth { get; set; }

    // ---- National identifiers ---------------------------------------------------------------

    /// <summary>CIN — Carte d'Identité Nationale. Primary identity proof for adults.</summary>
    public string? Cin { get; set; }

    /// <summary>Passport number, for foreign patients.</summary>
    public string? PassportNumber { get; set; }

    /// <summary>Nationality ISO 3166-1 alpha-2; "MA" by default.</summary>
    public string NationalityCode { get; set; } = "MA";

    /// <summary>For minors: the CIN of the parent or legal guardian who signed the consent.</summary>
    public string? GuardianCin { get; set; }
    public string? GuardianFullName { get; set; }
    public string? GuardianRelationship { get; set; }

    // ---- Contact ----------------------------------------------------------------------------

    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public MoroccanRegion Region { get; set; }
    public string? PostalCode { get; set; }

    /// <summary>Normalised to E.164 (+212…) on save.</summary>
    public string? MobilePhone { get; set; }
    public string? AlternatePhone { get; set; }
    public string? Email { get; set; }

    /// <summary>Language the patient's report and SMS notifications must be produced in.</summary>
    public AppLanguage PreferredLanguage { get; set; } = AppLanguage.French;

    // ---- Clinical context that affects interpretation ---------------------------------------

    /// <summary>Drives gestational reference ranges and blocks certain analyses.</summary>
    public bool IsPregnant { get; set; }
    public DateOnly? LastMenstrualPeriod { get; set; }

    public decimal? WeightKg { get; set; }
    public decimal? HeightCm { get; set; }
    public BloodGroup BloodGroup { get; set; } = BloodGroup.Unknown;

    /// <summary>Free-text treatment in progress — interferences are a frequent cause of odd results.</summary>
    public string? CurrentTreatment { get; set; }
    public string? KnownAllergies { get; set; }

    // ---- Loi 08-09: lawfulness, retention, portability --------------------------------------

    /// <summary>
    /// Earliest date at which the file may be purged. Moroccan practice for medical records is a
    /// minimum of 10 years from the last act; the value is computed, not guessed, by
    /// <see cref="ExtendRetention"/>.
    /// </summary>
    public DateOnly? RetentionUntil { get; private set; }

    /// <summary>Set when the patient exercised the right to erasure and the file was pseudonymised.</summary>
    public bool IsAnonymized { get; private set; }
    public DateTimeOffset? AnonymizedAt { get; private set; }

    /// <summary>Patient objected to any use of their data beyond the care itself (loi 08-09 art. 9).</summary>
    public bool OptedOutOfSecondaryUse { get; set; }

    public bool IsActive { get; set; } = true;

    public IReadOnlyCollection<PatientCoverage> Coverages => _coverages.AsReadOnly();
    public IReadOnlyCollection<PatientConsent> Consents => _consents.AsReadOnly();
    public IReadOnlyCollection<PatientAlert> Alerts => _alerts.AsReadOnly();

    /// <summary>Set when this file was merged into another; all lookups follow the pointer.</summary>
    public Guid? MergedIntoPatientId { get; private set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    // ---- Behaviour --------------------------------------------------------------------------

    public string FullName => $"{LastName} {FirstName}".Trim();

    public string FullNameArabic =>
        string.IsNullOrWhiteSpace(LastNameArabic) && string.IsNullOrWhiteSpace(FirstNameArabic)
            ? FullName
            : $"{LastNameArabic} {FirstNameArabic}".Trim();

    public string DisplayName(AppLanguage language) =>
        language == AppLanguage.Arabic ? FullNameArabic : FullName;

    /// <summary>Age in whole years at <paramref name="on"/>, from the birth date or the declared age.</summary>
    public int? AgeInYears(DateOnly on)
    {
        if (DateOfBirth is not { } dob) return DeclaredAgeYears;
        var age = on.Year - dob.Year;
        if (on < dob.AddYears(age)) age--;
        return age < 0 ? null : age;
    }

    /// <summary>Age in days — reference ranges for neonates are expressed in days, not years.</summary>
    public int? AgeInDays(DateOnly on) =>
        DateOfBirth is { } dob ? Math.Max(0, on.DayNumber - dob.DayNumber) : null;

    public bool IsMinor(DateOnly on) => AgeInYears(on) is { } age && age < 18;

    public decimal? BodySurfaceAreaM2 =>
        WeightKg is > 0 && HeightCm is > 0
            ? Math.Round((decimal)Math.Sqrt((double)(WeightKg.Value * HeightCm.Value) / 3600d), 3)
            : null;

    public Result Validate(DateOnly today)
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(LastName))
            errors.Add(Error.Validation("Patient_LastNameRequired"));
        if (string.IsNullOrWhiteSpace(FirstName))
            errors.Add(Error.Validation("Patient_FirstNameRequired"));
        if (DateOfBirth is null && DeclaredAgeYears is null)
            errors.Add(Error.Validation("Patient_AgeOrBirthDateRequired"));
        if (DateOfBirth is { } dob && dob > today)
            errors.Add(Error.Validation("Patient_BirthDateInFuture"));
        if (Cin is not null && !MoroccanIdentifiers.IsValidCin(Cin))
            errors.Add(Error.Validation("Patient_CinInvalid"));
        if (IsPregnant && Gender != Gender.Female)
            errors.Add(Error.Validation("Patient_PregnancyRequiresFemale"));
        // A provisional file has no guardian to name: the patient arrived unidentified.
        if (!IsProvisionalIdentity && IsMinor(today) && string.IsNullOrWhiteSpace(GuardianFullName))
            errors.Add(Error.Regulatory("Patient_MinorRequiresGuardian"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    public void AddCoverage(PatientCoverage coverage)
    {
        if (coverage.IsPrimary)
            foreach (var existing in _coverages) existing.IsPrimary = false;
        _coverages.Add(coverage);
    }

    public PatientCoverage? PrimaryCoverage(DateOnly on) =>
        _coverages.Where(c => c.IsValidOn(on)).OrderByDescending(c => c.IsPrimary)
                  .ThenByDescending(c => c.ValidFrom).FirstOrDefault();

    public void RecordConsent(PatientConsent consent) => _consents.Add(consent);

    /// <summary>The most recent non-withdrawn consent of the given kind, if any.</summary>
    public PatientConsent? ActiveConsent(ConsentPurpose purpose) =>
        _consents.Where(c => c.Purpose == purpose && c.WithdrawnAt is null)
                 .OrderByDescending(c => c.GivenAt).FirstOrDefault();

    public bool HasConsent(ConsentPurpose purpose) => ActiveConsent(purpose) is not null;

    public void AddAlert(PatientAlert alert) => _alerts.Add(alert);

    /// <summary>
    /// Pushes the retention horizon out to <paramref name="years"/> after the supplied act date.
    /// Called whenever a new order is registered, so the clock restarts on the last act.
    /// </summary>
    public void ExtendRetention(DateOnly lastActDate, int years)
    {
        var candidate = lastActDate.AddYears(years);
        if (RetentionUntil is null || candidate > RetentionUntil) RetentionUntil = candidate;
    }

    public bool IsRetentionExpired(DateOnly today) =>
        RetentionUntil is { } until && until < today;

    /// <summary>
    /// Pseudonymises the file in place: direct identifiers are cleared while the clinical history
    /// and the accounting trail stay intact. This is the right-to-erasure path under loi 08-09 —
    /// deleting the rows outright would break the 10-year medical retention obligation and the
    /// unalterable invoice sequence.
    /// </summary>
    public void Anonymize(DateTimeOffset now, string reference)
    {
        LastName = $"ANONYME-{reference}";
        FirstName = "-";
        LastNameArabic = null;
        FirstNameArabic = null;
        SearchKey = $"anonyme{reference}";
        Cin = null;
        PassportNumber = null;
        GuardianCin = null;
        GuardianFullName = null;
        GuardianRelationship = null;
        AddressLine = null;
        PostalCode = null;
        MobilePhone = null;
        AlternatePhone = null;
        Email = null;
        PlaceOfBirth = null;
        CurrentTreatment = null;
        KnownAllergies = null;
        // Year of birth is kept (as 1 January) because reference-range selection needs an age band.
        DateOfBirth = DateOfBirth is { } dob ? new DateOnly(dob.Year, 1, 1) : null;
        IsAnonymized = true;
        AnonymizedAt = now;
        IsActive = false;
    }

    public void MarkMergedInto(Guid survivingPatientId)
    {
        if (survivingPatientId == Id) throw new DomainException("A patient cannot be merged into itself.");
        MergedIntoPatientId = survivingPatientId;
        IsActive = false;
    }
}

public enum Gender
{
    Unknown = 0,
    Male = 1,
    Female = 2,
    /// <summary>Recorded only where a civil document states it; kept distinct from Unknown.</summary>
    Other = 9
}

public enum BloodGroup
{
    Unknown = 0,
    OPositive = 1, ONegative = 2,
    APositive = 3, ANegative = 4,
    BPositive = 5, BNegative = 6,
    ABPositive = 7, ABNegative = 8
}

/// <summary>Clinical or logistic flag shown prominently whenever the file is opened.</summary>
public sealed class PatientAlert : Entity
{
    private PatientAlert() { }

    public PatientAlert(Guid patientId, PatientAlertSeverity severity, MultilingualText message)
    {
        PatientId = patientId;
        Severity = severity;
        Message = message;
    }

    public Guid PatientId { get; private set; }
    public PatientAlertSeverity Severity { get; set; }
    public MultilingualText Message { get; set; } = new();
    public DateOnly? ExpiresOn { get; set; }
    public bool IsActive => ExpiresOn is null || ExpiresOn >= DateOnly.FromDateTime(DateTime.Today);
}

public enum PatientAlertSeverity
{
    Information = 0,
    Warning = 1,
    /// <summary>Blocks registration until acknowledged, e.g. unpaid balance or biohazard.</summary>
    Critical = 2
}
