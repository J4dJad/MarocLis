using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the purchasing scenario, created and dropped by the class.</summary>
public sealed class CycleC1cPurchasingDatabase : IDisposable
{
    private readonly bool _created;

    public CycleC1cPurchasingDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c1cachat_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
                                                 .UseSqlServer(ConnectionString).Options);
        context.Database.EnsureCreated();
        _created = true;
    }

    public string ConnectionString { get; } = string.Empty;

    public void Dispose()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        using var context = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
                                                 .UseSqlServer(ConnectionString).Options);
        context.Database.EnsureDeleted();
    }
}

/// <summary>
/// The purchasing chain end to end on SQL Server, through the services the screens call: a quote
/// request answered and awarded, the order approved by someone else and sent, received in two
/// deliveries on one screen (the second keyed first with the wrong expiry date), the supplier's
/// invoice blocked by its price, released by a second person with a justification, and paid.
/// </summary>
public sealed class ReviewFixesCycleC1cPurchasingDatabaseTests(CycleC1cPurchasingDatabase database)
    : IClassFixture<CycleC1cPurchasingDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 16);
    }

    private static readonly Guid Site = Guid.NewGuid();
    private static readonly FixedClock Clock = new();

    private static ICurrentUser User(Guid userId) => Stub<ICurrentUser>.Create(new()
    {
        ["get_IsAuthenticated"] = _ => true,
        ["get_UserId"] = _ => userId,
        ["get_UserName"] = _ => "achats",
        ["get_FullName"] = _ => "Achats",
        ["get_SiteId"] = _ => Site,
        ["get_SiteCode"] = _ => "PRINCIPAL",
        ["get_AccessibleSiteIds"] = _ => (IReadOnlyList<Guid>)[Site],
        ["Has"] = _ => true
    });

    private static IPermissionGuard Allowed => Stub<IPermissionGuard>.Create(new()
    {
        [nameof(IPermissionGuard.Require)] = _ => Result.Success(),
        [nameof(IPermissionGuard.RequireAny)] = _ => Result.Success(),
        [nameof(IPermissionGuard.RequireAll)] = _ => Result.Success()
    });

    private LisDbContext Context(ICurrentUser user) =>
        new(new DbContextOptionsBuilder<LisDbContext>()
                .UseSqlServer(database.ConnectionString)
                .AddInterceptors(new AuditSaveChangesInterceptor(user, Clock))
                .Options, user);

    private static QuoteService Quotes(LisDbContext c, ICurrentUser user) =>
        new(new Repository<QuoteRequest>(c, Clock), new PurchaseOrderRepository(c, Clock),
            new Repository<PurchasingDocument>(c, Clock), new QuoteQueries(c), new PurchaseOrderQueries(c),
            new SequenceAllocator(c, Clock, user), Stub<IMessageSender>.Create(), new UnitOfWork(c), Allowed, user, Clock);

    private static PurchaseOrderService Orders(LisDbContext c, ICurrentUser user) =>
        new(new PurchaseOrderRepository(c, Clock), new StockItemRepository(c, Clock),
            new Repository<StockMovement>(c, Clock), new PurchaseOrderQueries(c),
            new SequenceAllocator(c, Clock, user), new UnitOfWork(c), Allowed, user, Clock);

    private static SupplierInvoiceService Invoices(LisDbContext c, ICurrentUser user) =>
        new(new SupplierInvoiceRepository(c, Clock), new Repository<SupplierInvoiceFile>(c, Clock),
            new Repository<Supplier>(c, Clock), new Repository<LatePaymentDeclaration>(c, Clock),
            new SupplierInvoiceQueries(c), new ConfigurationWriter(c), new UnitOfWork(c), Allowed, user, Clock);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [SqlServerFact]
    public async Task FromQuoteToPayment_TheChainHoldsTogether()
    {
        var buyer = User(Guid.NewGuid());
        var manager = User(Guid.NewGuid());
        var clerk = User(Guid.NewGuid());

        var supplier = new Supplier(Unique("F"), "Bio-Distribution") { IsApproved = true, PaymentTermsDays = 60 };
        var item = new StockItem(Unique("GLU"), MultilingualText.FromFrench("Glucose HK"), StockItemKind.Reagent,
                                 "coffret", Site);
        await using (var seed = Context(buyer))
        {
            seed.Suppliers.Add(supplier);
            seed.StockItems.Add(item);
            await seed.SaveChangesAsync();
        }

        // ---- Quote request, offer, award -------------------------------------------------------
        Guid quoteId;
        await using (var c = Context(buyer))
        {
            var created = await Quotes(c, buyer).CreateAsync(new CreateQuoteRequest
            {
                Items = [new QuoteItemInput(item.Id, 6m)], SupplierIds = [supplier.Id]
            });
            created.IsSuccess.Should().BeTrue(string.Join(", ", created.Errors.Select(e => e.ResourceKey)));
            quoteId = created.Value;
        }

        string orderNumber;
        await using (var c = Context(buyer))
        {
            var quote = (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value;
            (await Quotes(c, buyer).RecordOfferAsync(quoteId, new RecordQuoteOfferRequest
            {
                SupplierId = supplier.Id, ReceivedOn = Clock.Today, ValidUntil = Clock.Today.AddDays(30),
                DeliveryDelayDays = 5, Lines = [new SupplierQuoteLineInput(quote.Items.Single().Id, 100m)]
            })).IsSuccess.Should().BeTrue();

            var offer = (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value.Offers.Single();
            var awarded = await Quotes(c, buyer).AwardAsync(quoteId, offer.Id);
            awarded.IsSuccess.Should().BeTrue(string.Join(", ", awarded.Errors.Select(e => e.ResourceKey)));
            orderNumber = awarded.Value;
        }

        // ---- Approval by someone else, then sending ----------------------------------------------
        Guid orderId, lineId;
        await using (var c = Context(buyer))
        {
            var order = await c.PurchaseOrders.Include(o => o.Lines).AsNoTracking().SingleAsync(o => o.Number == orderNumber);
            orderId = order.Id;
            lineId = order.Lines.Single().Id;
            order.Lines.Single().UnitPriceMad.Should().Be(100m);

            (await Orders(c, buyer).ApproveAsync(orderId)).FirstError.ResourceKey
                .Should().Be("Po_ApproverMustDifferFromRequester");
        }

        await using (var c = Context(manager))
        {
            (await Orders(c, manager).ApproveAsync(orderId)).IsSuccess.Should().BeTrue();
            (await Orders(c, manager).SendAsync(orderId)).IsSuccess.Should().BeTrue();
        }

        // ---- Two deliveries on one screen, the second first keyed with the wrong expiry ----------
        var expiry = Clock.Today.AddMonths(10);
        await using (var screen = Context(clerk))
        {
            var orders = Orders(screen, clerk);

            ReceivePurchaseOrderLineRequest Delivery(DateOnly on) => new()
            {
                LineId = lineId, Quantity = 3m, LotNumber = "L2611", ExpiryDate = on, DeliveryNoteReference = "BL-1"
            };

            (await orders.ReceiveLineAsync(orderId, Delivery(expiry))).IsSuccess.Should().BeTrue();

            (await orders.ReceiveLineAsync(orderId, Delivery(expiry.AddDays(1)))).FirstError.ResourceKey
                .Should().Be("Lot_ReceiveExpiryDiffers");

            var again = await orders.ReceiveLineAsync(orderId, Delivery(expiry));
            again.IsSuccess.Should().BeTrue(string.Join(", ", again.Errors.Select(e => e.ResourceKey)));
        }

        await using (var c = Context(clerk))
        {
            var order = await c.PurchaseOrders.Include(o => o.Lines).AsNoTracking().SingleAsync(o => o.Id == orderId);
            order.Lines.Single().QuantityReceived.Should().Be(6m);
            order.Status.Should().Be(PurchaseOrderStatus.Received);

            var lot = await c.ReagentLots.AsNoTracking().SingleAsync(l => l.StockItemId == item.Id);
            lot.QuantityReceived.Should().Be(6m);
            (await c.StockMovements.AsNoTracking().CountAsync(m => m.ReagentLotId == lot.Id)).Should().Be(2);
        }

        // ---- The invoice: blocked by its price, released by another hand, then paid --------------
        Guid invoiceId;
        await using (var c = Context(clerk))
        {
            var lines = (await Invoices(c, clerk).GetInvoiceableLinesAsync(supplier.Id)).Value;
            lines.Should().ContainSingle().Which.ReceivedNotInvoiced.Should().Be(6m);

            var entered = await Invoices(c, clerk).CreateAsync(new CreateSupplierInvoiceRequest
            {
                SupplierId = supplier.Id, InvoiceNumber = "FA-2611", InvoiceDate = Clock.Today,
                ReceivedOn = Clock.Today,
                Lines = [new SupplierInvoiceLineRequest(lineId, 6m, 110m, 0.20m)]
            });
            entered.IsSuccess.Should().BeTrue(string.Join(", ", entered.Errors.Select(e => e.ResourceKey)));
            entered.Value.Status.Should().Be(SupplierInvoiceStatus.Blocked);
            entered.Value.DueDate.Should().Be(Clock.Today.AddDays(60));
            invoiceId = entered.Value.InvoiceId;

            // The same number again for this supplier is refused.
            (await Invoices(c, clerk).CreateAsync(new CreateSupplierInvoiceRequest
            {
                SupplierId = supplier.Id, InvoiceNumber = " FA-2611 ", InvoiceDate = Clock.Today,
                ReceivedOn = Clock.Today, Lines = [new SupplierInvoiceLineRequest(lineId, 1m, 100m, 0.20m)]
            })).FirstError.ResourceKey.Should().Be("SupplierInvoice_NumberAlreadyEntered");

            (await Invoices(c, clerk).RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 100m, SupplierPaymentMethod.Transfer, "VIR-1"))).FirstError.ResourceKey
                .Should().Be("SupplierInvoice_NotApprovedForPayment");

            (await Invoices(c, clerk).UnblockAsync(invoiceId, "Hausse tarifaire annoncée")).FirstError.ResourceKey
                .Should().Be("SupplierInvoice_UnblockerMustDifferFromEnterer");
        }

        await using (var c = Context(manager))
        {
            var detail = (await Invoices(c, manager).GetDetailAsync(invoiceId)).Value;
            detail.Variances.Should().ContainSingle(v => v.Kind == VarianceKind.PriceDiffers && v.Expected == 100m && v.Actual == 110m);
            detail.Header.TotalIncludingVat.Should().Be(792m);

            (await Invoices(c, manager).UnblockAsync(invoiceId, "  ")).FirstError.ResourceKey
                .Should().Be("SupplierInvoice_JustificationRequired");
            (await Invoices(c, manager).UnblockAsync(invoiceId, "Hausse tarifaire annoncée")).IsSuccess.Should().BeTrue();

            (await Invoices(c, manager).RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 400m, SupplierPaymentMethod.Transfer, "VIR-1"))).IsSuccess.Should().BeTrue();
            (await Invoices(c, manager).RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 400m, SupplierPaymentMethod.Cheque, "CHQ-2"))).FirstError.ResourceKey
                .Should().Be("SupplierPayment_ExceedsOutstanding");
            (await Invoices(c, manager).RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 392m, SupplierPaymentMethod.Cheque, "CHQ-2"))).IsSuccess.Should().BeTrue();
        }

        await using (var c = Context(manager))
        {
            var paid = (await Invoices(c, manager).GetDetailAsync(invoiceId)).Value;
            paid.Header.Status.Should().Be(SupplierInvoiceStatus.Paid);
            paid.Header.AmountOutstanding.Should().Be(0m);
            paid.Payments.Should().HaveCount(2);
            paid.UnblockJustification.Should().Be("Hausse tarifaire annoncée");

            (await Invoices(c, manager).GetInvoiceableLinesAsync(supplier.Id)).Value.Should().BeEmpty();

            var reasons = await c.AuditEntries.AsNoTracking()
                                 .Where(a => a.EntityId == invoiceId && a.Reason != null)
                                 .Select(a => a.Reason!).ToListAsync();
            reasons.Should().Contain(r => r.StartsWith("Déblocage de la facture fournisseur FA-2611"));
        }
    }
}
