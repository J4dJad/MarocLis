using MarocLIS.Domain.Common;
using MarocLIS.Domain.Morocco;

namespace MarocLIS.Domain.Organization;

/// <summary>
/// A physical laboratory or sampling point. Every clinical and accounting record is site-scoped so
/// the same installation can serve a laboratory with satellite collection centres (centres de
/// prélèvement) while keeping each site's DGI invoice sequence and accreditation scope separate.
/// </summary>
public sealed class LabSite : AuditableAggregateRoot, ISoftDeletable
{
    private LabSite() { }

    public LabSite(string code, MultilingualText name, LabSiteKind kind)
    {
        Code = code;
        Name = name;
        Kind = kind;
    }

    /// <summary>Short code used as prefix for accession numbers and invoice numbers, e.g. "CASA".</summary>
    public string Code { get; set; } = string.Empty;

    public MultilingualText Name { get; set; } = new();

    public LabSiteKind Kind { get; set; }

    // ---- Legal identity printed on invoices and reports (DGI + loi 12-01) -------------------

    /// <summary>Registered company name (raison sociale) exactly as filed.</summary>
    public string LegalName { get; set; } = string.Empty;

    /// <summary>ICE — 15 digits. Mandatory on every invoice issued in Morocco.</summary>
    public string? Ice { get; set; }

    /// <summary>IF — Identifiant Fiscal.</summary>
    public string? FiscalId { get; set; }

    /// <summary>RC — Registre de Commerce number, with the tribunal that holds it.</summary>
    public string? CommercialRegisterNumber { get; set; }
    public string? CommercialRegisterCourt { get; set; }

    /// <summary>Taxe professionnelle (ex-patente) article number.</summary>
    public string? ProfessionalTaxNumber { get; set; }

    /// <summary>Employer CNSS affiliation number.</summary>
    public string? CnssAffiliationNumber { get; set; }

    /// <summary>INPE issued by ANAM — required to invoice AMO (CNSS / CNOPS) third-party payers.</summary>
    public string? Inpe { get; set; }

    /// <summary>
    /// Ministry of Health opening authorisation for a medical biology laboratory
    /// (loi n° 12-01 and its implementing decree). Printed on reports.
    /// </summary>
    public string? HealthMinistryAuthorizationNumber { get; set; }
    public DateOnly? HealthMinistryAuthorizationDate { get; set; }

    // ---- Accreditation (ISO 15189) ----------------------------------------------------------

    public bool IsAccredited { get; set; }

    /// <summary>Accreditation number issued by SEMAC / the Moroccan accreditation body.</summary>
    public string? AccreditationNumber { get; set; }
    public DateOnly? AccreditationValidUntil { get; set; }

    // ---- Contact ----------------------------------------------------------------------------

    public MultilingualText AddressLine { get; set; } = new();
    public string City { get; set; } = string.Empty;
    public MoroccanRegion Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    /// <summary>Logo bytes embedded in PDF reports and invoices.</summary>
    public byte[]? LogoPng { get; set; }

    /// <summary>The biologist legally responsible for the site (directeur de laboratoire, loi 12-01).</summary>
    public Guid? ResponsibleBiologistUserId { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    /// <summary>
    /// A site may not issue invoices or release reports until its legally required identifiers are
    /// present. Called before the first invoice of a site is numbered.
    /// </summary>
    public Result ValidateForInvoicing()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(LegalName))
            errors.Add(Error.Regulatory("Site_LegalNameRequired"));
        if (!MoroccanIdentifiers.IsValidIce(Ice))
            errors.Add(Error.Regulatory("Site_IceInvalid"));
        if (!MoroccanIdentifiers.IsValidFiscalId(FiscalId))
            errors.Add(Error.Regulatory("Site_FiscalIdInvalid"));
        if (string.IsNullOrWhiteSpace(AddressLine.Fr) || string.IsNullOrWhiteSpace(City))
            errors.Add(Error.Regulatory("Site_AddressRequired"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>A site must hold a valid ministry authorisation to operate as a biology laboratory.</summary>
    public Result ValidateForOperation(DateOnly today)
    {
        var errors = new List<Error>();

        if (Kind is LabSiteKind.MainLaboratory or LabSiteKind.SatelliteLaboratory &&
            string.IsNullOrWhiteSpace(HealthMinistryAuthorizationNumber))
            errors.Add(Error.Regulatory("Site_MinistryAuthorizationRequired"));

        if (ResponsibleBiologistUserId is null)
            errors.Add(Error.Regulatory("Site_ResponsibleBiologistRequired"));

        if (IsAccredited && AccreditationValidUntil is { } until && until < today)
            errors.Add(Error.Regulatory("Site_AccreditationExpired", until));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum LabSiteKind
{
    /// <summary>Full laboratory with a technical platform and a responsible biologist.</summary>
    MainLaboratory = 0,

    /// <summary>Secondary laboratory attached to the main one.</summary>
    SatelliteLaboratory = 1,

    /// <summary>Collection point only — samples are transported to a laboratory for analysis.</summary>
    CollectionCentre = 2,

    /// <summary>Partner laboratory used for subcontracted (sous-traitance) analyses.</summary>
    ReferralPartner = 3
}
