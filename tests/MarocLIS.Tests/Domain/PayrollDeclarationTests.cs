using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesHrTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// The declaration files: the CNSS e-BDS built from DAMANCOM's préétabli, and the état 9421
/// (Simpl-IR XML) of the year's salaries.
/// </summary>
public sealed class PayrollDeclarationTests
{
    private static readonly Guid Site = Guid.NewGuid();

    // ---- e-BDS: a synthetic préétabli ---------------------------------------------------------------

    private static string Field(string value, int width) => value.PadRight(width)[..width];
    private static string Zero(long value, int width) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    private static string A01(string affiliate = "1234567", string period = "202603") =>
        "A01" + affiliate + period + Field("LABO ANALYSES", 40) + Field("LABORATOIRE", 40) + Field("12 RUE X", 120)
        + Field("RABAT", 20) + Field("10000", 6) + "01" + "20260401" + "20260410";

    private static string A02(long insured, string name, int children, long pay, long deduct, long net,
                              string affiliate = "1234567", string period = "202603") =>
        "A02" + affiliate + period + Zero(insured, 9) + Field(name, 60) + Zero(children, 2) + Zero(pay, 6) + Zero(deduct, 6)
        + Zero(net, 6);

    private static byte[] Preestablished(params string[] insured)
    {
        var lines = new List<string> { "A00" + "00000000004242" + "A0" + new string(' ', 241), A01() };
        lines.AddRange(insured);
        lines.Add("A03" + "1234567" + "202603");
        return Encoding.Latin1.GetBytes(string.Join("\r\n", lines.Select(l => l.PadRight(260))) + "\r\n");
    }

    private static BdsPreestablished Parsed(params string[] insured)
    {
        var parsed = CnssBdsFile.Parse(Preestablished(insured));
        parsed.IsSuccess.Should().BeTrue();
        return parsed.Value;
    }

    private static string[] Records(byte[] bds)
    {
        var text = Encoding.ASCII.GetString(bds);
        text.Should().EndWith("\r\n");
        text.Replace("\r\n", string.Empty).Should().NotContain("\n").And.NotContain("\r");
        return text[..^2].Split("\r\n");
    }

    private static long N(string record, int start, int length) =>
        long.Parse(record.Substring(start, length), CultureInfo.InvariantCulture);

    [Fact]
    public void Bds_preestablished_is_read_field_by_field()
    {
        var file = Parsed(A02(100000002, "BENANI KARIM", 2, 30000, 0, 30000), A02(100000001, "ALAMI SARA", 0, 0, 0, 0));

        file.TransferId.Should().Be("00000000004242");
        file.AffiliateNumber.Should().Be("1234567");
        (file.Year, file.Month).Should().Be((2026, 3));
        file.Insured.Should().HaveCount(2);
        file.Insured[0].Should().Be(new BdsInsured(100000002, "BENANI KARIM", 2, 30000, 0, 30000));
    }

    [Fact]
    public void Bds_preestablished_that_is_not_one_is_refused_with_its_line()
    {
        var garbage = Encoding.ASCII.GetBytes("A00" + "00000000004242A0\r\n" + A01() + "\r\nHELLO WORLD\r\n");
        var parsed = CnssBdsFile.Parse(garbage);

        parsed.IsFailure.Should().BeTrue();
        parsed.FirstError.ResourceKey.Should().Be("Payroll_BdsFileInvalid");
        parsed.FirstError.Args.Should().Equal(3);

        CnssBdsFile.Parse(Encoding.ASCII.GetBytes("nothing")).FirstError.ResourceKey.Should().Be("Payroll_BdsFileInvalid");
        CnssBdsFile.Parse([]).FirstError.ResourceKey.Should().Be("Payroll_BdsFileNoHeader");
    }

    [Fact]
    public void Bds_preestablished_of_another_affiliate_or_month_is_refused()
    {
        var file = Parsed();

        CnssBdsFile.Matches(file, "123 4567", 2026, 3).IsSuccess.Should().BeTrue();
        CnssBdsFile.Matches(file, "01234567", 2026, 3).IsSuccess.Should().BeTrue();
        CnssBdsFile.Matches(file, null, 2026, 3).FirstError.ResourceKey.Should().Be("Payroll_BdsNoAffiliation");
        CnssBdsFile.Matches(file, "7654321", 2026, 3).FirstError.ResourceKey.Should().Be("Payroll_BdsWrongAffiliate");

        var period = CnssBdsFile.Matches(file, "1234567", 2026, 4);
        period.FirstError.ResourceKey.Should().Be("Payroll_BdsWrongPeriod");
        period.FirstError.Args.Should().Equal("03/2026", "04/2026");
    }

    [Fact]
    public void Bds_records_are_260_ascii_characters_in_the_spec_order_with_their_control_sums()
    {
        var file = Parsed(A02(100000002, "BENANI KARIM", 2, 30000, 0, 30000), A02(100000001, "ALAMI SARA", 0, 0, 0, 0),
                          A02(100000003, "TAZI OMAR", 1, 15000, 0, 15000));

        var bds = CnssBdsFile.Build(file,
        [
            // Above the ceiling: capped at 6,000 DH.
            new BdsSalary(100000002, "Benani Karim", "AB123456", 26, 8250.50m, 6000m),
            new BdsSalary(100000001, "Alami Sara", "X1", 30, 4000m, 4000m),
            // Not in the préétabli: an entrant, name without accents in upper case.
            new BdsSalary(100000009, "Él-Fassi Hélène", "BE998877", 12, 2100.25m, 2100.25m)
        ]);

        var records = Records(bds);
        records.Should().OnlyContain(r => r.Length == CnssBdsFile.RecordLength);
        records.Select(r => r[..3]).Should().Equal("B00", "B01", "B02", "B02", "B02", "B03", "B04", "B05", "B06");
        bds.Should().OnlyContain(b => b < 0x80);

        records[0].Substring(3, 14).Should().Be("00000000004242");
        records[0].Substring(17, 2).Should().Be("B0");
        records[1][3..].Should().Be(A01()[3..].PadRight(257));

        // B02 sorted by insured number; days at most 26; amounts in centimes.
        var alami = records[2];
        N(alami, 16, 9).Should().Be(100000001);
        alami.Substring(25, 60).TrimEnd().Should().Be("ALAMI SARA");
        N(alami, 111, 2).Should().Be(26);
        N(alami, 113, 13).Should().Be(400000);
        N(alami, 126, 9).Should().Be(400000);
        alami.Substring(135, 2).Should().Be("  ");
        N(alami, 137, 19).Should().Be(100000001 + 0 + 26 + 400000 + 400000);

        var benani = records[3];
        N(benani, 85, 2).Should().Be(2);
        N(benani, 87, 6).Should().Be(30000);
        N(benani, 99, 6).Should().Be(30000);
        N(benani, 105, 6).Should().Be(0);
        N(benani, 113, 13).Should().Be(825050);
        N(benani, 126, 9).Should().Be(600000);

        // Expected but not paid: left, no day, no salary, SO counted 1 in the control sum.
        var tazi = records[4];
        N(tazi, 111, 2).Should().Be(0);
        N(tazi, 113, 13).Should().Be(0);
        tazi.Substring(135, 2).Should().Be("SO");
        N(tazi, 137, 19).Should().Be(100000003 + 1);

        var b02Control = new[] { alami, benani, tazi }.Sum(r => N(r, 137, 19));
        var b03 = records[5];
        N(b03, 16, 6).Should().Be(3);
        N(b03, 22, 6).Should().Be(3);
        N(b03, 28, 12).Should().Be(45000);
        N(b03, 52, 12).Should().Be(45000);
        N(b03, 64, 15).Should().Be(100000001 + 100000002 + 100000003);
        N(b03, 79, 12).Should().Be(0);
        N(b03, 91, 6).Should().Be(52);
        N(b03, 97, 15).Should().Be(1225050);
        N(b03, 112, 13).Should().Be(1000000);
        N(b03, 125, 19).Should().Be(b02Control);

        var b04 = records[6];
        N(b04, 16, 9).Should().Be(100000009);
        b04.Substring(25, 60).TrimEnd().Should().Be("EL-FASSI HELENE");
        b04.Substring(85, 8).Should().Be("BE998877");
        N(b04, 93, 2).Should().Be(12);
        N(b04, 95, 13).Should().Be(210025);
        N(b04, 108, 9).Should().Be(210025);
        N(b04, 117, 19).Should().Be(100000009 + 12 + 210025 + 210025);

        var b05 = records[7];
        N(b05, 16, 6).Should().Be(1);
        N(b05, 22, 15).Should().Be(100000009);
        N(b05, 71, 19).Should().Be(N(b04, 117, 19));

        var b06 = records[8];
        N(b06, 16, 6).Should().Be(4);
        N(b06, 22, 15).Should().Be(N(b03, 64, 15) + 100000009);
        N(b06, 37, 6).Should().Be(52 + 12);
        N(b06, 43, 15).Should().Be(1225050 + 210025);
        N(b06, 58, 13).Should().Be(1000000 + 210025);
        N(b06, 71, 19).Should().Be(b02Control + N(b04, 117, 19));
    }

    [Fact]
    public void Bds_without_entrant_still_has_its_entrant_recap_at_zero_and_situations_are_coded()
    {
        var file = Parsed(A02(100000001, "ALAMI SARA", 0, 0, 0, 0), A02(100000002, "BENANI KARIM", 0, 0, 0, 0));

        var records = Records(CnssBdsFile.Build(file,
            [new BdsSalary(100000001, "Alami Sara", null, 0, 0m, 0m, BdsSituation.WorkAccident)],
            new Dictionary<long, BdsSituation> { [100000002] = BdsSituation.Deceased }));

        records.Select(r => r[..3]).Should().Equal("B00", "B01", "B02", "B02", "B03", "B05", "B06");
        records[2].Substring(135, 2).Should().Be("AT");
        N(records[2], 137, 19).Should().Be(100000001 + 5);
        records[3].Substring(135, 2).Should().Be("DE");
        N(records[3], 137, 19).Should().Be(100000002 + 2);
        records[5].Substring(16).Trim().Should().MatchRegex("^0+$");
    }

    [Fact]
    public void Bds_situation_codes_follow_the_spec_table()
    {
        CnssBdsFile.Situations.Select(s => (s.Value.Code, s.Value.Value)).Should().Equal(
            ("  ", 0), ("SO", 1), ("DE", 2), ("IT", 3), ("IL", 4), ("AT", 5), ("CS", 6), ("MS", 7), ("MP", 8));
    }

    // ---- e-BDS through the payroll -----------------------------------------------------------------

    private static readonly InvoiceIssuer Employer = new("Labo", "LABO ANALYSES SARL", "12 rue X", "Rabat", "10000",
        "0537000000", "contact@labo.ma", "001234567000089", "12345678", "98765", "Rabat", "45678901", "1234567", null, null);

    private static Payslip Slip(Employee e, ContractType type, decimal salary, PayrollParameters rates, DateOnly lastDay,
                                PayslipVariables? variables = null)
    {
        var slip = new Payslip(e.Id)
        {
            Number = e.Number, FullName = e.FullName, Cin = e.Cin, CnssNumber = e.CnssNumber, HireDate = e.HireDate,
            ContractType = type, BaseSalary = salary, DependentPersons = PayrollService.DependentPersons(e)
        };
        slip.Recompute(variables ?? new PayslipVariables(), rates, lastDay);
        return slip;
    }

    private static Employee Person(string number, string last, string first, string? cnss, ContractType type = ContractType.Permanent)
        => new(number, last, first, EmployeeJob.Technician, new DateOnly(2020, 1, 6), Site)
        {
            CnssNumber = cnss, Cin = "AB" + number, ContractType = type, BaseSalary = 5000m, Address = "3 rue Y",
            City = "Salé", FamilyStatus = FamilyStatus.Married, DependentChildren = 2
        };

    private static PayrollPeriod Month(int year, int month, PayrollParameters rates, bool close, params (Employee E, ContractType T, decimal Salary)[] staff)
    {
        var period = new PayrollPeriod(Site, year, month);
        foreach (var (e, t, salary) in staff) period.Add(Slip(e, t, salary, rates, period.LastDay));
        if (close) period.Close(DateTimeOffset.Now, Guid.NewGuid(), rates).IsSuccess.Should().BeTrue();
        return period;
    }

    private static PayrollService Service(InvoiceIssuer employer, PayrollPeriod[] periods, Employee[] staff)
    {
        var periodRepo = new InMemoryRepository<PayrollPeriod>();
        periodRepo.Items.AddRange(periods);
        var rates = new InMemoryRepository<PayrollParameters>();
        rates.Items.Add(new PayrollParameters(2026));
        rates.Items.Add(new PayrollParameters(2025));
        return new PayrollService(periodRepo, rates, Repo(staff), new Queries(employer),
                                  new InMemoryRepository<FinalSettlement>(), new InMemoryRepository<LeaveLedgerEntry>(),
                                  new FakeHrDocumentQueries(), new FakeUnitOfWork(), new AllowAll(), new User(),
                                  new FixedClock());
    }

    [Fact]
    public async Task Bds_of_the_payroll_declares_the_paid_months_and_excludes_interns()
    {
        var rates = new PayrollParameters(2026);
        var alami = Person("M1", "ALAMI", "Sara", "100000001");
        var newcomer = Person("M2", "BENANI", "Karim", "100000009");
        var intern = Person("M3", "TAZI", "Omar", null, ContractType.Internship);
        var period = Month(2026, 3, rates, close: true, (alami, ContractType.Permanent, 5000m),
                           (newcomer, ContractType.Anapec, 4000m), (intern, ContractType.Internship, 2000m));

        var payroll = Service(Employer, [period], [alami, newcomer, intern]);
        var built = await payroll.ExportBdsAsync(period.Id, Preestablished(A02(100000001, "ALAMI SARA", 0, 0, 0, 0)));

        built.IsSuccess.Should().BeTrue();
        built.Value.FileName.Should().Be("DS_1234567_202603.txt");
        (built.Value.Declared, built.Value.Entrants).Should().Be((2, 1));

        var records = Records(built.Value.Content);
        records.Select(r => r[..3]).Should().Equal("B00", "B01", "B02", "B03", "B04", "B05", "B06");
        // The gross subject to the CNSS (base and seniority), capped at 6,000 DH.
        var gross = period.Payslips.First(p => p.EmployeeId == alami.Id).Amounts.GrossTaxable;
        gross.Should().Be(5500m);
        N(records[2], 113, 13).Should().Be(550000);
        N(records[2], 126, 9).Should().Be(550000);
        N(records[2], 111, 2).Should().Be(26);
        N(records[4], 16, 9).Should().Be(100000009);
    }

    [Fact]
    public async Task Bds_is_refused_for_a_draft_month_a_wrong_preestablished_or_a_payslip_without_cnss_number()
    {
        var rates = new PayrollParameters(2026);
        var alami = Person("M1", "ALAMI", "Sara", "100000001");
        var nobody = Person("M2", "BENANI", "Karim", null);
        var draft = Month(2026, 3, rates, close: false, (alami, ContractType.Permanent, 5000m));
        var closed = Month(2026, 4, rates, close: true, (alami, ContractType.Permanent, 5000m), (nobody, ContractType.FixedTerm, 4000m));
        var payroll = Service(Employer, [draft, closed], [alami, nobody]);

        (await payroll.ExportBdsAsync(draft.Id, Preestablished())).FirstError.ResourceKey.Should().Be("Payroll_BdsNotClosed");
        (await payroll.ExportBdsAsync(closed.Id, Preestablished())).FirstError.ResourceKey.Should().Be("Payroll_BdsWrongPeriod");

        var april = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Preestablished()).Replace("202603", "202604"));
        var missing = await payroll.ExportBdsAsync(closed.Id, april);
        missing.FirstError.ResourceKey.Should().Be("Payroll_BdsMissingCnss");
        missing.FirstError.Args.Single().Should().Be("M2 BENANI Karim");

        var noAffiliate = Service(Employer with { CnssAffiliationNumber = null }, [closed], [alami, nobody]);
        (await noAffiliate.ExportBdsAsync(closed.Id, april)).FirstError.ResourceKey.Should().Be("Payroll_BdsNoAffiliation");
    }

    [Fact]
    public void Bds_situation_of_a_payslip_follows_the_departure_and_the_whole_month_stoppages()
    {
        var rates = new PayrollParameters(2026);
        var lastDay = new DateOnly(2026, 3, 31);
        var e = Person("M1", "ALAMI", "Sara", "100000001");

        var worked = Slip(e, ContractType.Permanent, 5000m, rates, lastDay);
        PayrollService.BdsSituationOf(e, worked, rates, lastDay).Should().Be(BdsSituation.Normal);

        var accident = Slip(e, ContractType.Permanent, 5000m, rates, lastDay,
                            new PayslipVariables { AbsenceDays = 26, WorkAccidentDays = 26 });
        accident.Amounts.PaidDays.Should().Be(0);
        PayrollService.BdsSituationOf(e, accident, rates, lastDay).Should().Be(BdsSituation.WorkAccident);

        var unpaid = Slip(e, ContractType.Permanent, 5000m, rates, lastDay, new PayslipVariables { AbsenceDays = 26 });
        PayrollService.BdsSituationOf(e, unpaid, rates, lastDay).Should().Be(BdsSituation.UnpaidLeave);

        e.RecordExit(new DateOnly(2026, 3, 20), ExitReason.Resignation, null).IsSuccess.Should().BeTrue();
        PayrollService.BdsSituationOf(e, worked, rates, lastDay).Should().Be(BdsSituation.Left);
    }

    // ---- État 9421 (Simpl-IR) ------------------------------------------------------------------------

    private static readonly SimplIrCodes Codes = new("141.01.01", SimplIrCodes.DefaultLowIncomeRateCode,
                                                     SimplIrCodes.DefaultHighIncomeRateCode);

    private static readonly string[] RootOrder =
    [
        "identifiantFiscal", "nom", "prenom", "raisonSociale", "exerciceFiscalDu", "exerciceFiscalAu", "annee", "commune",
        "adresse", "numeroCIN", "numeroCNSS", "numeroCE", "numeroRC", "identifiantTP", "numeroFax", "numeroTelephone", "email",
        "effectifTotal", "nbrPersoPermanent", "nbrPersoOccasionnel", "nbrStagiaires", "totalMtRevenuBrutImposablePP",
        "totalMtRevenuNetImposablePP", "totalMtTotalDeductionPP", "totalMtIrPrelevePP", "totalMtBrutSommesPO",
        "totalIrPrelevePO", "totalMtBrutTraitSalaireSTG", "totalMtBrutIndemnitesSTG", "totalMtRetenuesSTG",
        "totalMtRevenuNetImpSTG", "totalSommePayeRTS", "totalmtAnuuelRevenuSalarial", "totalmtAbondement",
        "montantPermanent", "montantOccasionnel", "montantStagiaire", "referenceDeclaration", "listPersonnelPermanent",
        "listPersonnelOccasionnel", "listStagiaires", "listBeneficiaires", "listBeneficiairesPlanEpargne", "listVersements"
    ];

    private static readonly string[] PermanentOrder =
    [
        "nom", "prenom", "adressePersonnelle", "numCNI", "numCE", "numPPR", "numCNSS", "ifu", "mtBrutTraitementSalaire",
        "periode", "mtExonere", "mtEcheances", "nbrReductions", "mtIndemnite", "mtAvantages", "mtRevenuBrutImposable",
        "mtFraisProfess", "mtCotisationAssur", "mtAutresRetenues", "mtRevenuNetImposable", "mtTotalDeduction", "irPreleve",
        "casSportif", "numMatricule", "datePermis", "dateAutorisation", "refSituationFamiliale", "refTaux"
    ];

    private static readonly string[] StagiaireOrder =
    [
        "nom", "prenom", "adressePersonnelle", "numCNI", "numCE", "numCNSS", "ifu", "mtBrutTraitementSalaire",
        "mtBrutIndemnites", "mtRetenues", "mtRevenuNetImposable", "periode"
    ];

    private static SimplIrPayslip Line(int month, Guid id, string last, ContractType type, decimal salary,
                                       FamilyStatus status = FamilyStatus.Unmarried, int dependants = 0,
                                       decimal transport = 0m)
    {
        // An ANAPEC contract within the 24 months of its exemption.
        var hired = type == ContractType.Anapec ? new DateOnly(2025, 1, 2) : new DateOnly(2020, 1, 6);
        var amounts = PayrollCalculator.Compute(new PayslipInput(salary, hired, new DateOnly(2025, month, 28),
                                                                 type, dependants, new PayslipVariables { TransportAllowance = transport }),
                                                new PayrollParameters(2025));
        return new SimplIrPayslip(month, id, "M-" + last, last, "Prénom", "3 rue Y, Salé", "AB1", "100000001", status, type,
                                  dependants, salary * 12 > 78000m, amounts);
    }

    private static XElement Declaration(IReadOnlyList<SimplIrPayslip> lines) =>
        XDocument.Parse(Encoding.UTF8.GetString(SimplIrSalaryDeclaration.Build(Employer, 2025, lines, Codes))).Root!;

    private static decimal D(XElement e, string name) => decimal.Parse(e.Element(name)!.Value, CultureInfo.InvariantCulture);

    [Fact]
    public void SimplIr_elements_come_in_the_official_order_empty_rather_than_left_out()
    {
        var root = Declaration([Line(1, Guid.NewGuid(), "ALAMI", ContractType.Permanent, 9000m),
                                Line(1, Guid.NewGuid(), "TAZI", ContractType.Internship, 2000m)]);

        root.Name.LocalName.Should().Be("TraitementEtSalaire");
        root.GetNamespaceOfPrefix("xsi")!.NamespaceName.Should().Be("http://www.w3.org/2001/XMLSchema-instance");
        root.Elements().Select(e => e.Name.LocalName).Should().Equal(RootOrder);
        root.Element("nom")!.Value.Should().BeEmpty();
        root.Element("listPersonnelOccasionnel")!.HasElements.Should().BeFalse();
        root.Element("exerciceFiscalDu")!.Value.Should().Be("2025-01-01");
        root.Element("exerciceFiscalAu")!.Value.Should().Be("2025-12-31");
        root.Element("commune")!.Element("code")!.Value.Should().Be("141.01.01");
        root.Element("identifiantFiscal")!.Value.Should().Be("12345678");
        root.Element("numeroCNSS")!.Value.Should().Be("1234567");

        root.Element("listPersonnelPermanent")!.Elements("PersonnelPermanent").Single()
            .Elements().Select(e => e.Name.LocalName).Should().Equal(PermanentOrder);
        root.Element("listStagiaires")!.Elements("Stagiaire").Single()
            .Elements().Select(e => e.Name.LocalName).Should().Equal(StagiaireOrder);
    }

    [Fact]
    public void SimplIr_is_utf8_with_decimal_points_and_two_decimals()
    {
        var bytes = SimplIrSalaryDeclaration.Build(Employer with { LegalName = "Laboratoire Hélène" }, 2025,
                                                   [Line(1, Guid.NewGuid(), "ALAMI", ContractType.Permanent, 5000.5m)], Codes);

        bytes.Take(3).Should().NotEqual(Encoding.UTF8.GetPreamble());
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        text.Should().Contain("<raisonSociale>Laboratoire Hélène</raisonSociale>");
        // 5,000.50 and 10 % seniority.
        text.Should().Contain("<mtBrutTraitementSalaire>5500.55</mtBrutTraitementSalaire>");
        text.Should().Contain("<totalMtBrutSommesPO>0.00</totalMtBrutSommesPO>");
    }

    [Fact]
    public void SimplIr_sums_the_year_per_employee_splits_the_stagiaires_and_totals_the_lists()
    {
        var alami = Guid.NewGuid();
        var benani = Guid.NewGuid();
        var tazi = Guid.NewGuid();
        var idmaj = Guid.NewGuid();
        List<SimplIrPayslip> lines = [];
        for (var month = 1; month <= 12; month++)
        {
            lines.Add(Line(month, alami, "ALAMI", ContractType.Permanent, 9000m, FamilyStatus.Married, 8, transport: 300m));
            if (month <= 6) lines.Add(Line(month, benani, "BENANI", ContractType.FixedTerm, 5000m, FamilyStatus.Divorced, 1));
        }
        lines.Add(Line(3, tazi, "TAZI", ContractType.Internship, 2000m));
        lines.Add(Line(4, idmaj, "IDRISSI", ContractType.Anapec, 4000m));
        lines.Add(Line(4, Guid.NewGuid(), "PARTNER", ContractType.Partner, 30000m));

        var root = Declaration(lines);

        var permanent = root.Element("listPersonnelPermanent")!.Elements().ToList();
        var stagiaires = root.Element("listStagiaires")!.Elements().ToList();
        permanent.Select(p => p.Element("nom")!.Value).Should().Equal("ALAMI", "BENANI");
        stagiaires.Select(p => p.Element("nom")!.Value).Should().Equal("IDRISSI", "TAZI");
        D(root, "effectifTotal").Should().Be(4);
        D(root, "nbrPersoPermanent").Should().Be(2);
        D(root, "nbrPersoOccasionnel").Should().Be(0);
        D(root, "nbrStagiaires").Should().Be(2);

        var a = permanent[0];
        var alamiSlips = lines.Where(l => l.EmployeeId == alami).ToList();
        D(a, "mtBrutTraitementSalaire").Should().Be(alamiSlips.Sum(l => l.Amounts.Gross));
        D(a, "mtExonere").Should().Be(12 * 300m);
        D(a, "mtRevenuBrutImposable").Should().Be(alamiSlips.Sum(l => l.Amounts.GrossTaxable));
        D(a, "mtCotisationAssur").Should().Be(alamiSlips.Sum(l => l.Amounts.CnssEmployee + l.Amounts.AmoEmployee));
        D(a, "mtTotalDeduction").Should().Be(D(a, "mtFraisProfess") + D(a, "mtCotisationAssur") + D(a, "mtAutresRetenues"));
        D(a, "irPreleve").Should().Be(alamiSlips.Sum(l => l.Amounts.IncomeTax));
        D(a, "periode").Should().Be(12 * 26);
        D(a, "nbrReductions").Should().Be(6);
        a.Element("refSituationFamiliale")!.Element("code")!.Value.Should().Be("M");
        a.Element("refTaux")!.Element("code")!.Value.Should().Be("TPP.25.2023");
        permanent[1].Element("refSituationFamiliale")!.Element("code")!.Value.Should().Be("D");
        permanent[1].Element("refTaux")!.Element("code")!.Value.Should().Be("TPP.35.2023");
        a.Element("casSportif")!.Value.Should().Be("false");

        decimal Sum(IEnumerable<XElement> rows, string name) => rows.Sum(r => D(r, name));
        D(root, "totalMtRevenuBrutImposablePP").Should().Be(Sum(permanent, "mtRevenuBrutImposable"));
        D(root, "totalMtRevenuNetImposablePP").Should().Be(Sum(permanent, "mtRevenuNetImposable"));
        D(root, "totalMtTotalDeductionPP").Should().Be(Sum(permanent, "mtTotalDeduction"));
        D(root, "totalMtIrPrelevePP").Should().Be(Sum(permanent, "irPreleve"));
        D(root, "montantPermanent").Should().Be(Sum(permanent, "irPreleve"));
        D(root, "totalMtBrutTraitSalaireSTG").Should().Be(Sum(stagiaires, "mtBrutTraitementSalaire")).And.Be(6000m);
        D(root, "totalMtBrutIndemnitesSTG").Should().Be(Sum(stagiaires, "mtBrutIndemnites"));
        D(root, "totalMtRetenuesSTG").Should().Be(Sum(stagiaires, "mtRetenues"));
        D(root, "totalMtRevenuNetImpSTG").Should().Be(Sum(stagiaires, "mtRevenuNetImposable"));

        // The tax of each month, as withheld; nothing about its payment to the DGI.
        var payments = root.Element("listVersements")!.Elements("VersementTraitementSalaire").ToList();
        payments.Select(p => D(p, "mois")).Should().Equal(Enumerable.Range(1, 12).Select(m => (decimal)m));
        payments.Sum(p => D(p, "totalVersement")).Should().Be(Sum(permanent, "irPreleve"));
        payments[0].Elements().Select(e => e.Name.LocalName)
                   .Should().Equal("mois", "totalVersement", "dateDerniereVersment", "listDetailPaiement");
        payments[0].Element("dateDerniereVersment")!.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task SimplIr_is_refused_while_a_month_of_the_year_is_open_and_built_from_the_closed_ones()
    {
        var rates = new PayrollParameters(2025);
        var alami = Person("M1", "ALAMI", "Sara", "100000001");
        var january = Month(2025, 1, rates, close: true, (alami, ContractType.Permanent, 9000m));
        var february = Month(2025, 2, rates, close: false, (alami, ContractType.Permanent, 9000m));
        var march = Month(2025, 3, rates, close: false, (alami, ContractType.Permanent, 9000m));

        var open = await Service(Employer, [january, february, march], [alami]).ExportSimplIrAsync(2025, Codes);
        open.FirstError.ResourceKey.Should().Be("Payroll_SimplIrOpenMonths");
        open.FirstError.Args.Should().Equal(2025, "02/2025, 03/2025");

        var payroll = Service(Employer, [january], [alami]);
        (await payroll.ExportSimplIrAsync(2024, Codes)).FirstError.ResourceKey.Should().Be("Payroll_SimplIrNothing");
        (await Service(Employer with { FiscalId = " " }, [january], [alami]).ExportSimplIrAsync(2025, Codes))
            .FirstError.ResourceKey.Should().Be("Payroll_SimplIrNoFiscalId");

        var built = await payroll.ExportSimplIrAsync(2025, Codes);
        built.IsSuccess.Should().BeTrue();
        built.Value.FileName.Should().Be("TraitementEtSalaire_12345678_2025.xml");
        var root = XDocument.Parse(Encoding.UTF8.GetString(built.Value.Content)).Root!;
        var person = root.Element("listPersonnelPermanent")!.Elements().Single();
        person.Element("nom")!.Value.Should().Be("ALAMI");
        person.Element("prenom")!.Value.Should().Be("Sara");
        person.Element("adressePersonnelle")!.Value.Should().Be("3 rue Y, Salé");
        person.Element("nbrReductions")!.Value.Should().Be("3");
        D(person, "irPreleve").Should().Be(january.Payslips.Single().Amounts.IncomeTax);
    }

    // ---- Fakes ---------------------------------------------------------------------------------------

    private sealed class Queries(InvoiceIssuer employer) : IPayrollQueries
    {
        public Task<IReadOnlyList<PayrollPeriodRow>> GetPeriodsAsync(Guid siteId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PayrollPeriodRow>>([]);
        public Task<Guid?> FindPeriodAsync(Guid siteId, int year, int month, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<Guid>> DraftPeriodIdsAsync(int year, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<PayrollCandidate>> CandidatesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                     decimal monthlyWorkingDays, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PayrollCandidate>>([]);
        public Task<InvoiceIssuer> EmployerAsync(Guid siteId, CancellationToken ct = default) => Task.FromResult(employer);
        public Task<IReadOnlyDictionary<Guid, SettledAmounts>> SettlementsAsync(DateOnly from, DateOnly to,
                                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SettledAmounts>>(new Dictionary<Guid, SettledAmounts>());
        public Task<EmployerOtherSites> EmployerOtherSitesAsync(Guid siteId, EmployerIdentifier by, int year, int? month,
                                                                CancellationToken ct = default) =>
            Task.FromResult(EmployerOtherSites.None);
    }

    private sealed class User : MarocLIS.Application.Abstractions.ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "rh";
        public string FullName => "RH";
        public Guid SiteId => Site;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [Site];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { global::MarocLIS.Domain.Identity.Permissions.HumanResources.Salary };
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "TEST";
    }
}
