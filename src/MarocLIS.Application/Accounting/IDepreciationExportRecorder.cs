namespace MarocLIS.Application.Accounting;

/// <summary>
/// A depreciation source that remembers what an export file carried, so that a later correction
/// is booked as a difference instead of silently rewriting months the accounting software holds.
/// </summary>
public interface IDepreciationExportRecorder
{
    /// <summary>Records the months of the period that have just gone out in an export file.</summary>
    Task RecordExportedAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default);
}
