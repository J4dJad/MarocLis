using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Quality;
using MarocLIS.Web.Components.Screens.Quality;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Rules = MarocLIS.Tests.Desktop.QualityControlScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Contrôle qualité » in the browser: the run typed beside its Levey-Jennings chart, a rejection
/// drawn as a square and awaiting review with its corrective action, a value that is no number
/// refused on its own box, each action kept by the right the workstation asks — all through the
/// workstation's QualityControlService.
/// </summary>
public sealed class WebQualityControlTests : WebScreenContext
{
    private readonly Fake<IQualityQueries> _queries = Fake<IQualityQueries>.Create();
    private readonly Fake<IRepository<QcTarget>> _targets = Fake<IRepository<QcTarget>>.Create();
    private readonly Fake<IRepository<QcResult>> _results = Fake<IRepository<QcResult>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Fake<IPurchasingQualityQueries> _lots = Fake<IPurchasingQualityQueries>.Create();

    public WebQualityControlTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_targets.Object);
        Services.AddSingleton(_results.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(_lots.Object);
        Services.AddSingleton(Fake<IRepository<QcMaterial>>.Create().Object);
        Services.AddSingleton(Fake<IRepository<NonConformity>>.Create().Object);
        Services.AddSingleton(Fake<ISequenceAllocator>.Create().Object);
        Services.AddScoped<QualityControlService>();
        Services.AddScoped<LotQualityService>();
    }

    private static readonly string[] Technician = [Permissions.Quality.View, Permissions.Quality.EnterQc];

    private static readonly string[] Manager =
    [
        Permissions.Quality.View, Permissions.Quality.EnterQc, Permissions.Quality.ReviewQc, Permissions.Quality.ManageQcRules
    ];

    private static readonly string[] Reviewer = [Permissions.Quality.View, Permissions.Quality.ReviewQc];

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    private (QcEntryRow Normal, QcEntryRow High, QcReviewRow Rejection) Sheet(int affected = 0)
    {
        var normal = Rules.Row();
        var high = Rules.Row(level: QcLevel.High, mean: 2.60m, sd: 0.08m, lot: "QCB-2611-N2", last: 2.63m);
        var creat = Rules.Row(code: "CREAT", mean: 12.0m, sd: 0.45m, instrument: Guid.NewGuid(), instrumentCode: "C311", last: 12.1m);
        var rejection = Rules.Review(normal, affected: affected);

        _queries.On(nameof(IQualityQueries.GetQcEntrySheetAsync), _ => new[] { normal, high, creat });
        _queries.On(nameof(IQualityQueries.GetPendingQcReviewAsync), _ => new[] { rejection });
        _queries.On(nameof(IQualityQueries.GetLeveyJenningsAsync), _ => Rules.Series(
            Rules.Point(0.4m, day: 14), Rules.Point(-0.8m, day: 15), Rules.Point(2.3m, QcOutcome.Warning, WestgardRule.OneTwoS, day: 16),
            Rules.Point(0.1m, day: 17), Rules.Point(3.4m, QcOutcome.Rejected, WestgardRule.OneThreeS, day: 18)));
        return (normal, high, rejection);
    }

    [Fact]
    public void TheRun_TheChartAndTheRejections_AreOnOneScreen_TheViolationDrawnAsASquare()
    {
        SignIn(AppLanguage.French, Manager);
        var (normal, _, rejection) = Sheet(affected: 3);

        var page = Render<QualityControl>();

        page.FindAll("[data-grid=entry] tbody tr").Should().HaveCount(3);
        page.Find("[data-grid=entry] tbody tr").TextContent.Should().Contain("Glycémie").And.Contain("1,00 ± 0,03");
        page.Find("[data-state=count]").TextContent.Should().Contain("3 contrôles").And.Contain("1 rejet");

        // The chart opens on the rejection awaiting review: the point out of control is on screen.
        _queries.Calls.First(c => c.Method == nameof(IQualityQueries.GetLeveyJenningsAsync)).Arguments[0].Should().Be(rejection.QcTargetId);
        page.Find("[data-chart=levey-jennings]").Should().NotBeNull();
        page.FindAll("[data-point=Rejected] rect").Should().ContainSingle("a rejection is a square");
        page.FindAll("[data-point=Warning] polygon").Should().ContainSingle("a warning is a triangle");
        page.FindAll("[data-point=Accepted] circle").Should().NotBeEmpty();
        page.Find("[data-state=active-rules]").TextContent.Should().Contain("1-2s · 1-3s · R-4s");
        page.Find("[data-state=chart-header]").TextContent.Should().Contain("limites fournisseur");

        page.Find("[data-grid=review] tbody tr").TextContent.Should().Contain("1-3s").And.Contain("3,40");
        page.Find("[data-card=review] .pill").TextContent.Should().Contain("3 résultats patients bloqués");

        page.Find("[data-point=Rejected]").Click();
        page.Find("[data-state=point]").TextContent.Should().Contain("Z 3,40").And.Contain("1-3s");

        page.Find("[data-chip=days-90]").Click();
        _queries.Calls.Last(c => c.Method == nameof(IQualityQueries.GetLeveyJenningsAsync)).Arguments[0].Should().Be(normal.QcTargetId);
        page.Find("[data-state=instruments]").TextContent.Should().Contain("poste Windows");
    }

    [Fact]
    public void TheFilters_PickTheControls_ByAnalyteAnalyserAndLevel()
    {
        SignIn(AppLanguage.French, Manager);
        Sheet();

        var page = Render<QualityControl>();

        page.Find("[data-field=search]").Input("créat");
        page.FindAll("[data-grid=entry] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("C311");
        page.FindAll("[data-grid=review] tbody tr").Should().BeEmpty("the rejection is a glucose");

        page.Find("[data-field=search]").Input("");
        page.Find("[data-field=filter-level]").Change(QcLevel.High.ToString());
        page.FindAll("[data-grid=entry] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("QCB-2611-N2");
    }

    [Fact]
    public void AValueThatIsNoNumber_IsRefusedOnItsOwnBox_AndNothingIsRecorded()
    {
        SignIn(AppLanguage.French, Technician);
        var (normal, high, _) = Sheet();

        var page = Render<QualityControl>();
        page.Find($"[data-field={QualityControlScreenRules.ValueField(normal.QcTargetId)}]").Input("1,07");
        page.Find($"[data-field={QualityControlScreenRules.ValueField(high.QcTargetId)}]").Input("2,6a");
        page.Find("[data-state=typed]").TextContent.Should().Contain("2 valeurs saisies");

        page.Find("[data-action='Action.Save']").Click();

        page.Find($"[data-refusal={QualityControlScreenRules.ValueField(high.QcTargetId)}]").TextContent.Should().Contain("« 2,6a » n'est pas une valeur numérique valide");
        _results.CallsTo(nameof(IRepository<QcResult>.AddAsync)).Should().Be(0);
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    [Fact]
    public void ARunRecorded_AnnouncesItsRejection_AndClearsTheValuesTyped()
    {
        SignIn(AppLanguage.French, Technician);
        var (normal, _, _) = Sheet();
        var target = new QcTarget(normal.QcMaterialId, normal.TestParameterId, normal.InstrumentId)
        {
            AssignedMean = 1.00m, AssignedSd = 0.03m, ActiveRules = WestgardRule.OneTwoS | WestgardRule.OneThreeS
        };
        _targets.On(nameof(IRepository<QcTarget>.GetByIdAsync), _ => target);
        _queries.On(nameof(IQualityQueries.GetParameterCodeAsync), _ => "GLY");

        var page = Render<QualityControl>();
        page.Find($"[data-field={QualityControlScreenRules.ValueField(normal.QcTargetId)}]").Input("1,102");
        page.Find("[data-action='Action.Save']").Click();

        page.Find("[data-state=qc-message]").TextContent.Should().Contain("Rejeté").And.Contain("GLY : 1,102 (Z 3,40)").And.Contain("action corrective");
        _results.CallsTo(nameof(IRepository<QcResult>.AddAsync)).Should().Be(1);
        AuditReason.Should().StartWith("Saisie du contrôle interne");
        page.Find("[data-action='Action.Ok']").Click();
        page.Find("[data-state=status]").TextContent.Should().Contain("0/1 accepté");
        page.FindAll("[data-state=typed]").Should().BeEmpty();
    }

    [Fact]
    public void ARejection_IsReviewedWithItsCorrectiveAction_TooShortARefused_AndTheNonConformityAsked()
    {
        SignIn(AppLanguage.French, Manager);
        var (normal, _, rejection) = Sheet();
        var result = new QcResult(normal.QcTargetId, normal.TestParameterId, normal.InstrumentId, Guid.NewGuid(), 1.10m, DateTimeOffset.Now, Guid.NewGuid());
        result.ApplyEvaluation(new WestgardEvaluation(WestgardRule.OneThreeS, QcOutcome.Rejected, ["Qc_Rule_1_3s"]));
        _results.On(nameof(IRepository<QcResult>.GetByIdAsync), _ => result);

        var page = Render<QualityControl>();
        page.Find("[data-grid=review] tbody tr").Click();
        page.Find("[data-action='Quality.Review']").Click();

        page.Find("[data-modal=reason]").TextContent.Should().Contain("Glycémie · 1-3s").And.Contain("action corrective");
        page.Find("[data-modal=reason] [data-field=reason]").Input("Recalibré");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();
        page.Find("[data-modal=reason] [role=alert]").Should().NotBeNull("a corrective action is fifteen characters at least");

        page.Find("[data-modal=reason] [data-field=reason]").Input("Réactif changé, calibration refaite, contrôle repassé conforme");
        page.Find("[data-modal=reason] [data-action='Action.Ok']").Click();
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("non-conformité");
        page.Find("[data-action='Action.No']").Click();

        _results.Calls.Should().Contain(c => c.Method == nameof(IRepository<QcResult>.GetByIdAsync) && (Guid)c.Arguments[0]! == rejection.QcResultId);
        result.CorrectiveActionTaken.Should().Be("Réactif changé, calibration refaite, contrôle repassé conforme");
        AuditReason.Should().Be("Revue du contrôle interne");
        page.Find("[data-state=status]").TextContent.Should().Contain("Enregistr");
    }

    [Fact]
    public void TheLaboratorysLimits_RefuseAWindowBeyondTheCalendar_InTheirBox()
    {
        SignIn(AppLanguage.French, Manager);
        Sheet();

        var page = Render<QualityControl>();
        page.Find("[data-action='Quality.EstablishLimits']").Click();
        page.Find("[data-modal=limits]").TextContent.Should().Contain("Glycémie · Normal").And.Contain("20 points");

        page.Find("[data-modal=limits] [data-field=text]").Input("5000");
        page.Find("[data-modal=limits] [data-action='Action.Ok']").Click();

        page.Find("[data-refusal=limits]").TextContent.Should().Contain("3650");
        _queries.CallsTo(nameof(IQualityQueries.GetAcceptedValuesAsync)).Should().Be(0);
    }

    [Fact]
    public void WhoMayOnlyReview_SeesTheRunReadOnly_AndOnlyTheReviewOffered()
    {
        SignIn(AppLanguage.French, Reviewer);
        var (normal, _, _) = Sheet();

        var page = Render<QualityControl>();

        page.Find("[data-action='Action.Save']").HasAttribute("disabled").Should().BeTrue();
        page.Find($"[data-field={QualityControlScreenRules.ValueField(normal.QcTargetId)}]").HasAttribute("readonly").Should().BeTrue();
        page.Find("[data-field=reagent-lot]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-action='Qc.StartEvaluation']").HasAttribute("disabled").Should().BeTrue();
        page.FindAll("[data-action='ResultEntry.MoreActions']").Should().BeEmpty("the limits and the rules are another right");
        page.Find("[data-action='Quality.Review']").HasAttribute("disabled").Should().BeFalse();
        _lots.CallsTo(nameof(IPurchasingQualityQueries.GetReagentLotsForQcAsync)).Should().Be(0, "the lots are read for who enters QC only");
    }

    [Fact]
    public void ANamedLot_AsksTheAnalyserItIsLoadedOn_BesideItsList()
    {
        SignIn(AppLanguage.French, Technician);
        var (normal, _, _) = Sheet();
        var lot = new ReagentLotOption(Guid.NewGuid(), "GLU-R1", "L2611-07", new DateOnly(2027, 3, 31), false);
        _lots.On(nameof(IPurchasingQualityQueries.GetReagentLotsForQcAsync), _ => new[] { lot });

        var page = Render<QualityControl>();
        page.Find("[data-field=reagent-lot]").Change(lot.ReagentLotId.ToString());
        page.Find($"[data-field={QualityControlScreenRules.ValueField(normal.QcTargetId)}]").Input("1,01");
        page.Find("[data-action='Action.Save']").Click();

        page.Find($"[data-refusal={QualityControlScreenRules.LotAnalyserField}]").TextContent.Should().Contain("Choisissez l'automate");
        _results.CallsTo(nameof(IRepository<QcResult>.AddAsync)).Should().Be(0);
    }

    [Fact]
    public void InArabic_TheScreenIsArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Manager);
        Sheet();

        var page = Render<QualityControl>();

        page.Find("[data-card=entry] .card-title").TextContent.Should().NotContain("Quality.").And.NotContain("[");
        page.Find("[data-state=instruments]").TextContent.Should().Contain("Windows").And.NotContain("Web.");
        page.Find("[data-chart=levey-jennings]").GetAttribute("dir").Should().Be("ltr", "the chart is laid out by hand, mirrored for Arabic");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }

    [Fact]
    public void TheWebServer_ReadsTheReagentLots_ForTheEvaluationRow()
    {
        // The screen's lot row asks IPurchasingQualityQueries; a server without it fails at the first render.
        var services = new ServiceCollection();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        MarocLIS.Web.Hosting.WebServiceCollectionExtensions.AddMarocLisWeb(services, configuration);

        services.Should().Contain(d => d.ServiceType == typeof(IPurchasingQualityQueries));
        services.Should().Contain(d => d.ServiceType == typeof(IQualityQueries));
    }
}
