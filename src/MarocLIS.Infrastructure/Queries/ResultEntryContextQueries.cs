using MarocLIS.Application.Results;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class ResultEntryContextQueries(LisDbContext db) : IResultEntryContextQueries
{
    /// <summary>
    /// Counted in SQL, one row per dossier: the values a technician keys, on analyses still on the
    /// order. Entered means anything past "awaited" — a value, a validated value, or a recorded
    /// "not done" — which is what "saisi" means on the entry screen.
    /// </summary>
    public async Task<IReadOnlyList<ResultEntryProgress>> GetProgressAsync(
        Guid siteId, IReadOnlyCollection<string> accessionNumbers, CancellationToken ct = default)
    {
        var wanted = accessionNumbers.Distinct().ToList();

        var rows = await (
                from r in db.TestResults.AsNoTracking()
                join o in db.LabOrders.AsNoTracking() on r.LabOrderId equals o.Id
                join i in db.LabOrderItems.AsNoTracking() on r.LabOrderItemId equals i.Id
                join p in db.TestParameters.AsNoTracking() on r.TestParameterId equals p.Id
                where o.SiteId == siteId
                      && wanted.Contains(o.AccessionNumber)
                      && i.Status != LabOrderItemStatus.Cancelled
                      // Only the analyses whose tube is in the laboratory, as the worklist counts
                      // them: a refused tube's analyses waiting for the new draw are not owed yet.
                      && (i.SampleId == null
                          || db.Samples.Any(s => s.Id == i.SampleId
                                             && (s.Status == SampleStatus.Received
                                                 || s.Status == SampleStatus.InAnalysis
                                                 || s.Status == SampleStatus.Stored)))
                      && (p.CalculationFormula == null || p.CalculationFormula == "")
                      && p.ValueType != ResultValueType.Heading
                group r by o.AccessionNumber
                into g
                select new
                {
                    AccessionNumber = g.Key,
                    Entered = g.Count(x => x.Status != TestResultStatus.Awaited),
                    Total = g.Count()
                })
            .ToListAsync(ct);

        return [.. rows.Select(x => new ResultEntryProgress(x.AccessionNumber, x.Entered, x.Total))];
    }

    public async Task<IReadOnlyList<ResultEntrySector>> GetSectorsAsync(
        IReadOnlyCollection<string> departmentCodes, CancellationToken ct = default)
    {
        var wanted = departmentCodes.Distinct().ToList();

        var rows = await db.Departments.AsNoTracking()
            .Where(d => wanted.Contains(d.Code))
            .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Code)
            .Select(d => new { d.Code, d.Name })
            .ToListAsync(ct);

        return [.. rows.Select(d => new ResultEntrySector(d.Code, d.Name))];
    }

    /// <summary>
    /// The analyser is read without the query filters: one taken out of service, or deleted, after
    /// it sent the value is still where the value came from, and showing such a value as keyed by
    /// hand would misstate its origin.
    /// </summary>
    public async Task<IReadOnlyList<ResultEntryOrigin>> GetOriginsAsync(Guid orderId, CancellationToken ct = default)
    {
        var results = await db.TestResults.AsNoTracking()
            .Where(r => r.LabOrderId == orderId)
            .Select(r => new { r.Id, r.DepartmentId, r.InstrumentId })
            .ToListAsync(ct);

        if (results.Count == 0) return [];

        var departmentIds = results.Select(r => r.DepartmentId).Distinct().ToList();
        var departments = await db.Departments.AsNoTracking()
            .Where(d => departmentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Code, d.Name })
            .ToDictionaryAsync(d => d.Id, ct);

        var instrumentIds = results.Where(r => r.InstrumentId != null).Select(r => r.InstrumentId!.Value)
                                   .Distinct().ToList();
        var instruments = await db.Instruments.AsNoTracking().IgnoreQueryFilters()
                .Where(i => instrumentIds.Contains(i.Id))
                .Select(i => new { i.Id, i.Code, i.Name })
                .ToDictionaryAsync(i => i.Id, ct);

        return
        [
            .. results.Select(r =>
            {
                var department = departments.GetValueOrDefault(r.DepartmentId);
                var instrument = r.InstrumentId is { } id ? instruments.GetValueOrDefault(id) : null;

                return new ResultEntryOrigin(r.Id, department?.Code, department?.Name,
                                             instrument?.Code, instrument?.Name);
            })
        ];
    }
}
