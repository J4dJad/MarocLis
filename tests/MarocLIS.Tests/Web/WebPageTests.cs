using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Audit;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Localization;
using MarocLIS.Web.Components.Pages;
using MarocLIS.Web.Security;
using MarocLIS.Web.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Licensing.LicenceTestKit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// The screen an address opens, rendered in memory: the placeholder of a screen not yet in the web
/// version, and the refusal — recorded — of one the operator may not see, typed in the address bar.
/// And the texts of the web pages, present in the three languages.
/// </summary>
public sealed class WebPageTests : BunitContext
{
    private readonly Auditor _auditor = new();
    private LicenceStatus _licence = FullStatus();

    public WebPageTests()
    {
        Services.AddSingleton<ILocalizer>(new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true }));
        Services.AddSingleton<IClock>(new Clock { Now = DateTimeOffset.Now });
        Services.AddSingleton(new PasswordPolicy());
        Services.AddSingleton(new WebDefaults(AppLanguage.French));
        Services.AddSingleton<WebSessionStore>();
        Services.AddSingleton<ISecurityAuditor>(_auditor);
        Services.AddSingleton<ILicenceState>(_ => new FixedLicenceState(_licence));
        Services.AddScoped<IPermissionGuard>(sp => new Guard(sp.GetRequiredService<ICurrentUser>()));
        Services.AddScoped<SessionContext>();
        Services.AddScoped<WebTexts>();
        Services.UsePerRequestCurrentUser();
    }

    private void SignIn(AppLanguage language, params string[] permissions)
    {
        var account = new AuthenticatedSession(Guid.NewGuid(), "nadia", "Nadia", Guid.NewGuid(), "A", language,
                                               ProfessionalQualification.None, new HashSet<string>(permissions), [],
                                               new HashSet<string>(), MustChangePassword: false);
        var session = Services.GetRequiredService<WebSessionStore>()
                              .Open(account, "WEB-AAAAAAAAAA", "WEB-AAAAAAAAAA (192.168.1.20)", language, false);
        Services.GetRequiredService<SessionContext>().Attach(session);
    }

    [Fact]
    public void AScreenOfTheMenu_SaysItIsNotYetInTheWebVersion()
    {
        SignIn(AppLanguage.French, Permissions.Relations.View);

        var page = Render<Screen>(p => p.Add(x => x.Key, "prescriberstats"));

        page.Find("h1").TextContent.Should().Contain("Statistiques prescripteurs");
        page.Find("[data-state=unavailable]").TextContent.Should()
            .Contain("Cet écran n'est pas encore disponible dans la version Web. Utilisez la version Windows.");
        _auditor.Events.Should().BeEmpty();
    }

    [Fact]
    public void AScreenWithoutTheRight_IsRefused_WhenItsAddressIsTyped()
    {
        SignIn(AppLanguage.French, Permissions.Samples.Collect);

        var page = Render<Screen>(p => p.Add(x => x.Key, "billing"));

        page.FindAll("[data-state=unavailable]").Should().BeEmpty();
        page.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit");
        page.Find("h1").TextContent.Should().NotContain("Facturation", "a screen refused is not named");
    }

    [Fact]
    public void QualityControl_TypedWithoutTheRight_IsRefused()
    {
        SignIn(AppLanguage.French, Permissions.Quality.View);

        var page = Render<Screen>(p => p.Add(x => x.Key, "qc"));

        page.FindAll("[data-screen=qc]").Should().BeEmpty();
        page.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit");
        page.Find("h1").TextContent.Should().NotContain("Contrôle", "a screen refused is not named");
    }

    [Theory]
    [InlineData("nonconformities", "Non-conformités")]
    [InlineData("eqa", "Évaluation externe")]
    [InlineData("documents", "Documents qualité")]
    public void TheQualityRegisters_TypedWithoutTheRight_AreRefused(string key, string title)
    {
        SignIn(AppLanguage.French, Permissions.Samples.Collect);

        var page = Render<Screen>(p => p.Add(x => x.Key, key));

        page.FindAll($"[data-screen={key}]").Should().BeEmpty();
        page.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit");
        page.Find("h1").TextContent.Should().NotContain(title, "a screen refused is not named");
    }

    [Theory]
    [InlineData("inventory", "Stocks")]
    [InlineData("consumption", "Consommation")]
    [InlineData("inventorycounts", "Inventaires")]
    [InlineData("temperatures", "Températures")]
    public void TheStockScreens_TypedWithoutTheRight_AreRefused(string key, string title)
    {
        SignIn(AppLanguage.French, Permissions.Samples.Collect);

        var page = Render<Screen>(p => p.Add(x => x.Key, key));

        page.FindAll($"[data-screen={key}]").Should().BeEmpty();
        page.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit");
        page.Find("h1").TextContent.Should().NotContain(title, "a screen refused is not named");
    }

    [Theory]
    [InlineData("purchaseorders", "Bons de commande")]
    [InlineData("quotes", "Demandes de devis")]
    [InlineData("supplierinvoices", "Factures fournisseurs")]
    [InlineData("supplierevaluation", "Évaluation")]
    [InlineData("equipment", "Équipements")]
    [InlineData("contracts", "Contrats automates")]
    [InlineData("accountingexport", "Export comptable")]
    [InlineData("accountingsettings", "Comptes et journaux")]
    [InlineData("fixedassets", "Immobilisations")]
    [InlineData("bankreconciliation", "Rapprochement bancaire")]
    [InlineData("cashforecast", "Prévision de trésorerie")]
    [InlineData("costmargin", "Coût de revient et marge")]
    [InlineData("statistics", "Statistiques d'activité")]
    [InlineData("steering", "Pilotage")]
    [InlineData("serumbank", "Sérothèque")]
    [InlineData("subcontracting", "Sous-traitance")]
    [InlineData("personnel", "Dossiers du personnel")]
    [InlineData("habilitations", "Habilitations")]
    [InlineData("hrdeadlines", "Échéances RH")]
    public void ThePurchasingScreens_TypedWithoutTheRight_AreRefused(string key, string title)
    {
        SignIn(AppLanguage.French, Permissions.Samples.Collect);

        var page = Render<Screen>(p => p.Add(x => x.Key, key));

        page.FindAll($"[data-screen={key}]").Should().BeEmpty();
        page.Find("[data-state=refused]").TextContent.Should().Contain("Vous n'avez pas le droit");
        page.Find("h1").TextContent.Should().NotContain(title, "a screen refused is not named");
    }

    [Fact]
    public void TheHome_LeadsToTheDashboard_TheScreenTheWorkstationOpensOn()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);

        var home = Render<Home>();

        var link = home.Find("[data-action=open-dashboard]");
        link.GetAttribute("href").Should().Be("ecran/dashboard");
        link.TextContent.Should().Contain("Tableau de bord");
        home.Find("h1").TextContent.Should().Contain("Bienvenue, Nadia", "the home keeps the session's facts");
    }

    [Fact]
    public void AScreenOfAModuleOutsideTheLicence_IsRefused_AndTheRefusalRecorded()
    {
        _licence = FullStatus() with
        {
            Modules = new HashSet<string>(LicenceModules.All.Where(m => m != LicenceModules.Quality), StringComparer.Ordinal)
        };
        SignIn(AppLanguage.French, Permissions.Quality.EnterQc);

        var page = Render<Screen>(p => p.Add(x => x.Key, "qc"));

        page.Find("[data-state=refused]").TextContent.Should().Contain("n'est pas inclus dans la licence");
        _auditor.Events.Should().ContainSingle(e => e.Kind == SecurityEventKind.AuthorizationDenied && !e.Succeeded
                                                    && e.Reason == "Licence_ModuleMissing");
    }

    [Fact]
    public void AnUnknownScreen_IsNotFound()
    {
        SignIn(AppLanguage.French, Permissions.Billing.View);

        var page = Render<Screen>(p => p.Add(x => x.Key, "../../admin"));

        page.Find("[data-state=not-found]").TextContent.Should().Contain("Cet écran n'existe pas.");
    }

    [Fact]
    public void TheArabicPlaceholder_IsInArabic()
    {
        SignIn(AppLanguage.Arabic, Permissions.Relations.View);

        var page = Render<Screen>(p => p.Add(x => x.Key, "prescriberstats"));

        page.Find("[data-state=unavailable]").TextContent.Should().Contain("نسخة الويب");
        Services.GetRequiredService<WebTexts>().Direction.Should().Be("rtl");
    }

    [Fact]
    public void EveryTextOfTheWebPages_IsInTheThreeCatalogues()
    {
        var root = Path.Combine(WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Web");
        var used = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "(?:Texts\\[|\\bT\\(|\\.Get\\(|\\bKey=)\"([A-Za-z_.]+)\"").Select(m => m.Groups[1].Value))
            .Concat(WebEndpoints.Reasons)
            .Concat(["Web.NetworkRefused", "Web_DeviceMismatch", "Licence_WebNoLicence", "Licence_WebPending",
                     "Licence_WebInvalid", "LicenceModule.VersionWeb"])
            .ToHashSet(StringComparer.Ordinal);

        used.Should().Contain(["Web.ScreenUnavailable", "Login.SignIn", "Web.Home.Welcome"], "the scan finds the pages' keys");

        foreach (var language in new[] { "fr", "en", "ar" })
        {
            var keys = Flatten(Path.Combine(WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Localization", "Resources", language + ".json"));
            used.Where(k => !keys.Contains(k)).Should().BeEmpty($"every key of the web pages is in {language}.json");
        }
    }

    private static HashSet<string> Flatten(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        void Walk(JsonElement element, string prefix)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                if (property.Value.ValueKind == JsonValueKind.Object) Walk(property.Value, key);
                else keys.Add(key);
            }
        }

        Walk(document.RootElement, "");
        return keys;
    }
}
