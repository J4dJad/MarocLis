using System.Text;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;
using MarocLIS.Reporting;
using Xunit;

namespace MarocLIS.Tests.Billing;

/// <summary>
/// The second print format of a patient invoice (« cotation B »): the acts with their national code
/// and coefficient, the total in B, the collection fee and the amount in dirhams, from the codes
/// frozen on the invoice lines. And the settings that choose the format and its display options.
/// </summary>
public sealed class InvoiceCotationFormatTests
{
    private static InvoiceDocumentLine Act(int number, string code, string label, string? letter,
                                           decimal? coefficient, decimal amount, int quantity = 1) =>
        new(number, code, label, quantity, amount / quantity, amount, 0m, amount, 0m, amount,
            IsVatExempt: true, VatRate: 0m)
        {
            NationalActCode = code,
            LetterKey = letter,
            Coefficient = coefficient,
            LetterKeyValue = coefficient is > 0m ? amount / (coefficient.Value * quantity) : null
        };

    /// <summary>The laboratory's model: B241 ECBU (90) and B242 antibiogram (60), 120,00 DH.</summary>
    private static InvoiceDocument Model(params InvoiceDocumentLine[] extra)
    {
        InvoiceDocumentLine[] lines =
        [
            Act(1, "B241", "ECBU EXAMEN CYTOBACTERIOLOGIQUE DES URINES", "B", 90m, 72m),
            Act(2, "B242", "ANTIBIOGRAMME (CLASSEMENT PAR FAMILLE)", "B", 60m, 48m),
            .. extra
        ];

        var total = lines.Sum(l => l.TotalAmount);

        return new InvoiceDocument(
            Guid.NewGuid(), "FAC/CASA/2026/000412", InvoiceKind.Invoice, InvoiceStatus.Paid,
            new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 16),
            new InvoiceIssuer("Laboratoire Al Amal", "LABO AL AMAL SARL", "12, bd Zerktouni", "Casablanca",
                              "20000", "05 22 00 00 00", null, "001234567000089", "12345678", "123456",
                              "Casablanca", "34567890", "1234567", "LAB0001", null),
            "EL ALAOUI Fatima", null, null, null, "Casablanca", "EL ALAOUI Fatima", "P-2026-004512", null,
            "CASA-261016-0007", "Dr Bennani", null, null,
            GrossAmount: total, DiscountAmount: 0m, DiscountReason: null, NetAmount: total, VatAmount: 0m,
            TotalAmount: total, PayerShareAmount: 0m, PatientShareAmount: total, PaidAmount: total,
            AmountInWords: AmountInWords.Render(total, AppLanguage.French),
            Lines: lines, Payments: []);
    }

    private static (QuestPdfInvoiceRenderer Renderer, JsonLocalizer Localizer) Create()
    {
        var localizer = new JsonLocalizer(new LocalizationOptions { MarkMissingKeys = true });
        var renderer = new QuestPdfInvoiceRenderer(new ReportingOptions
        {
            FontDirectory = Path.Combine(AppContext.BaseDirectory, "no-fonts-here")
        }, localizer);
        return (renderer, localizer);
    }

    private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 4);

    [Fact]
    public void The_model_invoice_lists_each_act_with_its_code_and_coefficient()
    {
        var layout = CotationInvoiceLayout.Build(Model(), InvoicePrintSettings.Default, AppLanguage.French);

        layout.Rows.Select(r => (r.Code, r.Description, r.Coefficient)).Should().Equal(
            ("B241", "ECBU EXAMEN CYTOBACTERIOLOGIQUE DES URINES", 90m),
            ("B242", "ANTIBIOGRAMME (CLASSEMENT PAR FAMILLE)", 60m));
        layout.Rows.Should().OnlyContain(r => r.IsBiologyAct);
        layout.ShowLinePrice.Should().BeFalse("the model shows no price per act");
    }

    [Fact]
    public void The_total_in_B_is_the_sum_of_the_coefficients()
    {
        var layout = CotationInvoiceLayout.Build(Model(), InvoicePrintSettings.Default, AppLanguage.French);

        layout.LetterTotals.Should().Equal(new CotationLetterTotal("B", 150m));
    }

    [Fact]
    public void The_collection_fee_line_shows_zero_when_none_was_billed()
    {
        var layout = CotationInvoiceLayout.Build(Model(), InvoicePrintSettings.Default, AppLanguage.French);

        layout.CollectionFee.Should().Be(0m);

        var hidden = CotationInvoiceLayout.Build(
            Model(), InvoicePrintSettings.Default with { CotationShowCollectionFeeWhenZero = false },
            AppLanguage.French);
        hidden.CollectionFee.Should().BeNull();
    }

    [Fact]
    public void A_collection_fee_billed_under_PC_goes_on_its_own_line_in_dirhams()
    {
        var layout = CotationInvoiceLayout.Build(
            Model(Act(3, "PC", "Prise de sang", "PC", null, 20m)),
            InvoicePrintSettings.Default with { CotationShowCollectionFeeWhenZero = false },
            AppLanguage.French);

        layout.Rows.Should().HaveCount(2, "the fee is not an act of the table");
        layout.CollectionFee.Should().Be(20m);
        layout.LetterTotals.Should().Equal(new CotationLetterTotal("B", 150m));
    }

    [Fact]
    public void The_amount_in_words_is_that_of_the_invoice_total()
    {
        var layout = CotationInvoiceLayout.Build(Model(), InvoicePrintSettings.Default, AppLanguage.French);

        layout.Words.Should().Be("CENT VINGT DIRHAMS");

        CotationInvoiceLayout.Build(Model(), InvoicePrintSettings.Default with { CotationShowAmountInWords = false },
                                    AppLanguage.French).Words.Should().BeNull();
    }

    [Fact]
    public void Mixed_letter_keys_are_totalled_apart_and_an_uncoded_line_shows_its_price()
    {
        var layout = CotationInvoiceLayout.Build(
            Model(Act(3, "K12", "Acte technique", "KB", 20m, 30m),
                  Act(4, "B241", "ECBU (contrôle)", "B", 90m, 144m, quantity: 2),
                  Act(5, "CERT", "Certificat", null, null, 50m)),
            InvoicePrintSettings.Default, AppLanguage.French);

        layout.LetterTotals.Should().Equal(new CotationLetterTotal("B", 330m), new CotationLetterTotal("KB", 20m));
        layout.Rows.Single(r => r.LineNumber == 4).Coefficient.Should().Be(180m, "90 × 2");

        var uncoded = layout.Rows.Single(r => r.LineNumber == 5);
        uncoded.Coefficient.Should().BeNull("no coefficient is invented");
        uncoded.Code.Should().Be("CERT");
        layout.ShowLinePrice.Should().BeTrue("the uncoded line has only its price to show");
    }

    [Fact]
    public void The_letter_value_is_shown_only_when_asked()
    {
        CotationInvoiceLayout.Build(Model(), InvoicePrintSettings.Default, AppLanguage.French)
            .LetterValues.Should().BeEmpty();

        CotationInvoiceLayout.Build(Model(), InvoicePrintSettings.Default with { CotationShowLetterKeyValue = true },
                                    AppLanguage.French)
            .LetterValues.Should().Equal(new CotationLetterTotal("B", 0.8m));
    }

    [Theory]
    [InlineData(AppLanguage.French)]
    [InlineData(AppLanguage.Arabic)]
    [InlineData(AppLanguage.English)]
    public void The_coded_invoice_renders_in_every_language_with_no_missing_text(AppLanguage language)
    {
        var (renderer, localizer) = Create();
        var document = Model(Act(3, "PC", "Prise de sang", "PC", null, 20m),
                             Act(4, "CERT", "Certificat", null, null, 50m)) with
        {
            DiscountAmount = 10m, DiscountReason = "Geste", PayerShareAmount = 40m, PaidAmount = 0m
        };

        var pdf = renderer.RenderInvoice(document, language, InvoicePrintFormat.CotationB,
                                         InvoicePrintSettings.Default with { CotationShowLetterKeyValue = true });

        Header(pdf).Should().Be("%PDF");
        localizer.UnresolvedKeysSeen.Should().BeEmpty();
    }

    [Fact]
    public void The_standard_invoice_renders_with_its_optional_columns()
    {
        var (renderer, _) = Create();

        var pdf = renderer.RenderInvoice(Model(), AppLanguage.French, InvoicePrintFormat.Standard,
                                         InvoicePrintSettings.Default with
                                         {
                                             StandardShowNationalActCode = true,
                                             StandardShowCoefficient = true,
                                             StandardShowAmountInWords = false
                                         });

        Header(pdf).Should().Be("%PDF");
    }

    [Fact]
    public void A_credit_note_keeps_the_standard_format()
    {
        InvoicePrintSettings.EffectiveFormat(InvoiceKind.CreditNote, InvoicePrintFormat.CotationB)
            .Should().Be(InvoicePrintFormat.Standard);
        InvoicePrintSettings.EffectiveFormat(InvoiceKind.Invoice, InvoicePrintFormat.CotationB)
            .Should().Be(InvoicePrintFormat.CotationB);

        var (renderer, _) = Create();
        var creditNote = Model() with { Kind = InvoiceKind.CreditNote, CorrectsInvoiceNumber = "FAC/CASA/2026/000411" };
        Header(renderer.RenderInvoice(creditNote, AppLanguage.Arabic, InvoicePrintFormat.CotationB,
                                      InvoicePrintSettings.Default)).Should().Be("%PDF");
    }

    [Fact]
    public void The_settings_default_to_the_existing_invoice_and_the_owner_model()
    {
        var settings = InvoicePrintSettings.FromValues(new Dictionary<string, string?>());

        settings.Should().Be(InvoicePrintSettings.Default);
        settings.DefaultFormat.Should().Be(InvoicePrintFormat.Standard);
        settings.StandardShowAmountInWords.Should().BeTrue();
        settings.StandardShowNationalActCode.Should().BeFalse();
        settings.StandardShowCoefficient.Should().BeFalse();
        settings.CotationShowLinePrice.Should().BeFalse();
        settings.CotationShowCollectionFeeWhenZero.Should().BeTrue();
        settings.CotationShowAmountInWords.Should().BeTrue();
        settings.CotationShowLetterKeyValue.Should().BeFalse();
    }

    [Fact]
    public void The_settings_round_trip_and_ignore_values_typed_wrong()
    {
        var chosen = new InvoicePrintSettings
        {
            DefaultFormat = InvoicePrintFormat.CotationB,
            StandardShowCoefficient = true,
            CotationShowLinePrice = true,
            CotationShowCollectionFeeWhenZero = false
        };

        InvoicePrintSettings.FromValues(chosen.ToValues().ToDictionary(v => v.Key, v => (string?)v.Value))
            .Should().Be(chosen);

        InvoicePrintSettings.FromValues(new Dictionary<string, string?>
        {
            [InvoicePrintSettings.DefaultFormatKey] = "7",
            [InvoicePrintSettings.CotationWordsKey] = "oui"
        }).Should().Be(InvoicePrintSettings.Default);
    }
}
