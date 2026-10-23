using System.Net;
using System.Security.Cryptography;
using MarocLIS.Infrastructure.Security;

namespace MarocLIS.Web.Security;

/// <summary>
/// The workstation a browser stands for, under the licence and in the audit trail.
/// <para>
/// A Windows workstation is its computer's name; a browser has none, so it is given one: a random
/// identifier kept in a long-lived cookie of its own, the first time it opens the web interface.
/// The same browser is then the same workstation from one day to the next — its tabs and its
/// sign-ins take one place under the licence, as two operators on one PC do — and two browsers are
/// two workstations, which is what « each open web session counts as one workstation » asks.
/// </para>
/// <para>
/// It is not a secret and proves nothing: like a computer's name, anyone can copy it. It only says
/// which place a browser takes. What proves who is signed in is the session cookie, which is bound
/// to this identifier (<see cref="WebSessionCookie"/>).
/// </para>
/// </summary>
public static class BrowserIdentity
{
    public const string CookieName = "MarocLIS.Poste";

    /// <summary>Every workstation of the web interface is named so, then its identifier.</summary>
    public const string Prefix = "WEB-";

    private const string ItemKey = "MarocLIS.Web.Workstation";
    private const int IdLength = 10;

    // Crockford's base 32: no I, L, O or U, so a name read aloud over the phone is not misheard.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Names the request's workstation, issuing the browser its identifier when it has none or a
    /// malformed one, and names it in the request's audit records, before anything can write one —
    /// the sign-in itself is recorded against it.
    /// </summary>
    public static Task Identify(HttpContext context, Func<Task> next)
    {
        var id = context.Request.Cookies[CookieName];
        if (!IsWellFormed(id))
        {
            id = NewId();
            context.Response.Cookies.Append(CookieName, id, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                IsEssential = true,
                Path = "/",
                MaxAge = TimeSpan.FromDays(3650)
            });
        }

        var workstation = Prefix + id;
        context.Items[ItemKey] = workstation;

        context.RequestServices.GetRequiredService<RequestSession>()
               .UseWorkstation(AuditName(workstation, context.Connection.RemoteIpAddress));

        return next();
    }

    /// <summary>The workstation <see cref="Identify"/> named for the request.</summary>
    public static string WorkstationOf(HttpContext context) =>
        context.Items[ItemKey] as string ?? Prefix + "?";

    /// <summary>
    /// The name recorded on audit entries and security events: the workstation and the address it
    /// came from, which an administrator investigating an incident can find on the network.
    /// </summary>
    public static string AuditName(string workstation, IPAddress? address)
    {
        if (address is { IsIPv4MappedToIPv6: true }) address = address.MapToIPv4();
        return address is null ? workstation : $"{workstation} ({address})";
    }

    public static bool IsWellFormed(string? id) =>
        id is { Length: IdLength } && id.All(c => Alphabet.Contains(c, StringComparison.Ordinal));

    public static string NewId()
    {
        Span<char> chars = stackalloc char[IdLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
