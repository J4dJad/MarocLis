using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Finance review 7, accounting export (A7, A10): the payer's share a credit note already took off
/// is neither refused nor left to the patient; Sage pieces stay distinct whatever the site codes.
/// A14 is the migration backfill, checked against SQL Server separately.
/// </summary>
public sealed class ReviewFixesFinanceAccounting2Tests
{
    private static readonly DateOnly Answer = new(2026, 11, 5);

    private static Invoice CoveredInvoice()
    {
        var invoice = new Invoice(string.Empty, Guid.NewGuid(), InvoiceKind.Invoice, new DateOnly(2026, 10, 2), Guid.NewGuid())
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

    private static Payment PayerSettlement(decimal amount, Invoice invoice) =>
        new(amount, PaymentMethod.ThirdPartyPayer, Answer, Guid.NewGuid(), invoice.SiteId);

    // ---- A7 --------------------------------------------------------------------------------------

    [Fact]
    public void A7_a_payer_paying_what_the_credit_note_left_it_leaves_nothing_to_the_patient()
    {
        var invoice = CoveredInvoice();
        invoice.ApplyCreditNote(500m).IsSuccess.Should().BeTrue();   // 300 off the patient, 200 off CNSS
        invoice.PayerCreditedAmount.Should().Be(200m);
        invoice.PayerDueAmount.Should().Be(500m);

        invoice.ClaimStatus = ClaimStatus.Submitted;
        invoice.RecordPayment(PayerSettlement(500m, invoice)).IsSuccess.Should().BeTrue();
        invoice.RecordPayerAnswer(500m, Answer);

        invoice.ShortfallTransfers.Should().BeEmpty();
        invoice.OutstandingAmount.Should().Be(0m);
    }

    [Fact]
    public void A7_only_what_the_payer_still_owed_and_refused_is_left_to_the_patient()
    {
        var invoice = CoveredInvoice();
        invoice.ApplyCreditNote(500m).IsSuccess.Should().BeTrue();

        invoice.ClaimStatus = ClaimStatus.Submitted;
        invoice.RecordPayment(PayerSettlement(400m, invoice)).IsSuccess.Should().BeTrue();
        invoice.RecordPayerAnswer(400m, Answer);

        // 500 owed by CNSS, 400 paid: 100 to the patient, who owes exactly that.
        invoice.ShortfallTransfers.Single().Amount.Should().Be(100m);
        invoice.PatientPayableAmount.Should().Be(100m);
    }

    [Fact]
    public void A7_a_credit_note_within_the_patient_share_leaves_the_payer_share_whole()
    {
        var invoice = CoveredInvoice();
        invoice.ApplyCreditNote(100m).IsSuccess.Should().BeTrue();
        invoice.PayerCreditedAmount.Should().Be(0m);

        invoice.ClaimStatus = ClaimStatus.Submitted;
        invoice.RecordPayment(PayerSettlement(600m, invoice)).IsSuccess.Should().BeTrue();
        invoice.RecordPayerAnswer(600m, Answer);

        invoice.ShortfallTransfers.Single().Amount.Should().Be(100m);
        invoice.PatientPayableAmount.Should().Be(300m);   // 200 of their own share + 100 refused
    }

    [Fact]
    public void A7_a_patient_who_paid_before_the_credit_note_gets_the_refused_share_set_against_it()
    {
        var invoice = CoveredInvoice();
        invoice.RecordPayment(new Payment(300m, PaymentMethod.Cash, new DateOnly(2026, 10, 2), Guid.NewGuid(), invoice.SiteId))
               .IsSuccess.Should().BeTrue();
        invoice.ApplyCreditNote(500m).IsSuccess.Should().BeTrue();   // 300 off the patient, 200 off CNSS

        invoice.ClaimStatus = ClaimStatus.Submitted;
        invoice.RecordPayment(PayerSettlement(200m, invoice)).IsSuccess.Should().BeTrue();
        invoice.RecordPayerAnswer(200m, Answer);

        // CNSS's account: 700 - 200 credited - 200 paid = 300, moved to the patient, whose account
        // stood at 300 in credit after the credit note: both end at nil, as billing has it.
        invoice.ShortfallTransfers.Single().Amount.Should().Be(300m);
        invoice.OutstandingAmount.Should().Be(0m);
    }

    [Fact]
    public async Task A7_the_settlement_of_the_reduced_share_settles_the_claim_without_a_reason()
    {
        var invoice = CoveredInvoice();
        invoice.ApplyCreditNote(500m).IsSuccess.Should().BeTrue();
        invoice.ClaimStatus = ClaimStatus.Submitted;

        var result = await Service(invoice).RecordPayerSettlementAsync(
            invoice.Id, 500m, PayerShortfallHandling.WriteOff, null, "VIR-1");

        result.IsSuccess.Should().BeTrue();
        invoice.ClaimStatus.Should().Be(ClaimStatus.Settled);
        invoice.ShortfallTransfers.Should().BeEmpty();
        invoice.WrittenOffAmount.Should().Be(0m);
    }

    [Fact]
    public async Task A7_a_refusal_of_the_reduced_share_written_off_leaves_nothing_to_the_patient()
    {
        var invoice = CoveredInvoice();
        invoice.ApplyCreditNote(500m).IsSuccess.Should().BeTrue();
        invoice.ClaimStatus = ClaimStatus.Submitted;

        var result = await Service(invoice).RecordPayerSettlementAsync(
            invoice.Id, 300m, PayerShortfallHandling.WriteOff, "Acte non remboursable", "VIR-1");

        result.IsSuccess.Should().BeTrue();
        invoice.ClaimStatus.Should().Be(ClaimStatus.PartiallyAccepted);
        invoice.WrittenOffAmount.Should().Be(200m);
        invoice.ShortfallTransfers.Should().BeEmpty();
        invoice.OutstandingAmount.Should().Be(0m);
    }

    private static BillingService Service(Invoice invoice)
    {
        var user = Guid.NewGuid();
        var today = Answer;
        Task<Invoice?> Find(object?[]? args) =>
            Task.FromResult((Guid)args![0]! == invoice.Id ? invoice : null);

        return new BillingService(
            Stub<IRepository<Invoice>>.With(("GetByIdWithDetailsAsync", Find), ("GetByIdAsync", Find)),
            Stub<IRepository<LabOrder>>.Create(),
            Stub<IRepository<Patient>>.Create(),
            Stub<IRepository<Payer>>.Create(),
            Stub<IRepository<LabSite>>.Create(),
            Stub<IRepository<AppUser>>.Create(),
            Stub<IRepository<CashSession>>.Create(),
            Stub<IRepository<Deposit>>.Create(),
            Stub<IBillingQueries>.Create(),
            Stub<ISequenceAllocator>.Create(),
            Stub<IUnitOfWork>.Create(),
            Stub<IPermissionGuard>.Create(),
            Stub<ICurrentUser>.With(("get_UserId", _ => user), ("get_SiteId", _ => invoice.SiteId)),
            Stub<IClock>.With(("get_Today", _ => today),
                              ("get_Now", _ => new DateTimeOffset(today.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(1)))));
    }

    private sealed class Stub<T> : ReviewFixesBilling2Tests.Stub<T> where T : class;

    // ---- A10 -------------------------------------------------------------------------------------

    [Fact]
    public void A10_sites_sharing_their_first_letters_get_pieces_of_their_own()
    {
        var casa1 = SupplierAccountingExport.SagePiece("FAC/CASA1/2026/000318");
        var casa2 = SupplierAccountingExport.SagePiece("FAC/CASA2/2026/000318");

        casa1.Should().NotBe(casa2);
        casa1.Should().StartWith("F").And.EndWith("26000318").And.HaveLength(13);
    }

    [Fact]
    public void A10_the_invoice_credit_note_and_receipt_of_a_site_code_with_a_separator_stay_apart()
    {
        string[] numbers =
        [
            "FAC/CASA-ANFA/2026/000318", "AV/CASA-ANFA/2026/000318", "REC/CASA-ANFA/2026/000318",
            "FAC/CASA-MAARIF/2026/000318", "AV/CASA-MAARIF/2026/000318", "REC/CASA-MAARIF/2026/000318",
            "FAC/CASA.ANFA/2026/000318", "FAC/CASA ANFA/2026/000318", "FAC/CASA_ANFA/2026/000318"
        ];

        var pieces = numbers.Select(SupplierAccountingExport.SagePiece).ToList();

        pieces.Should().OnlyHaveUniqueItems();
        pieces.Should().OnlyContain(p => p.Length <= SupplierAccountingExport.SagePieceLength);
        pieces[0].Should().StartWith("F").And.EndWith("26000318");
        pieces[1].Should().StartWith("A");
        pieces[2].Should().StartWith("R");
    }

    [Fact]
    public void A10_a_piece_is_the_same_in_every_export_and_a_short_piece_is_kept()
    {
        SupplierAccountingExport.SagePiece("FAC/CASA-ANFA/2026/000318")
            .Should().Be(SupplierAccountingExport.SagePiece(" FAC/CASA-ANFA/2026/000318 "));
        SupplierAccountingExport.SagePiece("PAIE2610-CASA").Should().Be("PAIE2610-CASA");
    }

    [Fact]
    public void A10_a_piece_outside_the_billing_series_keeps_its_initial_and_stays_distinct()
    {
        var a = SupplierAccountingExport.SagePiece("VIR-CNSS-2026-10-REF12345");
        var b = SupplierAccountingExport.SagePiece("VIR-CNOPS-2026-10-REF12345");

        a.Should().StartWith("V").And.EndWith("REF12345").And.HaveLength(13);
        a.Should().NotBe(b);
    }

    [Fact]
    public void A10_every_sequence_of_a_year_keeps_a_piece_of_its_own_across_two_sites()
    {
        var pieces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefix in new[] { "FAC", "AV", "REC" })
            foreach (var site in new[] { "CASA-ANFA", "CASA-MAARIF", "CASA1", "CASA2", "RABAT" })
                for (var n = 1; n <= 300; n++)
                    pieces.Add(SupplierAccountingExport.SagePiece($"{prefix}/{site}/2026/{n:000000}")).Should().BeTrue();
    }
}
