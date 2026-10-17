using MarocLIS.Application.Catalog;
using MarocLIS.Domain.Catalog;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>Read side of the national nomenclature of biology acts and of its comparison with the catalogue.</summary>
public sealed class NationalActQueries(LisDbContext db) : INationalActQueries
{
    public async Task<IReadOnlyList<NationalActRow>> GetActsAsync(string? search,
                                                                CancellationToken ct = default)
    {
        var query = db.NationalBiologyActs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // « b 118 », « B118 » and « 118 » all find the act; the label is searched accents and
            // case ignored by its collation (« glycemie » finds « Glycémie »).
            var code = NationalBiologyAct.Normalize(search)!;
            var codePattern = (char.IsDigit(code[0]) ? "B" + code : code) + "%";
            var labelPattern = $"%{search.Trim()}%";

            query = query.Where(a => EF.Functions.Like(a.Code, codePattern)
                                     || EF.Functions.Like(a.Label, labelPattern)
                                     || EF.Functions.Like(a.Chapter, labelPattern));
        }

        var rows = await query.OrderBy(a => a.Code).ToListAsync(ct);

        // Counted apart and matched in memory: the act code and the analysis's national code have
        // different collations, which SQL Server refuses to compare in one join.
        var cited = (await db.AnalysisTests.AsNoTracking()
                .Where(t => t.NationalActCode != null)
                .Select(t => t.NationalActCode!)
                .ToListAsync(ct))
            .Select(NationalBiologyAct.Normalize)
            .GroupBy(c => c!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return [.. rows.Select(a => new NationalActRow(a.Id, a.Code, a.Label, a.Coefficient, a.LetterKey,
                                                       a.Chapter, a.SubChapter, a.DecreeYear, a.IsActive,
                                                       cited.GetValueOrDefault(a.Code)))];
    }

    public async Task<NationalActRow?> FindActAsync(string code, CancellationToken ct = default)
    {
        var normalized = NationalBiologyAct.Normalize(code);
        if (normalized is null) return null;

        return await db.NationalBiologyActs.AsNoTracking()
            .Where(a => a.Code == normalized)
            .Select(a => new NationalActRow(a.Id, a.Code, a.Label, a.Coefficient, a.LetterKey,
                                            a.Chapter, a.SubChapter, a.DecreeYear, a.IsActive, 0))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<NomenclatureAnalysisFact>> GetAnalysisFactsAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var analyses = await db.AnalysisTests.AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new { a.Id, a.Code, a.Name, a.NationalActCode })
            .ToListAsync(ct);

        var tariffs = await db.Tariffs.AsNoTracking()
            .Where(t => t.ValidFrom <= today && (t.ValidUntil == null || t.ValidUntil >= today))
            .Select(t => new { t.AnalysisTestId, t.PayerId, t.ValidFrom, t.LetterKey, t.Coefficient })
            .ToListAsync(ct);

        // The tariff in force per payer is the latest that started; an earlier one still open on
        // paper (a gap in the history) is not what the counter charges.
        var inForce = tariffs
            .GroupBy(t => (t.AnalysisTestId, t.PayerId))
            .Select(g => g.OrderByDescending(t => t.ValidFrom).First())
            .ToLookup(t => t.AnalysisTestId,
                      t => new NomenclatureTariffFact(t.PayerId, t.LetterKey, t.Coefficient));

        return [.. analyses.Select(a => new NomenclatureAnalysisFact(
            a.Id, a.Code, a.Name, a.NationalActCode,
            [.. inForce[a.Id].OrderBy(t => t.PayerId is null ? 0 : 1)]))];
    }
}
