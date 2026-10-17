using MarocLIS.Domain.Samples;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence;

/// <summary>
/// The sample storage places a new installation starts with (« Paramètres › Lieux de stockage »):
/// the fridge, the two freezers, the serum bank and the bench, so reception and the serum bank have
/// something to offer on the first day.
/// <para>
/// The place is one name, copied as text onto each tube (<see cref="Sample.StorageLocation"/>), so
/// it is seeded in French, the language the tubes and the registers are kept in. The administrator
/// renames them to the laboratory's own equipment (« Réfrigérateur R1 »…) or adds others.
/// </para>
/// <para>
/// A new installation only: seeded with the starter catalogue, and only while the site has no place
/// at all. A laboratory already in use — whose list the <c>LieuxDeStockage</c> migration built from
/// its tubes, or which emptied it on purpose — never sees one appear at start-up.
/// </para>
/// </summary>
public static class StarterStorageLocations
{
    public static readonly IReadOnlyList<string> Names =
    [
        "Réfrigérateur 2–8 °C",
        "Congélateur −20 °C",
        "Congélateur −80 °C",
        "Sérothèque",
        "Température ambiante"
    ];

    internal static async Task SeedAsync(LisDbContext context, Guid siteId, CancellationToken ct)
    {
        if (await context.SampleStorageLocations.IgnoreQueryFilters().AnyAsync(ct)) return;

        foreach (var name in Names)
            context.SampleStorageLocations.Add(SampleStorageLocation.Create(siteId, name).Value);

        await context.SaveChangesAsync(ct);
    }
}
