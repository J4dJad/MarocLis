using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Assets;

/// <summary>One month of an asset's depreciation already sent to the accounting software.</summary>
/// <param name="ChargeAccount">The accounts it went out under; null on rows recorded before they were kept.</param>
public sealed record DepreciationPosting(Guid AssetId, int Year, int Month, DateOnly Date, decimal Amount,
                                         string? ChargeAccount = null, string? DepreciationAccount = null,
                                         DateTimeOffset? ExportedAt = null);

/// <summary>One month of an asset in an export.</summary>
/// <param name="Posted">Already exported: given again as it was.</param>
/// <param name="Adjusted">Not the plan's own amount: it carries the difference with what was exported.</param>
/// <param name="ChargeAccount">For an exported month, the accounts it went out under (null: the category's).</param>
public sealed record DepreciationExportMonth(int Year, int Month, DateOnly Date, decimal Amount, bool Posted,
                                             bool Adjusted, string? ChargeAccount = null,
                                             string? DepreciationAccount = null);

/// <summary>
/// A change of category of an asset already in the accounting software (<see cref="FixedAssetTransfer"/>):
/// the gross value and the exported depreciation move to the new category's accounts on <paramref name="Date"/>.
/// </summary>
public sealed record DepreciationTransfer(Guid AssetId, DateOnly Date, DateTimeOffset ChangedAt, CategoryAccounts From,
                                          CategoryAccounts To, decimal Gross, decimal Depreciation);

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
/// asset follow the plan, as before, unless the site's depreciation of that month was already
/// exported without them: they are then caught up in the first month still open (A8). The accounts
/// of an exported month are kept, and a change of category after export is booked as a transfer
/// (<see cref="DepreciationTransfer"/>).
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
        var transfers = await queries.GetDepreciationTransfersAsync(siteId, ct);

        return Entries(assets, settings, from, to, posted, transfers);
    }

    /// <summary>
    /// Records the months an export file has just carried, so that a later correction of an asset
    /// is booked as a difference rather than by rewriting them. Called when the file is produced,
    /// never by the check that precedes it.
    /// </summary>
    public async Task RecordExportedAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (from > to || postings is null || unitOfWork is null) return;

        var settings = FixedAssetSettings.FromValues(await queries.GetSettingValuesAsync(FixedAssetSettings.Prefix, ct));
        var assets = await queries.GetAssetsAsync(siteId, ct);
        var all = await queries.GetDepreciationPostingsAsync(siteId, ct);
        var posted = all.ToLookup(p => p.AssetId);
        var site = SiteMonths(all);
        var now = clock?.Now ?? DateTimeOffset.Now;

        var added = 0;
        foreach (var asset in assets)
        {
            var accounts = settings.For(asset.Category);
            foreach (var month in Months(asset, [.. posted[asset.Id]], from, to, site).Where(m => !m.Posted))
            {
                await postings.AddAsync(new FixedAssetDepreciationPosting(asset.Id, month.Year, month.Month, month.Date,
                                                                          month.Amount, now)
                                            .WithAccounts(accounts.Charge, accounts.Depreciation), ct);
                added++;
            }
        }

        if (added == 0) return;
        unitOfWork.AuditReason = $"Dotations aux amortissements exportées du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}";
        await unitOfWork.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<DepreciationEntry> Entries(IEnumerable<FixedAssetSource> assets,
                                                           FixedAssetSettings settings, DateOnly from, DateOnly to,
                                                           IEnumerable<DepreciationPosting>? posted = null,
                                                           IEnumerable<DepreciationTransfer>? transfers = null)
    {
        var entries = new List<DepreciationEntry>();
        var all = (posted ?? []).ToList();
        var byAsset = all.ToLookup(p => p.AssetId);
        var moved = (transfers ?? []).ToLookup(t => t.AssetId);
        var site = SiteMonths(all);

        // An asset with exported months is always looked at, even when its dates now start after the
        // period (I9): its exported months are given again and the reversal is not put off.
        foreach (var asset in assets.Where(a => a.AcquisitionDate <= to || byAsset[a.Id].Any())
                                    .OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            var current = settings.For(asset.Category);
            var assetTransfers = moved[asset.Id].OrderBy(t => t.ChangedAt).ToList();
            var exportedAt = byAsset[asset.Id].GroupBy(p => Key(p.Year, p.Month))
                                              .ToDictionary(g => g.Key, g => g.First().ExportedAt);

            foreach (var month in Months(asset, [.. byAsset[asset.Id]], from, to, site))
            {
                if (month.Amount == 0m) continue;
                var label = $"{(month.Amount < 0m ? "Reprise amort." : "Dot. amort.")} {month.Month:00}/{month.Year}"
                            + (month.Adjusted ? " régul." : string.Empty) + $" {asset.Label}";

                // An exported month keeps the accounts it went out under; a row recorded before they
                // were kept takes those of the category the asset had when it was exported.
                var accounts = current;
                if (month.Posted)
                {
                    var then = AccountsAt(assetTransfers, exportedAt.GetValueOrDefault(Key(month.Year, month.Month)),
                                          current);
                    accounts = new CategoryAccounts(then.Asset, month.DepreciationAccount ?? then.Depreciation,
                                                    month.ChargeAccount ?? then.Charge);
                }

                // A reversal goes to the other side: the depreciation in debit, the charge in credit.
                entries.Add(month.Amount > 0m
                    ? new DepreciationEntry(month.Date, asset.Code, label, accounts.Charge, accounts.Depreciation,
                                            Money.Round(month.Amount))
                    : new DepreciationEntry(month.Date, asset.Code, label, accounts.Depreciation, accounts.Charge,
                                            Money.Round(-month.Amount)));
            }

            // A change of category after export (I8): the depreciation already exported moves from the
            // old 28xxx to the new one (debit old, credit new), the gross value from the old asset
            // account to the new one (debit new, credit old), on the day of the change.
            foreach (var transfer in assetTransfers.Where(t => t.Date >= from && t.Date <= to))
            {
                if (transfer.Depreciation != 0m && transfer.From.Depreciation != transfer.To.Depreciation)
                    entries.Add(new DepreciationEntry(transfer.Date, asset.Code, $"Transfert amort. {asset.Label}",
                                                      transfer.From.Depreciation, transfer.To.Depreciation,
                                                      Money.Round(transfer.Depreciation)));
                if (transfer.Gross != 0m && transfer.From.Asset != transfer.To.Asset)
                    entries.Add(new DepreciationEntry(transfer.Date, asset.Code, $"Transfert immo. {asset.Label}",
                                                      transfer.To.Asset, transfer.From.Asset,
                                                      Money.Round(transfer.Gross)));
            }
        }

        return [.. entries.OrderBy(e => e.Date).ThenBy(e => e.AssetCode, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The months of an asset an export of the period carries:
    /// <list type="bullet">
    /// <item>exported months: as exported, on the accounts they went out under;</item>
    /// <item>
    /// a month not exported for the asset although the site's depreciation of that month was
    /// exported (an asset registered late, or its dates moved earlier, A8): nothing then, its
    /// depreciation is caught up in the first month still open;
    /// </item>
    /// <item>
    /// months between the asset's first and last exported months, or after its last one and before
    /// the period, that were never exported: nothing, the first month exported after them takes them up;
    /// </item>
    /// <item>
    /// any other month (open): the plan's depreciation accumulated at the end of the month less what
    /// the books hold. That is the plan's month unless the asset changed since, or months were
    /// missed; then it carries the difference, also when the plan has already ended or not yet
    /// started (a disposal entered late, dates moved later, I9: the reversal lands in the first
    /// open month).
    /// </item>
    /// </list>
    /// Open months before the period are counted at what an export of their own period would give.
    /// </summary>
    /// <param name="siteExported">The months (year × 12 + month − 1) already exported for the site.</param>
    public static IReadOnlyList<DepreciationExportMonth> Months(FixedAssetSource asset,
                                                                IReadOnlyCollection<DepreciationPosting> posted,
                                                                DateOnly from, DateOnly to,
                                                                IReadOnlySet<int>? siteExported = null)
    {
        var plan = Depreciation.Compute(asset.Terms).Months;
        if (plan.Count == 0 && posted.Count == 0) return [];
        bool InPeriod(DateOnly date) => date >= from && date <= to;

        var own = posted.GroupBy(p => Key(p.Year, p.Month)).ToDictionary(g => g.Key, g => g.First());
        var first = own.Count == 0 ? int.MaxValue : own.Keys.Min();
        var last = own.Count == 0 ? int.MinValue : own.Keys.Max();
        var fromKey = Key(from.Year, from.Month);

        var byKey = plan.ToDictionary(m => Key(m.Year, m.Month));
        var cumulative = new Dictionary<int, decimal>();
        var total = 0m;
        foreach (var m in plan)
        {
            total = Money.Round(total + m.Amount);
            cumulative[Key(m.Year, m.Month)] = total;
        }
        var planStart = plan.Count == 0 ? int.MaxValue : Key(plan[0].Year, plan[0].Month);
        var planEnd = plan.Count == 0 ? int.MinValue : Key(plan[^1].Year, plan[^1].Month);
        decimal Accumulated(int key) => key >= planEnd ? total : key < planStart ? 0m : cumulative.GetValueOrDefault(key);

        var months = new List<DepreciationExportMonth>();
        var booked = 0m;
        for (var key = Math.Min(planStart, first); key <= Key(to.Year, to.Month); key++)
        {
            var (year, month) = (key / 12, key % 12 + 1);
            if (own.TryGetValue(key, out var done))
            {
                booked = Money.Round(booked + done.Amount);
                if (InPeriod(done.Date))
                    months.Add(new DepreciationExportMonth(done.Year, done.Month, done.Date, done.Amount, true, false,
                                                           done.ChargeAccount, done.DepreciationAccount));
                continue;
            }

            // Taken up by a later month, or missed by an export of the site that did not carry it.
            if (key >= first && key <= last || key > last && last != int.MinValue && key < fromKey
                || siteExported?.Contains(key) == true)
                continue;

            var planned = byKey.GetValueOrDefault(key);
            var date = planned?.Date ?? new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            var amount = Money.Round(Accumulated(key) - booked);
            booked = Money.Round(booked + amount);
            if (!InPeriod(date) || planned is null && amount == 0m) continue;

            months.Add(new DepreciationExportMonth(year, month, date, amount, false, amount != (planned?.Amount ?? 0m)));
        }

        return [.. months.OrderBy(m => m.Date)];
    }

    /// <summary>The months (year × 12 + month − 1) the site's depreciation was exported for.</summary>
    public static IReadOnlySet<int> SiteMonths(IEnumerable<DepreciationPosting> posted) =>
        posted.Select(p => Key(p.Year, p.Month)).ToHashSet();

    /// <summary>
    /// The accounts of the asset's category at a moment: those its first later change of category
    /// left, or the current ones.
    /// </summary>
    public static CategoryAccounts AccountsAt(IEnumerable<DepreciationTransfer> transfers, DateTimeOffset? at,
                                              CategoryAccounts current) =>
        at is { } moment && transfers.Where(t => t.ChangedAt > moment).MinBy(t => t.ChangedAt) is { } next
            ? next.From
            : current;

    private static int Key(int year, int month) => year * 12 + month - 1;
}
