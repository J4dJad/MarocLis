using System.Net;
using MarocLIS.Application.Administration;
using MarocLIS.Localization;
using MarocLIS.Web.Hosting;

namespace MarocLIS.Web.Security;

/// <summary>
/// Which addresses the web interface answers: the laboratory's local network, nothing else.
/// <para>
/// The first step of every request, before a page, a file or the sign-in is reached: an address
/// outside the configured networks gets a bare 403 and learns nothing of what is behind. The peer
/// address is the TCP connection's own; no forwarded header is believed, since the web interface is
/// reached directly on the local network and a header is whatever the caller chose to write.
/// </para>
/// </summary>
public sealed class NetworkAccess
{
    private readonly IReadOnlyList<IPNetwork> _networks;

    public NetworkAccess(IEnumerable<string> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);

        // A range written wrong stops the start rather than being skipped: a filter quietly
        // narrower — or wider — than the administrator wrote is the worse failure.
        _networks = [.. networks.Select(text => IPNetwork.TryParse(text.Trim(), out var network)
            ? network
            : throw new FormatException($"Web:AllowedNetworks: « {text} » is not a network in CIDR notation."))];
    }

    public static NetworkAccess From(WebOptions options) => new(options.EffectiveNetworks);

    public IReadOnlyList<IPNetwork> Networks => _networks;

    /// <summary>
    /// Whether <paramref name="address"/> is served. An IPv4 address carried in IPv6 (::ffff:a.b.c.d,
    /// what a dual-stack socket reports) is judged as the IPv4 address it is. No address at all —
    /// a connection that is not TCP — is refused.
    /// </summary>
    public bool IsAllowed(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        foreach (var network in _networks)
            if (network.Contains(address)) return true;

        return false;
    }

    /// <summary>The middleware: 403 to every address outside the networks, logged once per request.</summary>
    public static Func<HttpContext, Func<Task>, Task> Filter(NetworkAccess access, ILocalizer localizer, ILogger logger) =>
        (context, next) =>
        {
            var peer = context.Connection.RemoteIpAddress;
            if (access.IsAllowed(peer)) return next();

            logger.LogWarning("Refused a request from {Address}, outside the networks the web interface serves.", peer);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            return context.Response.WriteAsync(RefusalText(localizer));
        };

    /// <summary>
    /// The body of the refusal: in the three languages at once, since nothing about the caller is
    /// known yet — not even whether it is a person.
    /// </summary>
    public static string RefusalText(ILocalizer localizer) =>
        string.Join('\n', LanguageSettings.AllLanguages.Select(l => localizer.Get("Web.NetworkRefused", l))) + "\n";
}
