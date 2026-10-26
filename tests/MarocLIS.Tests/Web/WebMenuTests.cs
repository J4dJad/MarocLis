using FluentAssertions;
using MarocLIS.Application.Navigation;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Web.Navigation;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The web interface's menu and the screens its addresses open: the Windows shell's menu, cut by
/// the same rights and licence modules, and a typed address refused exactly where the menu hides.
/// </summary>
public sealed class WebMenuTests
{
    private static LicenceStatus WithModules(params string[] modules) => FullStatus() with
    {
        Modules = new HashSet<string>(modules, StringComparer.Ordinal)
    };

    private static IEnumerable<string> Keys(IReadOnlyList<WebMenuGroup> groups) =>
        groups.SelectMany(g => g.Items).Select(i => i.Entry.Key);

    [Fact]
    public void TheMenu_ShowsOnlyTheScreensOfTheUsersRights()
    {
        var biller = new User(permissions: Permissions.Billing.View);

        var keys = Keys(WebMenu.Build(biller, FullStatus())).ToList();

        // « billing » by its right; the dashboard, statistics and the like have no right of their own.
        keys.Should().Contain("billing");
        keys.Should().NotContain(["users", "validation", "cash", "interfaces", "settings"]);
        keys.Should().Equal(NavigationMenu.Entries
            .Where(e => !e.IsGroupHeader && NavigationMenu.IsPermitted(e, biller, FullStatus()))
            .Select(e => e.Key), "the web menu is the shell's rule, not one of its own");
    }

    [Fact]
    public void AnAdministrator_SeesEveryScreen_AndAGroupWithNothingToOpenIsLeftOut()
    {
        var everything = new User(permissions: [.. Permissions.All]);
        var all = WebMenu.Build(everything, FullStatus());

        Keys(all).Should().Equal(NavigationMenu.Entries.Where(e => !e.IsGroupHeader).Select(e => e.Key));
        all.Should().OnlyContain(g => g.Items.Count > 0);

        var biller = WebMenu.Build(new User(permissions: Permissions.Billing.View), FullStatus());
        biller.Select(g => g.Header?.Key).Should().Contain("group.billing").And.NotContain("group.quality");
    }

    [Fact]
    public void AScreenOfAModuleOutsideTheLicence_IsNotInTheMenu()
    {
        var everything = new User(permissions: [.. Permissions.All]);
        var withoutQuality = LicenceModules.All.Where(m => m != LicenceModules.Quality).ToArray();

        var keys = Keys(WebMenu.Build(everything, WithModules(withoutQuality))).ToList();

        keys.Should().NotContain(["qc", "nonconformities", "eqa", "documents"]);
        keys.Should().Contain(["billing", "users"]);
    }

    [Fact]
    public void ThePurchasingScreens_AreInTheMenu_ByTheirRights()
    {
        var storeman = Keys(WebMenu.Build(new User(permissions: Permissions.Inventory.View), FullStatus())).ToList();
        storeman.Should().Contain("purchaseorders").And.NotContain(["quotes", "supplierinvoices", "supplierevaluation"]);

        var payer = Keys(WebMenu.Build(new User(permissions: Permissions.Inventory.ApprovePayments), FullStatus())).ToList();
        payer.Should().Contain("supplierinvoices").And.NotContain(["purchaseorders", "quotes", "supplierevaluation"]);

        var buyer = Keys(WebMenu.Build(new User(permissions: [Permissions.Inventory.ManageQuotes, Permissions.Inventory.ManagePurchaseOrders]),
                                       FullStatus())).ToList();
        buyer.Should().Contain(["quotes", "supplierevaluation"]);
    }

    [Fact]
    public void TheEquipmentScreens_AreInTheMenu_ByTheirRights()
    {
        var technician = Keys(WebMenu.Build(new User(permissions: Permissions.Equipment.View), FullStatus())).ToList();
        technician.Should().Contain("equipment").And.NotContain("contracts");

        var manager = Keys(WebMenu.Build(new User(permissions: Permissions.Equipment.ViewContracts), FullStatus())).ToList();
        manager.Should().Contain("contracts").And.NotContain("equipment");

        var collector = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Collect), FullStatus())).ToList();
        collector.Should().NotContain(["equipment", "contracts"]);
    }

    [Fact]
    public void TheFinanceScreens_AreInTheMenu_ByTheirRights()
    {
        var accountant = Keys(WebMenu.Build(new User(permissions: Permissions.Accounting.Export), FullStatus())).ToList();
        accountant.Should().Contain(["accountingexport", "accountingsettings"]).And.NotContain("fixedassets");

        var manager = Keys(WebMenu.Build(new User(permissions: Permissions.Assets.View), FullStatus())).ToList();
        manager.Should().Contain("fixedassets").And.NotContain(["accountingexport", "accountingsettings"]);

        var collector = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Collect), FullStatus())).ToList();
        collector.Should().NotContain(["accountingexport", "accountingsettings", "fixedassets"]);
    }

    [Fact]
    public void TheTreasuryAndMarginScreens_AreInTheMenu_ByTheirRights()
    {
        var reader = Keys(WebMenu.Build(new User(permissions: Permissions.Treasury.View), FullStatus())).ToList();
        reader.Should().Contain(["bankreconciliation", "cashforecast"]).And.NotContain("costmargin");

        var treasurer = Keys(WebMenu.Build(new User(permissions: Permissions.Treasury.Manage), FullStatus())).ToList();
        treasurer.Should().Contain(["bankreconciliation", "cashforecast"]);

        var director = Keys(WebMenu.Build(new User(permissions: Permissions.Steering.View), FullStatus())).ToList();
        director.Should().Contain("costmargin").And.NotContain(["bankreconciliation", "cashforecast"]);

        var collector = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Collect), FullStatus())).ToList();
        collector.Should().NotContain(["bankreconciliation", "cashforecast", "costmargin"]);
    }

    [Fact]
    public void TheSerumBankAndTheSubcontracting_AreInTheMenu_ByTheirRights()
    {
        var receiver = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Receive), FullStatus())).ToList();
        receiver.Should().Contain("serumbank").And.NotContain("subcontracting");

        var sender = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Subcontract), FullStatus())).ToList();
        sender.Should().Contain("subcontracting").And.NotContain("serumbank");

        var collector = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Collect), FullStatus())).ToList();
        collector.Should().NotContain(["serumbank", "subcontracting"]);
    }

    [Fact]
    public void TheStaffFilesTheHabilitationsAndTheHrDeadlines_AreInTheMenu_ForWhoeverReadsTheFiles()
    {
        var hr = Keys(WebMenu.Build(new User(permissions: Permissions.HumanResources.View), FullStatus())).ToList();
        hr.Should().Contain(["personnel", "habilitations", "hrdeadlines"]);

        var employee = Keys(WebMenu.Build(new User(permissions: Permissions.HumanResources.RequestLeave), FullStatus())).ToList();
        employee.Should().NotContain(["personnel", "habilitations", "hrdeadlines"], "asking for one's leave opens no one's file");

        var collector = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Collect), FullStatus())).ToList();
        collector.Should().NotContain(["personnel", "habilitations", "hrdeadlines"]);
    }

    [Fact]
    public void TheDashboard_IsEveryonesAndTheStatisticsAndSteering_AreInTheMenu_ByTheirRights()
    {
        var collector = Keys(WebMenu.Build(new User(permissions: Permissions.Samples.Collect), FullStatus())).ToList();
        collector.Should().Contain("dashboard").And.NotContain(["statistics", "steering"]);

        var statistician = Keys(WebMenu.Build(new User(permissions: Permissions.Statistics.View), FullStatus())).ToList();
        statistician.Should().Contain(["dashboard", "statistics"]).And.NotContain("steering");

        var director = Keys(WebMenu.Build(new User(permissions: Permissions.Steering.View), FullStatus())).ToList();
        director.Should().Contain(["dashboard", "steering", "costmargin"]).And.NotContain("statistics");
    }

    [Fact]
    public void EveryScreen_LinksToItsPlaceholder_WithTheShellsIcon()
    {
        var items = WebMenu.Build(new User(permissions: [.. Permissions.All]), FullStatus()).SelectMany(g => g.Items).ToList();

        items.Should().OnlyContain(i => i.Href == "ecran/" + i.Entry.Key);
        items.Single(i => i.Entry.Key == "billing").Icon.Should().Be(IconCatalog.Navigation["billing"]);
        items.Where(i => IconCatalog.Navigation.ContainsKey(i.Entry.Key)).Should().OnlyContain(i => i.Icon != null);
    }

    [Theory]
    [InlineData("billing", ScreenDecision.Open)]
    [InlineData("users", ScreenDecision.NoPermission)]
    [InlineData("qc", ScreenDecision.NotLicensed)]
    [InlineData("group.billing", ScreenDecision.NotFound)]
    [InlineData("nothing-here", ScreenDecision.NotFound)]
    [InlineData("", ScreenDecision.NotFound)]
    public void ATypedAddress_OpensWhatTheMenuShows_AndNothingElse(string key, ScreenDecision expected)
    {
        var user = new User(permissions: [Permissions.Billing.View, Permissions.Quality.EnterQc]);
        var licence = WithModules([.. LicenceModules.All.Where(m => m != LicenceModules.Quality)]);

        ScreenAccess.Decide(key, user, licence).Decision.Should().Be(expected);
    }

    [Fact]
    public void ATypedAddress_IsDecidedForEveryScreen_AsTheMenuIs()
    {
        var user = new User(permissions: [Permissions.Billing.View, Permissions.Results.Enter, Permissions.Quality.EnterQc]);
        var licence = WithModules(LicenceModules.Instruments, LicenceModules.Web);
        var shown = Keys(WebMenu.Build(user, licence)).ToHashSet();

        foreach (var entry in NavigationMenu.Entries.Where(e => !e.IsGroupHeader))
        {
            var opens = ScreenAccess.Decide(entry.Key, user, licence).Decision == ScreenDecision.Open;
            opens.Should().Be(shown.Contains(entry.Key), $"« {entry.Key} » opens exactly when it is in the menu");
        }
    }

    [Fact]
    public void EveryIconOfTheMenu_IsExportedForTheBrowser()
    {
        var icons = Path.Combine(RepositoryRoot(), "src", "MarocLIS.Web", "wwwroot", "icons");

        var missing = IconCatalog.Navigation.Values.Distinct()
            .Concat([Glyph.FlaskConical, Glyph.LogIn, Glyph.LogOut, Glyph.KeyRound, Glyph.Save, Glyph.Cpu, Glyph.Ban])
            .Where(glyph => !File.Exists(Path.Combine(icons, glyph + ".png")))
            .ToList();

        missing.Should().BeEmpty("each glyph of the shell's menu and of the web pages has its PNG in wwwroot/icons");
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarocLIS.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("MarocLIS.sln not found above the test binaries.");
    }
}
