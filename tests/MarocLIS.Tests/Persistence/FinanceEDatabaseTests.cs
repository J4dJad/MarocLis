using FluentAssertions;
using MarocLIS.Application.Accounting;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Patients;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the accounting export, created from the model.</summary>
public sealed class FinanceEScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_finE_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public FinanceEScratchDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = "";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = Name }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// The accounting queries against SQL Server: what billing, the till and payroll stored comes back
/// as sources, and the entries built from them balance on their accounts.
/// </summary>
public sealed class FinanceEDatabaseTests(FinanceEScratchDatabase database) : IClassFixture<FinanceEScratchDatabase>
{
    private static readonly TimeSpan Morocco = TimeSpan.FromHours(1);

    private static DateTimeOffset At(int day, int hour) => new(2026, 9, day, hour, 0, 0, Morocco);

    [SqlServerFact]
    public async Task A_month_of_billing_till_and_payroll_exports_balanced_entries()
    {
        var user = Guid.NewGuid();
        var site = new LabSite("FE", new MultilingualText("Site finance"), LabSiteKind.MainLaboratory);
        var department = new Department("HEMA", new MultilingualText("Hématologie"));
        var test = new AnalysisTest("NFS", new MultilingualText("Numération"), department.Id, Guid.NewGuid());
        var patient = new Patient("MR-0001", "ALAMI", "Sara", null, Gender.Unknown, site.Id);
        var payer = new Payer("CNSS-FE", new MultilingualText("AMO CNSS"), PayerKind.AmoCnss) { AccountingAccount = "34211" };

        var till = new CashSession(site.Id, user, At(10, 8), 100m);
        till.Accumulate(PaymentMethod.Cash, 30m);
        till.RecordWithdrawal(20m);
        till.Close(At(10, 18), user, 109.5m, "Pièce manquante").IsSuccess.Should().BeTrue();

        var invoice = new Invoice(string.Empty, site.Id, InvoiceKind.Invoice, new DateOnly(2026, 9, 10), user)
        {
            PatientId = patient.Id, PayerId = payer.Id, BilledToName = "ALAMI Sara", ClaimStatus = ClaimStatus.Prepared
        };
        var line = new InvoiceLine("Numération", 100m) { AnalysisTestId = test.Id };
        invoice.AddLine(line);
        line.SetCoverageSplit(0.70m, payerUsesReferenceTariff: false);
        invoice.Issue("FAC/FE/2026/000001", At(10, 9), null).IsSuccess.Should().BeTrue();
        invoice.RecordPayment(new Payment(30m, PaymentMethod.Cash, new DateOnly(2026, 9, 10), user, site.Id)
                              { CashSessionId = till.Id }).IsSuccess.Should().BeTrue();
        invoice.ClaimStatus = ClaimStatus.Submitted;
        invoice.RecordPayment(new Payment(70m, PaymentMethod.ThirdPartyPayer, new DateOnly(2026, 9, 25), user, site.Id)
                              { RemittanceReference = "VIR-CNSS" }).IsSuccess.Should().BeTrue();

        var credit = new Invoice(string.Empty, site.Id, InvoiceKind.CreditNote, new DateOnly(2026, 9, 20), user)
        {
            PatientId = patient.Id, BilledToName = "ALAMI Sara", CorrectsInvoiceId = invoice.Id
        };
        credit.AddLine(new InvoiceLine("Avoir partiel", 20m));
        credit.Issue("AV/FE/2026/000001", At(20, 9), null).IsSuccess.Should().BeTrue();

        var deposit = new Deposit("REC/FE/2026/000001", site.Id, Guid.NewGuid(), patient.Id, 50m, PaymentMethod.Card,
                                  At(5, 10), user);
        deposit.Refund(At(6, 11), "Dossier annulé").IsSuccess.Should().BeTrue();

        var month = new PayrollPeriod(site.Id, 2026, 9);
        var slip = new Payslip(Guid.NewGuid())
        {
            Number = "PAIE-1", FullName = "BENNANI Omar", BaseSalary = 6000m, HireDate = new DateOnly(2020, 1, 6),
            ContractType = ContractType.Permanent, DependentPersons = 2
        };
        month.Add(slip);
        slip.Recompute(new PayslipVariables { AdvanceDeduction = 300m }, new PayrollParameters(2026), month.LastDay);
        month.Close(At(30, 18), user).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.AddRange(site, department, test, patient, payer, till, invoice, credit, deposit, month);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new AccountingQueries(read);
        var (from, to) = (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        var sources = await queries.GetSalesSourcesAsync([site.Id], from, to);
        var payroll = await queries.GetPayrollMonthsAsync([site.Id], from, to);

        sources.Documents.Should().HaveCount(2);
        sources.Documents.Single(d => d.Kind == InvoiceKind.Invoice).Lines.Single().DepartmentCode.Should().Be("HEMA");
        sources.Documents.Single(d => d.Kind == InvoiceKind.CreditNote).Original!.Number.Should().Be("FAC/FE/2026/000001");
        sources.Payments.Should().HaveCount(2);
        sources.Deposits.Single().RefundedOn.Should().Be(new DateOnly(2026, 9, 6));
        sources.CashSessions.Single().Variance.Should().Be(-0.5m);
        payroll.Single().Advances.Should().Be(300m);

        var entries = SalesAccountingJournal.Build(sources, AccountingSettings.Default, from, to, AccountingJournals.All)
                                            .Concat(PayrollAccountingJournal.Build(payroll, AccountingSettings.Default))
                                            .ToList();

        AccountingExportCheck.Check(entries).Should().BeEmpty();
        var sale = entries.Single(e => e.Piece == "FAC/FE/2026/000001" && e.Journal == "VT");
        sale.Lines.Should().Contain(l => l.Account == "34211" && l.ThirdParty == "CNSS-FE" && l.Debit == 70m)
            .And.Contain(l => l.Account == "3421" && l.Debit == 30m);
        entries.Should().Contain(e => e.Piece == "VIR-CNSS" && e.Lines.Any(l => l.Account == "34211" && l.Credit == 70m));
        entries.Should().Contain(e => e.Journal == "CAI" && e.Lines.Any(l => l.Account == "5115" && l.Debit == 20m));
        entries.Should().Contain(e => e.Kind == AccountingJournals.Payroll && e.IsBalanced);

        // Outside the period, nothing comes back.
        (await queries.GetSalesSourcesAsync([site.Id], new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)))
            .Documents.Should().BeEmpty();
    }
}
