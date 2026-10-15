using FluentAssertions;
using MarocLIS.Application.Accounting;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Queries;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Finance review 6, accounting export against SQL Server: the payer's refused share left to the
/// patient comes back once, on the day of the answer, and its reversal on the day of the reopening;
/// a credit note carries where its invoice stood; a cash refund carries the site of the till that paid it.
/// </summary>
public sealed class ReviewFixesFinanceAccountingDatabaseTests(FinanceEScratchDatabase database)
    : IClassFixture<FinanceEScratchDatabase>
{
    private static readonly TimeSpan Morocco = TimeSpan.FromHours(1);

    [SqlServerFact]
    public async Task The_refusal_left_to_the_patient_is_exported_on_its_day_once_and_reversed_on_reopening()
    {
        var user = Guid.NewGuid();
        var casa = new LabSite("A1C", new MultilingualText("Casa"), LabSiteKind.MainLaboratory);
        var rabat = new LabSite("A1R", new MultilingualText("Rabat"), LabSiteKind.MainLaboratory);
        var patient = new Patient("MR-A1", "ALAMI", "Sara", null, Gender.Unknown, casa.Id);
        var payer = new Payer("CNSS-A1", new MultilingualText("AMO CNSS"), PayerKind.AmoCnss) { AccountingAccount = "34211" };

        var invoice = new Invoice(string.Empty, casa.Id, InvoiceKind.Invoice, new DateOnly(2026, 10, 2), user)
        {
            PatientId = patient.Id, PayerId = payer.Id, BilledToName = "ALAMI Sara", ClaimStatus = ClaimStatus.Prepared
        };
        var line = new InvoiceLine("NFS", 1000m);
        invoice.AddLine(line);
        line.SetCoverageSplit(0.70m, payerUsesReferenceTariff: false);
        invoice.Issue("FAC/A1C/2026/000500", new DateTimeOffset(2026, 10, 2, 9, 0, 0, Morocco), null).IsSuccess.Should().BeTrue();
        invoice.ClaimStatus = ClaimStatus.Rejected;
        invoice.RecordPayerAnswer(0m, new DateOnly(2026, 10, 20));
        invoice.ReopenRejectedClaim("renvoi", new DateOnly(2026, 11, 8)).IsSuccess.Should().BeTrue();

        // A cash payment refunded from the Rabat till.
        var cash = new Payment(300m, PaymentMethod.Cash, new DateOnly(2026, 10, 3), user, casa.Id);
        invoice.RecordPayment(cash).IsSuccess.Should().BeTrue();
        cash.Refund(new DateTimeOffset(2026, 10, 15, 10, 0, 0, Morocco), "Erreur").IsSuccess.Should().BeTrue();
        cash.RefundSiteId = rabat.Id;
        invoice.Recalculate();

        // A partial credit note while the claim is answered: it carries the share left to the patient.
        var credit = new Invoice(string.Empty, casa.Id, InvoiceKind.CreditNote, new DateOnly(2026, 10, 25), user)
        {
            PatientId = patient.Id, BilledToName = "ALAMI Sara", CorrectsInvoiceId = invoice.Id
        };
        credit.AddLine(new InvoiceLine("Avoir partiel", 200m));
        credit.Issue("AV/A1C/2026/000001", new DateTimeOffset(2026, 10, 25, 9, 0, 0, Morocco), null).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.AddRange(casa, rabat, patient, payer, invoice, credit);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new AccountingQueries(read);

        var october = await queries.GetSalesSourcesAsync([casa.Id], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var shortfall = october.Shortfalls.Single();
        shortfall.Date.Should().Be(new DateOnly(2026, 10, 20));
        shortfall.Amount.Should().Be(700m);
        shortfall.Reversal.Should().BeFalse();

        october.Payments.Single(p => p.Method == PaymentMethod.Cash).RefundSiteCode.Should().Be("A1R");

        var state = october.Documents.Single(d => d.Kind == InvoiceKind.CreditNote).CreditState!;
        state.LeftToPatient.Should().Be(700m);
        state.PriorCredited.Should().Be(0m);

        var november = await queries.GetSalesSourcesAsync([casa.Id], new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        var reversal = november.Shortfalls.Single();
        reversal.Reversal.Should().BeTrue();
        reversal.Date.Should().Be(new DateOnly(2026, 11, 8));

        var entries = SalesAccountingJournal.Build(october, AccountingSettings.Default, new DateOnly(2026, 10, 1),
                                                   new DateOnly(2026, 10, 31), AccountingJournals.All);
        AccountingExportCheck.Check(entries).Should().BeEmpty();
        entries.Single(e => e.Kind == AccountingJournals.Sales && e.Piece == "AV/A1C/2026/000001")
               .Lines.Should().NotContain(l => l.Account == "34211");
    }
}
