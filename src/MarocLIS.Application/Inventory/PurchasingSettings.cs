using System.Globalization;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// The purchasing parameters held as database settings (<c>AppSetting</c>, installation-wide):
/// the three-way-match tolerance, the late-payment rates of loi n° 69-21 and the accounting
/// accounts of the export. A key that is absent or unreadable falls back to the default here, so a
/// fresh installation works without anyone having opened the parameters.
/// </summary>
public sealed record PurchasingSettings
{
    public const string Prefix = "Purchasing.";

    public const string PriceTolerancePercentKey = "Purchasing.PriceTolerancePercent";
    public const string PriceToleranceMadKey = "Purchasing.PriceToleranceMad";
    public const string KeyRatePercentKey = "Purchasing.LatePenalty.KeyRatePercent";
    public const string PenaltyMarginPointsKey = "Purchasing.LatePenalty.MarginPoints";
    public const string FineFirstMonthPercentKey = "Purchasing.LateFine.FirstMonthPercent";
    public const string FineAdditionalMonthPercentKey = "Purchasing.LateFine.AdditionalMonthPercent";
    public const string PurchaseJournalKey = "Purchasing.Journal.Purchases";
    public const string BankJournalKey = "Purchasing.Journal.Bank";
    public const string CashJournalKey = "Purchasing.Journal.Cash";
    public const string SupplierAccountKey = "Purchasing.Account.Suppliers";
    public const string VatAccountKey = "Purchasing.Account.RecoverableVat";
    public const string AssetVatAccountKey = "Purchasing.Account.AssetVat";
    public const string BankAccountKey = "Purchasing.Account.Bank";
    public const string CashAccountKey = "Purchasing.Account.Cash";
    public const string ChargeAccountKeyPrefix = "Purchasing.Account.Charge.";

    /// <summary>Price difference accepted by the match, as a percentage of the ordered price. 0 = exact.</summary>
    public decimal PriceTolerancePercent { get; init; }

    /// <summary>Price difference accepted by the match, in dirhams per unit. 0 = exact.</summary>
    public decimal PriceToleranceMad { get; init; }

    /// <summary>
    /// Bank Al-Maghrib's key rate (taux directeur), in percent. Has to be updated when the bank
    /// changes it: the default is the rate known when this version was written.
    /// </summary>
    public decimal KeyRatePercent { get; init; } = 2.25m;

    /// <summary>Points added to the key rate for the penalty owed to the supplier.</summary>
    public decimal PenaltyMarginPoints { get; init; } = 7m;

    /// <summary>Fine owed to the Treasury for the first month of delay, in percent of the late amount.</summary>
    public decimal FineFirstMonthPercent { get; init; } = 2.25m;

    /// <summary>Fine for each further month or part of a month, in percent.</summary>
    public decimal FineAdditionalMonthPercent { get; init; } = 0.85m;

    public string PurchaseJournal { get; init; } = "ACH";
    public string BankJournal { get; init; } = "BQ";
    public string CashJournal { get; init; } = "CAI";

    /// <summary>CGNC 4411 Fournisseurs.</summary>
    public string SupplierAccount { get; init; } = "4411";

    /// <summary>CGNC 34552 État, TVA récupérable sur les charges.</summary>
    public string VatAccount { get; init; } = "34552";

    /// <summary>
    /// CGNC 34551 État, TVA récupérable sur les immobilisations: the VAT of an invoice linked to a
    /// fixed asset of the register, when deducted.
    /// </summary>
    public string AssetVatAccount { get; init; } = "34551";

    /// <summary>CGNC 5141 Banques.</summary>
    public string BankAccount { get; init; } = "5141";

    /// <summary>CGNC 5161 Caisses.</summary>
    public string CashAccount { get; init; } = "5161";

    /// <summary>Charge account per item category; missing kinds use <see cref="DefaultChargeAccount"/>.</summary>
    public IReadOnlyDictionary<StockItemKind, string> ChargeAccounts { get; init; } =
        new Dictionary<StockItemKind, string>();

    public decimal PenaltyRatePercent => KeyRatePercent + PenaltyMarginPoints;

    public PriceTolerance Tolerance => new(PriceTolerancePercent, PriceToleranceMad);

    /// <summary>
    /// CGNC 6122 Achats de matières et fournitures consommables for what the laboratory stocks and
    /// consumes; 6125 Achats non stockés de matières et fournitures for office supplies.
    /// </summary>
    public static string DefaultChargeAccount(StockItemKind kind) =>
        kind == StockItemKind.OfficeSupply ? "6125" : "6122";

    public string ChargeAccountFor(StockItemKind kind) =>
        ChargeAccounts.TryGetValue(kind, out var account) && !string.IsNullOrWhiteSpace(account)
            ? account
            : DefaultChargeAccount(kind);

    public static PurchasingSettings Default { get; } = new();

    public static PurchasingSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        var d = Default;

        decimal Number(string key, decimal fallback) =>
            values.TryGetValue(key, out var text) && text is not null
            && decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture,
                                out var value) && value >= 0m
                ? value
                : fallback;

        string Code(string key, string fallback) =>
            values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;

        var charges = new Dictionary<StockItemKind, string>();
        foreach (var kind in Enum.GetValues<StockItemKind>())
            charges[kind] = Code(ChargeAccountKeyPrefix + kind, DefaultChargeAccount(kind));

        return new PurchasingSettings
        {
            PriceTolerancePercent = Number(PriceTolerancePercentKey, d.PriceTolerancePercent),
            PriceToleranceMad = Number(PriceToleranceMadKey, d.PriceToleranceMad),
            KeyRatePercent = Number(KeyRatePercentKey, d.KeyRatePercent),
            PenaltyMarginPoints = Number(PenaltyMarginPointsKey, d.PenaltyMarginPoints),
            FineFirstMonthPercent = Number(FineFirstMonthPercentKey, d.FineFirstMonthPercent),
            FineAdditionalMonthPercent = Number(FineAdditionalMonthPercentKey, d.FineAdditionalMonthPercent),
            PurchaseJournal = Code(PurchaseJournalKey, d.PurchaseJournal),
            BankJournal = Code(BankJournalKey, d.BankJournal),
            CashJournal = Code(CashJournalKey, d.CashJournal),
            SupplierAccount = Code(SupplierAccountKey, d.SupplierAccount),
            VatAccount = Code(VatAccountKey, d.VatAccount),
            AssetVatAccount = Code(AssetVatAccountKey, d.AssetVatAccount),
            BankAccount = Code(BankAccountKey, d.BankAccount),
            CashAccount = Code(CashAccountKey, d.CashAccount),
            ChargeAccounts = charges
        };
    }

    /// <summary>Every setting as the text stored for it.</summary>
    public IReadOnlyDictionary<string, string> ToValues()
    {
        static string N(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PriceTolerancePercentKey] = N(PriceTolerancePercent),
            [PriceToleranceMadKey] = N(PriceToleranceMad),
            [KeyRatePercentKey] = N(KeyRatePercent),
            [PenaltyMarginPointsKey] = N(PenaltyMarginPoints),
            [FineFirstMonthPercentKey] = N(FineFirstMonthPercent),
            [FineAdditionalMonthPercentKey] = N(FineAdditionalMonthPercent),
            [PurchaseJournalKey] = PurchaseJournal,
            [BankJournalKey] = BankJournal,
            [CashJournalKey] = CashJournal,
            [SupplierAccountKey] = SupplierAccount,
            [VatAccountKey] = VatAccount,
            [AssetVatAccountKey] = AssetVatAccount,
            [BankAccountKey] = BankAccount,
            [CashAccountKey] = CashAccount
        };

        foreach (var kind in Enum.GetValues<StockItemKind>())
            values[ChargeAccountKeyPrefix + kind] = ChargeAccountFor(kind);

        return values;
    }
}
