using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using MarocLIS.Infrastructure.Licensing;
using MarocLIS.Tests.Licensing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Persistence.Demo;
using MarocLIS.Web.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A database of its own for the web host's first-run demonstration, dropped at the end.</summary>
public sealed class WebDemoSeedScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_WebDemo_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; } = "";

    private readonly bool _created;

    public WebDemoSeedScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;
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
/// The demonstration seeded by the web host itself, composed as the web host composes it: one
/// operator per scope. Every step used to be refused as an expired session, since each step's scope
/// had an operator of its own that nobody had signed in; the demonstration users now act in them.
/// (Without a licence yet, the steps the licence keeps for a licensed laboratory — new files, the
/// site's settings, the analysers — are refused on that ground, as they are at a workstation.)
/// </summary>
public sealed class WebDemoSeedDatabaseTests(WebDemoSeedScratchDatabase database) : IClassFixture<WebDemoSeedScratchDatabase>
{
    [SqlServerFact]
    public async Task TheWebHost_SeedsTheDemonstration_ItsStepsDoneAsTheDemonstrationUsers()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarocLisWeb(configuration);
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync())
                .IsSuccess.Should().BeTrue();

        IReadOnlyList<string> problems;
        await using (var scope = provider.CreateAsyncScope())
            problems = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

        problems.Should().NotContain(p => p.Contains("SessionExpired"), string.Join(" | ", problems));

        await using var context = database.Context();
        (await context.Patients.IgnoreQueryFilters().CountAsync(p => p.MedicalRecordNumber.StartsWith(DemoDataSeeder.DemoPrefix)))
            .Should().BeGreaterThan(0);
        (await context.Set<MarocLIS.Domain.HumanResources.Employee>().IgnoreQueryFilters().CountAsync()).Should().BeGreaterThan(0);
        (await context.Set<MarocLIS.Domain.HumanResources.WorkAccident>().IgnoreQueryFilters().CountAsync()).Should().BeGreaterThan(0);
    }
}

/// <summary>
/// The demonstration of a licensed laboratory, completed at the web host's start: the licence is
/// evaluated before the first step, and every step goes through — the tube kept past its date in the
/// serum bank and the CNSS claim of the clinic's order among them.
/// </summary>
public sealed class WebDemoSeedLicensedDatabaseTests(WebDemoSeedScratchDatabase database) : IClassFixture<WebDemoSeedScratchDatabase>
{
    [SqlServerFact]
    public async Task ALicensedLaboratory_GetsTheWholeDemonstration_AtTheNextStart()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        // The first start, then the licence imported (as from the import page), as at a new laboratory.
        var first = new ServiceCollection();
        first.AddLogging();
        first.AddMarocLisWeb(configuration);
        await using (var provider = first.BuildServiceProvider())
        {
            await using (var scope = provider.CreateAsyncScope())
                (await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync())
                    .IsSuccess.Should().BeTrue();

            await using (var scope = provider.CreateAsyncScope())
            {
                var stored = await scope.ServiceProvider.GetRequiredService<ILicenceStore>().ReadAsync();
                var terms = LicenceTestKit.Annual(365, [.. LicenceModules.All]) with
                {
                    IssuedOn = DateOnly.FromDateTime(DateTime.Today).AddDays(-10),
                    StartsOn = DateOnly.FromDateTime(DateTime.Today).AddDays(-10),
                    EndsOn = DateOnly.FromDateTime(DateTime.Today).AddDays(365),
                    Workstations = 20,
                    ServerId = ServerIdentity.Compute(stored.MachineName, stored.InstanceName, stored.InstallationId)
                };
                var imported = await scope.ServiceProvider.GetRequiredService<LicenceService>()
                    .ImportAsync(LicenceTestKit.Sign(terms), "test.lic");
                imported.IsSuccess.Should().BeTrue(string.Join(", ", imported.Errors.Select(e => e.ResourceKey)));
            }
        }

        // The next start: a new process, its licence status pending until evaluated.
        var next = new ServiceCollection();
        next.AddLogging();
        next.AddMarocLisWeb(configuration);
        await using var restarted = next.BuildServiceProvider();

        IReadOnlyList<string> problems;
        await using (var scope = restarted.CreateAsyncScope())
            problems = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

        problems.Should().BeEmpty(string.Join(" | ", problems));

        await using var context = database.Context();
        (await context.Samples.IgnoreQueryFilters().CountAsync(s => s.KeepUntil < DateOnly.FromDateTime(DateTime.Today)))
            .Should().BeGreaterThan(0, "the purge list has its expired tube");
        (await context.Invoices.IgnoreQueryFilters().CountAsync(i => i.ClaimReference != null))
            .Should().BeGreaterThan(0, "the clinic's order went to the CNSS in a claim batch");
    }
}
