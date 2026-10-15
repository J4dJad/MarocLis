using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Relations;
using MarocLIS.Application.Statistics;
using MarocLIS.Application.Steering;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Relations;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the relations and steering tests, created from the model.</summary>
public sealed class FinanceHScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_finH_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public FinanceHScratchDatabase()
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
/// The prescriber statistics, cost and margin, steering and patient-messaging queries against SQL
/// Server, through the services as the desktop composes them.
/// </summary>
public sealed class FinanceHDatabaseTests(FinanceHScratchDatabase database) : IClassFixture<FinanceHScratchDatabase>
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly TimeSpan Offset = DateTimeOffset.Now.Offset;

    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "direction";
        public string FullName => "Direction du site A";
        public Guid SiteId => SiteA;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteA];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "DIR-1";
    }

    private ServiceProvider Services()
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
        services.AddSingleton<ICurrentUser, Operator>();
        services.AddIsolatedQueries<IStatisticsQueries, StatisticsQueries>();
        services.AddIsolatedQueries<IPrescriberStatisticsQueries, PrescriberStatisticsQueries>();
        services.AddIsolatedQueries<IRelationsQueries, RelationsQueries>();
        services.AddIsolatedQueries<IPatientMessagingQueries, PatientMessagingQueries>();
        services.AddIsolatedQueries<ICostQueries, CostQueries>();
        services.AddIsolatedQueries<ISteeringQueries, SteeringQueries>();
        return services.BuildServiceProvider();
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static DateTimeOffset At(DateOnly day) => new(day.ToDateTime(new TimeOnly(10, 0)), Offset);

    [SqlServerFact]
    public async Task Prescriber_statistics_margin_and_steering_read_the_same_orders()
    {
        var glucose = new AnalysisTest(Unique("G"), new MultilingualText("Glycémie"), Guid.NewGuid(), Guid.NewGuid());
        var urea = new AnalysisTest(Unique("U"), new MultilingualText("Urée"), Guid.NewGuid(), Guid.NewGuid());
        var cholesterol = new AnalysisTest(Unique("C"), new MultilingualText("Cholestérol"), Guid.NewGuid(), Guid.NewGuid());
        var newcomer = new Prescriber("ALAMI", "Nadia", PrescriberKind.Specialist) { InstitutionName = "Clinique Atlas" };
        var departed = new Prescriber("BENNANI", "Omar", PrescriberKind.GeneralPractitioner);
        var patient = new Patient(Unique("P"), "IDRISSI", "Salma", new DateOnly(1980, 1, 1), Gender.Female, SiteA);

        // In the period: one order of a glucose and a urea at 30 MAD each, a cancelled third test,
        // and a cancelled order.
        var current = new LabOrder(Unique("O"), patient.Id, SiteA, At(Today.AddDays(-5)), Guid.NewGuid()) { PrescriberId = newcomer.Id };
        current.AddItem(new LabOrderItem(glucose.Id, OrderPriority.Routine) { ChargedPrice = 30m });
        current.AddItem(new LabOrderItem(urea.Id, OrderPriority.Routine) { ChargedPrice = 30m });
        var dropped = new LabOrderItem(cholesterol.Id, OrderPriority.Routine) { ChargedPrice = 99m };
        current.AddItem(dropped);

        var cancelled = new LabOrder(Unique("O"), patient.Id, SiteA, At(Today.AddDays(-4)), Guid.NewGuid()) { PrescriberId = newcomer.Id };
        cancelled.AddItem(new LabOrderItem(glucose.Id, OrderPriority.Routine) { ChargedPrice = 500m });

        // In the previous period only: the prescriber who stopped sending.
        var previous = new LabOrder(Unique("O"), patient.Id, SiteA, At(Today.AddDays(-45)), Guid.NewGuid()) { PrescriberId = departed.Id };
        previous.AddItem(new LabOrderItem(glucose.Id, OrderPriority.Routine) { ChargedPrice = 40m });

        // A glucose kit of 500 tests at 250 MAD: 0.50 MAD a test. The urea has no reagent on file.
        var kit = new StockItem(Unique("K"), new MultilingualText("Glucose kit"), StockItemKind.Reagent, "coffret", SiteA)
        {
            UsedByAnalysisCodesCsv = glucose.Code,
            TestsPerPack = 500,
            LastUnitPriceMad = 250m
        };

        await using (var context = database.Context())
        {
            context.AnalysisTests.AddRange(glucose, urea, cholesterol);
            context.Prescribers.AddRange(newcomer, departed);
            context.Patients.Add(patient);
            context.LabOrders.AddRange(current, cancelled, previous);
            context.StockItems.Add(kit);
            await context.SaveChangesAsync();

            // Cancelling an order cancels its tests, as the order screen does.
            await context.LabOrderItems.Where(i => i.Id == dropped.Id || i.LabOrderId == cancelled.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LabOrderItemStatus.Cancelled));
            await context.LabOrders.Where(o => o.Id == cancelled.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, LabOrderStatus.Cancelled));
        }

        await using var services = Services();
        using var scope = services.CreateScope();
        var from = Today.AddDays(-29);

        var statistics = await scope.ServiceProvider.GetRequiredService<PrescriberStatisticsService>().GetAsync(from, Today);
        statistics.IsSuccess.Should().BeTrue();
        statistics.Value.Total.Orders.Should().Be(1);
        statistics.Value.Total.Tests.Should().Be(2);
        statistics.Value.Total.Revenue.Should().Be(60m);
        statistics.Value.TotalPrevious.Revenue.Should().Be(40m);
        statistics.Value.NewPrescribers.Select(p => p.PrescriberId).Should().Equal(newcomer.Id);
        statistics.Value.LostPrescribers.Select(p => p.PrescriberId).Should().Equal(departed.Id);
        statistics.Value.Institutions.Should().Contain(i => i.Institution == "Clinique Atlas" && i.Current.Revenue == 60m);

        var margin = await scope.ServiceProvider.GetRequiredService<CostMarginService>().GetAsync(from, Today);
        margin.IsSuccess.Should().BeTrue();
        margin.Value.Totals.Tests.Should().Be(2);
        margin.Value.Totals.Revenue.Should().Be(60m);
        margin.Value.Totals.ReagentCost.Should().Be(0.5m);
        margin.Value.Totals.Margin.Should().Be(59.5m);
        margin.Value.Totals.TestsWithoutCost.Should().Be(1);
        margin.Value.ByAnalysis.Should().Contain(r => r.Code == glucose.Code && r.CostPerTest == 0.5m);

        var steering = await scope.ServiceProvider.GetRequiredService<SteeringService>().GetAsync(from, Today);
        steering.IsSuccess.Should().BeTrue();
        steering.Value.Orders.Should().Be(1);
        steering.Value.Revenue.Should().Be(60m);
        steering.Value.Margin.Margin.Should().Be(59.5m);
        steering.Value.Outstanding.Should().Be(0m);
    }

    [SqlServerFact]
    public async Task Reminders_and_surveys_reach_only_patients_with_the_follow_up_consent()
    {
        var hba1c = new AnalysisTest(Unique("H"), new MultilingualText("HbA1c"), Guid.NewGuid(), Guid.NewGuid());
        var consenting = new Patient(Unique("P"), "TAZI", "Amina", new DateOnly(1970, 5, 5), Gender.Female, SiteA)
        {
            MobilePhone = "0612345678"
        };
        var silent = new Patient(Unique("P"), "FASSI", "Karim", new DateOnly(1965, 6, 6), Gender.Male, SiteA)
        {
            MobilePhone = "0698765432"
        };

        // Both validated six months and three days ago; both reported three days ago for the survey.
        var resultDay = Today.AddMonths(-6).AddDays(-3);
        var orders = new[] { consenting, silent }.Select(p =>
        {
            var order = new LabOrder(Unique("O"), p.Id, SiteA, At(resultDay), Guid.NewGuid());
            order.AddItem(new LabOrderItem(hba1c.Id, OrderPriority.Routine) { ChargedPrice = 120m });
            return order;
        }).ToList();

        var rule = new ReminderRule(hba1c.Id, null, 6, new MultilingualText(MessagingSettings.DefaultReminderFr));
        var consent = new PatientConsent(consenting.Id, ConsentPurpose.FollowUpMessages, ConsentChannel.WrittenForm,
                                         At(resultDay), Guid.NewGuid());

        await using (var context = database.Context())
        {
            context.AnalysisTests.Add(hba1c);
            context.Patients.AddRange(consenting, silent);
            context.LabOrders.AddRange(orders);
            context.ReminderRules.Add(rule);
            context.PatientConsents.Add(consent);
            await context.SaveChangesAsync();

            var ids = orders.Select(o => o.Id).ToList();
            await context.LabOrderItems.Where(i => ids.Contains(i.LabOrderId))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LabOrderItemStatus.Validated)
                                          .SetProperty(i => i.ValidatedAt, At(resultDay)));
            await context.LabOrders.Where(o => ids.Contains(o.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, LabOrderStatus.Reported)
                                          .SetProperty(o => o.FirstReportedAt, At(Today.AddDays(-3))));
        }

        await using var services = Services();
        using var scope = services.CreateScope();
        var messaging = scope.ServiceProvider.GetRequiredService<PatientMessagingService>();

        var reminders = await messaging.PreviewRemindersAsync();
        reminders.IsSuccess.Should().BeTrue();
        reminders.Value.Should().ContainSingle();
        reminders.Value[0].PatientId.Should().Be(consenting.Id);
        reminders.Value[0].ResultDate.Should().Be(resultDay);
        reminders.Value[0].Phone.Should().Be("0612345678");

        var surveys = await messaging.PreviewSurveysAsync(Today.AddDays(-7), Today);
        surveys.IsSuccess.Should().BeTrue();
        surveys.Value.Select(s => s.PatientId).Should().Equal(consenting.Id);

        var rules = await messaging.GetRulesAsync();
        rules.IsSuccess.Should().BeTrue();
        rules.Value.Should().Contain(r => r.Id == rule.Id && r.Code == hba1c.Code);
    }
}
