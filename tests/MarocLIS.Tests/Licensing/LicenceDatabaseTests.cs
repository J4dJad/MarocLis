using FluentAssertions;
using MarocLIS.Domain.Licensing;
using MarocLIS.Infrastructure.Licensing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// The licence's tables on SQL Server, brought up by the migrations: the server ID read from what
/// SQL Server reports, the markers that only go forward, and the workstations' places taken from
/// two connections at once.
/// </summary>
public sealed class LicenceDatabaseTests(LicenceDatabaseTests.Database database)
    : IClassFixture<LicenceDatabaseTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(1));

    [SqlServerFact]
    public async Task TheServerId_IsTheSameFromEveryConnection_AndTheInstallationIdIsWrittenOnce()
    {
        await using var first = database.Context();
        await using var second = database.Context();

        var one = await new LicenceStore(first).ReadAsync();
        var two = await new LicenceStore(second).ReadAsync();

        one.MachineName.Should().NotBeNullOrWhiteSpace("SQL Server names the machine it runs on");
        two.InstallationId.Should().Be(one.InstallationId);
        ServerIdentity.Compute(two.MachineName, two.InstanceName, two.InstallationId)
            .Should().Be(ServerIdentity.Compute(one.MachineName, one.InstanceName, one.InstallationId));

        await using var check = database.Context();
        (await check.Settings.CountAsync(s => s.Key == LicenceStore.InstallationIdKey)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task AnImportedLicence_ReplacesTheCurrentOne_AndIsReadBack()
    {
        await using (var context = database.Context())
        {
            var store = new LicenceStore(context);
            await store.SaveAsync(new LicenceRecord("LIC-DB-1", 1, LicenceKind.Annual, Sign(Annual()), Now));
            await store.SaveAsync(new LicenceRecord("LIC-DB-1", 2, LicenceKind.Annual, Sign(Annual() with { Version = 2 }),
                                                    Now.AddMinutes(1)) { ImportedByUserName = "admin" });
        }

        await using var read = database.Context();
        var store2 = new LicenceStore(read);
        var stored = await store2.ReadAsync();
        stored.Version.Should().Be(2);
        (await store2.HighestVersionAsync("LIC-DB-1"))!.Value.Version.Should().Be(2);
        (await store2.HistoryAsync()).Where(h => h.Number == "LIC-DB-1")
            .Should().ContainSingle(h => h.ReplacedAt != null && h.Version == 1);
    }

    [SqlServerFact]
    public async Task TheLatestDaySeen_NeverGoesBack()
    {
        await using (var context = database.Context())
            await new LicenceStore(context).SaveMarkersAsync(Today.AddDays(5), Today, clearRestricted: false);
        await using (var context = database.Context())
            await new LicenceStore(context).SaveMarkersAsync(Today, null, clearRestricted: true);

        await using var read = database.Context();
        var stored = await new LicenceStore(read).ReadAsync();
        stored.LatestDateSeen.Should().Be(Today.AddDays(5), "a clock put back does not move the marker");
        stored.RestrictedSince.Should().BeNull();
    }

    [SqlServerFact]
    public async Task TheLastPlace_GoesToOneWorkstation_WhenTwoClaimItTogether()
    {
        await using (var context = database.Context())
            await new WorkstationRegistry(context).ClaimAsync("POSTE-A", "a", "1.0", limit: 2, Now);

        await using var b = database.Context();
        await using var c = database.Context();
        var claims = await Task.WhenAll(
            new WorkstationRegistry(b).ClaimAsync("POSTE-B", "b", "1.0", limit: 2, Now),
            new WorkstationRegistry(c).ClaimAsync("POSTE-C", "c", "1.0", limit: 2, Now));

        claims.Count(x => x.Granted).Should().Be(1);
        claims.Single(x => !x.Granted).Active.Should().HaveCount(2);

        // Signing out frees the place at once; silence frees it after fifteen minutes.
        var holder = claims.Single(x => x.Granted).Active.Select(w => w.MachineName).Single(n => n != "POSTE-A");
        await using (var context = database.Context())
            await new WorkstationRegistry(context).ReleaseAsync(holder, Now.AddMinutes(1));
        await using (var context = database.Context())
        {
            var registry = new WorkstationRegistry(context);
            (await registry.ClaimAsync("POSTE-D", "d", "1.0", limit: 2, Now.AddMinutes(2))).Granted.Should().BeTrue();
            (await registry.ClaimAsync("POSTE-E", "e", "1.0", limit: 2, Now.AddMinutes(3))).Granted.Should().BeFalse();
            (await registry.ClaimAsync("POSTE-E", "e", "1.0", limit: 2, Now.AddMinutes(20))).Granted.Should().BeTrue();
            (await registry.ActiveAsync(Now.AddMinutes(20))).Select(w => w.MachineName).Should().Equal("POSTE-E");
        }
    }

    /// <summary>A database of its own, <c>MarocLIS_T_LicenceDb</c>, brought up by migration and dropped at the end.</summary>
    public sealed class Database : IDisposable
    {
        private const string Name = "MarocLIS_T_LicenceDb";
        private readonly string _connectionString = "";
        private readonly bool _created;

        public Database()
        {
            var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
            if (string.IsNullOrWhiteSpace(configured)) return;

            _connectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;
            using var context = Context();
            if (context.Database.CanConnect())
                throw new InvalidOperationException($"Database {Name} already exists.");

            context.Database.Migrate();
            _created = true;
        }

        public LisDbContext Context() =>
            new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connectionString).Options);

        public void Dispose()
        {
            if (!_created) return;
            SqlConnection.ClearAllPools();
            using var context = Context();
            context.Database.EnsureDeleted();
        }
    }
}
