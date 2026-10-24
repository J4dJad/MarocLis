using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Patients;

/// <summary>A field of the patient file that refuses what was typed, and why.</summary>
/// <param name="Field">The field: <c>LastName</c>, <c>FirstName</c>, <c>Weight</c> or <c>Height</c>.</param>
/// <param name="Key">The text key of the message.</param>
/// <param name="Arguments">The message's arguments.</param>
public sealed record PatientFieldProblem(string Field, string Key, params object[] Arguments);

/// <summary>The rate typed in a coverage's override box, as read.</summary>
/// <param name="Rate">The fraction (0.7 for 70 %), or null when the box is empty or refused.</param>
/// <param name="ProblemKey">Why it is refused, or null.</param>
/// <param name="NeedsFractionConfirmation">« 1 » or « 0,7 » read as a fraction: said back before saving.</param>
public sealed record TypedRate(decimal? Rate, string? ProblemKey, bool NeedsFractionConfirmation);

/// <summary>What the actions of the patient file offer to the operator.</summary>
public sealed record PatientFileActions(bool Save, bool EditLists, bool ReadOnly);

/// <summary>What the patient list offers on the file it points at.</summary>
public sealed record PatientListActions(bool NewPatient, bool Edit, bool NewOrder, bool Anonymize, bool Merge,
                                        bool OpenOrders);

/// <summary>
/// The rules of the patient file that need no window — the age it shows, when a guardian is asked
/// for, what a save refuses before it is sent, how a coverage's rate is read, what a consent and an
/// alert require. They live in the application layer so the workstation and the web client fill in
/// a patient file the same way; <see cref="PatientRegistrationService"/> and the domain remain the
/// authority on what is saved.
/// </summary>
public static class PatientFileRules
{
    /// <summary>The age below which a file needs a guardian (<see cref="Patient.IsMinor"/>).</summary>
    public const int AdultAge = 18;

    /// <summary>The longest message an alert takes, as its columns.</summary>
    public const int AlertMessageMaxLength = 500;

    /// <summary>
    /// The age the file echoes: from the date of birth when one is given, else the age declared — a
    /// declared 0 being no age at all. Null when there is none, or the birth is in the future.
    /// </summary>
    public static int? AgeYears(DateOnly? dateOfBirth, int declaredAge, DateOnly today)
    {
        if (dateOfBirth is not { } dob) return declaredAge > 0 ? declaredAge : null;

        var age = today.Year - dob.Year;
        if (today < dob.AddYears(age)) age--;
        return age < 0 ? null : age;
    }

    /// <summary>Whether the guardian's block is shown and a consent needs its signer.</summary>
    public static bool IsMinor(int? ageYears) => ageYears is { } age && age < AdultAge;

    /// <summary>The pregnancy fields only make sense for a woman: off, and unticked, otherwise.</summary>
    public static bool PregnancyApplies(Gender gender) => gender == Gender.Female;

    /// <summary>
    /// The body surface (Mosteller) the clinical tab shows, from the weight and height as typed;
    /// null while either is missing, unreadable or not positive.
    /// </summary>
    public static decimal? BodySurfaceM2(string? weight, string? height) =>
        TypedNumber.Parse(weight) is { } w && TypedNumber.Parse(height) is { } h && w > 0 && h > 0
            ? Math.Round((decimal)Math.Sqrt((double)(w * h) / 3600d), 3)
            : null;

    /// <summary>A weight or height as the file records it: a positive number, or none.</summary>
    public static decimal? Measure(string? typed) =>
        TypedNumber.Parse(typed) is { } value && value > 0 ? value : null;

    /// <summary>
    /// What a save refuses before it is sent, in the order the form asks: the last name, the first
    /// name, then a weight or height the form cannot read (« 72 kg », « 1m70 ») or below zero —
    /// which was saved as no value without a word, and the calculations that use it (clearance,
    /// body surface) then had none. Null when nothing is refused.
    /// </summary>
    public static PatientFieldProblem? SaveProblem(string? lastName, string? firstName, string? weight, string? height)
    {
        if (string.IsNullOrWhiteSpace(lastName)) return new PatientFieldProblem("LastName", "Patient_LastNameRequired");
        if (string.IsNullOrWhiteSpace(firstName)) return new PatientFieldProblem("FirstName", "Patient_FirstNameRequired");

        foreach (var (field, text) in new[] { ("Weight", weight), ("Height", height) })
        {
            if (TypedNumber.IsUnreadable(text))
                return new PatientFieldProblem(field, "Result_NotANumber", text!.Trim());

            if (TypedNumber.Parse(text) is < 0m)
                return new PatientFieldProblem(field, "Patient_BodyMeasureOutOfRange",
                                               PatientRegistrationService.MaxWeightKg,
                                               PatientRegistrationService.MaxHeightCm);
        }

        return null;
    }

    /// <summary>
    /// Whether the duplicate check runs as the identity is typed: on a new file only — a saved one
    /// matches itself — and once both names are there.
    /// </summary>
    public static bool ChecksDuplicates(bool isNewFile, string? lastName, string? firstName) =>
        isNewFile && !string.IsNullOrWhiteSpace(lastName) && !string.IsNullOrWhiteSpace(firstName);

    /// <summary>A CIN match is near-certain identity; a name match is a coincidence worth checking.</summary>
    public static bool IsCertainMatch(DuplicateCandidate candidate) => candidate.MatchedOn == "Patient.Cin";

    /// <summary>
    /// What the file offers. A file opened by someone who may only read it shows everything and
    /// changes nothing; an anonymised file is a record of an erasure, not an editable patient.
    /// </summary>
    public static PatientFileActions Actions(bool isNewFile, bool isAnonymized, ICurrentUser user)
    {
        var save = !isAnonymized && user.Has(isNewFile ? Permissions.Patients.Create : Permissions.Patients.Edit);
        return new PatientFileActions(save, save && user.Has(Permissions.Patients.Edit), isAnonymized);
    }

    // ---- The list -------------------------------------------------------------------------------

    /// <summary>
    /// What the patient list offers on the file it points at. An anonymised file is the trace of an
    /// erasure: it is not anonymised again, merged or given a new request, all three of which the
    /// services refuse. Merging needs two files, so it is only offered once a search has produced
    /// more than one — exactly the situation a duplicate creates.
    /// </summary>
    public static PatientListActions ListActions(bool hasSelection, bool isAnonymized, int filesShown, ICurrentUser user)
    {
        var live = hasSelection && !isAnonymized;
        return new PatientListActions(
            NewPatient: user.Has(Permissions.Patients.Create),
            Edit: hasSelection,
            NewOrder: live && user.Has(Permissions.Orders.Create),
            Anonymize: live && user.Has(Permissions.Patients.Anonymize),
            Merge: live && filesShown > 1 && user.Has(Permissions.Patients.Merge),
            OpenOrders: user.Has(Permissions.Orders.View));
    }

    /// <summary>
    /// The files a duplicate may be merged into: the others of the result set, never an anonymised
    /// one (the service refuses it), the file with the most orders first.
    /// </summary>
    public static IReadOnlyList<T> MergeSurvivors<T>(IEnumerable<T> shown, Guid duplicateId, Func<T, Guid> id,
                                                     Func<T, bool> isAnonymized, Func<T, int> orderCount) =>
        [.. shown.Where(p => id(p) != duplicateId && !isAnonymized(p)).OrderByDescending(orderCount)];

    /// <summary>Loi 08-09: a search is logged as an access once it names somebody — three characters.</summary>
    public static bool IsLoggedSearch(string term) => term.Trim().Length >= 3;

    /// <summary>The reference an anonymised file keeps: its record number without the dashes.</summary>
    public static string AnonymizationReference(string medicalRecordNumber) =>
        medicalRecordNumber.Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>The service's refusal that the anonymisation may override, once confirmed.</summary>
    public const string RetentionNotExpiredKey = "Patient_RetentionNotExpired";

    // ---- Coverage -------------------------------------------------------------------------------

    /// <summary>The insured person is only meaningful for a dependent (ayant droit).</summary>
    public static bool AsksInsuredPerson(BeneficiaryRelationship relationship) =>
        relationship != BeneficiaryRelationship.Self;

    /// <summary>
    /// Reads the override rate as a clerk types it off the card: « 70 », « 70 % » or « 0,70 ».
    /// Above 1 it is a percentage; at most 1 it is a fraction (« 1 » is 100 %), which is said back
    /// before it is saved, since a clerk who meant a small percentage would never notice.
    /// </summary>
    public static TypedRate ReadRate(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return new TypedRate(null, null, false);

        if (!decimal.TryParse(typed.Replace(',', '.').TrimEnd('%', ' '), NumberStyles.Float,
                              CultureInfo.InvariantCulture, out var parsed))
            return new TypedRate(null, "Result_NotANumber", false);

        var rate = parsed > 1m ? parsed / 100m : parsed;
        if (rate is < 0m or > 1m) return new TypedRate(null, "Coverage_RateOutOfRange", false);

        return new TypedRate(rate, null, parsed is > 0m and <= 1m);
    }

    /// <summary>
    /// What a coverage lacks before it is added, as a text key: the membership number, and the prior
    /// agreement of a chronic-disease (ALD) scheme, which pays 100 % only on that reference.
    /// </summary>
    public static string? CoverageProblem(string? membershipNumber, bool isChronicDiseaseScheme, string? priorAgreement)
    {
        if (string.IsNullOrWhiteSpace(membershipNumber)) return "Coverage_MembershipNumberRequired";
        if (isChronicDiseaseScheme && string.IsNullOrWhiteSpace(priorAgreement)) return "Coverage_PriorAgreementRequired";
        return null;
    }

    /// <summary>
    /// The rate a coverage not yet saved will pay: 100 % under a chronic-disease scheme, else the
    /// override, else the payer's own.
    /// </summary>
    public static decimal PendingRate(bool isChronicDiseaseScheme, decimal? overrideRate, decimal? payerDefault) =>
        isChronicDiseaseScheme ? 1m : overrideRate ?? payerDefault ?? 0m;

    /// <summary>The text keys of what a payer implies, before anything is saved: the base its price is on, and the prescription.</summary>
    public static IReadOnlyList<string> PayerNoticeKeys(bool usesNationalReferenceTariff, bool requiresPrescription)
    {
        var keys = new List<string>(2)
        {
            usesNationalReferenceTariff ? "Coverage.PricedOnNationalTariff" : "Coverage.PricedOnLabPriceList"
        };
        if (requiresPrescription) keys.Add("Coverage.PrescriptionRequired");
        return keys;
    }

    /// <summary>Only an open coverage can be ended.</summary>
    public static bool CanEndCoverage(DateOnly? validUntil) => validUntil is null;

    // ---- Consent --------------------------------------------------------------------------------

    /// <summary>
    /// What a consent lacks before it is recorded, as a text key: the written form a purpose requires
    /// by law, then the signer of a minor's consent.
    /// </summary>
    public static string? ConsentProblem(ConsentPurpose purpose, ConsentChannel channel, bool isMinor, string? signedBy)
    {
        if (PatientConsent.RequiresWrittenEvidence(purpose) && channel == ConsentChannel.Verbal)
            return "Order_ConsentMustBeWritten";
        if (isMinor && string.IsNullOrWhiteSpace(signedBy)) return "Patient_MinorRequiresGuardian";
        return null;
    }

    /// <summary>
    /// The notice under the purpose, as a text key, while it is chosen rather than after Save: the
    /// written requirement (and that a verbal one will be refused), or a minor's guardian signing.
    /// </summary>
    public static string? ConsentNoticeKey(ConsentPurpose purpose, ConsentChannel channel, bool isMinor)
    {
        if (!PatientConsent.RequiresWrittenEvidence(purpose))
            return isMinor ? "Consent.MinorSignedByGuardian" : null;

        return channel == ConsentChannel.Verbal ? "Order_ConsentMustBeWritten" : "Consent.WrittenRequiredNotice";
    }

    /// <summary>A consent's state: withdrawn, missing the document the law requires, or active.</summary>
    public static string ConsentStateKey(bool withdrawn, bool requiresWrittenEvidence, bool hasEvidenceDocument) =>
        withdrawn ? "Consent.Withdrawn"
        : requiresWrittenEvidence && !hasEvidenceDocument ? "Consent.EvidenceMissing"
        : "Consent.Active";

    /// <summary>Only a consent still in force can be withdrawn.</summary>
    public static bool CanWithdrawConsent(DateTimeOffset? withdrawnAt) => withdrawnAt is null;

    // ---- Alerts ---------------------------------------------------------------------------------

    /// <summary>An alert needs its French message.</summary>
    public static string? AlertProblem(string? messageFrench) =>
        string.IsNullOrWhiteSpace(messageFrench) ? "Alert_MessageRequired" : null;

    /// <summary>
    /// A critical alert stops the next operator mid-registration, so choosing it is confirmed: an
    /// alert that interrupts everybody gets switched off, and then the one that mattered is off too.
    /// </summary>
    public static bool AlertNeedsConfirmation(PatientAlertSeverity severity) => severity == PatientAlertSeverity.Critical;

    /// <summary>What the chosen severity does, said before it is chosen.</summary>
    public static string AlertNoticeKey(PatientAlertSeverity severity) => severity switch
    {
        PatientAlertSeverity.Critical => "Patient.AlertCriticalNotice",
        PatientAlertSeverity.Warning => "Patient.AlertWarningNotice",
        _ => "Patient.AlertInformationNotice"
    };
}
