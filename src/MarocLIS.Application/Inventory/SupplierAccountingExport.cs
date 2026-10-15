using System.Globalization;
using System.Text;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>One supplier invoice as the purchases journal needs it.</summary>
/// <param name="Assets">The fixed assets of the register linked to this invoice, if any.</param>
/// <param name="Exported">Its purchases entry already went out in an accounting export file.</param>
public sealed record AccountingInvoiceSource(
    string SupplierCode,
    string SupplierName,
    string? SupplierAccount,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    IReadOnlyList<AccountingInvoiceLine> Lines,
    IReadOnlyList<AccountingAssetShare>? Assets = null,
    Guid Id = default,
    bool Exported = false);

/// <summary>
/// A fixed asset bought on a supplier invoice: its asset account (class 2, from its category), its
/// cost excluding VAT and its VAT, and whether that VAT is deducted.
/// </summary>
/// <param name="ReclassifiedOn">
/// Set when the asset was linked after the invoice's purchases entry was exported: that entry stays
/// as it went out, and a reclassification entry on this day moves the asset's share to its account.
/// </param>
public sealed record AccountingAssetShare(string AssetAccount, decimal ExcludingVat, decimal Vat, bool VatRecoverable,
                                          DateOnly? ReclassifiedOn = null);

/// <summary>An invoice line's amounts, with the category its charge account depends on.</summary>
public sealed record AccountingInvoiceLine(StockItemKind Kind, decimal ExcludingVat, decimal Vat);

/// <summary>One supplier payment as the bank or cash journal needs it.</summary>
public sealed record AccountingPaymentSource(
    string SupplierCode,
    string SupplierName,
    string? SupplierAccount,
    string InvoiceNumber,
    DateOnly PaidOn,
    decimal AmountMad,
    SupplierPaymentMethod Method,
    string? Reference,
    bool AssetPurchase = false);

/// <summary>One line of a journal entry.</summary>
public sealed record AccountingEntry(
    DateOnly Date,
    string Journal,
    string Account,
    string? ThirdParty,
    string Label,
    string Piece,
    decimal Debit,
    decimal Credit);

public enum AccountingExportLayout
{
    /// <summary>Semicolons, French dates and decimal commas, labels cut to 35 characters: Sage's import.</summary>
    Sage = 0,

    /// <summary>Semicolons, ISO dates and decimal points, every column always filled.</summary>
    Generic = 1
}

/// <summary>
/// Supplier invoices and payments as accounting entries under the Moroccan CGNC.
/// <para>
/// Purchases journal, per invoice: the charge accounts (6122 by default, per item category) and
/// recoverable VAT (34552) in debit, the supplier (4411, with the supplier's auxiliary account as
/// third party) in credit. Bank or cash journal, per payment: the supplier in debit, the bank
/// (5141) or the cash box (5161) in credit. Every entry balances, because the invoice total is the
/// sum of the same rounded line amounts the debits are.
/// </para>
/// <para>
/// An invoice a fixed asset of the register is linked to is capitalised, not expensed: the asset's
/// cost goes to its asset account (2332… by category) instead of the charge accounts, and its VAT
/// to 34551 TVA récupérable sur immobilisations, or into the asset account when it is not deducted
/// (an exempt laboratory, article 91 of the CGI). The register depreciates that same cost. What the
/// invoice holds beyond the asset (consumables bought with it) stays a charge. Such an invoice is
/// owed on 4481 Dettes sur acquisitions d'immobilisations, and its payments settle 4481.
/// </para>
/// <para>
/// An asset linked to an invoice whose entry was already exported leaves that entry as it went out:
/// <see cref="Reclassifications"/> moves the asset's share from the charges (and 34552) to the asset
/// account (and 34551) on the day of the link. The supplier stays on 4411 then (to confirm with the
/// accountant).
/// </para>
/// </summary>
public static class SupplierAccountingExport
{
    public static IReadOnlyList<AccountingEntry> BuildEntries(
        IEnumerable<AccountingInvoiceSource> invoices, IEnumerable<AccountingPaymentSource> payments,
        PurchasingSettings settings)
    {
        var entries = new List<AccountingEntry>();

        foreach (var invoice in invoices.OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNumber, StringComparer.Ordinal))
        {
            var label = $"Fact. {invoice.InvoiceNumber} {invoice.SupplierName}";
            var thirdParty = ThirdParty(invoice.SupplierAccount, invoice.SupplierCode);

            var byAccount = invoice.Lines
                .GroupBy(l => settings.ChargeAccountFor(l.Kind), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (Account: g.Key, Amount: Money.Round(g.Sum(l => l.ExcludingVat))))
                .ToList();

            var vat = Money.Round(invoice.Lines.Sum(l => l.Vat));

            decimal total = 0m;
            var supplier = settings.SupplierAccount;
            if (OnInvoice(invoice) is { Count: > 0 } assets)
            {
                supplier = settings.AssetSupplierAccount;
                var capitalised = Capitalise(assets, byAccount, ref vat);
                foreach (var (account, amount) in capitalised.AssetDebits)
                {
                    total += amount;
                    entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, account, null,
                                                    label, invoice.InvoiceNumber, amount, 0m));
                }
                if (capitalised.RecoverableVat != 0m)
                {
                    total += capitalised.RecoverableVat;
                    entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, settings.AssetVatAccount,
                                                    null, label, invoice.InvoiceNumber, capitalised.RecoverableVat, 0m));
                }
                byAccount = capitalised.Charges;
            }

            foreach (var (account, amount) in byAccount)
            {
                if (amount == 0m) continue;
                total += amount;
                entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, account, null,
                                                label, invoice.InvoiceNumber, amount, 0m));
            }

            if (vat != 0m)
            {
                total += vat;
                entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, settings.VatAccount,
                                                null, label, invoice.InvoiceNumber, vat, 0m));
            }

            entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, supplier,
                                            thirdParty, label, invoice.InvoiceNumber, 0m, Money.Round(total)));
        }

        foreach (var payment in payments.OrderBy(p => p.PaidOn).ThenBy(p => p.InvoiceNumber, StringComparer.Ordinal))
        {
            var cash = payment.Method == SupplierPaymentMethod.Cash;
            var journal = cash ? settings.CashJournal : settings.BankJournal;
            var treasury = cash ? settings.CashAccount : settings.BankAccount;
            var piece = string.IsNullOrWhiteSpace(payment.Reference) ? payment.InvoiceNumber : payment.Reference;
            var label = $"Règl. {MethodLabel(payment.Method)} fact. {payment.InvoiceNumber} {payment.SupplierName}";

            entries.Add(new AccountingEntry(payment.PaidOn, journal,
                                            payment.AssetPurchase ? settings.AssetSupplierAccount : settings.SupplierAccount,
                                            ThirdParty(payment.SupplierAccount, payment.SupplierCode),
                                            label, piece, payment.AmountMad, 0m));
            entries.Add(new AccountingEntry(payment.PaidOn, journal, treasury, null, label, piece,
                                            0m, payment.AmountMad));
        }

        return entries;
    }

    /// <summary>The assets capitalised in the invoice's own entry: those linked before it was exported.</summary>
    private static IReadOnlyList<AccountingAssetShare> OnInvoice(AccountingInvoiceSource invoice) =>
        [.. (invoice.Assets ?? []).Where(a => a.ReclassifiedOn is null)];

    private static List<(string Account, decimal Amount)> Charges(AccountingInvoiceSource invoice,
                                                                  PurchasingSettings settings) =>
    [
        .. invoice.Lines
            .GroupBy(l => settings.ChargeAccountFor(l.Kind), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Account: g.Key, Amount: Money.Round(g.Sum(l => l.ExcludingVat))))
    ];

    /// <summary>
    /// The reclassification entries of the assets linked to an invoice after its purchases entry was
    /// exported (I7), dated the day of the link when it falls within the period: the asset account
    /// (cost, plus the VAT not deducted) and 34551 (the VAT deducted) in debit, the charge accounts
    /// the export used and 34552 in credit, for the share the invoice still held after the assets
    /// capitalised in its own entry.
    /// </summary>
    public static IReadOnlyList<AccountingEntry> Reclassifications(IEnumerable<AccountingInvoiceSource> invoices,
                                                                   PurchasingSettings settings, DateOnly from,
                                                                   DateOnly to)
    {
        var entries = new List<AccountingEntry>();

        foreach (var invoice in invoices.OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNumber, StringComparer.Ordinal))
        {
            var late = (invoice.Assets ?? []).Where(a => a.ReclassifiedOn is not null)
                                             .OrderBy(a => a.ReclassifiedOn).ToList();
            if (late.Count == 0) continue;

            var charges = Charges(invoice, settings);
            var vat = Money.Round(invoice.Lines.Sum(l => l.Vat));
            if (OnInvoice(invoice) is { Count: > 0 } onInvoice) charges = Capitalise(onInvoice, charges, ref vat).Charges;

            foreach (var asset in late)
            {
                var vatBefore = vat;
                var moved = Capitalise([asset], charges, ref vat);
                var credits = charges.Zip(moved.Charges, (b, a) => (b.Account, Amount: Money.Round(b.Amount - a.Amount)))
                                     .Where(c => c.Amount != 0m).ToList();
                charges = moved.Charges;

                var date = asset.ReclassifiedOn!.Value;
                if (date < from || date > to) continue;

                var label = $"Reclass. immo. fact. {invoice.InvoiceNumber} {invoice.SupplierName}";
                AccountingEntry Line(string account, decimal debit, decimal credit) =>
                    new(date, settings.PurchaseJournal, account, null, label, invoice.InvoiceNumber, debit, credit);

                foreach (var (account, amount) in moved.AssetDebits) entries.Add(Line(account, amount, 0m));
                if (moved.RecoverableVat != 0m) entries.Add(Line(settings.AssetVatAccount, moved.RecoverableVat, 0m));
                foreach (var (account, amount) in credits) entries.Add(Line(account, 0m, amount));
                var vatMoved = Money.Round(vatBefore - vat);
                if (vatMoved != 0m) entries.Add(Line(settings.VatAccount, 0m, vatMoved));
            }
        }

        return entries;
    }

    private sealed record Capitalised(
        IReadOnlyList<(string Account, decimal Amount)> AssetDebits,
        decimal RecoverableVat,
        List<(string Account, decimal Amount)> Charges);

    /// <summary>
    /// Moves the assets' share of an invoice from its charges to the asset accounts. Each asset takes
    /// its cost and its VAT, never more than the invoice still holds (an asset whose cost includes
    /// transport invoiced elsewhere); the cost comes off the largest charges first. The debits only
    /// move between accounts, so the entry still balances against the supplier's credit.
    /// </summary>
    private static Capitalised Capitalise(IReadOnlyList<AccountingAssetShare> assets,
                                          IReadOnlyList<(string Account, decimal Amount)> charges, ref decimal vat)
    {
        var excluding = Money.Round(charges.Where(c => c.Amount > 0m).Sum(c => c.Amount));
        var debits = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var recoverable = 0m;
        var taken = 0m;

        foreach (var asset in assets)
        {
            var cost = Math.Max(0m, Math.Min(Money.Round(asset.ExcludingVat), excluding - taken));
            var assetVat = vat > 0m ? Math.Max(0m, Math.Min(Money.Round(asset.Vat), vat)) : 0m;
            taken += cost;
            vat = Money.Round(vat - assetVat);
            if (asset.VatRecoverable) recoverable += assetVat;

            var debit = Money.Round(cost + (asset.VatRecoverable ? 0m : assetVat));
            if (debit != 0m) debits[asset.AssetAccount] = debits.GetValueOrDefault(asset.AssetAccount) + debit;
        }

        var left = new List<(string Account, decimal Amount)>(charges);
        var remaining = taken;
        foreach (var index in Enumerable.Range(0, left.Count).OrderByDescending(i => left[i].Amount).ToList())
        {
            if (remaining <= 0m) break;
            var off = Math.Min(remaining, Math.Max(0m, left[index].Amount));
            left[index] = (left[index].Account, Money.Round(left[index].Amount - off));
            remaining -= off;
        }

        return new Capitalised([.. debits.Select(d => (d.Key, Money.Round(d.Value)))], Money.Round(recoverable), left);
    }

    private static string ThirdParty(string? account, string code) =>
        string.IsNullOrWhiteSpace(account) ? code : account.Trim();

    private static string MethodLabel(SupplierPaymentMethod method) => method switch
    {
        SupplierPaymentMethod.Transfer => "vir.",
        SupplierPaymentMethod.Cheque => "chq",
        SupplierPaymentMethod.Cash => "esp.",
        SupplierPaymentMethod.BillOfExchange => "effet",
        _ => string.Empty
    };

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>The entries as a CSV file, UTF-8 with its byte-order mark so Excel reads the accents.</summary>
    public static byte[] ToCsv(IEnumerable<AccountingEntry> entries, AccountingExportLayout layout)
    {
        var text = new StringBuilder();
        var sage = layout == AccountingExportLayout.Sage;

        text.AppendLine(sage
            ? "Date;Journal;Compte général;Compte tiers;Libellé;N° pièce;Débit;Crédit"
            : "date;journal;compte;tiers;libelle;piece;debit;credit");

        foreach (var e in entries)
        {
            var (piece, label) = sage ? SageCells(e.Piece, e.Label) : (e.Piece, e.Label);

            text.AppendLine(string.Join(';',
                sage ? e.Date.ToString("dd/MM/yyyy", Fr) : e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Cell(e.Journal), Cell(e.Account), Cell(e.ThirdParty ?? string.Empty), Cell(label), Cell(piece),
                Amount(e.Debit, sage), Amount(e.Credit, sage)));
        }

        return Encode(text.ToString());
    }

    /// <summary>Sage's "N° pièce" holds 13 characters, its "Libellé" 35.</summary>
    public const int SagePieceLength = 13;

    private const int SageLabelLength = 35;

    /// <summary>
    /// The piece and label as Sage takes them. A piece longer than 13 characters is compacted rather
    /// than cut (see <see cref="SagePiece"/>), and the full number is then put at the head of the
    /// label when the label, cut to 35, does not already show it.
    /// </summary>
    internal static (string Piece, string Label) SageCells(string piece, string label)
    {
        piece = piece.Trim();
        var cut = label.Length > SageLabelLength ? label[..SageLabelLength] : label;
        if (piece.Length <= SagePieceLength) return (piece, cut);

        var compact = SagePiece(piece);
        if (!cut.Contains(piece, StringComparison.Ordinal))
        {
            cut = $"{piece} {label}";
            if (cut.Length > SageLabelLength) cut = cut[..SageLabelLength].TrimEnd();
        }
        return (compact, cut);
    }

    /// <summary>
    /// A piece of at most 13 characters, each distinct number keeping a piece of its own. A number
    /// of the billing series, PREFIX/SITE/YEAR/SEQ ("FAC/CASA-ANFA/2026/000318"), becomes the
    /// prefix's initial, a short code worked out from the whole "PREFIX/SITE" (so that sites whose
    /// codes start alike, and the invoices, credit notes and receipts of one site, do not share
    /// it), the year's last two digits and the whole sequence: "F" + 4 characters + "26000318".
    /// Anything else keeps its first character, a short code of the whole text and the end of it.
    /// The short code depends on the text alone, so a piece is the same in every export.
    /// </summary>
    public static string SagePiece(string piece)
    {
        piece = piece.Trim();
        if (piece.Length <= SagePieceLength) return piece;

        // The site code is free text: only the slashes of the series separate its parts.
        var parts = piece.Split('/', StringSplitOptions.TrimEntries);
        if (parts is [var prefix, var site, var year, var sequence]
            && prefix.Length > 0 && char.IsAsciiLetter(prefix[0]) && site.Length > 0
            && year.Length == 4 && year.All(char.IsAsciiDigit)
            && sequence.Length > 0 && sequence.All(char.IsAsciiDigit))
        {
            var room = SagePieceLength - 1 - 2 - sequence.Length;
            if (room >= 3)
                return $"{char.ToUpperInvariant(prefix[0])}{ShortCode($"{prefix}/{site}", room)}{year[2..]}{sequence}";
        }

        var joined = string.Concat(piece.Where(char.IsAsciiLetterOrDigit)).ToUpperInvariant();
        if (joined.Length == 0) joined = piece;
        const int tail = SagePieceLength - 1 - 4;
        return $"{char.ToUpperInvariant(joined[0])}{ShortCode(piece, 4)}{(joined.Length > tail ? joined[^tail..] : joined)}";
    }

    /// <summary>
    /// <paramref name="width"/> base-36 characters taken from a 64-bit FNV-1a hash of the text, case
    /// ignored: stable from one run, machine or version to the next, unlike <c>string.GetHashCode</c>.
    /// </summary>
    private static string ShortCode(string text, int width)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text.ToUpperInvariant()))
            hash = (hash ^ b) * 1099511628211UL;

        var code = new char[width];
        for (var i = width - 1; i >= 0; i--)
        {
            code[i] = digits[(int)(hash % 36UL)];
            hash /= 36UL;
        }
        return new string(code);
    }

    private static string Amount(decimal value, bool sage) =>
        sage
            ? value == 0m ? string.Empty : value.ToString("0.00", Fr)
            : value.ToString("0.00", CultureInfo.InvariantCulture);

    internal static string Cell(string value) => CsvCell.Of(value);

    internal static byte[] Encode(string csv) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];
}
