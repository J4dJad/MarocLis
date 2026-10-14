using System.Text;
using MarocLIS.Domain.Equipment;

namespace MarocLIS.Instruments.Protocol;

/// <summary>
/// MLLP (Minimal Lower Layer Protocol) framing: <c>0x0B message 0x1C 0x0D</c>. HL7 over TCP has no
/// length prefix, so without this wrapper a reader cannot tell where one message ends.
/// </summary>
public static class Mllp
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;
    public const byte CarriageReturn = 0x0D;

    public static byte[] Wrap(string message, Encoding? encoding = null)
    {
        var enc = encoding ?? Encoding.UTF8;
        var payload = enc.GetBytes(message);

        var framed = new byte[payload.Length + 3];
        framed[0] = StartBlock;
        payload.CopyTo(framed, 1);
        framed[^2] = EndBlock;
        framed[^1] = CarriageReturn;
        return framed;
    }

    /// <summary>
    /// Incremental MLLP reader. Same reasoning as the ASTM frame reader: TCP delivers arbitrary
    /// chunks, so the boundary has to be found across reads rather than assumed per read.
    /// </summary>
    public sealed class Reader(Encoding? encoding = null)
    {
        private readonly Encoding _encoding = encoding ?? Encoding.UTF8;
        private readonly List<byte> _buffer = [];

        public void Feed(ReadOnlySpan<byte> data)
        {
            foreach (var b in data) _buffer.Add(b);
        }

        public string? TryReadMessage()
        {
            var start = _buffer.IndexOf(StartBlock);
            if (start < 0)
            {
                // Nothing framed yet; drop anything that arrived before a start block.
                if (_buffer.Count > 0) _buffer.Clear();
                return null;
            }

            for (var i = start + 1; i < _buffer.Count - 1; i++)
            {
                if (_buffer[i] != EndBlock || _buffer[i + 1] != CarriageReturn) continue;

                var payload = _buffer.GetRange(start + 1, i - start - 1).ToArray();
                _buffer.RemoveRange(0, i + 2);
                return _encoding.GetString(payload);
            }

            return null;
        }

        public void Reset() => _buffer.Clear();
    }
}

/// <summary>
/// Maps an HL7 v2 <c>ORU^R01</c> result message, or a query, onto the normalised
/// <see cref="InstrumentMessage"/>.
/// <para>
/// The specimen identifier is read from SPM-2 where the analyser sends a specimen segment and from
/// OBR-3 otherwise, falling back to OBR-2. Analysers differ on which of those they populate, and
/// picking only one is the difference between results landing in the right file and not landing at
/// all.
/// </para>
/// </summary>
public static class Hl7ResultParser
{
    public static InstrumentMessage Parse(string text, InstrumentMessageFormat? format = null)
    {
        format ??= InstrumentMessageFormat.Auto;
        var message = Hl7Message.Parse(text);
        var issues = new List<InstrumentParseIssue>();

        var type = message.MessageType;

        if (type is null)
        {
            return new InstrumentMessage
            {
                Kind = InstrumentMessageKind.Unsupported,
                Issues = [InstrumentParseIssue.Blocking("Instruments_MissingMsh")],
                RawText = text
            };
        }

        if (type.StartsWith("ACK", StringComparison.Ordinal))
        {
            return new InstrumentMessage
            {
                Kind = InstrumentMessageKind.Acknowledgement,
                SenderId = message.SendingApplication,
                ControlId = message.ControlId,
                MessageTimestamp = message.MessageTimestamp,
                RawText = text
            };
        }

        if (type.StartsWith("QRY", StringComparison.Ordinal) ||
            type.StartsWith("QBP", StringComparison.Ordinal))
            return ParseQuery(message, text);

        if (!type.StartsWith("ORU", StringComparison.Ordinal) &&
            !type.StartsWith("ORL", StringComparison.Ordinal))
            issues.Add(InstrumentParseIssue.Warning("Instruments_UnsupportedMessageType", type));

        var specimens = new List<InstrumentSpecimenResults>();

        // Walk the segments in order: PID opens a patient, OBR opens a specimen, OBX and NTE
        // attach to whichever is currently open. HL7's grouping is positional, not explicit.
        string? patientId = null;
        string? patientName = null;
        string? specimenIdFromSpm = null;
        string[]? lastSpm = null;
        string[]? lastOrc = null;

        Builder? current = null;

        void Close()
        {
            if (current is null) return;
            specimens.Add(current.Build());
            current = null;
        }

        foreach (var segment in message.Segments)
        {
            if (segment.Length == 0) continue;

            switch (segment[0])
            {
                case "PID":
                {
                    Close();
                    patientId = message.Component(segment, 3, 1) ?? message.Field(segment, 2);
                    var family = message.Component(segment, 5, 1);
                    var given = message.Component(segment, 5, 2);
                    patientName = string.Join(' ', new[] { family, given }
                        .Where(p => !string.IsNullOrWhiteSpace(p)));
                    if (patientName.Length == 0) patientName = null;
                    break;
                }

                case "ORC":
                    lastOrc = segment;
                    break;

                case "SPM":
                    lastSpm = segment;
                    // SPM-2 is the specimen identifier; it precedes the OBR it describes.
                    specimenIdFromSpm = message.Component(segment, 2, 1)
                                        ?? message.Component(segment, 2, 2)
                                        ?? message.Field(segment, 2);
                    break;

                case "OBR":
                {
                    Close();

                    var specimenId = ConfiguredSpecimenId(message, format, segment, lastSpm, lastOrc)
                                     ?? specimenIdFromSpm
                                     ?? message.Component(segment, 3, 1)
                                     ?? message.Field(segment, 3)
                                     ?? message.Component(segment, 2, 1)
                                     ?? message.Field(segment, 2);

                    specimenIdFromSpm = null;
                    lastSpm = null;

                    if (string.IsNullOrWhiteSpace(specimenId))
                    {
                        issues.Add(InstrumentParseIssue.Blocking("Instruments_MissingSpecimenId",
                                                                 string.Join('|', segment)));
                        break;
                    }

                    var specimenSource = message.Field(segment, 15);
                    var priority = message.Field(segment, 27);

                    current = new Builder
                    {
                        SpecimenId = specimenId!.Trim(),
                        PatientId = patientId,
                        PatientName = patientName,
                        Priority = priority,
                        CollectedAt = Hl7Message.ParseTimestamp(message.Field(segment, 7)),
                        ReceivedAt = Hl7Message.ParseTimestamp(message.Field(segment, 14)),
                        // "Q" in the specimen source, or an explicit control flag, marks QC material.
                        IsControl = specimenSource?.Contains("CONTROL", StringComparison.OrdinalIgnoreCase) == true
                                    || specimenSource?.Contains("QC", StringComparison.OrdinalIgnoreCase) == true,
                        ControlIdentifier = specimenSource
                    };
                    break;
                }

                case "OBX":
                {
                    if (current is null)
                    {
                        issues.Add(InstrumentParseIssue.Warning("Instruments_ResultWithoutOrder",
                                                                string.Join('|', segment)));
                        break;
                    }

                    var code = message.Component(segment, 3, format.TestCodeComponent is > 0 and var c ? c : 1);
                    if (!string.IsNullOrWhiteSpace(code)) code = format.StripSuffix(code.Trim());
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        issues.Add(InstrumentParseIssue.Warning("Instruments_MissingTestCode",
                                                                string.Join('|', segment)));
                        break;
                    }

                    // OBX-5 repeats for multi-part values; join them rather than keeping only the first.
                    var value = string.Join(' ', message.Repetitions(segment, 5)
                        .Select(message.Unescape)
                        .Where(v => v.Length > 0));

                    var valueType = message.Field(segment, 2);
                    var status = message.Field(segment, 11);

                    current.Results.Add(new InstrumentResult
                    {
                        InstrumentTestCode = code!.Trim(),
                        RawTestId = message.Field(segment, 3),
                        ResultType = string.Join('/', new[] { valueType, status }
                            .Where(t => !string.IsNullOrWhiteSpace(t))),
                        IsExcluded = !format.Accepts(valueType, status),
                        Value = value.Length > 0 ? value : null,
                        Unit = message.Component(segment, 6, 1),
                        ReferenceRange = message.Field(segment, 7),
                        AbnormalFlag = message.Field(segment, 8),
                        ResultStatus = message.Field(segment, 11),
                        MeasuredAt = Hl7Message.ParseTimestamp(message.Field(segment, 19))
                                     ?? Hl7Message.ParseTimestamp(message.Field(segment, 14)),
                        InstrumentOperator = message.Component(segment, 16, 1)
                    });
                    break;
                }

                case "NTE":
                {
                    var note = message.Unescape(message.Field(segment, 3));
                    if (string.IsNullOrWhiteSpace(note) || current is null) break;

                    if (current.Results.Count > 0)
                    {
                        var last = current.Results[^1];
                        current.Results[^1] = last with { Comments = [.. last.Comments, note] };
                    }
                    else
                    {
                        current.Comments.Add(note);
                    }
                    break;
                }
            }
        }

        Close();

        return new InstrumentMessage
        {
            Kind = specimens.Count > 0 ? InstrumentMessageKind.Results : InstrumentMessageKind.Unsupported,
            SenderId = message.SendingApplication,
            SenderVersion = message.Version,
            ControlId = message.ControlId,
            MessageTimestamp = message.MessageTimestamp,
            Specimens = specimens,
            Issues = issues,
            RawText = text
        };
    }

    /// <summary>
    /// The specimen number from the segment the link names ("SPM-2", "OBR-3", "OBR-2", "ORC-3"),
    /// taking the named component or the first. Null when the link is on Auto or the field is empty,
    /// so the usual fallbacks apply.
    /// </summary>
    private static string? ConfiguredSpecimenId(Hl7Message message, InstrumentMessageFormat format,
                                                string[] obr, string[]? spm, string[]? orc)
    {
        var location = format.SpecimenIdField?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(location)) return null;

        var parts = location.Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var field)) return null;

        var segment = parts[0] switch
        {
            "SPM" => spm,
            "ORC" => orc,
            "OBR" => obr,
            _ => null
        };

        if (segment is null) return null;

        var value = message.Component(segment, field, format.SpecimenIdComponent is > 0 and var c ? c : 1);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static InstrumentMessage ParseQuery(Hl7Message message, string text)
    {
        var ids = new List<string>();

        // v2.3-style QRD-8, and v2.5-style QPD-3.
        foreach (var name in (string[])["QRD", "QPD"])
        {
            foreach (var segment in message.SegmentsNamed(name))
            {
                var fieldNumber = name == "QRD" ? 8 : 3;
                foreach (var repetition in message.Repetitions(segment, fieldNumber))
                {
                    var id = repetition.Split(message.Encoding.Component)
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
                    if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal))
                        ids.Add(id!);
                }
            }
        }

        return new InstrumentMessage
        {
            Kind = InstrumentMessageKind.Query,
            SenderId = message.SendingApplication,
            ControlId = message.ControlId,
            MessageTimestamp = message.MessageTimestamp,
            QueriedSpecimenIds = ids,
            RawText = text
        };
    }

    /// <summary>
    /// Builds an <c>ORM^O01</c> order message to push work to an analyser that expects HL7 rather
    /// than ASTM for its download.
    /// </summary>
    public static string BuildOrderMessage(InstrumentOrderDownload order, string sendingApplication,
                                           string receivingApplication, DateTimeOffset now)
    {
        var e = Hl7Encoding.Default;
        var f = e.Field;
        var controlId = now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        var builder = new StringBuilder();

        builder.Append("MSH").Append(f).Append(e.EncodingCharacters).Append(f)
               .Append(sendingApplication).Append(f).Append(f)
               .Append(receivingApplication).Append(f).Append(f)
               .Append(Hl7Message.Timestamp(now)).Append(f).Append(f)
               .Append("ORM^O01").Append(f)
               .Append(controlId).Append(f)
               .Append('P').Append(f)
               .Append("2.5").Append('\r');

        builder.Append("PID").Append(f).Append('1').Append(f).Append(f)
               .Append(order.PatientId ?? string.Empty).Append(f).Append(f)
               .Append(order.PatientName ?? string.Empty).Append(f).Append(f)
               .Append(order.DateOfBirth?.ToString("yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(f)
               .Append(order.Sex ?? "U").Append('\r');

        builder.Append("ORC").Append(f).Append("NW").Append(f)
               .Append(order.SpecimenId).Append('\r');

        var sequence = 1;
        foreach (var code in order.InstrumentTestCodes)
        {
            builder.Append("OBR").Append(f)
                   .Append(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(f)
                   .Append(order.SpecimenId).Append(f)
                   .Append(order.SpecimenId).Append(f)
                   .Append(code).Append(f).Append(f)
                   .Append(order.CollectedAt is { } collected
                        ? Hl7Message.Timestamp(collected)
                        : Hl7Message.Timestamp(now))
                   .Append('\r');
            sequence++;
        }

        return builder.ToString();
    }

    private sealed class Builder
    {
        public string SpecimenId { get; init; } = string.Empty;
        public string? PatientId { get; init; }
        public string? PatientName { get; init; }
        public string? Priority { get; init; }
        public bool IsControl { get; init; }
        public string? ControlIdentifier { get; init; }
        public DateTimeOffset? CollectedAt { get; init; }
        public DateTimeOffset? ReceivedAt { get; init; }

        public List<InstrumentResult> Results { get; } = [];
        public List<string> Comments { get; } = [];

        public InstrumentSpecimenResults Build() => new()
        {
            SpecimenId = SpecimenId,
            PatientId = PatientId,
            PatientName = PatientName,
            Priority = Priority,
            IsControl = IsControl,
            ControlIdentifier = ControlIdentifier,
            CollectedAt = CollectedAt,
            ReceivedAt = ReceivedAt,
            Results = Results,
            Comments = Comments
        };
    }
}
