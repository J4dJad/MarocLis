namespace MarocLIS.Domain.Common;

/// <summary>
/// The kinds of file the laboratory keeps and lets users open: purchasing attachments and the
/// versions of controlled quality documents. Opening a stored file hands it to the Windows shell,
/// so a file kept by one user is run by everyone who opens it; only documents that cannot run
/// anything are accepted: PDF, images, and Word and Excel files without macros (.docm, .xlsm, the
/// old .doc and .xls can carry them).
/// </summary>
public static class SafeDocumentFile
{
    /// <summary>The extensions accepted, for a file picker filter.</summary>
    public const string PickerPattern = "*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.docx;*.xlsx";

    /// <summary>The content type of an accepted file, or null for any other kind.</summary>
    public static string? ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".tif" or ".tiff" => "image/tiff",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => null
        };
}
