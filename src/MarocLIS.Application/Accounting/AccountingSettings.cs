using System.Text.RegularExpressions;
using MarocLIS.Domain.Billing;

namespace MarocLIS.Application.Accounting;

/// <summary>How the patients appear in the receivable accounts of the export.</summary>
public enum PatientThirdPartyMode
{
    /// <summary>One auxiliary account for all patients; the cash receipts are summed per till session.</summary>
    Global = 0,

    /// <summary>Each patient as a third party, by medical record number; every receipt is its own entry.</summary>
    PerPatient = 1
}

/// <summary>
/// The journals and CGNC accounts of the sales, receipts, payroll and depreciation export, held as
/// installation-wide database settings (<c>AppSetting</c>, keys under <see cref="Prefix"/>), like
/// <see cref="Inventory.PurchasingSettings"/> holds those of the purchases. A key absent or blank
/// falls back to the default here, so an installation exports before anyone opened the screen.
/// <para>
/// The defaults are the usual CGNC accounts of a Moroccan medical laboratory; every one of them
/// is to be confirmed with the laboratory's accountant, who may use sub-accounts (34211, 71241…).
/// </para>
/// </summary>
public sealed record AccountingSettings
{
    public const string Prefix = "Accounting.";

    public const string SalesJournalKey = "Accounting.Journal.Sales";
    public const string CashJournalKey = "Accounting.Journal.Cash";
    public const string BankJournalKey = "Accounting.Journal.Bank";
    public const string MiscJournalKey = "Accounting.Journal.Miscellaneous";
    public const string PayrollJournalKey = "Accounting.Journal.Payroll";
    public const string DepreciationJournalKey = "Accounting.Journal.Depreciation";

    public const string PatientReceivableKey = "Accounting.Account.PatientReceivable";
    public const string PatientModeKey = "Accounting.Patients.Mode";
    public const string PatientGlobalThirdPartyKey = "Accounting.Patients.GlobalThirdParty";
    public const string PayerReceivableKey = "Accounting.Account.PayerReceivable";
    public const string DefaultRevenueKey = "Accounting.Account.Revenue";
    public const string OtherRevenueKey = "Accounting.Account.Revenue.Other";
    public const string RevenueKeyPrefix = "Accounting.Account.Revenue.Department.";
    public const string VatCollectedKey = "Accounting.Account.VatCollected";
    public const string CustomerAdvancesKey = "Accounting.Account.CustomerAdvances";
    public const string CashAccountKey = "Accounting.Account.Cash";
    public const string CashKeyPrefix = "Accounting.Account.Cash.Site.";
    public const string ReceiptKeyPrefix = "Accounting.Account.Receipt.";
    public const string PayerSettlementKey = "Accounting.Account.PayerSettlement";
    public const string WriteOffKey = "Accounting.Account.WriteOff";
    public const string CashTransitKey = "Accounting.Account.CashTransit";
    public const string CashShortageKey = "Accounting.Account.CashShortage";
    public const string CashOverageKey = "Accounting.Account.CashOverage";
    public const string GrossSalariesKey = "Accounting.Account.Payroll.Gross";
    public const string EmployerChargesKey = "Accounting.Account.Payroll.EmployerCharges";
    public const string NetPayableKey = "Accounting.Account.Payroll.NetPayable";
    public const string SocialBodiesKey = "Accounting.Account.Payroll.SocialBodies";
    public const string IncomeTaxKey = "Accounting.Account.Payroll.IncomeTax";
    public const string StaffAdvancesKey = "Accounting.Account.Payroll.Advances";
    public const string OtherDeductionsKey = "Accounting.Account.Payroll.OtherDeductions";

    /// <summary>The non-cash methods a patient pays with, each with its own treasury account.</summary>
    public static readonly IReadOnlyList<PaymentMethod> ReceiptMethods =
    [
        PaymentMethod.Cheque, PaymentMethod.Card, PaymentMethod.BankTransfer,
        PaymentMethod.BillOfExchange, PaymentMethod.MobilePayment
    ];

    public string SalesJournal { get; init; } = "VT";
    public string CashJournal { get; init; } = "CAI";
    public string BankJournal { get; init; } = "BQ";

    /// <summary>Write-offs, the transfer of a payer's refused share to the patient, till differences.</summary>
    public string MiscJournal { get; init; } = "OD";

    public string PayrollJournal { get; init; } = "OD";
    public string DepreciationJournal { get; init; } = "OD";

    /// <summary>CGNC 3421 Clients, for what the patients owe.</summary>
    public string PatientReceivable { get; init; } = "3421";

    public PatientThirdPartyMode PatientMode { get; init; } = PatientThirdPartyMode.Global;

    /// <summary>The auxiliary account of all patients when <see cref="PatientMode"/> is global.</summary>
    public string PatientGlobalThirdParty { get; init; } = "PATIENTS";

    /// <summary>
    /// CGNC 3421 Clients, for what the payers owe, when the payer has no account of its own
    /// (<c>Payer.AccountingAccount</c>).
    /// </summary>
    public string PayerReceivable { get; init; } = "3421";

    /// <summary>CGNC 7124 Ventes de services produits au Maroc, for a department with no account of its own.</summary>
    public string DefaultRevenue { get; init; } = "7124";

    /// <summary>Lines billing no analysis (a partial credit note with nothing to allocate it on, a certificate…).</summary>
    public string OtherRevenue { get; init; } = "7124";

    /// <summary>Revenue account per department code; a missing department uses <see cref="DefaultRevenue"/>.</summary>
    public IReadOnlyDictionary<string, string> RevenueByDepartment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>CGNC 4455 État, TVA facturée. Analyses are exempt (CGI art. 91): only taxable items reach it.</summary>
    public string VatCollected { get; init; } = "4455";

    /// <summary>CGNC 4421 Clients, avances et acomptes reçus, for the deposits held.</summary>
    public string CustomerAdvances { get; init; } = "4421";

    /// <summary>CGNC 5161 Caisses, for a site with no cash account of its own.</summary>
    public string CashAccount { get; init; } = "5161";

    /// <summary>Cash account per site code.</summary>
    public IReadOnlyDictionary<string, string> CashBySite { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Treasury account per non-cash method. CGNC 5141 Banques by default; a laboratory that books
    /// cheques and card batches until the bank credits them uses 5111 Chèques et valeurs à encaisser.
    /// </summary>
    public IReadOnlyDictionary<PaymentMethod, string> ReceiptAccounts { get; init; } =
        new Dictionary<PaymentMethod, string>();

    /// <summary>CGNC 5141 Banques, where the payers' transfers arrive.</summary>
    public string PayerSettlement { get; init; } = "5141";

    /// <summary>CGNC 6585 Créances devenues irrécouvrables.</summary>
    public string WriteOff { get; init; } = "6585";

    /// <summary>CGNC 5115 Virements de fonds, for cash taken out of the till for the bank.</summary>
    public string CashTransit { get; init; } = "5115";

    /// <summary>Till shortfall at closing. CGNC 6588 Autres charges non courantes.</summary>
    public string CashShortage { get; init; } = "6588";

    /// <summary>Till surplus at closing. CGNC 7588 Autres produits non courants.</summary>
    public string CashOverage { get; init; } = "7588";

    /// <summary>CGNC 6171 Appointements et salaires: the gross pay.</summary>
    public string GrossSalaries { get; init; } = "6171";

    /// <summary>CGNC 6174 Charges sociales: CNSS, allocations familiales, taxe de formation, AMO, employer's share.</summary>
    public string EmployerCharges { get; init; } = "6174";

    /// <summary>CGNC 4432 Rémunérations dues au personnel.</summary>
    public string NetPayable { get; init; } = "4432";

    /// <summary>CGNC 4441 Caisse nationale de sécurité sociale: both shares of CNSS and AMO, and the training tax it collects.</summary>
    public string SocialBodies { get; init; } = "4441";

    /// <summary>CGNC 4452 État, impôts, taxes et assimilés: the IR withheld.</summary>
    public string IncomeTax { get; init; } = "4452";

    /// <summary>CGNC 3431 Avances et acomptes au personnel: salary advances taken back.</summary>
    public string StaffAdvances { get; init; } = "3431";

    /// <summary>CGNC 4488 Divers créanciers: other deductions from net (loan, garnishment).</summary>
    public string OtherDeductions { get; init; } = "4488";

    public static AccountingSettings Default { get; } = new();

    public string RevenueFor(string? departmentCode) =>
        departmentCode is null
            ? OtherRevenue
            : RevenueByDepartment.TryGetValue(departmentCode, out var account) && !string.IsNullOrWhiteSpace(account)
                ? account
                : DefaultRevenue;

    public string CashFor(string siteCode) =>
        CashBySite.TryGetValue(siteCode, out var account) && !string.IsNullOrWhiteSpace(account)
            ? account
            : CashAccount;

    /// <summary>The treasury account a patient's payment by <paramref name="method"/> goes to.</summary>
    public string ReceiptAccountFor(PaymentMethod method, string siteCode) =>
        method == PaymentMethod.Cash
            ? CashFor(siteCode)
            : ReceiptAccounts.TryGetValue(method, out var account) && !string.IsNullOrWhiteSpace(account)
                ? account
                : "5141";

    public string ReceiptJournalFor(PaymentMethod method) =>
        method == PaymentMethod.Cash ? CashJournal : BankJournal;

    // ---- Validation ---------------------------------------------------------------------------

    private static readonly Regex AccountPattern = new(@"^[1-8][0-9]{3,19}$", RegexOptions.CultureInvariant);
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9_\-]{1,20}$", RegexOptions.CultureInvariant);

    /// <summary>A general account of the CGNC: classes 1 to 8, four digits at least, digits only.</summary>
    public static bool IsGeneralAccount(string? account) =>
        account is not null && AccountPattern.IsMatch(account);

    /// <summary>A journal code or an auxiliary (third-party) account: letters, digits, dash, underscore.</summary>
    public static bool IsCode(string? code) => code is not null && CodePattern.IsMatch(code);

    // ---- Storage ------------------------------------------------------------------------------

    public static AccountingSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        var d = Default;

        string Code(string key, string fallback) =>
            values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;

        var revenue = new Dictionary<string, string>(StringComparer.Ordinal);
        var cash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (key.StartsWith(RevenueKeyPrefix, StringComparison.Ordinal))
                revenue[key[RevenueKeyPrefix.Length..]] = value.Trim();
            else if (key.StartsWith(CashKeyPrefix, StringComparison.Ordinal))
                cash[key[CashKeyPrefix.Length..]] = value.Trim();
        }

        var receipts = new Dictionary<PaymentMethod, string>();
        foreach (var method in ReceiptMethods)
            receipts[method] = Code(ReceiptKeyPrefix + method, "5141");

        return new AccountingSettings
        {
            SalesJournal = Code(SalesJournalKey, d.SalesJournal),
            CashJournal = Code(CashJournalKey, d.CashJournal),
            BankJournal = Code(BankJournalKey, d.BankJournal),
            MiscJournal = Code(MiscJournalKey, d.MiscJournal),
            PayrollJournal = Code(PayrollJournalKey, d.PayrollJournal),
            DepreciationJournal = Code(DepreciationJournalKey, d.DepreciationJournal),
            PatientReceivable = Code(PatientReceivableKey, d.PatientReceivable),
            PatientMode = Enum.TryParse<PatientThirdPartyMode>(Code(PatientModeKey, d.PatientMode.ToString()), out var mode)
                          && Enum.IsDefined(mode)
                ? mode
                : d.PatientMode,
            PatientGlobalThirdParty = Code(PatientGlobalThirdPartyKey, d.PatientGlobalThirdParty),
            PayerReceivable = Code(PayerReceivableKey, d.PayerReceivable),
            DefaultRevenue = Code(DefaultRevenueKey, d.DefaultRevenue),
            OtherRevenue = Code(OtherRevenueKey, d.OtherRevenue),
            RevenueByDepartment = revenue,
            VatCollected = Code(VatCollectedKey, d.VatCollected),
            CustomerAdvances = Code(CustomerAdvancesKey, d.CustomerAdvances),
            CashAccount = Code(CashAccountKey, d.CashAccount),
            CashBySite = cash,
            ReceiptAccounts = receipts,
            PayerSettlement = Code(PayerSettlementKey, d.PayerSettlement),
            WriteOff = Code(WriteOffKey, d.WriteOff),
            CashTransit = Code(CashTransitKey, d.CashTransit),
            CashShortage = Code(CashShortageKey, d.CashShortage),
            CashOverage = Code(CashOverageKey, d.CashOverage),
            GrossSalaries = Code(GrossSalariesKey, d.GrossSalaries),
            EmployerCharges = Code(EmployerChargesKey, d.EmployerCharges),
            NetPayable = Code(NetPayableKey, d.NetPayable),
            SocialBodies = Code(SocialBodiesKey, d.SocialBodies),
            IncomeTax = Code(IncomeTaxKey, d.IncomeTax),
            StaffAdvances = Code(StaffAdvancesKey, d.StaffAdvances),
            OtherDeductions = Code(OtherDeductionsKey, d.OtherDeductions)
        };
    }

    /// <summary>
    /// Every setting as the text stored for it. A department or site without an account of its own
    /// is not written: it keeps following the default account.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SalesJournalKey] = SalesJournal,
            [CashJournalKey] = CashJournal,
            [BankJournalKey] = BankJournal,
            [MiscJournalKey] = MiscJournal,
            [PayrollJournalKey] = PayrollJournal,
            [DepreciationJournalKey] = DepreciationJournal,
            [PatientReceivableKey] = PatientReceivable,
            [PatientModeKey] = PatientMode.ToString(),
            [PatientGlobalThirdPartyKey] = PatientGlobalThirdParty,
            [PayerReceivableKey] = PayerReceivable,
            [DefaultRevenueKey] = DefaultRevenue,
            [OtherRevenueKey] = OtherRevenue,
            [VatCollectedKey] = VatCollected,
            [CustomerAdvancesKey] = CustomerAdvances,
            [CashAccountKey] = CashAccount,
            [PayerSettlementKey] = PayerSettlement,
            [WriteOffKey] = WriteOff,
            [CashTransitKey] = CashTransit,
            [CashShortageKey] = CashShortage,
            [CashOverageKey] = CashOverage,
            [GrossSalariesKey] = GrossSalaries,
            [EmployerChargesKey] = EmployerCharges,
            [NetPayableKey] = NetPayable,
            [SocialBodiesKey] = SocialBodies,
            [IncomeTaxKey] = IncomeTax,
            [StaffAdvancesKey] = StaffAdvances,
            [OtherDeductionsKey] = OtherDeductions
        };

        foreach (var method in ReceiptMethods)
            values[ReceiptKeyPrefix + method] = ReceiptAccountFor(method, string.Empty);
        foreach (var (department, account) in RevenueByDepartment)
            if (!string.IsNullOrWhiteSpace(account)) values[RevenueKeyPrefix + department] = account;
        foreach (var (site, account) in CashBySite)
            if (!string.IsNullOrWhiteSpace(account)) values[CashKeyPrefix + site] = account;

        return values;
    }

    /// <summary>Whether a stored key holds a journal or third-party code rather than a general account.</summary>
    public static bool IsCodeKey(string key) =>
        key.StartsWith("Accounting.Journal.", StringComparison.Ordinal)
        || key.StartsWith("Purchasing.Journal.", StringComparison.Ordinal)
        || key == PatientGlobalThirdPartyKey;
}
