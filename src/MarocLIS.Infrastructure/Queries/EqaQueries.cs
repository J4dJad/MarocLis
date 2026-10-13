using MarocLIS.Application.Quality;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class EqaQueries(LisDbContext db) : IEqaQueries
{
    public async Task<IReadOnlyList<EqaRow>> GetParticipationsAsync(
        Guid siteId, int year, bool onlyPending, DateOnly today, CancellationToken ct = default)
    {
        var first = new DateOnly(year, 1, 1);
        var last = new DateOnly(year, 12, 31);

        var rows = await db.EqaParticipations.AsNoTracking()
            .Where(e => e.SiteId == siteId && e.RoundDate >= first && e.RoundDate <= last
                        && (!onlyPending || e.Outcome == EqaOutcome.Pending))
            .OrderByDescending(e => e.RoundDate)
            .ThenBy(e => e.SchemeName).ThenBy(e => e.RoundReference)
            .Select(e => new
            {
                e.Id,
                e.SchemeName,
                e.RoundReference,
                e.RoundDate,
                e.SubmissionDeadline,
                e.SubmittedOn,
                Parameter = db.TestParameters.Where(p => p.Id == e.TestParameterId)
                                             .Select(p => new { p.Code, p.Name }).FirstOrDefault(),
                InstrumentCode = db.Instruments.Where(i => i.Id == e.InstrumentId)
                                               .Select(i => i.Code).FirstOrDefault(),
                e.ReportedValue,
                e.AssignedValue,
                e.PeerGroupMean,
                e.PeerGroupSd,
                e.ZScore,
                e.BiasPercent,
                e.Outcome,
                NonConformityReference = db.NonConformities.Where(n => n.Id == e.NonConformityId)
                                                           .Select(n => n.Reference).FirstOrDefault(),
                e.InvestigationNotes
            })
            .ToListAsync(ct);

        return rows
            .OrderByDescending(e => e.RoundDate)
            .ThenBy(e => e.SchemeName, StringComparer.Ordinal)
            .ThenBy(e => e.RoundReference, StringComparer.Ordinal)
            .ThenBy(e => e.Parameter?.Code, StringComparer.Ordinal)
            .Select(e => new EqaRow(
                e.Id, e.SchemeName, e.RoundReference, e.RoundDate, e.SubmissionDeadline,
                e.SubmittedOn, e.Parameter?.Code ?? "?", e.Parameter?.Name ?? new("?", "?", "?"),
                e.InstrumentCode, e.ReportedValue, e.AssignedValue, e.PeerGroupMean, e.PeerGroupSd,
                e.ZScore, e.BiasPercent, e.Outcome, e.NonConformityReference, e.InvestigationNotes,
                e.SubmittedOn is null && e.SubmissionDeadline is { } deadline && deadline < today
                    && e.Outcome == EqaOutcome.Pending))
            .ToList();
    }

    /// <summary>Numeric parameters only: an EQA score is a distance between two numbers.</summary>
    public async Task<IReadOnlyList<EqaOption>> GetParameterOptionsAsync(CancellationToken ct = default)
    {
        var rows = await db.TestParameters.AsNoTracking()
            .Where(p => p.ValueType == ResultValueType.Numeric)
            .OrderBy(p => p.Code)
            .Select(p => new { p.Id, p.Code, p.Name })
            .ToListAsync(ct);

        return rows.Select(p => new EqaOption(p.Id, p.Code, p.Name)).ToList();
    }

    public async Task<IReadOnlyList<EqaOption>> GetInstrumentOptionsAsync(
        Guid siteId, CancellationToken ct = default)
    {
        var rows = await db.Instruments.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.DecommissioningDate == null)
            .OrderBy(i => i.Code)
            .Select(i => new { i.Id, i.Code, i.Name })
            .ToListAsync(ct);

        return rows.Select(i => new EqaOption(i.Id, i.Code, i.Name)).ToList();
    }

    public async Task<IReadOnlySet<Guid>> GetRegisteredParameterIdsAsync(
        Guid siteId, string schemeName, string roundReference, CancellationToken ct = default)
    {
        var ids = await db.EqaParticipations.AsNoTracking()
            .Where(e => e.SiteId == siteId && e.SchemeName == schemeName
                        && e.RoundReference == roundReference)
            .Select(e => e.TestParameterId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    public async Task<string> GetParameterCodeAsync(Guid testParameterId,
                                                    CancellationToken ct = default) =>
        await db.TestParameters.AsNoTracking()
            .Where(p => p.Id == testParameterId)
            .Select(p => p.Code)
            .FirstOrDefaultAsync(ct) ?? "?";
}
