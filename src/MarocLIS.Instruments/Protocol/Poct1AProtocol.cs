using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MarocLIS.Domain.Equipment;

namespace MarocLIS.Instruments.Protocol;

/// <summary>
/// CLSI POCT1-A device messaging: XML documents exchanged one at a time over a stream.
/// <para>
/// A conversation is the device saying hello (<c>HEL.R01</c>), giving its status
/// (<c>DST.R01</c>, with how many observations it holds), sending its observations
/// (<c>OBS.R01</c>) and closing the topic (<c>EOT.R01</c>). The LIS acknowledges every message
/// with <c>ACK.R01</c>, asks for the observations with <c>REQ.R01</c>, and ends the conversation
/// with <c>END.R01</c>. Every element carries its value in a <c>V</c> attribute.
/// </para>
/// <para>
/// Written from the standard, not from one vendor's implementation: glucose meters, blood-gas and
/// HbA1c analysers each add their own vendor elements, which are ignored here. The observation
/// itself — code, value, unit, interpretation — is common to all of them.
/// </para>
/// </summary>
public static class Poct1A
{
    public const string Version = "POCT1";

    /// <summary>
    /// Splits a byte stream into complete XML documents.
    /// <para>
    /// POCT1-A puts no framing around a message: a document ends where its root element closes. The
    /// reader therefore looks for the root's name and waits for its closing tag (or a self-closing
    /// root), skipping the XML declaration and anything before the first element.
    /// </para>
    /// </summary>
    public sealed class Reader(Encoding encoding)
    {
        private readonly StringBuilder _buffer = new();
        private readonly Decoder _decoder = encoding.GetDecoder();

        public void Feed(ReadOnlySpan<byte> bytes)
        {
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            var count = _decoder.GetChars(bytes, chars, flush: false);
            _buffer.Append(chars, 0, count);
        }

        public void Reset() => _buffer.Clear();

        public string? TryReadMessage()
        {
            var text = _buffer.ToString();

            var start = RootStart(text);
            if (start < 0)
            {
                // Nothing that can start a document: line noise or keep-alive bytes. Keep at most a
                // partial "<" so a root split across two reads is not lost.
                var lt = text.LastIndexOf('<');
                _buffer.Clear();
                if (lt >= 0) _buffer.Append(text[lt..]);
                return null;
            }

            var nameEnd = start + 1;
            while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd])
                   && text[nameEnd] is not ('>' or '/'))
                nameEnd++;

            if (nameEnd >= text.Length) return null;

            var name = text[(start + 1)..nameEnd];

            var openEnd = text.IndexOf('>', nameEnd);
            if (openEnd < 0) return null;

            int end;

            if (text[openEnd - 1] == '/')
            {
                end = openEnd + 1;
            }
            else
            {
                var close = text.IndexOf($"</{name}", openEnd, StringComparison.Ordinal);
                if (close < 0) return null;

                var closeEnd = text.IndexOf('>', close);
                if (closeEnd < 0) return null;

                end = closeEnd + 1;
            }

            // The declaration, if it came with this document, belongs to it.
            var declaration = text.LastIndexOf("<?xml", start, StringComparison.Ordinal);
            var from = declaration >= 0 ? declaration : start;

            var message = text[from..end];

            _buffer.Clear();
            _buffer.Append(text[end..]);

            return message.Trim();
        }

        /// <summary>The first "&lt;" that opens an element, not a declaration or a comment.</summary>
        private static int RootStart(string text)
        {
            var at = 0;

            while ((at = text.IndexOf('<', at)) >= 0)
            {
                if (at + 1 >= text.Length) return -1;

                var next = text[at + 1];

                if (next is '?' or '!')
                {
                    var skip = text.IndexOf('>', at);
                    if (skip < 0) return -1;
                    at = skip + 1;
                    continue;
                }

                if (char.IsLetter(next)) return at;

                at++;
            }

            return -1;
        }
    }

    /// <summary>The type and control id of a message, which is all the conversation needs.</summary>
    public sealed record Header(string Type, string? ControlId, string? DeviceId,
                                int? NewObservations, string? Topic);

    public static Header? ReadHeader(string xml)
    {
        var document = Load(xml);
        if (document?.Root is not { } root) return null;

        return new Header(
            root.Name.LocalName,
            Value(root, "HDR.control_id"),
            Value(root, "DEV.device_id"),
            int.TryParse(Value(root, "DST.new_observations_qty"), out var count) ? count : null,
            Value(root, "EOT.topic_cd"));
    }

    // ---- Outbound ---------------------------------------------------------------------------------

    public static string BuildAck(string controlId, string? inboundControlId, bool accepted,
                                  string? note, DateTimeOffset at)
    {
        var ack = new XElement("ACK",
            V("ACK.type_cd", accepted ? "AA" : "AE"),
            V("ACK.ack_control_id", inboundControlId ?? string.Empty));

        if (!string.IsNullOrWhiteSpace(note)) ack.Add(V("ACK.note_txt", note));

        return Serialise(new XElement("ACK.R01", BuildHeader(controlId, at), ack));
    }

    /// <summary>Asks the device for its observations: <c>ROBS</c>.</summary>
    public static string BuildRequest(string controlId, string requestCode, DateTimeOffset at) =>
        Serialise(new XElement("REQ.R01", BuildHeader(controlId, at),
            new XElement("REQ", V("REQ.request_cd", requestCode))));

    /// <summary>Ends the conversation normally: <c>NRM</c>.</summary>
    public static string BuildEnd(string controlId, DateTimeOffset at) =>
        Serialise(new XElement("END.R01", BuildHeader(controlId, at),
            new XElement("TRM", V("TRM.reason_cd", "NRM"))));

    private static XElement BuildHeader(string controlId, DateTimeOffset at) =>
        new("HDR",
            V("HDR.control_id", controlId),
            V("HDR.version_id", Version),
            V("HDR.creation_dttm", at.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)));

    private static XElement V(string name, string value) => new(name, new XAttribute("V", value));

    private static string Serialise(XElement root) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting);

    // ---- Reading ----------------------------------------------------------------------------------

    internal static XDocument? Load(string xml)
    {
        try
        {
            // No DTD, no external entities: the document comes off a network socket.
            using var reader = XmlReader.Create(new StringReader(xml.Trim()), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });

            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>The <c>V</c> attribute of the first element with that name below the given one.</summary>
    internal static string? Value(XContainer scope, string element, string attribute = "V") =>
        scope.Descendants(element).FirstOrDefault()?.Attribute(attribute)?.Value is { } value
        && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}

/// <summary>
/// Reads a POCT1-A <c>OBS.R01</c> message into results.
/// <para>
/// Each <c>SVC</c> is one test event. Its role says what it is: <c>OBS</c> is a patient,
/// <c>LQC</c> and <c>EQC</c> are quality controls, <c>CAL</c> a calibration. Only patients and
/// controls carry anything for the LIS; calibrations are listed as an issue and dropped.
/// </para>
/// <para>
/// The tube is looked up by the specimen id when the device sends one, and by the patient id
/// otherwise: most bedside devices are used by scanning the tube's barcode into the patient field.
/// The format setting can force one or the other.
/// </para>
/// </summary>
public static class Poct1AResultParser
{
    public static InstrumentMessage Parse(string xml, InstrumentMessageFormat? format = null)
    {
        format ??= InstrumentMessageFormat.Auto;

        var document = Poct1A.Load(xml);

        if (document?.Root is not { } root)
            return new InstrumentMessage
            {
                Kind = InstrumentMessageKind.Unsupported,
                RawText = xml,
                Issues = [InstrumentParseIssue.Blocking("Interface_Poct1AInvalidXml")]
            };

        var type = root.Name.LocalName;
        var header = Header(root);

        if (!type.StartsWith("OBS.", StringComparison.OrdinalIgnoreCase))
            return header with
            {
                Kind = type.StartsWith("ACK.", StringComparison.OrdinalIgnoreCase)
                    ? InstrumentMessageKind.Acknowledgement
                    : InstrumentMessageKind.Status,
                RawText = xml
            };

        var issues = new List<InstrumentParseIssue>();
        var specimens = new List<InstrumentSpecimenResults>();

        foreach (var service in root.Descendants("SVC"))
        {
            var role = (Poct1A.Value(service, "SVC.role_cd") ?? "OBS").ToUpperInvariant();
            var isControl = role is "LQC" or "EQC";

            if (role is not ("OBS" or "LQC" or "EQC"))
            {
                issues.Add(InstrumentParseIssue.Warning("Interface_Poct1ARoleIgnored", role));
                continue;
            }

            var observedAt = Timestamp(Poct1A.Value(service, "SVC.observation_dttm"));

            var results = service.Descendants("OBS")
                .Select(obs => Observation(obs, format, observedAt))
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();

            var specimenId = SpecimenId(service, format);
            var lot = Poct1A.Value(service, "CTC.lot_number") ?? Poct1A.Value(service, "CTC.name");

            specimens.Add(new InstrumentSpecimenResults
            {
                SpecimenId = isControl ? specimenId ?? lot : specimenId,
                PatientId = Poct1A.Value(service, "PT.patient_id"),
                PatientName = Poct1A.Value(service, "PT.name"),
                IsControl = isControl,
                ControlIdentifier = isControl ? lot : null,
                CollectedAt = Timestamp(Poct1A.Value(service, "SPC.specimen_dttm")) ?? observedAt,
                Results = results
            });
        }

        if (specimens.Count == 0 && issues.Count == 0)
            issues.Add(InstrumentParseIssue.Warning("Interface_Poct1ANoObservation"));

        return header with
        {
            Kind = InstrumentMessageKind.Results,
            Specimens = specimens,
            Issues = issues,
            RawText = xml
        };
    }

    private static InstrumentMessage Header(XElement root) => new()
    {
        ControlId = Poct1A.Value(root, "HDR.control_id"),
        SenderVersion = Poct1A.Value(root, "HDR.version_id"),
        SenderId = Poct1A.Value(root, "DEV.device_id"),
        MessageTimestamp = Timestamp(Poct1A.Value(root, "HDR.creation_dttm"))
    };

    private static string? SpecimenId(XElement service, InstrumentMessageFormat format)
    {
        var specimen = Poct1A.Value(service, "SPC.specimen_id");
        var patient = Poct1A.Value(service, "PT.patient_id");

        return format.SpecimenIdField?.ToUpperInvariant() switch
        {
            "PT" => patient,
            "SPC" => specimen,
            _ => specimen ?? patient
        };
    }

    private static InstrumentResult? Observation(XElement obs, InstrumentMessageFormat format,
                                                  DateTimeOffset? observedAt)
    {
        var rawCode = Poct1A.Value(obs, "OBS.observation_id");
        if (rawCode is null) return null;

        var valueElement = obs.Element("OBS.value");
        var value = valueElement?.Attribute("V")?.Value?.Trim();
        var unit = valueElement?.Attribute("U")?.Value?.Trim();

        var comments = obs.Descendants("NTE.text")
            .Select(n => n.Attribute("V")?.Value?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .ToList();

        var interpretation = Poct1A.Value(obs, "OBS.interpretation_cd");
        var method = Poct1A.Value(obs, "OBS.method_cd");

        return new InstrumentResult
        {
            InstrumentTestCode = format.StripSuffix(rawCode),
            RawTestId = rawCode,
            Value = string.IsNullOrEmpty(value) ? null : value,
            Unit = string.IsNullOrEmpty(unit) ? null : unit,
            AbnormalFlag = interpretation,
            ReferenceRange = Poct1A.Value(obs, "OBS.normal_lo-hi_limit"),
            MeasuredAt = observedAt,
            Comments = comments,
            ResultType = method,
            // POCT1-A has no "not done" status; an observation with no value is how a device
            // reports a failed measurement.
            ResultStatus = string.IsNullOrEmpty(value) ? "X" : null,
            IsExcluded = method is not null && !format.Accepts(method)
        };
    }

    private static DateTimeOffset? Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeLocal, out var at)
            ? at
            : null;
}
