using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class BillingQueries(LisDbContext db) : IBillingQueries
{
    /// <summary>
    /// Orders with billable acts and no invoice yet, and orders still holding a deposit.
    /// <para>
    /// Cancelled orders are excluded, and so are cancelled items within a live order — a patient
    /// is not billed for an analysis the laboratory withdrew, and a tube the laboratory itself
    /// rejected is re-requested at zero price.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BillableOrderRow>> GetUninvoicedOrdersAsync(
        Guid siteId, CancellationToken ct = default) =>
        await db.LabOrders.AsNoTracking()
            .Where(o => o.SiteId == siteId
                        && ((o.InvoiceId == null
                             && o.Status != LabOrderStatus.Cancelled
                             && o.Status != LabOrderStatus.Draft
                             && o.Items.Any(i => i.Status != LabOrderItemStatus.Cancelled
                                                 && i.ChargedPrice > 0m))
                            // A request still holding a deposit stays listed, cancelled or already
                            // invoiced: what the invoice did not absorb (an analysis withdrawn, a
                            // discount at issue) has to be handed back from somewhere.
                            || db.Deposits.Any(d => d.LabOrderId == o.Id
                                                    && d.Status == DepositStatus.Held)))
            .Join(db.Patients.AsNoTracking(), o => o.PatientId, p => p.Id,
                  (o, p) => new { Order = o, Patient = p })
            // Ordered and capped before the projection: EF cannot sort on a member of a row built
            // through a constructor, and SQL Server rejected the query outright.
            .OrderBy(x => x.Order.RegisteredAt)
            .Take(500)
            .Select(x => new BillableOrderRow(
                x.Order.Id,
                x.Order.AccessionNumber,
                x.Patient.Id,
                x.Patient.LastName + " " + x.Patient.FirstName,
                x.Order.RegisteredAt,
                x.Order.PayerId == null
                    ? null
                    : db.Payers.Where(y => y.Id == x.Order.PayerId)
                               .Select(y => y.Name.Fr).FirstOrDefault(),
                x.Order.PayerId,
                x.Order.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled)
                             .Sum(i => i.ChargedPrice),
                x.Order.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled)
                             .Sum(i => i.PayerShare),
                x.Order.Items.Where(i => i.Status != LabOrderItemStatus.Cancelled)
                             .Sum(i => i.PatientShare),
                x.Order.Items.Count(i => i.Status != LabOrderItemStatus.Cancelled),
                x.Order.Status,
                db.Deposits.Where(d => d.LabOrderId == x.Order.Id && d.Status == DepositStatus.Held)
                           .Sum(d => d.Amount - d.AppliedAmount - d.RefundedAmount)))
            .ToListAsync(ct);

    public async Task<PagedResult<InvoiceRow>> GetInvoicesAsync(
        Guid siteId, InvoiceStatus? status, string? search, int page, int pageSize,
        DateOnly today, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var query = db.Invoices.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.Status != InvoiceStatus.Draft);

        if (status is { } wanted) query = query.Where(i => i.Status == wanted);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";

            query = query.Where(i =>
                EF.Functions.Like(i.Number, pattern)
                || EF.Functions.Like(i.BilledToName, pattern)
                || (i.CoveredOrderNumbersCsv != null
                    && EF.Functions.Like(i.CoveredOrderNumbersCsv, pattern)));
        }

        var total = await query.CountAsync(ct);

        var rows = await query
            // Newest first: the billing desk works on today's invoices, not on 2024's.
            .OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Number)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                i.Id,
                i.Number,
                i.Kind,
                i.Status,
                i.IssueDate,
                i.DueDate,
                i.BilledToName,
                i.TotalAmount,
                i.PaidAmount,
                i.CreditedAmount,
                i.ClaimStatus,
                i.CoveredOrderNumbersCsv,
                i.PayerShareAmount,
                i.PayerAcceptedAmount,
                // Closes the balance but was not paid: shown apart from the paid column.
                WrittenOff = db.Payments
                    .Where(p => p.InvoiceId == i.Id && p.Status == PaymentStatus.Settled
                                && p.Method == PaymentMethod.WriteOff)
                    .Sum(p => (decimal?)p.Amount) ?? 0m
            })
            .ToListAsync(ct);

        var mapped = rows.Select(r => new InvoiceRow(
            r.Id,
            r.Number,
            r.Kind,
            r.Status,
            r.IssueDate,
            r.DueDate,
            r.BilledToName,
            r.TotalAmount,
            // What was actually paid; a write-off is reported in its own column.
            Money.Round(r.PaidAmount - r.WrittenOff),
            // Credit notes reduce what is owed; the same rule as the entity's. The stored paid total
            // includes the write-offs, which do close the balance.
            Invoice.ComputeOutstanding(r.Kind, r.TotalAmount, r.PaidAmount, r.CreditedAmount),
            // Ageing computed here rather than stored, so it is never stale.
            r.DueDate is { } due && due < today
                && r.Status is InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid
                    ? today.DayNumber - due.DayNumber
                    : 0,
            r.ClaimStatus,
            r.CoveredOrderNumbersCsv,
            Money.Round(r.PayerShareAmount),
            r.PayerAcceptedAmount,
            r.CreditedAmount,
            Money.Round(r.WrittenOff))).ToList();

        return new PagedResult<InvoiceRow>(mapped, total, page, pageSize);
    }

    /// <summary>
    /// The operator's open till session, tracked because the caller accumulates onto it.
    /// </summary>
    public async Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(
        Guid invoiceId, CancellationToken ct = default) =>
        await db.Payments.AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId)
            // Correctable settlements first: the operator is here to fix one, and a list led by
            // already-refunded entries makes them hunt for the row they want.
            .OrderByDescending(p => p.Status == PaymentStatus.Settled)
            .ThenByDescending(p => p.PaymentDate)
            .Select(p => new PaymentRow(
                p.Id, p.Amount, p.Method, p.Status, p.PaymentDate,
                db.Users.Where(u => u.Id == p.ReceivedByUserId)
                        .Select(u => u.FullName).FirstOrDefault() ?? string.Empty,
                p.Reference, p.BankName, p.MaturityDate))
            .ToListAsync(ct);

    /// <summary>
    /// The printable content of one invoice.
    /// <para>
    /// Read as plain columns and shaped in memory: the line amounts are computed on the entity and
    /// never stored, and EF cannot order on a member of a record built through its constructor, so
    /// every ordering happens on the entity before anything is projected.
    /// </para>
    /// </summary>
    public async Task<InvoiceDocument?> GetInvoiceDocumentAsync(
        Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.AsNoTracking()
            .Where(i => i.Id == invoiceId)
            .Select(i => new
            {
                i.Id,
                i.Number,
                i.Kind,
                i.Status,
                i.IssueDate,
                i.DueDate,
                i.SiteId,
                i.PatientId,
                i.PayerId,
                i.BilledToName,
                i.BilledToIce,
                i.BilledToFiscalId,
                i.BilledToAddress,
                i.BilledToCity,
                i.CoveredOrderNumbersCsv,
                i.PrescriberName,
                i.CorrectsInvoiceNumber,
                i.CorrectionReason,
                i.GrossAmount,
                i.DiscountAmount,
                i.DiscountReason,
                i.NetAmount,
                i.VatAmount,
                i.TotalAmount,
                i.PayerShareAmount,
                i.PatientShareAmount,
                i.PaidAmount,
                i.CreditedAmount,
                i.AmountInWords
            })
            .FirstOrDefaultAsync(ct);

        if (invoice is null) return null;

        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == invoice.SiteId)
            .Select(s => new
            {
                Name = s.Name.Fr,
                s.LegalName,
                Address = s.AddressLine.Fr,
                s.City,
                s.PostalCode,
                s.Phone,
                s.Email,
                s.Ice,
                s.FiscalId,
                s.CommercialRegisterNumber,
                s.CommercialRegisterCourt,
                s.ProfessionalTaxNumber,
                s.CnssAffiliationNumber,
                s.Inpe,
                s.LogoPng
            })
            .FirstOrDefaultAsync(ct);

        var patient = invoice.PatientId is { } patientId
            ? await db.Patients.AsNoTracking()
                .Where(p => p.Id == patientId)
                .Select(p => new { Name = p.LastName + " " + p.FirstName, p.MedicalRecordNumber })
                .FirstOrDefaultAsync(ct)
            : null;

        var payerName = invoice.PayerId is { } payerId
            ? await db.Payers.AsNoTracking()
                .Where(p => p.Id == payerId)
                .Select(p => p.Name.Fr)
                .FirstOrDefaultAsync(ct)
            : null;

        // Entities, not a projection: net, VAT and patient share are computed properties.
        var lines = await db.InvoiceLines.AsNoTracking()
            .Where(l => l.InvoiceId == invoiceId)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(ct);

        // Oldest first: a receipt reads as the history of how the invoice was settled.
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.CreatedAt)
            .Select(p => new { p.PaymentDate, p.Method, p.Status, p.Amount, p.Reference, p.BankName })
            .ToListAsync(ct);

        var issuer = site is null
            ? new InvoiceIssuer(string.Empty, string.Empty, null, null, null, null, null, null,
                                null, null, null, null, null, null, null)
            : new InvoiceIssuer(site.Name, site.LegalName, site.Address, site.City, site.PostalCode,
                                site.Phone, site.Email, site.Ice, site.FiscalId,
                                site.CommercialRegisterNumber, site.CommercialRegisterCourt,
                                site.ProfessionalTaxNumber, site.CnssAffiliationNumber, site.Inpe,
                                site.LogoPng);

        return new InvoiceDocument(
            invoice.Id,
            invoice.Number,
            invoice.Kind,
            invoice.Status,
            invoice.IssueDate,
            invoice.DueDate,
            issuer,
            invoice.BilledToName,
            invoice.BilledToIce,
            invoice.BilledToFiscalId,
            invoice.BilledToAddress,
            invoice.BilledToCity,
            patient?.Name,
            patient?.MedicalRecordNumber,
            payerName,
            invoice.CoveredOrderNumbersCsv,
            invoice.PrescriberName,
            invoice.CorrectsInvoiceNumber,
            invoice.CorrectionReason,
            invoice.GrossAmount,
            invoice.DiscountAmount,
            invoice.DiscountReason,
            invoice.NetAmount,
            invoice.VatAmount,
            invoice.TotalAmount,
            invoice.PayerShareAmount,
            invoice.PatientShareAmount,
            invoice.PaidAmount,
            invoice.AmountInWords,
            [
                .. lines.Select(l => new InvoiceDocumentLine(
                    l.LineNumber,
                    l.AnalysisCode ?? l.NationalActCode,
                    l.Description,
                    l.Quantity,
                    l.UnitPrice,
                    l.NetAmount,
                    l.VatAmount,
                    l.TotalAmount,
                    l.PayerShare,
                    l.PatientShare,
                    l.IsVatExempt,
                    l.VatRate))
            ],
            [
                .. payments.Select(p => new InvoiceDocumentPayment(
                    p.PaymentDate, p.Method, p.Status, p.Amount, p.Reference, p.BankName))
            ])
        {
            CreditedAmount = invoice.CreditedAmount
        };
    }

    public Task<CashSession?> GetOpenCashSessionAsync(Guid operatorUserId,
                                                       CancellationToken ct = default) =>
        db.CashSessions
            .FirstOrDefaultAsync(s => s.OperatorUserId == operatorUserId
                                      && s.Status == CashSessionStatus.Open, ct);

    /// <summary>
    /// The till summary for the closing screen.
    /// <para>
    /// The figures are the session's own running counters — the very ones
    /// <see cref="CashSession.ExpectedClosingCash"/> and <c>Close()</c> reconcile against — so the
    /// expected cash pre-filled on the screen is the figure the variance is recorded on. It used to
    /// be re-derived from the settled payments, which disagreed with the domain twice over: a cash
    /// payment refunded in the same shift left the settled total and was also subtracted as a
    /// refund, and cash deposits (acomptes), which are not payments until invoiced, were missing
    /// altogether. Every such shift closed on a variance nobody had caused.
    /// </para>
    /// </summary>
    public async Task<CashSessionSummary?> GetCashSessionSummaryAsync(
        Guid operatorUserId, CancellationToken ct = default)
    {
        var session = await db.CashSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OperatorUserId == operatorUserId
                                      && s.Status == CashSessionStatus.Open, ct);

        if (session is null) return null;

        // What went through the till in this shift: payments taken at the counter and deposits.
        // A payment made by applying a deposit carries the deposit's session but is not a second
        // movement of money, so it is not counted again.
        var paymentCount = await db.Payments.AsNoTracking()
            .CountAsync(p => p.CashSessionId == session.Id && p.DepositId == null, ct);

        var depositCount = await db.Deposits.AsNoTracking()
            .CountAsync(d => d.CashSessionId == session.Id, ct);

        var operatorName = await db.Users.AsNoTracking()
            .Where(u => u.Id == session.OperatorUserId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        return CashSessionSummary.From(session, operatorName, paymentCount + depositCount);
    }

    // ---- Claims ----------------------------------------------------------------------------------

    /// <summary>
    /// The claim batches, aggregated from the invoices that carry each reference.
    /// <para>
    /// There is no batch table, and deliberately so: a batch is the set of invoices sharing a
    /// reference, and a separate header row would be a second place for the same fact to live and
    /// eventually disagree. The aggregation costs one grouped query and cannot drift from what was
    /// actually submitted.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ClaimBatchRow>> GetClaimBatchesAsync(
        Guid siteId, DateOnly since, DateOnly today, CancellationToken ct = default)
    {
        var rows = await db.Invoices.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && i.ClaimReference != null
                        && i.ClaimSubmittedOn != null
                        && i.ClaimSubmittedOn >= since)
            .Select(i => new
            {
                Reference = i.ClaimReference!,
                i.PayerId,
                SubmittedOn = i.ClaimSubmittedOn!.Value,
                i.ClaimStatus,
                i.NetAmount,
                i.PayerShareAmount,
                // What the payer itself paid. The invoice's PaidAmount also holds what the patient
                // paid at the counter and any write-off, which made a batch look settled when the
                // payer had sent nothing.
                PayerPaid = db.Payments
                    .Where(p => p.InvoiceId == i.Id)
                    .Where(Payment.IsPayerSettlement)
                    .Sum(p => (decimal?)p.Amount) ?? 0m,
                PayerName = db.Payers.Where(p => p.Id == i.PayerId)
                                      .Select(p => p.Name.Fr).FirstOrDefault(),
                // The payer's answer: its settlement or the write-off of what it refused, entered
                // after the submission. A counter payment or a deposit applied at issue is not an
                // outcome of the claim.
                LastOutcomeOn = db.Payments
                    .Where(p => p.InvoiceId == i.Id && p.Status == PaymentStatus.Settled
                                && (p.Method == PaymentMethod.ThirdPartyPayer
                                    || p.Method == PaymentMethod.WriteOff)
                                && p.PaymentDate >= i.ClaimSubmittedOn)
                    .Max(p => (DateOnly?)p.PaymentDate)
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                .GroupBy(r => new { r.Reference, r.PayerId })
                .Select(g => new ClaimBatchRow(
                    g.Key.Reference,
                    g.Key.PayerId,
                    g.First().PayerName ?? string.Empty,
                    g.Min(r => r.SubmittedOn),
                    g.Count(),
                    Money.Round(g.Sum(r => r.NetAmount)),
                    Money.Round(g.Sum(r => r.PayerShareAmount)),
                    Money.Round(g.Sum(r => r.PayerPaid)),
                    g.Count(r => r.ClaimStatus == ClaimStatus.Accepted
                                 || r.ClaimStatus == ClaimStatus.Settled),
                    g.Count(r => r.ClaimStatus == ClaimStatus.Rejected
                                 || r.ClaimStatus == ClaimStatus.PartiallyAccepted),
                    g.Count(r => r.ClaimStatus == ClaimStatus.Submitted
                                 || r.ClaimStatus == ClaimStatus.Resubmitted),
                    g.Max(r => r.LastOutcomeOn)))
                // Needs chasing first, then unanswered, then newest. The screen opens on the money
                // somebody has to go and ask about.
                .OrderByDescending(b => b.NeedsChasing(today))
                .ThenByDescending(b => b.IsAwaitingOutcome)
                .ThenByDescending(b => b.SubmittedOn)
        ];
    }

    public async Task<IReadOnlyList<ClaimInvoiceRow>> GetClaimInvoicesAsync(
        string claimReference, Guid? payerId = null, CancellationToken ct = default)
    {
        var rows = await db.Invoices.AsNoTracking()
            .Where(i => i.ClaimReference == claimReference
                        && (payerId == null || i.PayerId == payerId))
            .Select(i => new
            {
                i.Id,
                i.Number,
                i.IssueDate,
                i.BilledToName,
                i.PrescriberInpe,
                i.NetAmount,
                i.PayerShareAmount,
                i.PaidAmount,
                // A write-off of the refused share is not a payment: kept out of the paid column.
                WrittenOff = db.Payments
                    .Where(p => p.InvoiceId == i.Id && p.Status == PaymentStatus.Settled
                                && p.Method == PaymentMethod.WriteOff)
                    .Sum(p => (decimal?)p.Amount) ?? 0m,
                i.ClaimStatus,
                i.ClaimRejectionReason,
                i.CoveredOrderNumbersCsv,
                PatientName = db.Patients.Where(p => p.Id == i.PatientId)
                    .Select(p => p.LastName + " " + p.FirstName).FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                .Select(i => new ClaimInvoiceRow(
                    i.Id, i.Number, i.IssueDate,
                    i.PatientName ?? i.BilledToName ?? string.Empty,
                    i.PrescriberInpe,
                    Money.Round(i.NetAmount), Money.Round(i.PayerShareAmount), Money.Round(i.PaidAmount - i.WrittenOff),
                    i.ClaimStatus, i.ClaimRejectionReason, i.CoveredOrderNumbersCsv))
                // Refused first: those are the rows that need a decision, correct and resend or
                // write off.
                .OrderByDescending(i => i.WasRejected)
                .ThenBy(i => i.Number, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Issued invoices with a payer, not yet in a batch, with the blockers resolved per invoice.
    /// <para>
    /// The INPE requirement is read from the payer rather than assumed: every AMO scheme requires
    /// one and a private insurer generally does not, so a single rule would either block claims
    /// that are fine or pass ones that will be refused.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ClaimCandidateRow>> GetClaimCandidatesAsync(
        Guid siteId, CancellationToken ct = default)
    {
        var rows = await db.Invoices.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && i.ClaimReference == null
                        && i.PayerId != null
                        && i.Status != InvoiceStatus.Draft
                        && i.Status != InvoiceStatus.Cancelled
                        && i.Kind == InvoiceKind.Invoice
                        && i.PayerShareAmount > 0m
                        // Wholly reversed by credit notes: there is nothing left to claim.
                        && i.CreditedAmount < i.TotalAmount)
            .Select(i => new
            {
                i.Id,
                i.Number,
                i.IssueDate,
                PayerId = i.PayerId!.Value,
                i.BilledToName,
                i.NetAmount,
                i.PayerShareAmount,
                HasBiologistInpe = i.BiologistInpe != null && i.BiologistInpe != "",
                HasPrescriberInpe = i.PrescriberInpe != null && i.PrescriberInpe != "",
                Payer = db.Payers.Where(p => p.Id == i.PayerId)
                    .Select(p => new { p.Name, p.RequiresInpeOnClaim, p.RequiresPrescription,
                                       p.ThirdPartyPaymentAgreement })
                    .FirstOrDefault(),
                PatientName = db.Patients.Where(p => p.Id == i.PatientId)
                    .Select(p => p.LastName + " " + p.FirstName).FirstOrDefault()
            })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.Today);

        return
        [
            .. rows
                // Only payers the laboratory actually bills directly. Without a third-party
                // agreement the patient pays and reclaims, so there is no batch to send.
                .Where(i => i.Payer is { ThirdPartyPaymentAgreement: true })
                .Select(i => new ClaimCandidateRow(
                    i.Id, i.Number, i.IssueDate, i.PayerId,
                    i.Payer!.Name.Fr,
                    i.PatientName ?? i.BilledToName ?? string.Empty,
                    Money.Round(i.NetAmount), Money.Round(i.PayerShareAmount),
                    i.Payer.RequiresInpeOnClaim, i.HasBiologistInpe,
                    i.Payer.RequiresPrescription, i.HasPrescriberInpe,
                    today.DayNumber - i.IssueDate.DayNumber))
                // Oldest first: an unsent claim is money ageing, and the payer's own deadline runs
                // from the date of the act rather than from the date somebody got round to it.
                .OrderBy(i => i.PayerName, StringComparer.CurrentCulture)
                .ThenByDescending(i => i.DaysSinceIssue)
        ];
    }
}