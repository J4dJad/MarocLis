using MarocLIS.Domain.Common;
using MarocLIS.Domain.Morocco;

namespace MarocLIS.Domain.Patients;

/// <summary>
/// An insurance entitlement attached to a patient. In Morocco a single patient frequently carries
/// a compulsory AMO scheme plus a complementary private policy, and the laboratory must invoice
/// them in order: AMO first on the TNR base, complementary on the remainder, patient last.
/// </summary>
public sealed class PatientCoverage : Entity, IAuditable
{
    private PatientCoverage() { }

    public PatientCoverage(Guid patientId, Guid payerId, string membershipNumber, DateOnly validFrom)
    {
        PatientId = patientId;
        PayerId = payerId;
        MembershipNumber = membershipNumber;
        ValidFrom = validFrom;
    }

    public Guid PatientId { get; private set; }

    /// <summary>The paying organisation (CNSS, CNOPS, a mutuelle, an insurer, an employer).</summary>
    public Guid PayerId { get; private set; }
    public Payer? Payer { get; private set; }

    /// <summary>Numéro d'immatriculation / d'affiliation with the scheme.</summary>
    public string MembershipNumber { get; set; } = string.Empty;

    /// <summary>The insured person, when the patient is a dependent (ayant droit).</summary>
    public string? InsuredPersonName { get; set; }
    public string? InsuredPersonCin { get; set; }
    public BeneficiaryRelationship Relationship { get; set; } = BeneficiaryRelationship.Self;

    /// <summary>Employer, when the entitlement flows from salaried employment.</summary>
    public string? EmployerName { get; set; }

    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidUntil { get; set; }

    /// <summary>The scheme to invoice first.</summary>
    public bool IsPrimary { get; set; } = true;

    /// <summary>
    /// Reimbursement rate applied to the TNR base, as a fraction (0.70 = 70 %). AMO biology is
    /// commonly reimbursed at 70 % of the Tarif National de Référence; ALD/ALC files reach 100 %.
    /// Overrides the payer default when set.
    /// </summary>
    public decimal? ReimbursementRateOverride { get; set; }

    /// <summary>
    /// ALD / ALC — affection de longue durée / coûteuse. Entitles the patient to full coverage of
    /// the acts linked to the condition; the prior-agreement reference is required on the claim.
    /// </summary>
    public bool IsChronicDiseaseScheme { get; set; }
    public string? PriorAgreementNumber { get; set; }
    public DateOnly? PriorAgreementValidUntil { get; set; }

    /// <summary>Third-party payment: the laboratory bills the payer directly instead of the patient.</summary>
    public bool DirectBillingEnabled { get; set; } = true;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public bool IsValidOn(DateOnly date) =>
        ValidFrom <= date && (ValidUntil is null || ValidUntil >= date);

    /// <summary>
    /// Effective reimbursement rate for a given act date: 100 % for a live ALD agreement,
    /// otherwise the override or the payer's default.
    /// </summary>
    public decimal EffectiveRate(DateOnly on, decimal payerDefaultRate)
    {
        if (IsChronicDiseaseScheme &&
            (PriorAgreementValidUntil is null || PriorAgreementValidUntil >= on) &&
            !string.IsNullOrWhiteSpace(PriorAgreementNumber))
            return 1.0m;

        return ReimbursementRateOverride ?? payerDefaultRate;
    }

    public Result Validate(DateOnly today)
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(MembershipNumber))
            errors.Add(Error.Validation("Coverage_MembershipNumberRequired"));
        if (ValidUntil is { } until && until < ValidFrom)
            errors.Add(Error.Validation("Coverage_InvalidPeriod"));
        if (Relationship != BeneficiaryRelationship.Self && string.IsNullOrWhiteSpace(InsuredPersonName))
            errors.Add(Error.Validation("Coverage_InsuredPersonRequired"));
        if (IsChronicDiseaseScheme && string.IsNullOrWhiteSpace(PriorAgreementNumber))
            errors.Add(Error.Regulatory("Coverage_PriorAgreementRequired"));
        if (ReimbursementRateOverride is { } rate and (< 0m or > 1m))
            errors.Add(Error.Validation("Coverage_RateOutOfRange"));
        if (!IsValidOn(today) && ValidFrom > today)
            errors.Add(Error.Validation("Coverage_NotYetEffective"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum BeneficiaryRelationship
{
    Self = 0,
    Spouse = 1,
    Child = 2,
    Parent = 3,
    Other = 9
}

/// <summary>
/// An organisation that can be invoiced. Covers the compulsory AMO schemes, complementary
/// insurers and mutuelles, employers under convention, and the patient-pays case.
/// </summary>
public sealed class Payer : AuditableAggregateRoot
{
    private Payer() { }

    public Payer(string code, MultilingualText name, PayerKind kind)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
        Kind = kind;
    }

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();
    public PayerKind Kind { get; set; }

    /// <summary>Default share of the TNR base reimbursed by this payer (0.70 = 70 %).</summary>
    public decimal DefaultReimbursementRate { get; set; } = 0.70m;

    /// <summary>
    /// When true, invoices are priced on the Tarif National de Référence rather than on the
    /// laboratory's own price list. All AMO schemes require this.
    /// </summary>
    public bool UsesNationalReferenceTariff { get; set; } = true;

    /// <summary>Third-party payment agreement in force (convention de tiers payant).</summary>
    public bool ThirdPartyPaymentAgreement { get; set; }
    public string? AgreementNumber { get; set; }
    public DateOnly? AgreementValidUntil { get; set; }

    /// <summary>Contractual settlement delay, used by the receivables ageing report.</summary>
    public int PaymentTermsDays { get; set; } = 60;

    /// <summary>Whether a prescriber's prescription must be attached to the claim.</summary>
    public bool RequiresPrescription { get; set; } = true;

    /// <summary>Whether claims must carry the biologist's INPE.</summary>
    public bool RequiresInpeOnClaim { get; set; }

    public string? Ice { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }

    /// <summary>
    /// The payer's account in the accounting export: digits for a general sub-account (34211), any
    /// other text for an auxiliary account under the payers' receivable. Null: the default account
    /// with the payer's code as third party.
    /// </summary>
    public string? AccountingAccount { get; set; }

    public bool IsActive { get; set; } = true;

    // Not ISoftDeletable. A payer the laboratory stops accepting is deactivated —
    // <see cref="IsActive"/> — and has to stay resolvable for as long as its invoices and
    // settlements do, which under the DGI's retention rules is years.
    //
    // PatientCoverage holds a required reference here and carries no filter of its own, so a global
    // "not deleted" filter on the payer filtered coverages out from under it: deactivating a payer
    // would have made covered patients look uninsured, repriced their invoices at the full rate,
    // and dropped the coverage that justified a claim already submitted.

    public bool IsAgreementValid(DateOnly on) =>
        !ThirdPartyPaymentAgreement || AgreementValidUntil is null || AgreementValidUntil >= on;

    /// <summary>
    /// Whether this payer is one of the four compulsory AMO schemes. Several rules below follow from
    /// it, because AMO terms are set by convention and are not the laboratory's to choose.
    /// </summary>
    public bool IsCompulsoryScheme =>
        Kind is PayerKind.AmoCnss or PayerKind.AmoCnops
             or PayerKind.AmoTadamon or PayerKind.AmoAchamil;

    /// <summary>
    /// Rules a payer record has to satisfy before it can price an invoice.
    /// <para>
    /// These were enforced nowhere, because nothing could create or edit a payer — the seeded rows
    /// were the only ones that ever existed and they were written by hand. The moment an
    /// administrator can type a reimbursement rate, the arithmetic in
    /// <c>ActPricingCalculator</c> depends on these being true: it multiplies a base by
    /// <see cref="DefaultReimbursementRate"/> and treats it as a fraction, so a well-meaning "70"
    /// entered for 70 % would bill the payer seventy times the reference tariff.
    /// </para>
    /// </summary>
    public Result Validate()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(Code))
            errors.Add(Error.Validation("Payer_CodeRequired"));

        if (string.IsNullOrWhiteSpace(Name.Fr))
            errors.Add(Error.Validation("Payer_NameFrenchRequired"));

        // A fraction, not a percentage. 0.70 means 70 %.
        if (DefaultReimbursementRate is < 0m or > 1m)
            errors.Add(Error.Validation("Payer_RateOutOfRange"));

        if (PaymentTermsDays is < 0 or > 365)
            errors.Add(Error.Validation("Payer_PaymentTermsOutOfRange"));

        if (Ice is not null && !MoroccanIdentifiers.IsValidIce(Ice))
            errors.Add(Error.Validation("Payer_IceInvalid"));

        // The patient settling in cash is not reimbursed by anybody, and has no convention.
        if (Kind == PayerKind.SelfPay)
        {
            if (DefaultReimbursementRate != 0m)
                errors.Add(Error.Validation("Payer_SelfPayRateMustBeZero"));

            if (ThirdPartyPaymentAgreement)
                errors.Add(Error.Validation("Payer_SelfPayCannotHaveAgreement"));
        }

        // AMO settles against the Tarif National de Référence, never against the laboratory's own
        // price list. Pricing off the charged price would overstate the payer's liability and
        // understate the patient's ticket modérateur — the crux of AMO settlement.
        if (IsCompulsoryScheme && !UsesNationalReferenceTariff)
            errors.Add(Error.Validation("Payer_AmoRequiresReferenceTariff"));

        // Deliberately not a rule: a third-party arrangement without a citable number is not
        // refused here. For CNSS and CNOPS, tiers payant is a national arrangement rather than a
        // per-laboratory numbered convention, and the identifier a claim actually carries is the
        // biologist's INPE — so "no agreement number" is a normal state for the majority of
        // payers, and the ten rows the seeder ships are all in it. Refusing the save would have
        // made the shipped reference data invalid against its own invariants, and would have
        // blocked an administrator correcting a rate until they invented a number to type.
        //
        // It is still worth seeing, so PayerRow.Problems reports it as a flag on the maintenance
        // grid. Visible, and the administrator decides.

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

/// <summary>Categories of payer found in the Moroccan health-financing landscape.</summary>
public enum PayerKind
{
    /// <summary>The patient settles the invoice directly (paiement direct).</summary>
    SelfPay = 0,

    /// <summary>AMO — private-sector employees, managed by the CNSS.</summary>
    AmoCnss = 1,

    /// <summary>AMO — public-sector employees (historically CNOPS).</summary>
    AmoCnops = 2,

    /// <summary>AMO Tadamon — the scheme for economically vulnerable people that replaced RAMED.</summary>
    AmoTadamon = 3,

    /// <summary>AMO Achamil — independent workers and non-salaried professionals.</summary>
    AmoAchamil = 4,

    /// <summary>Military and paramilitary scheme (FAR / mutuelles des forces armées).</summary>
    ArmedForces = 5,

    /// <summary>Mutuelle providing complementary cover (MGPAP, MODEP, OMFAM, …).</summary>
    Mutuelle = 6,

    /// <summary>Private insurance company.</summary>
    PrivateInsurer = 7,

    /// <summary>Employer paying under a direct convention with the laboratory.</summary>
    Employer = 8,

    /// <summary>Another laboratory or clinic subcontracting analyses to us.</summary>
    ReferringInstitution = 9,

    /// <summary>Clinical trial sponsor or research protocol.</summary>
    ResearchSponsor = 10
}
