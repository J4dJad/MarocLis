using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// A pickable item in order entry — an analysis or a panel, presented uniformly.
/// <para>
/// One list rather than two tabs: a reception clerk told "bilan lipidique" does not know or care
/// whether the laboratory configured that as a panel or a single analysis, and making them choose
/// the right tab first is friction that produces wrong orders.
/// </para>
/// </summary>
public sealed record CatalogPickerItem(
    Guid Id,
    bool IsPanel,
    string Code,
    MultilingualText Name,
    string? DepartmentCode,
    MultilingualText? DepartmentName,
    decimal Price,
    int? FastingHours,
    int TurnaroundHours,
    bool IsAvailableUrgently,
    ConsentRequirement ConsentRequirement,
    bool IsSubcontracted,
    string? ContainerName,
    string? ContainerColourHex,
    int ComponentCount,
    IReadOnlyList<PickerComponent>? Components = null,
    decimal? PackagePrice = null);

/// <summary>A mandatory member of a panel and the price it is billed at.</summary>
public sealed record PickerComponent(Guid AnalysisTestId, decimal Price);

public sealed record DepartmentOption(Guid Id, string Code, MultilingualText Name, int DisplayOrder);

public interface ICatalogQueries
{
    /// <summary>
    /// Analyses and panels matching a search term, for the order-entry picker. Matches on code,
    /// name in any of the three languages, and the operator-defined synonym list.
    /// </summary>
    Task<IReadOnlyList<CatalogPickerItem>> SearchPickerAsync(
        string? term, Guid? payerId, DateOnly on, int take = 60, CancellationToken ct = default);

    Task<IReadOnlyList<DepartmentOption>> GetDepartmentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Catalogue entries still carrying an unverified reference interval or a placeholder tariff.
    /// Surfaced as a pre-go-live checklist: ISO 15189 §5.5.2 makes the intervals the laboratory's
    /// own responsibility, and a seeded placeholder left in place is a finding.
    /// </summary>
    Task<IReadOnlyList<string>> GetUnverifiedCatalogueEntriesAsync(CancellationToken ct = default);
}

public sealed class CatalogQueries(LisDbContext db) : ICatalogQueries
{
    public async Task<IReadOnlyList<CatalogPickerItem>> SearchPickerAsync(
        string? term, Guid? payerId, DateOnly on, int take = 60, CancellationToken ct = default)
    {
        var pattern = string.IsNullOrWhiteSpace(term) ? null : $"%{term.Trim()}%";
        // Codes are stored upper-case in a case-sensitive column: "nfs" typed at the counter has
        // to find NFS.
        var codePattern = pattern?.ToUpperInvariant();

        var analyses = db.AnalysisTests.AsNoTracking().Where(t => t.IsActive);

        if (pattern is not null)
        {
            analyses = analyses.Where(t =>
                EF.Functions.Like(t.Code, codePattern!)
                || EF.Functions.Like(t.Name.Fr, pattern)
                || (t.Name.Ar != null && EF.Functions.Like(t.Name.Ar, pattern))
                || (t.Name.En != null && EF.Functions.Like(t.Name.En, pattern))
                || (t.SearchAliases != null && EF.Functions.Like(t.SearchAliases, pattern)));
        }

        var analysisRows = await analyses
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Code)
            .Take(take)
            .Select(t => new
            {
                t.Id,
                t.Code,
                t.Name,
                DepartmentCode = t.Department!.Code,
                DepartmentName = t.Department.Name,
                t.RoutineTurnaroundHours,
                t.IsAvailableUrgently,
                t.ConsentRequirement,
                t.IsSubcontracted,
                t.FastingHoursOverride,
                SpecimenFasting = t.SpecimenType!.FastingHours,
                ContainerName = t.ContainerType != null ? t.ContainerType.Name : null,
                ContainerColour = t.ContainerType != null ? t.ContainerType.CapColourHex : null,
                ParameterCount = t.Parameters.Count(p => p.IsActive),
                // The payer's own tariff if one exists, otherwise the default price list — the
                // same fallback AnalysisTest.TariffFor applies, resolved here in one query.
                Price = t.Tariffs
                    .Where(f => f.PayerId == payerId
                                && f.ValidFrom <= on
                                && (f.ValidUntil == null || f.ValidUntil >= on))
                    .OrderByDescending(f => f.ValidFrom)
                    .Select(f => (decimal?)f.Price)
                    .FirstOrDefault()
                    ?? t.Tariffs
                        .Where(f => f.PayerId == null
                                    && f.ValidFrom <= on
                                    && (f.ValidUntil == null || f.ValidUntil >= on))
                        .OrderByDescending(f => f.ValidFrom)
                        .Select(f => (decimal?)f.Price)
                        .FirstOrDefault()
                    ?? 0m
            })
            .ToListAsync(ct);

        var panels = db.TestPanels.AsNoTracking().Where(p => p.IsActive);

        if (pattern is not null)
        {
            panels = panels.Where(p =>
                EF.Functions.Like(p.Code, codePattern!)
                || EF.Functions.Like(p.Name.Fr, pattern)
                || (p.Name.Ar != null && EF.Functions.Like(p.Name.Ar, pattern))
                || (p.Name.En != null && EF.Functions.Like(p.Name.En, pattern))
                || (p.SearchAliases != null && EF.Functions.Like(p.SearchAliases, pattern)));
        }

        var panelRows = await panels
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Code)
            .Take(take)
            .Select(p => new
            {
                p.Id,
                p.Code,
                p.Name,
                ComponentCount = p.Items.Count(i => !i.IsOptional),
                p.PackagePrice,
                // The members and their prices, not only their total: the counter has to know which
                // analyses a panel already contains, so one chosen twice — on its own and inside
                // the panel — is counted once, as the order itself counts it.
                Components = p.Items
                    .Where(i => !i.IsOptional)
                    .Select(i => new
                    {
                        i.AnalysisTestId,
                        // The payer's tariff first, as for an analysis on its own: quoting the
                        // laboratory's list here put a different figure on the screen from the
                        // one registration bills an insured patient.
                        Price = i.AnalysisTest!.Tariffs
                            .Where(f => f.PayerId == payerId
                                        && f.ValidFrom <= on
                                        && (f.ValidUntil == null || f.ValidUntil >= on))
                            .OrderByDescending(f => f.ValidFrom)
                            .Select(f => (decimal?)f.Price)
                            .FirstOrDefault()
                            ?? i.AnalysisTest!.Tariffs
                                .Where(f => f.PayerId == null
                                            && f.ValidFrom <= on
                                            && (f.ValidUntil == null || f.ValidUntil >= on))
                                .OrderByDescending(f => f.ValidFrom)
                                .Select(f => (decimal?)f.Price)
                                .FirstOrDefault() ?? 0m
                    })
                    .ToList(),
                // A panel inherits the strictest requirement of its members: if any component
                // needs twelve hours' fasting or a specific consent, the panel does.
                MaxFasting = p.Items
                    .Where(i => !i.IsOptional)
                    .Max(i => (int?)(i.AnalysisTest!.FastingHoursOverride
                                     ?? i.AnalysisTest.SpecimenType!.FastingHours)),
                MaxTurnaround = p.Items
                    .Where(i => !i.IsOptional)
                    .Max(i => (int?)i.AnalysisTest!.RoutineTurnaroundHours),
                RequiresConsent = p.Items
                    .Any(i => i.AnalysisTest!.ConsentRequirement != ConsentRequirement.None)
            })
            .ToListAsync(ct);

        var items = new List<CatalogPickerItem>(analysisRows.Count + panelRows.Count);

        items.AddRange(panelRows.Select(p => new CatalogPickerItem(
            p.Id, IsPanel: true, p.Code, p.Name,
            DepartmentCode: null, DepartmentName: null,
            // What registration bills: the package price when the panel has one, otherwise each
            // member at its own tariff.
            Price: p.PackagePrice ?? p.Components.Sum(c => c.Price),
            FastingHours: p.MaxFasting is > 0 ? p.MaxFasting : null,
            TurnaroundHours: p.MaxTurnaround ?? 24,
            IsAvailableUrgently: false,
            ConsentRequirement: p.RequiresConsent
                ? ConsentRequirement.Genetic   // Flagged as "needs a specific consent"; the exact
                                               // purpose is resolved per component at registration.
                : ConsentRequirement.None,
            IsSubcontracted: false,
            ContainerName: null, ContainerColourHex: null,
            ComponentCount: p.ComponentCount,
            Components: [.. p.Components.Select(c => new PickerComponent(c.AnalysisTestId, c.Price))],
            PackagePrice: p.PackagePrice)));

        items.AddRange(analysisRows.Select(t => new CatalogPickerItem(
            t.Id, IsPanel: false, t.Code, t.Name,
            t.DepartmentCode, t.DepartmentName,
            t.Price,
            FastingHours: (t.FastingHoursOverride ?? t.SpecimenFasting) is var fasting and > 0
                ? fasting : null,
            t.RoutineTurnaroundHours,
            t.IsAvailableUrgently,
            t.ConsentRequirement,
            t.IsSubcontracted,
            t.ContainerName?.Fr, t.ContainerColour,
            t.ParameterCount)));

        // Panels first: they are what a prescriber most often writes.
        return items;
    }

    public async Task<IReadOnlyList<DepartmentOption>> GetDepartmentsAsync(
        CancellationToken ct = default) =>
        await db.Departments.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Code)
            .Select(d => new DepartmentOption(d.Id, d.Code, d.Name, d.DisplayOrder))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetUnverifiedCatalogueEntriesAsync(
        CancellationToken ct = default)
    {
        const string pendingMarker = "À vérifier par le laboratoire";

        var ranges = await db.ReferenceRanges.AsNoTracking()
            .Where(r => r.IsActive && r.Source == pendingMarker)
            .Join(db.TestParameters.AsNoTracking(), r => r.TestParameterId, p => p.Id,
                  (r, p) => p.Code)
            .Distinct()
            .OrderBy(code => code)
            .ToListAsync(ct);

        var placeholderTariffs = await db.Tariffs.AsNoTracking()
            // 1.00 is the seeded placeholder letter-key value; a real TNR value is never exactly it.
            .Where(t => t.LetterKeyValue == 1.00m && t.ValidUntil == null)
            .Join(db.AnalysisTests.AsNoTracking(), t => t.AnalysisTestId, a => a.Id,
                  (t, a) => a.Code)
            .Distinct()
            .OrderBy(code => code)
            .ToListAsync(ct);

        return
        [
            .. ranges.Select(c => $"Valeurs usuelles à valider : {c}"),
            .. placeholderTariffs.Select(c => $"Tarif provisoire (lettre-clé non confirmée) : {c}")
        ];
    }
}
