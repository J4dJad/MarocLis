using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// The licence's life as MarocLIS reads it, day by day: valid, the orange month before the end,
/// the red fortnight of grace, then the read-only mode with its week to clear the bench — and the
/// clock that cannot be put back, the server it is bound to, the numbers revoked.
/// </summary>
public sealed class LicenceEvaluatorTests
{
    private static readonly DateOnly End = Today.AddDays(40);

    private static LicenceStatus On(DateOnly day, LicenceTerms? terms = null) =>
        Evaluate(Facts(Sign(terms ?? Annual(40))) with { Today = day });

    [Fact]
    public void NoLicence_OnAnEmptyDatabase_AsksForTheImportScreen()
    {
        var status = Evaluate(Facts(null));

        status.State.Should().Be(LicenceState.Missing);
        status.NeedsImportScreen.Should().BeTrue();
        status.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void NoLicence_OnADatabaseWithPatients_IsReadOnly_NeverTheImportScreen()
    {
        var status = Evaluate(Facts(null) with { HasPatients = true, RestrictedSince = Today.AddDays(-3) });

        status.NeedsImportScreen.Should().BeFalse("a laboratory is never kept from its results");
        status.IsReadOnly.Should().BeTrue();
        status.BenchClearingUntil.Should().Be(Today.AddDays(4), "seven days from the first day it was seen");
        status.InBenchClearing.Should().BeTrue();
    }

    [Fact]
    public void AValidLicence_RunsInFull_WithItsModulesAndWorkstations()
    {
        var status = Evaluate(Facts(Sign(Annual(100, LicenceModules.Instruments, "ModuleDuFutur"))));

        status.State.Should().Be(LicenceState.Valid);
        status.Mode.Should().Be(LicenceMode.Full);
        status.DaysRemaining.Should().Be(100);
        status.IsExpiringSoon.Should().BeFalse();
        status.WorkstationLimit.Should().Be(5);
        status.Modules.Should().BeEquivalentTo([LicenceModules.Instruments],
                                               "a module this version does not know is ignored, not refused");
        status.IsMarked.Should().BeFalse();
    }

    [Theory]
    [InlineData(-31, false)]
    [InlineData(-30, true)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    public void TheWarning_RunsFromThirtyDaysBeforeTheEnd(int offset, bool warned)
    {
        var status = On(End.AddDays(offset));

        status.State.Should().Be(LicenceState.Valid);
        status.IsExpiringSoon.Should().Be(warned);
        status.Mode.Should().Be(LicenceMode.Full);
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(15, 0)]
    public void AfterTheEnd_FifteenDaysOfGrace_StillInFull(int offset, int left)
    {
        var status = On(End.AddDays(offset));

        status.State.Should().Be(LicenceState.Grace);
        status.Mode.Should().Be(LicenceMode.Full);
        status.GraceDaysRemaining.Should().Be(left);
    }

    [Fact]
    public void AfterTheGrace_ReadOnly_WithAWeekToClearTheBench()
    {
        var first = On(End.AddDays(16));

        first.State.Should().Be(LicenceState.Expired);
        first.IsReadOnly.Should().BeTrue();
        first.WorkstationLimit.Should().BeNull("the read-only mode is the limit then");
        first.BenchClearingUntil.Should().Be(End.AddDays(22));
        first.RequireNewWork().IsFailure.Should().BeTrue();
        first.RequireBenchWork().IsSuccess.Should().BeTrue("results of orders in progress may still be finished");
        first.RequireSettingsChange().IsFailure.Should().BeTrue();

        var later = On(End.AddDays(23));
        later.RequireBenchWork().FirstError.ResourceKey.Should().Be("Licence_ReadOnlyBenchOver");
    }

    [Fact]
    public void TheGraceAndTheBench_AreTheLicencesOwn()
    {
        var terms = Annual(40) with { GraceDays = 5, BenchClearingDays = 2, WarningDays = 10 };

        On(End.AddDays(-11), terms).IsExpiringSoon.Should().BeFalse();
        On(End.AddDays(5), terms).State.Should().Be(LicenceState.Grace);
        On(End.AddDays(6), terms).BenchClearingUntil.Should().Be(End.AddDays(7));
    }

    [Fact]
    public void APerpetualLicence_HasNoEnd_ButCoversUpdatesOnlyUntilItsDate()
    {
        var perpetual = Annual() with { Kind = LicenceKind.Perpetual, EndsOn = null, UpdatesUntil = Today };

        var covered = Evaluate(Facts(Sign(perpetual)) with { ReleaseDate = Today });
        covered.State.Should().Be(LicenceState.Valid);
        covered.DaysRemaining.Should().BeNull();

        var newer = Evaluate(Facts(Sign(perpetual)) with { ReleaseDate = Today.AddDays(1) });
        newer.State.Should().Be(LicenceState.Invalid);
        newer.Problem!.ResourceKey.Should().Be("Licence_UpdatesExpired");
        newer.Terms.Should().NotBeNull("the licence is the vendor's, only not for this version");
    }

    [Fact]
    public void AClockPutBack_DoesNotGoBeforeTheLatestDaySeen()
    {
        var facts = Facts(Sign(Annual(40))) with { Today = End.AddDays(-60), LatestDateSeen = End.AddDays(20) };

        var status = Evaluate(facts);

        status.ClockRolledBack.Should().BeTrue();
        status.Today.Should().Be(End.AddDays(20));
        status.State.Should().Be(LicenceState.Expired, "the day already lived counts, not the clock");
    }

    [Fact]
    public void AClockPutBack_IsHeldByTheLatestOrderAsWell()
    {
        var status = Evaluate(Facts(Sign(Annual(40))) with { Today = End.AddDays(-60), LatestOrderOn = End.AddDays(3) });

        status.Today.Should().Be(End.AddDays(3));
        status.State.Should().Be(LicenceState.Grace);
    }

    [Fact]
    public void AClockADayBehind_IsNotCalledRolledBack()
    {
        // A workstation whose clock is a few hours behind the server's has seen « tomorrow » already.
        var status = Evaluate(Facts(Sign(Annual())) with { LatestDateSeen = Today.AddDays(1) });

        status.ClockRolledBack.Should().BeFalse();
        status.Today.Should().Be(Today.AddDays(1));
    }

    [Fact]
    public void ALicenceForAnotherServer_IsInvalid_AndSaysWhich()
    {
        var status = Evaluate(Facts(Sign(Annual() with { ServerId = "FFFF-FFFF-FFFF-FFFF-FFFF" })));

        status.State.Should().Be(LicenceState.Invalid);
        status.Problem!.ResourceKey.Should().Be("Licence_WrongServer");
        status.Problem.Args.Should().Equal("FFFF-FFFF-FFFF-FFFF-FFFF", ServerId);
        status.IsReadOnly.Should().BeTrue();
        status.Modules.Should().BeEmpty();
    }

    [Fact]
    public void AnUnboundLicence_RunsOnAnyServer()
    {
        Evaluate(Facts(Sign(Annual() with { ServerId = null })) with { ServerId = "0000-0000-0000-0000-0001" })
            .State.Should().Be(LicenceState.Valid);
    }

    [Theory]
    [InlineData("LIC-2026-0042")]
    [InlineData("lic-2026-0042/1")]
    public void ARevokedNumber_IsRefused(string entry)
    {
        var revoked = LicenceTrust.ParseRevoked($"# impayé\n{entry}  # volé\n");

        var status = Evaluate(Facts(Sign(Annual())) with { Revoked = revoked });

        status.State.Should().Be(LicenceState.Invalid);
        status.Problem!.ResourceKey.Should().Be("Licence_Revoked");
    }

    [Fact]
    public void RevokingOneVersion_LeavesTheNextOneValid()
    {
        var revoked = LicenceTrust.ParseRevoked("LIC-2026-0042/1");

        Evaluate(Facts(Sign(Annual() with { Version = 2 })) with { Revoked = revoked })
            .State.Should().Be(LicenceState.Valid);
    }

    [Fact]
    public void ALicenceNotYetStarted_IsNotInForce()
    {
        Evaluate(Facts(Sign(Annual() with { StartsOn = Today.AddDays(3) })))
            .Problem!.ResourceKey.Should().Be("Licence_NotYetValid");
    }

    [Theory]
    [InlineData(200, LicenceState.Valid)]
    [InlineData(201, LicenceState.Invalid)]
    public void ATrainingLicence_IsRefusedOnADatabaseOfMoreThan200Orders(int orders, LicenceState expected)
    {
        var training = Annual() with { Kind = LicenceKind.Training, EndsOn = null, Workstations = 1, ServerId = null };

        var status = Evaluate(Facts(Sign(training)) with { OrderCount = orders, HasPatients = true });

        status.State.Should().Be(expected);
        if (expected == LicenceState.Invalid)
            status.Problem!.ResourceKey.Should().Be("Licence_TrainingTooManyOrders");
        else
            status.IsMarked.Should().BeTrue("a training licence marks its reports like a trial");
    }

    [Fact]
    public void ATrialLicence_IsMarked()
    {
        var trial = Annual(30) with { Kind = LicenceKind.Trial, ServerId = null };

        Evaluate(Facts(Sign(trial))).IsMarked.Should().BeTrue();
    }

    [Fact]
    public void AnInconsistentLicence_IsRefusedForItsFirstProblem()
    {
        Evaluate(Facts(Sign(Annual() with { Workstations = 0 }))).Problem!.ResourceKey
            .Should().Be("Licence_WorkstationsInvalid");
        Evaluate(Facts(Sign(Annual() with { Kind = LicenceKind.Training, Workstations = 3, EndsOn = null })))
            .Problem!.ResourceKey.Should().Be("Licence_TrainingOneWorkstation");
    }
}
