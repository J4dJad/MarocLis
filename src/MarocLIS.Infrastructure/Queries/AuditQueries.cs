using MarocLIS.Application.Administration;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// Reads the three traceability tables.
/// <para>
/// Every query here takes one row more than the caller's ceiling, so the service can tell "exactly
/// N results" from "at least N results" and say so on screen. A truncated audit extract that
/// presents itself as complete is the one outcome worse than no extract.
/// </para>
/// <para>
/// Patient identity is resolved with <c>IgnoreQueryFilters</c>. The audit trail outlives the
/// records it describes: a patient may since have been soft-deleted or pseudonymised under the
/// right to erasure, and a confidentiality review that silently dropped those rows would hide
/// exactly the accesses most worth reviewing. The rows are reachable only with
/// <c>admin.audit.view</c>, and reading them is itself logged.
/// </para>
/// </summary>
public sealed class AuditQueries(LisDbContext db) : IAuditQueries
{
    public async Task<IReadOnlyList<AuditEntryRow>> GetChangesAsync(
        AuditTrailFilter filter, CancellationToken ct = default)
    {
        var (from, to) = Window(filter);

        var query = db.AuditEntries.AsNoTracking()
                      .Where(a => a.OccurredAt >= from && a.OccurredAt < to);

        if (filter.EntityId is { } entityId)
            query = query.Where(a => a.EntityId == entityId || a.AggregateId == entityId);

        if (filter.PatientId is { } patientId)
            query = query.Where(a => a.PatientId == patientId);

        if (!string.IsNullOrWhiteSpace(filter.UserName))
        {
            var user = filter.UserName.Trim();
            query = query.Where(a => a.UserName != null && a.UserName.Contains(user));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();

            // Not inside the payloads of personnel and payroll rows: searching them for "8500"
            // would find whose salary it is, even with the values masked on display.
            var payEntities = AuditRedaction.EntityNames.ToList();

            query = query.Where(a => a.EntityName.Contains(search)
                                     || (a.ChangedProperties != null
                                         && a.ChangedProperties.Contains(search))
                                     || (a.Reason != null && a.Reason.Contains(search))
                                     || (a.Changes != null && !payEntities.Contains(a.EntityName)
                                         && a.Changes.Contains(search)));
        }

        var rows = await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(filter.MaxRows + 1)
            .Select(a => new
            {
                a.Id, a.OccurredAt, a.EntityName, a.EntityId, a.Operation, a.UserName,
                a.Workstation, a.ChangedProperties, a.Changes, a.Reason, a.PatientId,
                a.AggregateName, a.CorrelationId,
                PatientRecordNumber = a.PatientId == null
                    ? null
                    : db.Patients.IgnoreQueryFilters()
                        .Where(p => p.Id == a.PatientId)
                        .Select(p => p.MedicalRecordNumber)
                        .FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(a => new AuditEntryRow(
                a.Id, a.OccurredAt, a.EntityName, a.EntityId, a.Operation, a.UserName,
                a.Workstation, a.ChangedProperties,
                // Rows written before redaction still hold the values; masked here, not rewritten.
                AuditRedaction.Mask(a.EntityName, a.Changes),
                a.Reason, a.PatientId, a.PatientRecordNumber, a.AggregateName, a.CorrelationId))
        ];
    }

    public async Task<IReadOnlyList<DataAccessRow>> GetDataAccessAsync(
        AuditTrailFilter filter, CancellationToken ct = default)
    {
        var (from, to) = Window(filter);

        var query = db.DataAccessLogs.AsNoTracking()
                      .Where(l => l.OccurredAt >= from && l.OccurredAt < to);

        if (filter.PatientId is { } patientId)
            query = query.Where(l => l.PatientId == patientId);

        if (!string.IsNullOrWhiteSpace(filter.PatientRecordNumber))
        {
            var recordNumber = filter.PatientRecordNumber.Trim();

            // IgnoreQueryFilters, because the person exercising a right of access may be the one
            // whose file was pseudonymised under the right to erasure — and the consultations of
            // it are exactly what they are entitled to be shown.
            query = query.Where(l => l.PatientId != null
                                     && db.Patients.IgnoreQueryFilters()
                                          .Any(p => p.Id == l.PatientId
                                                    && p.MedicalRecordNumber == recordNumber));
        }

        if (!string.IsNullOrWhiteSpace(filter.UserName))
        {
            var user = filter.UserName.Trim();
            query = query.Where(l => l.UserName != null && l.UserName.Contains(user));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(l => (l.QueryDescription != null
                                      && l.QueryDescription.Contains(search))
                                     || (l.Context != null && l.Context.Contains(search))
                                     || (l.BreakGlassJustification != null
                                         && l.BreakGlassJustification.Contains(search)));
        }

        var rows = await query
            // Break-glass accesses first within the ordering an auditor reads: they are the rows a
            // confidentiality review exists to find, and burying them in chronological order among
            // thousands of routine lookups is how a review misses one.
            .OrderByDescending(l => l.IsBreakGlass)
            .ThenByDescending(l => l.OccurredAt)
            .Take(filter.MaxRows + 1)
            .Select(l => new
            {
                l.Id, l.OccurredAt, l.UserName, l.Kind, l.PatientId, l.RecordCount,
                l.QueryDescription, l.Context, l.Workstation, l.IsBreakGlass,
                l.BreakGlassJustification,
                PatientRecordNumber = l.PatientId == null
                    ? null
                    : db.Patients.IgnoreQueryFilters()
                        .Where(p => p.Id == l.PatientId)
                        .Select(p => p.MedicalRecordNumber)
                        .FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(l => new DataAccessRow(
                l.Id, l.OccurredAt, l.UserName, l.Kind, l.PatientId, l.PatientRecordNumber,
                l.RecordCount, l.QueryDescription, l.Context, l.Workstation, l.IsBreakGlass,
                l.BreakGlassJustification))
        ];
    }

    public async Task<IReadOnlyList<SecurityEventRow>> GetSecurityEventsAsync(
        AuditTrailFilter filter, CancellationToken ct = default)
    {
        var (from, to) = Window(filter);

        var query = db.SecurityEvents.AsNoTracking()
                      .Where(e => e.OccurredAt >= from && e.OccurredAt < to);

        if (!string.IsNullOrWhiteSpace(filter.UserName))
        {
            var user = filter.UserName.Trim();

            // Matched against the name that was typed, which is the only name a failed login has:
            // there is no account behind it to join to.
            query = query.Where(e => e.AttemptedUserName != null
                                     && e.AttemptedUserName.Contains(user));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(e => (e.Details != null && e.Details.Contains(search))
                                     || (e.FailureReasonKey != null
                                         && e.FailureReasonKey.Contains(search))
                                     || (e.Workstation != null && e.Workstation.Contains(search)));
        }

        var rows = await query
            // Failures first: a run of refused logins is the signal, and it is a handful of rows
            // among a day's worth of successful ones.
            .OrderBy(e => e.Succeeded)
            .ThenByDescending(e => e.OccurredAt)
            .Take(filter.MaxRows + 1)
            .Select(e => new
            {
                e.Id, e.OccurredAt, e.Kind, e.Succeeded, e.AttemptedUserName, e.Workstation,
                e.OsUser, e.FailureReasonKey, e.Details,
                ActorUserName = e.UserId == null
                    ? null
                    : db.Users.IgnoreQueryFilters()
                        .Where(u => u.Id == e.UserId)
                        .Select(u => u.UserName)
                        .FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(e => new SecurityEventRow(
                e.Id, e.OccurredAt, e.Kind, e.Succeeded,
                // The resolved account where there is one, otherwise the name that was typed.
                e.ActorUserName ?? e.AttemptedUserName,
                e.Workstation, e.OsUser, e.FailureReasonKey, e.Details))
        ];
    }

    /// <summary>
    /// The filter's inclusive day range as a half-open instant range.
    /// <para>
    /// Half-open, and built from the local offset rather than from UTC: the columns are
    /// <c>datetimeoffset</c> and an operator asking for "today" means their working day. Comparing
    /// against <c>To</c> at midnight inclusive would silently drop everything recorded on the last
    /// day of the range, which is the day an auditor is usually asking about.
    /// </para>
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset To) Window(AuditTrailFilter filter)
    {
        var start = filter.From.ToDateTime(TimeOnly.MinValue);
        var endExclusive = filter.To.AddDays(1).ToDateTime(TimeOnly.MinValue);

        return (new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
                new DateTimeOffset(endExclusive, TimeZoneInfo.Local.GetUtcOffset(endExclusive)));
    }
}
