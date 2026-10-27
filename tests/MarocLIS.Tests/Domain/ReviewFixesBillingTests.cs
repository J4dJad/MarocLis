using FluentAssertions;
using MarocLIS.Domain.Billing;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Credit notes, settlement status and the payer split, as corrected after the October 2026 review.
/// <para>
/// An issued invoice is never edited: a correction is a credit note in its own series. The credit
/// note has to reduce what is owed on the original, and never reverse more than it charged.
/// </para>
/// </summary>
public sealed class ReviewFixesBillingTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static Invoice Issued(params decimal[] prices) => IssuedWithDiscount(0m, prices);

    private static Invoice IssuedWithDiscount(decimal discount, params decimal[] prices)
    {
        var invoice = Draft(InvoiceKind.Invoice, prices);
        if (discount > 0m) invoice.ApplyDiscount(discount, "Geste commercial").IsSuccess.Should().BeTrue();
        invoice.Issue("FAC/CASA/2026/000100", Now, null).IsSuccess.Should().BeTrue();
        return invoice;
    }

    private static Invoice Draft(InvoiceKind kind, params decimal[] prices)
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), kind, Today, Guid.NewGuid())
        {
            BilledToName = "ALAOUI Fatima"
        };

        var i = 0;
        foreach (var price in prices)
            invoice.AddLine(new InvoiceLine($"Analyse {++i}", price)).IsSuccess.Should().BeTrue();

        return invoice;
    }

    private static Invoice CreditNoteFor(Invoice original)
    {
        var note = Draft(InvoiceKind.CreditNote);
        note.MirrorLinesOf(original).IsSuccess.Should().BeTrue();
        note.Issue("AV/CASA/2026/000001", Now, null).IsSuccess.Should().BeTrue();
        return note;
    }

    private static Payment Cash(decimal amount) =>
        new(amount, PaymentMethod.Cash, Today, Guid.NewGuid(), Guid.NewGuid());

    private static Payment Cheque(decimal amount) =>
        new(amount, PaymentMethod.Cheque, Today, Guid.NewGuid(), Guid.NewGuid())
        {
            Reference = "CHQ-0042",
            BankName = "Attijariwafa bank"
        };

    // ---- F1: credit note netting ------------------------------------------------------------------

    [Fact]
    public void AFullCreditNote_SettlesTheOriginal()
    {
        var invoice = Issued(100m, 50m);
        var note = CreditNoteFor(invoice);

        invoice.ApplyCreditNote(note.TotalAmount).IsSuccess.Should().BeTrue();

        invoice.CreditedAmount.Should().Be(150m);
        invoice.OutstandingAmount.Should().Be(0m);
        invoice.IsFullyCredited.Should().BeTrue();
        invoice.HasCreditNote.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceStatus.Credited);
    }

    [Fact]
    public void APartialCreditNote_ReducesWhatIsOwed()
    {
        var invoice = Issued(100m);

        invoice.ApplyCreditNote(40m).IsSuccess.Should().BeTrue();

        invoice.OutstandingAmount.Should().Be(60m);
        invoice.Status.Should().Be(InvoiceStatus.Issued);

        // The patient cannot be charged the credited part.
        invoice.RecordPayment(Cash(61m)).FirstError.ResourceKey.Should().Be("Payment_ExceedsOutstanding");
        invoice.RecordPayment(Cash(60m)).IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Theory]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.French, "60,00 DH")]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.English, "MAD")]
    [InlineData(MarocLIS.Domain.Common.AppLanguage.Arabic, "د.م.")]
    public void AnOverpayment_NamesWhatIsOwed_InTheScreensCurrencyNotation(MarocLIS.Domain.Common.AppLanguage language, string expected)
    {
        var invoice = Issued(100m);
        invoice.ApplyCreditNote(40m).IsSuccess.Should().BeTrue();

        var error = invoice.RecordPayment(Cash(61m)).FirstError;
        error.Args.Single().Should().Be(MarocLIS.Domain.Common.Money.Of(60m));

        var localizer = new MarocLIS.Localization.JsonLocalizer(new MarocLIS.Localization.LocalizationOptions { MarkMissingKeys = true });
        var text = localizer.Format(language, error.ResourceKey, error.Args);
        text.Should().Contain(expected).And.NotContain("[");
        if (language != MarocLIS.Domain.Common.AppLanguage.English) text.Should().NotContain("MAD");
    }

    [Fact]
    public void ACreditNote_OwesNothingAndCannotBePaid()
    {
        var note = CreditNoteFor(Issued(100m));

        note.OutstandingAmount.Should().Be(0m);
        note.Status.Should().Be(InvoiceStatus.Paid);
        note.RecordPayment(Cash(10m)).FirstError.ResourceKey.Should().Be("Payment_KindNotPayable");
    }

    [Fact]
    public void ACreditNote_CannotItselfBeCredited()
    {
        var note = CreditNoteFor(Issued(100m));

        note.CanApplyCreditNote(10m).FirstError.ResourceKey.Should().Be("Invoice_CannotCreditACreditNote");
    }

    [Fact]
    public void TheSharedOutstandingRule_MatchesTheEntity()
    {
        // The ledger computes it from the stored columns.
        Invoice.ComputeOutstanding(InvoiceKind.Invoice, 100m, 30m, 50m).Should().Be(20m);
        Invoice.ComputeOutstanding(InvoiceKind.Invoice, 100m, 80m, 50m).Should().Be(0m);
        Invoice.ComputeOutstanding(InvoiceKind.CreditNote, 100m, 0m, 0m).Should().Be(0m);
    }

    // ---- F2: double credit and discount ---------------------------------------------------------

    [Fact]
    public void ASecondFullCredit_IsRefused()
    {
        var invoice = Issued(100m);
        invoice.ApplyCreditNote(100m).IsSuccess.Should().BeTrue();

        var again = invoice.CanApplyCreditNote(100m);

        again.IsFailure.Should().BeTrue();
        again.FirstError.ResourceKey.Should().Be("Invoice_AlreadyFullyCredited");
        invoice.CreditableAmount.Should().Be(0m);
    }

    [Fact]
    public void CreditNotes_TogetherNeverExceedTheTotal()
    {
        var invoice = Issued(100m);
        invoice.ApplyCreditNote(70m).IsSuccess.Should().BeTrue();

        var refused = invoice.ApplyCreditNote(40m);

        refused.FirstError.ResourceKey.Should().Be("Invoice_CreditNoteExceedsBalance");
        refused.FirstError.Args.Should().Equal(30m);
        invoice.CreditedAmount.Should().Be(70m);

        invoice.ApplyCreditNote(30m).IsSuccess.Should().BeTrue();
        invoice.IsFullyCredited.Should().BeTrue();
    }

    [Fact]
    public void AFullReversal_CarriesTheOriginalDiscount()
    {
        var invoice = IssuedWithDiscount(15m, 100m, 50m);
        invoice.TotalAmount.Should().Be(135m);

        var note = CreditNoteFor(invoice);

        // Mirrored at gross price only, the note came to 150 and credited 15 that were never charged.
        note.DiscountAmount.Should().Be(15m);
        note.TotalAmount.Should().Be(invoice.TotalAmount);
        invoice.ApplyCreditNote(note.TotalAmount).IsSuccess.Should().BeTrue();
        invoice.OutstandingAmount.Should().Be(0m);
    }

    // ---- F3: status follows the money ------------------------------------------------------------

    [Fact]
    public void ABouncedCheque_ReopensAPaidInvoice()
    {
        var invoice = Issued(100m);
        var cheque = Cheque(100m);
        invoice.RecordPayment(cheque).IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceStatus.Paid);

        cheque.MarkBounced(Now, "Provision insuffisante").IsSuccess.Should().BeTrue();
        invoice.Recalculate();

        invoice.OutstandingAmount.Should().Be(100m);
        invoice.Status.Should().Be(InvoiceStatus.Issued);
        invoice.IsOverdue(Today.AddDays(1)).Should().BeFalse(); // no due date set in this test
    }

    [Fact]
    public void ARefund_LeavesAPartlyPaidInvoice()
    {
        var invoice = Issued(100m);
        invoice.RecordPayment(Cash(40m)).IsSuccess.Should().BeTrue();
        var second = Cash(60m);
        invoice.RecordPayment(second).IsSuccess.Should().BeTrue();
        invoice.Status.Should().Be(InvoiceStatus.Paid);

        second.Refund(Now, "Erreur de caisse").IsSuccess.Should().BeTrue();
        invoice.Recalculate();

        invoice.Status.Should().Be(InvoiceStatus.PartiallyPaid);
        invoice.OutstandingAmount.Should().Be(60m);
    }

    // ---- F4: payer share after discount ----------------------------------------------------------

    [Fact]
    public void ADiscount_NeverLeavesThePayerShareAboveTheLineTotal()
    {
        var invoice = Draft(InvoiceKind.Invoice);
        var line = new InvoiceLine("NFS", 100m) { ReferenceTariffBase = 100m };
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        line.SetCoverageSplit(0.8m, payerUsesReferenceTariff: true);
        invoice.Recalculate();
        invoice.PayerShareAmount.Should().Be(80m);

        invoice.ApplyDiscount(50m, "Geste commercial").IsSuccess.Should().BeTrue();

        line.PayerShare.Should().Be(50m);
        line.PatientShare.Should().Be(0m);
        invoice.PayerShareAmount.Should().BeLessThanOrEqualTo(invoice.TotalAmount);
        invoice.PatientShareAmount.Should().Be(0m);

        // Removing the discount gives the payer its full share back.
        invoice.ApplyDiscount(0m, string.Empty).IsSuccess.Should().BeTrue();
        line.PayerShare.Should().Be(80m);
        invoice.PatientShareAmount.Should().Be(20m);
    }

    [Fact]
    public void ASmallDiscount_LeavesTheCoverageShareUntouched()
    {
        var invoice = Draft(InvoiceKind.Invoice);
        var line = new InvoiceLine("Glycémie", 100m) { ReferenceTariffBase = 100m };
        invoice.AddLine(line).IsSuccess.Should().BeTrue();
        line.SetCoverageSplit(0.8m, payerUsesReferenceTariff: true);

        invoice.ApplyDiscount(10m, "Geste commercial").IsSuccess.Should().BeTrue();

        line.PayerShare.Should().Be(80m);
        invoice.PatientShareAmount.Should().Be(10m);
    }
}
