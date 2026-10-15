using System.Globalization;
using System.Text;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;

namespace MarocLIS.Application.Inventory;

/// <summary>One supplier invoice as the purchases journal needs it.</summary>
public sealed record AccountingInvoiceSource(
    string SupplierCode,
    string SupplierName,
    string? SupplierAccount,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    IReadOnlyList<AccountingInvoiceLine> Lines);

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
    string? Reference);

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
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            decimal total = 0m;
            foreach (var group in byAccount)
            {
                var amount = Money.Round(group.Sum(l => l.ExcludingVat));
                if (amount == 0m) continue;
                total += amount;
                entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, group.Key, null,
                                                label, invoice.InvoiceNumber, amount, 0m));
            }

            var vat = Money.Round(invoice.Lines.Sum(l => l.Vat));
            if (vat != 0m)
            {
                total += vat;
                entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, settings.VatAccount,
                                                null, label, invoice.InvoiceNumber, vat, 0m));
            }

            entries.Add(new AccountingEntry(invoice.InvoiceDate, settings.PurchaseJournal, settings.SupplierAccount,
                                            thirdParty, label, invoice.InvoiceNumber, 0m, Money.Round(total)));
        }

        foreach (var payment in payments.OrderBy(p => p.PaidOn).ThenBy(p => p.InvoiceNumber, StringComparer.Ordinal))
        {
            var cash = payment.Method == SupplierPaymentMethod.Cash;
            var journal = cash ? settings.CashJournal : settings.BankJournal;
            var treasury = cash ? settings.CashAccount : settings.BankAccount;
            var piece = string.IsNullOrWhiteSpace(payment.Reference) ? payment.InvoiceNumber : payment.Reference;
            var label = $"Règl. {MethodLabel(payment.Method)} fact. {payment.InvoiceNumber} {payment.SupplierName}";

            entries.Add(new AccountingEntry(payment.PaidOn, journal, settings.SupplierAccount,
                                            ThirdParty(payment.SupplierAccount, payment.SupplierCode),
                                            label, piece, payment.AmountMad, 0m));
            entries.Add(new AccountingEntry(payment.PaidOn, journal, treasury, null, label, piece,
                                            0m, payment.AmountMad));
        }

        return entries;
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
            var label = sage && e.Label.Length > 35 ? e.Label[..35] : e.Label;

            text.AppendLine(string.Join(';',
                sage ? e.Date.ToString("dd/MM/yyyy", Fr) : e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Cell(e.Journal), Cell(e.Account), Cell(e.ThirdParty ?? string.Empty), Cell(label), Cell(e.Piece),
                Amount(e.Debit, sage), Amount(e.Credit, sage)));
        }

        return Encode(text.ToString());
    }

    private static string Amount(decimal value, bool sage) =>
        sage
            ? value == 0m ? string.Empty : value.ToString("0.00", Fr)
            : value.ToString("0.00", CultureInfo.InvariantCulture);

    internal static string Cell(string value) => CsvCell.Of(value);

    internal static byte[] Encode(string csv) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];
}
