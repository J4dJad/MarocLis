using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Quality;

/// <summary>
/// A document under control (procédure, mode opératoire, instruction, formulaire). ISO 15189 §4.3
/// requires the laboratory to know which version of each procedure is in force, who approved it,
/// and that superseded versions are withdrawn from use but retained. Versions are rows, never
/// edits, and exactly one version of a document can be effective at a time.
/// </summary>
public sealed class ControlledDocument : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<DocumentVersion> _versions = [];
    private readonly List<DocumentAcknowledgement> _acknowledgements = [];

    private ControlledDocument() { }

    public ControlledDocument(string reference, MultilingualText title, DocumentKind kind, Guid siteId)
    {
        Reference = reference;
        Title = title;
        Kind = kind;
        SiteId = siteId;
    }

    /// <summary>Document reference in the quality system, e.g. "PR-PRE-002".</summary>
    public string Reference { get; private set; } = string.Empty;

    public MultilingualText Title { get; set; } = new();
    public DocumentKind Kind { get; set; }
    public Guid SiteId { get; set; }

    public Guid? DepartmentId { get; set; }

    /// <summary>The process the document belongs to, for the quality manual's structure.</summary>
    public string? ProcessCode { get; set; }

    /// <summary>Owner responsible for keeping it current.</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>Months between mandatory reviews; ISO expects a defined periodicity.</summary>
    public int ReviewPeriodMonths { get; set; } = 24;

    /// <summary>Whether staff must acknowledge having read the effective version.</summary>
    public bool RequiresAcknowledgement { get; set; }

    public bool IsActive { get; set; } = true;

    public IReadOnlyCollection<DocumentVersion> Versions => _versions.AsReadOnly();
    public IReadOnlyCollection<DocumentAcknowledgement> Acknowledgements => _acknowledgements.AsReadOnly();

    public DocumentVersion? EffectiveVersion =>
        _versions.Where(v => v.Status == DocumentVersionStatus.Effective)
                 .OrderByDescending(v => v.VersionNumber).FirstOrDefault();

    public DocumentVersion? LatestVersion =>
        _versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

    public DateOnly? NextReviewDue =>
        EffectiveVersion?.EffectiveFrom.AddMonths(ReviewPeriodMonths);

    public bool IsReviewOverdue(DateOnly today) =>
        NextReviewDue is { } due && due < today;

    public DocumentVersion AddDraft(MultilingualText? changeSummary, Guid authorUserId)
    {
        var next = (_versions.Count == 0 ? 0 : _versions.Max(v => v.VersionNumber)) + 1;
        var version = new DocumentVersion(Id, next, authorUserId) { ChangeSummary = changeSummary };
        _versions.Add(version);
        return version;
    }

    /// <summary>
    /// Brings a version into force and withdraws whatever was effective before it. Enforced here
    /// so two versions of the same procedure can never both be in use.
    /// </summary>
    public Result MakeEffective(int versionNumber, DateOnly effectiveFrom, DateTimeOffset now,
                                Guid approvedByUserId)
    {
        var version = _versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
        if (version is null)
            return Result.Failure(Error.NotFound("Document_VersionNotFound"));
        if (version.Status != DocumentVersionStatus.Approved)
            return Result.Failure(Error.Regulatory("Document_MustBeApprovedBeforeEffective"));

        foreach (var previous in _versions.Where(v => v.Status == DocumentVersionStatus.Effective))
            previous.Withdraw(effectiveFrom.AddDays(-1), now);

        version.MakeEffective(effectiveFrom, now, approvedByUserId);
        return Result.Success();
    }

    public void RecordAcknowledgement(Guid userId, int versionNumber, DateTimeOffset at) =>
        _acknowledgements.Add(new DocumentAcknowledgement(Id, userId, versionNumber, at));

    /// <summary>Staff who have not yet acknowledged the version in force.</summary>
    public IReadOnlyList<Guid> PendingAcknowledgements(IEnumerable<Guid> expectedUserIds)
    {
        if (EffectiveVersion is not { } effective) return [];

        var done = _acknowledgements
            .Where(a => a.VersionNumber == effective.VersionNumber)
            .Select(a => a.UserId)
            .ToHashSet();

        return [.. expectedUserIds.Where(id => !done.Contains(id))];
    }
}

public enum DocumentKind
{
    /// <summary>Manuel qualité.</summary>
    QualityManual = 0,
    /// <summary>Procédure.</summary>
    Procedure = 1,
    /// <summary>Mode opératoire / instruction de travail.</summary>
    WorkInstruction = 2,
    /// <summary>Formulaire ou enregistrement.</summary>
    Form = 3,
    /// <summary>Fiche technique d'un réactif ou d'un automate.</summary>
    TechnicalSheet = 4,
    /// <summary>Fiche de sécurité (FDS).</summary>
    SafetyDataSheet = 5,
    /// <summary>Document externe: norme, texte réglementaire, notice fournisseur.</summary>
    ExternalDocument = 6,
    /// <summary>Habilitation ou fiche de poste.</summary>
    CompetenceRecord = 7
}

public sealed class DocumentVersion : Entity, IAuditable
{
    private DocumentVersion() { }

    public DocumentVersion(Guid controlledDocumentId, int versionNumber, Guid authorUserId)
    {
        ControlledDocumentId = controlledDocumentId;
        VersionNumber = versionNumber;
        AuthorUserId = authorUserId;
        Status = DocumentVersionStatus.Draft;
    }

    public Guid ControlledDocumentId { get; private set; }
    public int VersionNumber { get; private set; }
    public DocumentVersionStatus Status { get; private set; }

    public Guid AuthorUserId { get; private set; }

    /// <summary>What changed from the previous version — the reader needs this, not a diff.</summary>
    public MultilingualText? ChangeSummary { get; set; }

    /// <summary>The document file itself.</summary>
    public byte[]? Content { get; set; }
    public string? ContentType { get; set; }
    public string? FileName { get; set; }
    public string? ContentHash { get; set; }

    public Guid? ReviewedByUserId { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? WithdrawnOn { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public Result SubmitForReview()
    {
        if (Status != DocumentVersionStatus.Draft)
            return Result.Failure(Error.Conflict("Document_NotADraft"));
        if (Content is null || Content.Length == 0)
            return Result.Failure(Error.Validation("Document_ContentRequired"));

        Status = DocumentVersionStatus.UnderReview;
        return Result.Success();
    }

    public Result Review(DateTimeOffset at, Guid byUserId)
    {
        if (Status != DocumentVersionStatus.UnderReview)
            return Result.Failure(Error.Conflict("Document_NotUnderReview"));

        ReviewedAt = at;
        ReviewedByUserId = byUserId;
        Status = DocumentVersionStatus.Reviewed;
        return Result.Success();
    }

    /// <summary>
    /// Approval. The approver may not be the author: separating writing from approving is the
    /// point of document control, so the rule lives here rather than in the UI.
    /// </summary>
    public Result Approve(DateTimeOffset at, Guid byUserId)
    {
        if (Status is not (DocumentVersionStatus.Reviewed or DocumentVersionStatus.UnderReview))
            return Result.Failure(Error.Conflict("Document_NotReadyForApproval"));
        if (byUserId == AuthorUserId)
            return Result.Failure(Error.Regulatory("Document_ApproverMustDifferFromAuthor"));

        ApprovedAt = at;
        ApprovedByUserId = byUserId;
        Status = DocumentVersionStatus.Approved;
        return Result.Success();
    }

    internal void MakeEffective(DateOnly from, DateTimeOffset at, Guid approvedByUserId)
    {
        EffectiveFrom = from;
        ApprovedByUserId ??= approvedByUserId;
        ApprovedAt ??= at;
        Status = DocumentVersionStatus.Effective;
    }

    internal void Withdraw(DateOnly on, DateTimeOffset at)
    {
        WithdrawnOn = on;
        WithdrawnAt = at;
        Status = DocumentVersionStatus.Superseded;
    }
}

public enum DocumentVersionStatus
{
    Draft = 0,
    UnderReview = 1,
    Reviewed = 2,
    Approved = 3,
    /// <summary>In force — the only version staff may work from.</summary>
    Effective = 4,
    /// <summary>Replaced by a later version; retained but withdrawn from use.</summary>
    Superseded = 5,
    /// <summary>Withdrawn without a replacement.</summary>
    Obsolete = 6
}

public sealed class DocumentAcknowledgement : Entity
{
    private DocumentAcknowledgement() { }

    public DocumentAcknowledgement(Guid controlledDocumentId, Guid userId, int versionNumber,
                                   DateTimeOffset at)
    {
        ControlledDocumentId = controlledDocumentId;
        UserId = userId;
        VersionNumber = versionNumber;
        AcknowledgedAt = at;
    }

    public Guid ControlledDocumentId { get; private set; }
    public Guid UserId { get; private set; }
    public int VersionNumber { get; private set; }
    public DateTimeOffset AcknowledgedAt { get; private set; }
}

/// <summary>
/// Participation in an external quality assessment scheme (évaluation externe de la qualité).
/// ISO 15189 §5.6.3 makes inter-laboratory comparison mandatory where a scheme exists, and the
/// laboratory has to act on unsatisfactory performance — so a poor Z-score raises a non-conformity
/// rather than merely being filed.
/// </summary>
public sealed class EqaParticipation : AuditableAggregateRoot, ISiteScoped
{
    private EqaParticipation() { }

    public EqaParticipation(string schemeName, string roundReference, Guid testParameterId,
                            Guid siteId, DateOnly roundDate)
    {
        SchemeName = schemeName;
        RoundReference = roundReference;
        TestParameterId = testParameterId;
        SiteId = siteId;
        RoundDate = roundDate;
    }

    /// <summary>Organiser of the scheme.</summary>
    public string SchemeName { get; private set; } = string.Empty;

    /// <summary>Round or survey identifier.</summary>
    public string RoundReference { get; private set; } = string.Empty;

    public Guid TestParameterId { get; private set; }
    public Guid SiteId { get; set; }
    public Guid? InstrumentId { get; set; }

    public DateOnly RoundDate { get; private set; }
    public DateOnly? SubmissionDeadline { get; set; }
    public DateOnly? SubmittedOn { get; set; }

    /// <summary>The value the laboratory reported.</summary>
    public decimal? ReportedValue { get; set; }

    /// <summary>Assigned or consensus value published by the organiser.</summary>
    public decimal? AssignedValue { get; set; }

    /// <summary>Peer-group mean and SD, when published.</summary>
    public decimal? PeerGroupMean { get; set; }
    public decimal? PeerGroupSd { get; set; }

    /// <summary>Standardised deviation index — the headline performance figure.</summary>
    public decimal? ZScore { get; set; }

    /// <summary>Deviation from the assigned value in percent.</summary>
    public decimal? BiasPercent { get; set; }

    public EqaOutcome Outcome { get; set; } = EqaOutcome.Pending;

    /// <summary>Non-conformity opened when performance was unsatisfactory.</summary>
    public Guid? NonConformityId { get; set; }

    public MultilingualText? InvestigationNotes { get; set; }

    public DateOnly? ReviewedOn { get; set; }
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>
    /// Classifies performance from the Z-score using the conventional bands: |Z| ≤ 2 satisfactory,
    /// 2 &lt; |Z| ≤ 3 questionable, |Z| &gt; 3 unsatisfactory.
    /// </summary>
    public EqaOutcome ClassifyFromZScore()
    {
        if (ZScore is not { } z) return EqaOutcome.Pending;
        var magnitude = Math.Abs(z);
        return magnitude switch
        {
            <= 2m => EqaOutcome.Satisfactory,
            <= 3m => EqaOutcome.Questionable,
            _ => EqaOutcome.Unsatisfactory
        };
    }

    /// <summary>Unsatisfactory performance obliges the laboratory to investigate and act.</summary>
    public bool RequiresNonConformity => Outcome == EqaOutcome.Unsatisfactory;
}

public enum EqaOutcome
{
    Pending = 0,
    Satisfactory = 1,
    Questionable = 2,
    Unsatisfactory = 3,
    /// <summary>Not returned in time; counts as a missed round.</summary>
    NotSubmitted = 4,
    /// <summary>The organiser excluded the result from the evaluation.</summary>
    Excluded = 5
}
