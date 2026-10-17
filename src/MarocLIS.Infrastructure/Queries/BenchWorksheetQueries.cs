using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class BenchWorksheetQueries(LisDbContext db) : IBenchWorksheetQueries
{
    /// <summary>
    /// Analyses that are physically at the bench and still owe a value.
    /// <para>
    /// Three conditions, each closing a way for a sheet to send a technician after work that is not
    /// there: the act is received or started (not merely registered, not already entered); its tube
    /// is in the laboratory and usable (a rejected tube has a recollection pending, not a
    /// measurement); and at least one of its parameters is still awaited. Subcontracted acts are
    /// left out — they leave in a transport box, not on a bench.
    /// </para>
    /// </summary>
    private IQueryable<LabOrderItem> PendingItems(Guid siteId) => PendingItems(db, siteId);

    /// <summary>
    /// The same selection for another reader — the dashboard counts the files it holds, so the
    /// figure on the opening screen and the list on the entry screen never disagree.
    /// </summary>
    internal static IQueryable<LabOrderItem> PendingItems(LisDbContext db, Guid siteId) =>
        db.LabOrderItems.AsNoTracking()
            .Where(i => (i.Status == LabOrderItemStatus.Received
                         || i.Status == LabOrderItemStatus.InProgress)
                        && !i.IsSubcontracted
                        && db.LabOrders.Any(o => o.Id == i.LabOrderId
                                                 && o.SiteId == siteId
                                                 && o.Status != LabOrderStatus.Cancelled)
                        && db.Samples.Any(s => s.Id == i.SampleId
                                               && (s.Status == SampleStatus.Received
                                                   || s.Status == SampleStatus.InAnalysis
                                                   || s.Status == SampleStatus.Stored))
                        && db.TestResults.Any(r => r.LabOrderItemId == i.Id
                                                   && r.Status == TestResultStatus.Awaited));

    private IQueryable<LabOrderItem> PendingOrEnteredItems(Guid siteId) =>
        db.LabOrderItems.AsNoTracking()
            .Where(i => (i.Status == LabOrderItemStatus.Received
                         || i.Status == LabOrderItemStatus.InProgress
                         || i.Status == LabOrderItemStatus.ResultEntered)
                        && !i.IsSubcontracted
                        && db.LabOrders.Any(o => o.Id == i.LabOrderId
                                                 && o.SiteId == siteId
                                                 && o.Status != LabOrderStatus.Cancelled)
                        && db.Samples.Any(s => s.Id == i.SampleId
                                               && (s.Status == SampleStatus.Received
                                                   || s.Status == SampleStatus.InAnalysis
                                                   || s.Status == SampleStatus.Stored))
                        && db.TestResults.Any(r => r.LabOrderItemId == i.Id
                                                   && (r.Status == TestResultStatus.Awaited
                                                       || r.Status == TestResultStatus.Entered)));

    public async Task<BenchWorksheetOptions> GetOptionsAsync(Guid siteId, CancellationToken ct = default)
    {
        var pending = await PendingItems(siteId)
            .GroupBy(i => i.DepartmentId)
            .Select(g => new { DepartmentId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var counts = pending.ToDictionary(p => p.DepartmentId, p => p.Count);

        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Code)
            .Select(d => new { d.Id, d.Code, d.Name })
            .ToListAsync(ct);

        // Analysers only: a centrifuge or a fridge has no run list to print.
        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && i.Kind == EquipmentKind.Analyser
                        && i.Status != EquipmentStatus.Decommissioned)
            .OrderBy(i => i.Code)
            .Select(i => new BenchInstrumentOption(i.Id, i.Code, i.Name, i.DepartmentId))
            .ToListAsync(ct);

        return new BenchWorksheetOptions(
            [.. departments.Select(d => new BenchDepartmentOption(d.Id, d.Code, d.Name,
                                                                  counts.GetValueOrDefault(d.Id)))],
            instruments);
    }

    public async Task<BenchWorksheetContext?> GetContextAsync(
        Guid siteId, Guid? departmentId, Guid? instrumentId, CancellationToken ct = default)
    {
        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId)
            .Select(s => new { s.Name, s.LegalName, s.AddressLine, s.City, s.Phone, s.LogoPng })
            .FirstOrDefaultAsync(ct);

        if (site is null) return null;

        var department = departmentId is { } d
            ? await db.Departments.AsNoTracking()
                .Where(x => x.Id == d)
                .Select(x => new { x.Code, x.Name })
                .FirstOrDefaultAsync(ct)
            : null;

        var instrument = instrumentId is { } i
            ? await db.Instruments.AsNoTracking()
                .Where(x => x.Id == i && x.SiteId == siteId)
                .Select(x => new { x.Code, x.Name })
                .FirstOrDefaultAsync(ct)
            : null;

        return new BenchWorksheetContext(
            new BenchLaboratory(site.Name, site.LegalName, site.AddressLine, site.City, site.Phone,
                                site.LogoPng),
            department?.Code, department?.Name,
            instrument?.Code, instrument?.Name);
    }

    /// <summary>
    /// One row per awaited parameter, grouped back into one line per analysis in memory.
    /// <para>
    /// Read flat and ordered in SQL rather than as analyses with a nested parameter collection: the
    /// ordering — priority, reception, file, catalogue order — has to hold across the whole sheet,
    /// and a cap applied to a nested shape would be applied to the wrong thing. The cap is read one
    /// row past the maximum so a full sheet can be told from a cut one, and a cut sheet loses its
    /// last analysis whole: half a panel on the sheet reads as the whole panel.
    /// </para>
    /// </summary>
    public async Task<(IReadOnlyList<BenchWorksheetLine> Lines, bool Truncated)> GetPendingWorkAsync(
        Guid siteId, Guid? departmentId, Guid? instrumentId, bool onlyUrgent, DateOnly today,
        int maximumRows, CancellationToken ct = default, bool includeEntered = false)
    {
        // The entry screen also keeps the files whose values are all in but not yet technically
        // validated: that check is done from the same screen, and a file that left the list once
        // its last value was typed could no longer be validated without knowing its number.
        var items = includeEntered ? PendingOrEnteredItems(siteId) : PendingItems(siteId);

        if (departmentId is { } department)
            items = items.Where(i => i.DepartmentId == department);

        // An act can be urgent inside a routine file — a kaliémie added on a ward's call — so either
        // priority qualifies it.
        if (onlyUrgent)
            items = items.Where(i => i.Priority != OrderPriority.Routine
                                     || db.LabOrders.Any(o => o.Id == i.LabOrderId
                                                              && o.Priority != OrderPriority.Routine));

        var results = db.TestResults.AsNoTracking()
            .Where(r => r.SiteId == siteId
                        && (r.Status == TestResultStatus.Awaited
                            || (includeEntered && r.Status == TestResultStatus.Entered)));

        // An analyser's share of the work is what its interface is mapped to measure. Read from the
        // mappings rather than from the analysis, because one analysis is commonly split across two
        // instruments — the ionogram on one, the creatinine on the other.
        if (instrumentId is { } instrument)
            results = results.Where(r => db.InstrumentTestMappings.Any(
                m => m.TestParameterId == r.TestParameterId
                     && m.IsEnabled
                     && db.InstrumentInterfaces.Any(f => f.Id == m.InstrumentInterfaceId
                                                         && f.InstrumentId == instrument)));

        // Calculated parameters are derived on entry and headings hold nothing: neither is written
        // on a bench.
        var parameters = db.TestParameters.AsNoTracking()
            .Where(p => (p.CalculationFormula == null || p.CalculationFormula == "")
                        && p.ValueType != ResultValueType.Heading);

        var rows = await items
            .Join(results, i => i.Id, r => r.LabOrderItemId,
                  (i, r) => new { Item = i, r.TestParameterId, r.AppliedUnitSymbol })
            .Join(parameters, x => x.TestParameterId, p => p.Id,
                  (x, p) => new { x.Item, x.AppliedUnitSymbol, Parameter = p })
            .Join(db.LabOrders.AsNoTracking(), x => x.Item.LabOrderId, o => o.Id,
                  (x, o) => new { x.Item, x.AppliedUnitSymbol, x.Parameter, Order = o })
            .Join(db.Patients.AsNoTracking(), x => x.Order.PatientId, p => p.Id,
                  (x, p) => new { x.Item, x.AppliedUnitSymbol, x.Parameter, x.Order, Patient = p })
            .Join(db.Samples.AsNoTracking(), x => x.Item.SampleId, s => (Guid?)s.Id,
                  (x, s) => new { x.Item, x.AppliedUnitSymbol, x.Parameter, x.Order, x.Patient, Sample = s })
            .Join(db.AnalysisTests.AsNoTracking(), x => x.Item.AnalysisTestId, a => a.Id,
                  (x, a) => new { x.Item, x.AppliedUnitSymbol, x.Parameter, x.Order, x.Patient, x.Sample, Analysis = a })
            .Join(db.Departments.AsNoTracking(), x => x.Item.DepartmentId, d => d.Id,
                  (x, d) => new
                  {
                      ItemId = x.Item.Id,
                      ItemPriority = x.Item.Priority,
                      OrderPriority = x.Order.Priority,
                      ReceivedAt = x.Item.ReceivedAt ?? x.Sample.ReceivedAt,
                      DueAt = x.Item.DueAt ?? x.Order.DueAt,
                      x.Order.AccessionNumber,
                      x.Sample.Barcode,
                      x.Sample.Defects,
                      x.Patient.LastName,
                      x.Patient.FirstName,
                      x.Patient.LastNameArabic,
                      x.Patient.FirstNameArabic,
                      x.Patient.Gender,
                      x.Patient.DateOfBirth,
                      x.Patient.DeclaredAgeYears,
                      DepartmentCode = d.Code,
                      AnalysisCode = x.Analysis.Code,
                      AnalysisName = x.Analysis.Name,
                      AnalysisOrder = x.Analysis.DisplayOrder,
                      ParameterCode = x.Parameter.Code,
                      ParameterName = x.Parameter.Name,
                      ParameterOrder = x.Parameter.DisplayOrder,
                      x.Parameter.ValueType,
                      UnitSymbol = x.AppliedUnitSymbol
                                   ?? db.Units.Where(u => u.Id == x.Parameter.UnitId)
                                              .Select(u => u.DisplaySymbol)
                                              .FirstOrDefault()
                  })
            .OrderByDescending(x => x.OrderPriority > x.ItemPriority ? x.OrderPriority : x.ItemPriority)
            .ThenBy(x => x.ReceivedAt)
            .ThenBy(x => x.AccessionNumber)
            .ThenBy(x => x.AnalysisOrder)
            .ThenBy(x => x.AnalysisCode)
            .ThenBy(x => x.ItemId)
            .ThenBy(x => x.ParameterOrder)
            .ThenBy(x => x.ParameterCode)
            .Take(maximumRows + 1)
            .ToListAsync(ct);

        var truncated = rows.Count > maximumRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        // GroupBy on a list keeps first-appearance order, so the SQL ordering survives the grouping.
        var lines = rows
            .GroupBy(x => x.ItemId)
            .Select(g =>
            {
                var first = g.First();

                return new BenchWorksheetLine
                {
                    LabOrderItemId = first.ItemId,
                    AccessionNumber = first.AccessionNumber,
                    SampleBarcode = first.Barcode,
                    PatientName = $"{first.LastName} {first.FirstName}".Trim(),
                    PatientNameArabic = string.IsNullOrWhiteSpace(first.LastNameArabic)
                                        && string.IsNullOrWhiteSpace(first.FirstNameArabic)
                        ? null
                        : $"{first.LastNameArabic} {first.FirstNameArabic}".Trim(),
                    Gender = first.Gender,
                    AgeYears = AgeInYears(first.DateOfBirth, today) ?? first.DeclaredAgeYears,
                    Priority = first.OrderPriority > first.ItemPriority
                        ? first.OrderPriority
                        : first.ItemPriority,
                    DepartmentCode = first.DepartmentCode,
                    AnalysisCode = first.AnalysisCode,
                    AnalysisName = first.AnalysisName,
                    ReceivedAt = first.ReceivedAt,
                    DueAt = first.DueAt,
                    SampleDefects = first.Defects,
                    Parameters =
                    [
                        .. g.Select(x => new BenchWorksheetParameter(
                            x.ParameterCode, x.ParameterName, x.UnitSymbol, x.ValueType))
                    ]
                };
            })
            .ToList();

        if (truncated && lines.Count > 1) lines.RemoveAt(lines.Count - 1);

        return (lines, truncated);
    }

    private static int? AgeInYears(DateOnly? dateOfBirth, DateOnly on)
    {
        if (dateOfBirth is not { } dob) return null;
        var age = on.Year - dob.Year;
        if (on < dob.AddYears(age)) age--;
        return age < 0 ? null : age;
    }
}
