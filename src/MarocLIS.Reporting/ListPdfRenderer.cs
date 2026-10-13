using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>A screen's list as it stands, ready to print: the rows the operator is looking at.</summary>
public sealed record ListDocument(
    string Title,
    string Caption,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool RightToLeft = false);

public interface IListPdfRenderer
{
    byte[] Render(ListDocument document);
}

/// <summary>
/// Prints any list screen.
/// <para>
/// A laboratory prints its lists all day — the collection round, the morning worklist, the
/// expiring lots, the outstanding invoices — and a dedicated document for each would be a dozen
/// layouts to keep in step with a dozen screens. This one takes the columns and rows exactly as the
/// grid shows them, so what comes out of the printer is what was on the screen. Wide lists turn
/// the page to landscape; the header repeats on every page, and every page carries its number,
/// because a loose sheet of a worklist must still say which list it belongs to.
/// </para>
/// </summary>
public sealed class ListPdfRenderer : IListPdfRenderer
{
    private readonly ReportingOptions _options;

    public ListPdfRenderer(ReportingOptions options)
    {
        _options = options;
        QuestPdfReportRenderer.Initialise(options);
    }

    public byte[] Render(ListDocument document)
    {
        var landscape = document.Columns.Count > 6;
        var font = document.RightToLeft ? _options.ArabicFontFamily : _options.LatinFontFamily;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontFamily(font, _options.ArabicFontFamily).FontSize(8.5f));

                if (document.RightToLeft) page.ContentFromRightToLeft();

                page.Header().Column(column =>
                {
                    column.Item().Text(document.Title).FontSize(14).Bold();
                    column.Item().PaddingBottom(6).Text(document.Caption).FontSize(8).FontColor(Colors.Grey.Darken1);
                });

                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        foreach (var _ in document.Columns) columns.RelativeColumn();
                    });

                    table.Header(header =>
                    {
                        foreach (var title in document.Columns)
                        {
                            header.Cell().Background(Colors.Grey.Lighten3)
                                  .BorderBottom(0.75f).BorderColor(Colors.Grey.Darken1)
                                  .Padding(3).Text(title).SemiBold();
                        }
                    });

                    foreach (var row in document.Rows)
                    {
                        for (var i = 0; i < document.Columns.Count; i++)
                        {
                            var value = i < row.Count ? row[i] : string.Empty;

                            table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten1)
                                 .Padding(3).Text(value);
                        }
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf();
    }
}
