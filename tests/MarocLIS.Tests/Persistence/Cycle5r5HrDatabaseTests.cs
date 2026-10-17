using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// The owner's decisions on HR (cycle 5, lot r5), against SQL Server and the services as the
/// desktop composes them: the LIS account of an employee who leaves is deactivated as of the exit
/// date (at once when the date is reached, at the sign-in on the day otherwise, never the last
/// administrator), with the audit trail and the security log; and « Justificatif reçu » ticks the
/// certificate of a waiting sick leave so it can be approved.
/// </summary>
public sealed class Cycle5r5HrDatabaseTests(Cycle1HrScratchDatabase database) : IClassFixture<Cycle1HrScratchDatabase>
{
    private sealed class MovableClock : IClock
    {
        public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Today);
        public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(10, 0)));
    }

    private sealed class Operator(Guid userId, Guid siteId, IReadOnlySet<string> permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId;
        public string UserName => "rh";
        public string FullName => "Responsable RH";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = permissions;
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "RH-R5";
    }

    private const string Password = "Sortie-R5-2026!";
    private static readonly IReadOnlySet<string> Everything = new HashSet<string>(Permissions.All);

    private sealed record Lab(Guid SiteId, Guid OperatorId, MovableClock Clock);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Rib()
    {
        var first22 = string.Concat(Enumerable.Range(0, 22).Select(_ => Random.Shared.Next(10)));
        var remainder = 0;
        foreach (var c in first22 + "00") remainder = (remainder * 10 + (c - '0')) % 97;
        return first22 + ((97 - remainder) % 97).ToString("00");
    }

    private async Task<Lab> LabAsync()
    {
        var user = new AppUser(Unique("rh"), "Responsable RH", "x");
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire RH"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE R5 SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Casablanca",
            HealthMinistryAuthorizationNumber = "AUT-R5-1",
            ResponsibleBiologistUserId = user.Id
        };

        await using var context = database.Context();
        context.Users.Add(user);
        context.Sites.Add(site);
        await context.SaveChangesAsync();
        return new Lab(site.Id, user.Id, new MovableClock());
    }

    private ServiceProvider Services(Lab lab, IReadOnlySet<string>? permissions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(lab.OperatorId, lab.SiteId, permissions ?? Everything));
        services.AddSingleton<IClock>(lab.Clock);
        services.AddIsolatedQueries<IPersonnelQueries, PersonnelQueries>();
        services.AddIsolatedQueries<ILeaveQueries, LeaveQueries>();
        services.AddIsolatedQueries<IHrDocumentQueries, HrDocumentQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider services, Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.Should().BeTrue(string.Join(", ", result.Errors.Select(e => e.ResourceKey)));
        return result.Value;
    }

    private static void Ok(Result result) =>
        result.IsSuccess.Should().BeTrue(string.Join(", ", result.Errors.Select(e => e.ResourceKey)));

    /// <summary>A signed-in-able account on the site, holding <paramref name="roleId"/> when given.</summary>
    private async Task<AppUser> AccountAsync(Lab lab, Guid? roleId = null)
    {
        var user = new AppUser(Unique("tech"), "Technicienne", new BCryptPasswordHasher().Hash(Password));
        user.SetPassword(user.PasswordHash, lab.Clock.Now, false);
        user.GrantSiteAccess(lab.SiteId, isDefault: true);
        if (roleId is { } role) user.AssignRole(role);

        await using var context = database.Context();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<Guid> HireAsync(ServiceProvider services, Guid? userId, string lastName = "Alaoui") =>
        Ok(await On<PersonnelService, Result<Guid>>(services, s => s.SaveEmployeeAsync(new EmployeeRequest
        {
            LastName = lastName, FirstName = "Salma", Gender = Gender.Female, BirthDate = new DateOnly(1990, 5, 4),
            Cin = Unique("BK"), CnssNumber = "123456789", FamilyStatus = FamilyStatus.Married, Job = EmployeeJob.Technician,
            JobTitle = "Technicienne", ConfidentialityClauseSignedOn = new DateOnly(2024, 3, 1),
            ContractType = ContractType.Permanent, HireDate = new DateOnly(2024, 3, 1), BaseSalary = 6_000m, Rib = Rib(),
            BankName = "Banque Test", UserId = userId
        })));

    private async Task<AppUser> ReadUserAsync(Guid userId)
    {
        await using var context = database.Context();
        return await context.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private async Task<List<SecurityEvent>> DeactivationEventsAsync(Guid userId)
    {
        await using var context = database.Context();
        return await context.SecurityEvents.AsNoTracking()
            .Where(e => e.TargetUserId == userId && e.Kind == SecurityEventKind.UserDeactivated)
            .ToListAsync();
    }

    [SqlServerFact]
    public async Task AnExitDatedTodayOrBefore_DeactivatesTheLinkedAccountAtOnce_WithItsReason()
    {
        var lab = await LabAsync();
        using var services = Services(lab);
        var account = await AccountAsync(lab);
        var employeeId = await HireAsync(services, account.Id);

        var outcome = Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(services,
            s => s.RecordExitAsync(employeeId, lab.Clock.Today, ExitReason.Resignation, "Démission")));

        outcome.Should().Be(new DepartedAccountOutcome(account.UserName, DepartedAccountState.Deactivated, lab.Clock.Today));
        (await ReadUserAsync(account.Id)).IsActive.Should().BeFalse();

        var logged = (await DeactivationEventsAsync(account.Id)).Should().ContainSingle().Subject;
        logged.Succeeded.Should().BeTrue();
        logged.Details.Should().Contain("sortie de").And.Contain(lab.Clock.Today.ToString("dd/MM/yyyy"));

        await using (var context = database.Context())
            (await context.AuditEntries.AsNoTracking()
                    .Where(a => a.EntityId == account.Id && a.EntityName == nameof(AppUser))
                    .Select(a => a.Reason).ToListAsync())
                .Should().Contain(r => r != null && r.StartsWith($"Désactivation du compte {account.UserName} : sortie de"));

        // A file with no account, and a past exit: nothing to say about an account.
        var unlinked = await HireAsync(services, null, "Bennani");
        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(services,
            s => s.RecordExitAsync(unlinked, lab.Clock.Today.AddDays(-3), ExitReason.Resignation, null))).Should().BeNull();
    }

    [SqlServerFact]
    public async Task AnExitRecordedAhead_KeepsTheAccountUntilTheDay_ThenTheSignInRefusesAndDeactivatesIt()
    {
        var lab = await LabAsync();
        using var services = Services(lab);
        var account = await AccountAsync(lab);
        var employeeId = await HireAsync(services, account.Id);
        var exit = lab.Clock.Today.AddDays(10);

        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(services,
                s => s.RecordExitAsync(employeeId, exit, ExitReason.Resignation, "Préavis")))
            .Should().Be(new DepartedAccountOutcome(account.UserName, DepartedAccountState.Scheduled, exit));
        (await ReadUserAsync(account.Id)).IsActive.Should().BeTrue();

        // Before the day the employee still signs in: the notice is worked.
        (await SignInAsync(services, lab, account.UserName)).IsSuccess.Should().BeTrue();

        // On the exit date the sign-in deactivates the account and refuses it.
        lab.Clock.Today = exit;
        (await SignInAsync(services, lab, account.UserName)).IsFailure.Should().BeTrue();
        (await ReadUserAsync(account.Id)).IsActive.Should().BeFalse();
        (await DeactivationEventsAsync(account.Id)).Should().ContainSingle(e => e.Succeeded && e.Details!.Contains("sortie de"));

        // Done once: the next sweep finds nothing left to switch off.
        (await On<IDepartedAccounts, IReadOnlyList<string>>(services, s => s.DeactivateDueAsync()))
            .Should().NotContain(account.UserName);
    }

    [SqlServerFact]
    public async Task AnExitMovedToToday_DeactivatesNow_AndAWithdrawnExitLeavesTheAccountForTheAdministrator()
    {
        var lab = await LabAsync();
        using var services = Services(lab);
        var account = await AccountAsync(lab);
        var employeeId = await HireAsync(services, account.Id);

        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(services,
            s => s.RecordExitAsync(employeeId, lab.Clock.Today.AddDays(20), ExitReason.Resignation, null)));
        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(services,
                s => s.CorrectExitAsync(employeeId, lab.Clock.Today, ExitReason.Resignation, "Départ avancé")))
            .Should().Be(new DepartedAccountOutcome(account.UserName, DepartedAccountState.Deactivated, lab.Clock.Today));
        (await ReadUserAsync(account.Id)).IsActive.Should().BeFalse();

        // The exit day itself can still be withdrawn; the account is not switched back on by itself.
        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(services,
                s => s.CancelExitAsync(employeeId, "Démission retirée")))
            .Should().Be(new DepartedAccountOutcome(account.UserName, DepartedAccountState.Inactive, null));
        (await ReadUserAsync(account.Id)).IsActive.Should().BeFalse();
    }

    [SqlServerFact]
    public async Task TheLastAdministrator_IsNeverDeactivatedByAnExit_AndTheScreenIsTold()
    {
        var lab = await LabAsync();
        using var services = Services(lab);

        var role = new Role(Unique("ADM"), new MultilingualText("Administration R5"));
        role.Grant(Permissions.Administration.ManageUsers);
        await using (var context = database.Context())
        {
            // The scratch database has no seeded roles: this account is the only one managing users.
            context.Roles.Add(role);
            await context.SaveChangesAsync();
        }

        var administrator = await AccountAsync(lab, role.Id);
        var employeeId = await HireAsync(services, administrator.Id);

        Ok(await On<PersonnelService, Result<DepartedAccountOutcome?>>(services,
                s => s.RecordExitAsync(employeeId, lab.Clock.Today, ExitReason.Resignation, null)))
            .Should().Be(new DepartedAccountOutcome(administrator.UserName, DepartedAccountState.KeptLastAdministrator,
                                                    lab.Clock.Today));
        (await ReadUserAsync(administrator.Id)).IsActive.Should().BeTrue();
        (await DeactivationEventsAsync(administrator.Id)).Should()
            .ContainSingle(e => !e.Succeeded && e.FailureReasonKey == "User_WouldRemoveLastAdministrator");

        // The sign-in sweep leaves it too, without logging it again.
        (await SignInAsync(services, lab, administrator.UserName)).IsSuccess.Should().BeTrue();
        (await DeactivationEventsAsync(administrator.Id)).Should().ContainSingle();
    }

    private static async Task<Result<AuthenticatedSession>> SignInAsync(ServiceProvider services, Lab lab, string userName)
    {
        using var scope = services.CreateScope();
        var auth = new AuthenticationService(scope.ServiceProvider.GetRequiredService<LisDbContext>(),
                                             new BCryptPasswordHasher(), new PasswordPolicy(), new CurrentUserAccessor(),
                                             scope.ServiceProvider.GetRequiredService<ISecurityAuditor>(), lab.Clock);
        return await auth.SignInAsync(userName, Password, lab.SiteId);
    }

    [SqlServerFact]
    public async Task ASickLeaveKeyedWithoutItsCertificate_IsApprovedOnceHrTicksJustificatifRecu()
    {
        var lab = await LabAsync();
        using var hr = Services(lab);
        using var employeeSide = Services(lab, new HashSet<string>
        {
            Permissions.HumanResources.RequestLeave, Permissions.HumanResources.ApproveLeave
        });
        var employeeId = await HireAsync(hr, null);
        var start = lab.Clock.Today.AddDays(3);

        var sick = Ok(await On<LeaveService, Result<Guid>>(hr, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = employeeId, Type = LeaveType.Sick, StartDate = start, EndDate = start.AddDays(2),
            Reason = "Arrêt maladie", JustificationProvided = false
        })));

        (await On<LeaveService, Result>(hr, s => s.ApproveAsync(sick, null)))
            .FirstError.ResourceKey.Should().Be("Leave_JustificationMissing");

        // Ticking the certificate is HR's: the approver without the HR right is refused.
        (await On<LeaveService, Result>(employeeSide, s => s.RecordJustificationReceivedAsync(sick)))
            .FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        Ok(await On<LeaveService, Result>(hr, s => s.RecordJustificationReceivedAsync(sick)));
        (await On<LeaveService, Result>(hr, s => s.RecordJustificationReceivedAsync(sick)))
            .FirstError.ResourceKey.Should().Be("Leave_JustificationAlreadyReceived");

        Ok(await On<LeaveService, Result>(hr, s => s.ApproveAsync(sick, null)));
        (await On<LeaveService, Result>(hr, s => s.RecordJustificationReceivedAsync(sick)))
            .FirstError.ResourceKey.Should().Be("Leave_NotPending");

        await using (var context = database.Context())
        {
            var stored = await context.Set<LeaveRequest>().AsNoTracking().SingleAsync(r => r.Id == sick);
            stored.JustificationProvided.Should().BeTrue();
            stored.Status.Should().Be(LeaveStatus.Approved);
            (await context.AuditEntries.AsNoTracking().Where(a => a.EntityId == sick).Select(a => a.Reason).ToListAsync())
                .Should().Contain(r => r != null && r.StartsWith("Justificatif reçu pour le congé Sick"));
        }

        // A leave type with no certificate to bring has nothing to tick.
        Ok(await On<LeaveService, Result>(hr, s => s.AdjustAsync(employeeId, 10m, "Reliquat")));
        var annualStart = start.AddDays(14);
        var annual = Ok(await On<LeaveService, Result<Guid>>(hr, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = employeeId, Type = LeaveType.Annual, StartDate = annualStart, EndDate = annualStart.AddDays(1)
        })));
        (await On<LeaveService, Result>(hr, s => s.RecordJustificationReceivedAsync(annual)))
            .FirstError.ResourceKey.Should().Be("Leave_JustificationNotRequired");
    }
}
