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
using Rules = MarocLIS.Tests.Desktop.NonConformityScreenRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Non-conformités » in the browser: the register beside the file open, its actions under it, a
/// description left empty refused on its own box, a closure refused in full with each obligation
/// it misses, an action completed with its evidence, each step kept by the right the workstation
/// asks — all through the workstation's NonConformityService.
/// </summary>
public sealed class WebNonConformityTests : WebScreenContext
{
    private readonly Fake<IQualityQueries> _queries = Fake<IQualityQueries>.Create();
    private readonly Fake<IRepository<NonConformity>> _files = Fake<IRepository<NonConformity>>.Create();
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Fake<ISequenceAllocator> _sequences = Fake<ISequenceAllocator>.Create();

    public WebNonConformityTests()
    {
        Services.AddSingleton(_queries.Object);
        Services.AddSingleton(_files.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(_sequences.Object);
        Services.AddScoped<NonConformityService>();
    }

    private static readonly string[] Manager =
    [
        Permissions.Quality.View, Permissions.Quality.ReportNonConformity, Permissions.Quality.ManageNonConformity
    ];

    private static readonly string[] Reporter = [Permissions.Quality.View, Permissions.Quality.ReportNonConformity];

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    /// <summary>A major file whose only action is done, its analysis not written yet — and the register around it.</summary>
    private (NonConformity File, NonConformityRow Row, CorrectiveActionRow Action) Register(CorrectiveActionStatus status = CorrectiveActionStatus.Completed)
    {
        var file = new NonConformity("NC-2026-0012", Guid.NewGuid(), NonConformityCategory.Equipment, NonConformitySeverity.Major,
                                     new MultilingualText("Dérive de la calibration de la glycémie sur l'AU480"), TestClock.Now.AddDays(-9), Guid.NewGuid());
        var action = file.AddAction(new CorrectiveAction(ActionKind.CorrectiveAction, new MultilingualText("Recalibrer l'AU480 et repasser les contrôles"),
                                                         Guid.NewGuid(), new DateOnly(2026, 10, 20)));
        if (status == CorrectiveActionStatus.Completed) action.Complete(new DateOnly(2026, 10, 17), Guid.NewGuid(), null);

        var row = Rules.File(status: NonConformityStatus.InProgress, actions: 1, open: status == CorrectiveActionStatus.Completed ? 0 : 1) with { Id = file.Id };
        var overdue = Rules.File("NC-2026-0009", NonConformityStatus.Open, NonConformitySeverity.Critical, NonConformityCategory.PreAnalytical,
                                 affected: true, patients: 4, overdue: true, recurrence: true, actions: 0, open: 0,
                                 description: "Tubes hémolysés acceptés au prélèvement");
        var minor = Rules.File("NC-2026-0005", NonConformityStatus.AwaitingVerification, NonConformitySeverity.Minor, NonConformityCategory.Documentation,
                               description: "Fiche de paillasse non signée");
        var actionRow = Rules.Action(status) with { Id = action.Id };

        _queries.On(nameof(IQualityQueries.GetNonConformitiesAsync), _ => new[] { row, overdue, minor });
        _queries.On(nameof(IQualityQueries.GetActionsAsync), args => (Guid)args[0]! == file.Id ? new[] { actionRow } : Array.Empty<CorrectiveActionRow>());
        _files.On(nameof(IRepository<NonConformity>.GetByIdWithDetailsAsync), _ => file);
        _files.On(nameof(IRepository<NonConformity>.GetByIdAsync), _ => file);
        return (file, row, actionRow);
    }

    [Fact]
    public void TheRegister_TheFileOpen_AndItsActions_AreOnOneScreen()
    {
        SignIn(AppLanguage.French, Manager);
        Register();

        var page = Render<NonConformities>();

        page.FindAll("[data-grid=register] tbody tr").Should().HaveCount(3);
        page.Find("[data-state=count]").TextContent.Should().Contain("3").And.Contain("1");
        var overdue = page.Find("[data-row='NC-2026-0009']");
        overdue.TextContent.Should().Contain("↻").And.Contain("⚠").And.Contain("Critique");
        overdue.ClassName.Should().Contain("nc-overdue");
        overdue.QuerySelector(".clamp-2")!.GetAttribute("title").Should().Contain("Tubes hémolysés");

        // The first file is open: its facts beside the register, its action under it.
        page.Find("[data-state=reference]").TextContent.Should().Contain("NC-2026-0012");
        page.Find("[data-state=facts]").TextContent.Should().Contain("Karim Tahiri");
        page.FindAll("[data-grid=actions] tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("Recalibrer");

        page.Find("[data-row='NC-2026-0009']").Click();
        page.Find("[data-state=reference]").TextContent.Should().Contain("NC-2026-0009");
        page.Find("[data-state=no-action]").Should().NotBeNull();

        page.Find("[data-chip=only-open]").Click();
        _queries.Calls.Last(c => c.Method == nameof(IQualityQueries.GetNonConformitiesAsync)).Arguments[1].Should().Be(false);
    }

    [Fact]
    public void ADescriptionLeftEmpty_IsRefusedOnItsBox_ThenTheFileOpensWithItsReference()
    {
        SignIn(AppLanguage.French, Reporter);
        Register();
        _sequences.On(nameof(ISequenceAllocator.NextAsync), _ => Result.Success("NC-2026-0013"));

        var page = Render<NonConformities>();
        page.Find("[data-action='NonConformity.OpenNew']").Click();
        page.Find("[data-modal=nc-editor] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={NonConformityScreenRules.DescriptionField}]").TextContent.Should().NotBeNullOrWhiteSpace();
        _files.CallsTo(nameof(IRepository<NonConformity>.AddAsync)).Should().Be(0);

        page.Find($"[data-field={NonConformityScreenRules.DescriptionField}]").Input("Étiquette illisible sur un tube de sérum");
        page.Find("[data-modal=nc-editor] [data-action='Action.Save']").Click();

        _files.CallsTo(nameof(IRepository<NonConformity>.AddAsync)).Should().Be(1);
        AuditReason.Should().Be("Ouverture de la non-conformité NC-2026-0013");
        page.FindAll("[data-modal=nc-editor]").Should().BeEmpty();
        page.Find("[data-state=status]").TextContent.Should().Contain("NC-2026-0013");
    }

    /// <summary>
    /// A file is linked to the tube it is about, found by its file's number: the refused tube is
    /// picked first, another can be chosen, and the file is saved with the tube and its file — the
    /// domain held the link, but no screen offered it.
    /// </summary>
    [Fact]
    public void AFile_IsLinkedToTheTube_FoundByItsFileNumber()
    {
        SignIn(AppLanguage.French, Reporter);
        Register();
        _sequences.On(nameof(ISequenceAllocator.NextAsync), _ => Result.Success("NC-2026-0013"));
        var order = Guid.NewGuid();
        var serum = new NonConformitySampleChoice(Guid.NewGuid(), order, "0000000038", "2610-000014", new MultilingualText("Sérum"),
                                                  MarocLIS.Domain.Samples.SampleStatus.Received);
        var edta = new NonConformitySampleChoice(Guid.NewGuid(), order, "0000000036", "2610-000014", new MultilingualText("Sang total (EDTA)"),
                                                 MarocLIS.Domain.Samples.SampleStatus.Rejected);
        _queries.On(nameof(IQualityQueries.FindSamplesForNonConformityAsync),
                    args => (string)args[1]! == "2610-000014" ? new[] { serum, edta } : Array.Empty<NonConformitySampleChoice>());

        var page = Render<NonConformities>();
        page.Find("[data-action='NonConformity.OpenNew']").Click();

        page.Find("[data-field=sample-term]").Input("INCONNU");
        page.Find("[data-modal=nc-editor] [data-action='Action.Search']").Click();
        page.Find("[data-state=sample-not-found]").TextContent.Should().Contain("INCONNU");

        page.Find("[data-field=sample-term]").Input("2610-000014");
        page.Find("[data-modal=nc-editor] [data-action='Action.Search']").Click();
        page.FindAll("[data-field=sample] option").Should().HaveCount(3, "no tube, then the file's two tubes");
        page.Find("[data-field=sample]").QuerySelector("option[selected]")!.GetAttribute("value").Should().Be(edta.SampleId.ToString(),
            "the refused tube is the one a file is commonly about");
        page.Find("[data-field=sample] option:nth-child(3)").TextContent.Should().Contain("0000000036").And.Contain("Refusé");

        page.Find($"[data-field={NonConformityScreenRules.DescriptionField}]").Input("Tube EDTA hémolysé");
        page.Find("[data-modal=nc-editor] [data-action='Action.Save']").Click();

        var saved = (NonConformity)_files.Calls.Single(c => c.Method == nameof(IRepository<NonConformity>.AddAsync)).Arguments[0]!;
        saved.SampleId.Should().Be(edta.SampleId);
        saved.LabOrderId.Should().Be(order);
    }

    [Fact]
    public void AClosureRefused_IsSaidInFull_EachObligationTheFileMisses()
    {
        SignIn(AppLanguage.French, Manager);
        var (file, _, _) = Register();

        var page = Render<NonConformities>();
        page.Find("[data-action='NonConformity.Close']").Click();
        page.Find("[data-prompt=confirm]").TextContent.Should().Contain("NC-2026-0012");
        page.Find("[data-action='Action.Yes']").Click();

        var refusal = page.Find("[data-state=nc-message]").TextContent;
        refusal.Should().Contain("analyse des causes").And.Contain("efficacité");
        file.Status.Should().NotBe(NonConformityStatus.Closed);
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    [Fact]
    public void AnActionIsCompleted_WithItsEvidence_AndTheFileAwaitsVerification()
    {
        SignIn(AppLanguage.French, Manager);
        var (file, _, action) = Register(CorrectiveActionStatus.InProgress);

        var page = Render<NonConformities>();
        page.Find($"[data-grid=actions] [data-row='{action.Id}']").Click();
        page.Find("[data-action='NonConformity.CompleteAction']").Click();
        page.Find("[data-modal=evidence] [data-field=text]").Input("Calibration refaite le 17/10, contrôles N1 et N2 conformes");
        page.Find("[data-modal=evidence] [data-action='Action.Ok']").Click();

        file.Actions.Single().Status.Should().Be(CorrectiveActionStatus.Completed);
        file.Actions.Single().CompletionEvidence!.Fr.Should().Contain("contrôles N1 et N2 conformes");
        file.Status.Should().Be(NonConformityStatus.AwaitingVerification);
        AuditReason.Should().Be("Action soldée — NC NC-2026-0012");
    }

    [Fact]
    public void WhoMayOnlyReport_OpensAFile_AndEveryOtherStepIsOff()
    {
        SignIn(AppLanguage.French, Reporter);
        Register(CorrectiveActionStatus.InProgress);

        var page = Render<NonConformities>();

        page.Find("[data-action='NonConformity.OpenNew']").HasAttribute("disabled").Should().BeFalse();
        foreach (var step in new[] { "AddAction", "StartAction", "CompleteAction", "CancelAction", "RecordAnalysis", "Close", "Reopen", "Dismiss" })
            page.Find($"[data-action='NonConformity.{step}']").HasAttribute("disabled").Should().BeTrue(step);
    }

    [Fact]
    public void InArabic_TheScreenIsArabic_AndThePageRunsRightToLeft()
    {
        SignIn(AppLanguage.Arabic, Manager);
        Register();

        var page = Render<NonConformities>();

        page.Find("[data-screen=nonconformities]").TextContent.Should().NotContain("NonConformity.").And.NotContain("Web.").And.NotContain("[");
        page.Find("[data-state=description]").TextContent.Should().NotBeNullOrWhiteSpace();
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
