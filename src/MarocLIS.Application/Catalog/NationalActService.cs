using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Catalog;

// ---- Rows --------------------------------------------------------------------------------------

/// <summary>An act of the national nomenclature, with the number of catalogue analyses citing it.</summary>
public sealed record NationalActRow(
    Guid Id, string Code, string Label, decimal Coefficient, string LetterKey,
    string Chapter, string? SubChapter, int DecreeYear, bool IsActive, int AnalysisCount = 0);

/// <summary>A tariff in force, as the nomenclature check reads it.</summary>
public sealed record NomenclatureTariffFact(Guid? PayerId, string LetterKey, decimal Coefficient);

/// <summary>An active catalogue analysis with its national code and the tariffs in force today.</summary>
public sealed record NomenclatureAnalysisFact(
    Guid Id, string Code, MultilingualText Name, string? NationalActCode,
    IReadOnlyList<NomenclatureTariffFact> TariffsInForce);

/// <summary>Why an analysis is listed by the « Écarts avec la nomenclature » check.</summary>
public enum NomenclatureGapKind
{
    /// <summary>No national code: an act outside the nomenclature, or one never coded.</summary>
    MissingCode = 0,

    /// <summary>A code the nomenclature does not know (typing slip, old code, internal code).</summary>
    UnknownCode = 1,

    /// <summary>A tariff in force whose letter key or coefficient is not the official one.</summary>
    CoefficientDiffers = 2
}

/// <summary>
/// One line of the check. <see cref="CurrentCoefficient"/> is the laboratory price list's when it
/// has one in force, otherwise the first differing tariff's; <see cref="DifferingTariffCount"/>
/// counts every tariff in force (laboratory list and payers) the official coefficient would change.
/// </summary>
public sealed record NomenclatureGapRow(
    Guid AnalysisId, string AnalysisCode, MultilingualText AnalysisName, string? NationalActCode,
    NomenclatureGapKind Kind, string? ActLabel, decimal? OfficialCoefficient,
    decimal? CurrentCoefficient, int DifferingTariffCount)
{
    /// <summary>Only a coefficient gap can be put right from the check; the others need the editor.</summary>
    public bool CanApplyOfficialCoefficient => Kind == NomenclatureGapKind.CoefficientDiffers;
}

/// <summary>What applying the official coefficient changed.</summary>
public sealed record OfficialCoefficientOutcome(int NewVersions, int Corrected, int SkippedFutureDated);

// ---- Pure rules --------------------------------------------------------------------------------

/// <summary>The comparison of the catalogue with the nomenclature, kept free of storage so it is tested alone.</summary>
public static class NomenclatureGaps
{
    /// <summary>A tariff that does not carry the official letter key and coefficient.</summary>
    public static bool Differs(NomenclatureTariffFact tariff, decimal officialCoefficient) =>
        !string.Equals(tariff.LetterKey, NationalBiologyAct.BiologyLetterKey, StringComparison.OrdinalIgnoreCase)
        || tariff.Coefficient != officialCoefficient;

    public static IReadOnlyList<NomenclatureGapRow> Compute(
        IEnumerable<NomenclatureAnalysisFact> analyses, IReadOnlyDictionary<string, NationalActRow> acts)
    {
        var gaps = new List<NomenclatureGapRow>();

        foreach (var analysis in analyses)
        {
            var lab = analysis.TariffsInForce.FirstOrDefault(t => t.PayerId is null);
            var code = NationalBiologyAct.Normalize(analysis.NationalActCode);

            if (code is null)
            {
                gaps.Add(new NomenclatureGapRow(analysis.Id, analysis.Code, analysis.Name, null,
                    NomenclatureGapKind.MissingCode, null, null, lab?.Coefficient, 0));
                continue;
            }

            if (!acts.TryGetValue(code, out var act))
            {
                gaps.Add(new NomenclatureGapRow(analysis.Id, analysis.Code, analysis.Name,
                    analysis.NationalActCode, NomenclatureGapKind.UnknownCode, null, null,
                    lab?.Coefficient, 0));
                continue;
            }

            var differing = analysis.TariffsInForce.Where(t => Differs(t, act.Coefficient)).ToList();
            if (differing.Count == 0) continue;

            gaps.Add(new NomenclatureGapRow(analysis.Id, analysis.Code, analysis.Name, act.Code,
                NomenclatureGapKind.CoefficientDiffers, act.Label, act.Coefficient,
                (lab ?? differing[0]).Coefficient, differing.Count));
        }

        return [.. gaps.OrderBy(g => g.Kind).ThenBy(g => g.AnalysisCode, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The price a tariff takes with a new coefficient. A price that followed the reimbursement base
    /// (coefficient × key value) follows it to the new base; a price the laboratory set apart from
    /// the base is its own decision and is kept — only the base it is compared with changes.
    /// </summary>
    public static decimal PriceWith(decimal price, decimal oldCoefficient, decimal letterKeyValue,
                                    decimal newCoefficient) =>
        price == Money.Round(oldCoefficient * letterKeyValue)
            ? Money.Round(newCoefficient * letterKeyValue)
            : price;
}

/// <summary>How the screens find an act: by code (« 118 », « b118 ») or by label, accents and case ignored.</summary>
public static class NationalActSearch
{
    public static bool Matches(NationalActRow act, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var code = NationalBiologyAct.Normalize(search)!;
        if (act.Code.StartsWith(char.IsDigit(code[0]) ? "B" + code : code, StringComparison.Ordinal)) return true;

        // « glycemie » finds « Glycémie »: the label is matched ignoring case and diacritics.
        var compare = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions loose =
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var text = search.Trim();

        return compare.IndexOf(act.Label, text, loose) >= 0
               || compare.IndexOf(act.Chapter, text, loose) >= 0
               || (act.SubChapter is { } sub && compare.IndexOf(sub, text, loose) >= 0);
    }
}

// ---- Service -----------------------------------------------------------------------------------

/// <summary>
/// The official nomenclature of medical biology acts (NABM): reading it, finding an act for the
/// analysis editor, listing where the catalogue departs from it, and bringing a tariff to the
/// official coefficient.
/// <para>
/// The nomenclature never replaces the laboratory's own analysis codes. It fills
/// <see cref="AnalysisTest.NationalActCode"/> and proposes a coefficient; the internal code stays
/// what the laboratory typed, several analyses may cite one act, and an analysis outside the
/// nomenclature cites none.
/// </para>
/// <para>
/// A coefficient brought to the official value is a new tariff version starting today, as any
/// price revision is: the tariff in force closes yesterday, orders already priced keep their price
/// and issued invoices are not touched. Only a tariff that itself started today is corrected in
/// place, with the replaced values in the audit trail.
/// </para>
/// </summary>
public sealed class NationalActService(
    IRepository<AnalysisTest> analyses,
    INationalActQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<NationalActRow>>> GetActsAsync(
        string? search = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<NationalActRow>>(permitted.FirstError);

        return Result.Success(await queries.GetActsAsync(Trim(search), ct));
    }

    /// <summary>The act a code names, or a not-found failure for a code outside the nomenclature.</summary>
    public async Task<Result<NationalActRow>> FindAsync(string? code, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<NationalActRow>(permitted.FirstError);

        var normalized = NationalBiologyAct.Normalize(code);
        if (normalized is null) return Result.Failure<NationalActRow>(Error.Validation("Nabm_CodeRequired"));

        var act = await queries.FindActAsync(normalized, ct);

        return act is null
            ? Result.Failure<NationalActRow>(Error.NotFound("Nabm_UnknownCode", normalized))
            : Result.Success(act);
    }

    /// <summary>The active analyses whose national code is missing or unknown, or whose tariff departs from it.</summary>
    public async Task<Result<IReadOnlyList<NomenclatureGapRow>>> GetGapsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<NomenclatureGapRow>>(permitted.FirstError);

        var acts = await queries.GetActsAsync(null, ct);
        var facts = await queries.GetAnalysisFactsAsync(clock.Today, ct);

        return Result.Success(NomenclatureGaps.Compute(
            facts, acts.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Brings every tariff in force of an analysis — the laboratory list and each payer — to the
    /// official letter key and coefficient of its national act.
    /// </summary>
    public async Task<Result<OfficialCoefficientOutcome>> ApplyOfficialCoefficientAsync(
        Guid analysisTestId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return Result.Failure<OfficialCoefficientOutcome>(permitted.FirstError);

        var analysis = await analyses.GetByIdWithDetailsAsync(analysisTestId, ct);
        if (analysis is null) return Result.Failure<OfficialCoefficientOutcome>(Error.NotFound("Test_NotFound"));

        var code = NationalBiologyAct.Normalize(analysis.NationalActCode);
        if (code is null)
            return Result.Failure<OfficialCoefficientOutcome>(Error.Validation("Nabm_AnalysisHasNoCode", analysis.Code));

        var act = await queries.FindActAsync(code, ct);
        if (act is null)
            return Result.Failure<OfficialCoefficientOutcome>(Error.NotFound("Nabm_UnknownCode", code));

        var today = clock.Today;
        var official = act.Coefficient;

        var inForce = analysis.Tariffs
            .Where(t => t.IsValidOn(today))
            .GroupBy(t => t.PayerId)
            .Select(g => g.OrderByDescending(t => t.ValidFrom).First())
            .Where(t => NomenclatureGaps.Differs(new NomenclatureTariffFact(t.PayerId, t.LetterKey, t.Coefficient), official))
            .ToList();

        if (inForce.Count == 0)
            return Result.Failure<OfficialCoefficientOutcome>(Error.Conflict("Nabm_AlreadyOfficial", analysis.Code, act.Code));

        // A tariff with no key value has no base to compute: the same refusal as entering it by hand.
        if (inForce.Any(t => t.LetterKeyValue <= 0m))
            return Result.Failure<OfficialCoefficientOutcome>(Error.Regulatory("Tariff_CodedActNeedsBase", act.Code));

        int added = 0, corrected = 0, skipped = 0;
        var changes = new List<string>();

        foreach (var tariff in inForce)
        {
            var price = NomenclatureGaps.PriceWith(tariff.Price, tariff.Coefficient, tariff.LetterKeyValue, official);
            var before = $"{tariff.LetterKey} {tariff.Coefficient:0.##} ({tariff.Price:0.00} MAD)";
            var after = $"{NationalBiologyAct.BiologyLetterKey} {official:0.##} ({price:0.00} MAD)";

            if (tariff.ValidFrom == today)
            {
                // Started today: a second version the same day is refused, so it is corrected.
                tariff.LetterKey = NationalBiologyAct.BiologyLetterKey;
                tariff.Coefficient = official;
                tariff.Price = price;
                corrected++;
            }
            else if (analysis.Tariffs.Any(t => t.PayerId == tariff.PayerId && t.ValidFrom > today))
            {
                // A revision is already scheduled after today: inserting one before it would leave
                // two open-ended prices. Left for the operator, on the Tarifs tab.
                skipped++;
                continue;
            }
            else
            {
                analysis.AddTariff(new AnalysisTestTariff(analysis.Id, tariff.PayerId, today)
                {
                    LetterKey = NationalBiologyAct.BiologyLetterKey,
                    Coefficient = official,
                    LetterKeyValue = tariff.LetterKeyValue,
                    Price = price,
                    IsVatExempt = tariff.IsVatExempt,
                    VatRate = tariff.VatRate
                });
                added++;
            }

            changes.Add($"{before} → {after}");
        }

        if (added + corrected == 0)
            return Result.Failure<OfficialCoefficientOutcome>(Error.Conflict("Nabm_RevisionScheduled", analysis.Code));

        unitOfWork.AuditReason =
            $"Coefficient officiel {act.Code} appliqué à {analysis.Code} à compter du {today:dd/MM/yyyy} : "
            + string.Join(" ; ", changes);

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new OfficialCoefficientOutcome(added, corrected, skipped));
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface INationalActQueries
{
    /// <summary>The acts, by code; filtered on a code or label fragment when a search is given.</summary>
    Task<IReadOnlyList<NationalActRow>> GetActsAsync(string? search, CancellationToken ct = default);

    Task<NationalActRow?> FindActAsync(string code, CancellationToken ct = default);

    /// <summary>The active analyses with their national code and their tariffs in force on <paramref name="today"/>.</summary>
    Task<IReadOnlyList<NomenclatureAnalysisFact>> GetAnalysisFactsAsync(DateOnly today,
                                                                      CancellationToken ct = default);
}
