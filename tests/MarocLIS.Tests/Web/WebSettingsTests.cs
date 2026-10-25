using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Web.Components.Screens.Admin;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Paramètres » in the browser: the tabs offered with their rights, the licence's with its own
/// alone, the tab named in the address opened, the printers and the restore said to be the Windows
/// server's, and a discount ceiling out of range refused next to its box before anything is saved.
/// </summary>
public sealed class WebSettingsTests : WebAdminContext
{
    private static readonly string[] Settings = [Permissions.Administration.ManageSettings];

    private static IReadOnlyList<string> TabsOf(IRenderedComponent<MarocLIS.Web.Components.Screens.Admin.Settings> page) =>
        [.. page.FindAll("[data-tab]").Select(t => t.GetAttribute("data-tab")!)];

    [Fact]
    public void TheAdministrator_IsOfferedEveryTab_AndTheAddressOpensTheOneItNames()
    {
        SignIn(AppLanguage.French, Administrator);

        var page = Render<MarocLIS.Web.Components.Screens.Admin.Settings>(p => p.Add(s => s.Tab, "licence"));

        TabsOf(page).Should().Equal("Site", "Numbering", "Settings", "InvoicePrint", "Languages", "Storage", "Printers", "Backups", "Licence");
        page.Find("[data-tab=Licence]").ClassList.Should().Contain("is-selected");
        page.Find("[data-state=facts]").Should().NotBeNull();
        page.Find("[data-action='Licence.Import']").Should().NotBeNull();
    }

    [Fact]
    public void TheLicenceRightAlone_OpensTheLicenceTabAndNoOther()
    {
        SignIn(AppLanguage.French, Permissions.Administration.License);

        var page = Render<MarocLIS.Web.Components.Screens.Admin.Settings>();

        TabsOf(page).Should().Equal("Licence");
        page.Find("[data-tab=Licence]").ClassList.Should().Contain("is-selected");
    }

    [Fact]
    public void ATabWithoutItsRight_IsNotOpenedFromTheAddress()
    {
        SignIn(AppLanguage.French, Settings);

        var page = Render<MarocLIS.Web.Components.Screens.Admin.Settings>(p => p.Add(s => s.Tab, "licence"));

        TabsOf(page).Should().NotContain(["Licence", "Backups"]);
        page.Find("[data-tab=Site]").ClassList.Should().Contain("is-selected");
    }

    [Fact]
    public void ThePrinters_AreSaidToBeEachWindowsWorkstations()
    {
        SignIn(AppLanguage.French, Settings);

        var page = Render<MarocLIS.Web.Components.Screens.Admin.Settings>(p => p.Add(s => s.Tab, "printers"));

        page.Find("[data-state=printers-workstation]").TextContent.Should().Contain("chaque poste Windows");
    }

    [Fact]
    public void TheRestore_IsSaidToBeTheServersWorkstation()
    {
        SignIn(AppLanguage.French, Administrator);

        Engine.On(nameof(IDatabaseBackupEngine.GetServerInfoAsync), _ =>
            new BackupServerInfo("MarocLIS", "SRV-LABO", "Express", false, "maroclis", true, true, null));
        Engine.On(nameof(IDatabaseBackupEngine.GetServerTimeAsync), _ => new DateTime(2026, 10, 18, 9, 0, 0));

        var page = Render<MarocLIS.Web.Components.Screens.Admin.Settings>(p => p.Add(s => s.Tab, "backups"));

        page.Find("[data-state=restore-note]").TextContent.Should().Contain("La restauration se fait sur le serveur (SRV-LABO)");
        page.FindAll("[data-action*=Restore]").Should().BeEmpty("a restore replaces the database and is run on the server itself");
        page.Find("[data-action='Backup.TestFolder']").Should().NotBeNull();
    }

    [Fact]
    public void ADiscountCeilingOutOfRange_IsRefusedNextToItsBox_AndNothingIsSaved()
    {
        SignIn(AppLanguage.French, Settings);
        PrintQueries.On(nameof(IInvoicePrintSettingsQueries.GetSettingValuesAsync), args =>
            (string)args[0]! == DiscountCapSettings.Prefix
                ? new Dictionary<string, string?> { [DiscountCapSettings.MaxPercentKey] = "20" }
                : new Dictionary<string, string?>());

        var page = Render<MarocLIS.Web.Components.Screens.Admin.Settings>(p => p.Add(s => s.Tab, "invoiceprint"));
        page.Find("[data-field=cap-percent]").GetAttribute("value").Should().Be("20");
        page.Find("[data-field=cap-percent]").Change("150");
        page.Find("[data-action='Action.Save']").Click();

        page.Find("[data-refusal=cap-percent]").Should().NotBeNull();
        Work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
        Writer.Calls.Should().BeEmpty();
    }

    [Fact]
    public void InArabic_TheTabsAreNamedInArabic_AndTheScreenIsMirrored()
    {
        SignIn(AppLanguage.Arabic, Settings);

        var page = Render<MarocLIS.Web.Components.Screens.Admin.Settings>(p => p.Add(s => s.Tab, "printers"));

        page.Find("[data-tab=Printers]").TextContent.Should().MatchRegex(@"\p{IsArabic}");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }
}
