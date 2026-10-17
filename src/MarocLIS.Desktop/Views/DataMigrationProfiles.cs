using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarocLIS.Application.Migration;
using MarocLIS.Domain.Migration;

namespace MarocLIS.Desktop.Views;

/// <summary>What one kind of data of a source was read from and how its columns were matched.</summary>
public sealed class MigrationKindProfile
{
    /// <summary>MarocLIS field key → source column.</summary>
    public Dictionary<string, string> Columns { get; set; } = new(StringComparer.Ordinal);

    public string? TableSchema { get; set; }
    public string? TableName { get; set; }
    public bool TableIsView { get; set; }
    public string? Query { get; set; }
    public string? Sheet { get; set; }
}

/// <summary>
/// A saved « profil de reprise »: one old application, how to reach its database, the column
/// matching of each kind of data and the « Correspondances » settled during the trials, so the
/// final import on cut-over day reuses them as they are. The password is kept only encrypted for
/// the Windows user (DPAPI), and only when the operator asked.
/// </summary>
public sealed class MigrationProfile
{
    public const int NameMaxLength = 60;

    public string Name { get; set; } = string.Empty;

    /// <summary>The « Ancienne application » name the source keys are built with.</summary>
    public string SourceSystem { get; set; } = string.Empty;

    public SourceConnectionSettings? Connection { get; set; }

    /// <summary>The password, encrypted with DPAPI for the current Windows user; never in clear.</summary>
    public string? ProtectedPassword { get; set; }

    public Dictionary<DataMigrationKind, MigrationKindProfile> Kinds { get; set; } = [];

    public CorrespondenceTables Correspondences { get; set; } = new();

    public bool LinkByNameAndBirthDate { get; set; } = true;

    public DateTimeOffset SavedAt { get; set; }

    public MigrationKindProfile For(DataMigrationKind kind)
    {
        if (!Kinds.TryGetValue(kind, out var profile)) Kinds[kind] = profile = new MigrationKindProfile();
        return profile;
    }
}

/// <summary>
/// The profiles of this workstation, one JSON file each under
/// <c>%LOCALAPPDATA%\MarocLIS\Reprise\Profils</c>; exported and imported to carry the trial's
/// settings to the workstation of the final run (without the password, which DPAPI ties to one
/// Windows account anyway).
/// </summary>
public sealed class MigrationProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Distinguishes these blobs from any other DPAPI data of the same user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MarocLIS.Reprise.Profil");

    public MigrationProfileStore(string? folder = null)
    {
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "MarocLIS", "Reprise", "Profils");
    }

    public string Folder { get; }

    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(Folder)) return [];

        var names = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (Read(file) is { Name.Length: > 0 } profile) names.Add(profile.Name);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }

        return [.. names.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    public MigrationProfile? Load(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? Read(path) : null;
    }

    /// <summary>
    /// Saves <paramref name="profile"/>; <paramref name="password"/> is encrypted into it when
    /// <paramref name="rememberPassword"/>, and dropped otherwise.
    /// </summary>
    public void Save(MigrationProfile profile, string? password, bool rememberPassword)
    {
        profile.Name = CleanName(profile.Name);
        if (profile.Name.Length == 0) throw new ArgumentException("Migration_ProfileNameRequired");

        profile.ProtectedPassword = rememberPassword && !string.IsNullOrEmpty(password) ? Protect(password) : null;
        profile.SavedAt = DateTimeOffset.Now;

        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathOf(profile.Name), JsonSerializer.Serialize(profile, Json), new UTF8Encoding(false));
    }

    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>The profile as a file to carry elsewhere: everything but the password.</summary>
    public static byte[] Export(MigrationProfile profile)
    {
        var copy = JsonSerializer.Deserialize<MigrationProfile>(JsonSerializer.Serialize(profile, Json), Json)!;
        copy.ProtectedPassword = null;
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy, Json));
    }

    public static MigrationProfile Import(byte[] content)
    {
        var profile = JsonSerializer.Deserialize<MigrationProfile>(CsvSource.Decode(content), Json)
                      ?? throw new JsonException();
        profile.ProtectedPassword = null;
        profile.Name = CleanName(profile.Name);
        return profile;
    }

    /// <summary>The password a profile keeps, when this Windows user can still decrypt it.</summary>
    public static string? PasswordOf(MigrationProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ProtectedPassword) || !OperatingSystem.IsWindows()) return null;

        try
        {
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(profile.ProtectedPassword), Entropy,
                                                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Saved by another Windows account or another machine: the operator types it again.
            return null;
        }
    }

    /// <summary>Encrypted for the current Windows user; null where DPAPI does not exist, so nothing is kept.</summary>
    public static string? Protect(string password)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(sealedBytes);
    }

    public static string CleanName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        return text.Length > MigrationProfile.NameMaxLength ? text[..MigrationProfile.NameMaxLength] : text;
    }

    private string PathOf(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var file = new string([.. CleanName(name).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]);
        return Path.Combine(Folder, file + ".json");
    }

    private static MigrationProfile? Read(string path) =>
        JsonSerializer.Deserialize<MigrationProfile>(File.ReadAllText(path), Json);
}
