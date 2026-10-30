using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The workstation's scanner, through Windows Image Acquisition (WIA 2.0, « wiaaut.dll », part of
/// Windows from 7 to 11), reached by COM late binding: no interop assembly and no package, and a
/// workstation without it — a Windows « N » edition, a server — is told so instead of failing to
/// start.
/// <para>
/// One page per call, through WIA's own dialog (<c>CommonDialog.ShowAcquireImage</c>): it lists the
/// scanners, previews the page and lets the operator crop it, in the language of Windows. The page
/// is asked for as JPEG; a driver that answers in BMP or TIFF has its page converted here to JPEG,
/// since a bitmap of an A4 page at 300 dpi is 25 MB, above what a page may weigh.
/// </para>
/// </summary>
public sealed class WiaDocumentScanner(ILogger<WiaDocumentScanner>? logger = null) : IDocumentScanner
{
    private const int ScannerDeviceType = 1;
    private const int ColorIntent = 1;
    private const int MaximizeQuality = 131072;
    private const string FormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    // WIA_S_NO_DEVICE_AVAILABLE, WIA_ERROR_OFFLINE, WIA_ERROR_DEVICE_COMMUNICATION, and the class
    // not registered when wiaaut.dll is missing.
    private static readonly HashSet<int> NoScanner =
    [
        unchecked((int)0x80210015), unchecked((int)0x80210005), unchecked((int)0x8021000A),
        unchecked((int)0x80040154)
    ];

    // WIA_ERROR_USER_CANCEL / the dialog closed.
    private static readonly HashSet<int> Cancelled = [unchecked((int)0x80210064)];

    public ScanOutcome AcquirePage()
    {
        var type = Type.GetTypeFromProgID("WIA.CommonDialog", throwOnError: false);
        if (type is null) return ScanOutcome.Unavailable;

        object? dialog = null;
        object? image = null;

        try
        {
            dialog = Activator.CreateInstance(type);
            if (dialog is null) return ScanOutcome.Unavailable;

            // ShowAcquireImage(DeviceType, Intent, Bias, FormatID, AlwaysSelectDevice, UseCommonUI, CancelError)
            image = type.InvokeMember("ShowAcquireImage", System.Reflection.BindingFlags.InvokeMethod, null, dialog,
                                      [ScannerDeviceType, ColorIntent, MaximizeQuality, FormatJpeg, false, true, false],
                                      System.Globalization.CultureInfo.InvariantCulture);
            if (image is null) return ScanOutcome.Cancelled;

            var fileData = image.GetType().InvokeMember("FileData", System.Reflection.BindingFlags.GetProperty,
                                                        null, image, null, System.Globalization.CultureInfo.InvariantCulture);
            var bytes = fileData?.GetType().InvokeMember("BinaryData", System.Reflection.BindingFlags.GetProperty,
                                                         null, fileData, null, System.Globalization.CultureInfo.InvariantCulture) as byte[];
            if (fileData is not null && Marshal.IsComObject(fileData)) Marshal.ReleaseComObject(fileData);

            if (bytes is not { Length: > 0 }) return ScanOutcome.Failed("page vide");

            return ScanOutcome.Page(AsJpegOrPng(bytes));
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is COMException com)
        {
            return FromCom(com);
        }
        catch (COMException com)
        {
            return FromCom(com);
        }
        catch (Exception ex) when (ex is InvalidComObjectException or NotSupportedException or MissingMethodException
                                       or ArgumentException or ExternalException)
        {
            logger?.LogWarning(ex, "Numérisation WIA impossible");
            return ScanOutcome.Failed(ex.Message);
        }
        finally
        {
            if (image is not null && Marshal.IsComObject(image)) Marshal.ReleaseComObject(image);
            if (dialog is not null && Marshal.IsComObject(dialog)) Marshal.ReleaseComObject(dialog);
        }
    }

    private ScanOutcome FromCom(COMException ex)
    {
        if (Cancelled.Contains(ex.HResult)) return ScanOutcome.Cancelled;
        if (NoScanner.Contains(ex.HResult)) return ScanOutcome.Unavailable;

        logger?.LogWarning(ex, "Numérisation WIA en erreur (0x{HResult:X8})", ex.HResult);
        return ScanOutcome.Failed($"{ex.Message} (0x{ex.HResult:X8})");
    }

    /// <summary>The page as it came when it is a JPEG or a PNG; otherwise converted to JPEG.</summary>
    internal static byte[] AsJpegOrPng(byte[] bytes)
    {
        if (Domain.Common.SafeDocumentFile.ContentTypeFromSignature(bytes) is "image/jpeg" or "image/png") return bytes;

        using var input = new MemoryStream(bytes);
        using var picture = Image.FromStream(input);
        using var output = new MemoryStream();

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        picture.Save(output, codec, parameters);
        return output.ToArray();
    }
}
