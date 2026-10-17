using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Billing;
using MarocLIS.Application.HumanResources;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.HumanResources;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesHrTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Third verification (W1 to W5): a departure corrected or withdrawn after its settlement reached
/// the payroll (W1), the month of a re-hire paid on the previous contract (W2), the year's rates
/// retaken on reopening (W3), the supplier's discount on an automatic order (W4) and the non-cash
/// lines of the shift after a refund or a bounce (W5). The leave backfill (W6) runs on SQL Server.
/// </summary>
public sealed class ReviewFixesRound3RestTests
{
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static Employee Technician(DateOnly hired, decimal salary = 6000m) =>
        new("M0001", "ALAMI", "Sara", EmployeeJob.Technician, hired, Site) { BaseSalary = salary };

    private static PersonnelService Personnel(Employee employee, InMemoryRepository<FinalSettlement> settlements,
                                              InMemoryRepository<LeaveLedgerEntry> ledger) =>
        new(Repo(employee), new InMemoryRepository<CredentialFile>(), settlements, ledger, new InMemoryRepository<LeaveRequest>(), null!,
            new FakeHrDocumentQueries(), null!, new FakeUnitOfWork(), new AllowAll(),
            new FakeUser(Permissions.HumanResources.Manage), new FixedClock(), new NoLinkedAccounts());

    private static (PayrollPeriod Period, Payslip Slip) MonthWithIndemnity(Employee e, int month, decimal indemnity,
                                                                         decimal monthDays = 0m, decimal contractDays = 0m)
    {
        var period = new PayrollPeriod(Site, 2026, month);
        var slip = new Payslip(e.Id) { Number = e.Number, BaseSalary = 6000m, HireDate = e.HireDate };
        slip.Recompute(new PayslipVariables
        {
            LeaveIndemnity = indemnity, MonthWorkingDays = monthDays, ContractWorkingDays = contractDays
        }, new PayrollParameters(2026), period.LastDay);
        period.Add(slip);
        return (period, slip);
    }

    // ---- W1 ------------------------------------------------------------------------------------

    [Fact]
    public async Task W1_Closing_after_a_withdrawn_exit_pays_no_indemnity_and_takes_no_leave_off()
    {
        var e = Technician(new DateOnly(2024, 1, 2));
        e.RecordExit(new DateOnly(2026, 11, 30), ExitReason.Resignation, null);
        var settlement = new FinalSettlement(e.Id, Site, e.ExitDate!.Value, ExitReason.Resignation);
        settlement.Update(new SettlementAmounts(6000m, 12m, 2769.23m, false, 0m, 0m, null));
        var settlements = new InMemoryRepository<FinalSettlement>();
        settlements.Items.Add(settlement);
        var ledger = new InMemoryRepository<LeaveLedgerEntry>();
        // The settlement was saved: November's draft carries its indemnity.
        var (november, slip) = MonthWithIndemnity(e, 11, 2769.23m);

        (await Personnel(e, settlements, ledger).CancelExitAsync(e.Id, "Démission retirée")).IsSuccess.Should().BeTrue();
        // November closed without "Reprendre les dossiers".
        var payroll = Payroll(new FakePayrollQueries(), [november], [e], [new PayrollParameters(2026)], settlements, ledger);
        (await payroll.CloseAsync(november.Id)).IsSuccess.Should().BeTrue();

        slip.Variables.LeaveIndemnity.Should().Be(0m, "the voided settlement pays nothing");
        ledger.Items.Should().BeEmpty("no leave was paid, none is taken off");
    }

    [Fact]
    public async Task W1_A_month_prorated_to_an_exit_moved_since_is_not_closed_before_it_is_refreshed()
    {
        var e = Technician(new DateOnly(2024, 1, 2));
        e.RecordExit(new DateOnly(2026, 11, 15), ExitReason.Resignation, null);
        // Prepared with the exit on 15/11: 13 of 25 working days.
        var (november, slip) = MonthWithIndemnity(e, 11, 0m, 25m, 13m);
        (await Personnel(e, new InMemoryRepository<FinalSettlement>(), new InMemoryRepository<LeaveLedgerEntry>())
            .CorrectExitAsync(e.Id, new DateOnly(2026, 12, 31), ExitReason.Resignation, null)).IsSuccess.Should().BeTrue();

        // The files now give a full November.
        var payroll = Payroll(new FakePayrollQueries { Candidates = [new PayrollCandidate(e.Id, 0m)] }, [november], [e],
                              [new PayrollParameters(2026)]);
        var refused = await payroll.CloseAsync(november.Id);

        refused.FirstError.ResourceKey.Should().Be("Payroll_DepartureChanged");
        november.IsClosed.Should().BeFalse();

        (await payroll.RefreshAsync(november.Id)).IsSuccess.Should().BeTrue();
        (await payroll.CloseAsync(november.Id)).IsSuccess.Should().BeTrue();
        slip.Variables.ContractWorkingDays.Should().Be(0m);
        slip.Amounts.Gross.Should().BeGreaterThanOrEqualTo(6000m, "a full month is paid");
    }

    // ---- W2 ------------------------------------------------------------------------------------

    [Fact]
    public async Task W2_The_month_of_the_departure_keeps_the_previous_contracts_seniority_and_salary()
    {
        var e = Technician(new DateOnly(2019, 3, 1));
        e.RecordExit(new DateOnly(2026, 9, 30), ExitReason.EndOfFixedTerm, null);
        e.Rehire(new DateOnly(2026, 10, 1)).IsSuccess.Should().BeTrue();
        e.BaseSalary = 7000m;                                  // the new contract
        e.PreviousBaseSalary.Should().Be(6000m);

        var september = new PayrollPeriod(Site, 2026, 9);
        var queries = new FakePayrollQueries { Candidates = [new PayrollCandidate(e.Id, 0m)] };
        (await Payroll(queries, [september], [e], [new PayrollParameters(2026)]).RefreshAsync(september.Id))
            .IsSuccess.Should().BeTrue();

        var slip = september.Payslips.Single();
        slip.HireDate.Should().Be(new DateOnly(2019, 3, 1));
        slip.BaseSalary.Should().Be(6000m);
        slip.Amounts.SeniorityRate.Should().Be(10m);
        slip.Amounts.SeniorityBonus.Should().Be(600m);

        // October is the new contract's.
        var (hire, salary) = PayrollService.ContractOfMonth(e, new PayrollPeriod(Site, 2026, 10), null);
        hire.Should().Be(new DateOnly(2026, 10, 1));
        salary.Should().Be(7000m);
    }

    [Fact]
    public void W2_A_rehire_recorded_before_the_salary_was_kept_keeps_the_salary_already_on_the_draft()
    {
        var e = Technician(new DateOnly(2019, 3, 1), 7000m);
        e.RecordExit(new DateOnly(2026, 9, 30), ExitReason.EndOfFixedTerm, null);
        e.Rehire(new DateOnly(2026, 10, 1));
        typeof(Employee).GetProperty(nameof(Employee.PreviousBaseSalary))!.SetValue(e, null);

        PayrollService.ContractOfMonth(e, new PayrollPeriod(Site, 2026, 9), 6000m).BaseSalary.Should().Be(6000m);
    }

    // ---- W3 ------------------------------------------------------------------------------------

    [Fact]
    public async Task W3_A_rate_typed_wrong_can_be_corrected_by_reopening_with_the_current_rates()
    {
        var wrong = new PayrollParameters(2026) { AmoEmployeeRate = 2.62m };
        var e = Technician(new DateOnly(2025, 6, 2));
        var (january, slip) = MonthWithIndemnity(e, 1, 0m);
        slip.HireDate = new DateOnly(2025, 6, 2);
        var service = Payroll(new FakePayrollQueries(), [january], [e], [wrong]);
        (await service.CloseAsync(january.Id)).IsSuccess.Should().BeTrue();
        slip.Amounts.AmoEmployee.Should().Be(157.20m);

        (await service.SaveParametersAsync(2026, p => p.AmoEmployeeRate = 2.26m)).IsSuccess.Should().BeTrue();

        // By default the month keeps the rates of its closing.
        (await service.ReopenAsync(january.Id, "Vérification")).IsSuccess.Should().BeTrue();
        (await service.CloseAsync(january.Id)).IsSuccess.Should().BeTrue();
        slip.Amounts.AmoEmployee.Should().Be(157.20m);

        // Asked for, the year's corrected rates are taken, and kept by the next closing.
        (await service.ReopenAsync(january.Id, "Taux AMO mal saisi", applyCurrentRates: true)).IsSuccess.Should().BeTrue();
        slip.Amounts.AmoEmployee.Should().Be(135.60m);
        (await service.CloseAsync(january.Id)).IsSuccess.Should().BeTrue();
        slip.Amounts.AmoEmployee.Should().Be(135.60m);
        (await service.GetDocumentsAsync(january.Id, null)).Value.Should().OnlyContain(d => d.Parameters.AmoEmployeeRate == 2.26m);
    }

    // ---- W4 ------------------------------------------------------------------------------------

    [Fact]
    public async Task W4_An_automatic_order_carries_the_suppliers_usual_discount()
    {
        var supplier = Guid.NewGuid();
        var candidate = new ReorderCandidate(Guid.NewGuid(), "REA-01", "boîte", supplier, true, 1m, 2m, 10m, null,
                                             100m, 0.10m);
        var added = new List<PurchaseOrder>();
        var planner = new ReorderPlanner(
            ReviewFixesBilling2Tests.Stub<IRepository<PurchaseOrder>>.With(
                ("AddAsync", args => { added.Add((PurchaseOrder)args![0]!); return Task.CompletedTask; })),
            ReviewFixesBilling2Tests.Stub<IPurchaseOrderQueries>.With(
                ("GetReorderCandidatesAsync", _ => Task.FromResult<IReadOnlyList<ReorderCandidate>>([candidate]))),
            ReviewFixesBilling2Tests.Stub<ISequenceAllocator>.With(
                ("NextAsync", _ => Task.FromResult(Result.Success("BC/2026/000001")))),
            ReviewFixesBilling2Tests.Stub<IUnitOfWork>.With(
                ("BeginTransactionAsync", _ => Task.FromResult<IAsyncDisposable>(new Nothing()))),
            ReviewFixesBilling2Tests.Stub<ICurrentUser>.With(("get_SiteId", _ => Site), ("get_UserId", _ => Guid.NewGuid())),
            new FixedClock());

        (await planner.RaiseDraftsAsync()).DraftedItems.Should().Equal("REA-01");

        var line = added.Single().Lines.Single();
        line.UnitPriceMad.Should().Be(100m);
        line.DiscountRate.Should().Be(0.10m);
        line.NetUnitPriceMad.Should().Be(90m);
    }

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- W5 ------------------------------------------------------------------------------------

    [Fact]
    public void W5_A_card_refunded_in_the_shift_leaves_the_card_line_as_the_terminal_batch()
    {
        var session = new CashSession(Site, Guid.NewGuid(), Now, 100m);
        session.Accumulate(PaymentMethod.Card, 900m);
        session.Accumulate(PaymentMethod.Cheque, 300m);

        session.ReverseNonCash(PaymentMethod.Card, 900m);
        session.ReverseNonCash(PaymentMethod.Cash, 50m);      // cash goes through RecordCashRefund

        session.CardReceipts.Should().Be(0m);
        session.ChequeReceipts.Should().Be(300m);
        session.ExpectedClosingCash.Should().Be(100m);
    }

    [Fact]
    public async Task W5_Refunds_and_bounces_come_off_the_non_cash_lines_of_the_open_shift()
    {
        var invoice = new Invoice(string.Empty, Site, InvoiceKind.Invoice, new DateOnly(2026, 10, 15), Guid.NewGuid())
        {
            BilledToName = "ALAOUI Fatima"
        };
        invoice.AddLine(new InvoiceLine("Analyse", 2000m)).IsSuccess.Should().BeTrue();
        invoice.Issue("FAC/CASA/2026/000900", Now, null).IsSuccess.Should().BeTrue();
        var session = new CashSession(Site, Guid.NewGuid(), Now, 100m);

        Task<Invoice?> FindInvoice(object?[]? _) => Task.FromResult<Invoice?>(invoice);
        var service = new BillingService(
            ReviewFixesBilling2Tests.Stub<IRepository<Invoice>>.With(("GetByIdWithDetailsAsync", FindInvoice),
                                                                     ("GetByIdAsync", FindInvoice)),
            ReviewFixesBilling2Tests.Stub<IRepository<MarocLIS.Domain.Orders.LabOrder>>.Create(),
            ReviewFixesBilling2Tests.Stub<IRepository<MarocLIS.Domain.Patients.Patient>>.Create(),
            ReviewFixesBilling2Tests.Stub<IRepository<MarocLIS.Domain.Patients.Payer>>.Create(),
            ReviewFixesBilling2Tests.Stub<IRepository<LabSite>>.Create(),
            ReviewFixesBilling2Tests.Stub<IRepository<AppUser>>.Create(),
            ReviewFixesBilling2Tests.Stub<IRepository<CashSession>>.With(
                ("GetByIdAsync", args => Task.FromResult<CashSession?>((Guid)args![0]! == session.Id ? session : null))),
            ReviewFixesBilling2Tests.Stub<IRepository<Deposit>>.Create(),
            ReviewFixesBilling2Tests.Stub<IBillingQueries>.With(
                ("GetOpenCashSessionAsync", _ => Task.FromResult<CashSession?>(session))),
            ReviewFixesBilling2Tests.Stub<ISequenceAllocator>.Create(),
            ReviewFixesBilling2Tests.Stub<IUnitOfWork>.Create(),
            ReviewFixesBilling2Tests.Stub<IPermissionGuard>.Create(),
            ReviewFixesBilling2Tests.Stub<ICurrentUser>.With(("get_UserId", _ => Guid.NewGuid()), ("get_SiteId", _ => Site)),
            ReviewFixesBilling2Tests.Stub<IClock>.With(("get_Now", _ => Now), ("get_Today", _ => new DateOnly(2026, 10, 15))));

        (await service.RecordPaymentAsync(new RecordPaymentRequest(invoice.Id, 900m, PaymentMethod.Card, "AUT-1",
                                                                   null, null, null))).IsSuccess.Should().BeTrue();
        (await service.RecordPaymentAsync(new RecordPaymentRequest(invoice.Id, 400m, PaymentMethod.Cheque, "CHQ-1",
                                                                   null, null, null))).IsSuccess.Should().BeTrue();
        session.CardReceipts.Should().Be(900m);
        session.ChequeReceipts.Should().Be(400m);

        var card = invoice.Payments.Single(p => p.Method == PaymentMethod.Card);
        var cheque = invoice.Payments.Single(p => p.Method == PaymentMethod.Cheque);
        (await service.RefundPaymentAsync(invoice.Id, card.Id, "Mauvais dossier patient")).IsSuccess.Should().BeTrue();
        (await service.MarkPaymentBouncedAsync(invoice.Id, cheque.Id, "Provision insuffisante")).IsSuccess.Should().BeTrue();

        session.CardReceipts.Should().Be(0m);
        session.ChequeReceipts.Should().Be(0m);
        session.ExpectedClosingCash.Should().Be(100m);
    }
}

/// <summary>The ReviewFixes2 leave backfill against SQL Server (W6).</summary>
public sealed class ReviewFixesRound3RestDatabaseTests : IDisposable
{
    private readonly string _connection;
    private readonly bool _created;

    public ReviewFixesRound3RestDatabaseTests()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) { _connection = ""; return; }

        var builder = new SqlConnectionStringBuilder(configured);
        builder.InitialCatalog = (string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "MarocLIS_Test" : builder.InitialCatalog)
                                 + "_R3" + Guid.NewGuid().ToString("N")[..6];
        _connection = builder.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;
    }

    private LisDbContext Context() =>
        new(new DbContextOptionsBuilder<LisDbContext>().UseSqlServer(_connection).Options);

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = Context();
        context.Database.EnsureDeleted();
    }

    [SqlServerFact]
    public async Task W6_The_backfill_never_leaves_a_rehired_employee_below_zero()
    {
        var site = new LabSite("W" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), new MultilingualText("Site"),
                               LabSiteKind.MainLaboratory);
        var exit = new DateOnly(2026, 9, 30);

        // Rehired on 01/11: 9 days on the account, 10.5 paid (with the September accrual never credited).
        var rehired = Employee("W1", site.Id);
        rehired.RecordExit(exit, ExitReason.EndOfFixedTerm, null);
        rehired.Rehire(new DateOnly(2026, 11, 2));
        // Still out: debited what the account held; a signature later brings it to the days paid.
        var stillOut = Employee("W2", site.Id);
        stillOut.RecordExit(exit, ExitReason.EndOfFixedTerm, null);
        // Settlement saved after the closing: the payslip did not pay it, nothing is debited.
        var savedAfter = Employee("W3", site.Id);
        savedAfter.RecordExit(exit, ExitReason.EndOfFixedTerm, null);

        var september = new PayrollPeriod(site.Id, 2026, 9);
        foreach (var (e, indemnity) in new[] { (rehired, 2423.08m), (stillOut, 2423.08m), (savedAfter, 0m) })
        {
            var slip = new Payslip(e.Id) { Number = e.Number, FullName = e.FullName, BaseSalary = 6000m, HireDate = e.HireDate };
            slip.Recompute(new PayslipVariables { LeaveIndemnity = indemnity }, new PayrollParameters(2026), september.LastDay);
            september.Add(slip);
        }
        september.Close(Now(), Guid.NewGuid(), new PayrollParameters(2026));

        await using (var db = Context())
        {
            db.Sites.Add(site);
            db.Employees.AddRange(rehired, stillOut, savedAfter);
            db.PayrollPeriods.Add(september);
            foreach (var e in new[] { rehired, stillOut, savedAfter })
            {
                db.LeaveLedger.Add(new LeaveLedgerEntry(e.Id, new DateOnly(2026, 8, 31), LeaveLedgerKind.Accrual, 9m, "ACQ-2026-08"));
                var settlement = new FinalSettlement(e.Id, site.Id, exit, ExitReason.EndOfFixedTerm);
                settlement.Update(new SettlementAmounts(6000m, 10.5m, 2423.08m, false, 0m, 0m, null));
                db.FinalSettlements.Add(settlement);
            }
            // Leave taken in the new contract is not the old one's.
            db.LeaveLedger.Add(new LeaveLedgerEntry(rehired.Id, new DateOnly(2026, 11, 30), LeaveLedgerKind.Accrual, 1.5m, "ACQ-2026-11"));
            await db.SaveChangesAsync();
        }

        var backfill = new MarocLIS.Infrastructure.Persistence.Migrations.ReviewFixes2().UpOperations.OfType<SqlOperation>().Single(o => o.Sql.Contains("SOLDE-"));
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlRawAsync(backfill.Sql);
            await db.Database.ExecuteSqlRawAsync(backfill.Sql);      // rerunnable
        }

        await using (var db = Context())
        {
            async Task<decimal> Balance(Guid id) => await db.LeaveLedger.Where(l => l.EmployeeId == id).SumAsync(l => l.Days);
            (await Balance(rehired.Id)).Should().Be(1.5m, "the old contract ends at zero, the new one starts from it");
            (await Balance(stillOut.Id)).Should().Be(0m);
            (await Balance(savedAfter.Id)).Should().Be(9m);
            (await db.LeaveLedger.CountAsync(l => l.Reference == "SOLDE-20260930")).Should().Be(2);
        }
    }

    private static Employee Employee(string number, Guid siteId) =>
        new(number + Guid.NewGuid().ToString("N")[..4], "A", "B", EmployeeJob.Technician, new DateOnly(2024, 1, 2), siteId)
            { BaseSalary = 6000m };

    private static DateTimeOffset Now() => new(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(1));
}
