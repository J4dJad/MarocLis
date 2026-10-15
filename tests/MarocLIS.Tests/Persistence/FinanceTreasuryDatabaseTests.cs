using System.Text;
using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Treasury;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the treasury tests, created from the model.</summary>
public sealed class FinanceTreasuryScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_finF_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public FinanceTreasuryScratchDatabase()
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
/// The treasury queries and services against SQL Server: the statement import and its duplicate
/// check, the movements read from payments, card batches, payer remittances, supplier payments,
/// banked till withdrawals and payroll, the automatic and manual reconciliation, the cash forecast
/// and the tills' sign-off.
/// </summary>
public sealed class FinanceTreasuryDatabaseTests(FinanceTreasuryScratchDatabase database)
    : IClassFixture<FinanceTreasuryScratchDatabase>
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private sealed class Operator(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "compta";
        public string FullName => "Comptable";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [siteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "COMPTA-1";
    }

    private ServiceProvider Services(Guid siteId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId));
        services.AddIsolatedQueries<ITreasuryQueries, TreasuryQueries>();
        return services.BuildServiceProvider();
    }

    private static string Rib()
    {
        var first22 = string.Concat(Enumerable.Range(0, 22).Select(_ => Random.Shared.Next(10)));
        var remainder = 0;
        foreach (var c in first22 + "00") remainder = (remainder * 10 + (c - '0')) % 97;
        return first22 + ((97 - remainder) % 97).ToString("00");
    }

    private static string D(DateOnly date) => date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly DateOnly MonthStart = new(Today.Year, Today.Month, 1);
    private static readonly DateOnly PreviousMonthEnd = MonthStart.AddDays(-1);

    private static byte[] Statement() => Encoding.UTF8.GetBytes(
        "Date;Libellé;Référence;Débit;Crédit\n" +
        $"{D(Today.AddDays(-10))};REMISE CHQ 0012345;;;800,00\n" +
        $"{D(Today.AddDays(-9))};REMISE CB TPE 4455;;;1 250,00\n" +
        $"{D(Today.AddDays(-8))};VERSEMENT ESPECES;;;3 000,00\n" +
        $"{D(Today.AddDays(-5))};CHQ 7654321;;3 600,00;\n" +
        $"{D(Today.AddDays(-5))};FRAIS TENUE DE COMPTE;;25,00;\n" +
        $"{D(Today.AddDays(-4))};VIR CNSS AMO-B1;AMO-B1;;18 400,50\n" +
        $"{D(MonthStart)};VIR SALAIRES;;7 000,00;\n");

    private static readonly BankStatementCsvMapping Mapping = new()
    {
        DateColumn = 1, LabelColumn = 2, ReferenceColumn = 3, DebitColumn = 4, CreditColumn = 5
    };

    /// <summary>What the application recorded over the period, for the site.</summary>
    private async Task SeedMovementsAsync(Guid site)
    {
        var user = Guid.NewGuid();
        await using var context = database.Context();

        context.Payments.AddRange(
            new Payment(800m, PaymentMethod.Cheque, Today.AddDays(-11), user, site) { Reference = "0012345" },
            new Payment(800m, PaymentMethod.Cheque, Today.AddDays(-12), user, site) { Reference = "0099999" },
            new Payment(500m, PaymentMethod.Card, Today.AddDays(-10), user, site),
            new Payment(750m, PaymentMethod.Card, Today.AddDays(-10), user, site),
            new Payment(10_000m, PaymentMethod.ThirdPartyPayer, Today.AddDays(-6), user, site) { RemittanceReference = "AMO-B1" },
            new Payment(8_400.50m, PaymentMethod.ThirdPartyPayer, Today.AddDays(-6), user, site) { RemittanceReference = "AMO-B1" },
            // Cash never reaches the bank as such: only the till's withdrawals do.
            new Payment(300m, PaymentMethod.Cash, Today.AddDays(-8), user, site) { CashSessionId = Guid.NewGuid() });

        var session = new CashSession(site, user, new DateTimeOffset(Today.AddDays(-8).ToDateTime(new TimeOnly(8, 0))), 500m);
        session.RecordWithdrawal(3_000m, session.OpenedAt.AddHours(3), "Versement BMCE");
        // Only a closed shift's withdrawals are offered: an open one can still take more out.
        session.Close(session.OpenedAt.AddHours(9), user, 0m, "Fond remis au coffre").IsSuccess.Should().BeTrue();
        context.CashSessions.Add(session);

        var supplier = new Supplier("F" + Guid.NewGuid().ToString("N")[..8], "Biomed Maroc");
        context.Suppliers.Add(supplier);

        var invoice = new SupplierInvoice(supplier.Id, site, "FA-" + Guid.NewGuid().ToString("N")[..6],
                                          Today.AddDays(-40), Today.AddDays(-38), user);
        invoice.AddLine(new SupplierInvoiceLine(Guid.NewGuid(), Guid.NewGuid(), "Réactifs", 10m, 500m, 0.20m))
               .IsSuccess.Should().BeTrue();
        context.SupplierInvoices.Add(invoice);
        context.Entry(invoice).Property(nameof(SupplierInvoice.Status)).CurrentValue = SupplierInvoiceStatus.PartiallyPaid;
        context.Entry(invoice).Property(nameof(SupplierInvoice.DueDate)).CurrentValue = Today.AddDays(10);

        var payment = new SupplierPayment(Today.AddDays(-12), 3_600m, SupplierPaymentMethod.Cheque, "7654321",
                                          DateTimeOffset.Now, user);
        context.SupplierPayments.Add(payment);
        context.Entry(payment).Property(nameof(SupplierPayment.SupplierInvoiceId)).CurrentValue = invoice.Id;

        var period = new PayrollPeriod(site, PreviousMonthEnd.Year, PreviousMonthEnd.Month);
        var slip = new Payslip(Guid.NewGuid()) { FullName = "Technicien", Rib = Rib(), Number = "M1" };
        typeof(Payslip).GetProperty(nameof(Payslip.Amounts))!.SetValue(slip, new PayslipAmounts { NetPay = 7_000m });
        period.Add(slip).IsSuccess.Should().BeTrue();
        context.PayrollPeriods.Add(period);
        context.Entry(period).Property(nameof(PayrollPeriod.Status)).CurrentValue = PayrollStatus.Closed;

        await context.SaveChangesAsync();
    }

    [SqlServerFact]
    public async Task A_statement_is_imported_once_and_reconciled_with_what_was_recorded()
    {
        var site = Guid.NewGuid();
        await SeedMovementsAsync(site);

        using var provider = Services(site);
        using var scope = provider.CreateScope();
        var treasury = scope.ServiceProvider.GetRequiredService<TreasuryService>();

        var accountId = (await treasury.SaveBankAccountAsync(new BankAccountInput(
            null, "BMCE Bank", "Compte courant", Rib(), "51410001", 10_000m, Today.AddDays(-60), true))).Value;

        // The same RIB again is refused.
        var account = (await treasury.GetBankAccountsAsync()).Value.Single();
        (await treasury.SaveBankAccountAsync(new BankAccountInput(null, "Autre", "Doublon", account.Rib, "51410002",
                                                                  0m, Today, true)))
            .FirstError.ResourceKey.Should().Be("BankAccount_RibInUse");

        // Import, then the same file again: everything already there.
        var preview = (await treasury.PreviewImportAsync(accountId, Statement(), Mapping)).Value;
        preview.NewCount.Should().Be(7);
        preview.NewTotalMad.Should().Be(12_825.50m);

        var imported = (await treasury.ImportAsync(accountId, Statement(), "releve.csv", Mapping)).Value;
        imported.Imported.Should().Be(7);

        (await treasury.PreviewImportAsync(accountId, Statement(), Mapping)).Value.DuplicateCount.Should().Be(7);
        (await treasury.ImportAsync(accountId, Statement(), "releve.csv", Mapping))
            .FirstError.ResourceKey.Should().Be("BankStatement_NothingToImport");

        var from = Today.AddDays(-40);

        // What the application recorded and the bank should show.
        var before = (await treasury.GetReconciliationAsync(accountId, from, Today)).Value;
        before.Lines.Should().HaveCount(7);
        before.UnmatchedMovements.Select(m => m.Kind).Should().BeEquivalentTo(
        [
            TreasuryMovementKind.PatientPayment, TreasuryMovementKind.PatientPayment, TreasuryMovementKind.CardBatch,
            TreasuryMovementKind.PayerSettlement, TreasuryMovementKind.SupplierPayment, TreasuryMovementKind.CashDeposit,
            TreasuryMovementKind.Payroll
        ]);
        before.UnmatchedMovements.Single(m => m.Kind == TreasuryMovementKind.CardBatch).AmountMad.Should().Be(1_250m);
        before.UnmatchedMovements.Single(m => m.Kind == TreasuryMovementKind.PayerSettlement).AmountMad.Should().Be(18_400.50m);
        before.UnmatchedMovements.Single(m => m.Kind == TreasuryMovementKind.SupplierPayment).AmountMad.Should().Be(-3_600m);
        before.UnmatchedMovements.Single(m => m.Kind == TreasuryMovementKind.Payroll).AmountMad.Should().Be(-7_000m);
        before.Summary.StatementBalanceMad.Should().Be(22_825.50m);
        before.Summary.ReconciledBalanceMad.Should().Be(10_000m);

        // The automatic match takes the six it can be sure of, the bank charge stays.
        (await treasury.AutoMatchAsync(accountId, from, Today)).Value.Should().Be(6);

        var after = (await treasury.GetReconciliationAsync(accountId, from, Today)).Value;
        after.UnmatchedMovements.Should().ContainSingle().Which.Reference.Should().Be("0099999");
        var charge = after.Lines.Single(l => !l.IsReconciled);
        charge.AmountMad.Should().Be(-25m);
        after.Lines.Single(l => l.Label.StartsWith("REMISE CHQ", StringComparison.Ordinal)).IsAutomatic.Should().BeTrue();

        (await treasury.JustifyAsync(charge.Id, "Frais bancaires du mois")).IsSuccess.Should().BeTrue();
        var justified = (await treasury.GetReconciliationAsync(accountId, from, Today)).Value.Summary;
        justified.ReconciledBalanceMad.Should().Be(justified.StatementBalanceMad);
        justified.UnmatchedStatementCount.Should().Be(0);

        // Undo the card batch, then match it by hand.
        var card = after.Lines.Single(l => l.Label.StartsWith("REMISE CB", StringComparison.Ordinal));
        (await treasury.UnmatchAsync(card.Id)).IsSuccess.Should().BeTrue();
        var reopened = (await treasury.GetReconciliationAsync(accountId, from, Today)).Value;
        var batch = reopened.UnmatchedMovements.Single(m => m.Kind == TreasuryMovementKind.CardBatch);

        (await treasury.MatchAsync(card.Id, [batch], null)).IsSuccess.Should().BeTrue();
        (await treasury.MatchAsync(card.Id, [batch], null)).FirstError.ResourceKey.Should().Be("BankMatch_LineAlreadyMatched");

        // A movement already held by a line cannot explain another one.
        var cheque = reopened.UnmatchedMovements.Single(m => m.Reference == "0099999");
        (await treasury.MatchAsync(after.Lines.Single(l => l.Label.StartsWith("VERSEMENT", StringComparison.Ordinal)).Id,
                                   [cheque], "x")).FirstError.ResourceKey.Should().Be("BankMatch_LineAlreadyMatched");

        await using var check = database.Context();
        (await check.BankMatches.CountAsync(m => m.BankAccountId == accountId && m.UnmatchedAt != null))
            .Should().Be(1, "the undone match is kept, closed");
        (await check.BankAccounts.SingleAsync(a => a.Id == accountId)).CsvMapping.Should().NotBeNullOrEmpty();
    }

    [SqlServerFact]
    public async Task The_forecast_reads_the_balances_the_claims_the_invoices_and_the_payroll()
    {
        var site = Guid.NewGuid();
        await SeedMovementsAsync(site);

        await using (var context = database.Context())
        {
            var payer = new Payer("T" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Payeur test"), PayerKind.AmoCnss)
            {
                PaymentTermsDays = 30
            };
            context.Payers.Add(payer);

            Invoice Claim(string reference, DateOnly submitted, decimal share)
            {
                var invoice = new Invoice("FAC/" + Guid.NewGuid().ToString("N")[..8], site, InvoiceKind.Invoice,
                                          submitted.AddDays(-5), Guid.NewGuid())
                {
                    PayerId = payer.Id,
                    ClaimReference = reference,
                    ClaimSubmittedOn = submitted,
                    ClaimStatus = ClaimStatus.Submitted
                };
                context.Invoices.Add(invoice);
                context.Entry(invoice).Property(nameof(Invoice.Status)).CurrentValue = InvoiceStatus.Issued;
                context.Entry(invoice).Property(nameof(Invoice.PayerShareAmount)).CurrentValue = share;
                return invoice;
            }

            Claim("B-RECENT", Today.AddDays(-10), 3_000m);
            Claim("B-RECENT", Today.AddDays(-10), 2_000m);
            Claim("B-OLD", Today.AddDays(-100), 1_500m);

            context.TreasuryForecastItems.Add(new TreasuryForecastItem(site, "Loyer", -12_000m, ForecastRecurrence.Monthly,
                                                                       Today.AddDays(3)));
            await context.SaveChangesAsync();
        }

        using var provider = Services(site);
        using var scope = provider.CreateScope();
        var treasury = scope.ServiceProvider.GetRequiredService<TreasuryService>();

        var accountId = (await treasury.SaveBankAccountAsync(new BankAccountInput(
            null, "CIH Bank", "Compte courant", Rib(), "51410002", 10_000m, Today.AddDays(-60), true))).Value;
        (await treasury.ImportAsync(accountId, Statement(), "releve.csv", Mapping)).IsSuccess.Should().BeTrue();

        var forecast = (await treasury.GetForecastAsync()).Value;

        forecast.Weeks.Should().HaveCount(13);
        forecast.OpeningMad.Should().Be(22_825.50m);

        // The recent batch, both invoices together, 30 days after its submission (no history yet).
        var payerFlow = forecast.Flows.Single(f => f.Category == ForecastCategory.PayerReceipts);
        payerFlow.AmountMad.Should().Be(5_000m);
        payerFlow.Date.Should().Be(Today.AddDays(20));
        forecast.OverduePayerReceiptsMad.Should().Be(1_500m, "the old batch is late and shown apart");

        // The supplier invoice: 6,000 including VAT, 3,600 already paid.
        var supplierFlow = forecast.Flows.Single(f => f.Category == ForecastCategory.SupplierPayments);
        supplierFlow.AmountMad.Should().Be(-2_400m);
        supplierFlow.Date.Should().Be(Today.AddDays(10));

        forecast.LastPayrollNetMad.Should().Be(7_000m);
        forecast.Flows.Where(f => f.Category == ForecastCategory.Payroll).Should().HaveCountGreaterThanOrEqualTo(2)
            .And.OnlyContain(f => f.AmountMad == -7_000m);
        forecast.Flows.Count(f => f.Category == ForecastCategory.Other).Should().Be(3);

        var total = forecast.Flows.Sum(f => f.AmountMad);
        forecast.Weeks[^1].ClosingMad.Should().Be(Money.Round(22_825.50m + total));

        // Asked for: the late batch counts in the first week.
        var withOverdue = (await treasury.GetForecastAsync(new CashForecastOptions { IncludeOverduePayerReceipts = true })).Value;
        withOverdue.Weeks[0].PayerReceiptsMad.Should().Be(1_500m);
    }

    [SqlServerFact]
    public async Task A_closed_till_is_signed_off_by_the_supervisor()
    {
        var site = Guid.NewGuid();
        var cashier = Guid.NewGuid();

        var session = new CashSession(site, cashier, DateTimeOffset.Now.AddHours(-9), 500m);
        session.Accumulate(PaymentMethod.Cash, 1_000m);
        session.Close(DateTimeOffset.Now.AddHours(-1), cashier, 1_480m, "Pièce manquante").IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.CashSessions.Add(session);
            await context.SaveChangesAsync();
        }

        using var provider = Services(site);
        using var scope = provider.CreateScope();
        var review = scope.ServiceProvider.GetRequiredService<CashSessionReviewService>();

        var pending = (await review.GetSessionsAsync(Today.AddDays(-1), Today, pendingOnly: true)).Value;
        pending.Should().ContainSingle().Which.Variance.Should().Be(-20m);

        (await review.ReconcileAsync(session.Id, null)).FirstError.ResourceKey
            .Should().Be("CashSession_ReconciliationCommentRequired");
        (await review.ReconcileAsync(session.Id, "Écart accepté")).IsSuccess.Should().BeTrue();

        (await review.GetSessionsAsync(Today.AddDays(-1), Today, pendingOnly: true)).Value.Should().BeEmpty();
        var all = (await review.GetSessionsAsync(Today.AddDays(-1), Today, pendingOnly: false)).Value;
        all.Should().ContainSingle().Which.ReconciliationComment.Should().Be("Écart accepté");

        await using var check = database.Context();
        (await check.CashSessions.SingleAsync(s => s.Id == session.Id)).Status.Should().Be(CashSessionStatus.Reconciled);
    }
}
