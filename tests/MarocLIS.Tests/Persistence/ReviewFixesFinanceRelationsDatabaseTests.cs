using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Relations;
using MarocLIS.Application.Statistics;
using MarocLIS.Application.Steering;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
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

/// <summary>A scratch database of its own for the review 6 relations fixes, created from the model.</summary>
public sealed class FinanceRelationsScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_f6rel_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public FinanceRelationsScratchDatabase()
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

/// <summary>Review 6 (relations, messaging, steering) and the security findings S1, S2, S6, against SQL Server.</summary>
public sealed class ReviewFixesFinanceRelationsDatabaseTests(FinanceRelationsScratchDatabase database)
    : IClassFixture<FinanceRelationsScratchDatabase>
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();
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

    /// <summary>An SMS gateway that checks the message is already logged when it is handed over.</summary>
    private sealed class RecordingSender(FinanceRelationsScratchDatabase database) : IMessageSender
    {
        public HashSet<string> Refuse { get; } = [];
        public List<(string Phone, bool LoggedBefore)> Sent { get; } = [];

        public bool CanSendEmail => false;
        public bool CanSendSms => true;

        public Task<Result> SendEmailAsync(EmailMessage message, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public async Task<Result> SendSmsAsync(string phoneNumber, string text, CancellationToken ct = default)
        {
            await using var context = database.Context();
            var logged = await context.PatientMessages.IgnoreQueryFilters().AnyAsync(m => m.PhoneNumber == phoneNumber, ct);
            Sent.Add((phoneNumber, logged));
            return Refuse.Contains(phoneNumber)
                ? Result.Failure(Error.Validation("Sms_GatewayRefused"))
                : Result.Success();
        }
    }

    private ServiceProvider Services(RecordingSender? sender = null)
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
        if (sender is not null) services.AddSingleton<IMessageSender>(sender);
        services.AddIsolatedQueries<IStatisticsQueries, StatisticsQueries>();
        services.AddIsolatedQueries<IPrescriberStatisticsQueries, PrescriberStatisticsQueries>();
        services.AddIsolatedQueries<IRelationsQueries, RelationsQueries>();
        services.AddIsolatedQueries<IPatientMessagingQueries, PatientMessagingQueries>();
        services.AddIsolatedQueries<ICostQueries, CostQueries>();
        services.AddIsolatedQueries<ISteeringQueries, SteeringQueries>();
        services.AddIsolatedQueries<IAdministrationQueries, AdministrationQueries>();
        return services.BuildServiceProvider();
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static DateTimeOffset At(DateOnly day) => new(day.ToDateTime(new TimeOnly(10, 0)), Offset);

    private static string Mobile() => "06" + Random.Shared.Next(10_000_000, 99_999_999);

    private static Patient NewPatient(string name) =>
        new(Unique("P"), name, "Test", new DateOnly(1970, 1, 1), Gender.Female, SiteA) { MobilePhone = Mobile() };

    private static PatientConsent FollowUp(Patient p) =>
        new(p.Id, ConsentPurpose.FollowUpMessages, ConsentChannel.WrittenForm, At(Today.AddYears(-1)), Guid.NewGuid());

    private static LabOrder Order(Patient p, Guid site, DateOnly day, Guid test, Guid? panel = null)
    {
        var order = new LabOrder(Unique("O"), p.Id, site, At(day), Guid.NewGuid());
        order.AddItem(new LabOrderItem(test, OrderPriority.Routine) { ChargedPrice = 100m, TestPanelId = panel });
        return order;
    }

    private static async Task Validate(LisDbContext context, IEnumerable<LabOrder> orders)
    {
        foreach (var order in orders)
        {
            var at = order.RegisteredAt;
            await context.LabOrderItems.Where(i => i.LabOrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LabOrderItemStatus.Validated)
                                          .SetProperty(i => i.ValidatedAt, at));
        }
    }

    private static AnalysisTest Analysis(string name) =>
        new(Unique("T"), new MultilingualText(name), Guid.NewGuid(), Guid.NewGuid());

    private static ReminderRule SixMonths(Guid? test, Guid? panel = null) =>
        new(test, panel, 6, new MultilingualText(MessagingSettings.DefaultReminderFr));

    [SqlServerFact]
    public async Task Reminders_honour_the_objection_one_per_order_merges_and_other_sites()
    {
        var hba1c = Analysis("HbA1c");
        var panel = new TestPanel(Unique("B"), new MultilingualText("Bilan diabète"));
        var due = Today.AddMonths(-6).AddDays(-3);

        // R1: consent given, then objection to secondary use.
        var objecting = NewPatient("OBJECTION");
        objecting.OptedOutOfSecondaryUse = true;
        // R2: the analysis inside the panel, both with a rule.
        var panelPatient = NewPatient("PANEL");
        // R5: the survivor's old result, and a recent one filed under the duplicate merged into it.
        var survivor = NewPatient("SURVIVANT");
        var duplicate = NewPatient("DOUBLON");
        duplicate.MarkMergedInto(survivor.Id);
        // R6: done at A, done again at B since.
        var traveller = NewPatient("AUTRESITE");

        var orders = new List<LabOrder>
        {
            Order(objecting, SiteA, due, hba1c.Id),
            Order(panelPatient, SiteA, due, hba1c.Id, panel.Id),
            Order(survivor, SiteA, due, hba1c.Id),
            Order(duplicate, SiteA, Today.AddMonths(-2), hba1c.Id),
            Order(traveller, SiteA, due, hba1c.Id),
            Order(traveller, SiteB, Today.AddMonths(-1), hba1c.Id)
        };

        await using (var context = database.Context())
        {
            context.AnalysisTests.Add(hba1c);
            context.TestPanels.Add(panel);
            context.TestPanelItems.Add(new TestPanelItem(panel.Id, hba1c.Id));
            context.Patients.AddRange(objecting, panelPatient, survivor, duplicate, traveller);
            context.PatientConsents.AddRange(new[] { objecting, panelPatient, survivor, traveller }.Select(FollowUp));
            context.LabOrders.AddRange(orders);
            context.ReminderRules.AddRange(SixMonths(hba1c.Id), SixMonths(null, panel.Id));
            await context.SaveChangesAsync();
            await Validate(context, orders);
        }

        await using var services = Services();
        using var scope = services.CreateScope();
        var preview = await scope.ServiceProvider.GetRequiredService<PatientMessagingService>().PreviewRemindersAsync();
        preview.IsSuccess.Should().BeTrue();
        var ours = preview.Value.Where(c => new[] { objecting.Id, panelPatient.Id, survivor.Id, traveller.Id }
                                               .Contains(c.PatientId)).ToList();

        ours.Should().ContainSingle("only the panel patient is due: one SMS, not one per rule");
        ours[0].PatientId.Should().Be(panelPatient.Id);
        ours[0].Analysis.Should().Be("Bilan diabète");
    }

    [SqlServerFact]
    public async Task Reminders_never_name_an_analysis_under_a_specific_consent()
    {
        var hiv = Analysis("Charge virale VIH-1");
        hiv.ConsentRequirement = ConsentRequirement.Hiv;
        var panel = new TestPanel(Unique("B"), new MultilingualText("Suivi VIH"));
        var patient = NewPatient("VIH");
        var order = Order(patient, SiteA, Today.AddMonths(-6).AddDays(-3), hiv.Id);

        await using (var context = database.Context())
        {
            context.AnalysisTests.Add(hiv);
            context.TestPanels.Add(panel);
            context.TestPanelItems.Add(new TestPanelItem(panel.Id, hiv.Id));
            context.Patients.Add(patient);
            context.PatientConsents.Add(FollowUp(patient));
            context.LabOrders.Add(order);
            // A rule saved before the analysis was put under the specific consent.
            context.ReminderRules.Add(SixMonths(hiv.Id));
            await context.SaveChangesAsync();
            await Validate(context, [order]);
        }

        await using var services = Services();
        using var scope = services.CreateScope();
        var messaging = scope.ServiceProvider.GetRequiredService<PatientMessagingService>();

        (await messaging.GetTargetsAsync()).Value.Should().NotContain(t => t.Id == hiv.Id || t.Id == panel.Id);

        var refused = await messaging.SaveRuleAsync(new SaveReminderRuleRequest
        {
            TestPanelId = panel.Id,
            Template = new MultilingualText(MessagingSettings.DefaultReminderFr)
        });
        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Reminder_SpecificConsentAnalysis");

        (await messaging.PreviewRemindersAsync()).Value.Should().NotContain(c => c.PatientId == patient.Id);
    }

    [SqlServerFact]
    public async Task Sends_are_logged_before_the_gateway_and_a_failed_survey_can_be_sent_again()
    {
        var patient = NewPatient("ENQUETE");
        var order = new LabOrder(Unique("O"), patient.Id, SiteA, At(Today.AddDays(-3)), Guid.NewGuid());

        await using (var context = database.Context())
        {
            context.Patients.Add(patient);
            context.PatientConsents.Add(FollowUp(patient));
            context.LabOrders.Add(order);
            await context.SaveChangesAsync();
            await context.LabOrders.Where(o => o.Id == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, LabOrderStatus.Reported)
                                          .SetProperty(o => o.FirstReportedAt, At(Today.AddDays(-2))));
        }

        var sender = new RecordingSender(database);
        sender.Refuse.Add(patient.MobilePhone!);

        await using var services = Services(sender);
        using (var scope = services.CreateScope())
        {
            var messaging = scope.ServiceProvider.GetRequiredService<PatientMessagingService>();
            var outcome = await messaging.SendSurveysAsync(Today.AddDays(-7), Today, [order.Id]);
            outcome.Value.Failed.Should().Be(1);
        }

        sender.Sent.Should().ContainSingle().Which.LoggedBefore.Should().BeTrue();

        await using (var context = database.Context())
        {
            (await context.PatientMessages.SingleAsync(m => m.PatientId == patient.Id)).Status
                .Should().Be(PatientMessageStatus.Failed);
            (await context.Set<SurveyInvitation>().IgnoreQueryFilters().SingleAsync(s => s.LabOrderId == order.Id))
                .Delivered.Should().BeFalse();
        }

        // R4: never received, so still proposed, and sent once the gateway accepts it.
        sender.Refuse.Clear();
        using (var scope = services.CreateScope())
        {
            var messaging = scope.ServiceProvider.GetRequiredService<PatientMessagingService>();
            (await messaging.PreviewSurveysAsync(Today.AddDays(-7), Today)).Value
                .Should().Contain(c => c.OrderId == order.Id);
            (await messaging.SendSurveysAsync(Today.AddDays(-7), Today, [order.Id])).Value.Sent.Should().Be(1);
            (await messaging.PreviewSurveysAsync(Today.AddDays(-7), Today)).Value
                .Should().NotContain(c => c.OrderId == order.Id);

            // R10: the export of a messaging list is in the data-access log.
            (await messaging.LogExportAsync("Relations/MessageLog", 2)).IsSuccess.Should().BeTrue();
        }

        await using (var context = database.Context())
            (await context.DataAccessLogs.AnyAsync(l => l.Kind == DataAccessKind.Export
                                                        && l.Context == "Relations/MessageLog" && l.RecordCount == 2))
                .Should().BeTrue();
    }

    [SqlServerFact]
    public async Task Self_referral_inactive_links_other_site_files_and_reserved_settings()
    {
        var test = Analysis("Glycémie");
        var walkIn = new Prescriber("SANS ORDONNANCE", "", PrescriberKind.SelfReferral);
        var retired = new Prescriber("RETRAITE", "Ali", PrescriberKind.GeneralPractitioner) { IsActive = false };
        var patient = NewPatient("STAT");
        var order = Order(patient, SiteA, Today.AddDays(-2), test.Id);
        order.PrescriberId = walkIn.Id;
        var ours = new Partnership(SiteA, "Clinique A", PartnerKind.Clinic, PartnershipKind.CareConvention, Today.AddYears(-1));
        var theirs = new Partnership(SiteB, "Clinique B", PartnerKind.Clinic, PartnershipKind.CareConvention, Today.AddYears(-1));

        await using (var context = database.Context())
        {
            context.AnalysisTests.Add(test);
            context.Prescribers.AddRange(walkIn, retired);
            context.Patients.Add(patient);
            context.LabOrders.Add(order);
            context.Partnerships.AddRange(ours, theirs);
            context.PartnershipDocuments.AddRange(
                new PartnershipDocument(ours.Id, "convention.pdf", "application/pdf", [1, 2, 3], At(Today), Guid.NewGuid()),
                new PartnershipDocument(theirs.Id, "convention.pdf", "application/pdf", [4, 5, 6], At(Today), Guid.NewGuid()));
            await context.SaveChangesAsync();
        }

        await using var services = Services();
        using var scope = services.CreateScope();

        // R7: the walk-in record is "no prescriber".
        var statistics = await scope.ServiceProvider.GetRequiredService<PrescriberStatisticsService>()
            .GetAsync(Today.AddDays(-7), Today);
        statistics.Value.Prescribers.Should().NotContain(p => p.PrescriberId == walkIn.Id);
        statistics.Value.Prescribers.Should().Contain(p => p.PrescriberId == null && p.Current.Orders >= 1);
        var steering = await scope.ServiceProvider.GetRequiredService<SteeringService>().GetAsync(Today.AddDays(-7), Today);
        steering.Value.ByPrescriber.Should().NotContain(r => r.Key == walkIn.Id.ToString());

        // R9: the inactive prescriber a record names stays in the list, flagged.
        var relations = scope.ServiceProvider.GetRequiredService<RelationsService>();
        (await relations.GetOptionsAsync()).Value.Prescribers.Should().NotContain(p => p.Id == retired.Id);
        (await relations.GetOptionsAsync([retired.Id])).Value.Prescribers
            .Should().ContainSingle(p => p.Id == retired.Id).Which.IsActive.Should().BeFalse();

        // S6: another site's convention file is not served.
        (await relations.GetDocumentAsync(ours.Id)).IsSuccess.Should().BeTrue();
        var other = await relations.GetDocumentAsync(theirs.Id);
        other.IsFailure.Should().BeTrue();
        other.FirstError.ResourceKey.Should().Be("Partnership_NotFound");

        // R3: the generic settings screen neither lists nor writes the messaging and cost keys.
        var configuration = scope.ServiceProvider.GetRequiredService<ConfigurationService>();
        var refused = await configuration.SetSettingAsync(MessagingSettings.SurveyTemplateFrKey, "-10 % répondez {code}");
        refused.IsFailure.Should().BeTrue();
        refused.FirstError.ResourceKey.Should().Be("Setting_ManagedElsewhere");
        (await configuration.SetSettingAsync(CostSettings.AllocationPerTestKey, "999999")).IsFailure.Should().BeTrue();
        (await configuration.GetSettingsAsync()).Value.Should().NotContain(s => ConfigurationService.IsDedicated(s.Key));
    }
}
