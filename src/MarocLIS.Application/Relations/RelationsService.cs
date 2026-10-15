using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Relations;

namespace MarocLIS.Application.Relations;

public sealed record DelegateVisitRow(
    Guid Id,
    DateOnly VisitDate,
    Guid PrescriberId,
    string PrescriberName,
    string? Institution,
    Guid DelegateUserId,
    string DelegateName,
    VisitPurpose Purpose,
    string? Notes,
    DateOnly? NextVisitOn,
    bool NextVisitDue);

public sealed record SaveDelegateVisitRequest
{
    public Guid? Id { get; init; }
    public required DateOnly VisitDate { get; init; }
    public required Guid PrescriberId { get; init; }
    public required Guid DelegateUserId { get; init; }
    public VisitPurpose Purpose { get; init; }
    public string? Notes { get; init; }
    public DateOnly? NextVisitOn { get; init; }
}

public sealed record PartnershipRow(
    Guid Id,
    string PartnerName,
    PartnerKind PartnerKind,
    PartnershipKind Kind,
    string? Reference,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool IsActive,
    bool InForce,
    bool EndingSoon,
    decimal? DiscountPercent,
    string? ContactName,
    string? ContactPhone,
    string? FileName);

public sealed record PartnershipDetail(
    Guid Id,
    string PartnerName,
    PartnerKind PartnerKind,
    Guid? PrescriberId,
    string? PartnerIce,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    PartnershipKind Kind,
    string? Reference,
    DateOnly StartDate,
    DateOnly? EndDate,
    string? Terms,
    decimal? DiscountPercent,
    int? PaymentTermsDays,
    string? Notes,
    bool IsActive);

public sealed record SavePartnershipRequest
{
    public Guid? Id { get; init; }
    public required string PartnerName { get; init; }
    public PartnerKind PartnerKind { get; init; }
    public Guid? PrescriberId { get; init; }
    public string? PartnerIce { get; init; }
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public string? ContactEmail { get; init; }
    public PartnershipKind Kind { get; init; }
    public string? Reference { get; init; }
    public required DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? Terms { get; init; }
    public decimal? DiscountPercent { get; init; }
    public int? PaymentTermsDays { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>Something to pick in a relations editor: a prescriber or a staff member.</summary>
public sealed record RelationLookup(Guid Id, string Display);

public sealed record RelationOptions(IReadOnlyList<RelationLookup> Prescribers, IReadOnlyList<RelationLookup> Staff);

/// <summary>
/// The delegate's visits to prescribers and the conventions with clinics, companies and partner
/// laboratories, with the signed convention attached. Follow-up of existing relations: the screens
/// and the record are worded that way, and nothing here sends anything to anyone.
/// </summary>
public sealed class RelationsService(
    IRepository<DelegateVisit> visits,
    IRepository<Partnership> partnerships,
    IRepository<PartnershipDocument> documents,
    IRelationsQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    // ---- Visits --------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<DelegateVisitRow>>> GetVisitsAsync(DateOnly from, DateOnly to,
                                                                             Guid? prescriberId = null,
                                                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<DelegateVisitRow>>(permitted.FirstError);
        if (to < from) return Result.Failure<IReadOnlyList<DelegateVisitRow>>(Error.Validation("Stats_PeriodReversed"));

        return Result.Success(await queries.GetVisitsAsync(currentUser.SiteId, from, to, prescriberId, clock.Today, ct));
    }

    /// <summary>Visits whose next date has come and that no later visit to the same prescriber followed.</summary>
    public async Task<Result<IReadOnlyList<DelegateVisitRow>>> GetDueVisitsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<DelegateVisitRow>>(permitted.FirstError);

        return Result.Success(await queries.GetDueVisitsAsync(currentUser.SiteId, clock.Today, ct));
    }

    public async Task<Result<RelationOptions>> GetOptionsAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Relations.Manage, Permissions.Relations.View);
        if (permitted.IsFailure) return Result.Failure<RelationOptions>(permitted.FirstError);

        return Result.Success(await queries.GetOptionsAsync(currentUser.SiteId, ct));
    }

    public async Task<Result<Guid>> SaveVisitAsync(SaveDelegateVisitRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        DelegateVisit visit;
        if (request.Id is { } id)
        {
            var existing = await visits.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Visit_NotFound"));
            visit = existing;
        }
        else
        {
            visit = new DelegateVisit(currentUser.SiteId, request.VisitDate, request.PrescriberId,
                                      request.DelegateUserId, request.Purpose);
        }

        visit.VisitDate = request.VisitDate;
        visit.PrescriberId = request.PrescriberId;
        visit.DelegateUserId = request.DelegateUserId;
        visit.Purpose = request.Purpose;
        visit.Notes = Blank(request.Notes);
        visit.NextVisitOn = request.NextVisitOn;

        var valid = visit.Validate(clock.Today);
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (request.Id is null) await visits.AddAsync(visit, ct);
        else visits.Update(visit);

        unitOfWork.AuditReason = request.Id is null
            ? $"Visite du délégué du {visit.VisitDate:dd/MM/yyyy} enregistrée"
            : $"Visite du délégué du {visit.VisitDate:dd/MM/yyyy} modifiée";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(visit.Id);
    }

    public async Task<Result> DeleteVisitAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.Manage);
        if (permitted.IsFailure) return permitted;
        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure(Error.Validation("Relations_ReasonRequired"));

        var visit = await visits.GetByIdAsync(id, ct);
        if (visit is null) return Result.Failure(Error.NotFound("Visit_NotFound"));

        visits.SoftDelete(visit, currentUser.UserId, reason.Trim());
        unitOfWork.AuditReason = $"Visite du {visit.VisitDate:dd/MM/yyyy} supprimée : {reason.Trim()}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Conventions ---------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<PartnershipRow>>> GetPartnershipsAsync(bool includeInactive,
                                                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PartnershipRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPartnershipsAsync(currentUser.SiteId, includeInactive, clock.Today, ct));
    }

    public async Task<Result<PartnershipDetail>> GetPartnershipAsync(Guid id, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.View);
        if (permitted.IsFailure) return Result.Failure<PartnershipDetail>(permitted.FirstError);

        var p = await partnerships.GetByIdAsync(id, ct);
        if (p is null) return Result.Failure<PartnershipDetail>(Error.NotFound("Partnership_NotFound"));

        return Result.Success(new PartnershipDetail(p.Id, p.PartnerName, p.PartnerKind, p.PrescriberId, p.PartnerIce,
                                                    p.ContactName, p.ContactPhone, p.ContactEmail, p.Kind, p.Reference,
                                                    p.StartDate, p.EndDate, p.Terms, p.DiscountPercent,
                                                    p.PaymentTermsDays, p.Notes, p.IsActive));
    }

    public async Task<Result<Guid>> SavePartnershipAsync(SavePartnershipRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var name = request.PartnerName?.Trim() ?? string.Empty;

        Partnership partnership;
        if (request.Id is { } id)
        {
            var existing = await partnerships.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Partnership_NotFound"));
            partnership = existing;
        }
        else
        {
            partnership = new Partnership(currentUser.SiteId, name, request.PartnerKind, request.Kind, request.StartDate);
        }

        partnership.PartnerName = name;
        partnership.PartnerKind = request.PartnerKind;
        partnership.PrescriberId = request.PrescriberId;
        partnership.PartnerIce = Blank(request.PartnerIce);
        partnership.ContactName = Blank(request.ContactName);
        partnership.ContactPhone = Blank(request.ContactPhone);
        partnership.ContactEmail = Blank(request.ContactEmail);
        partnership.Kind = request.Kind;
        partnership.Reference = Blank(request.Reference);
        partnership.StartDate = request.StartDate;
        partnership.EndDate = request.EndDate;
        partnership.Terms = Blank(request.Terms);
        partnership.DiscountPercent = request.DiscountPercent;
        partnership.PaymentTermsDays = request.PaymentTermsDays;
        partnership.Notes = Blank(request.Notes);
        partnership.IsActive = request.IsActive;

        var valid = partnership.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (request.Id is null) await partnerships.AddAsync(partnership, ct);
        else partnerships.Update(partnership);

        unitOfWork.AuditReason = request.Id is null
            ? $"Convention avec {partnership.PartnerName} enregistrée"
            : $"Convention avec {partnership.PartnerName} modifiée";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(partnership.Id);
    }

    /// <summary>
    /// Attaches the signed convention. Only the document kinds that cannot run anything are taken
    /// (<see cref="SafeDocumentFile"/>): opening it hands the file to the Windows shell.
    /// </summary>
    public async Task<Result> AttachDocumentAsync(Guid partnershipId, string fileName, byte[] content,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.Manage);
        if (permitted.IsFailure) return permitted;

        if (content.Length == 0 || string.IsNullOrWhiteSpace(fileName))
            return Result.Failure(Error.Validation("Purchasing_FileEmpty"));
        if (content.Length > PartnershipDocument.MaximumBytes)
            return Result.Failure(Error.Validation("Purchasing_FileTooLarge", PartnershipDocument.MaximumBytes / (1024 * 1024)));

        var name = Path.GetFileName(fileName.Trim());
        if (SafeDocumentFile.ContentTypeFor(name) is not { } contentType)
            return Result.Failure(Error.Validation("Purchasing_FileType"));

        var partnership = await partnerships.GetByIdAsync(partnershipId, ct);
        if (partnership is null) return Result.Failure(Error.NotFound("Partnership_NotFound"));

        await documents.AddAsync(new PartnershipDocument(partnership.Id, name, contentType, content, clock.Now,
                                                         currentUser.UserId), ct);

        unitOfWork.AuditReason = $"Convention avec {partnership.PartnerName} : fichier {name} joint";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<(string FileName, byte[] Content)>> GetDocumentAsync(Guid partnershipId,
                                                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Relations.View);
        if (permitted.IsFailure) return Result.Failure<(string, byte[])>(permitted.FirstError);

        var file = await queries.GetLatestDocumentAsync(partnershipId, ct);
        if (file is not { } found) return Result.Failure<(string, byte[])>(Error.NotFound("Purchasing_FileNotFound"));

        // Re-checked on the way out: a kind accepted by an older version stays closed.
        return SafeDocumentFile.ContentTypeFor(found.FileName) is null
            ? Result.Failure<(string, byte[])>(Error.Validation("Purchasing_FileType"))
            : Result.Success(found);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IRelationsQueries
{
    Task<IReadOnlyList<DelegateVisitRow>> GetVisitsAsync(Guid siteId, DateOnly from, DateOnly to, Guid? prescriberId,
                                                         DateOnly today, CancellationToken ct = default);

    Task<IReadOnlyList<DelegateVisitRow>> GetDueVisitsAsync(Guid siteId, DateOnly today, CancellationToken ct = default);

    /// <summary>Active prescribers, and the active staff of the site who can be named delegate.</summary>
    Task<RelationOptions> GetOptionsAsync(Guid siteId, CancellationToken ct = default);

    Task<IReadOnlyList<PartnershipRow>> GetPartnershipsAsync(Guid siteId, bool includeInactive, DateOnly today,
                                                             CancellationToken ct = default);

    Task<(string FileName, byte[] Content)?> GetLatestDocumentAsync(Guid partnershipId, CancellationToken ct = default);
}
