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
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesHrTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Review cycle 4, payroll: the BDS and the état 9421 are the employer's, built from every site
/// sharing its CNSS affiliation number or identifiant fiscal (H1); a Tahfiz salary within
/// 10,000 DH is declared exempt (H2); the ANAPEC stipend is exempt up to 6,000 DH and for
/// 24 months only (H3); the month of a re-hire keeps the previous contract's type (H4).
/// </summary>
public sealed class ReviewFixesCycle2HrTests
{
    private static readonly Guid Main = Guid.NewGuid();
    private static readonly Guid Centre = Guid.NewGuid();

    private static readonly InvoiceIssuer Employer = new("Labo", "LABO ANALYSES SARL", "12 rue X", "Rabat", "10000",
        "0537000000", "contact@labo.ma", "001234567000089", "12345678", "98765", "Rabat", "45678901", "1234567", null, null);

    private static Employee Person(string number, string last, string cnss, Guid site) =>
        new(number, last, "Prénom", EmployeeJob.Technician, new DateOnly(2025, 6, 2), site)
        {
            CnssNumber = cnss, Cin = "AB" + number, BaseSalary = 5000m, FamilyStatus = FamilyStatus.Unmarried
        };

    private static PayrollPeriod Month(Guid site, int month, bool close, params Employee[] staff)
    {
        var rates = new PayrollParameters(2026);
        var period = new PayrollPeriod(site, 2026, month);
        foreach (var e in staff)
        {
            var slip = new Payslip(e.Id)
            {
                Number = e.Number, FullName = e.FullName, Cin = e.Cin, CnssNumber = e.CnssNumber, HireDate = e.HireDate,
                ContractType = ContractType.Permanent, BaseSalary = 5000m
            };
            slip.Recompute(new PayslipVariables(), rates, period.LastDay);
            period.Add(slip);
        }

        if (close) period.Close(DateTimeOffset.Now, Guid.NewGuid(), rates).IsSuccess.Should().BeTrue();
        return period;
    }

    private static string Field(string value, int width) => value.PadRight(width)[..width];
    private static string Zero(long value, int width) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    private static byte[] Preestablished(params (long Number, string Name)[] insured)
    {
        var a01 = "A01" + "1234567" + "202604" + Field("LABO ANALYSES", 40) + Field("LABORATOIRE", 40) + Field("12 RUE X", 120)
                  + Field("RABAT", 20) + Field("10000", 6) + "01" + "20260501" + "20260510";
        var lines = new List<string> { "A00" + "00000000004242" + "A0" + new string(' ', 241), a01 };
        lines.AddRange(insured.Select(i => "A02" + "1234567" + "202604" + Zero(i.Number, 9) + Field(i.Name, 60)
                                           + Zero(0, 2) + Zero(0, 6) + Zero(0, 6) + Zero(0, 6)));
        lines.Add("A03" + "1234567" + "202604");
        return Encoding.Latin1.GetBytes(string.Join("\r\n", lines.Select(l => l.PadRight(260))) + "\r\n");
    }

    private static string[] Records(byte[] bds) =>
        Encoding.ASCII.GetString(bds).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private static long N(string record, int start, int length) =>
        long.Parse(record.Substring(start, length), CultureInfo.InvariantCulture);

    // ---- H1: the BDS and the 9421 cover every site of the employer ---------------------------------

    [Fact]
    public async Task H1_the_bds_of_one_site_declares_the_staff_paid_on_the_other_site_of_the_affiliate()
    {
        var alami = Person("M1", "ALAMI", "100000001", Main);
        var benani = Person("M2", "BENANI", "100000002", Centre);
        var main = Month(Main, 4, close: true, alami);
        var centre = Month(Centre, 4, close: true, benani);
        var queries = new Queries { Others = new([new EmployerSiteMonth("Centre de prélèvement", centre)], [benani]) };

        var built = await Service(queries, [main], [alami])
            .ExportBdsAsync(main.Id, Preestablished((100000001, "ALAMI"), (100000002, "BENANI")));

        built.IsSuccess.Should().BeTrue(built.IsFailure ? built.FirstError.ResourceKey : null);
        built.Value.Declared.Should().Be(2);
        built.Value.Entrants.Should().Be(0);
        queries.Asked.Should().Equal(EmployerIdentifier.CnssAffiliation);

        var b02 = Records(built.Value.Content).Where(r => r.StartsWith("B02", StringComparison.Ordinal)).ToList();
        var line = b02.Single(r => N(r, 16, 9) == 100000002);
        N(line, 111, 2).Should().Be(26, "her month is paid on the centre's payroll");
        N(line, 113, 13).Should().Be((long)(centre.Payslips.Single().Amounts.GrossTaxable * 100m));
        line.Substring(135, 2).Should().Be("  ", "she has not left");
    }

    [Fact]
    public async Task H1_the_bds_is_refused_while_the_month_of_another_site_of_the_affiliate_is_a_draft()
    {
        var alami = Person("M1", "ALAMI", "100000001", Main);
        var benani = Person("M2", "BENANI", "100000002", Centre);
        var main = Month(Main, 4, close: true, alami);
        var centre = Month(Centre, 4, close: false, benani);
        var queries = new Queries { Others = new([new EmployerSiteMonth("Centre de prélèvement", centre)], [benani]) };

        var built = await Service(queries, [main], [alami]).ExportBdsAsync(main.Id, Preestablished((100000001, "ALAMI")));

        built.FirstError.ResourceKey.Should().Be("Payroll_BdsOtherSiteDraft");
        built.FirstError.Args.Should().Equal("04/2026", "Centre de prélèvement");
    }

    [Fact]
    public async Task H1_the_9421_holds_the_staff_of_every_site_with_the_same_fiscal_id_and_waits_for_their_months()
    {
        var alami = Person("M1", "ALAMI", "100000001", Main);
        var benani = Person("M2", "BENANI", "100000002", Centre);
        var main = Month(Main, 4, close: true, alami);
        var centre = Month(Centre, 4, close: true, benani);
        var codes = new SimplIrCodes("141.01.01", SimplIrCodes.DefaultLowIncomeRateCode, SimplIrCodes.DefaultHighIncomeRateCode);

        var queries = new Queries { Others = new([new EmployerSiteMonth("Centre", centre)], [benani]) };
        var built = await Service(queries, [main], [alami]).ExportSimplIrAsync(2026, codes);

        built.IsSuccess.Should().BeTrue(built.IsFailure ? built.FirstError.ResourceKey : null);
        queries.Asked.Should().Equal(EmployerIdentifier.FiscalId);
        built.Value.Declared.Should().Be(2);
        var root = XDocument.Parse(Encoding.UTF8.GetString(built.Value.Content)).Root!;
        root.Element("listPersonnelPermanent")!.Elements().Select(p => p.Element("nom")!.Value)
            .Should().Equal("ALAMI", "BENANI");
        root.Element("listPersonnelPermanent")!.Elements().Single(p => p.Element("nom")!.Value == "BENANI")
            .Element("mtRevenuBrutImposable")!.Value.Should().Be("5000.00");

        var draft = Month(Centre, 5, close: false, benani);
        var waiting = new Queries { Others = new([new EmployerSiteMonth("Centre", centre), new EmployerSiteMonth("Centre", draft)], [benani]) };
        var refused = await Service(waiting, [main], [alami]).ExportSimplIrAsync(2026, codes);
        refused.FirstError.ResourceKey.Should().Be("Payroll_SimplIrOpenMonths");
        refused.FirstError.Args.Should().Equal(2026, "05/2026 (Centre)");
    }

    // ---- H2: a Tahfiz salary within 10,000 DH is exempt on the 9421 ---------------------------------

    private static readonly SimplIrCodes Codes = new("141.01.01", SimplIrCodes.DefaultLowIncomeRateCode,
                                                     SimplIrCodes.DefaultHighIncomeRateCode);

    private static SimplIrPayslip Line(int month, Guid id, ContractType type, decimal salary, DateOnly hired, int year = 2025)
    {
        var amounts = PayrollCalculator.Compute(new PayslipInput(salary, hired, new DateOnly(year, month, 28), type, 0,
                                                                 new PayslipVariables { TransportAllowance = 300m }),
                                                new PayrollParameters(year));
        return new SimplIrPayslip(month, id, "M-1", "IDRISSI", "Nadia", "3 rue Y", "AB1", "100000001",
                                  FamilyStatus.Unmarried, type, 0, false, amounts);
    }

    private static XElement Declaration(IReadOnlyList<SimplIrPayslip> lines) =>
        XDocument.Parse(Encoding.UTF8.GetString(SimplIrSalaryDeclaration.Build(Employer, 2025, lines, Codes))).Root!;

    private static decimal D(XElement e, string name) => decimal.Parse(e.Element(name)!.Value, CultureInfo.InvariantCulture);

    [Fact]
    public void H2_a_tahfiz_salary_within_ten_thousand_is_declared_exempt_not_taxable()
    {
        var id = Guid.NewGuid();
        var lines = Enumerable.Range(1, 12).Select(m => Line(m, id, ContractType.Tahfiz, 8000m, new DateOnly(2024, 9, 2))).ToList();
        lines.Should().OnlyContain(l => l.Amounts.IncomeTax == 0m && l.Amounts.GrossTaxable == 8000m);

        var root = Declaration(lines);
        var row = root.Element("listPersonnelPermanent")!.Elements("PersonnelPermanent").Single();

        D(row, "mtBrutTraitementSalaire").Should().Be(12 * 8300m);
        D(row, "mtExonere").Should().Be(12 * 8300m, "the salary and the exempt transport");
        D(row, "mtRevenuBrutImposable").Should().Be(0m);
        D(row, "mtRevenuNetImposable").Should().Be(0m);
        D(row, "mtTotalDeduction").Should().Be(0m);
        D(row, "irPreleve").Should().Be(0m);
        D(root, "totalMtRevenuBrutImposablePP").Should().Be(0m);
        D(root, "totalMtRevenuNetImposablePP").Should().Be(0m);
    }

    [Fact]
    public void H2_a_tahfiz_salary_above_ten_thousand_stays_taxable()
    {
        var id = Guid.NewGuid();
        var lines = Enumerable.Range(1, 12).Select(m => Line(m, id, ContractType.Tahfiz, 12000m, new DateOnly(2024, 9, 2))).ToList();

        var row = Declaration(lines).Element("listPersonnelPermanent")!.Elements("PersonnelPermanent").Single();

        D(row, "mtExonere").Should().Be(12 * 300m);
        D(row, "mtRevenuBrutImposable").Should().Be(lines.Sum(l => l.Amounts.GrossTaxable));
        D(row, "mtRevenuNetImposable").Should().Be(lines.Sum(l => l.Amounts.NetTaxable)).And.BePositive();
        D(row, "irPreleve").Should().Be(lines.Sum(l => l.Amounts.IncomeTax)).And.BePositive();
    }

    // ---- H3: the ANAPEC exemption is capped at 6,000 DH and lasts 24 months -------------------------

    private static PayslipAmounts Anapec(decimal stipend, DateOnly hired, DateOnly paid) =>
        PayrollCalculator.Compute(new PayslipInput(stipend, hired, paid, ContractType.Anapec, 0, new PayslipVariables()),
                                  new PayrollParameters(2026));

    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public void H3_an_anapec_stipend_within_the_cap_bears_nothing()
    {
        var a = Anapec(6000m, new DateOnly(2026, 1, 5), new DateOnly(2026, 4, 30));

        a.Exemption.Should().Be(PayrollExemption.InsertionContract);
        (a.CnssEmployee + a.AmoEmployee + a.CnssEmployer + a.AmoEmployer + a.FamilyAllowanceEmployer
         + a.TrainingTaxEmployer + a.IncomeTax + a.NetTaxable).Should().Be(0m);
        a.NetPay.Should().Be(6000m);
    }

    [Fact]
    public void H3_the_part_of_an_anapec_stipend_above_six_thousand_is_taxed_and_bears_the_contributions()
    {
        var rates = new PayrollParameters(2026);
        var a = Anapec(9000m, new DateOnly(2026, 1, 5), new DateOnly(2026, 4, 30));

        a.Exemption.Should().Be(PayrollExemption.InsertionContract);
        a.GrossTaxable.Should().Be(9000m);
        PayrollCalculator.ContributionBase(a).Should().Be(3000m);
        a.CnssEmployee.Should().Be(R(3000m * rates.CnssEmployeeRate / 100m));
        a.AmoEmployee.Should().Be(R(3000m * rates.AmoEmployeeRate / 100m));
        a.CnssEmployer.Should().Be(R(3000m * rates.CnssEmployerRate / 100m));
        a.AmoEmployer.Should().Be(R(3000m * rates.AmoEmployerRate / 100m));
        a.NetTaxable.Should().Be(R(3000m - a.ProfessionalExpenses - a.CnssEmployee - a.AmoEmployee)).And.BePositive();
        a.SeniorityBonus.Should().Be(0m, "a stipend is not a salary");

        // Enough above the cap for the scale to tax it.
        Anapec(15000m, new DateOnly(2026, 1, 5), new DateOnly(2026, 4, 30)).IncomeTax.Should().BePositive();
    }

    [Fact]
    public void H3_an_anapec_month_beyond_the_twenty_four_months_is_an_ordinary_salary()
    {
        var hired = new DateOnly(2024, 1, 15);

        // The month in which the 24 months end is the last exempt one.
        Anapec(4000m, hired, new DateOnly(2026, 1, 31)).Exemption.Should().Be(PayrollExemption.InsertionContract);

        var beyond = Anapec(4000m, hired, new DateOnly(2026, 2, 28));
        beyond.Exemption.Should().Be(PayrollExemption.None);
        beyond.CnssEmployee.Should().BePositive();
        beyond.CnssEmployer.Should().BePositive();
        beyond.NetTaxable.Should().BePositive();
    }

    [Fact]
    public void H3_the_9421_declares_the_taxed_excess_of_an_anapec_stipend_and_the_months_beyond_as_permanent()
    {
        var id = Guid.NewGuid();
        var hired = new DateOnly(2023, 3, 1);
        // Jan and Feb 2025 within the 24 months, at 9,000 DH; March on, beyond.
        var lines = Enumerable.Range(1, 6).Select(m => Line(m, id, ContractType.Anapec, 9000m, hired)).ToList();
        var exempt = lines.Take(2).ToList();
        var beyond = lines.Skip(2).ToList();
        exempt.Should().OnlyContain(l => l.Amounts.Exemption == PayrollExemption.InsertionContract);
        beyond.Should().OnlyContain(l => l.Amounts.Exemption == PayrollExemption.None);

        var root = Declaration(lines);

        var stagiaire = root.Element("listStagiaires")!.Elements("Stagiaire").Single();
        D(stagiaire, "mtBrutTraitementSalaire").Should().Be(2 * 9000m);
        D(stagiaire, "mtRevenuNetImposable").Should().Be(exempt.Sum(l => l.Amounts.NetTaxable)).And.BePositive();
        D(stagiaire, "mtRetenues").Should().Be(exempt.Sum(l => l.Amounts.CnssEmployee + l.Amounts.AmoEmployee)).And.BePositive();
        D(root, "montantStagiaire").Should().Be(exempt.Sum(l => l.Amounts.IncomeTax));

        var permanent = root.Element("listPersonnelPermanent")!.Elements("PersonnelPermanent").Single();
        D(permanent, "mtRevenuBrutImposable").Should().Be(beyond.Sum(l => l.Amounts.GrossTaxable));
        D(permanent, "irPreleve").Should().Be(beyond.Sum(l => l.Amounts.IncomeTax));
    }

    // ---- H4: the month of a re-hire keeps the previous contract's type ------------------------------

    private static Employee Rehired(ContractType before, ContractType after)
    {
        var e = new Employee("M0001", "ALAMI", "Sara", EmployeeJob.Technician, new DateOnly(2025, 6, 2), Main)
        {
            ContractType = before, BaseSalary = 4000m, CnssNumber = "100000001"
        };
        e.RecordExit(new DateOnly(2026, 3, 15), ExitReason.EndOfFixedTerm, null).IsSuccess.Should().BeTrue();
        e.Rehire(new DateOnly(2026, 3, 16)).IsSuccess.Should().BeTrue();
        e.ContractType = after;                                 // the new contract
        e.BaseSalary = 7000m;
        return e;
    }

    [Fact]
    public async Task H4_an_anapec_contract_ended_and_rehired_as_cdi_in_the_month_is_paid_as_anapec()
    {
        var e = Rehired(ContractType.Anapec, ContractType.Permanent);
        e.PreviousContractType.Should().Be(ContractType.Anapec);

        var march = new PayrollPeriod(Main, 2026, 3);
        var queries = new FakePayrollQueries { Candidates = [new PayrollCandidate(e.Id, 0m)] };
        (await Payroll(queries, [march], [e], [new PayrollParameters(2026)]).RefreshAsync(march.Id)).IsSuccess.Should().BeTrue();

        var slip = march.Payslips.Single();
        slip.ContractType.Should().Be(ContractType.Anapec);
        slip.BaseSalary.Should().Be(4000m);
        slip.Amounts.Exemption.Should().Be(PayrollExemption.InsertionContract);
        (slip.Amounts.CnssEmployee + slip.Amounts.AmoEmployee + slip.Amounts.IncomeTax).Should().Be(0m);

        PayrollService.ContractTypeOfMonth(e, new PayrollPeriod(Main, 2026, 4), null).Should().Be(ContractType.Permanent);
    }

    [Fact]
    public async Task H4_a_cdi_ended_and_rehired_as_anapec_in_the_month_is_not_exempted()
    {
        var e = Rehired(ContractType.Permanent, ContractType.Anapec);

        var march = new PayrollPeriod(Main, 2026, 3);
        var queries = new FakePayrollQueries { Candidates = [new PayrollCandidate(e.Id, 0m)] };
        (await Payroll(queries, [march], [e], [new PayrollParameters(2026)]).RefreshAsync(march.Id)).IsSuccess.Should().BeTrue();

        var slip = march.Payslips.Single();
        slip.ContractType.Should().Be(ContractType.Permanent);
        slip.Amounts.Exemption.Should().Be(PayrollExemption.None);
        slip.Amounts.CnssEmployee.Should().BePositive();
    }

    [Fact]
    public void H4_a_rehire_recorded_before_the_type_was_kept_keeps_the_type_already_on_the_draft()
    {
        var e = Rehired(ContractType.Anapec, ContractType.Permanent);
        typeof(Employee).GetProperty(nameof(Employee.PreviousContractType))!.SetValue(e, null);

        PayrollService.ContractTypeOfMonth(e, new PayrollPeriod(Main, 2026, 3), ContractType.Anapec)
                      .Should().Be(ContractType.Anapec);
        PayrollService.ContractTypeOfMonth(e, new PayrollPeriod(Main, 2026, 3), null).Should().Be(ContractType.Permanent);
    }

    [Fact]
    public void H4_the_previous_contract_type_is_a_column_of_the_employee()
    {
        using var db = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
                                        .UseSqlServer("Server=.;Database=none;TrustServerCertificate=true").Options);
        db.Model.FindEntityType(typeof(Employee))!.FindProperty(nameof(Employee.PreviousContractType))
          .Should().NotBeNull();
    }

    // ---- Fakes -----------------------------------------------------------------------------------

    private static PayrollService Service(Queries queries, PayrollPeriod[] periods, Employee[] staff)
    {
        var periodRepo = new InMemoryRepository<PayrollPeriod>();
        periodRepo.Items.AddRange(periods);
        var rates = new InMemoryRepository<PayrollParameters>();
        rates.Items.Add(new PayrollParameters(2026));
        return new PayrollService(periodRepo, rates, Repo(staff), queries,
                                  new InMemoryRepository<FinalSettlement>(), new InMemoryRepository<LeaveLedgerEntry>(),
                                  new FakeHrDocumentQueries(), new FakeUnitOfWork(), new AllowAll(), new User(),
                                  new FixedClock());
    }

    private sealed class Queries : IPayrollQueries
    {
        public EmployerOtherSites Others { get; init; } = EmployerOtherSites.None;
        public List<EmployerIdentifier> Asked { get; } = [];

        public Task<IReadOnlyList<PayrollPeriodRow>> GetPeriodsAsync(Guid siteId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PayrollPeriodRow>>([]);
        public Task<Guid?> FindPeriodAsync(Guid siteId, int year, int month, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<Guid>> DraftPeriodIdsAsync(int year, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<PayrollCandidate>> CandidatesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                     decimal monthlyWorkingDays, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PayrollCandidate>>([]);
        public Task<InvoiceIssuer> EmployerAsync(Guid siteId, CancellationToken ct = default) => Task.FromResult(Employer);
        public Task<IReadOnlyDictionary<Guid, SettledAmounts>> SettlementsAsync(DateOnly from, DateOnly to,
                                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SettledAmounts>>(new Dictionary<Guid, SettledAmounts>());

        public Task<EmployerOtherSites> EmployerOtherSitesAsync(Guid siteId, EmployerIdentifier by, int year, int? month,
                                                                CancellationToken ct = default)
        {
            siteId.Should().Be(Main);
            Asked.Add(by);
            return Task.FromResult(new EmployerOtherSites(
                [.. Others.Months.Where(m => m.Period.Year == year && (month is null || m.Period.Month == month))], Others.Staff));
        }
    }

    internal sealed class User(Guid? site = null) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "rh";
        public string FullName => "RH";
        public Guid SiteId => site ?? Main;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { global::MarocLIS.Domain.Identity.Permissions.HumanResources.Salary };
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => Permissions.Contains(permission);
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "TEST";
    }
}

/// <summary>H1 against SQL Server: the employer's other sites are read across the site filter.</summary>
public sealed class ReviewFixesCycle2HrDatabaseTests : IDisposable
{
    private readonly string _connection;
    private readonly bool _created;

    public ReviewFixesCycle2HrDatabaseTests()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) { _connection = ""; return; }

        var builder = new SqlConnectionStringBuilder(configured);
        builder.InitialCatalog = (string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "MarocLIS_Test" : builder.InitialCatalog)
                                 + "_C2" + Guid.NewGuid().ToString("N")[..6];
        _connection = builder.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;
    }

    private LisDbContext Context(ICurrentUser? user = null) =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connection).Options, user);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }

    private static LabSite Site(string name, LabSiteKind kind, string? fiscalId, string? cnss) =>
        new("S" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), new MultilingualText(name), kind)
        {
            LegalName = "LABO SARL", FiscalId = fiscalId, CnssAffiliationNumber = cnss
        };

    private static (Employee Employee, PayrollPeriod Period) Paid(LabSite site, string number, int month)
    {
        var e = new Employee(number + Guid.NewGuid().ToString("N")[..4], "NOM", "Prénom", EmployeeJob.Technician,
                             new DateOnly(2025, 6, 2), site.Id) { BaseSalary = 5000m, CnssNumber = "1000000" + number[^2..] };
        var period = new PayrollPeriod(site.Id, 2026, month);
        var slip = new Payslip(e.Id) { Number = e.Number, FullName = e.FullName, BaseSalary = 5000m, HireDate = e.HireDate };
        slip.Recompute(new PayslipVariables(), new PayrollParameters(2026), period.LastDay);
        period.Add(slip);
        return (e, period);
    }

    [SqlServerFact]
    public async Task H1_the_other_sites_of_the_employer_are_read_whatever_sites_the_operator_sees()
    {
        var main = Site("Laboratoire", LabSiteKind.MainLaboratory, "12345678", "1234567");
        var centre = Site("Centre", LabSiteKind.CollectionCentre, " 12345678 ", "123 4567");
        var other = Site("Autre société", LabSiteKind.MainLaboratory, "99999999", "7654321");
        var (alami, mainApril) = Paid(main, "M01", 4);
        var (benani, centreApril) = Paid(centre, "M02", 4);
        var (tazi, otherApril) = Paid(other, "M04", 4);

        await using (var db = Context())
        {
            db.Sites.AddRange(main, centre, other);
            db.Employees.AddRange(alami, benani, tazi);
            db.PayrollPeriods.AddRange(mainApril, centreApril, otherApril);
            await db.SaveChangesAsync();
        }

        // The operator sees the main laboratory only.
        await using var scoped = Context(new ReviewFixesCycle2HrTests.User(main.Id));
        (await scoped.PayrollPeriods.CountAsync()).Should().Be(1, "the site filter applies to the operator");

        var queries = new PayrollQueries(scoped);
        var cnss = await queries.EmployerOtherSitesAsync(main.Id, EmployerIdentifier.CnssAffiliation, 2026, 4);
        cnss.Months.Should().ContainSingle().Which.SiteName.Should().Be("Centre");
        cnss.Months[0].Period.Payslips.Should().ContainSingle().Which.EmployeeId.Should().Be(benani.Id);
        cnss.Staff.Select(e => e.Id).Should().Contain(benani.Id).And.NotContain(tazi.Id).And.NotContain(alami.Id);

        var fiscal = await queries.EmployerOtherSitesAsync(main.Id, EmployerIdentifier.FiscalId, 2026, null);
        fiscal.Months.Select(m => m.Period.Id).Should().Equal(centreApril.Id);

        (await queries.EmployerOtherSitesAsync(other.Id, EmployerIdentifier.FiscalId, 2026, null)).Months.Should().BeEmpty();
    }
}
