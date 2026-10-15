using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Relations;

/// <summary>
/// A visit of the laboratory's delegate to a prescriber: when, who, why, what was said and when to
/// go back. The record is the follow-up of an existing working relationship — turnaround, report
/// delivery, a complaint, the sample pick-up round — and is worded as such: the code of ethics of
/// the profession does not let a laboratory canvass for prescriptions.
/// </summary>
public sealed class DelegateVisit : AuditableAggregateRoot, ISiteScoped, ISoftDeletable
{
    public const int NotesMaxLength = 2000;

    private DelegateVisit() { }

    public DelegateVisit(Guid siteId, DateOnly visitDate, Guid prescriberId, Guid delegateUserId,
                         VisitPurpose purpose)
    {
        SiteId = siteId;
        VisitDate = visitDate;
        PrescriberId = prescriberId;
        DelegateUserId = delegateUserId;
        Purpose = purpose;
    }

    public Guid SiteId { get; set; }
    public DateOnly VisitDate { get; set; }
    public Guid PrescriberId { get; set; }

    /// <summary>The staff member who made the visit.</summary>
    public Guid DelegateUserId { get; set; }

    public VisitPurpose Purpose { get; set; }
    public string? Notes { get; set; }

    /// <summary>Date agreed for the next visit, if one was.</summary>
    public DateOnly? NextVisitOn { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    /// <summary>The next visit is planned and its date has come.</summary>
    public bool IsNextVisitDue(DateOnly today) => NextVisitOn is { } next && next <= today;

    public Result Validate(DateOnly today)
    {
        var errors = new List<Error>();

        // A visit is recorded once it took place; a future one is the previous visit's next date.
        if (VisitDate > today) errors.Add(Error.Validation("Visit_DateInFuture"));
        if (NextVisitOn is { } next && next <= VisitDate) errors.Add(Error.Validation("Visit_NextBeforeVisit"));
        if (PrescriberId == Guid.Empty) errors.Add(Error.Validation("Visit_PrescriberRequired"));
        if (DelegateUserId == Guid.Empty) errors.Add(Error.Validation("Visit_DelegateRequired"));
        if (Notes is { Length: > NotesMaxLength }) errors.Add(Error.Validation("Relations_TextTooLong", NotesMaxLength));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum VisitPurpose
{
    /// <summary>Regular follow-up of the working relationship.</summary>
    FollowUp = 0,
    /// <summary>Quality of service: turnaround, report presentation, reachability.</summary>
    ServiceReview = 1,
    /// <summary>A complaint or a difficulty raised by the prescriber.</summary>
    Complaint = 2,
    /// <summary>How reports reach the prescriber: e-mail, courier, interface.</summary>
    ReportDelivery = 3,
    /// <summary>Sample pick-up round, home or practice collection.</summary>
    SampleLogistics = 4,
    Other = 9
}

/// <summary>
/// A convention or partnership with a clinic, a company (occupational health), an association or
/// another laboratory: who, what kind, from when to when, and the terms as agreed. The signed
/// document is kept in <see cref="PartnershipDocument"/>.
/// </summary>
public sealed class Partnership : AuditableAggregateRoot, ISiteScoped, ISoftDeletable
{
    public const int TermsMaxLength = 4000;

    /// <summary>A convention ending within this many days is flagged for renewal.</summary>
    public const int EndingSoonDays = 60;

    private Partnership() { }

    public Partnership(Guid siteId, string partnerName, PartnerKind partnerKind, PartnershipKind kind,
                       DateOnly startDate)
    {
        SiteId = siteId;
        PartnerName = partnerName;
        PartnerKind = partnerKind;
        Kind = kind;
        StartDate = startDate;
    }

    public Guid SiteId { get; set; }
    public string PartnerName { get; set; } = string.Empty;
    public PartnerKind PartnerKind { get; set; }

    /// <summary>The partner's record in the prescriber file, when it is a prescribing institution.</summary>
    public Guid? PrescriberId { get; set; }

    public string? PartnerIce { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }

    public PartnershipKind Kind { get; set; }
    public string? Reference { get; set; }
    public DateOnly StartDate { get; set; }

    /// <summary>Open-ended when empty (tacit renewal).</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>The terms as agreed: services, price list, payment terms, transport.</summary>
    public string? Terms { get; set; }

    /// <summary>Discount on the laboratory's price list agreed in the convention, in percent.</summary>
    public decimal? DiscountPercent { get; set; }

    /// <summary>Days the partner has to pay the laboratory's invoices.</summary>
    public int? PaymentTermsDays { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public bool IsInForce(DateOnly day) => IsActive && day >= StartDate && (EndDate is null || day <= EndDate);

    public bool IsEndingSoon(DateOnly today) =>
        IsInForce(today) && EndDate is { } end && end.DayNumber - today.DayNumber <= EndingSoonDays;

    public Result Validate()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(PartnerName)) errors.Add(Error.Validation("Partnership_PartnerRequired"));
        if (EndDate is { } end && end < StartDate) errors.Add(Error.Validation("Partnership_EndBeforeStart"));
        if (DiscountPercent is < 0m or > 100m) errors.Add(Error.Validation("Partnership_DiscountOutOfRange"));
        if (PaymentTermsDays is < 0 or > 365) errors.Add(Error.Validation("Partnership_PaymentTermsOutOfRange"));
        if (PartnerIce is not null && !Morocco.MoroccanIdentifiers.IsValidIce(PartnerIce))
            errors.Add(Error.Validation("Prescriber_IceInvalid"));
        if (Terms is { Length: > TermsMaxLength }) errors.Add(Error.Validation("Relations_TextTooLong", TermsMaxLength));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum PartnerKind
{
    Clinic = 0,
    Hospital = 1,
    /// <summary>A company, for its employees' occupational-health analyses.</summary>
    Company = 2,
    Laboratory = 3,
    Association = 4,
    Other = 9
}

public enum PartnershipKind
{
    /// <summary>The clinic sends its patients' analyses to the laboratory.</summary>
    CareConvention = 0,
    /// <summary>Médecine du travail: periodic examinations of a company's staff.</summary>
    OccupationalHealth = 1,
    /// <summary>Collection on the partner's premises, with pick-up by the laboratory.</summary>
    SampleCollection = 2,
    /// <summary>Analyses sent to, or received from, a partner laboratory.</summary>
    Subcontracting = 3,
    /// <summary>The partner pays for its members' analyses on invoice.</summary>
    DirectBilling = 4,
    Other = 9
}

/// <summary>
/// The signed convention, kept as its own row like the purchasing attachments. Only the kinds of
/// <see cref="SafeDocumentFile"/> are accepted; attaching again adds a version.
/// </summary>
public sealed class PartnershipDocument : AggregateRoot, IAuditable
{
    public const int MaximumBytes = 15 * 1024 * 1024;

    private PartnershipDocument() { }

    public PartnershipDocument(Guid partnershipId, string fileName, string contentType, byte[] content,
                               DateTimeOffset attachedAt, Guid attachedByUserId)
    {
        PartnershipId = partnershipId;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
        SizeBytes = content.Length;
        AttachedAt = attachedAt;
        AttachedByUserId = attachedByUserId;
    }

    public Guid PartnershipId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Content { get; private set; } = [];
    public int SizeBytes { get; private set; }
    public DateTimeOffset AttachedAt { get; private set; }
    public Guid AttachedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }
}
