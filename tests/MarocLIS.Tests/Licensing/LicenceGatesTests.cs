using FluentAssertions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Licensing;

/// <summary>
/// What the licence opens and shuts: the modules in the menu, the gates the services ask, the
/// banners, and the count of the workstations.
/// </summary>
public sealed class LicenceGatesTests
{
    private static LicenceStatus WithModules(params string[] modules) => FullStatus() with
    {
        Modules = new HashSet<string>(modules, StringComparer.Ordinal)
    };

    // ---- Modules -----------------------------------------------------------------------------------

    [Fact]
    public void TheBase_IsAlwaysInTheMenu_TheModulesOnlyWhenLicensed()
    {
        var essential = WithModules();

        foreach (var baseEntry in new[] { "dashboard", "patients", "orders", "entry", "validation", "reports",
                                          "billing", "cash", "statistics", "catalog", "users", "audit",
                                          "masterdata", "settings", "migration", "collection", "serumbank" })
            LicenceNavigation.IsLicensed(baseEntry, essential).Should().BeTrue(baseEntry);

        LicenceNavigation.IsLicensed("interfaces", essential).Should().BeFalse();
        LicenceNavigation.IsLicensed("claims", essential).Should().BeFalse();
        LicenceNavigation.IsLicensed("payroll", essential).Should().BeFalse();
        LicenceNavigation.IsLicensed("cashreview", essential).Should().BeFalse("the cash review belongs to Finance");

        var professional = WithModules([.. LicencePacks.Professional.Modules]);
        LicenceNavigation.IsLicensed("interfaces", professional).Should().BeTrue();
        LicenceNavigation.IsLicensed("qc", professional).Should().BeTrue();
        LicenceNavigation.IsLicensed("inventory", professional).Should().BeFalse();
    }

    [Fact]
    public void EveryOptionalModule_HoldsAMenuEntry_ExceptThoseGatedElsewhere()
    {
        // Mobile validation (the API), Arabic (the languages) and multi-site (site creation) have no
        // screen of their own in the workstation's menu.
        var inMenu = LicenceNavigation.ModuleOfEntry.Values.Distinct().ToHashSet();

        LicenceModules.All.Except(inMenu).Should().BeEquivalentTo(
            [LicenceModules.MobileValidation, LicenceModules.Arabic, LicenceModules.MultiSite]);
    }

    [Fact]
    public void ThePacks_AreTheDesignsPresets_AndListOnlyKnownModules()
    {
        LicencePacks.Essential.Modules.Should().BeEmpty();
        LicencePacks.Essential.Workstations.Should().Be(2);
        LicencePacks.Professional.Modules.Should().HaveCount(5);
        LicencePacks.Professional.Workstations.Should().Be(5);
        LicencePacks.Complete.Modules.Should().HaveCount(12).And.NotContain(LicenceModules.MultiSite);
        LicencePacks.Complete.Workstations.Should().Be(10);
        LicencePacks.Defaults.SelectMany(p => p.Modules).Should().OnlyContain(m => LicenceModules.IsKnown(m));
    }

    [Fact]
    public void AMissingModule_IsRefusedByName()
    {
        var refused = WithModules().RequireModule(LicenceModules.Instruments);

        refused.FirstError.ResourceKey.Should().Be("Licence_ModuleMissing");
        refused.FirstError.Args.Should().Equal(LicenceModule.Instruments);
    }

    [Fact]
    public void WithoutALicenceState_NothingIsRefused()
    {
        ILicenceState? none = null;

        none.RequireNewWork().IsSuccess.Should().BeTrue();
        none.RequireModule(LicenceModules.Payroll).IsSuccess.Should().BeTrue();
        none.HasModule(LicenceModules.Arabic).Should().BeTrue();
    }

    [Fact]
    public void ThePhone_NeedsTheModule_AndALicenceInForce()
    {
        WithModules().RequireMobileValidation().FirstError.ResourceKey.Should().Be("Licence_ModuleMissing");
        FullStatus().RequireMobileValidation().IsSuccess.Should().BeTrue();
        ReadOnlyStatus(Today.AddDays(3)).RequireMobileValidation().FirstError.ResourceKey
            .Should().Be("Licence_ReadOnlyMobile", "the bench days are the workstations', not the phone's");
    }

    [Fact]
    public void AnalyserResults_NeedTheModule_AndALicenceInForce()
    {
        WithModules().RequireInstrumentResults().FirstError.ResourceKey.Should().Be("Licence_ModuleMissing");
        ReadOnlyStatus(Today.AddDays(3)).RequireInstrumentResults().FirstError.ResourceKey
            .Should().Be("Licence_ReadOnlyInstruments");
    }

    [Fact]
    public void Configuring_NeedsTheModule_ThenTheSettingsOpen()
    {
        ILicenceState readOnly = new FixedLicenceState(ReadOnlyStatus());
        ILicenceState essential = new FixedLicenceState(WithModules());

        essential.RequireConfiguration(LicenceModules.Instruments).FirstError.ResourceKey.Should().Be("Licence_ModuleMissing");
        readOnly.RequireConfiguration(LicenceModules.Instruments).FirstError.ResourceKey.Should().Be("Licence_ReadOnlySettings");
    }

    [Fact]
    public void TheLicencesMarkers_AreNotOrdinarySettings()
    {
        ConfigurationService.IsDedicated("Licence.InstallationId").Should().BeTrue();
        ConfigurationService.IsDedicated("Licence.LastSeenOn").Should().BeTrue();
    }

    // ---- Banners -----------------------------------------------------------------------------------

    [Fact]
    public void TheOrangeBanner_IsForAdministratorsAndBiologists_TheRedOnesForEveryone()
    {
        var soon = Evaluate(Facts(Sign(Annual(12))));

        LicenceNavigation.BannerFor(soon, administrator: false, biologist: false).Should().BeNull();
        var orange = LicenceNavigation.BannerFor(soon, administrator: false, biologist: true)!;
        orange.Level.Should().Be(LicenceBannerLevel.Warning);
        orange.ResourceKey.Should().Be("Licence.Banner.ExpiresIn");
        orange.Arguments.Should().Equal(12);

        LicenceNavigation.BannerFor(Evaluate(Facts(Sign(Annual(2)))), true, false)!.ResourceKey
            .Should().Be("Licence.Banner.ExpiresIn");
        LicenceNavigation.BannerFor(Evaluate(Facts(Sign(Annual(1)))), true, false)!.ResourceKey
            .Should().Be("Licence.Banner.ExpiresTomorrow");
        LicenceNavigation.BannerFor(Evaluate(Facts(Sign(Annual(0)))), true, false)!.ResourceKey
            .Should().Be("Licence.Banner.ExpiresToday", "the licence runs through its last day");

        var grace = Evaluate(Facts(Sign(Annual(-3))));
        var red = LicenceNavigation.BannerFor(grace, false, false)!;
        red.Level.Should().Be(LicenceBannerLevel.Critical);
        red.ResourceKey.Should().Be("Licence.Banner.Grace");
        red.Arguments.Should().Equal(Today.AddDays(-3), 12);

        var expired = Evaluate(Facts(Sign(Annual(-17))));
        var readOnly = LicenceNavigation.BannerFor(expired, false, false)!;
        readOnly.ResourceKey.Should().Be("Licence.Banner.ReadOnlyBench");
        readOnly.CauseKey.Should().Be("Licence.Cause.Expired");

        LicenceNavigation.BannerFor(Evaluate(Facts(Sign(Annual(-40)))), false, false)!.ResourceKey
            .Should().Be("Licence.Banner.ReadOnly");
        LicenceNavigation.BannerFor(Evaluate(Facts(Sign(Annual(100)))), true, true).Should().BeNull();
    }

    [Fact]
    public void AnInvalidOrMissingLicence_IsSaidAtSignIn_AndInTheBanner()
    {
        var invalid = Evaluate(Facts(SignWithStranger(Annual())));
        LicenceNavigation.SignInNoticeKey(invalid).Should().Be("Licence.Login.ReadOnly");
        LicenceNavigation.BannerFor(invalid, false, false)!.CauseKey.Should().Be("Licence.Cause.Invalid");

        var missingWithData = Evaluate(Facts(null) with { HasPatients = true });
        LicenceNavigation.SignInNoticeKey(missingWithData).Should().Be("Licence.Login.ReadOnly");
        LicenceNavigation.SignInNoticeKey(Evaluate(Facts(Sign(Annual(-2))))).Should().Be("Licence.Login.Grace");
        LicenceNavigation.SignInNoticeKey(Evaluate(Facts(Sign(Annual(5))))).Should().BeNull();
    }

    [Fact]
    public void TheDaysRemaining_AreGreenThenOrangeThenRed()
    {
        LicenceNavigation.DaysLevel(Evaluate(Facts(Sign(Annual(31))))).Should().Be(LicenceBannerLevel.Normal);
        LicenceNavigation.DaysLevel(Evaluate(Facts(Sign(Annual(29))))).Should().Be(LicenceBannerLevel.Warning);
        LicenceNavigation.DaysLevel(Evaluate(Facts(Sign(Annual(-1))))).Should().Be(LicenceBannerLevel.Critical);
    }

    // ---- Workstations ------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(1));

    private static Workstation Seen(string machine, TimeSpan ago, string? user = null)
    {
        var row = new Workstation(machine, Now - ago - TimeSpan.FromHours(1));
        row.Seen(Now - ago, user);
        return row;
    }

    [Fact]
    public void AWorkstation_IsGivenAPlace_WhileFewerThanTheLimitAreActive()
    {
        var rows = new[] { Seen("POSTE-01", TimeSpan.FromMinutes(2), "accueil") };

        var claim = WorkstationClaim.Decide(rows, "POSTE-02", Now, limit: 2);

        claim.Granted.Should().BeTrue();
        claim.InUse.Should().Be(2);
    }

    [Fact]
    public void TheWorkstationOneTooMany_IsRefused_AndShownTheOthers()
    {
        var rows = new[] { Seen("POSTE-01", TimeSpan.FromMinutes(2), "accueil"), Seen("POSTE-02", TimeSpan.FromMinutes(4), "tech") };

        var claim = WorkstationClaim.Decide(rows, "POSTE-03", Now, limit: 2);

        claim.Granted.Should().BeFalse();
        claim.InUse.Should().Be(2);
        claim.Limit.Should().Be(2);
        claim.Active.Select(a => (a.MachineName, a.UserName)).Should().BeEquivalentTo([("POSTE-01", "accueil"), ("POSTE-02", "tech")]);
    }

    [Fact]
    public void AWorkstationAlreadyCounted_KeepsItsPlace_WhatevertheCase()
    {
        var rows = new[] { Seen("POSTE-01", TimeSpan.FromMinutes(2)), Seen("POSTE-02", TimeSpan.FromMinutes(4)) };

        WorkstationClaim.Decide(rows, "poste-02", Now, limit: 2).Granted.Should().BeTrue("two operators on one PC take one place");
    }

    [Fact]
    public void AWorkstationSilentForAQuarterOfAnHour_OrSignedOut_GivesItsPlaceUp()
    {
        var silent = Seen("POSTE-01", Workstation.SilenceTimeout);
        var signedOut = Seen("POSTE-02", TimeSpan.FromMinutes(1));
        signedOut.Release(Now);
        var active = Seen("POSTE-03", Workstation.SilenceTimeout - TimeSpan.FromMinutes(1));

        var claim = WorkstationClaim.Decide([silent, signedOut, active], "POSTE-04", Now, limit: 2);

        claim.Granted.Should().BeTrue();
        claim.Active.Select(a => a.MachineName).Should().Equal("POSTE-03");
    }

    [Fact]
    public void WithoutALimit_EveryWorkstationIsGivenAPlace()
    {
        var rows = Enumerable.Range(1, 30).Select(i => Seen($"POSTE-{i:00}", TimeSpan.FromMinutes(1))).ToArray();

        WorkstationClaim.Decide(rows, "POSTE-99", Now, limit: null).Granted.Should().BeTrue();
    }

    [Fact]
    public void ANewPresence_StartsAfterASilenceOrARelease()
    {
        var row = new Workstation("POSTE-01", Now.AddHours(-3));
        row.Seen(Now.AddHours(-3).AddMinutes(5), "a");
        row.FirstSeenAt.Should().Be(Now.AddHours(-3));

        row.Seen(Now, "b");
        row.FirstSeenAt.Should().Be(Now, "silent for hours, it signed in anew");
        row.LastUserName.Should().Be("b");
    }
}
