namespace MarocLIS.Instruments;

/// <summary>
/// A result as an analyser reports it, before the LIS has decided what it means.
/// <para>
/// Deliberately all strings: an analyser is free to send "&lt;0.01", "SEE COMMENT", "1:320" or a
/// number with an instrument-specific flag attached, and coercing that to a decimal at the wire
/// boundary loses information the biologist needs. Interpretation happens in the application layer,
/// against the catalogue, where the parameter's declared value type is known.
/// </para>
/// </summary>
public sealed record InstrumentResult
{
    /// <summary>Test code as the analyser knows it; mapped to a catalogue parameter by the driver.</summary>
    public required string InstrumentTestCode { get; init; }

    /// <summary>The value exactly as transmitted.</summary>
    public string? Value { get; init; }

    public string? Unit { get; init; }

    /// <summary>Instrument abnormality flag: "H", "L", "HH", "LL", "&gt;", "&lt;", or vendor-specific.</summary>
    public string? AbnormalFlag { get; init; }

    /// <summary>Reference interval the analyser reported, kept for comparison with the catalogue's.</summary>
    public string? ReferenceRange { get; init; }

    /// <summary>Result status as sent: F final, P preliminary, C correction, X cannot be done.</summary>
    public string? ResultStatus { get; init; }

    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>Dilution the analyser applied, when it reports one.</summary>
    public decimal? DilutionFactor { get; init; }

    /// <summary>Operator identifier on the analyser side, if transmitted.</summary>
    public string? InstrumentOperator { get; init; }

    /// <summary>Comments attached to this result by the analyser.</summary>
    public IReadOnlyList<string> Comments { get; init; } = [];

    /// <summary>True when the analyser marked the result as unusable ("X" status or equivalent).</summary>
    public bool IsNotDone =>
        string.Equals(ResultStatus, "X", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the analyser marked the value as a correction of one already sent.</summary>
    public bool IsCorrection =>
        string.Equals(ResultStatus, "C", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One specimen's worth of results as received from an analyser. A single transmission commonly
/// carries several of these.
/// </summary>
public sealed record InstrumentSpecimenResults
{
    /// <summary>
    /// Specimen identifier the analyser read from the tube's barcode. This is the join back to the
    /// LIS, and the only field whose absence makes the whole message unusable.
    /// </summary>
    public string? SpecimenId { get; init; }

    /// <summary>Patient identifier, when the analyser was given one. Used only as a cross-check.</summary>
    public string? PatientId { get; init; }
    public string? PatientName { get; init; }

    /// <summary>Position on the analyser's rack or carousel, useful when tracing a mix-up.</summary>
    public string? RackPosition { get; init; }

    /// <summary>
    /// True when this is a quality-control specimen rather than a patient one. Control results go
    /// to the QC module, never into a patient file — telling them apart at the boundary is what
    /// stops a control value being reported as a patient's.
    /// </summary>
    public bool IsControl { get; init; }

    /// <summary>Control material identifier or lot number, when this is a control specimen.</summary>
    public string? ControlIdentifier { get; init; }

    public DateTimeOffset? CollectedAt { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>Priority as the analyser reported it: "S" stat, "R" routine.</summary>
    public string? Priority { get; init; }

    public IReadOnlyList<InstrumentResult> Results { get; init; } = [];

    public IReadOnlyList<string> Comments { get; init; } = [];
}

/// <summary>A complete inbound transmission, parsed and ready for the application layer.</summary>
public sealed record InstrumentMessage
{
    public InstrumentMessageKind Kind { get; init; } = InstrumentMessageKind.Results;

    /// <summary>Sender identification from the message header.</summary>
    public string? SenderId { get; init; }
    public string? SenderVersion { get; init; }
    public DateTimeOffset? MessageTimestamp { get; init; }

    /// <summary>Message control identifier, echoed in the acknowledgement for HL7.</summary>
    public string? ControlId { get; init; }

    public IReadOnlyList<InstrumentSpecimenResults> Specimens { get; init; } = [];

    /// <summary>
    /// Specimen identifiers the analyser is asking the LIS for, when the message is a query. The
    /// analyser has read a barcode and wants to know which tests to run on it.
    /// </summary>
    public IReadOnlyList<string> QueriedSpecimenIds { get; init; } = [];

    /// <summary>Problems found while parsing. A message can be partly usable, so these are data.</summary>
    public IReadOnlyList<InstrumentParseIssue> Issues { get; init; } = [];

    /// <summary>The raw text, stored verbatim in the message log for diagnosis and replay.</summary>
    public string? RawText { get; init; }

    public bool HasBlockingIssues => Issues.Any(i => i.IsBlocking);
}

public enum InstrumentMessageKind
{
    /// <summary>Results transmitted by the analyser.</summary>
    Results = 0,

    /// <summary>The analyser is asking which tests to run on a specimen it has scanned.</summary>
    Query = 1,

    /// <summary>An acknowledgement.</summary>
    Acknowledgement = 2,

    /// <summary>Status or error reported by the analyser.</summary>
    Status = 3,

    /// <summary>Recognised as a valid frame but of no kind this driver handles.</summary>
    Unsupported = 9
}

/// <summary>A problem found while parsing, with a resource key so it can be shown in any language.</summary>
public sealed record InstrumentParseIssue(string ResourceKey, string? Detail, bool IsBlocking)
{
    public static InstrumentParseIssue Blocking(string key, string? detail = null) => new(key, detail, true);
    public static InstrumentParseIssue Warning(string key, string? detail = null) => new(key, detail, false);
}

/// <summary>
/// A test order sent down to an analyser in response to a query, or pushed ahead of the tube.
/// Keeps the driver independent of the domain model: the application layer projects orders onto
/// this shape, and the driver serialises it into whichever protocol the analyser speaks.
/// </summary>
public sealed record InstrumentOrderDownload
{
    public required string SpecimenId { get; init; }

    /// <summary>Analyser-side test codes to run, already translated from catalogue codes.</summary>
    public required IReadOnlyList<string> InstrumentTestCodes { get; init; }

    public string? PatientId { get; init; }
    public string? PatientName { get; init; }

    /// <summary>"M", "F" or "U" — several assays select their own calculation by sex.</summary>
    public string? Sex { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    /// <summary>"S" for stat, "R" for routine.</summary>
    public string Priority { get; init; } = "R";

    public string? SpecimenType { get; init; }
    public DateTimeOffset? CollectedAt { get; init; }

    /// <summary>Clinical information some analysers print on their own report.</summary>
    public string? ClinicalInformation { get; init; }

    /// <summary>Rack and position, for analysers that are told where the tube is.</summary>
    public string? RackPosition { get; init; }
}
