namespace MarocLIS.Api.Contracts;

/// <summary>The biological validation worklist under one chip, with the count of every chip.</summary>
public sealed record WorklistResponse(string Filter, FilterCounts Counts, IReadOnlyList<WorklistEntry> Entries);

public sealed record FilterCounts(int All, int Urgent, int Abnormal, int QcBlocked);

/// <summary>A pill of the « Signalements » column, already in the biologist's language.</summary>
public sealed record AlertPill(string Tone, string Text);

/// <summary>One file awaiting biological validation.</summary>
/// <param name="Priority">Routine, Urgent or VitalEmergency.</param>
/// <param name="Gender">Male, Female or Unknown, as the domain names it.</param>
/// <param name="Alerts">Most pressing first, as the workstation orders them.</param>
public sealed record WorklistEntry(
    Guid OrderId,
    string AccessionNumber,
    string PatientName,
    int? AgeYears,
    string Gender,
    string Priority,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? DueAt,
    bool IsOverdue,
    int AwaitingCount,
    int AbnormalCount,
    int CriticalCount,
    int UncommunicatedCriticalCount,
    bool HasQcFailure,
    IReadOnlyList<AlertPill> Alerts);

public sealed record PatientSummary(
    string Name,
    string? NameArabic,
    string Gender,
    int? AgeYears,
    DateOnly? DateOfBirth,
    bool IsPregnant);

/// <summary>A line of the « Contexte » card: a badge in a pill, then a sentence.</summary>
public sealed record ContextLine(string Tone, string Badge, string Text);

/// <summary>An open file: its patient, its context and the rows the biologist decides on.</summary>
/// <param name="CanIssueReport">« Compte rendu » is offered: a value of the file is validated and not yet reported.</param>
public sealed record OrderFile(
    Guid OrderId,
    string AccessionNumber,
    PatientSummary Patient,
    string Priority,
    string? ClinicalInformation,
    DateTimeOffset? DueAt,
    bool CanIssueReport,
    IReadOnlyList<ContextLine> Context,
    IReadOnlyList<ResultRow> Rows);

/// <summary>One row of an open file.</summary>
/// <param name="Interpretation">Normal, Low, High, CriticalLow, CriticalHigh, Abnormal or NotInterpretable.</param>
/// <param name="Flags">The names of the result's flags (QcOutOfControl, DeltaCheckFailed…).</param>
/// <param name="Indicator">The glyphs of the workstation's narrow column: ↑ ↓ ↑↑ ↓↓ *, or CIQ Δ ! × R ST ≷.</param>
/// <param name="Status">Entered, TechnicallyValidated, Validated…: only a technically validated value can be ticked.</param>
/// <param name="EnteredAt">
/// When the value shown was stored. Sent back with a decision or a critical call
/// (<c>displayedEnteredAt</c>): a value stored since is refused, and the phone reloads the file.
/// </param>
/// <param name="CanTick">The value can be released now.</param>
/// <param name="PreTicked">The row comes ticked: the biologist unticks what they hold back.</param>
public sealed record ResultRow(
    Guid ResultId,
    Guid ParameterId,
    string AnalysisCode,
    string AnalysisName,
    string ParameterName,
    bool IsHeading,
    int IndentLevel,
    string? Value,
    string? Unit,
    string? ReferenceRange,
    string Interpretation,
    IReadOnlyList<string> Flags,
    string Indicator,
    bool IsAbnormal,
    decimal? PreviousValue,
    DateOnly? PreviousDate,
    bool PreviousImported,
    string? InstrumentFlags,
    bool IsCritical,
    bool CriticalCommunicated,
    string Status,
    DateTimeOffset? EnteredAt,
    bool CanTick,
    bool PreTicked);

/// <summary>A point of the antériorité chart: a validated value of the same parameter.</summary>
public sealed record HistoryPoint(
    DateTimeOffset ValidatedAt,
    decimal Value,
    string? ValueOperator,
    string AccessionNumber,
    bool Imported);

public sealed record ValidationRequest(IReadOnlyList<ValidationDecisionRequest> Decisions);

/// <summary>The biologist's decision on one row.</summary>
/// <param name="Validate">True releases the value; false holds it back.</param>
/// <param name="Comment">The biologist's comment, written onto the result if it is released.</param>
/// <param name="QcJustification">Required to release a value covered by an out-of-control QC run.</param>
/// <param name="DisplayedEnteredAt">The row's <see cref="ResultRow.EnteredAt"/> as displayed; required to release.</param>
public sealed record ValidationDecisionRequest(
    Guid ResultId,
    bool Validate,
    string? Comment = null,
    string? QcJustification = null,
    DateTimeOffset? DisplayedEnteredAt = null);

/// <summary>What the validation did.</summary>
/// <param name="Blocked">Each refusal, in the biologist's language: each names a gate to clear.</param>
/// <param name="ValueChanged">A value changed since it was displayed: the phone reloads the file.</param>
public sealed record ValidationOutcomeResponse(
    int Validated,
    int Refused,
    IReadOnlyList<string> Blocked,
    bool OrderFullyValidated,
    bool ValueChanged);
