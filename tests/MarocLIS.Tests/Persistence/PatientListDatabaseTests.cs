using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the patient list, foreign keys off.</summary>
public sealed class PatientListScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Patients_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public PatientListScratchDatabase()
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
/// The patient list's scope chips and payer filter, against SQL Server: "Identité à compléter",
/// "Vus aujourd'hui", the payer of the primary coverage, and the counts the chips show.
/// </summary>
public sealed class PatientListDatabaseTests(PatientListScratchDatabase database)
    : IClassFixture<PatientListScratchDatabase>
{
    private sealed class Operator(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "accueil";
        public string FullName => "Secrétaire d'accueil";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private ServiceProvider Services(Guid siteId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId));
        // As DesktopServiceCollectionExtensions registers it for the screen.
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        return services.BuildServiceProvider();
    }

    private static Patient Named(string family, string first, DateOnly? born, Gender gender, Guid siteId) =>
        new(Unique("P"), family, first, born, gender, siteId)
        {
            SearchKey = PatientSearchKey.Build(family, first)
        };

    [SqlServerFact]
    public async Task Scopes_and_payer_narrow_the_list_and_the_chips_count_them()
    {
        var siteId = Guid.NewGuid();
        var family = Unique("ZLIST");
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var amo = new Payer(Unique("AMO"), new MultilingualText("AMO — CNSS"), PayerKind.AmoCnss);

        var complete = Named(family, "Complet", new DateOnly(1970, 5, 1), Gender.Female, siteId);
        complete.AddCoverage(new PatientCoverage(complete.Id, amo.Id, "123456", validFrom));

        var noBirth = Named(family, "Sansdate", null, Gender.Male, siteId);
        var unknown = Named(family, "Inconnu", null, Gender.Unknown, siteId);

        var now = DateTimeOffset.Now;
        var today = new LabOrder(Unique("A"), complete.Id, siteId, now, Guid.NewGuid());
        var yesterday = new LabOrder(Unique("A"), noBirth.Id, siteId, now.AddDays(-1), Guid.NewGuid());
        var elsewhere = new LabOrder(Unique("A"), unknown.Id, Guid.NewGuid(), now, Guid.NewGuid());

        await using (var context = database.Context())
        {
            context.Payers.Add(amo);
            context.Patients.AddRange(complete, noBirth, unknown);
            context.LabOrders.AddRange(today, yesterday, elsewhere);
            await context.SaveChangesAsync();
        }

        await using var provider = Services(siteId);
        var queries = provider.GetRequiredService<IPatientQueries>();

        var all = await queries.ListAsync(new PatientListQuery(family, siteId));
        all.TotalCount.Should().Be(3);
        all.Items.Single(p => p.Id == complete.Id).IdentityGaps.Should().Be(PatientIdentityGap.None);
        all.Items.Single(p => p.Id == noBirth.Id).IdentityGaps.Should().Be(PatientIdentityGap.DateOfBirth);
        all.Items.Single(p => p.Id == unknown.Id).IdentityGaps
            .Should().Be(PatientIdentityGap.Gender | PatientIdentityGap.DateOfBirth);

        var incomplete = await queries.ListAsync(new PatientListQuery(family, siteId, PatientListScope.IdentityIncomplete));
        incomplete.Items.Select(p => p.Id).Should().BeEquivalentTo([noBirth.Id, unknown.Id]);

        // Today, at this site: the order registered elsewhere and yesterday's do not count.
        var seen = await queries.ListAsync(new PatientListQuery(family, siteId, PatientListScope.SeenToday));
        seen.Items.Select(p => p.Id).Should().Equal(complete.Id);

        var insured = await queries.ListAsync(new PatientListQuery(family, siteId, PayerId: amo.Id));
        insured.Items.Select(p => p.Id).Should().Equal(complete.Id);

        var counts = await queries.CountListAsync(new PatientListQuery(family, siteId));
        counts.Should().Be(new PatientListCounts(3, 2, 1));

        var insuredCounts = await queries.CountListAsync(new PatientListQuery(family, siteId, PayerId: amo.Id));
        insuredCounts.Should().Be(new PatientListCounts(1, 0, 1));

        // The search the order screen uses is unchanged: every file, whatever its scope.
        (await queries.SearchAsync(family, siteId)).TotalCount.Should().Be(3);
    }

    [SqlServerFact]
    public async Task A_provisional_file_is_incomplete_until_its_identity_is_confirmed()
    {
        var siteId = Guid.NewGuid();
        var provisional = Patient.Provisional(Unique("XURG"), Gender.Male, 40, siteId);
        provisional.SearchKey = PatientSearchKey.Build(provisional.LastName, provisional.FirstName);

        await using (var context = database.Context())
        {
            context.Patients.Add(provisional);
            await context.SaveChangesAsync();
        }

        await using var provider = Services(siteId);
        var queries = provider.GetRequiredService<IPatientQueries>();

        var page = await queries.ListAsync(new PatientListQuery(provisional.MedicalRecordNumber, siteId,
                                                                PatientListScope.IdentityIncomplete));
        page.Items.Should().ContainSingle()
            .Which.IdentityGaps.Should().HaveFlag(PatientIdentityGap.Provisional);
    }
}
