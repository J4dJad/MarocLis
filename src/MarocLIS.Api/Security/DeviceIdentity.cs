using System.Net;
using System.Text;

namespace MarocLIS.Api.Security;

/// <summary>
/// Names the device a request comes from, for the « Poste » column of every audit entry it causes:
/// « Mobile · karim.alami@gmail.com (100.101.7.12) ».
/// <para>
/// The API listens on the loopback only and is reached through <c>tailscale serve</c>, which
/// terminates TLS on the server and proxies each request to 127.0.0.1. On the way it adds who is
/// calling, as the tailnet knows them: <c>Tailscale-User-Login</c> (the account the phone is signed
/// in to Tailscale with) and <c>Tailscale-User-Name</c> (its display name), and the phone's tailnet
/// address in <c>X-Forwarded-For</c>. Tailscale does not pass the node's machine name, so the device
/// is named by its owner's login and its tailnet address, which is stable for a given phone.
/// </para>
/// <para>
/// Those headers are believed <b>only when the connection itself comes from the loopback</b>, i.e.
/// from <c>tailscale serve</c> or another process on the server. A request arriving any other way
/// (the API reconfigured to listen on the LAN, say) could carry whatever headers its sender typed,
/// and a forged name in the audit trail is worse than none: it is then named by the address it
/// connects from. Processes on the server itself are trusted — they could equally read the
/// database's connection string.
/// </para>
/// </summary>
public static class DeviceIdentity
{
    public const string Prefix = "Mobile · ";

    public const string LoginHeader = "Tailscale-User-Login";
    public const string NameHeader = "Tailscale-User-Name";

    /// <summary>
    /// The workstation name for a request.
    /// </summary>
    /// <param name="fromLoopback">The TCP connection came from the server itself (before any forwarded header is applied).</param>
    /// <param name="login">The Tailscale-User-Login header, if any.</param>
    /// <param name="displayName">The Tailscale-User-Name header, if any.</param>
    /// <param name="forwardedClient">The client address <c>tailscale serve</c> forwarded, if any.</param>
    /// <param name="connection">The address of the TCP peer.</param>
    public static string Describe(bool fromLoopback, string? login, string? displayName,
                                  IPAddress? forwardedClient, IPAddress? connection)
    {
        if (fromLoopback)
        {
            var who = Clean(login) ?? Clean(displayName);
            var where = forwardedClient is { } client && !IPAddress.IsLoopback(client) ? client : null;

            if (who is not null)
                return where is null ? Prefix + who : $"{Prefix}{who} ({where})";

            if (where is not null) return Prefix + where;
        }

        return Prefix + (connection?.ToString() ?? "?");
    }

    /// <summary>
    /// A header value fit for the audit trail: printable characters only, trimmed, bounded. A name a
    /// client could choose must not be able to break a line of the security log or fill its column.
    /// </summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            if (!char.IsControl(c)) text.Append(c);

        var cleaned = text.ToString().Trim();
        if (cleaned.Length == 0) return null;

        return cleaned.Length <= 64 ? cleaned : cleaned[..64];
    }
}
