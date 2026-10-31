using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// The reads of « Encaissement » and « À encaisser »: a file's balance, today's files still owing,
/// the user's last method and the receipt of a deposit. The payment status is the report counter's
/// own (<see cref="OrderPaymentReader"/>), so the counter and the panel never disagree.
/// </summary>
public sealed class CounterCollectionQueries(LisDbContext db) : ICounterCollectionQueries
{
    /// <summary>The longest queue read: a day's counter, not a ledger.</summary>
    public const int QueueLimit = 500;

    public async Task<CounterBalance?> GetBalanceAsync(Guid labOrderId, Guid siteId, CancellationToken ct = default)
    {
        var order = await db.LabOrders.AsNoTracking()
            .Where(o => o.Id == labOrderId && o.SiteId == siteId)
            .Select(o => new
            {
                o.Id,
                o.AccessionNumber,
                o.PatientId,
                PatientName = o.Patient != null ? o.Patient.LastName + " " + o.Patient.FirstName : string.Empty,
                o.RegisteredAt,
                o.DiscountAmount
            })
            .FirstOrDefaultAsync(ct);

        if (order is null) return null;

        var facts = (await OrderPaymentReader.ReadAsync(db, [order.Id], ct))[order.Id];

        return new CounterBalance(order.Id, order.AccessionNumber, order.PatientId, order.PatientName.Trim(),
                                  order.RegisteredAt, facts.Payment, facts.InvoiceId, facts.Total, facts.PayerShare,
                                  order.DiscountAmount, facts.AlreadyPaid);
    }

    public async Task<IReadOnlyList<CollectionQueueRow>> GetQueueAsync(Guid siteId, DateTimeOffset from, DateTimeOffset to,
                                                                       CancellationToken ct = default)
    {
        var orders = await db.LabOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId
                        && o.RegisteredAt >= from && o.RegisteredAt < to
                        && o.Status != LabOrderStatus.Cancelled && o.Status != LabOrderStatus.Draft)
            .OrderBy(o => o.RegisteredAt)
            .Take(QueueLimit)
            .Select(o => new
            {
                o.Id,
                o.AccessionNumber,
                PatientName = o.Patient != null ? o.Patient.LastName + " " + o.Patient.FirstName : string.Empty,
                o.RegisteredAt
            })
            .ToListAsync(ct);

        var facts = await OrderPaymentReader.ReadAsync(db, [.. orders.Select(o => o.Id)], ct);

        return
        [
            .. orders
                .Select(o => (Order: o, Facts: facts[o.Id]))
                .Where(x => x.Facts.Payment is { IsSettled: false, PatientDue: > 0m })
                .Select(x => new CollectionQueueRow(x.Order.Id, x.Order.AccessionNumber, x.Order.PatientName.Trim(),
                                                    x.Order.RegisteredAt, x.Facts.Payment.State,
                                                    x.Facts.Payment.PatientDue ?? 0m, x.Facts.Payment.InvoiceNumber,
                                                    x.Facts.AlreadyPaid))
        ];
    }

    public async Task<PaymentMethod?> GetLastMethodAsync(Guid userId, CancellationToken ct = default)
    {
        var counter = BillingScreenRules.CounterMethods.ToList();

        // A deposit applied to an invoice is a settlement too, but not one this user took then.
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.ReceivedByUserId == userId && p.DepositId == null && counter.Contains(p.Method))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Method, At = p.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var deposit = await db.Deposits.AsNoTracking()
            .Where(d => d.ReceivedByUserId == userId && d.CarriedFromInvoiceId == null && counter.Contains(d.Method))
            .OrderByDescending(d => d.ReceivedAt)
            .Select(d => new { d.Method, At = d.ReceivedAt })
            .FirstOrDefaultAsync(ct);

        if (payment is null) return deposit?.Method;
        if (deposit is null) return payment.Method;
        return deposit.At > payment.At ? deposit.Method : payment.Method;
    }

    public async Task<InvoiceDocument?> GetDepositReceiptAsync(Guid labOrderId, CancellationToken ct = default)
    {
        var order = await db.LabOrders.AsNoTracking()
            .Where(o => o.Id == labOrderId)
            .Select(o => new
            {
                o.Id,
                o.SiteId,
                o.AccessionNumber,
                o.DiscountAmount,
                o.DiscountReason,
                PatientName = o.Patient != null ? o.Patient.LastName + " " + o.Patient.FirstName : string.Empty,
                RecordNumber = o.Patient != null ? o.Patient.MedicalRecordNumber : null,
                AddressLine = o.Patient != null ? o.Patient.AddressLine : null,
                City = o.Patient != null ? o.Patient.City : null,
                Prescriber = o.Prescriber != null ? o.Prescriber.LastName + " " + o.Prescriber.FirstName : o.PrescriberFreeText,
                PayerName = o.PayerId == null ? null : db.Payers.Where(p => p.Id == o.PayerId).Select(p => p.Name.Fr).FirstOrDefault(),
                PayerBilledDirectly = o.PayerId != null && o.DirectBillingAtRegistration,
                Total = o.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled).Sum(i => i.ChargedPrice),
                PayerShare = o.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled).Sum(i => i.PayerShare),
                PatientShare = o.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled).Sum(i => i.PatientShare)
            })
            .FirstOrDefaultAsync(ct);

        if (order is null) return null;

        var held = await db.Deposits.AsNoTracking()
            .Where(d => d.LabOrderId == labOrderId && d.Status == DepositStatus.Held)
            .OrderBy(d => d.ReceivedAt)
            .Select(d => new
            {
                d.Number,
                d.ReceivedAt,
                d.Method,
                Remaining = d.Amount - d.AppliedAmount - d.RefundedAmount,
                d.Reference,
                d.BankName
            })
            .ToListAsync(ct);

        if (held.Count == 0) return null;

        var issuer = await db.Sites.AsNoTracking()
            .Where(s => s.Id == order.SiteId)
            .Select(s => new InvoiceIssuer(s.Name.Fr, s.LegalName, s.AddressLine.Fr, s.City, s.PostalCode, s.Phone, s.Email,
                                           s.Ice, s.FiscalId, s.CommercialRegisterNumber, s.CommercialRegisterCourt,
                                           s.ProfessionalTaxNumber, s.CnssAffiliationNumber, s.Inpe, s.LogoPng))
            .FirstOrDefaultAsync(ct)
            ?? new InvoiceIssuer(string.Empty, string.Empty, null, null, null, null, null, null, null, null, null, null,
                                 null, null, null);

        var last = held[^1];
        var paid = Money.Round(held.Sum(d => d.Remaining));
        var payerShare = order.PayerBilledDirectly ? Money.Round(order.PayerShare) : 0m;
        var patientShare = order.PayerBilledDirectly ? Money.Round(order.PatientShare) : Money.Round(order.Total);

        // Laid out as the invoice's receipt, numbered by the deposit's receipt number: the receipt
        // series is the deposits' own. Nothing is persisted: the deposits are the record.
        return new InvoiceDocument(
            order.Id,
            last.Number,
            InvoiceKind.Receipt,
            InvoiceStatus.Issued,
            DateOnly.FromDateTime(last.ReceivedAt.Date),
            null,
            issuer,
            order.PatientName.Trim(),
            null,
            null,
            order.AddressLine,
            order.City,
            order.PatientName.Trim(),
            order.RecordNumber,
            order.PayerName,
            order.AccessionNumber,
            order.Prescriber,
            null,
            null,
            Money.Round(order.Total + order.DiscountAmount),
            order.DiscountAmount,
            order.DiscountReason,
            Money.Round(order.Total),
            0m,
            Money.Round(order.Total),
            payerShare,
            patientShare,
            paid,
            null,
            [],
            [
                .. held.Select(d => new InvoiceDocumentPayment(DateOnly.FromDateTime(d.ReceivedAt.Date), d.Method,
                                                               PaymentStatus.Settled, Money.Round(d.Remaining),
                                                               d.Reference ?? d.Number, d.BankName))
            ]);
    }
}
