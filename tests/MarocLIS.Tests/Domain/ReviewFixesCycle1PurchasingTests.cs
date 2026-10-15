using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Third review, purchasing and stock: count variance against the stock at the count (P1), fresh
/// count lines on a long-lived screen (P2), the delivery a supplier invoice bills (P4), concurrent
/// supplier payments (P5), overlapping counts (P6), CSV formula guard (P7, S2) and the attachment
/// allow-list (S1).
/// </summary>
public sealed class ReviewFixesCycle1PurchasingTests(ReviewFixesCycle1PurchasingDatabase database)
    : IClassFixture<ReviewFixesCycle1PurchasingDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(1));

    // ---- P1: an issue made before the shelf is counted is not taken off twice -------------------

    [Fact]
    public void P1_count_variance_is_measured_against_the_stock_at_the_count()
    {
        var user = Guid.NewGuid();
        var lot = new ReagentLot("L1", Today.AddDays(90), 10m, Today.AddDays(-10));
        lot.Release(Today.AddDays(-9), user, "CQ conforme").IsSuccess.Should().BeTrue();

        var count = new InventoryCount("INV/2026/0010", Guid.NewGuid(), Today, InventoryCountScope.All, user);
        count.StartCounting([new InventoryCountLineInput(Guid.NewGuid(), lot.Id, "GLU", "L1", lot.ExpiryDate,
                                                         "Frigo 1", lot.QuantityRemaining)], Now)
             .IsSuccess.Should().BeTrue();

        // Two units go to the analyser, then the shelf is counted: 8 there, as the stock says.
        lot.Consume(2m, Today).IsSuccess.Should().BeTrue();
        var line = count.Lines.Single();
        count.RecordCount(line.Id, 8m, user, Now.AddHours(1), null, lot.QuantityRemaining).IsSuccess.Should().BeTrue();

        line.StockAtCount.Should().Be(8m);
        line.Variance.Should().Be(0m);
        lot.ApplyCountVariance(line.Variance!.Value).Should().Be(0m);
        lot.QuantityRemaining.Should().Be(8m);

        // One box really missing: only that one comes off.
        count.RecordCount(line.Id, 7m, user, Now.AddHours(1), null, lot.QuantityRemaining).IsSuccess.Should().BeTrue();
        lot.ApplyCountVariance(line.Variance!.Value).Should().Be(-1m);
        lot.QuantityRemaining.Should().Be(7m);
    }

    [Fact]
    public void P1_without_a_stock_reading_the_frozen_quantity_still_serves()
    {
        var count = new InventoryCount("INV/2026/0011", Guid.NewGuid(), Today, InventoryCountScope.All, Guid.NewGuid());
        count.StartCounting([new InventoryCountLineInput(Guid.NewGuid(), Guid.NewGuid(), "GLU", "L1", Today.AddDays(9),
                                                         null, 5m)], Now).IsSuccess.Should().BeTrue();

        count.RecordCount(count.Lines.Single().Id, 4m, Guid.NewGuid(), Now, null).IsSuccess.Should().BeTrue();
        count.Lines.Single().Variance.Should().Be(-1m);
    }

    // ---- P4: the due date runs from the delivery invoiced ----------------------------------------

    [Fact]
    public void P4_delivery_is_the_receipt_that_covers_the_quantity_invoiced()
    {
        DatedQuantity[] receipts = [new(new DateOnly(2026, 1, 1), 5m), new(new DateOnly(2026, 2, 15), 5m)];

        PaymentTerms.DeliveryCovering(receipts, 0m, 5m).Should().Be(new DateOnly(2026, 1, 1));
        PaymentTerms.DeliveryCovering(receipts, 5m, 5m).Should().Be(new DateOnly(2026, 2, 15));
        PaymentTerms.DeliveryCovering(receipts, 0m, 6m).Should().Be(new DateOnly(2026, 2, 15));
        PaymentTerms.DeliveryCovering(receipts, 0m, 20m).Should().Be(new DateOnly(2026, 2, 15));
        PaymentTerms.DeliveryCovering([], 0m, 1m).Should().BeNull();
    }

    [Fact]
    public void P4_january_delivery_invoiced_after_a_february_one_keeps_its_january_term()
    {
        var source = new OrderLineMatchSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GLU", 10m, 0m, 90m,
                                              0.20m, new DateOnly(2026, 2, 15),
                                              [new(new DateOnly(2026, 1, 1), 5m), new(new DateOnly(2026, 2, 15), 5m)],
                                              []);

        var delivery = source.DeliveryFor(new DateOnly(2026, 1, 5), 5m);
        delivery.Should().Be(new DateOnly(2026, 1, 1));
        PaymentTerms.DueDate(new DateOnly(2026, 1, 5), delivery, 60).Should().Be(new DateOnly(2026, 3, 2));

        // The February invoice, with January's already billed by an earlier one, runs from February.
        var later = source with { OtherInvoices = [new(new DateOnly(2026, 1, 5), 5m)] };
        later.DeliveryFor(new DateOnly(2026, 2, 20), 5m).Should().Be(new DateOnly(2026, 2, 15));

        // No dated receipt on record: the line's latest delivery, as before.
        (source with { Receipts = [] }).DeliveryFor(new DateOnly(2026, 1, 5), 5m).Should().Be(new DateOnly(2026, 2, 15));
    }

    // ---- P5: each payment changes the invoice row ----------------------------------------------

    [Fact]
    public void P5_every_payment_changes_the_invoice_row()
    {
        var invoice = ApprovedInvoice(Guid.NewGuid());

        invoice.RecordPayment(Payment(3_000m)).IsSuccess.Should().BeTrue();
        invoice.PaymentCount.Should().Be(1);
        invoice.Status.Should().Be(SupplierInvoiceStatus.PartiallyPaid);
        invoice.RecordPayment(Payment(3_000m)).IsSuccess.Should().BeTrue();
        invoice.PaymentCount.Should().Be(2);
        invoice.Status.Should().Be(SupplierInvoiceStatus.PartiallyPaid);
    }

    // ---- P7 / S2: CSV formula guard --------------------------------------------------------------

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\";\"DUPONT\")", "\"'=HYPERLINK(\"\"http://x\"\";\"\"DUPONT\"\")\"")]
    [InlineData("+212600000000x", "'+212600000000x")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tvaleur", "'\tvaleur")]
    [InlineData("-12,50", "-12,50")]
    [InlineData("-3.5", "-3.5")]
    [InlineData("DUPONT", "DUPONT")]
    [InlineData("", "")]
    public void P7_S2_every_purchasing_csv_neutralises_formulas(string value, string expected)
    {
        CsvCell.Of(value).Should().Be(expected);
        StockCsv.Cell(value).Should().Be(expected);
    }

    [Fact]
    public void P7_S2_recall_list_and_accounting_exports_use_the_guard()
    {
        var recall = System.Text.Encoding.UTF8.GetString(PurchasingCsv.Recall(
            [new RecallResultRow(Guid.NewGuid(), Guid.NewGuid(), "=HYPERLINK(\"http://x\")", "-1x", Guid.NewGuid(), "A1",
                                 "GLU", "GLU", null, true)], ["Patient"]));
        recall.Should().Contain("'=HYPERLINK").And.Contain(";'-1x;");

        var late = System.Text.Encoding.UTF8.GetString(SupplierInvoiceService.LatePaymentsCsv(
            [new LatePaymentRow("@Fournisseur", "ICE", "IF", "FA-1", Today, Today, Today, 100m, 100m, null, 3, 1m, 1m)]));
        late.Should().Contain("'@Fournisseur");
    }

    // ---- S1: contract and quote attachments ----------------------------------------------------

    [Theory]
    [InlineData("contrat.pdf", "application/pdf")]
    [InlineData("scan.JPG", "image/jpeg")]
    [InlineData("scan.tiff", "image/tiff")]
    [InlineData("offre.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("offre.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("offre.hta", null)]
    [InlineData("offre.exe", null)]
    [InlineData("offre.js", null)]
    [InlineData("offre.xlsm", null)]
    [InlineData("offre.doc", null)]
    [InlineData("offre.pdf.lnk", null)]
    [InlineData("offre", null)]
    public void S1_only_documents_that_cannot_run_are_accepted(string fileName, string? contentType) =>
        PurchasingDocument.ContentTypeFor(fileName).Should().Be(contentType);

    [Fact]
    public async Task S1_contract_and_quote_services_refuse_other_files_and_will_not_open_them()
    {
        var guard = new Guard(Permissions.Equipment.ManageContracts, Permissions.Equipment.ViewContracts,
                              Permissions.Inventory.ManageQuotes);
        var user = new User(Guid.NewGuid());
        var stored = ("offre.hta", new byte[] { 1 });

        var contracts = new InstrumentContractService(null!, null!, null!,
            Stub<IInstrumentContractQueries>.Returning<(string, byte[])?>("GetDocumentAsync", stored),
            null!, guard, user, new Clock());
        (await contracts.AttachFileAsync(Guid.NewGuid(), "contrat.exe", [1, 2]))
            .FirstError.ResourceKey.Should().Be("Purchasing_FileType");
        (await contracts.GetFileAsync(Guid.NewGuid())).FirstError.ResourceKey.Should().Be("Purchasing_FileType");

        var quotes = new QuoteService(null!, null!, null!,
            Stub<IQuoteQueries>.Returning<(string, byte[])?>("GetDocumentAsync", stored),
            null!, null!, null!, null!, guard, user, new Clock());
        (await quotes.RecordOfferAsync(Guid.NewGuid(), new RecordQuoteOfferRequest
        {
            SupplierId = Guid.NewGuid(), ReceivedOn = Today, Lines = [],
            FileName = "offre.js", FileContent = [1, 2]
        })).FirstError.ResourceKey.Should().Be("Purchasing_FileType");
        (await quotes.GetOfferFileAsync(Guid.NewGuid())).FirstError.ResourceKey.Should().Be("Purchasing_FileType");
    }

    // ---- SQL Server: P1, P2, P6 through the count service; P5 on the row version ----------------

    [SqlServerFact]
    public async Task P1_P2_P6_count_on_a_long_lived_screen_closes_on_the_lines_entered_elsewhere()
    {
        var siteId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var item = new StockItem("R" + Guid.NewGuid().ToString("N")[..8], new MultilingualText("Glucose"),
                                 StockItemKind.Reagent, "coffret", siteId);
        var lot = item.AddLot(new ReagentLot("L1", Today.AddDays(200), 10m, Today.AddDays(-5)) { StorageLocation = "Frigo 1" });
        lot.Release(Today.AddDays(-4), user, "CQ conforme").IsSuccess.Should().BeTrue();
        var first = new InventoryCount("INV/" + Guid.NewGuid().ToString("N")[..8], siteId, Today, InventoryCountScope.All, user);
        var second = new InventoryCount("INV/" + Guid.NewGuid().ToString("N")[..8], siteId, Today, InventoryCountScope.All, user);

        await using (var setup = database.Context())
        {
            setup.StockItems.Add(item);
            setup.InventoryCounts.AddRange(first, second);
            await setup.SaveChangesAsync();
        }

        // The manager's screen keeps one context for as long as it is open.
        await using var screen = database.Context();
        var manager = Service(screen, siteId);
        (await manager.StartCountingAsync(first.Id)).Value.Should().Be(1);

        // P6: the same lot cannot go on a second open count.
        (await manager.StartCountingAsync(second.Id)).FirstError.ResourceKey.Should().Be("InventoryCount_LotsInOpenCount");

        // Two units issued for a run before the shelf is counted.
        await using (var bench = database.Context())
        {
            var tracked = await bench.ReagentLots.SingleAsync(l => l.Id == lot.Id);
            tracked.Consume(2m, Today).IsSuccess.Should().BeTrue();
            await bench.SaveChangesAsync();
        }

        // The technician counts 7 on another workstation.
        var detail = (await manager.GetDetailAsync(first.Id)).Value;
        await using (var workstation = database.Context())
        {
            (await Service(workstation, siteId).RecordCountAsync(first.Id, detail.Lines.Single().LineId, 7m, null))
                .IsSuccess.Should().BeTrue();
        }

        // P2: the screen sees that count. P1: only the missing box comes off.
        (await manager.CloseAsync(first.Id)).Value.Should().Be(1);

        await using var read = database.Context();
        (await read.ReagentLots.AsNoTracking().SingleAsync(l => l.Id == lot.Id)).QuantityRemaining.Should().Be(7m);
        var line = (await read.InventoryCounts.AsNoTracking().SingleAsync(c => c.Id == first.Id)).Lines.Single();
        line.StockAtCount.Should().Be(8m);
        line.Variance.Should().Be(-1m);

        // Once the first is closed, the lot may be counted again.
        (await Service(read, siteId).StartCountingAsync(second.Id)).IsSuccess.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task P2_a_tracked_aggregate_picks_up_child_rows_saved_elsewhere()
    {
        var invoice = ApprovedInvoice(Guid.NewGuid());
        await using (var setup = database.Context())
        {
            setup.SupplierInvoices.Add(invoice);
            await setup.SaveChangesAsync();
        }

        await using var screen = database.Context();
        var repository = new SupplierInvoiceRepository(screen, new Clock());
        (await repository.GetByIdWithDetailsAsync(invoice.Id))!.Payments.Should().BeEmpty();

        await using (var other = database.Context())
        {
            var elsewhere = await new SupplierInvoiceRepository(other, new Clock()).GetByIdWithDetailsAsync(invoice.Id);
            elsewhere!.RecordPayment(Payment(4_000m)).IsSuccess.Should().BeTrue();
            await other.SaveChangesAsync();
        }

        var refreshed = await repository.GetByIdAsync(invoice.Id);
        refreshed!.Payments.Should().ContainSingle();
        refreshed.AmountOutstanding.Should().Be(6_000m);
        refreshed.RecordPayment(Payment(7_000m)).FirstError.ResourceKey.Should().Be("SupplierPayment_ExceedsOutstanding");
    }

    [SqlServerFact]
    public async Task P5_two_payments_on_the_same_balance_conflict()
    {
        var invoice = ApprovedInvoice(Guid.NewGuid());
        invoice.RecordPayment(Payment(4_000m)).IsSuccess.Should().BeTrue();
        await using (var setup = database.Context())
        {
            setup.SupplierInvoices.Add(invoice);
            await setup.SaveChangesAsync();
        }

        await using var a = database.Context();
        await using var b = database.Context();
        var onA = await new SupplierInvoiceRepository(a, new Clock()).GetByIdWithDetailsAsync(invoice.Id);
        var onB = await new SupplierInvoiceRepository(b, new Clock()).GetByIdWithDetailsAsync(invoice.Id);

        onA!.RecordPayment(Payment(3_000m)).IsSuccess.Should().BeTrue();
        onB!.RecordPayment(Payment(5_000m)).IsSuccess.Should().BeTrue();
        await a.SaveChangesAsync();

        await b.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var read = database.Context();
        (await read.SupplierPayments.AsNoTracking().Where(p => p.SupplierInvoiceId == invoice.Id).SumAsync(p => p.AmountMad))
            .Should().Be(7_000m);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static SupplierInvoice ApprovedInvoice(Guid supplierId)
    {
        var invoice = new SupplierInvoice(supplierId, Guid.NewGuid(), "FA-" + Guid.NewGuid().ToString("N")[..8],
                                          Today.AddDays(-5), Today.AddDays(-3), Guid.NewGuid());
        invoice.AddLine(new SupplierInvoiceLine(Guid.NewGuid(), Guid.NewGuid(), "GLU", 100m, 100m, 0m))
               .IsSuccess.Should().BeTrue();
        invoice.ApplyMatch([]).IsSuccess.Should().BeTrue();
        invoice.TotalIncludingVat.Should().Be(10_000m);
        return invoice;
    }

    private static SupplierPayment Payment(decimal amount) =>
        new(Today, amount, SupplierPaymentMethod.Transfer, "VIR-" + amount, Now, Guid.NewGuid());

    private static InventoryCountService Service(LisDbContext context, Guid siteId)
    {
        var clock = new Clock();
        return new InventoryCountService(new Repository<InventoryCount>(context, clock),
                                         new Repository<StockMovement>(context, clock),
                                         new InventoryCountQueries(context), new InventoryQueries(context), null!,
                                         new UnitOfWork(context),
                                         new Guard(Permissions.Inventory.CloseCounts, Permissions.Inventory.Receive),
                                         new User(siteId), clock);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now => ReviewFixesCycle1PurchasingTests.Now;
        public DateOnly Today => ReviewFixesCycle1PurchasingTests.Today;
    }

    private sealed class Guard(params string[] granted) : IPermissionGuard
    {
        public Result Require(string permission) =>
            granted.Contains(permission) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Result RequireAny(params string[] permissions) =>
            permissions.Any(granted.Contains) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Result RequireAll(params string[] permissions) =>
            permissions.All(granted.Contains) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class User(Guid siteId) : ICurrentUser
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

    /// <summary>A query interface where one method answers and every other one is not expected.</summary>
    public class Stub<T> : DispatchProxy where T : class
    {
        private string _method = string.Empty;
        private object? _value;

        public static T Returning<TResult>(string method, TResult value)
        {
            var proxy = Create<T, Stub<T>>();
            var self = (Stub<T>)(object)proxy;
            self._method = method;
            self._value = Task.FromResult(value);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == _method ? _value : throw new NotSupportedException(targetMethod?.Name);
    }
}

/// <summary>A scratch database for the purchasing review fixes, created from the model and dropped.</summary>
public sealed class ReviewFixesCycle1PurchasingDatabase : IDisposable
{
    private readonly bool _created;

    public ReviewFixesCycle1PurchasingDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c1purch_" + Guid.NewGuid().ToString("N")[..8]
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
