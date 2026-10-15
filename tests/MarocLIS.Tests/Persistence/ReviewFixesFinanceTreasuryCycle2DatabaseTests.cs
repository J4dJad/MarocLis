using System.Globalization;
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
using MarocLIS.Domain.Treasury;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// Review 7 (finance cycle 2), treasury, against SQL Server: old bills read back only after the
/// opening balance, supplier cheques not cashed yet and the payroll in the forecast's opening, a
/// cheque handed back before remittance, and the withdrawals kept as a total before the upgrade.
/// </summary>
public sealed class ReviewFixesFinanceTreasuryCycle2DatabaseTests(FinanceTreasuryScratchDatabase database)
    : IClassFixture<FinanceTreasuryScratchDatabase>
{
    /// <summary>The backfill the RevueFinance1 migration runs (T14), checked here against the model's tables.</summary>
    private const string WithdrawalBackfill =
        """
        INSERT INTO [bil].[CashWithdrawals] ([Id], [CashSessionId], [Amount], [At], [Reason])
        SELECT s.[Id], s.[Id], s.[CashWithdrawals], s.[OpenedAt], NULL
        FROM [bil].[CashSessions] s
        WHERE s.[CashWithdrawals] > 0
          AND NOT EXISTS (SELECT 1 FROM [bil].[CashWithdrawals] w WHERE w.[CashSessionId] = s.[Id]);
        """;

    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset Now => new(today.ToDateTime(new TimeOnly(12, 0)));
        public DateOnly Today => today;
    }

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

    private ServiceProvider Services(Guid siteId, DateOnly today)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser>(new Operator(siteId));
        services.AddSingleton<IClock>(new FixedClock(today));
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

    private static DateOnly Day(int year, int month, int day) => new(year, month, day);

    private static DateTimeOffset At(DateOnly day, int hour) => new(day.ToDateTime(new TimeOnly(hour, 0)));

    private static readonly BankStatementCsvMapping Mapping = new() { DateColumn = 1, LabelColumn = 2, AmountColumn = 3 };

    private static void AddSupplierPayment(LisDbContext context, Guid site, SupplierPayment payment)
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

    private static void AddClosedPayroll(LisDbContext context, Guid site, int year, int month, decimal net, DateOnly closedOn)
    {
        var period = new PayrollPeriod(site, year, month);
        var payslip = new Payslip(Guid.NewGuid())
        {
            Number = "S" + Guid.NewGuid().ToString("N")[..6], FullName = "Salariés", Rib = Rib()
        };
        typeof(Payslip).GetProperty(nameof(Payslip.Amounts))!.SetValue(payslip, new PayslipAmounts { NetPay = net });
        period.Add(payslip).IsSuccess.Should().BeTrue();
        period.Close(At(closedOn, 18), Guid.NewGuid()).IsSuccess.Should().BeTrue();
        context.PayrollPeriods.Add(period);
    }

    private async Task<(ServiceProvider Provider, TreasuryService Treasury, Guid AccountId)> AccountAsync(
        Guid site, DateOnly today, decimal opening, DateOnly openedOn, params (DateOnly Date, decimal Amount)[] lines)
    {
        var provider = Services(site, today);
        var treasury = provider.CreateScope().ServiceProvider.GetRequiredService<TreasuryService>();
        var accountId = (await treasury.SaveBankAccountAsync(new BankAccountInput(
            null, "BMCE Bank", "Compte courant", Rib(), "51410001", opening, openedOn, true))).Value;

        if (lines.Length > 0)
        {
            var csv = "Date;Libellé;Montant\n" + string.Concat(lines.Select(l =>
                $"{l.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)};LIGNE;{l.Amount.ToString("0.00", CultureInfo.GetCultureInfo("fr-FR"))}\n"));
            (await treasury.ImportAsync(accountId, Encoding.UTF8.GetBytes(csv), "releve.csv", Mapping)).IsSuccess.Should().BeTrue();
        }

        return (provider, treasury, accountId);
    }

    // ---- T10, T12, T11 (pay day 5) -----------------------------------------------------------------

    [SqlServerFact]
    public async Task The_forecast_leaves_out_bills_debited_before_the_opening_and_counts_uncashed_cheques_and_the_payroll_once()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();
        var today = Day(2026, 11, 3);

        await using (var context = database.Context())
        {
            // T10: an old LCN, without its due date, issued and debited before the books opened on 30/09.
            AddSupplierPayment(context, site, new SupplierPayment(Day(2026, 9, 20), 150_000m,
                SupplierPaymentMethod.BillOfExchange, "LCN-OLD", DateTimeOffset.Now, user));
            // T12: a supplier cheque written before the last statement line and not cashed yet.
            AddSupplierPayment(context, site, new SupplierPayment(Day(2026, 10, 20), 30_000m,
                SupplierPaymentMethod.Cheque, "CHQ-30", DateTimeOffset.Now, user));
            // ...and one cashed on 22/10, on the statement but not reconciled yet.
            AddSupplierPayment(context, site, new SupplierPayment(Day(2026, 10, 18), 10_000m,
                SupplierPaymentMethod.Cheque, "CHQ-10", DateTimeOffset.Now, user));
            // T11: October closed on 30/10, paid on the 5th.
            AddClosedPayroll(context, site, 2026, 10, 120_000m, Day(2026, 10, 30));
            await context.SaveChangesAsync();
        }

        var (provider, treasury, _) = await AccountAsync(site, today, 300_000m, Day(2026, 9, 30),
                                                         (Day(2026, 10, 22), -10_000m), (Day(2026, 10, 25), 1_000m));
        using var _p = provider;

        var forecast = (await treasury.GetForecastAsync(new CashForecastOptions { PayrollDay = 5 })).Value;

        var recorded = forecast.Openings.Single(o => o.IsRecordedNotOnStatement);
        recorded.AsOf.Should().Be(Day(2026, 10, 25));
        recorded.BalanceMad.Should().Be(-30_000m, "one cheque is not cashed, the other is on the statement; the old LCN is in the opening balance; "
                                                  + "October's salaries are paid on 05/11");

        forecast.Flows.Where(f => f.Category == ForecastCategory.Payroll && f.Date == Day(2026, 11, 5))
                .Sum(f => f.AmountMad).Should().Be(-120_000m, "counted once, on the pay day");
    }

    // ---- T11 (pay day 25) --------------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_payroll_already_paid_on_the_chosen_pay_day_is_in_the_forecast_opening()
    {
        var site = Guid.NewGuid();
        var today = Day(2026, 10, 27);

        await using (var context = database.Context())
        {
            AddClosedPayroll(context, site, 2026, 10, 120_000m, Day(2026, 10, 24));
            await context.SaveChangesAsync();
        }

        var (provider, treasury, _) = await AccountAsync(site, today, 300_000m, Day(2026, 9, 30), (Day(2026, 10, 20), 1_000m));
        using var _p = provider;

        var forecast = (await treasury.GetForecastAsync(new CashForecastOptions { PayrollDay = 25 })).Value;

        forecast.Openings.Single(o => o.IsRecordedNotOnStatement).BalanceMad
                .Should().Be(-120_000m, "paid on 25/10, after the last statement line");
        forecast.Flows.Where(f => f.Category == ForecastCategory.Payroll).Min(f => f.Date)
                .Should().Be(Day(2026, 11, 25));
    }

    // ---- T10 (reconciliation) ----------------------------------------------------------------------

    [SqlServerFact]
    public async Task An_old_bill_is_read_back_no_further_than_the_opening_balance()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using (var context = database.Context())
        {
            AddSupplierPayment(context, site, new SupplierPayment(Day(2026, 9, 20), 50_000m,
                SupplierPaymentMethod.BillOfExchange, "LCN-BEFORE", DateTimeOffset.Now, user));
            AddSupplierPayment(context, site, new SupplierPayment(Day(2026, 10, 2), 48_000m,
                SupplierPaymentMethod.BillOfExchange, "LCN-AFTER", DateTimeOffset.Now, user));
            await context.SaveChangesAsync();
        }

        var (provider, treasury, accountId) = await AccountAsync(site, Day(2026, 11, 20), 300_000m, Day(2026, 9, 30));
        using var _p = provider;

        var data = (await treasury.GetReconciliationAsync(accountId, Day(2026, 11, 1), Day(2026, 11, 30))).Value;

        data.UnmatchedMovements.Select(m => m.Reference).Should().Equal("LCN-AFTER");
    }

    // ---- T13 ---------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_cheque_handed_back_before_its_shift_was_remitted_never_reaches_the_bank()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();
        var day = Day(2026, 10, 9);

        await using (var context = database.Context())
        {
            var shift = new CashSession(site, user, At(day, 8), 500m);
            context.CashSessions.Add(shift);

            // Refunded at 11:00, the shift closing at 19:00: handed back, never remitted.
            var returned = new Payment(400m, PaymentMethod.Cheque, day, user, site) { Reference = "CHQ-400", CashSessionId = shift.Id };
            returned.Refund(At(day, 11), "Annulation").IsSuccess.Should().BeTrue();

            // Refunded the next day, after the remittance: in, then out.
            var remitted = new Payment(700m, PaymentMethod.Cheque, day, user, site) { Reference = "CHQ-700", CashSessionId = shift.Id };
            remitted.Refund(At(day.AddDays(1), 10), "Trop-perçu").IsSuccess.Should().BeTrue();
            context.Payments.AddRange(returned, remitted);

            // A cheque deposit handed back in full while the shift was open.
            var deposit = new Deposit("REC/" + Guid.NewGuid().ToString("N")[..6], site, Guid.NewGuid(), Guid.NewGuid(), 300m,
                                      PaymentMethod.Cheque, At(day, 9), user) { Reference = "CHQ-300", CashSessionId = shift.Id };
            deposit.Refund(At(day, 12), "Demande annulée").IsSuccess.Should().BeTrue();
            context.Deposits.Add(deposit);

            shift.Close(At(day, 19), user, 500m, null).IsSuccess.Should().BeTrue();
            await context.SaveChangesAsync();
        }

        var (provider, treasury, accountId) = await AccountAsync(site, Day(2026, 10, 31), 0m, Day(2026, 9, 30));
        using var _p = provider;

        var movements = (await treasury.GetReconciliationAsync(accountId, Day(2026, 10, 1), Day(2026, 10, 31))).Value
            .UnmatchedMovements;

        movements.Select(m => (m.Reference, m.AmountMad))
                 .Should().BeEquivalentTo(new[] { ("CHQ-700", 700m), ("CHQ-700", -700m) });
    }

    // ---- T14 ---------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task The_backfill_keeps_withdrawals_recorded_as_a_total_and_their_matches()
    {
        var site = Guid.NewGuid();
        var user = Guid.NewGuid();
        var day = Day(2026, 10, 12);
        Guid shiftId;

        await using (var context = database.Context())
        {
            // 5 000 banked before the upgrade (total only), the shift still open.
            var shift = new CashSession(site, user, At(day, 8), 500m);
            shift.RecordWithdrawal(5_000m);
            context.CashSessions.Add(shift);
            await context.SaveChangesAsync();
            shiftId = shift.Id;

            await context.Database.ExecuteSqlRawAsync(WithdrawalBackfill);
            await context.Database.ExecuteSqlRawAsync(WithdrawalBackfill);   // runs once per session
        }

        await using (var context = database.Context())
        {
            // After the upgrade: 8 000 more, then closing.
            var shift = await context.CashSessions.Include(s => s.Withdrawals).SingleAsync(s => s.Id == shiftId);
            shift.RecordWithdrawal(8_000m, At(day, 17), "Bordereau 2");
            shift.Close(At(day, 19), user, 0m, "Fond au coffre").IsSuccess.Should().BeTrue();
            await context.SaveChangesAsync();
        }

        var (provider, treasury, accountId) = await AccountAsync(site, Day(2026, 10, 31), 0m, Day(2026, 9, 30));
        using var _p = provider;

        var movements = (await treasury.GetReconciliationAsync(accountId, Day(2026, 10, 1), Day(2026, 10, 31))).Value
            .UnmatchedMovements.Where(m => m.Kind == TreasuryMovementKind.CashDeposit).ToList();

        movements.Select(m => m.AmountMad).Should().BeEquivalentTo(new[] { 5_000m, 8_000m });
        movements.Single(m => m.AmountMad == 5_000m).Key.Should().Be(shiftId.ToString("N"),
            "keyed as the session total was, so a match made before the upgrade still holds it");
    }
}
