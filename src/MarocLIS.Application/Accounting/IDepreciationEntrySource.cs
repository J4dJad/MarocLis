namespace MarocLIS.Application.Accounting;

public interface IDepreciationEntrySource { Task<IReadOnlyList<DepreciationEntry>> GetEntriesAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default); }

public sealed record DepreciationEntry(DateOnly Date, string AssetCode, string Label, string ChargeAccount, string DepreciationAccount, decimal Amount);
