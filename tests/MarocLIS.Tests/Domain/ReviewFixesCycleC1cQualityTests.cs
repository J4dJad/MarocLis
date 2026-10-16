using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Defect hunt, cycle 1, quality lot: a non-conformity worked on after it was closed or dismissed,
/// the analysis dialog that opened blank and overwrote the recorded cause, the Westgard rules shown
/// by their identifiers, and the versions of a retired document still moving.
/// </summary>
public sealed class ReviewFixesCycleC1cQualityTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(1));

    private static NonConformity File(NonConformitySeverity severity = NonConformitySeverity.Minor) =>
        new("NC-2026-0001", Guid.NewGuid(), NonConformityCategory.Analytical, severity,
            MultilingualText.FromFrench("Écart"), Now.AddDays(-3), Guid.NewGuid());

    private static CorrectiveAction Action() =>
        new(ActionKind.Correction, MultilingualText.FromFrench("Action"), Guid.NewGuid(), Today.AddDays(7));

    private static NonConformity ClosedFile()
    {
        var file = File();
        file.AddAction(Action()).Complete(Today, Guid.NewGuid(), null);
        file.Close(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        return file;
    }

    private static NonConformityService Service(NonConformity file) =>
        Build<NonConformityService>(
            (typeof(IRepository<NonConformity>), ReviewFixesBilling2Tests.Stub<IRepository<NonConformity>>.With(
                ("GetByIdWithDetailsAsync", _ => Task.FromResult<NonConformity?>(file)),
                ("GetByIdAsync", _ => Task.FromResult<NonConformity?>(file)))));

    // ---- Domain ---------------------------------------------------------------------------------

    [Fact]
    public void AnActionAddedWhileAwaitingVerification_PutsTheFileBackInProgress()
    {
        var file = File();
        file.AddAction(Action()).Complete(Today, Guid.NewGuid(), null);
        file.MarkAwaitingVerification();

        file.AddAction(Action());

        file.Status.Should().Be(NonConformityStatus.InProgress);
    }

    [Fact]
    public void AClosedFile_CannotBeClosedAgain()
    {
        var file = ClosedFile();
        var closedAt = file.ClosedAt;

        file.Close(Now.AddDays(2), Guid.NewGuid()).FirstError.ResourceKey.Should().Be("Nc_AlreadyClosed");
        file.ClosedAt.Should().Be(closedAt);
    }

    [Fact]
    public void ADismissedFile_CannotBeClosed()
    {
        var file = File();
        file.AddAction(Action()).Complete(Today, Guid.NewGuid(), null);
        file.Dismiss(Now, Guid.NewGuid(), "Signalement non fondé").IsSuccess.Should().BeTrue();

        file.Close(Now, Guid.NewGuid()).FirstError.ResourceKey.Should().Be("Nc_AlreadyDismissed");
        file.Status.Should().Be(NonConformityStatus.Rejected);
    }

    [Fact]
    public void ACompletedAction_KeepsItsDateAndEvidence()
    {
        var action = File().AddAction(Action());
        action.Complete(Today, Guid.NewGuid(), MultilingualText.FromFrench("Bon de maintenance")).IsSuccess.Should().BeTrue();

        action.Complete(Today.AddDays(5), Guid.NewGuid(), MultilingualText.FromFrench("Autre"))
              .FirstError.ResourceKey.Should().Be("Nc_ActionAlreadyCompleted");

        action.CompletedOn.Should().Be(Today);
        action.CompletionEvidence!.Fr.Should().Be("Bon de maintenance");
    }

    // ---- Service: a closed or dismissed file is read-only ---------------------------------------------

    [Fact]
    public async Task AClosedFile_RefusesANewAction()
    {
        var file = ClosedFile();

        var added = await Service(file).AddActionAsync(new AddActionRequest(
            file.Id, ActionKind.CorrectiveAction, MultilingualText.FromFrench("Nouvelle"), Guid.NewGuid(), Today));

        added.FirstError.ResourceKey.Should().Be("Nc_AlreadyClosed");
        file.Actions.Should().HaveCount(1);
        file.Status.Should().Be(NonConformityStatus.Closed);
    }

    [Fact]
    public async Task ADismissedFile_IsNotMovedBackToVerification()
    {
        var file = File();
        var pending = file.AddAction(Action());
        file.Dismiss(Now, Guid.NewGuid(), "Signalement non fondé").IsSuccess.Should().BeTrue();

        var completed = await Service(file).CompleteActionAsync(file.Id, pending.Id, null);

        completed.FirstError.ResourceKey.Should().Be("Nc_AlreadyDismissed");
        file.Status.Should().Be(NonConformityStatus.Rejected);
        pending.Status.Should().Be(CorrectiveActionStatus.Planned);
    }

    [Fact]
    public async Task AClosedFile_RefusesAnalysisStartAndCancel()
    {
        var file = ClosedFile();
        var action = file.Actions.Single();
        var service = Service(file);

        (await service.RecordAnalysisAsync(file.Id, RootCauseCategory.Workload)).FirstError.ResourceKey
            .Should().Be("Nc_AlreadyClosed");
        (await service.StartActionAsync(file.Id, action.Id)).FirstError.ResourceKey.Should().Be("Nc_AlreadyClosed");
        (await service.CancelActionAsync(file.Id, action.Id, "Erreur")).FirstError.ResourceKey
            .Should().Be("Nc_AlreadyClosed");

        file.RootCause.Should().BeNull();
    }

    [Fact]
    public async Task ACancelledAction_KeepsItsFirstReason()
    {
        var file = File();
        file.AddAction(Action()).Complete(Today, Guid.NewGuid(), null);
        var wrong = file.AddAction(Action());
        var service = Service(file);

        (await service.CancelActionAsync(file.Id, wrong.Id, "Saisie en double")).IsSuccess.Should().BeTrue();
        (await service.CancelActionAsync(file.Id, wrong.Id, "Autre motif")).FirstError.ResourceKey
            .Should().Be("Action_Cancelled");

        wrong.Notes.Should().Be("Saisie en double");
    }

    // ---- Analysis: what was recorded is shown, and saving it again changes nothing --------------------

    [Fact]
    public async Task TheRecordedAnalysis_IsReturnedForTheDialog()
    {
        var file = File(NonConformitySeverity.Major);
        file.RootCause = RootCauseCategory.EquipmentFailure;
        file.RootCauseAnalysis = MultilingualText.FromFrench("Lampe en fin de vie");
        file.EffectivenessVerification = MultilingualText.FromFrench("CIQ conformes 5 jours");
        file.EffectivenessVerifiedOn = Today.AddDays(-2);

        var analysis = await Service(file).GetAnalysisAsync(file.Id);

        analysis.IsSuccess.Should().BeTrue();
        analysis.Value.RootCause.Should().Be(RootCauseCategory.EquipmentFailure);
        analysis.Value.RootCauseAnalysis!.Fr.Should().Be("Lampe en fin de vie");
        analysis.Value.EffectivenessVerification!.Fr.Should().Be("CIQ conformes 5 jours");
        analysis.Value.EffectivenessVerifiedOn.Should().Be(Today.AddDays(-2));
    }

    [Fact]
    public async Task SavingTheSameEffectivenessCheckAgain_KeepsItsDateAndAuthor()
    {
        var file = File(NonConformitySeverity.Major);
        var verifier = Guid.NewGuid();
        file.EffectivenessVerification = MultilingualText.FromFrench("CIQ conformes 5 jours");
        file.EffectivenessVerifiedOn = Today.AddDays(-2);
        file.EffectivenessVerifiedByUserId = verifier;

        var saved = await Service(file).RecordAnalysisAsync(
            file.Id, RootCauseCategory.EquipmentFailure, MultilingualText.FromFrench("Lampe"),
            effectivenessVerification: MultilingualText.FromFrench("CIQ conformes 5 jours"));

        saved.IsSuccess.Should().BeTrue();
        file.RootCause.Should().Be(RootCauseCategory.EquipmentFailure);
        file.EffectivenessVerifiedOn.Should().Be(Today.AddDays(-2));
        file.EffectivenessVerifiedByUserId.Should().Be(verifier);
    }

    [Fact]
    public async Task AChangedEffectivenessCheck_IsDatedAgain()
    {
        var file = File(NonConformitySeverity.Major);
        file.EffectivenessVerification = MultilingualText.FromFrench("CIQ conformes 5 jours");
        file.EffectivenessVerifiedOn = Today.AddDays(-2);

        (await Service(file).RecordAnalysisAsync(
            file.Id, effectivenessVerification: MultilingualText.FromFrench("CIQ conformes 10 jours")))
            .IsSuccess.Should().BeTrue();

        file.EffectivenessVerifiedOn.Should().Be(Today);
        file.EffectivenessVerification!.Fr.Should().Be("CIQ conformes 10 jours");
    }

    // ---- Westgard rules by their short names -------------------------------------------------------

    [Fact]
    public void TheViolatedRules_ReadAsTheBenchSaysThem()
    {
        WestgardRuleNames.Describe(WestgardRule.OneThreeS | WestgardRule.OneTwoS).Should().Be("1-2s, 1-3s");
        WestgardRuleNames.Describe(WestgardRule.R4S | WestgardRule.TenX, " · ").Should().Be("R-4s · 10x");
        WestgardRuleNames.Describe(WestgardRule.None).Should().BeEmpty();
    }

    [Fact]
    public void EveryRule_HasAShortName()
    {
        foreach (var rule in Enum.GetValues<WestgardRule>().Where(r => r != WestgardRule.None))
            WestgardRuleNames.Of(rule).Should().ContainSingle().Which.Should().NotBe(rule.ToString());
    }

    // ---- Documents: a retired document's versions do not move --------------------------------------

    private static (ControlledDocument Document, DocumentControlService Service) RetiredDocument(
        DocumentVersionStatus upTo)
    {
        var document = new ControlledDocument("PR-PRE-002", MultilingualText.FromFrench("Prélèvement"),
                                              DocumentKind.Procedure, Guid.NewGuid());
        var version = document.AddDraft(null, Guid.NewGuid());
        version.Content = [1, 2, 3];
        if (upTo >= DocumentVersionStatus.UnderReview) version.SubmitForReview().IsSuccess.Should().BeTrue();
        if (upTo >= DocumentVersionStatus.Approved) version.Approve(Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        document.IsActive = false;

        var service = Build<DocumentControlService>(
            (typeof(IRepository<ControlledDocument>), ReviewFixesBilling2Tests.Stub<IRepository<ControlledDocument>>.With(
                ("GetByIdWithDetailsAsync", _ => Task.FromResult<ControlledDocument?>(document)),
                ("GetByIdAsync", _ => Task.FromResult<ControlledDocument?>(document)))));
        return (document, service);
    }

    [Fact]
    public async Task ARetiredDocument_CannotBePutInForce()
    {
        var (document, service) = RetiredDocument(DocumentVersionStatus.Approved);

        var made = await service.MakeEffectiveAsync(document.Id, 1, Today);

        made.FirstError.ResourceKey.Should().Be("Document_Retired");
        document.EffectiveVersion.Should().BeNull();
    }

    [Fact]
    public async Task ARetiredDocument_RefusesReviewApprovalAndANewVersion()
    {
        var (document, service) = RetiredDocument(DocumentVersionStatus.UnderReview);

        (await service.ReviewAsync(document.Id, 1)).FirstError.ResourceKey.Should().Be("Document_Retired");
        (await service.ApproveAsync(document.Id, 1)).FirstError.ResourceKey.Should().Be("Document_Retired");
        (await service.AddDraftAsync(document.Id, null, [1], "PR-PRE-002.pdf")).FirstError.ResourceKey
            .Should().Be("Document_Retired");

        document.Versions.Should().ContainSingle().Which.Status.Should().Be(DocumentVersionStatus.UnderReview);
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static T Build<T>(params (Type Type, object Instance)[] overrides)
    {
        var constructor = typeof(T).GetConstructors().Single();

        var args = constructor.GetParameters().Select(p =>
        {
            var given = overrides.FirstOrDefault(o => o.Type == p.ParameterType);
            if (given.Instance is not null) return given.Instance;

            if (p.ParameterType == typeof(IClock)) return new FixedClock();

            return typeof(ReviewFixesBilling2Tests.Stub<>).MakeGenericType(p.ParameterType)
                       .GetMethod("Create")!.Invoke(null, null);
        }).ToArray();

        return (T)constructor.Invoke(args);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => ReviewFixesCycleC1cQualityTests.Now;
        public DateOnly Today => ReviewFixesCycleC1cQualityTests.Today;
    }
}
