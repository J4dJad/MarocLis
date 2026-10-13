using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Persistence.Demo;

/// <summary>
/// The money side of the demonstration: open tills, issued invoices in every payment state, a
/// claim batch sent to the CNSS, and orders deliberately left uninvoiced.
/// </summary>
public sealed partial class DemoDataSeeder
{
    private sealed record DemoInvoice(string Patient, PaymentMethod? Method, bool PayInFull,
                                      bool SubmitClaim = false, string? Reference = null,
                                      string? Bank = null);

    /// <summary>
    /// Five invoices: a self-paying patient's, half paid in cash; one whose patient share was
    /// settled by card and whose payer share waits in the claims list; one whose patient share was
    /// settled by bank transfer, its mutual share likewise awaiting a claim; one whose payer share
    /// has been sent to the CNSS in a claim batch; and one paid in full by cheque. Every other
    /// demonstration order stays uninvoiced, so the billing screen's "to invoice" list is not empty
    /// either.
    /// </summary>
    private static readonly DemoInvoice[] Invoices =
    [
        new("DEMO-000002", PaymentMethod.Cash, PayInFull: false),
        new("DEMO-000004", PaymentMethod.Card, PayInFull: false, Reference: "TPE-DEMO-0417"),
        new("DEMO-000009", PaymentMethod.BankTransfer, PayInFull: false,
            Reference: "VIR-DEMO-2026-0912", Bank: "Banque Populaire (démo)"),
        new("DEMO-000010", PaymentMethod.Cash, PayInFull: false, SubmitClaim: true),
        new("DEMO-000011", PaymentMethod.Cheque, PayInFull: true,
            Reference: "CHQ-DEMO-0048213", Bank: "Attijariwafa bank (démo)")
    ];

    private async Task SeedBillingAsync(CancellationToken ct)
    {
        // Open tills: the secretary's, which takes the cash payments below, and the
        // administrator's, so the cash-session screen has an open session whoever signs in first.
        foreach (var (actor, openingFloat) in new[] { (DemoActor.Secretary, 500m), (DemoActor.Administrator, 300m) })
        {
            var userId = _actors[actor];

            var open = await QueryAsync(db => db.CashSessions.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(c => c.OperatorUserId == userId && c.ClosedAt == null, ct));

            if (!open)
            {
                Check(await AsAsync<BillingService, Result<Guid>>(actor,
                          s => s.OpenCashSessionAsync(openingFloat, ct), ct),
                      $"Ouverture de caisse ({actor})");
            }
        }

        var claimable = new List<Guid>();

        foreach (var plan in Invoices)
        {
            var order = await QueryAsync(db => db.LabOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.InternalNote == DemoMarker
                            && o.Patient!.MedicalRecordNumber == plan.Patient)
                .Select(o => new { o.Id, o.InvoiceId })
                .FirstOrDefaultAsync(ct));

            if (order is null) continue;

            var invoiceId = order.InvoiceId;

            if (invoiceId is null)
            {
                var issued = await AsAsync<BillingService, Result<Guid>>(DemoActor.Secretary,
                    s => s.IssueInvoiceAsync([order.Id], ct: ct), ct);

                if (!Check(issued, $"Facture {plan.Patient}")) continue;

                invoiceId = issued.Value;

                // Paid only on the run that issued it, or each start would pay it again.
                if (plan.Method is { } method)
                {
                    var invoice = await QueryAsync(db => db.Invoices.AsNoTracking()
                        .Where(i => i.Id == invoiceId)
                        .Select(i => new { i.TotalAmount, i.PatientShareAmount, i.PayerShareAmount, i.Number })
                        .FirstAsync(ct));

                    // A partial cash payment leaves half of the patient's share outstanding; the
                    // other methods settle the patient's share and leave the payer's to the claim.
                    var amount = plan.PayInFull
                        ? invoice.TotalAmount
                        : invoice.PayerShareAmount == 0m
                            ? Math.Round(invoice.TotalAmount / 2m, 2, MidpointRounding.AwayFromZero)
                            : invoice.PatientShareAmount;

                    if (amount > 0m)
                    {
                        Check(await AsAsync<BillingService, Result>(DemoActor.Secretary,
                                  s => s.RecordPaymentAsync(new RecordPaymentRequest(
                                      invoiceId.Value, amount, method, plan.Reference, plan.Bank,
                                      null, "Règlement de démonstration"), ct), ct),
                              $"Règlement {invoice.Number}");
                    }
                }
            }

            if (plan.SubmitClaim) claimable.Add(invoiceId.Value);
        }

        // The claim batch: only invoices not already in one, so a restart does not open a second.
        var toClaim = await QueryAsync(db => db.Invoices.AsNoTracking()
            .Where(i => claimable.Contains(i.Id) && i.ClaimReference == null
                        && i.PayerId != null && i.PayerShareAmount > 0m)
            .Select(i => i.Id)
            .ToListAsync(ct));

        if (toClaim.Count > 0)
        {
            Check(await AsAsync<BillingService, Result<string>>(DemoActor.Secretary,
                      s => s.SubmitClaimBatchAsync(toClaim, ct), ct),
                  "Bordereau tiers payant");
        }
    }
}
