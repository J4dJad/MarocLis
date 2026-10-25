using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarocLIS.Domain.Migration;

namespace MarocLIS.Application.Migration;

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
/// the account that saved it, and only when the operator asked.
/// </summary>
public sealed class MigrationProfile
{
    public const int NameMaxLength = 60;

    /// <summary>The largest profile file read back: anything far larger is not one.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    public string Name { get; set; } = string.Empty;

    /// <summary>The « Ancienne application » name the source keys are built with.</summary>
    public string SourceSystem { get; set; } = string.Empty;

    public SourceConnectionSettings? Connection { get; set; }

    /// <summary>The password, encrypted for the account that saved it (DPAPI on the workstation); never in clear.</summary>
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
/// The profiles of a folder, one JSON file each — the workstation's under
/// <c>%LOCALAPPDATA%\MarocLIS\Reprise\Profils</c>, the web server's under its own; exported and
/// imported to carry the trial's settings to where the final run is made, without the password.
/// <para>
/// The password is kept only through <c>protect</c>, which encrypts it for the account that saves
/// it (DPAPI on the workstation); without one nothing is kept, and the operator types it again.
/// </para>
/// </summary>
public sealed class MigrationProfileStore(string folder, Func<string, string?>? protect = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Folder { get; } = folder;

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
    /// <paramref name="rememberPassword"/> and the store can protect it, and dropped otherwise.
    /// </summary>
    public void Save(MigrationProfile profile, string? password, bool rememberPassword)
    {
        ArgumentNullException.ThrowIfNull(profile);

        profile.Name = CleanName(profile.Name);
        if (profile.Name.Length == 0) throw new ArgumentException("Migration_ProfileNameRequired");

        profile.ProtectedPassword = rememberPassword && !string.IsNullOrEmpty(password) && protect is not null
            ? protect(password)
            : null;
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
