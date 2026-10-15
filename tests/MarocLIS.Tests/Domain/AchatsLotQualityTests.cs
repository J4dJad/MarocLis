using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The quality of a reagent lot after reception (achats A4 and A6): the QC gate on release, the
/// biologist's derogation, the new-lot / old-lot comparison and the lot a result was produced with.
/// </summary>
public sealed class AchatsLotQualityTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static readonly Guid Low = Guid.NewGuid();
    private static readonly Guid High = Guid.NewGuid();

    private static readonly RequiredQcLevel[] TwoLevels =
        [new(Low, "GLU · niveau bas"), new(High, "GLU · niveau haut")];

    private static LotQcPoint Point(Guid target, Guid? run, QcOutcome outcome, int minutesAgo) =>
        new(Guid.NewGuid(), target, run, outcome, Now.AddMinutes(-minutesAgo));

    // ---- A4: the QC gate --------------------------------------------------------------------------

    [Fact]
    public void AConsumable_OrAReagentTiedToNoAnalysis_IsNotGated()
    {
        LotQcGate.RequiresQc(StockItemKind.Consumable, ["GLU"]).Should().BeFalse();
        LotQcGate.RequiresQc(StockItemKind.Reagent, []).Should().BeFalse();
        LotQcGate.RequiresQc(StockItemKind.Reagent, ["GLU"]).Should().BeTrue();

        var verdict = LotQcGate.Evaluate(false, TwoLevels, []);
        verdict.Kind.Should().Be(LotQcVerdictKind.NotRequired);
        verdict.AllowsRelease.Should().BeTrue();
    }

    [Fact]
    public void AReagentLot_WithNoQcRun_CannotBeReleased()
    {
        var verdict = LotQcGate.Evaluate(true, TwoLevels, []);

        verdict.Kind.Should().Be(LotQcVerdictKind.NoQcRun);
        verdict.AllowsRelease.Should().BeFalse();
        verdict.ResourceKey.Should().Be("Lot_ReleaseNoQcRun");
    }

    [Fact]
    public void ARunWithBothLevelsAccepted_ReleasesTheLot()
    {
        var run = Guid.NewGuid();

        var verdict = LotQcGate.Evaluate(true, TwoLevels,
            [Point(Low, run, QcOutcome.Accepted, 10), Point(High, run, QcOutcome.Warning, 10)]);

        verdict.Kind.Should().Be(LotQcVerdictKind.Conform);
        verdict.AllowsRelease.Should().BeTrue();
    }

    [Fact]
    public void EveryRunHavingARejection_IsNoConformingRun()
    {
        var run = Guid.NewGuid();

        var verdict = LotQcGate.Evaluate(true, TwoLevels,
            [Point(Low, run, QcOutcome.Accepted, 10), Point(High, run, QcOutcome.Rejected, 10)]);

        verdict.Kind.Should().Be(LotQcVerdictKind.NoConformingRun);
        verdict.ResourceKey.Should().Be("Lot_ReleaseQcRejected");
    }

    [Fact]
    public void ALevelNeverRunOnTheLot_IsNamed()
    {
        var verdict = LotQcGate.Evaluate(true, TwoLevels, [Point(Low, null, QcOutcome.Accepted, 10)]);

        verdict.Kind.Should().Be(LotQcVerdictKind.LevelsNotConform);
        verdict.FailingLevels.Should().Equal("GLU · niveau haut");
        verdict.ResourceKey.Should().Be("Lot_ReleaseQcLevelsMissing");
    }

    [Fact]
    public void TheLastPointOfALevel_Decides_NotAnEarlierAcceptedOne()
    {
        var first = Guid.NewGuid();

        var verdict = LotQcGate.Evaluate(true, TwoLevels,
        [
            Point(Low, first, QcOutcome.Accepted, 60), Point(High, first, QcOutcome.Accepted, 60),
            Point(High, null, QcOutcome.Rejected, 5)
        ]);

        verdict.Kind.Should().Be(LotQcVerdictKind.LevelsNotConform);
        verdict.FailingLevels.Should().Equal("GLU · niveau haut");
    }

    [Fact]
    public void TheDerogation_NeedsAJustification_AndIsKeptOnTheLot()
    {
        var lot = new ReagentLot("L1", Today.AddYears(1), 5m, Today);

        lot.ReleaseOverridingQc(Today, Guid.NewGuid(), "CQ fournisseur", "  ")
           .FirstError.ResourceKey.Should().Be("Lot_QcOverrideJustificationRequired");
        lot.IsQuarantined.Should().BeTrue();

        lot.ReleaseOverridingQc(Today, Guid.NewGuid(), "CQ fournisseur", " Rupture, analyse urgente ")
           .IsSuccess.Should().BeTrue();
        lot.IsQuarantined.Should().BeFalse();
        lot.ReleasedWithQcOverride.Should().BeTrue();
        lot.QcOverrideJustification.Should().Be("Rupture, analyse urgente");
    }

    [Fact]
    public async Task AReagentLotWithoutQc_IsNotReleasedOnEvidenceAlone()
    {
        var harness = new ReleaseHarness(points: []);

        var result = await harness.Service.ReleaseLotAsync(harness.Lot.Id, "Notice lue");

        result.FirstError.ResourceKey.Should().Be("Lot_ReleaseNoQcRun");
        result.FirstError.Args[0].Should().Be("GLU-2611");
        harness.Lot.IsQuarantined.Should().BeTrue();
    }

    [Fact]
    public async Task AReagentLotWithConformingQc_IsReleased()
    {
        var run = Guid.NewGuid();
        var harness = new ReleaseHarness(points:
            [Point(Low, run, QcOutcome.Accepted, 30), Point(High, run, QcOutcome.Accepted, 30)]);

        (await harness.Service.ReleaseLotAsync(harness.Lot.Id, "CQ niveaux bas et haut conformes"))
            .IsSuccess.Should().BeTrue();
        harness.Lot.IsQuarantined.Should().BeFalse();
        harness.Lot.ReleasedWithQcOverride.Should().BeFalse();
    }

    [Fact]
    public async Task TheDerogation_IsRefusedToWhoeverIsNotABiologist()
    {
        var harness = new ReleaseHarness(points: [], denied: Permissions.Results.BiologicalValidate);

        (await harness.Service.ReleaseLotOverridingQcAsync(harness.Lot.Id, "Notice", "Rupture de stock"))
            .FirstError.ResourceKey.Should().Be("Lot_QcOverrideRequiresBiologist");
        harness.Lot.IsQuarantined.Should().BeTrue();
    }

    [Fact]
    public async Task TheBiologist_ReleasesByDerogation_AndTheAuditSaysSo()
    {
        var harness = new ReleaseHarness(points: []);

        (await harness.Service.ReleaseLotOverridingQcAsync(harness.Lot.Id, "Notice", "Rupture de stock"))
            .IsSuccess.Should().BeTrue();
        harness.Lot.ReleasedWithQcOverride.Should().BeTrue();
        harness.AuditReason.Should().Contain("PAR DÉROGATION").And.Contain("Rupture de stock");
    }

    [Fact]
    public async Task AConsumable_KeepsTheEvidenceOnlyRelease()
    {
        var harness = new ReleaseHarness(points: [], kind: StockItemKind.Consumable);

        (await harness.Service.ReleaseLotAsync(harness.Lot.Id, "Aspect conforme")).IsSuccess.Should().BeTrue();
    }

    // ---- A4: new lot against old lot --------------------------------------------------------------

    [Fact]
    public void TheComparison_GivesMeanSdCountAndTheShiftInPercent()
    {
        var rows = LotComparison.Compare(
            [new(Low, "Bas", 5.0m), new(Low, "Bas", 5.2m), new(Low, "Bas", 5.4m)],
            [new(Low, "Bas", 5.0m), new(Low, "Bas", 5.0m), new(High, "Haut", 15m)]);

        rows.Should().HaveCount(2);
        var low = rows.Single(r => r.Label == "Bas");
        low.NewCount.Should().Be(3);
        low.NewMean.Should().Be(5.2m);
        low.NewSd.Should().Be(0.2m);
        low.OldCount.Should().Be(2);
        low.OldMean.Should().Be(5.0m);
        low.OldSd.Should().Be(0m);
        low.DifferencePercent.Should().Be(4.00m);

        var high = rows.Single(r => r.Label == "Haut");
        high.NewCount.Should().Be(0);
        high.NewMean.Should().BeNull();
        high.DifferencePercent.Should().BeNull();
    }

    // ---- A6: the lot a result was produced with ---------------------------------------------------

    private static StockItem Reagent(string code, string analyses)
    {
        var item = new StockItem(code, new MultilingualText(code), StockItemKind.Reagent, "coffret", Guid.NewGuid())
        {
            UsedByAnalysisCodesCsv = analyses
        };
        return item;
    }

    private static ReagentLot OpenLot(StockItem item, string number, int expiresInDays, int openedDaysAgo,
                                      decimal quantity = 2m)
    {
        var lot = item.AddLot(new ReagentLot(number, Today.AddDays(expiresInDays), quantity, Today.AddDays(-60)));
        lot.Release(Today.AddDays(-50), Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();
        lot.OpenedOn = Today.AddDays(-openedDaysAgo);
        return lot;
    }

    [Fact]
    public void TheAnalysisList_IsSplitOnAnySeparator_UpperCasedAndDeduplicated()
    {
        ReagentLotSelector.AnalysisCodes(" glu; GLU ,urea|crea\tna").Should().Equal("GLU", "UREA", "CREA", "NA");
        ReagentLotSelector.AnalysisCodes(null).Should().BeEmpty();
    }

    [Fact]
    public void TheLotInUse_IsTheOpenedUsableOne_FirstExpiryFirst()
    {
        var item = Reagent("GLU-R1", "GLU");
        var later = OpenLot(item, "B", 200, 3);
        var sooner = OpenLot(item, "A", 100, 1);
        item.AddLot(new ReagentLot("C-QUARANTINE", Today.AddDays(50), 2m, Today));

        ReagentLotSelector.LotInUse([item], "glu", Today).Should().BeSameAs(sooner);
        later.Should().NotBeSameAs(sooner);
    }

    [Fact]
    public void TwoOpenLotsExpiringTheSameDay_AreAmbiguous_AndNoLotIsGuessed()
    {
        var item = Reagent("GLU-R1", "GLU");
        OpenLot(item, "A", 100, 3);
        OpenLot(item, "B", 100, 1);

        ReagentLotSelector.LotInUse(item, Today).Should().BeNull();
    }

    [Fact]
    public void TwoReagentArticlesForTheAnalysis_AreAmbiguous()
    {
        var first = Reagent("GLU-R1", "GLU");
        var second = Reagent("GLU-R2", "GLU,UREA");
        OpenLot(first, "A", 100, 3);
        var urea = OpenLot(second, "B", 100, 1);

        ReagentLotSelector.LotInUse([first, second], "GLU", Today).Should().BeNull();
        ReagentLotSelector.LotInUse([first, second], "UREA", Today).Should().BeSameAs(urea);
    }

    [Fact]
    public void ALotDrawnToZero_IsInUseOnlyWhenNoOtherOpenLotHasStock()
    {
        var item = Reagent("GLU-R1", "GLU");
        var empty = OpenLot(item, "A", 100, 1, quantity: 1m);
        empty.Consume(1m, Today).IsSuccess.Should().BeTrue();

        ReagentLotSelector.LotInUse(item, Today).Should().BeSameAs(empty);

        var stocked = OpenLot(item, "B", 300, 5);
        ReagentLotSelector.LotInUse(item, Today).Should().BeSameAs(stocked);
    }

    [Fact]
    public void AnExpiredOrBeyondStabilityLot_IsNotInUse()
    {
        var item = Reagent("GLU-R1", "GLU");
        var lot = OpenLot(item, "A", 100, 40);
        lot.StabilityAfterOpeningDays = 28;

        ReagentLotSelector.LotInUse(item, Today).Should().BeNull();
    }

    // ---- Harness ----------------------------------------------------------------------------------

    private sealed class ReleaseHarness
    {
        public ReleaseHarness(IReadOnlyList<LotQcPoint> points, string? denied = null,
                              StockItemKind kind = StockItemKind.Reagent)
        {
            Lot = new ReagentLot("GLU-2611", Today.AddYears(1), 4m, Today);

            var evidence = new LotQcEvidence(Lot.Id, Lot.LotNumber, "GLU-R1", kind, ["GLU"], TwoLevels, points);

            var unitOfWork = AchatsReceptionTests.Stub<IUnitOfWork>.With(
                ("set_AuditReason", args => { AuditReason = (string?)args![0]; return null; }));

            Service = new InventoryService(
                AchatsReceptionTests.Stub<IRepository<StockItem>>.Create(),
                AchatsReceptionTests.Stub<IRepository<StockMovement>>.Create(),
                AchatsReceptionTests.Stub<IRepository<Supplier>>.Create(),
                AchatsReceptionTests.Stub<IInventoryQueries>.With(
                    ("GetLotEntityAsync", _ => Task.FromResult<ReagentLot?>(Lot))),
                unitOfWork,
                denied is null ? AchatsReceptionTests.Guard() : AchatsReceptionTests.Guard(denied),
                AchatsReceptionTests.Stub<ICurrentUser>.With(("get_UserId", _ => Guid.NewGuid())),
                AchatsReceptionTests.Clock(),
                qualityQueries: AchatsReceptionTests.Stub<IPurchasingQualityQueries>.With(
                    ("GetLotQcEvidenceAsync", _ => Task.FromResult<LotQcEvidence?>(evidence))));
        }

        public ReagentLot Lot { get; }
        public InventoryService Service { get; }
        public string? AuditReason { get; private set; }
    }
}
