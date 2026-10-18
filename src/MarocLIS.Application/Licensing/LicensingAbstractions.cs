using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Application.Licensing;

/// <summary>
/// The licence as last evaluated, for every gate of the process: the services refuse through it,
/// the shell's banners and menu read it.
/// <para>
/// The services take it as an optional dependency. The workstation and the API register it; a test
/// that composes the services without it works as an unlicensed build of the past did, with
/// nothing refused — the rules of the licence have tests of their own.
/// </para>
/// </summary>
public interface ILicenceState
{
    LicenceStatus Current { get; }
}

/// <summary>The process's evaluation, replaced whole at each refresh so a reader never sees half of one.</summary>
public sealed class LicenceStatusHolder : ILicenceState
{
    private volatile LicenceStatus _current = LicenceStatus.Pending;

    public LicenceStatus Current
    {
        get => _current;
        set => _current = value ?? LicenceStatus.Pending;
    }

    /// <summary>Raised after each change of <see cref="Current"/>'s state or mode, for the banners.</summary>
    public event EventHandler? Changed;

    public void Publish(LicenceStatus status)
    {
        var previous = _current;
        Current = status;
        if (previous != status) Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A state that is always the given status; for a host or a test that decides it itself.</summary>
public sealed class FixedLicenceState(LicenceStatus status) : ILicenceState
{
    public LicenceStatus Current { get; } = status;
}

/// <summary>
/// The gates as the services call them: a missing <see cref="ILicenceState"/> refuses nothing.
/// </summary>
public static class LicenceGuards
{
    public static Result RequireNewWork(this ILicenceState? licence) =>
        licence?.Current.RequireNewWork() ?? Result.Success();

    public static Result RequireBenchWork(this ILicenceState? licence) =>
        licence?.Current.RequireBenchWork() ?? Result.Success();

    public static Result RequireSettingsChange(this ILicenceState? licence) =>
        licence?.Current.RequireSettingsChange() ?? Result.Success();

    public static Result RequireModule(this ILicenceState? licence, string module) =>
        licence?.Current.RequireModule(module) ?? Result.Success();

    public static Result RequireInstrumentResults(this ILicenceState? licence) =>
        licence?.Current.RequireInstrumentResults() ?? Result.Success();

    public static Result RequireMobileValidation(this ILicenceState? licence) =>
        licence?.Current.RequireMobileValidation() ?? Result.Success();

    /// <summary>Configuring a module: the module licensed, and the settings open to changes.</summary>
    public static Result RequireConfiguration(this ILicenceState? licence, string module)
    {
        var licensed = licence.RequireModule(module);
        return licensed.IsFailure ? licensed : licence.RequireSettingsChange();
    }

    public static bool HasModule(this ILicenceState? licence, string module) =>
        licence is null || licence.Current.HasModule(module);
}

/// <summary>
/// What MarocLIS trusts a licence by: the vendor's public key and the numbers revoked, both
/// compiled into this version, and the version's release date.
/// <para>
/// The key comes from <c>Licensing/licence-public-key.pem</c>, embedded at build. The repository
/// ships a development key whose private half is a test fixture; <see cref="IsDevelopmentKey"/>
/// says so on the licence tab, and the installer's build refuses to package it.
/// </para>
/// </summary>
public sealed class LicenceTrust : IDisposable
{
    private const string KeyResource = "MarocLIS.Application.Licensing.licence-public-key.pem";
    private const string RevokedResource = "MarocLIS.Application.Licensing.licence-revoked.txt";

    public LicenceTrust() : this(ReadResource(KeyResource), ReadResource(RevokedResource), BuiltOn()) { }

    public LicenceTrust(string publicKeyPem, string revokedList, DateOnly releaseDate)
    {
        PublicKey = LicenceKeys.ImportPublicKey(publicKeyPem);
        Fingerprint = LicenceKeys.Fingerprint(PublicKey);
        Revoked = ParseRevoked(revokedList);
        ReleaseDate = releaseDate;
    }

    public ECDsa PublicKey { get; }
    public string Fingerprint { get; }
    public IReadOnlySet<string> Revoked { get; }
    public DateOnly ReleaseDate { get; }

    public bool IsDevelopmentKey => Fingerprint == LicenceKeys.DevelopmentFingerprint;

    /// <summary>
    /// One number per line, « LIC-2026-0042 » for every version of it or « LIC-2026-0042/1 » for
    /// one; what follows a « # » is a comment (the reason: unpaid, lost, stolen).
    /// </summary>
    public static IReadOnlySet<string> ParseRevoked(string? text) =>
        new HashSet<string>(
            (text ?? string.Empty).Split('\n')
                .Select(line => line.Split('#', 2)[0].Trim())
                .Where(line => line.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The release date stamped into this assembly at build (<c>MarocLIS.ReleaseDate</c>), which
    /// a perpetual licence's « mises à jour jusqu'au » is compared with.
    /// </summary>
    public static DateOnly BuiltOn()
    {
        var stamp = typeof(LicenceTrust).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MarocLIS.ReleaseDate")?.Value;

        return DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.MinValue;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(LicenceTrust).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"The resource {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose() => PublicKey.Dispose();
}

/// <summary>
/// The settings the licence keeps among the others (the database's identifier, the latest day
/// seen, the start of the read-only mode): written by the licence's store alone, never listed nor
/// written by Paramètres › Paramètres, where changing one would move the clock or the server ID.
/// </summary>
public static class LicenceSettings
{
    public const string Prefix = "Licence.";
}

/// <summary>What the database says about the licence and around it.</summary>
public sealed record StoredLicence
{
    public string? Content { get; init; }
    public string? Number { get; init; }
    public int? Version { get; init; }

    /// <summary>The Windows name of the machine running SQL Server, as SQL Server reports it.</summary>
    public required string MachineName { get; init; }
    public string? InstanceName { get; init; }

    /// <summary>Written into the database the first time MarocLIS read it.</summary>
    public required Guid InstallationId { get; init; }

    public int OrderCount { get; init; }
    public DateOnly? LatestOrderOn { get; init; }
    public bool HasPatients { get; init; }
    public DateOnly? LatestDateSeen { get; init; }
    public DateOnly? RestrictedSince { get; init; }

    /// <summary>The main laboratory's name, legal name and authorisation, for the request file.</summary>
    public string? SiteName { get; init; }
    public string? LegalName { get; init; }
    public string? Authorisation { get; init; }
}

/// <summary>A licence of the history, for the licence tab.</summary>
public sealed record LicenceHistoryRow(
    string Number, int Version, LicenceKind Kind, DateTimeOffset ImportedAt, string? ImportedBy,
    string? Workstation, DateTimeOffset? ReplacedAt);

/// <summary>The licence's storage, implemented over the database.</summary>
public interface ILicenceStore
{
    Task<StoredLicence> ReadAsync(CancellationToken ct = default);

    Task<IReadOnlyList<LicenceHistoryRow>> HistoryAsync(CancellationToken ct = default);

    /// <summary>The highest version ever imported for <paramref name="number"/>, and its text.</summary>
    Task<(int Version, string Content)?> HighestVersionAsync(string number, CancellationToken ct = default);

    /// <summary>Records <paramref name="record"/> as the licence in force, the previous one marked replaced.</summary>
    Task SaveAsync(LicenceRecord record, CancellationToken ct = default);

    /// <summary>
    /// The latest date seen and the start of the read-only mode; a null leaves a value as it is,
    /// <paramref name="clearRestricted"/> removes the start once a licence is valid again.
    /// </summary>
    Task SaveMarkersAsync(DateOnly? latestDateSeen, DateOnly? restrictedSince, bool clearRestricted,
                          CancellationToken ct = default);
}

/// <summary>The workstations' presence, implemented over the database.</summary>
public interface IWorkstationRegistry
{
    /// <summary>
    /// Asks for a place for <paramref name="machineName"/> under <paramref name="limit"/>, and records
    /// its presence when given one. Serialised across workstations, so two signing in together
    /// cannot both take the last place.
    /// </summary>
    Task<WorkstationClaim> ClaimAsync(string machineName, string? userName, string? appVersion, int? limit,
                                      DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Frees the place of a workstation signing out.</summary>
    Task ReleaseAsync(string machineName, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<ActiveWorkstation>> ActiveAsync(DateTimeOffset now, CancellationToken ct = default);
}
