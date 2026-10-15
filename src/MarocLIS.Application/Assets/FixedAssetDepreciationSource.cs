using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Assets;

/// <summary>One month of an asset's depreciation already sent to the accounting software.</summary>
public sealed record DepreciationPosting(Guid AssetId, int Year, int Month, DateOnly Date, decimal Amount);

/// <summary>One month of an asset in an export.</summary>
/// <param name="Posted">Already exported: given again as it was.</param>
/// <param name="Adjusted">Not the plan's own amount: it carries the difference with what was exported.</param>
public sealed record DepreciationExportMonth(int Year, int Month, DateOnly Date, decimal Amount, bool Posted,
                                             bool Adjusted);

/// <summary>
/// The depreciation entries of the fixed-assets register for the accounting export: one entry per
/// asset and month, dated the last day of the month (or the disposal date in the month of a
/// disposal), the category's charge account (6193) in debit and its depreciation account (28xxx) in
/// credit. A month is exported when its date falls within the period.
/// <para>
/// Each month that goes out in an export file is recorded (<see cref="FixedAssetDepreciationPosting"/>).
/// An asset corrected afterwards — its cost, its dates, its plan, a disposal entered late or
/// cancelled — does not restate those months: they are given again as exported, and the first
/// month not yet exported books the difference between the plan as it now stands and what the
/// books hold (a reversal when it is negative). Months the plan has before the first export of an
/// asset follow the plan, as before.
/// </para>
/// <para>
/// No permission check here: the export that asks has checked its own, and the entries carry
/// amounts and accounts, nothing more.
/// </para>
/// </summary>
public sealed class FixedAssetDepreciationSource(
    IFixedAssetQueries queries,
    IRepository<FixedAssetDepreciationPosting>? postings = null,
    IUnitOfWork? unitOfWork = null,
    IClock? clock = null) : IDepreciationEntrySource, IDepreciationExportRecorder
{
    public async Task<IReadOnlyList<DepreciationEntry>> GetEntriesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                       CancellationToken ct = default)
    {
        if (from > to) return [];

        var settings = FixedAssetSettings.FromValues(await queries.GetSettingValuesAsync(FixedAssetSettings.Prefix, ct));
        var assets = await queries.GetAssetsAsync(siteId, ct);
        var posted = await queries.GetDepreciationPostingsAsync(siteId, ct);

        return Entries(assets, settings, from, to, posted);
    }

    /// <summary>
    /// Records the months an export file has just carried, so that a later correction of an asset
    /// is booked as a difference rather than by rewriting them. Called when the file is produced,
    /// never by the check that precedes it.
    /// </summary>
    public async Task RecordExportedAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (from > to || postings is null || unitOfWork is null) return;

        var assets = await queries.GetAssetsAsync(siteId, ct);
        var posted = (await queries.GetDepreciationPostingsAsync(siteId, ct)).ToLookup(p => p.AssetId);
        var now = clock?.Now ?? DateTimeOffset.Now;

        var added = 0;
        foreach (var asset in assets.Where(a => a.AcquisitionDate <= to))
            foreach (var month in Months(asset, [.. posted[asset.Id]], from, to).Where(m => !m.Posted))
            {
                await postings.AddAsync(new FixedAssetDepreciationPosting(asset.Id, month.Year, month.Month, month.Date,
                                                                          month.Amount, now), ct);
                added++;
            }

        if (added == 0) return;
        unitOfWork.AuditReason = $"Dotations aux amortissements exportées du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}";
        await unitOfWork.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<DepreciationEntry> Entries(IEnumerable<FixedAssetSource> assets,
                                                           FixedAssetSettings settings, DateOnly from, DateOnly to,
                                                           IEnumerable<DepreciationPosting>? posted = null)
    {
        var entries = new List<DepreciationEntry>();
        var byAsset = (posted ?? []).ToLookup(p => p.AssetId);

        foreach (var asset in assets.Where(a => a.AcquisitionDate <= to).OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            var accounts = settings.For(asset.Category);
            foreach (var month in Months(asset, [.. byAsset[asset.Id]], from, to))
            {
                if (month.Amount == 0m) continue;
                var label = $"{(month.Amount < 0m ? "Reprise amort." : "Dot. amort.")} {month.Month:00}/{month.Year}"
                            + (month.Adjusted ? " régul." : string.Empty) + $" {asset.Label}";

                // A reversal goes to the other side: the depreciation in debit, the charge in credit.
                entries.Add(month.Amount > 0m
                    ? new DepreciationEntry(month.Date, asset.Code, label, accounts.Charge, accounts.Depreciation,
                                            Money.Round(month.Amount))
                    : new DepreciationEntry(month.Date, asset.Code, label, accounts.Depreciation, accounts.Charge,
                                            Money.Round(-month.Amount)));
            }
        }

        return [.. entries.OrderBy(e => e.Date).ThenBy(e => e.AssetCode, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The months of an asset an export of the period carries:
    /// <list type="bullet">
    /// <item>never exported yet: the plan's months in the period;</item>
    /// <item>before its first exported month: the plan's months, as before;</item>
    /// <item>exported months: as exported;</item>
    /// <item>
    /// after its last exported month: the plan's depreciation accumulated at the end of the month
    /// less what the books hold. That is the plan's month unless the asset changed since; then the
    /// first such month carries the difference, also when the plan has already ended (a disposal
    /// entered after its months were exported);
    /// </item>
    /// <item>
    /// months between the first and the last exported that were never exported: nothing, the
    /// month exported after them took them up.
    /// </item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<DepreciationExportMonth> Months(FixedAssetSource asset,
                                                                IReadOnlyCollection<DepreciationPosting> posted,
                                                                DateOnly from, DateOnly to)
    {
        var plan = Depreciation.Compute(asset.Terms).Months;
        bool InPeriod(DateOnly date) => date >= from && date <= to;

        if (posted.Count == 0)
            return
            [
                .. plan.Where(m => InPeriod(m.Date))
                       .Select(m => new DepreciationExportMonth(m.Year, m.Month, m.Date, m.Amount, false, false))
            ];

        var first = posted.Min(p => Key(p.Year, p.Month));
        var last = posted.Max(p => Key(p.Year, p.Month));

        var months = new List<DepreciationExportMonth>();
        months.AddRange(plan.Where(m => Key(m.Year, m.Month) < first && InPeriod(m.Date))
                            .Select(m => new DepreciationExportMonth(m.Year, m.Month, m.Date, m.Amount, false, false)));
        months.AddRange(posted.Where(p => InPeriod(p.Date))
                              .Select(p => new DepreciationExportMonth(p.Year, p.Month, p.Date, p.Amount, true, false)));

        // What the books hold: the plan before the first export, then what was exported.
        var booked = Money.Round(plan.Where(m => Key(m.Year, m.Month) < first).Sum(m => m.Amount)
                                 + posted.Sum(p => p.Amount));

        var byKey = plan.ToDictionary(m => Key(m.Year, m.Month));
        var cumulative = new Dictionary<int, decimal>();
        var total = 0m;
        foreach (var m in plan)
        {
            total = Money.Round(total + m.Amount);
            cumulative[Key(m.Year, m.Month)] = total;
        }
        var planEnd = plan.Count == 0 ? int.MinValue : Key(plan[^1].Year, plan[^1].Month);

        for (var key = Math.Max(last + 1, Key(from.Year, from.Month)); key <= Key(to.Year, to.Month); key++)
        {
            var (year, month) = (key / 12, key % 12 + 1);
            var planned = byKey.GetValueOrDefault(key);
            var date = planned?.Date ?? new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            if (!InPeriod(date)) continue;

            var accumulated = key >= planEnd ? total : cumulative.GetValueOrDefault(key);
            var amount = Money.Round(accumulated - booked);
            if (planned is null && amount == 0m)
            {
                if (key > planEnd) break;
                continue;
            }

            months.Add(new DepreciationExportMonth(year, month, date, amount, false, amount != (planned?.Amount ?? 0m)));
            booked = Money.Round(booked + amount);
        }

        return [.. months.OrderBy(m => m.Date)];
    }

    private static int Key(int year, int month) => year * 12 + month - 1;
}
