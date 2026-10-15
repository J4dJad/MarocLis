using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A test that needs SQL Server: the lock hints of the sequence allocator and the site filters of
/// the payroll queries cannot be exercised without one. Skipped unless
/// <c>MAROCLIS_TEST_SQLSERVER</c> holds a connection string; its database is created by the test
/// and dropped afterwards, and an existing database is refused rather than reused.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string Variable = "MAROCLIS_TEST_SQLSERVER";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not set: no scratch SQL Server database to run against.";
    }
}

/// <summary>Creates the scratch database once for the class, and drops it at the end.</summary>
public sealed class ScratchDatabase : IDisposable
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable) ?? "";

    private readonly bool _created;

    public ScratchDatabase()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        using var context = Context();
        if (context.Database.CanConnect())
            throw new InvalidOperationException(
                $"Database {new SqlConnectionStringBuilder(ConnectionString).InitialCatalog} already exists; "
                + "the tests create and drop their own and will not touch an existing one.");

        context.Database.EnsureCreated();
        _created = true;
    }

    public LisDbContext Context(ICurrentUser? user = null) =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options, user);

    public void Dispose()
    {
        if (!_created) return;
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

public sealed class ReviewFixesInfraDatabaseTests(ScratchDatabase database) : IClassFixture<ScratchDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 15);
    }

    private sealed class Operator(Guid siteId, params Guid[] accessible) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "rh";
        public string FullName => "RH";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => accessible;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "TEST";
    }

    private static async Task<string> AllocateAsync(LisDbContext context, int year)
    {
        var unitOfWork = new UnitOfWork(context);
        await using var transaction = await unitOfWork.BeginTransactionAsync();
        var number = await new SequenceAllocator(context, new FixedClock(), null)
            .NextAsync(SequencePurpose.NonConformity, siteId: null, year);
        await unitOfWork.CommitAsync();
        return number.Value;
    }

    // ---- I2 --------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Allocator_OnALongLivedContext_SeesNumbersTakenByAnotherWorkstation()
    {
        await using var screen = database.Context();
        await using var other = database.Context();

        (await AllocateAsync(screen, 2101)).Should().Be("NC-2101-0001");
        (await AllocateAsync(other, 2101)).Should().Be("NC-2101-0002");

        // The screen's context still tracks the counter as it left it, at 2.
        (await AllocateAsync(screen, 2101)).Should().Be("NC-2101-0003");
    }

    [SqlServerFact]
    public async Task Allocator_AfterARollback_GivesTheReturnedNumberAgain()
    {
        await using var screen = database.Context();
        (await AllocateAsync(screen, 2102)).Should().Be("NC-2102-0001");

        var unitOfWork = new UnitOfWork(screen);
        await using (await unitOfWork.BeginTransactionAsync())
        {
            var taken = await new SequenceAllocator(screen, new FixedClock(), null)
                .NextAsync(SequencePurpose.NonConformity, siteId: null, 2102);
            taken.Value.Should().Be("NC-2102-0002");
            await unitOfWork.RollbackAsync();
        }

        (await AllocateAsync(screen, 2102)).Should().Be("NC-2102-0002", "the series is gapless");
    }

    // ---- R1 --------------------------------------------------------------------------------------

    private sealed record TwoSites(Guid A, Guid B, Guid EmployeeA, Guid EmployeeB, Guid PeriodB);

    private async Task<TwoSites> SeedTwoSitesAsync(int year, int month)
    {
        await using var context = database.Context();
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var a = new LabSite("A" + suffix, new MultilingualText("Site A"), LabSiteKind.MainLaboratory);
        var b = new LabSite("B" + suffix, new MultilingualText("Site B"), LabSiteKind.SatelliteLaboratory);
        context.Sites.AddRange(a, b);

        var employeeA = new Employee("EA" + suffix, "ALAMI", "Amine", EmployeeJob.Technician, new DateOnly(2020, 1, 1), a.Id)
            { BaseSalary = 6000m };
        var employeeB = new Employee("EB" + suffix, "BENANI", "Badr", EmployeeJob.Technician, new DateOnly(2020, 1, 1), b.Id)
            { BaseSalary = 6000m };
        context.Employees.AddRange(employeeA, employeeB);

        // Site B has already run its month; site A has opened its own, still empty.
        var periodB = new PayrollPeriod(b.Id, year, month);
        periodB.Add(new Payslip(employeeB.Id) { Number = employeeB.Number, FullName = "BENANI Badr" });
        context.PayrollPeriods.AddRange(periodB, new PayrollPeriod(a.Id, year, month + 1));

        await context.SaveChangesAsync();
        return new TwoSites(a.Id, b.Id, employeeA.Id, employeeB.Id, periodB.Id);
    }

    [SqlServerFact]
    public async Task Payroll_OfAMultiSiteOperator_TakesOnlyTheSiteWorkedOn()
    {
        var seed = await SeedTwoSitesAsync(2026, 3);
        var operatorOnA = new Operator(seed.A, seed.A, seed.B);
        await using var scoped = database.Context(operatorOnA);
        var queries = new PayrollQueries(scoped);

        var candidates = await queries.CandidatesAsync(seed.A, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 26m);
        candidates.Select(c => c.EmployeeId).Should().Contain(seed.EmployeeA).And.NotContain(seed.EmployeeB);

        (await queries.FindPeriodAsync(seed.A, 2026, 3)).Should().BeNull("site B's month is not site A's");
        (await queries.FindPeriodAsync(seed.B, 2026, 3)).Should().Be(seed.PeriodB);
        (await queries.GetPeriodsAsync(seed.A)).Should().OnlyContain(p => p.Year == 2026 && p.Month == 4);
    }

    [SqlServerFact]
    public async Task PayslipOfMonth_IsTakenFromTheEmployeesOwnSite()
    {
        var seed = await SeedTwoSitesAsync(2026, 5);
        await using var context = database.Context(new Operator(seed.A, seed.A, seed.B));

        // Site A opens May too: the lookup must still find B's payslip for B's employee.
        context.PayrollPeriods.Add(new PayrollPeriod(seed.A, 2026, 5));
        await context.SaveChangesAsync();

        var queries = new HrDocumentQueries(context);

        var ofB = await queries.PayslipOfMonthAsync(seed.EmployeeB, 2026, 5);
        ofB.Should().NotBeNull();
        ofB!.Value.PeriodId.Should().Be(seed.PeriodB);
        ofB.Value.Payslip.Should().NotBeNull();

        var ofA = await queries.PayslipOfMonthAsync(seed.EmployeeA, 2026, 5);
        ofA.Should().NotBeNull();
        ofA!.Value.PeriodId.Should().NotBe(seed.PeriodB);
        ofA.Value.Payslip.Should().BeNull();
    }
}
