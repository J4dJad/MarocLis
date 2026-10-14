using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;

namespace MarocLIS.Application.Abstractions;

/// <summary>
/// Re-imports a frame that is already in the log, once the reason it did not import has been dealt
/// with — a mapping added, or a technician accepting a held transmission.
/// <para>
/// Declared here and implemented in the instruments layer because re-importing starts by re-parsing
/// the stored frame, and the parser belongs to the protocol. The application layer knows that a
/// frame can be replayed; it does not know what ASTM looks like.
/// </para>
/// <para>
/// The frame is not re-received, it is re-read. That is what makes a transmission the laboratory
/// nearly lost a re-import rather than a second venepuncture.
/// </para>
/// </summary>
public interface IInstrumentResultReplayer
{
    /// <summary>
    /// Replays one logged frame. <paramref name="byUserId"/> is the operator who asked for it, and
    /// is stamped on the log entry; the values themselves stay attributed to the instrument service
    /// account, because the analyser is what measured them.
    /// </summary>
    Task<Result<int>> ReplayAsync(Guid logId, Guid byUserId, CancellationToken ct = default);
}

/// <summary>
/// Reads a pasted or logged frame the way a link would, without storing anything.
/// <para>
/// What the "Tester une trame" window shows: for each result the code the parser extracted, the
/// parameter it maps to, and what the import would do with it. The format is passed in rather than
/// read from the link, so a setting can be tried before it is saved.
/// </para>
/// </summary>
public interface IInstrumentMessagePreviewer
{
    Task<Result<MessagePreview>> PreviewAsync(Guid interfaceId, InstrumentProtocol protocol,
                                              InstrumentMessageFormat format,
                                              UnmappedNonNumericHandling unmappedNonNumeric,
                                              string raw, CancellationToken ct = default);
}

/// <summary>What a frame would become. Issues are "Key\tdetail", like the message log's.</summary>
public sealed record MessagePreview(IReadOnlyList<string> Issues, IReadOnlyList<PreviewLine> Lines)
{
    public int WouldStore => Lines.Count(l => l.Outcome is PreviewOutcome.WouldStore
                                                       or PreviewOutcome.WouldStoreControl
                                                       or PreviewOutcome.WouldMarkNotDone);
}

/// <summary>One result of the frame, with its specimen repeated so the grid reads flat.</summary>
public sealed record PreviewLine(
    string? SpecimenId, bool IsControl, string? AccessionNumber, string? PatientName,
    string? RawTestId, string? Code, Guid? TestParameterId, string? ParameterCode,
    string? Value, string? StoredValue, string? Unit, string? Flags, string? ResultType,
    PreviewOutcome Outcome, string? Detail);

public enum PreviewOutcome
{
    WouldStore,
    WouldStoreControl,
    WouldMarkNotDone,
    ExcludedType,
    IgnoredNonNumeric,
    UnmappedCode,
    NotOrdered,
    AlreadyValidated,
    NotNumeric,
    EmptyValue,
    SpecimenIdMissing,
    SpecimenUnknown,
    SpecimenAmbiguous,
    SpecimenNotUsable,
    OrderNotUsable,
    NoResults
}

/// <summary>
/// Append-only store for every frame exchanged with an analyser.
/// <para>
/// A narrow abstraction rather than a repository because <see cref="InstrumentMessageLog"/> is not
/// an aggregate root: nothing navigates to it, nothing edits it, and the only operations are "write
/// one" and "read some back". It is separate from the result import on purpose — the frame must be
/// recorded whether or not it could be parsed, because when a clinician disputes a value the raw
/// frame is the only evidence of what the analyser actually transmitted.
/// </para>
/// </summary>
public interface IInstrumentMessageLogStore
{
    /// <summary>
    /// Records a frame and returns its id. Committed on its own, not with the results: a frame that
    /// could not be imported must still be on record, and sharing a transaction with the import
    /// would roll the evidence back along with the failure.
    /// </summary>
    Task<Guid> RecordAsync(InstrumentMessageLog entry, CancellationToken ct = default);

    /// <summary>Updates the outcome of a frame already recorded.</summary>
    Task MarkProcessedAsync(Guid logId, InstrumentMessageProcessingStatus status,
                             int? resultCount, int? importedCount, string? issues,
                             DateTimeOffset processedAt, CancellationToken ct = default);

    Task<InstrumentMessageLog?> GetByIdAsync(Guid logId, CancellationToken ct = default);

    /// <summary>
    /// The recent traffic on one interface, or on every interface when none is named. Newest first,
    /// because the question asked of this log is almost always "what just happened?".
    /// </summary>
    Task<IReadOnlyList<InstrumentMessageLog>> GetRecentAsync(Guid? interfaceId, int take,
                                                              CancellationToken ct = default);

    /// <summary>Stamps a replay, so a re-import is distinguishable from the original transmission.</summary>
    Task MarkReplayedAsync(Guid logId, Guid byUserId, DateTimeOffset at,
                            CancellationToken ct = default);

    /// <summary>
    /// Counts what a purge at <paramref name="cutoff"/> would remove and what it would keep, without
    /// removing anything. Asked before the operator is shown a confirmation: a purge that reports
    /// its scope afterwards is a purge nobody can consent to.
    /// </summary>
    Task<MessageLogCensus> CensusAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    /// Deletes inbound and outbound frames older than <paramref name="cutoff"/> whose processing is
    /// finished, and returns how many went. Frames still replayable are never touched — see
    /// <see cref="MessageLogCensus.RetainedReplayable"/>.
    /// </summary>
    Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>
/// What a frame-log purge would do. Three numbers rather than one, because the interesting figure is
/// usually the second: frames old enough to purge that are being kept because somebody could still
/// act on them.
/// </summary>
/// <param name="Purgeable">Frames past the cutoff whose processing is finished.</param>
/// <param name="RetainedReplayable">
/// Frames past the cutoff held back because they are still replayable — failed, partially imported,
/// or awaiting acceptance. These are unfinished work, not history: deleting one would throw away the
/// only route by which its results can still reach a patient file.
/// </param>
/// <param name="OldestPurgeable">
/// When the oldest purgeable frame arrived, so the screen can say what period is about to go.
/// </param>
/// <param name="TotalRows">Everything in the log, for the growth figure the retention cap exists for.</param>
public sealed record MessageLogCensus(
    int Purgeable,
    int RetainedReplayable,
    DateTimeOffset? OldestPurgeable,
    int TotalRows);

/// <summary>
/// How the instrument host runs. Settings a laboratory decides once and an installer writes.
/// </summary>
public sealed class InstrumentHostOptions
{
    public const string SectionName = "Instruments";

    /// <summary>
    /// Whether the host starts at all.
    /// <para>
    /// Off by default, and only one workstation on the LAN should set it: two processes opening the
    /// same serial port is a conflict, and two opening the same TCP listener is a race in which
    /// results land in whichever won. An interface pointed at a port nobody configured would also
    /// log a reconnection failure every two seconds for as long as the application is open.
    /// </para>
    /// </summary>
    public bool EnableInstrumentHost { get; set; }

    /// <summary>
    /// Starts the built-in ASTM analyser simulator, for training and for commissioning an interface
    /// before the analyser is on site. Never on a production workstation: it transmits invented
    /// results down a real link, and they would be stored as real ones.
    /// </summary>
    public bool EnableSimulator { get; set; }

    public int SimulatorPort { get; set; } = 5150;

    /// <summary>
    /// User name of the non-interactive account results arriving from an analyser are attributed to.
    /// <para>
    /// A machine write needs an accountable identity for the same reason a manual one does, and the
    /// honest answer is not a person. Attributing an analyser's value to whichever operator happened
    /// to be signed in would put a name against a measurement they never saw — so the host resolves
    /// this account and refuses to start without it, rather than silently borrowing a human's.
    /// </para>
    /// </summary>
    public string ServiceAccountUserName { get; set; } = "interface";

    /// <summary>
    /// How often the host re-reads the interface list, so enabling an interface takes effect without
    /// restarting the application.
    /// </summary>
    public int ConfigurationPollSeconds { get; set; } = 30;

    /// <summary>Frames kept in the log. Beyond this the oldest are purged by the maintenance job.</summary>
    public int MessageLogRetentionDays { get; set; } = 180;
}
