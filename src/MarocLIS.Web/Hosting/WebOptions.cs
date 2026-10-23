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
