using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Morocco;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;

namespace MarocLIS.Application.Administration;

/// <summary>The legal and operational identity of one laboratory site.</summary>
public sealed record SiteRequest
{
    public required Guid SiteId { get; init; }

    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }
    public LabSiteKind Kind { get; init; }

    // ---- Legal identity printed on every invoice and report --------------------------------

    public required string LegalName { get; init; }
    public string? Ice { get; init; }
    public string? FiscalId { get; init; }
    public string? CommercialRegisterNumber { get; init; }
    public string? CommercialRegisterCourt { get; init; }
    public string? ProfessionalTaxNumber { get; init; }
    public string? CnssAffiliationNumber { get; init; }
    public string? Inpe { get; init; }

    /// <summary>Loi n° 12-01: without this the site may not operate as a biology laboratory.</summary>
    public string? HealthMinistryAuthorizationNumber { get; init; }
    public DateOnly? HealthMinistryAuthorizationDate { get; init; }

    public bool IsAccredited { get; init; }
    public string? AccreditationNumber { get; init; }
    public DateOnly? AccreditationValidUntil { get; init; }

    /// <summary>The biologist who answers for the site. Required before any report can be released.</summary>
    public Guid? ResponsibleBiologistUserId { get; init; }

    public MultilingualText? AddressLine { get; init; }
    public string? City { get; init; }
    public MoroccanRegion Region { get; init; }
    public string? PostalCode { get; init; }
    public string? Phone { get; init; }
    public string? Fax { get; init; }
    public string? Email { get; init; }
    public string? Website { get; init; }

    public byte[]? LogoPng { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed record SiteDetail(
    Guid Id,
    string Code,
    MultilingualText Name,
    LabSiteKind Kind,
    string LegalName,
    string? Ice,
    string? FiscalId,
    string? CommercialRegisterNumber,
    string? CommercialRegisterCourt,
    string? ProfessionalTaxNumber,
    string? CnssAffiliationNumber,
    string? Inpe,
    string? HealthMinistryAuthorizationNumber,
    DateOnly? HealthMinistryAuthorizationDate,
    bool IsAccredited,
    string? AccreditationNumber,
    DateOnly? AccreditationValidUntil,
    Guid? ResponsibleBiologistUserId,
    string? ResponsibleBiologistName,
    MultilingualText? AddressLine,
    string? City,
    MoroccanRegion Region,
    string? PostalCode,
    string? Phone,
    string? Fax,
    string? Email,
    string? Website,
    bool HasLogo,
    bool IsActive,
    IReadOnlyList<string> InvoicingBlockers,
    IReadOnlyList<string> OperationBlockers);

public sealed record SequenceRow(
    Guid SequenceId,
    SequencePurpose Purpose,
    Guid? SiteId,
    string? SiteCode,
    int? Year,
    string Format,
    string Preview,
    long NextValue,
    long LastAllocatedValue,
    DateTimeOffset? LastAllocatedAt,
    bool IsClosed,
    bool HasBeenUsed);

public sealed record SettingRow(
    Guid SettingId,
    string Key,
    string? Value,
    Guid? SiteId,
    string? Description,
    bool IsSecuritySensitive);

/// <summary>
/// Installation configuration: the site's legal identity, the document numbering, and the
/// database-held settings.
/// <para>
/// This is not a convenience screen. The seeder deliberately creates the site with placeholder
/// text and no identifiers, on the assumption that an administrator completes them on day one —
/// and until they are complete, <see cref="LabSite.ValidateForInvoicing"/> and
/// <see cref="LabSite.ValidateForOperation"/> refuse every invoice and every report release. Before
/// this service existed there was no way to complete them, so billing and reporting were
/// unreachable on any fresh installation.
/// </para>
/// <para>
/// The numbering format is editable only until the first document is numbered. After that it is
/// frozen: the DGI requires an unbroken chronological invoice series, and changing the format
/// mid-series produces two incompatible runs of numbers that cannot be reconciled.
/// </para>
/// </summary>
public sealed class ConfigurationService(
    IRepository<LabSite> sites,
    IRepository<AppUser> users,
    IAdministrationQueries queries,
    IConfigurationWriter writer,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ISecurityAuditor securityAuditor,
    IClock clock)
{
    // ---- Sites ---------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<SiteDetail>>> GetSitesAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Administration.ManageSites,
                                          Permissions.Administration.ViewUsers);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SiteDetail>>(permitted.FirstError);

        return Result.Success(await queries.GetSiteDetailsAsync(clock.Today, ct));
    }

    /// <summary>
    /// Saves the site's identity.
    /// <para>
    /// Deliberately does <b>not</b> refuse an incomplete record: an administrator configuring a new
    /// installation fills this in over several sittings, and refusing a partial save would force
    /// them to keep it on paper. The completeness gates stay where they belong — on issuing an
    /// invoice and on releasing a report — and this service reports what is still missing so the
    /// screen can show it.
    /// </para>
    /// </summary>
    public async Task<Result<SiteSaveOutcome>> SaveSiteAsync(SiteRequest request,
                                                              CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSites);
        if (permitted.IsFailure) return Result.Failure<SiteSaveOutcome>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code))
            errors.Add(Error.Validation("Site_CodeRequired"));

        if (string.IsNullOrWhiteSpace(request.Name.Fr))
            errors.Add(Error.Validation("Site_NameRequired"));

        // Validated when present rather than required: a site may be saved incomplete, but a
        // malformed identifier is a typo that would print on a legal document.
        if (!string.IsNullOrWhiteSpace(request.Ice) && !MoroccanIdentifiers.IsValidIce(request.Ice))
            errors.Add(Error.Validation("Site_IceInvalid"));

        if (!string.IsNullOrWhiteSpace(request.FiscalId)
            && !MoroccanIdentifiers.IsValidFiscalId(request.FiscalId))
            errors.Add(Error.Validation("Site_FiscalIdInvalid"));

        if (!string.IsNullOrWhiteSpace(request.Inpe) && !MoroccanIdentifiers.IsValidInpe(request.Inpe))
            errors.Add(Error.Validation("Site_InpeInvalid"));

        if (!string.IsNullOrWhiteSpace(request.CnssAffiliationNumber)
            && !MoroccanIdentifiers.IsValidCnssNumber(request.CnssAffiliationNumber))
            errors.Add(Error.Validation("Site_CnssInvalid"));

        if (request.IsAccredited && string.IsNullOrWhiteSpace(request.AccreditationNumber))
            errors.Add(Error.Validation("Site_AccreditationNumberRequired"));

        if (errors.Count > 0) return Result.Failure<SiteSaveOutcome>(errors);

        var site = await sites.GetByIdAsync(request.SiteId, ct);
        if (site is null) return Result.Failure<SiteSaveOutcome>(Error.NotFound("Site_NotFound"));

        // The responsible biologist must actually be one. Loi n° 12-01 reserves the direction of a
        // medical biology laboratory to a registered biologist, so pointing this at a technician
        // would make every report they sign indefensible.
        if (request.ResponsibleBiologistUserId is { } biologistId)
        {
            var biologist = await users.GetByIdAsync(biologistId, ct);

            if (biologist is null)
                return Result.Failure<SiteSaveOutcome>(Error.NotFound("User_NotFound"));

            if (biologist.Qualification is not (ProfessionalQualification.MedicalBiologist
                                             or ProfessionalQualification.PharmacistBiologist))
                return Result.Failure<SiteSaveOutcome>(
                    Error.Regulatory("Site_ResponsibleMustBeBiologist"));

            if (string.IsNullOrWhiteSpace(biologist.ProfessionalOrderNumber))
                return Result.Failure<SiteSaveOutcome>(
                    Error.Regulatory("Validation_RequiresOrderRegistration"));
        }

        var codeChanged = !string.Equals(site.Code, request.Code.Trim(),
                                          StringComparison.OrdinalIgnoreCase);

        // The site code is a prefix in accession and invoice numbers. Renaming it after documents
        // exist would split one series into two that no longer sort together.
        if (codeChanged && await queries.SiteHasNumberedDocumentsAsync(site.Id, ct))
            return Result.Failure<SiteSaveOutcome>(Error.Conflict("Site_CodeFrozenAfterFirstDocument"));

        if (codeChanged && await queries.SiteCodeExistsAsync(request.Code.Trim(), site.Id, ct))
            return Result.Failure<SiteSaveOutcome>(Error.Conflict("Site_CodeAlreadyTaken",
                                                                   request.Code.Trim()));

        site.Code = request.Code.Trim().ToUpperInvariant();
        site.Name = request.Name;
        site.Kind = request.Kind;
        site.LegalName = request.LegalName.Trim();
        site.Ice = Digits(request.Ice);
        site.FiscalId = Digits(request.FiscalId);
        site.CommercialRegisterNumber = Blank(request.CommercialRegisterNumber);
        site.CommercialRegisterCourt = Blank(request.CommercialRegisterCourt);
        site.ProfessionalTaxNumber = Blank(request.ProfessionalTaxNumber);
        site.CnssAffiliationNumber = Digits(request.CnssAffiliationNumber);
        site.Inpe = Digits(request.Inpe);
        site.HealthMinistryAuthorizationNumber = Blank(request.HealthMinistryAuthorizationNumber);
        site.HealthMinistryAuthorizationDate = request.HealthMinistryAuthorizationDate;
        site.IsAccredited = request.IsAccredited;
        site.AccreditationNumber = Blank(request.AccreditationNumber);
        site.AccreditationValidUntil = request.AccreditationValidUntil;
        site.ResponsibleBiologistUserId = request.ResponsibleBiologistUserId;
        site.AddressLine = request.AddressLine ?? site.AddressLine;
        // City is non-nullable on the site and is printed in the invoice and report headers the DGI
        // and the Ministry require, so a blank in the request leaves the stored value alone rather
        // than erasing it.
        site.City = Blank(request.City) ?? site.City;
        site.Region = request.Region;
        site.PostalCode = Blank(request.PostalCode);
        site.Phone = MoroccanIdentifiers.TryNormalizePhone(request.Phone) ?? Blank(request.Phone);
        site.Fax = Blank(request.Fax);
        site.Email = Blank(request.Email);
        site.Website = Blank(request.Website);
        site.IsActive = request.IsActive;

        if (request.LogoPng is { Length: > 0 }) site.LogoPng = request.LogoPng;

        sites.Update(site);

        unitOfWork.AuditReason = $"Identité du site {site.Code} mise à jour";
        await unitOfWork.SaveChangesAsync(ct);

        // The legal identity of the laboratory is a security-relevant setting: it is what a
        // recipient uses to establish who produced a result.
        await securityAuditor.LogAsync(SecurityEventKind.SecuritySettingChanged, succeeded: true,
                                        details: $"Site {site.Code} — identité légale", ct: ct);

        var invoicing = site.ValidateForInvoicing();
        var operation = site.ValidateForOperation(clock.Today);

        return Result.Success(new SiteSaveOutcome(
            site.Id,
            [.. invoicing.Errors.Select(e => e.ResourceKey)],
            [.. operation.Errors.Select(e => e.ResourceKey)]));
    }

    // ---- Numbering -----------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<SequenceRow>>> GetSequencesAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<SequenceRow>>(permitted.FirstError);

        return Result.Success(await queries.GetSequencesAsync(clock.Now, ct));
    }

    /// <summary>
    /// Changes a sequence's format.
    /// <para>
    /// Refused once the sequence has handed out a number. The DGI requires an unbroken
    /// chronological invoice series; two formats in one series cannot be reconciled, and the
    /// documents already issued cannot be renumbered.
    /// </para>
    /// </summary>
    public async Task<Result> SetSequenceFormatAsync(Guid sequenceId, string format,
                                                      CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(format))
            return Result.Failure(Error.Validation("Sequence_FormatRequired"));

        if (!format.Contains("{SEQ", StringComparison.Ordinal))
            return Result.Failure(Error.Validation("Sequence_FormatMustContainSeq"));

        var sequence = await writer.GetSequenceAsync(sequenceId, ct);
        if (sequence is null) return Result.Failure(Error.NotFound("Sequence_NotFound"));

        if (sequence.LastAllocatedValue > 0)
            return Result.Failure(Error.Regulatory("Sequence_FormatFrozenAfterFirstUse",
                                                     sequence.LastAllocatedValue));

        var previous = sequence.Format;
        sequence.Format = format.Trim();

        unitOfWork.AuditReason =
            $"Format de la numérotation {sequence.Purpose} : « {previous} » → « {sequence.Format} »";

        await unitOfWork.SaveChangesAsync(ct);

        await securityAuditor.LogAsync(SecurityEventKind.SecuritySettingChanged, succeeded: true,
                                        details: $"{sequence.Purpose} → {sequence.Format}", ct: ct);

        return Result.Success();
    }

    /// <summary>
    /// Closes a numbering series at a financial-year end, or reopens one closed by mistake.
    /// Reopening is audited separately: it lets a document be numbered into a period whose accounts
    /// may already have been filed.
    /// </summary>
    public async Task<Result> SetSequenceClosedAsync(Guid sequenceId, bool closed, string reason,
                                                      CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Sequence_CloseReasonRequired"));

        var sequence = await writer.GetSequenceAsync(sequenceId, ct);
        if (sequence is null) return Result.Failure(Error.NotFound("Sequence_NotFound"));

        if (sequence.IsClosed == closed)
            return Result.Failure(Error.Validation("Sequence_AlreadyInThatState"));

        if (closed) sequence.Close(clock.Now); else sequence.Reopen();

        unitOfWork.AuditReason =
            $"{(closed ? "Clôture" : "Réouverture")} de la numérotation {sequence.Purpose} : {reason}";

        await unitOfWork.SaveChangesAsync(ct);

        await securityAuditor.LogAsync(SecurityEventKind.SecuritySettingChanged, succeeded: true,
                                        details: $"{sequence.Purpose} " +
                                                 $"{(closed ? "closed" : "reopened")}: {reason}",
                                        ct: ct);

        return Result.Success();
    }

    // ---- Settings ------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<SettingRow>>> GetSettingsAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<SettingRow>>(permitted.FirstError);

        return Result.Success(await queries.GetSettingsAsync(ct));
    }

    public async Task<Result> SetSettingAsync(string key, string? value, Guid? siteId = null,
                                               string? description = null,
                                               bool isSecuritySensitive = false,
                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(key))
            return Result.Failure(Error.Validation("Setting_KeyRequired"));

        var normalised = key.Trim();

        var existing = await writer.GetSettingAsync(normalised, siteId, ct);

        if (existing is null)
        {
            var setting = new AppSetting(normalised, value, siteId)
            {
                Description = Blank(description),
                IsSecuritySensitive = isSecuritySensitive
            };

            await writer.AddSettingAsync(setting, ct);
            unitOfWork.AuditReason = $"Création du paramètre {normalised}";
        }
        else
        {
            var previous = existing.Value;
            existing.Value = value;
            if (description is not null) existing.Description = Blank(description);

            // A sensitive setting's previous value is not written to the reason: some of them hold
            // credentials. The audit interceptor records the before/after itself, in a table that
            // only the audit trail can read.
            unitOfWork.AuditReason = existing.IsSecuritySensitive
                ? $"Modification du paramètre sensible {normalised}"
                : $"Paramètre {normalised} : « {previous} » → « {value} »";
        }

        await unitOfWork.SaveChangesAsync(ct);

        if (existing?.IsSecuritySensitive == true || isSecuritySensitive)
        {
            await securityAuditor.LogAsync(SecurityEventKind.SecuritySettingChanged,
                                            succeeded: true, details: normalised, ct: ct);
        }

        return Result.Success();
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Keeps only the digits. ICE, IF and CNSS numbers are quoted with spaces and slashes on the
    /// documents they come from, and the validators expect bare digits.
    /// </summary>
    private static string? Digits(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : new string(value.Where(char.IsDigit).ToArray()) is { Length: > 0 } digits
                ? digits
                : null;
}

/// <summary>
/// What is still missing after a site save, as resource keys the screen can render.
/// <para>
/// Returned rather than thrown because an incomplete site is a normal intermediate state during
/// installation — but the administrator has to be able to see that invoicing and reporting are
/// still blocked, and exactly by what.
/// </para>
/// </summary>
public sealed record SiteSaveOutcome(
    Guid SiteId,
    IReadOnlyList<string> InvoicingBlockers,
    IReadOnlyList<string> OperationBlockers)
{
    public bool CanInvoice => InvoicingBlockers.Count == 0;
    public bool CanReleaseReports => OperationBlockers.Count == 0;
}

/// <summary>
/// Tracked-entity access for the configuration aggregates that have no repository of their own.
/// <para>
/// <see cref="NumberSequence"/> and <see cref="AppSetting"/> are plain entities rather than
/// aggregate roots, so <c>IRepository&lt;T&gt;</c> does not cover them; this keeps the application
/// layer off the DbContext without inventing two more repositories.
/// </para>
/// </summary>
public interface IConfigurationWriter
{
    Task<NumberSequence?> GetSequenceAsync(Guid sequenceId, CancellationToken ct = default);

    Task<AppSetting?> GetSettingAsync(string key, Guid? siteId, CancellationToken ct = default);

    Task AddSettingAsync(AppSetting setting, CancellationToken ct = default);
}
