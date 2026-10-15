using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Treasury;

/// <summary>
/// One line of a bank statement, imported from the bank's CSV export. A credit is positive, a debit
/// negative. Imported lines are not edited: a wrong import is corrected by the bank's next
/// statement, and what the bank said is the reference the reconciliation is made against.
/// </summary>
public sealed class BankStatementLine : AuditableAggregateRoot, ISiteScoped
{
    private BankStatementLine() { }

    public BankStatementLine(Guid bankAccountId, Guid siteId, Guid importId, DateOnly operationDate,
                             DateOnly? valueDate, string label, string? reference, decimal amountMad,
                             string fingerprint)
    {
        BankAccountId = bankAccountId;
        SiteId = siteId;
        ImportId = importId;
        OperationDate = operationDate;
        ValueDate = valueDate;
        Label = label.Trim();
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        AmountMad = Money.Round(amountMad);
        Fingerprint = fingerprint;
    }

    public Guid BankAccountId { get; private set; }
    public Guid SiteId { get; set; }

    /// <summary>The import the line came in with: one file, one identifier.</summary>
    public Guid ImportId { get; private set; }

    public string? SourceFileName { get; set; }

    public DateOnly OperationDate { get; private set; }
    public DateOnly? ValueDate { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public string? Reference { get; private set; }

    /// <summary>Signed: a credit to the account is positive, a debit negative.</summary>
    public decimal AmountMad { get; private set; }

    /// <summary>
    /// What identifies the line across imports: see <see cref="ComputeFingerprint"/>. The same
    /// statement imported twice, or two statements that overlap, bring no line in twice.
    /// </summary>
    public string Fingerprint { get; private set; } = string.Empty;

    /// <summary>
    /// The account, date, amount, label and reference of a line, and its rank among the identical
    /// lines of the file (two card commissions of the same amount on the same day are two lines).
    /// Hashed so the column stays short whatever the bank puts in its labels.
    /// </summary>
    public static string ComputeFingerprint(Guid bankAccountId, DateOnly date, decimal amount, string label,
                                            string? reference, int occurrence)
    {
        static string Compact(string? text) =>
            new([.. (text ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit)]);

        var key = string.Join('|',
            bankAccountId.ToString("N"),
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            Money.Round(amount).ToString("0.00", CultureInfo.InvariantCulture),
            Compact(label), Compact(reference),
            occurrence.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
    }
}

/// <summary>
/// The kinds of movement the application records that reach the bank, and what a statement line can
/// be reconciled with.
/// </summary>
public enum TreasuryMovementKind
{
    /// <summary>A patient's cheque, transfer or bill of exchange (règlement patient).</summary>
    PatientPayment = 0,

    /// <summary>A deposit (acompte) taken by cheque, transfer or bill.</summary>
    PatientDeposit = 1,

    /// <summary>The day's card payments, which the acquirer credits as one batch (remise carte).</summary>
    CardBatch = 2,

    /// <summary>A payer's settlement, one remittance however many invoices it settles.</summary>
    PayerSettlement = 3,

    /// <summary>A supplier payment by transfer, cheque or bill.</summary>
    SupplierPayment = 4,

    /// <summary>Cash taken out of a till during a shift and banked (versement d'espèces).</summary>
    CashDeposit = 5,

    /// <summary>A closed month's net salaries, transferred together.</summary>
    Payroll = 6,

    /// <summary>A patient's cheque returned unpaid: the bank takes the amount back.</summary>
    ReturnedCheque = 7,

    /// <summary>
    /// No movement of the application: a bank charge, interest, a tax the bank collected. The line
    /// is justified with a comment instead, for the accounting entry the accountant makes.
    /// </summary>
    Justified = 9
}

/// <summary>
/// A statement line reconciled with one recorded movement (pointage). A line settled by several
/// movements, a cheque remittance of five cheques, has one match per movement. Unmatching keeps the
/// row, closed, so who reconciled what and when stays readable.
/// </summary>
public sealed class BankMatch : AuditableAggregateRoot, ISiteScoped
{
    private BankMatch() { }

    public BankMatch(Guid siteId, Guid bankAccountId, Guid bankStatementLineId, TreasuryMovementKind kind,
                     string sourceKey, DateOnly movementDate, decimal amountMad, bool isAutomatic,
                     string? comment, DateTimeOffset matchedAt, Guid matchedByUserId)
    {
        SiteId = siteId;
        BankAccountId = bankAccountId;
        BankStatementLineId = bankStatementLineId;
        Kind = kind;
        SourceKey = sourceKey;
        MovementDate = movementDate;
        AmountMad = Money.Round(amountMad);
        IsAutomatic = isAutomatic;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        MatchedAt = matchedAt;
        MatchedByUserId = matchedByUserId;
    }

    public Guid SiteId { get; set; }
    public Guid BankAccountId { get; private set; }
    public Guid BankStatementLineId { get; private set; }

    public TreasuryMovementKind Kind { get; private set; }

    /// <summary>
    /// What identifies the movement within its kind: the payment's id, the site and day of a card
    /// batch, the site, payer, day and remittance of a settlement, the payroll month.
    /// </summary>
    public string SourceKey { get; private set; } = string.Empty;

    public DateOnly MovementDate { get; private set; }

    /// <summary>Signed as the statement line: what the movement brought in or took out.</summary>
    public decimal AmountMad { get; private set; }

    public bool IsAutomatic { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset MatchedAt { get; private set; }
    public Guid MatchedByUserId { get; private set; }

    public DateTimeOffset? UnmatchedAt { get; private set; }
    public Guid? UnmatchedByUserId { get; private set; }

    public bool IsActive => UnmatchedAt is null;

    public Result Unmatch(DateTimeOffset at, Guid byUserId)
    {
        if (!IsActive) return Result.Failure(Error.Conflict("BankMatch_AlreadyUnmatched"));

        UnmatchedAt = at;
        UnmatchedByUserId = byUserId;
        return Result.Success();
    }
}
