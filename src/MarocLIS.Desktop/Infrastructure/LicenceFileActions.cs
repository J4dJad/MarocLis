using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Licensing;
using MarocLIS.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// « Importer une licence… » and « Exporter la demande », shared by the import screen of a new
/// installation and the licence tab: the same file dialogs, the same messages, the same service.
/// </summary>
internal static class LicenceFileActions
{
    /// <summary>The version written into the request, so the vendor knows what the laboratory runs.</summary>
    public static string AppVersion => System.Windows.Forms.Application.ProductVersion.Split('+')[0];

    /// <summary>
    /// Asks for a licence file and imports it, in a scope of its own. The licence is in force on
    /// this workstation as soon as this returns it; a refusal has been explained to the operator.
    /// </summary>
    public static async Task<LicenceStatus?> ImportAsync(IWin32Window owner, IServiceScopeFactory scopes,
                                                         IUserDialogs dialogs, ILocalizer localizer, string title)
    {
        using var picker = new OpenFileDialog
        {
            Title = UserDialogs.CaptionOf(title, title),
            Filter = $"{localizer["Licence.FileFilter"]} (*{LicenceFile.Extension})|*{LicenceFile.Extension}|"
                     + $"{localizer["Licence.AllFiles"]} (*.*)|*.*",
            CheckFileExists = true
        };

        if (picker.ShowDialog(owner) != DialogResult.OK) return null;

        string text;
        try
        {
            // A licence is a few hundred bytes; anything far larger is not one, and is not read.
            if (!LicenceTabRules.IsReadableSize(new FileInfo(picker.FileName).Length))
            {
                dialogs.Warn(localizer["Licence_FileUnreadable"], title);
                return null;
            }

            text = await File.ReadAllTextAsync(picker.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Warn(localizer.Format("Licence.FileNotRead", ex.Message), title);
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var imported = await scope.ServiceProvider.GetRequiredService<LicenceService>()
                                  .ImportAsync(text, picker.FileName);

        if (imported.IsFailure)
        {
            dialogs.ShowFailure(imported, title);
            return null;
        }

        var terms = imported.Value.Terms!;
        dialogs.Info(localizer.Format("Licence.Imported", terms.Number, terms.Version, terms.Laboratory), title);
        return imported.Value;
    }

    /// <summary>Writes the request file wherever the operator chooses, to send to the vendor.</summary>
    public static async Task ExportRequestAsync(IWin32Window owner, IServiceScopeFactory scopes,
                                                IUserDialogs dialogs, ILocalizer localizer, string title)
    {
        LicenceRequest request;
        await using (var scope = scopes.CreateAsyncScope())
            request = await scope.ServiceProvider.GetRequiredService<LicenceService>().BuildRequestAsync(AppVersion);

        using var dialog = new SaveFileDialog
        {
            Title = UserDialogs.CaptionOf(title, title),
            FileName = RequestFileName(request),
            Filter = $"{localizer["Licence.RequestFilter"]} (*{LicenceRequest.Extension})|*{LicenceRequest.Extension}",
            DefaultExt = LicenceRequest.Extension.TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK) return;

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, request.Write());
            dialogs.Info(localizer.Format("Licence.RequestSaved", dialog.FileName), title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Warn(localizer.Format("Licence.FileNotWritten", ex.Message), title);
        }
    }

    /// <summary>« demande-licence-LABO-CENTRAL.demande »: the site in the name, so the vendor's inbox tells them apart.</summary>
    internal static string RequestFileName(LicenceRequest request) => LicenceTabRules.RequestFileName(request);

    public static void CopyServerId(string? serverId, IUserDialogs dialogs, ILocalizer localizer)
    {
        if (string.IsNullOrEmpty(serverId)) return;

        try
        {
            Clipboard.SetText(serverId);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holding the clipboard; the identifier is on screen to copy by hand.
            dialogs.Warn(localizer["Licence.ClipboardBusy"]);
        }
    }
}
