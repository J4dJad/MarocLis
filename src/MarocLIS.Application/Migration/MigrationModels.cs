using MarocLIS.Domain.Migration;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Migration;

/// <summary>What « Importer » would do with a row, and did.</summary>
public enum MigrationOutcome
{
    Create = 0,
    Update = 1,

    /// <summary>Already here and nothing to add, or set aside on purpose (« Ignorer », unconfirmed match).</summary>
    Skip = 2,

    Error = 3
}

public enum MigrationSeverity
{
    /// <summary>For the record: a patient found by CIN, a default section used.</summary>
    Info = 0,

    /// <summary>The row is imported, a value is left out or deserves a look.</summary>
    Warning = 1,

    /// <summary>The row is not imported.</summary>
    Error = 2,

    /// <summary>A source value with no correspondence: nothing is imported until it has one.</summary>
    Blocking = 3
}

/// <summary>A line of the « Rapport d'anomalies »: row, field, problem, suggestion — each a resource key and its arguments.</summary>
public sealed record MigrationAnomaly(
    int Row,
    string? FieldKey,
    MigrationSeverity Severity,
    string ProblemKey,
    object?[] ProblemArgs,
    string? SuggestionKey = null,
    object?[]? SuggestionArgs = null);

/// <summary>One source row as the import will apply it.</summary>
public sealed class PlannedRow
{
    public required int Row { get; init; }
    public MigrationOutcome Outcome { get; set; }
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>The record it updates (found through its source key, or matched by CIN, INPE, name…).</summary>
    public Guid? ExistingId { get; set; }

    /// <summary>Whether the existing record was found through a previous run's source key.</summary>
    public bool KnownSourceKey { get; set; }

    /// <summary>The validated values, typed, by field key (and a few derived keys such as « PatientId »).</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => Values.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public string? Text(string key) => Values.TryGetValue(key, out var value) ? value as string : null;
}

/// <summary>The result of « Simulation »: every row's plan, the anomalies, and the counts.</summary>
public sealed class MigrationAnalysis
{
    public required DataMigrationKind Kind { get; init; }
    public required IReadOnlyList<PlannedRow> Rows { get; init; }
    public required IReadOnlyList<MigrationAnomaly> Anomalies { get; init; }

    public int ToCreate => Rows.Count(r => r.Outcome == MigrationOutcome.Create);
    public int ToUpdate => Rows.Count(r => r.Outcome == MigrationOutcome.Update);
    public int Skipped => Rows.Count(r => r.Outcome == MigrationOutcome.Skip);
    public int InError => Rows.Count(r => r.Outcome == MigrationOutcome.Error);

    /// <summary>Source values without a correspondence: « Importer » is refused while there is one.</summary>
    public bool IsBlocked => Anomalies.Any(a => a.Severity == MigrationSeverity.Blocking);
}

/// <summary>What the operator asks for: the rows read, how their columns and values map, the options.</summary>
public sealed class MigrationRequest
{
    public required DataMigrationKind Kind { get; init; }

    /// <summary>The previous application's name: the first part of every source key, so a re-run finds its rows.</summary>
    public required string SourceSystem { get; init; }

    /// <summary>The file, or the server, database and table or query (never a password), for the journal.</summary>
    public required string SourceLabel { get; init; }

    public required SourceTable Table { get; init; }

    /// <summary>Field key → source column name.</summary>
    public required IReadOnlyDictionary<string, string> Columns { get; init; }

    public CorrespondenceTables Correspondences { get; init; } = new();

    /// <summary>A patient matched only by name and birth date is linked (true) or set aside (false).</summary>
    public bool LinkByNameAndBirthDate { get; init; } = true;

    /// <summary>Rows written per transaction.</summary>
    public int BatchSize { get; init; } = 200;
}

/// <summary>How far an analysis or an import has gone, for the progress bar.</summary>
public sealed record MigrationProgress(int Done, int Total, string StepKey);

/// <summary>What « Importer » did.</summary>
public sealed record MigrationSummary(
    Guid BatchId, DataMigrationKind Kind, int Rows, int Created, int Updated, int Skipped, int Failed,
    bool Interrupted, IReadOnlyList<MigrationAnomaly> Anomalies);

/// <summary>A run of the journal of the take-over.</summary>
public sealed record MigrationBatchRow(
    Guid Id, DataMigrationKind Kind, string SourceSystem, string SourceLabel, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, string? UserName, int Rows, int Created, int Updated, int Skipped, int Failed,
    bool Interrupted, bool Undone, DateTimeOffset? UndoneAt);

/// <summary>What « Annuler la dernière reprise » removed, and what it had to leave.</summary>
public sealed record MigrationUndoOutcome(Guid BatchId, DataMigrationKind Kind, int Removed, int Kept);

/// <summary>A patient found by an old number, and the antériorités reprises carrying it.</summary>
public sealed record OldNumberMatch(Guid PatientId, string RecordNumber, string FullName, DateOnly? DateOfBirth,
                                    string? OldNumber, IReadOnlyList<HistoricalResultRow> History);

public sealed record HistoricalResultRow(Guid Id, DateTimeOffset Date, string ParameterCode, string ParameterName,
                                         string Value, string? Unit, string? ReferenceRange, string? OldAccessionNumber);

public sealed record OpeningBalanceRow(Guid Id, DateOnly AsOf, string? PatientRecordNumber, string? PatientName,
                                       string? PayerCode, string? PayerName, decimal Amount, string? OldInvoiceNumber,
                                       DateOnly? OldInvoiceDate, string? Label);

// ---- What the planner reads from the database --------------------------------------------------

public sealed record PatientSnapshot(
    Guid Id, string RecordNumber, string LastName, string FirstName, DateOnly? DateOfBirth, Gender Gender,
    string? Cin, string? MobilePhone, string? AlternatePhone, string? Email, string? AddressLine, string? City,
    string? LastNameArabic, string? FirstNameArabic, string? GuardianFullName,
    IReadOnlyList<(Guid PayerId, string MembershipNumber)> Coverages);

public sealed record PrescriberSnapshot(
    Guid Id, string LastName, string FirstName, string? Inpe, string? ProfessionalOrderNumber, string? Phone,
    string? MobilePhone, string? Email, string? AddressLine, string? City, string? InstitutionName, string? Speciality);

public sealed record AnalysisSnapshot(
    Guid Id, string Code, string Name, string? NationalActCode, decimal? Price, decimal? Coefficient, string? LetterKey);

public sealed record HistorySnapshot(Guid Id, DateTimeOffset Date, string ValueText, string? Unit, string? ReferenceRange,
                                     string? OldAccessionNumber);

public sealed record BalanceSnapshot(Guid Id, decimal Amount, DateOnly AsOf);

/// <summary>
/// Everything the planner compares the rows with, read once per analysis: the source keys of
/// earlier runs, the records rows may match, and the values coded fields may take.
/// </summary>
public sealed class MigrationContext
{
    /// <summary>Source key → record, for this kind.</summary>
    public IReadOnlyDictionary<string, Guid> Records { get; init; } = new Dictionary<string, Guid>();

    /// <summary>Source key of a patient (« système|Patients|id ») → patient, for history and balances.</summary>
    public IReadOnlyDictionary<string, Guid> PatientRecords { get; init; } = new Dictionary<string, Guid>();

    /// <summary>Old file number (normalised) → patient.</summary>
    public IReadOnlyDictionary<string, Guid> OldNumbers { get; init; } = new Dictionary<string, Guid>();

    public IReadOnlyList<PatientSnapshot> Patients { get; init; } = [];
    public IReadOnlyList<PrescriberSnapshot> Prescribers { get; init; } = [];
    public IReadOnlyList<AnalysisSnapshot> Analyses { get; init; } = [];

    /// <summary>Existing history rows and balances by their record id, to tell an update from a repeat.</summary>
    public IReadOnlyDictionary<Guid, HistorySnapshot> History { get; init; } = new Dictionary<Guid, HistorySnapshot>();
    public IReadOnlyDictionary<Guid, BalanceSnapshot> Balances { get; init; } = new Dictionary<Guid, BalanceSnapshot>();

    /// <summary>The acts of the national nomenclature (normalised codes).</summary>
    public IReadOnlySet<string> NationalActs { get; init; } = new HashSet<string>();

    /// <summary>The current value of the B letter key, to price an analysis given only its coefficient.</summary>
    public decimal? LetterKeyValue { get; init; }

    /// <summary>Payer code → payer.</summary>
    public IReadOnlyDictionary<string, Guid> PayerIds { get; init; } = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<MigrationCodeList, IReadOnlyList<CodeTarget>> Targets { get; init; } =
        new Dictionary<MigrationCodeList, IReadOnlyList<CodeTarget>>();

    /// <summary>The section and specimen an analysis created without them is filed under.</summary>
    public string? DefaultDepartmentCode { get; init; }
    public string? DefaultSpecimenCode { get; init; }

    public DateOnly Today { get; init; }
}

/// <summary>A write the store had to abandon (a closed numbering series…), as a resource key.</summary>
public sealed class DataMigrationException(string resourceKey, params object?[] args) : Exception(resourceKey)
{
    public string ResourceKey { get; } = resourceKey;
    public object?[] Args { get; } = args;
}
