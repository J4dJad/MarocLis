using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Samples;

/// <summary>
/// A physical tube or container of biological material. Every movement it makes is recorded as a
/// <see cref="SampleEvent"/>: ISO 15189 §5.4 requires the laboratory to be able to reconstruct the
/// pre-analytical history of any sample, including who drew it, when, at what temperature it
/// travelled, and on what documented grounds it was accepted or rejected.
/// </summary>
public sealed class Sample : AuditableAggregateRoot, ISoftDeletable, ISiteScoped
{
    private readonly List<SampleEvent> _events = [];
    private readonly List<SampleAliquot> _aliquots = [];

    private Sample() { }

    public Sample(string barcode, Guid labOrderId, Guid specimenTypeId, Guid siteId)
    {
        Barcode = barcode;
        LabOrderId = labOrderId;
        SpecimenTypeId = specimenTypeId;
        SiteId = siteId;
        Status = SampleStatus.AwaitingCollection;
    }

    /// <summary>
    /// Value encoded on the label. Unique for the life of the installation — a reused barcode is
    /// the single most dangerous defect a LIS can have, so uniqueness is enforced in the database.
    /// </summary>
    public string Barcode { get; private set; } = string.Empty;

    public Guid LabOrderId { get; private set; }
    public Guid SiteId { get; set; }

    public Guid SpecimenTypeId { get; private set; }
    public Guid? ContainerTypeId { get; set; }

    /// <summary>Ordinal of the tube within the order, printed on the label as "2/4".</summary>
    public int SequenceInOrder { get; set; } = 1;

    public SampleStatus Status { get; private set; }

    // ---- Collection ---------------------------------------------------------------------------

    public DateTimeOffset? CollectedAt { get; private set; }

    /// <summary>The person who drew the sample — traceable, and accountable for the labelling.</summary>
    public Guid? CollectedByUserId { get; private set; }

    /// <summary>Where it was drawn: at the laboratory, at home, in a ward.</summary>
    public CollectionLocation CollectionLocation { get; set; } = CollectionLocation.Laboratory;

    /// <summary>Anatomical site or side, where it matters (bras droit, cathéter).</summary>
    public string? CollectionSite { get; set; }

    /// <summary>Volume actually obtained, checked against the container minimum.</summary>
    public decimal? VolumeMl { get; set; }

    /// <summary>Number of collection attempts — a quality indicator for the phlebotomy team.</summary>
    public int CollectionAttempts { get; set; } = 1;

    /// <summary>Difficulty noted at the draw, which may explain haemolysis downstream.</summary>
    public string? CollectionNote { get; set; }

    // ---- Transport and reception --------------------------------------------------------------

    public DateTimeOffset? DispatchedAt { get; private set; }
    public Guid? TransportRunId { get; set; }

    public DateTimeOffset? ReceivedAt { get; private set; }
    public Guid? ReceivedByUserId { get; private set; }

    /// <summary>Temperature on arrival, recorded when the cold chain applies.</summary>
    public decimal? ReceptionTemperatureCelsius { get; set; }

    /// <summary>Whether the cold chain was respected end to end.</summary>
    public bool? ColdChainRespected { get; set; }

    // ---- Conformity (conformité du prélèvement) -----------------------------------------------

    /// <summary>
    /// Set when the sample is usable despite a defect — a slightly haemolysed tube for a test that
    /// tolerates it. The defect is still recorded and printed on the report as a reservation.
    /// </summary>
    public bool IsAcceptedWithReservation { get; private set; }

    public SampleDefect Defects { get; private set; } = SampleDefect.None;

    public DateTimeOffset? RejectedAt { get; private set; }
    public Guid? RejectedByUserId { get; private set; }
    public SampleRejectionReason? RejectionReason { get; private set; }
    public string? RejectionComment { get; private set; }

    /// <summary>Set when a replacement was requested after a rejection.</summary>
    public bool RecollectionRequested { get; private set; }
    public Guid? ReplacementSampleId { get; private set; }

    // ---- Storage and disposal -----------------------------------------------------------------

    /// <summary>Where the tube physically is: rack, freezer, position.</summary>
    public string? StorageLocation { get; set; }
    public string? StoragePosition { get; set; }

    /// <summary>
    /// Date until which the sample is kept for possible re-analysis (sérothèque). Beyond it the
    /// tube is discarded, which is itself a logged event.
    /// </summary>
    public DateOnly? KeepUntil { get; set; }

    public DateTimeOffset? DiscardedAt { get; private set; }
    public Guid? DiscardedByUserId { get; private set; }

    public IReadOnlyCollection<SampleEvent> Events => _events.AsReadOnly();
    public IReadOnlyCollection<SampleAliquot> Aliquots => _aliquots.AsReadOnly();

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    // ---- Transitions --------------------------------------------------------------------------

    public Result Collect(DateTimeOffset at, Guid byUserId, decimal? volumeMl, decimal? minimumVolumeMl)
    {
        if (Status is not (SampleStatus.AwaitingCollection or SampleStatus.Collected))
            return Result.Failure(Error.Conflict("Sample_CannotCollectFromStatus", Status));

        CollectedAt = at;
        CollectedByUserId = byUserId;
        VolumeMl = volumeMl;
        Status = SampleStatus.Collected;

        if (volumeMl is { } volume && minimumVolumeMl is { } minimum && volume < minimum)
            Defects |= SampleDefect.InsufficientVolume;

        AddEvent(SampleEventKind.Collected, at, byUserId, null);
        return Result.Success();
    }

    public Result Dispatch(DateTimeOffset at, Guid byUserId, Guid? transportRunId)
    {
        if (Status != SampleStatus.Collected)
            return Result.Failure(Error.Conflict("Sample_MustBeCollectedBeforeDispatch"));

        DispatchedAt = at;
        TransportRunId = transportRunId;
        Status = SampleStatus.InTransit;
        AddEvent(SampleEventKind.Dispatched, at, byUserId, null);
        return Result.Success();
    }

    /// <summary>
    /// Accepts the sample onto the technical platform. Defects are recorded even when the sample is
    /// accepted, because the report has to disclose anything that could affect interpretation.
    /// </summary>
    public Result Receive(DateTimeOffset at, Guid byUserId, SampleDefect observedDefects,
                          bool acceptWithReservation, decimal? temperatureCelsius, string? note)
    {
        // Received twice would overwrite the first reception's time, operator and temperature.
        if (Status is SampleStatus.Rejected or SampleStatus.Discarded
                   or SampleStatus.Received or SampleStatus.InAnalysis or SampleStatus.Stored)
            return Result.Failure(Error.Conflict("Sample_CannotReceiveFromStatus", Status));
        if (CollectedAt is null)
            return Result.Failure(Error.Conflict("Sample_NotCollected"));

        // Checked before anything is recorded. Refused here, the tube is not received: stamping
        // the reception first left it with a reception time, an operator and defects while still
        // in transit — and saved that way, since the caller saves the batch.
        if (observedDefects != SampleDefect.None && !acceptWithReservation)
            return Result.Failure(Error.Conflict("Sample_DefectsRequireExplicitDecision", observedDefects));

        ReceivedAt = at;
        ReceivedByUserId = byUserId;
        ReceptionTemperatureCelsius = temperatureCelsius;
        Defects |= observedDefects;

        IsAcceptedWithReservation = observedDefects != SampleDefect.None;
        Status = SampleStatus.Received;
        AddEvent(SampleEventKind.Received, at, byUserId, note);
        return Result.Success();
    }

    /// <summary>
    /// Refuses the sample. A rejection must always name a reason from the controlled list: the
    /// rejection rate by reason is a mandatory quality indicator, and free text cannot be counted.
    /// </summary>
    public Result Reject(DateTimeOffset at, Guid byUserId, SampleRejectionReason reason,
                         string? comment, bool requestRecollection)
    {
        if (Status is SampleStatus.Discarded)
            return Result.Failure(Error.Conflict("Sample_CannotRejectFromStatus", Status));

        // Refusing the same tube twice would issue a second replacement for one draw.
        if (Status is SampleStatus.Rejected)
            return Result.Failure(Error.Conflict("Sample_AlreadyRejected", Barcode));

        RejectedAt = at;
        RejectedByUserId = byUserId;
        RejectionReason = reason;
        RejectionComment = comment;
        RecollectionRequested = requestRecollection;
        Status = SampleStatus.Rejected;
        AddEvent(SampleEventKind.Rejected, at, byUserId, $"{reason}: {comment}");
        return Result.Success();
    }

    public void LinkReplacement(Guid replacementSampleId) => ReplacementSampleId = replacementSampleId;

    public Result MoveToStorage(DateTimeOffset at, Guid byUserId, string location, string? position,
                                DateOnly? keepUntil)
    {
        if (Status is SampleStatus.Discarded)
            return Result.Failure(Error.Conflict("Sample_AlreadyDiscarded"));

        StorageLocation = location;
        StoragePosition = position;
        KeepUntil = keepUntil;
        if (Status == SampleStatus.Received) Status = SampleStatus.Stored;
        AddEvent(SampleEventKind.Stored, at, byUserId, $"{location} {position}".Trim());
        return Result.Success();
    }

    public Result Discard(DateTimeOffset at, Guid byUserId, string? note)
    {
        if (Status == SampleStatus.Discarded) return Result.Success();

        DiscardedAt = at;
        DiscardedByUserId = byUserId;
        Status = SampleStatus.Discarded;
        AddEvent(SampleEventKind.Discarded, at, byUserId, note);
        return Result.Success();
    }

    public SampleAliquot CreateAliquot(string barcode, decimal? volumeMl, DateTimeOffset at, Guid byUserId)
    {
        var aliquot = new SampleAliquot(Id, barcode) { VolumeMl = volumeMl, CreatedAtUtc = at };
        _aliquots.Add(aliquot);
        AddEvent(SampleEventKind.Aliquoted, at, byUserId, barcode);
        return aliquot;
    }

    public void AddEvent(SampleEventKind kind, DateTimeOffset at, Guid? byUserId, string? detail,
                         decimal? temperatureCelsius = null, string? workstation = null)
        => _events.Add(new SampleEvent(Id, kind, at, byUserId, detail)
        {
            TemperatureCelsius = temperatureCelsius,
            Workstation = workstation
        });

    // ---- Derived views ------------------------------------------------------------------------

    public bool IsUsable => Status is SampleStatus.Received or SampleStatus.Stored
                                   or SampleStatus.InAnalysis;

    public bool HasDefects => Defects != SampleDefect.None;

    /// <summary>Time from draw to reception — the pre-analytical delay quality indicator.</summary>
    public TimeSpan? TransportDelay =>
        CollectedAt is { } collected && ReceivedAt is { } received ? received - collected : null;

    public bool IsPastKeepUntil(DateOnly today) => KeepUntil is { } until && until < today;
}

public enum SampleStatus
{
    AwaitingCollection = 0,
    Collected = 1,
    InTransit = 2,
    Received = 3,
    InAnalysis = 4,
    Stored = 5,
    Rejected = 8,
    Discarded = 9
}

public enum CollectionLocation
{
    Laboratory = 0,
    CollectionCentre = 1,
    PatientHome = 2,
    HospitalWard = 3,
    Workplace = 4,
    /// <summary>Brought in by the patient (urine, stool) — collection conditions are declarative.</summary>
    BroughtByPatient = 5
}

/// <summary>
/// Observable defects of a sample. A flags enum because a single tube is frequently both
/// under-filled and haemolysed, and the quality report must count each defect.
/// </summary>
[Flags]
public enum SampleDefect
{
    None = 0,
    /// <summary>Hémolyse.</summary>
    Haemolysed = 1 << 0,
    /// <summary>Coagulé — a clot in an anticoagulated tube.</summary>
    Clotted = 1 << 1,
    /// <summary>Quantité insuffisante.</summary>
    InsufficientVolume = 1 << 2,
    /// <summary>Lactescent / lipaemic.</summary>
    Lipaemic = 1 << 3,
    /// <summary>Ictère — high bilirubin interfering with photometry.</summary>
    Icteric = 1 << 4,
    /// <summary>Wrong container or additive for the requested analysis.</summary>
    WrongContainer = 1 << 5,
    /// <summary>Label missing, illegible or contradicting the request.</summary>
    LabellingDefect = 1 << 6,
    /// <summary>Cold chain broken in transit.</summary>
    ColdChainBroken = 1 << 7,
    /// <summary>Transport delay exceeded the sample's stability.</summary>
    ExcessiveDelay = 1 << 8,
    /// <summary>Container leaked or broke.</summary>
    ContainerDamaged = 1 << 9,
    /// <summary>Visibly contaminated.</summary>
    Contaminated = 1 << 10,
    /// <summary>Patient had not fasted as the analysis required.</summary>
    FastingNotRespected = 1 << 11
}

/// <summary>
/// Controlled list of rejection grounds. Counted monthly as a quality indicator, so the list is
/// closed; anything genuinely new is added to the enum and to the quality manual together.
/// </summary>
public enum SampleRejectionReason
{
    IdentificationMismatch = 0,
    MissingOrIllegibleLabel = 1,
    InsufficientVolume = 2,
    Haemolysed = 3,
    Clotted = 4,
    WrongContainer = 5,
    ExcessiveTransportDelay = 6,
    ColdChainBroken = 7,
    ContainerDamagedOrLeaked = 8,
    Contaminated = 9,
    MissingPrescription = 10,
    MissingConsent = 11,
    FastingNotRespected = 12,
    DuplicateRequest = 13,
    PatientRefused = 14,
    Other = 99
}

/// <summary>One recorded movement or observation in a sample's life.</summary>
public sealed class SampleEvent : Entity
{
    private SampleEvent() { }

    public SampleEvent(Guid sampleId, SampleEventKind kind, DateTimeOffset occurredAt,
                       Guid? byUserId, string? detail)
    {
        SampleId = sampleId;
        Kind = kind;
        OccurredAt = occurredAt;
        ByUserId = byUserId;
        Detail = detail;
    }

    public Guid SampleId { get; private set; }
    public SampleEventKind Kind { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? ByUserId { get; private set; }
    public string? Detail { get; private set; }

    /// <summary>Recorded on transport and storage events where the cold chain is documented.</summary>
    public decimal? TemperatureCelsius { get; set; }

    /// <summary>Machine name the event was recorded from — useful when tracing a mis-scan.</summary>
    public string? Workstation { get; set; }
}

public enum SampleEventKind
{
    LabelPrinted = 0,
    Collected = 1,
    Dispatched = 2,
    Received = 3,
    Rejected = 4,
    Centrifuged = 5,
    Aliquoted = 6,
    LoadedOnInstrument = 7,
    Stored = 8,
    RetrievedFromStorage = 9,
    SentToSubcontractor = 10,
    Discarded = 11,
    TemperatureRecorded = 12,
    /// <summary>Barcode reprinted — recorded because a duplicate label is a real safety risk.</summary>
    LabelReprinted = 13,

    /// <summary>The patient was called or sent a message to come back for a new draw.</summary>
    PatientRecalled = 14
}

/// <summary>A child tube split off a primary sample, with its own barcode.</summary>
public sealed class SampleAliquot : Entity
{
    private SampleAliquot() { }

    public SampleAliquot(Guid parentSampleId, string barcode)
    {
        ParentSampleId = parentSampleId;
        Barcode = barcode;
    }

    public Guid ParentSampleId { get; private set; }
    public string Barcode { get; private set; } = string.Empty;
    public decimal? VolumeMl { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? StorageLocation { get; set; }
    public string? Purpose { get; set; }
}
