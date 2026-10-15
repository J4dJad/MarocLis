using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MarocLIS.Application.Billing;
using MarocLIS.Domain.HumanResources;

namespace MarocLIS.Application.HumanResources;

/// <summary>
/// The DGI referential codes the declaration needs and the application does not hold: the
/// employer's commune and the professional-expenses rate of each employee, typed by HR from the
/// Simpl-IR referential.
/// </summary>
/// <param name="LowIncomeRateCode">The rate applied up to the yearly threshold (35 % in 2026), e.g. TPP.35.2023.</param>
/// <param name="HighIncomeRateCode">The rate applied above it (25 % in 2026), e.g. TPP.25.2023.</param>
public sealed record SimplIrCodes(string Commune, string LowIncomeRateCode, string HighIncomeRateCode)
{
    public const string DefaultLowIncomeRateCode = "TPP.35.2023";
    public const string DefaultHighIncomeRateCode = "TPP.25.2023";
}

/// <summary>One closed payslip of the year, with the employee as the file now describes them.</summary>
/// <param name="HighExpensesRate">The payslip's professional expenses were at the rate above the threshold.</param>
public sealed record SimplIrPayslip(int Month, Guid EmployeeId, string Number, string LastName, string FirstName,
                                    string? Address, string? Cin, string? CnssNumber, FamilyStatus FamilyStatus,
                                    ContractType ContractType, int DependentPersons, bool HighExpensesRate,
                                    PayslipAmounts Amounts);

/// <summary>
/// État 9421, "Traitements et salaires", as the Simpl-IR XML of the DGI EDI spec (v2): the year's
/// salaries per employee with the tax withheld, and the tax of each month.
/// <para>
/// UTF-8, amounts with a decimal point and two decimals, dates AAAA-MM-JJ. Every element of the
/// official example is written, empty when the application has nothing for it, in its order.
/// </para>
/// <list type="bullet">
/// <item>Permanent staff (listPersonnelPermanent): every salaried employee, fixed-term included.</item>
/// <item>Stagiaires: internship agreements and ANAPEC insertion contracts, whose stipend the
/// CGI (art. 57-16°) exempts up to 6,000 DH a month and the stagiaire annex lists, the taxed
/// excess as its net taxable income. An ANAPEC month past the 24 months of the exemption is
/// an ordinary salary and goes to the permanent list.</item>
/// <item>A Tahfiz salary up to 10,000 DH (CGI art. 57-20°) is declared exempt (mtExonere), not
/// as taxable income on which no tax was withheld.</item>
/// <item>Occasional staff: none, the payroll has no such contract.</item>
/// <item>Partners and agency staff are not paid by the payroll and never appear.</item>
/// </list>
/// </summary>
public static class SimplIrSalaryDeclaration
{
    /// <summary>Dependants counted for the tax reduction, at most.</summary>
    public const int MaximumReductions = 6;

    public static string FileName(string fiscalId, int year) =>
        $"TraitementEtSalaire_{fiscalId.Trim()}_{year}.xml";

    public static bool IsStagiaire(ContractType type) => type is ContractType.Internship or ContractType.Anapec;

    /// <summary>
    /// The payslip goes to the stagiaire list: an internship, or an insertion contract within the
    /// 24 months of its exemption (<see cref="PayrollCalculator.WithinInsertionExemption"/>).
    /// </summary>
    public static bool IsStagiaire(SimplIrPayslip payslip) =>
        IsStagiaire(payslip.ContractType) && payslip.Amounts.Exemption != PayrollExemption.None;

    /// <summary>A Tahfiz salary the income tax exempts: gross taxable within 10,000 DH (CGI art. 57-20°).</summary>
    public static bool IsExemptTahfiz(PayslipAmounts a) =>
        a.Exemption == PayrollExemption.Tahfiz && a.GrossTaxable <= PayrollCalculator.TahfizTaxExemptGross;

    public static string FamilyStatusCode(FamilyStatus status) => status switch
    {
        FamilyStatus.Married => "M",
        FamilyStatus.Divorced => "D",
        FamilyStatus.Widowed => "V",
        _ => "C"
    };

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static XElement E(string name, object? value = null) => new(name, value ?? string.Empty);

    private sealed record Person(SimplIrPayslip Latest, IReadOnlyList<SimplIrPayslip> Slips)
    {
        public decimal Sum(Func<PayslipAmounts, decimal> amount) => Slips.Sum(s => amount(s.Amounts));
        public long Days => (long)Slips.Sum(s => s.Amounts.PaidDays);
    }

    public static byte[] Build(InvoiceIssuer employer, int year, IReadOnlyList<SimplIrPayslip> payslips, SimplIrCodes codes)
    {
        // Each payslip goes to the list of its own contract: an ANAPEC contract turned into a CDI
        // during the year gives one stagiaire row (the exempt months) and one permanent row.
        var people = payslips
            .Where(p => PayrollCalculator.IsOnPayroll(p.ContractType))
            .GroupBy(p => (p.EmployeeId, Stagiaire: IsStagiaire(p)))
            .Select(g => new Person(g.OrderBy(p => p.Month).Last(), [.. g.OrderBy(p => p.Month)]))
            .OrderBy(p => p.Latest.LastName, StringComparer.Ordinal).ThenBy(p => p.Latest.FirstName, StringComparer.Ordinal)
            .ToList();

        var permanent = people.Where(p => !IsStagiaire(p.Latest)).ToList();
        var stagiaires = people.Where(p => IsStagiaire(p.Latest)).ToList();

        var permanentRows = permanent.Select(p => Permanent(p, codes)).ToList();
        var stagiaireRows = stagiaires.Select(Stagiaire).ToList();

        decimal Total(IEnumerable<XElement> rows, string element) =>
            rows.Sum(r => decimal.Parse(r.Element(element)!.Value, CultureInfo.InvariantCulture));

        var irPermanent = Total(permanentRows, "irPreleve");
        var irStagiaires = stagiaires.Sum(p => p.Sum(a => a.IncomeTax));

        var address = string.Join(", ", new[] { employer.Address, employer.City }.Where(s => !string.IsNullOrWhiteSpace(s))
                                                                                .Select(s => s!.Trim()));

        var root = new XElement("TraitementEtSalaire",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            E("identifiantFiscal", employer.FiscalId?.Trim()),
            E("nom"),
            E("prenom"),
            E("raisonSociale", string.IsNullOrWhiteSpace(employer.LegalName) ? employer.Name : employer.LegalName),
            E("exerciceFiscalDu", $"{year:0000}-01-01"),
            E("exerciceFiscalAu", $"{year:0000}-12-31"),
            E("annee", Int(year)),
            new XElement("commune", E("code", codes.Commune.Trim())),
            E("adresse", address),
            E("numeroCIN"),
            E("numeroCNSS", employer.CnssAffiliationNumber?.Trim()),
            E("numeroCE"),
            E("numeroRC", employer.CommercialRegisterNumber?.Trim()),
            E("identifiantTP", employer.ProfessionalTaxNumber?.Trim()),
            E("numeroFax"),
            E("numeroTelephone", employer.Phone?.Trim()),
            E("email", employer.Email?.Trim()),
            E("effectifTotal", Int(permanent.Count + stagiaires.Count)),
            E("nbrPersoPermanent", Int(permanent.Count)),
            E("nbrPersoOccasionnel", Int(0)),
            E("nbrStagiaires", Int(stagiaires.Count)),
            E("totalMtRevenuBrutImposablePP", Amount(Total(permanentRows, "mtRevenuBrutImposable"))),
            E("totalMtRevenuNetImposablePP", Amount(Total(permanentRows, "mtRevenuNetImposable"))),
            E("totalMtTotalDeductionPP", Amount(Total(permanentRows, "mtTotalDeduction"))),
            E("totalMtIrPrelevePP", Amount(irPermanent)),
            E("totalMtBrutSommesPO", Amount(0m)),
            E("totalIrPrelevePO", Amount(0m)),
            E("totalMtBrutTraitSalaireSTG", Amount(Total(stagiaireRows, "mtBrutTraitementSalaire"))),
            E("totalMtBrutIndemnitesSTG", Amount(Total(stagiaireRows, "mtBrutIndemnites"))),
            E("totalMtRetenuesSTG", Amount(Total(stagiaireRows, "mtRetenues"))),
            E("totalMtRevenuNetImpSTG", Amount(Total(stagiaireRows, "mtRevenuNetImposable"))),
            E("totalSommePayeRTS", Amount(0m)),
            E("totalmtAnuuelRevenuSalarial", Amount(0m)),
            E("totalmtAbondement", Amount(0m)),
            E("montantPermanent", Amount(irPermanent)),
            E("montantOccasionnel", Amount(0m)),
            E("montantStagiaire", Amount(irStagiaires)),
            E("referenceDeclaration"),
            new XElement("listPersonnelPermanent", permanentRows),
            new XElement("listPersonnelOccasionnel"),
            new XElement("listStagiaires", stagiaireRows),
            new XElement("listBeneficiaires"),
            new XElement("listBeneficiairesPlanEpargne"),
            new XElement("listVersements", Payments(payslips)));

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        return stream.ToArray();
    }

    private static XElement Permanent(Person p, SimplIrCodes codes)
    {
        var e = p.Latest;
        var gross = p.Sum(a => a.Gross);

        // A Tahfiz month within 10,000 DH is not taxable: its salary is declared exempt, and the
        // expenses and contributions are not deducted from a taxable income it does not have.
        var taxed = p.Slips.Where(s => !IsExemptTahfiz(s.Amounts)).ToList();
        decimal Taxed(Func<PayslipAmounts, decimal> amount) => taxed.Sum(s => amount(s.Amounts));

        var exempt = p.Sum(a => a.ExemptAllowances) + p.Slips.Where(s => IsExemptTahfiz(s.Amounts)).Sum(s => s.Amounts.GrossTaxable);
        var taxable = Taxed(a => a.GrossTaxable);
        var expenses = Taxed(a => a.ProfessionalExpenses);
        var insurance = Taxed(a => a.CnssEmployee + a.AmoEmployee);
        const decimal otherWithholdings = 0m;

        // One rate per employee on the return: the one most of the year's taxable pay was at.
        var high = taxed.Where(s => s.HighExpensesRate).Sum(s => s.Amounts.GrossTaxable);
        var low = taxed.Where(s => !s.HighExpensesRate).Sum(s => s.Amounts.GrossTaxable);
        var rate = high > low ? codes.HighIncomeRateCode : codes.LowIncomeRateCode;

        return new XElement("PersonnelPermanent",
            E("nom", e.LastName.Trim()),
            E("prenom", e.FirstName.Trim()),
            E("adressePersonnelle", e.Address?.Trim()),
            E("numCNI", e.Cin?.Trim()),
            E("numCE"),
            E("numPPR"),
            E("numCNSS", e.CnssNumber?.Trim()),
            E("ifu"),
            E("mtBrutTraitementSalaire", Amount(gross)),
            E("periode", Int(p.Days)),
            E("mtExonere", Amount(exempt)),
            E("mtEcheances", Amount(0m)),
            E("nbrReductions", Int(Math.Clamp(e.DependentPersons, 0, MaximumReductions))),
            E("mtIndemnite", Amount(0m)),
            E("mtAvantages", Amount(0m)),
            E("mtRevenuBrutImposable", Amount(taxable)),
            E("mtFraisProfess", Amount(expenses)),
            E("mtCotisationAssur", Amount(insurance)),
            E("mtAutresRetenues", Amount(otherWithholdings)),
            E("mtRevenuNetImposable", Amount(Taxed(a => a.NetTaxable))),
            E("mtTotalDeduction", Amount(expenses + insurance + otherWithholdings)),
            E("irPreleve", Amount(p.Sum(a => a.IncomeTax))),
            E("casSportif", "false"),
            E("numMatricule", e.Number.Trim()),
            E("datePermis"),
            E("dateAutorisation"),
            new XElement("refSituationFamiliale", E("code", FamilyStatusCode(e.FamilyStatus))),
            new XElement("refTaux", E("code", rate.Trim())));
    }

    private static XElement Stagiaire(Person p)
    {
        var e = p.Latest;
        return new XElement("Stagiaire",
            E("nom", e.LastName.Trim()),
            E("prenom", e.FirstName.Trim()),
            E("adressePersonnelle", e.Address?.Trim()),
            E("numCNI", e.Cin?.Trim()),
            E("numCE"),
            E("numCNSS", e.CnssNumber?.Trim()),
            E("ifu"),
            E("mtBrutTraitementSalaire", Amount(p.Sum(a => a.GrossTaxable))),
            E("mtBrutIndemnites", Amount(p.Sum(a => a.ExemptAllowances))),
            E("mtRetenues", Amount(p.Sum(a => a.CnssEmployee + a.AmoEmployee))),
            E("mtRevenuNetImposable", Amount(p.Sum(StagiaireNetTaxable))),
            E("periode", Int(p.Days)));
    }

    /// <summary>
    /// An internship's gratification is exempt and not taxed by the payroll; an ANAPEC stipend is
    /// exempt up to 6,000 DH a month (CGI art. 57-16°) and its excess taxed. The net taxable
    /// declared is that of the excess, nothing for a stipend within the cap whatever a payslip
    /// computed before the cap was applied.
    /// </summary>
    private static decimal StagiaireNetTaxable(PayslipAmounts a) =>
        PayrollCalculator.ContributionBase(a) > 0m ? a.NetTaxable : 0m;

    /// <summary>
    /// The tax withheld each month. When and how it was paid to the DGI is not recorded in the
    /// application: the date and the payment details are left for Simpl-IR.
    /// </summary>
    private static IEnumerable<XElement> Payments(IReadOnlyList<SimplIrPayslip> payslips) =>
        payslips.Where(p => PayrollCalculator.IsOnPayroll(p.ContractType))
                .GroupBy(p => p.Month)
                .Select(g => (Month: g.Key, Tax: g.Sum(p => p.Amounts.IncomeTax)))
                .Where(m => m.Tax > 0m)
                .OrderBy(m => m.Month)
                .Select(m => new XElement("VersementTraitementSalaire",
                    E("mois", Int(m.Month)),
                    E("totalVersement", Amount(m.Tax)),
                    E("dateDerniereVersment"),
                    new XElement("listDetailPaiement")));
}
