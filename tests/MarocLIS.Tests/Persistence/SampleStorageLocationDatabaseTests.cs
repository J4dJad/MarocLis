using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Samples;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own, uniquely named, built by the migrations up to the one before the
/// storage list, so the list's seeding from the places already on the tubes can be watched.
/// </summary>
public sealed class StorageLocationsDatabase : IDisposable
{
    /// <summary>The migration just before <c>LieuxDeStockage</c>.</summary>
    public const string Previous = "20261015234723_CompteurDossiersCommun";

    private readonly bool _created;

    public StorageLocationsDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_lieux_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = Context();
        context.GetService<IMigrator>().Migrate(Previous);
        _created = true;

        // The tubes below hang off orders and specimen types this test does not need.
        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public string ConnectionString { get; } = string.Empty;

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// The site's storage list in SQL Server: seeded by the migration from the places already written
/// on the tubes, read for the settings with the tubes kept in each place, unique per site.
/// </summary>
public sealed class SampleStorageLocationDatabaseTests(StorageLocationsDatabase database)
    : IClassFixture<StorageLocationsDatabase>
{
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 16);
    }

    private static readonly SemaphoreSlim Migrated = new(1, 1);
    private static bool _migrated;

    private static readonly Guid SeededSite = Guid.NewGuid();
    private static readonly Guid SeededOtherSite = Guid.NewGuid();
    private static List<(Guid Id, string? Place)> _seededTubes = [];

    /// <summary>
    /// Writes tubes with places as an installation holds them before the list existed, then applies
    /// the remaining migrations — once for the class, whichever test runs first.
    /// </summary>
    private async Task EnsureMigratedAsync()
    {
        await Migrated.WaitAsync();
        try
        {
            if (_migrated) return;

            Sample Tube(Guid site, string? place) =>
                new(Unique("T"), Guid.NewGuid(), Guid.NewGuid(), site) { StorageLocation = place };

            var tubes = new[]
            {
                Tube(SeededSite, "Réfrigérateur R1"),
                Tube(SeededSite, "Réfrigérateur R1 "),
                Tube(SeededSite, "  Paillasse hémostase"),
                Tube(SeededSite, "réfrigérateur r1"),
                Tube(SeededSite, null),
                Tube(SeededSite, "   "),
                Tube(SeededOtherSite, "Congélateur C9")
            };

            // Raw SQL: the model now holds the storage list, whose table does not exist yet.
            await using (var context = database.Context())
            {
                foreach (var tube in tubes)
                {
                    await context.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO [smp].[Samples] ([Id], [Barcode], [LabOrderId], [SpecimenTypeId], [SiteId], [Status],
    [StorageLocation], [IsDeleted], [CreatedAt], [SequenceInOrder], [RecollectionRequested],
    [CollectionLocation], [CollectionAttempts], [Defects], [IsAcceptedWithReservation])
VALUES ({tube.Id}, {tube.Barcode}, {tube.LabOrderId}, {tube.SpecimenTypeId}, {tube.SiteId}, 5,
    {tube.StorageLocation}, 0, SYSDATETIMEOFFSET(), 1, 0, 0, 0, 0, 0)");
                }

                await context.GetService<IMigrator>().MigrateAsync();
            }

            _seededTubes = [.. tubes.Select(t => (t.Id, t.StorageLocation))];
            _migrated = true;
        }
        finally
        {
            Migrated.Release();
        }
    }

    [SqlServerFact]
    public async Task The_migration_seeds_the_list_from_the_places_on_the_tubes_per_site()
    {
        await EnsureMigratedAsync();

        await using var context = database.Context();

        var seeded = await context.SampleStorageLocations.AsNoTracking()
            .Where(l => l.SiteId == SeededSite)
            .OrderBy(l => l.Name)
            .ToListAsync();

        seeded.Select(l => l.Name).Should().HaveCount(2)
              .And.Contain("Paillasse hémostase")
              .And.Contain(n => string.Equals(n, "Réfrigérateur R1", StringComparison.OrdinalIgnoreCase));
        seeded.Should().OnlyContain(l => l.IsActive);

        (await context.SampleStorageLocations.AsNoTracking().Where(l => l.SiteId == SeededOtherSite)
                .Select(l => l.Name).ToListAsync())
            .Should().Equal("Congélateur C9");

        // The tubes keep exactly what was written on them.
        var ids = _seededTubes.Select(t => t.Id).ToList();
        var places = await context.Samples.AsNoTracking().IgnoreQueryFilters()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.StorageLocation })
            .ToListAsync();

        places.Select(p => (p.Id, p.StorageLocation))
              .Should().BeEquivalentTo(_seededTubes.Select(t => (t.Id, t.Place)));
    }

    [SqlServerFact]
    public async Task The_settings_list_carries_the_inactive_places_and_the_tubes_kept_in_each()
    {
        await EnsureMigratedAsync();

        var site = Guid.NewGuid();
        var fridge = SampleStorageLocation.Create(site, "Réfrigérateur R2").Value;
        var bench = SampleStorageLocation.Create(site, "Paillasse").Value;
        var freezer = SampleStorageLocation.Create(site, "Congélateur ancien").Value;
        freezer.Deactivate();

        Sample Tube(string? place, SampleStatus status = SampleStatus.Stored)
        {
            var tube = new Sample(Unique("T"), Guid.NewGuid(), Guid.NewGuid(), site) { StorageLocation = place };
            typeof(Sample).GetProperty(nameof(Sample.Status))!.SetValue(tube, status);
            return tube;
        }

        await using (var context = database.Context())
        {
            context.SampleStorageLocations.AddRange(fridge, bench, freezer);
            context.Samples.AddRange(
                Tube("Réfrigérateur R2"), Tube("réfrigérateur r2 "), Tube("Réfrigérateur R2", SampleStatus.Discarded),
                Tube("Congélateur ancien"), Tube(null));
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new SampleStorageLocationQueries(read);

        var all = await queries.GetStorageLocationsAsync(site, includeInactive: true);
        all.Select(r => (r.Name, r.IsActive, r.StoredSampleCount)).Should().Equal(
            ("Congélateur ancien", false, 1),
            ("Paillasse", true, 0),
            ("Réfrigérateur R2", true, 2));

        (await queries.GetStorageLocationsAsync(site, includeInactive: false))
            .Select(r => r.Name).Should().Equal("Paillasse", "Réfrigérateur R2");

        (await new SampleQueries(read).GetStorageLocationsAsync(site))
            .Should().Equal("Paillasse", "Réfrigérateur R2");

        (await queries.NameExistsAsync(site, " réfrigérateur r2 ", Guid.Empty)).Should().BeTrue();
        (await queries.NameExistsAsync(site, "Réfrigérateur R2", fridge.Id)).Should().BeFalse();
        (await queries.NameExistsAsync(site, "congélateur ancien", Guid.Empty))
            .Should().BeTrue("an inactive place keeps its name taken");
        (await queries.NameExistsAsync(Guid.NewGuid(), "Paillasse", Guid.Empty)).Should().BeFalse();
    }

    [SqlServerFact]
    public async Task The_database_refuses_a_second_place_of_the_same_name_on_a_site()
    {
        await EnsureMigratedAsync();

        var site = Guid.NewGuid();

        await using (var context = database.Context())
        {
            context.SampleStorageLocations.Add(SampleStorageLocation.Create(site, "Frigo").Value);
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
        {
            context.SampleStorageLocations.Add(SampleStorageLocation.Create(site, "FRIGO").Value);
            var save = () => context.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>();
        }

        await using (var context = database.Context())
        {
            // Another site may have its own.
            context.SampleStorageLocations.Add(SampleStorageLocation.Create(Guid.NewGuid(), "Frigo").Value);
            await context.SaveChangesAsync();
        }
    }

    [SqlServerFact]
    public async Task Renaming_a_place_through_the_service_leaves_the_tubes_stored_there_untouched()
    {
        await EnsureMigratedAsync();

        var site = Guid.NewGuid();
        var tube = new Sample(Unique("T"), Guid.NewGuid(), Guid.NewGuid(), site) { StorageLocation = "Frigo R3" };

        await using (var context = database.Context())
        {
            context.Samples.Add(tube);
            await context.SaveChangesAsync();
        }

        Guid id;
        await using (var context = database.Context())
        {
            var service = Service(context, site);

            var created = await service.CreateAsync("Frigo R3");
            created.IsSuccess.Should().BeTrue();
            id = created.Value;

            (await service.CreateAsync(" frigo r3")).FirstError.ResourceKey.Should().Be("SampleStorageLocation_NameTaken");
            (await service.RenameAsync(id, "Réfrigérateur R3")).IsSuccess.Should().BeTrue();
            (await service.SetActiveAsync(id, false)).IsSuccess.Should().BeTrue();
        }

        await using (var context = database.Context())
        {
            var place = await context.SampleStorageLocations.AsNoTracking().SingleAsync(l => l.Id == id);
            place.Name.Should().Be("Réfrigérateur R3");
            place.IsActive.Should().BeFalse();

            (await context.Samples.AsNoTracking().SingleAsync(s => s.Id == tube.Id))
                .StorageLocation.Should().Be("Frigo R3");
        }
    }

    private static SampleStorageLocationService Service(LisDbContext context, Guid site)
    {
        var guard = Stub<IPermissionGuard>.Create(new()
        {
            [nameof(IPermissionGuard.Require)] = _ => Result.Success()
        });

        var user = Stub<ICurrentUser>.Create(new()
        {
            ["get_" + nameof(ICurrentUser.SiteId)] = _ => site,
            ["get_" + nameof(ICurrentUser.UserId)] = _ => Guid.NewGuid()
        });

        return new SampleStorageLocationService(
            new Repository<SampleStorageLocation>(context, new FixedClock()),
            new SampleStorageLocationQueries(context), new UnitOfWork(context), guard, user);
    }
}
