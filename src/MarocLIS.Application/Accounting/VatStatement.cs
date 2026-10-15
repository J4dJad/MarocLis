using System.Globalization;
using System.Text;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Accounting;

public enum VatStatementLineKind
{
    /// <summary>Turnover exempt from VAT: the analyses (CGI art. 91).</summary>
    ExemptTurnover = 0,

    /// <summary>Taxable turnover at one rate, with the VAT collected on it.</summary>
    TaxableTurnover = 1,

    /// <summary>Purchases at one rate, with the VAT invoiced by the suppliers.</summary>
    Purchases = 2,

    /// <summary>The share of the turnover that is taxable (prorata de déduction), in percent.</summary>
    Prorata = 3,

    /// <summary>VAT on purchases deductible after the prorata.</summary>
    DeductibleAfterProrata = 4,

    /// <summary>VAT collected less deductible: positive is owed, negative is a credit.</summary>
    Balance = 5
}

/// <param name="Rate">The VAT rate in percent, for the turnover and purchase lines.</param>
public sealed record VatStatementLine(VatStatementLineKind Kind, decimal? Rate, decimal Base, decimal Vat);

/// <summary>
/// A simple VAT statement for a period: the turnover exempt and taxable, the VAT collected, the VAT
/// invoiced on purchases, the prorata and what it leaves deductible.
/// <para>
/// A working document for the accountant, not the return: sales are counted on their invoice dates
/// net of credit notes, purchases on the supplier invoice dates. A laboratory under the regime of
/// receipts (régime des encaissements) declares the VAT when it is collected and deducts it when it
/// is paid, and a purchase used only for the exempt activity is not deductible at all; the
/// accountant decides both.
/// </para>
/// </summary>
public sealed record VatStatement(DateOnly From, DateOnly To, IReadOnlyList<VatStatementLine> Lines)
{
    public decimal ExemptTurnover => Lines.Where(l => l.Kind == VatStatementLineKind.ExemptTurnover).Sum(l => l.Base);
    public decimal TaxableTurnover => Lines.Where(l => l.Kind == VatStatementLineKind.TaxableTurnover).Sum(l => l.Base);
    public decimal Collected => Lines.Where(l => l.Kind == VatStatementLineKind.TaxableTurnover).Sum(l => l.Vat);
    public decimal Invoiced => Lines.Where(l => l.Kind == VatStatementLineKind.Purchases).Sum(l => l.Vat);

    /// <summary>Taxable turnover over total turnover, in percent, rounded up to the unit as CGI art. 104 provides.</summary>
    public decimal ProrataPercent =>
        ExemptTurnover + TaxableTurnover <= 0m
            ? 0m
            : Math.Ceiling(Math.Max(0m, TaxableTurnover) * 100m / (ExemptTurnover + TaxableTurnover));

    public decimal Deductible => Money.Round(Invoiced * ProrataPercent / 100m);

    public decimal Balance => Money.Round(Collected - Deductible);

    public static VatStatement Build(DateOnly from, DateOnly to, IEnumerable<SalesDocumentSource> sales,
                                     IEnumerable<AccountingInvoiceSource> purchases)
    {
        var exempt = 0m;
        var taxable = new SortedDictionary<decimal, (decimal Base, decimal Vat)>();

        foreach (var document in sales.Where(d => d.IssueDate >= from && d.IssueDate <= to
                                                  && d.Kind is InvoiceKind.Invoice or InvoiceKind.Receipt
                                                                or InvoiceKind.CreditNote))
        {
            var sign = document.Kind == InvoiceKind.CreditNote ? -1m : 1m;
            foreach (var line in document.Lines)
            {
                if (line.IsVatExempt || line.VatRate == 0m)
                {
                    exempt += sign * line.Net;
                    continue;
                }

                var rate = Money.Round(line.VatRate * 100m);
                var sum = taxable.TryGetValue(rate, out var s) ? s : (Base: 0m, Vat: 0m);
                taxable[rate] = (sum.Base + sign * line.Net, sum.Vat + sign * line.Vat);
            }
        }

        var bought = new SortedDictionary<decimal, (decimal Base, decimal Vat)>();
        foreach (var line in purchases.Where(p => p.InvoiceDate >= from && p.InvoiceDate <= to).SelectMany(p => p.Lines))
        {
            var rate = line.ExcludingVat == 0m ? 0m : Math.Round(line.Vat * 100m / line.ExcludingVat, 0);
            var sum = bought.TryGetValue(rate, out var s) ? s : (Base: 0m, Vat: 0m);
            bought[rate] = (sum.Base + line.ExcludingVat, sum.Vat + line.Vat);
        }

        var lines = new List<VatStatementLine> { new(VatStatementLineKind.ExemptTurnover, null, Money.Round(exempt), 0m) };
        lines.AddRange(taxable.Select(t => new VatStatementLine(VatStatementLineKind.TaxableTurnover, t.Key,
                                                                Money.Round(t.Value.Base), Money.Round(t.Value.Vat))));
        lines.AddRange(bought.Select(t => new VatStatementLine(VatStatementLineKind.Purchases, t.Key,
                                                               Money.Round(t.Value.Base), Money.Round(t.Value.Vat))));

        var statement = new VatStatement(from, to, lines);
        return statement with
        {
            Lines =
            [
                .. lines,
                new(VatStatementLineKind.Prorata, null, 0m, statement.ProrataPercent),
                new(VatStatementLineKind.DeductibleAfterProrata, null, 0m, statement.Deductible),
                new(VatStatementLineKind.Balance, null, 0m, statement.Balance)
            ]
        };
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>The French label of a line, as the CSV and the PDF print it.</summary>
    public static string LabelOf(VatStatementLine line) => line.Kind switch
    {
        VatStatementLineKind.ExemptTurnover => "Chiffre d'affaires exonéré (analyses, art. 91 CGI)",
        VatStatementLineKind.TaxableTurnover => $"Chiffre d'affaires taxable à {line.Rate:0.##} % — TVA collectée",
        VatStatementLineKind.Purchases => $"Achats à {line.Rate:0.##} % — TVA facturée par les fournisseurs",
        VatStatementLineKind.Prorata => "Prorata de déduction (%)",
        VatStatementLineKind.DeductibleAfterProrata => "TVA déductible après prorata",
        VatStatementLineKind.Balance => "TVA due (crédit si négatif)",
        _ => string.Empty
    };

    public const string Warning = "Document de travail à vérifier avec votre comptable : ce n'est pas la déclaration de TVA.";

    /// <summary>The statement as rows for the screen and the PDF: label, base, VAT.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows() =>
    [
        .. Lines.Select(l => (IReadOnlyList<string>)
        [
            LabelOf(l),
            l.Kind is VatStatementLineKind.Prorata or VatStatementLineKind.DeductibleAfterProrata or VatStatementLineKind.Balance
                ? string.Empty
                : l.Base.ToString("N2", Fr),
            l.Kind == VatStatementLineKind.ExemptTurnover ? string.Empty : l.Vat.ToString("N2", Fr)
        ])
    ];

    public byte[] ToCsv()
    {
        var text = new StringBuilder();
        text.AppendLine(SupplierAccountingExport.Cell($"État de TVA du {From.ToString("dd/MM/yyyy", Fr)} au {To.ToString("dd/MM/yyyy", Fr)}"));
        text.AppendLine(SupplierAccountingExport.Cell(Warning));
        text.AppendLine("Libellé;Base HT;TVA");
        foreach (var l in Lines)
            text.AppendLine(string.Join(';', SupplierAccountingExport.Cell(LabelOf(l)),
                                        l.Base.ToString("0.00", Fr), l.Vat.ToString("0.00", Fr)));
        return SupplierAccountingExport.Encode(text.ToString());
    }
}
