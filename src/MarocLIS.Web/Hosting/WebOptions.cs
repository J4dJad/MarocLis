namespace MarocLIS.Web.Hosting;

/// <summary>The « Web » section of appsettings.json.</summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>
    /// The networks served, in CIDR notation. Left empty, <see cref="DefaultNetworks"/>: the web
    /// interface is the laboratory's, on its local network, and never a site on the Internet.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = [];

    /// <summary>Creates or migrates the database and seeds the first administrator at start-up.</summary>
    public bool InitializeDatabase { get; set; } = true;

    /// <summary>
    /// Where « Reprise de données » keeps its profiles on the server, one JSON file each. Left
    /// empty, <see cref="DefaultMigrationProfilesFolder"/>. A profile saved here keeps no password:
    /// the web reads files, never a database of the old application.
    /// </summary>
    public string? MigrationProfilesFolder { get; set; }

    /// <summary>
    /// The public part of the server's HTTPS certificate (DER), as the installer exports it next to
    /// the executable: served at <see cref="Security.WebPaths.ServerCertificate"/> so the « Poste Web »
    /// setup of a workstation can fetch it, its fingerprint confirmed by whoever installs. A relative
    /// path is taken from the content root.
    /// </summary>
    public string ServerCertificateFile { get; set; } = "certificat-serveur.cer";

    /// <summary>The service account's own application data, under MarocLIS\Web\Reprise\Profils.</summary>
    public static string DefaultMigrationProfilesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarocLIS", "Web", "Reprise", "Profils");

    /// <summary>The folder of the profiles actually used: the one configured, or the default.</summary>
    public string EffectiveMigrationProfilesFolder =>
        string.IsNullOrWhiteSpace(MigrationProfilesFolder) ? DefaultMigrationProfilesFolder : MigrationProfilesFolder;

    /// <summary>
    /// The loopback, the private ranges (RFC 1918, RFC 4193), the link-local ones, and the shared
    /// address space of RFC 6598, which Tailscale and other carrier-grade VPNs hand out.
    /// </summary>
    public static IReadOnlyList<string> DefaultNetworks { get; } =
    [
        "127.0.0.0/8", "::1/128",
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
        "169.254.0.0/16",
        "100.64.0.0/10",
        "fc00::/7", "fe80::/10"
    ];

    /// <summary>The networks actually served: those configured, or the defaults.</summary>
    public IReadOnlyList<string> EffectiveNetworks =>
        AllowedNetworks.Count > 0 ? AllowedNetworks : DefaultNetworks;
}
