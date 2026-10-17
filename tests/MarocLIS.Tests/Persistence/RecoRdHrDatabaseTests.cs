using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.HumanResources;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// The owner's decision on the public holiday entered after the leaves around it (lot rd), against
/// SQL Server and the services as the desktop composes them: the leaves it falls in are recounted,
/// an approved annual leave gets the day back on its balance, and the leaves are named.
/// </summary>
public sealed class RecoRdHrDatabaseTests(Cycle1HrScratchDatabase database) : IClassFixture<Cycle1HrScratchDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
        public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(10, 0)));
    }

    private sealed class Operator(Guid userId, Guid siteId) : ICurrentUser
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
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(MarocLIS.Domain.Identity.Permissions.All);
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "RH-1";
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Rib()
    {
        var first22 = string.Concat(Enumerable.Range(0, 22).Select(_ => Random.Shared.Next(10)));
        var remainder = 0;
        foreach (var c in first22 + "00") remainder = (remainder * 10 + (c - '0')) % 97;
        return first22 + ((97 - remainder) % 97).ToString("00");
    }

    private async Task<(ServiceProvider Services, IClock Clock)> LabAsync()
    {
        var user = new AppUser(Unique("rh"), "Responsable RH", "x");
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire RH"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE RH SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Casablanca",
            HealthMinistryAuthorizationNumber = "AUT-RH-1",
            ResponsibleBiologistUserId = user.Id
        };

        await using (var context = database.Context())
        {
            context.Users.Add(user);
            context.Sites.Add(site);
            await context.SaveChangesAsync();
        }

        var clock = new FixedClock();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(user.Id, site.Id));
        services.AddSingleton<IClock>(clock);
        services.AddIsolatedQueries<IPersonnelQueries, PersonnelQueries>();
        services.AddIsolatedQueries<ILeaveQueries, LeaveQueries>();
        services.AddIsolatedQueries<IHrDocumentQueries, HrDocumentQueries>();
        return (services.BuildServiceProvider(), clock);
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

    private static async Task<Guid> HireAsync(ServiceProvider services, string lastName) =>
        Ok(await On<PersonnelService, Result<Guid>>(services, s => s.SaveEmployeeAsync(new EmployeeRequest
        {
            LastName = lastName, FirstName = "Salma", Gender = Gender.Female, BirthDate = new DateOnly(1990, 5, 4),
            Cin = Unique("BK"), CnssNumber = "123456789", FamilyStatus = FamilyStatus.Married, Job = EmployeeJob.Technician,
            JobTitle = "Technicienne", ConfidentialityClauseSignedOn = new DateOnly(2024, 3, 1),
            ContractType = ContractType.Permanent, HireDate = new DateOnly(2024, 3, 1), BaseSalary = 6_000m, Rib = Rib(),
            BankName = "Banque Test"
        })));

    private static async Task<decimal> BalanceAsync(ServiceProvider services, Guid employeeId) =>
        Ok(await On<LeaveService, Result<IReadOnlyList<LeaveBalanceRow>>>(services, s => s.GetBalancesAsync()))
            .Single(b => b.EmployeeId == employeeId).Balance;

    /// <summary>A Monday-to-Saturday week ahead with no fixed civil holiday in it.</summary>
    private static DateOnly FreeWeek(DateOnly today)
    {
        var monday = today.AddDays(30);
        while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(1);
        while (Enumerable.Range(0, 6).Any(d => PublicHoliday.FixedHolidays.Any(h => h.Month == monday.AddDays(d).Month
                                                                                 && h.Day == monday.AddDays(d).Day)))
            monday = monday.AddDays(7);
        return monday;
    }

    [SqlServerFact]
    public async Task AHolidayEnteredAfterTheLeaves_RecountsThemAndGivesTheDayBack()
    {
        var (services, clock) = await LabAsync();
        var approvedFor = await HireAsync(services, "Alaoui");
        var pendingFor = await HireAsync(services, "Bennani");
        foreach (var employee in new[] { approvedFor, pendingFor })
            Ok(await On<LeaveService, Result>(services, s => s.AdjustAsync(employee, 18m, "Reliquat à la reprise du dossier")));

        var monday = FreeWeek(clock.Today);
        var approved = Ok(await On<LeaveService, Result<Guid>>(services, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = approvedFor, Type = LeaveType.Annual, StartDate = monday, EndDate = monday.AddDays(5)
        })));
        Ok(await On<LeaveService, Result>(services, s => s.ApproveAsync(approved, null)));
        var pending = Ok(await On<LeaveService, Result<Guid>>(services, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = pendingFor, Type = LeaveType.Annual, StartDate = monday, EndDate = monday.AddDays(5)
        })));
        (await BalanceAsync(services, approvedFor)).Should().Be(12m);

        // The holiday is announced on the Wednesday of that week.
        var holiday = monday.AddDays(2);
        var recounted = Ok(await On<LeaveService, Result<IReadOnlyList<HolidayRecount>>>(services,
            s => s.AddHolidayAsync(holiday, "Aïd al-Fitr", religious: true)));

        recounted.Should().HaveCount(2);
        recounted.Should().ContainSingle(r => r.LeaveRequestId == approved && r.Status == LeaveStatus.Approved
                                              && r.DaysBefore == 6m && r.DaysAfter == 5m && r.EmployeeName.StartsWith("ALAOUI"));
        recounted.Should().ContainSingle(r => r.LeaveRequestId == pending && r.Status == LeaveStatus.Pending && r.DaysAfter == 5m);

        // The approved leave's day is back on the balance; the pending one had debited nothing.
        (await BalanceAsync(services, approvedFor)).Should().Be(13m);
        (await BalanceAsync(services, pendingFor)).Should().Be(18m);

        await using (var context = database.Context())
        {
            (await context.Set<LeaveRequest>().AsNoTracking().Where(r => r.Id == approved || r.Id == pending)
                                              .Select(r => r.Days).ToListAsync())
                .Should().AllBeEquivalentTo(5m);
            var returned = await context.Set<LeaveLedgerEntry>().AsNoTracking()
                .SingleAsync(l => l.LeaveRequestId == approved && l.Kind == LeaveLedgerKind.Returned);
            returned.Days.Should().Be(1m);
            returned.Reference.Should().Be($"FERIE-{holiday:yyyyMMdd}");
        }

        // Approving the pending leave now debits the five days it counts.
        Ok(await On<LeaveService, Result>(services, s => s.ApproveAsync(pending, null)));
        (await BalanceAsync(services, pendingFor)).Should().Be(13m);

        // A holiday in no leave recounts nothing; the same day twice is still refused.
        Ok(await On<LeaveService, Result<IReadOnlyList<HolidayRecount>>>(services,
            s => s.AddHolidayAsync(monday.AddDays(14), "Fête de test", religious: true))).Should().BeEmpty();
        (await On<LeaveService, Result<IReadOnlyList<HolidayRecount>>>(services,
            s => s.AddHolidayAsync(holiday, "Aïd al-Fitr", religious: true)))
            .Errors.Should().ContainSingle(e => e.ResourceKey == "Holiday_AlreadyExists");
    }

    [SqlServerFact]
    public async Task AHolidayAlreadyPast_LeavesTheLeavesAsTheyWere()
    {
        var (services, clock) = await LabAsync();
        var employee = await HireAsync(services, "Chraibi");
        Ok(await On<LeaveService, Result>(services, s => s.AdjustAsync(employee, 18m, "Reliquat à la reprise du dossier")));

        // A leave in progress, the holiday entered on one of its days already gone: that day was taken.
        var past = clock.Today.AddDays(-1);
        while (past.DayOfWeek == DayOfWeek.Sunday) past = past.AddDays(-1);
        var leave = Ok(await On<LeaveService, Result<Guid>>(services, s => s.SubmitAsync(new LeaveSubmission
        {
            EmployeeId = employee, Type = LeaveType.Annual, StartDate = past.AddDays(-1), EndDate = clock.Today.AddDays(2)
        })));
        Ok(await On<LeaveService, Result>(services, s => s.ApproveAsync(leave, null)));
        var balance = await BalanceAsync(services, employee);

        Ok(await On<LeaveService, Result<IReadOnlyList<HolidayRecount>>>(services,
            s => s.AddHolidayAsync(past, "Fête passée", religious: true))).Should().BeEmpty();
        (await BalanceAsync(services, employee)).Should().Be(balance);
    }
}
