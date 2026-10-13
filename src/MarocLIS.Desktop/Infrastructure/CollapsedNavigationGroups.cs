using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The groups of the left menu one user has folded away.
/// <para>
/// Kept per user, like the keyboard shortcuts, in
/// <c>%LOCALAPPDATA%\MarocLIS\navigation\{user}.json</c>: a secretary who never opens the quality
/// screens folds that group once and finds it folded at the next sign-in.
/// </para>
/// </summary>
public sealed class CollapsedNavigationGroups
{
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly ILogger? _logger;

    public CollapsedNavigationGroups(Guid userId, ILogger? logger = null, string? directory = null)
    {
        _logger = logger;
        _path = Path.Combine(
            directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarocLIS", "navigation"),
            $"{userId:N}.json");

        Load();
    }

    public bool IsCollapsed(string group) => _collapsed.Contains(group);

    /// <summary>Folds <paramref name="group"/> if it is open, opens it if it is folded.</summary>
    public void Toggle(string group)
    {
        if (!_collapsed.Remove(group)) _collapsed.Add(group);
        Save();
    }

    /// <summary>Opens <paramref name="group"/>; returns whether it was folded.</summary>
    public bool Expand(string group)
    {
        if (!_collapsed.Remove(group)) return false;
        Save();
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var stored = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path));
            foreach (var group in stored ?? []) _collapsed.Add(group);
        }
        catch (Exception ex)
        {
            // A damaged file must not cost the user the menu; every group opens.
            _logger?.LogWarning(ex, "Reading the folded menu groups failed.");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_collapsed.Order(StringComparer.Ordinal)));
        }
        catch (Exception ex)
        {
            // Folding still works for this session; it is only not remembered.
            _logger?.LogWarning(ex, "Saving the folded menu groups failed.");
        }
    }
}
