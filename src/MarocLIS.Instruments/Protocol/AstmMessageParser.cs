using System.Globalization;

namespace MarocLIS.Instruments.Protocol;

/// <summary>
/// Delimiters an ASTM sender declares in the second field of its header record. Almost every
/// analyser uses the defaults, but the standard allows them to be redefined and a few do, so they
/// are read from the header rather than assumed.
/// </summary>
public sealed record AstmDelimiters(char Field, char Repeat, char Component, char Escape)
{
    public static readonly AstmDelimiters Default = new('|', '\\', '^', '&');

    /// <summary>
    /// Reads the delimiters from a header record. The field delimiter is the character immediately
    /// after "H"; the next field then declares repeat, component and escape in that order.
    /// </summary>
    public static AstmDelimiters FromHeader(string headerRecord)
    {
        if (headerRecord.Length < 2) return Default;

        var field = headerRecord[1];
        var parts = headerRecord.Split(field);

        if (parts.Length < 2 || parts[1].Length < 3) return Default with { Field = field };

        return new AstmDelimiters(field, parts[1][0], parts[1][1], parts[1][2]);
    }
}

/// <summary>
/// Parses ASTM E1394 records into the normalised <see cref="InstrumentMessage"/>.
/// <para>
/// The parser is forgiving by design. Analyser firmware omits fields the standard calls mandatory,
/// pads sequence numbers inconsistently and puts the test code in whichever component of the
/// universal test identifier the vendor preferred. Rejecting a whole transmission over any of that
/// would mean results sitting unreported on a machine, so anything recoverable becomes a
/// non-blocking issue and only a missing specimen identifier — the join back to the patient — is
/// treated as fatal.
/// </para>
/// </summary>
public static class AstmMessageParser
{
    public static InstrumentMessage Parse(IEnumerable<string> records)
    {
        var list = records.Select(r => r.TrimEnd('\r', '\n')).Where(r => r.Length > 0).ToList();

        if (list.Count == 0)
        {
            return new InstrumentMessage
            {
                Kind = InstrumentMessageKind.Unsupported,
                Issues = [InstrumentParseIssue.Blocking("Instruments_EmptyMessage")]
            };
        }

        var header = list.FirstOrDefault(r => r.StartsWith('H'));
        var delimiters = header is not null ? AstmDelimiters.FromHeader(header) : AstmDelimiters.Default;

        var issues = new List<InstrumentParseIssue>();
        var specimens = new List<InstrumentSpecimenResults>();
        var queried = new List<string>();

        string? senderId = null;
        string? senderVersion = null;
        DateTimeOffset? messageTimestamp = null;

        // State for the specimen currently being assembled. ASTM is a flat record stream where an
        // O record opens a specimen and the following R and C records belong to it.
        SpecimenBuilder? current = null;
        string? pendingPatientId = null;
        string? pendingPatientName = null;

        void CloseCurrent()
        {
            if (current is null) return;
            specimens.Add(current.Build());
            current = null;
        }

        foreach (var record in list)
        {
            var fields = record.Split(delimiters.Field);
            var type = record.Length > 0 ? char.ToUpperInvariant(record[0]) : ' ';

            switch (type)
            {
                case 'H':
                {
                    var sender = Component(Field(fields, 5), delimiters);
                    senderId = sender.ElementAtOrDefault(0);
                    senderVersion = sender.ElementAtOrDefault(1);
                    messageTimestamp = ParseTimestamp(Field(fields, 14));
                    break;
                }

                case 'P':
                {
                    CloseCurrent();
                    // Field 4 is the laboratory-assigned patient identifier; field 3 the practice one.
                    pendingPatientId = Coalesce(Field(fields, 4), Field(fields, 3), Field(fields, 5));
                    var name = Component(Field(fields, 6), delimiters);
                    pendingPatientName = name.Length > 0
                        ? string.Join(' ', name.Where(p => !string.IsNullOrWhiteSpace(p)))
                        : null;
                    break;
                }

                case 'O':
                {
                    CloseCurrent();

                    var specimenId = Coalesce(Field(fields, 3), Field(fields, 4));
                    if (string.IsNullOrWhiteSpace(specimenId))
                    {
                        issues.Add(InstrumentParseIssue.Blocking("Instruments_MissingSpecimenId", record));
                        break;
                    }

                    var actionCode = Field(fields, 12);
                    var specimenDescriptor = Field(fields, 16);

                    current = new SpecimenBuilder
                    {
                        SpecimenId = specimenId!.Trim(),
                        PatientId = pendingPatientId,
                        PatientName = pendingPatientName,
                        RackPosition = Field(fields, 4),
                        Priority = Field(fields, 6),
                        // Action code "Q" marks the specimen as a control; some analysers instead
                        // signal it in the specimen descriptor, so both are checked.
                        IsControl = string.Equals(actionCode, "Q", StringComparison.OrdinalIgnoreCase) ||
                                    (specimenDescriptor?.Contains("CONTROL", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                    (specimenDescriptor?.Contains("QC", StringComparison.OrdinalIgnoreCase) ?? false),
                        ControlIdentifier = string.Equals(actionCode, "Q", StringComparison.OrdinalIgnoreCase)
                            ? Coalesce(specimenDescriptor, specimenId)
                            : null,
                        CollectedAt = ParseTimestamp(Field(fields, 8)),
                        ReceivedAt = ParseTimestamp(Field(fields, 15))
                    };
                    break;
                }

                case 'R':
                {
                    if (current is null)
                    {
                        issues.Add(InstrumentParseIssue.Warning("Instruments_ResultWithoutOrder", record));
                        break;
                    }

                    var testCode = TestCodeFrom(Field(fields, 3), delimiters);
                    if (string.IsNullOrWhiteSpace(testCode))
                    {
                        issues.Add(InstrumentParseIssue.Warning("Instruments_MissingTestCode", record));
                        break;
                    }

                    current.Results.Add(new InstrumentResult
                    {
                        InstrumentTestCode = testCode!,
                        Value = Field(fields, 4)?.Trim(),
                        Unit = Field(fields, 5)?.Trim(),
                        ReferenceRange = Field(fields, 6)?.Trim(),
                        AbnormalFlag = Field(fields, 7)?.Trim(),
                        ResultStatus = Field(fields, 9)?.Trim(),
                        InstrumentOperator = Field(fields, 11)?.Trim(),
                        MeasuredAt = ParseTimestamp(Field(fields, 13)) ?? ParseTimestamp(Field(fields, 12))
                    });
                    break;
                }

                case 'C':
                {
                    var text = Field(fields, 4);
                    if (string.IsNullOrWhiteSpace(text)) break;

                    if (current is null)
                    {
                        break;
                    }

                    // A comment following an R record annotates that result; otherwise the specimen.
                    if (current.Results.Count > 0)
                    {
                        var last = current.Results[^1];
                        current.Results[^1] = last with { Comments = [.. last.Comments, text!.Trim()] };
                    }
                    else
                    {
                        current.Comments.Add(text!.Trim());
                    }
                    break;
                }

                case 'Q':
                {
                    // The analyser is asking what to run. The range start holds the specimen id,
                    // often as "^barcode" or "barcode^".
                    var range = Component(Field(fields, 3), delimiters);
                    var id = range.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
                    if (!string.IsNullOrWhiteSpace(id) && id != "ALL") queried.Add(id!);
                    break;
                }

                case 'L':
                    CloseCurrent();
                    break;

                case 'M':
                    // Manufacturer-specific record. Kept in the raw log; nothing portable to read.
                    break;

                default:
                    issues.Add(InstrumentParseIssue.Warning("Instruments_UnknownRecordType", record));
                    break;
            }
        }

        CloseCurrent();

        var kind = queried.Count > 0 && specimens.Count == 0
            ? InstrumentMessageKind.Query
            : specimens.Count > 0
                ? InstrumentMessageKind.Results
                : InstrumentMessageKind.Unsupported;

        return new InstrumentMessage
        {
            Kind = kind,
            SenderId = senderId,
            SenderVersion = senderVersion,
            MessageTimestamp = messageTimestamp,
            Specimens = specimens,
            QueriedSpecimenIds = queried,
            Issues = issues,
            RawText = string.Join("\r", list)
        };
    }

    /// <summary>
    /// Serialises orders into ASTM records for download to an analyser, in response to a query.
    /// </summary>
    public static IReadOnlyList<string> BuildOrderDownload(
        IReadOnlyList<InstrumentOrderDownload> orders,
        string senderId,
        DateTimeOffset now,
        AstmDelimiters? delimiters = null)
    {
        var d = delimiters ?? AstmDelimiters.Default;
        var f = d.Field;
        var c = d.Component;

        var records = new List<string>
        {
            // H|\^&|||sender|||||||P|1|timestamp
            $"H{f}{d.Repeat}{c}{d.Escape}{f}{f}{f}{senderId}{f}{f}{f}{f}{f}{f}P{f}1{f}{Timestamp(now)}"
        };

        var sequence = 1;

        foreach (var order in orders)
        {
            records.Add(string.Join(f,
                "P",
                sequence.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                order.PatientId ?? string.Empty,
                string.Empty,
                order.PatientName ?? string.Empty,
                string.Empty,
                order.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
                order.Sex ?? "U"));

            // The universal test identifier repeats once per requested test, as ^^^CODE.
            var tests = string.Join(d.Repeat,
                order.InstrumentTestCodes.Select(code => $"{c}{c}{c}{code}"));

            records.Add(string.Join(f,
                "O",
                "1",
                order.SpecimenId,
                order.RackPosition ?? string.Empty,
                tests,
                order.Priority,
                Timestamp(now),
                order.CollectedAt is { } collected ? Timestamp(collected) : string.Empty,
                string.Empty, string.Empty, string.Empty,
                "N",                                      // action code: new order
                string.Empty,
                order.ClinicalInformation ?? string.Empty,
                string.Empty,
                order.SpecimenType ?? string.Empty));

            sequence++;
        }

        records.Add($"L{f}1{f}N");
        return records;
    }

    /// <summary>Formats a timestamp in the ASTM <c>yyyyMMddHHmmss</c> form.</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an ASTM timestamp. Accepts the full <c>yyyyMMddHHmmss</c> and the truncated forms
    /// analysers actually send, down to a bare date.
    /// <para>
    /// ASTM E1394 carries no time zone, and the analyser sending the record is on the bench in the
    /// same room as this workstation — so the digits are local wall-clock time and are read as
    /// such. Reading them as UTC and converting, which is what this did, moved every measurement
    /// and collection time by the local offset: a 10:30 result was filed as 11:30 in winter and
    /// 12:30 in summer. That lands in the patient's record, on the report, and in the
    /// turnaround-time statistics. Same treatment as
    /// <see cref="Hl7Message.ParseTimestamp"/> gives a stamp with no explicit offset.
    /// </para>
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new string(value.Where(char.IsDigit).ToArray());

        string[] formats = ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMddHH", "yyyyMMdd"];

        foreach (var format in formats)
        {
            if (digits.Length >= format.Length &&
                DateTime.TryParseExact(digits[..format.Length], format, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var parsed))
                return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        }

        return null;
    }

    /// <summary>1-based field accessor, so the code reads like the standard's field numbering.</summary>
    private static string? Field(string[] fields, int oneBasedIndex) =>
        oneBasedIndex >= 1 && oneBasedIndex <= fields.Length
            ? fields[oneBasedIndex - 1] is { Length: > 0 } value ? value : null
            : null;

    private static string[] Component(string? field, AstmDelimiters delimiters) =>
        string.IsNullOrEmpty(field) ? [] : field.Split(delimiters.Component);

    /// <summary>
    /// Pulls the test code out of a universal test identifier. The standard puts the local code in
    /// the fourth component, but a good share of analysers send it in the first, so the fourth is
    /// preferred and the first non-empty component is the fallback.
    /// </summary>
    private static string? TestCodeFrom(string? field, AstmDelimiters delimiters)
    {
        var components = Component(field, delimiters);
        if (components.Length == 0) return null;

        if (components.Length >= 4 && !string.IsNullOrWhiteSpace(components[3]))
            return components[3].Trim();

        return components.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
    }

    private static string? Coalesce(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private sealed class SpecimenBuilder
    {
        public string SpecimenId { get; init; } = string.Empty;
        public string? PatientId { get; init; }
        public string? PatientName { get; init; }
        public string? RackPosition { get; init; }
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
            RackPosition = RackPosition,
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
