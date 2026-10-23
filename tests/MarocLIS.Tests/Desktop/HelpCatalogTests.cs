using FluentAssertions;
using MarocLIS.Application.Navigation;
using MarocLIS.Desktop.Infrastructure;
using MarocLIS.Domain.Common;
using Xunit;

namespace MarocLIS.Tests.Desktop;

/// <summary>
/// The help F1 opens: every screen of the menu has a chapter, every chapter is shipped in French
/// with the screen's section in it, and a page missing in a language falls back to French.
/// </summary>
public sealed class HelpCatalogTests : IDisposable
{
    private static readonly string Repo = FindRepo();
    private static readonly string ShippedRoot = Path.Combine(Repo, "src", "MarocLIS.Desktop", "Help");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "maroclis-help-" + Guid.NewGuid().ToString("N"));

    // The screens of the menu, as the shared menu declares them (group headers aside).
    private static List<string> NavigationKeys() =>
        NavigationMenu.Entries.Where(e => !e.IsGroupHeader).Select(e => e.Key).ToList();

    [Fact]
    public void Map_CoversEveryScreenOfTheMenu_AndNothingElse()
    {
        var keys = NavigationKeys();
        var map = new HelpCatalog(ShippedRoot).Map;

        keys.Should().HaveCountGreaterThan(40);
        keys.Should().BeSubsetOf(map.Keys, "every screen opens its own help");
        map.Keys.Should().BeSubsetOf(keys, "a mapped key that is not a screen is a typo");
    }

    [Fact]
    public void EveryMappedChapter_IsShippedInFrench_WithTheScreensSection()
    {
        var catalog = new HelpCatalog(ShippedRoot);

        foreach (var (key, chapter) in catalog.Map)
        {
            var path = Path.Combine(ShippedRoot, "fr", chapter);
            File.Exists(path).Should().BeTrue($"{chapter} holds the help of {key}");
            File.ReadAllText(path).Should().Contain($"id=\"{key}\"", $"{chapter} opens on the section of {key}");
        }

        File.Exists(Path.Combine(ShippedRoot, "fr", HelpCatalog.IndexFileName)).Should().BeTrue();
    }

    [Fact]
    public void Resolve_KnownScreen_OpensItsChapterAtItsSection()
    {
        var topic = new HelpCatalog(ShippedRoot).Resolve("validation", AppLanguage.French);

        topic.Should().NotBeNull();
        Path.GetFileName(topic!.FilePath).Should().Be("03-analytique-comptes-rendus.html");
        topic.Anchor.Should().Be("validation");
        topic.Uri.IsFile.Should().BeTrue();
        topic.Uri.Fragment.Should().Be("#validation");
    }

    [Fact]
    public void Resolve_UnknownOrNoScreen_OpensTheTableOfContents()
    {
        var catalog = new HelpCatalog(ShippedRoot);

        foreach (var key in new[] { null, "", "nosuchscreen" })
        {
            var topic = catalog.Resolve(key, AppLanguage.French);
            Path.GetFileName(topic!.FilePath).Should().Be(HelpCatalog.IndexFileName);
            topic.Anchor.Should().BeNull();
        }
    }

    [Fact]
    public void Resolve_PrefersTheLanguage_AndFallsBackToFrenchFileByFile()
    {
        Write("help-map.json", """{"billing":"04-facturation.html","cash":"04-facturation.html","users":"10-administration.html"}""");
        Write("fr/04-facturation.html", "fr");
        Write("fr/10-administration.html", "fr");
        Write("fr/index.html", "fr");
        Write("ar/04-facturation.html", "ar");

        var catalog = new HelpCatalog(_temp);

        Folder(catalog.Resolve("billing", AppLanguage.Arabic)).Should().Be("ar");
        Folder(catalog.Resolve("users", AppLanguage.Arabic)).Should().Be("fr");
        Folder(catalog.Resolve("billing", AppLanguage.English)).Should().Be("fr");
        Folder(catalog.Index(AppLanguage.Arabic)).Should().Be("fr");
    }

    [Fact]
    public void Resolve_MissingChapter_OpensTheTableOfContents_AndNoHelpAtAllIsNull()
    {
        Write("help-map.json", """{"billing":"04-facturation.html"}""");
        Write("fr/index.html", "fr");

        var catalog = new HelpCatalog(_temp);
        Path.GetFileName(catalog.Resolve("billing", AppLanguage.French)!.FilePath).Should().Be("index.html");

        new HelpCatalog(Path.Combine(_temp, "absent")).Resolve("billing", AppLanguage.French).Should().BeNull();
    }

    [Fact]
    public void Map_CannotPointOutsideTheHelpFolder()
    {
        Write("help-map.json", """{"billing":"../secret.html"}""");
        Write("secret.html", "x");
        Write("fr/index.html", "fr");

        var catalog = new HelpCatalog(_temp);

        Path.GetFileName(catalog.Resolve("billing", AppLanguage.French)!.FilePath).Should().Be("index.html");
        catalog.Contains(Path.Combine(_temp, "fr", "index.html")).Should().BeTrue();
        catalog.Contains(Path.Combine(_temp, "..", "elsewhere.html")).Should().BeFalse();
        catalog.Contains(_temp + "-sibling" + Path.DirectorySeparatorChar + "x.html").Should().BeFalse();
    }

    [Fact]
    public void DamagedMap_LeavesEveryScreenOnTheTableOfContents()
    {
        Write("help-map.json", "{ not json");
        Write("fr/index.html", "fr");

        var catalog = new HelpCatalog(_temp);

        catalog.Map.Should().BeEmpty();
        Path.GetFileName(catalog.Resolve("billing", AppLanguage.French)!.FilePath).Should().Be("index.html");
    }

    private static string? Folder(HelpTopic? topic) =>
        topic is null ? null : Path.GetFileName(Path.GetDirectoryName(topic.FilePath));

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarocLIS.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("MarocLIS.sln not found.");
    }
}
