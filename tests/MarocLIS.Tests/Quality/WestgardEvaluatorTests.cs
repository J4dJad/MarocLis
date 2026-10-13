using FluentAssertions;
using MarocLIS.Domain.Quality;
using Xunit;

namespace MarocLIS.Tests.Quality;

/// <summary>
/// Tests for the Westgard multirule evaluator.
/// <para>
/// This is the logic a laboratory is most likely to have to defend in an accreditation audit, so
/// each rule is tested for both directions of the mean, for the boundary (a point exactly on the
/// limit must not fire — the rules say "beyond", not "at"), and for the case where the rule is
/// switched off. The evaluator is a pure function, which is what makes that practical.
/// </para>
/// </summary>
public sealed class WestgardEvaluatorTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static QcPoint P(decimal z, int minutesAgo = 0, Guid? runId = null,
                             QcLevel level = QcLevel.Normal) =>
        new(z, DateTimeOffset.Now.AddMinutes(-minutesAgo), runId, level);

    /// <summary>Builds a history from oldest to newest, which is the order the evaluator expects.</summary>
    private static List<QcPoint> History(params decimal[] zScores) =>
        [.. zScores.Select((z, i) => P(z, zScores.Length - i))];

    // ---- No history / no target ------------------------------------------------------------------

    [Fact]
    public void Evaluate_WithNoHistory_IsNotEvaluable()
    {
        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.OneThreeS, []);

        evaluation.Outcome.Should().Be(QcOutcome.Pending);
        evaluation.ViolationKeys.Should().Contain("Qc_NoTargetEstablished");
    }

    // ---- 1-3s ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3.5)]
    [InlineData(-3.5)]
    public void OneThreeS_FiresBeyondThreeSd_AndRejects(decimal z)
    {
        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.OneThreeS, History(z));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.OneThreeS);
        evaluation.Outcome.Should().Be(QcOutcome.Rejected);
    }

    [Theory]
    [InlineData(3.0)]    // exactly on the limit: the rule says "beyond", so it must not fire
    [InlineData(2.99)]
    [InlineData(-3.0)]
    public void OneThreeS_DoesNotFireAtOrInsideTheLimit(decimal z)
    {
        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.OneThreeS, History(z));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.OneThreeS);
    }

    // ---- 1-2s is a warning, not a rejection -------------------------------------------------------

    [Fact]
    public void OneTwoS_Alone_IsAWarningAndDoesNotBlockRelease()
    {
        // 1-2s is expected to fire on roughly 5 % of in-control points. Treating it as a rejection
        // would stop a laboratory working; it exists to make the series worth watching.
        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.OneTwoS, History(2.4m));

        evaluation.ViolatedRules.Should().Be(WestgardRule.OneTwoS);
        evaluation.Outcome.Should().Be(QcOutcome.Warning);
        evaluation.IsRejection.Should().BeFalse();
    }

    [Fact]
    public void OneTwoS_WithAnotherRule_Rejects()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.OneTwoS | WestgardRule.OneThreeS, History(3.4m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.OneTwoS);
        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.OneThreeS);
        evaluation.Outcome.Should().Be(QcOutcome.Rejected);
    }

    // ---- 2-2s: across runs and within a run --------------------------------------------------------

    [Fact]
    public void TwoTwoS_FiresOnTwoConsecutivePointsSameSide()
    {
        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.TwoTwoS, History(2.3m, 2.5m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.TwoTwoS);
        evaluation.Outcome.Should().Be(QcOutcome.Rejected);
    }

    [Fact]
    public void TwoTwoS_DoesNotFireWhenPointsAreOnOppositeSides()
    {
        // Opposite sides is random error, not the systematic shift 2-2s is looking for.
        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.TwoTwoS, History(2.3m, -2.5m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.TwoTwoS);
    }

    [Fact]
    public void TwoTwoS_FiresWithinARunAcrossControlLevels()
    {
        // The within-run form compares levels rather than consecutive runs: both the normal and
        // the high control being beyond +2 SD in the same run is the classic calibration shift.
        var current = History(2.4m);
        var peers = new List<QcPoint> { P(2.6m, runId: Run, level: QcLevel.High) };

        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.TwoTwoS, current, peers);

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.TwoTwoS);
    }

    // ---- R-4s --------------------------------------------------------------------------------------

    [Fact]
    public void R4S_FiresWhenTwoLevelsInOneRunSpanMoreThanFourSdOnOppositeSides()
    {
        var current = History(2.3m);
        var peers = new List<QcPoint> { P(-2.2m, runId: Run, level: QcLevel.High) };

        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.R4S, current, peers);

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.R4S);
        evaluation.Outcome.Should().Be(QcOutcome.Rejected);
    }

    [Fact]
    public void R4S_DoesNotFireWhenBothPointsAreOnTheSameSide()
    {
        // Same-side points four SD apart would mean one is beyond 4 SD, which 1-3s catches. R-4s
        // is specifically about the spread between levels, so it requires opposite sides.
        var current = History(4.5m);
        var peers = new List<QcPoint> { P(0.2m, runId: Run, level: QcLevel.High) };

        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.R4S, current, peers);

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.R4S);
    }

    [Fact]
    public void R4S_NeedsRunPeers_AndDoesNotFireFromHistoryAlone()
    {
        var evaluation = WestgardEvaluator.Evaluate(WestgardRule.R4S, History(-2.5m, 2.5m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.R4S);
    }

    // ---- 4-1s and 3-1s -------------------------------------------------------------------------------

    [Fact]
    public void FourOneS_FiresOnFourConsecutivePointsBeyondOneSdSameSide()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.FourOneS, History(1.2m, 1.4m, 1.1m, 1.6m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.FourOneS);
    }

    [Fact]
    public void FourOneS_DoesNotFireWhenTheRunIsBroken()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.FourOneS, History(1.2m, 0.4m, 1.1m, 1.6m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.FourOneS);
    }

    [Fact]
    public void ThreeOneS_FiresOnThreeConsecutivePointsBeyondOneSd()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.ThreeOneS, History(-1.3m, -1.9m, -1.1m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.ThreeOneS);
    }

    // ---- 2of3-2s ------------------------------------------------------------------------------------

    [Fact]
    public void TwoOfThreeTwoS_FiresWhenTwoOfTheLastThreeAreBeyondTwoSd()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.TwoOfThreeTwoS, History(2.2m, 0.5m, 2.4m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.TwoOfThreeTwoS);
    }

    [Fact]
    public void TwoOfThreeTwoS_RequiresTheNewestPointToBeOneOfThem()
    {
        // Otherwise the rule would refire on every subsequent in-control point, and a laboratory
        // would be chasing a violation that has already passed.
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.TwoOfThreeTwoS, History(2.2m, 2.4m, 0.3m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.TwoOfThreeTwoS);
    }

    // ---- Bias rules: 8x, 10x, 12x ---------------------------------------------------------------------

    [Fact]
    public void TenX_FiresOnTenConsecutivePointsOnOneSideOfTheMean()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.TenX,
            History(0.2m, 0.4m, 0.1m, 0.6m, 0.3m, 0.5m, 0.2m, 0.7m, 0.4m, 0.3m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.TenX);
        evaluation.Outcome.Should().Be(QcOutcome.Rejected);
    }

    [Fact]
    public void TenX_DoesNotFireWithOnlyNinePoints()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.TenX,
            History(0.2m, 0.4m, 0.1m, 0.6m, 0.3m, 0.5m, 0.2m, 0.7m, 0.4m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.TenX);
    }

    [Fact]
    public void TenX_DoesNotFireWhenAPointCrossesTheMean()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.TenX,
            History(0.2m, 0.4m, 0.1m, -0.1m, 0.3m, 0.5m, 0.2m, 0.7m, 0.4m, 0.3m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.TenX);
    }

    [Fact]
    public void EightX_And_TenX_BothFireOnALongEnoughRun()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.EightX | WestgardRule.TenX,
            History(-0.2m, -0.4m, -0.1m, -0.6m, -0.3m, -0.5m, -0.2m, -0.7m, -0.4m, -0.3m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.EightX);
        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.TenX);
    }

    // ---- 7-T (drift) -----------------------------------------------------------------------------------

    [Fact]
    public void SevenT_FiresOnSevenConsecutiveRisingPoints()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.SevenT, History(-1.2m, -0.8m, -0.4m, 0.1m, 0.5m, 0.9m, 1.3m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.SevenT);
    }

    [Fact]
    public void SevenT_FiresOnSevenConsecutiveFallingPoints()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.SevenT, History(1.3m, 0.9m, 0.5m, 0.1m, -0.4m, -0.8m, -1.2m));

        evaluation.ViolatedRules.Should().HaveFlag(WestgardRule.SevenT);
    }

    [Fact]
    public void SevenT_DoesNotFireWhenTheTrendPlateaus()
    {
        // A flat step breaks the trend, which is the conventional reading of 7-T: drift means
        // consistent movement, and a repeated value is not movement.
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.SevenT, History(-1.2m, -0.8m, -0.4m, -0.4m, 0.5m, 0.9m, 1.3m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.SevenT);
    }

    // ---- Rule selection ---------------------------------------------------------------------------------

    [Fact]
    public void DisabledRules_DoNotFire()
    {
        // A laboratory running only 1-3s on a low-precision assay must not be rejected by 4-1s.
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.OneThreeS, History(1.2m, 1.4m, 1.1m, 1.6m));

        evaluation.ViolatedRules.Should().Be(WestgardRule.None);
        evaluation.Outcome.Should().Be(QcOutcome.Accepted);
    }

    [Fact]
    public void InControlSeries_IsAccepted()
    {
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.OneTwoS | WestgardRule.OneThreeS | WestgardRule.TwoTwoS
            | WestgardRule.R4S | WestgardRule.FourOneS | WestgardRule.TenX,
            History(0.3m, -0.5m, 0.8m, -0.2m, 0.6m, -0.9m, 0.1m));

        evaluation.ViolatedRules.Should().Be(WestgardRule.None);
        evaluation.Outcome.Should().Be(QcOutcome.Accepted);
        evaluation.ViolationKeys.Should().BeEmpty();
    }

    [Fact]
    public void ZeroZScore_DoesNotCountTowardsASideRun()
    {
        // A point exactly on the mean has no side, so it cannot extend a same-side run.
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.TenX,
            History(0.2m, 0.4m, 0.1m, 0.6m, 0.3m, 0.5m, 0.2m, 0.7m, 0.4m, 0m));

        evaluation.ViolatedRules.Should().NotHaveFlag(WestgardRule.TenX);
    }

    [Fact]
    public void EveryViolation_CarriesALocalisableResourceKey()
    {
        // Messages must render in French, Arabic or English, so a rule violation carries a key
        // rather than a sentence.
        var evaluation = WestgardEvaluator.Evaluate(
            WestgardRule.OneThreeS | WestgardRule.OneTwoS, History(3.6m));

        evaluation.ViolationKeys.Should().NotBeEmpty();
        evaluation.ViolationKeys.Should().OnlyContain(k => k.StartsWith("Qc_Rule_"));
    }
}

/// <summary>
/// Tests for the running mean and standard deviation on a QC target.
/// <para>
/// ISO 15189 §5.6 expects the laboratory to establish its own mean and SD from its own data, so the
/// accumulation has to be right — and it has to agree with the textbook two-pass calculation, which
/// is what these tests check.
/// </para>
/// </summary>
public sealed class QcTargetStatisticsTests
{
    private static QcTarget NewTarget() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void AccumulatePoint_MatchesTheTwoPassMeanAndSampleSd()
    {
        decimal[] values = [5.1m, 5.3m, 4.9m, 5.0m, 5.4m, 5.2m, 5.0m, 5.1m];

        var target = NewTarget();
        foreach (var value in values) target.AccumulatePoint(value);

        var expectedMean = values.Average();
        var expectedSd = (decimal)Math.Sqrt(
            values.Sum(v => Math.Pow((double)(v - expectedMean), 2)) / (values.Length - 1));

        target.LaboratoryPointCount.Should().Be(values.Length);
        target.LaboratoryMean.Should().BeApproximately(expectedMean, 0.0001m);
        target.LaboratorySd.Should().BeApproximately(expectedSd, 0.0001m);
    }

    [Fact]
    public void EffectiveStatistics_PreferTheInsertUntilEnoughOwnPointsExist()
    {
        // Below twenty points the laboratory's own SD is not yet trustworthy, so the
        // manufacturer's assigned values are used — which is the conventional threshold.
        var target = NewTarget();
        target.AssignedMean = 5.0m;
        target.AssignedSd = 0.2m;

        for (var i = 0; i < 5; i++) target.AccumulatePoint(5.9m);

        target.EffectiveMean.Should().Be(5.0m);
        target.EffectiveSd.Should().Be(0.2m);
    }

    [Fact]
    public void EffectiveStatistics_SwitchToTheLaboratoryValuesOnceEstablished()
    {
        var target = NewTarget();
        target.AssignedMean = 5.0m;
        target.AssignedSd = 0.2m;

        for (var i = 0; i < QcTarget.MinimumPointsForLaboratoryStatistics; i++)
            target.AccumulatePoint(i % 2 == 0 ? 5.8m : 6.0m);

        target.EffectiveMean.Should().NotBe(5.0m);
        target.EffectiveMean.Should().BeApproximately(5.9m, 0.01m);
    }

    [Fact]
    public void ZScore_IsNullWhenNoTargetIsEstablished()
    {
        // No target means the point cannot be interpreted at all, which must not silently become
        // a Z-score of zero — that would read as perfectly in control.
        NewTarget().ZScore(5.0m).Should().BeNull();
    }

    [Fact]
    public void ZScore_UsesTheEffectiveMeanAndSd()
    {
        var target = NewTarget();
        target.AssignedMean = 5.0m;
        target.AssignedSd = 0.25m;

        target.ZScore(5.5m).Should().Be(2.0m);
        target.ZScore(4.5m).Should().Be(-2.0m);
        target.ZScore(5.0m).Should().Be(0m);
    }

    [Fact]
    public void CvPercent_IsComputedFromTheEffectiveStatistics()
    {
        var target = NewTarget();
        target.AssignedMean = 100m;
        target.AssignedSd = 3m;

        target.CvPercent.Should().Be(3.00m);
    }

    [Fact]
    public void CvAcceptability_FailsWhenTheImprecisionExceedsTheSpecification()
    {
        var target = NewTarget();
        target.AssignedMean = 100m;
        target.AssignedSd = 8m;
        target.MaxAcceptableCvPercent = 5m;

        target.IsCvAcceptable.Should().BeFalse();
    }
}
