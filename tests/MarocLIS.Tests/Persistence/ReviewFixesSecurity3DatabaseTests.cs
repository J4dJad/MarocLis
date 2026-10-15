using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Persistence.Demo;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A migrated and seeded scratch database of its own, <c>MarocLIS_FixSecurity3</c>, dropped at the
/// end. An existing database of that name is refused rather than reused.
/// </summary>
public sealed class SeededSecurityDatabase : IDisposable
{
    public const string Name = "MarocLIS_FixSecurity3";

    public string ConnectionString { get; } = "";

    public IPasswordHasher Hasher { get; } = new BCryptPasswordHasher(10);

    private readonly bool _created;

    public SeededSecurityDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        if (context.Database.CanConnect())
            throw new InvalidOperationException(
                $"Database {Name} already exists; the tests create and drop their own and will not touch an existing one.");

        _created = true;
        Initializer(context).InitializeAsync().GetAwaiter().GetResult()
            .IsSuccess.Should().BeTrue();
    }

    public LisDbContext Context(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString)
                .AddInterceptors(interceptors).Options);

    public DatabaseInitializer Initializer(LisDbContext context) => new(context, Hasher, new FixedClock());

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }

    public sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 15);
    }
}

/// <summary>
/// The demonstration accounts once the database holds real data (X1), and the session check
/// running while the operator's own password change is being saved (X6).
/// </summary>
public sealed class ReviewFixesSecurity3DatabaseTests(SeededSecurityDatabase database)
    : IClassFixture<SeededSecurityDatabase>
{
    [SqlServerFact]
    public async Task Demonstration_accounts_on_their_published_passwords_are_disabled_once_real_data_exists()
    {
        var site = await SiteIdAsync();

        // A production database started once with the flag on: demonstration accounts and
        // patients, nothing real yet. One of the accounts has had its password changed.
        await using (var db = database.Context())
        {
            db.Users.Add(DemoUser("demo.biologiste", site));
            db.Users.Add(DemoUser("demo.technicien", site, password: "Choisi-Par-Le-Labo-2026!"));
            db.Patients.Add(new Patient(DemoDataSeeder.DemoPrefix + "X1-0001", "DEMO", "Patient",
                                        new DateOnly(1980, 1, 1), Gender.Female, site));
            await db.SaveChangesAsync();
        }

        // Demonstration data only: a demonstration machine keeps its accounts.
        await using (var db = database.Context())
        {
            (await DemoAccountGuard.DisableExposedAccountsAsync(db, database.Hasher)).Should().BeEmpty();
            (await database.Initializer(db).InitializeAsync()).IsSuccess.Should().BeTrue();
        }
        (await IsActiveAsync("demo.biologiste")).Should().BeTrue();

        // The laboratory registers its first real patient; the next start disables the account
        // whose password is still the published one, and only that one.
        await using (var db = database.Context())
        {
            db.Patients.Add(new Patient("P-X1000001", "ALAOUI", "Sara",
                                        new DateOnly(1990, 5, 5), Gender.Female, site));
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
            (await database.Initializer(db).InitializeAsync()).IsSuccess.Should().BeTrue();

        (await IsActiveAsync("demo.biologiste")).Should().BeFalse();
        (await IsActiveAsync("demo.technicien")).Should().BeTrue();

        // Between the first real patient and the next start: refused at its own sign-in.
        await using (var db = database.Context())
        {
            db.Users.Add(DemoUser("demo.secretaire", site));
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var published = DemoDataSeeder.Users.Single(u => u.UserName == "demo.secretaire").Password;
            var result = await Authentication(db, new CurrentUserAccessor())
                .SignInAsync("demo.secretaire", published, null);

            result.IsFailure.Should().BeTrue();
            result.FirstError.ResourceKey.Should().Be("Auth_DemoAccountDisabled");
        }

        (await IsActiveAsync("demo.secretaire")).Should().BeFalse();
    }

    [SqlServerFact]
    public async Task A_session_check_during_ones_own_password_change_does_not_end_the_session()
    {
        var site = await SiteIdAsync();
        const string password = "Ancien-Mot-2026!";

        await using (var db = database.Context())
        {
            var user = new AppUser("x6.operateur", "Opérateur X6", database.Hasher.Hash(password));
            user.SetPassword(user.PasswordHash, new SeededSecurityDatabase.FixedClock().Now, false);
            user.GrantSiteAccess(site, isDefault: true);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var session = new CurrentUserAccessor();
        var services = new ServiceCollection()
            .AddScoped(_ => database.Context())
            .BuildServiceProvider();
        var validator = new SessionValidator(services.GetRequiredService<IServiceScopeFactory>(), session);

        // The shell's minute tick, run right after each save of the change has committed — the
        // moment the race left the new stamp in the database and the old one in the session.
        var probe = new SessionCheckAfterSave(validator);

        await using (var db = database.Context(probe))
        {
            var auth = Authentication(db, session);
            (await auth.SignInAsync("x6.operateur", password, null)).IsSuccess.Should().BeTrue();

            probe.Armed = true;
            (await auth.ChangePasswordAsync(session.UserId, password, "Nouveau-Mot-2026!")).IsSuccess
                .Should().BeTrue();
            probe.Armed = false;
        }

        probe.Results.Should().NotBeEmpty().And.OnlyContain(valid => valid);
        (await validator.IsStillValidAsync()).Should().BeTrue();
    }

    private sealed class SessionCheckAfterSave(SessionValidator validator) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public List<bool> Results { get; } = [];

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
                                                               CancellationToken cancellationToken = default)
        {
            if (Armed) Results.Add(await validator.IsStillValidAsync(cancellationToken));
            return result;
        }
    }

    private AuthenticationService Authentication(LisDbContext db, CurrentUserAccessor session) =>
        new(db, database.Hasher, new PasswordPolicy(), session, Stub<ISecurityAuditor>.Create(),
            new SeededSecurityDatabase.FixedClock());

    private AppUser DemoUser(string userName, Guid site, string? password = null)
    {
        var demo = DemoDataSeeder.Users.Single(u => u.UserName == userName);
        var user = new AppUser(userName, demo.FullName, database.Hasher.Hash(password ?? demo.Password))
        {
            Qualification = demo.Qualification
        };
        user.SetPassword(user.PasswordHash, new SeededSecurityDatabase.FixedClock().Now, false);
        user.GrantSiteAccess(site, isDefault: true);
        return user;
    }

    private async Task<Guid> SiteIdAsync()
    {
        await using var db = database.Context();
        return await db.Sites.IgnoreQueryFilters().Select(s => s.Id).FirstAsync();
    }

    private async Task<bool> IsActiveAsync(string userName)
    {
        await using var db = database.Context();
        return await db.Users.IgnoreQueryFilters().Where(u => u.UserName == userName)
            .Select(u => u.IsActive).SingleAsync();
    }
}
