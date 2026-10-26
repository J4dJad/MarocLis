using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;

namespace MarocLIS.Application.Instruments;

/// <summary>
/// What the link editor holds while it is open: the transport, the line, the protocol settings and
/// the message format, each as the box shows it. The serial settings are kept under the port's own
/// names (« None », « XOnXOff »), which the screens show in the operator's language.
/// </summary>
public sealed class InterfaceForm
{
    public InstrumentProtocol Protocol { get; set; } = InstrumentProtocol.AstmE1394;
    public InstrumentTransportKind Transport { get; set; } = InstrumentTransportKind.Serial;
    public InstrumentDirection Direction { get; set; } = InstrumentDirection.Unidirectional;
    public bool IsEnabled { get; set; }

    public string SerialPortName { get; set; } = string.Empty;
    public string BaudRate { get; set; } = "9600";
    public string DataBits { get; set; } = "8";
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public string Handshake { get; set; } = "None";

    public string Host { get; set; } = string.Empty;
    public string TcpPort { get; set; } = string.Empty;
    public bool ListenAsServer { get; set; }

    public string InboundDirectory { get; set; } = string.Empty;
    public string FilePattern { get; set; } = string.Empty;
    public string ProcessedDirectory { get; set; } = string.Empty;
    public string OutboundDirectory { get; set; } = string.Empty;

    public string Encoding { get; set; } = "ISO-8859-1";
    public int AcknowledgementTimeoutSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 3;

    public bool RaiseIssueOnUnmappedCode { get; set; } = true;
    public bool RouteControlResultsToQc { get; set; } = true;
    public bool AutoAcceptResults { get; set; }

    /// <summary>The component of the test field that carries the code; null is « Auto ».</summary>
    public int? TestCodeComponent { get; set; }

    public string TestCodeSuffix { get; set; } = string.Empty;

    /// <summary>The field that carries the tube number, as stored (« 3 », « SPM-2 »); null is « Auto ».</summary>
    public string? SpecimenIdField { get; set; }

    public int? SpecimenIdComponent { get; set; }
    public string AcceptedResultTypes { get; set; } = string.Empty;
    public UnmappedNonNumericHandling UnmappedNonNumeric { get; set; } = UnmappedNonNumericHandling.Report;
    public bool KeepInstrumentFlags { get; set; } = true;

    /// <summary>The vendor's reading of HL7; Standard on any other protocol.</summary>
    public Hl7Dialect Hl7Dialect { get; set; }
}

/// <summary>Which half of the link editor means anything for the transport chosen, and what to say about it.</summary>
public sealed record TransportFields(bool Serial, bool Host, bool TcpPort, bool Listen, bool File, string NoticeKey);

/// <summary>A format preset: its caption's key, and the format it fills in (null for « Personnalisé »).</summary>
public sealed record FormatProfile(string Key, MessageFormatSettings? Format);

/// <summary>
/// The rules of the link editor and of the code table, shared by the workstation's
/// InstrumentInterfaceDialog and InstrumentMappingDialog and the web's editors: the choices offered,
/// the presets, the half of the form the transport disables, the checks said before the save and the
/// request built. The refusals on saving stay InstrumentInterfaceService's.
/// </summary>
public static class InterfaceEditorRules
{
    // As long as the interface's columns: a longer path or host failed the save of the whole
    // configuration with no word on which field.
    public const int SerialPortMaxLength = 20;
    public const int HostMaxLength = 150;
    public const int DirectoryMaxLength = 300;
    public const int FilePatternMaxLength = 60;
    public const int TcpPortMaxLength = 5;

    // The lengths the service accepts: past them the save refused the whole configuration.
    public const int CodeSuffixMaxLength = 10;
    public const int ResultTypesMaxLength = 60;

    public const int AcknowledgementTimeoutMax = 300;
    public const int MaxRetriesMax = 10;

    // Each editable column of the code table as long as its field: a longer entry failed the whole table's save.
    public const int MappingCodeMaxLength = 40;
    public const int MappingUnitMaxLength = 40;
    public const int CodedValueMapMaxLength = 1000;

    /// <summary>Only the protocols this build has drivers for: one with no driver would be a setting that silently does nothing.</summary>
    public static readonly IReadOnlyList<InstrumentProtocol> Protocols =
        [InstrumentProtocol.AstmE1394, InstrumentProtocol.Hl7V2, InstrumentProtocol.Poct1A, InstrumentProtocol.RocheIntegra];

    public static readonly IReadOnlyList<InstrumentTransportKind> Transports =
        [InstrumentTransportKind.Serial, InstrumentTransportKind.Tcp, InstrumentTransportKind.FileDrop];

    // A fixed list of values, where free text would only be a way to mistype one.
    public static readonly IReadOnlyList<string> BaudRates = ["1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"];
    public static readonly IReadOnlyList<string> DataBitChoices = ["7", "8"];
    public static readonly IReadOnlyList<string> Parities = ["None", "Even", "Odd", "Mark", "Space"];
    public static readonly IReadOnlyList<string> StopBitChoices = ["One", "OnePointFive", "Two"];
    public static readonly IReadOnlyList<string> Handshakes = ["None", "XOnXOff", "RequestToSend", "RequestToSendXOnXOff"];
    public static readonly IReadOnlyList<string> Encodings = ["ISO-8859-1", "UTF-8", "ASCII", "Windows-1252", "Windows-1256"];

    /// <summary>The readings of HL7 the driver knows, offered on an HL7 link only.</summary>
    public static readonly IReadOnlyList<Hl7Dialect> Dialects =
        [Hl7Dialect.Standard, Hl7Dialect.MindrayBs, Hl7Dialect.MindrayBc, Hl7Dialect.MindrayCl];

    /// <summary>The components a code or a tube number can be read from; null is « Auto ».</summary>
    public static readonly IReadOnlyList<int?> Components = [null, 1, 2, 3, 4, 5, 6];

    /// <summary>The key a serial setting is shown under: « Interface.ParityNone ».</summary>
    public static string SerialKey(string setting, string value) => $"Interface.{setting}{value}";

    public const string CustomProfile = "Interface.FormatProfile.Custom";
    public const string IntegraProfile = "Interface.FormatProfile.RocheIntegra";
    public const string MindrayBsProfile = "Interface.FormatProfile.MindrayBs";
    public const string MindrayBcProfile = "Interface.FormatProfile.MindrayBc";
    public const string MindrayClProfile = "Interface.FormatProfile.MindrayCl";

    /// <summary>
    /// The port suggested for a Mindray link, the LIS listening: the analyser's « LIS » screen takes
    /// the LIS address and a port, which only has to be the same on both sides. One per family, so a
    /// BS and a BC cabled to the same workstation do not claim the same port.
    /// </summary>
    public static int SuggestedMindrayPort(Hl7Dialect dialect) => dialect switch
    {
        Hl7Dialect.MindrayBc => 5101,
        Hl7Dialect.MindrayCl => 5102,
        _ => 5100
    };

    /// <summary>
    /// Presets for the vendors whose layout is known to depart from the standard. A preset fills the
    /// fields; the fields are what is saved, so a preset is never a hidden setting.
    /// </summary>
    public static readonly IReadOnlyList<FormatProfile> FormatProfiles =
    [
        new(CustomProfile, null),
        new("Interface.FormatProfile.Standard", MessageFormatSettings.Default),
        // cobas c 311 / c 501 / e 411: "^^^GLY/" — the dilution after the slash.
        new("Interface.FormatProfile.RocheCobas", MessageFormatSettings.Default with { TestCodeSuffix = "/" }),
        // Architect / Alinity: "^^^16^TSH^UNDILUTED^F" — keep the final value, not the
        // interpretation (I) or the preliminary one (P).
        new("Interface.FormatProfile.Abbott", MessageFormatSettings.Default with { AcceptedResultTypes = "F" }),
        // COBAS INTEGRA 400 plus: its own host protocol, not ASTM. Choosing it also sets the
        // protocol and the serial line (see ApplyProfile); the format stays "Auto".
        new(IntegraProfile, MessageFormatSettings.Default),
        // Mindray in HL7 v2.3.1: the BS (and the CL, laid out alike) put the tube's barcode in OBR-2
        // and the test code, as the analyser's test settings name it, in OBX-3. The BC puts the
        // sample number in OBR-3 and « 6690-2^WBC^LN » in OBX-3: its second component is the name
        // the laboratory knows (WBC, HGB), and its alarms and histograms are not values. Choosing one
        // also sets HL7, TCP with the LIS listening, the direction and the encoding (see ApplyProfile).
        new(MindrayBsProfile, MessageFormatSettings.Default with { SpecimenIdField = "OBR-2", Dialect = Hl7Dialect.MindrayBs }),
        new(MindrayBcProfile, MessageFormatSettings.Default with
        {
            SpecimenIdField = "OBR-3", TestCodeComponent = 2,
            UnmappedNonNumeric = UnmappedNonNumericHandling.Ignore, Dialect = Hl7Dialect.MindrayBc
        }),
        new(MindrayClProfile, MessageFormatSettings.Default with { SpecimenIdField = "OBR-2", Dialect = Hl7Dialect.MindrayCl })
    ];

    /// <summary>The Mindray preset of a dialect, null for Standard.</summary>
    public static string? MindrayProfile(Hl7Dialect dialect) => dialect switch
    {
        Hl7Dialect.MindrayBs => MindrayBsProfile,
        Hl7Dialect.MindrayBc => MindrayBcProfile,
        Hl7Dialect.MindrayCl => MindrayClProfile,
        _ => null
    };

    /// <summary>The form of a link being edited, or of a new one with the editor's defaults.</summary>
    public static InterfaceForm FormOf(InterfaceRow? existing)
    {
        var format = existing?.Format ?? MessageFormatSettings.Default;

        return new InterfaceForm
        {
            Protocol = existing?.Protocol ?? InstrumentProtocol.AstmE1394,
            Transport = existing?.Transport ?? InstrumentTransportKind.Serial,
            Direction = existing?.Direction ?? InstrumentDirection.Unidirectional,
            IsEnabled = existing?.IsEnabled ?? false,
            SerialPortName = existing?.SerialPortName ?? string.Empty,
            BaudRate = Offered(BaudRates, (existing?.BaudRate ?? 9600).ToString(CultureInfo.InvariantCulture)),
            DataBits = Offered(DataBitChoices, (existing?.DataBits ?? 8).ToString(CultureInfo.InvariantCulture)),
            Parity = Offered(Parities, existing?.Parity ?? "None"),
            StopBits = Offered(StopBitChoices, existing?.StopBits ?? "One"),
            Handshake = Offered(Handshakes, existing?.Handshake ?? "None"),
            Host = existing?.Host ?? string.Empty,
            TcpPort = existing?.TcpPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ListenAsServer = existing?.ListenAsServer ?? false,
            InboundDirectory = existing?.InboundDirectory ?? string.Empty,
            FilePattern = existing?.FilePattern ?? string.Empty,
            ProcessedDirectory = existing?.ProcessedDirectory ?? string.Empty,
            OutboundDirectory = existing?.OutboundDirectory ?? string.Empty,
            Encoding = Offered(Encodings, existing?.Encoding ?? "ISO-8859-1"),
            AcknowledgementTimeoutSeconds = Math.Clamp(existing?.AcknowledgementTimeoutSeconds ?? 15, 0, AcknowledgementTimeoutMax),
            MaxRetries = Math.Clamp(existing?.MaxRetries ?? 3, 0, MaxRetriesMax),
            RaiseIssueOnUnmappedCode = existing?.RaiseIssueOnUnmappedCode ?? true,
            RouteControlResultsToQc = existing?.RouteControlResultsToQc ?? true,
            AutoAcceptResults = existing?.AutoAcceptResults ?? false,
            TestCodeComponent = Offered(format.TestCodeComponent),
            TestCodeSuffix = format.TestCodeSuffix ?? string.Empty,
            SpecimenIdField = OfferedField(existing?.Protocol ?? InstrumentProtocol.AstmE1394, format.SpecimenIdField),
            SpecimenIdComponent = Offered(format.SpecimenIdComponent),
            AcceptedResultTypes = format.AcceptedResultTypes ?? string.Empty,
            UnmappedNonNumeric = format.UnmappedNonNumeric,
            KeepInstrumentFlags = format.KeepInstrumentFlags,
            Hl7Dialect = existing?.Protocol == InstrumentProtocol.Hl7V2 ? format.Dialect : Hl7Dialect.Standard
        };
    }

    /// <summary>The value when the list offers it (in any case), the list's first otherwise — as a fixed drop-down falls back.</summary>
    public static string Offered(IReadOnlyList<string> values, string? current)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.FirstOrDefault(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase)) ?? values[0];
    }

    private static int? Offered(int? component) => Components.Contains(component) ? component : null;

    // ---- Message format ------------------------------------------------------------------------

    /// <summary>
    /// The fields a tube number can be read from in a protocol, each with the caption the manuals
    /// print: ASTM fields by their place in the O record (« O-3 »).
    /// </summary>
    public static IReadOnlyList<(string Value, string Caption)> SpecimenFields(InstrumentProtocol protocol) =>
        [.. MessageFormatSettings.SpecimenFieldsFor(protocol)
            .Select(f => (f, protocol == InstrumentProtocol.AstmE1394 ? $"O-{f}" : f))];

    /// <summary>The field kept when the protocol offers it, « Auto » otherwise.</summary>
    public static string? OfferedField(InstrumentProtocol protocol, string? field)
    {
        if (string.IsNullOrEmpty(field)) return null;

        var bare = field.Replace("O-", "", StringComparison.Ordinal);
        return MessageFormatSettings.SpecimenFieldsFor(protocol)
                                    .FirstOrDefault(f => string.Equals(f, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a code and a tube number are read from components. POCT1-A is XML, where an element
    /// holds one value; the INTEGRA's lines have fixed fields (test number, barcode, value).
    /// </summary>
    public static bool IsDelimited(InstrumentProtocol protocol) =>
        protocol is not (InstrumentProtocol.Poct1A or InstrumentProtocol.RocheIntegra);

    /// <summary>
    /// After the protocol changed: the tube field kept if the new protocol offers it, the
    /// components back to « Auto » where there are none.
    /// </summary>
    public static void ProtocolChanged(InterfaceForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        form.SpecimenIdField = OfferedField(form.Protocol, form.SpecimenIdField);

        if (!IsDialectOffered(form.Protocol)) form.Hl7Dialect = Hl7Dialect.Standard;

        if (!IsDelimited(form.Protocol))
        {
            form.TestCodeComponent = null;
            form.SpecimenIdComponent = null;
        }
    }

    /// <summary>A dialect is HL7's reading: offered on an HL7 link, Standard and greyed on any other.</summary>
    public static bool IsDialectOffered(InstrumentProtocol protocol) => protocol == InstrumentProtocol.Hl7V2;

    /// <summary>The format the form describes, saved or not: what is tried in « Tester une trame » and what is saved.</summary>
    public static MessageFormatSettings Format(InterfaceForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new MessageFormatSettings(
            form.TestCodeComponent,
            Blank(form.TestCodeSuffix),
            OfferedField(form.Protocol, form.SpecimenIdField),
            form.SpecimenIdComponent,
            Blank(form.AcceptedResultTypes),
            form.UnmappedNonNumeric,
            form.KeepInstrumentFlags)
        {
            Dialect = IsDialectOffered(form.Protocol) ? form.Hl7Dialect : Hl7Dialect.Standard
        };
    }

    /// <summary>
    /// The preset a format matches, « Personnalisé » when none does. The INTEGRA preset is its
    /// protocol: shown for an INTEGRA link, and for no other.
    /// </summary>
    public static string ProfileFor(InstrumentProtocol protocol, MessageFormatSettings format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (protocol == InstrumentProtocol.RocheIntegra) return IntegraProfile;

        foreach (var profile in FormatProfiles.Skip(1).Where(p => p.Key != IntegraProfile))
        {
            var p = profile.Format!;
            if (p.TestCodeComponent == format.TestCodeComponent
                && string.Equals(p.TestCodeSuffix ?? "", format.TestCodeSuffix ?? "", StringComparison.Ordinal)
                && string.Equals(p.SpecimenIdField ?? "", format.SpecimenIdField ?? "", StringComparison.OrdinalIgnoreCase)
                && p.SpecimenIdComponent == format.SpecimenIdComponent
                && string.Equals(p.AcceptedResultTypes ?? "", format.AcceptedResultTypes ?? "", StringComparison.OrdinalIgnoreCase)
                && p.Dialect == (protocol == InstrumentProtocol.Hl7V2 ? format.Dialect : Hl7Dialect.Standard))
                return profile.Key;
        }

        return CustomProfile;
    }

    /// <summary>The preset the form stands at now.</summary>
    public static string ProfileOf(InterfaceForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return ProfileFor(form.Protocol, Format(form));
    }

    /// <summary>
    /// Fills the form from a preset. « Personnalisé » changes nothing; a vendor format chosen on an
    /// INTEGRA link puts it back on ASTM, whose format it is. The INTEGRA preset is its host protocol
    /// and an RS-232 line its « Configuration › General › Interface » screen offers: the manual lists
    /// the choices but recommends no speed, so 9600 8N1 is a starting point to align with the
    /// analyser, not a fact; it does recommend the software handshake and a host time-out of 180 s.
    /// <para>
    /// A Mindray preset is HL7 v2.3.1 over TCP, the analyser connecting to the LIS: the LIS listens,
    /// on the family's suggested port unless one is typed already. The BS and the CL ask the LIS for
    /// each tube's worklist (bidirectional); the BC only sends its counts, in UTF-8 (« UNICODE » in
    /// its header), its alarms and histograms dropped as unknown non-numeric codes.
    /// </para>
    /// </summary>
    public static void ApplyProfile(InterfaceForm form, string key)
    {
        ArgumentNullException.ThrowIfNull(form);

        var format = FormatProfiles.FirstOrDefault(p => p.Key == key)?.Format;
        if (format is null) return;

        if (key == IntegraProfile)
        {
            form.Protocol = InstrumentProtocol.RocheIntegra;
            form.Transport = InstrumentTransportKind.Serial;
            form.Direction = InstrumentDirection.HostQueryDownload;
            form.BaudRate = "9600";
            form.DataBits = "8";
            form.Parity = "None";
            form.StopBits = "One";
            form.Handshake = "XOnXOff";
            form.Encoding = "ASCII";
            form.AcknowledgementTimeoutSeconds = Math.Min(AcknowledgementTimeoutMax, 180);
            form.MaxRetries = Math.Min(MaxRetriesMax, 3);
        }
        else if (format.Dialect != Hl7Dialect.Standard)
        {
            form.Protocol = InstrumentProtocol.Hl7V2;
            form.Transport = InstrumentTransportKind.Tcp;
            form.ListenAsServer = true;
            if (string.IsNullOrWhiteSpace(form.TcpPort))
                form.TcpPort = SuggestedMindrayPort(format.Dialect).ToString(CultureInfo.InvariantCulture);
            form.Direction = format.Dialect == Hl7Dialect.MindrayBc
                ? InstrumentDirection.Unidirectional
                : InstrumentDirection.Bidirectional;
            form.Encoding = format.Dialect == Hl7Dialect.MindrayBc ? "UTF-8" : "ISO-8859-1";
            form.UnmappedNonNumeric = format.UnmappedNonNumeric;
        }
        else if (form.Protocol == InstrumentProtocol.RocheIntegra)
        {
            form.Protocol = InstrumentProtocol.AstmE1394;
        }

        form.Hl7Dialect = format.Dialect;
        form.TestCodeComponent = format.TestCodeComponent;
        form.TestCodeSuffix = format.TestCodeSuffix ?? string.Empty;
        form.SpecimenIdField = format.SpecimenIdField;
        form.SpecimenIdComponent = format.SpecimenIdComponent;
        form.AcceptedResultTypes = format.AcceptedResultTypes ?? string.Empty;

        ProtocolChanged(form);
    }

    /// <summary>On an INTEGRA link the notice says where its own settings are, instead of the ASTM advice.</summary>
    public static string FormatNoticeKey(InstrumentProtocol protocol) =>
        FormatNoticeKey(protocol, Hl7Dialect.Standard);

    /// <summary>On a Mindray link the notice says what to set on the analyser's side.</summary>
    public static string FormatNoticeKey(InstrumentProtocol protocol, Hl7Dialect dialect) => protocol switch
    {
        InstrumentProtocol.RocheIntegra => "Interface.IntegraNotice",
        InstrumentProtocol.Hl7V2 when dialect == Hl7Dialect.MindrayBc => "Interface.MindrayBcNotice",
        InstrumentProtocol.Hl7V2 when dialect != Hl7Dialect.Standard => "Interface.MindrayNotice",
        _ => "Interface.FormatNotice"
    };

    // ---- Transport -----------------------------------------------------------------------------

    /// <summary>
    /// The half of the form the transport gives a meaning to. A serial analyser has no host and port;
    /// a file-drop interface has no baud rate; a link that listens has no host to reach. Leaving both
    /// halves open invites somebody to fill in a TCP port on a serial link and then spend an afternoon
    /// wondering why it will not connect.
    /// </summary>
    public static TransportFields Fields(InstrumentTransportKind transport, bool listenAsServer)
    {
        var serial = transport == InstrumentTransportKind.Serial;
        var tcp = transport == InstrumentTransportKind.Tcp;
        var file = transport == InstrumentTransportKind.FileDrop;

        return new TransportFields(
            Serial: serial,
            Host: tcp && !listenAsServer,
            TcpPort: tcp,
            Listen: tcp,
            File: file,
            NoticeKey: transport switch
            {
                InstrumentTransportKind.Serial => "Interface.SerialNotice",
                InstrumentTransportKind.Tcp => "Interface.TcpNotice",
                _ => "Interface.FileDropNotice"
            });
    }

    // ---- Checks and request --------------------------------------------------------------------

    /// <summary>
    /// What is said before the save, on the box at fault: a link belongs to an analyser chosen
    /// beforehand; a serial link names its port, a TCP one a port between 1 and 65535 and, unless
    /// it listens, the host it reaches; a file drop the folder it reads.
    /// </summary>
    public static FieldRefusal? Check(InterfaceForm form, Guid instrumentId)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (instrumentId == Guid.Empty) return new FieldRefusal("instrument", "Interface_InstrumentRequired");

        if (form.Transport == InstrumentTransportKind.Serial && string.IsNullOrWhiteSpace(form.SerialPortName))
            return new FieldRefusal("serialport", "Interface_SerialPortRequired");

        if (form.Transport == InstrumentTransportKind.Tcp)
        {
            if (TcpPort(form) is null or < 1 or > 65535) return new FieldRefusal("tcpport", "Interface_PortInvalid");

            if (!form.ListenAsServer && string.IsNullOrWhiteSpace(form.Host))
                return new FieldRefusal("host", "Interface_HostRequired");
        }

        if (form.Transport == InstrumentTransportKind.FileDrop && string.IsNullOrWhiteSpace(form.InboundDirectory))
            return new FieldRefusal("inbound", "Interface_InboundDirectoryRequired");

        return null;
    }

    /// <summary>
    /// Said before the save, not after a refusal: auto-acceptance is the setting that can put an
    /// analyser's number in a patient file with nobody looking at it.
    /// </summary>
    public static bool NeedsAutoAcceptConfirmation(InterfaceForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.AutoAcceptResults;
    }

    /// <summary>The request the form saves, the line's figures read as the drop-downs give them.</summary>
    public static InterfaceRequest ToRequest(InterfaceForm form, Guid? id, Guid instrumentId)
    {
        ArgumentNullException.ThrowIfNull(form);

        return new InterfaceRequest
        {
            Id = id,
            InstrumentId = instrumentId,
            Protocol = form.Protocol,
            Transport = form.Transport,
            Direction = form.Direction,
            IsEnabled = form.IsEnabled,

            SerialPortName = Blank(form.SerialPortName),
            BaudRate = int.TryParse(form.BaudRate, NumberStyles.None, CultureInfo.InvariantCulture, out var baud) ? baud : 9600,
            DataBits = int.TryParse(form.DataBits, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) ? bits : 8,
            Parity = Blank(form.Parity) ?? "None",
            StopBits = Blank(form.StopBits) ?? "One",
            Handshake = Blank(form.Handshake) ?? "None",

            Host = Blank(form.Host),
            TcpPort = TcpPort(form),
            ListenAsServer = form.ListenAsServer,

            InboundDirectory = Blank(form.InboundDirectory),
            OutboundDirectory = Blank(form.OutboundDirectory),
            FilePattern = Blank(form.FilePattern),
            ProcessedDirectory = Blank(form.ProcessedDirectory),

            Encoding = Blank(form.Encoding) ?? "ISO-8859-1",
            AcknowledgementTimeoutSeconds = form.AcknowledgementTimeoutSeconds,
            MaxRetries = form.MaxRetries,

            AutoAcceptResults = form.AutoAcceptResults,
            RaiseIssueOnUnmappedCode = form.RaiseIssueOnUnmappedCode,
            RouteControlResultsToQc = form.RouteControlResultsToQc,

            Format = Format(form)
        };
    }

    private static int? TcpPort(InterfaceForm form) =>
        int.TryParse(form.TcpPort.Trim(), out var port) ? port : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---- The code table ------------------------------------------------------------------------

    /// <summary>
    /// The codes the table shows. A removed code is kept, switched off, so old messages can still be
    /// read through it; it is not shown again, and re-adding the parameter under the same code
    /// brings it back.
    /// </summary>
    public static List<MappingLine> Lines(IEnumerable<MappingRow> existing, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(existing);

        return [.. existing.Where(m => m.IsEnabled).Select(m => new MappingLine
        {
            Original = m,
            Code = m.InstrumentTestCode,
            InstrumentUnit = m.InstrumentUnit ?? string.Empty,
            Factor = m.ConversionFactor is { } factor ? TypedNumber.ForEditing(factor, 0, culture) : string.Empty,
            Offset = m.ConversionOffset is { } offset ? TypedNumber.ForEditing(offset, 0, culture) : string.Empty,
            CodedValueMap = m.CodedValueMap ?? string.Empty
        })];
    }

    /// <summary>The switched-off codes, kept and not shown.</summary>
    public static int Retired(IEnumerable<MappingRow> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing.Count(m => !m.IsEnabled);
    }

    /// <summary>
    /// A catalogue parameter linked to the analyser: its code starts as the laboratory's own, to be
    /// overwritten with the code the analyser actually transmits — the line says so until it is.
    /// </summary>
    public static MappingLine NewLine(MappableParameter parameter, Guid interfaceId)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return new MappingLine
        {
            Original = new MappingRow(
                Guid.Empty, interfaceId, parameter.ParameterCode, parameter.TestParameterId,
                parameter.ParameterCode, parameter.ParameterName, parameter.AnalysisCode,
                parameter.ValueType, parameter.UnitSymbol, null, null, null, null, true),
            Code = parameter.ParameterCode,
            IsNew = true
        };
    }

    /// <summary>The parameters the table maps already: one parameter takes one code, so the picker offers them greyed.</summary>
    public static HashSet<Guid> Mapped(IEnumerable<MappingLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return [.. lines.Select(l => l.Original.TestParameterId)];
    }

    /// <summary>« GLY/GLY  Glycémie »: the parameter a line feeds, which the table does not let be typed.</summary>
    public static string ParameterLabel(MappingRow mapping, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return $"{mapping.AnalysisCode}/{mapping.ParameterCode}  {mapping.ParameterName.For(language)}";
    }

    /// <summary>
    /// The analyser reports another unit and no factor is typed: the conversion factor is the most
    /// dangerous number on the screen, a wrong or missing one a hundredfold error on a report.
    /// </summary>
    public static bool IsUnitMismatch(MappingLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return !string.IsNullOrWhiteSpace(line.InstrumentUnit)
               && !string.IsNullOrWhiteSpace(line.Original.CatalogueUnit)
               && !string.Equals(line.InstrumentUnit, line.Original.CatalogueUnit, StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrWhiteSpace(line.Factor);
    }

    /// <summary>« 7 code(s) · 2 retiré(s) ».</summary>
    public static string Summary(int total, int retired, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text("Interface.MappingSummary", total, retired);
    }

    /// <summary>
    /// The table as the service saves it, or the first line at fault: a code is required, a factor
    /// and an offset are numbers — read in any of the cultures, the Arabic minus with its direction
    /// mark included — a factor is not zero, and neither is past what its column holds.
    /// </summary>
    public static (IReadOnlyList<MappingRequest> Mappings, FieldRefusal? Refusal, int Line) Check(IReadOnlyList<MappingLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var mappings = new List<MappingRequest>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line.Code))
                return ([], new FieldRefusal("code", "Interface_MappingCodeRequired"), i);

            if (!TryParse(line.Factor, out var factor) || !TryParse(line.Offset, out var offset))
                return ([], new FieldRefusal("factor", "Interface_ConversionNotNumeric"), i);

            if (factor is 0m)
                return ([], new FieldRefusal("factor", "Interface_ConversionFactorZero"), i);

            if (Math.Abs(factor ?? 0m) >= InstrumentInterfaceService.MaximumConversion
                || Math.Abs(offset ?? 0m) >= InstrumentInterfaceService.MaximumConversion)
                return ([], new FieldRefusal("factor", "Interface_ConversionOutOfRange"), i);

            mappings.Add(new MappingRequest(line.Code.Trim(), line.Original.TestParameterId,
                                            line.InstrumentUnit, factor, offset, line.CodedValueMap, true));
        }

        return (mappings, null, -1);
    }

    private static bool TryParse(string? text, out decimal? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text)) return true;

        value = TypedNumber.Parse(text);
        return value is not null;
    }
}

/// <summary>One line of the code table as the editor holds it, the parameter it feeds kept from the row.</summary>
public sealed class MappingLine
{
    public required MappingRow Original { get; init; }
    public string Code { get; set; } = string.Empty;
    public string InstrumentUnit { get; set; } = string.Empty;
    public string Factor { get; set; } = string.Empty;
    public string Offset { get; set; } = string.Empty;
    public string CodedValueMap { get; set; } = string.Empty;

    /// <summary>Added in this edit: its code is still the laboratory's, not yet the analyser's.</summary>
    public bool IsNew { get; init; }
}
