using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Whether this workstation is the database server, and whether its Windows user is an
/// administrator of it: the two conditions of a restore (Paramètres › Sauvegardes).
/// </summary>
internal static class ServerMachine
{
    /// <summary>The same Windows machine as SQL Server, as SQL Server names itself.</summary>
    public static bool IsThisMachine(string? serverMachine) =>
        !string.IsNullOrWhiteSpace(serverMachine)
        && string.Equals(serverMachine.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The process runs with the administrators' rights (« Exécuter en tant qu'administrateur »).</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// The Windows user is a member of the local Administrators group, elevated or not. With UAC on,
    /// an administrator's ordinary process carries a filtered token in which the group is deny-only,
    /// so IsInRole says no: the token's elevation type, "limited", is what says the user is one.
    /// </summary>
    public static bool IsLocalAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (IsElevated()) return true;

        using var identity = WindowsIdentity.GetCurrent();
        var size = Marshal.SizeOf<int>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return GetTokenInformation(identity.Token, TokenElevationTypeClass, buffer, size, out _)
                   && Marshal.ReadInt32(buffer) == TokenElevationTypeLimited;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Starts MarocLIS again with the administrators' rights, which on a server set up by the
    /// installer makes the Windows login sysadmin of SQL Server (BUILTIN\Administrators). False when
    /// Windows' prompt was refused.
    /// </summary>
    public static bool RelaunchElevated()
    {
        var path = Environment.ProcessPath;
        if (path is null) return false;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationTypeLimited = 3;
    private const int ErrorCancelled = 1223;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
                                                   int informationLength, out int returnLength);
}
