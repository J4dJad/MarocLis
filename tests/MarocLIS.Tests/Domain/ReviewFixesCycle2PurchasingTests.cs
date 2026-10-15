using FluentAssertions;
using MarocLIS.Application;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Desktop.Composition;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Fourth review, purchasing and stock: a count keyed in from the paper sheet is measured against
/// the stock when the shelf was counted (P1), a comment or a correction keeps the line's reference
/// (P2), the loi 69-21 declaration does not declare the same days twice (P3), deliveries are billed
/// in invoice order whatever the entry order (P4), the grid shows the reference the variance is
/// taken against (P5), and counts open during the upgrade (U1).
/// </summary>
public sealed class ReviewFixesCycle2PurchasingTests(ReviewFixesCycle2PurchasingDatabase database)
    : IClassFixture<ReviewFixesCycle2PurchasingDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset At8 = new(2026, 10, 15, 8, 0, 0, TimeSpan.FromHours(1));

    // ---- P1 / P2: the line's reference ---------------------------------------------------------------

    private static (InventoryCount Count, InventoryCountLine Line) StartedCount(decimal expected = 10m)
    {
        var count = new InventoryCount("INV/2026/0020", Guid.NewGuid(), Today, InventoryCountScope.All, Guid.NewGuid());
        count.StartCounting([new InventoryCountLineInput(Guid.NewGuid(), Guid.NewGuid(), "GLU", "L1", Today.AddDays(90),
                                                         "Frigo 1", expected)], At8).IsSuccess.Should().BeTrue();
        return (count, count.Lines.Single());
    }

    [Fact]
    public void P1_a_sheet_keyed_in_later_keeps_the_time_the_shelf_was_counted()
    {
        var (count, line) = StartedCount();
        var user = Guid.NewGuid();

        // Shelf counted at 09:00 (10, as the stock then); keyed in at 15:00 with the stock at 09:00.
        count.RecordCount(line.Id, 10m, user, At8.AddHours(7), null, 10m, At8.AddHours(1)).IsSuccess.Should().BeTrue();

        line.CountedAt.Should().Be(At8.AddHours(1));
        line.StockAtCount.Should().Be(10m);
        line.Variance.Should().Be(0m);
    }

    [Fact]
    public void P1_a_shelf_time_before_the_start_or_after_the_entry_is_refused()
    {
        var (count, line) = StartedCount();
        var user = Guid.NewGuid();

        count.RecordCount(line.Id, 10m, user, At8.AddHours(7), null, 10m, At8.AddHours(-1))
             .FirstError.ResourceKey.Should().Be("InventoryCount_CountedBeforeStart");
        count.RecordCount(line.Id, 10m, user, At8.AddHours(7), null, 10m, At8.AddHours(8))
             .FirstError.ResourceKey.Should().Be("InventoryCount_CountedInFuture");

        // Written to the minute, a sheet time in the minute the count started is the start.
        count.ShelfTimeFor(line, At8.AddHours(7), At8.AddSeconds(-30)).Should().Be(At8);
        count.RecordCount(line.Id, 10m, user, At8.AddHours(7), null, 10m, At8.AddSeconds(-30)).IsSuccess.Should().BeTrue();
        line.CountedAt.Should().Be(At8);
    }

    [Fact]
    public void P2_a_comment_or_a_corrected_figure_keeps_the_reference_of_the_count()
    {
        var (count, line) = StartedCount();
        var user = Guid.NewGuid();

        // 09:00: 8 counted while the stock shows 8.
        count.RecordCount(line.Id, 8m, user, At8.AddHours(1), null, 8m).IsSuccess.Should().BeTrue();
        count.ShelfTimeFor(line, At8.AddHours(6), null).Should().Be(At8.AddHours(1));

        // 14:00, two boxes issued since: a comment is added. The reference stays 8.
        count.RecordCount(line.Id, 8m, user, At8.AddHours(6), "étagère du bas", 6m).IsSuccess.Should().BeTrue();
        line.StockAtCount.Should().Be(8m);
        line.Variance.Should().Be(0m);
        line.CountedAt.Should().Be(At8.AddHours(1));
        line.Comment.Should().Be("étagère du bas");

        // A typo corrected: still the 09:00 count.
        count.RecordCount(line.Id, 7m, user, At8.AddHours(6), "étagère du bas", 6m).IsSuccess.Should().BeTrue();
        line.StockAtCount.Should().Be(8m);
        line.Variance.Should().Be(-1m);

        // The shelf counted again at 14:00, on purpose: new reference.
        count.RecordCount(line.Id, 6m, user, At8.AddHours(6), null, 6m, At8.AddHours(6)).IsSuccess.Should().BeTrue();
        line.StockAtCount.Should().Be(6m);
        line.Variance.Should().Be(0m);
    }

    // ---- P3: the declaration over consecutive periods ----------------------------------------------

    [Fact]
    public void P3_an_amount_declared_unpaid_then_paid_later_adds_up_to_the_fine_for_the_whole_delay()
    {
        var settings = new PurchasingSettings();
        var source = new LatePaymentSource("Biomed", "ICE", "IF", "FA-9", new DateOnly(2026, 1, 10), null,
                                           new DateOnly(2026, 2, 20), 10_000m,
                                           [new PaymentFact(new DateOnly(2026, 5, 31), 10_000m)]);

        // Q1, as declared on 31 March: unpaid then, 39 days late (2 months begun).
        var q1Unpaid = SupplierInvoiceService.ComputeLatePayments(
            [source with { Payments = [] }], new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), settings).Single();
        q1Unpaid.DaysLate.Should().Be(39);
        q1Unpaid.Fine.Should().Be(310m);
        q1Unpaid.DaysDeclaredBefore.Should().Be(0);

        // Q2: paid 31 May, 100 days late in all, 39 of them already declared (Q1 listed it).
        var q2 = SupplierInvoiceService.ComputeLatePayments([source with { DeclaredThrough = new DateOnly(2026, 3, 31) }],
                                                            new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30),
                                                            settings).Single();
        q2.DaysLate.Should().Be(100);
        q2.DaysDeclaredBefore.Should().Be(39);
        q2.FineDeclaredBefore.Should().Be(310m);
        q2.Fine.Should().Be(170m);
        (q1Unpaid.Fine + q2.Fine).Should().Be(LatePayment.Fine(10_000m, 100, 2.25m, 0.85m)).And.Be(480m);

        // The penalty owed to the supplier likewise: the 39 days are charged once.
        (q1Unpaid.Penalty + q2.Penalty).Should().Be(LatePayment.Penalty(10_000m, settings.PenaltyRatePercent, 100));

        // A payment late within its own period is unaffected.
        var once = SupplierInvoiceService.ComputeLatePayments([source], new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30),
                                                              settings).Single();
        once.Fine.Should().Be(480m);
        once.FineDeclaredBefore.Should().Be(0m);

        var csv = System.Text.Encoding.UTF8.GetString(SupplierInvoiceService.LatePaymentsCsv([q2]));
        csv.Should().Contain("Amende déjà déclarée").And.Contain($";100;{q2.Penalty:0.00};170,00;39;310,00".Replace('.', ','));
    }

    // ---- P4: deliveries billed in invoice order ---------------------------------------------------

    [Fact]
    public void P4_an_invoice_of_the_same_date_entered_earlier_billed_the_first_delivery()
    {
        var entered = DateTimeOffset.Now;
        var source = new OrderLineMatchSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GLU", 10m, 5m, 90m, 0.20m,
                                              new DateOnly(2026, 2, 10),
                                              [new(new DateOnly(2026, 1, 5), 5m), new(new DateOnly(2026, 2, 10), 5m)],
                                              [new(new DateOnly(2026, 2, 12), 5m, entered)]);

        // Being entered now, after the one of the same date: the February delivery.
        source.DeliveryFor(new DateOnly(2026, 2, 12), 5m).Should().Be(new DateOnly(2026, 2, 10));

        // Matched again later, an invoice entered before that one keeps January.
        source.DeliveryFor(new DateOnly(2026, 2, 12), 5m, entered.AddMinutes(-5)).Should().Be(new DateOnly(2026, 1, 5));
    }

    // ---- P5: the reference column -----------------------------------------------------------------

    [SqlServerFact]
    public async Task P5_the_line_row_carries_the_reference_of_the_variance()
    {
        var siteId = Guid.NewGuid();
        var (count, line) = StartedCount();
        count.SiteId = siteId;
        count.RecordCount(line.Id, 7m, Guid.NewGuid(), At8.AddHours(2), null, 8m).IsSuccess.Should().BeTrue();
        await using (var setup = database.Context())
        {
            setup.InventoryCounts.Add(count);
            await setup.SaveChangesAsync();
        }

        await using var read = database.Context();
        var row = (await new InventoryCountQueries(read).GetDetailAsync(count.Id))!.Lines.Single();
        row.ExpectedQuantity.Should().Be(10m);
        row.StockAtCount.Should().Be(8m);
        row.CountedAt.Should().Be(At8.AddHours(2));
        (row.CountedQuantity - row.StockAtCount).Should().Be(row.Variance).And.Be(-1m);
    }

    // ---- SQL Server: P1 / P2 through the service ----------------------------------------------------

    [SqlServerFact]
    public async Task P1_P2_sheet_keyed_in_after_an_issue_posts_no_adjustment()
    {
        var siteId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var item = new StockItem("R" + Guid.NewGuid().ToString("N")[..8], new MultilingualText("Glucose"),
                                 StockItemKind.Reagent, "coffret", siteId);
        var lot = item.AddLot(new ReagentLot("L1", Today.AddDays(200), 10m, Today.AddDays(-5)) { StorageLocation = "Frigo 1" });
        lot.Release(Today.AddDays(-4), user, "CQ conforme").IsSuccess.Should().BeTrue();
        var count = new InventoryCount("INV/" + Guid.NewGuid().ToString("N")[..8], siteId, Today, InventoryCountScope.All, user);

        await using (var setup = database.Context())
        {
            setup.StockItems.Add(item);
            setup.InventoryCounts.Add(count);
            await setup.SaveChangesAsync();
        }

        var clock = new Clock { Now = At8 };
        await using (var context = database.Context())
            (await Service(context, siteId, clock).StartCountingAsync(count.Id)).Value.Should().Be(1);

        // 09:00 the shelf is counted on paper: 10. 10:00 two boxes are issued.
        await using (var bench = database.Context())
        {
            var tracked = await bench.ReagentLots.SingleAsync(l => l.Id == lot.Id);
            tracked.Consume(2m, Today).IsSuccess.Should().BeTrue();
            bench.StockMovements.Add(new StockMovement(item.Id, lot.Id, StockMovementKind.Issue, 2m, At8.AddHours(2), user,
                                                       siteId) { BalanceAfter = 8m });
            await bench.SaveChangesAsync();
        }

        // 15:00 the sheet is keyed in with its time.
        clock.Now = At8.AddHours(7);
        Guid lineId;
        await using (var context = database.Context())
        {
            var service = Service(context, siteId, clock);
            lineId = (await service.GetDetailAsync(count.Id)).Value.Lines.Single().LineId;
            (await service.RecordCountAsync(count.Id, lineId, 10m, null, At8.AddHours(-2)))
                .FirstError.ResourceKey.Should().Be("InventoryCount_CountedBeforeStart");
            (await service.RecordCountAsync(count.Id, lineId, 10m, null, At8.AddHours(1))).IsSuccess.Should().BeTrue();
        }

        // 16:00 a comment: the reference stays the stock at 09:00.
        clock.Now = At8.AddHours(8);
        await using (var context = database.Context())
            (await Service(context, siteId, clock).RecordCountAsync(count.Id, lineId, 10m, "étagère du bas")).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            var row = (await Service(context, siteId, clock).GetDetailAsync(count.Id)).Value.Lines.Single();
            row.StockAtCount.Should().Be(10m);
            row.Variance.Should().Be(0m);
            row.CountedAt.Should().Be(At8.AddHours(1));
            row.Comment.Should().Be("étagère du bas");

            (await Service(context, siteId, clock).CloseAsync(count.Id)).Value.Should().Be(0);
        }

        await using var read = database.Context();
        (await read.ReagentLots.AsNoTracking().SingleAsync(l => l.Id == lot.Id)).QuantityRemaining.Should().Be(8m);
    }

    // ---- SQL Server: U1, counts open during the upgrade ---------------------------------------------

    [SqlServerFact]
    public async Task U1_lines_counted_before_the_upgrade_take_the_stock_at_their_count()
    {
        var siteId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        InventoryCount Counted(string number, InventoryCountStatus status)
        {
            var count = new InventoryCount(number + Guid.NewGuid().ToString("N")[..6], siteId, Today,
                                           InventoryCountScope.All, user);
            count.StartCounting([new InventoryCountLineInput(itemId, lotId, "GLU", "L1", Today.AddDays(90), null, 10m)], At8)
                 .IsSuccess.Should().BeTrue();
            count.RecordCount(count.Lines.Single().Id, 8m, user, At8.AddHours(3), null).IsSuccess.Should().BeTrue();
            if (status == InventoryCountStatus.Closed) count.Close(user, At8.AddHours(4)).IsSuccess.Should().BeTrue();
            return count;
        }

        var open = Counted("INV/O", InventoryCountStatus.Counting);
        var closed = Counted("INV/C", InventoryCountStatus.Closed);

        await using (var setup = database.Context())
        {
            setup.InventoryCounts.AddRange(open, closed);
            // Two boxes issued before the count, one received before it, one issued after it.
            setup.StockMovements.AddRange(
                new StockMovement(itemId, lotId, StockMovementKind.Issue, 3m, At8.AddHours(1), user, siteId),
                new StockMovement(itemId, lotId, StockMovementKind.PositiveAdjustment, 1m, At8.AddHours(2), user, siteId),
                new StockMovement(itemId, lotId, StockMovementKind.Issue, 1m, At8.AddHours(5), user, siteId),
                new StockMovement(itemId, lotId, StockMovementKind.Issue, 4m, At8.AddHours(-1), user, siteId));
            await setup.SaveChangesAsync();

            // As they were before the upgrade: no reference kept.
            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE inv.InventoryCountLines SET StockAtCount = NULL WHERE InventoryCountId IN ({0}, {1})", open.Id, closed.Id);
        }

        var backfill = new MarocLIS.Infrastructure.Persistence.Migrations.RevueCycle1().UpOperations
            .OfType<SqlOperation>().Single(o => o.Sql.Contains("StockAtCount"));
        await using (var migrate = database.Context())
            await migrate.Database.ExecuteSqlRawAsync(backfill.Sql);

        await using var read = database.Context();
        var lines = await read.InventoryCounts.AsNoTracking().Where(c => c.Id == open.Id || c.Id == closed.Id)
                              .ToDictionaryAsync(c => c.Id, c => c.Lines.Single());
        lines[open.Id].StockAtCount.Should().Be(8m, "10 at the start, 3 issued and 1 put back before the count");
        lines[open.Id].Variance.Should().Be(0m);
        lines[closed.Id].StockAtCount.Should().BeNull("a closed count posted its variances already");
    }

    [SqlServerFact]
    public async Task U1_two_counts_open_on_the_same_lot_cannot_both_close()
    {
        var siteId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var lotId = Guid.NewGuid();

        InventoryCount Open()
        {
            var count = new InventoryCount("INV/" + Guid.NewGuid().ToString("N")[..8], siteId, Today, InventoryCountScope.All, user);
            count.StartCounting([new InventoryCountLineInput(Guid.NewGuid(), lotId, "GLU", "L1", Today.AddDays(90), null, 10m)],
                                At8).IsSuccess.Should().BeTrue();
            count.RecordCount(count.Lines.Single().Id, 10m, user, At8.AddHours(1), null, 10m).IsSuccess.Should().BeTrue();
            return count;
        }

        // Both opened before the start check existed.
        var first = Open();
        var second = Open();
        await using (var setup = database.Context())
        {
            setup.InventoryCounts.AddRange(first, second);
            await setup.SaveChangesAsync();
        }

        var clock = new Clock { Now = At8.AddHours(2) };
        await using var context = database.Context();
        var service = Service(context, siteId, clock);

        (await service.CloseAsync(first.Id)).FirstError.ResourceKey.Should().Be("InventoryCount_LotsInOtherOpenCount");
        (await service.CancelAsync(second.Id, "Doublon")).IsSuccess.Should().BeTrue();
        (await service.CloseAsync(first.Id)).IsSuccess.Should().BeTrue();
    }

    // ---- SQL Server: P4 through the invoice service -----------------------------------------------

    [SqlServerFact]
    public async Task P4_entering_or_cancelling_an_earlier_invoice_moves_the_later_ones_due_date()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var january = today.AddDays(-60);
        var february = today.AddDays(-30);

        async Task<(Supplier, Guid)> OrderAsync()
        {
            var supplier = new Supplier("F" + Guid.NewGuid().ToString("N")[..8], "Biomed Maroc")
            {
                IsApproved = true, PaymentTermsDays = 60
            };
            var item = new StockItem("GLU" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Glucose"),
                                     StockItemKind.Reagent, "coffret", Operator.Site);
            var order = new PurchaseOrder("BC/" + Guid.NewGuid().ToString("N")[..8], supplier.Id, Operator.Site,
                                          today.AddDays(-90), Guid.NewGuid());
            order.AddLine(new PurchaseOrderLine(item.Id, 10m, 100m) { Description = "GLU" });
            order.Approve(DateTimeOffset.Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
            order.Send(DateTimeOffset.Now).IsSuccess.Should().BeTrue();
            var line = order.Lines.Single();
            order.ReceiveLine(line.Id, 10m).IsSuccess.Should().BeTrue();
            line.LastReceivedOn = february;
            item.AddLot(new ReagentLot("L1", today.AddDays(300), 5m, january) { PurchaseOrderId = order.Id });
            item.AddLot(new ReagentLot("L2", today.AddDays(300), 5m, february) { PurchaseOrderId = order.Id });

            await using var context = database.Context();
            context.Suppliers.Add(supplier);
            context.StockItems.Add(item);
            context.PurchaseOrders.Add(order);
            await context.SaveChangesAsync();
            return (supplier, line.Id);
        }

        CreateSupplierInvoiceRequest Invoice(Supplier supplier, Guid line, DateOnly on) => new()
        {
            SupplierId = supplier.Id, InvoiceNumber = "FA-" + Guid.NewGuid().ToString("N")[..8], InvoiceDate = on,
            ReceivedOn = on, Lines = [new SupplierInvoiceLineRequest(line, 5m, 100m, 0.20m)]
        };

        using var provider = Services();

        // Two invoices of the same date, entered in delivery order: one delivery each.
        var (supplier, line) = await OrderAsync();
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>();
            (await service.CreateAsync(Invoice(supplier, line, today.AddDays(-20)))).Value.DueDate.Should().Be(january.AddDays(60));
        }
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>();
            (await service.CreateAsync(Invoice(supplier, line, today.AddDays(-20)))).Value.DueDate.Should().Be(february.AddDays(60));
        }

        // The later invoice entered first takes January; the earlier one, entered next, takes it back.
        (supplier, line) = await OrderAsync();
        SupplierInvoiceOutcome later, earlier;
        using (var scope = provider.CreateScope())
            later = (await scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>()
                                .CreateAsync(Invoice(supplier, line, today.AddDays(-10)))).Value;
        later.DueDate.Should().Be(january.AddDays(60));

        using (var scope = provider.CreateScope())
            earlier = (await scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>()
                                  .CreateAsync(Invoice(supplier, line, today.AddDays(-20)))).Value;
        earlier.DueDate.Should().Be(january.AddDays(60));
        (await StoredAsync(later.InvoiceId)).Should().Be((february, february.AddDays(60)));

        // The earlier one cancelled: the later one bills January again.
        using (var scope = provider.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>()
                        .CancelAsync(earlier.InvoiceId, "Saisie en double")).IsSuccess.Should().BeTrue();
        (await StoredAsync(later.InvoiceId)).Should().Be((january, january.AddDays(60)));
    }

    private async Task<(DateOnly?, DateOnly)> StoredAsync(Guid invoiceId)
    {
        await using var read = database.Context();
        var invoice = await read.SupplierInvoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        return (invoice.DeliveryDate, invoice.DueDate);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private ServiceProvider Services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser, Operator>();
        services.AddIsolatedQueries<ISupplierInvoiceQueries, SupplierInvoiceQueries>();
        return services.BuildServiceProvider();
    }

    private static InventoryCountService Service(LisDbContext context, Guid siteId, Clock clock) =>
        new(new Repository<InventoryCount>(context, clock), new Repository<StockMovement>(context, clock),
            new InventoryCountQueries(context), new InventoryQueries(context), null!, new UnitOfWork(context),
            new Guard(), new User(siteId), clock);

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; }
        public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
    }

    private sealed class Guard : IPermissionGuard
    {
        public Result Require(string permission) => Result.Success();
        public Result RequireAny(params string[] permissions) => Result.Success();
        public Result RequireAll(params string[] permissions) => Result.Success();
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private class User(Guid siteId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "user";
        public string FullName => "User";
        public Guid SiteId => siteId;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => default;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteId];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "ACHATS-1";
    }

    private sealed class Operator() : User(Site)
    {
        public static readonly Guid Site = Guid.NewGuid();
    }
}

/// <summary>A scratch database for the cycle-2 purchasing fixes, created from the model and dropped.</summary>
public sealed class ReviewFixesCycle2PurchasingDatabase : IDisposable
{
    private readonly bool _created;

    public ReviewFixesCycle2PurchasingDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c2purch_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
    }

    public string ConnectionString { get; } = string.Empty;

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
