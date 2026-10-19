using System.Globalization;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// The coded invoice (« cotation B »): the same letterhead, parties and footer as the standard
/// invoice, then one line per act with its national code, its label and its coefficient, the total
/// in B, the collection fee and the amount in dirhams. Every amount is the invoice's own stored
/// one; only the presentation differs.
/// </summary>
public sealed partial class QuestPdfInvoiceRenderer
{
    // Widths shared by the act lines and the totals under them, so the totals line up with the
    // column they total.
    private const float CodeWidth = 58;
    private const float CotationWidth = 72;
    private const float PriceWidth = 72;

    private byte[] RenderCotation(InvoiceDocument doc, AppLanguage language, InvoicePrintSettings settings)
    {
        // Without a localizer (a renderer built by hand), the coded invoice is written in French.
        var wording = _localizer is null ? Wording.French : Wording.For(_localizer, language);
        var formatter = new Formatter(language, wording["InvoicePrint.Currency"]);
        var layout = CotationInvoiceLayout.Build(doc, settings, language);

        return Create(doc, formatter, wording, c => ComposeCotationBody(c, doc, layout, formatter, wording),
                      DocumentTitle(doc.Kind, wording), receipt: false).GeneratePdf();
    }

    private static void ComposeCotationBody(IContainer container, InvoiceDocument doc, CotationInvoiceLayout layout,
                                            Formatter format, Wording wording)
    {
        container.Column(column =>
        {
            column.Item().Element(c => ComposeParties(c, doc, wording));
            column.Item().PaddingTop(10).Element(c => ComposeCotationTable(c, doc, layout, format, wording));

            if (layout.Words is { Length: > 0 } words)
                column.Item().PaddingTop(8).Text(text =>
                {
                    text.Span(wording["InvoicePrint.AmountInWords"] + " ").FontSize(9);
                    text.Span(words).FontSize(9).Bold();
                });

            var vatMention = doc.HasVat
                ? doc.Lines.Any(l => l.IsVatExempt) ? wording["InvoicePrint.VatExemptMixed"] : null
                : wording["InvoicePrint.VatExempt"];

            if (vatMention is not null)
                column.Item().PaddingTop(4).Text(vatMention).FontSize(7.5f).Italic();
        });
    }

    private static void ComposeCotationTable(IContainer container, InvoiceDocument doc, CotationInvoiceLayout layout,
                                             Formatter format, Wording wording)
    {
        var showPrice = layout.ShowLinePrice;
        var figureColumns = showPrice ? 2u : 1u;

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(CodeWidth);
                columns.RelativeColumn();
                columns.ConstantColumn(CotationWidth);
                if (showPrice) columns.ConstantColumn(PriceWidth);
            });

            table.Header(header =>
            {
                GridHeader(header.Cell(), wording["InvoicePrint.Code"]);
                GridHeader(header.Cell(), wording["InvoicePrint.Act"]);
                GridHeader(header.Cell(), wording["InvoicePrint.Cotation"]);
                if (showPrice) GridHeader(header.Cell(), wording["InvoicePrint.LinePrice"]);
            });

            foreach (var row in layout.Rows)
            {
                table.Cell().Element(GridCell).Text(wording.Isolate(row.Code)).FontSize(8.5f);

                table.Cell().Element(GridCell).Text(row.Quantity > 1
                    ? wording.Format("InvoicePrint.Quantity", row.Description, row.Quantity)
                    : row.Description).FontSize(8.5f);

                table.Cell().Element(GridCell).Element(wording.Trailing)
                     .Text(wording.Isolate(CotationText(row, format))).FontSize(8.5f);

                if (showPrice)
                    table.Cell().Element(GridCell).Element(wording.Trailing)
                         .Text(wording.Isolate(format.Amount(row.Amount))).FontSize(8.5f);
            }

            // The totals, under the label and figure columns; nothing under the code column.
            void Total(string label, string value, bool bold = false)
            {
                table.Cell().Element(c => c);
                var left = table.Cell().Element(GridCell).Text(label).FontSize(8.5f);
                var right = table.Cell().ColumnSpan(figureColumns).Element(GridCell).Element(wording.Trailing)
                                 .Text(wording.Isolate(value)).FontSize(8.5f);

                if (bold)
                {
                    left.Bold();
                    right.Bold();
                }
            }

            // A gap between the acts and their totals, as on the laboratory's model.
            table.Cell().ColumnSpan(showPrice ? 4u : 3u).Height(4);

            foreach (var total in layout.LetterTotals)
                Total(wording.Format("InvoicePrint.LetterTotal", total.Letter),
                      $"{total.Letter} {format.Coefficient(total.Value)}");

            if (layout.CollectionFee is { } fee)
                Total(wording["InvoicePrint.CollectionFee"], format.Money(fee));

            if (doc.DiscountAmount > 0m)
            {
                Total(wording["InvoicePrint.Gross"], format.Money(doc.GrossAmount));
                Total(doc.DiscountReason is { Length: > 0 } reason
                          ? wording.Format("InvoicePrint.DiscountWithReason", reason)
                          : wording["InvoicePrint.Discount"],
                      $"- {format.Money(doc.DiscountAmount)}");
            }

            if (doc.HasVat)
            {
                Total(wording["InvoicePrint.TotalExclVat"], format.Money(doc.NetAmount));
                Total(wording["InvoicePrint.Vat"], format.Money(doc.VatAmount));
            }

            Total(wording["InvoicePrint.AmountDh"], format.Money(doc.TotalAmount), bold: true);

            if (doc.PayerShareAmount > 0m)
            {
                Total(wording["InvoicePrint.PayerShare"], format.Money(doc.PayerShareAmount));
                Total(wording["InvoicePrint.PatientShare"], format.Money(doc.PatientShareAmount));
            }

            // What is still owed, only when something is: the model is the invoice of a patient who
            // has paid, and stays as it is; an unpaid one says so, with the same figures as the
            // standard format.
            if (doc.OutstandingAmount != 0m || doc.WrittenOffAmount > 0m || doc.CreditedAmount > 0m)
            {
                Total(wording["InvoicePrint.Paid"], format.Money(doc.CollectedAmount));

                if (doc.WrittenOffAmount > 0m)
                    Total(wording["InvoicePrint.WrittenOff"], format.Money(doc.WrittenOffAmount));

                if (doc.CreditedAmount > 0m)
                    Total(wording["InvoicePrint.Credited"], format.Money(doc.CreditedAmount));

                Total(wording["InvoicePrint.Outstanding"], format.Money(doc.OutstandingAmount), bold: true);
            }

            foreach (var value in layout.LetterValues)
                Total(wording.Format("InvoicePrint.LetterValue", value.Letter), format.Money(value.Value));
        });
    }

    /// <summary>
    /// The coefficient of a biology act alone, as on the model (« 90 »); another letter-key with its
    /// letter (« KB 20 »); a line with no coefficient shows its letter as stored, or nothing.
    /// </summary>
    private static string CotationText(CotationInvoiceRow row, Formatter format) => row switch
    {
        { IsBiologyAct: true } => format.Coefficient(row.Coefficient!.Value),
        { Letter: { } letter, Coefficient: { } coefficient } => $"{letter} {format.Coefficient(coefficient)}",
        { Letter: { } letter } => letter,
        _ => "—"
    };

    private static void GridHeader(IContainer cell, string text) =>
        cell.Border(0.5f).BorderColor(Colors.Grey.Darken2).Background(Colors.Grey.Lighten4)
            .PaddingVertical(3).PaddingHorizontal(3).AlignCenter()
            .Text(text).FontSize(8.5f).Bold();

    private static IContainer GridCell(IContainer container) =>
        container.Border(0.5f).BorderColor(Colors.Grey.Darken2).PaddingVertical(2.5f).PaddingHorizontal(3);

    /// <summary>
    /// The words of a printed invoice. The standard format is French whatever the session's
    /// language (its fixed text, as it always printed); the coded format reads the catalogues, in
    /// the session's language, and turns the page right to left in Arabic.
    /// </summary>
    private sealed class Wording
    {
        private static readonly Dictionary<string, string> FrenchTexts = new(StringComparer.Ordinal)
        {
            ["Licence.TrialMark"] = TrialMark.French,
            ["InvoicePrint.Invoice"] = "Facture",
            ["InvoicePrint.CreditNote"] = "Facture d'avoir",
            ["InvoicePrint.Quotation"] = "Devis",
            ["InvoicePrint.Receipt"] = "Reçu",
            ["InvoicePrint.Number"] = "N° {0}",
            ["InvoicePrint.Date"] = "Date : {0}",
            ["InvoicePrint.DueDate"] = "Échéance : {0}",
            ["InvoicePrint.Phone"] = "Tél. {0}",
            ["InvoicePrint.ProfessionalTax"] = "Patente",
            ["InvoicePrint.Patient"] = "Patient",
            ["InvoicePrint.RecordNumber"] = "N° de dossier : {0}",
            ["InvoicePrint.Orders"] = "Demande(s) : {0}",
            ["InvoicePrint.Prescriber"] = "Prescripteur : {0}",
            ["InvoicePrint.BilledTo"] = "Facturé à",
            ["InvoicePrint.Ice"] = "ICE : {0}",
            ["InvoicePrint.FiscalId"] = "IF : {0}",
            ["InvoicePrint.Payer"] = "Organisme : {0}",
            ["InvoicePrint.FooterIce"] = "ICE {0}",
            ["InvoicePrint.Page"] = "Page",
            ["InvoicePrint.Currency"] = "DH",
            ["InvoicePrint.Code"] = "Code",
            ["InvoicePrint.Act"] = "Acte de biologie demandé",
            ["InvoicePrint.Cotation"] = "Cotation (B)",
            ["InvoicePrint.LinePrice"] = "Montant (DH)",
            ["InvoicePrint.Quantity"] = "{0} (× {1})",
            ["InvoicePrint.LetterTotal"] = "Total du ({0})",
            ["InvoicePrint.CollectionFee"] = "Prise de sang (PC)",
            ["InvoicePrint.Gross"] = "Total brut",
            ["InvoicePrint.Discount"] = "Remise",
            ["InvoicePrint.DiscountWithReason"] = "Remise ({0})",
            ["InvoicePrint.TotalExclVat"] = "Total HT",
            ["InvoicePrint.Vat"] = "TVA",
            ["InvoicePrint.AmountDh"] = "Montant en DH",
            ["InvoicePrint.PayerShare"] = "Part organisme",
            ["InvoicePrint.PatientShare"] = "Part patient",
            ["InvoicePrint.Paid"] = "Montant payé",
            ["InvoicePrint.WrittenOff"] = "Passé en perte",
            ["InvoicePrint.Credited"] = "Avoirs émis",
            ["InvoicePrint.Outstanding"] = "Reste à payer",
            ["InvoicePrint.LetterValue"] = "Valeur du {0}",
            ["InvoicePrint.AmountInWords"] = "Arrêtée la présente facture à la somme de :",
            ["InvoicePrint.VatExempt"] = "Actes de biologie médicale exonérés de TVA.",
            ["InvoicePrint.VatExemptMixed"] =
                "Actes de biologie médicale exonérés de TVA ; TVA appliquée aux autres prestations."
        };

        private readonly Func<string, string> _text;

        private Wording(Func<string, string> text, bool rightToLeft, CultureInfo culture)
        {
            _text = text;
            RightToLeft = rightToLeft;
            Culture = culture;
        }

        /// <summary>The standard invoice's own French text.</summary>
        public static Wording French { get; } =
            new(key => FrenchTexts[key], rightToLeft: false, CultureInfo.InvariantCulture);

        public static Wording For(ILocalizer localizer, AppLanguage language) =>
            new(key => localizer.Get(key, language), language.IsRightToLeft(), language.ToCulture());

        public bool RightToLeft { get; }

        public CultureInfo Culture { get; }

        public string this[string key] => _text(key);

        public string Format(string key, params object?[] args) =>
            string.Format(Culture, this[key], args);

        /// <summary>A code, a number or a date keeps its own direction inside Arabic text.</summary>
        public string Isolate(string? value) =>
            RightToLeft ? CultureManager.IsolateLtr(value) : value ?? string.Empty;

        /// <summary>
        /// Digits and spaces only (a phone number) forced left to right on an Arabic page: the
        /// isolate alone left its groups in reverse order (« 00 00 00 22 05 »).
        /// </summary>
        public string Digits(string? value) =>
            RightToLeft && !string.IsNullOrEmpty(value) ? $"\u202D{value}\u202C" : value ?? string.Empty;

        /// <summary>
        /// A label and its value kept together inside Arabic text, in the direction of their own
        /// first letter: « ICE : … » left to right, « الضريبة المهنية : … » right to left.
        /// </summary>
        public string IsolateAuto(string value) =>
            RightToLeft ? $"\u2068{value}\u2069" : value;

        /// <summary>The trailing edge: right on a French page, left on an Arabic one.</summary>
        public IContainer Trailing(IContainer container) =>
            RightToLeft ? container.AlignLeft() : container.AlignRight();
    }
}
