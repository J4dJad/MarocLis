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
    ILogger<LabelPrinter>? logger = null,
    WorkstationPrinters? workstation = null) : ILabelPrinter
{
    // Taken only so the workstation's printer choice is loaded, and overrides the configured
    // printer, before the first label goes out.
    private readonly WorkstationPrinters? _workstation = workstation;

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

            var label = new SizeF(widthHundredths, heightHundredths);
            var index = 0;

            document.PrintPage += (_, e) =>
            {
                if (e.Graphics is null) { e.HasMorePages = false; return; }

                // A label printer takes the custom size and gives one label per page. A printer that
                // ignores it (an office printer, or "Microsoft Print to PDF") gives an A4 page; the
                // labels then go down the page at their true size instead of one per sheet.
                var slots = LabelSheet.Slots(e.PageBounds.Size, label);

                foreach (var slot in slots)
                {
                    if (index >= labels.Count) break;
                    DrawLabel(e.Graphics, labels[index], slot);
                    index++;
                }

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
    /// Draws one label in <paramref name="area"/> (hundredths of an inch), with the layout of the
    /// PDF labels: cap colour, patient, tube n/m, barcode, request number, analyses, container.
    /// <para>
    /// The two must look the same: an operator who prints a test label from the PDF and then from
    /// the printer should not have to learn a second label.
    /// </para>
    /// </summary>
    private void DrawLabel(Graphics graphics, SampleLabel label, RectangleF area)
    {
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using (var white = new SolidBrush(Color.White)) graphics.FillRectangle(white, area);

        var pad = labelOptions.MarginMm / 25.4f * 100f;
        var left = area.Left + pad;
        var right = area.Right - pad;
        var top = area.Top + pad;
        var width = right - left;

        using var nameFont = new Font("Arial", 7.5f, FontStyle.Bold);
        using var smallFont = new Font("Arial", 6f);
        using var markerFont = new Font("Arial", 7f, FontStyle.Bold);
        using var urgentFont = new Font("Arial", 6.5f, FontStyle.Bold);
        using var accessionFont = new Font("Arial", 6.5f, FontStyle.Bold);
        using var detailFont = new Font("Arial", 5.5f);
        using var grey = new SolidBrush(Color.FromArgb(0x61, 0x61, 0x61));

        // Header: the cap colour, then the patient, then "tube n of m" on the right.
        var marker = $"{label.SequenceInOrder}/{label.TotalSamples}";
        var markerWidth = graphics.MeasureString(marker, markerFont).Width;
        var showDob = labelOptions.PrintDateOfBirth && label.DateOfBirth is not null;
        var headerHeight = LineHeight(nameFont) + (showDob ? LineHeight(smallFont) : 0f);

        using (var cap = new SolidBrush(CapColour(label.ContainerColourHex)))
            graphics.FillRectangle(cap, left, top, 3f / 25.4f * 100f, headerHeight);

        var textLeft = left + 3f / 25.4f * 100f + 1.5f;
        var textWidth = right - markerWidth - textLeft;

        DrawLine(graphics, Shorten(label.PatientName, 26), nameFont, Brushes.Black, textLeft, top, textWidth);
        if (showDob)
            DrawLine(graphics, $"{label.DateOfBirth:dd/MM/yyyy}", smallFont, Brushes.Black,
                     textLeft, top + LineHeight(nameFont), textWidth);

        graphics.DrawString(marker, markerFont, Brushes.Black, right - markerWidth, top);
        if (label.IsUrgent)
        {
            var urgentWidth = graphics.MeasureString("URGENT", urgentFont).Width;
            graphics.DrawString("URGENT", urgentFont, Brushes.Black, right - urgentWidth,
                                top + LineHeight(markerFont));
        }

        var y = top + headerHeight + 2f;

        // Barcode at print resolution: a bitmap scaled up at draw time gets soft edges, and soft
        // edges are what a scanner fails on.
        var barcodeHeight = (labelOptions.PrintHumanReadableBarcode ? 8.5f : 7f) / 25.4f * 100f;

        try
        {
            var png = renderer.RenderBarcodeImage(label.Barcode,
                widthPx: Math.Max(200, (int)(width / 100f * graphics.DpiX)),
                heightPx: Math.Max(60, (int)(barcodeHeight / 100f * graphics.DpiY)));

            using var stream = new MemoryStream(png);
            using var barcode = Image.FromStream(stream);

            var interpolation = graphics.InterpolationMode;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.DrawImage(barcode, left, y, width, barcodeHeight);
            graphics.InterpolationMode = interpolation;
        }
        catch (Exception ex)
        {
            // A label without a barcode is worse than useless — it looks valid and cannot be
            // scanned — so the failure is drawn onto the label itself rather than swallowed.
            logger?.LogError(ex, "Rendering the barcode for {Barcode} failed.", label.Barcode);
            graphics.DrawString("BARCODE ERROR", accessionFont, Brushes.Black, left, y);
        }

        y += barcodeHeight + 1f;

        // Request number on the left, print time on the right.
        var printed = $"{label.PrintedAt:dd/MM HH:mm}";
        var printedWidth = graphics.MeasureString(printed, detailFont).Width;

        DrawLine(graphics, label.AccessionNumber, accessionFont, Brushes.Black, left, y, width - printedWidth);
        graphics.DrawString(printed, detailFont, Brushes.Black, right - printedWidth, y + 1f);
        y += LineHeight(accessionFont);

        if (labelOptions.PrintAnalysisCodes && label.AnalysisCodes.Count > 0)
        {
            DrawLine(graphics, Shorten(string.Join(" ", label.AnalysisCodes), 44), detailFont, grey, left, y, width);
            y += LineHeight(detailFont);
        }

        DrawLine(graphics, Shorten(label.ContainerName, 34), detailFont, Brushes.Black, left, y, width);
    }

    /// <summary>Line height in hundredths of an inch, which is the unit a printer page uses.</summary>
    private static float LineHeight(Font font) => font.SizeInPoints / 72f * 100f * 1.18f;

    private static void DrawLine(Graphics graphics, string text, Font font, Brush brush,
                                 float x, float y, float width)
    {
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        graphics.DrawString(text, font, brush, new RectangleF(x, y, Math.Max(1f, width), LineHeight(font) + 2f), format);
    }

    /// <summary>The tube cap colour from the catalogue, grey when it is missing or malformed.</summary>
    internal static Color CapColour(string? hex)
    {
        var grey = Color.FromArgb(0x9E, 0x9E, 0x9E);
        if (string.IsNullOrWhiteSpace(hex)) return grey;

        var value = hex.Trim().TrimStart('#');
        if (value.Length == 8) value = value[2..];
        if (value.Length != 6) return grey;

        return int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF)
            : grey;
    }

    private static string Shorten(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}

/// <summary>
/// Where labels go on a printed page, in hundredths of an inch.
/// </summary>
internal static class LabelSheet
{
    /// <summary>Gap between labels, and the offset from the edge, on a sheet that is not label stock.</summary>
    private const float SheetGap = 12f;

    /// <summary>
    /// One slot filling the page when the page is the label (the printer took the custom size);
    /// otherwise as many label-sized slots as fit down the page, then across.
    /// </summary>
    internal static IReadOnlyList<RectangleF> Slots(SizeF page, SizeF label)
    {
        // Within a few millimetres of the label size: the printer honoured it.
        if (page.Width < label.Width * 1.5f && page.Height < label.Height * 1.5f)
            return [new RectangleF(0f, 0f, page.Width, page.Height)];

        var slots = new List<RectangleF>();

        for (var x = SheetGap * 2; x + label.Width <= page.Width - SheetGap; x += label.Width + SheetGap)
        {
            for (var y = SheetGap * 2; y + label.Height <= page.Height - SheetGap; y += label.Height + SheetGap)
                slots.Add(new RectangleF(x, y, label.Width, label.Height));
        }

        // A page smaller than one label still gets one, rather than an endless run of blank pages.
        return slots.Count > 0 ? slots : [new RectangleF(0f, 0f, page.Width, page.Height)];
    }
}
