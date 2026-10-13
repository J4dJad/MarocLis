using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Reports;

/// <summary>
/// A released report (compte rendu d'analyses). Reports are versioned and immutable: once released,
/// a report is never edited. A correction produces a new version that states what changed and why,
/// because the previous version is already in the hands of a clinician who may have acted on it.
/// </summary>
public sealed class LabReport : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<LabReportDelivery> _deliveries = [];

    private LabReport() { }

    public LabReport(Guid labOrderId, Guid siteId, int versionNumber, ReportCompleteness completeness,
                     AppLanguage language, DateTimeOffset releasedAt, Guid releasedByBiologistUserId)
    {
        LabOrderId = labOrderId;
        SiteId = siteId;
        VersionNumber = versionNumber;
        Completeness = completeness;
        Language = language;
        ReleasedAt = releasedAt;
        ReleasedByBiologistUserId = releasedByBiologistUserId;
    }

    public Guid LabOrderId { get; private set; }
    public Guid SiteId { get; set; }

    /// <summary>
    /// Version of the report for this order, starting at 1. Printed on the document so a clinician
    /// holding two copies can tell which one supersedes the other.
    /// </summary>
    public int VersionNumber { get; private set; }

    /// <summary>Whether every requested analysis is included, or only those validated so far.</summary>
    public ReportCompleteness Completeness { get; private set; }

    public AppLanguage Language { get; private set; }

    public DateTimeOffset ReleasedAt { get; private set; }

    /// <summary>
    /// The biologist who released it. Their name, qualification and order registration number are
    /// printed in the signature block, as loi n° 12-01 requires the report to identify the
    /// biologist who validated it.
    /// </summary>
    public Guid ReleasedByBiologistUserId { get; private set; }

    /// <summary>Signature block as rendered, frozen so a reprint is byte-identical.</summary>
    public string? SignatoryName { get; set; }
    public string? SignatoryQualification { get; set; }
    public string? SignatoryOrderNumber { get; set; }

    // ---- The rendered document ---------------------------------------------------------------

    /// <summary>
    /// The PDF exactly as delivered. Stored rather than regenerated: a report regenerated two years
    /// later from current catalogue data would not be the document the clinician received.
    /// </summary>
    public byte[]? RenderedPdf { get; set; }

    public int? PageCount { get; set; }

    /// <summary>
    /// SHA-256 of <see cref="RenderedPdf"/>. Lets the laboratory prove that a PDF presented later
    /// is the one it issued, and detects silent corruption of the archive.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>Short opaque token used in the download link sent by SMS or email.</summary>
    public string? AccessToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    // ---- Amendment ---------------------------------------------------------------------------

    /// <summary>The version this one supersedes, when it is a correction.</summary>
    public Guid? SupersedesReportId { get; set; }

    /// <summary>Set on this row when a later version has replaced it.</summary>
    public Guid? SupersededByReportId { get; private set; }

    public ReportAmendmentKind AmendmentKind { get; set; } = ReportAmendmentKind.None;

    /// <summary>
    /// Why the report was reissued, printed prominently on the amended document. Never optional for
    /// an amendment: a corrected report that does not say what changed is worse than no report.
    /// </summary>
    public MultilingualText? AmendmentReason { get; set; }

    /// <summary>Parameters whose values changed, so the amended report can highlight them.</summary>
    public string? AmendedParameterCodesCsv { get; set; }

    // ---- Content flags printed on the document ------------------------------------------------

    /// <summary>Whether any included analysis was carried out by a partner laboratory.</summary>
    public bool ContainsSubcontractedResults { get; set; }

    /// <summary>Whether any result was produced on a sample accepted with a documented defect.</summary>
    public bool ContainsReservations { get; set; }

    /// <summary>Whether any included result is a panic value.</summary>
    public bool ContainsCriticalResults { get; set; }

    /// <summary>Whether every included analysis is inside the ISO 15189 accreditation scope.</summary>
    public bool AllResultsAccredited { get; set; }

    /// <summary>The biologist's global conclusion, printed above the signature.</summary>
    public MultilingualText? Conclusion { get; set; }

    public IReadOnlyCollection<LabReportDelivery> Deliveries => _deliveries.AsReadOnly();

    /// <summary>
    /// Order items covered by this version, persisted as a comma-separated list of identifiers.
    /// <para>
    /// Stored on the report rather than as a relationship on purpose: this is part of the frozen
    /// document. A reprint two years from now must show exactly the analyses the released version
    /// showed, which a live join to the order would not guarantee once the order has moved on.
    /// </para>
    /// </summary>
    public string? IncludedOrderItemIdsCsv { get; private set; }

    /// <summary>Order items covered by this version — a partial report covers only some.</summary>
    public IReadOnlyList<Guid> IncludedOrderItemIds =>
        string.IsNullOrWhiteSpace(IncludedOrderItemIdsCsv)
            ? []
            : [.. IncludedOrderItemIdsCsv
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
                    .Where(id => id != Guid.Empty)];

    public bool IsSuperseded => SupersededByReportId is not null;

    public bool IsAmendment => AmendmentKind != ReportAmendmentKind.None;

    // ---- Behaviour ---------------------------------------------------------------------------

    public void Include(IEnumerable<Guid> orderItemIds)
    {
        var current = IncludedOrderItemIds.ToList();

        foreach (var id in orderItemIds)
            if (id != Guid.Empty && !current.Contains(id)) current.Add(id);

        IncludedOrderItemIdsCsv = current.Count == 0
            ? null
            : string.Join(',', current.Select(id => id.ToString("D")));
    }

    public void MarkSupersededBy(Guid newerReportId) => SupersededByReportId = newerReportId;

    public LabReportDelivery RecordDelivery(LabReportDelivery delivery)
    {
        _deliveries.Add(delivery);
        return delivery;
    }

    public bool WasDelivered => _deliveries.Any(d => d.Status == DeliveryStatus.Delivered);

    public DateTimeOffset? FirstDeliveredAt =>
        _deliveries.Where(d => d.Status == DeliveryStatus.Delivered)
                   .Select(d => d.CompletedAt).Where(d => d is not null)
                   .DefaultIfEmpty(null).Min();

    /// <summary>
    /// Checks the report against what the regulation requires it to carry before it can be
    /// released. Called by the release use case; a failure here stops the release rather than
    /// producing a defective document.
    /// </summary>
    public Result ValidateForRelease()
    {
        var errors = new List<Error>();

        if (IncludedOrderItemIds.Count == 0)
            errors.Add(Error.Validation("Report_NoResultsIncluded"));
        if (string.IsNullOrWhiteSpace(SignatoryName))
            errors.Add(Error.Regulatory("Report_SignatoryRequired"));
        if (string.IsNullOrWhiteSpace(SignatoryOrderNumber))
            errors.Add(Error.Regulatory("Report_SignatoryOrderNumberRequired"));
        if (IsAmendment && (AmendmentReason is null || string.IsNullOrWhiteSpace(AmendmentReason.Fr)))
            errors.Add(Error.Regulatory("Report_AmendmentReasonRequired"));
        if (IsAmendment && SupersedesReportId is null)
            errors.Add(Error.Validation("Report_AmendmentMustSupersede"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>The label printed in the document header, in the report's own language.</summary>
    public string HeaderLabel() => (Completeness, IsAmendment) switch
    {
        (_, true) => Language switch
        {
            AppLanguage.Arabic => "تقرير مُصحَّح",
            AppLanguage.English => "AMENDED REPORT",
            _ => "COMPTE RENDU RECTIFIÉ"
        },
        (ReportCompleteness.Partial, false) => Language switch
        {
            AppLanguage.Arabic => "تقرير جزئي",
            AppLanguage.English => "PARTIAL REPORT",
            _ => "COMPTE RENDU PARTIEL"
        },
        _ => Language switch
        {
            AppLanguage.Arabic => "تقرير نهائي",
            AppLanguage.English => "FINAL REPORT",
            _ => "COMPTE RENDU DÉFINITIF"
        }
    };
}

public enum ReportCompleteness
{
    /// <summary>Only the analyses validated so far — clearly marked as provisional.</summary>
    Partial = 0,

    /// <summary>Every analysis of the order is validated and included.</summary>
    Final = 1
}

public enum ReportAmendmentKind
{
    None = 0,

    /// <summary>A result value was corrected.</summary>
    ResultCorrection = 1,

    /// <summary>Patient identity or demographics were corrected, which may change interpretation.</summary>
    IdentityCorrection = 2,

    /// <summary>An analysis was added to an already-reported order.</summary>
    Addition = 3,

    /// <summary>The interpretive comment or conclusion was revised.</summary>
    CommentRevision = 4,

    /// <summary>The report is withdrawn in full, e.g. after a proven sample mix-up.</summary>
    Retraction = 5
}

/// <summary>
/// One attempt to deliver a report version to one recipient. Kept per attempt so the laboratory can
/// show when and how a result reached a prescriber, and so a failed send is visible rather than lost.
/// </summary>
public sealed class LabReportDelivery : Entity, IAuditable
{
    private LabReportDelivery() { }

    public LabReportDelivery(Guid labReportId, DeliveryChannel channel, ReportRecipientKind recipientKind,
                             DateTimeOffset requestedAt)
    {
        LabReportId = labReportId;
        Channel = channel;
        RecipientKind = recipientKind;
        RequestedAt = requestedAt;
        Status = DeliveryStatus.Pending;
    }

    public Guid LabReportId { get; private set; }
    public DeliveryChannel Channel { get; private set; }
    public ReportRecipientKind RecipientKind { get; private set; }

    /// <summary>Who it went to — a name for a counter hand-over, an address for an electronic send.</summary>
    public string? RecipientIdentifier { get; set; }
    public string? RecipientName { get; set; }

    /// <summary>Set when the recipient is the registered prescriber or a laboratory user.</summary>
    public Guid? PrescriberId { get; set; }

    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public DeliveryStatus Status { get; private set; }

    /// <summary>Attempt counter for channels that retry.</summary>
    public int AttemptCount { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>
    /// Who physically handed the report over, and to whom. A report collected at the counter must
    /// only be released to the patient or a person they authorised — recording the bearer's
    /// identity is what makes that checkable.
    /// </summary>
    public Guid? HandedOverByUserId { get; set; }
    public string? BearerName { get; set; }
    public string? BearerCin { get; set; }

    /// <summary>
    /// Set when the recipient was given the report under a third-party disclosure consent rather
    /// than being the patient themselves.
    /// </summary>
    public Guid? AuthorizingConsentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public void MarkSent(DateTimeOffset at)
    {
        AttemptCount++;
        Status = DeliveryStatus.Sent;
        CompletedAt = at;
    }

    public void MarkDelivered(DateTimeOffset at)
    {
        Status = DeliveryStatus.Delivered;
        CompletedAt = at;
    }

    public void MarkFailed(DateTimeOffset at, string reason)
    {
        AttemptCount++;
        Status = DeliveryStatus.Failed;
        CompletedAt = at;
        FailureReason = reason;
    }
}

public enum DeliveryChannel
{
    /// <summary>Printed and handed over at the counter.</summary>
    PrintedAtCounter = 0,
    Email = 1,
    SmsLink = 2,
    Fax = 3,
    Courier = 4,
    /// <summary>Pushed to a prescriber's or hospital's system.</summary>
    ElectronicInterface = 5,
    /// <summary>Downloaded by the patient or prescriber from the portal.</summary>
    PortalDownload = 6
}

public enum ReportRecipientKind
{
    Patient = 0,
    Prescriber = 1,
    /// <summary>A person the patient explicitly authorised.</summary>
    AuthorizedThirdParty = 2,
    /// <summary>The institution that sent the order.</summary>
    Institution = 3,
    /// <summary>The payer, as a supporting document for a claim.</summary>
    Payer = 4,
    /// <summary>The laboratory's own archive copy.</summary>
    InternalArchive = 5
}

public enum DeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Delivered = 2,
    Failed = 3,
    Cancelled = 4
}
