using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// Where the patient stands on paying for each order, as the Caisse reads it: the patient payable of
/// the issued invoice the order carries, or, with none, what the patient will owe less the deposits
/// held (<see cref="OrderPayment"/>). Two queries for the whole list, not one per row. Read by the
/// report counter and by « Encaissement » / « À encaisser », so both say the same thing.
/// </summary>
internal static class OrderPaymentReader
{
    /// <summary>One order's payment status, with the figures the counter's panel shows beside it.</summary>
    /// <param name="InvoiceId">The issued invoice billing the order; null while none does.</param>
    /// <param name="Total">The invoice's total, or the order's live charges while it is not invoiced.</param>
    /// <param name="PayerShare">The payer's share when the payer is billed for it, otherwise zero.</param>
    /// <param name="AlreadyPaid">What was paid on the invoice, or the deposits held on the order.</param>
    internal sealed record Facts(OrderPayment Payment, Guid? InvoiceId, decimal Total, decimal PayerShare,
                                 decimal AlreadyPaid);

    public static async Task<Dictionary<Guid, Facts>> ReadAsync(LisDbContext db, IReadOnlyCollection<Guid> orderIds,
                                                                CancellationToken ct)
    {
        if (orderIds.Count == 0) return [];

        var orders = await db.LabOrders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .Select(o => new
            {
                o.Id,
                o.InvoiceId,
                // As the billing desk lists an order awaiting invoicing: its live acts only.
                Total = o.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled).Sum(i => i.ChargedPrice),
                PayerShare = o.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled).Sum(i => i.PayerShare),
                PatientShare = o.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled).Sum(i => i.PatientShare),
                PayerBilledDirectly = o.PayerId != null && o.DirectBillingAtRegistration,
                DepositHeld = db.Deposits.Where(d => d.LabOrderId == o.Id && d.Status == DepositStatus.Held)
                                         .Sum(d => d.Amount - d.AppliedAmount - d.RefundedAmount)
            })
            .ToListAsync(ct);

        var invoiceIds = orders.Where(o => o.InvoiceId != null).Select(o => o.InvoiceId!.Value).Distinct().ToList();

        var invoices = invoiceIds.Count == 0
            ? []
            : await db.Invoices.AsNoTracking()
                .Where(i => invoiceIds.Contains(i.Id)
                            && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled)
                .Select(i => new
                {
                    i.Id,
                    i.Number,
                    i.Kind,
                    i.TotalAmount,
                    i.PaidAmount,
                    i.CreditedAmount,
                    i.ClaimStatus,
                    i.PayerShareAmount,
                    i.PayerAcceptedAmount,
                    CoveredOrders = db.LabOrders.Count(o => o.InvoiceId == i.Id)
                })
                .ToDictionaryAsync(i => i.Id, ct);

        return orders.ToDictionary(
            o => o.Id,
            o => o.InvoiceId is { } id && invoices.TryGetValue(id, out var i)
                ? new Facts(OrderPayment.ForInvoice(i.Number, i.Kind, i.TotalAmount, i.PaidAmount, i.CreditedAmount,
                                                    i.ClaimStatus, i.PayerShareAmount, i.PayerAcceptedAmount, i.CoveredOrders),
                            i.Id, i.TotalAmount,
                            i.ClaimStatus == ClaimStatus.NotApplicable ? 0m : i.PayerShareAmount,
                            i.PaidAmount)
                : new Facts(OrderPayment.ForUninvoiced(o.Total, o.PatientShare, o.PayerBilledDirectly, o.DepositHeld),
                            null, o.Total, o.PayerBilledDirectly ? o.PayerShare : 0m, o.DepositHeld));
    }
}
