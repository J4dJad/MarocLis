using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Migration;

/// <summary>
/// The kinds of data taken over from the laboratory's previous application (« Reprise de
/// données »), in the order they are meant to be imported: a history row needs its patient, an
/// opening balance its patient or payer.
/// </summary>
public enum DataMigrationKind
{
    Patients = 0,
    Prescribers = 1,
    Catalogue = 2,
    History = 3,
    OpeningBalances = 4
}

/// <summary>
/// One run of « Importer »: who, when, from where, and what it did. The journal of the take-over,
/// and what « Annuler la dernière reprise » works from.
/// </summary>
public sealed class MigrationBatch : AuditableAggregateRoot
{
    public const int SourceSystemMaxLength = 60;
    public const int SourceLabelMaxLength = 400;

    private MigrationBatch() { }

    public MigrationBatch(DataMigrationKind kind, string sourceSystem, string sourceLabel, DateTimeOffset startedAt,
                          Guid? userId, string? userName)
    {
        Kind = kind;
        SourceSystem = sourceSystem;
        SourceLabel = sourceLabel.Length > SourceLabelMaxLength ? sourceLabel[..SourceLabelMaxLength] : sourceLabel;
        StartedAt = startedAt;
        UserId = userId;
        UserName = userName;
    }

    public DataMigrationKind Kind { get; private set; }

    /// <summary>The previous application's name, as the operator typed it: the first part of every source key.</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>The file, or the server, database and table or query, the rows were read from (no password).</summary>
    public string SourceLabel { get; private set; } = string.Empty;

    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid? UserId { get; private set; }
    public string? UserName { get; private set; }

    public int RowCount { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>The run stopped before its last row (cancelled or failed): the rows written so far stay, keyed.</summary>
    public bool IsInterrupted { get; private set; }

    public bool IsUndone { get; private set; }
    public DateTimeOffset? UndoneAt { get; private set; }
    public int UndoneCount { get; private set; }

    public void Complete(DateTimeOffset at, bool interrupted)
    {
        CompletedAt = at;
        IsInterrupted = interrupted;
    }

    public void MarkUndone(DateTimeOffset at, int removed)
    {
        IsUndone = true;
        UndoneAt = at;
        UndoneCount = removed;
    }
}

/// <summary>
/// The link between a row of the old application and the record it became here. Unique per kind
/// and source key, which is what makes re-running the same file update rather than duplicate.
/// </summary>
public sealed class MigrationRecord : Entity
{
    public const int SourceKeyMaxLength = 300;
    public const int OldNumberMaxLength = 60;

    private MigrationRecord() { }

    public MigrationRecord(DataMigrationKind kind, string sourceKey, Guid entityId, Guid batchId,
                           bool createdByBatch, DateTimeOffset at)
    {
        Kind = kind;
        SourceKey = sourceKey;
        EntityId = entityId;
        FirstBatchId = batchId;
        LastBatchId = batchId;
        CreatedByBatch = createdByBatch;
        CreatedAt = at;
    }

    public DataMigrationKind Kind { get; private set; }

    /// <summary>« ancienne application | type | ancien identifiant ».</summary>
    public string SourceKey { get; private set; } = string.Empty;

    /// <summary>The patient, prescriber, analysis, history row or opening balance it became.</summary>
    public Guid EntityId { get; private set; }

    public Guid FirstBatchId { get; private set; }
    public Guid LastBatchId { get; set; }

    /// <summary>The first run created the record; false when it was matched to one already here.</summary>
    public bool CreatedByBatch { get; private set; }

    /// <summary>The old application's file number (patients), for « ancien n° → patient ».</summary>
    public string? OldNumber { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// A validated result taken over from the old application: shown with the patient's antériorités
/// (entry, validation, the chart) and flagged « reprise ». Read-only: nothing in MarocLIS edits,
/// validates or reports it; it exists so the history a biologist reads does not start on the day
/// of the switch.
/// </summary>
public sealed class HistoricalResult : Entity, ISiteScoped
{
    public const int ValueMaxLength = 400;
    public const int UnitMaxLength = 40;
    public const int RangeMaxLength = 120;
    public const int AccessionMaxLength = 60;
    public const int CodeMaxLength = 60;

    private HistoricalResult() { }

    public HistoricalResult(Guid patientId, Guid testParameterId, DateTimeOffset resultDate, string valueText,
                            Guid siteId, Guid batchId)
    {
        PatientId = patientId;
        TestParameterId = testParameterId;
        ResultDate = resultDate;
        ValueText = valueText;
        SiteId = siteId;
        BatchId = batchId;
    }

    public Guid PatientId { get; set; }
    public Guid TestParameterId { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>When the old application validated it (the day, at noon, when only a date was given).</summary>
    public DateTimeOffset ResultDate { get; set; }

    public string ValueText { get; set; } = string.Empty;
    public decimal? NumericValue { get; set; }
    public string? ValueOperator { get; set; }

    public string? Unit { get; set; }
    public string? ReferenceRange { get; set; }

    /// <summary>« N° dossier ancien »: the old application's file number, kept searchable.</summary>
    public string? OldAccessionNumber { get; set; }

    /// <summary>The analysis code the old application used.</summary>
    public string? SourceCode { get; set; }

    public Guid BatchId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

/// <summary>
/// What a patient or a payer still owed the laboratory in the old application on the day of the
/// switch. Deliberately not an invoice: an invoice number belongs to the DGI's uninterrupted
/// series, and these debts were invoiced — and numbered — by the old application.
/// </summary>
public sealed class OpeningBalance : AuditableAggregateRoot, ISiteScoped
{
    public const int ReferenceMaxLength = 60;
    public const int LabelMaxLength = 400;

    private OpeningBalance() { }

    public OpeningBalance(Guid? patientId, Guid? payerId, decimal amount, DateOnly asOf, Guid siteId, Guid batchId)
    {
        if (patientId is null && payerId is null)
            throw new DomainException("An opening balance is owed by a patient or a payer.");
        PatientId = patientId;
        PayerId = payerId;
        Amount = amount;
        AsOf = asOf;
        SiteId = siteId;
        BatchId = batchId;
    }

    public Guid? PatientId { get; set; }
    public Guid? PayerId { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>The amount still due, in dirhams.</summary>
    public decimal Amount { get; set; }

    public DateOnly AsOf { get; set; }

    /// <summary>The old application's invoice number, for reference only.</summary>
    public string? OldInvoiceNumber { get; set; }
    public DateOnly? OldInvoiceDate { get; set; }
    public string? Label { get; set; }

    public Guid BatchId { get; set; }
}
