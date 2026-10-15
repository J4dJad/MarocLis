using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesHrTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Review cycle 3, payroll declarations: a CNSS number shared by two payslips refuses the BDS,
/// the état 9421 splits an employee's year by the contract of each payslip, an exempt stipend
/// declares no net taxable income, and an ANAPEC payslip warns of a missing CNSS number.
/// </summary>
public sealed class ReviewFixesCycle1HrTests
{
    private static readonly Guid Site = Guid.NewGuid();

    private static readonly InvoiceIssuer Employer = new("Labo", "LABO ANALYSES SARL", "12 rue X", "Rabat", "10000",
        "0537000000", "contact@labo.ma", "001234567000089", "12345678", "98765", "Rabat", "45678901", "1234567", null, null);

    private static Employee Person(string number, string last, string first, string? cnss) =>
        new(number, last, first, EmployeeJob.Technician, new DateOnly(2020, 1, 6), Site)
        {
            CnssNumber = cnss, Cin = "AB" + number, BaseSalary = 5000m, FamilyStatus = FamilyStatus.Unmarried
        };

    private static Payslip Slip(Employee e, ContractType type, decimal salary, PayrollParameters rates, DateOnly lastDay)
    {
        var slip = new Payslip(e.Id)
        {
            Number = e.Number, FullName = e.FullName, Cin = e.Cin, CnssNumber = e.CnssNumber, HireDate = e.HireDate,
            ContractType = type, BaseSalary = salary, DependentPersons = PayrollService.DependentPersons(e)
        };
        slip.Recompute(new PayslipVariables(), rates, lastDay);
        return slip;
    }

    private static byte[] Preestablished()
    {
        static string Field(string value, int width) => value.PadRight(width)[..width];
        var a01 = "A01" + "1234567" + "202604" + Field("LABO ANALYSES", 40) + Field("LABORATOIRE", 40) + Field("12 RUE X", 120)
                  + Field("RABAT", 20) + Field("10000", 6) + "01" + "20260501" + "20260510";
        string[] lines = ["A00" + "00000000004242" + "A0" + new string(' ', 241), a01, "A03" + "1234567" + "202604"];
        return Encoding.Latin1.GetBytes(string.Join("\r\n", lines.Select(l => l.PadRight(260))) + "\r\n");
    }

    // ---- H1: a CNSS number on two payslips refuses the BDS -----------------------------------------

    [Fact]
    public async Task H1_bds_is_refused_when_two_declared_payslips_share_a_cnss_number_and_lists_them()
    {
        var rates = new PayrollParameters(2026);
        var alami = Person("M1", "ALAMI", "Sara", "100000001");
        var benani = Person("M2", "BENANI", "Karim", "100 000 001");
        var tazi = Person("M3", "TAZI", "Omar", "100000003");
        var period = new PayrollPeriod(Site, 2026, 4);
        period.Add(Slip(alami, ContractType.Permanent, 4000m, rates, period.LastDay));
        period.Add(Slip(benani, ContractType.Anapec, 7000m, rates, period.LastDay));
        period.Add(Slip(tazi, ContractType.Permanent, 5000m, rates, period.LastDay));
        period.Close(DateTimeOffset.Now, Guid.NewGuid(), rates).IsSuccess.Should().BeTrue();

        var built = await Service([period], [alami, benani, tazi]).ExportBdsAsync(period.Id, Preestablished());

        built.IsFailure.Should().BeTrue();
        built.FirstError.ResourceKey.Should().Be("Payroll_BdsDuplicateCnss");
        built.FirstError.Args.Single().Should().Be("100000001 (M1 ALAMI Sara, M2 BENANI Karim)");
    }

    [Fact]
    public async Task H1_an_intern_sharing_a_number_is_not_declared_and_does_not_refuse_the_bds()
    {
        var rates = new PayrollParameters(2026);
        var alami = Person("M1", "ALAMI", "Sara", "100000001");
        var intern = Person("M2", "TAZI", "Omar", "100000001");
        var period = new PayrollPeriod(Site, 2026, 4);
        period.Add(Slip(alami, ContractType.Permanent, 4000m, rates, period.LastDay));
        period.Add(Slip(intern, ContractType.Internship, 2000m, rates, period.LastDay));
        period.Close(DateTimeOffset.Now, Guid.NewGuid(), rates).IsSuccess.Should().BeTrue();

        var built = await Service([period], [alami, intern]).ExportBdsAsync(period.Id, Preestablished());

        built.IsSuccess.Should().BeTrue();
        built.Value.Declared.Should().Be(1);
    }

    // ---- H2 / H3: état 9421 ----------------------------------------------------------------------

    private static readonly SimplIrCodes Codes = new("141.01.01", SimplIrCodes.DefaultLowIncomeRateCode,
                                                     SimplIrCodes.DefaultHighIncomeRateCode);

    private static SimplIrPayslip Line(int month, Guid id, ContractType type, decimal salary)
    {
        var amounts = PayrollCalculator.Compute(new PayslipInput(salary, new DateOnly(2020, 1, 6), new DateOnly(2025, month, 28),
                                                                 type, 0, new PayslipVariables()),
                                                new PayrollParameters(2025));
        return new SimplIrPayslip(month, id, "M-1", "IDRISSI", "Nadia", "3 rue Y", "AB1", "100000001",
                                  FamilyStatus.Unmarried, type, 0, false, amounts);
    }

    private static XElement Declaration(IReadOnlyList<SimplIrPayslip> lines) =>
        XDocument.Parse(Encoding.UTF8.GetString(SimplIrSalaryDeclaration.Build(Employer, 2025, lines, Codes))).Root!;

    private static decimal D(XElement e, string name) => decimal.Parse(e.Element(name)!.Value, CultureInfo.InvariantCulture);

    [Fact]
    public void H2_anapec_months_then_cdi_months_give_one_stagiaire_row_and_one_permanent_row()
    {
        var id = Guid.NewGuid();
        var lines = Enumerable.Range(1, 6).Select(m => Line(m, id, ContractType.Anapec, 4000m))
                              .Concat(Enumerable.Range(7, 6).Select(m => Line(m, id, ContractType.Permanent, 6000m)))
                              .ToList();
        var cdi = lines.Where(l => l.ContractType == ContractType.Permanent).ToList();
        var anapec = lines.Where(l => l.ContractType == ContractType.Anapec).ToList();

        var root = Declaration(lines);

        var permanent = root.Element("listPersonnelPermanent")!.Elements("PersonnelPermanent").Single();
        D(permanent, "mtRevenuBrutImposable").Should().Be(cdi.Sum(l => l.Amounts.GrossTaxable));
        D(permanent, "mtRevenuNetImposable").Should().Be(cdi.Sum(l => l.Amounts.NetTaxable));
        D(permanent, "irPreleve").Should().Be(cdi.Sum(l => l.Amounts.IncomeTax));
        D(permanent, "periode").Should().Be(cdi.Sum(l => l.Amounts.PaidDays));

        var stagiaire = root.Element("listStagiaires")!.Elements("Stagiaire").Single();
        D(stagiaire, "mtBrutTraitementSalaire").Should().Be(anapec.Sum(l => l.Amounts.GrossTaxable)).And.Be(24000m);
        D(stagiaire, "periode").Should().Be(anapec.Sum(l => l.Amounts.PaidDays));

        D(root, "nbrPersoPermanent").Should().Be(1);
        D(root, "nbrStagiaires").Should().Be(1);
        D(root, "totalMtRevenuBrutImposablePP").Should().Be(cdi.Sum(l => l.Amounts.GrossTaxable));
        D(root, "totalMtBrutTraitSalaireSTG").Should().Be(24000m);
    }

    [Fact]
    public void H2_cdi_months_then_anapec_months_keep_the_taxed_salary_in_the_permanent_list()
    {
        var id = Guid.NewGuid();
        var lines = Enumerable.Range(1, 3).Select(m => Line(m, id, ContractType.Permanent, 6000m))
                              .Concat(Enumerable.Range(4, 9).Select(m => Line(m, id, ContractType.Anapec, 4000m)))
                              .ToList();

        var root = Declaration(lines);

        var permanent = root.Element("listPersonnelPermanent")!.Elements("PersonnelPermanent").Single();
        D(permanent, "irPreleve").Should().Be(lines.Take(3).Sum(l => l.Amounts.IncomeTax));
        D(root.Element("listStagiaires")!.Elements("Stagiaire").Single(), "mtBrutTraitementSalaire").Should().Be(9 * 4000m);
    }

    [Fact]
    public void H3_an_exempt_stipend_declares_no_net_taxable_income()
    {
        var anapec = Enumerable.Range(1, 6).Select(m => Line(m, Guid.NewGuid(), ContractType.Anapec, 4000m)).ToList();
        var intern = Line(3, Guid.NewGuid(), ContractType.Internship, 2000m);
        anapec[0].Amounts.NetTaxable.Should().BePositive("the calculator still computes it");
        anapec[0].Amounts.IncomeTax.Should().Be(0m);

        var root = Declaration([anapec[0], intern]);

        root.Element("listStagiaires")!.Elements("Stagiaire").Should().HaveCount(2)
            .And.OnlyContain(s => D(s, "mtRevenuNetImposable") == 0m);
        D(root, "totalMtRevenuNetImpSTG").Should().Be(0m);
        D(root, "totalMtBrutTraitSalaireSTG").Should().Be(6000m);
    }

    // ---- H4: missing CNSS number warned on every declared payslip --------------------------------

    [Fact]
    public void H4_a_payslip_without_cnss_number_is_warned_unless_it_is_an_internship()
    {
        var rates = new PayrollParameters(2026);
        var lastDay = new DateOnly(2026, 4, 30);
        var nobody = Person("M1", "ALAMI", "Sara", null);

        PayrollService.Warnings(Slip(nobody, ContractType.Anapec, 4000m, rates, lastDay), rates)
                      .Should().Contain(PayslipWarning.NoCnssNumber).And.NotContain(PayslipWarning.BelowMinimumWage);
        PayrollService.Warnings(Slip(nobody, ContractType.Permanent, 5000m, rates, lastDay), rates)
                      .Should().Contain(PayslipWarning.NoCnssNumber);
        PayrollService.Warnings(Slip(nobody, ContractType.Internship, 2000m, rates, lastDay), rates)
                      .Should().NotContain(PayslipWarning.NoCnssNumber);

        var numbered = Person("M2", "BENANI", "Karim", "100000002");
        PayrollService.Warnings(Slip(numbered, ContractType.Anapec, 4000m, rates, lastDay), rates)
                      .Should().NotContain(PayslipWarning.NoCnssNumber);
    }

    // ---- Fakes -----------------------------------------------------------------------------------

    private static PayrollService Service(PayrollPeriod[] periods, Employee[] staff)
    {
        var periodRepo = new InMemoryRepository<PayrollPeriod>();
        periodRepo.Items.AddRange(periods);
        var rates = new InMemoryRepository<PayrollParameters>();
        rates.Items.Add(new PayrollParameters(2026));
        return new PayrollService(periodRepo, rates, Repo(staff), new Queries(Employer),
                                  new InMemoryRepository<FinalSettlement>(), new InMemoryRepository<LeaveLedgerEntry>(),
                                  new FakeHrDocumentQueries(), new FakeUnitOfWork(), new AllowAll(), new User(),
                                  new FixedClock());
    }

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
    }

    private sealed class User : ICurrentUser
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
