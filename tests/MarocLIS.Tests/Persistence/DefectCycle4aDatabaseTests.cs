using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Application.Relations;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Relations;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for lot c4a, created from the model and dropped at the end.</summary>
public sealed class Cycle4aScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle4a_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle4aScratchDatabase()
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
/// Cycle 4, lot c4a, against SQL Server: the free texts a screen sends to a column of fixed width
/// (the reason a delegate's visit is deleted, the reason a numbering series is closed) are refused
/// or kept to the column, instead of failing the save with an unexpected error.
/// </summary>
public sealed class DefectCycle4aDatabaseTests(Cycle4aScratchDatabase database) : IClassFixture<Cycle4aScratchDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
        public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(10, 0)));
    }

    /// <summary>A biologist holding every right, signed in on the test's site.</summary>
    private sealed class Operator(Guid userId, Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId;
        public string UserName => "c4a";
        public string FullName => "Dr Cycle Quatre";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(MarocLIS.Domain.Identity.Permissions.All);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "C4A-1";
    }

    private sealed record Lab(ServiceProvider Services, LabSite Site, AppUser Biologist);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Errors(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private async Task<Lab> LabAsync()
    {
        var biologist = new AppUser(Unique("c4a"), "Dr Cycle Quatre", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-C4A-1"
        };
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire C4A"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE C4A SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-C4A-1",
            ResponsibleBiologistUserId = biologist.Id
        };

        await using (var context = database.Context())
        {
            context.Users.Add(biologist);
            context.Sites.Add(site);
            await context.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(biologist.Id, site.Id));
        services.AddSingleton<IClock>(new FixedClock());
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ICatalogMaintenanceQueries, CatalogMaintenanceQueries>();
        services.AddIsolatedQueries<IAdministrationQueries, AdministrationQueries>();
        services.AddIsolatedQueries<IRelationsQueries, RelationsQueries>();
        return new Lab(services.BuildServiceProvider(), site, biologist);
    }

    private static async Task<T> On<TService, T>(Lab lab, Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = lab.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    // ---- A delegate's visit deleted with a long reason ---------------------------------------------

    /// <summary>
    /// The deletion reason is kept in a column of 500 characters: a longer one is refused with the
    /// limit named, the visit stays, and a reason that fits deletes it.
    /// </summary>
    [SqlServerFact]
    public async Task AVisit_DeletedWithAReasonLongerThanItsColumn_IsRefusedAndKept()
    {
        var lab = await LabAsync();
        var prescriber = new Prescriber("BENANI", "Karim", PrescriberKind.GeneralPractitioner);
        await using (var context = database.Context())
        {
            context.Add(prescriber);
            await context.SaveChangesAsync();
        }

        var saved = await On<RelationsService, Result<Guid>>(lab, s => s.SaveVisitAsync(new SaveDelegateVisitRequest
        {
            VisitDate = DateOnly.FromDateTime(DateTime.Today),
            PrescriberId = prescriber.Id,
            DelegateUserId = lab.Biologist.Id,
            Purpose = VisitPurpose.FollowUp
        }));
        saved.IsSuccess.Should().BeTrue(Errors(saved));

        var tooLong = await On<RelationsService, Result>(lab, s =>
            s.DeleteVisitAsync(saved.Value, new string('m', RelationsService.DeletionReasonMaxLength + 1)));
        Errors(tooLong).Should().Be("Relations_TextTooLong");

        await using (var check = database.Context())
            (await check.Set<DelegateVisit>().AsNoTracking().CountAsync(v => v.Id == saved.Value)).Should().Be(1);

        var fits = await On<RelationsService, Result>(lab, s =>
            s.DeleteVisitAsync(saved.Value, new string('m', RelationsService.DeletionReasonMaxLength)));
        fits.IsSuccess.Should().BeTrue(Errors(fits));

        await using (var check = database.Context())
            (await check.Set<DelegateVisit>().AsNoTracking().CountAsync(v => v.Id == saved.Value)).Should().Be(0);
    }

    // ---- A numbering series closed with a long reason ----------------------------------------------

    /// <summary>
    /// The reason is written to the security journal after the series is closed, in a column of
    /// 2000 characters: a longer one failed with an unexpected error once the closing was already
    /// saved. The series is closed and the journal keeps the reason cut to its column.
    /// </summary>
    [SqlServerFact]
    public async Task ASequence_ClosedWithAVeryLongReason_IsClosedAndJournalled()
    {
        var lab = await LabAsync();
        var sequence = new NumberSequence(SequencePurpose.WorkAccident, null, 2098, "AT-{YEAR}-{SEQ:4}");
        await using (var context = database.Context())
        {
            context.Add(sequence);
            await context.SaveChangesAsync();
        }

        var reason = "Clôture de l'exercice " + new string('x', 2500);
        var closed = await On<ConfigurationService, Result>(lab, s => s.SetSequenceClosedAsync(sequence.Id, true, reason));
        closed.IsSuccess.Should().BeTrue(Errors(closed));

        await using var check = database.Context();
        (await check.Set<NumberSequence>().AsNoTracking().SingleAsync(x => x.Id == sequence.Id)).IsClosed.Should().BeTrue();
        var journalled = await check.SecurityEvents.AsNoTracking()
            .Where(e => e.Kind == SecurityEventKind.SecuritySettingChanged && e.Details!.StartsWith("WorkAccident closed"))
            .Select(e => e.Details!).ToListAsync();
        journalled.Should().ContainSingle().Which.Length.Should().Be(2000);
    }
}
