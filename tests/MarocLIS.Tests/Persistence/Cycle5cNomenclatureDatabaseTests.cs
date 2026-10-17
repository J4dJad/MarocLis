using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
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

/// <summary>A scratch database of its own for lot c5c, created from the model and dropped at the end.</summary>
public sealed class Cycle5cScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Cycle5c_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public Cycle5cScratchDatabase()
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
/// Cycle 5, lot c5c, against SQL Server and the services as the desktop composes them: an analysis
/// created with the laboratory's own internal code, linked to an act of the national nomenclature,
/// counted under that act, brought to the official coefficient — and the new price charged on the
/// requests registered after it, never on those before.
/// </summary>
public sealed class Cycle5cNomenclatureDatabaseTests(Cycle5cScratchDatabase database) : IClassFixture<Cycle5cScratchDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
        public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(10, 0)));
    }

    private sealed class Operator(Guid userId, Guid siteId, IReadOnlySet<string> permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId => userId;
        public string UserName => "c5c";
        public string FullName => "Dr Cycle Cinq";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions => permissions;
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "C5C-1";
    }

    private sealed record Lab(LabSite Site, AppUser Biologist, Department Department, SpecimenType Specimen);

    private static readonly IReadOnlySet<string> Everything = new HashSet<string>(Permissions.All);

    private static DateOnly Today => new FixedClock().Today;

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string Errors(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private async Task<Lab> LabAsync()
    {
        var biologist = new AppUser(Unique("c5c").ToLowerInvariant(), "Dr Cycle Cinq", "x")
        {
            Qualification = ProfessionalQualification.MedicalBiologist,
            ProfessionalOrderNumber = "CNOM-C5C-1"
        };
        var site = new LabSite(Unique("L"), new MultilingualText("Laboratoire C5C"), LabSiteKind.MainLaboratory)
        {
            LegalName = "LABORATOIRE C5C SARL",
            Ice = Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(),
            FiscalId = "45128796",
            AddressLine = new MultilingualText("1, rue du Test"),
            City = "Rabat",
            HealthMinistryAuthorizationNumber = "AUT-C5C-1",
            ResponsibleBiologistUserId = biologist.Id
        };
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));

        await using var context = database.Context();
        context.Users.Add(biologist);
        context.Sites.Add(site);
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);

        // The few acts these tests cite, as the shipped list words them (the whole list is seeded at
        // start-up; the nomenclature tests check that).
        foreach (var (code, label, coefficient) in new[] { ("B118", "Glycémie", 30m), ("B216", "Hémogramme", 70m) })
            if (!await context.NationalBiologyActs.AnyAsync(a => a.Code == code))
                context.NationalBiologyActs.Add(new NationalBiologyAct(code, label, coefficient, 2005)
                {
                    Chapter = "I - Analyses biochimiques"
                });

        await context.SaveChangesAsync();
        return new Lab(site, biologist, department, specimen);
    }

    private ServiceProvider Services(Lab lab, IReadOnlySet<string>? permissions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(lab.Biologist.Id, lab.Site.Id, permissions ?? Everything));
        services.AddSingleton<IClock>(new FixedClock());
        services.AddIsolatedQueries<IPatientQueries, PatientQueries>();
        services.AddIsolatedQueries<ICatalogQueries, CatalogQueries>();
        services.AddIsolatedQueries<IOrderQueries, OrderQueries>();
        services.AddIsolatedQueries<ICatalogMaintenanceQueries, CatalogMaintenanceQueries>();
        services.AddIsolatedQueries<INationalActQueries, NationalActQueries>();
        return services.BuildServiceProvider();
    }

    private async Task<T> On<TService, T>(Lab lab, Func<TService, Task<T>> action, IReadOnlySet<string>? permissions = null)
        where TService : notnull
    {
        await using var provider = Services(lab, permissions);
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static AnalysisRequest Analysis(Lab lab, string code, string? actCode) => new()
    {
        Code = code,
        Name = new MultilingualText("Glycémie à jeun " + code),
        DepartmentId = lab.Department.Id,
        SpecimenTypeId = lab.Specimen.Id,
        NationalActCode = actCode,
        RoutineTurnaroundHours = 4,
        UrgentTurnaroundMinutes = 60,
        IsAvailableUrgently = true,
        IsActive = true
    };

    /// <summary>A numeric parameter, so the analysis can be ordered.</summary>
    private async Task ParameterAsync(Guid analysisId, string code)
    {
        await using var context = database.Context();
        var analysis = await context.AnalysisTests.Include(a => a.Parameters).SingleAsync(a => a.Id == analysisId);
        var parameter = new TestParameter(code, new MultilingualText("Glycémie"), ResultValueType.Numeric) { Decimals = 2 };
        parameter.AddReferenceRange(ReferenceRange.Numeric(0.7m, 1.1m));
        analysis.AddParameter(parameter);
        await context.SaveChangesAsync();
    }

    // ---- The internal code -------------------------------------------------------------------------

    /// <summary>
    /// The internal code is the laboratory's: required, unique whatever the case it is typed in, and
    /// never renamed. Several analyses may cite the same act, and an analysis may cite none.
    /// </summary>
    [SqlServerFact]
    public async Task TheInternalCode_IsRequiredUniqueAndKept_WhileSeveralAnalysesShareOneAct()
    {
        var lab = await LabAsync();
        var code = Unique("GLYJ");

        Errors(await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(Analysis(lab, " ", "B118"))))
            .Should().Contain("Test_CodeRequired");

        var first = await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(Analysis(lab, code, "B118")));
        first.IsSuccess.Should().BeTrue(Errors(first));

        Errors(await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(Analysis(lab, code.ToLowerInvariant(), null))))
            .Should().Be("Test_CodeAlreadyUsed", "« glyj… » is the code already given, typed in lower case");

        // A second analysis citing the same act (a fasting and a post-prandial glucose, say), and one citing none.
        var second = await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(Analysis(lab, Unique("GLYPP"), "B118")));
        second.IsSuccess.Should().BeTrue(Errors(second));
        var outside = await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(Analysis(lab, Unique("HORS"), null)));
        outside.IsSuccess.Should().BeTrue(Errors(outside));

        var acts = await On<NationalActService, Result<IReadOnlyList<NationalActRow>>>(lab, s => s.GetActsAsync("B118"));
        acts.Value.Single(a => a.Code == "B118").AnalysisCount.Should().BeGreaterThanOrEqualTo(2);

        var renamed = Analysis(lab, Unique("AUTRE"), "B118") with { Id = first.Value };
        Errors(await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(renamed)))
            .Should().Be("Test_CodeImmutable");

        var gaps = (await On<NationalActService, Result<IReadOnlyList<NomenclatureGapRow>>>(lab, s => s.GetGapsAsync())).Value;
        gaps.Should().Contain(g => g.AnalysisId == outside.Value && g.Kind == NomenclatureGapKind.MissingCode);
        gaps.Should().NotContain(g => g.AnalysisId == first.Value && g.Kind != NomenclatureGapKind.CoefficientDiffers,
                                 "its act is known; only its tariff can depart from it");
    }

    // ---- The official coefficient and the requests ----------------------------------------------

    /// <summary>
    /// An analysis priced B 25 linked to B118 (B 30): applying the official coefficient opens a new
    /// tariff from today, the next request is charged the new price, and the request registered
    /// before keeps the price it was registered at. The read-only right cannot apply it.
    /// </summary>
    [SqlServerFact]
    public async Task TheOfficialCoefficient_AppliesToTheRequestsRegisteredAfterIt_AndToNoneBefore()
    {
        var lab = await LabAsync();
        var code = Unique("GLY");

        var saved = await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(Analysis(lab, code, "B118")));
        saved.IsSuccess.Should().BeTrue(Errors(saved));
        var analysisId = saved.Value;
        await ParameterAsync(analysisId, code);

        await using (var context = database.Context())
        {
            var analysis = await context.AnalysisTests.Include(a => a.Tariffs).SingleAsync(a => a.Id == analysisId);
            analysis.AddTariff(new AnalysisTestTariff(analysisId, null, Today.AddMonths(-3))
            {
                LetterKey = "B", Coefficient = 25m, LetterKeyValue = 1m, Price = 25m, IsVatExempt = true
            });
            await context.SaveChangesAsync();
        }

        var patient = new Patient(Unique("P"), "BENNANI", "Karim", new DateOnly(1975, 3, 2), Gender.Male, lab.Site.Id)
        {
            MobilePhone = "0612345678",
            SearchKey = PatientSearchKey.Build("BENNANI", "Karim")
        };
        await using (var context = database.Context())
        {
            context.Patients.Add(patient);
            await context.SaveChangesAsync();
        }

        async Task<Guid> RegisterAsync()
        {
            var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(lab, s => s.RegisterAsync(new RegisterOrderRequest
            {
                PatientId = patient.Id, AnalysisTestIds = [analysisId], PrescriberFreeText = "Dr Prescripteur"
            }));
            registered.IsSuccess.Should().BeTrue(Errors(registered));
            return registered.Value.OrderId;
        }

        async Task<(decimal Price, string? Act)> ChargedAsync(Guid orderId)
        {
            await using var context = database.Context();
            var item = await context.LabOrderItems.AsNoTracking().SingleAsync(i => i.LabOrderId == orderId);
            return (item.ChargedPrice, item.BilledActCode);
        }

        var before = await RegisterAsync();
        (await ChargedAsync(before)).Should().Be((25m, "B118"));

        var gap = (await On<NationalActService, Result<IReadOnlyList<NomenclatureGapRow>>>(lab, s => s.GetGapsAsync())).Value
            .Single(g => g.AnalysisId == analysisId);
        gap.Kind.Should().Be(NomenclatureGapKind.CoefficientDiffers);
        (gap.OfficialCoefficient, gap.CurrentCoefficient).Should().Be((30m, 25m));

        var refused = await On<NationalActService, Result<OfficialCoefficientOutcome>>(lab, s => s.ApplyOfficialCoefficientAsync(analysisId),
            new HashSet<string> { Permissions.Catalog.View, Permissions.Catalog.Manage });
        refused.IsSuccess.Should().BeFalse("bringing a price to the official coefficient needs the tariff right");

        var applied = await On<NationalActService, Result<OfficialCoefficientOutcome>>(lab, s => s.ApplyOfficialCoefficientAsync(analysisId));
        applied.IsSuccess.Should().BeTrue(Errors(applied));
        applied.Value.Should().Be(new OfficialCoefficientOutcome(1, 0, 0));

        var after = await RegisterAsync();
        (await ChargedAsync(after)).Should().Be((30m, "B118"), "a price that followed the base (25 × 1) follows it (30 × 1)");
        (await ChargedAsync(before)).Should().Be((25m, "B118"), "a request keeps the price it was registered at");

        (await On<NationalActService, Result<IReadOnlyList<NomenclatureGapRow>>>(lab, s => s.GetGapsAsync())).Value
            .Should().NotContain(g => g.AnalysisId == analysisId);
    }

    /// <summary>
    /// A revision already scheduled after today is left for the operator: no tariff is inserted in
    /// front of it, the refusal says why, and nothing is written. With the laboratory's list
    /// scheduled but a payer's not, the payer's is brought to the official coefficient and the
    /// laboratory's counted as left alone.
    /// </summary>
    [SqlServerFact]
    public async Task ARevisionScheduledAfterToday_IsLeftForTheOperator()
    {
        var lab = await LabAsync();
        var payer = new Payer(Unique("CNSS"), new MultilingualText("Organisme C5C"), PayerKind.AmoCnss);

        var saved = await On<CatalogMaintenanceService, Result<Guid>>(lab, s => s.SaveAnalysisAsync(Analysis(lab, Unique("NFS"), "B216")));
        saved.IsSuccess.Should().BeTrue(Errors(saved));
        var analysisId = saved.Value;

        await using (var context = database.Context())
        {
            context.Payers.Add(payer);
            var analysis = await context.AnalysisTests.Include(a => a.Tariffs).SingleAsync(a => a.Id == analysisId);
            analysis.AddTariff(new AnalysisTestTariff(analysisId, null, Today.AddMonths(-3))
            {
                LetterKey = "B", Coefficient = 60m, LetterKeyValue = 1m, Price = 60m, IsVatExempt = true
            });
            analysis.AddTariff(new AnalysisTestTariff(analysisId, null, Today.AddDays(10))
            {
                LetterKey = "B", Coefficient = 65m, LetterKeyValue = 1m, Price = 65m, IsVatExempt = true
            });
            await context.SaveChangesAsync();
        }

        Errors(await On<NationalActService, Result<OfficialCoefficientOutcome>>(lab, s => s.ApplyOfficialCoefficientAsync(analysisId)))
            .Should().Be("Nabm_RevisionScheduled");

        await using (var context = database.Context())
        {
            (await context.Tariffs.CountAsync(t => t.AnalysisTestId == analysisId)).Should().Be(2, "nothing was written");

            var analysis = await context.AnalysisTests.Include(a => a.Tariffs).SingleAsync(a => a.Id == analysisId);
            analysis.AddTariff(new AnalysisTestTariff(analysisId, payer.Id, Today.AddMonths(-3))
            {
                LetterKey = "B", Coefficient = 60m, LetterKeyValue = 1m, Price = 80m, IsVatExempt = true
            });
            await context.SaveChangesAsync();
        }

        var applied = await On<NationalActService, Result<OfficialCoefficientOutcome>>(lab, s => s.ApplyOfficialCoefficientAsync(analysisId));
        applied.IsSuccess.Should().BeTrue(Errors(applied));
        applied.Value.Should().Be(new OfficialCoefficientOutcome(1, 0, 1));

        await using (var context = database.Context())
        {
            var tariffs = await context.Tariffs.AsNoTracking().Where(t => t.AnalysisTestId == analysisId).ToListAsync();
            tariffs.Where(t => t.PayerId == null).Select(t => (t.ValidFrom, t.Coefficient))
                   .Should().BeEquivalentTo([(Today.AddMonths(-3), 60m), (Today.AddDays(10), 65m)], "the laboratory's list is left as scheduled");
            var payerNow = tariffs.Single(t => t.PayerId == payer.Id && t.ValidFrom == Today);
            (payerNow.Coefficient, payerNow.Price).Should().Be((70m, 80m), "a price set apart from the base is the laboratory's own");
        }
    }
}
