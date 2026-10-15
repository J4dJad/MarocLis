using System.Globalization;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// Renders an invoice, a credit note, or the payment receipt of either, to PDF.
/// <para>
/// The text is French — the working language of Moroccan accounting and the language the DGI reads
/// invoices in. The language parameter governs how numbers and dates are written, exactly as it
/// does for the result report, so an invoice printed from an Arabic session still formats amounts
/// for that session's culture rather than for whatever the thread happens to be running.
/// </para>
/// <para>
/// Everything printed comes from the invoice as frozen at issue. The renderer computes nothing but
/// the layout: the totals are the stored ones, so a reprint is the document the customer was given.
/// </para>
/// </summary>
public sealed class QuestPdfInvoiceRenderer : IInvoiceRenderer
{
    private readonly ReportingOptions _options;

    public QuestPdfInvoiceRenderer(ReportingOptions options)
    {
        _options = options;
        QuestPdfReportRenderer.Initialise(options);
    }

    public byte[] RenderInvoice(InvoiceDocument doc, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var format = new Formatter(language);

        return Create(doc, format, c => ComposeInvoiceBody(c, doc, format),
                      DocumentTitle(doc.Kind), receipt: false).GeneratePdf();
    }

    public byte[] RenderReceipt(InvoiceDocument doc, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var format = new Formatter(language);

        return Create(doc, format, c => ComposeReceiptBody(c, doc, format),
                      "Reçu de paiement", receipt: true).GeneratePdf();
    }

    private Document Create(InvoiceDocument doc, Formatter format, Action<IContainer> body,
                            string title, bool receipt) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(14, Unit.Millimetre);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily(_options.LatinFontFamily)
                                            .FontColor(Colors.Black));

                page.Header().Element(c => ComposeHeader(c, doc, format, title, receipt));
                page.Content().PaddingTop(6).Element(body);
                page.Footer().Element(c => ComposeFooter(c, doc));
            });
        });

    private static string DocumentTitle(InvoiceKind kind) => kind switch
    {
        InvoiceKind.CreditNote => "Facture d'avoir",
        InvoiceKind.Quotation => "Devis",
        InvoiceKind.Receipt => "Reçu",
        _ => "Facture"
    };

    // ---- Header ---------------------------------------------------------------------------------

    private static void ComposeHeader(IContainer container, InvoiceDocument doc, Formatter format,
                                      string title, bool receipt)
    {
        var issuer = doc.Issuer;

        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (issuer.LogoPng is { Length: > 0 })
                    row.ConstantItem(24, Unit.Millimetre)
                       .Height(18, Unit.Millimetre)
                       .Image(issuer.LogoPng).FitArea();

                row.RelativeItem().PaddingHorizontal(4).Column(identity =>
                {
                    var name = string.IsNullOrWhiteSpace(issuer.Name) ? issuer.LegalName : issuer.Name;
                    identity.Item().Text(name).FontSize(13).Bold();

                    if (!string.IsNullOrWhiteSpace(issuer.LegalName) && issuer.LegalName != name)
                        identity.Item().Text(issuer.LegalName).FontSize(8);

                    var city = string.Join(" ", new[] { issuer.PostalCode, issuer.City }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));

                    var address = string.Join(" — ", new[] { issuer.Address, city }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));

                    if (address.Length > 0) identity.Item().Text(address).FontSize(8);

                    var contact = string.Join("   ", new[]
                    {
                        issuer.Phone is { Length: > 0 } ? $"Tél. {issuer.Phone}" : null,
                        issuer.Email
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));

                    if (contact.Length > 0) identity.Item().Text(contact).FontSize(8);
                });

                row.ConstantItem(62, Unit.Millimetre).AlignRight().Column(reference =>
                {
                    reference.Item().AlignRight().Text(title.ToUpperInvariant())
                             .FontSize(13).Bold().LetterSpacing(0.05f);

                    // A receipt has no number of its own: it is identified by the invoice it settles,
                    // and saying so in the header stops the invoice number being read as the
                    // receipt's.
                    reference.Item().AlignRight()
                             .Text(receipt ? $"{DocumentTitle(doc.Kind)} N° {doc.Number}" : $"N° {doc.Number}")
                             .FontSize(10).Bold();

                    reference.Item().AlignRight()
                             .Text(receipt
                                 ? $"du {format.Date(doc.IssueDate)}"
                                 : $"Date : {format.Date(doc.IssueDate)}").FontSize(8);

                    if (!receipt && doc.DueDate is { } due && doc.Kind == InvoiceKind.Invoice)
                        reference.Item().AlignRight()
                                 .Text($"Échéance : {format.Date(due)}").FontSize(8);
                });
            });

            // The DGI identifiers, on one line under the letterhead. ICE and IF are mandatory on
            // every Moroccan invoice; the rest are printed when the site has them.
            var legal = LegalIdentifiers(issuer);

            if (legal.Length > 0)
                column.Item().PaddingTop(3).Text(legal).FontSize(7.5f).FontColor(Colors.Grey.Darken3);

            column.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Black);
        });
    }

    private static string LegalIdentifiers(InvoiceIssuer issuer)
    {
        var rc = issuer.CommercialRegisterNumber is { Length: > 0 } number
            ? issuer.CommercialRegisterCourt is { Length: > 0 } court ? $"{number} ({court})" : number
            : null;

        var parts = new (string Label, string? Value)[]
        {
            ("ICE", issuer.Ice),
            ("IF", issuer.FiscalId),
            ("RC", rc),
            ("Patente", issuer.ProfessionalTaxNumber),
            ("CNSS", issuer.CnssAffiliationNumber),
            ("INPE", issuer.Inpe)
        };

        return string.Join("   ·   ", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value))
                                          .Select(p => $"{p.Label} : {p.Value}"));
    }

    private static void ComposeParties(IContainer container, InvoiceDocument doc)
    {
        container.Border(0.5f).BorderColor(Colors.Grey.Darken1).Padding(5).Row(row =>
        {
            row.RelativeItem().Column(patient =>
            {
                patient.Item().Text("Patient").FontSize(8).Bold();
                patient.Item().Text(doc.PatientName ?? doc.BilledToName).FontSize(11).Bold();

                if (!string.IsNullOrWhiteSpace(doc.PatientRecordNumber))
                    patient.Item().Text($"N° de dossier : {doc.PatientRecordNumber}").FontSize(8);

                if (!string.IsNullOrWhiteSpace(doc.CoveredOrders))
                    patient.Item().Text($"Demande(s) : {doc.CoveredOrders}").FontSize(8);

                if (!string.IsNullOrWhiteSpace(doc.PrescriberName))
                    patient.Item().Text($"Prescripteur : {doc.PrescriberName}").FontSize(8);
            });

            row.ConstantItem(6);

            row.RelativeItem().Column(billed =>
            {
                billed.Item().Text("Facturé à").FontSize(8).Bold();
                billed.Item().Text(doc.BilledToName).FontSize(10).Bold();

                var place = string.Join(" — ", new[] { doc.BilledToAddress, doc.BilledToCity }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                if (place.Length > 0) billed.Item().Text(place).FontSize(8);

                if (!string.IsNullOrWhiteSpace(doc.BilledToIce))
                    billed.Item().Text($"ICE : {doc.BilledToIce}").FontSize(8);

                if (!string.IsNullOrWhiteSpace(doc.BilledToFiscalId))
                    billed.Item().Text($"IF : {doc.BilledToFiscalId}").FontSize(8);

                if (!string.IsNullOrWhiteSpace(doc.PayerName))
                    billed.Item().PaddingTop(2).Text($"Organisme : {doc.PayerName}").FontSize(8);
            });
        });
    }

    // ---- Invoice --------------------------------------------------------------------------------

    private static void ComposeInvoiceBody(IContainer container, InvoiceDocument doc, Formatter format)
    {
        container.Column(column =>
        {
            column.Item().Element(c => ComposeParties(c, doc));

            if (doc.Kind == InvoiceKind.CreditNote && doc.CorrectsInvoiceNumber is { Length: > 0 })
            {
                column.Item().PaddingTop(5).Border(1).BorderColor(Colors.Black).Padding(4).Column(note =>
                {
                    note.Item().Text($"Avoir sur la facture N° {doc.CorrectsInvoiceNumber}")
                        .FontSize(8.5f).Bold();

                    if (!string.IsNullOrWhiteSpace(doc.CorrectionReason))
                        note.Item().Text($"Motif : {doc.CorrectionReason}").FontSize(8);
                });
            }

            column.Item().PaddingTop(8).Element(c => ComposeLines(c, doc, format));
            column.Item().PaddingTop(8).Element(c => ComposeTotals(c, doc, format));

            if (!string.IsNullOrWhiteSpace(doc.AmountInWords))
                column.Item().PaddingTop(8).Text(text =>
                {
                    text.Span("Arrêtée la présente facture à la somme de : ").FontSize(8.5f);
                    text.Span(doc.AmountInWords).FontSize(8.5f).Bold();
                });

            // Medical biology acts are VAT-exempt in Morocco; saying so is what makes a zero VAT
            // line read as a lawful exemption rather than an omission.
            var vatMention = doc.HasVat
                ? doc.Lines.Any(l => l.IsVatExempt)
                    ? "Actes de biologie médicale exonérés de TVA ; TVA appliquée aux autres prestations."
                    : null
                : "Actes de biologie médicale exonérés de TVA.";

            if (vatMention is not null)
                column.Item().PaddingTop(4).Text(vatMention).FontSize(7.5f).Italic();
        });
    }

    private static void ComposeLines(IContainer container, InvoiceDocument doc, Formatter format)
    {
        var showVat = doc.HasVat;

        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(52);                 // code
                columns.RelativeColumn(5);                  // designation
                columns.ConstantColumn(66);                 // amount
                if (showVat) columns.ConstantColumn(52);    // VAT
                columns.ConstantColumn(66);                 // payer share
                columns.ConstantColumn(66);                 // patient share
            });

            table.Header(header =>
            {
                HeaderCell(header.Cell(), "Code");
                HeaderCell(header.Cell(), "Désignation");
                HeaderCell(header.Cell(), "Montant", alignRight: true);
                if (showVat) HeaderCell(header.Cell(), "TVA", alignRight: true);
                HeaderCell(header.Cell(), "Part organisme", alignRight: true);
                HeaderCell(header.Cell(), "Part patient", alignRight: true);
            });

            foreach (var line in doc.Lines)
            {
                table.Cell().Element(Cell).Text(line.Code ?? string.Empty).FontSize(8);

                table.Cell().Element(Cell).Text(line.Quantity > 1
                    ? $"{line.Description} (× {line.Quantity})"
                    : line.Description).FontSize(8.5f);

                table.Cell().Element(Cell).AlignRight().Text(format.Amount(line.NetAmount)).FontSize(8.5f);

                if (showVat)
                    table.Cell().Element(Cell).AlignRight()
                         .Text(line.IsVatExempt ? "Exo." : format.Amount(line.VatAmount)).FontSize(8);

                table.Cell().Element(Cell).AlignRight()
                     .Text(line.PayerShare > 0m ? format.Amount(line.PayerShare) : "—").FontSize(8.5f);

                table.Cell().Element(Cell).AlignRight().Text(format.Amount(line.PatientShare)).FontSize(8.5f);
            }
        });
    }

    private static void ComposeTotals(IContainer container, InvoiceDocument doc, Formatter format)
    {
        container.Row(row =>
        {
            row.RelativeItem();

            row.ConstantItem(85, Unit.Millimetre).Column(totals =>
            {
                if (doc.DiscountAmount > 0m)
                {
                    TotalLine(totals, "Total brut", format.Money(doc.GrossAmount));
                    TotalLine(totals, doc.DiscountReason is { Length: > 0 } reason
                                  ? $"Remise ({reason})"
                                  : "Remise",
                              $"- {format.Money(doc.DiscountAmount)}");
                }

                if (doc.HasVat)
                {
                    TotalLine(totals, "Total HT", format.Money(doc.NetAmount));
                    TotalLine(totals, "TVA", format.Money(doc.VatAmount));
                }

                TotalLine(totals, doc.HasVat ? "Total TTC" : "Total", format.Money(doc.TotalAmount),
                          bold: true, rule: true);

                if (doc.PayerShareAmount > 0m)
                    TotalLine(totals, "Part organisme", format.Money(doc.PayerShareAmount));

                TotalLine(totals, "Part patient", format.Money(doc.PatientShareAmount));
                TotalLine(totals, "Montant payé", format.Money(doc.CollectedAmount));

                // Closes the balance without having been paid: stated apart, or the amount paid
                // would include money nobody paid.
                if (doc.WrittenOffAmount > 0m)
                    TotalLine(totals, "Passé en perte", format.Money(doc.WrittenOffAmount));

                // A credit note reduces what is owed; without this line the balance would not add up.
                if (doc.CreditedAmount > 0m)
                    TotalLine(totals, "Avoirs émis", format.Money(doc.CreditedAmount));

                TotalLine(totals, "Reste à payer", format.Money(doc.OutstandingAmount),
                          bold: true, rule: true);
            });
        });
    }

    // ---- Receipt --------------------------------------------------------------------------------

    private static void ComposeReceiptBody(IContainer container, InvoiceDocument doc, Formatter format)
    {
        container.Column(column =>
        {
            column.Item().Element(c => ComposeParties(c, doc));

            column.Item().PaddingTop(8).Text("Règlements").FontSize(9.5f).Bold();

            if (doc.Payments.Count == 0)
            {
                column.Item().PaddingTop(3).Text("Aucun règlement enregistré.").FontSize(8.5f).Italic();
            }
            else
            {
                column.Item().PaddingTop(3).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(62);     // date
                        columns.RelativeColumn(2);      // method
                        columns.RelativeColumn(3);      // reference
                        columns.ConstantColumn(80);     // amount
                    });

                    table.Header(header =>
                    {
                        HeaderCell(header.Cell(), "Date");
                        HeaderCell(header.Cell(), "Mode de paiement");
                        HeaderCell(header.Cell(), "Référence");
                        HeaderCell(header.Cell(), "Montant", alignRight: true);
                    });

                    foreach (var payment in doc.Payments)
                    {
                        // A bounced cheque or a refund is listed, struck through and labelled, but
                        // not counted: the receipt has to reconcile with the balance it states. A
                        // write-off is labelled as not received: it is counted as a loss, not paid.
                        var settled = payment.IsSettled;

                        table.Cell().Element(Cell).Text(format.Date(payment.PaymentDate)).FontSize(8.5f);

                        table.Cell().Element(Cell).Text(!settled
                            ? $"{MethodLabel(payment.Method)} ({StatusLabel(payment.Status)})"
                            : payment.IsWriteOff
                                ? $"{MethodLabel(payment.Method)} (non encaissé)"
                                : MethodLabel(payment.Method)).FontSize(8.5f);

                        var reference = string.Join(" — ", new[] { payment.Reference, payment.BankName }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));

                        table.Cell().Element(Cell).Text(reference).FontSize(8);

                        table.Cell().Element(Cell).AlignRight().Text(text =>
                        {
                            var span = text.Span(format.Money(payment.Amount)).FontSize(8.5f);
                            if (!settled) span.Strikethrough().FontColor(Colors.Grey.Darken1);
                        });
                    }
                });
            }

            column.Item().PaddingTop(10).Row(row =>
            {
                row.RelativeItem();

                row.ConstantItem(85, Unit.Millimetre).Column(totals =>
                {
                    TotalLine(totals, "Total facturé", format.Money(doc.TotalAmount));

                    if (doc.PayerShareAmount > 0m)
                    {
                        TotalLine(totals, "Part organisme", format.Money(doc.PayerShareAmount));
                        TotalLine(totals, "Part patient", format.Money(doc.PatientShareAmount));
                    }

                    TotalLine(totals, "Total payé", format.Money(doc.CollectedAmount), bold: true, rule: true);

                    if (doc.WrittenOffAmount > 0m)
                        TotalLine(totals, "Passé en perte", format.Money(doc.WrittenOffAmount));

                    if (doc.CreditedAmount > 0m)
                        TotalLine(totals, "Avoirs émis", format.Money(doc.CreditedAmount));

                    TotalLine(totals, "Reste à payer", format.Money(doc.OutstandingAmount),
                              bold: true, rule: true);
                });
            });

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem();
                row.ConstantItem(70, Unit.Millimetre).Column(sign =>
                {
                    sign.Item().AlignCenter().Text("Cachet et signature").FontSize(8).Bold();
                    sign.Item().Height(18, Unit.Millimetre);
                });
            });
        });
    }

    private static string MethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Espèces",
        PaymentMethod.Cheque => "Chèque",
        PaymentMethod.Card => "Carte bancaire",
        PaymentMethod.BankTransfer => "Virement bancaire",
        PaymentMethod.BillOfExchange => "Effet / traite",
        PaymentMethod.MobilePayment => "Paiement mobile",
        PaymentMethod.ThirdPartyPayer => "Tiers payant",
        PaymentMethod.WriteOff => "Passage en perte",
        _ => method.ToString()
    };

    private static string StatusLabel(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => "en attente",
        PaymentStatus.Bounced => "impayé",
        PaymentStatus.Refunded => "remboursé",
        PaymentStatus.Cancelled => "annulé",
        _ => string.Empty
    };

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

    private static void ComposeFooter(IContainer container, InvoiceDocument doc)
    {
        container.PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Darken1)
                 .PaddingTop(2).Row(row =>
        {
            var issuer = doc.Issuer;
            var identity = string.Join(" — ", new[]
            {
                string.IsNullOrWhiteSpace(issuer.LegalName) ? issuer.Name : issuer.LegalName,
                issuer.Ice is { Length: > 0 } ? $"ICE {issuer.Ice}" : null
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

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

    /// <summary>
    /// Numbers and dates formatted for the requested language's culture, never the thread's.
    /// </summary>
    private sealed class Formatter(AppLanguage language)
    {
        private readonly CultureInfo _culture = language.ToCulture();

        public string Amount(decimal value) =>
            Domain.Common.Money.Round(value).ToString("N2", _culture);

        public string Money(decimal value) => $"{Amount(value)} {Symbol}";

        public string Date(DateOnly value) => value.ToString("dd'/'MM'/'yyyy", _culture);

        private string Symbol => language switch
        {
            AppLanguage.English => Domain.Common.Money.CurrencyCode,
            // The French symbol in every other case: the text of this document is French, and an
            // Arabic currency sign in a left-to-right French line renders with its dots misplaced.
            _ => Domain.Common.Money.SymbolFr
        };
    }
}
