using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The work behind the prescription strip that does not depend on the screen: getting pages from
/// the scanner or from files, and opening the assembled PDF. Shared by « Nouvelle demande » and
/// « Dossier ».
/// </summary>
public sealed class PrescriptionScreenActions(IDocumentScanner scanner, IUserDialogs dialogs, ILocalizer localizer,
                                              IDocumentOutput documents)
{
    /// <summary>
    /// Scans pages until the operator says there is no other. With no scanner on the workstation, the
    /// message says so and points to « Joindre un fichier »; null when nothing was scanned.
    /// </summary>
    public IReadOnlyList<PrescriptionPage>? Scan(string title, int pagesAlready)
    {
        var run = new PrescriptionScanFlow(scanner).Scan(
            _ => dialogs.Confirm(localizer["Order.PrescriptionAddMorePagesText"], localizer["Order.PrescriptionAddMorePages"]),
            pagesAlready);

        if (run.Problem is { } problem)
        {
            var message = localizer.Describe(problem);
            if (run.SuggestsAttachingAFile) dialogs.Info(message, title);
            else dialogs.Warn(message, title);
        }

        return run.Pages.Count == 0 ? null : run.Pages;
    }

    /// <summary>
    /// Files chosen in the picker (PDF, JPEG, PNG, several at once), read from disk. A file over the
    /// limit is refused before it is read; null when nothing was chosen or read.
    /// </summary>
    public IReadOnlyList<PrescriptionPage>? PickFiles(IWin32Window owner, string title)
    {
        using var picker = new OpenFileDialog
        {
            Title = title,
            Filter = $"PDF, JPEG, PNG ({PrescriptionScanPolicy.PickerPattern})|{PrescriptionScanPolicy.PickerPattern}",
            CheckFileExists = true,
            Multiselect = true
        };

        if (picker.ShowDialog(owner) != DialogResult.OK || picker.FileNames.Length == 0) return null;

        var pages = new List<PrescriptionPage>(picker.FileNames.Length);
        foreach (var path in picker.FileNames)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > PrescriptionScanPolicy.MaxFileBytes)
                {
                    dialogs.Warn(localizer.Describe(Error.Validation("Prescription_FileTooLarge", info.Name,
                                                                     PrescriptionScanPolicy.MaxFileMegabytes)), title);
                    return null;
                }

                pages.Add(new PrescriptionPage(info.Name, File.ReadAllBytes(path)));
            }
            catch (IOException ex)
            {
                dialogs.Warn($"{localizer["Documents.FileUnreadable"]}\n{ex.Message}", title);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                dialogs.Warn(localizer["Documents.FileAccessDenied"], title);
                return null;
            }
        }

        return pages;
    }

    /// <summary>Opens the PDF in the workstation's viewer, never on the printer.</summary>
    public async Task ViewAsync(byte[] pdf, string title)
    {
        var opened = await documents.ViewAsync(pdf, ".pdf");
        if (opened.IsFailure) dialogs.ShowFailure(opened, title);
    }
}
