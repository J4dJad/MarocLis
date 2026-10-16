using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
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

/// <summary>A scratch database of its own for the "Nouvelle demande" screen, foreign keys off.</summary>
public sealed class OrderEntryScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_NouvelleDemande_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public OrderEntryScratchDatabase()
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

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
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
/// The counter's quote, its repeat reminder and its frequent profiles, against SQL Server and the
/// services as the desktop composes them. The quote is checked against an actual registration of
/// the same request — same total, same payer share, same amount due, one announced tube per label —
/// which also proves registration still works end to end from the screen's request.
/// </summary>
public sealed class OrderEntryScreenDatabaseTests(OrderEntryScratchDatabase database)
    : IClassFixture<OrderEntryScratchDatabase>
{
    private sealed class Operator(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "accueil";
        public string FullName => "Secrétaire d'accueil";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACCUEIL-1";
    }

    private sealed record Lab(Guid SiteId, Patient Insured, Patient Uninsured, Payer Amo,
                              AnalysisTest Nfs, AnalysisTest Hba1c, AnalysisTest Creat, AnalysisTest Iono,
                              TestPanel Renal, TestPanel Other);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private ServiceProvider Services(Guid siteId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId));
        // As DesktopServiceCollectionExtensions registers them for the screen.
        services.AddIsolatedQueries<IOrderEntryQueries, OrderEntryQueries>();
        services.AddIsolatedQueries<IOrderQuotes, OrderQuoteService>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> On<TService, T>(ServiceProvider provider, Func<TService, Task<T>> action)
        where TService : notnull
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static AnalysisTest Analysis(string code, Department department, SpecimenType specimen,
                                         ContainerType container, decimal price, DateOnly validFrom)
    {
        var analysis = new AnalysisTest(Unique(code), new MultilingualText(code), department.Id, specimen.Id)
        {
            ContainerTypeId = container.Id,
            RoutineTurnaroundHours = 4
        };

        var parameter = new TestParameter(Unique(code), new MultilingualText(code), ResultValueType.Numeric);
        parameter.AddReferenceRange(ReferenceRange.Numeric(1m, 100m));
        analysis.AddParameter(parameter);

        // A coefficient equal to the price on a letter key worth one: the reference base is the price.
        analysis.AddTariff(new AnalysisTestTariff(analysis.Id, payerId: null, validFrom)
        {
            LetterKey = "B",
            Coefficient = price,
            LetterKeyValue = 1m,
            Price = price,
            IsVatExempt = true
        });

        return analysis;
    }

    private async Task<Lab> LabAsync()
    {
        var siteId = Guid.NewGuid();
        var validFrom = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);

        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sang"));
        var edta = new ContainerType(Unique("EDTA"), new MultilingualText("EDTA"), "#7B3FBF") { NominalVolumeMl = 4m };
        var heparin = new ContainerType(Unique("HEP"), new MultilingualText("Héparine"), "#3E8E41") { NominalVolumeMl = 5m };

        var nfs = Analysis("NFS", department, specimen, edta, 40m, validFrom);
        var hba1c = Analysis("HBA1C", department, specimen, edta, 40m, validFrom);
        var creat = Analysis("CREAT", department, specimen, heparin, 7m, validFrom);
        var iono = Analysis("IONO", department, specimen, heparin, 18m, validFrom);

        var renal = new TestPanel(Unique("BILAN"), new MultilingualText("Bilan rénal")) { DisplayOrder = 2 };
        renal.AddAnalysis(creat.Id);
        renal.AddAnalysis(iono.Id);
        var other = new TestPanel(Unique("BILAN"), new MultilingualText("Bilan autre")) { DisplayOrder = 1 };
        other.AddAnalysis(nfs.Id);

        var amo = new Payer(Unique("AMO"), new MultilingualText("AMO — CNSS"), PayerKind.AmoCnss)
        {
            DefaultReimbursementRate = 0.80m,
            ThirdPartyPaymentAgreement = true
        };

        var insured = new Patient(Unique("P"), "ALAOUI", "Mohammed", new DateOnly(1959, 9, 2), Gender.Male, siteId);
        insured.AddCoverage(new PatientCoverage(insured.Id, amo.Id, "123456", validFrom));
        var uninsured = new Patient(Unique("P"), "BENNANI", "Salma", new DateOnly(1980, 1, 1), Gender.Female, siteId);

        await using var context = database.Context();
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.ContainerTypes.AddRange(edta, heparin);
        context.AnalysisTests.AddRange(nfs, hba1c, creat, iono);
        context.TestPanels.AddRange(renal, other);
        context.Payers.Add(amo);
        context.Patients.AddRange(insured, uninsured);
        await context.SaveChangesAsync();

        return new Lab(siteId, insured, uninsured, amo, nfs, hba1c, creat, iono, renal, other);
    }

    private static Task<Result<OrderQuote>> QuoteAsync(ServiceProvider provider, Guid? patientId, bool selfPay,
                                                       Guid[] analyses, Guid[] panels) =>
        provider.GetRequiredService<IOrderQuotes>().QuoteAsync(new OrderQuoteRequest
        {
            PatientId = patientId,
            AnalysisTestIds = analyses,
            TestPanelIds = panels,
            BillDirectlyToPatient = selfPay
        });

    private static async Task<RegisteredOrder> RegisterAsync(ServiceProvider provider, Guid patientId,
                                                             Guid[] analyses, Guid[] panels)
    {
        var registered = await On<OrderRegistrationService, Result<RegisteredOrder>>(provider,
            s => s.RegisterAsync(new RegisterOrderRequest
            {
                PatientId = patientId,
                AnalysisTestIds = analyses,
                TestPanelIds = panels,
                PrescriberFreeText = "Dr BENNIS Karim",
                // "Non renseigné" on the screen.
                PatientFasted = null
            }));

        registered.IsSuccess.Should().BeTrue(string.Join(", ", registered.Errors.Select(e => e.ResourceKey)));
        return registered.Value;
    }

    [SqlServerFact]
    public async Task Quote_IsWhatRegistrationBills_AndAnnouncesOneTubePerLabel()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        Guid[] analyses = [lab.Nfs.Id, lab.Hba1c.Id];
        Guid[] panels = [lab.Renal.Id];

        var quote = await QuoteAsync(provider, lab.Insured.Id, selfPay: false, analyses, panels);
        quote.IsSuccess.Should().BeTrue();

        var registered = await RegisterAsync(provider, lab.Insured.Id, analyses, panels);

        // 40 + 40 + 7 + 18, the AMO paying 80 % directly.
        quote.Value.AnalysisCount.Should().Be(4);
        quote.Value.Pricing.Total.Should().Be(registered.Pricing.Total);
        quote.Value.Pricing.Total.Amount.Should().Be(105m);
        quote.Value.Pricing.PayerShare.Should().Be(registered.Pricing.PayerShare);
        quote.Value.Pricing.DueAtCounter.Should().Be(registered.Pricing.DueAtCounter);
        quote.Value.Pricing.DueAtCounter.Amount.Should().Be(21m);
        quote.Value.PayerName!.Fr.Should().Be("AMO — CNSS");
        quote.Value.Problems.Should().BeEmpty();

        // Two tubes, EDTA and heparin, as the labels printed for the registered file.
        quote.Value.Tubes.Should().HaveCount(registered.Labels.Count).And.HaveCount(2);
        quote.Value.Tubes.Select(t => t.Name.Fr).Should().BeEquivalentTo(registered.Labels.Select(l => l.ContainerName));
        quote.Value.Tubes.Single(t => t.Name.Fr == "EDTA").VolumeMl.Should().Be(4m);

        await using var context = database.Context();
        var order = await context.LabOrders.AsNoTracking().SingleAsync(o => o.Id == registered.OrderId);
        order.PatientFasted.Should().BeNull();
    }

    [SqlServerFact]
    public async Task Quote_WithoutPayer_TheCounterCollectsTheGross()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        var noPatient = await QuoteAsync(provider, null, selfPay: false, [lab.Nfs.Id], []);
        var uninsured = await QuoteAsync(provider, lab.Uninsured.Id, selfPay: false, [lab.Nfs.Id], []);
        var directPayment = await QuoteAsync(provider, lab.Insured.Id, selfPay: true, [lab.Nfs.Id], []);

        foreach (var quote in new[] { noPatient, uninsured, directPayment })
        {
            quote.IsSuccess.Should().BeTrue();
            quote.Value.PayerName.Should().BeNull();
            quote.Value.Pricing.DueAtCounter.Should().Be(quote.Value.Pricing.Total);
            quote.Value.Pricing.Total.Amount.Should().Be(40m);
        }
    }

    [SqlServerFact]
    public async Task RecentAnalyses_AreThePatientsLiveAnalysesWithinTheWindow()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        var registered = await RegisterAsync(provider, lab.Insured.Id, [lab.Hba1c.Id], []);

        // An older file, outside the window, and another patient's file.
        var old = new LabOrder(Unique("A"), lab.Insured.Id, lab.SiteId, DateTimeOffset.Now.AddDays(-45), Guid.NewGuid());
        old.AddItem(new LabOrderItem(lab.Nfs.Id, OrderPriority.Routine));
        var theirs = new LabOrder(Unique("A"), lab.Uninsured.Id, lab.SiteId, DateTimeOffset.Now.AddDays(-2), Guid.NewGuid());
        theirs.AddItem(new LabOrderItem(lab.Creat.Id, OrderPriority.Routine));
        await using (var context = database.Context())
        {
            context.LabOrders.AddRange(old, theirs);
            await context.SaveChangesAsync();
        }

        var recent = await provider.GetRequiredService<IOrderEntryQueries>().GetRecentAnalysesAsync(
            lab.Insured.Id, DateTimeOffset.Now.AddDays(-MarocLIS.Desktop.Views.OrderRegistrationRules.RepeatWindowDays));

        recent.Should().ContainSingle();
        recent[0].AnalysisTestId.Should().Be(lab.Hba1c.Id);
        recent[0].AccessionNumber.Should().Be(registered.AccessionNumber);
    }

    [SqlServerFact]
    public async Task FrequentPanels_MostOrderedFirst_CountedPerFile_ThenCatalogueOrder()
    {
        var lab = await LabAsync();
        using var provider = Services(lab.SiteId);

        // The renal panel, of two analyses, ordered once: one order, not two.
        await RegisterAsync(provider, lab.Uninsured.Id, [], [lab.Renal.Id]);

        var frequent = await provider.GetRequiredService<IOrderEntryQueries>().GetFrequentPanelsAsync(
            lab.SiteId, DateTimeOffset.Now.AddDays(-180), take: 20);

        var ours = frequent.Where(f => f.PanelId == lab.Renal.Id || f.PanelId == lab.Other.Id).ToList();
        ours.Select(f => f.PanelId).Should().Equal(lab.Renal.Id, lab.Other.Id);
        ours[0].OrderCount.Should().Be(1);
        ours[1].OrderCount.Should().Be(0);
    }
}
