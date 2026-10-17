using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Catalog;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the nomenclature, created from the model, seeded, and dropped at the end.</summary>
public sealed class NabmScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_Nabm_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public NabmScratchDatabase()
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

        ReferenceDataSeeder.SeedAsync(context, new BCryptPasswordHasher(4), new NabmNomenclatureDatabaseTests.FixedClock())
                           .GetAwaiter().GetResult();
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
/// The national nomenclature against SQL Server: seeded once and kept in step with the shipped
/// list, found by code or label, compared with the catalogue, and an official coefficient applied
/// as a new tariff version — the version in force before it left as it was.
/// </summary>
public sealed class NabmNomenclatureDatabaseTests(NabmScratchDatabase database) : IClassFixture<NabmScratchDatabase>
{
    internal sealed class FixedClock : IClock
    {
        public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
        public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(10, 0)));
    }

    private sealed class Operator(IReadOnlySet<string> permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "nabm";
        public string FullName => "Dr Nomenclature";
        public Guid SiteId { get; } = Guid.NewGuid();
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.MedicalBiologist;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteId];
        public IReadOnlySet<string> Permissions => permissions;
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "NABM-1";
    }

    private static readonly IReadOnlySet<string> Everything = new HashSet<string>(MarocLIS.Domain.Identity.Permissions.All);

    private ServiceProvider Services(IReadOnlySet<string>? permissions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(permissions ?? Everything));
        services.AddSingleton<IClock>(new FixedClock());
        services.AddIsolatedQueries<ICatalogMaintenanceQueries, CatalogMaintenanceQueries>();
        services.AddIsolatedQueries<INationalActQueries, NationalActQueries>();
        return services.BuildServiceProvider();
    }

    private async Task<T> On<T>(Func<NationalActService, Task<T>> action, IReadOnlySet<string>? permissions = null)
    {
        await using var provider = Services(permissions);
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<NationalActService>());
    }

    private static string Errors(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private static DateOnly Today => new FixedClock().Today;

    /// <summary>An analysis citing <paramref name="act"/>, with a laboratory and a payer tariff started a month ago.</summary>
    private async Task<(Guid Id, string Code, Guid PayerId)> AnalysisAsync(string act, decimal coefficient,
                                                                          decimal labPrice, decimal payerPrice,
                                                                          DateOnly? since = null)
    {
        await using var context = database.Context();
        var department = await context.Departments.FirstAsync();
        var specimen = await context.SpecimenTypes.FirstAsync();
        var payer = await context.Payers.FirstAsync();
        var code = "NABM" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var analysis = new AnalysisTest(code, new MultilingualText("Analyse " + code), department.Id, specimen.Id)
        {
            NationalActCode = act
        };
        var from = since ?? Today.AddDays(-30);
        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, null, from)
        {
            LetterKey = "B", Coefficient = coefficient, LetterKeyValue = 1.10m, Price = labPrice, IsVatExempt = true
        });
        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payer.Id, from)
        {
            LetterKey = "B", Coefficient = coefficient, LetterKeyValue = 1.10m, Price = payerPrice, IsVatExempt = true
        });

        context.AnalysisTests.Add(analysis);
        await context.SaveChangesAsync();
        return (analysis.Id, code, payer.Id);
    }

    // ---- Seeding --------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Seeding_InsertsTheWholeList_AndKeepsItInStepWithTheShippedFile()
    {
        await using var context = database.Context();

        (await context.NationalBiologyActs.CountAsync()).Should().Be(492);
        (await NationalBiologyActSeed.SynchroniseAsync(context)).Should().Be(0, "a second run changes nothing");

        // An official field altered in the database is brought back; whether the act is active is the laboratory's.
        var act = await context.NationalBiologyActs.SingleAsync(a => a.Code == "B135");
        act.Label = "Urée (modifiée)";
        act.Coefficient = 99m;
        act.IsActive = false;
        await context.SaveChangesAsync();

        await using (var again = database.Context())
            (await NationalBiologyActSeed.SynchroniseAsync(again)).Should().Be(1);

        await using var check = database.Context();
        var restored = await check.NationalBiologyActs.SingleAsync(a => a.Code == "B135");
        restored.Label.Should().Be("Urée");
        restored.Coefficient.Should().Be(30m);
        restored.IsActive.Should().BeFalse();

        restored.IsActive = true;
        await check.SaveChangesAsync();
    }

    [SqlServerFact]
    public async Task StarterCatalogue_CarriesTheRealActsAndOfficialCoefficients_AndKeepsItsInternalCodes()
    {
        await using var context = database.Context();

        var starter = await context.AnalysisTests.Include(a => a.Tariffs)
            .Where(a => new[] { "GLY", "CREAT", "UREE", "HBA1C", "CRP", "NFS", "VS", "TP_INR", "TSH", "HBS_AG",
                                 "BILAN_LIP", "TRANSAM", "IONO", "CHIM_URIN", "ECBU", "GROUPAGE" }.Contains(a.Code))
            .ToListAsync();

        string? Act(string code) => starter.Single(a => a.Code == code).NationalActCode;
        decimal Coefficient(string code) => starter.Single(a => a.Code == code).Tariffs.Single(t => t.PayerId == null).Coefficient;

        Act("GLY").Should().Be("B118");
        Coefficient("GLY").Should().Be(30m);
        Act("TSH").Should().Be("B164");
        Coefficient("TSH").Should().Be(250m);
        Act("NFS").Should().Be("B216");
        Coefficient("NFS").Should().Be(80m);
        Act("HBS_AG").Should().Be("B317");

        foreach (var code in new[] { "BILAN_LIP", "TRANSAM", "IONO", "CHIM_URIN", "ECBU", "GROUPAGE" })
            Act(code).Should().BeNull($"{code} maps to no single act");

        starter.Should().OnlyContain(a => a.NationalActCode != a.Code, "the internal code is never the act code");

        // Every mapped starter analysis is on the official coefficient: none appears as a coefficient gap.
        var gaps = await On(s => s.GetGapsAsync());
        gaps.Value.Where(g => g.Kind == NomenclatureGapKind.CoefficientDiffers)
            .Select(g => g.AnalysisCode).Should().NotIntersectWith(starter.Select(a => a.Code));
    }

    [SqlServerFact]
    public async Task Upgrade_ReplacesTheInternalCodeSeededAsActCode_AndLeavesAnActTheLaboratoryChose()
    {
        await using (var context = database.Context())
        {
            var analyses = await context.AnalysisTests.Where(a => a.Code == "GLY" || a.Code == "BILAN_LIP" || a.Code == "CRP").ToListAsync();
            analyses.Single(a => a.Code == "GLY").NationalActCode = "GLY";
            analyses.Single(a => a.Code == "BILAN_LIP").NationalActCode = "BILAN_LIP";
            analyses.Single(a => a.Code == "CRP").NationalActCode = "B457";
            await context.SaveChangesAsync();
        }

        await using (var context = database.Context())
            await ReferenceDataSeeder.SeedAsync(context, new BCryptPasswordHasher(4), new FixedClock());

        await using var check = database.Context();
        var codes = await check.AnalysisTests.Where(a => a.Code == "GLY" || a.Code == "BILAN_LIP" || a.Code == "CRP")
                                             .ToDictionaryAsync(a => a.Code, a => a.NationalActCode);
        codes["GLY"].Should().Be("B118");
        codes["BILAN_LIP"].Should().BeNull();
        codes["CRP"].Should().Be("B457", "an act the laboratory chose is its own");

        var crp = await check.AnalysisTests.SingleAsync(a => a.Code == "CRP");
        crp.NationalActCode = "B370";
        await check.SaveChangesAsync();
    }

    // ---- Lookup ---------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Lookup_FindsAnAct_ByCodeAsTyped_OrByLabelWithoutAccents()
    {
        var found = await On(s => s.FindAsync(" b 118"));
        found.IsSuccess.Should().BeTrue(Errors(found));
        found.Value.Label.Should().Be("Glycémie");
        found.Value.Coefficient.Should().Be(30m);

        var byLabel = await On(s => s.GetActsAsync("glycemie"));
        byLabel.Value.Select(a => a.Code).Should().Contain("B118");
        byLabel.Value.Single(a => a.Code == "B118").AnalysisCount.Should().BeGreaterThanOrEqualTo(1, "GLY cites it");

        (await On(s => s.GetActsAsync("118"))).Value.Select(a => a.Code).Should().Equal("B118");

        var unknown = await On(s => s.FindAsync("B999"));
        Errors(unknown).Should().Be("Nabm_UnknownCode");
    }

    // ---- Gaps and the official coefficient ------------------------------------------------------

    [SqlServerFact]
    public async Task Gaps_ListMissingUnknownAndDifferingAnalyses_WithBothCodes()
    {
        var differing = await AnalysisAsync("B163", 60m, 66m, 100m);
        var unknown = await AnalysisAsync("B999", 10m, 11m, 11m);
        var official = await AnalysisAsync("B111", 30m, 33m, 33m);

        var gaps = (await On(s => s.GetGapsAsync())).Value;

        var gap = gaps.Single(g => g.AnalysisId == differing.Id);
        gap.Kind.Should().Be(NomenclatureGapKind.CoefficientDiffers);
        gap.AnalysisCode.Should().Be(differing.Code);
        gap.NationalActCode.Should().Be("B163");
        gap.OfficialCoefficient.Should().Be(250m);
        gap.CurrentCoefficient.Should().Be(60m);
        gap.DifferingTariffCount.Should().Be(2);

        gaps.Single(g => g.AnalysisId == unknown.Id).Kind.Should().Be(NomenclatureGapKind.UnknownCode);
        gaps.Should().NotContain(g => g.AnalysisId == official.Id);
        gaps.Should().Contain(g => g.AnalysisCode == "ECBU" && g.Kind == NomenclatureGapKind.MissingCode);
    }

    [SqlServerFact]
    public async Task ApplyingTheOfficialCoefficient_OpensANewTariffVersionToday_AndLeavesThePastAsItWas()
    {
        var analysis = await AnalysisAsync("B163", 60m, 66m, 100m);

        var applied = await On(s => s.ApplyOfficialCoefficientAsync(analysis.Id));
        applied.IsSuccess.Should().BeTrue(Errors(applied));
        applied.Value.Should().Be(new OfficialCoefficientOutcome(2, 0, 0));

        await using var context = database.Context();
        var tariffs = await context.Tariffs.Where(t => t.AnalysisTestId == analysis.Id).ToListAsync();
        tariffs.Should().HaveCount(4);

        var oldLab = tariffs.Single(t => t.PayerId == null && t.ValidFrom < Today);
        oldLab.ValidUntil.Should().Be(Today.AddDays(-1));
        oldLab.Coefficient.Should().Be(60m, "the version invoices were priced at is not rewritten");
        oldLab.Price.Should().Be(66m);

        var newLab = tariffs.Single(t => t.PayerId == null && t.ValidFrom == Today);
        newLab.Coefficient.Should().Be(250m);
        newLab.Price.Should().Be(275m, "a price that followed the base (60 × 1.10) follows it (250 × 1.10)");
        newLab.ValidUntil.Should().BeNull();

        var newPayer = tariffs.Single(t => t.PayerId == analysis.PayerId && t.ValidFrom == Today);
        newPayer.Coefficient.Should().Be(250m);
        newPayer.Price.Should().Be(100m, "a price the laboratory set apart from the base is its own");

        var audit = await context.AuditEntries.Where(e => e.Reason != null && e.Reason.Contains(analysis.Code)).ToListAsync();
        audit.Should().Contain(e => e.Reason!.StartsWith("Coefficient officiel B163 appliqué à " + analysis.Code));

        (await On(s => s.GetGapsAsync())).Value.Should().NotContain(g => g.AnalysisId == analysis.Id);

        Errors(await On(s => s.ApplyOfficialCoefficientAsync(analysis.Id))).Should().Be("Nabm_AlreadyOfficial");
    }

    [SqlServerFact]
    public async Task ApplyingTheOfficialCoefficient_ToATariffStartedToday_CorrectsIt()
    {
        var analysis = await AnalysisAsync("B118", 5m, 5.50m, 5.50m, since: Today);

        var applied = await On(s => s.ApplyOfficialCoefficientAsync(analysis.Id));
        applied.IsSuccess.Should().BeTrue(Errors(applied));
        applied.Value.Should().Be(new OfficialCoefficientOutcome(0, 2, 0));

        await using var context = database.Context();
        var tariffs = await context.Tariffs.Where(t => t.AnalysisTestId == analysis.Id).ToListAsync();
        tariffs.Should().HaveCount(2);
        tariffs.Should().OnlyContain(t => t.Coefficient == 30m && t.Price == 33m);
    }

    [SqlServerFact]
    public async Task ApplyingTheOfficialCoefficient_NeedsTheTariffRight_AndAKnownAct()
    {
        var analysis = await AnalysisAsync("B163", 60m, 66m, 66m);

        var refused = await On(s => s.ApplyOfficialCoefficientAsync(analysis.Id),
                               new HashSet<string> { MarocLIS.Domain.Identity.Permissions.Catalog.View });
        refused.IsSuccess.Should().BeFalse();

        var unknown = await AnalysisAsync("B999", 60m, 66m, 66m);
        Errors(await On(s => s.ApplyOfficialCoefficientAsync(unknown.Id))).Should().Be("Nabm_UnknownCode");

        await using var context = database.Context();
        (await context.Tariffs.CountAsync(t => t.AnalysisTestId == analysis.Id)).Should().Be(2, "nothing was written");
    }
}
