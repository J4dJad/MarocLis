using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Reports;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Prescriber scoping, self-edits of the competence record, revocation of open sessions, pay data
/// in the audit trail, the sign-out record, the password rehash and the locked-account answer.
/// </summary>
public sealed class ReviewFixesSecurity2Tests
{
    // ---- S1: prescriber scope ---------------------------------------------------------------------

    private static readonly IReadOnlySet<string> PrescriberRolePermissions = new HashSet<string>
    {
        Permissions.Patients.View, Permissions.Orders.View, Permissions.Results.View,
        Permissions.Reports.View, Permissions.Reports.ReprintValidated
    };

    [Fact]
    public void An_account_linked_to_a_prescriber_without_staff_permissions_is_scoped()
    {
        PrescriberScope.Applies(Guid.NewGuid(), ProfessionalQualification.None, [],
                                PrescriberRolePermissions).Should().BeTrue();
    }

    [Fact]
    public void An_unlinked_account_with_the_prescriber_role_is_scoped_and_so_sees_nothing()
    {
        PrescriberScope.Applies(null, ProfessionalQualification.None, [SystemRoles.Prescriber],
                                PrescriberRolePermissions).Should().BeTrue();
    }

    [Fact]
    public void A_biologist_who_is_also_a_prescriber_is_staff_and_not_scoped()
    {
        var permissions = new HashSet<string>(PrescriberRolePermissions) { Permissions.Results.BiologicalValidate };

        PrescriberScope.Applies(Guid.NewGuid(), ProfessionalQualification.MedicalBiologist,
                                [SystemRoles.Prescriber], permissions).Should().BeFalse();
    }

    [Fact]
    public void A_staff_account_with_no_prescriber_link_is_not_scoped()
    {
        PrescriberScope.Applies(null, ProfessionalQualification.Administrative, ["COMPTABILITE"],
                                new HashSet<string> { Permissions.Patients.View }).Should().BeFalse();
    }

    [Fact]
    public void Linking_a_prescriber_rotates_the_security_stamp()
    {
        var user = new AppUser("drx", "Dr X", "hash");
        var before = user.SecurityStamp;

        user.LinkPrescriber(Guid.NewGuid());

        user.PrescriberId.Should().NotBeNull();
        user.SecurityStamp.Should().NotBe(before);
    }

    [Fact]
    public void The_shipped_prescriber_role_cannot_release_reports_but_can_reprint_released_ones()
    {
        var permissions = ShippedRole(SystemRoles.Prescriber);

        permissions.Should().NotContain(Permissions.Reports.Print);
        permissions.Should().Contain(Permissions.Reports.ReprintValidated);
    }

    [Fact]
    public void An_existing_prescriber_role_loses_report_release_once()
    {
        var role = new Role(SystemRoles.Prescriber, new MultilingualText("P", "P", "P"), isBuiltIn: true);
        role.SeedShippedPermissions([Permissions.Reports.View, Permissions.Reports.Print]);

        var shipped = ShippedRole(SystemRoles.Prescriber);

        role.ReplaceShippedPermissionOnce(Permissions.Reports.Print, Permissions.Reports.ReprintValidated)
            .Should().BeTrue();
        role.ApplyShippedPermissions(shipped);

        role.Has(Permissions.Reports.Print).Should().BeFalse();
        role.Has(Permissions.Reports.ReprintValidated).Should().BeTrue();

        // An administrator who deliberately gives it back afterwards keeps it.
        role.Grant(Permissions.Reports.Print);
        role.ReplaceShippedPermissionOnce(Permissions.Reports.Print, Permissions.Reports.ReprintValidated)
            .Should().BeFalse();
        role.Has(Permissions.Reports.Print).Should().BeTrue();
    }

    [Fact]
    public void A_role_seeded_before_tracking_also_loses_report_release_once()
    {
        var role = new Role(SystemRoles.Prescriber, new MultilingualText("P", "P", "P"), isBuiltIn: true);
        role.Grant(Permissions.Reports.View);
        role.Grant(Permissions.Reports.Print);

        role.ReplaceShippedPermissionOnce(Permissions.Reports.Print, Permissions.Reports.ReprintValidated)
            .Should().BeTrue();
        role.ApplyShippedPermissions(ShippedRole(SystemRoles.Prescriber));

        role.Has(Permissions.Reports.Print).Should().BeFalse();
        role.Has(Permissions.Reports.ReprintValidated).Should().BeTrue();
    }

    [Fact]
    public void A_scoped_session_filters_patients_and_orders_to_its_prescriber()
    {
        var prescriberId = Guid.NewGuid();
        using var context = NewContext(SignedIn(scoped: true, prescriberId));

        var patients = context.Patients.ToQueryString();
        var orders = context.LabOrders.ToQueryString();
        var reports = context.LabReports.ToQueryString();

        patients.Should().Contain("PrescriberId").And.ContainEquivalentOf(prescriberId.ToString());
        orders.Should().Contain("PrescriberId");
        reports.Should().Contain("PrescriberId");
    }

    [Fact]
    public void A_scoped_session_linked_to_nobody_matches_no_prescriber()
    {
        using var context = NewContext(SignedIn(scoped: true, prescriberId: null));

        context.PrescriberScoped.Should().BeTrue();
        context.PrescriberScopeId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void A_staff_session_is_not_prescriber_scoped()
    {
        using var context = NewContext(SignedIn(scoped: false, prescriberId: Guid.NewGuid()));

        context.PrescriberScoped.Should().BeFalse();
    }

    [Fact]
    public async Task A_scoped_prescriber_cannot_release_a_report_even_holding_the_permission()
    {
        var guard = Stub<IPermissionGuard>.Create(new()
        {
            [nameof(IPermissionGuard.Require)] = _ => Result.Success()
        });

        var orderLoads = 0;
        var orders = Stub<IRepository<MarocLIS.Domain.Orders.LabOrder>>.Create(new()
        {
            [nameof(IRepository<MarocLIS.Domain.Orders.LabOrder>.GetByIdWithDetailsAsync)] = _ =>
            {
                orderLoads++;
                return Task.FromResult<MarocLIS.Domain.Orders.LabOrder?>(null);
            }
        });

        var service = new ReportReleaseService(
            orders,
            Stub<IRepository<MarocLIS.Domain.Patients.Patient>>.Create(),
            Stub<IRepository<MarocLIS.Domain.Organization.LabSite>>.Create(),
            Stub<IRepository<MarocLIS.Domain.Catalog.AnalysisTest>>.Create(),
            Stub<IRepository<MarocLIS.Domain.Samples.Sample>>.Create(),
            Stub<IRepository<AppUser>>.Create(),
            Stub<IRepository<MarocLIS.Domain.Reports.LabReport>>.Create(),
            Stub<MarocLIS.Application.Results.IResultQueries>.Create(),
            Stub<IReportQueries>.Create(),
            Stub<IReportRenderer>.Create(),
            Stub<IFontAvailability>.Create(),
            Stub<IUnitOfWork>.Create(),
            guard,
            SignedIn(scoped: true, Guid.NewGuid()),
            Stub<IDataAccessAuditor>.Create(),
            Stub<IClock>.Create(),
            Stub<IMessageSender>.Create());

        var result = await service.ReleaseAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Message.PermissionDenied");
        orderLoads.Should().Be(0);
    }

    [Fact]
    public async Task Creating_a_user_links_the_chosen_prescriber()
    {
        var fixture = new AdminFixture(Permissions.Administration.ManageUsers);
        var prescriberId = Guid.NewGuid();

        var result = await fixture.Service.CreateUserAsync(new CreateUserRequest
        {
            UserName = "drx",
            FullName = "Dr X",
            InitialPassword = "Motdepasse#2026",
            PrescriberId = prescriberId
        });

        result.IsSuccess.Should().BeTrue();
        fixture.Users[result.Value].PrescriberId.Should().Be(prescriberId);
    }

    // ---- S5: one's own competence record ----------------------------------------------------------

    [Fact]
    public async Task An_administrator_cannot_make_themselves_a_biologist()
    {
        var fixture = new AdminFixture(Permissions.Administration.ManageUsers);
        var self = new AppUser("admin", "Admin", "hash");
        fixture.Users[self.Id] = self;
        fixture.CallerId = self.Id;

        var result = await fixture.Service.UpdateUserAsync(new UpdateUserRequest
        {
            UserId = self.Id,
            FullName = "Admin",
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "12345"
        });

        result.FirstError.ResourceKey.Should().Be("User_CannotEditOwnQualification");
        self.Qualification.Should().Be(ProfessionalQualification.None);
        fixture.Saves.Should().Be(0);
    }

    [Theory]
    [InlineData("validity")]
    [InlineData("departments")]
    [InlineData("prescriber")]
    public async Task Other_competence_fields_of_ones_own_account_are_refused_too(string field)
    {
        var fixture = new AdminFixture(Permissions.Administration.ManageUsers);
        var self = new AppUser("bio", "Bio", "hash")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "12345",
            AuthorizedDepartmentCodesCsv = "BIOCH"
        };
        fixture.Users[self.Id] = self;
        fixture.CallerId = self.Id;

        var request = new UpdateUserRequest
        {
            UserId = self.Id,
            FullName = "Bio",
            Qualification = self.Qualification,
            ProfessionalOrderNumber = self.ProfessionalOrderNumber,
            AuthorizedDepartmentCodes = ["BIOCH"]
        };

        request = field switch
        {
            "validity" => request with { QualificationValidUntil = DateOnly.FromDateTime(DateTime.Today.AddYears(5)) },
            "departments" => request with { AuthorizedDepartmentCodes = [] },
            _ => request with { PrescriberId = Guid.NewGuid() }
        };

        var result = await fixture.Service.UpdateUserAsync(request);

        result.FirstError.ResourceKey.Should().Be("User_CannotEditOwnQualification");
    }

    [Fact]
    public async Task Ones_own_contact_details_can_still_be_edited()
    {
        var fixture = new AdminFixture(Permissions.Administration.ManageUsers);
        var self = new AppUser("bio", "Bio", "hash")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "12345",
            AuthorizedDepartmentCodesCsv = "HEMATO,BIOCH"
        };
        fixture.Users[self.Id] = self;
        fixture.CallerId = self.Id;

        var result = await fixture.Service.UpdateUserAsync(new UpdateUserRequest
        {
            UserId = self.Id,
            FullName = "Bio",
            Phone = "0600000000",
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = " 12345 ",
            AuthorizedDepartmentCodes = ["bioch", "HEMATO"]
        });

        result.IsSuccess.Should().BeTrue();
        self.Phone.Should().Be("0600000000");
    }

    [Fact]
    public async Task Another_users_qualification_can_still_be_set()
    {
        var fixture = new AdminFixture(Permissions.Administration.ManageUsers);
        var other = new AppUser("bio", "Bio", "hash");
        fixture.Users[other.Id] = other;

        var result = await fixture.Service.UpdateUserAsync(new UpdateUserRequest
        {
            UserId = other.Id,
            FullName = "Bio",
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "12345"
        });

        result.IsSuccess.Should().BeTrue();
        other.Qualification.Should().Be(ProfessionalQualification.MedicalBiologist);
    }

    // ---- S6: open sessions ------------------------------------------------------------------------

    [Fact]
    public void Deactivation_password_reset_and_role_changes_rotate_the_stamp()
    {
        var user = new AppUser("tech", "Tech", "hash");

        var stamp = user.SecurityStamp;
        user.Deactivate();
        user.SecurityStamp.Should().NotBe(stamp);

        user.Activate();
        stamp = user.SecurityStamp;
        user.SetPassword("hash2", DateTimeOffset.UtcNow, mustChangeAtNextLogon: true);
        user.SecurityStamp.Should().NotBe(stamp);

        stamp = user.SecurityStamp;
        user.AssignRole(Guid.NewGuid());
        user.SecurityStamp.Should().NotBe(stamp);
    }

    [Fact]
    public void A_session_matching_the_account_stays_valid()
    {
        var site = Guid.NewGuid();
        var session = Session([Permissions.Patients.View], [site]);

        SessionValidator.Matches(session, "stamp", true, "stamp", null, [Permissions.Patients.View], [site])
            .Should().BeTrue();
    }

    [Fact]
    public void A_session_of_a_deactivated_account_is_ended()
    {
        var session = Session([], []);

        SessionValidator.Matches(session, "stamp", false, "stamp", null, [], []).Should().BeFalse();
    }

    [Fact]
    public void A_session_whose_stamp_moved_is_ended()
    {
        var session = Session([], []);

        SessionValidator.Matches(session, "stamp", true, "other", null, [], []).Should().BeFalse();
    }

    [Fact]
    public void A_session_whose_role_permissions_changed_is_ended()
    {
        var session = Session([Permissions.Patients.View, Permissions.Results.Enter], []);

        SessionValidator.Matches(session, "stamp", true, "stamp", null, [Permissions.Patients.View], [])
            .Should().BeFalse();
    }

    // ---- S9: pay data in the audit trail ----------------------------------------------------------

    [Fact]
    public void Salary_and_bank_details_are_recorded_as_changed_without_their_values()
    {
        using var context = NewContext(SignedIn(scoped: false, prescriberId: null));

        var employee = new Employee("E001", "Alaoui", "Sara", EmployeeJob.Technician,
                                    new DateOnly(2020, 1, 1), Guid.NewGuid());
        context.Attach(employee);

        employee.BaseSalary = 8_512.75m;
        employee.Rib = "011780000012345678901234";
        employee.BankName = "Banque Populaire";

        var entry = RunInterceptor(context);

        entry.ChangedProperties.Should().Contain("BaseSalary").And.Contain("Rib").And.Contain("BankName");
        entry.Changes.Should().NotContain("8512").And.NotContain("0117800000").And.NotContain("Populaire");
        entry.Changes.Should().Contain("redacted");
    }

    [Fact]
    public void A_new_employee_record_does_not_carry_the_salary_into_the_audit_trail()
    {
        using var context = NewContext(SignedIn(scoped: false, prescriberId: null));

        var employee = new Employee("E002", "Bennani", "Omar", EmployeeJob.Technician,
                                    new DateOnly(2021, 1, 1), Guid.NewGuid())
        {
            BaseSalary = 9_999m,
            Rib = "011780000012345678901234"
        };
        context.Add(employee);

        var entry = RunInterceptor(context);

        entry.Changes.Should().NotContain("9999").And.NotContain("0117800000");
        entry.Changes.Should().Contain("Bennani");
    }

    [Fact]
    public void Rows_written_before_redaction_are_masked_when_read()
    {
        const string stored = """{"BaseSalary":{"old":7000,"new":8500},"Phone":{"old":"1","new":"2"}}""";

        var masked = AuditRedaction.Mask(nameof(Employee), stored);

        masked.Should().NotContain("8500").And.NotContain("7000").And.Contain("Phone");
        AuditRedaction.Mask("Patient", stored).Should().Be(stored);
    }

    // ---- S10: sign-out record ---------------------------------------------------------------------

    [Fact]
    public async Task The_idle_sign_out_is_recorded_as_a_timeout_and_clears_the_session()
    {
        var recorded = new List<SecurityEventKind>();
        var auditor = Stub<ISecurityAuditor>.Create(new()
        {
            [nameof(ISecurityAuditor.LogAsync)] = a => { recorded.Add((SecurityEventKind)a[0]!); return Task.CompletedTask; }
        });

        var accessor = new CurrentUserAccessor();
        typeof(CurrentUserAccessor).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(accessor, [new AuthenticatedSession(Guid.NewGuid(), "tech", "Tech", Guid.NewGuid(), "S1",
                AppLanguage.French, ProfessionalQualification.None, new HashSet<string>(), [],
                new HashSet<string>(), false)]);

        using var context = NewContext(accessor);
        var service = new AuthenticationService(context, new BCryptPasswordHasher(), new PasswordPolicy(),
                                                accessor, auditor, new SystemClock());

        await service.SignOutAsync(SecurityEventKind.SessionTimeout);

        recorded.Should().Equal(SecurityEventKind.SessionTimeout);
        accessor.IsAuthenticated.Should().BeFalse();
    }

    // ---- S11: rehash ------------------------------------------------------------------------------

    [Fact]
    public void Upgrading_the_hash_keeps_the_password_age_and_the_session_stamp()
    {
        var user = new AppUser("tech", "Tech", "old");
        var changedAt = DateTimeOffset.UtcNow.AddDays(-400);
        user.SetPassword("old", changedAt);
        var stamp = user.SecurityStamp;

        user.ReplacePasswordHash("new");

        user.PasswordHash.Should().Be("new");
        user.PasswordChangedAt.Should().Be(changedAt);
        user.SecurityStamp.Should().Be(stamp);
    }

    // ---- S12: locked accounts ---------------------------------------------------------------------

    [Fact]
    public void A_locked_account_says_so_only_to_the_holder_of_the_password()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(10);

        AuthenticationService.LockedOutAnswer(passwordCorrect: false, until).ResourceKey
            .Should().Be("Auth_InvalidCredentials");
        AuthenticationService.LockedOutAnswer(passwordCorrect: true, until).ResourceKey
            .Should().Be("Auth_AccountLockedOut");
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static LisDbContext NewContext(ICurrentUser user) =>
        new(new DbContextOptionsBuilder<LisDbContext>()
                .UseSqlServer("Server=.;Database=MarocLIS;Integrated Security=true")
                .Options,
            user);

    private static ICurrentUser SignedIn(bool scoped, Guid? prescriberId) =>
        Stub<ICurrentUser>.Create(new()
        {
            ["get_" + nameof(ICurrentUser.IsAuthenticated)] = _ => true,
            ["get_" + nameof(ICurrentUser.IsPrescriberScoped)] = _ => scoped,
            ["get_" + nameof(ICurrentUser.PrescriberId)] = _ => prescriberId,
            ["get_" + nameof(ICurrentUser.UserId)] = _ => Guid.NewGuid(),
            ["get_" + nameof(ICurrentUser.AccessibleSiteIds)] = _ => (IReadOnlyList<Guid>)[Guid.NewGuid()]
        });

    private static ICurrentUser Session(IEnumerable<string> permissions, IEnumerable<Guid> sites)
    {
        var granted = permissions.ToHashSet(StringComparer.Ordinal);
        var siteList = sites.ToList();

        return Stub<ICurrentUser>.Create(new()
        {
            ["get_" + nameof(ICurrentUser.IsAuthenticated)] = _ => true,
            ["get_" + nameof(ICurrentUser.Permissions)] = _ => (IReadOnlySet<string>)granted,
            ["get_" + nameof(ICurrentUser.AccessibleSiteIds)] = _ => (IReadOnlyList<Guid>)siteList,
            ["get_" + nameof(ICurrentUser.PrescriberId)] = _ => null
        });
    }

    /// <summary>Runs the audit interceptor over the context's pending changes, without saving.</summary>
    private static AuditEntry RunInterceptor(LisDbContext context)
    {
        var interceptor = new AuditSaveChangesInterceptor(null, new SystemClock());

        typeof(AuditSaveChangesInterceptor)
            .GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(interceptor, [context]);

        return context.ChangeTracker.Entries<AuditEntry>().Single().Entity;
    }

    private static string[] ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly
            .GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;

        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        return [];
    }

    /// <summary>The user administration service over in-memory stand-ins.</summary>
    private sealed class AdminFixture
    {
        public Dictionary<Guid, AppUser> Users { get; } = [];
        public Guid CallerId { get; set; } = Guid.NewGuid();
        public int Saves { get; private set; }

        public UserAdministrationService Service { get; }

        public AdminFixture(params string[] callerPermissions)
        {
            var granted = callerPermissions.ToHashSet(StringComparer.Ordinal);

            var guard = Stub<IPermissionGuard>.Create(new()
            {
                [nameof(IPermissionGuard.Require)] = a => granted.Contains((string)a[0]!)
                    ? Result.Success()
                    : Result.Failure(Error.Forbidden("Forbidden"))
            });

            var currentUser = Stub<ICurrentUser>.Create(new()
            {
                [nameof(ICurrentUser.Has)] = a => granted.Contains((string)a[0]!),
                ["get_" + nameof(ICurrentUser.UserId)] = _ => CallerId,
                ["get_" + nameof(ICurrentUser.SiteId)] = _ => Guid.NewGuid()
            });

            var users = Stub<IRepository<AppUser>>.Create(new()
            {
                [nameof(IRepository<AppUser>.GetByIdAsync)] = a => Task.FromResult(Users.GetValueOrDefault((Guid)a[0]!)),
                [nameof(IRepository<AppUser>.GetByIdWithDetailsAsync)] = a => Task.FromResult(Users.GetValueOrDefault((Guid)a[0]!)),
                [nameof(IRepository<AppUser>.AddAsync)] = a => { var u = (AppUser)a[0]!; Users[u.Id] = u; return Task.CompletedTask; }
            });

            var queries = Stub<IAdministrationQueries>.Create(new()
            {
                [nameof(IAdministrationQueries.UserNameExistsAsync)] = _ => Task.FromResult(false),
                [nameof(IAdministrationQueries.PrescriberExistsAsync)] = _ => Task.FromResult(true),
                [nameof(IAdministrationQueries.GetPermissionsForRolesAsync)] = _ =>
                    Task.FromResult<IReadOnlySet<string>>(new HashSet<string>())
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
                users, Stub<IRepository<Role>>.Create(), queries, Stub<IAuthenticationService>.Create(),
                unitOfWork, guard, currentUser, Stub<ISecurityAuditor>.Create(), hasher,
                new PasswordPolicy(), clock);
        }
    }
}
