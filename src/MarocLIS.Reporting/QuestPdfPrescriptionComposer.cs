using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace MarocLIS.Reporting;

/// <summary>
/// Assembles the pages of a scanned prescription into the one PDF kept on the order.
/// <para>
/// A photo or a scanner page becomes a page of its own, on an A4 sheet turned the way the picture
/// is: upright as the camera recorded it, brought to a long side of
/// <see cref="PrescriptionScanPolicy.ImageLongSide"/> pixels and recompressed as JPEG at quality
/// <see cref="PrescriptionScanPolicy.JpegQuality"/>. Re-encoding also drops what a phone writes in a
/// photo besides the picture (where it was taken, the phone's model). A PDF is kept as it is, its
/// pages merged in their place by QuestPDF's document operations (qpdf, shipped with QuestPDF); a
/// single PDF handed in alone is returned untouched.
/// </para>
/// </summary>
public sealed class QuestPdfPrescriptionComposer : IPrescriptionDocumentComposer
{
    public QuestPdfPrescriptionComposer(ReportingOptions options)
    {
        QuestPdfReportRenderer.Initialise(options);
    }

    public Result<byte[]> Compose(IReadOnlyList<PrescriptionSource> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) return Result.Failure<byte[]>(Error.Validation("Prescription_NoPage"));

        // Runs of pictures become one document each; PDFs stay whole. The parts are then merged in
        // the order given.
        var parts = new List<(string Name, byte[] Pdf)>();
        var pictures = new List<Picture>();

        foreach (var page in pages)
        {
            if (page.ContentType == PrescriptionScanPolicy.PdfContentType)
            {
                Flush();
                parts.Add((page.FileName, page.Content));
                continue;
            }

            if (Downscale(page.Content) is not { } picture)
                return Result.Failure<byte[]>(Error.Validation("Prescription_UnreadableFile", page.FileName));

            pictures.Add(picture);
        }

        Flush();

        return parts.Count == 1 ? Result.Success(parts[0].Pdf) : Merge(parts);

        void Flush()
        {
            if (pictures.Count == 0) return;
            parts.Add(("ordonnance", PicturesToPdf(pictures)));
            pictures.Clear();
        }
    }

    /// <summary>A picture ready to be put on a page.</summary>
    public readonly record struct Picture(byte[] Jpeg, int Width, int Height);

    /// <summary>
    /// The picture upright, its long side at most <see cref="PrescriptionScanPolicy.ImageLongSide"/>
    /// pixels, on white (a PNG may be transparent), as JPEG. Null when it cannot be decoded.
    /// </summary>
    public static Picture? Downscale(byte[] content)
    {
        try
        {
            using var stream = new SKMemoryStream(content);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return null;

            using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var read = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (read is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            // Scaled first, in the picture's stored axes, then turned: turning a 12-megapixel photo
            // at full size would cost a second copy of it for nothing.
            var scale = Math.Min(1.0, (double)PrescriptionScanPolicy.ImageLongSide / Math.Max(info.Width, info.Height));
            var storedWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
            var storedHeight = Math.Max(1, (int)Math.Round(info.Height * scale));

            var origin = codec.EncodedOrigin;
            var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                                 or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var width = turned ? storedHeight : storedWidth;
            var height = turned ? storedWidth : storedHeight;

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            Orient(canvas, origin, storedWidth, storedHeight);

            using (var image = SKImage.FromBitmap(decoded))
            using (var paint = new SKPaint { IsAntialias = true })
                canvas.DrawImage(image, new SKRect(0, 0, storedWidth, storedHeight),
                                 new SKSamplingOptions(SKCubicResampler.Mitchell), paint);

            canvas.Flush();
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, PrescriptionScanPolicy.JpegQuality);
            return data is null ? null : new Picture(data.ToArray(), width, height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the canvas so a picture drawn in its stored axes (<paramref name="w"/> × <paramref name="h"/>)
    /// comes out as the camera held it (the EXIF orientation).
    /// </summary>
    private static void Orient(SKCanvas canvas, SKEncodedOrigin origin, int w, int h)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(-90); break;
        }
    }

    /// <summary>One A4 page per picture, portrait or landscape like the picture, the picture fitted in.</summary>
    private static byte[] PicturesToPdf(IReadOnlyList<Picture> pictures) =>
        Document.Create(container =>
        {
            foreach (var picture in pictures)
            {
                container.Page(page =>
                {
                    page.Size(picture.Width > picture.Height ? PageSizes.A4.Landscape() : PageSizes.A4);
                    page.Margin(5, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.Content().AlignCenter().AlignMiddle()
                        .Image(picture.Jpeg)
                        .FitArea()
                        // Already the size and quality wanted: kept as it is, not resampled again.
                        .WithRasterDpi(1200)
                        .WithCompressionQuality(ImageCompressionQuality.VeryHigh);
                });
            }
        })
        .WithMetadata(new DocumentMetadata { Title = "Ordonnance", Creator = "MarocLIS", Producer = "MarocLIS" })
        .GeneratePdf();

    /// <summary>
    /// Merges the parts through temporary files, as qpdf works on files. When the merge fails, each
    /// part is tried alone so the refusal names the PDF that cannot be read (damaged, or protected by
    /// a password).
    /// </summary>
    private static Result<byte[]> Merge(List<(string Name, byte[] Pdf)> parts)
    {
        var folder = Path.Combine(Path.GetTempPath(), "maroclis-ordonnance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var files = new List<string>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                var file = Path.Combine(folder, $"{i:D3}.pdf");
                File.WriteAllBytes(file, parts[i].Pdf);
                files.Add(file);
            }

            var output = Path.Combine(folder, "ordonnance.pdf");

            try
            {
                var operation = DocumentOperation.LoadFile(files[0]);
                foreach (var file in files.Skip(1)) operation = operation.MergeFile(file);
                operation.Save(output);
                return Result.Success(File.ReadAllBytes(output));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                for (var i = 0; i < files.Count; i++)
                {
                    try
                    {
                        DocumentOperation.LoadFile(files[i]).Save(Path.Combine(folder, $"probe-{i:D3}.pdf"));
                    }
                    catch (Exception probe) when (probe is not OutOfMemoryException)
                    {
                        return Result.Failure<byte[]>(Error.Validation("Prescription_UnreadableFile", parts[i].Name));
                    }
                }

                return Result.Failure<byte[]>(Error.Validation("Prescription_UnreadableFile", parts[0].Name));
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
