using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own, uniquely named, created and dropped by the class.</summary>
public sealed class SiteCreationDatabase : IDisposable
{
    private readonly bool _created;

    public SiteCreationDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_nouveausite_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;
    }

    public string ConnectionString { get; } = string.Empty;

    public LisDbContext Context(ICurrentUser? user = null) =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options, user);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// A partner laboratory created from the settings is offered where analyses are sent out, and
/// nowhere someone works: not in the user editor's sites, not at sign-in, not on a site switch.
/// </summary>
public sealed class SiteCreationDatabaseTests(SiteCreationDatabase database) : IClassFixture<SiteCreationDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 15);
    }

    private static readonly IPasswordHasher Hasher = new BCryptPasswordHasher(4);

    private async Task<Guid> CreatePartnerAsync(string code)
    {
        await using var context = database.Context();
        var clock = new FixedClock();
        var guard = Stub<IPermissionGuard>.Create(new()
        {
            [nameof(IPermissionGuard.Require)] = _ => Result.Success()
        });

        var service = new ConfigurationService(
            new Repository<LabSite>(context, clock), new Repository<AppUser>(context, clock),
            new AdministrationQueries(context), new ConfigurationWriter(context), new UnitOfWork(context),
            guard, Stub<ISecurityAuditor>.Create(), clock);

        var created = await service.CreateSiteAsync(new NewSiteRequest
        {
            Code = code.ToLowerInvariant(),
            Name = new MultilingualText("Laboratoire partenaire " + code)
        });

        created.IsSuccess.Should().BeTrue(string.Join(", ", created.Errors.Select(e => e.ResourceKey)));

        // The same code a second time is refused by the service, not by the unique index.
        var again = await service.CreateSiteAsync(new NewSiteRequest
        {
            Code = code,
            Name = new MultilingualText("Doublon")
        });
        again.FirstError.ResourceKey.Should().Be("Site_CodeAlreadyTaken");

        return created.Value;
    }

    [SqlServerFact]
    public async Task A_partner_created_in_the_settings_is_offered_for_subcontracting_only()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var partner = await CreatePartnerAsync("P" + suffix);

        Guid main;
        await using (var context = database.Context())
        {
            var site = new LabSite("M" + suffix, new MultilingualText("Laboratoire"), LabSiteKind.MainLaboratory);
            context.Sites.Add(site);
            await context.SaveChangesAsync();
            main = site.Id;

            (await context.Sites.SingleAsync(s => s.Id == partner)).Code.Should().Be("P" + suffix);
        }

        await using (var context = database.Context())
        {
            (await new CatalogMaintenanceQueries(context).GetPartnerSitesAsync())
                .Select(s => s.Id).Should().Contain(partner).And.NotContain(main);
            (await new SubcontractQueries(context).GetPartnersAsync())
                .Select(s => s.Id).Should().Contain(partner).And.NotContain(main);

            var queries = new AdministrationQueries(context);
            (await queries.GetSitesAsync()).Select(s => s.SiteId)
                .Should().Contain(main).And.NotContain(partner, "nobody is given access to a partner");
            (await queries.AnyPartnerSiteAsync([main, partner])).Should().BeTrue();
            (await queries.AnyPartnerSiteAsync([main])).Should().BeFalse();
            (await queries.SiteHasUserAccessAsync(main)).Should().BeFalse();
        }
    }

    [SqlServerFact]
    public async Task A_deleted_sites_code_is_still_taken()
    {
        var code = "D" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        await using (var context = database.Context())
        {
            context.Sites.Add(new LabSite(code, new MultilingualText("Ancien centre"), LabSiteKind.CollectionCentre)
            {
                IsDeleted = true,
                DeletedAt = new FixedClock().Now
            });
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
            (await new AdministrationQueries(context).SiteCodeExistsAsync(code.ToLowerInvariant(), Guid.Empty))
                .Should().BeTrue("the unique index on the code covers deleted sites too");
    }

    [SqlServerFact]
    public async Task Nobody_signs_in_to_or_switches_to_a_partner_laboratory()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var partner = await CreatePartnerAsync("Q" + suffix);
        const string password = "Mot-De-Passe-2026!";
        var userName = "op" + suffix.ToLowerInvariant();

        Guid main;
        await using (var context = database.Context())
        {
            var site = new LabSite("N" + suffix, new MultilingualText("Laboratoire"), LabSiteKind.MainLaboratory);
            context.Sites.Add(site);
            main = site.Id;

            // An access row to the partner, as an installation could hold from before the user
            // administration refused one.
            var user = new AppUser(userName, "Opérateur", Hasher.Hash(password));
            user.SetPassword(user.PasswordHash, new FixedClock().Now, false);
            user.GrantSiteAccess(main, isDefault: true);
            user.GrantSiteAccess(partner);
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
        {
            var session = new CurrentUserAccessor();
            var auth = new AuthenticationService(context, Hasher, new PasswordPolicy(), session,
                                                 Stub<ISecurityAuditor>.Create(), new FixedClock());

            var onPartner = await auth.SignInAsync(userName, password, partner);
            onPartner.IsFailure.Should().BeTrue();
            onPartner.FirstError.ResourceKey.Should().Be("Auth_SiteNotAuthorized");

            (await auth.SignInAsync(userName, password, main)).IsSuccess.Should().BeTrue();
            session.SiteId.Should().Be(main);

            var switched = await auth.SwitchSiteAsync(partner);
            switched.IsFailure.Should().BeTrue();
            session.SiteId.Should().Be(main);
        }

        await using (var context = database.Context())
            (await new AdministrationQueries(context).SiteHasUserAccessAsync(partner)).Should().BeTrue();
    }
}
