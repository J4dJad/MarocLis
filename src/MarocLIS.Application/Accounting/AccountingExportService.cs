using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Accounting;

public sealed record AccountingSiteOption(Guid Id, string Code, string Name);

public sealed record AccountingDepartmentOption(string Code, string Name);

/// <summary>What the export screen asks for.</summary>
public sealed record AccountingExportRequest(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<Guid> SiteIds,
    AccountingJournals Journals,
    AccountingExportLayout Layout);

/// <summary>The entries of a period, and why some of them cannot be exported.</summary>
/// <param name="DepreciationAvailable">False when no fixed-assets register is installed to give the depreciation.</param>
public sealed record AccountingExportPreview(
    IReadOnlyList<JournalEntry> Entries,
    IReadOnlyList<AccountingProblem> Problems,
    bool DepreciationAvailable)
{
    public bool CanExport => Problems.Count == 0 && Entries.Count > 0;
}

public enum AccountingSettingKind
{
    Account = 0,
    Code = 1,
    PatientMode = 2
}

/// <summary>One line of the accounting settings screen.</summary>
/// <param name="LabelKey">Resource key of the label; <paramref name="Argument"/> fills its {0}.</param>
/// <param name="ArgumentKey">When set, the argument is itself a resource key.</param>
/// <param name="AllowBlank">A department, site or method line: blank follows the default account.</param>
public sealed record AccountingSettingRow(
    string Key,
    string Section,
    string LabelKey,
    string? Argument,
    bool ArgumentKey,
    AccountingSettingKind Kind,
    string Value,
    bool AllowBlank);

/// <summary>
/// The accounting export of everything the laboratory records: sales, credit notes, payer claims
/// and settlements, patient receipts and deposits, the till, payroll, depreciation and the supplier
/// entries, in one file for Sage or any accounting software. The laboratory keeps its accounting
/// software or firm: there is no general ledger here, only clean entries for it.
/// <para>
/// Nothing is written unless every entry balances and every line has a CGNC account: the refusal
/// lists the entries to correct (an account to set, a payer's account typed wrong).
/// </para>
/// </summary>
public sealed class AccountingExportService(
    IAccountingQueries queries,
    ISupplierInvoiceQueries supplierQueries,
    IEnumerable<IDepreciationEntrySource> depreciationSources,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser)
{
    private const string Right = Permissions.Accounting.Export;

    public async Task<Result<IReadOnlyList<AccountingSiteOption>>> GetSitesAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Right);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<AccountingSiteOption>>(permitted.FirstError);

        return Result.Success(await queries.GetSitesAsync(Accessible(), ct));
    }

    private IReadOnlyList<Guid> Accessible() =>
        currentUser.AccessibleSiteIds.Count > 0 ? currentUser.AccessibleSiteIds : [currentUser.SiteId];

    private Result<IReadOnlyList<Guid>> CheckSites(IReadOnlyList<Guid> siteIds)
    {
        if (siteIds.Count == 0) return Result.Failure<IReadOnlyList<Guid>>(Error.Validation("Accounting_NoSite"));

        var accessible = Accessible();
        if (siteIds.Any(s => !accessible.Contains(s)))
            return Result.Failure<IReadOnlyList<Guid>>(Error.Forbidden("Accounting_SiteNotAccessible"));

        return Result.Success<IReadOnlyList<Guid>>([.. siteIds.Distinct()]);
    }

    // ---- Export ------------------------------------------------------------------------------------

    public async Task<Result<AccountingExportPreview>> PrepareAsync(AccountingExportRequest request,
                                                                    CancellationToken ct = default)
    {
        var permitted = guard.Require(Right);
        if (permitted.IsFailure) return Result.Failure<AccountingExportPreview>(permitted.FirstError);
        if (request.From > request.To)
            return Result.Failure<AccountingExportPreview>(Error.Validation("Purchasing_PeriodReversed"));
        if (request.Journals == AccountingJournals.None)
            return Result.Failure<AccountingExportPreview>(Error.Validation("Accounting_NoJournal"));

        var sites = CheckSites(request.SiteIds);
        if (sites.IsFailure) return Result.Failure<AccountingExportPreview>(sites.FirstError);

        var siteCodes = (await queries.GetSitesAsync(sites.Value, ct)).ToDictionary(s => s.Id, s => s.Code);
        var settings = AccountingSettings.FromValues(await queries.GetSettingValuesAsync(AccountingSettings.Prefix, ct));
        var (from, to, journals) = (request.From, request.To, request.Journals);

        var entries = new List<JournalEntry>();

        if ((journals & (AccountingJournals.Sales | AccountingJournals.Receipts | AccountingJournals.Adjustments)) != 0)
        {
            var sales = await queries.GetSalesSourcesAsync(sites.Value, from, to, ct);
            entries.AddRange(SalesAccountingJournal.Build(sales, settings, from, to, journals));
        }

        if (journals.HasFlag(AccountingJournals.Payroll))
            entries.AddRange(PayrollAccountingJournal.Build(await queries.GetPayrollMonthsAsync(sites.Value, from, to, ct),
                                                            settings));

        var depreciation = depreciationSources.ToList();
        if (journals.HasFlag(AccountingJournals.Depreciation) && depreciation.Count > 0)
        {
            var lines = new List<(string, DepreciationEntry)>();
            foreach (var siteId in sites.Value)
                foreach (var source in depreciation)
                    lines.AddRange((await source.GetEntriesAsync(siteId, from, to, ct))
                                   .Where(e => e.Date >= from && e.Date <= to)
                                   .Select(e => (siteCodes.GetValueOrDefault(siteId, string.Empty), e)));
            entries.AddRange(DepreciationAccountingJournal.Build(lines, settings));
        }

        if (journals.HasFlag(AccountingJournals.Purchases))
        {
            var purchasing = PurchasingSettings.FromValues(
                await supplierQueries.GetSettingValuesAsync(PurchasingSettings.Prefix, ct));
            foreach (var siteId in sites.Value)
            {
                var invoices = await supplierQueries.GetAccountingInvoicesAsync(siteId, from, to, ct);
                var payments = await supplierQueries.GetAccountingPaymentsAsync(siteId, from, to, ct);
                entries.AddRange(AccountingExportCheck.Group(
                    SupplierAccountingExport.BuildEntries(invoices, payments, purchasing),
                    siteCodes.GetValueOrDefault(siteId, string.Empty)));
            }
        }

        IReadOnlyList<JournalEntry> ordered =
        [
            .. entries.Where(e => e.Lines.Count > 0)
                      .OrderBy(e => e.Journal, StringComparer.Ordinal)
                      .ThenBy(e => e.Date)
                      .ThenBy(e => e.Piece, StringComparer.Ordinal)
        ];

        return Result.Success(new AccountingExportPreview(ordered, AccountingExportCheck.Check(ordered),
                                                          depreciation.Count > 0));
    }

    /// <summary>
    /// The file, refused while any entry is unbalanced or has a line without a CGNC account: an
    /// accounting import that takes half a file, or a wrong account, costs the accountant more than
    /// the export saves.
    /// </summary>
    public async Task<Result<byte[]>> ExportAsync(AccountingExportRequest request, CancellationToken ct = default)
    {
        var preview = await PrepareAsync(request, ct);
        if (preview.IsFailure) return Result.Failure<byte[]>(preview.FirstError);

        if (preview.Value.Problems.Count > 0)
            return Result.Failure<byte[]>(Error.Validation("Accounting_ExportRefused", preview.Value.Problems.Count));
        if (preview.Value.Entries.Count == 0)
            return Result.Failure<byte[]>(Error.Validation("Purchasing_NothingToExport"));

        return Result.Success(SupplierAccountingExport.ToCsv(preview.Value.Entries.SelectMany(e => e.Lines),
                                                             request.Layout));
    }

    // ---- VAT ---------------------------------------------------------------------------------------

    public async Task<Result<VatStatement>> GetVatStatementAsync(DateOnly from, DateOnly to, IReadOnlyList<Guid> siteIds,
                                                                 CancellationToken ct = default)
    {
        var permitted = guard.Require(Right);
        if (permitted.IsFailure) return Result.Failure<VatStatement>(permitted.FirstError);
        if (from > to) return Result.Failure<VatStatement>(Error.Validation("Purchasing_PeriodReversed"));

        var sites = CheckSites(siteIds);
        if (sites.IsFailure) return Result.Failure<VatStatement>(sites.FirstError);

        var sales = await queries.GetSalesSourcesAsync(sites.Value, from, to, ct);
        var purchases = new List<AccountingInvoiceSource>();
        foreach (var siteId in sites.Value)
            purchases.AddRange(await supplierQueries.GetAccountingInvoicesAsync(siteId, from, to, ct));

        return Result.Success(VatStatement.Build(from, to, sales.Documents, purchases));
    }

    // ---- Settings ----------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<AccountingSettingRow>>> GetSettingsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Right);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<AccountingSettingRow>>(permitted.FirstError);

        var settings = AccountingSettings.FromValues(await queries.GetSettingValuesAsync(AccountingSettings.Prefix, ct));
        var purchasing = PurchasingSettings.FromValues(
            await supplierQueries.GetSettingValuesAsync(PurchasingSettings.Prefix, ct));
        var departments = await queries.GetDepartmentsAsync(ct);
        var sites = await queries.GetSitesAsync(Accessible(), ct);

        return Result.Success(Sheet(settings, purchasing, departments, sites));
    }

    /// <summary>Every setting of the screen, in the order it shows them.</summary>
    public static IReadOnlyList<AccountingSettingRow> Sheet(AccountingSettings s, PurchasingSettings p,
                                                            IReadOnlyList<AccountingDepartmentOption> departments,
                                                            IReadOnlyList<AccountingSiteOption> sites)
    {
        var rows = new List<AccountingSettingRow>();
        void Add(string section, string key, string label, string value,
                 AccountingSettingKind kind = AccountingSettingKind.Account,
                 string? argument = null, bool argumentKey = false, bool allowBlank = false) =>
            rows.Add(new AccountingSettingRow(key, section, "AccountingSetting." + label, argument, argumentKey,
                                              kind, value, allowBlank));

        const string journals = "Journals", customers = "Customers", revenue = "Revenue", treasury = "Treasury",
                     payroll = "Payroll", purchases = "Purchases";
        const AccountingSettingKind code = AccountingSettingKind.Code;

        Add(journals, AccountingSettings.SalesJournalKey, "SalesJournal", s.SalesJournal, code);
        Add(journals, AccountingSettings.CashJournalKey, "CashJournal", s.CashJournal, code);
        Add(journals, AccountingSettings.BankJournalKey, "BankJournal", s.BankJournal, code);
        Add(journals, AccountingSettings.MiscJournalKey, "MiscJournal", s.MiscJournal, code);
        Add(journals, AccountingSettings.PayrollJournalKey, "PayrollJournal", s.PayrollJournal, code);
        Add(journals, AccountingSettings.DepreciationJournalKey, "DepreciationJournal", s.DepreciationJournal, code);

        Add(customers, AccountingSettings.PatientReceivableKey, "PatientReceivable", s.PatientReceivable);
        Add(customers, AccountingSettings.PatientModeKey, "PatientMode", s.PatientMode.ToString(),
            AccountingSettingKind.PatientMode);
        Add(customers, AccountingSettings.PatientGlobalThirdPartyKey, "PatientGlobalThirdParty",
            s.PatientGlobalThirdParty, code);
        Add(customers, AccountingSettings.PayerReceivableKey, "PayerReceivable", s.PayerReceivable);
        Add(customers, AccountingSettings.CustomerAdvancesKey, "CustomerAdvances", s.CustomerAdvances);
        Add(customers, AccountingSettings.WriteOffKey, "WriteOff", s.WriteOff);

        Add(revenue, AccountingSettings.DefaultRevenueKey, "DefaultRevenue", s.DefaultRevenue);
        Add(revenue, AccountingSettings.OtherRevenueKey, "OtherRevenue", s.OtherRevenue);
        Add(revenue, AccountingSettings.VatCollectedKey, "VatCollected", s.VatCollected);
        foreach (var department in departments)
            Add(revenue, AccountingSettings.RevenueKeyPrefix + department.Code, "DepartmentRevenue",
                s.RevenueByDepartment.GetValueOrDefault(department.Code, string.Empty),
                argument: $"{department.Code} — {department.Name}", allowBlank: true);

        Add(treasury, AccountingSettings.CashAccountKey, "CashAccount", s.CashAccount);
        foreach (var site in sites)
            Add(treasury, AccountingSettings.CashKeyPrefix + site.Code, "SiteCash",
                s.CashBySite.GetValueOrDefault(site.Code, string.Empty),
                argument: $"{site.Code} — {site.Name}", allowBlank: true);
        foreach (var method in AccountingSettings.ReceiptMethods)
            Add(treasury, AccountingSettings.ReceiptKeyPrefix + method, "ReceiptAccount",
                s.ReceiptAccountFor(method, string.Empty), argument: $"PaymentMethod.{method}", argumentKey: true);
        Add(treasury, AccountingSettings.PayerSettlementKey, "PayerSettlement", s.PayerSettlement);
        Add(treasury, AccountingSettings.CashTransitKey, "CashTransit", s.CashTransit);
        Add(treasury, AccountingSettings.CashShortageKey, "CashShortage", s.CashShortage);
        Add(treasury, AccountingSettings.CashOverageKey, "CashOverage", s.CashOverage);

        Add(payroll, AccountingSettings.GrossSalariesKey, "GrossSalaries", s.GrossSalaries);
        Add(payroll, AccountingSettings.EmployerChargesKey, "EmployerCharges", s.EmployerCharges);
        Add(payroll, AccountingSettings.SocialBodiesKey, "SocialBodies", s.SocialBodies);
        Add(payroll, AccountingSettings.IncomeTaxKey, "IncomeTax", s.IncomeTax);
        Add(payroll, AccountingSettings.StaffAdvancesKey, "StaffAdvances", s.StaffAdvances);
        Add(payroll, AccountingSettings.OtherDeductionsKey, "OtherDeductions", s.OtherDeductions);
        Add(payroll, AccountingSettings.NetPayableKey, "NetPayable", s.NetPayable);

        Add(purchases, PurchasingSettings.PurchaseJournalKey, "PurchaseJournal", p.PurchaseJournal, code);
        Add(purchases, PurchasingSettings.BankJournalKey, "SupplierBankJournal", p.BankJournal, code);
        Add(purchases, PurchasingSettings.CashJournalKey, "SupplierCashJournal", p.CashJournal, code);
        Add(purchases, PurchasingSettings.SupplierAccountKey, "SupplierAccount", p.SupplierAccount);
        Add(purchases, PurchasingSettings.VatAccountKey, "RecoverableVat", p.VatAccount);
        Add(purchases, PurchasingSettings.BankAccountKey, "SupplierBankAccount", p.BankAccount);
        Add(purchases, PurchasingSettings.CashAccountKey, "SupplierCashAccount", p.CashAccount);
        foreach (var kind in Enum.GetValues<StockItemKind>())
            Add(purchases, PurchasingSettings.ChargeAccountKeyPrefix + kind, "ChargeAccount", p.ChargeAccountFor(kind),
                argument: $"StockItemKind.{kind}", argumentKey: true);

        return rows;
    }

    /// <summary>
    /// Validates the values typed against the rows of the screen, before anything is stored. Only
    /// keys of the sheet are accepted.
    /// </summary>
    public static Result<IReadOnlyDictionary<string, string?>> Validate(IReadOnlyList<AccountingSettingRow> sheet,
                                                                         IReadOnlyDictionary<string, string?> values)
    {
        var accepted = new Dictionary<string, string?>(StringComparer.Ordinal);
        var errors = new List<Error>();

        foreach (var row in sheet)
        {
            if (!values.TryGetValue(row.Key, out var raw)) continue;
            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

            if (value is null)
            {
                if (row.AllowBlank) accepted[row.Key] = null;
                else errors.Add(Error.Validation("Accounting_SettingRequired", row.Key));
                continue;
            }

            var valid = row.Kind switch
            {
                AccountingSettingKind.Code => AccountingSettings.IsCode(value),
                AccountingSettingKind.PatientMode => Enum.TryParse<PatientThirdPartyMode>(value, out var mode)
                                                     && Enum.IsDefined(mode),
                _ => AccountingSettings.IsGeneralAccount(value)
            };

            if (valid) accepted[row.Key] = value;
            else errors.Add(Error.Validation("Accounting_AccountInvalid", value));
        }

        return errors.Count > 0
            ? Result.Failure<IReadOnlyDictionary<string, string?>>(errors)
            : Result.Success<IReadOnlyDictionary<string, string?>>(accepted);
    }

    public async Task<Result> SaveSettingsAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        var sheet = await GetSettingsAsync(ct);
        if (sheet.IsFailure) return Result.Failure(sheet.FirstError);

        var valid = Validate(sheet.Value, values);
        if (valid.IsFailure) return Result.Failure(valid.Errors);

        var changed = new List<string>();
        foreach (var (key, value) in valid.Value)
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                if (value is null) continue;
                await settingsWriter.AddSettingAsync(new AppSetting(key, value)
                {
                    Description = key.StartsWith(PurchasingSettings.Prefix, StringComparison.Ordinal)
                        ? "Achats : rapprochement, loi 69-21 et export comptable"
                        : "Export comptable : journaux et comptes CGNC"
                }, ct);
                changed.Add(key);
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(key);
            }
        }

        if (changed.Count == 0) return Result.Success();

        unitOfWork.AuditReason = $"Paramètres comptables modifiés : {string.Join(", ", changed)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>Read-side queries of the accounting export, across the sites asked for.</summary>
public interface IAccountingQueries
{
    Task<IReadOnlyList<AccountingSiteOption>> GetSitesAsync(IReadOnlyCollection<Guid> siteIds, CancellationToken ct = default);

    Task<IReadOnlyList<AccountingDepartmentOption>> GetDepartmentsAsync(CancellationToken ct = default);

    /// <summary>The installation-wide settings whose key starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// The issued invoices, receipts and credit notes of the period; the settlements received, bounced,
    /// refunded or cancelled in it; the deposits taken or refunded in it; the payers' refused shares
    /// left to the patient; the tills closed in it.
    /// </summary>
    Task<SalesAccountingSources> GetSalesSourcesAsync(IReadOnlyCollection<Guid> siteIds, DateOnly from, DateOnly to,
                                                      CancellationToken ct = default);

    /// <summary>The closed payroll months ending within the period.</summary>
    Task<IReadOnlyList<PayrollMonthSource>> GetPayrollMonthsAsync(IReadOnlyCollection<Guid> siteIds, DateOnly from,
                                                                  DateOnly to, CancellationToken ct = default);
}
