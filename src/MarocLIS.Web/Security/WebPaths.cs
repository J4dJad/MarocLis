namespace MarocLIS.Web.Security;

/// <summary>The addresses of the web interface that the gates treat apart.</summary>
public static class WebPaths
{
    public const string Licence = "/licence";
    public const string PasswordChange = "/mot-de-passe";
    public const string SignOut = "/deconnexion";
    public const string Language = "/langue";

    /// <summary>The files every page needs, the sign-in and the licence message included: no gate holds them.</summary>
    private static readonly string[] AssetPrefixes = ["/_framework", "/_content", "/css", "/js", "/icons", "/fonts"];

    public static bool IsAsset(PathString path) =>
        AssetPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
        || path.Equals("/favicon.png", StringComparison.OrdinalIgnoreCase)
        || path.Value?.EndsWith(".styles.css", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The circuit's connection (SignalR): not a page, never redirected.</summary>
    public static bool IsCircuit(PathString path) =>
        path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase);

    /// <summary>A local address to go back to, or "/" — never another site (an open redirect).</summary>
    public static string LocalOrHome(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
                                   && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
