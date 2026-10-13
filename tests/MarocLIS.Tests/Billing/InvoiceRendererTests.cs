using System.Text;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Reporting;
using Xunit;

namespace MarocLIS.Tests.Billing;

/// <summary>
/// Tests for the invoice and payment-receipt PDFs printed from the billing desk.
/// <para>
/// The layout itself is not asserted — a PDF's visual content is not something a unit test reads
/// usefully. What is asserted is that both documents render to a PDF from a realistic invoice, in
/// every language the application serves, including the awkward cases: a payer share, a discount,
/// a VAT-bearing line, and a bounced cheque among the payments.
/// </para>
/// </summary>
public sealed class InvoiceRendererTests
{
    private static readonly QuestPdfInvoiceRenderer Renderer = new(new ReportingOptions
    {
        // No fonts beside the test binaries: QuestPDF falls back to its own.
        FontDirectory = Path.Combine(AppContext.BaseDirectory, "no-fonts-here")
    });

    private static InvoiceDocument Sample(params InvoiceDocumentPayment[] payments) => new(
        InvoiceId: Guid.NewGuid(),
        Number: "FAC/CASA/2026/000318",
        Kind: InvoiceKind.Invoice,
        Status: InvoiceStatus.PartiallyPaid,
        IssueDate: new DateOnly(2026, 10, 12),
        DueDate: new DateOnly(2026, 11, 11),
        Issuer: new InvoiceIssuer(
            "Laboratoire d'analyses médicales Al Amal", "LABO AL AMAL SARL",
            "12, boulevard Zerktouni", "Casablanca", "20000", "05 22 00 00 00",
            "contact@example.ma", "001234567000089", "12345678", "123456", "Casablanca",
            "34567890", "1234567", "LAB0001", null),
        BilledToName: "CNSS",
        BilledToIce: "002345678000012",
        BilledToFiscalId: null,
        BilledToAddress: "649, boulevard Mohammed V",
        BilledToCity: "Casablanca",
        PatientName: "EL ALAOUI Fatima",
        PatientRecordNumber: "P-2026-004512",
        PayerName: "CNSS",
        CoveredOrders: "CASA-261012-0042",
        PrescriberName: "Dr Bennani",
        CorrectsInvoiceNumber: null,
        CorrectionReason: null,
        GrossAmount: 1_340m,
        DiscountAmount: 40m,
        DiscountReason: "Geste commercial",
        NetAmount: 1_300m,
        VatAmount: 20m,
        TotalAmount: 1_320m,
        PayerShareAmount: 880m,
        PatientShareAmount: 440m,
        PaidAmount: 300m,
        AmountInWords: AmountInWords.Render(1_320m, AppLanguage.French),
        Lines:
        [
            new InvoiceDocumentLine(1, "NFS", "Numération formule sanguine", 1, 100m, 97m, 0m, 97m,
                                    77.6m, 19.4m, IsVatExempt: true, VatRate: 0m),
            new InvoiceDocumentLine(2, "GLY", "Glycémie à jeun", 1, 1_140m, 1_105m, 0m, 1_105m,
                                    802.4m, 302.6m, IsVatExempt: true, VatRate: 0m),
            new InvoiceDocumentLine(3, null, "Certificat médical", 1, 100m, 98m, 20m, 118m,
                                    0m, 118m, IsVatExempt: false, VatRate: 0.20m)
        ],
        Payments: payments);

    private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 4);

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void Invoice_renders_as_a_pdf(AppLanguage language)
    {
        var pdf = Renderer.RenderInvoice(Sample(), language);

        pdf.Should().NotBeEmpty();
        Header(pdf).Should().Be("%PDF");
    }

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void Receipt_renders_as_a_pdf_with_settled_and_bounced_payments(AppLanguage language)
    {
        var document = Sample(
            new InvoiceDocumentPayment(new DateOnly(2026, 10, 12), PaymentMethod.Cash,
                                       PaymentStatus.Settled, 200m, null, null),
            new InvoiceDocumentPayment(new DateOnly(2026, 10, 13), PaymentMethod.Cheque,
                                       PaymentStatus.Bounced, 140m, "CHQ 0012345", "Attijariwafa bank"),
            new InvoiceDocumentPayment(new DateOnly(2026, 10, 14), PaymentMethod.Card,
                                       PaymentStatus.Settled, 100m, "AUTH 998877", null));

        var pdf = Renderer.RenderReceipt(document, language);

        pdf.Should().NotBeEmpty();
        Header(pdf).Should().Be("%PDF");
    }

    [Fact]
    public void Receipt_renders_when_nothing_has_been_paid_yet()
    {
        var pdf = Renderer.RenderReceipt(Sample() with { PaidAmount = 0m }, AppLanguage.French);

        Header(pdf).Should().Be("%PDF");
    }

    [Fact]
    public void Credit_note_renders_with_its_reference_to_the_original()
    {
        var creditNote = Sample() with
        {
            Kind = InvoiceKind.CreditNote,
            Number = "AV/CASA/2026/000007",
            CorrectsInvoiceNumber = "FAC/CASA/2026/000318",
            CorrectionReason = "Analyse facturée en double"
        };

        Header(Renderer.RenderInvoice(creditNote, AppLanguage.French)).Should().Be("%PDF");
    }

    [Fact]
    public void Remaining_balance_is_total_minus_paid() =>
        Sample().OutstandingAmount.Should().Be(1_020m);
}
