using System.Security.Cryptography;
using System.Text;
using MarocLIS.Application.Migration;

namespace MarocLIS.Desktop.Views;

/// <summary>
/// The workstation's « profils de reprise »: one JSON file each under
/// <c>%LOCALAPPDATA%\MarocLIS\Reprise\Profils</c> (MigrationProfileStore, shared with the web), the
/// password kept only encrypted with DPAPI for the Windows user, and only when the operator asked.
/// </summary>
public static class MigrationProfilePasswords
{
    // Distinguishes these blobs from any other DPAPI data of the same user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MarocLIS.Reprise.Profil");

    /// <summary>The profiles of this workstation's Windows user.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarocLIS", "Reprise", "Profils");

    /// <summary>The store of this workstation, keeping a password only as DPAPI can protect it.</summary>
    public static MigrationProfileStore Store(string? folder = null) => new(folder ?? DefaultFolder, Protect);

    /// <summary>The password a profile keeps, when this Windows user can still decrypt it.</summary>
    public static string? PasswordOf(MigrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
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
}
