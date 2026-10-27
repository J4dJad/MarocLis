using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Relations;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Relations;
using MarocLIS.Web.Components.Screens.Relations;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Statistiques prescripteurs », « Visites et conventions » and « Rappels et enquêtes » in the
/// browser, over fakes of their queries and repositories and the services themselves: the ranking and
/// a row's detail, the amounts in dirhams, what each right offers, the refusals said beside their box,
/// the patients who may not be written to left out, and a campaign sent through a fake gateway only.
/// </summary>
public sealed class WebRelationsTests : WebScreenContext
{
    private readonly Fake<IPrescriberStatisticsQueries> _statistics = Fake<IPrescriberStatisticsQueries>.Create();
    private readonly Fake<IRelationsQueries> _relations = Fake<IRelationsQueries>.Create();
    private readonly Fake<IPatientMessagingQueries> _messaging = Fake<IPatientMessagingQueries>.Create();
    private readonly Fake<IRepository<DelegateVisit>> _visits = Fake<IRepository<DelegateVisit>>.Create();
    private readonly Fake<IRepository<Partnership>> _partnerships = Fake<IRepository<Partnership>>.Create();
    private readonly Fake<IRepository<ReminderRule>> _rules = Fake<IRepository<ReminderRule>>.Create();
    private readonly Fake<IRepository<PatientMessage>> _messages = Fake<IRepository<PatientMessage>>.Create();
    private readonly Fake<IMessageSender> _gateway = Fake<IMessageSender>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();

    private static readonly Guid Alami = Guid.NewGuid(), Bennani = Guid.NewGuid(), Tazi = Guid.NewGuid(), Delegate = Guid.NewGuid();

    public WebRelationsTests()
    {
        Services.AddSingleton(_statistics.Object);
        Services.AddSingleton(_relations.Object);
        Services.AddSingleton(_messaging.Object);
        Services.AddSingleton(_visits.Object);
        Services.AddSingleton(_partnerships.Object);
        Services.AddSingleton(Fake<IRepository<PartnershipDocument>>.Create().Object);
        Services.AddSingleton(_rules.Object);
        Services.AddSingleton(_messages.Object);
        Services.AddSingleton(Fake<IRepository<SurveyInvitation>>.Create().Object);
        Services.AddSingleton(Fake<IConfigurationWriter>.Create().Object);
        Services.AddSingleton(_gateway.Object);
        Services.AddSingleton(_work.Object);
        Services.AddScoped<PrescriberStatisticsService>();
        Services.AddScoped<RelationsService>();
        Services.AddScoped<PatientMessagingService>();

        _gateway.On("get_CanSendSms", _ => true)
                .On(nameof(IMessageSender.SendSmsAsync), args => ((string)args[0]!).EndsWith("99", StringComparison.Ordinal)
                    ? Result.Failure(Error.Validation("Messaging_SmsFailed", "HTTP 500 numero bloque"))
                    : Result.Success());
    }

    // ---- Statistiques prescripteurs ------------------------------------------------------------------

    private void Activity()
    {
        var site = Services.GetRequiredService<SessionContext>().User.SiteId;
        PrescriberOrderRow Order(Guid? prescriber, int day, decimal revenue) =>
            new(Guid.NewGuid(), site, prescriber, new DateTimeOffset(2026, 10, day, 9, 0, 0, TimeSpan.FromHours(1)), 3, revenue);
        _statistics.On(nameof(IPrescriberStatisticsQueries.GetOrdersAsync), args => ((DateTimeOffset)args[1]!).Month == 10 && ((DateTimeOffset)args[1]!).Year == 2026
                       ? (IReadOnlyList<PrescriberOrderRow>)[Order(Alami, 2, 1200m), Order(Alami, 9, 800m), Order(Bennani, 12, 450m)]
                       : ((DateTimeOffset)args[1]!).Month == 9
                           ? [Order(Alami, 1, 900m) with { RegisteredAt = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.FromHours(1)) },
                              Order(Tazi, 1, 600m) with { RegisteredAt = new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.FromHours(1)) }]
                           : [])
                   .On(nameof(IPrescriberStatisticsQueries.GetActivePrescriberIdsAsync), _ => (IReadOnlySet<Guid>)new HashSet<Guid> { Alami, Tazi })
                   .On(nameof(IPrescriberStatisticsQueries.GetLabelsAsync), _ => (IReadOnlyDictionary<Guid, PrescriberLabel>)new Dictionary<Guid, PrescriberLabel>
                   {
                       [Alami] = new(Alami, "Dr ALAMI Karim", "Clinique Al Amal", PrescriberKind.GeneralPractitioner, "Casablanca"),
                       [Bennani] = new(Bennani, "Dr BENNANI Salma", null, PrescriberKind.GeneralPractitioner, "Rabat"),
                       [Tazi] = new(Tazi, "Dr TAZI Omar", null, PrescriberKind.GeneralPractitioner, "Fès")
                   });
    }

    [Fact]
    public void The_ranking_shows_each_prescribers_files_and_revenue_in_dirhams_a_new_one_on_green()
    {
        SignIn(AppLanguage.French, Permissions.Relations.View);
        Activity();

        var page = Render<PrescriberStatisticsScreen>();

        page.WaitForAssertion(() => page.FindAll("[data-grid=prescriberstats] tbody tr").Should().HaveCount(3));
        var rows = page.FindAll("[data-grid=prescriberstats] tbody tr");
        rows[0].TextContent.Should().Contain("Dr ALAMI Karim").And.MatchRegex(@"2\s000,00\sDH");
        page.Find("[data-grid=prescriberstats] tbody tr.is-new").TextContent.Should().Contain("Dr BENNANI Salma");
        page.Find("[data-tile=revenue]").TextContent.Should().MatchRegex(@"2\s450,00\sDH");
        page.Find("[data-tile=new]").TextContent.Should().Contain("1");
        page.Find("[data-tile=lost]").TextContent.Should().Contain("1");
        page.Find("[data-tab=LostPrescribers]").Click();
        page.Find("[data-grid=prescriberstats] tbody").TextContent.Should().Contain("Dr TAZI Omar");
    }

    [Fact]
    public void A_prescribers_months_open_over_the_table_and_leave_as_csv()
    {
        SignIn(AppLanguage.French, Permissions.Relations.View);
        Activity();
        var page = Render<PrescriberStatisticsScreen>();
        page.WaitForAssertion(() => page.FindAll("[data-grid=prescriberstats] tbody tr").Should().HaveCount(3));

        page.FindAll("[data-grid=prescriberstats] tbody tr")[0].DoubleClick();

        var drill = page.Find("[data-modal=prescriber-drill]");
        drill.TextContent.Should().Contain("Dr ALAMI Karim").And.Contain("10/2026");
        page.Find("[data-modal=prescriber-drill] [data-action='Statistics.ExportCsv']").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                    && (string)i.Arguments[0]! == "prescripteurs-par-prescripteur-20261001-20261018-detail.csv");
        page.Find("[data-modal=prescriber-drill] [data-action='Action.Close']").Click();

        page.Find(".action-bar [data-action='Statistics.ExportCsv']").Click();
        JSInterop.Invocations.Should().Contain(i => (string?)i.Arguments.FirstOrDefault() == "prescripteurs-par-prescripteur-20261001-20261018.csv");
    }

    [Fact]
    public void A_reversed_period_is_refused_beside_its_box()
    {
        SignIn(AppLanguage.French, Permissions.Steering.View);
        Activity();
        var page = Render<PrescriberStatisticsScreen>();

        page.Find("[data-field=prescriberstats-from]").Change("2026-10-20");
        page.Find("[data-field=prescriberstats-period]").Change("2026-10-02");
        page.Find(".action-bar [data-action='Statistics.Compute']").Click();

        page.WaitForAssertion(() => page.Find("[data-refusal=prescriberstats-period]").Should().NotBeNull());
    }

    // ---- Visites et conventions ----------------------------------------------------------------------

    private void Relations()
    {
        var visit = new DelegateVisitRow(Guid.NewGuid(), new DateOnly(2026, 10, 12), Alami, "Dr ALAMI Karim", "Clinique Al Amal", Delegate,
                                         "Nadia Fassi", VisitPurpose.Complaint, "Deux patientes renvoyées pour un jeûne mal indiqué.",
                                         new DateOnly(2026, 10, 15), true);
        _relations.On(nameof(IRelationsQueries.GetVisitsAsync), _ => (IReadOnlyList<DelegateVisitRow>)[visit])
                  .On(nameof(IRelationsQueries.GetDueVisitsAsync), _ => (IReadOnlyList<DelegateVisitRow>)[visit])
                  .On(nameof(IRelationsQueries.GetOptionsAsync), _ => new RelationOptions(
                      [new RelationLookup(Alami, "Dr ALAMI Karim"), new RelationLookup(Bennani, "Dr BENNANI Salma")],
                      [new RelationLookup(Delegate, "Nadia Fassi")]))
                  .On(nameof(IRelationsQueries.GetPartnershipsAsync), _ => (IReadOnlyList<PartnershipRow>)
                  [
                      new(Guid.NewGuid(), "Lesieur Cristal — médecine du travail", PartnerKind.Company, PartnershipKind.OccupationalHealth, "MT-2025-007",
                          new DateOnly(2025, 11, 26), new DateOnly(2026, 11, 19), true, true, true, 15m, "Dr Hicham Alaoui", "0661 20 33 48", null)
                  ]);
    }

    [Fact]
    public void A_visit_whose_follow_up_has_come_is_flagged_and_its_file_reads_in_full()
    {
        SignIn(AppLanguage.French, Permissions.Relations.View);
        Relations();

        var page = Render<RelationsScreen>();

        page.WaitForAssertion(() => page.FindAll("[data-grid=visits] tbody tr").Should().ContainSingle());
        page.Find("[data-grid=visits] [data-state=next]").ClassList.Should().Contain("abnormal");
        page.Find("[data-state=file] [data-state=notes]").TextContent.Should().Be("Deux patientes renvoyées pour un jeûne mal indiqué.");
        page.Find("[data-tab=Partnerships]").Click();
        page.WaitForAssertion(() => page.Find("[data-grid=partnerships] tbody tr").GetAttribute("data-state").Should().Be("EndingSoon"));
        page.Find("[data-grid=partnerships] tbody tr").ClassList.Should().Contain("is-abnormal");
    }

    [Fact]
    public void Who_only_sees_the_relations_records_nothing()
    {
        SignIn(AppLanguage.French, Permissions.Relations.View);
        Relations();

        var page = Render<RelationsScreen>();

        page.FindAll(".action-bar [data-action='Relations.NewVisit']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Action.Edit']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Action.Delete']").Should().BeEmpty();
        page.FindAll(".action-bar [data-action='Statistics.ExportCsv']").Should().ContainSingle();
    }

    [Fact]
    public void A_visit_without_its_prescriber_is_refused_beside_the_box_then_saved()
    {
        SignIn(AppLanguage.French, Permissions.Relations.View, Permissions.Relations.Manage);
        Relations();
        var page = Render<RelationsScreen>();

        page.Find(".action-bar [data-action='Relations.NewVisit']").Click();
        page.Find("[data-modal=visit] .modal-footer .btn-primary").Click();

        page.Find("[data-refusal=visit-prescriber]").TextContent.Should().NotBeNullOrWhiteSpace();
        _visits.CallsTo(nameof(IRepository<DelegateVisit>.AddAsync)).Should().Be(0);

        page.Find("[data-field=visit-prescriber]").Change(Bennani.ToString());
        page.Find("[data-field=visit-notes]").Input("Présentation du nouveau catalogue des sérologies.");
        page.Find("[data-field=visit-next]").Change("2026-11-20");
        page.Find("[data-modal=visit] .modal-footer .btn-primary").Click();

        page.WaitForAssertion(() => page.FindAll("[data-modal=visit]").Should().BeEmpty());
        var added = (DelegateVisit)_visits.Calls.Single(c => c.Method == nameof(IRepository<DelegateVisit>.AddAsync)).Arguments[0]!;
        added.PrescriberId.Should().Be(Bennani);
        added.DelegateUserId.Should().Be(Services.GetRequiredService<SessionContext>().User.UserId, "a new visit is the operator's own");
        added.NextVisitOn.Should().Be(new DateOnly(2026, 11, 20));
    }

    [Fact]
    public void A_convention_with_a_wrong_ice_is_refused_beside_the_box()
    {
        SignIn(AppLanguage.French, Permissions.Relations.View, Permissions.Relations.Manage);
        Relations();
        var page = Render<RelationsScreen>();
        page.Find("[data-tab=Partnerships]").Click();

        page.Find(".action-bar [data-action='Relations.NewPartnership']").Click();
        page.WaitForAssertion(() => page.Find("[data-modal=partnership]").Should().NotBeNull());
        page.Find("[data-field=partner-name]").Input("Clinique Les Orangers");
        page.Find("[data-field=partner-ice]").Input("12345");
        page.Find("[data-modal=partnership] .modal-footer .btn-primary").Click();

        page.Find("[data-refusal=partner-ice]").Should().NotBeNull();
        _partnerships.CallsTo(nameof(IRepository<Partnership>.AddAsync)).Should().Be(0);
    }

    // ---- Rappels et enquêtes -------------------------------------------------------------------------

    private static readonly Guid Glycaemia = Guid.NewGuid();
    private static readonly Guid Salma = Guid.NewGuid(), Yassine = Guid.NewGuid(), Imane = Guid.NewGuid(), Karim = Guid.NewGuid();

    /// <summary>Each patient's order of the glycaemia, the same at each reading (the send reads the list again).</summary>
    private static readonly Dictionary<Guid, Guid> GlycaemiaOrders = new() { [Salma] = Guid.NewGuid(), [Yassine] = Guid.NewGuid(), [Imane] = Guid.NewGuid(), [Karim] = Guid.NewGuid() };

    private void Campaign()
    {
        var site = Services.GetRequiredService<SessionContext>().User.SiteId;
        var rule = new ReminderRule(Glycaemia, null, 3,
                                    new MultilingualText(MessagingSettings.DefaultReminderFr, MessagingSettings.DefaultReminderAr));
        var validated = new DateTimeOffset(2026, 7, 10, 11, 0, 0, TimeSpan.FromHours(1));
        ReminderSourceRow Source(Guid patient) => new(patient, GlycaemiaOrders[patient], validated.AddDays(-1), validated, site);

        _rules.On(nameof(IRepository<ReminderRule>.ListAsync), _ => (IReadOnlyList<ReminderRule>)[rule]);
        _messaging.On(nameof(IPatientMessagingQueries.GetTargetsAsync), _ => (IReadOnlyList<ReminderTarget>)
                      [new ReminderTarget(Glycaemia, false, "GLY", new MultilingualText("Glycémie à jeun", "سكر الدم على الصيام"))])
                  .On(nameof(IPatientMessagingQueries.GetSignatureAsync), _ => new MessageSignature("Laboratoire Al Massira", "0522 25 01 01"))
                  .On(nameof(IPatientMessagingQueries.GetReminderSourcesAsync), _ => (IReadOnlyList<ReminderSourceRow>)
                      [Source(Salma), Source(Yassine), Source(Imane), Source(Karim)])
                  .On(nameof(IPatientMessagingQueries.GetRecipientsAsync), _ => (IReadOnlyDictionary<Guid, MessageRecipient>)new Dictionary<Guid, MessageRecipient>
                  {
                      [Salma] = new(Salma, "OUAZZANI Salma", "0661000003", AppLanguage.French, true, false),
                      [Yassine] = new(Yassine, "IDRISSI Yassine", "0661234599", AppLanguage.French, true, false),
                      // Opposed to any use of the data beyond the care: never written to.
                      [Imane] = new(Imane, "TAZI Imane", "0661000004", AppLanguage.French, true, true),
                      // No follow-up consent.
                      [Karim] = new(Karim, "BENNIS Karim", "0661000005", AppLanguage.French, false, false)
                  })
                  .On(nameof(IPatientMessagingQueries.GetRulesAsync), _ => (IReadOnlyList<ReminderRuleRow>)
                      [new ReminderRuleRow(rule.Id, Glycaemia, null, "GLY", new MultilingualText("Glycémie à jeun"), 3, 60, rule.Template, true)]);
    }

    [Fact]
    public void The_reminders_list_only_the_patients_who_may_be_written_to_each_text_as_sent()
    {
        SignIn(AppLanguage.French, Permissions.Messaging.Campaign);
        Campaign();

        var page = Render<PatientMessagingScreen>();

        page.WaitForAssertion(() => page.FindAll("[data-grid=reminders] tbody tr").Should().HaveCount(2));
        page.Find("[data-grid=reminders] tbody").TextContent.Should().NotContain("TAZI Imane").And.NotContain("BENNIS Karim");
        page.Find("[data-grid=reminders] tbody").TextContent.Should().Contain("Bonjour OUAZZANI Salma, votre dernier dosage Glycémie à jeun date du 10/07/2026");
        page.FindAll("[data-grid=reminders] [data-field=tick]").Should().OnlyContain(c => c.HasAttribute("checked"));
        page.Find("[data-state=notice]").TextContent.Should().Contain("consentement");
        DataAudit.Entries.Should().Contain(e => e.Context == "Relations/ReminderPreview" && e.Count == 2);
    }

    [Fact]
    public void Only_the_patients_ticked_are_sent_once_confirmed_and_a_refusal_of_the_gateway_is_logged()
    {
        SignIn(AppLanguage.French, Permissions.Messaging.Campaign);
        Campaign();
        var page = Render<PatientMessagingScreen>();
        page.WaitForAssertion(() => page.FindAll("[data-grid=reminders] tbody tr").Should().HaveCount(2));

        page.Find("[data-grid=reminders] tr[data-row='OUAZZANI Salma'] [data-field=tick]").Change(false);
        page.Find(".action-bar [data-action='Messaging.SendSelected']").Click();
        page.Find("[data-modal=confirm]").TextContent.Should().Contain("Envoyer 1 SMS maintenant ?");
        _gateway.CallsTo(nameof(IMessageSender.SendSmsAsync)).Should().Be(0, "nothing leaves before the confirmation");
        page.Find("[data-modal=confirm] [data-action='Action.Yes']").Click();

        page.WaitForAssertion(() => _gateway.CallsTo(nameof(IMessageSender.SendSmsAsync)).Should().Be(1));
        _gateway.Calls.Single(c => c.Method == nameof(IMessageSender.SendSmsAsync)).Arguments[0].Should().Be("0661234599");
        var logged = (PatientMessage)_messages.Calls.Single(c => c.Method == nameof(IRepository<PatientMessage>.AddAsync)).Arguments[0]!;
        logged.Status.Should().Be(PatientMessageStatus.Failed);
        page.WaitForAssertion(() => page.Find(".action-bar-status").TextContent.Should().Contain("0 envoyé, 1 en échec, 0 ignoré."));
    }

    [Fact]
    public void Without_a_gateway_the_lists_are_previewed_but_nothing_can_be_sent()
    {
        _gateway.On("get_CanSendSms", _ => false);
        SignIn(AppLanguage.French, Permissions.Messaging.Campaign);
        Campaign();

        var page = Render<PatientMessagingScreen>();

        page.WaitForAssertion(() => page.FindAll("[data-grid=reminders] tbody tr").Should().HaveCount(2));
        page.Find(".action-bar [data-action='Messaging.SendSelected']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-state=notice]").TextContent.Should().Contain("Aucune passerelle SMS");
    }

    [Fact]
    public void A_rules_delay_out_of_range_is_refused_beside_its_box_with_both_limits()
    {
        SignIn(AppLanguage.French, Permissions.Messaging.Campaign);
        Campaign();
        var page = Render<PatientMessagingScreen>();
        page.Find("[data-tab=Rules]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-grid=rules] tbody tr").Should().ContainSingle());

        page.Find(".action-bar [data-action='Messaging.NewRule']").Click();
        page.WaitForAssertion(() => page.Find("[data-modal=reminder-rule]").Should().NotBeNull());
        page.Find("[data-field=rule-target]").Change(Glycaemia.ToString());
        page.Find("[data-field=rule-delay]").Input("99");
        page.Find("[data-modal=reminder-rule] .modal-footer .btn-primary").Click();

        page.Find("[data-refusal=rule-delay]").TextContent.Should().Be("Le délai doit être compris entre 1 et 60 mois.");
        _rules.CallsTo(nameof(IRepository<ReminderRule>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void The_log_shows_a_refused_message_in_red_with_the_gateways_reason_and_its_export_is_recorded()
    {
        SignIn(AppLanguage.French, Permissions.Messaging.Campaign);
        Campaign();
        _messaging.On(nameof(IPatientMessagingQueries.GetMessagesAsync), _ => (IReadOnlyList<PatientMessageRow>)
        [
            new(new DateTimeOffset(2026, 10, 16, 10, 5, 0, TimeSpan.FromHours(1)), PatientMessageKind.BilanReminder, "IDRISSI Yassine", "0661234599",
                PatientMessageStatus.Failed, "Messaging_SmsFailed HTTP 500 numero bloque", "Bonjour IDRISSI Yassine…"),
            new(new DateTimeOffset(2026, 10, 16, 10, 5, 0, TimeSpan.FromHours(1)), PatientMessageKind.SatisfactionSurvey, "OUAZZANI Salma", "0661000003",
                PatientMessageStatus.Sent, null, "Merci de votre visite…")
        ]);
        var page = Render<PatientMessagingScreen>();

        page.Find("[data-tab=Log]").Click();

        page.WaitForAssertion(() => page.FindAll("[data-grid=log] tbody tr").Should().HaveCount(2));
        var failed = page.Find("[data-grid=log] tr[data-state=Failed]");
        failed.QuerySelector(".pill")!.TextContent.Should().Contain("Échec");
        failed.QuerySelector(".messaging-text")!.ClassList.Should().Contain("critical");
        failed.TextContent.Should().Contain("HTTP 500 numero bloque");

        page.Find(".action-bar [data-action='Statistics.ExportCsv']").Click();
        page.WaitForAssertion(() => DataAudit.Entries.Should().Contain(e => e.Context == "Relations/MessageLog" && e.Count == 2));
    }

    [Fact]
    public void The_arabic_screen_reads_right_to_left_with_its_tabs_in_arabic()
    {
        SignIn(AppLanguage.Arabic, Permissions.Messaging.Campaign);
        Campaign();

        var page = Render<PatientMessagingScreen>();

        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
        page.Find("[data-tab=Reminders]").TextContent.Should().MatchRegex("[؀-ۿ]");
        page.Find("[data-tab=Rules]").TextContent.Should().NotContain("Règles");
    }
}
