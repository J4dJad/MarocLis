using System.Collections.Concurrent;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own, uniquely named, created and dropped by the class.</summary>
public sealed class LockoutRaceDatabase : IDisposable
{
    private readonly bool _created;

    public LockoutRaceDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_verrou_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;
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
/// Wrong passwords sent all at once are each counted and each logged: the lockout after five
/// failures cannot be stretched into five rounds of any size, and none of them ends on an error page.
/// </summary>
public sealed class LockoutRaceDatabaseTests(LockoutRaceDatabase database) : IClassFixture<LockoutRaceDatabase>
{
    private const int Parallel = 12;
    private const string RightPassword = "Juste-Mot-2026!";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 27);
    }

    /// <summary>
    /// Holds every password check until all the attempts have read the account, so they all race
    /// on the same counter value — the window the hash's quarter of a second opens in production.
    /// </summary>
    private sealed class GatedHasher(CountdownEvent gate) : IPasswordHasher
    {
        private static readonly BCryptPasswordHasher Inner = new(4);

        public string Hash(string password) => Inner.Hash(password);

        public bool Verify(string password, string hash)
        {
            var verified = Inner.Verify(password, hash);
            gate.Signal();
            gate.Wait(TimeSpan.FromSeconds(30));
            return verified;
        }

        public bool NeedsRehash(string hash) => false;
    }

    private async Task<Guid> CreateUserAsync(string userName)
    {
        await using var context = database.Context();
        var user = new AppUser(userName, "Biologiste " + userName, new BCryptPasswordHasher(4).Hash(RightPassword));
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static ISecurityAuditor Recorder(ConcurrentBag<(SecurityEventKind Kind, string? Details)> events) =>
        Stub<ISecurityAuditor>.Create(new()
        {
            [nameof(ISecurityAuditor.LogAsync)] = a =>
            {
                events.Add(((SecurityEventKind)a[0]!, (string?)a[5]));
                return Task.CompletedTask;
            }
        });

    private async Task<(int Attempts, DateTimeOffset? LockedOutUntil)> CounterOfAsync(Guid userId)
    {
        await using var context = database.Context();
        var user = await context.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId);
        return (user.FailedLoginAttempts, user.LockedOutUntil);
    }

    [SqlServerFact]
    public async Task Parallel_wrong_passwords_at_sign_in_are_all_counted_and_logged()
    {
        const string userName = "biologiste-course";
        var userId = await CreateUserAsync(userName);

        using var gate = new CountdownEvent(Parallel);
        var events = new ConcurrentBag<(SecurityEventKind Kind, string? Details)>();

        var attempts = Enumerable.Range(0, Parallel).Select(i => Task.Run(async () =>
        {
            await using var context = database.Context();
            var auth = new AuthenticationService(context, new GatedHasher(gate), new PasswordPolicy(),
                                                 new CurrentUserAccessor(), Recorder(events), new FixedClock());
            return await auth.SignInAsync(userName, $"Faux-{i}", null);
        })).ToList();

        var answers = await Task.WhenAll(attempts);

        // Every guess gets the ordinary refusal, never an unexpected error.
        answers.Should().OnlyContain(a => a.IsFailure && a.FirstError.ResourceKey == "Auth_InvalidCredentials");

        var (count, lockedOutUntil) = await CounterOfAsync(userId);
        count.Should().Be(Parallel);
        lockedOutUntil.Should().NotBeNull();

        events.Should().HaveCount(Parallel);
        events.Should().Contain(e => e.Kind == SecurityEventKind.AccountLockedOut);
        events.Should().OnlyContain(e => e.Details != null && e.Details.StartsWith("Attempt "));

        // Locked: even the right password is now refused, with the lockout said to its holder.
        await using (var context = database.Context())
        {
            var auth = new AuthenticationService(context, new BCryptPasswordHasher(4), new PasswordPolicy(),
                                                 new CurrentUserAccessor(), Recorder([]), new FixedClock());
            (await auth.SignInAsync(userName, RightPassword, null)).FirstError.ResourceKey
                .Should().Be("Auth_AccountLockedOut");
        }
    }

    [SqlServerFact]
    public async Task Parallel_wrong_passwords_in_the_approval_box_are_all_counted_and_logged()
    {
        const string userName = "approbateur-course";
        var userId = await CreateUserAsync(userName);

        using var gate = new CountdownEvent(Parallel);
        var events = new ConcurrentBag<(SecurityEventKind Kind, string? Details)>();

        var attempts = Enumerable.Range(0, Parallel).Select(i => Task.Run(async () =>
        {
            await using var context = database.Context();
            var auth = new AuthenticationService(context, new GatedHasher(gate), new PasswordPolicy(),
                                                 new CurrentUserAccessor(), Recorder(events), new FixedClock());
            return await auth.VerifyApproverAsync(userName, $"Faux-{i}", Permissions.Billing.CreditNote);
        })).ToList();

        var answers = await Task.WhenAll(attempts);

        answers.Should().OnlyContain(a => a.IsFailure && a.FirstError.ResourceKey == "Approval_InvalidCredentials");

        var (count, lockedOutUntil) = await CounterOfAsync(userId);
        count.Should().Be(Parallel);
        lockedOutUntil.Should().NotBeNull();

        events.Should().HaveCount(Parallel);
        events.Should().Contain(e => e.Kind == SecurityEventKind.AccountLockedOut);
        events.Should().OnlyContain(e => e.Details != null && e.Details.StartsWith("Approval; attempt "));
    }

    private async Task<Guid> CreateWorkingUserAsync(string userName, int failedAttempts = 0, string? permission = null)
    {
        await using var context = database.Context();
        var site = new LabSite("S" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Principal"),
                               LabSiteKind.MainLaboratory);
        context.Sites.Add(site);

        var user = new AppUser(userName, "Biologiste " + userName, new BCryptPasswordHasher(4).Hash(RightPassword));
        user.GrantSiteAccess(site.Id, isDefault: true);
        for (var i = 0; i < failedAttempts; i++)
            user.RegisterFailedLogin(new FixedClock().Now, maxAttempts: 5, TimeSpan.FromMinutes(15));

        if (permission is not null)
        {
            var role = new Role("R" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Approbation"));
            role.Grant(permission);
            context.Roles.Add(role);
            user.AssignRole(role.Id);
        }

        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    [SqlServerFact]
    public async Task Two_correct_sign_ins_of_the_same_account_at_the_same_moment_both_open_a_session()
    {
        const string userName = "biologiste-double";
        var userId = await CreateWorkingUserAsync(userName, failedAttempts: 2);

        // Both read the account before either saves: a double click on « Se connecter », or the
        // same person signing in on two workstations at once.
        using var gate = new CountdownEvent(2);
        var events = new ConcurrentBag<(SecurityEventKind Kind, string? Details)>();

        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var context = database.Context();
            var auth = new AuthenticationService(context, new GatedHasher(gate), new PasswordPolicy(),
                                                 new CurrentUserAccessor(), Recorder(events), new FixedClock());
            return await auth.SignInAsync(userName, RightPassword, null);
        })));

        answers.Should().OnlyContain(a => a.IsSuccess, "the password was right both times; no error page");
        events.Should().OnlyContain(e => e.Kind == SecurityEventKind.LoginSucceeded).And.HaveCount(2);

        await using var check = database.Context();
        var user = await check.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId);
        (user.FailedLoginAttempts, user.LastLoginAt).Should().Be((0, new FixedClock().Now));
    }

    [SqlServerFact]
    public async Task A_correct_sign_in_does_not_undo_a_deactivation_made_while_the_password_was_checked()
    {
        const string userName = "biologiste-partie";
        var userId = await CreateWorkingUserAsync(userName);

        await using var context = database.Context();
        var auth = new AuthenticationService(context, new DeactivatingHasher(database, userId), new PasswordPolicy(),
                                             new CurrentUserAccessor(), Recorder([]), new FixedClock());

        (await auth.SignInAsync(userName, RightPassword, null)).IsFailure.Should().BeTrue();

        await using var check = database.Context();
        (await check.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId)).IsActive
            .Should().BeFalse("the administrator's deactivation stands");
    }

    [SqlServerFact]
    public async Task Two_correct_approvals_of_the_same_account_at_the_same_moment_both_pass()
    {
        const string userName = "approbateur-double";
        var userId = await CreateWorkingUserAsync(userName, failedAttempts: 1, permission: Permissions.Billing.CreditNote);

        using var gate = new CountdownEvent(2);
        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var context = database.Context();
            var auth = new AuthenticationService(context, new GatedHasher(gate), new PasswordPolicy(),
                                                 new CurrentUserAccessor(), Recorder([]), new FixedClock());
            return await auth.VerifyApproverAsync(userName, RightPassword, Permissions.Billing.CreditNote);
        })));

        answers.Should().OnlyContain(a => a.IsSuccess);
        (await CounterOfAsync(userId)).Attempts.Should().Be(0);
    }

    /// <summary>An administrator deactivates the account while its password is being checked.</summary>
    private sealed class DeactivatingHasher(LockoutRaceDatabase database, Guid userId) : IPasswordHasher
    {
        private static readonly BCryptPasswordHasher Inner = new(4);

        public string Hash(string password) => Inner.Hash(password);

        public bool Verify(string password, string hash)
        {
            using var context = database.Context();
            var user = context.Users.IgnoreQueryFilters().Single(u => u.Id == userId);
            user.Deactivate();
            context.SaveChanges();
            return Inner.Verify(password, hash);
        }

        public bool NeedsRehash(string hash) => false;
    }
}
