using System.Security.Cryptography;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Application.Quality;

/// <summary>One controlled document in the register, with the state of its newest version.</summary>
public sealed record ControlledDocumentRow(
    Guid Id,
    string Reference,
    MultilingualText Title,
    DocumentKind Kind,
    string? ProcessCode,
    string? OwnerName,
    int ReviewPeriodMonths,
    bool RequiresAcknowledgement,
    bool IsActive,
    int? EffectiveVersion,
    DateOnly? EffectiveFrom,
    DateOnly? NextReviewDue,
    bool IsReviewOverdue,
    int? LatestVersion,
    DocumentVersionStatus? LatestStatus,
    bool AcknowledgedByCurrentUser);

/// <summary>One version of a controlled document, oldest first.</summary>
public sealed record DocumentVersionRow(
    Guid Id,
    int VersionNumber,
    DocumentVersionStatus Status,
    MultilingualText? ChangeSummary,
    string? FileName,
    string AuthorName,
    DateTimeOffset CreatedAt,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? ApprovedByName,
    DateTimeOffset? ApprovedAt,
    DateOnly? EffectiveFrom,
    DateOnly? WithdrawnOn,
    bool HasContent);

/// <summary>A version's stored file, for opening on the workstation.</summary>
public sealed record DocumentContent(byte[] Content, string FileName, string? ContentType);

public sealed record CreateControlledDocumentRequest
{
    public required string Reference { get; init; }
    public required MultilingualText Title { get; init; }
    public required DocumentKind Kind { get; init; }
    public string? ProcessCode { get; init; }
    public int ReviewPeriodMonths { get; init; } = 24;
    public bool RequiresAcknowledgement { get; init; }
}

/// <summary>
/// Document control (maîtrise documentaire).
/// <para>
/// ISO 15189 §8.3 asks three things of a laboratory's procedures: that the version in force is
/// known, that it was reviewed and approved by someone other than its author before use, and that
/// superseded versions are withdrawn but kept. The domain carries those rules; this service puts a
/// permission in front of each step and records who did it. Drafting and approving are separate
/// grants on purpose — the person who may write a procedure is not, by that fact alone, the person
/// who may bring it into force.
/// </para>
/// </summary>
public sealed class DocumentControlService(
    IRepository<ControlledDocument> documents,
    IDocumentControlQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Largest file accepted. A procedure is a few hundred kilobytes; this is generous.</summary>
    public const int MaximumContentBytes = 20 * 1024 * 1024;

    public async Task<Result<IReadOnlyList<ControlledDocumentRow>>> GetRegisterAsync(
        bool includeInactive = false, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<ControlledDocumentRow>>(permitted.FirstError);

        return Result.Success(await queries.GetDocumentsAsync(
            currentUser.SiteId, currentUser.UserId, includeInactive, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<DocumentVersionRow>>> GetVersionsAsync(
        Guid documentId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure)
            return Result.Failure<IReadOnlyList<DocumentVersionRow>>(permitted.FirstError);

        return Result.Success(await queries.GetVersionsAsync(documentId, ct));
    }

    public async Task<Result<Guid>> CreateAsync(CreateControlledDocumentRequest request,
                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageDocuments);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var reference = request.Reference.Trim().ToUpperInvariant();

        if (reference.Length == 0)
            return Result.Failure<Guid>(Error.Validation("Document_ReferenceRequired"));
        if (string.IsNullOrWhiteSpace(request.Title.Fr))
            return Result.Failure<Guid>(Error.Validation("Document_TitleRequired"));
        if (request.ReviewPeriodMonths is < 1 or > 120)
            return Result.Failure<Guid>(Error.Validation("Document_ReviewPeriodOutOfRange"));

        // The reference is unique across the laboratory, not per site: it is how a procedure is
        // cited in every other document, and two "PR-PRE-002" would make those citations ambiguous.
        if (await queries.ReferenceExistsAsync(reference, ct))
            return Result.Failure<Guid>(Error.Conflict("Document_ReferenceAlreadyTaken", reference));

        var document = new ControlledDocument(reference, request.Title, request.Kind,
                                              currentUser.SiteId)
        {
            ProcessCode = string.IsNullOrWhiteSpace(request.ProcessCode)
                ? null : request.ProcessCode.Trim(),
            ReviewPeriodMonths = request.ReviewPeriodMonths,
            RequiresAcknowledgement = request.RequiresAcknowledgement,
            OwnerUserId = currentUser.UserId
        };

        await documents.AddAsync(document, ct);

        unitOfWork.AuditReason = $"Création du document {reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(document.Id);
    }

    /// <summary>
    /// Starts a new version with its file. The file is attached at creation because a draft
    /// without content cannot be submitted, and an empty draft lying in the register is noise.
    /// The content type comes from the extension, and only the kinds of <see cref="SafeDocumentFile"/>
    /// are accepted: every reviewer and reader opens the version through the Windows shell.
    /// </summary>
    public async Task<Result<int>> AddDraftAsync(Guid documentId, MultilingualText? changeSummary,
                                                 byte[] content, string fileName,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ManageDocuments);
        if (permitted.IsFailure) return Result.Failure<int>(permitted.FirstError);

        if (SafeDocumentFile.ContentTypeFor(fileName) is not { } contentType)
            return Result.Failure<int>(Error.Validation("Document_FileType"));
        if (content.Length == 0)
            return Result.Failure<int>(Error.Validation("Document_ContentRequired"));
        if (content.Length > MaximumContentBytes)
            return Result.Failure<int>(Error.Validation("Document_ContentTooLarge",
                                                        MaximumContentBytes / (1024 * 1024)));

        var document = await documents.GetByIdWithDetailsAsync(documentId, ct);
        if (document is null) return Result.Failure<int>(Error.NotFound("Document_NotFound"));

        // One open draft at a time. Two parallel drafts of the same procedure means two people
        // rewriting it, and whichever is approved second silently discards the other's changes.
        if (document.Versions.Any(v => v.Status is DocumentVersionStatus.Draft
                                           or DocumentVersionStatus.UnderReview
                                           or DocumentVersionStatus.Reviewed
                                           or DocumentVersionStatus.Approved))
            return Result.Failure<int>(Error.Conflict("Document_VersionAlreadyInProgress"));

        var version = document.AddDraft(changeSummary, currentUser.UserId);
        version.Content = content;
        version.FileName = Path.GetFileName(fileName);
        version.ContentType = contentType;
        version.ContentHash = Convert.ToHexString(SHA256.HashData(content));

        unitOfWork.AuditReason = $"Nouvelle version {version.VersionNumber} de {document.Reference}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(version.VersionNumber);
    }

    public Task<Result> SubmitForReviewAsync(Guid documentId, int versionNumber,
                                             CancellationToken ct = default) =>
        ApplyAsync(documentId, versionNumber, Permissions.Quality.ManageDocuments,
                   v => v.SubmitForReview(), "Soumission à la revue", ct);

    /// <summary>Records the review. Like approval, it is refused to the author.</summary>
    public Task<Result> ReviewAsync(Guid documentId, int versionNumber,
                                    CancellationToken ct = default) =>
        ApplyAsync(documentId, versionNumber, Permissions.Quality.ManageDocuments,
                   v => v.AuthorUserId == currentUser.UserId
                       ? Result.Failure(Error.Regulatory("Document_ReviewerMustDifferFromAuthor"))
                       : v.Review(clock.Now, currentUser.UserId),
                   "Revue", ct);

    public Task<Result> ApproveAsync(Guid documentId, int versionNumber,
                                     CancellationToken ct = default) =>
        ApplyAsync(documentId, versionNumber, Permissions.Quality.ApproveDocument,
                   v => v.Approve(clock.Now, currentUser.UserId), "Approbation", ct);

    /// <summary>
    /// Brings an approved version into force and withdraws the one it replaces. The effective date
    /// may be in the future — a procedure is often approved ahead of the day staff are trained on
    /// it — but not in the past, which would back-date the version actually used for past work.
    /// </summary>
    public async Task<Result> MakeEffectiveAsync(Guid documentId, int versionNumber,
                                                 DateOnly effectiveFrom,
                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ApproveDocument);
        if (permitted.IsFailure) return permitted;

        if (effectiveFrom < clock.Today)
            return Result.Failure(Error.Validation("Document_EffectiveDateInPast"));

        var document = await documents.GetByIdWithDetailsAsync(documentId, ct);
        if (document is null) return Result.Failure(Error.NotFound("Document_NotFound"));

        var version = document.Versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
        if (version is null) return Result.Failure(Error.NotFound("Document_VersionNotFound"));
        if (version.Status != DocumentVersionStatus.Approved)
            return Result.Failure(Error.Regulatory("Document_MustBeApprovedBeforeEffective"));

        // The database allows a single effective version per document, and the replacement has to
        // reach the server after the withdrawal of its predecessor. Saving the two changes in one
        // batch leaves their order to the provider, so the withdrawal is written first on its own,
        // inside the same transaction so that a failure leaves the old version in force.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        if (document.EffectiveVersion is not null)
        {
            document.WithdrawEffectiveVersion(effectiveFrom.AddDays(-1), clock.Now);
            unitOfWork.AuditReason = $"Retrait de la version en vigueur de {document.Reference}";
            await unitOfWork.SaveChangesAsync(ct);
        }

        var made = document.MakeEffective(versionNumber, effectiveFrom, clock.Now,
                                          currentUser.UserId);
        if (made.IsFailure) return made;

        unitOfWork.AuditReason =
            $"Mise en application de la version {versionNumber} de {document.Reference}";
        await unitOfWork.SaveChangesAsync(ct);
        await unitOfWork.CommitAsync(ct);

        return Result.Success();
    }

    /// <summary>Records that the signed-in user has read the version in force.</summary>
    public async Task<Result> AcknowledgeAsync(Guid documentId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure) return permitted;

        var document = await documents.GetByIdWithDetailsAsync(documentId, ct);
        if (document is null) return Result.Failure(Error.NotFound("Document_NotFound"));

        if (document.EffectiveVersion is not { } effective)
            return Result.Failure(Error.Conflict("Document_NoEffectiveVersion"));

        if (document.Acknowledgements.Any(a => a.UserId == currentUser.UserId
                                               && a.VersionNumber == effective.VersionNumber))
            return Result.Success();

        document.RecordAcknowledgement(currentUser.UserId, effective.VersionNumber, clock.Now);

        unitOfWork.AuditReason =
            $"Prise de connaissance de {document.Reference} v{effective.VersionNumber}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    /// <summary>
    /// Withdraws a document from the register without a replacement. Its versions stay readable:
    /// a withdrawn procedure is still the one that governed the work done under it.
    /// </summary>
    public async Task<Result> SetActiveAsync(Guid documentId, bool active, string reason,
                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.ApproveDocument);
        if (permitted.IsFailure) return permitted;

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Document_ReasonRequired"));

        var document = await documents.GetByIdAsync(documentId, ct);
        if (document is null) return Result.Failure(Error.NotFound("Document_NotFound"));

        document.IsActive = active;

        unitOfWork.AuditReason = $"{document.Reference} {(active ? "réactivé" : "retiré")} : {reason}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result<DocumentContent>> GetContentAsync(Guid documentId, int versionNumber,
                                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Quality.View);
        if (permitted.IsFailure) return Result.Failure<DocumentContent>(permitted.FirstError);

        var content = await queries.GetContentAsync(documentId, versionNumber, ct);
        if (content is null) return Result.Failure<DocumentContent>(Error.NotFound("Document_VersionNotFound"));

        // A version stored before the allow-list may be a script or a macro document: it stays on
        // record but is never handed out to be opened.
        return SafeDocumentFile.ContentTypeFor(content.FileName) is null
            ? Result.Failure<DocumentContent>(Error.Validation("Document_FileType"))
            : Result.Success(content);
    }

    private async Task<Result> ApplyAsync(Guid documentId, int versionNumber, string permission,
                                          Func<DocumentVersion, Result> step, string label,
                                          CancellationToken ct)
    {
        var permitted = guard.Require(permission);
        if (permitted.IsFailure) return permitted;

        var document = await documents.GetByIdWithDetailsAsync(documentId, ct);
        if (document is null) return Result.Failure(Error.NotFound("Document_NotFound"));

        var version = document.Versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
        if (version is null) return Result.Failure(Error.NotFound("Document_VersionNotFound"));

        var done = step(version);
        if (done.IsFailure) return done;

        unitOfWork.AuditReason = $"{label} de {document.Reference} v{versionNumber}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }
}

/// <summary>Read-side queries for the document-control screen.</summary>
public interface IDocumentControlQueries
{
    Task<IReadOnlyList<ControlledDocumentRow>> GetDocumentsAsync(
        Guid siteId, Guid currentUserId, bool includeInactive, DateOnly today,
        CancellationToken ct = default);

    Task<IReadOnlyList<DocumentVersionRow>> GetVersionsAsync(Guid documentId,
                                                             CancellationToken ct = default);

    Task<DocumentContent?> GetContentAsync(Guid documentId, int versionNumber,
                                           CancellationToken ct = default);

    Task<bool> ReferenceExistsAsync(string reference, CancellationToken ct = default);
}
