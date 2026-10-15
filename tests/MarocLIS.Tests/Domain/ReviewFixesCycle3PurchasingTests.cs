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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Fifth review, purchasing and stock: a second delivery of the same manufacturer lot joins the lot
/// (P1), the loi 69-21 declaration deducts only what a recorded declaration listed (P2), a paid
/// invoice's due date follows an earlier invoice entered late (P4). P3 and U2 are view changes
/// (the sheet time goes with the first figure of a line only; the picker sits on a second line).
/// </summary>
public sealed class ReviewFixesCycle3PurchasingTests(ReviewFixesCycle3PurchasingDatabase database)
    : IClassFixture<ReviewFixesCycle3PurchasingDatabase>
{
    private static readonly DateOnly Expiry = new(2027, 6, 30);

    // ---- P1: the lot received again ------------------------------------------------------------------

    [Fact]
    public void P1_a_further_delivery_adds_to_the_lot_and_keeps_its_release()
    {
        var lot = new ReagentLot("L123", Expiry, 5m, new DateOnly(2026, 1, 5)) { UnitPriceMad = 100m };
        lot.Release(new DateOnly(2026, 1, 6), Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();
        lot.Consume(1m, new DateOnly(2026, 1, 7)).IsSuccess.Should().BeTrue();

        lot.ReceiveMore(5m, Expiry, 110m).IsSuccess.Should().BeTrue();

        lot.QuantityReceived.Should().Be(10m);
        lot.QuantityRemaining.Should().Be(9m);
        lot.IsQuarantined.Should().BeFalse();
        lot.ReleasedOn.Should().Be(new DateOnly(2026, 1, 6));
        // 4 on the shelf at 100, 5 more at 110.
        lot.UnitPriceMad.Should().Be(Money.Round((4m * 100m + 5m * 110m) / 9m));

        // A return up to what was received in all.
        lot.Restore(1m).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void P1_a_rejected_or_recalled_lot_or_another_expiry_takes_no_more_stock()
    {
        var rejected = new ReagentLot("L1", Expiry, 5m, new DateOnly(2026, 1, 5));
        rejected.Reject("CQ non conforme").IsSuccess.Should().BeTrue();
        rejected.ReceiveMore(5m, Expiry, null).FirstError.ResourceKey.Should().Be("Lot_ReceiveIntoRefusedLot");

        var recalled = new ReagentLot("L2", Expiry, 5m, new DateOnly(2026, 1, 5));
        recalled.Recall(new DateOnly(2026, 2, 1), "FSN-12");
        recalled.ReceiveMore(5m, Expiry, null).FirstError.ResourceKey.Should().Be("Lot_ReceiveIntoRefusedLot");

        var other = new ReagentLot("L3", Expiry, 5m, new DateOnly(2026, 1, 5));
        other.ReceiveMore(5m, Expiry.AddMonths(1), null).FirstError.ResourceKey.Should().Be("Lot_ReceiveExpiryDiffers");
        other.QuantityReceived.Should().Be(5m);
    }

    [SqlServerFact]
    public async Task P1_the_backorder_of_the_same_lot_is_received_on_its_own_order_and_date()
    {
        var site = Guid.NewGuid();
        var clock = new Clock { Now = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.FromHours(1)) };
        var supplier = new Supplier("F" + Guid.NewGuid().ToString("N")[..8], "Biomed Maroc") { IsApproved = true };
        var item = new StockItem("GLU" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Glucose"),
                                 StockItemKind.Reagent, "coffret", site);
        var first = Order(supplier, item, site, 10m);
        var second = Order(supplier, item, site, 3m);

        await using (var seed = database.Context())
        {
            seed.Suppliers.Add(supplier);
            seed.StockItems.Add(item);
            seed.PurchaseOrders.AddRange(first, second);
            await seed.SaveChangesAsync();
        }

        async Task<Result> ReceiveAsync(PurchaseOrder order, decimal quantity, string lot, DateOnly expiry)
        {
            await using var context = database.Context();
            return await PurchaseOrders(context, site, clock).ReceiveLineAsync(order.Id, new ReceivePurchaseOrderLineRequest
            {
                LineId = order.Lines.Single().Id, Quantity = quantity, LotNumber = lot, ExpiryDate = expiry,
                DeliveryNoteReference = "BL-" + clock.Today.Month
            });
        }

        (await ReceiveAsync(first, 5m, "L123", Expiry)).IsSuccess.Should().BeTrue();
        await using (var release = database.Context())
        {
            var lot = await release.ReagentLots.SingleAsync(l => l.StockItemId == item.Id);
            lot.Release(clock.Today, Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();
            await release.SaveChangesAsync();
        }

        // 10 February: the other 5 boxes, same lot; then another order, same lot again.
        clock.Now = new DateTimeOffset(2026, 2, 10, 10, 0, 0, TimeSpan.FromHours(1));
        (await ReceiveAsync(first, 5m, "l123", Expiry.AddDays(1))).FirstError.ResourceKey.Should().Be("Lot_ReceiveExpiryDiffers");
        (await ReceiveAsync(first, 5m, "l123", Expiry)).IsSuccess.Should().BeTrue();
        clock.Now = new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.FromHours(1));
        (await ReceiveAsync(second, 3m, "L123", Expiry)).IsSuccess.Should().BeTrue();

        await using (var read = database.Context())
        {
            var lot = await read.ReagentLots.AsNoTracking().SingleAsync(l => l.StockItemId == item.Id);
            lot.QuantityReceived.Should().Be(13m);
            lot.QuantityRemaining.Should().Be(13m);
            lot.IsQuarantined.Should().BeFalse();
            lot.PurchaseOrderId.Should().Be(first.Id);

            var receipts = await read.StockMovements.IgnoreQueryFilters().AsNoTracking()
                                     .Where(m => m.ReagentLotId == lot.Id).OrderBy(m => m.OccurredAt).ToListAsync();
            receipts.Select(m => (m.Kind, m.Quantity, m.PurchaseOrderId, m.Reference)).Should().Equal(
                (StockMovementKind.Receipt, 5m, first.Id, "BL-1"), (StockMovementKind.Receipt, 5m, first.Id, "BL-2"),
                (StockMovementKind.Receipt, 3m, second.Id, "BL-3"));

            (await read.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == first.Lines.Single().Id))
                .QuantityReceived.Should().Be(10m);

            // Loi 69-21: each order line's dated receipts are its own deliveries.
            var sources = await new SupplierInvoiceQueries(read).GetOrderLineSourcesAsync(
                [first.Lines.Single().Id, second.Lines.Single().Id], null);
            sources.Single(s => s.PurchaseOrderLineId == first.Lines.Single().Id).Receipts.Should().Equal(
                new DatedQuantity(new DateOnly(2026, 1, 5), 5m), new DatedQuantity(new DateOnly(2026, 2, 10), 5m));
            sources.Single(s => s.PurchaseOrderLineId == second.Lines.Single().Id).Receipts.Should().Equal(
                new DatedQuantity(new DateOnly(2026, 3, 2), 3m));

            // The supplier evaluation counts three deliveries.
            (await new PurchasingQualityQueries(read).GetSupplierEvaluationInputsAsync(2026))
                .Single(s => s.SupplierId == supplier.Id).Deliveries.Select(d => d.ReceivedOn).Should()
                .BeEquivalentTo([new DateOnly(2026, 1, 5), new DateOnly(2026, 2, 10), new DateOnly(2026, 3, 2)]);
        }

        // The lot recalled: the stock screen's reception refuses it too.
        await using (var recall = database.Context())
        {
            (await recall.ReagentLots.SingleAsync(l => l.StockItemId == item.Id)).Recall(clock.Today, "FSN-1");
            await recall.SaveChangesAsync();
        }

        await using (var context = database.Context())
        {
            var refused = await Inventory(context, site, clock).ReceiveLotAsync(new ReceiveLotRequest
            {
                StockItemId = item.Id, LotNumber = "L123", ExpiryDate = Expiry, Quantity = 2m
            });
            refused.FirstError.ResourceKey.Should().Be("Lot_ReceiveIntoRefusedLot");
        }
    }

    [SqlServerFact]
    public async Task P1_the_stock_screen_adds_to_a_lot_awaiting_release()
    {
        var site = Guid.NewGuid();
        var clock = new Clock { Now = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.FromHours(1)) };
        var item = new StockItem("CRP" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("CRP"),
                                 StockItemKind.Reagent, "coffret", site);
        await using (var seed = database.Context())
        {
            seed.StockItems.Add(item);
            await seed.SaveChangesAsync();
        }

        Guid firstId, secondId;
        await using (var context = database.Context())
            firstId = (await Inventory(context, site, clock).ReceiveLotAsync(new ReceiveLotRequest
            {
                StockItemId = item.Id, LotNumber = "C9", ExpiryDate = Expiry, Quantity = 2m
            })).Value;
        await using (var context = database.Context())
            secondId = (await Inventory(context, site, clock).ReceiveLotAsync(new ReceiveLotRequest
            {
                StockItemId = item.Id, LotNumber = "C9 ", ExpiryDate = Expiry, Quantity = 3m
            })).Value;

        secondId.Should().Be(firstId);
        await using var read = database.Context();
        var lot = await read.ReagentLots.AsNoTracking().SingleAsync(l => l.StockItemId == item.Id);
        (lot.QuantityReceived, lot.QuantityRemaining, lot.IsAwaitingRelease).Should().Be((5m, 5m, true));
    }

    // ---- P2: what an earlier declaration listed ---------------------------------------------------------

    [Fact]
    public void P2_the_fine_already_declared_is_deducted_only_for_an_invoice_a_declaration_listed()
    {
        var settings = new PurchasingSettings();
        // Delivered 15 January, 60 days: due 16 March; paid 30 April, 45 days late.
        var source = new LatePaymentSource("Biomed", "ICE", "IF", "FA-2", new DateOnly(2026, 4, 2), new DateOnly(2026, 1, 15),
                                           new DateOnly(2026, 3, 16), 10_000m,
                                           [new PaymentFact(new DateOnly(2026, 4, 30), 10_000m)]);
        var q2From = new DateOnly(2026, 4, 1);
        var q2To = new DateOnly(2026, 6, 30);

        // Never listed (dated or entered after the Q1 declaration): its whole delay is declared now.
        var full = SupplierInvoiceService.ComputeLatePayments([source], q2From, q2To, settings).Single();
        full.DaysLate.Should().Be(45);
        full.DaysDeclaredBefore.Should().Be(0);
        full.Fine.Should().Be(310m);

        // Listed in Q1: those 15 days were declared then.
        var listed = SupplierInvoiceService.ComputeLatePayments(
            [source with { DeclaredThrough = new DateOnly(2026, 3, 31) }], q2From, q2To, settings).Single();
        listed.DaysDeclaredBefore.Should().Be(15);
        listed.Fine.Should().Be(85m);

        var declaration = new LatePaymentDeclaration(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31),
                                                     new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero), Guid.NewGuid());
        declaration.Listed(new DateOnly(2026, 3, 1), new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero)).Should().BeTrue();
        declaration.Listed(new DateOnly(2026, 4, 2), new DateTimeOffset(2026, 4, 3, 9, 0, 0, TimeSpan.Zero)).Should().BeFalse();
        declaration.Listed(new DateOnly(2026, 3, 25), new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }

    [SqlServerFact]
    public async Task P2_a_declared_period_exports_the_same_list_and_late_invoices_go_whole_into_the_next()
    {
        var site = Guid.NewGuid();
        var supplier = new Supplier("F" + Guid.NewGuid().ToString("N")[..8], "Biomed Maroc") { IsApproved = true };
        var due = new DateOnly(2026, 3, 16);
        var paid = new DateOnly(2026, 4, 30);

        // B: dated 1 March, entered 10 March, listed in Q1. A: dated 2 April. C: dated 25 March but
        // entered 22 April, after Q1 was exported on 20 April.
        var listed = Invoice(supplier, site, "FA-B", new DateOnly(2026, 3, 1), due, At(2026, 3, 10), paid);
        var dated = Invoice(supplier, site, "FA-A", new DateOnly(2026, 4, 2), due, At(2026, 4, 22), paid);
        var late = Invoice(supplier, site, "FA-C", new DateOnly(2026, 3, 25), due, At(2026, 4, 22), paid);

        await using (var seed = database.Context())
        {
            seed.Suppliers.Add(supplier);
            seed.SupplierInvoices.AddRange(listed, dated, late);
            seed.LatePaymentDeclarations.Add(new LatePaymentDeclaration(site, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31),
                                                                        At(2026, 4, 20), Guid.NewGuid()));
            await seed.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new SupplierInvoiceQueries(read);

        // Q1 again: what was filed, B alone, unpaid at the end of March.
        var q1 = await queries.GetLatePaymentSourcesAsync(site, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));
        q1.Select(s => s.InvoiceNumber).Should().Equal("FA-B");
        q1.Single().Payments.Should().BeEmpty();

        // Q2: B with the 15 days declared in Q1 deducted; A and C with their whole delay.
        var q2 = SupplierInvoiceService.ComputeLatePayments(
            await queries.GetLatePaymentSourcesAsync(site, new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), new PurchasingSettings());
        q2.Select(r => (r.InvoiceNumber, r.DaysDeclaredBefore)).Should().BeEquivalentTo(
            [("FA-A", 0), ("FA-B", 15), ("FA-C", 0)]);
    }

    [SqlServerFact]
    public async Task P2_exporting_an_ended_period_records_it_once()
    {
        using var provider = Services(new Clock { Now = new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.FromHours(1)) });

        for (var i = 0; i < 2; i++)
        {
            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>();
            (await service.ExportLatePaymentsAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31))).IsSuccess.Should().BeTrue();
            (await service.ExportLatePaymentsAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30))).IsSuccess.Should().BeTrue();
        }

        await using var read = database.Context();
        (await read.LatePaymentDeclarations.IgnoreQueryFilters().Where(d => d.SiteId == Operator.Site).ToListAsync())
            .Select(d => (d.PeriodFrom, d.PeriodTo))
            .Should().Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)));
    }

    // ---- P4: a paid invoice's delivery follows an earlier invoice entered late -----------------------------

    [SqlServerFact]
    public async Task P4_entering_an_earlier_invoice_late_moves_a_paid_invoices_due_date()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var january = today.AddDays(-100);
        var february = today.AddDays(-70);

        var supplier = new Supplier("F" + Guid.NewGuid().ToString("N")[..8], "Biomed Maroc") { IsApproved = true, PaymentTermsDays = 60 };
        var item = new StockItem("GLU" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Glucose"),
                                 StockItemKind.Reagent, "coffret", Operator.Site);
        var order = Order(supplier, item, Operator.Site, 10m, today.AddDays(-120));
        var line = order.Lines.Single();
        order.ReceiveLine(line.Id, 10m).IsSuccess.Should().BeTrue();
        line.LastReceivedOn = february;
        item.AddLot(new ReagentLot("L1", today.AddDays(300), 5m, january) { PurchaseOrderId = order.Id });
        item.AddLot(new ReagentLot("L2", today.AddDays(300), 5m, february) { PurchaseOrderId = order.Id });
        await using (var seed = database.Context())
        {
            seed.Suppliers.Add(supplier);
            seed.StockItems.Add(item);
            seed.PurchaseOrders.Add(order);
            await seed.SaveChangesAsync();
        }

        CreateSupplierInvoiceRequest Invoice(DateOnly on) => new()
        {
            SupplierId = supplier.Id, InvoiceNumber = "FA-" + Guid.NewGuid().ToString("N")[..8], InvoiceDate = on,
            ReceivedOn = on, Lines = [new SupplierInvoiceLineRequest(line.Id, 5m, 100m, 0.20m)]
        };

        using var provider = Services(null);

        // B, the February boxes, entered alone: billed against January; then paid in full.
        SupplierInvoiceOutcome later;
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>();
            later = (await service.CreateAsync(Invoice(february.AddDays(2)))).Value;
            later.DueDate.Should().Be(january.AddDays(60));
            (await service.RecordPaymentAsync(later.InvoiceId, new RecordSupplierPaymentRequest(
                today.AddDays(-5), 600m, SupplierPaymentMethod.Transfer, "VIR-1"))).IsSuccess.Should().BeTrue();
        }

        // A, the January boxes, arrives late: B, though paid, now bills February.
        using (var scope = provider.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>()
                        .CreateAsync(Invoice(january.AddDays(10)))).Value.DueDate.Should().Be(january.AddDays(60));

        await using var read = database.Context();
        var stored = await read.SupplierInvoices.IgnoreQueryFilters().AsNoTracking().Include(i => i.Payments)
                               .SingleAsync(i => i.Id == later.InvoiceId);
        (stored.DeliveryDate, stored.DueDate, stored.Status).Should().Be((february, february.AddDays(60), SupplierInvoiceStatus.Paid));
        stored.Payments.Should().ContainSingle().Which.AmountMad.Should().Be(600m);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private static PurchaseOrder Order(Supplier supplier, StockItem item, Guid site, decimal quantity, DateOnly? on = null)
    {
        var order = new PurchaseOrder("BC/" + Guid.NewGuid().ToString("N")[..8], supplier.Id, site,
                                      on ?? new DateOnly(2025, 12, 20), Guid.NewGuid());
        order.AddLine(new PurchaseOrderLine(item.Id, quantity, 100m) { Description = item.Code });
        order.Approve(DateTimeOffset.Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        order.Send(DateTimeOffset.Now).IsSuccess.Should().BeTrue();
        return order;
    }

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 9, 0, 0, TimeSpan.Zero);

    private static SupplierInvoice Invoice(Supplier supplier, Guid site, string number, DateOnly on, DateOnly due,
                                           DateTimeOffset enteredAt, DateOnly paidOn)
    {
        var invoice = new SupplierInvoice(supplier.Id, site, number, on, on, Guid.NewGuid()) { CreatedAt = enteredAt };
        invoice.AddLine(new SupplierInvoiceLine(Guid.NewGuid(), Guid.NewGuid(), "GLU", 1m, 10_000m, 0m)).IsSuccess.Should().BeTrue();
        invoice.SetPaymentTerms(due.AddDays(-60), 60);
        invoice.ApplyMatch([]).IsSuccess.Should().BeTrue();
        invoice.RecordPayment(new SupplierPayment(paidOn, 10_000m, SupplierPaymentMethod.Transfer, "VIR",
                                                  new DateTimeOffset(paidOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                                                  Guid.NewGuid())).IsSuccess.Should().BeTrue();
        return invoice;
    }

    private ServiceProvider Services(IClock? clock)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddMarocLisInfrastructure(configuration);
        services.AddMarocLisApplication(configuration);
        services.AddSingleton<ICurrentUser, Operator>();
        if (clock is not null) services.AddSingleton(clock);
        services.AddIsolatedQueries<ISupplierInvoiceQueries, SupplierInvoiceQueries>();
        return services.BuildServiceProvider();
    }

    private static PurchaseOrderService PurchaseOrders(LisDbContext context, Guid site, Clock clock) =>
        new(new PurchaseOrderRepository(context, clock), new StockItemRepository(context, clock),
            new Repository<StockMovement>(context, clock), null!, null!, new UnitOfWork(context), new Guard(),
            new User(site), clock);

    private static InventoryService Inventory(LisDbContext context, Guid site, Clock clock) =>
        new(new StockItemRepository(context, clock), new Repository<StockMovement>(context, clock),
            new Repository<Supplier>(context, clock), null!, new UnitOfWork(context), new Guard(), new User(site), clock);

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

/// <summary>A scratch database for the cycle-3 purchasing fixes, created from the model and dropped.</summary>
public sealed class ReviewFixesCycle3PurchasingDatabase : IDisposable
{
    private readonly bool _created;

    public ReviewFixesCycle3PurchasingDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c3purch_" + Guid.NewGuid().ToString("N")[..8]
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
