using System.Globalization;
using MarocLIS.Application.Treasury;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Treasury;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class TreasuryQueries(LisDbContext db) : ITreasuryQueries
{
    // ---- Bank accounts ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<BankAccountRow>> GetBankAccountsAsync(Guid siteId, bool includeInactive,
                                                                          CancellationToken ct = default)
    {
        var rows = await db.BankAccounts.AsNoTracking()
            .Where(a => a.SiteId == siteId && (includeInactive || a.IsActive))
            .OrderBy(a => a.BankName).ThenBy(a => a.Label)
            .Select(a => new
            {
                a.Id,
                a.BankName,
                a.Label,
                a.Rib,
                a.AccountingAccount,
                a.IsActive,
                a.OpeningBalanceMad,
                a.OpeningBalanceDate,
                a.CsvMapping,
                Movement = db.BankStatementLines
                    .Where(l => l.BankAccountId == a.Id && l.OperationDate > a.OpeningBalanceDate)
                    .Sum(l => (decimal?)l.AmountMad) ?? 0m,
                LastLine = db.BankStatementLines.Where(l => l.BankAccountId == a.Id)
                    .Max(l => (DateOnly?)l.OperationDate),
                Unreconciled = db.BankStatementLines
                    .Count(l => l.BankAccountId == a.Id
                                && !db.BankMatches.Any(m => m.BankStatementLineId == l.Id && m.UnmatchedAt == null))
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(a => new BankAccountRow(
                a.Id, a.BankName, a.Label, a.Rib, a.AccountingAccount, a.IsActive, a.OpeningBalanceMad,
                a.OpeningBalanceDate, Money.Round(a.OpeningBalanceMad + a.Movement), a.LastLine, a.Unreconciled,
                a.CsvMapping))
        ];
    }

    public Task<bool> RibInUseAsync(string rib, Guid? exceptId, CancellationToken ct = default) =>
        // Every site: the same account declared on two sites would split its statement in two.
        db.BankAccounts.IgnoreQueryFilters().AsNoTracking()
          .AnyAsync(a => a.Rib == rib && (exceptId == null || a.Id != exceptId), ct);

    public async Task<IReadOnlySet<string>> GetExistingFingerprintsAsync(
        Guid bankAccountId, IReadOnlyCollection<string> fingerprints, CancellationToken ct = default)
    {
        if (fingerprints.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var wanted = fingerprints.Distinct(StringComparer.Ordinal).ToList();
        var found = await db.BankStatementLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.BankAccountId == bankAccountId && wanted.Contains(l.Fingerprint))
            .Select(l => l.Fingerprint)
            .ToListAsync(ct);

        return found.ToHashSet(StringComparer.Ordinal);
    }

    // ---- Reconciliation --------------------------------------------------------------------------

    public async Task<IReadOnlyList<StatementLineRow>> GetStatementLinesAsync(Guid bankAccountId, DateOnly from,
                                                                              DateOnly to, CancellationToken ct = default)
    {
        var rows = await db.BankStatementLines.AsNoTracking()
            .Where(l => l.BankAccountId == bankAccountId && l.OperationDate >= from && l.OperationDate <= to)
            .OrderBy(l => l.OperationDate).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.OperationDate,
                l.ValueDate,
                l.Label,
                l.Reference,
                l.AmountMad,
                Matches = db.BankMatches
                    .Where(m => m.BankStatementLineId == l.Id && m.UnmatchedAt == null)
                    .Select(m => new { m.Kind, m.AmountMad, m.IsAutomatic, m.Comment })
                    .ToList()
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        return
        [
            .. rows.Select(l => new StatementLineRow(
                l.Id, l.OperationDate, l.ValueDate, l.Label, l.Reference, l.AmountMad,
                Money.Round(l.Matches.Sum(m => m.AmountMad)), l.Matches.Count,
                l.Matches.Count > 0 && l.Matches.All(m => m.IsAutomatic),
                [.. l.Matches.Select(m => m.Kind).Distinct()],
                l.Matches.Select(m => m.Comment).FirstOrDefault(c => c != null)))
        ];
    }

    public async Task<IReadOnlyList<TreasuryMovement>> GetUnmatchedMovementsAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                                  CancellationToken ct = default)
    {
        var movements = new List<TreasuryMovement>();

        // Instants a day wider than the period on each side: the dates are taken in memory from
        // the offset each instant was recorded with.
        var fromInstant = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toInstant = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        bool InPeriod(DateOnly date) => date >= from && date <= to;
        static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.Date);

        // -- Patient payments by cheque, transfer or bill, and card payments for the day's batch.
        // A payment made by applying a deposit is not money coming in: the deposit was.
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.DepositId == null
                        && (p.Status == PaymentStatus.Settled || p.Status == PaymentStatus.Bounced
                            || p.Status == PaymentStatus.Refunded)
                        && (p.Method == PaymentMethod.Cheque || p.Method == PaymentMethod.BankTransfer
                            || p.Method == PaymentMethod.BillOfExchange || p.Method == PaymentMethod.Card
                            || p.Method == PaymentMethod.ThirdPartyPayer)
                        && ((p.PaymentDate >= from.AddDays(-60) && p.PaymentDate <= to)
                            || (p.MaturityDate != null && p.MaturityDate >= from && p.MaturityDate <= to)
                            || (p.BouncedAt != null && p.BouncedAt >= fromInstant && p.BouncedAt < toInstant)))
            .Select(p => new
            {
                p.Id,
                p.Method,
                p.Status,
                p.Amount,
                p.PaymentDate,
                p.MaturityDate,
                p.Reference,
                p.RemittanceReference,
                p.BouncedAt,
                Invoice = db.Invoices.Where(i => i.Id == p.InvoiceId)
                    .Select(i => new { i.Number, i.BilledToName, i.PayerId }).FirstOrDefault()
            })
            .ToListAsync(ct);

        var payerIds = payments.Where(p => p.Invoice?.PayerId != null).Select(p => p.Invoice!.PayerId!.Value)
                               .Distinct().ToList();
        var payerNames = await db.Payers.IgnoreQueryFilters().AsNoTracking()
            .Where(p => payerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name.Fr })
            .ToDictionaryAsync(p => p.Id, p => p.Fr, ct);

        foreach (var p in payments.Where(p => p.Method is PaymentMethod.Cheque or PaymentMethod.BankTransfer
                                                        or PaymentMethod.BillOfExchange))
        {
            // A post-dated cheque or a bill reaches the bank on its due date.
            var date = p.MaturityDate ?? p.PaymentDate;
            if (InPeriod(date))
            {
                movements.Add(new TreasuryMovement(TreasuryMovementKind.PatientPayment, p.Id.ToString("N"), date,
                                                   Money.Round(p.Amount), Describe(p.Invoice?.Number, p.Invoice?.BilledToName),
                                                   p.Reference, ClearsLate: false));
            }

            if (p.Status == PaymentStatus.Bounced && p.BouncedAt is { } bounced && InPeriod(DayOf(bounced)))
            {
                movements.Add(new TreasuryMovement(TreasuryMovementKind.ReturnedCheque, p.Id.ToString("N"), DayOf(bounced),
                                                   -Money.Round(p.Amount), Describe(p.Invoice?.Number, p.Invoice?.BilledToName),
                                                   p.Reference, ClearsLate: false));
            }
        }

        // -- Deposits taken by cheque, transfer, bill or card.
        var deposits = await db.Deposits.AsNoTracking()
            .Where(d => d.SiteId == siteId && d.ReceivedAt >= fromInstant && d.ReceivedAt < toInstant
                        && (d.Method == PaymentMethod.Cheque || d.Method == PaymentMethod.BankTransfer
                            || d.Method == PaymentMethod.BillOfExchange || d.Method == PaymentMethod.Card))
            .Select(d => new { d.Id, d.Number, d.Method, d.Amount, d.ReceivedAt, d.MaturityDate, d.Reference })
            .ToListAsync(ct);

        foreach (var d in deposits.Where(d => d.Method != PaymentMethod.Card))
        {
            var date = d.MaturityDate ?? DayOf(d.ReceivedAt);
            if (!InPeriod(date)) continue;
            movements.Add(new TreasuryMovement(TreasuryMovementKind.PatientDeposit, d.Id.ToString("N"), date,
                                               Money.Round(d.Amount), d.Number, d.Reference, ClearsLate: false));
        }

        // -- The day's card takings, which the acquirer credits as one batch.
        var cards = payments.Where(p => p.Method == PaymentMethod.Card && p.Status == PaymentStatus.Settled)
                            .Select(p => (Date: p.PaymentDate, p.Amount))
                            .Concat(deposits.Where(d => d.Method == PaymentMethod.Card)
                                            .Select(d => (Date: DayOf(d.ReceivedAt), d.Amount)))
                            .Where(c => InPeriod(c.Date))
                            .GroupBy(c => c.Date);

        foreach (var day in cards)
        {
            // The site is part of the key: every site has a batch on the same day.
            movements.Add(new TreasuryMovement(TreasuryMovementKind.CardBatch,
                                               $"{siteId:N}|{day.Key.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}", day.Key,
                                               Money.Round(day.Sum(c => c.Amount)),
                                               day.Count().ToString(CultureInfo.InvariantCulture), null, ClearsLate: false));
        }

        // -- Payer settlements: one remittance, however many invoices it settles.
        var remittances = payments
            .Where(p => p.Method == PaymentMethod.ThirdPartyPayer && p.Status == PaymentStatus.Settled
                        && InPeriod(p.PaymentDate))
            .GroupBy(p => (p.PaymentDate, PayerId: p.Invoice?.PayerId ?? Guid.Empty,
                           Reference: (p.RemittanceReference ?? p.Reference ?? string.Empty).Trim()));

        foreach (var r in remittances)
        {
            var reference = new string([.. r.Key.Reference.Where(char.IsLetterOrDigit)]);
            if (reference.Length > 38) reference = reference[..38];

            var key = $"{siteId:N}|{r.Key.PayerId:N}|{r.Key.PaymentDate:yyyyMMdd}|{reference}";
            movements.Add(new TreasuryMovement(TreasuryMovementKind.PayerSettlement, key, r.Key.PaymentDate,
                                               Money.Round(r.Sum(p => p.Amount)),
                                               payerNames.GetValueOrDefault(r.Key.PayerId) ?? "?",
                                               r.Key.Reference.Length == 0 ? null : r.Key.Reference, ClearsLate: false));
        }

        // -- Supplier payments. Cash ones never reach the bank.
        var supplierPayments = await (
                from p in db.SupplierPayments.AsNoTracking()
                join i in db.SupplierInvoices.AsNoTracking() on p.SupplierInvoiceId equals i.Id
                where i.SiteId == siteId && p.Method != SupplierPaymentMethod.Cash
                      && p.PaidOn >= @from && p.PaidOn <= to
                select new
                {
                    p.Id,
                    p.PaidOn,
                    p.AmountMad,
                    p.Method,
                    p.Reference,
                    i.InvoiceNumber,
                    Supplier = db.Suppliers.IgnoreQueryFilters().Where(s => s.Id == i.SupplierId)
                                 .Select(s => s.Name).FirstOrDefault()
                })
            .ToListAsync(ct);

        movements.AddRange(supplierPayments.Select(p => new TreasuryMovement(
            TreasuryMovementKind.SupplierPayment, p.Id.ToString("N"), p.PaidOn, -Money.Round(p.AmountMad),
            Describe(p.InvoiceNumber, p.Supplier), p.Reference,
            ClearsLate: p.Method is SupplierPaymentMethod.Cheque or SupplierPaymentMethod.BillOfExchange)));

        // -- Cash taken out of the tills and banked, per shift.
        var sessions = await db.CashSessions.AsNoTracking()
            .Where(s => s.SiteId == siteId && s.CashWithdrawals > 0m
                        && s.OpenedAt >= fromInstant && s.OpenedAt < toInstant)
            .Select(s => new
            {
                s.Id,
                s.OpenedAt,
                s.CashWithdrawals,
                Operator = db.Users.Where(u => u.Id == s.OperatorUserId).Select(u => u.FullName).FirstOrDefault()
            })
            .ToListAsync(ct);

        movements.AddRange(sessions.Where(s => InPeriod(DayOf(s.OpenedAt))).Select(s => new TreasuryMovement(
            TreasuryMovementKind.CashDeposit, s.Id.ToString("N"), DayOf(s.OpenedAt), Money.Round(s.CashWithdrawals),
            s.Operator ?? "?", null, ClearsLate: false)));

        // -- Net salaries of a closed month paid by transfer (the payslips carrying a RIB).
        var periods = await db.PayrollPeriods.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.Status == PayrollStatus.Closed)
            .Select(p => new
            {
                p.Id,
                p.Year,
                p.Month,
                Net = db.Payslips.Where(s => s.PayrollPeriodId == p.Id && s.Rib != null && s.Rib != "")
                                 .Sum(s => (decimal?)s.Amounts.NetPay) ?? 0m
            })
            .ToListAsync(ct);

        foreach (var p in periods)
        {
            var payDay = new DateOnly(p.Year, p.Month, 1).AddMonths(1).AddDays(-1);
            if (!InPeriod(payDay) || p.Net <= 0m) continue;
            movements.Add(new TreasuryMovement(TreasuryMovementKind.Payroll, p.Id.ToString("N"), payDay,
                                               -Money.Round(p.Net), $"{p.Month:00}/{p.Year}", null, ClearsLate: false));
        }

        // -- Less what an active match already holds.
        var matched = await db.BankMatches.AsNoTracking()
            .Where(m => m.SiteId == siteId && m.UnmatchedAt == null
                        && m.MovementDate >= from && m.MovementDate <= to)
            .Select(m => new { m.Kind, m.SourceKey })
            .ToListAsync(ct);
        var held = matched.Select(m => (m.Kind, m.SourceKey)).ToHashSet();

        return
        [
            .. movements.Where(m => !held.Contains((m.Kind, m.Key)))
                        .OrderBy(m => m.Date).ThenBy(m => m.Kind).ThenBy(m => m.Label, StringComparer.Ordinal)
        ];
    }

    private static string Describe(string? number, string? name) =>
        string.Join(" — ", new[] { number, name }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public Task<bool> IsMovementMatchedAsync(TreasuryMovementKind kind, string key, CancellationToken ct = default) =>
        db.BankMatches.IgnoreQueryFilters().AsNoTracking()
          .AnyAsync(m => m.Kind == kind && m.SourceKey == key && m.UnmatchedAt == null, ct);

    // ---- Forecast --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ForecastItemRow>> GetForecastItemsAsync(Guid siteId, CancellationToken ct = default) =>
        await db.TreasuryForecastItems.AsNoTracking()
            .Where(i => i.SiteId == siteId)
            .OrderByDescending(i => i.IsActive).ThenBy(i => i.StartDate).ThenBy(i => i.Label)
            .Select(i => new ForecastItemRow(i.Id, i.Label, i.AmountMad, i.Recurrence, i.StartDate, i.EndDate, i.IsActive))
            .ToListAsync(ct);

    public async Task<CashForecastSource> GetForecastSourceAsync(Guid siteId, DateOnly today, DateOnly historyFrom,
                                                                 DateOnly patientHistoryFrom, CancellationToken ct = default)
    {
        // -- Opening: each active account's statement balance, as of its last imported line.
        var accounts = await GetBankAccountsAsync(siteId, includeInactive: false, ct);
        var openings = accounts.Select(a => new ForecastOpeningBalance(a.Display, a.StatementBalanceMad,
                                                                       a.LastLineDate ?? a.OpeningBalanceDate)).ToList();

        // -- Claim batches still awaiting the payer's money.
        var claimed = await db.Invoices.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.PayerId != null && i.ClaimReference != null
                        && i.ClaimSubmittedOn != null && i.Status != InvoiceStatus.Cancelled
                        && (i.ClaimStatus == ClaimStatus.Submitted || i.ClaimStatus == ClaimStatus.Resubmitted
                            || i.ClaimStatus == ClaimStatus.Accepted || i.ClaimStatus == ClaimStatus.PartiallyAccepted))
            .Select(i => new
            {
                Reference = i.ClaimReference!,
                PayerId = i.PayerId!.Value,
                SubmittedOn = i.ClaimSubmittedOn!.Value,
                Expected = i.PayerAcceptedAmount ?? i.PayerShareAmount,
                PayerPaid = db.Payments.Where(p => p.InvoiceId == i.Id)
                                       .Where(Payment.IsPayerSettlement)
                                       .Sum(p => (decimal?)p.Amount) ?? 0m
            })
            .ToListAsync(ct);

        var payerIds = claimed.Select(c => c.PayerId).Distinct().ToList();
        var payers = await db.Payers.IgnoreQueryFilters().AsNoTracking()
            .Where(p => payerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name.Fr, p.PaymentTermsDays })
            .ToDictionaryAsync(p => p.Id, ct);

        var batches = claimed
            .GroupBy(c => (c.Reference, c.PayerId))
            .Select(g => new OpenClaimBatch(
                g.Key.Reference, g.Key.PayerId,
                payers.TryGetValue(g.Key.PayerId, out var payer) ? payer.Fr : "?",
                payers.TryGetValue(g.Key.PayerId, out var terms) ? terms.PaymentTermsDays : 60,
                g.Min(c => c.SubmittedOn),
                Money.Round(g.Sum(c => Math.Max(0m, c.Expected - c.PayerPaid)))))
            .ToList();

        // -- What each payer actually took over the last year, batch by batch.
        var history = await db.Invoices.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.PayerId != null && i.ClaimReference != null
                        && i.ClaimSubmittedOn != null && i.ClaimSubmittedOn >= historyFrom)
            .Select(i => new
            {
                Reference = i.ClaimReference!,
                PayerId = i.PayerId!.Value,
                SubmittedOn = i.ClaimSubmittedOn!.Value,
                FirstPaid = db.Payments.Where(p => p.InvoiceId == i.Id)
                                       .Where(Payment.IsPayerSettlement)
                                       .Min(p => (DateOnly?)p.PaymentDate)
            })
            .ToListAsync(ct);

        var samples = history
            .GroupBy(h => (h.Reference, h.PayerId))
            .Where(g => g.Any(h => h.FirstPaid != null))
            .Select(g => new PayerDelaySample(
                g.Key.PayerId,
                g.Where(h => h.FirstPaid != null).Min(h => h.FirstPaid!.Value).DayNumber - g.Min(h => h.SubmittedOn).DayNumber))
            .ToList();

        // -- Supplier invoices still owed.
        var invoices = await db.SupplierInvoices.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && (i.Status == SupplierInvoiceStatus.Pending || i.Status == SupplierInvoiceStatus.Blocked
                            || i.Status == SupplierInvoiceStatus.ApprovedForPayment
                            || i.Status == SupplierInvoiceStatus.PartiallyPaid))
            .Select(i => new
            {
                i.InvoiceNumber,
                i.DueDate,
                i.Status,
                Supplier = db.Suppliers.IgnoreQueryFilters().Where(s => s.Id == i.SupplierId)
                             .Select(s => s.Name).FirstOrDefault(),
                Lines = i.Lines.Select(l => new { l.Quantity, l.UnitPriceMad, l.VatRate }).ToList(),
                Paid = i.Payments.Sum(p => (decimal?)p.AmountMad) ?? 0m
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        var due = invoices.Select(i =>
        {
            // Same rounding as the aggregate: each line, then the totals.
            var excluding = i.Lines.Select(l => (Ht: Money.Round(l.Quantity * l.UnitPriceMad), l.VatRate)).ToList();
            var ht = Money.Round(excluding.Sum(l => l.Ht));
            var vat = Money.Round(excluding.Sum(l => Money.Round(l.Ht * l.VatRate)));
            var outstanding = Math.Max(0m, Money.Round(ht + vat - i.Paid));
            return new SupplierInvoiceDue(i.Supplier ?? "?", i.InvoiceNumber, i.DueDate, outstanding,
                                          i.Status is SupplierInvoiceStatus.ApprovedForPayment
                                                   or SupplierInvoiceStatus.PartiallyPaid);
        }).Where(i => i.OutstandingMad > 0m).ToList();

        // -- The last closed month's net salaries.
        var lastPayroll = await db.PayrollPeriods.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.Status == PayrollStatus.Closed)
            .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
            .Select(p => new
            {
                p.Year,
                p.Month,
                Net = db.Payslips.Where(s => s.PayrollPeriodId == p.Id).Sum(s => (decimal?)s.Amounts.NetPay) ?? 0m
            })
            .FirstOrDefaultAsync(ct);

        // -- The counter's takings over the history window: patient payments and deposits.
        var fromInstant = new DateTimeOffset(patientHistoryFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toInstant = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var collected = await db.Payments.AsNoTracking()
            .Where(p => p.SiteId == siteId && p.DepositId == null
                        && p.PaymentDate >= patientHistoryFrom && p.PaymentDate < today
                        && p.Method != PaymentMethod.ThirdPartyPayer)
            .Where(Payment.IsMoneyCollected)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var deposited = await db.Deposits.AsNoTracking()
            .Where(d => d.SiteId == siteId && d.ReceivedAt >= fromInstant && d.ReceivedAt < toInstant)
            .SumAsync(d => (decimal?)d.Amount, ct) ?? 0m;

        return new CashForecastSource(
            openings, batches, samples, due,
            Money.Round(lastPayroll?.Net ?? 0m),
            lastPayroll is null ? null : $"{lastPayroll.Month:00}/{lastPayroll.Year}",
            Money.Round(collected + deposited));
    }

    // ---- Cash sessions ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<CashSessionReviewRow>> GetClosedCashSessionsAsync(
        Guid siteId, DateOnly from, DateOnly to, bool pendingOnly, CancellationToken ct = default)
    {
        var fromInstant = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toInstant = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var sessions = await db.CashSessions.AsNoTracking()
            .Where(s => s.SiteId == siteId && s.Status != CashSessionStatus.Open
                        && (!pendingOnly || s.Status == CashSessionStatus.Closed)
                        && s.OpenedAt >= fromInstant && s.OpenedAt < toInstant)
            .OrderByDescending(s => s.OpenedAt)
            .ToListAsync(ct);

        sessions = [.. sessions.Where(s => DateOnly.FromDateTime(s.OpenedAt.Date) is var d && d >= from && d <= to)];

        var userIds = sessions.Select(s => s.OperatorUserId)
                              .Concat(sessions.Where(s => s.ReconciledByUserId != null).Select(s => s.ReconciledByUserId!.Value))
                              .Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return
        [
            .. sessions.Select(s => new CashSessionReviewRow(
                s.Id, names.GetValueOrDefault(s.OperatorUserId) ?? "?", s.Workstation, s.OpenedAt, s.ClosedAt,
                s.OpeningFloat, s.ExpectedClosingCash, s.CountedCash, s.Variance, s.VarianceExplanation,
                s.CashWithdrawals, Money.Round(s.ChequeReceipts + s.CardReceipts + s.TransferReceipts),
                s.Status == CashSessionStatus.Reconciled,
                s.ReconciledByUserId is { } by ? names.GetValueOrDefault(by) : null,
                s.ReconciledAt, s.ReconciliationComment))
        ];
    }
}
