using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Web.Components.Screens.Quality;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Rules = MarocLIS.Tests.Desktop.EqaScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Évaluation externe » in the browser: the year's participations with their score, a figure
/// that is no number or a deviation of zero refused on its own box, a result out of tolerance that
/// opens its non-conformity and says which, a round registered with its analytes, each step kept by
/// the right the workstation asks — all through the workstation's EqaService.
/// </summary>
public sealed class WebEqaTests : WebScreenContext
{
    private readonly Fake<IEqaQueries> _queries = Fake<IEqaQueries>.Create();
    private readonly Fake<IRepository<EqaParticipation>> _participations = Fake<IRepository<EqaParticipation>>.Create();
    private readonly Fake<IRepository<NonConformity>> _files = Fake<IRepository<NonConformity>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Fake<ISequenceAllocator> _sequences = Fake<ISequenceAllocator>.Create();

    public WebEqaTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_participations.Object);
        Services.AddSingleton(_files.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(_sequences.Object);
        Services.AddScoped<EqaService>();
    }

    private static readonly string[] Biologist = [Permissions.Quality.View, Permissions.Quality.EnterQc, Permissions.Quality.ReviewQc];
    private static readonly string[] Technician = [Permissions.Quality.View, Permissions.Quality.EnterQc];
    private static readonly string[] Reader = [Permissions.Quality.View];

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    /// <summary>A glucose sent and awaiting its verdict, a creatinine out of tolerance already scored, a CRP not sent.</summary>
    private (EqaRow Sent, EqaParticipation Participation) Year()
    {
        var participation = new EqaParticipation("ProBioQual Biochimie", "2026-03", Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 9, 28))
        {
            ReportedValue = 6.20m, SubmittedOn = new DateOnly(2026, 10, 8)
        };
        var sent = Rules.Row(reported: 6.20m, submitted: new DateOnly(2026, 10, 8)) with { Id = participation.Id };
        var failed = Rules.Row(EqaOutcome.Unsatisfactory, reported: 14.1m, assigned: 11.8m, peerMean: 11.9m, peerSd: 0.6m, z: 3.67m,
                               bias: 19.49m, nc: "NC-2026-0010", code: "CREA", name: "Créatinine");
        var missing = Rules.Row(overdue: true, code: "CRP", name: "Protéine C réactive");

        _queries.On(nameof(IEqaQueries.GetParticipationsAsync), _ => new[] { sent, failed, missing });
        _queries.On(nameof(IEqaQueries.GetParameterCodeAsync), _ => "GLU");
        _participations.On(nameof(IRepository<EqaParticipation>.GetByIdAsync), _ => participation);
        _sequences.On(nameof(ISequenceAllocator.NextAsync), _ => Result.Success("NC-2026-0013"));
        return (sent, participation);
    }

    [Fact]
    public void TheYearsParticipations_ShowTheirScore_TheOutOfToleranceInRed_AndTheirFile()
    {
        SignIn(AppLanguage.French, Biologist);
        Year();

        var page = Render<ExternalQuality>();

        page.FindAll("[data-grid=participations] tbody tr").Should().HaveCount(3);
        _queries.Calls.First(c => c.Method == nameof(IEqaQueries.GetParticipationsAsync)).Arguments[1].Should().Be(2026);
        var failed = page.Find("[data-row='2026-03-CREA']");
        failed.TextContent.Should().Contain("3,67").And.Contain("NC-2026-0010").And.Contain("Non satisfaisant");
        failed.QuerySelector(".critical")!.TextContent.Should().Contain("3,67");
        page.Find("[data-row='2026-03-CRP'] .abnormal").Should().NotBeNull("an answer past its deadline is said in the warning's ink");

        page.Find("[data-row='2026-03-CREA']").Click();
        page.Find("[data-card=detail]").TextContent.Should().Contain("Créatinine").And.Contain("ProBioQual Biochimie");

        page.Find("[data-chip=only-pending]").Click();
        _queries.Calls.Last(c => c.Method == nameof(IEqaQueries.GetParticipationsAsync)).Arguments[2].Should().Be(true);
    }

    [Fact]
    public void AFigureThatIsNoNumber_AndADeviationOfZero_AreRefusedOnTheirBox_AndNothingIsScored()
    {
        SignIn(AppLanguage.French, Biologist);
        var (_, participation) = Year();

        var page = Render<ExternalQuality>();
        page.Find("[data-action='Eqa.RecordEvaluation']").Click();
        page.Find($"[data-field={EqaScreenRules.AssignedField}]").Input("5,0a");
        page.Find("[data-modal=eqa-evaluation] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={EqaScreenRules.AssignedField}]").TextContent.Should().Contain("« 5,0a » n'est pas une valeur numérique valide");

        page.Find($"[data-field={EqaScreenRules.AssignedField}]").Input("5,0");
        page.Find($"[data-field={EqaScreenRules.PeerSdField}]").Input("0");
        page.Find("[data-modal=eqa-evaluation] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={EqaScreenRules.PeerSdField}]").TextContent.Should().Contain("strictement positif");
        participation.ZScore.Should().BeNull();
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    [Fact]
    public void AResultOutOfTolerance_OpensItsNonConformity_AndTheScreenSaysWhich()
    {
        SignIn(AppLanguage.French, Biologist);
        var (_, participation) = Year();

        var page = Render<ExternalQuality>();
        page.Find("[data-action='Eqa.RecordEvaluation']").Click();
        page.Find($"[data-field={EqaScreenRules.AssignedField}]").Input("5,00");
        page.Find($"[data-field={EqaScreenRules.PeerMeanField}]").Input("5,05");
        page.Find($"[data-field={EqaScreenRules.PeerSdField}]").Input("0,30");
        page.Find("[data-modal=eqa-evaluation] [data-action='Action.Save']").Click();

        participation.ZScore.Should().Be(3.83m);
        participation.Outcome.Should().Be(EqaOutcome.Unsatisfactory);
        _files.CallsTo(nameof(IRepository<NonConformity>.AddAsync)).Should().Be(1);
        AuditReason.Should().Contain("ouverture de la NC NC-2026-0013");
        var message = page.Find("[data-state=eqa-message]");
        message.TextContent.Should().Contain("NC-2026-0013").And.Contain("Non-conformités");
        message.ClassName.Should().Contain("critical");
    }

    [Fact]
    public void ARound_IsRegisteredWithItsAnalytes_ADeadlineBeforeTheRoundRefusedOnItsBox()
    {
        SignIn(AppLanguage.French, Biologist);
        Year();
        var glucose = new EqaOption(Guid.NewGuid(), "GLU", new MultilingualText("Glucose", "الجلوكوز"));
        var urea = new EqaOption(Guid.NewGuid(), "UREE", new MultilingualText("Urée", "اليوريا"));
        _queries.On(nameof(IEqaQueries.GetParameterOptionsAsync), _ => new[] { glucose, urea });
        _queries.On(nameof(IEqaQueries.GetRegisteredParameterIdsAsync), _ => new HashSet<Guid>());

        var page = Render<ExternalQuality>();
        page.Find("[data-action='Eqa.RegisterRound']").Click();
        page.Find($"[data-field={EqaScreenRules.SchemeField}]").Input("ProBioQual Biochimie");
        page.Find($"[data-field={EqaScreenRules.RoundField}]").Input("2026-04");
        page.Find("[data-field=round-date]").Change("2026-10-20");
        page.Find($"[data-field={EqaScreenRules.DeadlineField}]").Change("2026-10-10");
        page.Find("[data-parameter=GLU]").Change(true);
        page.Find("[data-parameter=UREE]").Change(true);
        page.Find("[data-modal=eqa-round] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={EqaScreenRules.DeadlineField}]").TextContent.Should().Contain("précéder la date de la campagne");
        _participations.CallsTo(nameof(IRepository<EqaParticipation>.AddAsync)).Should().Be(0);

        page.Find($"[data-field={EqaScreenRules.DeadlineField}]").Change("2026-11-03");
        page.Find("[data-modal=eqa-round] [data-action='Action.Save']").Click();

        _participations.CallsTo(nameof(IRepository<EqaParticipation>.AddAsync)).Should().Be(2);
        AuditReason.Should().Be("Enregistrement de la campagne EEQ ProBioQual Biochimie 2026-04");
        page.Find("[data-state=eqa-message]").TextContent.Should().Contain("2 paramètre(s)");
    }

    [Fact]
    public void WhoEntersQc_SendsTheResult_ButNeitherRegistersNorScores_AndWhoOnlyReadsDoesNothing()
    {
        SignIn(AppLanguage.French, Technician);
        Year();

        var page = Render<ExternalQuality>();
        page.Find("[data-action='Eqa.RecordSubmission']").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-action='Eqa.RecordEvaluation']").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Eqa.RegisterRound']").HasAttribute("disabled").Should().BeTrue();

        page.Find("[data-row='2026-03-CREA']").Click();
        page.Find("[data-action='Eqa.RecordSubmission']").HasAttribute("disabled").Should().BeTrue("a value is not changed once scored");
    }

    [Fact]
    public void WhoOnlyReads_SeesTheYear_WithEveryStepOff()
    {
        SignIn(AppLanguage.French, Reader);
        Year();

        var page = Render<ExternalQuality>();

        page.FindAll("[data-grid=participations] tbody tr").Should().HaveCount(3);
        foreach (var step in new[] { "RecordSubmission", "RecordEvaluation", "RegisterRound" })
            page.Find($"[data-action='Eqa.{step}']").HasAttribute("disabled").Should().BeTrue(step);
    }

    [Fact]
    public void InArabic_TheScreenIsArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Biologist);
        Year();

        var page = Render<ExternalQuality>();

        page.Find("[data-screen=eqa]").TextContent.Should().NotContain("Eqa.").And.NotContain("Web.").And.NotContain("[");
        page.Find("[data-row='2026-03-GLU']").TextContent.Should().Contain("الجلوكوز");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }

    [Fact]
    public void TheWebServer_ReadsTheEqaAndTheDocumentRegisters()
    {
        // The two screens ask IEqaQueries and IDocumentControlQueries; a server without them fails at the first render.
        var services = new ServiceCollection();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        MarocLIS.Web.Hosting.WebServiceCollectionExtensions.AddMarocLisWeb(services, configuration);

        services.Should().Contain(d => d.ServiceType == typeof(IEqaQueries));
        services.Should().Contain(d => d.ServiceType == typeof(IDocumentControlQueries));
    }
}
