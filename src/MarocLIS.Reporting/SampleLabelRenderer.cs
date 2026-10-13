using MarocLIS.Application.Orders;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace MarocLIS.Reporting;

public sealed class LabelOptions
{
    public const string SectionName = "Labels";

    /// <summary>Label width in millimetres. 50 × 25 mm suits the common tube-label stock.</summary>
    public float WidthMm { get; set; } = 50f;
    public float HeightMm { get; set; } = 25f;
    public float MarginMm { get; set; } = 1.5f;

    /// <summary>
    /// Barcode symbology. Code 128 is the default: it encodes the full ASCII range densely and
    /// every laboratory scanner reads it. Code 39 is offered only for older dedicated readers.
    /// </summary>
    public string Symbology { get; set; } = "Code128";

    /// <summary>
    /// Print the human-readable barcode value under the bars. Keep this on — when a scanner
    /// fails or a label is creased, somebody has to be able to type the number.
    /// </summary>
    public bool PrintHumanReadableBarcode { get; set; } = true;

    /// <summary>Include the date of birth. On by default: it is the second identity check.</summary>
    public bool PrintDateOfBirth { get; set; } = true;

    /// <summary>Include the analysis codes carried by this tube.</summary>
    public bool PrintAnalysisCodes { get; set; } = true;
}

/// <summary>Renders tube labels. Implemented here so the desktop client never touches a PDF library.</summary>
public interface ISampleLabelRenderer
{
    /// <summary>One PDF containing one page per label, ready to send to the label printer.</summary>
    byte[] Render(IReadOnlyList<SampleLabel> labels);

    /// <summary>A PNG of the barcode alone, for on-screen display and for a reprint dialog.</summary>
    byte[] RenderBarcodeImage(string value, int widthPx = 400, int heightPx = 100);
}

/// <summary>
/// Tube-label renderer.
/// <para>
/// A label has one job: let whoever holds the tube confirm it belongs to this patient, and let a
/// scanner bind it to the right order. Everything on it serves one of those two ends, and the
/// layout is driven by what survives the physical realities — a curved tube, a fridge, a creased
/// label and a scanner that occasionally fails.
/// </para>
/// </summary>
public sealed class SampleLabelRenderer(LabelOptions options) : ISampleLabelRenderer
{
    public byte[] Render(IReadOnlyList<SampleLabel> labels)
    {
        if (labels.Count == 0) return [];

        return Document.Create(container =>
        {
            foreach (var label in labels)
            {
                container.Page(page =>
                {
                    page.Size(options.WidthMm, options.HeightMm, Unit.Millimetre);
                    page.Margin(options.MarginMm, Unit.Millimetre);
                    // Labels stay left-to-right in every UI language: the barcode and the accession
                    // number are Latin, and a scanner does not care about the operator's language.
                    page.DefaultTextStyle(x => x.FontSize(6).FontColor(Colors.Black));
                    page.Content().Element(c => ComposeLabel(c, label));
                });
            }
        }).GeneratePdf();
    }

    private void ComposeLabel(IContainer container, SampleLabel label)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                // A colour bar matching the tube cap. The phlebotomist matches label to tube by
                // colour before reading anything — this is the fastest check on the label.
                row.ConstantItem(3, Unit.Millimetre)
                   .Background(NormaliseColour(label.ContainerColourHex));

                row.ConstantItem(1);

                row.RelativeItem().Column(identity =>
                {
                    identity.Item().Text(text =>
                    {
                        text.Span(Truncate(label.PatientName, 26)).FontSize(7.5f).Bold();
                    });

                    if (options.PrintDateOfBirth && label.DateOfBirth is { } dob)
                        identity.Item().Text($"{dob:dd/MM/yyyy}").FontSize(6);
                });

                row.AutoItem().AlignRight().Column(marker =>
                {
                    // Tube n of m: the draw is incomplete until every tube is accounted for.
                    marker.Item().AlignRight()
                          .Text($"{label.SequenceInOrder}/{label.TotalSamples}")
                          .FontSize(7).Bold();

                    if (label.IsUrgent)
                        marker.Item().AlignRight().Text("URGENT").FontSize(6.5f).Bold();
                });
            });

            column.Item().PaddingTop(0.5f).Element(c => ComposeBarcode(c, label.Barcode));

            column.Item().Row(row =>
            {
                row.RelativeItem().Text(label.AccessionNumber).FontSize(6.5f).Bold();
                row.AutoItem().Text($"{label.PrintedAt:dd/MM HH:mm}").FontSize(5.5f);
            });

            if (options.PrintAnalysisCodes && label.AnalysisCodes.Count > 0)
            {
                column.Item().Text(Truncate(string.Join(" ", label.AnalysisCodes), 44))
                      .FontSize(5.5f).FontColor(Colors.Grey.Darken2);
            }

            column.Item().Text(Truncate(label.ContainerName, 34)).FontSize(5.5f);
        });
    }

    private void ComposeBarcode(IContainer container, string value)
    {
        var png = RenderBarcodeImage(value, 640, options.PrintHumanReadableBarcode ? 130 : 110);

        container.Height(options.PrintHumanReadableBarcode ? 8.5f : 7f, Unit.Millimetre)
                 .AlignCenter()
                 .Image(png).FitArea();
    }

    /// <summary>
    /// Encodes the barcode and rasterises it with SkiaSharp.
    /// <para>
    /// ZXing produces the bit matrix and Skia draws it, rather than pulling in
    /// <c>ZXing.Net.Bindings.SkiaSharp</c>. One fewer dependency to keep aligned, and SkiaSharp is
    /// already present transitively through the PDF library — a barcode renderer that breaks on a
    /// package upgrade would stop the laboratory from drawing blood.
    /// </para>
    /// </summary>
    public byte[] RenderBarcodeImage(string value, int widthPx = 400, int heightPx = 100)
    {
        var format = options.Symbology.Equals("Code39", StringComparison.OrdinalIgnoreCase)
            ? BarcodeFormat.CODE_39
            : BarcodeFormat.CODE_128;

        var textHeight = options.PrintHumanReadableBarcode ? Math.Max(14, heightPx / 6) : 0;
        var barsHeight = Math.Max(20, heightPx - textHeight);

        var matrix = new MultiFormatWriter().encode(
            value, format, widthPx, barsHeight,
            new Dictionary<EncodeHintType, object>
            {
                // Quiet zone: a scanner needs clear space either side of the bars, and a label
                // printed edge to edge is the commonest cause of a "won't scan" complaint.
                [EncodeHintType.MARGIN] = 8,
                [EncodeHintType.PURE_BARCODE] = true
            });

        using var surface = SKSurface.Create(new SKImageInfo(matrix.Width, barsHeight + textHeight));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var black = new SKPaint { Color = SKColors.Black, IsAntialias = false };

        // The matrix is one column per module; fill full-height rectangles so the bars stay crisp
        // at label resolution instead of being smeared by interpolation.
        for (var x = 0; x < matrix.Width; x++)
        {
            for (var y = 0; y < matrix.Height; y++)
            {
                if (matrix[x, y]) canvas.DrawRect(x, y, 1, 1, black);
            }
        }

        if (options.PrintHumanReadableBarcode)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Consolas")
                                        ?? SKTypeface.Default, textHeight * 0.85f);
            using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            var width = font.MeasureText(value);
            canvas.DrawText(value, (matrix.Width - width) / 2f,
                            barsHeight + textHeight * 0.85f, SKTextAlign.Left, font, textPaint);
        }

        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Normalises a cap colour to a QuestPDF-acceptable value, falling back to a visible grey.
    /// A malformed colour in the catalogue must not take down a label print at the drawing chair.
    /// </summary>
    private static string NormaliseColour(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Colors.Grey.Medium;

        var value = hex.Trim();
        if (!value.StartsWith('#')) value = "#" + value;

        return value.Length is 7 or 9 ? value : Colors.Grey.Medium;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
