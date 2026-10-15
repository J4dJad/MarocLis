using System.Globalization;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Steering;

/// <summary>A reagent item and what it costs, as the cost model reads it.</summary>
public sealed record CostReagentItem(
    Guid StockItemId,
    Guid SiteId,
    string Code,
    string? UsedByAnalysisCodesCsv,
    string? TestsPerResultCsv,
    int? TestsPerPack,
    decimal? DeadVolumePercent,
    decimal? LastUnitPriceMad,
    decimal? LatestLotUnitPriceMad);

/// <summary>A captive reagent of a contract in force: its contracted price per unit and per test.</summary>
public sealed record CostContractReagent(
    Guid SiteId,
    Guid StockItemId,
    IReadOnlyList<Guid> InstrumentIds,
    decimal? ContractPriceMad,
    decimal? CostPerTestMad);

/// <summary>One performed test as the margin needs it.</summary>
public sealed record CostActivityRow(
    Guid SiteId,
    Guid AnalysisTestId,
    string AnalysisCode,
    Guid? InstrumentId,
    Guid? ReagentLotId,
    decimal NetPrice);

public sealed record CostLabels(
    IReadOnlyDictionary<Guid, (string Code, MultilingualText Name)> Analyses,
    IReadOnlyDictionary<Guid, (string Code, MultilingualText Name)> Sites,
    IReadOnlyDictionary<Guid, (string Code, MultilingualText Name)> Instruments);

/// <summary>Where a test's reagent cost came from, worst case of its reagents.</summary>
public enum CostBasis
{
    /// <summary>The contract states a cost per test.</summary>
    ContractPerTest = 0,
    /// <summary>Unit price of the lot used, or of the last lot received, over the tests per pack.</summary>
    LotPrice = 1,
    /// <summary>The item's last ordered price (or the contract's unit price) over the tests per pack.</summary>
    LastPrice = 2,
    /// <summary>No reagent declared for the analysis, or one without price or tests per pack.</summary>
    Unknown = 3
}

public sealed record TestCost(decimal Amount, CostBasis Basis);

/// <summary>
/// The reagent cost of one test.
/// <para>
/// For each reagent item of the site that lists the analysis among the analyses it serves: the
/// contract's cost per test when a contract in force states one (the contract of the analyser that
/// ran the test first); otherwise the unit price — of the lot recorded on the result, else of the
/// last lot received, else the last ordered price, else the contract's unit price — divided by the
/// tests a pack gives, times the item's consumption factor for the analysis, and grossed up by its
/// dead volume. Controls, calibrators and consumables are not counted per test: they belong to the
/// allocation.
/// </para>
/// </summary>
public sealed class ReagentCostModel
{
    private readonly Dictionary<(Guid Site, string Code), List<CostReagentItem>> _byAnalysis = new();
    private readonly Dictionary<Guid, List<CostContractReagent>> _contracts;
    private readonly IReadOnlyDictionary<Guid, (Guid StockItemId, decimal? UnitPriceMad)> _lots;
    private readonly Dictionary<(Guid, string, Guid?, Guid?), TestCost> _cache = new();

    public ReagentCostModel(IEnumerable<CostReagentItem> reagents, IEnumerable<CostContractReagent> contracts,
                            IReadOnlyDictionary<Guid, (Guid StockItemId, decimal? UnitPriceMad)> lots)
    {
        foreach (var item in reagents)
            foreach (var code in Codes(item.UsedByAnalysisCodesCsv))
            {
                var key = (item.SiteId, code);
                if (!_byAnalysis.TryGetValue(key, out var list)) _byAnalysis[key] = list = [];
                list.Add(item);
            }

        _contracts = contracts.GroupBy(c => c.StockItemId).ToDictionary(g => g.Key, g => g.ToList());
        _lots = lots;
    }

    private static IEnumerable<string> Codes(string? csv) =>
        (csv ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n', '|'],
                                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(c => c.ToUpperInvariant()).Distinct();

    public TestCost CostOf(Guid siteId, string analysisCode, Guid? instrumentId, Guid? reagentLotId)
    {
        var code = analysisCode.Trim().ToUpperInvariant();
        var key = (siteId, code, instrumentId, reagentLotId);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        if (!_byAnalysis.TryGetValue((siteId, code), out var items) || items.Count == 0)
            return _cache[key] = new TestCost(0m, CostBasis.Unknown);

        var total = 0m;
        var basis = CostBasis.ContractPerTest;

        foreach (var item in items)
        {
            var factor = ConsumptionFactors.For(item.TestsPerResultCsv, code);

            var contract = _contracts.TryGetValue(item.StockItemId, out var candidates)
                ? candidates.Where(c => c.SiteId == siteId)
                            .OrderByDescending(c => instrumentId is { } i && c.InstrumentIds.Contains(i))
                            .ThenByDescending(c => c.CostPerTestMad is not null)
                            .FirstOrDefault()
                : null;

            if (contract?.CostPerTestMad is { } perTest)
            {
                total += perTest * factor;
                continue;
            }

            decimal? price = null;
            var itemBasis = CostBasis.LotPrice;

            if (reagentLotId is { } lotId && _lots.TryGetValue(lotId, out var lot) && lot.StockItemId == item.StockItemId)
                price = lot.UnitPriceMad;
            price ??= item.LatestLotUnitPriceMad;
            if (price is null)
            {
                itemBasis = CostBasis.LastPrice;
                price = item.LastUnitPriceMad ?? contract?.ContractPriceMad;
            }

            if (price is not { } unitPrice || item.TestsPerPack is not > 0)
            {
                basis = CostBasis.Unknown;
                continue;
            }

            var usable = 1m - Math.Clamp(item.DeadVolumePercent ?? 0m, 0m, 99m) / 100m;
            total += unitPrice / item.TestsPerPack.Value * factor / usable;
            if (itemBasis > basis) basis = itemBasis;
        }

        return _cache[key] = new TestCost(Math.Round(total, 4), basis);
    }
}

/// <summary>Cost and margin of one group of tests (an analysis, a site, an analyser).</summary>
public sealed record MarginRow(
    string Code,
    string Name,
    int Tests,
    decimal Revenue,
    decimal ReagentCost,
    decimal Allocation,
    decimal AverageNetPrice,
    decimal CostPerTest,
    decimal MarginPerTest,
    decimal Margin,
    double? MarginPercent,
    int TestsWithoutCost);

public sealed record MarginTotals(int Tests, decimal Revenue, decimal ReagentCost, decimal Allocation, decimal Margin,
                                  double? MarginPercent, int TestsWithoutCost);

public sealed record MarginAnalysis(
    DateOnly From,
    DateOnly To,
    decimal AllocationPerTest,
    MarginTotals Totals,
    IReadOnlyList<MarginRow> ByAnalysis,
    IReadOnlyList<MarginRow> BySite,
    IReadOnlyList<MarginRow> ByInstrument);

/// <summary>The cost parameters, held as installation settings.</summary>
public sealed record CostSettings
{
    public const string Prefix = "Steering.Cost.";
    public const string AllocationPerTestKey = "Steering.Cost.AllocationPerTestMad";

    /// <summary>
    /// Indirect costs spread over each test, in dirhams: controls, calibrators, consumables,
    /// maintenance, the analyser's rent. Zero leaves the margin a margin on reagents.
    /// </summary>
    public decimal AllocationPerTestMad { get; init; }

    public static CostSettings FromValues(IReadOnlyDictionary<string, string?> values) => new()
    {
        AllocationPerTestMad = values.TryGetValue(AllocationPerTestKey, out var text) && text is not null
                               && decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number,
                                                   CultureInfo.InvariantCulture, out var value) && value >= 0m
            ? value
            : 0m
    };
}

/// <summary>
/// Cost price and margin per analysis, per site and per analyser over a period: what was charged
/// (net of discounts, at the price frozen on the order) against the reagents the tests consumed and
/// the optional allocation of indirect costs. Tests sent to a partner laboratory are left out: their
/// cost is the partner's invoice, not a reagent.
/// </summary>
public sealed class CostMarginService(
    ICostQueries queries,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int MaximumPeriodDays = 366;

    public async Task<Result<MarginAnalysis>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Steering.View);
        if (permitted.IsFailure) return Result.Failure<MarginAnalysis>(permitted.FirstError);
        if (to < from) return Result.Failure<MarginAnalysis>(Error.Validation("Stats_PeriodReversed"));
        if (to.DayNumber - from.DayNumber + 1 > MaximumPeriodDays)
            return Result.Failure<MarginAnalysis>(Error.Validation("Stats_PeriodTooLong", MaximumPeriodDays));

        return Result.Success(await ComputeAsync(from, to, ct));
    }

    /// <summary>Without the permission check, for the steering dashboard that already made it.</summary>
    internal async Task<MarginAnalysis> ComputeAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var offset = clock.Now.Offset;
        var sites = Sites(currentUser);
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), offset);

        var activity = await queries.GetActivityAsync(sites, start, end, ct);
        var reagents = await queries.GetReagentsAsync(sites, to, ct);
        var contracts = await queries.GetContractReagentsAsync(sites, to, ct);
        var lots = await queries.GetLotPricesAsync([.. activity.Where(a => a.ReagentLotId is not null)
                                                               .Select(a => a.ReagentLotId!.Value).Distinct()], ct);
        var labels = await queries.GetLabelsAsync(ct);
        var settings = CostSettings.FromValues(await queries.GetSettingValuesAsync(CostSettings.Prefix, ct));

        var model = new ReagentCostModel(reagents, contracts, lots);
        return Compute(from, to, activity, model, settings.AllocationPerTestMad, labels, currentUser.Language);
    }

    internal static IReadOnlyList<Guid> Sites(ICurrentUser user) =>
        user.AccessibleSiteIds.Count > 0 ? user.AccessibleSiteIds : [user.SiteId];

    public static MarginAnalysis Compute(DateOnly from, DateOnly to, IReadOnlyList<CostActivityRow> activity,
                                         ReagentCostModel model, decimal allocationPerTest, CostLabels labels,
                                         AppLanguage language)
    {
        var costed = activity
            .Select(a => (Row: a, Cost: model.CostOf(a.SiteId, a.AnalysisCode, a.InstrumentId, a.ReagentLotId)))
            .ToList();

        List<MarginRow> Group<TKey>(Func<CostActivityRow, TKey> key,
                                    Func<TKey, (string Code, string Name)> label) where TKey : notnull =>
            costed.GroupBy(c => key(c.Row))
                  .Select(g => Row(label(g.Key), [.. g]))
                  .OrderByDescending(r => r.Revenue).ThenBy(r => r.Code, StringComparer.Ordinal)
                  .ToList();

        MarginRow Row((string Code, string Name) label, IReadOnlyList<(CostActivityRow Row, TestCost Cost)> rows)
        {
            var tests = rows.Count;
            var revenue = Money.Round(rows.Sum(r => r.Row.NetPrice));
            var reagent = Money.Round(rows.Sum(r => r.Cost.Amount));
            var allocation = Money.Round(allocationPerTest * tests);
            var margin = revenue - reagent - allocation;

            return new MarginRow(label.Code, label.Name, tests, revenue, reagent, allocation,
                                 tests == 0 ? 0m : Money.Round(revenue / tests),
                                 tests == 0 ? 0m : Money.Round((reagent + allocation) / tests),
                                 tests == 0 ? 0m : Money.Round(margin / tests),
                                 margin,
                                 revenue == 0m ? null : Math.Round((double)(margin / revenue * 100m), 1),
                                 rows.Count(r => r.Cost.Basis == CostBasis.Unknown));
        }

        (string, string) Label(IReadOnlyDictionary<Guid, (string Code, MultilingualText Name)> names, Guid? id) =>
            id is { } key && names.TryGetValue(key, out var found) ? (found.Code, found.Name.For(language)) : ("—", "—");

        var total = Row(("", ""), costed);

        return new MarginAnalysis(
            from, to, allocationPerTest,
            new MarginTotals(total.Tests, total.Revenue, total.ReagentCost, total.Allocation, total.Margin,
                             total.MarginPercent, total.TestsWithoutCost),
            Group(r => r.AnalysisTestId, id => Label(labels.Analyses, id)),
            Group(r => r.SiteId, id => Label(labels.Sites, id)),
            // A manual bench result has no analyser: grouped under Guid.Empty, shown as "—".
            Group(r => r.InstrumentId ?? Guid.Empty, id => Label(labels.Instruments, id == Guid.Empty ? null : id)));
    }

    // ---- Settings ------------------------------------------------------------------------------------

    public async Task<Result<CostSettings>> GetSettingsAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Steering.View, Permissions.Steering.ManageCosts);
        if (permitted.IsFailure) return Result.Failure<CostSettings>(permitted.FirstError);
        return Result.Success(CostSettings.FromValues(await queries.GetSettingValuesAsync(CostSettings.Prefix, ct)));
    }

    public async Task<Result> SaveSettingsAsync(CostSettings settings, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Steering.ManageCosts);
        if (permitted.IsFailure) return permitted;
        if (settings.AllocationPerTestMad is < 0m or > 100000m)
            return Result.Failure(Error.Validation("Steering_AllocationOutOfRange"));

        var value = Money.Round(settings.AllocationPerTestMad).ToString(CultureInfo.InvariantCulture);
        var existing = await settingsWriter.GetSettingAsync(CostSettings.AllocationPerTestKey, null, ct);
        if (existing is null)
            await settingsWriter.AddSettingAsync(new AppSetting(CostSettings.AllocationPerTestKey, value)
            {
                Description = "Pilotage : frais indirects répartis par test (MAD)"
            }, ct);
        else if (existing.Value == value) return Result.Success();
        else existing.Value = value;

        unitOfWork.AuditReason = $"Frais indirects par test fixés à {value} MAD";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public interface ICostQueries
{
    /// <summary>
    /// Tests performed on orders registered in <c>[start, end)</c> on the given sites: cancelled tests
    /// and tests sent to a partner laboratory left out, with the analyser and the reagent lot of
    /// their first result that records one.
    /// </summary>
    Task<IReadOnlyList<CostActivityRow>> GetActivityAsync(IReadOnlyList<Guid> siteIds, DateTimeOffset start,
                                                          DateTimeOffset end, CancellationToken ct = default);

    /// <summary>Reagent items of the sites, with the price of their last priced lot received by <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<CostReagentItem>> GetReagentsAsync(IReadOnlyList<Guid> siteIds, DateOnly asOf,
                                                          CancellationToken ct = default);

    /// <summary>Captive reagents of the contracts in force on <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<CostContractReagent>> GetContractReagentsAsync(IReadOnlyList<Guid> siteIds, DateOnly asOf,
                                                                      CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, (Guid StockItemId, decimal? UnitPriceMad)>> GetLotPricesAsync(
        IReadOnlyList<Guid> lotIds, CancellationToken ct = default);

    Task<CostLabels> GetLabelsAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);
}
