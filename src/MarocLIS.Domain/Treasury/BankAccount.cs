using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;

namespace MarocLIS.Domain.Treasury;

/// <summary>
/// A bank account of the laboratory (compte bancaire): where the card batches, cheques, transfers
/// and payer settlements land and the supplier and payroll transfers leave from. Its statements are
/// imported and reconciled against what the application recorded.
/// <para>
/// The opening balance is the statement balance on <see cref="OpeningBalanceDate"/>; every imported
/// line dated after it moves the balance, so the statement balance on any day is the opening plus
/// those lines, without a second figure to keep in step.
/// </para>
/// </summary>
public sealed class BankAccount : AuditableAggregateRoot, ISiteScoped
{
    private BankAccount() { }

    public BankAccount(Guid siteId, string bankName, string label, string rib, string accountingAccount)
    {
        SiteId = siteId;
        BankName = bankName.Trim();
        Label = label.Trim();
        Rib = NormalizeRib(rib);
        AccountingAccount = accountingAccount.Trim();
        IsActive = true;
    }

    public Guid SiteId { get; set; }

    public string BankName { get; set; } = string.Empty;

    /// <summary>The operator's name for the account ("BMCE — compte courant").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Relevé d'identité bancaire: 24 digits, stored without spaces.</summary>
    public string Rib { get; private set; } = string.Empty;

    /// <summary>The account's CGNC sub-account in the accounting software (5141xxxx).</summary>
    public string AccountingAccount { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    /// <summary>Statement balance on <see cref="OpeningBalanceDate"/>, the day before the first imported line.</summary>
    public decimal OpeningBalanceMad { get; private set; }

    public DateOnly OpeningBalanceDate { get; private set; }

    /// <summary>
    /// The column mapping of the bank's CSV export, kept as text (JSON) so the next statement of
    /// the same bank imports without the mapping being set again.
    /// </summary>
    public string? CsvMapping { get; set; }

    public void ChangeRib(string rib) => Rib = NormalizeRib(rib);

    public void SetOpeningBalance(decimal amount, DateOnly on)
    {
        OpeningBalanceMad = Money.Round(amount);
        OpeningBalanceDate = on;
    }

    /// <summary>The bank's name and the account's label: the columns are this long.</summary>
    public const int NameMaxLength = 120;

    public Result Validate()
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(BankName))
            errors.Add(Error.Validation("BankAccount_BankNameRequired"));
        if (string.IsNullOrWhiteSpace(Label))
            errors.Add(Error.Validation("BankAccount_LabelRequired"));
        if (BankName.Length > NameMaxLength || Label.Length > NameMaxLength)
            errors.Add(Error.Validation("Treasury_TextTooLong", NameMaxLength));
        if (!Employee.IsValidRib(Rib))
            errors.Add(Error.Validation("BankAccount_RibInvalid"));
        if (!IsValidAccountingAccount(AccountingAccount))
            errors.Add(Error.Validation("BankAccount_AccountingAccountInvalid", AccountingAccount));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>
    /// A CGNC treasury account: digits only, 4 to 12 of them, in class 5 under 51 (5141 Banques,
    /// 5143 Trésorerie générale, 5146 Chèques postaux). The sub-account after the four-digit class
    /// is the laboratory's, as its accounting software numbers it.
    /// </summary>
    public static bool IsValidAccountingAccount(string? account) =>
        !string.IsNullOrWhiteSpace(account)
        && account.Trim() is { Length: >= 4 and <= 12 } trimmed
        && trimmed.All(char.IsAsciiDigit)
        && trimmed.StartsWith("51", StringComparison.Ordinal);

    public static string NormalizeRib(string? rib) =>
        new([.. (rib ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-')]);
}
