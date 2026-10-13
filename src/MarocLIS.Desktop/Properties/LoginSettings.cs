using System.Text.Json;

namespace MarocLIS.Desktop.Properties;

/// <summary>
/// Per-workstation convenience settings.
/// <para>
/// Stored as a small JSON file under the roaming profile rather than in <c>app.config</c>, so it
/// follows the Windows user rather than the machine — laboratory workstations are shared, and the
/// last user name is a per-person convenience, not a machine setting.
/// </para>
/// <para>
/// Nothing sensitive goes here. A user name is not a secret; a password would be, so there is no
/// facility to remember one. "Remember my password" on a shared reception counter defeats the
/// individual accountability that the whole audit trail rests on.
/// </para>
/// </summary>
public static class LoginSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MarocLIS", "workstation.json");

    /// <summary>
    /// Cached: <see cref="JsonSerializerOptions"/> builds and memoises its converter set on first
    /// use, so a fresh instance per write throws that work away. Indented because somebody will
    /// open this file in Notepad to see why a workstation is behaving oddly.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static Settings _cached = Load();

    public static string? LastUserName
    {
        get => _cached.LastUserName;
        set
        {
            _cached = _cached with { LastUserName = value };
            Save();
        }
    }

    /// <summary>Language the login screen last used, so an Arabic-speaking operator is not reset.</summary>
    public static string? LastLanguage
    {
        get => _cached.LastLanguage;
        set
        {
            _cached = _cached with { LastLanguage = value };
            Save();
        }
    }

    private static Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Settings();

            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))
                   ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable preferences file must never stop the application starting.
            return new Settings();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_cached, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a convenience preference is not worth interrupting the operator for.
        }
    }

    private sealed record Settings
    {
        public string? LastUserName { get; init; }
        public string? LastLanguage { get; init; }
    }
}
