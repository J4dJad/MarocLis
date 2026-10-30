using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>What one acquisition from the scanner gave.</summary>
public enum ScanStatus
{
    /// <summary>A page came back.</summary>
    Acquired,

    /// <summary>The operator closed the scanner's window.</summary>
    Cancelled,

    /// <summary>No scanner on this workstation, or Windows' image acquisition (WIA) is missing.</summary>
    Unavailable,

    /// <summary>The scanner answered with an error (paper jam, offline, busy).</summary>
    Failed
}

/// <summary>The outcome of one acquisition: the page as JPEG or PNG bytes, or why there is none.</summary>
public sealed record ScanOutcome(ScanStatus Status, byte[]? Image = null, string? Detail = null)
{
    public static ScanOutcome Page(byte[] image) => new(ScanStatus.Acquired, image);
    public static readonly ScanOutcome Cancelled = new(ScanStatus.Cancelled);
    public static readonly ScanOutcome Unavailable = new(ScanStatus.Unavailable);
    public static ScanOutcome Failed(string detail) => new(ScanStatus.Failed, Detail: detail);
}

/// <summary>
/// A document scanner, one page per call. Behind an interface so the screens' handling of a page, a
/// cancellation and a missing scanner is tested without one; the workstation's is
/// <c>WiaDocumentScanner</c>.
/// </summary>
public interface IDocumentScanner
{
    ScanOutcome AcquirePage();
}

/// <summary>What a scanning session gave: the pages, and the error to show when there is one.</summary>
public sealed record ScanRun(IReadOnlyList<PrescriptionPage> Pages, Error? Problem)
{
    /// <summary>The scanner could not be reached: the screen points to « Joindre un fichier ».</summary>
    public bool SuggestsAttachingAFile => Problem?.ResourceKey == "Prescription_ScannerUnavailable";
}

/// <summary>
/// Scans the pages of a prescription one after the other, asking after each page whether there is
/// another, up to <see cref="PrescriptionScanPolicy.MaxPages"/>. WinForms-free, so it is tested with
/// a fake scanner; the screen supplies the question.
/// </summary>
public sealed class PrescriptionScanFlow(IDocumentScanner scanner)
{
    /// <param name="anotherPage">Asked after each page with the number scanned so far.</param>
    /// <param name="pagesAlready">Pages the prescription already has, counted against the limit.</param>
    public ScanRun Scan(Func<int, bool> anotherPage, int pagesAlready = 0)
    {
        ArgumentNullException.ThrowIfNull(anotherPage);
        var pages = new List<PrescriptionPage>();

        while (true)
        {
            ScanOutcome outcome;
            try
            {
                outcome = scanner.AcquirePage();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException
                                           or NotSupportedException or UnauthorizedAccessException)
            {
                outcome = ScanOutcome.Failed(ex.Message);
            }

            switch (outcome.Status)
            {
                case ScanStatus.Acquired when outcome.Image is { Length: > 0 } image:
                    pages.Add(new PrescriptionPage($"scan-{pages.Count + 1}.jpg", image));
                    if (pagesAlready + pages.Count >= PrescriptionScanPolicy.MaxPages || !anotherPage(pages.Count))
                        return new ScanRun(pages, null);
                    break;

                case ScanStatus.Acquired:
                case ScanStatus.Cancelled:
                    return new ScanRun(pages, null);

                // The pages already scanned are kept: the scanner going away after the second page
                // does not throw away the first two.
                case ScanStatus.Unavailable:
                    return new ScanRun(pages, pages.Count > 0 ? null : Error.Validation("Prescription_ScannerUnavailable"));

                default:
                    return new ScanRun(pages, Error.Validation("Prescription_ScanFailed", outcome.Detail ?? string.Empty));
            }
        }
    }
}
