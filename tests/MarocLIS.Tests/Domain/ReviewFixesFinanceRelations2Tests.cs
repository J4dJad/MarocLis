using FluentAssertions;
using MarocLIS.Application.Relations;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Relations;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>Review 7, relations: reminders under several rules, survey templates and survey results.</summary>
public sealed class ReviewFixesFinanceRelations2Tests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(1);
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 10, 0, 0, Offset);

    private static ReminderRule Rule(int months, Guid? test = null, Guid? panel = null) =>
        new(panel is null ? test ?? Guid.NewGuid() : null, panel, months,
            new MultilingualText(MessagingSettings.DefaultReminderFr)) { WindowDays = 60 };

    // ---- R11: a longer-delay rule on the same order still reminds -----------------------------------

    [Fact]
    public void R11_ARuleWithALongerDelay_StillRemindsAnOrderRemindedUnderAShorterOne()
    {
        // HbA1c at 3 months (sent in April), the diabetes panel containing it at 6 months (due now).
        var hba1c = Rule(3);
        var panel = Rule(6, panel: Guid.NewGuid());
        var patient = Guid.NewGuid();
        var order = Guid.NewGuid();
        var sources = new[] { new ReminderSourceRow(patient, order, At(2026, 4, 9), At(2026, 4, 10)) };
        var sent = new[] { new SentReminder(hba1c.Id, order, At(2026, 7, 10)) };

        var planned = ReminderPlanner.Plan(panel, sources, sent.Select(s => (s.RuleId, s.OrderId)).ToHashSet(),
                                           Today, Offset);
        planned.Should().ContainSingle().Which.DueOn.Should().Be(new DateOnly(2026, 10, 10));

        ReminderPlanner.OnePerPatient(planned, sent, new HashSet<Guid> { panel.Id }, Offset)
            .Should().ContainSingle().Which.RuleId.Should().Be(panel.Id);
    }

    [Fact]
    public void R11_ARuleDueAtTheSameTime_IsCoveredByTheReminderAlreadySent_EvenALittleEarlier()
    {
        var panel = Rule(3, panel: Guid.NewGuid());
        var analysis = Rule(3);
        var patient = Guid.NewGuid();
        var order = Guid.NewGuid();
        var sources = new[] { new ReminderSourceRow(patient, order, At(2026, 7, 9), At(2026, 7, 10)) };
        var planned = ReminderPlanner.Plan(analysis, sources, new HashSet<(Guid, Guid)>(), Today, Offset);

        // The panel reminder went out the day before yesterday, or within 30 days before the due date.
        ReminderPlanner.OnePerPatient(planned, [new SentReminder(panel.Id, order, At(2026, 10, 13))],
                                      new HashSet<Guid>(), Offset).Should().BeEmpty();
        ReminderPlanner.OnePerPatient(planned, [new SentReminder(panel.Id, order, At(2026, 9, 12))],
                                      new HashSet<Guid>(), Offset).Should().BeEmpty();
        // Long before: that was another reminder, this one is due.
        ReminderPlanner.OnePerPatient(planned, [new SentReminder(panel.Id, order, At(2026, 8, 1))],
                                      new HashSet<Guid>(), Offset).Should().ContainSingle();
    }

    // ---- R13: {lien} alone needs a survey address ---------------------------------------------------

    private const string LinkOnly = "{labo} : merci, donnez-nous votre avis : {lien}";

    [Fact]
    public void R13_AStoredLinkOnlyTemplate_WhoseAddressIsDropped_FallsBackToTheDefaultWithTheCode()
    {
        var settings = MessagingSettings.FromValues(new Dictionary<string, string?>
        {
            [MessagingSettings.SurveyTemplateFrKey] = LinkOnly,
            [MessagingSettings.SurveyTemplateArKey] = LinkOnly,
            [MessagingSettings.SurveyUrlKey] = "http://avis.labo.ma/{code}"
        });

        settings.SurveyUrl.Should().BeEmpty();
        settings.SurveyTemplateFr.Should().Be(MessagingSettings.Default.SurveyTemplateFr).And.Contain("{code}");
        settings.SurveyTemplateAr.Should().Be(MessagingSettings.Default.SurveyTemplateAr);

        // With a valid address the template is kept, and the link carries the code.
        var kept = MessagingSettings.FromValues(new Dictionary<string, string?>
        {
            [MessagingSettings.SurveyTemplateFrKey] = LinkOnly,
            [MessagingSettings.SurveyUrlKey] = "https://avis.labo.ma/{code}"
        });
        kept.SurveyTemplateFr.Should().Be(LinkOnly);
        kept.SurveyLink("ABC234").Should().Be("https://avis.labo.ma/ABC234");
    }

    [Fact]
    public void R13_HasCode_AcceptsTheLinkAloneOnlyWithAValidAddress()
    {
        MessagingSettings.HasCode(LinkOnly, "").Should().BeFalse();
        MessagingSettings.HasCode(LinkOnly, "http://avis.labo.ma").Should().BeFalse();
        MessagingSettings.HasCode(LinkOnly, "https://avis.labo.ma").Should().BeTrue();
        MessagingSettings.HasCode("Répondez {code} et une note", "").Should().BeTrue();
    }

    // ---- R14: each order counts once in the survey results ------------------------------------------

    [Fact]
    public void R14_ASurveyResentAfterAFailure_CountsOneInvitedOrder_Delivered()
    {
        var resent = Guid.NewGuid();
        var neverDelivered = Guid.NewGuid();
        var rows = new[]
        {
            new SurveyAnswerRow(At(2026, 10, 15), false, null, null, null, resent),
            new SurveyAnswerRow(At(2026, 10, 16), true, 4, null, At(2026, 10, 17), resent),
            new SurveyAnswerRow(At(2026, 10, 15), false, null, null, null, neverDelivered),
            new SurveyAnswerRow(At(2026, 10, 16), false, null, null, null, neverDelivered)
        };

        var results = PatientMessagingService.SummariseSurveys(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
                                                               rows, Offset);

        results.Invited.Should().Be(2);
        results.Delivered.Should().Be(1);
        results.Answered.Should().Be(1);
        results.ResponseRatePercent.Should().Be(100.0);
    }
}
