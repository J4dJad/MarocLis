using System.Text;
using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Assets;
using MarocLIS.Application.HumanResources;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Composition;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
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

/// <summary>A scratch database of its own for lot c2c, created from the model and dropped at the end.</summary>
public sealed class Cycle2cScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle2c_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle2cScratchDatabase()
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
/// Cycle 2, lot c2c, against SQL Server and the services as the desktop composes them: the money
/// boxes' length and the columns behind them, a statement mapped on the wrong column, and a
/// habilitation dated before the hiring.
/// </summary>
public sealed class Cycle2cDatabaseTests(Cycle2cScratchDatabase database) : IClassFixture<Cycle2cScratchDatabase>
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
        public string UserName => "c2c";
        public string FullName => "Dr Cycle Deux";
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
        public string Workstation => "C2C-1";
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Rib()
    {
        var first22 = string.Concat(Enumerable.Range(0, 22).Select(_ => Random.Shared.Next(10)));
        var remainder = 0;
        foreach (var c in first22 + "00") remainder = (remainder * 10 + (c - '0')) % 97;
        return first22 + ((97 - remainder) % 97).ToString("00");
    }

    private async Task<ServiceProvider> LabAsync()
    {
        var user = new AppUser(Unique("c2c"), "Dr Cycle Deux", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-C2C-1"
        };
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire C2C"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE C2C SARL",
            City = "Rabat",
            ResponsibleBiologistUserId = user.Id
        };

        await using (var context = database.Context())
        {
            context.Users.Add(user);
            context.Sites.Add(site);
            await context.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(user.Id, site.Id));
        services.AddSingleton<IClock>(new FixedClock());
        services.AddIsolatedQueries<IPersonnelQueries, PersonnelQueries>();
        services.AddIsolatedQueries<IHrDocumentQueries, HrDocumentQueries>();
        services.AddIsolatedQueries<ITreasuryQueries, TreasuryQueries>();
        services.AddIsolatedQueries<IFixedAssetQueries, FixedAssetQueries>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider lab, Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = lab.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static string Errors(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private static SaveFixedAssetRequest Asset(decimal cost) => new()
    {
        Code = Unique("IMM-"), Label = "Automate d'hématologie", Category = FixedAssetCategory.LaboratoryEquipment,
        AcquisitionDate = new DateOnly(2025, 1, 10), InServiceDate = new DateOnly(2025, 2, 1),
        CostExcludingVatMad = cost, Method = DepreciationMethod.StraightLine, UsefulLifeYears = 5
    };

    /// <summary>
    /// The longest figure a money box takes saves; one figure more than the column holds is what
    /// used to reach the database and come back as « Erreur inattendue », the form lost with it.
    /// </summary>
    [SqlServerFact]
    public async Task TheLongestAmountABoxTakes_Saves_WhereALongerOneFailedOnTheColumn()
    {
        var lab = await LabAsync();

        var longest = EditorValues.ParseDecimal(new string('9', EditorValues.AmountMaxLength))!.Value;
        var saved = await On<FixedAssetService, Result<Guid>>(lab, s => s.SaveAsync(Asset(longest)));
        saved.IsSuccess.Should().BeTrue(Errors(saved));

        var tooLong = EditorValues.ParseDecimal(new string('9', 17))!.Value;
        var refused = async () => await On<FixedAssetService, Result<Guid>>(lab, s => s.SaveAsync(Asset(tooLong)));
        await refused.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// A statement mapped with the amount on the RIB column: the lines are refused as unreadable
    /// in the preview and the import says there is nothing to bring in, instead of failing on the
    /// amount column; mapped right, the same file imports.
    /// </summary>
    [SqlServerFact]
    public async Task AStatementMappedOnTheRibColumn_IsRefusedLineByLine_NotByTheDatabase()
    {
        var lab = await LabAsync();
        var accountId = (await On<TreasuryService, Result<Guid>>(lab, s => s.SaveBankAccountAsync(new BankAccountInput(
            null, "CIH Bank", "Compte courant", Rib(), "51410002", 0m, new DateOnly(2026, 1, 1), true)))).Value;

        var csv = Encoding.UTF8.GetBytes("Date;Libellé;Compte;Montant\n" +
                                         "02/10/2026;VIR CNSS;011780000012345678901234;-3 250,00\n" +
                                         "05/10/2026;REMISE CHEQUES;011780000012345678901234;1 840,50\n");
        var wrong = new BankStatementCsvMapping { Delimiter = ';', DecimalSeparator = ',', DateColumn = 1, LabelColumn = 2, AmountColumn = 3 };

        var preview = await On<TreasuryService, Result<ImportPreview>>(lab, s => s.PreviewImportAsync(accountId, csv, wrong));
        preview.Value.InvalidCount.Should().Be(2);
        var import = await On<TreasuryService, Result<ImportResult>>(lab, s => s.ImportAsync(accountId, csv, "releve.csv", wrong));
        Errors(import).Should().Be("BankStatement_NothingToImport");

        var right = wrong with { AmountColumn = 4 };
        var imported = await On<TreasuryService, Result<ImportResult>>(lab, s => s.ImportAsync(accountId, csv, "releve.csv", right));
        imported.IsSuccess.Should().BeTrue(Errors(imported));
        imported.Value.Imported.Should().Be(2);
    }

    /// <summary>A habilitation cannot be dated before the person was hired; from the hiring day on, it can.</summary>
    [SqlServerFact]
    public async Task AHabilitation_DatedBeforeTheHiring_IsRefused()
    {
        var lab = await LabAsync();
        var hired = DateOnly.FromDateTime(DateTime.Today).AddDays(-30);
        var employeeId = (await On<PersonnelService, Result<Guid>>(lab, s => s.SaveEmployeeAsync(new EmployeeRequest
        {
            LastName = "Tazi", FirstName = "Nadia", Gender = Gender.Female, Cin = Unique("BE"), Job = EmployeeJob.Technician,
            ContractType = ContractType.Permanent, HireDate = hired, WeeklyHours = 44m, BaseSalary = 5_000m
        }))).Value;

        var early = await On<PersonnelService, Result>(lab, s => s.GrantAsync(employeeId, HabilitationActivity.AdultCollection, null,
                                                                               HabilitationLevel.Authorized, hired.AddDays(-1), null, null));
        Errors(early).Should().Be("Habilitation_BeforeHire");

        var onTime = await On<PersonnelService, Result>(lab, s => s.GrantAsync(employeeId, HabilitationActivity.AdultCollection, null,
                                                                                HabilitationLevel.Authorized, hired, null, null));
        onTime.IsSuccess.Should().BeTrue(Errors(onTime));
    }
}
