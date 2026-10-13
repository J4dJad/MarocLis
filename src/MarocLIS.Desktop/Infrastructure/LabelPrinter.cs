using System.Drawing.Printing;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Orders;
using MarocLIS.Domain.Common;
using MarocLIS.Reporting;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Settings a laboratory decides once about where labels come out.
/// </summary>
public sealed class LabelPrintOptions
{
    public const string SectionName = "Labels";

    /// <summary>
    /// Windows name of the label printer, exactly as it appears in Devices and Printers.
    /// <para>
    /// Empty means "no label printer configured", and the application then falls back to producing a
    /// PDF for the operator's viewer. That fallback is why this can ship empty, and also why it must
    /// not stay empty: a 50 x 25 mm label PDF opened in a PDF reader prints to the default printer,
    /// which in a laboratory that has both an A4 report printer and a label printer is the wrong one
    /// — usually one label per A4 sheet, which is how a roll of stock gets wasted on the first day.
    /// </para>
    /// </summary>
    public string LabelPrinterName { get; set; } = string.Empty;

    /// <summary>Copies per label. One; raised only where a protocol needs a duplicate on the tube.</summary>
    public int Copies { get; set; } = 1;
}

/// <inheritdoc cref="ILabelPrinter"/>
public sealed class LabelPrinter(
    LabelPrintOptions printOptions,
    LabelOptions labelOptions,
    ISampleLabelRenderer renderer,
    ILogger<LabelPrinter>? logger = null) : ILabelPrinter
{
    public string ConfiguredPrinter => printOptions.LabelPrinterName;

    public bool IsAvailable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(printOptions.LabelPrinterName)) return false;

            try
            {
                return new PrinterSettings { PrinterName = printOptions.LabelPrinterName }.IsValid;
            }
            catch (Exception ex)
            {
                // Querying the spooler can throw on a workstation whose print service is stopped.
                logger?.LogWarning(ex, "Checking the label printer failed.");
                return false;
            }
        }
    }

    public Result Print(IReadOnlyList<SampleLabel> labels)
    {
        if (labels.Count == 0) return Result.Failure(Error.Validation("Label_NothingToPrint"));

        if (string.IsNullOrWhiteSpace(printOptions.LabelPrinterName))
            return Result.Failure(Error.Validation("Label_NoPrinterConfigured"));

        if (!IsAvailable)
            return Result.Failure(
                Error.Conflict("Label_PrinterNotFound", printOptions.LabelPrinterName));

        try
        {
            using var document = new PrintDocument();

            document.DocumentName = $"MarocLIS labels ({labels.Count})";
            document.PrinterSettings.PrinterName = printOptions.LabelPrinterName;
            document.PrinterSettings.Copies = (short)Math.Clamp(printOptions.Copies, 1, 5);

            // Hundredths of an inch is what PaperSize takes; the configuration is in millimetres
            // because that is what label stock is sold in.
            var widthHundredths = (int)Math.Round(labelOptions.WidthMm / 25.4f * 100f);
            var heightHundredths = (int)Math.Round(labelOptions.HeightMm / 25.4f * 100f);

            document.DefaultPageSettings.PaperSize =
                new PaperSize("MarocLIS label", widthHundredths, heightHundredths);

            // Zero margins: the stock is the margin. A printer-imposed hard margin would otherwise
            // shrink the drawing and with it the barcode.
            document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            document.OriginAtMargins = false;

            var index = 0;

            document.PrintPage += (_, e) =>
            {
                if (e.Graphics is not null) DrawLabel(e.Graphics, labels[index], e.PageBounds);

                index++;
                e.HasMorePages = index < labels.Count;
            };

            document.Print();

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Printing {Count} label(s) failed.", labels.Count);
            return Result.Failure(Error.Conflict("Label_PrintFailed"));
        }
    }

    /// <summary>
    /// Draws one label: barcode first, then the identity that lets a human check it.
    /// <para>
    /// The barcode gets the top two thirds because it is the part a scanner must read and the part
    /// that degrades on a curved tube. The patient's name and date of birth are underneath because
    /// they are the second identity check, performed by a person who is already holding the tube.
    /// </para>
    /// </summary>
    private void DrawLabel(Graphics graphics, SampleLabel label, Rectangle bounds)
    {
        graphics.Clear(Color.White);

        var barcodeHeight = (int)(bounds.Height * 0.52f);
        var pad = Math.Max(2, (int)(bounds.Width * 0.02f));

        try
        {
            // Rendered at print resolution rather than screen resolution: a barcode bitmap scaled up
            // at draw time gets soft edges, and soft edges are what a scanner fails on.
            var png = renderer.RenderBarcodeImage(label.Barcode,
                                                   widthPx: Math.Max(bounds.Width - 2 * pad, 200),
                                                   heightPx: Math.Max(barcodeHeight - pad, 60));

            using var stream = new MemoryStream(png);
            using var barcode = Image.FromStream(stream);

            graphics.DrawImage(barcode, pad, pad, bounds.Width - 2 * pad, barcodeHeight - pad);
        }
        catch (Exception ex)
        {
            // A label without a barcode is worse than useless — it looks valid and cannot be
            // scanned — so the failure is drawn onto the label itself rather than swallowed.
            logger?.LogError(ex, "Rendering the barcode for {Barcode} failed.", label.Barcode);

            using var warningFont = new Font(FontFamily.GenericSansSerif, 6f, FontStyle.Bold);
            graphics.DrawString("BARCODE ERROR", warningFont, Brushes.Black, pad, pad);
        }

        var y = barcodeHeight;

        using var codeFont = new Font(FontFamily.GenericMonospace, 6.5f, FontStyle.Bold);
        using var nameFont = new Font(FontFamily.GenericSansSerif, 6f, FontStyle.Bold);
        using var detailFont = new Font(FontFamily.GenericSansSerif, 5.5f);

        // Latin script and digits throughout, deliberately: a label is transcribed by hand and read
        // by a scanner, and both go wrong when the digits are not 0-9. This mirrors the rule the
        // rest of the application applies to barcodes and record numbers.
        graphics.DrawString(label.Barcode, codeFont, Brushes.Black, pad, y);
        y += codeFont.Height - 1;

        graphics.DrawString(Shorten(label.PatientName, 28), nameFont, Brushes.Black, pad, y);
        y += nameFont.Height - 1;

        var detail = label.DateOfBirth is { } dob
            ? $"{dob:dd/MM/yyyy}  {label.SequenceInOrder}/{label.TotalSamples}  {label.ContainerName}"
            : $"{label.SequenceInOrder}/{label.TotalSamples}  {label.ContainerName}";

        graphics.DrawString(Shorten(detail, 34), detailFont, Brushes.Black, pad, y);

        if (label.IsUrgent)
        {
            using var urgentFont = new Font(FontFamily.GenericSansSerif, 6f, FontStyle.Bold);

            var size = graphics.MeasureString("URGENT", urgentFont);

            graphics.DrawString("URGENT", urgentFont, Brushes.Black,
                                 bounds.Width - size.Width - pad, barcodeHeight);
        }
    }

    private static string Shorten(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
