using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.HumanResources;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
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
/// Review cycle 5, payroll: the BDS and the 9421 wait for a month another site of the employer
/// has salaried staff for and never opened (H1); the Tahfiz benefits last 24 months and the
/// employer charges are waived within 10,000 DH only (H2); the 9421 declares the capped net
/// taxable of an ANAPEC payslip closed before the cap (H3); the month of a re-hire pays the
/// contract that is on the payroll when the other is not (H4).
/// </summary>
public sealed class ReviewFixesCycle3HrTests
{
    private static readonly Guid Main = Guid.NewGuid();

    private static readonly InvoiceIssuer Employer = new("Labo", "LABO ANALYSES SARL", "12 rue X", "Rabat", "10000",
        "0537000000", "contact@labo.ma", "001234567000089", "12345678", "98765", "Rabat", "45678901", "1234567", null, null);

    private static readonly SimplIrCodes Codes = new("141.01.01", SimplIrCodes.DefaultLowIncomeRateCode,
                                                     SimplIrCodes.DefaultHighIncomeRateCode);

    private static Employee Person(string number, string last, string cnss, Guid site) =>
        new(number, last, "Prénom", EmployeeJob.Technician, new DateOnly(2025, 6, 2), site)
        {
            CnssNumber = cnss, Cin = "AB" + number, BaseSalary = 5000m, FamilyStatus = FamilyStatus.Unmarried
        };

    private static PayrollPeriod Month(Guid site, int month, params Employee[] staff)
    {
        var rates = new PayrollParameters(2026);
        var period = new PayrollPeriod(site, 2026, month);
        foreach (var e in staff)
        {
            var slip = new Payslip(e.Id)
            {
                Number = e.Number, FullName = e.FullName, Cin = e.Cin, CnssNumber = e.CnssNumber, HireDate = e.HireDate,
                ContractType = e.ContractType, BaseSalary = e.BaseSalary ?? 0m
            };
            slip.Recompute(new PayslipVariables(), rates, period.LastDay);
            period.Add(slip);
        }

        period.Close(DateTimeOffset.Now, Guid.NewGuid(), rates).IsSuccess.Should().BeTrue();
        return period;
    }

    private static byte[] Preestablished()
    {
        static string Field(string value, int width) => value.PadRight(width)[..width];
        var a01 = "A01" + "1234567" + "202604" + Field("LABO ANALYSES", 40) + Field("LABORATOIRE", 40) + Field("12 RUE X", 120)
                  + Field("RABAT", 20) + Field("10000", 6) + "01" + "20260501" + "20260510";
        var lines = new List<string>
        {
            "A00" + "00000000004242" + "A0" + new string(' ', 241), a01,
            "A02" + "1234567" + "202604" + "100000001" + Field("ALAMI", 60) + "00" + "000000" + "000000" + "000000",
            "A03" + "1234567" + "202604"
        };
        return Encoding.Latin1.GetBytes(string.Join("\r\n", lines.Select(l => l.PadRight(260))) + "\r\n");
    }

    // ---- H1: a month another site of the employer never opened -----------------------------------

    [Fact]
    public async Task H1_the_bds_is_refused_while_another_site_with_salaried_staff_has_not_opened_the_month()
    {
        var alami = Person("M1", "ALAMI", "100000001", Main);
        var main = Month(Main, 4, alami);
        var queries = new Queries { Others = new([], [], [new EmployerSiteMissingMonth("Centre de prélèvement", 4)]) };

        var built = await Service(queries, [main], [alami]).ExportBdsAsync(main.Id, Preestablished());

        built.FirstError.ResourceKey.Should().Be("Payroll_BdsOtherSiteNotOpened");
        built.FirstError.Args.Should().Equal("04/2026", "Centre de prélèvement");

        var none = await Service(new Queries(), [main], [alami]).ExportBdsAsync(main.Id, Preestablished());
        none.IsSuccess.Should().BeTrue(none.IsFailure ? none.FirstError.ResourceKey : null);
    }

    [Fact]
    public async Task H1_the_9421_is_refused_for_a_month_another_site_never_opened_within_the_months_paid()
    {
        var alami = Person("M1", "ALAMI", "100000001", Main);
        var months = Enumerable.Range(4, 9).Select(m => Month(Main, m, alami)).ToArray();     // April to December

        var queries = new Queries
        {
            Others = new([], [], [new EmployerSiteMissingMonth("Centre", 2), new EmployerSiteMissingMonth("Centre", 12)])
        };
        var refused = await Service(queries, months, [alami]).ExportSimplIrAsync(2026, Codes);

        refused.FirstError.ResourceKey.Should().Be("Payroll_SimplIrMonthsNotOpened");
        refused.FirstError.Args.Should().Equal(2026, "12/2026 (Centre)");

        // Before the employer's payroll started on the application, nothing is asked for.
        var before = new Queries { Others = new([], [], [new EmployerSiteMissingMonth("Centre", 2)]) };
        var built = await Service(before, months, [alami]).ExportSimplIrAsync(2026, Codes);
        built.IsSuccess.Should().BeTrue(built.IsFailure ? built.FirstError.ResourceKey : null);
    }

    [Fact]
    public void H1_a_site_has_a_month_to_open_for_salaried_staff_on_a_contract_the_payroll_pays()
    {
        DateOnly first = new(2026, 4, 1), last = new(2026, 4, 30);
        var cdi = Person("M1", "ALAMI", "1", Main);
        PayrollService.IsOnPayrollIn(cdi, first, last).Should().BeTrue();

        var temp = Person("M2", "BENANI", "2", Main);
        temp.ContractType = ContractType.Temporary;
        PayrollService.IsOnPayrollIn(temp, first, last).Should().BeFalse("an agency temp is paid by the agency");

        var unpaid = Person("M3", "CHAKIR", "3", Main);
        unpaid.BaseSalary = null;
        PayrollService.IsOnPayrollIn(unpaid, first, last).Should().BeFalse();

        var gone = Person("M4", "DRISSI", "4", Main);
        gone.RecordExit(new DateOnly(2026, 3, 31), ExitReason.Resignation, null).IsSuccess.Should().BeTrue();
        PayrollService.IsOnPayrollIn(gone, first, last).Should().BeFalse();

        var later = Person("M5", "EL FASSI", "5", Main);
        later.HireDate = new DateOnly(2026, 5, 4);
        PayrollService.IsOnPayrollIn(later, first, last).Should().BeFalse();
    }

    // ---- H2: the Tahfiz benefits last 24 months, within 10,000 DH -----------------------------------

    private static PayslipAmounts Pay(ContractType type, decimal salary, DateOnly hired, DateOnly paid) =>
        PayrollCalculator.Compute(new PayslipInput(salary, hired, paid, type, 0, new PayslipVariables()),
                                  new PayrollParameters(2026));

    [Fact]
    public void H2_a_tahfiz_month_beyond_the_twenty_four_months_is_an_ordinary_salary()
    {
        var hired = new DateOnly(2023, 3, 1);
        var tahfiz = Pay(ContractType.Tahfiz, 9000m, hired, new DateOnly(2026, 9, 30));
        var cdi = Pay(ContractType.Permanent, 9000m, hired, new DateOnly(2026, 9, 30));

        tahfiz.Exemption.Should().Be(PayrollExemption.None);
        tahfiz.Should().BeEquivalentTo(cdi);
        tahfiz.IncomeTax.Should().BePositive();
        tahfiz.CnssEmployer.Should().BePositive();
        tahfiz.FamilyAllowanceEmployer.Should().BePositive();
        tahfiz.TrainingTaxEmployer.Should().BePositive();
        SimplIrSalaryDeclaration.IsExemptTahfiz(tahfiz).Should().BeFalse();
    }

    [Fact]
    public void H2_the_month_in_which_the_twenty_four_months_end_is_the_last_exempt_one()
    {
        var hired = new DateOnly(2024, 10, 15);

        var last = Pay(ContractType.Tahfiz, 9000m, hired, new DateOnly(2026, 10, 31));
        last.Exemption.Should().Be(PayrollExemption.Tahfiz);
        (last.IncomeTax + last.CnssEmployer + last.FamilyAllowanceEmployer + last.TrainingTaxEmployer).Should().Be(0m);
        SimplIrSalaryDeclaration.IsExemptTahfiz(last).Should().BeTrue();

        Pay(ContractType.Tahfiz, 9000m, hired, new DateOnly(2026, 11, 30)).Exemption.Should().Be(PayrollExemption.None);
    }

    [Fact]
    public void H2_a_tahfiz_salary_above_ten_thousand_bears_the_employer_charges()
    {
        var hired = new DateOnly(2026, 1, 5);
        var tahfiz = Pay(ContractType.Tahfiz, 15000m, hired, new DateOnly(2026, 9, 30));
        var cdi = Pay(ContractType.Permanent, 15000m, hired, new DateOnly(2026, 9, 30));

        tahfiz.Exemption.Should().Be(PayrollExemption.Tahfiz);
        tahfiz.IncomeTax.Should().Be(cdi.IncomeTax).And.BePositive();
        tahfiz.CnssEmployer.Should().Be(cdi.CnssEmployer).And.BePositive();
        tahfiz.FamilyAllowanceEmployer.Should().Be(cdi.FamilyAllowanceEmployer).And.BePositive();
        tahfiz.TrainingTaxEmployer.Should().Be(cdi.TrainingTaxEmployer).And.BePositive();
        tahfiz.EmployerCost.Should().Be(cdi.EmployerCost);
    }

    // ---- H3: an ANAPEC payslip closed before the cap ---------------------------------------------------

    [Fact]
    public async Task H3_the_9421_declares_the_capped_net_taxable_of_an_anapec_payslip_closed_before_the_cap()
    {
        var rates = new PayrollParameters(2026);
        var trainee = Person("M7", "IDRISSI", "100000007", Main);
        trainee.ContractType = ContractType.Anapec;
        trainee.BaseSalary = 9000m;
        trainee.HireDate = new DateOnly(2026, 1, 5);

        var march = Month(Main, 3, trainee);
        var current = march.Payslips.Single().Amounts;
        var expected = PayrollCalculator.ContributionBase(current) - current.ProfessionalExpenses - current.CnssEmployee
                       - current.AmoEmployee;
        current.NetTaxable.Should().Be(expected).And.BeApproximately(1747.80m, 0.01m);

        // As the calculator stored it before the cap: the net taxable of the whole stipend.
        typeof(Payslip).GetProperty(nameof(Payslip.Amounts))!.SetValue(march.Payslips.Single(),
            current with { CnssEmployee = 0m, AmoEmployee = 0m, IncomeTax = 0m, NetTaxable = 6750m });

        var built = await Service(new Queries(), [march], [trainee]).ExportSimplIrAsync(2026, Codes);

        built.IsSuccess.Should().BeTrue(built.IsFailure ? built.FirstError.ResourceKey : null);
        var root = XDocument.Parse(Encoding.UTF8.GetString(built.Value.Content)).Root!;
        var row = root.Element("listStagiaires")!.Elements("Stagiaire").Single();
        decimal.Parse(row.Element("mtRevenuNetImposable")!.Value, CultureInfo.InvariantCulture).Should().Be(expected);
        PayrollCalculator.NetTaxableOf(current, rates).Should().Be(expected);
    }

    // ---- H4: the month of a re-hire between a contract on the payroll and one that is not ------------

    private static Employee Rehired(ContractType before, ContractType after)
    {
        var e = new Employee("M0001", "ALAMI", "Sara", EmployeeJob.Technician, new DateOnly(2025, 6, 2), Main)
        {
            ContractType = before, BaseSalary = 4000m, CnssNumber = "100000001"
        };
        e.RecordExit(new DateOnly(2026, 3, 15), ExitReason.EndOfFixedTerm, null).IsSuccess.Should().BeTrue();
        e.Rehire(new DateOnly(2026, 3, 16)).IsSuccess.Should().BeTrue();
        e.ContractType = after;
        e.BaseSalary = 7000m;
        return e;
    }

    // March 2026, 25 working days: 13 before the exit, 12 after the re-hire.
    private static PayrollCandidate Candidate(Guid id) =>
        new(id, 0m)
        {
            PreviousContract = new PayrollCandidate(id, 0m, MonthWorkingDays: 25m, ContractWorkingDays: 13m),
            CurrentContract = new PayrollCandidate(id, 0m, MonthWorkingDays: 25m, ContractWorkingDays: 12m)
        };

    [Fact]
    public async Task H4_an_agency_temp_rehired_on_a_cdi_is_paid_the_days_of_the_cdi()
    {
        var e = Rehired(ContractType.Temporary, ContractType.Permanent);
        var march = new PayrollPeriod(Main, 2026, 3);
        var queries = new FakePayrollQueries { Candidates = [Candidate(e.Id)] };

        (await Payroll(queries, [march], [e], [new PayrollParameters(2026)]).RefreshAsync(march.Id)).IsSuccess.Should().BeTrue();

        var slip = march.Payslips.Single();
        slip.ContractType.Should().Be(ContractType.Permanent);
        slip.BaseSalary.Should().Be(7000m);
        slip.HireDate.Should().Be(new DateOnly(2026, 3, 16));
        slip.Variables.ContractWorkingDays.Should().Be(12m);
        slip.Amounts.GrossTaxable.Should().Be(Math.Round(7000m * 12m / 25m, 2));
        slip.Amounts.PaidDays.Should().Be(12m);
    }

    [Fact]
    public async Task H4_a_cdi_rehired_as_a_partner_is_paid_the_days_of_the_cdi_only()
    {
        var e = Rehired(ContractType.Permanent, ContractType.Partner);
        var march = new PayrollPeriod(Main, 2026, 3);
        var queries = new FakePayrollQueries { Candidates = [Candidate(e.Id)] };

        (await Payroll(queries, [march], [e], [new PayrollParameters(2026)]).RefreshAsync(march.Id)).IsSuccess.Should().BeTrue();

        var slip = march.Payslips.Single();
        slip.ContractType.Should().Be(ContractType.Permanent);
        slip.BaseSalary.Should().Be(4000m);
        slip.Variables.ContractWorkingDays.Should().Be(13m);
        slip.Amounts.PaidDays.Should().Be(13m);
    }

    [Fact]
    public void H4_two_contracts_both_on_the_payroll_pay_the_days_of_both_under_the_previous_type()
    {
        var e = Rehired(ContractType.Anapec, ContractType.Permanent);
        var candidate = Candidate(e.Id);

        var (type, figures, currentOnly) = PayrollService.StintOfMonth(e, new PayrollPeriod(Main, 2026, 3), null, candidate);

        type.Should().Be(ContractType.Anapec);
        figures.Should().BeSameAs(candidate);
        currentOnly.Should().BeFalse();

        // The month after, the new contract alone.
        var april = new PayrollCandidate(e.Id, 0m);
        PayrollService.StintOfMonth(e, new PayrollPeriod(Main, 2026, 4), null, april)
                      .Should().Be((ContractType.Permanent, april, false));
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
                                  new FakeHrDocumentQueries(), new FakeUnitOfWork(), new AllowAll(),
                                  new ReviewFixesCycle2HrTests.User(Main), new FixedClock());
    }

    private sealed class Queries : IPayrollQueries
    {
        public EmployerOtherSites Others { get; init; } = EmployerOtherSites.None;

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
                                                                CancellationToken ct = default) =>
            Task.FromResult(Others);
    }
}

/// <summary>H1 and H4 against SQL Server: the months another site never opened, the figures of each contract.</summary>
public sealed class ReviewFixesCycle3HrDatabaseTests : IDisposable
{
    private readonly string _connection;
    private readonly bool _created;

    public ReviewFixesCycle3HrDatabaseTests()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) { _connection = ""; return; }

        var builder = new SqlConnectionStringBuilder(configured);
        builder.InitialCatalog = (string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "MarocLIS_Test" : builder.InitialCatalog)
                                 + "_C3" + Guid.NewGuid().ToString("N")[..6];
        _connection = builder.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;
    }

    private LisDbContext Context() => new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connection).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }

    private static LabSite Site(string name, LabSiteKind kind) =>
        new("S" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), new MultilingualText(name), kind)
        {
            LegalName = "LABO SARL", FiscalId = "12345678", CnssAffiliationNumber = "1234567"
        };

    private static Employee Staff(LabSite site, string number, ContractType type = ContractType.Permanent) =>
        new(number + Guid.NewGuid().ToString("N")[..4], "NOM", "Prénom", EmployeeJob.Technician, new DateOnly(2025, 6, 2), site.Id)
        {
            BaseSalary = 5000m, CnssNumber = "1000000" + number[^2..], ContractType = type
        };

    [SqlServerFact]
    public async Task H1_a_month_another_site_with_salaried_staff_never_opened_is_returned()
    {
        var main = Site("Laboratoire", LabSiteKind.MainLaboratory);
        var centre = Site("Centre", LabSiteKind.CollectionCentre);
        var agency = Site("Antenne", LabSiteKind.CollectionCentre);
        var alami = Staff(main, "M01");
        var benani = Staff(centre, "M02");
        var temp = Staff(agency, "M03", ContractType.Temporary);
        var april = new PayrollPeriod(main.Id, 2026, 4);

        await using (var db = Context())
        {
            db.Sites.AddRange(main, centre, agency);
            db.Employees.AddRange(alami, benani, temp);
            db.PayrollPeriods.Add(april);
            await db.SaveChangesAsync();
        }

        await using var read = Context();
        var queries = new PayrollQueries(read);

        var bds = await queries.EmployerOtherSitesAsync(main.Id, EmployerIdentifier.CnssAffiliation, 2026, 4);
        bds.NotOpened.Should().Equal(new EmployerSiteMissingMonth("Centre", 4));

        var year = await queries.EmployerOtherSitesAsync(main.Id, EmployerIdentifier.FiscalId, 2026, null);
        year.NotOpened.Select(m => m.Month).Should().Equal(Enumerable.Range(1, 12));
        year.NotOpened.Should().OnlyContain(m => m.SiteName == "Centre", "the agency's only staff is an agency temp");
    }

    [SqlServerFact]
    public async Task H4_the_month_of_a_rehire_carries_the_figures_of_each_contract()
    {
        var main = Site("Laboratoire", LabSiteKind.MainLaboratory);
        var e = Staff(main, "M05", ContractType.Temporary);
        e.RecordExit(new DateOnly(2026, 3, 15), ExitReason.EndOfFixedTerm, null).IsSuccess.Should().BeTrue();
        e.Rehire(new DateOnly(2026, 3, 16)).IsSuccess.Should().BeTrue();
        e.ContractType = ContractType.Permanent;

        await using (var db = Context())
        {
            db.Sites.Add(main);
            db.Employees.Add(e);
            await db.SaveChangesAsync();
        }

        await using var read = Context();
        var candidate = (await new PayrollQueries(read).CandidatesAsync(main.Id, new DateOnly(2026, 3, 1),
                                                                        new DateOnly(2026, 3, 31), 26m)).Single();

        candidate.MonthWorkingDays.Should().Be(0m, "both contracts together cover the month");
        candidate.PreviousContract!.ContractWorkingDays.Should().BePositive();
        candidate.CurrentContract!.ContractWorkingDays.Should().BePositive();
        (candidate.PreviousContract.ContractWorkingDays + candidate.CurrentContract.ContractWorkingDays)
            .Should().Be(candidate.CurrentContract.MonthWorkingDays);
    }
}
