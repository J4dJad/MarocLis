using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Administration;

/// <summary>
/// What the audit screens filter by.
/// <para>
/// A date range is required rather than optional. These three tables are the fastest-growing in the
/// database — the change log gets a row per modified entity per save — so an unbounded query is one
/// that eventually locks a reception desk out of its own workstation.
/// </para>
/// </summary>
public sealed record AuditTrailFilter
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }

    /// <summary>Matches the user name captured on the row, not the current account name.</summary>
    public string? UserName { get; init; }

    /// <summary>Free text across the row's descriptive columns.</summary>
    public string? Search { get; init; }

    /// <summary>Narrows the change log to one entity's history.</summary>
    public Guid? EntityId { get; init; }

    /// <summary>Narrows to one patient — the shape a data-subject access request takes.</summary>
    public Guid? PatientId { get; init; }

    /// <summary>
    /// Narrows to one patient by the number printed on their file, which is the identifier the
    /// person standing at the counter can actually quote.
    /// </summary>
    public string? PatientRecordNumber { get; init; }

    /// <summary>
    /// Hard ceiling on rows returned. The screen says when it was hit, because a truncated audit
    /// extract that looks complete is worse than no extract.
    /// </summary>
    public int MaxRows { get; init; } = 500;
}

public sealed record AuditEntryRow(
    Guid AuditEntryId, DateTimeOffset OccurredAt, string EntityName, Guid EntityId,
    AuditOperation Operation, string? UserName, string? Workstation, string? ChangedProperties,
    string? Changes, string? Reason, Guid? PatientId, string? PatientRecordNumber,
    string? AggregateName, Guid? CorrelationId);

public sealed record DataAccessRow(
    Guid DataAccessLogId, DateTimeOffset OccurredAt, string? UserName, DataAccessKind Kind,
    Guid? PatientId, string? PatientRecordNumber, int? RecordCount, string? QueryDescription,
    string? Context, string? Workstation, bool IsBreakGlass, string? BreakGlassJustification);

// SecurityEventRow is declared beside the user-administration rows, where it was already defined
// for that screen's recent-events panel, and extended there rather than duplicated here.

/// <summary>A page of audit rows, with whether the ceiling cut it short.</summary>
public sealed record AuditPage<TRow>(IReadOnlyList<TRow> Rows, bool Truncated);

/// <summary>
/// Read access to the traceability record.
/// <para>
/// The three tables this reads were written meticulously and could not be read by the application
/// at all: an auditor asking "who opened this patient's file?" had no answer short of a DBA with
/// SSMS. Both loi n° 08-09, which makes the laboratory accountable for showing who consulted a
/// file, and ISO 15189 §5.8, which requires the change history of a result to be retrievable,
/// expect the controller to be able to produce that from the system holding the data.
/// </para>
/// <para>
/// Read-only by construction. There is no write path here and there is none anywhere else either:
/// the tables carry <c>INSTEAD OF UPDATE, DELETE</c> triggers installed by the initial migration,
/// so even this service's own SQL login cannot alter what it is showing.
/// </para>
/// </summary>
public sealed class AuditTrailService(
    IAuditQueries queries,
    IPermissionGuard guard,
    IDataAccessAuditor accessAuditor,
    IClock clock)
{
    /// <summary>The recorded change history — who altered what, and from which value to which.</summary>
    public async Task<Result<AuditPage<AuditEntryRow>>> GetChangesAsync(
        AuditTrailFilter filter, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ViewAuditTrail);
        if (permitted.IsFailure)
            return Result.Failure<AuditPage<AuditEntryRow>>(permitted.FirstError);

        var invalid = Validate(filter);
        if (invalid is not null) return Result.Failure<AuditPage<AuditEntryRow>>(invalid);

        var rows = await queries.GetChangesAsync(filter, ct);

        await LogConsultationAsync(filter, rows.Count, "Journal des modifications", ct);

        return Result.Success(Page(rows, filter));
    }

    /// <summary>
    /// The consultation log — who read personal data, and whether it was a break-glass access.
    /// </summary>
    public async Task<Result<AuditPage<DataAccessRow>>> GetDataAccessAsync(
        AuditTrailFilter filter, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ViewAuditTrail);
        if (permitted.IsFailure)
            return Result.Failure<AuditPage<DataAccessRow>>(permitted.FirstError);

        var invalid = Validate(filter);
        if (invalid is not null) return Result.Failure<AuditPage<DataAccessRow>>(invalid);

        var rows = await queries.GetDataAccessAsync(filter, ct);

        await LogConsultationAsync(filter, rows.Count, "Journal des consultations", ct);

        return Result.Success(Page(rows, filter));
    }

    /// <summary>
    /// Logins, lockouts and privilege changes.
    /// <para>
    /// Not logged as a personal-data access: a login record concerns an operator, not a patient,
    /// and recording every glance at the security log as a health-data consultation would bury the
    /// rows that are one.
    /// </para>
    /// </summary>
    public async Task<Result<AuditPage<SecurityEventRow>>> GetSecurityEventsAsync(
        AuditTrailFilter filter, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ViewAuditTrail);
        if (permitted.IsFailure)
            return Result.Failure<AuditPage<SecurityEventRow>>(permitted.FirstError);

        var invalid = Validate(filter);
        if (invalid is not null) return Result.Failure<AuditPage<SecurityEventRow>>(invalid);

        return Result.Success(Page(await queries.GetSecurityEventsAsync(filter, ct), filter));
    }

    /// <summary>
    /// Every consultation of one patient's file, for answering a data-subject access request under
    /// loi n° 08-09.
    /// <para>
    /// Identified by the medical record number rather than by an internal id, because the person
    /// exercising the right is standing at the counter quoting the number printed on their file.
    /// </para>
    /// </summary>
    public async Task<Result<AuditPage<DataAccessRow>>> GetPatientAccessHistoryAsync(
        string medicalRecordNumber, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(medicalRecordNumber))
            return Result.Failure<AuditPage<DataAccessRow>>(
                Error.Validation("Audit_RecordNumberRequired"));

        var filter = new AuditTrailFilter
        {
            From = from,
            To = to,
            PatientRecordNumber = medicalRecordNumber.Trim(),
            // Answered in full. A truncated answer to a statutory access request is not an answer,
            // and the requester has no way of telling that it was cut short.
            MaxRows = 10_000
        };

        return await GetDataAccessAsync(filter, ct);
    }

    private static Error? Validate(AuditTrailFilter filter)
    {
        if (filter.To < filter.From) return Error.Validation("Audit_DateRangeInverted");

        // Thirteen months covers a full year plus the overlap an auditor asks for, and keeps a
        // single query off the whole table.
        if (filter.From.AddMonths(13) < filter.To) return Error.Validation("Audit_DateRangeTooWide");

        return null;
    }

    private static AuditPage<TRow> Page<TRow>(IReadOnlyList<TRow> rows, AuditTrailFilter filter) =>
        // The query asks for one more row than the ceiling precisely so this can tell the
        // difference between "exactly 500 results" and "at least 500 results".
        rows.Count > filter.MaxRows
            ? new AuditPage<TRow>([.. rows.Take(filter.MaxRows)], Truncated: true)
            : new AuditPage<TRow>(rows, Truncated: false);

    /// <summary>
    /// Reading the traceability record is itself an access to personal data when it concerns a
    /// patient, so it is logged like any other consultation. An audit screen exempt from auditing
    /// would be the one blind spot in the system.
    /// </summary>
    private async Task LogConsultationAsync(AuditTrailFilter filter, int rowCount, string context,
                                              CancellationToken ct)
    {
        var narrowedToOnePatient = filter.PatientId is not null
                                   || !string.IsNullOrWhiteSpace(filter.PatientRecordNumber);

        var kind = narrowedToOnePatient
            ? DataAccessKind.SubjectAccessRequest
            : DataAccessKind.Search;

        await accessAuditor.LogAsync(kind, filter.PatientId, recordCount: rowCount,
                                      queryDescription:
                                          $"{context} : {filter.From:yyyy-MM-dd} → {filter.To:yyyy-MM-dd}",
                                      ct: ct);
    }

    /// <summary>Today and the thirty days before it — what the screen opens on.</summary>
    public AuditTrailFilter DefaultFilter() => new()
    {
        From = clock.Today.AddDays(-30),
        To = clock.Today
    };
}

/// <summary>Read-side queries for the audit screens.</summary>
public interface IAuditQueries
{
    Task<IReadOnlyList<AuditEntryRow>> GetChangesAsync(AuditTrailFilter filter,
                                                        CancellationToken ct = default);

    Task<IReadOnlyList<DataAccessRow>> GetDataAccessAsync(AuditTrailFilter filter,
                                                            CancellationToken ct = default);

    Task<IReadOnlyList<SecurityEventRow>> GetSecurityEventsAsync(AuditTrailFilter filter,
                                                                   CancellationToken ct = default);
}
