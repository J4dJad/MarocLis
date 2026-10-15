using System.Text;
using FluentAssertions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Finance review 6, accounting export (A1 to A6): the payer's refused share left to the patient
/// dated on the answer and reversed on reopening, credit notes taken off the receivables as billing
/// takes them, Sage pieces within 13 characters, cash refunds from another site's till.
/// </summary>
public sealed class ReviewFixesFinanceAccountingTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly AccountingSettings Defaults = AccountingSettings.Default;

    private static readonly AccountingCustomer Covered = new("MR-000500", "ALAMI Sara", "CNSS", "34211");
    private static readonly AccountingCustomer Patient = new("MR-000500", "ALAMI Sara", null, null);

    private static Invoice CoveredInvoice()
    {
        var invoice = new Invoice(string.Empty, Site, InvoiceKind.Invoice, new DateOnly(2026, 10, 2), Guid.NewGuid())
        {
            PatientId = Guid.NewGuid(), PayerId = Guid.NewGuid(), BilledToName = "ALAMI Sara",
            ClaimStatus = ClaimStatus.Prepared
        };
        var line = new InvoiceLine("NFS", 1000m);
        invoice.AddLine(line);
        line.SetCoverageSplit(0.70m, payerUsesReferenceTariff: false);
        invoice.Issue("FAC/CASA/2026/000500", DateTimeOffset.Now, null).IsSuccess.Should().BeTrue();
        invoice.PayerShareAmount.Should().Be(700m);
        return invoice;
    }

    private static SalesAccountingSources Sources(IEnumerable<SalesDocumentSource>? documents = null,
                                                  IEnumerable<SalesPaymentSource>? payments = null,
                                                  IEnumerable<PayerShortfallSource>? shortfalls = null,
                                                  IEnumerable<DepositSource>? deposits = null) =>
        new([.. documents ?? []], [.. payments ?? []], [.. shortfalls ?? []], [.. deposits ?? []], []);

    private static IReadOnlyList<JournalEntry> Build(SalesAccountingSources sources, AccountingSettings? settings = null) =>
        SalesAccountingJournal.Build(sources, settings ?? Defaults, From, To, AccountingJournals.All);

    // ---- A1 / A2 ---------------------------------------------------------------------------------

    [Fact]
    public void A1_the_share_left_to_the_patient_is_recorded_once_on_the_day_of_the_answer()
    {
        var invoice = CoveredInvoice();
        invoice.ClaimStatus = ClaimStatus.Rejected;
        invoice.RecordPayerAnswer(0m, new DateOnly(2026, 10, 20));

        var transfer = invoice.ShortfallTransfers.Single();
        transfer.Amount.Should().Be(700m);
        transfer.TransferredOn.Should().Be(new DateOnly(2026, 10, 20));

        // The patient pays later: nothing new is transferred.
        invoice.RecordPayment(new Payment(700m, PaymentMethod.Cash, new DateOnly(2026, 11, 12), Guid.NewGuid(), Site))
               .IsSuccess.Should().BeTrue();
        invoice.ShortfallTransfers.Should().ContainSingle();
    }

    [Fact]
    public void A1_a_refusal_written_off_leaves_nothing_to_transfer()
    {
        var invoice = CoveredInvoice();
        invoice.ClaimStatus = ClaimStatus.Submitted;
        invoice.RecordPayment(new Payment(700m, PaymentMethod.WriteOff, new DateOnly(2026, 10, 20), Guid.NewGuid(), Site))
               .IsSuccess.Should().BeTrue();
        invoice.RecordPayerAnswer(0m, new DateOnly(2026, 10, 20));

        invoice.ShortfallTransfers.Should().BeEmpty();
    }

    [Fact]
    public void A2_reopening_the_claim_reverses_the_transfer_on_its_day_and_the_export_moves_it_back()
    {
        var invoice = CoveredInvoice();
        invoice.ClaimStatus = ClaimStatus.Rejected;
        invoice.RecordPayerAnswer(0m, new DateOnly(2026, 10, 20));

        invoice.ReopenRejectedClaim("renvoi", new DateOnly(2026, 10, 28)).IsSuccess.Should().BeTrue();
        invoice.ShortfallTransfers.Single().ReversedOn.Should().Be(new DateOnly(2026, 10, 28));
        invoice.PayerAcceptedAmount.Should().BeNull();

        var entries = Build(Sources(shortfalls:
        [
            new PayerShortfallSource(Site, "CASA", invoice.Number, new DateOnly(2026, 10, 20), Covered, 700m),
            new PayerShortfallSource(Site, "CASA", invoice.Number, new DateOnly(2026, 10, 28), Covered, 700m,
                                     Reversal: true)
        ]));

        entries.Should().HaveCount(2).And.OnlyContain(e => e.IsBalanced && e.Journal == "OD");
        var back = entries.Single(e => e.Label.StartsWith("Annul. refus"));
        back.Date.Should().Be(new DateOnly(2026, 10, 28));
        back.Lines.Single(l => l.Account == "34211").Debit.Should().Be(700m);
        back.Lines.Single(l => l.Account == "3421").Credit.Should().Be(700m);

        // Over the two entries, both receivables are back where they were.
        var lines = entries.SelectMany(e => e.Lines).ToList();
        lines.Where(l => l.Account == "34211").Sum(l => l.Debit - l.Credit).Should().Be(0m);
        lines.Where(l => l.Account == "3421").Sum(l => l.Debit - l.Credit).Should().Be(0m);
    }

    // ---- A3 / A4 ---------------------------------------------------------------------------------

    private static SalesDocumentSource Original() =>
        new(Site, "CASA", InvoiceKind.Invoice, "FAC/CASA/2026/000500", new DateOnly(2026, 10, 2), Covered, true,
            1000m, 0m, 700m, [new SalesLineSource("HEMA", 1000m, 0m, true, 0m)]);

    private static SalesDocumentSource Credit(decimal amount, CreditNoteState state, string number = "AV/CASA/2026/000010") =>
        new(Site, "CASA", InvoiceKind.CreditNote, number, new DateOnly(2026, 10, 25), Covered, true,
            amount, 0m, 0m, [new SalesLineSource(null, amount, 0m, true, 0m)], Original(), state);

    private static decimal Balance(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.Account == account).Sum(l => l.Debit - l.Credit);

    [Fact]
    public void A3_a_partial_credit_note_on_a_prepared_claim_comes_off_the_patient_share()
    {
        var entry = Build(Sources(documents: [Credit(200m, CreditNoteState.None)])).Single();

        entry.IsBalanced.Should().BeTrue();
        Balance(entry, "3421").Should().Be(-200m);
        entry.Lines.Should().NotContain(l => l.Account == "34211");
    }

    [Fact]
    public void A3_beyond_the_patient_share_and_after_earlier_credit_notes_the_payer_is_credited()
    {
        // 250 already credited off the patient's 300: this 200 takes the last 50 and 150 off the payer.
        var entry = Build(Sources(documents: [Credit(200m, new CreditNoteState(250m, 0m, 0m, 0m))])).Single();

        entry.IsBalanced.Should().BeTrue();
        Balance(entry, "3421").Should().Be(-50m);
        Balance(entry, "34211").Should().Be(-150m);
    }

    [Fact]
    public void A4_after_a_refusal_written_off_the_credit_note_reverses_the_loss_not_the_payer()
    {
        var entry = Build(Sources(documents: [Credit(1000m, new CreditNoteState(0m, 0m, 700m, 0m))])).Single();

        entry.IsBalanced.Should().BeTrue();
        Balance(entry, "3421").Should().Be(-300m);
        Balance(entry, "6585").Should().Be(-700m);
        entry.Lines.Should().NotContain(l => l.Account == "34211");
    }

    [Fact]
    public void A4_after_a_refusal_left_to_the_patient_the_credit_note_comes_off_the_patient_alone()
    {
        var entry = Build(Sources(documents: [Credit(1000m, new CreditNoteState(0m, 0m, 0m, 700m))])).Single();

        entry.IsBalanced.Should().BeTrue();
        Balance(entry, "3421").Should().Be(-1000m);
        entry.Lines.Should().NotContain(l => l.Account == "34211");
    }

    [Fact]
    public void A4_a_full_credit_note_before_any_answer_still_reverses_both_shares()
    {
        var entry = Build(Sources(documents: [Credit(1000m, CreditNoteState.None)])).Single();

        Balance(entry, "3421").Should().Be(-300m);
        Balance(entry, "34211").Should().Be(-700m);
    }

    // ---- A5 --------------------------------------------------------------------------------------

    // Since review 7 (A10) the site code is replaced by a short code of the whole "PREFIX/SITE".
    [Theory]
    [InlineData("FAC/CASA/2026/000318", "F", "26000318")]
    [InlineData("AV/CASA/2026/000318", "A", "26000318")]
    [InlineData("REC/RABAT/2026/000007", "R", "26000007")]
    [InlineData("FAC/FE/2026/000001", "F", "26000001")]
    [InlineData("PAIE2610-CASA", "PAIE2610-CASA", "PAIE2610-CASA")]
    [InlineData("VIR-CNSS-2026-10-REF12345", "V", "REF12345")]
    public void A5_sage_pieces_hold_in_13_characters(string piece, string head, string tail)
    {
        var compact = SupplierAccountingExport.SagePiece(piece);

        compact.Should().StartWith(head).And.EndWith(tail);
        compact.Length.Should().BeLessThanOrEqualTo(13);
    }

    [Fact]
    public void A5_the_sage_file_writes_the_compact_piece_and_keeps_the_full_number_in_the_label()
    {
        var lines = new[]
        {
            new AccountingEntry(new DateOnly(2026, 10, 2), "VT", "3421", null,
                                "Règl. tiers payant fact. FAC/CASA/2026/000318 ALAMI Sara", "FAC/CASA/2026/000318", 100m, 0m)
        };

        var sage = Encoding.UTF8.GetString(SupplierAccountingExport.ToCsv(lines, AccountingExportLayout.Sage)).Split('\n')[1];
        var cells = sage.Split(';');
        cells[5].Should().Be(SupplierAccountingExport.SagePiece("FAC/CASA/2026/000318")).And.HaveLength(13);
        cells[4].Should().StartWith("FAC/CASA/2026/000318").And.HaveLength(35);

        var generic = Encoding.UTF8.GetString(SupplierAccountingExport.ToCsv(lines, AccountingExportLayout.Generic));
        generic.Should().Contain(";FAC/CASA/2026/000318;");
    }

    // ---- A6 --------------------------------------------------------------------------------------

    private static readonly AccountingSettings PerSiteCash = Defaults with
    {
        CashBySite = new Dictionary<string, string> { ["CASA"] = "51611", ["RABAT"] = "51612" }
    };

    [Fact]
    public void A6_a_cash_refund_from_another_sites_till_comes_out_of_that_sites_cash_account()
    {
        var payment = new SalesPaymentSource(Site, "CASA", "FAC/CASA/2026/000500", new DateOnly(2026, 9, 2), Patient, false,
                                             PaymentMethod.Cash, PaymentStatus.Refunded, 400m, new DateOnly(2026, 9, 2),
                                             null, new DateOnly(2026, 10, 12), null, null, null, null, null, "RABAT");

        var refund = Build(Sources(payments: [payment]), PerSiteCash).Single();

        refund.Lines.Single(l => l.Credit > 0m).Account.Should().Be("51612");
        refund.SiteCode.Should().Be("CASA");
    }

    [Fact]
    public void A6_a_deposit_refunded_from_another_sites_till_comes_out_of_that_sites_cash_account()
    {
        var deposit = new DepositSource(Site, "CASA", "REC/CASA/2026/000007", Patient, PaymentMethod.Cash, 200m,
                                        new DateOnly(2026, 10, 3), 200m, new DateOnly(2026, 10, 5), null, null, "RABAT");

        var entries = Build(Sources(deposits: [deposit]), PerSiteCash);

        entries.Single(e => e.Label.StartsWith("Acompte")).Lines.Single(l => l.Debit > 0m).Account.Should().Be("51611");
        entries.Single(e => e.Label.StartsWith("Rembt")).Lines.Single(l => l.Credit > 0m).Account.Should().Be("51612");
    }
}
