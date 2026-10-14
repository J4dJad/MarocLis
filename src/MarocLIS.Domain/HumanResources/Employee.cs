using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Domain.HumanResources;

/// <summary>
/// A member of the laboratory's staff: the personnel file.
/// <para>
/// Not the same thing as <see cref="Identity.AppUser"/>. The cleaner, the courier and the
/// accountant who never opens the LIS are employees all the same, with a contract, a CNSS number
/// and, for the first two, a hepatitis B vaccination to keep up to date. An employee who does use
/// the system is linked to their account; the account stays the identity on results and in the
/// audit trail, the employee file is where the contract, the diplomas and the habilitations live.
/// </para>
/// <para>
/// Never deleted. A departure is recorded with its date and reason, and the file stays readable
/// for as long as the law keeps the documents in it.
/// </para>
/// </summary>
public sealed class Employee : AuditableAggregateRoot, ISiteScoped
{
    // Named after the navigations: EF Core finds a collection's backing field by name.
    private readonly List<EmployeeCredential> _credentials = [];
    private readonly List<EmployeeHabilitation> _habilitations = [];

    private Employee() { }

    public Employee(string number, string lastName, string firstName, EmployeeJob job,
                    DateOnly hireDate, Guid siteId)
    {
        Number = number.Trim().ToUpperInvariant();
        LastName = lastName.Trim();
        FirstName = firstName.Trim();
        Job = job;
        HireDate = hireDate;
        SiteId = siteId;
        IsExposedToBiologicalRisk = IsExposedByDefault(job);
    }

    /// <summary>Matricule, printed on every HR document.</summary>
    public string Number { get; private set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? FullNameArabic { get; set; }

    public string FullName => $"{LastName} {FirstName}".Trim();

    public Gender Gender { get; set; }
    public DateOnly? BirthDate { get; set; }

    /// <summary>Carte nationale d'identité électronique.</summary>
    public string? Cin { get; set; }

    /// <summary>Numéro d'immatriculation CNSS.</summary>
    public string? CnssNumber { get; set; }

    /// <summary>Family situation: allowances and income-tax deductions depend on it.</summary>
    public FamilyStatus FamilyStatus { get; set; }
    public int DependentChildren { get; set; }

    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    // ---- Position ------------------------------------------------------------------------------

    public EmployeeJob Job { get; set; }
    public string? JobTitle { get; set; }

    /// <summary>Main site; the planning can still place the employee elsewhere.</summary>
    public Guid SiteId { get; set; }

    /// <summary>Discipline the employee mainly works in (haematology, biochemistry…), if any.</summary>
    public Guid? DepartmentId { get; set; }

    /// <summary>LIS account of an employee who uses the system.</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Whether the post handles blood, biological fluids, cultures or infectious waste. Set from the
    /// job when the file is created and adjustable: a secretary who also centrifuges is exposed.
    /// </summary>
    public bool IsExposedToBiologicalRisk { get; set; }

    /// <summary>Date the professional-secrecy clause was signed. Required for every post.</summary>
    public DateOnly? ConfidentialityClauseSignedOn { get; set; }

    // ---- Contract ------------------------------------------------------------------------------

    public ContractType ContractType { get; set; } = ContractType.Permanent;
    public DateOnly HireDate { get; set; }

    /// <summary>End of a fixed-term contract, internship or programme.</summary>
    public DateOnly? ContractEndDate { get; set; }

    public DateOnly? TrialPeriodEndDate { get; set; }

    /// <summary>Contractual weekly hours; 44 is the legal full time outside agriculture.</summary>
    public decimal WeeklyHours { get; set; } = 44m;

    /// <summary>Monthly gross base salary in dirhams. Only readable with the salary right.</summary>
    public decimal? BaseSalary { get; set; }

    /// <summary>Relevé d'identité bancaire, 24 digits.</summary>
    public string? Rib { get; set; }
    public string? BankName { get; set; }

    // ---- Departure -----------------------------------------------------------------------------

    public DateOnly? ExitDate { get; private set; }
    public ExitReason? ExitReason { get; private set; }
    public string? ExitComment { get; private set; }

    public bool IsActive => ExitDate is null;

    public IReadOnlyCollection<EmployeeCredential> Credentials => _credentials.AsReadOnly();
    public IReadOnlyCollection<EmployeeHabilitation> Habilitations => _habilitations.AsReadOnly();

    // ---- Rules ---------------------------------------------------------------------------------

    public static bool IsExposedByDefault(EmployeeJob job) => job is EmployeeJob.Biologist
        or EmployeeJob.Technician or EmployeeJob.Nurse or EmployeeJob.Courier or EmployeeJob.Cleaner;

    /// <summary>Fixed-term contracts, programmes and internships must say when they end.</summary>
    public static bool RequiresEndDate(ContractType type) => type is ContractType.FixedTerm
        or ContractType.Anapec or ContractType.Tahfiz or ContractType.Internship;

    public Result Validate()
    {
        if (string.IsNullOrWhiteSpace(LastName) || string.IsNullOrWhiteSpace(FirstName))
            return Result.Failure(Error.Validation("Employee_NameRequired"));

        if (RequiresEndDate(ContractType) && ContractEndDate is null)
            return Result.Failure(Error.Validation("Employee_ContractEndRequired"));

        if (ContractEndDate is { } end && end < HireDate)
            return Result.Failure(Error.Validation("Employee_ContractEndBeforeHire"));

        if (TrialPeriodEndDate is { } trial && trial < HireDate)
            return Result.Failure(Error.Validation("Employee_TrialBeforeHire"));

        if (WeeklyHours is <= 0m or > 60m)
            return Result.Failure(Error.Validation("Employee_WeeklyHoursInvalid"));

        if (BaseSalary is < 0m)
            return Result.Failure(Error.Validation("Employee_SalaryInvalid"));

        if (!string.IsNullOrWhiteSpace(Rib) && !IsValidRib(Rib))
            return Result.Failure(Error.Validation("Employee_RibInvalid"));

        if (DependentChildren is < 0 or > 20)
            return Result.Failure(Error.Validation("Employee_ChildrenInvalid"));

        return Result.Success();
    }

    /// <summary>
    /// A Moroccan RIB: 24 digits, the last two a key such that the whole number is divisible by 97.
    /// Checked so a typo is caught at entry, not when the bank returns the transfer.
    /// </summary>
    public static bool IsValidRib(string rib)
    {
        var digits = new string([.. rib.Where(c => !char.IsWhiteSpace(c))]);
        if (digits.Length != 24 || !digits.All(char.IsAsciiDigit)) return false;

        var remainder = 0;
        foreach (var c in digits) remainder = (remainder * 10 + (c - '0')) % 97;
        return remainder == 0;
    }

    public Result RecordExit(DateOnly on, ExitReason reason, string? comment)
    {
        if (!IsActive) return Result.Failure(Error.Conflict("Employee_AlreadyExited", ExitDate!));
        if (on < HireDate) return Result.Failure(Error.Validation("Employee_ExitBeforeHire"));

        ExitDate = on;
        ExitReason = reason;
        ExitComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

        // A person who has left holds no habilitation: the matrix must not show them as able to
        // validate, and a return later starts from a fresh evaluation.
        foreach (var habilitation in _habilitations.Where(h => h.IsActive))
            habilitation.Withdraw(on, "Sortie du laboratoire");

        return Result.Success();
    }

    /// <summary>Re-hiring keeps the file; the contract dates are entered again.</summary>
    public Result Rehire(DateOnly on)
    {
        if (IsActive) return Result.Failure(Error.Conflict("Employee_AlreadyActive"));

        ExitDate = null;
        ExitReason = null;
        ExitComment = null;
        HireDate = on;
        ContractEndDate = null;
        TrialPeriodEndDate = null;
        return Result.Success();
    }

    public Result<EmployeeCredential> AddCredential(EmployeeCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Title))
            return Result.Failure<EmployeeCredential>(Error.Validation("Credential_TitleRequired"));

        if (credential.ExpiresOn is { } expires && credential.ObtainedOn is { } obtained && expires < obtained)
            return Result.Failure<EmployeeCredential>(Error.Validation("Credential_ExpiryBeforeIssue"));

        credential.AttachTo(Id);
        _credentials.Add(credential);
        return Result.Success(credential);
    }

    /// <summary>Removes a document entered by mistake. The audit trail keeps what it said.</summary>
    public Result RemoveCredential(Guid credentialId)
    {
        var credential = _credentials.FirstOrDefault(c => c.Id == credentialId);
        if (credential is null) return Result.Failure(Error.NotFound("Credential_NotFound"));

        _credentials.Remove(credential);
        return Result.Success();
    }

    /// <summary>
    /// Grants or changes a habilitation. An activity is held once at a time: a new level replaces
    /// the active one, which is closed rather than overwritten so the history of who was authorised
    /// for what, and when, survives for the accreditation audit.
    /// </summary>
    public Result<EmployeeHabilitation> Grant(HabilitationActivity activity, Guid? subjectId,
                                              HabilitationLevel level, DateOnly grantedOn,
                                              DateOnly? reviewDueOn, Guid grantedByUserId,
                                              string? evidence)
    {
        if (!IsActive)
            return Result.Failure<EmployeeHabilitation>(Error.Conflict("Habilitation_EmployeeExited"));

        if (EmployeeHabilitation.RequiresSubject(activity) && subjectId is null)
            return Result.Failure<EmployeeHabilitation>(Error.Validation("Habilitation_SubjectRequired"));

        if (reviewDueOn is { } due && due <= grantedOn)
            return Result.Failure<EmployeeHabilitation>(Error.Validation("Habilitation_ReviewBeforeGrant"));

        var subject = EmployeeHabilitation.RequiresSubject(activity) ? subjectId : null;

        foreach (var current in _habilitations.Where(h => h.IsActive && h.Activity == activity
                                                          && h.SubjectId == subject))
            current.Withdraw(grantedOn, "Remplacée par une nouvelle décision");

        var habilitation = new EmployeeHabilitation(Id, activity, subject, level, grantedOn,
                                                    reviewDueOn, grantedByUserId,
                                                    string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim());
        _habilitations.Add(habilitation);
        return Result.Success(habilitation);
    }

    public Result Withdraw(Guid habilitationId, DateOnly on, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Habilitation_WithdrawReasonRequired"));

        var habilitation = _habilitations.FirstOrDefault(h => h.Id == habilitationId);
        if (habilitation is null) return Result.Failure(Error.NotFound("Habilitation_NotFound"));
        if (!habilitation.IsActive) return Result.Failure(Error.Conflict("Habilitation_AlreadyWithdrawn"));

        habilitation.Withdraw(on, reason.Trim());
        return Result.Success();
    }
}

/// <summary>The job families of a medical biology laboratory, each with its own qualification.</summary>
public enum EmployeeJob
{
    /// <summary>Biologiste (médecin ou pharmacien), inscrit à son Ordre.</summary>
    Biologist = 0,
    Technician = 1,
    /// <summary>Infirmier ou préleveur.</summary>
    Nurse = 2,
    /// <summary>Secrétaire médicale, accueil.</summary>
    Secretary = 3,
    /// <summary>Coursier, chauffeur : transport des échantillons.</summary>
    Courier = 4,
    /// <summary>Agent d'entretien : exposé aux déchets à risque infectieux.</summary>
    Cleaner = 5,
    /// <summary>Qualité, informatique, comptabilité, direction administrative.</summary>
    Support = 6
}

public enum FamilyStatus
{
    Unmarried = 0,
    Married = 1,
    Divorced = 2,
    Widowed = 3
}

public enum ContractType
{
    /// <summary>CDI.</summary>
    Permanent = 0,
    /// <summary>CDD.</summary>
    FixedTerm = 1,
    /// <summary>Contrat d'insertion ANAPEC (Idmaj).</summary>
    Anapec = 2,
    /// <summary>Programme Tahfiz.</summary>
    Tahfiz = 3,
    /// <summary>Convention de stage.</summary>
    Internship = 4,
    /// <summary>Temps partiel.</summary>
    PartTime = 5,
    /// <summary>Intérimaire, payé par l'agence.</summary>
    Temporary = 6,
    /// <summary>Biologiste associé, non salarié.</summary>
    Partner = 7
}

public enum ExitReason
{
    Resignation = 0,
    EndOfFixedTerm = 1,
    Dismissal = 2,
    DismissalForSeriousMisconduct = 3,
    MutualAgreement = 4,
    Retirement = 5,
    Death = 6,
    EndOfTrialPeriod = 7
}

/// <summary>
/// A document the file holds with its dates: diploma, registration with the Order, driving
/// licence, vaccination, fitness certificate, safety training, work permit.
/// <para>
/// Only the fact and the dates. A medical visit records the fitness decision, never a diagnosis:
/// the occupational physician keeps the medical file, the laboratory keeps the aptitude.
/// </para>
/// </summary>
public sealed class EmployeeCredential : Entity, IAuditable
{
    private EmployeeCredential() { }

    public EmployeeCredential(CredentialKind kind, string title)
    {
        Kind = kind;
        Title = title.Trim();
    }

    public Guid EmployeeId { get; private set; }
    internal void AttachTo(Guid employeeId) => EmployeeId = employeeId;

    public CredentialKind Kind { get; private set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Number: diploma, Order registration, licence, certificate.</summary>
    public string? Reference { get; set; }

    /// <summary>School, Order, prefecture, physician.</summary>
    public string? IssuedBy { get; set; }

    public DateOnly? ObtainedOn { get; set; }

    /// <summary>When it must be renewed, if ever. Diplomas do not expire; vaccinations do.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>For a medical visit only.</summary>
    public MedicalFitness? Fitness { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}

public enum CredentialKind
{
    Diploma = 0,
    /// <summary>Inscription à l'Ordre des médecins ou des pharmaciens.</summary>
    OrderRegistration = 1,
    DrivingLicence = 2,
    HepatitisBVaccination = 3,
    OtherVaccination = 4,
    /// <summary>Visite d'embauche, périodique ou de reprise.</summary>
    MedicalVisit = 5,
    /// <summary>Risque biologique, déchets, triple emballage, incendie.</summary>
    SafetyTraining = 6,
    /// <summary>Visa du contrat de travail étranger.</summary>
    WorkPermit = 7,
    ResidencePermit = 8,
    ContinuingEducation = 9,
    Other = 99
}

public enum MedicalFitness
{
    Fit = 0,
    FitWithRestrictions = 1,
    Unfit = 2
}

/// <summary>
/// What an employee is authorised to do, at what level, decided by whom and until when.
/// <para>
/// ISO 15189 asks that nobody perform an activity they have not been found competent for. A row
/// here is that decision: granted on a date by a biologist, reviewed on a date, closed with a
/// reason when it ends. Rows are never edited or deleted; a new level is a new row.
/// </para>
/// </summary>
public sealed class EmployeeHabilitation : Entity, IAuditable
{
    private EmployeeHabilitation() { }

    internal EmployeeHabilitation(Guid employeeId, HabilitationActivity activity, Guid? subjectId,
                                  HabilitationLevel level, DateOnly grantedOn, DateOnly? reviewDueOn,
                                  Guid grantedByUserId, string? evidence)
    {
        EmployeeId = employeeId;
        Activity = activity;
        SubjectId = subjectId;
        Level = level;
        GrantedOn = grantedOn;
        ReviewDueOn = reviewDueOn;
        GrantedByUserId = grantedByUserId;
        Evidence = evidence;
    }

    public Guid EmployeeId { get; private set; }
    public HabilitationActivity Activity { get; private set; }

    /// <summary>The department or the analyser the activity concerns, when it concerns one.</summary>
    public Guid? SubjectId { get; private set; }

    public HabilitationLevel Level { get; private set; }
    public DateOnly GrantedOn { get; private set; }
    public DateOnly? ReviewDueOn { get; private set; }
    public Guid GrantedByUserId { get; private set; }

    /// <summary>What the decision rests on: training, supervised period, practical evaluation.</summary>
    public string? Evidence { get; private set; }

    public DateOnly? WithdrawnOn { get; private set; }
    public string? WithdrawalReason { get; private set; }

    public bool IsActive => WithdrawnOn is null;

    public bool IsReviewOverdue(DateOnly today) => IsActive && ReviewDueOn is { } due && due < today;

    internal void Withdraw(DateOnly on, string reason)
    {
        WithdrawnOn = on;
        WithdrawalReason = reason;
    }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    /// <summary>Activities exercised on one analyser or in one discipline name it.</summary>
    public static bool RequiresSubject(HabilitationActivity activity) => activity is
        HabilitationActivity.InstrumentOperation or HabilitationActivity.ManualTechnique
        or HabilitationActivity.TechnicalValidation or HabilitationActivity.BiologicalValidation;

    public static bool SubjectIsInstrument(HabilitationActivity activity) =>
        activity == HabilitationActivity.InstrumentOperation;
}

public enum HabilitationActivity
{
    AdultCollection = 0,
    PaediatricCollection = 1,
    HomeCollection = 2,
    /// <summary>Use of one analyser.</summary>
    InstrumentOperation = 3,
    /// <summary>Manual techniques of one discipline (microscopy, serology on slide…).</summary>
    ManualTechnique = 4,
    TechnicalValidation = 5,
    BiologicalValidation = 6,
    /// <summary>Carrying specimens: triple packaging, cold chain.</summary>
    SpecimenTransport = 7,
    SampleReception = 8
}

public enum HabilitationLevel
{
    /// <summary>Works under supervision; may not act alone.</summary>
    InTraining = 0,
    Authorized = 1,
    /// <summary>Authorised and may train and supervise others.</summary>
    Trainer = 2
}
