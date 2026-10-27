using MarocLIS.Application.Administration;
using MarocLIS.Desktop.Properties;
using MarocLIS.Domain.Common;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The language this workstation chose, at the sign-in or in the header: the desktop's counterpart
/// of the web's language cookie. Kept with the remembered user name, per Windows user.
/// </summary>
internal static class WorkstationLanguage
{
    /// <summary>The language last chosen here, or null when none was.</summary>
    public static AppLanguage? Chosen
    {
        get => LanguageSettings.Parse(LoginSettings.LastLanguage);
        set => LoginSettings.LastLanguage = value?.ToString();
    }
}
