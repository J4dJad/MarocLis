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

    /// <summary>
    /// The content type a file really holds, read from its first bytes: PDF, JPEG or PNG, or null for
    /// anything else. An extension says what the sender called the file, not what it is: a program
    /// renamed « ordonnance.pdf » has the extension of a PDF and the bytes of a program.
    /// </summary>
    public static string? ContentTypeFromSignature(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("%PDF-"u8)) return "application/pdf";
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) return "image/jpeg";
        if (content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        return null;
    }
}
