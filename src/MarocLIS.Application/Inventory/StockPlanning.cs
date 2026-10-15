using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// The stock parameters held as database settings (<c>AppSetting</c>, installation-wide): the
/// variance that flags a theoretical-against-real gap, the consumption window the averages are
/// taken over, and the two expiry alert levels. A key absent or unreadable falls back to the
/// default here.
/// </summary>
public sealed record StockSettings
{
    public const string Prefix = "Stock.";

    public const string VarianceThresholdPercentKey = "Stock.ConsumptionVarianceThresholdPercent";
    public const string ConsumptionWindowDaysKey = "Stock.ConsumptionWindowDays";
    public const string ExpiryWarningDaysKey = "Stock.ExpiryWarningDays";
    public const string ExpiryCriticalDaysKey = "Stock.ExpiryCriticalDays";

    /// <summary>Theoretical-against-real variance flagged beyond this share, in percent.</summary>
    public decimal VarianceThresholdPercent { get; init; } = 10m;

    /// <summary>Days of history the average daily consumption is taken over.</summary>
    public int ConsumptionWindowDays { get; init; } = 90;

    /// <summary>First expiry alert level (J−30).</summary>
    public int ExpiryWarningDays { get; init; } = 30;

    /// <summary>Second expiry alert level (J−7).</summary>
    public int ExpiryCriticalDays { get; init; } = 7;

    public static StockSettings Default { get; } = new();

    public Result Validate() =>
        VarianceThresholdPercent is < 0m or > 100m || ConsumptionWindowDays is < 7 or > 730
        || ExpiryWarningDays is < 1 or > 365 || ExpiryCriticalDays < 0 || ExpiryCriticalDays >= ExpiryWarningDays
            ? Result.Failure(Error.Validation("Stock_SettingOutOfRange"))
            : Result.Success();

    public static StockSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        var d = Default;

        decimal Number(string key, decimal fallback) =>
            values.TryGetValue(key, out var text) && text is not null
            && decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture,
                                out var value) && value >= 0m
                ? value
                : fallback;

        int Days(string key, int fallback) =>
            values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer,
                                                                  CultureInfo.InvariantCulture, out var value)
            && value >= 0
                ? value
                : fallback;

        var settings = new StockSettings
        {
            VarianceThresholdPercent = Number(VarianceThresholdPercentKey, d.VarianceThresholdPercent),
            ConsumptionWindowDays = Days(ConsumptionWindowDaysKey, d.ConsumptionWindowDays),
            ExpiryWarningDays = Days(ExpiryWarningDaysKey, d.ExpiryWarningDays),
            ExpiryCriticalDays = Days(ExpiryCriticalDaysKey, d.ExpiryCriticalDays)
        };

        // A stored combination that makes no sense (J−7 after J−30) is not half-applied.
        return settings.Validate().IsSuccess ? settings : d;
    }

    public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [VarianceThresholdPercentKey] = VarianceThresholdPercent.ToString("0.##", CultureInfo.InvariantCulture),
        [ConsumptionWindowDaysKey] = ConsumptionWindowDays.ToString(CultureInfo.InvariantCulture),
        [ExpiryWarningDaysKey] = ExpiryWarningDays.ToString(CultureInfo.InvariantCulture),
        [ExpiryCriticalDaysKey] = ExpiryCriticalDays.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>
/// What the stock and the activity say about one item over a period: the analyses' activity, the
/// movements, the stock at both ends. Read in a few set-based queries for every item of the site.
/// </summary>
/// <param name="Issued">Units issued or consumed in the period, returns deducted.</param>
/// <param name="Receipts">Units received in the period (deliveries and transfers in).</param>
/// <param name="Opening">Physical stock at the start of the period.</param>
/// <param name="Closing">Physical stock at the end of the period.</param>
/// <param name="CountNumber">The last inventory closed in the period that counted the item.</param>
/// <param name="Usable">Usable stock now: released, in date, within open stability.</param>
public sealed record ItemConsumptionData(
    Guid StockItemId,
    string Code,
    MultilingualText Name,
    StockItemKind Kind,
    string UnitOfMeasure,
    int? TestsPerPack,
    StockConsumptionPolicy Policy,
    IReadOnlyList<string> AnalysisCodes,
    IReadOnlyList<AnalysisActivity> Activity,
    decimal Issued,
    decimal Receipts,
    decimal Opening,
    decimal Closing,
    string? CountNumber,
    int LeadTimeDays,
    decimal MinimumStock,
    decimal ReorderPoint,
    decimal Usable,
    bool IsCritical = false);

/// <summary>One line of the "Consommation" screen.</summary>
public sealed record ConsumptionRow(
    Guid StockItemId,
    string Code,
    MultilingualText Name,
    StockItemKind Kind,
    string UnitOfMeasure,
    ConsumptionBreakdown Breakdown,
    StockReconciliation Reconciliation,
    decimal RealIssued,
    string? CountNumber,
    IReadOnlyList<AnalysisActivity> Activity);

/// <summary>A lot expiring within the alert levels.</summary>
public sealed record ExpiringLotRow(
    Guid ReagentLotId,
    string ItemCode,
    MultilingualText ItemName,
    string LotNumber,
    DateOnly ExpiryDate,
    decimal QuantityRemaining,
    string UnitOfMeasure,
    ExpiryAlertLevel Level);

/// <summary>An item whose usable stock runs out before an order placed today would arrive.</summary>
public sealed record StockOutForecastRow(
    Guid StockItemId,
    string Code,
    MultilingualText Name,
    decimal Usable,
    string UnitOfMeasure,
    decimal AverageDailyConsumption,
    DateOnly StockOutDate,
    int LeadTimeDays,
    bool IsCritical);

public sealed record StockAlertSummary(
    IReadOnlyList<ExpiringLotRow> ExpiringLots,
    IReadOnlyList<StockOutForecastRow> StockOuts,
    StockSettings Settings);

/// <summary>A reorder point suggestion for an item, as the item editor shows it.</summary>
public sealed record ReorderPointProposal(ReorderSuggestion Suggestion, decimal CurrentReorderPoint, int WindowDays);

/// <summary>Read side of the consumption, the alerts and the computed reorder points.</summary>
public interface IStockPlanningQueries
{
    /// <summary>The consumption data of the site's active items over a period (both ends included).</summary>
    Task<IReadOnlyList<ItemConsumptionData>> GetConsumptionDataAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                    DateOnly today,
                                                                    IReadOnlyCollection<Guid>? stockItemIds,
                                                                    CancellationToken ct = default);

    /// <summary>Lots with stock left, not rejected or recalled, expiring by the horizon (expired included).</summary>
    Task<IReadOnlyList<ExpiringLotRow>> GetExpiringLotsAsync(Guid siteId, DateOnly today, int horizonDays,
                                                            CancellationToken ct = default);

    /// <summary>The items whose reorder point is computed.</summary>
    Task<IReadOnlyList<Guid>> GetComputedReorderItemsAsync(Guid siteId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);
}

/// <summary>
/// The arithmetic shared by the service and the dashboard: averages, forecasts, reconciliation.
/// </summary>
public static class StockPlanningCalculator
{
    public static ConsumptionRow Row(ItemConsumptionData data, decimal thresholdPercent)
    {
        var breakdown = data.AnalysisCodes.Count == 0
            ? ConsumptionBreakdown.None
            : TheoreticalConsumption.Compute(data.Policy, data.TestsPerPack, data.Activity,
                                             data.Issued > 0m ? data.Issued : null);

        var reconciliation = new StockReconciliation(data.Opening, data.Receipts, breakdown.Units, data.Closing,
                                                     thresholdPercent, data.AnalysisCodes.Count > 0);

        return new ConsumptionRow(data.StockItemId, data.Code, data.Name, data.Kind, data.UnitOfMeasure, breakdown,
                                  reconciliation, data.Issued, data.CountNumber, data.Activity);
    }

    /// <summary>Average daily consumption over the window the data was read for.</summary>
    public static (decimal Daily, bool FromTheoretical)? AverageDaily(ItemConsumptionData data, int windowDays)
    {
        var theoretical = data.AnalysisCodes.Count == 0
            ? 0m
            : TheoreticalConsumption.Compute(data.Policy, data.TestsPerPack, data.Activity).Units;

        return ReorderPolicy.AverageDailyConsumption(data.Issued, theoretical, windowDays);
    }

    public static ReorderSuggestion? Suggest(ItemConsumptionData data, int windowDays) =>
        AverageDaily(data, windowDays) is { } average
            ? ReorderPolicy.Suggest(average.Daily, average.FromTheoretical, data.LeadTimeDays,
                                    data.Policy.SafetyStockDays, data.MinimumStock)
            : null;

    public static IReadOnlyList<StockOutForecastRow> StockOuts(IEnumerable<ItemConsumptionData> data,
                                                               int windowDays, DateOnly today) =>
    [
        .. data.Select(d => (Data: d, Average: AverageDaily(d, windowDays)))
               .Where(x => x.Average is not null)
               .Select(x => (x.Data, Daily: x.Average!.Value.Daily,
                             Out: StockAlerts.PredictedStockOut(x.Data.Usable, x.Average!.Value.Daily, today)))
               .Where(x => StockAlerts.RunsOutBeforeDelivery(x.Out, today, x.Data.LeadTimeDays))
               .OrderBy(x => x.Out)
               .Select(x => new StockOutForecastRow(x.Data.StockItemId, x.Data.Code, x.Data.Name, x.Data.Usable,
                                                    x.Data.UnitOfMeasure, x.Daily, x.Out!.Value,
                                                    x.Data.LeadTimeDays, x.Data.IsCritical))
    ];
}

/// <summary>
/// Stock and consumption: the theoretical consumption the activity explains against the real one,
/// the reorder points computed from it, the stock-out forecast and the expiry alerts.
/// </summary>
public sealed class StockPlanningService(
    IStockPlanningQueries queries,
    IRepository<StockItem> items,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock)
{
    // The day each site's computed reorder points were last refreshed in this process: the
    // planner runs after every stock operation, the recomputation needs to run once a day.
    private static readonly ConcurrentDictionary<Guid, DateOnly> Refreshed = new();

    private async Task<StockSettings> SettingsCoreAsync(CancellationToken ct) =>
        StockSettings.FromValues(await queries.GetSettingValuesAsync(StockSettings.Prefix, ct));

    public async Task<Result<StockSettings>> GetSettingsAsync(CancellationToken ct = default)
    {
        var permitted = guard.RequireAny(Permissions.Inventory.View, Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return Result.Failure<StockSettings>(permitted.FirstError);

        return Result.Success(await SettingsCoreAsync(ct));
    }

    public async Task<Result> SaveSettingsAsync(StockSettings settings, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        var valid = settings.Validate();
        if (valid.IsFailure) return valid;

        var changed = new List<string>();
        foreach (var (key, value) in settings.ToValues())
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                await settingsWriter.AddSettingAsync(new AppSetting(key, value)
                {
                    Description = "Stock : écart de consommation, fenêtre de calcul et alertes de péremption"
                }, ct);
                changed.Add(key);
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(key);
            }
        }

        if (changed.Count == 0) return Result.Success();

        unitOfWork.AuditReason = $"Paramètres de stock modifiés : {string.Join(", ", changed)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Theoretical against real consumption of every item over a period.</summary>
    public async Task<Result<IReadOnlyList<ConsumptionRow>>> GetConsumptionAsync(DateOnly from, DateOnly to,
                                                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ConsumptionRow>>(permitted.FirstError);

        if (to < from) return Result.Failure<IReadOnlyList<ConsumptionRow>>(Error.Validation("Stock_PeriodInvalid"));
        if (to > clock.Today) to = clock.Today;

        var settings = await SettingsCoreAsync(ct);
        var data = await queries.GetConsumptionDataAsync(currentUser.SiteId, from, to, clock.Today, null, ct);

        return Result.Success<IReadOnlyList<ConsumptionRow>>(
            [.. data.Select(d => StockPlanningCalculator.Row(d, settings.VarianceThresholdPercent))
                    .OrderByDescending(r => r.Reconciliation.IsFlagged)
                    .ThenBy(r => r.Code, StringComparer.Ordinal)]);
    }

    /// <summary>The reorder point the consumption calls for, for the item editor.</summary>
    public async Task<Result<ReorderPointProposal?>> ProposeReorderPointAsync(Guid stockItemId,
                                                                             CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<ReorderPointProposal?>(permitted.FirstError);

        var settings = await SettingsCoreAsync(ct);
        var (from, to) = Window(settings);
        var found = await queries.GetConsumptionDataAsync(currentUser.SiteId, from, to, clock.Today, [stockItemId], ct);
        if (found.Count == 0) return Result.Success<ReorderPointProposal?>(null);
        var data = found[0];

        var suggestion = StockPlanningCalculator.Suggest(data, settings.ConsumptionWindowDays);
        return Result.Success(suggestion is null
            ? null
            : new ReorderPointProposal(suggestion, data.ReorderPoint, settings.ConsumptionWindowDays));
    }

    /// <summary>The average daily consumption of items, for the reorder planner's shelf-life cap.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> AverageDailyConsumptionAsync(
        IReadOnlyCollection<Guid> stockItemIds, CancellationToken ct = default)
    {
        if (stockItemIds.Count == 0) return new Dictionary<Guid, decimal>();

        var settings = await SettingsCoreAsync(ct);
        var (from, to) = Window(settings);
        var data = await queries.GetConsumptionDataAsync(currentUser.SiteId, from, to, clock.Today, stockItemIds, ct);

        return data.Select(d => (d.StockItemId, Average: StockPlanningCalculator.AverageDaily(d, settings.ConsumptionWindowDays)))
                   .Where(x => x.Average is not null)
                   .ToDictionary(x => x.StockItemId, x => x.Average!.Value.Daily);
    }

    /// <summary>
    /// Recomputes the reorder point of the items that follow their consumption, once a day per site
    /// (or when forced). Run by the reorder planner before it looks for items to order. A system
    /// step, not an operator's: it needs no right, and records what it changed in the audit trail.
    /// </summary>
    /// <returns>The codes of the items whose reorder point changed.</returns>
    public async Task<IReadOnlyList<string>> RefreshComputedReorderPointsAsync(bool force = false,
                                                                              CancellationToken ct = default)
    {
        var site = currentUser.SiteId;
        if (!force && Refreshed.TryGetValue(site, out var last) && last == clock.Today) return [];

        var ids = await queries.GetComputedReorderItemsAsync(site, ct);
        Refreshed[site] = clock.Today;
        if (ids.Count == 0) return [];

        var settings = await SettingsCoreAsync(ct);
        var (from, to) = Window(settings);
        var data = await queries.GetConsumptionDataAsync(site, from, to, clock.Today, ids, ct);

        var changed = new List<string>();
        foreach (var d in data)
        {
            if (StockPlanningCalculator.Suggest(d, settings.ConsumptionWindowDays) is not { } suggestion
                || suggestion.ReorderPoint == d.ReorderPoint)
                continue;

            var item = await items.GetByIdAsync(d.StockItemId, ct);
            if (item is null || !item.ReorderPointComputed) continue;

            item.ReorderPoint = suggestion.ReorderPoint;
            items.Update(item);
            changed.Add($"{item.Code} {d.ReorderPoint:0.###} → {suggestion.ReorderPoint:0.###}");
        }

        if (changed.Count == 0) return [];

        unitOfWork.AuditReason =
            $"Seuils de réapprovisionnement recalculés (consommation moyenne × délai fournisseur + stock de sécurité) : {string.Join(", ", changed)}";
        await unitOfWork.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Expiry alerts at both levels and the stock-out forecast.</summary>
    public async Task<Result<StockAlertSummary>> GetAlertsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Inventory.View);
        if (permitted.IsFailure) return Result.Failure<StockAlertSummary>(permitted.FirstError);

        var settings = await SettingsCoreAsync(ct);
        var expiring = await queries.GetExpiringLotsAsync(currentUser.SiteId, clock.Today,
                                                          settings.ExpiryWarningDays, ct);

        var levelled = expiring
            .Select(l => l with
            {
                Level = StockAlerts.ExpiryLevel(l.ExpiryDate, clock.Today, settings.ExpiryWarningDays,
                                                                 settings.ExpiryCriticalDays)
            })
            .OrderByDescending(l => l.Level).ThenBy(l => l.ExpiryDate)
            .ToList();

        var (from, to) = Window(settings);
        var data = await queries.GetConsumptionDataAsync(currentUser.SiteId, from, to, clock.Today, null, ct);

        return Result.Success(new StockAlertSummary(levelled,
                                              StockPlanningCalculator.StockOuts(data, settings.ConsumptionWindowDays,
                                                                                clock.Today),
                                              settings));
    }

    private (DateOnly From, DateOnly To) Window(StockSettings settings) =>
        (clock.Today.AddDays(-settings.ConsumptionWindowDays), clock.Today.AddDays(-1));
}

/// <summary>CSV exports of the stock screens: semicolon-separated, with a BOM, as the other purchasing exports.</summary>
public static class StockCsv
{
    public static byte[] Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(';', headers.Select(Cell)));
        foreach (var row in rows) csv.AppendLine(string.Join(';', row.Select(Cell)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    /// <summary>The cells of one consumption line, shared by the grid, the CSV and the PDF.</summary>
    public static IReadOnlyList<string> ConsumptionCells(ConsumptionRow r)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var computable = r.Reconciliation.Computable;
        string N(decimal v) => v.ToString("0.###", fr);

        return
        [
            r.Code, r.Name.Fr, r.UnitOfMeasure,
            N(r.Reconciliation.Opening),
            N(r.Reconciliation.Receipts),
            computable ? N(r.Breakdown.TotalTests) : "—",
            computable ? N(r.Reconciliation.TheoreticalConsumption) : "—",
            N(r.Reconciliation.RealConsumption),
            computable ? N(r.Reconciliation.TheoreticalClosing) : "—",
            N(r.Reconciliation.RealClosing),
            computable ? N(r.Reconciliation.Variance) : "—",
            computable && r.Reconciliation.VariancePercent is { } p ? p.ToString("0.#", fr) + " %" : "—",
            r.CountNumber ?? string.Empty
        ];
    }

    /// <summary>Guards a cell against the separator, quotes, line breaks and spreadsheet formulas.</summary>
    public static string Cell(string? value) => CsvCell.Of(value);
}
