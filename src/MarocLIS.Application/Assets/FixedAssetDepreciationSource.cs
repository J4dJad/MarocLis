using MarocLIS.Application.Accounting;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Assets;

/// <summary>
/// The depreciation entries of the fixed-assets register for the accounting export: one entry per
/// asset and month, dated the last day of the month (or the disposal date in the month of a
/// disposal), the category's charge account (6193) in debit and its depreciation account (28xxx) in
/// credit. A month is exported when its date falls within the period.
/// <para>
/// No permission check here: the export that asks has checked its own, and the entries carry
/// amounts and accounts, nothing more.
/// </para>
/// </summary>
public sealed class FixedAssetDepreciationSource(IFixedAssetQueries queries) : IDepreciationEntrySource
{
    public async Task<IReadOnlyList<DepreciationEntry>> GetEntriesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                       CancellationToken ct = default)
    {
        if (from > to) return [];

        var settings = FixedAssetSettings.FromValues(await queries.GetSettingValuesAsync(FixedAssetSettings.Prefix, ct));
        var assets = await queries.GetAssetsAsync(siteId, ct);

        return Entries(assets, settings, from, to);
    }

    public static IReadOnlyList<DepreciationEntry> Entries(IEnumerable<FixedAssetSource> assets,
                                                           FixedAssetSettings settings, DateOnly from, DateOnly to)
    {
        var entries = new List<DepreciationEntry>();

        foreach (var asset in assets.Where(a => a.AcquisitionDate <= to).OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            var accounts = settings.For(asset.Category);
            foreach (var month in Depreciation.Compute(asset.Terms).Months.Where(m => m.Date >= from && m.Date <= to))
            {
                if (month.Amount == 0m) continue;
                entries.Add(new DepreciationEntry(
                    month.Date, asset.Code, $"Dot. amort. {month.Month:00}/{month.Year} {asset.Label}",
                    accounts.Charge, accounts.Depreciation, Money.Round(month.Amount)));
            }
        }

        return [.. entries.OrderBy(e => e.Date).ThenBy(e => e.AssetCode, StringComparer.Ordinal)];
    }
}
