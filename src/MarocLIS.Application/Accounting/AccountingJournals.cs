using System.Globalization;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Accounting;

/// <summary>The journals the export can hold, chosen on the export screen.</summary>
[Flags]
public enum AccountingJournals
{
    None = 0,

    /// <summary>Invoices and credit notes (VT).</summary>
    Sales = 1,

    /// <summary>Patient receipts, payer settlements, deposits, refunds, bounced cheques, cash to the bank (CAI, BQ).</summary>
    Receipts = 2,

    /// <summary>Write-offs, deposits applied to invoices, the payers' refused shares left to the patient (OD).</summary>
    Adjustments = 4,

    /// <summary>Closed payroll months (OD paie).</summary>
    Payroll = 8,

    /// <summary>Depreciation of the fixed assets, when the register is installed.</summary>
    Depreciation = 16,

    /// <summary>Supplier invoices and payments, as the purchases export writes them (ACH, BQ, CAI).</summary>
    Purchases = 32,

    All = Sales | Receipts | Adjustments | Payroll | Depreciation | Purchases
}

/// <summary>One accounting entry (écriture): lines that balance, under one date, journal and piece.</summary>
public sealed record JournalEntry(
    DateOnly Date,
    string Journal,
    string Piece,
    string Label,
    AccountingJournals Kind,
    string SiteCode,
    IReadOnlyList<AccountingEntry> Lines)
{
    public decimal Debit => Money.Round(Lines.Sum(l => l.Debit));
    public decimal Credit => Money.Round(Lines.Sum(l => l.Credit));
    public bool IsBalanced => Debit == Credit;
}

public enum AccountingProblemKind
{
    /// <summary>Debit and credit differ.</summary>
    Unbalanced = 0,

    /// <summary>A line has no account, or one that is not a CGNC general account.</summary>
    Unmapped = 1
}

/// <summary>Why an entry cannot be exported.</summary>
/// <param name="Detail">The account found, or the debit and credit totals.</param>
public sealed record AccountingProblem(
    AccountingProblemKind Kind,
    DateOnly Date,
    string Journal,
    string Piece,
    string Label,
    string Detail);

/// <summary>
/// The sales, receipts and adjustments journals of a medical laboratory under the CGNC, built from
/// what billing recorded. Pure: everything it needs comes in, so it is tested without a database.
/// <list type="bullet">
/// <item>Invoice: the patient (3421) and, for the share claimed from a payer, the payer (3421 or the
/// payer's own account) in debit; revenue (7124, per department) and any VAT collected (4455) in
/// credit. A credit note reverses the same accounts, off the patient's share first as billing
/// takes it (see <see cref="CreditOnPayer"/>).</item>
/// <item>Patient payment: the treasury account of the method (5161 per site for cash, 5141 or
/// 5111 otherwise) in debit, the patient in credit. Payer settlement: the bank in debit, the payer in
/// credit. A bounced cheque or a refund reverses the receipt on the day it happened.</item>
/// <item>Deposit: the treasury in debit, 4421 in credit when taken; 4421 to the patient when it
/// settles the invoice; 4421 to the treasury for what is handed back. A deposit carried over from a
/// credited invoice brought no money: the patient's account, left in credit by the credit note,
/// is debited instead of the treasury, in the adjustments.</item>
/// <item>Write-off of a payer's refused share: 6585 in debit, the payer in credit; the share left
/// to the patient instead moves from the payer's account to the patient's on the day of the
/// answer, and back on the day the claim is reopened.</item>
/// <item>Closed till: the cash sent to the bank from 5161 to 5115, and the difference at the
/// count to 6588 or 7588.</item>
/// </list>
/// </summary>
public static class SalesAccountingJournal
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static IReadOnlyList<JournalEntry> Build(SalesAccountingSources sources, AccountingSettings settings,
                                                   DateOnly from, DateOnly to, AccountingJournals journals)
    {
        var entries = new List<JournalEntry>();
        bool In(DateOnly? date) => date is { } d && d >= from && d <= to;

        if (journals.HasFlag(AccountingJournals.Sales))
            foreach (var document in sources.Documents.Where(d => In(d.IssueDate)))
                if (Document(document, settings) is { } entry) entries.Add(entry);

        var aggregated = new Dictionary<(Guid Session, DateOnly Date, PaymentMethod Method), List<SalesPaymentSource>>();

        foreach (var payment in sources.Payments)
        {
            if (payment.Status == PaymentStatus.Pending) continue;

            var receivedOn = payment.DepositNumber is null ? payment.PaymentDate : payment.InvoiceDate;
            if (In(receivedOn))
            {
                var kind = KindOfReceipt(payment);
                if (journals.HasFlag(kind))
                {
                    // Patients' payments taken in a till are summed per shift when the patients
                    // share one account: the cash book shows the shift's takings, not each ticket.
                    if (kind == AccountingJournals.Receipts
                        && settings.PatientMode == PatientThirdPartyMode.Global
                        && payment.Method is not PaymentMethod.ThirdPartyPayer
                        && payment.CashSessionId is { } session)
                    {
                        var key = (session, receivedOn, payment.Method);
                        if (!aggregated.TryGetValue(key, out var list)) aggregated[key] = list = [];
                        list.Add(payment);
                    }
                    else
                    {
                        entries.Add(Receipt(payment, receivedOn, settings));
                    }
                }
            }

            if (journals.HasFlag(AccountingJournals.Receipts) && payment.Method != PaymentMethod.WriteOff)
            {
                if (payment.Status == PaymentStatus.Bounced && In(payment.BouncedOn))
                    entries.Add(Reversal(payment, payment.BouncedOn!.Value, "Impayé", settings));
                if (payment.Status == PaymentStatus.Refunded && In(payment.RefundedOn))
                    entries.Add(Reversal(payment, payment.RefundedOn!.Value, "Rembt", settings,
                                         payment.RefundSiteCode));
            }

            if (journals.HasFlag(AccountingJournals.Adjustments) && payment.Method == PaymentMethod.WriteOff
                && payment.Status is PaymentStatus.Cancelled or PaymentStatus.Refunded
                && In(payment.CancelledOn ?? payment.RefundedOn))
            {
                var on = (payment.CancelledOn ?? payment.RefundedOn)!.Value;
                var label = $"Annul. perte fact. {payment.InvoiceNumber}";
                var (account, third) = Receivable(payment.Customer, payment.PayerClaimed, settings);
                entries.Add(Entry(on, settings.MiscJournal, payment.InvoiceNumber, label, AccountingJournals.Adjustments,
                                  payment.SiteCode,
                                  Line(account, third, payment.Amount, 0m),
                                  Line(settings.WriteOff, null, 0m, payment.Amount)));
            }
        }

        foreach (var ((session, date, method), payments) in aggregated.OrderBy(a => a.Key.Date)
                                                                      .ThenBy(a => a.Key.Session)
                                                                      .ThenBy(a => a.Key.Method))
        {
            var site = payments[0].SiteCode;
            var amount = Money.Round(payments.Sum(p => p.Amount));
            var piece = SessionPiece(session);
            var label = $"Encaissements {MethodLabel(method)} {date.ToString("dd/MM/yy", Fr)} {site}";
            entries.Add(Entry(date, settings.ReceiptJournalFor(method), piece, label, AccountingJournals.Receipts, site,
                              Line(settings.ReceiptAccountFor(method, site), null, amount, 0m),
                              Line(settings.PatientReceivable, settings.PatientGlobalThirdParty, 0m, amount)));
        }

        if (journals.HasFlag(AccountingJournals.Adjustments))
        {
            foreach (var shortfall in sources.Shortfalls.Where(s => In(s.Date) && s.Amount > 0m))
            {
                // Reopened claim: the share left to the patient goes back to the payer, as a
                // cancelled write-off does.
                var label = shortfall.Reversal
                    ? $"Annul. refus tiers payant fact. {shortfall.InvoiceNumber}"
                    : $"Refus tiers payant fact. {shortfall.InvoiceNumber}";
                var (patient, patientThird) = Patient(shortfall.Customer, settings);
                var (payer, payerThird) = Payer(shortfall.Customer, settings);
                var toPatient = shortfall.Reversal ? 0m : shortfall.Amount;
                var toPayer = shortfall.Reversal ? shortfall.Amount : 0m;
                entries.Add(Entry(shortfall.Date, settings.MiscJournal, shortfall.InvoiceNumber, label,
                                  AccountingJournals.Adjustments, shortfall.SiteCode,
                                  Line(patient, patientThird, toPatient, toPayer),
                                  Line(payer, payerThird, toPayer, toPatient)));
            }
        }

        if (journals.HasFlag(AccountingJournals.Adjustments))
        {
            foreach (var deposit in sources.Deposits.Where(d => d.CarriedFromInvoiceNumber is not null && In(d.ReceivedOn)))
            {
                var (patient, third) = Patient(deposit.Customer, settings);
                entries.Add(Entry(deposit.ReceivedOn, settings.MiscJournal, deposit.Number,
                                  $"Report règl. fact. {deposit.CarriedFromInvoiceNumber} en acompte {deposit.Number} {deposit.Customer.PatientName}",
                                  AccountingJournals.Adjustments, deposit.SiteCode,
                                  Line(patient, third, deposit.Amount, 0m),
                                  Line(settings.CustomerAdvances, third, 0m, deposit.Amount)));
            }
        }

        if (journals.HasFlag(AccountingJournals.Receipts))
        {
            foreach (var deposit in sources.Deposits)
            {
                var treasury = settings.ReceiptAccountFor(deposit.Method, deposit.SiteCode);
                var journal = settings.ReceiptJournalFor(deposit.Method);
                var (_, third) = Patient(deposit.Customer, settings);

                if (In(deposit.ReceivedOn) && deposit.CarriedFromInvoiceNumber is null)
                    entries.Add(Entry(deposit.ReceivedOn, journal, deposit.Number,
                                      $"Acompte {MethodLabel(deposit.Method)} {deposit.Number} {deposit.Customer.PatientName}",
                                      AccountingJournals.Receipts, deposit.SiteCode,
                                      Line(treasury, null, deposit.Amount, 0m),
                                      Line(settings.CustomerAdvances, third, 0m, deposit.Amount)));

                // Cash handed back from another site's till comes out of that site's cash account.
                var refundTreasury = deposit.Method == PaymentMethod.Cash && !string.IsNullOrEmpty(deposit.RefundSiteCode)
                    ? settings.ReceiptAccountFor(deposit.Method, deposit.RefundSiteCode)
                    : treasury;
                if (deposit.RefundedAmount > 0m && In(deposit.RefundedOn))
                    entries.Add(Entry(deposit.RefundedOn!.Value, journal, deposit.Number,
                                      $"Rembt acompte {deposit.Number} {deposit.Customer.PatientName}",
                                      AccountingJournals.Receipts, deposit.SiteCode,
                                      Line(settings.CustomerAdvances, third, deposit.RefundedAmount, 0m),
                                      Line(refundTreasury, null, 0m, deposit.RefundedAmount)));
            }

            foreach (var session in sources.CashSessions.Where(s => In(s.ClosedOn)))
            {
                var cash = settings.CashFor(session.SiteCode);
                var piece = SessionPiece(session.CashSessionId);

                if (session.Withdrawals > 0m)
                    entries.Add(Entry(session.ClosedOn, settings.CashJournal, piece,
                                      $"Versement banque caisse {session.OperatorName}",
                                      AccountingJournals.Receipts, session.SiteCode,
                                      Line(settings.CashTransit, null, session.Withdrawals, 0m),
                                      Line(cash, null, 0m, session.Withdrawals)));

                if (session.Variance < 0m)
                    entries.Add(Entry(session.ClosedOn, settings.CashJournal, piece,
                                      $"Manquant de caisse {session.OperatorName}",
                                      AccountingJournals.Receipts, session.SiteCode,
                                      Line(settings.CashShortage, null, -session.Variance, 0m),
                                      Line(cash, null, 0m, -session.Variance)));
                else if (session.Variance > 0m)
                    entries.Add(Entry(session.ClosedOn, settings.CashJournal, piece,
                                      $"Excédent de caisse {session.OperatorName}",
                                      AccountingJournals.Receipts, session.SiteCode,
                                      Line(cash, null, session.Variance, 0m),
                                      Line(settings.CashOverage, null, 0m, session.Variance)));
            }
        }

        return entries;
    }

    /// <summary>Which journal a settlement's own entry belongs to.</summary>
    private static AccountingJournals KindOfReceipt(SalesPaymentSource payment) =>
        payment.Method == PaymentMethod.WriteOff || payment.DepositNumber is not null
            ? AccountingJournals.Adjustments
            : AccountingJournals.Receipts;

    // ---- Invoices and credit notes -------------------------------------------------------------

    private static JournalEntry? Document(SalesDocumentSource document, AccountingSettings settings)
    {
        if (document.Total == 0m && document.Lines.All(l => l.Net == 0m)) return null;

        var credit = document.Kind == InvoiceKind.CreditNote;
        var basis = credit ? document.Original : document;

        var payerPart = 0m;
        var lossReversed = 0m;
        if (credit)
        {
            if (basis is { PayerClaimed: true, PayerShare: > 0m, Total: > 0m } && document.Total > 0m)
                (payerPart, lossReversed) = CreditOnPayer(document.Total, basis,
                                                          document.CreditState ?? CreditNoteState.None);
        }
        else if (document.PayerClaimed)
        {
            payerPart = document.PayerShare;
        }
        var patientPart = Money.Round(document.Total - payerPart - lossReversed);

        var lines = new List<AccountingEntry>();
        var label = $"{(credit ? "Avoir" : "Fact.")} {document.Number} {Name(document.Customer)}";

        // The receivables: in debit for an invoice, in credit for a credit note.
        if (payerPart != 0m)
        {
            var (account, third) = Payer(document.Customer, settings);
            lines.Add(Signed(account, third, payerPart, debit: !credit));
        }
        if (patientPart != 0m)
        {
            var (account, third) = Patient(document.Customer, settings);
            lines.Add(Signed(account, third, patientPart, debit: !credit));
        }

        // The payer's refused share was written off: the revenue it stood for is cancelled, so is the loss.
        if (lossReversed != 0m) lines.Add(Signed(settings.WriteOff, null, lossReversed, debit: false));

        foreach (var (account, amount) in Revenue(document, settings))
            lines.Add(Signed(account, null, amount, debit: credit));

        var vat = Money.Round(document.Vat);
        if (vat != 0m) lines.Add(Signed(settings.VatCollected, null, vat, debit: credit));

        return new JournalEntry(document.IssueDate, settings.SalesJournal, document.Number, label,
                                AccountingJournals.Sales, document.SiteCode,
                                [.. lines.Select(Label(label, document.Number, document.IssueDate, settings.SalesJournal))]);
    }

    /// <summary>
    /// How a credit note comes off the receivables, as billing takes it: off the patient's share
    /// first (the payer is still claimed and settles up to its whole share, see
    /// <c>Invoice.ComputePatientPayable</c>), the payer's share left to the patient included; then off
    /// what the payer still owes; then, once the payer's refusal was written off, off that loss.
    /// The credit notes on one invoice are taken in their order, so each comes off what the
    /// earlier ones left.
    /// </summary>
    /// <returns>The payer's part and the write-off reversed; the patient's part is the rest.</returns>
    internal static (decimal Payer, decimal Loss) CreditOnPayer(decimal amount, SalesDocumentSource original,
                                                               CreditNoteState state)
    {
        var patientShare = Math.Max(0m, Money.Round(original.Total - original.PayerShare + state.LeftToPatient));
        var payerOpen = Math.Max(0m, Money.Round(original.PayerShare - state.PayerSettled - state.WrittenOff
                                                 - state.LeftToPatient));
        var loss = Math.Max(0m, state.WrittenOff);

        (decimal Payer, decimal Loss) Cumulative(decimal credited)
        {
            var rest = Math.Max(0m, credited - Math.Min(credited, patientShare));
            var payer = Math.Min(rest, payerOpen);
            return (payer, Math.Min(rest - payer, loss));
        }

        var before = Cumulative(Math.Max(0m, state.PriorCredited));
        var after = Cumulative(Math.Max(0m, state.PriorCredited) + amount);
        return (Money.Round(after.Payer - before.Payer), Money.Round(after.Loss - before.Loss));
    }

    /// <summary>
    /// Revenue per account. A credit note line that bills no analysis (a partial credit note, the
    /// balance of an invoice) is spread over the accounts of the invoice it reverses, in proportion,
    /// so the departments' revenue is reduced where it was earned.
    /// </summary>
    internal static IReadOnlyList<(string Account, decimal Amount)> Revenue(SalesDocumentSource document,
                                                                          AccountingSettings settings)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        void Add(string account, decimal amount) =>
            totals[account] = (totals.TryGetValue(account, out var sum) ? sum : 0m) + amount;

        var unallocated = 0m;
        foreach (var line in document.Lines)
        {
            if (line.DepartmentCode is null && document.Kind == InvoiceKind.CreditNote && document.Original is not null)
                unallocated += line.Net;
            else
                Add(settings.RevenueFor(line.DepartmentCode), line.Net);
        }

        if (unallocated != 0m)
        {
            var weights = document.Original!.Lines
                .GroupBy(l => settings.RevenueFor(l.DepartmentCode), StringComparer.Ordinal)
                .Select(g => (Account: g.Key, Net: g.Sum(l => l.Net)))
                .Where(g => g.Net > 0m)
                .OrderBy(g => g.Account, StringComparer.Ordinal)
                .ToList();
            var basis = weights.Sum(w => w.Net);

            if (basis <= 0m)
            {
                Add(settings.OtherRevenue, unallocated);
            }
            else
            {
                var allocated = 0m;
                for (var i = 0; i < weights.Count; i++)
                {
                    var share = i == weights.Count - 1
                        ? Money.Round(unallocated - allocated)
                        : Money.Round(unallocated * weights[i].Net / basis);
                    allocated += share;
                    Add(weights[i].Account, share);
                }
            }
        }

        return [.. totals.Where(t => Money.Round(t.Value) != 0m)
                         .OrderBy(t => t.Key, StringComparer.Ordinal)
                         .Select(t => (t.Key, Money.Round(t.Value)))];
    }

    // ---- Settlements ---------------------------------------------------------------------------

    private static JournalEntry Receipt(SalesPaymentSource payment, DateOnly on, AccountingSettings settings)
    {
        var name = Name(payment.Customer);

        if (payment.DepositNumber is { } deposit)
        {
            var (account, third) = Patient(payment.Customer, settings);
            return Entry(on, settings.MiscJournal, payment.InvoiceNumber,
                         $"Imput. acompte {deposit} fact. {payment.InvoiceNumber}", AccountingJournals.Adjustments,
                         payment.SiteCode,
                         Line(settings.CustomerAdvances, third, payment.Amount, 0m),
                         Line(account, third, 0m, payment.Amount));
        }

        if (payment.Method == PaymentMethod.WriteOff)
        {
            var (account, third) = Receivable(payment.Customer, payment.PayerClaimed, settings);
            return Entry(on, settings.MiscJournal, payment.InvoiceNumber,
                         $"Perte fact. {payment.InvoiceNumber} {name}", AccountingJournals.Adjustments, payment.SiteCode,
                         Line(settings.WriteOff, null, payment.Amount, 0m),
                         Line(account, third, 0m, payment.Amount));
        }

        var (receivable, receivableThird) = payment.Method == PaymentMethod.ThirdPartyPayer
            ? Payer(payment.Customer, settings)
            : Patient(payment.Customer, settings);
        var (treasury, journal) = Treasury(payment, settings);
        var piece = string.IsNullOrWhiteSpace(payment.Reference) ? payment.InvoiceNumber : payment.Reference!.Trim();

        return Entry(on, journal, piece, $"Règl. {MethodLabel(payment.Method)} fact. {payment.InvoiceNumber} {name}",
                     AccountingJournals.Receipts, payment.SiteCode,
                     Line(treasury, null, payment.Amount, 0m),
                     Line(receivable, receivableThird, 0m, payment.Amount));
    }

    /// <summary>A bounced cheque or a refund: the receipt reversed on the day it happened.</summary>
    /// <param name="paidAtSite">For a refund: the site whose till paid it, when it is not the payment's.</param>
    private static JournalEntry Reversal(SalesPaymentSource payment, DateOnly on, string what, AccountingSettings settings,
                                         string? paidAtSite = null)
    {
        var (receivable, third) = payment.Method == PaymentMethod.ThirdPartyPayer
            ? Payer(payment.Customer, settings)
            : Patient(payment.Customer, settings);
        var (treasury, journal) = Treasury(payment, settings);
        if (payment.Method == PaymentMethod.Cash && !string.IsNullOrEmpty(paidAtSite))
            treasury = settings.CashFor(paidAtSite);

        return Entry(on, journal, payment.InvoiceNumber,
                     $"{what} {MethodLabel(payment.Method)} fact. {payment.InvoiceNumber} {Name(payment.Customer)}",
                     AccountingJournals.Receipts, payment.SiteCode,
                     Line(receivable, third, payment.Amount, 0m),
                     Line(treasury, null, 0m, payment.Amount));
    }

    private static (string Account, string Journal) Treasury(SalesPaymentSource payment, AccountingSettings settings) =>
        payment.Method == PaymentMethod.ThirdPartyPayer
            ? (settings.PayerSettlement, settings.BankJournal)
            : (settings.ReceiptAccountFor(payment.Method, payment.SiteCode), settings.ReceiptJournalFor(payment.Method));

    // ---- Accounts ------------------------------------------------------------------------------

    internal static (string Account, string? ThirdParty) Patient(AccountingCustomer customer, AccountingSettings settings) =>
        settings.PatientMode == PatientThirdPartyMode.PerPatient && !string.IsNullOrWhiteSpace(customer.PatientRecordNumber)
            ? (settings.PatientReceivable, customer.PatientRecordNumber!.Trim())
            : (settings.PatientReceivable, settings.PatientGlobalThirdParty);

    /// <summary>
    /// The payer's receivable. A payer account made of digits is a general sub-account (34211),
    /// used with the payer's code as third party; any other text is the payer's auxiliary account
    /// under the default receivable, as for the suppliers.
    /// </summary>
    internal static (string Account, string? ThirdParty) Payer(AccountingCustomer customer, AccountingSettings settings)
    {
        var own = customer.PayerAccount?.Trim();
        if (string.IsNullOrEmpty(own)) return (settings.PayerReceivable, customer.PayerCode);
        return own.All(char.IsAsciiDigit) ? (own, customer.PayerCode) : (settings.PayerReceivable, own);
    }

    private static (string Account, string? ThirdParty) Receivable(AccountingCustomer customer, bool payerClaimed,
                                                                   AccountingSettings settings) =>
        payerClaimed && customer.PayerCode is not null ? Payer(customer, settings) : Patient(customer, settings);

    // ---- Helpers -------------------------------------------------------------------------------

    public static string SessionPiece(Guid session) => "CS" + session.ToString("N")[..8].ToUpperInvariant();

    private static string Name(AccountingCustomer customer) =>
        customer.PatientName ?? customer.PayerCode ?? string.Empty;

    internal static string MethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "esp.",
        PaymentMethod.Cheque => "chq",
        PaymentMethod.Card => "CB",
        PaymentMethod.BankTransfer => "vir.",
        PaymentMethod.BillOfExchange => "effet",
        PaymentMethod.MobilePayment => "mobile",
        PaymentMethod.ThirdPartyPayer => "tiers payant",
        PaymentMethod.WriteOff => "perte",
        _ => string.Empty
    };

    private static AccountingEntry Line(string account, string? thirdParty, decimal debit, decimal credit) =>
        new(default, string.Empty, account, thirdParty, string.Empty, string.Empty, Money.Round(debit), Money.Round(credit));

    private static AccountingEntry Signed(string account, string? thirdParty, decimal amount, bool debit)
    {
        // A negative amount (a discount line) goes to the other side rather than as a negative figure.
        var value = Money.Round(amount);
        if (value < 0m) (value, debit) = (-value, !debit);
        return Line(account, thirdParty, debit ? value : 0m, debit ? 0m : value);
    }

    private static Func<AccountingEntry, AccountingEntry> Label(string label, string piece, DateOnly date, string journal) =>
        line => line with { Date = date, Journal = journal, Label = label, Piece = piece };

    internal static JournalEntry Entry(DateOnly date, string journal, string piece, string label, AccountingJournals kind,
                                       string siteCode, params AccountingEntry[] lines) =>
        new(date, journal, piece, label, kind, siteCode,
            [.. lines.Where(l => l.Debit != 0m || l.Credit != 0m).Select(Label(label, piece, date, journal))]);
}

/// <summary>
/// The payroll journal of a closed month, from the payslips as stored: the gross pay (6171) and the
/// employer's contributions (6174) in debit; the CNSS (4441) for both shares, the income tax withheld
/// (4452), the advances taken back (3431), the other deductions (4488) and the net pay owed to the
/// staff (4432) in credit. The payslip's net is the gross less exactly these deductions, so the
/// entry balances to the dirham.
/// </summary>
public static class PayrollAccountingJournal
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static IReadOnlyList<JournalEntry> Build(IEnumerable<PayrollMonthSource> months, AccountingSettings settings) =>
    [
        .. months.Where(m => m.PayslipCount > 0)
                 .OrderBy(m => m.Year).ThenBy(m => m.Month).ThenBy(m => m.SiteCode, StringComparer.Ordinal)
                 .Select(m =>
                 {
                     var piece = $"PAIE{m.Year % 100:00}{m.Month:00}{(m.SiteCode.Length > 0 ? "-" + m.SiteCode : string.Empty)}";
                     if (piece.Length > 13) piece = piece[..13];
                     var label = $"Paie {new DateOnly(m.Year, m.Month, 1).ToString("MMMM yyyy", Fr)} {m.SiteCode}";
                     return SalesAccountingJournal.Entry(
                         m.LastDay, settings.PayrollJournal, piece, label, AccountingJournals.Payroll, m.SiteCode,
                         new AccountingEntry(default, "", settings.GrossSalaries, null, "", "", Money.Round(m.Gross), 0m),
                         new AccountingEntry(default, "", settings.EmployerCharges, null, "", "", Money.Round(m.EmployerCharges), 0m),
                         new AccountingEntry(default, "", settings.SocialBodies, null, "", "", 0m,
                                             Money.Round(m.EmployeeContributions + m.EmployerCharges)),
                         new AccountingEntry(default, "", settings.IncomeTax, null, "", "", 0m, Money.Round(m.IncomeTax)),
                         new AccountingEntry(default, "", settings.StaffAdvances, null, "", "", 0m, Money.Round(m.Advances)),
                         new AccountingEntry(default, "", settings.OtherDeductions, null, "", "", 0m, Money.Round(m.OtherDeductions)),
                         new AccountingEntry(default, "", settings.NetPayable, null, "", "", 0m, Money.Round(m.NetPay)));
                 })
    ];
}

/// <summary>Depreciation entries of the fixed-assets register: the charge in debit, the depreciation in credit.</summary>
public static class DepreciationAccountingJournal
{
    public static IReadOnlyList<JournalEntry> Build(IEnumerable<(string SiteCode, DepreciationEntry Entry)> entries,
                                                   AccountingSettings settings) =>
    [
        .. entries.Where(e => e.Entry.Amount != 0m)
                  .OrderBy(e => e.Entry.Date).ThenBy(e => e.Entry.AssetCode, StringComparer.Ordinal)
                  .Select(e => SalesAccountingJournal.Entry(
                      e.Entry.Date, settings.DepreciationJournal, e.Entry.AssetCode,
                      string.IsNullOrWhiteSpace(e.Entry.Label) ? $"Dotation {e.Entry.AssetCode}" : e.Entry.Label,
                      AccountingJournals.Depreciation, e.SiteCode,
                      new AccountingEntry(default, "", e.Entry.ChargeAccount?.Trim() ?? "", null, "", "", Money.Round(e.Entry.Amount), 0m),
                      new AccountingEntry(default, "", e.Entry.DepreciationAccount?.Trim() ?? "", null, "", "", 0m, Money.Round(e.Entry.Amount))))
    ];
}

/// <summary>The checks every entry passes before anything is written to the file.</summary>
public static class AccountingExportCheck
{
    /// <summary>
    /// The supplier entries of <see cref="SupplierAccountingExport"/> grouped back into entries: its
    /// lines come one invoice or payment after the other, each under its own date, journal and piece.
    /// </summary>
    public static IReadOnlyList<JournalEntry> Group(IEnumerable<AccountingEntry> lines, string siteCode)
    {
        var entries = new List<JournalEntry>();
        var current = new List<AccountingEntry>();

        void Flush()
        {
            if (current.Count == 0) return;
            var first = current[0];
            entries.Add(new JournalEntry(first.Date, first.Journal, first.Piece, first.Label,
                                         AccountingJournals.Purchases, siteCode, [.. current]));
            current = [];
        }

        foreach (var line in lines)
        {
            if (current.Count > 0)
            {
                var first = current[0];
                if (first.Date != line.Date || first.Journal != line.Journal || first.Piece != line.Piece
                    || first.Label != line.Label)
                    Flush();
            }
            current.Add(line);
        }
        Flush();

        return entries;
    }

    public static IReadOnlyList<AccountingProblem> Check(IEnumerable<JournalEntry> entries)
    {
        var problems = new List<AccountingProblem>();

        foreach (var entry in entries)
        {
            if (!AccountingSettings.IsCode(entry.Journal))
                problems.Add(new AccountingProblem(AccountingProblemKind.Unmapped, entry.Date, entry.Journal,
                                                   entry.Piece, entry.Label, $"journal « {entry.Journal} »"));

            foreach (var line in entry.Lines.Where(l => !AccountingSettings.IsGeneralAccount(l.Account)))
                problems.Add(new AccountingProblem(AccountingProblemKind.Unmapped, entry.Date, entry.Journal,
                                                   entry.Piece, entry.Label,
                                                   string.IsNullOrWhiteSpace(line.Account) ? "—" : line.Account));

            if (!entry.IsBalanced)
                problems.Add(new AccountingProblem(AccountingProblemKind.Unbalanced, entry.Date, entry.Journal,
                                                   entry.Piece, entry.Label,
                                                   $"{entry.Debit.ToString("0.00", CultureInfo.InvariantCulture)} / " +
                                                   entry.Credit.ToString("0.00", CultureInfo.InvariantCulture)));
        }

        return problems;
    }
}
