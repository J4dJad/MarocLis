using FluentAssertions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Samples;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the collection and reception screens, created from the model.</summary>
public sealed class CollectionReceptionScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Reception_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public CollectionReceptionScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

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
/// What the rebuilt « Réception des échantillons » screen reads beyond what it read before: the
/// scanned tube's cap colour and the number of tubes in its order, and the storage places already
/// used on the site.
/// </summary>
public sealed class CollectionReceptionScreenDatabaseTests(CollectionReceptionScratchDatabase database)
    : IClassFixture<CollectionReceptionScratchDatabase>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [SqlServerFact]
    public async Task ReceptionCandidate_CarriesTheCapColour_AndTheTubesOfItsOrder()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();

        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Plasma citraté"));
        var citrate = new ContainerType(Unique("CIT"), new MultilingualText("Tube citrate"), "#4B8FD6");
        var patient = new Patient(Unique("P"), "ALAOUI", "Mohammed", new DateOnly(1970, 3, 2), Gender.Male, site);

        var order = new LabOrder(Unique("A"), patient.Id, site, Now.AddHours(-1), user);
        var scanned = new Sample(Unique("T"), order.Id, specimen.Id, site) { ContainerTypeId = citrate.Id };
        scanned.Collect(Now.AddMinutes(-16), user, null, null).IsSuccess.Should().BeTrue();
        var second = new Sample(Unique("T"), order.Id, specimen.Id, site);
        var third = new Sample(Unique("T"), order.Id, specimen.Id, site);

        await using (var context = database.Context())
        {
            context.SpecimenTypes.Add(specimen);
            context.ContainerTypes.Add(citrate);
            context.Patients.Add(patient);
            context.LabOrders.Add(order);
            context.Samples.AddRange(scanned, second, third);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var candidate = await new SampleQueries(read).GetReceptionCandidateAsync(scanned.Barcode, Now);

        candidate.Should().NotBeNull();
        candidate!.ContainerColourHex.Should().Be("#4B8FD6");
        candidate.TotalSamplesInOrder.Should().Be(3);
        candidate.Elapsed.Should().NotBeNull();

        // The bench's own list carries the same facts.
        var awaiting = await new SampleQueries(read).GetAwaitingReceptionAsync(site, Now);
        awaiting.Should().ContainSingle(c => c.SampleId == scanned.Id)
                .Which.ContainerColourHex.Should().Be("#4B8FD6");
    }

    [SqlServerFact]
    public async Task StorageLocations_AreThoseOfTheSite_DistinctAndInOrder()
    {
        var site = Guid.NewGuid();
        var otherSite = Guid.NewGuid();
        var user = Guid.NewGuid();
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var order = new LabOrder(Unique("A"), Guid.NewGuid(), site, Now.AddHours(-3), user);

        Sample Stored(Guid at, string? place)
        {
            var tube = new Sample(Unique("T"), order.Id, specimen.Id, at) { StorageLocation = place };
            return tube;
        }

        await using (var context = database.Context())
        {
            context.SpecimenTypes.Add(specimen);
            context.LabOrders.Add(order);
            context.Samples.AddRange(
                Stored(site, "Réfrigérateur R1"),
                Stored(site, "Paillasse hémostase"),
                Stored(site, "Réfrigérateur R1"),
                Stored(site, "Paillasse hémostase "),
                Stored(site, null),
                Stored(site, ""),
                Stored(otherSite, "Congélateur C9"));
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var places = await new SampleQueries(read).GetStorageLocationsAsync(site);

        places.Should().Equal("Paillasse hémostase", "Réfrigérateur R1");
    }
}
