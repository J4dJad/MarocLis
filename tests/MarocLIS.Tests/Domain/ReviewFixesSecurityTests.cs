using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Seeding of built-in role permissions, account unlocking and the privilege checks of the user
/// administration service.
/// </summary>
public sealed class ReviewFixesSecurityTests
{
    private const string ManageUsers = Permissions.Administration.ManageUsers;
    private const string ManageRoles = Permissions.Administration.ManageRoles;
    private const string ViewUsers = Permissions.Administration.ViewUsers;
    private const string ViewAudit = Permissions.Administration.ViewAuditTrail;

    // ---- Built-in role seeding -----------------------------------------------------------------

    [Fact]
    public void A_role_created_by_the_seeder_gets_the_full_shipped_set()
    {
        var role = new Role("X", new MultilingualText("X", "X", "X"), isBuiltIn: true);

        role.SeedShippedPermissions([ViewUsers, ManageUsers]);

        role.Has(ViewUsers).Should().BeTrue();
        role.Has(ManageUsers).Should().BeTrue();
        role.SeededPermissions.Should().NotBeNull();
    }

    [Fact]
    public void A_permission_removed_by_an_administrator_is_not_granted_back_on_restart()
    {
        var role = new Role("X", new MultilingualText("X", "X", "X"), isBuiltIn: true);
        role.SeedShippedPermissions([ViewUsers, ManageUsers]);

        role.Revoke(ManageUsers);
        var granted = role.ApplyShippedPermissions([ViewUsers, ManageUsers]);

        granted.Should().BeEmpty();
        role.Has(ManageUsers).Should().BeFalse();
    }

    [Fact]
    public void A_permission_new_in_the_shipped_set_is_granted_once()
    {
        var role = new Role("X", new MultilingualText("X", "X", "X"), isBuiltIn: true);
        role.SeedShippedPermissions([ViewUsers]);

        role.ApplyShippedPermissions([ViewUsers, ViewAudit]).Should().Equal(ViewAudit);
        role.Has(ViewAudit).Should().BeTrue();

        // Removed afterwards by the administrator: stays removed.
        role.Revoke(ViewAudit);
        role.ApplyShippedPermissions([ViewUsers, ViewAudit]).Should().BeEmpty();
        role.Has(ViewAudit).Should().BeFalse();
    }

    [Fact]
    public void A_role_with_nothing_recorded_gets_the_shipped_permissions_its_version_lacked()
    {
        // A role seeded by a version that did not record what it offered — and that granted every
        // missing shipped permission back at each start, so a missing one is new, not removed.
        var role = new Role("X", new MultilingualText("X", "X", "X"), isBuiltIn: true);
        role.Grant(ViewUsers);

        role.ApplyShippedPermissions([ViewUsers, ManageUsers]).Should().Equal(ManageUsers);
        role.Has(ManageUsers).Should().BeTrue();
        role.SeededPermissions.Should().Contain(ManageUsers);

        // From then on, only what is new is granted.
        role.ApplyShippedPermissions([ViewUsers, ManageUsers, ViewAudit]).Should().Equal(ViewAudit);
    }

    // ---- Unlock --------------------------------------------------------------------------------

    [Fact]
    public void Clearing_a_lockout_does_not_touch_the_active_flag()
    {
        var user = new AppUser("tech", "Tech", "hash");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) user.RegisterFailedLogin(now, 5, TimeSpan.FromMinutes(15));

        user.ClearLockout();

        user.IsLockedOut(now).Should().BeFalse();
        user.FailedLoginAttempts.Should().Be(0);
        user.IsActive.Should().BeTrue();
    }

    [Fact]
    public void An_inactive_account_cannot_be_unlocked_in_the_domain()
    {
        var user = new AppUser("tech", "Tech", "hash");
        user.Deactivate();

        var act = () => user.ClearLockout();

        act.Should().Throw<DomainException>();
        user.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Unlocking_a_deactivated_account_is_refused_and_does_not_reactivate_it()
    {
        var user = new AppUser("tech", "Tech", "hash");
        user.RegisterFailedLogin(DateTimeOffset.UtcNow, 5, TimeSpan.FromMinutes(15));
        user.Deactivate();

        var fixture = new Fixture(ManageUsers);
        fixture.Users[user.Id] = user;

        var result = await fixture.Service.UnlockAsync(user.Id);

        result.FirstError.ResourceKey.Should().Be("User_CannotUnlockInactive");
        user.IsActive.Should().BeFalse();
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Unlocking_an_active_account_clears_its_failed_attempts()
    {
        var user = new AppUser("tech", "Tech", "hash");
        user.RegisterFailedLogin(DateTimeOffset.UtcNow, 5, TimeSpan.FromMinutes(15));

        var fixture = new Fixture(ManageUsers);
        fixture.Users[user.Id] = user;

        (await fixture.Service.UnlockAsync(user.Id)).IsSuccess.Should().BeTrue();
        user.FailedLoginAttempts.Should().Be(0);
    }

    // ---- Role deactivation ---------------------------------------------------------------------

    [Fact]
    public async Task Deactivating_the_role_of_the_last_administrators_is_refused()
    {
        var role = new Role("GESTION", new MultilingualText("G", "G", "G"));
        role.Grant(ManageUsers);

        var fixture = new Fixture(ManageRoles) { Survivors = 0 };
        fixture.Roles[role.Id] = role;

        var result = await fixture.Service.SetRoleActiveAsync(role.Id, active: false);

        result.FirstError.ResourceKey.Should().Be("Role_WouldRemoveLastAdministrator");
        role.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Deactivating_an_administrative_role_is_allowed_when_another_administrator_remains()
    {
        var role = new Role("GESTION", new MultilingualText("G", "G", "G"));
        role.Grant(ManageUsers);

        var fixture = new Fixture(ManageRoles) { Survivors = 1 };
        fixture.Roles[role.Id] = role;

        (await fixture.Service.SetRoleActiveAsync(role.Id, active: false)).IsSuccess.Should().BeTrue();
        role.IsActive.Should().BeFalse();
    }

    // ---- Privilege checks ----------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_user_with_roles_requires_role_management()
    {
        var fixture = new Fixture(ManageUsers);

        var result = await fixture.Service.CreateUserAsync(new CreateUserRequest
        {
            UserName = "nouveau",
            FullName = "Nouveau",
            InitialPassword = "Motdepasse#2026",
            RoleIds = [Guid.NewGuid()]
        });

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be(Fixture.ForbiddenKey);
        fixture.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Creating_a_user_without_roles_needs_only_user_management()
    {
        var fixture = new Fixture(ManageUsers);

        var result = await fixture.Service.CreateUserAsync(new CreateUserRequest
        {
            UserName = "nouveau",
            FullName = "Nouveau",
            InitialPassword = "Motdepasse#2026"
        });

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Resetting_the_password_of_a_more_privileged_account_is_refused()
    {
        var admin = new AppUser("admin", "Admin", "hash");
        admin.AssignRole(Guid.NewGuid());

        var fixture = new Fixture(ManageUsers);
        fixture.Users[admin.Id] = admin;
        fixture.RolePermissions = [ManageUsers, ManageRoles];

        var result = await fixture.Service.ResetPasswordAsync(admin.Id, "Motdepasse#2026");

        result.FirstError.ResourceKey.Should().Be("User_ResetPasswordExceedsOwnPermissions");
        fixture.PasswordResets.Should().Be(0);
    }

    [Fact]
    public async Task Resetting_the_password_of_an_account_within_ones_own_permissions_is_allowed()
    {
        var tech = new AppUser("tech", "Tech", "hash");
        tech.AssignRole(Guid.NewGuid());

        var fixture = new Fixture(ManageUsers, ViewUsers);
        fixture.Users[tech.Id] = tech;
        fixture.RolePermissions = [ViewUsers];

        (await fixture.Service.ResetPasswordAsync(tech.Id, "Motdepasse#2026")).IsSuccess.Should().BeTrue();
        fixture.PasswordResets.Should().Be(1);
    }

    // ---- Fixture -------------------------------------------------------------------------------

    /// <summary>
    /// The service over in-memory stand-ins: only the members these tests reach are given a
    /// behaviour, every other member returns its type's empty value.
    /// </summary>
    private sealed class Fixture
    {
        public const string ForbiddenKey = "Forbidden";

        public Dictionary<Guid, AppUser> Users { get; } = [];
        public Dictionary<Guid, Role> Roles { get; } = [];
        public HashSet<string> RolePermissions { get; set; } = [];
        public int Survivors { get; set; } = 1;
        public int Saves { get; private set; }
        public int PasswordResets { get; private set; }

        public UserAdministrationService Service { get; }

        public Fixture(params string[] callerPermissions)
        {
            var granted = callerPermissions.ToHashSet(StringComparer.Ordinal);
            var callerId = Guid.NewGuid();

            var guard = Stub<IPermissionGuard>.Create(new()
            {
                [nameof(IPermissionGuard.Require)] = a => granted.Contains((string)a[0]!)
                    ? Result.Success()
                    : Result.Failure(Error.Forbidden(ForbiddenKey))
            });

            var currentUser = Stub<ICurrentUser>.Create(new()
            {
                [nameof(ICurrentUser.Has)] = a => granted.Contains((string)a[0]!),
                ["get_" + nameof(ICurrentUser.UserId)] = _ => callerId,
                ["get_" + nameof(ICurrentUser.SiteId)] = _ => Guid.NewGuid()
            });

            var users = Stub<IRepository<AppUser>>.Create(new()
            {
                [nameof(IRepository<AppUser>.GetByIdAsync)] = a => Task.FromResult(Users.GetValueOrDefault((Guid)a[0]!)),
                [nameof(IRepository<AppUser>.GetByIdWithDetailsAsync)] = a => Task.FromResult(Users.GetValueOrDefault((Guid)a[0]!)),
                [nameof(IRepository<AppUser>.AddAsync)] = a => { var u = (AppUser)a[0]!; Users[u.Id] = u; return Task.CompletedTask; }
            });

            var roles = Stub<IRepository<Role>>.Create(new()
            {
                [nameof(IRepository<Role>.GetByIdAsync)] = a => Task.FromResult(Roles.GetValueOrDefault((Guid)a[0]!)),
                [nameof(IRepository<Role>.GetByIdWithDetailsAsync)] = a => Task.FromResult(Roles.GetValueOrDefault((Guid)a[0]!))
            });

            var queries = Stub<IAdministrationQueries>.Create(new()
            {
                [nameof(IAdministrationQueries.UserNameExistsAsync)] = _ => Task.FromResult(false),
                [nameof(IAdministrationQueries.GetPermissionsForRolesAsync)] = a =>
                    Task.FromResult<IReadOnlySet<string>>(((IReadOnlyList<Guid>)a[0]!).Count == 0
                        ? new HashSet<string>() : RolePermissions),
                [nameof(IAdministrationQueries.CountActiveUsersWithPermissionAsync)] = _ => Task.FromResult(Survivors)
            });

            var authentication = Stub<IAuthenticationService>.Create(new()
            {
                [nameof(IAuthenticationService.ResetPasswordAsync)] = _ => { PasswordResets++; return Task.FromResult(Result.Success()); }
            });

            var unitOfWork = Stub<IUnitOfWork>.Create(new()
            {
                [nameof(IUnitOfWork.SaveChangesAsync)] = _ => { Saves++; return Task.FromResult(1); }
            });

            var clock = Stub<IClock>.Create(new()
            {
                ["get_" + nameof(IClock.Now)] = _ => DateTimeOffset.UtcNow,
                ["get_" + nameof(IClock.Today)] = _ => DateOnly.FromDateTime(DateTime.UtcNow)
            });

            var hasher = Stub<IPasswordHasher>.Create(new()
            {
                [nameof(IPasswordHasher.Hash)] = _ => "hash"
            });

            Service = new UserAdministrationService(
                users, roles, queries, authentication, unitOfWork, guard, currentUser,
                Stub<ISecurityAuditor>.Create(), hasher, new PasswordPolicy(), clock);
        }
    }

    /// <summary>
    /// An interface implemented by a table of handlers keyed by member name. Unlisted members
    /// return the empty value of their type, a completed task for <c>Task</c>, and a completed task
    /// of the default value for <c>Task&lt;T&gt;</c>.
    /// </summary>
    public class Stub<T> : DispatchProxy where T : class
    {
        private Dictionary<string, Func<object?[], object?>> _handlers = [];

        public static T Create(Dictionary<string, Func<object?[], object?>>? handlers = null)
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy)._handlers = handlers ?? [];
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (_handlers.TryGetValue(method.Name, out var handler))
                return handler(args ?? []);

            var type = method.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                    .Invoke(null, [inner.IsValueType ? Activator.CreateInstance(inner) : null]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
