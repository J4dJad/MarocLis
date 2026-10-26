using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Dashboard;
using MarocLIS.Application.Statistics;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Web.Components.Screens.Steering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Tableau de bord », « Statistiques d'activité » and « Pilotage » in the browser: the day's flow
/// and the alerts each with the way to its screen, only what the operator's rights let them see;
/// the month's activity and money in DH by department, analysis, payer and day, each table
/// downloaded; the director's period across the sites with the payroll tile only for whom may see
/// salaries, a band of receivables overdue in orange — and a period typed backwards refused beside
/// its dates — through the workstation's queries and services.
/// </summary>
public sealed class WebPilotageTests : WebScreenContext
{
    private readonly Fake<IDashboardQueries> _dashboard = Fake<IDashboardQueries>.Create();
    private readonly Fake<IStatisticsQueries> _statistics = Fake<IStatisticsQueries>.Create();
    private readonly Fake<ISteeringQueries> _steering = Fake<ISteeringQueries>.Create();
    private readonly Fake<ICostQueries> _costs = Fake<ICostQueries>.Create();
    private readonly Guid _department = Guid.NewGuid();
    private readonly Guid _glucose = Guid.NewGuid();
    private readonly Guid _cnops = Guid.NewGuid();

    public WebPilotageTests()
    {
        Services.AddSingleton(_dashboard.Object);
        Services.AddSingleton(_statistics.Object);
        Services.AddSingleton(_steering.Object);
        Services.AddSingleton(_costs.Object);
        Services.AddSingleton(Fake<IConfigurationWriter>.Create().Object);
        Services.AddSingleton(Fake<IUnitOfWork>.Create().Object);
        Services.AddScoped<StatisticsService>();
        Services.AddScoped<CostMarginService>();
        Services.AddScoped<SteeringService>();

        _dashboard.On(nameof(IDashboardQueries.GetSnapshotAsync), _ => new DashboardSnapshot(
            [
                new("Dashboard.OrdersToday", 42, DashboardSeverity.Neutral, "orders"),
                new("Dashboard.AwaitingCollection", 5, DashboardSeverity.Attention, "collection"),
                new("Dashboard.AwaitingReception", 3, DashboardSeverity.Attention, "reception"),
                new("Dashboard.AwaitingEntry", 7, DashboardSeverity.Attention, "entry"),
                new("Dashboard.AwaitingValidation", 12, DashboardSeverity.Attention, "validation"),
                new("Dashboard.ReportsToRelease", 4, DashboardSeverity.Attention, "reports"),
                new("Report.Plural", 9, DashboardSeverity.Neutral, "reports")
            ],
            [
                new("Dashboard.Receivables", 9, DashboardSeverity.Attention, "billing"),
                new("Dashboard.CriticalPending", 2, DashboardSeverity.Urgent, "validation"),
                new("Inventory.ToReorder", 6, DashboardSeverity.Attention, "inventory"),
                new("Dashboard.QcAlerts", 0, DashboardSeverity.Neutral, "qc")
            ],
            TestClock.Now));
        Catalog.On(nameof(ICatalogQueries.GetCatalogueSetupStatusAsync), _ =>
            new CatalogueSetupStatus(["GLY", "UREE", "CREA", "NA"], ["NFS"]));

        // Ten glucose tests in October, four for the CNOPS, one cancelled.
        _statistics.On(nameof(IStatisticsQueries.GetActivityAsync), _ => Enumerable.Range(0, 10).Select(i =>
            new MarocLIS.Application.Statistics.ActivityItem(Guid.NewGuid(), Guid.NewGuid(), TestClock.Now.AddDays(-i % 3), i < 4 ? _cnops : null,
                                 _glucose, _department, i == 9 ? LabOrderItemStatus.Cancelled : LabOrderItemStatus.Validated,
                                 TestClock.Now.AddDays(-i % 3), TestClock.Now.AddDays(-i % 3).AddHours(5), TestClock.Now.AddDays(1),
                                 30m, i < 4 ? 24m : 0m, i < 4 ? 6m : 30m)).ToList());
        _statistics.On(nameof(IStatisticsQueries.GetLabelsAsync), _ => new StatisticsLabels(
            new Dictionary<Guid, (string, MultilingualText)> { [_glucose] = ("GLY", new MultilingualText("Glycémie à jeun", "سكر الدم", "Fasting glucose")) },
            new Dictionary<Guid, (string, MultilingualText)> { [_department] = ("BIO", new MultilingualText("Biochimie", "الكيمياء الحيوية", "Biochemistry")) },
            new Dictionary<Guid, MultilingualText> { [_cnops] = new MultilingualText("CNOPS") }));
        _statistics.On(nameof(IStatisticsQueries.GetFinanceAsync), _ => new FinanceTotals(270m, 0m, 180m));

        _steering.On(nameof(ISteeringQueries.GetActivityAsync), a => ((IReadOnlyList<Guid>)a[0]!).SelectMany(site =>
            Enumerable.Range(0, 4).Select(i => new SteeringActivityRow(site, null,
                new MarocLIS.Application.Statistics.ActivityItem(Guid.NewGuid(), Guid.NewGuid(), TestClock.Now, null, _glucose, _department,
                                 LabOrderItemStatus.Validated, TestClock.Now, TestClock.Now.AddHours(4), TestClock.Now.AddDays(1), 100m, 0m, 100m))))
            .ToList());
        _steering.On(nameof(ISteeringQueries.GetReceivablesAsync), a => new[]
        {
            new ReceivableRow(((IReadOnlyList<Guid>)a[0]!)[0], InvoiceKind.Invoice, new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 31),
                              5_400m, 0m, 0m)
        });
        _steering.On(nameof(ISteeringQueries.GetClosedPayrollAsync), _ => Array.Empty<PayrollMonthRow>());
        _steering.On(nameof(ISteeringQueries.GetQualityAsync), _ => new QualityCounts(140, 6, 3, 2, new Dictionary<EqaOutcome, int>()));
        _steering.On(nameof(ISteeringQueries.GetLabelsAsync), _ => new SteeringLabels(
            new Dictionary<Guid, string>(), new Dictionary<Guid, MultilingualText>(), new Dictionary<Guid, string>()));
        _costs.On(nameof(ICostQueries.GetLabelsAsync), _ => new CostLabels(
            new Dictionary<Guid, (string, MultilingualText)>(), new Dictionary<Guid, (string, MultilingualText)>(),
            new Dictionary<Guid, (string, MultilingualText)>()));
        _costs.On(nameof(ICostQueries.GetSettingValuesAsync), _ => new Dictionary<string, string?>());
    }

    // ---- Tableau de bord ---------------------------------------------------------------------------

    [Fact]
    public void TheDashboard_ShowsTheDaysFlowAndTheAlerts_EachLeadingToItsScreen()
    {
        SignIn(AppLanguage.French, [.. Permissions.All]);

        var page = Render<DashboardScreen>();

        page.Find("[data-state=greeting]").TextContent.Should().Be("Bonjour, Nadia");
        page.Find("[data-state=summary]").TextContent.Should().Contain("42").And.Contain("9");
        page.FindAll(".dashboard-step").Select(s => s.GetAttribute("href"))
            .Should().Equal("ecran/collection", "ecran/reception", "ecran/entry", "ecran/validation", "ecran/reports");
        page.Find("[data-step='Dashboard.AwaitingValidation']").ClassName.Should().Contain("is-highlighted");

        page.FindAll("[data-alert]").Select(a => a.GetAttribute("data-alert"))
            .Should().Equal("Dashboard.CriticalPending", "Inventory.ToReorder", "Dashboard.Receivables");
        page.Find("[data-alert='Dashboard.CriticalPending'] .pill").TextContent.Should().Be("Urgent");
        page.Find("[data-alert='Inventory.ToReorder'] a").GetAttribute("href").Should().Be("ecran/inventory");
        page.Find("[data-quiet='Dashboard.QcAlertsLong']").TextContent.Should().Contain("Alerte de contrôle interne");

        page.Find("[data-state=setup]").TextContent.Should().Contain("GLY, UREE, CREA");
        page.Find("[data-state=setup] a").GetAttribute("href").Should().Be("ecran/catalog");

        page.Find("[data-action='Action.Refresh']").Click();
        _dashboard.CallsTo(nameof(IDashboardQueries.GetSnapshotAsync)).Should().Be(2);
    }

    [Fact]
    public void ACashier_SeesOnlyTheFiguresOfTheirRights_AndNoWayToTheCatalogue()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);

        var page = Render<DashboardScreen>();

        page.FindAll("[data-card=flow]").Should().BeEmpty("no step of the flow is the cashier's");
        page.FindAll("[data-alert]").Should().ContainSingle().Which.GetAttribute("data-alert").Should().Be("Dashboard.Receivables");
        page.Find("[data-alert='Dashboard.Receivables'] a").GetAttribute("href").Should().Be("ecran/billing");
        page.FindAll("[data-quiet]").Should().BeEmpty();
        page.Find("[data-state=setup]").TextContent.Should().Contain("GLY", "the banner shows to everyone");
        page.FindAll("[data-state=setup] a").Should().BeEmpty("the way to the catalogue only to whom may open it");
    }

    [Fact]
    public void AFigureTheOperatorMayNotOpen_IsShownWithoutItsWay()
    {
        SignIn(AppLanguage.French, Permissions.Results.View, Permissions.Results.Enter);

        var page = Render<DashboardScreen>();

        page.Find("[data-step='Dashboard.AwaitingEntry']").GetAttribute("href").Should().Be("ecran/entry");
        page.Find("[data-step='Dashboard.AwaitingValidation']").TagName.Should().Be("DIV");
        page.FindAll("[data-alert='Dashboard.CriticalPending'] a").Should().BeEmpty();
    }

    // ---- Statistiques d'activité -------------------------------------------------------------------

    [Fact]
    public void TheMonthsStatistics_AreShownInDH_ByTab_AndDownloaded()
    {
        SignIn(AppLanguage.French, Permissions.Statistics.View);

        var page = Render<ActivityStatisticsScreen>();

        page.FindAll("[data-tile]").Should().HaveCount(8);
        page.Find("[data-tile=tests]").TextContent.Should().Contain("annulées 1");
        page.Find("[data-tile=charged]").TextContent.Should().Contain("270,00 DH").And.NotContain("MAD");
        page.Find("[data-grid=statistics] tbody tr").TextContent.Should().Contain("Biochimie").And.Contain("5,0 h");

        page.Find($"[data-tab={StatisticsTab.ByPayer}]").Click();
        page.Find("[data-grid=statistics]").TextContent.Should().Contain("Patient payant").And.Contain("CNOPS");

        page.Find("[data-action='Statistics.ExportCsv']").Click();
        var download = JSInterop.Invocations.Single(i => i.Identifier == "marocLis.download");
        ((string)download.Arguments[0]!).Should().StartWith("statistiques-par-payeur-").And.EndWith(".csv");
    }

    [Fact]
    public void APeriodTypedBackwards_IsRefusedBesideItsDates()
    {
        SignIn(AppLanguage.French, Permissions.Statistics.View);
        var page = Render<ActivityStatisticsScreen>();

        page.Find("[data-field=statistics-from]").Change("2026-10-20");
        page.Find($"[data-field={StatisticsScreenRules.PeriodField}]").Change("2026-10-01");
        page.Find("[data-action='Statistics.Compute']").Click();

        page.Find($"[data-refusal={StatisticsScreenRules.PeriodField}]").TextContent.Should().NotBeEmpty();
        page.Find($"[data-filter={PeriodPreset.Custom}]").ClassName.Should().Contain("is-pressed");

        page.Find($"[data-filter={PeriodPreset.LastMonth}]").Click();
        page.FindAll($"[data-refusal={StatisticsScreenRules.PeriodField}]").Should().BeEmpty("a chip gives a period that is computed");
        page.Find("[data-state=heading]").TextContent.Should().Contain("01/09/2026 → 30/09/2026");
    }

    // ---- Pilotage ----------------------------------------------------------------------------------

    [Fact]
    public void TheDirectorsPeriod_ShowsNoPayrollTileWithoutTheSalaryRight_AndTheOverdueBandInOrange()
    {
        SignIn(AppLanguage.French, Permissions.Steering.View);

        var page = Render<SteeringScreen>();

        page.FindAll("[data-tile]").Select(t => t.GetAttribute("data-tile"))
            .Should().Equal("revenue", "invoiced", "reagentCost", "margin", "overdue", "turnaround", "qc");
        page.Find("[data-tile=revenue]").TextContent.Should().Contain("400,00 DH");
        page.Find("[data-tile=overdue]").GetAttribute("data-ink").Should().Be("Abnormal");

        page.Find($"[data-tab={SteeringTab.Receivables}]").Click();
        var late = page.Find("[data-grid=steering] tr[data-ink=Abnormal]");
        late.TextContent.Should().Contain("61 à 90 jours").And.Contain("400,00 DH");

        page.Find("[data-action='Statistics.ExportCsv']").Click();
        var download = JSInterop.Invocations.Single(i => i.Identifier == "marocLis.download");
        ((string)download.Arguments[0]!).Should().StartWith("pilotage-creances-");
    }

    [Fact]
    public void WithTheSalaryRight_ThePayrollTileIsShown()
    {
        SignIn(AppLanguage.French, Permissions.Steering.View, Permissions.HumanResources.Salary);

        var page = Render<SteeringScreen>();

        page.Find("[data-tile=payroll]").TextContent.Should().Contain("Aucun mois de paie clôturé");
    }

    [Theory]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void TheScreens_AreTranslated(AppLanguage language)
    {
        SignIn(language, [.. Permissions.All]);

        Render<DashboardScreen>().Markup.Should().NotContain("[[");
        Render<ActivityStatisticsScreen>().Markup.Should().NotContain("[[");
        Render<SteeringScreen>().Markup.Should().NotContain("[[");
    }
}
