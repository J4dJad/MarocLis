using System.Text.Json;
using MarocLIS.Domain.Common;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>A help page to show: the chapter file and the section of the screen in it, if any.</summary>
public sealed record HelpTopic(string FilePath, string? Anchor)
{
    /// <summary>The <c>file:///…#anchor</c> address of the page.</summary>
    public Uri Uri =>
        Anchor is null ? new Uri(FilePath) : new UriBuilder(new Uri(FilePath)) { Fragment = Anchor }.Uri;
}

/// <summary>
/// Finds the help page of a screen in the folder shipped beside the executable.
/// <para>
/// The folder holds one sub-folder per language (<c>fr</c>, <c>ar</c>, <c>en</c>) of chapters
/// <c>NN-slug.html</c> and an <c>index.html</c>, and <c>help-map.json</c>, which names the chapter
/// of each screen by its navigation key. In a chapter, the section of a screen carries the
/// navigation key as its id, so the page opens on it. French is written first and is the fallback:
/// a page not yet translated opens in French rather than not at all.
/// </para>
/// <para>Free of WinForms, so the tests can check the map against the files shipped.</para>
/// </summary>
public sealed class HelpCatalog
{
    public const string MapFileName = "help-map.json";
    public const string IndexFileName = "index.html";
    public const string FallbackFolder = "fr";

    private readonly Dictionary<string, string> _map;

    public HelpCatalog(string root)
    {
        Root = Path.GetFullPath(root);
        _map = LoadMap(Path.Combine(Root, MapFileName));
    }

    /// <summary>The help folder shipped beside the executable.</summary>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "Help");

    public string Root { get; }

    /// <summary>Navigation key → chapter file name, as read from <see cref="MapFileName"/>.</summary>
    public IReadOnlyDictionary<string, string> Map => _map;

    public static string Folder(AppLanguage language) => language switch
    {
        AppLanguage.Arabic => "ar",
        AppLanguage.English => "en",
        _ => FallbackFolder
    };

    /// <summary>
    /// The page for a screen: its chapter at its section, or the table of contents when the
    /// screen is unknown or its chapter is missing. Null when there is no help at all.
    /// </summary>
    public HelpTopic? Resolve(string? navigationKey, AppLanguage language)
    {
        if (!string.IsNullOrEmpty(navigationKey) && _map.TryGetValue(navigationKey, out var chapter)
            && Find(chapter, language) is { } file)
            return new HelpTopic(file, navigationKey);

        return Index(language);
    }

    /// <summary>The table of contents in the language, or in French.</summary>
    public HelpTopic? Index(AppLanguage language) =>
        Find(IndexFileName, language) is { } file ? new HelpTopic(file, null) : null;

    /// <summary>The file in the language's folder, or in the French one when not translated.</summary>
    public string? Find(string fileName, AppLanguage language)
    {
        // A file name from the map, never a path: the map cannot point outside the folder.
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)) return null;

        foreach (var folder in new[] { Folder(language), FallbackFolder }.Distinct())
        {
            var path = Path.Combine(Root, folder, fileName);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>Whether a local path lies inside the help folder (the only place the viewer may go).</summary>
    public bool Contains(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows()
                                         ? StringComparison.OrdinalIgnoreCase
                                         : StringComparison.Ordinal);
    }

    // A missing or damaged map leaves every screen on the table of contents; help is never a
    // reason for the application to fail.
    private static Dictionary<string, string> LoadMap(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                           is { } map
                    ? new Dictionary<string, string>(map, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
