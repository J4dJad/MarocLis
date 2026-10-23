using FluentAssertions;
using MarocLIS.Application.Licensing;
using MarocLIS.Application.Navigation;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Desktop;

/// <summary>The menu every client shows, and who is shown which entry and which dashboard figure.</summary>
public sealed class NavigationMenuTests
{
    private static LicenceStatus WithModules(params string[] modules) => FullStatus() with
    {
        Modules = new HashSet<string>(modules, StringComparer.Ordinal)
    };

    private static MenuEntry Entry(string key) => NavigationMenu.Find(key)!;

    [Fact]
    public void Menu_KeepsTheShellsOrderAndGroups()
    {
        NavigationMenu.Entries.Select(e => e.Key).Should().Equal(
            "dashboard", "statistics", "steering",
            "group.reception", "patients", "orders", "orderdetail",
            "group.technical", "collection", "reception", "recollections", "serumbank", "subcontracting",
            "entry", "validation", "reports",
            "group.billing", "billing", "cash", "claims",
            "group.quality", "qc", "nonconformities", "eqa", "documents",
            "group.logistics", "inventory", "purchaseorders", "quotes", "supplierinvoices", "supplierevaluation",
            "consumption", "inventorycounts", "temperatures", "equipment", "contracts",
            "group.personnel", "personnel", "habilitations", "hrdeadlines", "leave", "planning", "payroll",
            "accidents",
            "group.relations", "prescriberstats", "relations", "patientmessaging",
            "group.finance", "accountingexport", "accountingsettings", "fixedassets", "bankreconciliation",
            "cashforecast", "cashreview", "costmargin",
            "group.administration", "users", "audit", "catalog", "interfaces", "masterdata", "migration",
            "settings");

        NavigationMenu.Entries.Where(e => e.IsGroupHeader).Should().OnlyContain(e => e.Key.StartsWith("group."));
        NavigationMenu.Entries.Select(e => e.Key).Should().OnlyHaveUniqueItems();
        NavigationMenu.GroupOf("collection").Should().Be("group.technical");
        NavigationMenu.GroupOf("dashboard").Should().BeNull();
        NavigationMenu.GroupEntries("group.billing").Select(e => e.Key).Should().Equal("billing", "cash", "claims");
    }

    [Fact]
    public void AnEntry_IsShownWithOneOfItsRights_AndAnEntryWithoutAnyToAll()
    {
        var nobody = new User(permissions: []);
        var rejecter = new User(permissions: Permissions.Samples.Reject);

        NavigationMenu.IsPermitted(Entry("dashboard"), nobody, licence: null).Should().BeTrue();
        NavigationMenu.IsPermitted(Entry("recollections"), nobody, licence: null).Should().BeFalse();
        NavigationMenu.IsPermitted(Entry("recollections"), rejecter, licence: null).Should().BeTrue();
        NavigationMenu.IsPermitted(Entry("settings"), new User(permissions: Permissions.Administration.License), null)
            .Should().BeTrue("the licence tab is reachable with its own right alone");
    }

    [Fact]
    public void AModuleTheLicenceDoesNotOpen_IsHidden_EvenWithTheRight()
    {
        var user = new User(permissions: Permissions.Instruments.View);
        var interfaces = Entry("interfaces");

        NavigationMenu.IsPermitted(interfaces, user, WithModules()).Should().BeFalse();
        NavigationMenu.IsPermitted(interfaces, user, WithModules(LicenceModules.Instruments)).Should().BeTrue();
        NavigationMenu.IsPermitted(interfaces, user, licence: null).Should().BeTrue("no licensing, only the rights");
    }

    [Fact]
    public void Access_SaysWhyAnEntryIsClosed_TheRightBeforeTheLicence()
    {
        var instruments = new User(permissions: Permissions.Instruments.View);
        var nobody = new User(permissions: []);

        NavigationMenu.Access(Entry("interfaces"), instruments, WithModules(LicenceModules.Instruments))
            .Should().Be(MenuAccess.Permitted);
        NavigationMenu.Access(Entry("interfaces"), instruments, WithModules()).Should().Be(MenuAccess.NotLicensed);
        NavigationMenu.Access(Entry("interfaces"), nobody, WithModules()).Should().Be(MenuAccess.NoPermission,
            "a screen the user has no right to is refused for that, whatever the licence");
        NavigationMenu.Access(Entry("dashboard"), nobody, WithModules()).Should().Be(MenuAccess.Permitted);

        foreach (var entry in NavigationMenu.Entries)
            NavigationMenu.IsPermitted(entry, instruments, WithModules())
                .Should().Be(NavigationMenu.Access(entry, instruments, WithModules()) == MenuAccess.Permitted);
    }

    [Fact]
    public void EachLanguage_IsNamedInItself_ForEveryClientsList()
    {
        MarocLIS.Application.Administration.LanguageSettings.OwnName(MarocLIS.Domain.Common.AppLanguage.Arabic)
            .Should().Be("العربية");
        MarocLIS.Application.Administration.LanguageSettings.OwnName(MarocLIS.Domain.Common.AppLanguage.English)
            .Should().Be("English");
        MarocLIS.Application.Administration.LanguageSettings.OwnName(MarocLIS.Domain.Common.AppLanguage.French)
            .Should().Be("Français");
    }

    [Fact]
    public void AGroup_IsShownWhenOneOfItsScreensIs_WhateverItsHeaderAsks()
    {
        // A phlebotomist: Collection without Results.View, the right the technical header names.
        var phlebotomist = new User(permissions: Permissions.Samples.Collect);

        NavigationMenu.IsGroupShown("group.technical", phlebotomist, null).Should().BeTrue();
        NavigationMenu.IsGroupShown("group.billing", phlebotomist, null).Should().BeFalse();

        var payroll = new User(permissions: Permissions.HumanResources.Salary);
        NavigationMenu.IsGroupShown("group.personnel", payroll, WithModules(LicenceModules.HumanResources))
            .Should().BeFalse("payroll is a module of its own");
        NavigationMenu.IsGroupShown("group.personnel", payroll, WithModules(LicenceModules.Payroll)).Should().BeTrue();
    }

    [Fact]
    public void EveryEntryTheLicenceGates_IsAMenuEntry()
    {
        var screens = NavigationMenu.Entries.Where(e => !e.IsGroupHeader).Select(e => e.Key).ToHashSet();
        LicenceNavigation.ModuleOfEntry.Keys.Should().BeSubsetOf(screens);
    }

    // ---- Dashboard ---------------------------------------------------------------------------------

    [Fact]
    public void AFigure_OpensItsScreen_OnlyToWhomTheMenuOffersIt()
    {
        var screens = NavigationMenu.Entries.Where(e => !e.IsGroupHeader).Select(e => e.Key).ToHashSet();
        DashboardRights.TargetKeys.Should().BeSubsetOf(screens);

        var biologist = new User(permissions: Permissions.Results.BiologicalValidate);
        DashboardRights.CanOpen("validation", biologist).Should().BeTrue();
        DashboardRights.CanOpen("entry", biologist).Should().BeFalse();
        DashboardRights.CanOpen("qc", new User(permissions: Permissions.Quality.ReviewQc)).Should().BeTrue();

        DashboardRights.CanOpen("validation", null).Should().BeFalse("nobody signed in");
        DashboardRights.CanOpen(null, biologist).Should().BeFalse();
        DashboardRights.CanOpen("users", new User(permissions: Permissions.Administration.ViewUsers))
            .Should().BeFalse("no figure leads to the users");
    }

    [Fact]
    public void AFigure_IsShownWithItsRight_AndAFigureWithoutOneToAll()
    {
        var cashier = new User(permissions: Permissions.Billing.View);

        DashboardRights.MaySee("Dashboard.CriticalPending", cashier).Should().BeFalse();
        DashboardRights.MaySee("Dashboard.Receivables", cashier).Should().BeTrue();
        DashboardRights.MaySee("Dashboard.Receivables", null).Should().BeFalse();
        DashboardRights.MaySee("Dashboard.SomethingUnlisted", null).Should().BeTrue();
    }
}
