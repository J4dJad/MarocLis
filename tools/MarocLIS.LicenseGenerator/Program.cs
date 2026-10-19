using System.Globalization;
using MarocLIS.LicenseGenerator.Ui;

namespace MarocLIS.LicenseGenerator;

internal static class Program
{
    /// <summary>Overrides the data folder: a test key, a copy of the register.</summary>
    public const string FolderVariable = "MAROCLIS_GENERATEUR_DOSSIER";

    [STAThread]
    private static void Main(string[] args)
    {
        // A French tool: dates and numbers as the vendor reads them, whatever the Windows language.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("fr-FR");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(DataFolder(args)));
    }

    /// <summary>
    /// The key, the register and the packs: « --dossier », else the variable, else the vendor's own
    /// profile (%APPDATA%), which is not shared with other Windows users of the computer.
    /// </summary>
    public static string DataFolder(string[] args)
    {
        var index = Array.IndexOf(args, "--dossier");
        if (index >= 0 && index + 1 < args.Length) return Path.GetFullPath(args[index + 1]);

        if (Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } configured)
            return Path.GetFullPath(configured);

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "MarocLIS", "GenerateurLicences");
    }
}
