using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Relations;
using MarocLIS.Application.Statistics;
using MarocLIS.Application.Steering;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>Review 6, relations and steering: the planner, the stored settings and the order counts.</summary>
public sealed class ReviewFixesFinanceRelationsTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(1);
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 10, 0, 0, Offset);

    private static ReminderRule Rule(Guid? test = null, Guid? panel = null) =>
        new(panel is null ? test ?? Guid.NewGuid() : null, panel, 3,
            new MultilingualText(MessagingSettings.DefaultReminderFr)) { WindowDays = 60 };

    // ---- R2: one reminder per patient ----------------------------------------------------------------

    [Fact]
    public void R2_AnAnalysisRuleAndAPanelRuleOnTheSameOrder_SendOneReminder_ThePanelOne()
    {
        var analysisRule = Rule();
        var panelRule = Rule(panel: Guid.NewGuid());
        var patient = Guid.NewGuid();
        var order = Guid.NewGuid();
        var sources = new[] { new ReminderSourceRow(patient, order, At(2026, 7, 9), At(2026, 7, 10)) };
        var none = new HashSet<(Guid, Guid)>();

        var planned = ReminderPlanner.Plan(analysisRule, sources, none, Today, Offset)
            .Concat(ReminderPlanner.Plan(panelRule, sources, none, Today, Offset)).ToList();
        planned.Should().HaveCount(2);

        var chosen = ReminderPlanner.OnePerPatient(planned, [], new HashSet<Guid> { panelRule.Id }, Offset);

        chosen.Should().ContainSingle();
        chosen[0].RuleId.Should().Be(panelRule.Id);
    }

    [Fact]
    public void R2_AnOrderJustRemindedUnderAnotherRule_IsNotRemindedAgain()
    {
        var first = Rule();
        var second = Rule();
        var patient = Guid.NewGuid();
        var order = Guid.NewGuid();
        var sources = new[] { new ReminderSourceRow(patient, order, At(2026, 7, 9), At(2026, 7, 10)) };
        var sent = new HashSet<(Guid, Guid)> { (first.Id, order) };

        var planned = ReminderPlanner.Plan(second, sources, sent, Today, Offset);
        planned.Should().ContainSingle();

        // Sent under the first rule on its due date, which is this rule's too (review 7, R11: only
        // a reminder sent at about the same time covers it).
        ReminderPlanner.OnePerPatient(planned, [new SentReminder(first.Id, order, At(2026, 10, 10))],
                                      new HashSet<Guid>(), Offset).Should().BeEmpty();
    }

    // ---- R6: the sources cover every site -----------------------------------------------------------

    [Fact]
    public void R6_APatientBackAtAnotherSite_IsNotReminded_AndOnlyTheSiteOfTheLatestResultReminds()
    {
        var rule = Rule();
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var back = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        var sources = new[]
        {
            // Done at A in July, again at B in October: A has nothing to remind.
            new ReminderSourceRow(back, Guid.NewGuid(), At(2026, 7, 9), At(2026, 7, 10), siteA),
            new ReminderSourceRow(back, Guid.NewGuid(), At(2026, 9, 25), At(2026, 9, 26), siteB),
            // Latest result at B: B reminds it, A does not.
            new ReminderSourceRow(elsewhere, Guid.NewGuid(), At(2026, 7, 9), At(2026, 7, 10), siteB)
        };
        var none = new HashSet<(Guid, Guid)>();

        ReminderPlanner.Plan(rule, sources, none, Today, Offset, siteA).Should().BeEmpty();
        ReminderPlanner.Plan(rule, sources, none, Today, Offset, siteB)
            .Select(p => p.PatientId).Should().Equal(elsewhere);
    }

    // ---- R3: stored settings that their screen would refuse ----------------------------------------

    [Fact]
    public void R3_AStoredSurveyTemplateThatIsPromotionalOrHasNoCode_FallsBackToTheDefault()
    {
        var settings = MessagingSettings.FromValues(new Dictionary<string, string?>
        {
            [MessagingSettings.SurveyTemplateFrKey] = "-10 % sur votre prochain bilan, répondez {code}",
            [MessagingSettings.SurveyTemplateArKey] = "{labo}: شكرا",
            [MessagingSettings.SurveyUrlKey] = "http://example.org/avis"
        });

        settings.SurveyTemplateFr.Should().Be(MessagingSettings.Default.SurveyTemplateFr);
        settings.SurveyTemplateAr.Should().Be(MessagingSettings.Default.SurveyTemplateAr);
        settings.SurveyUrl.Should().BeEmpty();

        MessagingSettings.FromValues(new Dictionary<string, string?>
        {
            [MessagingSettings.SurveyTemplateFrKey] = "{labo} : votre avis compte. Répondez {code} et une note de 1 à 5.",
            [MessagingSettings.SurveyUrlKey] = "https://example.org/avis?c={code}"
        }).Should().Match<MessagingSettings>(s => s.SurveyTemplateFr.StartsWith("{labo} : votre avis")
                                                 && s.SurveyUrl == "https://example.org/avis?c={code}");
    }

    [Fact]
    public void R3_AStoredAllocationAboveTheCeiling_IsIgnored()
    {
        CostSettings.FromValues(new Dictionary<string, string?> { [CostSettings.AllocationPerTestKey] = "250000" })
            .AllocationPerTestMad.Should().Be(0m);
        CostSettings.FromValues(new Dictionary<string, string?> { [CostSettings.AllocationPerTestKey] = "12,5" })
            .AllocationPerTestMad.Should().Be(12.5m);
    }

    [Fact]
    public void R3_TheMessagingAndCostKeys_AreReservedToTheirScreens()
    {
        ConfigurationService.IsDedicated(MessagingSettings.SurveyTemplateFrKey).Should().BeTrue();
        ConfigurationService.IsDedicated(" " + CostSettings.AllocationPerTestKey).Should().BeTrue();
        ConfigurationService.IsDedicated("Accounting.Journal.Sales").Should().BeFalse();
    }

    // ---- R8: one definition of the order count -----------------------------------------------------

    [Fact]
    public void R8_AnOrderCancelledWhole_IsNotCountedInTheStatisticsOrders_AsInSteering()
    {
        var day = new DateOnly(2026, 10, 1);
        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), Offset);
        var department = Guid.NewGuid();
        var test = Guid.NewGuid();

        ActivityItem Item(Guid order, LabOrderItemStatus status) =>
            new(order, Guid.NewGuid(), at, null, test, department, status, at, null, null, 50m, 0m, 50m);

        var items = new[]
        {
            Item(Guid.NewGuid(), LabOrderItemStatus.Validated),
            Item(Guid.NewGuid(), LabOrderItemStatus.Cancelled)
        };
        var labels = new StatisticsLabels(new Dictionary<Guid, (string, MultilingualText)>(),
                                          new Dictionary<Guid, (string, MultilingualText)>(),
                                          new Dictionary<Guid, MultilingualText>());

        var stats = StatisticsService.Compute(day, day, items, labels, new FinanceTotals(0m, 0m, 0m), Offset);

        stats.Summary.OrderCount.Should().Be(1);
        stats.Summary.PatientCount.Should().Be(1);
        stats.Summary.CancelledTestCount.Should().Be(1);
        stats.Days.Single().OrderCount.Should().Be(1);
    }
}
