using System.Globalization;
using MarocLIS.Application.Input;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;

namespace MarocLIS.Application.Accounting;

/// <summary>
/// What « Export comptable » offers by right and by what was checked — the workstation's
/// PermissionGate on its four buttons, and the export kept off until a check found every entry
/// balanced and on an account.
/// </summary>
public sealed record AccountingExportActions(bool Right, bool Check, bool Export, bool VatStatement);

/// <summary>The export as chosen on the screen: the period, the layout, the sites and the journals ticked.</summary>
public sealed class AccountingExportForm
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public AccountingExportLayout Layout { get; set; } = AccountingExportLayout.Sage;
    public HashSet<Guid> SiteIds { get; } = [];
    public AccountingJournals Journals { get; set; }
}

/// <summary>One line of the journals' summary: a journal and a nature, its entries and its totals.</summary>
public sealed record AccountingJournalSummary(string Journal, AccountingJournals Kind, int Entries, decimal Debit, decimal Credit);

/// <summary>The message under the period: what the check found, in its tone.</summary>
public sealed record AccountingExportStatus(string Text, StockInk Ink);

/// <summary>
/// The rules of « Export comptable » that need no window: the period offered (the last calendar
/// month, the usual rhythm of the accountant's entries), the journals offered, what a choice must
/// hold before it is sent — a period in order, a site, a journal, each refused on its own box —,
/// the summary of the entries by journal and nature, the message of the check, the names of the
/// files and the VAT statement's printout. The workstation's AccountingExportView and the web's
/// screen apply the same ones; the entries, their check and the files are AccountingExportService's.
/// </summary>
public static class AccountingExportScreenRules
{
    public const string PeriodField = "export-period";
    public const string SitesField = "export-sites";
    public const string JournalsField = "export-journals";

    /// <summary>The journals offered, in the workstation's order.</summary>
    public static readonly IReadOnlyList<AccountingJournals> JournalChoices =
    [
        AccountingJournals.Sales, AccountingJournals.Receipts, AccountingJournals.Adjustments,
        AccountingJournals.Payroll, AccountingJournals.Depreciation, AccountingJournals.Purchases
    ];

    /// <summary>Every journal offered, ticked as the screen opens.</summary>
    public static AccountingJournals AllJournals =>
        JournalChoices.Aggregate(AccountingJournals.None, (all, journal) => all | journal);

    public static AccountingExportActions Actions(Func<string, bool> has, AccountingExportPreview? preview)
    {
        ArgumentNullException.ThrowIfNull(has);
        var right = has(Permissions.Accounting.Export);
        return new AccountingExportActions(right, right, right && preview is { CanExport: true }, right);
    }

    /// <summary>The last calendar month, every site and every journal, Sage's layout.</summary>
    public static AccountingExportForm NewForm(DateOnly today, IEnumerable<AccountingSiteOption> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var form = new AccountingExportForm
        {
            From = firstOfMonth.AddMonths(-1),
            To = firstOfMonth.AddDays(-1),
            Journals = AllJournals
        };
        foreach (var site in sites) form.SiteIds.Add(site.Id);
        return form;
    }

    /// <summary>« CAS — Casablanca Centre », as the sites are listed.</summary>
    public static string SiteDisplay(AccountingSiteOption site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return $"{site.Code} — {site.Name}";
    }

    public static string JournalKey(AccountingJournals journal) => $"AccountingJournals.{journal}";

    public static string LayoutKey(AccountingExportLayout layout) => $"AccountingExportLayout.{layout}";

    public static string ProblemKey(AccountingProblemKind kind) => $"AccountingProblemKind.{kind}";

    /// <summary>The journals of the form with <paramref name="journal"/> ticked or unticked.</summary>
    public static AccountingJournals Toggle(AccountingJournals journals, AccountingJournals journal, bool ticked) =>
        ticked ? journals | journal : journals & ~journal;

    /// <summary>
    /// The choice as AccountingExportService takes it, or the box at fault: a period in reverse, no
    /// site, no journal — refused by the service too, said here before anything is read.
    /// </summary>
    public static (FieldRefusal? Refusal, AccountingExportRequest? Request) Check(AccountingExportForm form,
                                                                                 IReadOnlyList<AccountingSiteOption> sites)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(sites);
        if (form.From > form.To) return (new FieldRefusal(PeriodField, "Purchasing_PeriodReversed"), null);

        // In the order the sites are listed, as the workstation reads its ticked boxes.
        var siteIds = sites.Where(s => form.SiteIds.Contains(s.Id)).Select(s => s.Id).ToList();
        if (siteIds.Count == 0) return (new FieldRefusal(SitesField, "Accounting_NoSite"), null);
        if (form.Journals == AccountingJournals.None) return (new FieldRefusal(JournalsField, "Accounting_NoJournal"), null);

        return (null, new AccountingExportRequest(form.From, form.To, siteIds, form.Journals, form.Layout));
    }

    /// <summary>The box a refusal of AccountingExportService names; any other is said in the message.</summary>
    public static string? FieldOf(Error? error) => error?.ResourceKey switch
    {
        "Purchasing_PeriodReversed" => PeriodField,
        "Accounting_NoSite" or "Accounting_SiteNotAccessible" => SitesField,
        "Accounting_NoJournal" => JournalsField,
        _ => null
    };

    /// <summary>The entries by journal and nature, the journals in their code's order.</summary>
    public static IReadOnlyList<AccountingJournalSummary> Summary(AccountingExportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return
        [
            .. preview.Entries.GroupBy(e => (e.Journal, e.Kind))
                              .OrderBy(g => g.Key.Journal, StringComparer.Ordinal)
                              .Select(g => new AccountingJournalSummary(g.Key.Journal, g.Key.Kind, g.Count(),
                                                                        Money.Round(g.Sum(e => e.Debit)),
                                                                        Money.Round(g.Sum(e => e.Credit))))
        ];
    }

    /// <summary>
    /// What the check found: nothing checked yet; entries refused (they are listed); nothing in the
    /// period; or every entry balanced and on an account, the export open. The depreciation asked
    /// for without a register to give it is added to the message.
    /// </summary>
    public static AccountingExportStatus Status(AccountingExportPreview? preview, AccountingJournals journals, ScreenText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (preview is null) return new AccountingExportStatus(text("Accounting.CheckFirst"), StockInk.Muted);

        var status = preview.Problems.Count > 0
            ? new AccountingExportStatus(text("Accounting_ExportRefused", preview.Problems.Count), StockInk.CriticalBold)
            : preview.Entries.Count == 0
                ? new AccountingExportStatus(text("Purchasing_NothingToExport"), StockInk.Muted)
                : new AccountingExportStatus(text("Accounting.Balanced", preview.Entries.Count), StockInk.Plain);

        return !preview.DepreciationAvailable && journals.HasFlag(AccountingJournals.Depreciation)
            ? status with { Text = $"{status.Text}  ·  {text("Accounting.NoDepreciationSource")}" }
            : status;
    }

    /// <summary>« export-comptable-sage-20260901-20260930.csv ».</summary>
    public static string ExportFileName(AccountingExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"export-comptable-{request.Layout.ToString().ToLowerInvariant()}-{request.From:yyyyMMdd}-{request.To:yyyyMMdd}.csv";
    }

    /// <summary>« etat-tva-20260901-20260930.pdf » or « .csv ».</summary>
    public static string VatFileName(DateOnly from, DateOnly to, bool pdf) =>
        $"etat-tva-{from:yyyyMMdd}-{to:yyyyMMdd}.{(pdf ? "pdf" : "csv")}";

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// The VAT statement's title: a working document for the accountant, in French as the
    /// declaration it prepares, whatever the screen's language.
    /// </summary>
    public static string VatTitle(DateOnly from, DateOnly to) =>
        $"État de TVA du {from.ToString("dd/MM/yyyy", Fr)} au {to.ToString("dd/MM/yyyy", Fr)}";

    /// <summary>The VAT statement's columns, in French with its lines.</summary>
    public static readonly IReadOnlyList<string> VatColumns = ["Libellé", "Base HT", "TVA"];
}
