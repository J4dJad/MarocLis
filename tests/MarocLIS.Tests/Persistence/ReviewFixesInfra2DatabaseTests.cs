using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own, <c>MarocLIS_FixInfra2</c>, brought up by migration as a site's is,
/// and dropped at the end. The server comes from <c>MAROCLIS_TEST_SQLSERVER</c>; its database name is
/// replaced, and an existing database of that name is refused rather than reused.
/// </summary>
public sealed class MigratedScratchDatabase : IDisposable
{
    public const string Name = "MarocLIS_FixInfra2";

    public string ConnectionString { get; }

    private readonly bool _created;

    public MigratedScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        if (context.Database.CanConnect())
            throw new InvalidOperationException(
                $"Database {Name} already exists; the tests create and drop their own and will not touch an existing one.");

        context.Database.Migrate();
        _created = true;
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
/// Start-up seeding when several workstations start at once: one at a time, under an application
/// lock, instead of racing to insert the same administrator.
/// </summary>
public sealed class ReviewFixesInfra2DatabaseTests(MigratedScratchDatabase database)
    : IClassFixture<MigratedScratchDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 15);
    }

    private DatabaseInitializer Initializer(LisDbContext context) =>
        new(context, new BCryptPasswordHasher(10), new FixedClock());

    [SqlServerFact]
    public async Task WorkstationsStartingTogether_AllStart_AndSeedOnce()
    {
        var contexts = Enumerable.Range(0, 4).Select(_ => database.Context()).ToList();

        try
        {
            var results = await Task.WhenAll(contexts.Select(c => Initializer(c).InitializeAsync()));

            results.Should().OnlyContain(r => r.IsSuccess);

            await using var check = database.Context();
            (await check.Users.IgnoreQueryFilters().CountAsync(u => u.UserName == "admin")).Should().Be(1);
            (await check.Users.IgnoreQueryFilters().CountAsync(u => u.UserName == "interface")).Should().Be(1);
        }
        finally
        {
            foreach (var context in contexts) await context.DisposeAsync();
        }
    }

    [SqlServerFact]
    public async Task Seeding_WaitsForTheWorkstationHoldingTheLock()
    {
        // Another workstation is seeding: it holds the lock on its session.
        await using var other = new SqlConnection(database.ConnectionString);
        await other.OpenAsync();
        await using (var take = new SqlCommand(
            "EXEC sp_getapplock @Resource = 'MarocLIS.Seeding', @LockMode = 'Exclusive', @LockOwner = 'Session'", other))
            await take.ExecuteNonQueryAsync();

        await using var context = database.Context();
        var start = Initializer(context).InitializeAsync();

        await Task.Delay(1500);
        start.IsCompleted.Should().BeFalse("seeding waits while another workstation holds the lock");

        await using (var release = new SqlCommand(
            "EXEC sp_releaseapplock @Resource = 'MarocLIS.Seeding', @LockOwner = 'Session'", other))
            await release.ExecuteNonQueryAsync();

        (await start.WaitAsync(TimeSpan.FromSeconds(60))).IsSuccess.Should().BeTrue();

        // And it let go: the lock can be taken again at once.
        await using var probe = new SqlCommand(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'MarocLIS.Seeding', @LockMode = 'Exclusive', " +
            "@LockOwner = 'Session', @LockTimeout = 0; SELECT @r", other);
        ((int)(await probe.ExecuteScalarAsync())!).Should().BeGreaterThanOrEqualTo(0);
    }
}
