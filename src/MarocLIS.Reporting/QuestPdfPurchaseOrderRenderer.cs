using System.Globalization;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// Renders a purchase order (bon de commande) to PDF, in the layout of the invoices: the laboratory's
/// letterhead and legal identifiers, the supplier with its ICE and IF, the lines with their prices
/// excluding VAT, the VAT and the totals, the delivery address, and the approver's name above the
/// signature area.
/// <para>
/// The text is French, the language of Moroccan purchasing and accounting documents; the language
/// parameter only governs how numbers and dates are written, as for the invoice.
/// </para>
/// </summary>
public sealed class QuestPdfPurchaseOrderRenderer : IPurchaseOrderRenderer
{
    private readonly ReportingOptions _options;

    public QuestPdfPurchaseOrderRenderer(ReportingOptions options)
    {
        _options = options;
        QuestPdfReportRenderer.Initialise(options);
    }

    public byte[] Render(PurchaseOrderDocument document, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(document);
        var format = new Formatter(language);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(14, Unit.Millimetre);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily(_options.LatinFontFamily)
                                            .FontColor(Colors.Black));

                page.Header().Element(c => ComposeHeader(c, document, format));
                page.Content().PaddingTop(6).Element(c => ComposeBody(c, document, format));
                page.Footer().Element(c => ComposeFooter(c, document.Laboratory));
            });
        }).GeneratePdf();
    }

    // ---- Header ---------------------------------------------------------------------------------

    private static void ComposeHeader(IContainer container, PurchaseOrderDocument document, Formatter format)
    {
        var laboratory = document.Laboratory;

        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (laboratory.LogoPng is { Length: > 0 })
                    row.ConstantItem(24, Unit.Millimetre)
                       .Height(18, Unit.Millimetre)
                       .Image(laboratory.LogoPng).FitArea();

                row.RelativeItem().PaddingHorizontal(4).Column(identity =>
                {
                    var name = string.IsNullOrWhiteSpace(laboratory.Name) ? laboratory.LegalName : laboratory.Name;
                    identity.Item().Text(name).FontSize(13).Bold();

                    if (!string.IsNullOrWhiteSpace(laboratory.LegalName) && laboratory.LegalName != name)
                        identity.Item().Text(laboratory.LegalName).FontSize(8);

                    var address = Join(" — ", laboratory.Address, Join(" ", laboratory.PostalCode, laboratory.City));
                    if (address.Length > 0) identity.Item().Text(address).FontSize(8);

                    var contact = Join("   ", laboratory.Phone is { Length: > 0 } ? $"Tél. {laboratory.Phone}" : null,
                                       laboratory.Email);
                    if (contact.Length > 0) identity.Item().Text(contact).FontSize(8);
                });

                row.ConstantItem(62, Unit.Millimetre).AlignRight().Column(reference =>
                {
                    reference.Item().AlignRight().Text("BON DE COMMANDE").FontSize(13).Bold().LetterSpacing(0.05f);
                    reference.Item().AlignRight().Text($"N° {document.Number}").FontSize(10).Bold();
                    reference.Item().AlignRight().Text($"Date : {format.Date(document.OrderDate)}").FontSize(8);

                    if (document.ExpectedDeliveryDate is { } expected)
                        reference.Item().AlignRight()
                                 .Text($"Livraison souhaitée : {format.Date(expected)}").FontSize(8).Bold();

                    if (!string.IsNullOrWhiteSpace(document.SupplierReference))
                        reference.Item().AlignRight()
                                 .Text($"Votre réf. : {document.SupplierReference}").FontSize(8);
                });
            });

            // ICE and IF identify the buyer on the supplier's invoice; printing them here is what
            // lets the invoice be drawn up correctly the first time.
            var legal = LegalIdentifiers(laboratory);
            if (legal.Length > 0)
                column.Item().PaddingTop(3).Text(legal).FontSize(7.5f).FontColor(Colors.Grey.Darken3);

            column.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);
        });
    }

    private static string LegalIdentifiers(InvoiceIssuer laboratory)
    {
        var rc = laboratory.CommercialRegisterNumber is { Length: > 0 } number
            ? laboratory.CommercialRegisterCourt is { Length: > 0 } court ? $"{number} ({court})" : number
            : null;

        var parts = new (string Label, string? Value)[]
        {
            ("ICE", laboratory.Ice),
            ("IF", laboratory.FiscalId),
            ("RC", rc),
            ("Patente", laboratory.ProfessionalTaxNumber),
            ("CNSS", laboratory.CnssAffiliationNumber)
        };

        return string.Join("   ·   ", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value))
                                          .Select(p => $"{p.Label} : {p.Value}"));
    }

    // ---- Body -----------------------------------------------------------------------------------

    private static void ComposeBody(IContainer container, PurchaseOrderDocument document, Formatter format)
    {
        container.Column(column =>
        {
            column.Item().Element(c => ComposeParties(c, document));
            column.Item().PaddingTop(8).Element(c => ComposeLines(c, document, format));
            column.Item().PaddingTop(8).Element(c => ComposeTotals(c, document, format));

            if (!string.IsNullOrWhiteSpace(document.Notes))
                column.Item().PaddingTop(8).Text(text =>
                {
                    text.Span("Observations : ").FontSize(8.5f).Bold();
                    text.Span(document.Notes).FontSize(8.5f);
                });

            column.Item().PaddingTop(6).Text(
                    "Merci de rappeler le numéro de ce bon de commande sur le bon de livraison et sur la facture. " +
                    "Les réactifs doivent être livrés dans le respect de leurs conditions de conservation, avec leur " +
                    "numéro de lot et leur date de péremption.")
                .FontSize(7.5f).Italic();

            column.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Column(issued =>
                {
                    issued.Item().Text($"Établi par : {document.RaisedByName}").FontSize(8);
                });

                row.ConstantItem(80, Unit.Millimetre).Column(sign =>
                {
                    sign.Item().AlignCenter().Text("Bon pour commande").FontSize(8).Bold();

                    if (document.ApprovedByName is { Length: > 0 } approver)
                        sign.Item().AlignCenter().Text(document.ApprovedAt is { } at
                            ? $"{approver}, le {format.Date(DateOnly.FromDateTime(at.LocalDateTime))}"
                            : approver).FontSize(8);

                    sign.Item().AlignCenter().Text("Cachet et signature").FontSize(7.5f).FontColor(Colors.Grey.Darken2);
                    sign.Item().Height(22, Unit.Millimetre).Border(0.5f).BorderColor(Colors.Grey.Lighten1);
                });
            });
        });
    }

    private static void ComposeParties(IContainer container, PurchaseOrderDocument document)
    {
        var supplier = document.Supplier;

        container.Border(0.5f).BorderColor(Colors.Grey.Darken1).Padding(5).Row(row =>
        {
            row.RelativeItem().Column(to =>
            {
                to.Item().Text("Fournisseur").FontSize(8).Bold();
                to.Item().Text(supplier.Name).FontSize(11).Bold();

                var place = Join(" — ", supplier.Address, supplier.City);
                if (place.Length > 0) to.Item().Text(place).FontSize(8);

                if (!string.IsNullOrWhiteSpace(supplier.Ice)) to.Item().Text($"ICE : {supplier.Ice}").FontSize(8);
                if (!string.IsNullOrWhiteSpace(supplier.FiscalId)) to.Item().Text($"IF : {supplier.FiscalId}").FontSize(8);
                if (!string.IsNullOrWhiteSpace(supplier.CommercialRegisterNumber))
                    to.Item().Text($"RC : {supplier.CommercialRegisterNumber}").FontSize(8);

                var contact = Join("   ", supplier.ContactName,
                                   supplier.Phone is { Length: > 0 } ? $"Tél. {supplier.Phone}" : null, supplier.Email);
                if (contact.Length > 0) to.Item().Text(contact).FontSize(8);
            });

            row.ConstantItem(6);

            row.RelativeItem().Column(delivery =>
            {
                delivery.Item().Text("Adresse de livraison").FontSize(8).Bold();
                delivery.Item().Text(document.DeliveryAddress ?? "—").FontSize(9);
            });
        });
    }

    private static void ComposeLines(IContainer container, PurchaseOrderDocument document, Formatter format)
    {
        var showDiscount = document.Lines.Any(l => l.DiscountRate > 0m);

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(18);                     // #
                columns.ConstantColumn(62);                     // reference
                columns.RelativeColumn(5);                      // designation
                columns.ConstantColumn(44);                     // quantity
                columns.ConstantColumn(58);                     // unit price
                if (showDiscount) columns.ConstantColumn(34);   // discount
                columns.ConstantColumn(34);                     // VAT rate
                columns.ConstantColumn(64);                     // total HT
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "#");
                HeaderCell(header.Cell(), "Référence");
                HeaderCell(header.Cell(), "Désignation");
                HeaderCell(header.Cell(), "Qté", alignRight: true);
                HeaderCell(header.Cell(), "P.U. HT", alignRight: true);
                if (showDiscount) HeaderCell(header.Cell(), "Remise", alignRight: true);
                HeaderCell(header.Cell(), "TVA", alignRight: true);
                HeaderCell(header.Cell(), "Total HT", alignRight: true);
            });

            foreach (var line in document.Lines)
            {
                table.Cell().Element(Cell).Text(line.LineNumber.ToString(CultureInfo.InvariantCulture)).FontSize(8);
                table.Cell().Element(Cell).Text(line.Reference).FontSize(8);
                table.Cell().Element(Cell).Text(line.Unit.Length > 0
                    ? $"{line.Designation} ({line.Unit})"
                    : line.Designation).FontSize(8.5f);
                table.Cell().Element(Cell).AlignRight().Text(format.Quantity(line.Quantity)).FontSize(8.5f);
                table.Cell().Element(Cell).AlignRight().Text(format.Amount(line.UnitPriceMad)).FontSize(8.5f);
                if (showDiscount)
                    table.Cell().Element(Cell).AlignRight()
                         .Text(line.DiscountRate > 0m ? format.Percent(line.DiscountRate) : "—").FontSize(8);
                table.Cell().Element(Cell).AlignRight().Text(format.Percent(line.VatRate)).FontSize(8);
                table.Cell().Element(Cell).AlignRight().Text(format.Amount(line.TotalExcludingVat)).FontSize(8.5f);
            }
        });
    }

    private static void ComposeTotals(IContainer container, PurchaseOrderDocument document, Formatter format)
    {
        container.Row(row =>
        {
            row.RelativeItem();

            row.ConstantItem(85, Unit.Millimetre).Column(totals =>
            {
                TotalLine(totals, "Total HT", format.Money(document.TotalExcludingVat));

                var breakdown = document.VatBreakdown;
                if (breakdown.Count > 1)
                    foreach (var (rate, _, vat) in breakdown)
                        TotalLine(totals, $"TVA {format.Percent(rate)}", format.Money(vat));
                else
                    TotalLine(totals, breakdown.Count == 1 ? $"TVA {format.Percent(breakdown[0].Rate)}" : "TVA",
                              format.Money(document.TotalVat));

                TotalLine(totals, "Total TTC", format.Money(document.TotalIncludingVat), bold: true, rule: true);
            });
        });
    }

    // ---- Shared pieces --------------------------------------------------------------------------

    private static void TotalLine(ColumnDescriptor column, string label, string value,
                                  bool bold = false, bool rule = false)
    {
        var item = column.Item();
        if (rule) item = item.BorderTop(0.5f).BorderColor(Colors.Grey.Darken1);

        item.PaddingVertical(1.5f).Row(row =>
        {
            var left = row.RelativeItem().Text(label).FontSize(8.5f);
            var right = row.AutoItem().AlignRight().Text(value).FontSize(8.5f);

            if (bold)
            {
                left.Bold();
                right.Bold();
            }
        });
    }

    private static void ComposeFooter(IContainer container, InvoiceIssuer laboratory)
    {
        container.PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Darken1)
                 .PaddingTop(2).Row(row =>
        {
            var identity = Join(" — ",
                                string.IsNullOrWhiteSpace(laboratory.LegalName) ? laboratory.Name : laboratory.LegalName,
                                laboratory.Ice is { Length: > 0 } ? $"ICE {laboratory.Ice}" : null);

            row.RelativeItem().Text(identity).FontSize(6.5f).FontColor(Colors.Grey.Darken2);

            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Darken2));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });
    }

    private static void HeaderCell(IContainer cell, string text, bool alignRight = false)
    {
        var container = cell.BorderBottom(0.5f).BorderColor(Colors.Grey.Darken1)
                            .PaddingVertical(2).PaddingHorizontal(2);

        if (alignRight) container = container.AlignRight();

        container.Text(text).FontSize(7.5f).Bold().FontColor(Colors.Grey.Darken3);
    }

    private static IContainer Cell(IContainer container) =>
        container.BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2)
                 .PaddingVertical(1.5f).PaddingHorizontal(2);

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Numbers and dates formatted for the requested language's culture, never the thread's.</summary>
    private sealed class Formatter(AppLanguage language)
    {
        private readonly CultureInfo _culture = language.ToCulture();

        public string Amount(decimal value) => Domain.Common.Money.Round(value).ToString("N2", _culture);

        public string Money(decimal value) => $"{Amount(value)} {Symbol}";

        public string Quantity(decimal value) => value.ToString("0.###", _culture);

        public string Percent(decimal rate) => $"{(rate * 100m).ToString("0.##", _culture)} %";

        public string Date(DateOnly value) => value.ToString("dd'/'MM'/'yyyy", _culture);

        private string Symbol => language switch
        {
            AppLanguage.English => Domain.Common.Money.CurrencyCode,
            _ => Domain.Common.Money.SymbolFr
        };
    }
}
