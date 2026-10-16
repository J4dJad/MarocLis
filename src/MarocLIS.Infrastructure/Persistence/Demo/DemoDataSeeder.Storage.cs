using MarocLIS.Domain.Samples;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence.Demo;

public sealed partial class DemoDataSeeder
{
    /// <summary>
    /// The site's storage places (« Paramètres › Lieux de stockage »), among them the serum-bank
    /// fridge the demonstration tubes are shelved in. Before the orders: reception and the serum
    /// bank offer only the places of this list.
    /// </summary>
    private static readonly string[] DemoStoragePlaces =
    [
        "Sérothèque — réfrigérateur S1",
        "Paillasse biochimie",
        "Congélateur −20 °C — C1"
    ];

    private async Task SeedStorageLocationsAsync(CancellationToken ct)
    {
        var existing = await QueryAsync(db => db.SampleStorageLocations.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.SiteId == _siteId)
            .Select(l => l.Name)
            .ToListAsync(ct));

        var missing = DemoStoragePlaces
            .Where(p => !existing.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Select(p => SampleStorageLocation.Create(_siteId, p).Value)
            .ToList();

        if (missing.Count == 0) return;

        await WriteAsync(db =>
        {
            db.SampleStorageLocations.AddRange(missing);
            return Task.CompletedTask;
        }, ct);
    }
}
