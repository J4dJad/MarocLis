using MarocLIS.Application.Samples;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class SampleStorageLocationQueries(LisDbContext db) : ISampleStorageLocationQueries
{
    public async Task<IReadOnlyList<SampleStorageLocationRow>> GetStorageLocationsAsync(
        Guid siteId, bool includeInactive, CancellationToken ct = default)
    {
        var places = await db.SampleStorageLocations.AsNoTracking()
            .Where(l => l.SiteId == siteId && (includeInactive || l.IsActive))
            .OrderBy(l => l.Name)
            .Select(l => new { l.Id, l.Name, l.IsActive })
            .ToListAsync(ct);

        // The tubes still kept there, so the administrator sees which place is in use before
        // taking it out of the list. Matched on the trimmed name, case ignored, as the database's
        // collation compares them.
        var counts = await db.Samples.AsNoTracking()
            .Where(s => s.SiteId == siteId && s.StorageLocation != null && s.Status != SampleStatus.Discarded)
            .GroupBy(s => s.StorageLocation!)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byName = counts
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Count), StringComparer.OrdinalIgnoreCase);

        return [.. places.Select(p => new SampleStorageLocationRow(
            p.Id, p.Name, p.IsActive, byName.GetValueOrDefault(p.Name.Trim())))];
    }

    public Task<bool> NameExistsAsync(Guid siteId, string name, Guid exceptId, CancellationToken ct = default)
    {
        var normalised = SampleStorageLocation.Normalize(name);

        // Compared by the database's collation, which ignores case, as the unique index does.
        return db.SampleStorageLocations.AsNoTracking()
            .AnyAsync(l => l.SiteId == siteId && l.Id != exceptId && l.Name == normalised, ct);
    }
}
