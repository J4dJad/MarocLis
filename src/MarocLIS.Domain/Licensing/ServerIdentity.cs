using System.Security.Cryptography;
using System.Text;

namespace MarocLIS.Domain.Licensing;

/// <summary>
/// The identifier a licence is bound to, computed the same way by every workstation and by the API.
/// <para>
/// The workstations cannot read the server's registry or hardware, so it is built from what every
/// one of them reads alike through SQL Server: the Windows name of the machine that runs the
/// instance (<c>SERVERPROPERTY('MachineName')</c>), the instance's name, and an identifier written
/// into the database the first time MarocLIS opened it. Changing a disk or a network card changes
/// none of the three. Restoring a backup on the same server keeps all three, so a laboratory that
/// lost its disk is not left without a licence. Copying the database and the application to
/// another server changes the machine name, and a second database made from nothing has another
/// identifier: either way the licence no longer matches.
/// </para>
/// <para>
/// What it does not stop is someone who copies the database to a machine renamed after the
/// server's name and instance — deliberate circumvention, which is not what this protects against
/// (see the design's « Sécurité » table).
/// </para>
/// </summary>
public static class ServerIdentity
{
    /// <summary>« 8F3A-21C9-77B0-4D1E-A2C3 »: 80 bits of a SHA-256, in groups of four.</summary>
    public static string Compute(string machineName, string? instanceName, Guid installationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        // Case is Windows's own business: a machine renamed LABSRV or labsrv is the same machine.
        var text = string.Join("|",
            "MarocLIS-serveur-1",
            machineName.Trim().ToUpperInvariant(),
            (string.IsNullOrWhiteSpace(instanceName) ? "MSSQLSERVER" : instanceName.Trim()).ToUpperInvariant(),
            installationId.ToString("D"));

        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 10);
        return string.Join("-", Enumerable.Range(0, 5).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>The identifier as typed or pasted: upper case, spaces removed.</summary>
    public static string? Normalise(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : new string([.. id.Where(c => !char.IsWhiteSpace(c))]).ToUpperInvariant();
}
