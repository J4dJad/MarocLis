using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Turns the picture an administrator picks into the PNG stored as the laboratory's logo.
/// <para>
/// Whatever the source format, the stored logo is a PNG no wider than <see cref="MaxWidth"/>: it
/// travels inside every report and invoice PDF, and a 6000-pixel scan straight from a phone would
/// make each of them several megabytes for a mark printed four centimetres wide.
/// </para>
/// </summary>
public static class LogoImage
{
    private const int MaxWidth = 800;

    /// <summary>The file as a PNG, or null when it is not a picture that can be read.</summary>
    public static byte[]? ToPng(string path)
    {
        try
        {
            using var source = Image.FromFile(path);

            var scale = Math.Min(1.0, (double)MaxWidth / source.Width);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException
                                       or UnauthorizedAccessException or ExternalException)
        {
            // Image.FromFile reports a file that is not an image as OutOfMemoryException.
            return null;
        }
    }

    /// <summary>A stand-alone copy of a stored logo, for showing on screen.</summary>
    public static Image? Load(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
