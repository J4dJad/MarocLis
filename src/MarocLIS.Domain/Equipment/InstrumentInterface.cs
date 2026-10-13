using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Equipment;

/// <summary>
/// How one analyser talks to the LIS. Kept apart from <see cref="Instrument"/> because most
/// equipment in an inventory — fridges, centrifuges, pipettes — has no interface at all, and
/// because an analyser can be replaced without losing its maintenance history.
/// </summary>
public sealed class InstrumentInterface : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<InstrumentTestMapping> _mappings = [];

    private InstrumentInterface() { }

    public InstrumentInterface(Guid instrumentId, Guid siteId, InstrumentProtocol protocol,
                               InstrumentTransportKind transport)
    {
        InstrumentId = instrumentId;
        SiteId = siteId;
        Protocol = protocol;
        Transport = transport;
    }

    public Guid InstrumentId { get; private set; }
    public Guid SiteId { get; set; }

    public InstrumentProtocol Protocol { get; set; }
    public InstrumentTransportKind Transport { get; set; }

    /// <summary>
    /// Unidirectional means the analyser only pushes results; bidirectional means it also asks the
    /// LIS which tests to run on a scanned tube. Bidirectional removes manual worklist entry, which
    /// is where mis-assignment errors come from, so it is preferred where the analyser supports it.
    /// </summary>
    public InstrumentDirection Direction { get; set; } = InstrumentDirection.Unidirectional;

    public bool IsEnabled { get; set; }

    // ---- Serial settings ----------------------------------------------------------------------

    /// <summary>e.g. "COM3".</summary>
    public string? SerialPortName { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;

    /// <summary>"None", "Odd", "Even", "Mark", "Space".</summary>
    public string Parity { get; set; } = "None";

    /// <summary>"One", "Two", "OnePointFive".</summary>
    public string StopBits { get; set; } = "One";

    /// <summary>"None", "RequestToSend", "XOnXOff".</summary>
    public string Handshake { get; set; } = "None";

    // ---- Network settings ---------------------------------------------------------------------

    public string? Host { get; set; }
    public int? TcpPort { get; set; }

    /// <summary>True when the LIS listens and the analyser connects to it.</summary>
    public bool ListenAsServer { get; set; }

    // ---- File-drop settings -------------------------------------------------------------------

    /// <summary>Folder watched for result files, for analysers that only export to disk.</summary>
    public string? InboundDirectory { get; set; }
    public string? OutboundDirectory { get; set; }
    public string? FilePattern { get; set; } = "*.txt";

    /// <summary>Folder consumed files are moved to, so a message is never processed twice.</summary>
    public string? ProcessedDirectory { get; set; }

    // ---- Behaviour ----------------------------------------------------------------------------

    /// <summary>Character encoding on the wire. Latin-1 for most analysers; a few use UTF-8.</summary>
    public string Encoding { get; set; } = "ISO-8859-1";

    /// <summary>Seconds to wait for an acknowledgement before giving up on a frame.</summary>
    public int AcknowledgementTimeoutSeconds { get; set; } = 15;

    /// <summary>Times a frame is resent after a NAK.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Whether results land directly as entered values or wait in a holding area for a technician
    /// to accept. Auto-acceptance is safe only where the analyser's own checks are trusted and the
    /// test codes are fully mapped, so it is off by default.
    /// </summary>
    public bool AutoAcceptResults { get; set; }

    /// <summary>
    /// Whether an unmapped test code is silently ignored or raises a non-conformity. Ignoring it
    /// quietly is how a laboratory discovers months later that a parameter was never being stored.
    /// </summary>
    public bool RaiseIssueOnUnmappedCode { get; set; } = true;

    /// <summary>Whether control results are routed to the QC module automatically.</summary>
    public bool RouteControlResultsToQc { get; set; } = true;

    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public string? LastError { get; set; }

    public IReadOnlyCollection<InstrumentTestMapping> Mappings => _mappings.AsReadOnly();

    public InstrumentTestMapping AddMapping(InstrumentTestMapping mapping)
    {
        mapping.AttachTo(Id);
        _mappings.Add(mapping);
        return mapping;
    }

    public Result Validate()
    {
        var errors = new List<Error>();

        switch (Transport)
        {
            case InstrumentTransportKind.Serial when string.IsNullOrWhiteSpace(SerialPortName):
                errors.Add(Error.Validation("Interface_SerialPortRequired"));
                break;

            case InstrumentTransportKind.Tcp when !ListenAsServer && string.IsNullOrWhiteSpace(Host):
                errors.Add(Error.Validation("Interface_HostRequired"));
                break;

            case InstrumentTransportKind.Tcp when TcpPort is null or < 1 or > 65535:
                errors.Add(Error.Validation("Interface_PortInvalid"));
                break;

            case InstrumentTransportKind.FileDrop when string.IsNullOrWhiteSpace(InboundDirectory):
                errors.Add(Error.Validation("Interface_InboundDirectoryRequired"));
                break;
        }

        if (Direction == InstrumentDirection.Bidirectional && _mappings.Count == 0)
            errors.Add(Error.Validation("Interface_MappingsRequiredForBidirectional"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public enum InstrumentProtocol
{
    /// <summary>ASTM E1381 link layer with E1394 records — the most common analyser protocol.</summary>
    AstmE1394 = 0,

    /// <summary>HL7 v2.x over MLLP.</summary>
    Hl7V2 = 1,

    /// <summary>Vendor-specific delimited text, handled by a plug-in driver.</summary>
    ProprietaryDelimited = 2,

    /// <summary>Fixed-width text file export.</summary>
    FixedWidthFile = 3,

    /// <summary>CSV export.</summary>
    Csv = 4,

    /// <summary>POCT1-A, for point-of-care devices.</summary>
    Poct1A = 5
}

public enum InstrumentTransportKind
{
    Serial = 0,
    Tcp = 1,
    /// <summary>The analyser writes files to a folder the LIS watches.</summary>
    FileDrop = 2,
    /// <summary>Vendor SDK or middleware, reached through a plug-in.</summary>
    VendorSdk = 3
}

public enum InstrumentDirection
{
    /// <summary>Results only, from the analyser to the LIS.</summary>
    Unidirectional = 0,

    /// <summary>The analyser queries the LIS for a scanned tube's worklist, then returns results.</summary>
    Bidirectional = 1,

    /// <summary>The LIS pushes the worklist ahead of the tube.</summary>
    HostQueryDownload = 2
}

/// <summary>
/// Translation between an analyser's own test code and a catalogue parameter.
/// <para>
/// This table is the interface's whole safety story. An unmapped or mis-mapped code puts a
/// potassium value in a sodium field, so mappings are explicit rows with an audit trail rather
/// than a configuration file, and the interface can be set to refuse anything it does not
/// recognise.
/// </para>
/// </summary>
public sealed class InstrumentTestMapping : Entity, IAuditable
{
    private InstrumentTestMapping() { }

    public InstrumentTestMapping(string instrumentTestCode, Guid testParameterId)
    {
        InstrumentTestCode = instrumentTestCode.Trim();
        TestParameterId = testParameterId;
    }

    public Guid InstrumentInterfaceId { get; private set; }
    internal void AttachTo(Guid interfaceId) => InstrumentInterfaceId = interfaceId;

    /// <summary>Code exactly as the analyser transmits it — case-sensitive on some instruments.</summary>
    public string InstrumentTestCode { get; private set; } = string.Empty;

    public Guid TestParameterId { get; private set; }

    /// <summary>Unit the analyser reports in, when it differs from the catalogue's.</summary>
    public string? InstrumentUnit { get; set; }

    /// <summary>
    /// Factor applied to the transmitted value to reach the catalogue's unit. Null means no
    /// conversion. Wrong here is a hundredfold error on a report, so it is deliberately explicit
    /// rather than inferred from unit strings.
    /// </summary>
    public decimal? ConversionFactor { get; set; }

    /// <summary>Offset applied after the factor, for the rare scale that is not purely multiplicative.</summary>
    public decimal? ConversionOffset { get; set; }

    /// <summary>
    /// Translation of the analyser's own coded answers to the parameter's coded options, as
    /// <c>instrument=catalogue</c> pairs separated by semicolons, e.g. <c>POS=POSITIF;NEG=NEGATIF</c>.
    /// </summary>
    public string? CodedValueMap { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    /// <summary>Applies the configured conversion to a transmitted numeric value.</summary>
    public decimal Convert(decimal instrumentValue) =>
        instrumentValue * (ConversionFactor ?? 1m) + (ConversionOffset ?? 0m);

    /// <summary>Translates an analyser coded answer, returning the input when no mapping applies.</summary>
    public string TranslateCode(string instrumentValue)
    {
        if (string.IsNullOrWhiteSpace(CodedValueMap)) return instrumentValue;

        foreach (var pair in CodedValueMap.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 &&
                string.Equals(parts[0].Trim(), instrumentValue.Trim(), StringComparison.OrdinalIgnoreCase))
                return parts[1].Trim();
        }

        return instrumentValue;
    }
}

/// <summary>
/// Every message exchanged with an analyser, stored verbatim.
/// <para>
/// Append-only and deliberately generous: when a clinician disputes a result, the raw frame is the
/// only evidence of what the analyser actually transmitted, and being able to replay it is what
/// turns a lost transmission from a recollection into a re-import.
/// </para>
/// </summary>
public sealed class InstrumentMessageLog : Entity, ISiteScoped
{
    private InstrumentMessageLog() { }

    public InstrumentMessageLog(Guid instrumentInterfaceId, Guid siteId, MessageDirection direction,
                                string rawContent, DateTimeOffset occurredAt)
    {
        InstrumentInterfaceId = instrumentInterfaceId;
        SiteId = siteId;
        Direction = direction;
        RawContent = rawContent;
        OccurredAt = occurredAt;
    }

    public Guid InstrumentInterfaceId { get; private set; }
    public Guid SiteId { get; set; }
    public MessageDirection Direction { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>The frame or message exactly as it went over the wire.</summary>
    public string RawContent { get; private set; } = string.Empty;

    public InstrumentMessageProcessingStatus Status { get; set; } = InstrumentMessageProcessingStatus.Received;

    /// <summary>Specimen identifiers extracted, for searching the log by barcode.</summary>
    public string? SpecimenIds { get; set; }

    public int? ResultCount { get; set; }

    /// <summary>Results actually stored, which is lower than <see cref="ResultCount"/> when codes were unmapped.</summary>
    public int? ImportedCount { get; set; }

    /// <summary>Problems found, as resource keys with details, one per line.</summary>
    public string? Issues { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Set when an operator re-imported the message after fixing a mapping.</summary>
    public DateTimeOffset? ReplayedAt { get; set; }
    public Guid? ReplayedByUserId { get; set; }
}

public enum MessageDirection
{
    /// <summary>From the analyser to the LIS.</summary>
    Inbound = 0,

    /// <summary>From the LIS to the analyser.</summary>
    Outbound = 1
}

public enum InstrumentMessageProcessingStatus
{
    Received = 0,
    /// <summary>Parsed and stored in full.</summary>
    Imported = 1,
    /// <summary>Stored, but some results could not be matched.</summary>
    PartiallyImported = 2,
    /// <summary>Could not be parsed or matched at all.</summary>
    Failed = 3,
    /// <summary>Recognised but of no interest, e.g. an acknowledgement.</summary>
    Ignored = 4,
    /// <summary>Held for a technician to accept, because auto-acceptance is off.</summary>
    AwaitingAcceptance = 5
}
