using MarocLIS.Application.Orders;
using MarocLIS.Application.Results;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class OrderQueries(LisDbContext db, IResultQueries results) : IOrderQueries
{
    /// <summary>
    /// Delegated rather than reimplemented. The resolver's rule — equality on the barcode columns,
    /// never a <c>LIKE</c>, because a partial match could attach one patient's tube to another
    /// patient's order — is the single rule in this application least survivable as two copies.
    /// </summary>
    public Task<Guid?> ResolveOrderIdAsync(string term, Guid siteId,
                                            CancellationToken ct = default) =>
        results.ResolveOrderIdAsync(term, siteId, ct);

    /// <summary>
    /// The whole file for the order screen.
    /// <para>
    /// Three round trips — header, items, tubes — rather than one join, because a single query
    /// joining items to tubes to results multiplies the rows and then has to be de-duplicated in
    /// memory. Split deliberately, not by oversight.
    /// </para>
    /// <para>
    /// Cancelled items are included. A cancelled analysis and its reason are part of the file's
    /// history: the question an operator asks this screen is "why is the TSH not on the report?",
    /// and a row that has been filtered out cannot answer it.
    /// </para>
    /// </summary>
    public async Task<OrderDetail?> GetDetailAsync(Guid orderId, Guid siteId,
                                                    CancellationToken ct = default)
    {
        var header = await db.LabOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.SiteId == siteId)
            .Select(o => new
            {
                o.Id,
                o.AccessionNumber,
                o.Status,
                o.Priority,
                o.Origin,
                o.RegisteredAt,
                o.DueAt,
                o.PatientId,
                o.PrescriberId,
                o.PrescriberFreeText,
                o.PrescriptionDate,
                o.PayerId,
                o.ReimbursementRateAtRegistration,
                o.PriorAgreementNumber,
                o.ClinicalInformation,
                o.DiagnosisCode,
                o.PatientFasted,
                o.DeclaredFastingHours,
                o.WardOrUnit,
                o.RoomOrBed,
                o.InternalNote,
                o.ReportLanguage,
                o.InvoiceId,
                o.CancelledAt,
                o.CancellationReason,
                RegisteredByName = db.Users.Where(u => u.Id == o.RegisteredByUserId)
                                           .Select(u => u.FullName).FirstOrDefault(),
                Patient = db.Patients.Where(p => p.Id == o.PatientId)
                    .Select(p => new
                    {
                        p.LastName, p.FirstName, p.LastNameArabic, p.FirstNameArabic,
                        p.DateOfBirth, p.Gender, p.Cin, p.MedicalRecordNumber, p.MobilePhone
                    }).FirstOrDefault(),
                Prescriber = db.Prescribers.Where(pr => pr.Id == o.PrescriberId)
                    .Select(pr => new
                    {
                        pr.LastName, pr.FirstName, pr.InstitutionName, pr.Kind, pr.Inpe
                    }).FirstOrDefault(),
                PayerName = db.Payers.Where(pa => pa.Id == o.PayerId)
                                     .Select(pa => pa.Name).FirstOrDefault(),
                InvoiceNumber = db.Invoices.Where(i => i.Id == o.InvoiceId)
                                           .Select(i => i.Number).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (header is null) return null;

        var items = await db.LabOrderItems.AsNoTracking()
            .Where(i => i.LabOrderId == orderId)
            .Select(i => new
            {
                i.Id,
                i.AnalysisTestId,
                i.Status,
                i.Priority,
                i.ChargedPrice,
                i.PayerShare,
                i.PatientShare,
                i.BilledLetterKey,
                i.BilledCoefficient,
                i.SampleId,
                i.DueAt,
                i.ValidatedAt,
                i.ValidatedByUserId,
                i.RunCount,
                i.RepeatReason,
                i.CancelledAt,
                i.CancellationReason,
                i.IsSubcontracted,
                Analysis = db.AnalysisTests.Where(a => a.Id == i.AnalysisTestId)
                    .Select(a => new { a.Code, a.Name, a.DepartmentId }).FirstOrDefault(),
                SampleBarcode = db.Samples.Where(s => s.Id == i.SampleId)
                                          .Select(s => s.Barcode).FirstOrDefault(),
                ValidatedByName = db.Users.Where(u => u.Id == i.ValidatedByUserId)
                                          .Select(u => u.FullName).FirstOrDefault(),
                ResultCount = db.TestResults.Count(r => r.LabOrderItemId == i.Id),
                // "Entered" counts the rows that carry a value, not the rows that exist: the
                // awaited rows are created at registration, so a count of rows would show every
                // analysis as complete from the moment it was ordered.
                EnteredResultCount = db.TestResults.Count(
                    r => r.LabOrderItemId == i.Id && r.Status != TestResultStatus.Awaited)
            })
            .ToListAsync(ct);

        var departmentCodes = await db.Departments.AsNoTracking()
            .Select(d => new { d.Id, d.Code })
            .ToDictionaryAsync(d => d.Id, d => d.Code, ct);

        var samples = await db.Samples.AsNoTracking()
            .Where(s => s.LabOrderId == orderId)
            .OrderBy(s => s.SequenceInOrder)
            .Select(s => new
            {
                s.Id,
                s.Barcode,
                s.Status,
                s.SequenceInOrder,
                s.CollectedAt,
                s.ReceivedAt,
                s.StorageLocation,
                SpecimenTypeName = db.SpecimenTypes.Where(t => t.Id == s.SpecimenTypeId)
                                                   .Select(t => t.Name).FirstOrDefault(),
                Container = db.ContainerTypes.Where(c => c.Id == s.ContainerTypeId)
                    .Select(c => new { c.Name, c.CapColourHex }).FirstOrDefault(),
                AnalysisCount = db.LabOrderItems.Count(i => i.SampleId == s.Id && !i.CancelledAt.HasValue)
            })
            .ToListAsync(ct);

        var patientName = header.Patient is null
            ? string.Empty
            : $"{header.Patient.LastName} {header.Patient.FirstName}".Trim();

        var patientNameArabic = header.Patient is null
            ? null
            : string.IsNullOrWhiteSpace(header.Patient.LastNameArabic)
              && string.IsNullOrWhiteSpace(header.Patient.FirstNameArabic)
                ? null
                : $"{header.Patient.LastNameArabic} {header.Patient.FirstNameArabic}".Trim();

        var prescriberName = header.Prescriber is null
            ? null
            : header.Prescriber.Kind == Domain.Orders.PrescriberKind.Institution
                ? header.Prescriber.InstitutionName
                : $"{header.Prescriber.LastName} {header.Prescriber.FirstName}".Trim();

        return new OrderDetail(
            header.Id,
            header.AccessionNumber,
            header.Status,
            header.Priority,
            header.Origin,
            header.RegisteredAt,
            header.RegisteredByName ?? string.Empty,
            header.DueAt,
            header.PatientId,
            patientName,
            patientNameArabic,
            header.Patient?.DateOfBirth,
            header.Patient?.Gender ?? Domain.Patients.Gender.Unknown,
            header.Patient?.Cin,
            header.Patient?.MedicalRecordNumber ?? string.Empty,
            header.Patient?.MobilePhone,
            header.PrescriberId,
            prescriberName,
            header.Prescriber?.Inpe,
            header.PrescriberFreeText,
            header.PrescriptionDate,
            header.PayerName?.Fr,
            header.ReimbursementRateAtRegistration,
            header.PriorAgreementNumber,
            header.ClinicalInformation,
            header.DiagnosisCode,
            header.PatientFasted,
            header.DeclaredFastingHours,
            header.WardOrUnit,
            header.RoomOrBed,
            header.InternalNote,
            header.ReportLanguage,
            header.InvoiceId,
            header.InvoiceNumber,
            header.CancelledAt,
            header.CancellationReason,
            [.. items
                .OrderBy(i => i.CancelledAt.HasValue)
                .ThenBy(i => i.Analysis == null ? string.Empty : i.Analysis.Code)
                .Select(i => new OrderItemDetail(
                    i.Id,
                    i.AnalysisTestId,
                    i.Analysis?.Code ?? string.Empty,
                    i.Analysis?.Name ?? new MultilingualText(string.Empty),
                    i.Analysis is null ? null
                        : departmentCodes.TryGetValue(i.Analysis.DepartmentId, out var code)
                            ? code : null,
                    i.Status,
                    i.Priority,
                    i.ChargedPrice,
                    i.PayerShare,
                    i.PatientShare,
                    i.BilledLetterKey,
                    i.BilledCoefficient,
                    i.SampleId,
                    i.SampleBarcode,
                    i.DueAt,
                    i.ValidatedAt,
                    i.ValidatedByName,
                    i.RunCount,
                    i.RepeatReason,
                    i.CancelledAt,
                    i.CancellationReason,
                    i.IsSubcontracted,
                    i.ResultCount,
                    i.EnteredResultCount))],
            [.. samples.Select(s => new OrderSampleDetail(
                s.Id,
                s.Barcode,
                s.SpecimenTypeName ?? new MultilingualText(string.Empty),
                s.Container?.Name.Fr,
                s.Container?.CapColourHex,
                s.Status,
                s.SequenceInOrder,
                s.CollectedAt,
                s.ReceivedAt,
                s.StorageLocation,
                s.AnalysisCount))]);
    }
}
