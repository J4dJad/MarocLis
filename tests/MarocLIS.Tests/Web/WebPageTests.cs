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
using MarocLIS.Web.Navigation;
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
    public void EveryScreenOfTheMenu_HasItsWebScreen_AndNoneSaysItIsNotYetThere()
    {
        var source = File.ReadAllText(Path.Combine(WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Web", "Components", "Pages", "Screen.razor"));
        var mapped = Regex.Matches(source, "Key == \"([a-z]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var everyone = new User(permissions: [.. Permissions.All]);
        var menu = WebMenu.Build(everyone, FullStatus()).SelectMany(g => g.Items).Select(i => i.Entry.Key).ToList();

        menu.Should().NotBeEmpty();
        menu.Where(k => !mapped.Contains(k)).Should().BeEmpty("every entry of the menu opens its screen in the web version");
        source.Should().NotContain("Web.ScreenUnavailable", "no screen is left saying it is only on the workstation");
    }

    /// <summary>
    /// A text parameter given a field without its « @ » receives the field's name, not its value: the
    /// invoice dialog showed « _approvalRefused » in red on every invoice issued.
    /// </summary>
    [Fact]
    public void NoTextParameterOfAComponent_IsGivenAFieldsNameInsteadOfItsValue()
    {
        var components = Path.Combine(WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Web", "Components");
        var files = Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "_Imports.razor")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);

        var textParameters = files.ToDictionary(
            f => f.Key,
            f => Regex.Matches(f.Value, @"\[Parameter[^\]]*\]\s*public\s+string\??\s+(\w+)\s*\{")
                      .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal));

        var wrong = new List<string>();
        foreach (var (name, source) in files)
            foreach (Match tag in Regex.Matches(source, @"<([A-Z]\w*)\b((?:[^>""]|""[^""]*"")*)/?>"))
                if (textParameters.TryGetValue(tag.Groups[1].Value, out var parameters))
                    foreach (Match attribute in Regex.Matches(tag.Groups[2].Value, @"(\w+)=""(_\w+)"""))
                        if (parameters.Contains(attribute.Groups[1].Value))
                            wrong.Add($"{name}: <{tag.Groups[1].Value} {attribute.Value}>");

        wrong.Should().BeEmpty("a field is passed as @_field, else the text shown is its name");
    }

    [Fact]
    public void TheWorkstationOnlyText_IsGoneFromTheThreeCatalogues_NothingSayingItAnyMore()
    {
        var resources = Path.Combine(WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Localization", "Resources");
        foreach (var language in new[] { "fr", "en", "ar" })
        {
            using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(resources, language + ".json")));
            catalogue.RootElement.GetProperty("Web").TryGetProperty("ScreenUnavailable", out _)
                .Should().BeFalse($"no screen says it is only on the workstation since lot 10 ({language})");
        }
    }

    /// <summary>
    /// A turnaround or a fast written « 4 h » in code read « h 4 » in Arabic, the Latin letter
    /// thrown to the other end of the line: the hours now go through the catalogue's own text.
    /// </summary>
    [Fact]
    public void HoursOfATurnaroundOrAFast_AreWrittenInTheScreensLanguage_NotWithAHardCodedH()
    {
        var root = WebMenuTests.RepositoryRoot();
        var sources = new[] { "MarocLIS.Web", "MarocLIS.Desktop", "MarocLIS.Application" }
            .SelectMany(project => Directory.GetFiles(Path.Combine(root, "src", project), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var hardCoded = sources
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => Regex.IsMatch(l.Text, @"(Hours|hours|fasting|Fasting|required)\} h\b") || Regex.IsMatch(l.Text, @"@[\w.]*Hours h\b"))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();
        hardCoded.Should().BeEmpty("hours are written with Catalog.HoursValue");

        var resources = Path.Combine(root, "src", "MarocLIS.Localization", "Resources");
        foreach (var (language, expected) in new[] { ("fr", "{0} h"), ("en", "{0} h"), ("ar", "{0} سا") })
        {
            using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(resources, language + ".json")));
            catalogue.RootElement.GetProperty("Catalog").GetProperty("HoursValue").GetString().Should().Be(expected);
        }
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
    [InlineData("leave", "Congés et absences")]
    [InlineData("planning", "Planning des gardes")]
    [InlineData("payroll", "Paie")]
    [InlineData("accidents", "Accidents du travail")]
    [InlineData("prescriberstats", "Statistiques prescripteurs")]
    [InlineData("relations", "Visites et conventions")]
    [InlineData("patientmessaging", "Rappels et enquêtes")]
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
    public void TheArabicRefusal_IsInArabic()
    {
        SignIn(AppLanguage.Arabic, Permissions.Samples.Collect);

        var page = Render<Screen>(p => p.Add(x => x.Key, "patientmessaging"));

        page.Find("[data-state=refused]").TextContent.Should().MatchRegex("[\u0600-\u06FF]");
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

        used.Should().Contain(["Web.ScreenNotFound", "Login.SignIn", "Web.Home.Welcome"], "the scan finds the pages' keys");

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
