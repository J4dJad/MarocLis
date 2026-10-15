using System.Globalization;
using System.Text;
using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Treasury;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Treasury;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Review 6 (finance), treasury, against SQL Server: supplier bills on their due date, card
/// batches and refunds, till withdrawals one by one, the reconciliation's unmatched total, the
/// import's duplicate check and its withdrawal, and the forecast's opening.
/// </summary>
public sealed class ReviewFixesFinanceTreasuryDatabaseTests(FinanceTreasuryScratchDatabase database)
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
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

    private static string D(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static DateTimeOffset At(DateOnly day, int hour) => new(day.ToDateTime(new TimeOnly(hour, 0)));

    private static void AddSupplierPayment(MarocLIS.Infrastructure.Persistence.LisDbContext context, Guid site, SupplierPayment payment)
    {
        var supplier = new Supplier("F" + Guid.NewGuid().ToString("N")[..8], "Biomed Maroc");
        context.Suppliers.Add(supplier);
        var invoice = new SupplierInvoice(supplier.Id, site, "FA-" + Guid.NewGuid().ToString("N")[..6],
                                          payment.PaidOn.AddDays(-5), payment.PaidOn.AddDays(-4), Guid.NewGuid());
        context.SupplierInvoices.Add(invoice);
        context.Entry(invoice).Property(nameof(SupplierInvoice.Status)).CurrentValue = SupplierInvoiceStatus.Paid;
        context.SupplierPayments.Add(payment);
        context.Entry(payment).Property(nameof(SupplierPayment.SupplierInvoiceId)).CurrentValue = invoice.Id;
    }

    private static readonly BankStatementCsvMapping Mapping = new() { DateColumn = 1, LabelColumn = 2, AmountColumn = 3 };

    // ---- T1, T3, T6, T7 ----------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Movements_are_dated_when_the_bank_sees_them_and_the_unmatched_total_stops_at_the_period_end()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using (var context = database.Context())
        {
            // T1: an LCN issued 80 days ago, due 2 days ago; one recorded before due dates were kept.
            AddSupplierPayment(context, site, new SupplierPayment(Today.AddDays(-80), 48_000m,
                SupplierPaymentMethod.BillOfExchange, "LCN-48", DateTimeOffset.Now, user, Today.AddDays(-2)));
            AddSupplierPayment(context, site, new SupplierPayment(Today.AddDays(-100), 12_000m,
                SupplierPaymentMethod.BillOfExchange, "LCN-12", DateTimeOffset.Now, user));

            // T3: three card payments on day -6, the 500 refunded on day -4 where 900 more was taken.
            var refunded = new Payment(500m, PaymentMethod.Card, Today.AddDays(-6), user, site);
            refunded.Refund(At(Today.AddDays(-4), 10), "Annulation").IsSuccess.Should().BeTrue();
            context.Payments.AddRange(
                new Payment(1_000m, PaymentMethod.Card, Today.AddDays(-6), user, site),
                refunded,
                new Payment(700m, PaymentMethod.Card, Today.AddDays(-6), user, site),
                new Payment(900m, PaymentMethod.Card, Today.AddDays(-4), user, site));

            // A card deposit refunded the same day: nothing reaches the bank.
            var deposit = new Deposit("REC/" + Guid.NewGuid().ToString("N")[..6], site, Guid.NewGuid(), Guid.NewGuid(), 300m,
                                      PaymentMethod.Card, At(Today.AddDays(-3), 9), user);
            deposit.Refund(At(Today.AddDays(-3), 11), "Demande annulée").IsSuccess.Should().BeTrue();
            context.Deposits.Add(deposit);

            // A cheque refunded by the laboratory: money going out.
            var cheque = new Payment(400m, PaymentMethod.Cheque, Today.AddDays(-5), user, site) { Reference = "CHQ-400" };
            cheque.Refund(At(Today.AddDays(-3), 15), "Trop-perçu").IsSuccess.Should().BeTrue();
            context.Payments.Add(cheque);

            // T6: a closed shift banking twice, an open one that already banked once.
            var closed = new CashSession(site, user, At(Today.AddDays(-5), 8), 500m);
            closed.RecordWithdrawal(5_000m, At(Today.AddDays(-5), 11), "Bordereau 1");
            closed.RecordWithdrawal(8_000m, At(Today.AddDays(-5), 17), "Bordereau 2");
            closed.Close(At(Today.AddDays(-5), 19), user, 0m, "Fond au coffre").IsSuccess.Should().BeTrue();
            var open = new CashSession(site, Guid.NewGuid(), At(Today.AddDays(-5), 8), 500m);
            open.RecordWithdrawal(2_000m, At(Today.AddDays(-5), 12), "Bordereau 3");
            context.CashSessions.AddRange(closed, open);

            await context.SaveChangesAsync();
        }

        using var provider = Services(site);
        using var scope = provider.CreateScope();
        var treasury = scope.ServiceProvider.GetRequiredService<TreasuryService>();
        var accountId = (await treasury.SaveBankAccountAsync(new BankAccountInput(
            null, "BMCE Bank", "Compte courant", Rib(), "51410001", 0m, Today.AddDays(-200), true))).Value;

        var data = (await treasury.GetReconciliationAsync(accountId, Today.AddDays(-30), Today)).Value;
        var movements = data.UnmatchedMovements;

        var lcn = movements.Single(m => m.Reference == "LCN-48");
        lcn.Date.Should().Be(Today.AddDays(-2), "a bill reaches the bank on its due date");
        movements.Should().Contain(m => m.Reference == "LCN-12", "a bill without its due date is read further back");

        movements.Where(m => m.Kind == TreasuryMovementKind.CardBatch)
                 .Select(m => (m.Date, m.AmountMad))
                 .Should().BeEquivalentTo(new[] { (Today.AddDays(-6), 2_200m), (Today.AddDays(-4), 400m) },
                                          "each day's batch is what the acquirer credits that day");

        movements.Single(m => m.Kind == TreasuryMovementKind.PatientRefund).AmountMad.Should().Be(-400m);

        movements.Where(m => m.Kind == TreasuryMovementKind.CashDeposit).Select(m => m.AmountMad)
                 .Should().BeEquivalentTo(new[] { 5_000m, 8_000m }, "one movement per slip, the open shift left out");

        // T7: a period ending three days ago counts no movement dated after it in its total.
        var earlier = (await treasury.GetReconciliationAsync(accountId, Today.AddDays(-30), Today.AddDays(-3))).Value;
        earlier.UnmatchedMovements.Should().Contain(m => m.Date > Today.AddDays(-3), "read a little past the end to match");
        earlier.Summary.UnmatchedMovementsMad.Should().Be(
            Money.Round(earlier.UnmatchedMovements.Where(m => m.Date <= Today.AddDays(-3)).Sum(m => m.AmountMad)));
    }

    // ---- T4, T5, T8 --------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task An_overlapping_statement_brings_nothing_twice_and_a_wrong_import_can_be_withdrawn()
    {
        var site = Guid.NewGuid();
        using var provider = Services(site);
        using var scope = provider.CreateScope();
        var treasury = scope.ServiceProvider.GetRequiredService<TreasuryService>();
        var accountId = (await treasury.SaveBankAccountAsync(new BankAccountInput(
            null, "CIH Bank", "Compte courant", Rib(), "51410002", 1_000m, Today.AddDays(-60), true))).Value;

        // Mid-month, from the web banking: cut labels, no reference, dot decimals (read by default).
        var web = Encoding.UTF8.GetBytes(
            "Date;Libellé;Montant\n" +
            $"{D(Today.AddDays(-20))};VIR CNSS;18400.50\n" +
            $"{D(Today.AddDays(-18))};FRAIS;-25.00\n" +
            $"{D(Today.AddDays(-18))};FRAIS;-25.00\n");
        (await treasury.ImportAsync(accountId, web, "web.csv", Mapping)).Value.Imported.Should().Be(3);

        // The month's statement: full labels, a reference column, a 320-character motive.
        var statement = Encoding.UTF8.GetBytes(
            "Date;Libellé;Référence;Montant\n" +
            $"{D(Today.AddDays(-20))};VIR CNSS BORDEREAU AMO-1;AMO-1;18 400,50\n" +
            $"{D(Today.AddDays(-18))};FRAIS TENUE COMPTE;;-25,00\n" +
            $"{D(Today.AddDays(-18))};FRAIS TENUE COMPTE;;-25,00\n" +
            $"{D(Today.AddDays(-5))};VIR SEPA {new string('M', 320)};;-3 600,00\n");
        var full = new BankStatementCsvMapping { DateColumn = 1, LabelColumn = 2, ReferenceColumn = 3, AmountColumn = 4 };

        var preview = (await treasury.PreviewImportAsync(accountId, statement, full)).Value;
        preview.DuplicateCount.Should().Be(3);
        preview.NewCount.Should().Be(1);

        (await treasury.ImportAsync(accountId, statement, "releve.csv", full)).Value.Imported.Should().Be(1);

        var account = (await treasury.GetBankAccountsAsync()).Value.Single();
        account.StatementBalanceMad.Should().Be(1_000m + 18_400.50m - 50m - 3_600m);

        var data = (await treasury.GetReconciliationAsync(accountId, Today.AddDays(-30), Today)).Value;
        data.Lines.Single(l => l.AmountMad == -3_600m).Label.Should().HaveLength(BankStatementLine.LabelMaxLength);

        // T8: a comment longer than its column is refused by name, not by the database.
        var charge = data.Lines.First(l => l.AmountMad == -25m);
        (await treasury.JustifyAsync(charge.Id, new string('j', 501))).FirstError.ResourceKey.Should().Be("Treasury_TextTooLong");

        // T4: a line of the web import is reconciled, so that import cannot be withdrawn yet.
        (await treasury.JustifyAsync(charge.Id, "Frais bancaires")).IsSuccess.Should().BeTrue();
        (await treasury.WithdrawImportAsync(charge.Id)).FirstError.ResourceKey.Should().Be("BankStatement_ImportHasMatches");

        (await treasury.UnmatchAsync(charge.Id)).IsSuccess.Should().BeTrue();
        (await treasury.WithdrawImportAsync(charge.Id)).Value.Should().Be(3);

        account = (await treasury.GetBankAccountsAsync()).Value.Single();
        account.StatementBalanceMad.Should().Be(1_000m - 3_600m, "the withdrawn lines leave the balance");

        // The statement brings them back, on fingerprints the withdrawn lines do not hold.
        (await treasury.ImportAsync(accountId, statement, "releve.csv", full)).Value.Imported.Should().Be(3);
        account = (await treasury.GetBankAccountsAsync()).Value.Single();
        account.StatementBalanceMad.Should().Be(1_000m + 18_400.50m - 50m - 3_600m);

        await using var check = database.Context();
        (await check.BankStatementLines.IgnoreQueryFilters().CountAsync(l => l.BankAccountId == accountId && l.IsDeleted))
            .Should().Be(3, "a withdrawn line is kept, flagged, for the audit trail");
    }

    // ---- T2 ------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task The_forecast_opens_on_the_statement_plus_what_was_recorded_since_and_a_bill_falls_on_its_due_date()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using (var context = database.Context())
        {
            AddSupplierPayment(context, site, new SupplierPayment(Today.AddDays(-5), 120_000m,
                SupplierPaymentMethod.Transfer, "VIR-120", DateTimeOffset.Now, user));
            AddSupplierPayment(context, site, new SupplierPayment(Today.AddDays(-5), 80_000m,
                SupplierPaymentMethod.BillOfExchange, "LCN-80", DateTimeOffset.Now, user, Today.AddDays(20)));
            context.Payments.Add(new Payment(90_000m, PaymentMethod.ThirdPartyPayer, Today.AddDays(-3), user, site)
            {
                RemittanceReference = "CNSS-90"
            });
            await context.SaveChangesAsync();
        }

        using var provider = Services(site);
        using var scope = provider.CreateScope();
        var treasury = scope.ServiceProvider.GetRequiredService<TreasuryService>();
        var accountId = (await treasury.SaveBankAccountAsync(new BankAccountInput(
            null, "Attijariwafa", "Compte courant", Rib(), "51410003", 299_000m, Today.AddDays(-60), true))).Value;
        (await treasury.ImportAsync(accountId, Encoding.UTF8.GetBytes(
            $"Date;Libellé;Montant\n{D(Today.AddDays(-10))};REMISE;1000,00\n"), "releve.csv", Mapping)).IsSuccess.Should().BeTrue();

        var forecast = (await treasury.GetForecastAsync()).Value;

        var recorded = forecast.Openings.Single(o => o.IsRecordedNotOnStatement);
        recorded.BalanceMad.Should().Be(-120_000m + 90_000m);
        recorded.AsOf.Should().Be(Today.AddDays(-10));
        forecast.OpeningMad.Should().Be(300_000m - 30_000m);

        var bill = forecast.Flows.Single(f => f.Category == ForecastCategory.SupplierPayments);
        bill.AmountMad.Should().Be(-80_000m);
        bill.Date.Should().Be(Today.AddDays(20));
    }
}
