using System.Text.RegularExpressions;
using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Common;
using static MarocLIS.Tests.Licensing.LicenceTestKit;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Localization;
using MarocLIS.Web.Security;
using MarocLIS.Web.Components.Shared;
using MarocLIS.Web.Session;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Web;

/// <summary>
/// MarocLIS Web on the workstations of Windows 7, 8 and 8.1: Chrome and Edge 109, Firefox ESR 115 —
/// the last versions those systems receive — draw every screen as a recent browser does. What they
/// lack is decided by the server (the action bar's classes, the list tools' search box) or supplied
/// before blazor.web.js (js/compat.js); and a browser older still is told so on the sign-in page.
/// </summary>
public sealed class WebBrowserCompatibilityTests : BunitContext
{
    public WebBrowserCompatibilityTests()
    {
        Services.AddSingleton<ILocalizer>(new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true }));
        Services.AddSingleton<IClock>(new Clock { Now = DateTimeOffset.Now });
        Services.AddSingleton(new PasswordPolicy());
        Services.AddSingleton(new WebDefaults(AppLanguage.French));
        Services.AddSingleton<WebSessionStore>();
        Services.AddScoped<SessionContext>();
        Services.AddScoped<WebTexts>();
        Services.UsePerRequestCurrentUser();
    }

    private static string WebFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Web", .. parts]));

    /// <summary>
    /// CSS that Chrome 109 or Firefox 115 drops: a rule they do not understand is skipped whole, and
    /// the layout it carried is lost on those workstations only.
    /// </summary>
    [Fact]
    public void TheStyleSheet_UsesNothingChrome109OrFirefox115Lacks()
    {
        var css = Regex.Replace(WebFile("wwwroot", "css", "app.css"), @"/\*.*?\*/", "", RegexOptions.Singleline);

        foreach (var feature in new[] { ":has(", "color-mix(", "text-wrap", "light-dark(", ":popover-open", "@starting-style",
                                        "@scope", "anchor-name", "position-anchor", "field-sizing", "@property", "content-visibility",
                                        "oklch(", "oklab(", "round(", "interpolate-size", "text-box" })
            css.Should().NotContain(feature, $"{feature} is unknown to Chrome 109 or Firefox 115");

        Regex.IsMatch(css, @"(^|[;{\s])&[\s.:#\[>+~]").Should().BeFalse("CSS nesting is unknown to Chrome 109 and Firefox 115");
        Regex.IsMatch(css, @"\d(r?lh|cap|rcap|ic|ric)\b").Should().BeFalse("the lh unit is unknown to Firefox 115");
    }

    [Fact]
    public void ABarWhoseOnlyButtonIsHidden_IsNotDrawn_AndOneWithAButtonIs()
    {
        var hidden = Render<ActionBar>(p => p
            .Add(b => b.ShowStatus, false)
            .Add(b => b.Primary, (RenderFragment)(builder =>
            {
                builder.OpenComponent<ActionButton>(0);
                builder.AddComponentParameter(1, nameof(ActionButton.Key), "Action.Save");
                builder.AddComponentParameter(2, nameof(ActionButton.Hidden), true);
                builder.CloseComponent();
            })));
        hidden.Find("[data-bar=actions]").ClassList.Should().Contain("is-empty");

        var shown = Render<ActionBar>(p => p
            .Add(b => b.ShowStatus, false)
            .Add(b => b.Primary, (RenderFragment)(builder =>
            {
                builder.OpenComponent<ActionButton>(0);
                builder.AddComponentParameter(1, nameof(ActionButton.Key), "Action.Save");
                builder.CloseComponent();
            })));
        shown.Find("[data-bar=actions]").ClassList.Should().NotContain("is-empty");

        var message = Render<ActionBar>(p => p
            .Add(b => b.StatusText, "Enregistré.")
            .Add(b => b.Primary, (RenderFragment)(_ => { })));
        message.Find("[data-bar=actions]").ClassList.Should().NotContain("is-empty", "a message is worth the bar");
    }

    [Fact]
    public void TheMessageAloneInTheMiddle_IsMarkedSo_ToTakeItsSecondLine()
    {
        var alone = Render<ActionBar>(p => p.Add(b => b.StatusText, "Dossier 2610-000014"));
        alone.Find(".action-bar-middle").ClassList.Should().Contain("is-alone");

        var withInfo = Render<ActionBar>(p => p
            .Add(b => b.StatusText, "Dossier 2610-000014")
            .Add(b => b.Info, (RenderFragment)(builder => builder.AddContent(0, "3 à valider"))));
        withInfo.Find(".action-bar-middle").ClassList.Should().NotContain("is-alone");
    }

    /// <summary>
    /// The list tools that open on a search or scan box say so (has-search), which puts the end tools
    /// under the box in a list pane; checked on the screens' markup, where the class is written.
    /// </summary>
    [Fact]
    public void EveryListToolsRowOpeningOnASearchBox_CarriesHasSearch()
    {
        var root = Path.Combine(WebMenuTests.RepositoryRoot(), "src", "MarocLIS.Web", "Components");
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], "<div class=\"list-tools[ \"]")) continue;
                var indent = lines[i].Length - lines[i].TrimStart().Length;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].Trim().Length == 0) continue;
                    var childIndent = lines[j].Length - lines[j].TrimStart().Length;
                    if (childIndent <= indent) break;
                    if (childIndent == indent + 4 && Regex.IsMatch(lines[j], "class=\"(search-box|input-with-button)"))
                    {
                        if (!lines[i].Contains("has-search", StringComparison.Ordinal))
                            missing.Add($"{Path.GetFileName(file)}:{i + 1}");
                        break;
                    }
                }
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void TheCompatibilityScript_RunsBeforeBlazor_AndIsPlainEs5()
    {
        var app = WebFile("Components", "App.razor");
        app.IndexOf("js/compat.js", StringComparison.Ordinal).Should()
           .BeLessThan(app.IndexOf("_framework/blazor.web.js", StringComparison.Ordinal));

        var script = Regex.Replace(WebFile("wwwroot", "js", "compat.js"), @"//[^\n]*", "");
        script.Should().Contain("Promise.withResolvers");
        script.Should().NotContain("=>").And.NotContain("`").And.NotContain("?.").And.NotContain("??");
        Regex.IsMatch(script, @"\b(let|const|class)\s").Should().BeFalse("an older browser must be able to read it to be told it is too old");
    }

    [Fact]
    public void TheSignInPage_HasTheNoticeForATooOldBrowser_NamingTheSupportedOnes()
    {
        WebFile("Components", "Pages", "Login.razor").Should().Contain("data-state=\"browser-too-old\" hidden");

        var localizer = Services.GetRequiredService<ILocalizer>();
        foreach (var language in new[] { AppLanguage.French, AppLanguage.English, AppLanguage.Arabic })
        {
            var text = localizer.Get("Login.BrowserTooOld", language);
            text.Should().Contain("109").And.Contain("115").And.Contain("Firefox");
            localizer.Get("Login.JavaScriptRequired", language).Should().NotStartWith("[");
        }
    }
}
