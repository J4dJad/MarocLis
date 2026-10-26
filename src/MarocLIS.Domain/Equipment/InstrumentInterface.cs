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

    // ---- Message format -------------------------------------------------------------------------
    // Where this analyser puts things in its messages. Every vendor reads the same standard a little
    // differently, so these are per link, and all null means "Auto": the parser's own fallbacks.

    /// <summary>Component of the test identifier (ASTM R-3, HL7 OBX-3) holding the code, 1-based.</summary>
    public int? FormatTestCodeComponent { get; set; }

    /// <summary>Separator after which the code carries a suffix to drop, such as Roche's "/" dilution.</summary>
    public string? FormatTestCodeSuffix { get; set; }

    /// <summary>Where the specimen number is: ASTM order field "3" or "4"; HL7 "SPM-2", "OBR-3", "OBR-2" or "ORC-3".</summary>
    public string? FormatSpecimenIdField { get; set; }

    /// <summary>Component of that field holding the barcode, 1-based; null takes the field as a whole (ASTM) or its first component (HL7).</summary>
    public int? FormatSpecimenIdComponent { get; set; }

    /// <summary>
    /// Result types or statuses kept, comma-separated (ASTM: the type after the code, such as Abbott's
    /// F or I, else the result status; HL7: OBX-2 or OBX-11). Null keeps everything.
    /// </summary>
    public string? FormatAcceptedResultTypes { get; set; }

    /// <summary>What to do with a result whose code is not mapped and whose value is not a number.</summary>
    public UnmappedNonNumericHandling UnmappedNonNumeric { get; set; }

    /// <summary>Whether the analyser's own flags and comments are kept with the result for validation.</summary>
    public bool KeepInstrumentFlags { get; set; } = true;

    /// <summary>
    /// The vendor's reading of HL7, for an HL7 link: Mindray marks its QC and calibration results in
    /// the header and answers a worklist query with QCK^Q02 and DSR^Q03 rather than ORM^O01.
    /// Standard on any other protocol.
    /// </summary>
    public Hl7Dialect Hl7Dialect { get; set; }

    /// <summary>The format settings as one value, for the parsers.</summary>
    public InstrumentMessageFormat MessageFormat => new()
    {
        TestCodeComponent = FormatTestCodeComponent,
        TestCodeSuffix = FormatTestCodeSuffix,
        SpecimenIdField = FormatSpecimenIdField,
        SpecimenIdComponent = FormatSpecimenIdComponent,
        AcceptedResultTypes = FormatAcceptedResultTypes,
        Hl7Dialect = Protocol == InstrumentProtocol.Hl7V2 ? Hl7Dialect : Hl7Dialect.Standard
    };

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
    Poct1A = 5,

    /// <summary>
    /// Roche COBAS INTEGRA host interface (COBAS INTEGRA 400 plus): Roche's own block protocol over
    /// RS-232, in which the LIS polls the analyser.
    /// </summary>
    RocheIntegra = 6
}

/// <summary>
/// How an analyser reads HL7 v2 where vendors depart from the textbook. The Mindray values name the
/// family: they speak one dialect (HL7 v2.3.1 over MLLP, results ORU^R01 marked sample / calibration
/// / QC in MSH-16, worklist query QRY^Q02 answered by QCK^Q02 then DSR^Q03), and the family decides
/// what the link offers: the haematology BC series has no worklist query here.
/// </summary>
public enum Hl7Dialect
{
    /// <summary>The standard's own reading: ORU^R01 results, ORM^O01 orders in answer to a query.</summary>
    Standard = 0,

    /// <summary>Mindray BS chemistry (BS-200E, BS-240, BS-380, BS-480, BS-800, BS-2000…).</summary>
    MindrayBs = 1,

    /// <summary>Mindray BC haematology (BC-5000, BC-5150, BC-5380, BC-5390, BC-6000, BC-6800…).</summary>
    MindrayBc = 2,

    /// <summary>Mindray CL immunoassay in HL7 mode (CL-900i, CL-1000i, CL-2000i, CL-6000i…).</summary>
    MindrayCl = 3
}

/// <summary>An unmapped result that is not a number: an analyser alarm, a histogram file name.</summary>
public enum UnmappedNonNumericHandling
{
    /// <summary>Reported as an unknown code, like any other.</summary>
    Report = 0,

    /// <summary>Dropped without a word: haematology analysers send dozens per tube.</summary>
    Ignore = 1
}

/// <summary>
/// Where an analyser puts the test code, the specimen number and the result type in its messages.
/// All null is "Auto", which is the parsers' own reading of the standard.
/// </summary>
public sealed record InstrumentMessageFormat
{
    public static readonly InstrumentMessageFormat Auto = new();

    public int? TestCodeComponent { get; init; }
    public string? TestCodeSuffix { get; init; }
    public string? SpecimenIdField { get; init; }
    public int? SpecimenIdComponent { get; init; }
    public string? AcceptedResultTypes { get; init; }

    /// <summary>The vendor's reading of HL7; Standard for every other protocol.</summary>
    public Hl7Dialect Hl7Dialect { get; init; }

    /// <summary>Whether the link speaks Mindray's HL7 (BS, BC or CL).</summary>
    public bool IsMindray => Hl7Dialect is Hl7Dialect.MindrayBs or Hl7Dialect.MindrayBc or Hl7Dialect.MindrayCl;

    /// <summary>The accepted result types as a set, upper-cased; empty keeps everything.</summary>
    public IReadOnlySet<string> AcceptedTypes =>
        (AcceptedResultTypes ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToUpperInvariant())
            .ToHashSet();

    /// <summary>The code with the configured suffix removed.</summary>
    public string StripSuffix(string code)
    {
        if (string.IsNullOrEmpty(TestCodeSuffix)) return code;

        var at = code.IndexOf(TestCodeSuffix, StringComparison.Ordinal);
        return at > 0 ? code[..at] : code;
    }

    /// <summary>Whether a result of this type or status is kept. True when no filter is set.</summary>
    public bool Accepts(params string?[] types)
    {
        var accepted = AcceptedTypes;
        if (accepted.Count == 0) return true;

        return types.Any(t => !string.IsNullOrWhiteSpace(t) && accepted.Contains(t.Trim().ToUpperInvariant()));
    }
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
