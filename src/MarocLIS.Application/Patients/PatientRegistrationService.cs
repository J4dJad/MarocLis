using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Patients;

/// <summary>Civil identity, contact and clinical context for a patient file.</summary>
public sealed record PatientIdentityRequest
{
    public required string LastName { get; init; }
    public required string FirstName { get; init; }

    public string? LastNameArabic { get; init; }
    public string? FirstNameArabic { get; init; }

    public Gender Gender { get; init; } = Gender.Unknown;
    public DateOnly? DateOfBirth { get; init; }

    /// <summary>Used when no birth record exists — common with elderly patients.</summary>
    public int? DeclaredAgeYears { get; init; }
    public string? PlaceOfBirth { get; init; }

    public string? Cin { get; init; }
    public string? PassportNumber { get; init; }
    public string NationalityCode { get; init; } = "MA";

    public string? GuardianFullName { get; init; }
    public string? GuardianCin { get; init; }
    public string? GuardianRelationship { get; init; }

    public string? AddressLine { get; init; }
    public string? City { get; init; }
    public MoroccanRegion Region { get; init; } = MoroccanRegion.Unspecified;
    public string? PostalCode { get; init; }

    public string? MobilePhone { get; init; }
    public string? AlternatePhone { get; init; }
    public string? Email { get; init; }

    public AppLanguage PreferredLanguage { get; init; } = AppLanguage.French;

    public bool IsPregnant { get; init; }
    public DateOnly? LastMenstrualPeriod { get; init; }
    public decimal? WeightKg { get; init; }
    public decimal? HeightCm { get; init; }
    public BloodGroup BloodGroup { get; init; } = BloodGroup.Unknown;
    public string? CurrentTreatment { get; init; }
    public string? KnownAllergies { get; init; }

    public bool OptedOutOfSecondaryUse { get; init; }

    /// <summary>
    /// Set by the operator after being shown candidate duplicates, to say "these are different
    /// people, create the file anyway". Stored in the audit trail: a duplicate file splits a
    /// clinical history in two, and whoever decided to accept the risk should be identifiable.
    /// </summary>
    public string? DuplicateOverrideReason { get; init; }
}

/// <summary>A patient that may be the same person as the one being registered.</summary>
public sealed record DuplicateCandidate(
    Guid PatientId,
    string MedicalRecordNumber,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    int? AgeYears,
    string? Cin,
    string? MobilePhone,
    string MatchedOn);

public sealed record CoverageRequest
{
    public required Guid PayerId { get; init; }
    public required string MembershipNumber { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
    public bool IsPrimary { get; init; } = true;

    public BeneficiaryRelationship Relationship { get; init; } = BeneficiaryRelationship.Self;
    public string? InsuredPersonName { get; init; }
    public string? InsuredPersonCin { get; init; }
    public string? EmployerName { get; init; }

    public decimal? ReimbursementRateOverride { get; init; }

    public bool IsChronicDiseaseScheme { get; init; }
    public string? PriorAgreementNumber { get; init; }
    public DateOnly? PriorAgreementValidUntil { get; init; }

    public bool DirectBillingEnabled { get; init; } = true;
    public string? Notes { get; init; }
}

public sealed record ConsentRequest
{
    public required ConsentPurpose Purpose { get; init; }
    public ConsentChannel Channel { get; init; } = ConsentChannel.WrittenForm;
    public AppLanguage PresentedInLanguage { get; init; } = AppLanguage.French;

    public string? SignedByName { get; init; }
    public string? SignedByCin { get; init; }
    public string? SignedByRelationship { get; init; }

    public string? FormVersion { get; init; }
    public DateOnly? ExpiresOn { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Registration and lifecycle of the patient file.
/// <para>
/// This is the entry point to the whole workflow: nothing downstream — an order, a sample, a
/// result, an invoice — exists without a patient file, and the identity recorded here is the one
/// printed on a report that carries a legal signature.
/// </para>
/// <para>
/// Four behaviours here are regulatory rather than clerical:
/// </para>
/// <list type="bullet">
/// <item><b>Duplicate detection runs before every creation</b>, and a match must be explicitly
/// overridden with a reason. A duplicate file splits a patient's history, which costs the next
/// clinician the previous-result column that makes a delta check mean anything — the silent
/// failure this check exists to prevent.</item>
/// <item><b>The file number comes from a gapless sequence inside the transaction.</b> Same
/// mechanism as the accession and invoice series, so a rollback returns the number instead of
/// burning it.</item>
/// <item><b>A minor cannot be registered without a named guardian</b>
/// (<see cref="Patient.Validate"/>), because the consent for any analysis is the guardian's to
/// give.</item>
/// <item><b>Creating a file is an act of processing personal data</b> under loi n° 08-09 and is
/// logged as such, not merely as an entity insert.</item>
/// </list>
/// </summary>
public sealed class PatientRegistrationService(
    IRepository<Patient> patients,
    IPatientDuplicateFinder duplicates,
    ISequenceAllocator sequences,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IDataAccessAuditor dataAccessAuditor,
    IClock clock,
    IRepository<LabOrder> orders,
    ResultEntryService resultEntry)
{
    /// <summary>
    /// Candidate duplicates for an identity the operator has typed but not yet saved.
    /// <para>
    /// Separate from <see cref="RegisterAsync"/> so the screen can warn while the clerk is still
    /// looking at the patient, rather than after they have pressed save and turned away.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<DuplicateCandidate>>> FindDuplicatesAsync(
        string lastName, string firstName, string? cin, DateOnly? dateOfBirth,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<DuplicateCandidate>>(permitted.FirstError);

        return Result.Success(await FindCandidatesAsync(lastName, firstName, cin, dateOfBirth, ct));
    }

    /// <summary>Creates the patient file, its coverages and its consents in one transaction.</summary>
    public async Task<Result<PatientRegistrationOutcome>> RegisterAsync(
        PatientIdentityRequest identity,
        IReadOnlyList<CoverageRequest>? coverages = null,
        IReadOnlyList<ConsentRequest>? consents = null,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Create);
        if (permitted.IsFailure)
            return Result.Failure<PatientRegistrationOutcome>(permitted.FirstError);

        var today = clock.Today;
        var cin = Normalise(identity.Cin);

        // Duplicate check first: it is cheap, and it is the one decision that cannot be undone
        // cleanly once two files carry the same person's history.
        var candidates = await FindCandidatesAsync(identity.LastName, identity.FirstName, cin,
                                                    identity.DateOfBirth, ct);

        if (candidates.Count > 0 && string.IsNullOrWhiteSpace(identity.DuplicateOverrideReason))
        {
            return Result.Success(new PatientRegistrationOutcome(null, candidates));
        }

        // A transaction, because the file number is allocated from a gapless sequence: the
        // pessimistic row lock must be held until the patient row is written.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.MedicalRecord, siteId: null, ct: ct);
        if (number.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<PatientRegistrationOutcome>(number.FirstError);
        }

        var patient = new Patient(number.Value, identity.LastName.Trim(), identity.FirstName.Trim(),
                                   identity.DateOfBirth, identity.Gender, currentUser.SiteId)
        {
            LastNameArabic = Blank(identity.LastNameArabic),
            FirstNameArabic = Blank(identity.FirstNameArabic),
            SearchKey = PatientSearchKey.Build(identity.LastName, identity.FirstName),
            DeclaredAgeYears = identity.DateOfBirth is null ? identity.DeclaredAgeYears : null,
            PlaceOfBirth = Blank(identity.PlaceOfBirth),
            Cin = cin,
            PassportNumber = Normalise(identity.PassportNumber),
            NationalityCode = string.IsNullOrWhiteSpace(identity.NationalityCode)
                ? "MA" : identity.NationalityCode.Trim().ToUpperInvariant(),
            GuardianFullName = Blank(identity.GuardianFullName),
            GuardianCin = Normalise(identity.GuardianCin),
            GuardianRelationship = Blank(identity.GuardianRelationship),
            AddressLine = Blank(identity.AddressLine),
            City = Blank(identity.City),
            Region = identity.Region,
            PostalCode = Blank(identity.PostalCode),

            // Stored in E.164. The laboratory sends result-ready notifications by SMS, and a
            // locally formatted number silently fails at the gateway.
            MobilePhone = MoroccanIdentifiers.TryNormalizePhone(identity.MobilePhone),
            AlternatePhone = MoroccanIdentifiers.TryNormalizePhone(identity.AlternatePhone),
            Email = Blank(identity.Email),
            PreferredLanguage = identity.PreferredLanguage,
            IsPregnant = identity.IsPregnant,
            LastMenstrualPeriod = identity.LastMenstrualPeriod,
            WeightKg = identity.WeightKg,
            HeightCm = identity.HeightCm,
            BloodGroup = identity.BloodGroup,
            CurrentTreatment = Blank(identity.CurrentTreatment),
            KnownAllergies = Blank(identity.KnownAllergies),
            OptedOutOfSecondaryUse = identity.OptedOutOfSecondaryUse
        };

        var valid = patient.Validate(today);
        if (valid.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<PatientRegistrationOutcome>(valid.Errors);
        }

        var attached = AttachCoverages(patient, coverages, today);
        if (attached.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<PatientRegistrationOutcome>(attached.Errors);
        }

        var recorded = RecordConsents(patient, consents);
        if (recorded.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<PatientRegistrationOutcome>(recorded.Errors);
        }

        await patients.AddAsync(patient, ct);

        unitOfWork.AuditReason = candidates.Count > 0
            ? $"Création du dossier {patient.MedicalRecordNumber} ({patient.FullName}) " +
              $"malgré {candidates.Count} doublon(s) possible(s) : {identity.DuplicateOverrideReason}"
            : $"Création du dossier {patient.MedicalRecordNumber} ({patient.FullName})";

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        // Loi 08-09: collecting personal data is itself a processing act, and the consultation log
        // is what lets the laboratory answer "who handled this person's data, and when".
        await dataAccessAuditor.LogAsync(DataAccessKind.ViewPatient, patient.Id,
                                          context: "Création du dossier patient", ct: ct);

        return Result.Success(new PatientRegistrationOutcome(patient.Id, []));
    }

    /// <summary>
    /// Opens a provisional file ("X-URG …") for a patient who cannot be identified yet, so the
    /// request can be registered and the sample drawn at once. No duplicate check: there is no
    /// identity to compare.
    /// </summary>
    public async Task<Result<Guid>> RegisterProvisionalAsync(Gender gender, int estimatedAgeYears,
                                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Create);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        if (estimatedAgeYears is < 0 or > 130)
            return Result.Failure<Guid>(Error.Validation("Patient_EstimatedAgeInvalid"));

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var number = await sequences.NextAsync(SequencePurpose.MedicalRecord, siteId: null, ct: ct);
        if (number.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<Guid>(number.FirstError);
        }

        var patient = Patient.Provisional(number.Value, gender, estimatedAgeYears, currentUser.SiteId);
        patient.SearchKey = PatientSearchKey.Build(patient.LastName, patient.FirstName);

        var valid = patient.Validate(clock.Today);
        if (valid.IsFailure)
        {
            await unitOfWork.RollbackAsync(ct);
            return Result.Failure<Guid>(valid.Errors);
        }

        await patients.AddAsync(patient, ct);

        unitOfWork.AuditReason =
            $"Création du dossier provisoire {patient.MedicalRecordNumber} ({patient.FullName}) : patient non identifié";

        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success(patient.Id);
    }

    /// <summary>Updates the identity of an existing file. The file number is never reassigned.</summary>
    public async Task<Result> UpdateAsync(Guid patientId, PatientIdentityRequest identity,
                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Edit);
        if (permitted.IsFailure) return permitted;

        var patient = await patients.GetByIdWithDetailsAsync(patientId, ct);
        if (patient is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        if (patient.IsAnonymized)
            return Result.Failure(Error.Regulatory("Patient_AnonymizedIsReadOnly"));

        var demographicsBefore = Demographics(patient);

        patient.LastName = identity.LastName.Trim();
        patient.FirstName = identity.FirstName.Trim();
        patient.LastNameArabic = Blank(identity.LastNameArabic);
        patient.FirstNameArabic = Blank(identity.FirstNameArabic);
        patient.SearchKey = PatientSearchKey.Build(identity.LastName, identity.FirstName);
        patient.Gender = identity.Gender;
        patient.DateOfBirth = identity.DateOfBirth;
        patient.DeclaredAgeYears = identity.DateOfBirth is null ? identity.DeclaredAgeYears : null;
        patient.PlaceOfBirth = Blank(identity.PlaceOfBirth);
        patient.Cin = Normalise(identity.Cin);
        patient.PassportNumber = Normalise(identity.PassportNumber);
        patient.NationalityCode = string.IsNullOrWhiteSpace(identity.NationalityCode)
            ? "MA" : identity.NationalityCode.Trim().ToUpperInvariant();
        patient.GuardianFullName = Blank(identity.GuardianFullName);
        patient.GuardianCin = Normalise(identity.GuardianCin);
        patient.GuardianRelationship = Blank(identity.GuardianRelationship);
        patient.AddressLine = Blank(identity.AddressLine);
        patient.City = Blank(identity.City);
        patient.Region = identity.Region;
        patient.PostalCode = Blank(identity.PostalCode);
        patient.MobilePhone = MoroccanIdentifiers.TryNormalizePhone(identity.MobilePhone);
        patient.AlternatePhone = MoroccanIdentifiers.TryNormalizePhone(identity.AlternatePhone);
        patient.Email = Blank(identity.Email);
        patient.PreferredLanguage = identity.PreferredLanguage;
        patient.IsPregnant = identity.IsPregnant;
        patient.LastMenstrualPeriod = identity.LastMenstrualPeriod;
        patient.WeightKg = identity.WeightKg;
        patient.HeightCm = identity.HeightCm;
        patient.BloodGroup = identity.BloodGroup;
        patient.CurrentTreatment = Blank(identity.CurrentTreatment);
        patient.KnownAllergies = Blank(identity.KnownAllergies);
        patient.OptedOutOfSecondaryUse = identity.OptedOutOfSecondaryUse;

        // A provisional file becomes a real one the moment it is given a name other than the
        // provisional one; the audit line says so, because results already issued under "X-URG"
        // now belong to a named person.
        var completesProvisional = patient.IsProvisionalIdentity
            && !string.Equals(patient.LastName, Patient.ProvisionalLastName, StringComparison.OrdinalIgnoreCase);

        if (completesProvisional) patient.ConfirmIdentity();

        var valid = patient.Validate(clock.Today);
        if (valid.IsFailure) return valid;

        patients.Update(patient);

        unitOfWork.AuditReason = completesProvisional
            ? $"Identité provisoire complétée : dossier {patient.MedicalRecordNumber} désormais au nom de {patient.FullName}"
            : $"Modification du dossier {patient.MedicalRecordNumber} ({patient.FullName})";

        // Sex, age and pregnancy decide which reference interval applies and what an age- or
        // sex-dependent formula yields. Results still on the bench are re-interpreted with the
        // corrected file, in the same save; validated ones are left as released — a figure in a
        // clinician's hands changes only through an amendment — and the audit line says how many
        // were interpreted against the former demographics, so the biologist can review them.
        if (Demographics(patient) != demographicsBefore)
        {
            var (reinterpreted, validated) = await ReinterpretResultsAsync(patient, ct);

            if (reinterpreted > 0)
                unitOfWork.AuditReason += $" ; {reinterpreted} résultat(s) non validé(s) réinterprété(s)";
            if (validated > 0)
                unitOfWork.AuditReason +=
                    $" ; données démographiques modifiées : {validated} résultat(s) déjà validé(s) " +
                    "interprété(s) selon les anciennes données, à revoir et à amender si nécessaire";
        }

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>The facts about the patient that interpretation and calculation depend on.</summary>
    private static (Gender, DateOnly?, int?, bool, DateOnly?, decimal?, decimal?) Demographics(Patient patient) =>
        (patient.Gender, patient.DateOfBirth, patient.DeclaredAgeYears, patient.IsPregnant,
         patient.LastMenstrualPeriod, patient.WeightKg, patient.HeightCm);

    private async Task<(int Reinterpreted, int Validated)> ReinterpretResultsAsync(Patient patient,
                                                                                   CancellationToken ct) =>
        await ReinterpretResultsAsync(patient, await WithMergedDuplicatesAsync(patient.Id, ct), ct);

    /// <summary>
    /// Re-interprets the pending results of the orders filed under any of
    /// <paramref name="patientIds"/>, with <paramref name="patient"/>'s demographics.
    /// <para>
    /// The files merged into the patient are included: a merge leaves the duplicate's orders
    /// where they were, and a correction of the survivor's sex or date of birth has to reach the
    /// order still pending on the duplicate, which was interpreted with the duplicate's mistakes.
    /// </para>
    /// </summary>
    private async Task<(int Reinterpreted, int Validated)> ReinterpretResultsAsync(
        Patient patient, IReadOnlyCollection<Guid> patientIds, CancellationToken ct)
    {
        var files = await orders.ListAsync(o => patientIds.Contains(o.PatientId)
                                                && o.Status != LabOrderStatus.Cancelled, ct);

        var reinterpreted = 0;
        var validated = 0;

        foreach (var order in files)
        {
            var (pending, released) = await resultEntry.ReinterpretPendingAsync(order.Id, patient, ct);
            reinterpreted += pending;
            validated += released;
        }

        return (reinterpreted, validated);
    }

    /// <summary>The patient and every file merged into it, followed transitively.</summary>
    private async Task<List<Guid>> WithMergedDuplicatesAsync(Guid patientId, CancellationToken ct)
    {
        var all = new HashSet<Guid> { patientId };
        var frontier = new List<Guid> { patientId };

        while (frontier.Count > 0)
        {
            var current = frontier;
            var merged = await patients.ListAsync(
                p => p.MergedIntoPatientId != null && current.Contains(p.MergedIntoPatientId.Value), ct);

            // The visited set stops a corrupt cycle from looping forever.
            frontier = [.. merged.Select(p => p.Id).Where(all.Add)];
        }

        return [.. all];
    }

    // ---- Coverage ------------------------------------------------------------------------------

    public async Task<Result> AddCoverageAsync(Guid patientId, CoverageRequest request,
                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Edit);
        if (permitted.IsFailure) return permitted;

        var patient = await patients.GetByIdWithDetailsAsync(patientId, ct);
        if (patient is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        var attached = AttachCoverages(patient, [request], clock.Today);
        if (attached.IsFailure) return attached;

        patients.Update(patient);

        unitOfWork.AuditReason =
            $"Couverture {request.MembershipNumber} ajoutée au dossier {patient.MedicalRecordNumber}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Ends a coverage as of a date rather than deleting it. An invoice already issued was priced
    /// on the cover in force at the time, and removing the row would make that invoice unexplainable.
    /// </summary>
    public async Task<Result> EndCoverageAsync(Guid patientId, Guid coverageId, DateOnly validUntil,
                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Edit);
        if (permitted.IsFailure) return permitted;

        var patient = await patients.GetByIdWithDetailsAsync(patientId, ct);
        if (patient is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        var coverage = patient.Coverages.FirstOrDefault(c => c.Id == coverageId);
        if (coverage is null) return Result.Failure(Error.NotFound("Coverage_NotFound"));

        if (validUntil < coverage.ValidFrom)
            return Result.Failure(Error.Validation("Coverage_InvalidPeriod"));

        coverage.ValidUntil = validUntil;
        patients.Update(patient);

        unitOfWork.AuditReason =
            $"Couverture {coverage.MembershipNumber} close au {validUntil:dd/MM/yyyy} " +
            $"sur le dossier {patient.MedicalRecordNumber}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Consent -------------------------------------------------------------------------------

    public async Task<Result> RecordConsentAsync(Guid patientId, ConsentRequest request,
                                                  byte[]? evidence = null,
                                                  string? evidenceContentType = null,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Edit);
        if (permitted.IsFailure) return permitted;

        var patient = await patients.GetByIdWithDetailsAsync(patientId, ct);
        if (patient is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        var recorded = RecordConsents(patient, [request], evidence, evidenceContentType);
        if (recorded.IsFailure) return recorded;

        patients.Update(patient);

        unitOfWork.AuditReason =
            $"Consentement {request.Purpose} recueilli sur le dossier {patient.MedicalRecordNumber}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> WithdrawConsentAsync(Guid patientId, Guid consentId, string reason,
                                                    CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Edit);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Consent_WithdrawalReasonRequired"));

        var patient = await patients.GetByIdWithDetailsAsync(patientId, ct);
        if (patient is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        var consent = patient.Consents.FirstOrDefault(c => c.Id == consentId);
        if (consent is null) return Result.Failure(Error.NotFound("Consent_NotFound"));

        consent.Withdraw(clock.Now, currentUser.UserId, reason);
        patients.Update(patient);

        unitOfWork.AuditReason =
            $"Retrait du consentement {consent.Purpose} sur le dossier " +
            $"{patient.MedicalRecordNumber} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Alerts --------------------------------------------------------------------------------

    public async Task<Result> AddAlertAsync(Guid patientId, PatientAlertSeverity severity,
                                             MultilingualText message, DateOnly? expiresOn = null,
                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Edit);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(message.Fr))
            return Result.Failure(Error.Validation("Alert_MessageRequired"));

        var patient = await patients.GetByIdWithDetailsAsync(patientId, ct);
        if (patient is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        patient.AddAlert(new PatientAlert(patient.Id, severity, message) { ExpiresOn = expiresOn });
        patients.Update(patient);

        unitOfWork.AuditReason =
            $"Alerte {severity} ajoutée au dossier {patient.MedicalRecordNumber} : {message.Fr}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Merge and erasure ---------------------------------------------------------------------

    /// <summary>
    /// Points a duplicate file at the surviving one.
    /// <para>
    /// The losing record is kept, not deleted: it is the actor on its own audit entries and may be
    /// referenced by an already-issued invoice. Searches follow the pointer, so the clinical
    /// history reads as one file again.
    /// </para>
    /// </summary>
    public async Task<Result> MergeAsync(Guid duplicatePatientId, Guid survivingPatientId,
                                          string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Merge);
        if (permitted.IsFailure) return permitted;

        if (duplicatePatientId == survivingPatientId)
            return Result.Failure(Error.Validation("Patient_CannotMergeIntoItself"));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Patient_MergeReasonRequired"));

        var duplicate = await patients.GetByIdWithDetailsAsync(duplicatePatientId, ct);
        if (duplicate is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        var surviving = await patients.GetByIdAsync(survivingPatientId, ct);
        if (surviving is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        if (duplicate.MergedIntoPatientId is not null)
            return Result.Failure(Error.Conflict("Patient_AlreadyMerged"));

        // Refused rather than chained: a pointer to a record that is itself a pointer means a
        // lookup can loop, and the surviving file is supposed to be the one that is live.
        if (surviving.MergedIntoPatientId is not null)
            return Result.Failure(Error.Conflict("Patient_SurvivorAlreadyMerged"));

        // The duplicate's pending orders were interpreted with the duplicate's demographics and
        // checked against the duplicate's history, which is usually none; from now on they are the
        // survivor's, the file that gets corrected and holds the years of history. Read before the
        // pointer is set, while the duplicate's own merged files are still found under it.
        var demographicsDiffer = Demographics(duplicate) != Demographics(surviving);
        var duplicateFiles = await WithMergedDuplicatesAsync(duplicate.Id, ct);

        // One transaction: the pointer is saved first, inside it, because the history lookup reads
        // the database and finds the survivor's history only once the pointer is there. Run before
        // the save, the re-interpretation still found nothing, and a true duplicate — same name,
        // date of birth and sex — was not re-interpreted at all: a value entered on it before the
        // merge was validated with no previous value and no delta check.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        duplicate.MarkMergedInto(survivingPatientId);

        patients.Update(duplicate);

        unitOfWork.AuditReason =
            $"Fusion du dossier {duplicate.MedicalRecordNumber} dans " +
            $"{surviving.MedicalRecordNumber} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        var (reinterpreted, validated) = await ReinterpretResultsAsync(surviving, duplicateFiles, ct);

        if (reinterpreted > 0 || (demographicsDiffer && validated > 0))
        {
            unitOfWork.AuditReason =
                $"Fusion du dossier {duplicate.MedicalRecordNumber} dans {surviving.MedicalRecordNumber}";

            if (reinterpreted > 0)
                unitOfWork.AuditReason +=
                    $" ; {reinterpreted} résultat(s) non validé(s) réinterprété(s) avec l'antériorité " +
                    "du dossier conservé";

            // Validated results were interpreted with the duplicate's sex or age; when the two
            // files agree on those, nothing about them was wrong.
            if (demographicsDiffer && validated > 0)
                unitOfWork.AuditReason +=
                    $" ; {validated} résultat(s) déjà validé(s) interprété(s) selon les données " +
                    "du doublon, à revoir et à amender si nécessaire";

            await unitOfWork.SaveChangesAsync(ct);
        }

        await unitOfWork.CommitAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Pseudonymises the file at the patient's request (loi n° 08-09, right to erasure).
    /// <para>
    /// Not a delete. The clinical history and the invoice series must survive — the first because
    /// medical records carry a ten-year retention obligation, the second because the numbering may
    /// not have gaps. Refused while the retention period is still running, since the obligation to
    /// keep the record outranks the request to remove it.
    /// </para>
    /// </summary>
    public async Task<Result> AnonymizeAsync(Guid patientId, string reference, string justification,
                                              bool overrideRetention = false,
                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Patients.Anonymize);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(justification))
            return Result.Failure(Error.Validation("Patient_AnonymizeJustificationRequired"));

        var patient = await patients.GetByIdWithDetailsAsync(patientId, ct);
        if (patient is null) return Result.Failure(Error.NotFound("Patient_NotFound"));

        if (patient.IsAnonymized)
            return Result.Failure(Error.Conflict("Patient_AlreadyAnonymized"));

        if (!patient.IsRetentionExpired(clock.Today) && !overrideRetention)
        {
            return Result.Failure(Error.Regulatory("Patient_RetentionNotExpired",
                                                     patient.RetentionUntil));
        }

        patient.Anonymize(clock.Now, reference);
        patients.Update(patient);

        unitOfWork.AuditReason =
            $"Anonymisation du dossier {patient.MedicalRecordNumber} (réf. {reference}) : " +
            justification;

        await unitOfWork.SaveChangesAsync(ct);

        await dataAccessAuditor.LogAsync(DataAccessKind.SubjectAccessRequest, patient.Id,
                                          context: justification, ct: ct);

        return Result.Success();
    }

    // ---- Internals -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<DuplicateCandidate>> FindCandidatesAsync(
        string lastName, string firstName, string? cin, DateOnly? dateOfBirth,
        CancellationToken ct)
    {
        var searchKey = PatientSearchKey.Build(lastName ?? string.Empty,
                                                        firstName ?? string.Empty);

        var found = await duplicates.FindPossibleDuplicatesAsync(searchKey, cin, dateOfBirth, ct);

        var today = clock.Today;

        return
        [
            .. found.Select(p => new DuplicateCandidate(
                p.Id, p.MedicalRecordNumber, p.FullName, p.Gender, p.DateOfBirth,
                p.AgeInYears(today), p.Cin, p.MobilePhone,
                // Says which field matched, because "same name" and "same CIN" carry very
                // different weight for the clerk deciding whether it is the same person.
                !string.IsNullOrWhiteSpace(cin) && p.Cin == cin
                    ? "Patient.Cin"
                    : dateOfBirth is not null && p.DateOfBirth == dateOfBirth
                        ? "Patient.DateOfBirth"
                        : "Patient.Title"))
        ];
    }

    private static Result AttachCoverages(Patient patient, IReadOnlyList<CoverageRequest>? requests,
                                    DateOnly today)
    {
        if (requests is null || requests.Count == 0) return Result.Success();

        var errors = new List<Error>();

        foreach (var request in requests)
        {
            var coverage = new PatientCoverage(patient.Id, request.PayerId,
                                                request.MembershipNumber.Trim(),
                                                request.ValidFrom ?? today)
            {
                ValidUntil = request.ValidUntil,
                IsPrimary = request.IsPrimary,
                Relationship = request.Relationship,
                InsuredPersonName = Blank(request.InsuredPersonName),
                InsuredPersonCin = Normalise(request.InsuredPersonCin),
                EmployerName = Blank(request.EmployerName),
                ReimbursementRateOverride = request.ReimbursementRateOverride,
                IsChronicDiseaseScheme = request.IsChronicDiseaseScheme,
                PriorAgreementNumber = Blank(request.PriorAgreementNumber),
                PriorAgreementValidUntil = request.PriorAgreementValidUntil,
                DirectBillingEnabled = request.DirectBillingEnabled,
                Notes = Blank(request.Notes)
            };

            var valid = coverage.Validate(today);
            if (valid.IsFailure) { errors.AddRange(valid.Errors); continue; }

            patient.AddCoverage(coverage);
        }

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    private Result RecordConsents(Patient patient, IReadOnlyList<ConsentRequest>? requests,
                                   byte[]? evidence = null, string? evidenceContentType = null)
    {
        if (requests is null || requests.Count == 0) return Result.Success();

        var errors = new List<Error>();

        foreach (var request in requests)
        {
            // The law and professional practice require certain purposes to be in writing —
            // HIV serology and genetic testing among them. A verbal tick is not consent there.
            if (PatientConsent.RequiresWrittenEvidence(request.Purpose)
                && request.Channel == ConsentChannel.Verbal)
            {
                errors.Add(Error.Regulatory("Order_ConsentMustBeWritten", request.Purpose));
                continue;
            }

            // For a minor the guardian signs. Recording the file as self-consented would
            // misstate on whose authority the analysis was run.
            var signedBy = Blank(request.SignedByName)
                           ?? (patient.IsMinor(clock.Today) ? patient.GuardianFullName : null);

            if (patient.IsMinor(clock.Today) && string.IsNullOrWhiteSpace(signedBy))
            {
                errors.Add(Error.Regulatory("Patient_MinorRequiresGuardian"));
                continue;
            }

            patient.RecordConsent(new PatientConsent(patient.Id, request.Purpose, request.Channel,
                                                      clock.Now, currentUser.UserId)
            {
                SignedByName = signedBy,
                SignedByCin = Normalise(request.SignedByCin)
                              ?? (patient.IsMinor(clock.Today) ? patient.GuardianCin : null),
                SignedByRelationship = Blank(request.SignedByRelationship)
                                       ?? (patient.IsMinor(clock.Today)
                                           ? patient.GuardianRelationship : null),
                PresentedInLanguage = request.PresentedInLanguage,
                FormVersion = Blank(request.FormVersion),
                ExpiresOn = request.ExpiresOn,
                EvidenceDocument = evidence,
                EvidenceDocumentContentType = Blank(evidenceContentType),
                Notes = Blank(request.Notes)
            });
        }

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Replace(" ", string.Empty).ToUpperInvariant();
}

/// <summary>
/// The result of a registration attempt.
/// <para>
/// When <see cref="PatientId"/> is null and <see cref="Duplicates"/> is non-empty, nothing was
/// written: the caller must show the candidates and either pick one or resubmit with a
/// <see cref="PatientIdentityRequest.DuplicateOverrideReason"/>. Modelled as a successful outcome
/// rather than a failure because the operator did nothing wrong — the system is asking a question.
/// </para>
/// </summary>
public sealed record PatientRegistrationOutcome(
    Guid? PatientId,
    IReadOnlyList<DuplicateCandidate> Duplicates)
{
    public bool NeedsDuplicateDecision => PatientId is null && Duplicates.Count > 0;
}

/// <summary>
/// Candidate-duplicate lookup, behind an interface so the application layer does not depend on the
/// concrete EF repository. Implemented by <c>PatientRepository</c>.
/// </summary>
public interface IPatientDuplicateFinder
{
    Task<IReadOnlyList<Patient>> FindPossibleDuplicatesAsync(
        string searchKey, string? cin, DateOnly? dateOfBirth, CancellationToken ct = default);
}
