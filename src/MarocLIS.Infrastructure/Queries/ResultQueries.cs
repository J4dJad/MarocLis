using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Reports;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Reports;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class ResultQueries(LisDbContext db) : IResultQueries
{
    public async Task<IReadOnlyList<TestResult>> GetResultsForOrderAsync(
        Guid orderId, Guid? departmentId, CancellationToken ct = default)
    {
        // Tracked, not AsNoTracking: the entry and validation pipelines mutate these entities and
        // rely on the audit interceptor seeing the changes.
        var query = db.TestResults.Where(r => r.LabOrderId == orderId);

        if (departmentId is { } department)
            query = query.Where(r => r.DepartmentId == department);

        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// The patient's most recent released value for a parameter before the current order.
    /// <para>
    /// Joins through the order rather than denormalising a patient identifier onto the result: a
    /// result's patient can change when two files are merged, and a stale copy would silently pair
    /// a delta check against the wrong person's history.
    /// </para>
    /// </summary>
    public async Task<PreviousValue?> GetPreviousValueAsync(
        Guid patientId, Guid parameterId, DateTimeOffset before, CancellationToken ct = default)
    {
        var row = await db.TestResults
            .AsNoTracking()
            .Where(r => r.TestParameterId == parameterId
                        && r.NumericValue != null
                        && r.ValidatedAt != null
                        && r.ValidatedAt < before)
            .Join(db.LabOrders.AsNoTracking().Where(o => o.PatientId == patientId),
                  result => result.LabOrderId,
                  order => order.Id,
                  (result, order) => new { result.Id, result.NumericValue, result.ValidatedAt })
            .OrderByDescending(x => x.ValidatedAt)
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new PreviousValue(row.NumericValue!.Value,
                                DateOnly.FromDateTime(row.ValidatedAt!.Value.Date),
                                row.Id);
    }

    public async Task<IReadOnlyList<ReflexRule>> GetActiveReflexRulesAsync(
        IReadOnlyList<Guid> triggerParameterIds, CancellationToken ct = default) =>
        await db.ReflexRules
            .AsNoTracking()
            .Where(r => r.IsActive && triggerParameterIds.Contains(r.TriggerParameterId))
            .ToListAsync(ct);

    /// <summary>
    /// Resolves a scanned or typed identifier to an order.
    /// <para>
    /// The accession number is tried first, then the tube barcode, then an aliquot barcode. Both
    /// barcode columns carry a case-sensitive collation and are matched with equality, never a
    /// <c>LIKE</c>: a partial barcode match would be capable of attaching one patient's tube to
    /// another patient's order, which is the single worst defect a LIS can have.
    /// </para>
    /// </summary>
    public async Task<Guid?> ResolveOrderIdAsync(string term, Guid siteId,
                                                  CancellationToken ct = default)
    {
        var byAccession = await db.LabOrders
            .AsNoTracking()
            .Where(o => o.AccessionNumber == term)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(ct);

        if (byAccession is not null) return byAccession;

        var bySample = await db.Samples
            .AsNoTracking()
            .Where(s => s.Barcode == term)
            .Select(s => (Guid?)s.LabOrderId)
            .FirstOrDefaultAsync(ct);

        if (bySample is not null) return bySample;

        return await db.SampleAliquots
            .AsNoTracking()
            .Where(a => a.Barcode == term)
            .Join(db.Samples.AsNoTracking(), a => a.ParentSampleId, s => s.Id,
                  (a, s) => (Guid?)s.LabOrderId)
            .FirstOrDefaultAsync(ct);
    }
}

public sealed class ValidationQueries(LisDbContext db, IUnitOfWork unitOfWork) : IValidationQueries
{
    /// <summary>
    /// The biologist's worklist, projected in one round trip.
    /// <para>
    /// Aggregating in SQL rather than loading orders and counting in memory: a busy laboratory has
    /// hundreds of open files and this screen is refreshed constantly. Uncommunicated critical
    /// results sort first because they are the only entries that block a release.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ValidationWorklistEntry>> GetValidationWorklistAsync(
        Guid siteId, Guid? departmentId, IReadOnlySet<string> authorizedDepartmentCodes,
        bool onlyUrgent, DateTimeOffset now, CancellationToken ct = default)
    {
        var scopedDepartmentIds = authorizedDepartmentCodes.Count == 0
            ? null
            : await db.Departments.AsNoTracking()
                .Where(d => authorizedDepartmentCodes.Contains(d.Code))
                .Select(d => d.Id)
                .ToListAsync(ct);

        var results = db.TestResults.AsNoTracking()
            .Where(r => r.SiteId == siteId
                        && r.Status == TestResultStatus.TechnicallyValidated);

        if (departmentId is { } explicitDepartment)
            results = results.Where(r => r.DepartmentId == explicitDepartment);
        else if (scopedDepartmentIds is not null)
            results = results.Where(r => scopedDepartmentIds.Contains(r.DepartmentId));

        var orders = db.LabOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId
                        && o.Status != LabOrderStatus.Cancelled
                        && o.Status != LabOrderStatus.Reported);

        if (onlyUrgent)
            orders = orders.Where(o => o.Priority != OrderPriority.Routine);

        // Critical results whose communication is complete — validated read-back included.
        var communicated = db.CriticalResultNotifications.AsNoTracking()
            .Where(n => n.Outcome == NotificationOutcome.Acknowledged && n.ReadBackConfirmed)
            .Select(n => n.TestResultId);

        var grouped = await results
            .GroupBy(r => r.LabOrderId)
            .Select(g => new
            {
                LabOrderId = g.Key,
                Awaiting = g.Count(),
                Abnormal = g.Count(r => r.Interpretation != ResultInterpretation.Normal
                                        && r.Interpretation != ResultInterpretation.NotInterpretable),
                Critical = g.Count(r => (r.Flags & ResultFlag.CriticalLow) != 0
                                        || (r.Flags & ResultFlag.CriticalHigh) != 0),
                Uncommunicated = g.Count(r => ((r.Flags & ResultFlag.CriticalLow) != 0
                                               || (r.Flags & ResultFlag.CriticalHigh) != 0)
                                              && !communicated.Contains(r.Id)),
                QcFailure = g.Any(r => (r.Flags & ResultFlag.QcOutOfControl) != 0),
                DeltaFailure = g.Any(r => (r.Flags & ResultFlag.DeltaCheckFailed) != 0),
                SampleDefect = g.Any(r => (r.Flags & ResultFlag.SampleDefect) != 0)
            })
            .Join(orders, x => x.LabOrderId, o => o.Id, (x, o) => new { Stats = x, Order = o })
            .Join(db.Patients.AsNoTracking(),
                  x => x.Order.PatientId, p => p.Id,
                  (x, p) => new
                  {
                      x.Stats,
                      x.Order.Id,
                      x.Order.AccessionNumber,
                      x.Order.Priority,
                      x.Order.RegisteredAt,
                      x.Order.DueAt,
                      p.LastName,
                      p.FirstName,
                      p.DateOfBirth,
                      p.Gender
                  })
            .OrderByDescending(x => x.Stats.Uncommunicated)
            .ThenByDescending(x => x.Priority)
            .ThenBy(x => x.RegisteredAt)
            .Take(500)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(now.Date);

        // Department codes per order: a second small query rather than a correlated subquery, which
        // SQL Server Express plans poorly at this shape.
        var orderIds = grouped.Select(x => x.Id).ToList();

        var departmentsByOrder = await db.LabOrderItems.AsNoTracking()
            .Where(i => orderIds.Contains(i.LabOrderId))
            .Join(db.Departments.AsNoTracking(), i => i.DepartmentId, d => d.Id,
                  (i, d) => new { i.LabOrderId, d.Code })
            .Distinct()
            .ToListAsync(ct);

        var codeLookup = departmentsByOrder
            .GroupBy(x => x.LabOrderId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(x => x.Code).Order()]);

        return
        [
            .. grouped.Select(x => new ValidationWorklistEntry
            {
                LabOrderId = x.Id,
                AccessionNumber = x.AccessionNumber,
                PatientName = $"{x.LastName} {x.FirstName}".Trim(),
                AgeYears = AgeInYears(x.DateOfBirth, today),
                Gender = x.Gender,
                Priority = x.Priority,
                RegisteredAt = x.RegisteredAt,
                DueAt = x.DueAt,
                IsOverdue = x.DueAt is { } due && now > due,
                AwaitingCount = x.Stats.Awaiting,
                AbnormalCount = x.Stats.Abnormal,
                CriticalCount = x.Stats.Critical,
                UncommunicatedCriticalCount = x.Stats.Uncommunicated,
                HasQcFailure = x.Stats.QcFailure,
                HasDeltaCheckFailure = x.Stats.DeltaFailure,
                HasSampleDefect = x.Stats.SampleDefect,
                DepartmentCodes = codeLookup.GetValueOrDefault(x.Id, [])
            })
        ];
    }

    private static int? AgeInYears(DateOnly? dateOfBirth, DateOnly on)
    {
        if (dateOfBirth is not { } dob) return null;
        var age = on.Year - dob.Year;
        if (on < dob.AddYears(age)) age--;
        return age < 0 ? null : age;
    }

    public Task<TestResult?> GetResultAsync(Guid testResultId, CancellationToken ct = default) =>
        db.TestResults.FirstOrDefaultAsync(r => r.Id == testResultId, ct);

    public Task<bool> IsCriticalCommunicatedAsync(Guid testResultId, CancellationToken ct = default) =>
        db.CriticalResultNotifications
            .AsNoTracking()
            .AnyAsync(n => n.TestResultId == testResultId
                           && n.Outcome == NotificationOutcome.Acknowledged
                           && n.ReadBackConfirmed, ct);

    public Task<int> CountNotificationAttemptsAsync(Guid testResultId, CancellationToken ct = default) =>
        db.CriticalResultNotifications
            .AsNoTracking()
            .CountAsync(n => n.TestResultId == testResultId, ct);

    /// <summary>
    /// Writes the justification onto every control point of the covering run.
    /// <para>
    /// Onto the QC point rather than only onto the released result, because the question an auditor
    /// asks is "this control was out — what did you do about the patient results it covered?", and
    /// that question is asked from the Levey-Jennings chart.
    /// </para>
    /// </summary>
    public async Task<Result> RecordQcOverrideAsync(Guid? qcRunId, string justification,
                                                     CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(justification))
            return Result.Failure(Error.Regulatory("Qc_OverrideJustificationRequired"));

        if (qcRunId is not { } runId)
        {
            // No covering run is recorded. The override is still allowed — a manual bench method may
            // genuinely have none — but the audit reason carries it so the exception is not invisible.
            unitOfWork.AuditReason = $"Dérogation CIQ sans série de contrôle identifiée : {justification}";
            return Result.Success();
        }

        var points = await db.QcResults
            .Where(r => r.QcRunId == runId && r.Outcome == QcOutcome.Rejected)
            .ToListAsync(ct);

        if (points.Count == 0)
            return Result.Failure(Error.NotFound("Qc_RunNotFound", runId));

        foreach (var point in points)
        {
            var overridden = point.Override(justification);
            if (overridden.IsFailure) return overridden;
        }

        return Result.Success();
    }
}

public sealed class ReportQueries(LisDbContext db) : IReportQueries
{
    public Task<LabReport?> GetLatestVersionAsync(Guid orderId, CancellationToken ct = default) =>
        db.LabReports
            .Include(r => r.Deliveries)
            .Where(r => r.LabOrderId == orderId)
            .OrderByDescending(r => r.VersionNumber)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<LabReport>> GetVersionsAsync(Guid orderId,
                                                                  CancellationToken ct = default) =>
        await db.LabReports
            .AsNoTracking()
            .Include(r => r.Deliveries)
            .Where(r => r.LabOrderId == orderId)
            .OrderBy(r => r.VersionNumber)
            .ToListAsync(ct);

    public Task<LabReport?> GetByAccessTokenAsync(string token, DateTimeOffset now,
                                                   CancellationToken ct = default) =>
        db.LabReports
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.AccessToken == token
                                      && r.AccessTokenExpiresAt != null
                                      && r.AccessTokenExpiresAt > now, ct);

    /// <summary>
    /// The register of released versions.
    /// <para>
    /// Windowed by date rather than paged. The question this list answers — "what have we released
    /// and not handed over?" — is about the last few weeks; a register going back three years
    /// answers a different question, and one nobody asks at a counter.
    /// </para>
    /// <para>
    /// The delivery count and the last delivery are aggregated in the query rather than by loading
    /// the deliveries: a register of two hundred rows would otherwise pull every hand-over record
    /// the site has ever made in order to display one date per row.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ReportRegisterRow>> GetRegisterAsync(
        Guid siteId, string? search, bool onlyUndelivered, DateOnly from,
        CancellationToken ct = default)
    {
        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var query = db.LabReports.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.ReleasedAt >= since);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";

            query = query.Where(r =>
                db.LabOrders.Any(o => o.Id == r.LabOrderId
                                      && (EF.Functions.Like(o.AccessionNumber, pattern)
                                          || db.Patients.Any(p => p.Id == o.PatientId
                                                                  && (EF.Functions.Like(p.SearchKey, pattern)
                                                                      || EF.Functions.Like(p.MedicalRecordNumber, pattern))))));
        }

        var rows = await query
            .Select(r => new
            {
                ReportId = r.Id,
                r.LabOrderId,
                r.VersionNumber,
                r.Completeness,
                r.AmendmentKind,
                r.AmendmentReason,
                r.Language,
                r.ReleasedAt,
                r.SignatoryName,
                r.ContainsCriticalResults,
                r.ContainsReservations,
                r.ContainsSubcontractedResults,
                r.AllResultsAccredited,
                HasStoredPdf = r.RenderedPdf != null,
                r.PageCount,
                r.SupersededByReportId,
                DeliveryCount = db.ReportDeliveries.Count(d => d.LabReportId == r.Id),
                LastDeliveredAt = db.ReportDeliveries
                    .Where(d => d.LabReportId == r.Id
                                && d.Status == DeliveryStatus.Delivered
                                && d.CompletedAt != null)
                    .Max(d => d.CompletedAt),
                Order = db.LabOrders.Where(o => o.Id == r.LabOrderId)
                    .Select(o => new
                    {
                        o.AccessionNumber,
                        o.PatientId,
                        PrescriberName = db.Prescribers.Where(p => p.Id == o.PrescriberId)
                                                        .Select(p => p.LastName + " " + p.FirstName)
                                                        .FirstOrDefault(),
                        PrescriberPreference = db.Prescribers
                            .Where(p => p.Id == o.PrescriberId)
                            .Select(p => (ReportDeliveryPreference?)p.DeliveryPreference)
                            .FirstOrDefault(),
                        o.PrescriberFreeText
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var patientIds = rows.Where(r => r.Order != null)
                             .Select(r => r.Order!.PatientId)
                             .Distinct()
                             .ToList();

        var patients = await db.Patients.AsNoTracking()
            .Where(p => patientIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id, p.LastName, p.FirstName, p.LastNameArabic, p.FirstNameArabic, p.MobilePhone
            })
            .ToListAsync(ct);

        var byId = patients.ToDictionary(p => p.Id);

        var result = rows
            .Select(r =>
            {
                var patient = r.Order is not null && byId.TryGetValue(r.Order.PatientId, out var p)
                    ? p : null;

                return new ReportRegisterRow(
                    r.ReportId,
                    r.LabOrderId,
                    r.Order?.PatientId ?? Guid.Empty,
                    r.Order?.AccessionNumber ?? string.Empty,
                    patient is null ? string.Empty
                                    : $"{patient.LastName} {patient.FirstName}".Trim(),
                    patient is null || (string.IsNullOrWhiteSpace(patient.LastNameArabic)
                                        && string.IsNullOrWhiteSpace(patient.FirstNameArabic))
                        ? null
                        : $"{patient.LastNameArabic} {patient.FirstNameArabic}".Trim(),
                    patient?.MobilePhone,
                    r.VersionNumber,
                    r.Completeness,
                    r.AmendmentKind,
                    r.AmendmentReason,
                    r.Language,
                    r.ReleasedAt,
                    r.SignatoryName ?? string.Empty,
                    r.ContainsCriticalResults,
                    r.ContainsReservations,
                    r.ContainsSubcontractedResults,
                    r.AllResultsAccredited,
                    r.HasStoredPdf,
                    r.PageCount ?? 0,
                    r.SupersededByReportId,
                    r.DeliveryCount,
                    r.LastDeliveredAt,
                    r.Order?.PrescriberName ?? r.Order?.PrescriberFreeText,
                    r.Order?.PrescriberPreference ?? ReportDeliveryPreference.PatientCollects);
            })
            .Where(r => !onlyUndelivered || r.AwaitingDelivery)
            // Critical and undelivered first: ISO 15189 §5.9 makes reaching a clinician with a
            // panic value an obligation, not a courtesy. Then the rest, newest first.
            .OrderByDescending(r => r.IsUrgentToDeliver)
            .ThenByDescending(r => r.AwaitingDelivery)
            .ThenByDescending(r => r.ReleasedAt)
            .ToList();

        return result;
    }

    public async Task<IReadOnlyList<ReportDeliveryRow>> GetDeliveriesAsync(
        Guid reportId, CancellationToken ct = default) =>
        await db.ReportDeliveries.AsNoTracking()
                .Where(d => d.LabReportId == reportId)
                .OrderByDescending(d => d.RequestedAt)
                .Select(d => new ReportDeliveryRow(
                    d.Id,
                    d.Channel,
                    d.RecipientKind,
                    d.Status,
                    d.RecipientName,
                    d.RecipientIdentifier,
                    d.BearerName,
                    d.BearerCin,
                    d.RequestedAt,
                    d.CompletedAt,
                    d.AttemptCount,
                    d.FailureReason,
                    db.Users.Where(u => u.Id == d.HandedOverByUserId)
                            .Select(u => u.FullName).FirstOrDefault(),
                    d.AuthorizingConsentId != null))
                .ToListAsync(ct);
}
