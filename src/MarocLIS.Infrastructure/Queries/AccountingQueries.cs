using MarocLIS.Application.Accounting;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class AccountingQueries(LisDbContext db) : IAccountingQueries
{
    public async Task<IReadOnlyList<AccountingSiteOption>> GetSitesAsync(IReadOnlyCollection<Guid> siteIds,
                                                                         CancellationToken ct = default)
    {
        var ids = siteIds.ToList();
        var rows = await db.Sites.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Code)
            .Select(s => new { s.Id, s.Code, s.Name })
            .ToListAsync(ct);

        return [.. rows.Select(s => new AccountingSiteOption(s.Id, s.Code, s.Name.Fr))];
    }

    public async Task<IReadOnlyList<AccountingDepartmentOption>> GetDepartmentsAsync(CancellationToken ct = default)
    {
        var rows = await db.Departments.AsNoTracking()
            .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Code)
            .Select(d => new { d.Code, d.Name })
            .ToListAsync(ct);

        return [.. rows.Select(d => new AccountingDepartmentOption(d.Code, d.Name.Fr))];
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix,
                                                                                  CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.SiteId == null && s.Key.StartsWith(prefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    private sealed record Party(string? RecordNumber, string? PayerCode, string? PayerAccount);

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.Date);

    public async Task<SalesAccountingSources> GetSalesSourcesAsync(IReadOnlyCollection<Guid> siteIds, DateOnly from,
                                                                   DateOnly to, CancellationToken ct = default)
    {
        var ids = siteIds.ToList();
        var (start, end) = (from, to);

        // The timestamps (bounce, refund, deposit) are read a day wider on each side and cut to the
        // day in memory: the day is the local date the operator saw, not the server's.
        var after = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var before = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var sites = await db.Sites.AsNoTracking().IgnoreQueryFilters()
            .Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code, ct);
        string SiteCode(Guid id) => sites.GetValueOrDefault(id, string.Empty);

        // ---- Invoices and credit notes ----

        var documents = await db.Invoices.AsNoTracking()
            .Where(i => ids.Contains(i.SiteId)
                        && (i.Kind == InvoiceKind.Invoice || i.Kind == InvoiceKind.Receipt || i.Kind == InvoiceKind.CreditNote)
                        && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled
                        && i.IssueDate >= start && i.IssueDate <= end)
            .Select(i => new DocumentRow(i.Id, i.SiteId, i.Kind, i.Number, i.IssueDate, i.PatientId, i.PayerId,
                                         i.BilledToName, i.ClaimStatus, i.TotalAmount, i.VatAmount,
                                         i.PayerShareAmount, i.CorrectsInvoiceId))
            .ToListAsync(ct);

        var originalIds = documents.Where(d => d.CorrectsInvoiceId is not null)
                                   .Select(d => d.CorrectsInvoiceId!.Value).Distinct().ToList();
        var originals = originalIds.Count == 0
            ? []
            : await db.Invoices.AsNoTracking().IgnoreQueryFilters()
                .Where(i => originalIds.Contains(i.Id))
                .Select(i => new DocumentRow(i.Id, i.SiteId, i.Kind, i.Number, i.IssueDate, i.PatientId, i.PayerId,
                                             i.BilledToName, i.ClaimStatus, i.TotalAmount, i.VatAmount,
                                             i.PayerShareAmount, i.CorrectsInvoiceId))
                .ToListAsync(ct);

        var lineInvoiceIds = documents.Select(d => d.Id).Concat(originalIds).Distinct().ToList();
        var lines = await LinesAsync(lineInvoiceIds, ct);

        // ---- Settlements ----

        var payments = await (
                from p in db.Payments.AsNoTracking()
                join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                where ids.Contains(p.SiteId)
                      && ((p.PaymentDate >= start && p.PaymentDate <= end)
                          || (p.BouncedAt != null && p.BouncedAt >= after && p.BouncedAt < before)
                          || (p.RefundedAt != null && p.RefundedAt >= after && p.RefundedAt < before)
                          || (p.Status == PaymentStatus.Cancelled && p.ModifiedAt != null
                              && p.ModifiedAt >= after && p.ModifiedAt < before)
                          || (p.DepositId != null && i.IssueDate >= start && i.IssueDate <= end))
                select new
                {
                    p.SiteId, i.Number, i.IssueDate, i.PatientId, i.PayerId, i.BilledToName, i.ClaimStatus,
                    p.Method, p.Status, p.Amount, p.PaymentDate, p.BouncedAt, p.RefundedAt, p.ModifiedAt,
                    p.DepositId, p.CashSessionId, Reference = p.RemittanceReference
                })
            .ToListAsync(ct);

        var depositIds = payments.Where(p => p.DepositId is not null).Select(p => p.DepositId!.Value).Distinct().ToList();
        var depositNumbers = depositIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Deposits.AsNoTracking().IgnoreQueryFilters()
                .Where(d => depositIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Number, ct);

        var sessionIds = payments.Where(p => p.CashSessionId is not null).Select(p => p.CashSessionId!.Value).Distinct().ToList();
        var sessionDates = sessionIds.Count == 0
            ? new Dictionary<Guid, DateTimeOffset>()
            : await db.CashSessions.AsNoTracking().IgnoreQueryFilters()
                .Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.OpenedAt, ct);

        // ---- Payers' refused shares left to the patient ----

        var shortfallRows = await db.Invoices.AsNoTracking()
            .Where(i => ids.Contains(i.SiteId)
                        && i.Kind == InvoiceKind.Invoice
                        && i.ClaimStatus != ClaimStatus.NotApplicable
                        && i.PayerAcceptedAmount != null && i.PayerAcceptedAmount < i.PayerShareAmount
                        && (i.ModifiedAt ?? i.CreatedAt) >= after)
            .Select(i => new
            {
                i.SiteId, i.Number, i.PatientId, i.PayerId, i.BilledToName, i.PayerShareAmount, i.PayerAcceptedAmount,
                i.ModifiedAt, i.CreatedAt,
                Settled = i.Payments.Where(p => p.Method == PaymentMethod.ThirdPartyPayer && p.Status == PaymentStatus.Settled)
                                    .Select(p => (DateOnly?)p.PaymentDate).Max(),
                WrittenOff = i.Payments.Where(p => p.Method == PaymentMethod.WriteOff && p.Status == PaymentStatus.Settled)
                                       .Sum(p => (decimal?)p.Amount) ?? 0m,
                WrittenOn = i.Payments.Where(p => p.Method == PaymentMethod.WriteOff && p.Status == PaymentStatus.Settled)
                                      .Select(p => (DateOnly?)p.PaymentDate).Max()
            })
            .ToListAsync(ct);

        // ---- Deposits ----

        var deposits = await db.Deposits.AsNoTracking()
            .Where(d => ids.Contains(d.SiteId)
                        && ((d.ReceivedAt >= after && d.ReceivedAt < before)
                            || (d.RefundedAt != null && d.RefundedAt >= after && d.RefundedAt < before)))
            .Select(d => new
            {
                d.SiteId, d.Number, d.PatientId, d.Method, d.Amount, d.ReceivedAt, d.RefundedAmount, d.RefundedAt,
                d.CashSessionId, d.Reference
            })
            .ToListAsync(ct);

        // ---- Closed tills ----

        var sessions = await (
                from s in db.CashSessions.AsNoTracking()
                where ids.Contains(s.SiteId) && s.Status != CashSessionStatus.Open
                      && s.ClosedAt != null && s.ClosedAt >= after && s.ClosedAt < before
                join u in db.Users.AsNoTracking().IgnoreQueryFilters() on s.OperatorUserId equals u.Id into uj
                from u in uj.DefaultIfEmpty()
                select new
                {
                    s.Id, s.SiteId, s.OpenedAt, s.ClosedAt, s.CashWithdrawals, s.OpeningFloat,
                    s.ExpectedCashReceipts, s.CashRefunds, s.CountedCash,
                    Operator = u == null ? null : u.FullName
                })
            .ToListAsync(ct);

        // ---- Who owes it ----

        var patientIds = documents.Concat(originals).Select(d => d.PatientId)
            .Concat(payments.Select(p => p.PatientId)).Concat(shortfallRows.Select(s => s.PatientId))
            .Concat(deposits.Select(d => (Guid?)d.PatientId))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var patients = patientIds.Count == 0
            ? new Dictionary<Guid, (string Record, string Name)>()
            : (await db.Patients.AsNoTracking().IgnoreQueryFilters()
                .Where(p => patientIds.Contains(p.Id))
                .Select(p => new { p.Id, p.MedicalRecordNumber, p.LastName, p.FirstName })
                .ToListAsync(ct))
                .ToDictionary(p => p.Id, p => (Record: p.MedicalRecordNumber, Name: $"{p.LastName} {p.FirstName}".Trim()));

        var payers = await db.Payers.AsNoTracking().IgnoreQueryFilters()
            .Select(p => new { p.Id, p.Code, p.AccountingAccount })
            .ToDictionaryAsync(p => p.Id, p => (Code: p.Code, Account: p.AccountingAccount), ct);

        AccountingCustomer Customer(Guid? patientId, Guid? payerId, string? billedTo)
        {
            var patient = patientId is { } pid && patients.TryGetValue(pid, out var pt) ? pt : default;
            var payer = payerId is { } yid && payers.TryGetValue(yid, out var py) ? py : default;
            return new AccountingCustomer(patient.Record, patient.Name ?? billedTo, payer.Code, payer.Account);
        }

        SalesDocumentSource Document(DocumentRow d, SalesDocumentSource? original) =>
            new(d.SiteId, SiteCode(d.SiteId), d.Kind, d.Number, d.IssueDate, Customer(d.PatientId, d.PayerId, d.BilledToName),
                d.ClaimStatus != ClaimStatus.NotApplicable && d.PayerId is not null, d.Total, d.Vat, d.PayerShare,
                lines.GetValueOrDefault(d.Id, []), original);

        var originalSources = originals.ToDictionary(o => o.Id, o => Document(o, null));

        return new SalesAccountingSources(
            [
                .. documents.Select(d => Document(d, d.CorrectsInvoiceId is { } oid
                                                         ? originalSources.GetValueOrDefault(oid)
                                                         : null))
            ],
            [
                .. payments.Select(p => new SalesPaymentSource(
                    p.SiteId, SiteCode(p.SiteId), p.Number, p.IssueDate, Customer(p.PatientId, p.PayerId, p.BilledToName),
                    p.ClaimStatus != ClaimStatus.NotApplicable && p.PayerId is not null,
                    p.Method, p.Status, p.Amount, p.PaymentDate,
                    p.Status == PaymentStatus.Bounced && p.BouncedAt is { } b ? Day(b) : null,
                    p.Status == PaymentStatus.Refunded && p.RefundedAt is { } r ? Day(r) : null,
                    p.Status == PaymentStatus.Cancelled && p.ModifiedAt is { } m ? Day(m) : null,
                    p.DepositId is { } did ? depositNumbers.GetValueOrDefault(did, "?") : null,
                    p.CashSessionId,
                    p.CashSessionId is { } sid && sessionDates.TryGetValue(sid, out var opened) ? Day(opened) : null,
                    p.Reference))
            ],
            [
                .. shortfallRows.Select(s => new PayerShortfallSource(
                    s.SiteId, SiteCode(s.SiteId), s.Number,
                    s.Settled ?? s.WrittenOn ?? Day(s.ModifiedAt ?? s.CreatedAt),
                    Customer(s.PatientId, s.PayerId, s.BilledToName),
                    Money.Round(s.PayerShareAmount - (s.PayerAcceptedAmount ?? 0m) - s.WrittenOff)))
                    .Where(s => s.Amount > 0m)
            ],
            [
                .. deposits.Select(d => new DepositSource(
                    d.SiteId, SiteCode(d.SiteId), d.Number, Customer(d.PatientId, null, null), d.Method, d.Amount,
                    Day(d.ReceivedAt), d.RefundedAmount, d.RefundedAt is { } r ? Day(r) : null, d.CashSessionId,
                    d.Reference))
            ],
            [
                .. sessions.Select(s => new CashSessionSource(
                    s.SiteId, SiteCode(s.SiteId), s.Id, s.Operator ?? string.Empty, Day(s.OpenedAt), Day(s.ClosedAt!.Value),
                    s.CashWithdrawals,
                    s.CountedCash is { } counted
                        ? Money.Round(counted - Money.Round(s.OpeningFloat + s.ExpectedCashReceipts - s.CashRefunds - s.CashWithdrawals))
                        : 0m))
            ]);
    }

    private sealed record DocumentRow(Guid Id, Guid SiteId, InvoiceKind Kind, string Number, DateOnly IssueDate,
                                      Guid? PatientId, Guid? PayerId, string BilledToName, ClaimStatus ClaimStatus,
                                      decimal Total, decimal Vat, decimal PayerShare, Guid? CorrectsInvoiceId);

    /// <summary>
    /// The lines of the invoices, with the department of the analysis each bills. The amounts are
    /// worked out as <c>InvoiceLine</c> works them out: they are not stored.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<SalesLineSource>>> LinesAsync(List<Guid> invoiceIds,
                                                                                    CancellationToken ct)
    {
        if (invoiceIds.Count == 0) return [];

        var rows = new List<(Guid InvoiceId, Guid? TestId, decimal UnitPrice, int Quantity, decimal Discount, bool Exempt, decimal Rate)>();
        foreach (var chunk in invoiceIds.Chunk(1000))
        {
            var part = await db.InvoiceLines.AsNoTracking()
                .Where(l => chunk.Contains(l.InvoiceId))
                .Select(l => new { l.InvoiceId, l.AnalysisTestId, l.UnitPrice, l.Quantity, l.AllocatedDiscount, l.IsVatExempt, l.VatRate })
                .ToListAsync(ct);
            rows.AddRange(part.Select(l => (l.InvoiceId, l.AnalysisTestId, l.UnitPrice, l.Quantity, l.AllocatedDiscount,
                                             l.IsVatExempt, l.VatRate)));
        }

        var testIds = rows.Where(r => r.TestId is not null).Select(r => r.TestId!.Value).Distinct().ToList();
        var departments = testIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await (from t in db.AnalysisTests.AsNoTracking().IgnoreQueryFilters()
                     join d in db.Departments.AsNoTracking().IgnoreQueryFilters() on t.DepartmentId equals d.Id
                     where testIds.Contains(t.Id)
                     select new { t.Id, d.Code })
                .ToDictionaryAsync(x => x.Id, x => x.Code, ct);

        return rows.GroupBy(r => r.InvoiceId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<SalesLineSource>)
            [
                .. g.Select(r =>
                {
                    var net = Money.Round(Money.Round(r.UnitPrice * r.Quantity) - r.Discount);
                    var vat = r.Exempt ? 0m : Money.Round(net * r.Rate);
                    return new SalesLineSource(r.TestId is { } id ? departments.GetValueOrDefault(id) : null,
                                               net, vat, r.Exempt, r.Rate);
                })
            ]);
    }

    public async Task<IReadOnlyList<PayrollMonthSource>> GetPayrollMonthsAsync(IReadOnlyCollection<Guid> siteIds,
                                                                               DateOnly from, DateOnly to,
                                                                               CancellationToken ct = default)
    {
        var ids = siteIds.ToList();
        var periods = (await db.PayrollPeriods.AsNoTracking()
                .Where(p => ids.Contains(p.SiteId) && p.Status == PayrollStatus.Closed
                            && p.Year >= from.Year && p.Year <= to.Year)
                .Select(p => new { p.Id, p.SiteId, p.Year, p.Month })
                .ToListAsync(ct))
            .Where(p =>
            {
                var last = new DateOnly(p.Year, p.Month, 1).AddMonths(1).AddDays(-1);
                return last >= from && last <= to;
            })
            .ToList();

        if (periods.Count == 0) return [];

        var periodIds = periods.Select(p => p.Id).ToList();
        var slips = await db.Payslips.AsNoTracking()
            .Where(s => periodIds.Contains(s.PayrollPeriodId))
            .Select(s => new
            {
                s.PayrollPeriodId,
                s.Amounts.Gross, s.Amounts.CnssEmployee, s.Amounts.AmoEmployee, s.Amounts.IncomeTax,
                s.Amounts.OtherDeductions, s.Amounts.NetPay, s.Amounts.CnssEmployer,
                s.Amounts.FamilyAllowanceEmployer, s.Amounts.TrainingTaxEmployer, s.Amounts.AmoEmployer,
                s.Variables.AdvanceDeduction
            })
            .ToListAsync(ct);

        var sites = await db.Sites.AsNoTracking().IgnoreQueryFilters()
            .Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code, ct);

        return
        [
            .. periods.Select(p =>
            {
                var own = slips.Where(s => s.PayrollPeriodId == p.Id).ToList();
                var advances = own.Sum(s => Math.Min(Math.Max(0m, s.AdvanceDeduction), s.OtherDeductions));
                return new PayrollMonthSource(
                    p.SiteId, sites.GetValueOrDefault(p.SiteId, string.Empty), p.Year, p.Month, own.Count,
                    own.Sum(s => s.Gross),
                    own.Sum(s => s.CnssEmployer + s.FamilyAllowanceEmployer + s.TrainingTaxEmployer + s.AmoEmployer),
                    own.Sum(s => s.CnssEmployee + s.AmoEmployee),
                    own.Sum(s => s.IncomeTax),
                    advances,
                    own.Sum(s => s.OtherDeductions) - advances,
                    own.Sum(s => s.NetPay));
            })
        ];
    }
}
