using System.Text;
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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database of its own for the supplier-invoice tests, created from the model.</summary>
public sealed class AchatsInvoicesScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_AchatsD_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public AchatsInvoicesScratchDatabase()
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
/// The supplier-invoice queries against SQL Server, through the services as the desktop composes
/// them: what is still invoiceable, "already invoiced elsewhere" with cancelled invoices left out,
/// the filtered unique number, the due-date dashboard, the late-payment declaration and the export.
/// </summary>
public sealed class AchatsInvoicesDatabaseTests(AchatsInvoicesScratchDatabase database)
    : IClassFixture<AchatsInvoicesScratchDatabase>
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private sealed class Operator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "compta";
        public string FullName => "Comptable du site A";
        public Guid SiteId => SiteA;
        public string SiteCode => "A";
        public AppLanguage Language => AppLanguage.French;
        public ProfessionalQualification Qualification => ProfessionalQualification.None;
        public IReadOnlyList<Guid> AccessibleSiteIds => [SiteA];
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public IReadOnlySet<string> AuthorizedDepartmentCodes { get; } = new HashSet<string>();
        public bool Has(string permission) => true;
        public Guid? PrescriberId => null;
        public bool IsPrescriberScoped => false;
        public string Workstation => "COMPTA-1";
    }

    private ServiceProvider Services()
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
        services.AddSingleton<ICurrentUser, Operator>();
        services.AddIsolatedQueries<ISupplierInvoiceQueries, SupplierInvoiceQueries>();
        return services.BuildServiceProvider();
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>
    /// An approved order of two lines — 10 at 100 less 10 %, and 5 at 50 — delivered 40 days ago
    /// in full on the first line and for 2 on the second, from a supplier on 10-day terms.
    /// </summary>
    private async Task<(Supplier Supplier, PurchaseOrder Order)> OrderAsync()
    {
        var supplier = new Supplier(Unique("F"), "Biomed Maroc") { IsApproved = true, PaymentTermsDays = 10, AccountingAccount = "FBIOMED" };
        var reagent = new StockItem(Unique("GLU"), new MultilingualText("Glucose"), StockItemKind.Reagent, "coffret", SiteA);
        var gloves = new StockItem(Unique("GAN"), new MultilingualText("Gants"), StockItemKind.Consumable, "boîte", SiteA);

        var order = new PurchaseOrder(Unique("BC/"), supplier.Id, SiteA, Today.AddDays(-50), Guid.NewGuid());
        order.AddLine(new PurchaseOrderLine(reagent.Id, 10m, 100m) { DiscountRate = 0.10m, Description = "GLU — Glucose" });
        order.AddLine(new PurchaseOrderLine(gloves.Id, 5m, 50m) { Description = "GAN — Gants" });
        order.Approve(DateTimeOffset.Now, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        order.Send(DateTimeOffset.Now).IsSuccess.Should().BeTrue();

        var first = order.Lines.Single(l => l.LineNumber == 1);
        var second = order.Lines.Single(l => l.LineNumber == 2);
        order.ReceiveLine(first.Id, 10m).IsSuccess.Should().BeTrue();
        first.LastReceivedOn = Today.AddDays(-40);
        order.ReceiveLine(second.Id, 2m).IsSuccess.Should().BeTrue();
        second.LastReceivedOn = Today.AddDays(-40);

        await using var context = database.Context();
        context.Suppliers.Add(supplier);
        context.StockItems.AddRange(reagent, gloves);
        context.PurchaseOrders.Add(order);
        await context.SaveChangesAsync();

        return (supplier, order);
    }

    private static CreateSupplierInvoiceRequest Invoice(Supplier supplier, string number, params SupplierInvoiceLineRequest[] lines) => new()
    {
        SupplierId = supplier.Id,
        InvoiceNumber = number,
        InvoiceDate = Today.AddDays(-39),
        ReceivedOn = Today.AddDays(-35),
        Lines = lines
    };

    [SqlServerFact]
    public async Task InvoicesAgainstAnOrder_MatchDueDatesAndDeclareLateness()
    {
        var (supplier, order) = await OrderAsync();
        var reagentLine = order.Lines.Single(l => l.LineNumber == 1).Id;
        var glovesLine = order.Lines.Single(l => l.LineNumber == 2).Id;

        using var provider = Services();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>();

        // What can be invoiced: both lines, at the net ordered price.
        var invoiceable = (await service.GetInvoiceableLinesAsync(supplier.Id)).Value;
        invoiceable.Should().HaveCount(2);
        invoiceable.Single(l => l.PurchaseOrderLineId == reagentLine)
            .Should().Match<InvoiceableOrderLine>(l => l.NetUnitPriceMad == 90m && l.ReceivedNotInvoiced == 10m);

        // First invoice: 6 of the 10 received, at the ordered price — matched, due 10 days after delivery.
        var first = (await service.CreateAsync(Invoice(supplier, "FA-1",
            new SupplierInvoiceLineRequest(reagentLine, 6m, 90m, 0.20m)))).Value;
        first.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
        first.DueDate.Should().Be(Today.AddDays(-30));

        // Second: 5 more, but only 4 remain received and not invoiced — blocked.
        var second = (await service.CreateAsync(Invoice(supplier, "FA-2",
            new SupplierInvoiceLineRequest(reagentLine, 5m, 90m, 0.20m)))).Value;
        second.Status.Should().Be(SupplierInvoiceStatus.Blocked);

        // The same supplier number again is refused; once cancelled, it can be keyed correctly.
        (await service.CreateAsync(Invoice(supplier, "FA-1", new SupplierInvoiceLineRequest(reagentLine, 1m, 90m, 0.20m))))
            .FirstError.ResourceKey.Should().Be("SupplierInvoice_NumberAlreadyEntered");
        (await service.CancelAsync(second.InvoiceId, "Quantité mal saisie")).IsSuccess.Should().BeTrue();
        var corrected = (await service.CreateAsync(Invoice(supplier, "FA-2",
            new SupplierInvoiceLineRequest(reagentLine, 4m, 90m, 0.20m)))).Value;
        corrected.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment, "the cancelled invoice no longer counts as invoiced");

        // Gloves billed in full while only 2 of 5 arrived: blocked, then matched once the rest is received.
        var gloves = (await service.CreateAsync(Invoice(supplier, "FA-3",
            new SupplierInvoiceLineRequest(glovesLine, 5m, 50m, 0.20m)))).Value;
        gloves.Status.Should().Be(SupplierInvoiceStatus.Blocked);

        await using (var context = database.Context())
        {
            var stored = await context.PurchaseOrders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
            var line = stored.Lines.Single(l => l.Id == glovesLine);
            stored.ReceiveLine(line.Id, 3m).IsSuccess.Should().BeTrue();
            line.LastReceivedOn = Today.AddDays(-5);
            await context.SaveChangesAsync();
        }

        var rematched = (await service.RematchAsync(gloves.InvoiceId)).Value;
        rematched.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
        rematched.DueDate.Should().Be(Today.AddDays(5), "the term now runs from the last delivery");

        // Nothing left to invoice on the reagent line.
        (await service.GetInvoiceableLinesAsync(supplier.Id)).Value
            .Should().NotContain(l => l.PurchaseOrderLineId == reagentLine);

        // The register and its dashboard: FA-1 and FA-2 overdue, FA-3 due within the week.
        var open = (await service.GetInvoicesAsync(SupplierInvoiceFilter.Open)).Value
            .Where(r => r.SupplierId == supplier.Id).ToList();
        open.Should().HaveCount(3);
        open.Count(r => r.IsOverdue).Should().Be(2);
        open.Single(r => r.InvoiceNumber == "FA-1").TotalIncludingVat.Should().Be(648m);

        await using (var context = database.Context())
        {
            var snapshot = await new DashboardQueries(context).GetSnapshotAsync(SiteA, DateTimeOffset.Now);
            snapshot.Alerts.Single(a => a.LabelKey == "SupplierInvoices.DashboardOverdue").Value.Should().BeGreaterThanOrEqualTo(2);
            snapshot.Alerts.Single(a => a.LabelKey == "SupplierInvoices.DashboardDueSoon").Value.Should().BeGreaterThanOrEqualTo(1);
        }

        // FA-1 paid in part today, 30 days late; the rest still owed.
        (await service.RecordPaymentAsync(first.InvoiceId,
            new RecordSupplierPaymentRequest(Today, 300m, SupplierPaymentMethod.Transfer, "VIR-1"))).IsSuccess.Should().BeTrue();

        var detail = (await service.GetDetailAsync(first.InvoiceId)).Value;
        detail.Header.Status.Should().Be(SupplierInvoiceStatus.PartiallyPaid);
        detail.Payments.Single().Reference.Should().Be("VIR-1");
        detail.Lines.Single().OrderNumber.Should().Be(order.Number);

        var late = (await service.GetLatePaymentsAsync(Today.AddDays(-60), Today)).Value
            .Where(r => r.SupplierName == "Biomed Maroc" && (r.InvoiceNumber == "FA-1" || r.InvoiceNumber == "FA-2")).ToList();
        late.Should().HaveCount(3);
        late.Should().ContainSingle(r => r.InvoiceNumber == "FA-1" && r.PaidOn == Today && r.LateAmount == 300m && r.DaysLate == 30);
        late.Should().ContainSingle(r => r.InvoiceNumber == "FA-1" && r.PaidOn == null && r.LateAmount == 348m);
        late.Should().ContainSingle(r => r.InvoiceNumber == "FA-2" && r.PaidOn == null && r.LateAmount == 432m);

        // The accounting export: purchases on 6122 for the reagent, the supplier on its auxiliary account.
        var file = await service.ExportAccountingAsync(Today.AddDays(-60), Today, AccountingExportLayout.Generic);
        var csv = Encoding.UTF8.GetString(file.Value);
        csv.Should().Contain(";ACH;6122;;Fact. FA-1 Biomed Maroc;FA-1;540.00;0.00");
        csv.Should().Contain(";ACH;4411;FBIOMED;Fact. FA-1 Biomed Maroc;FA-1;0.00;648.00");
        csv.Should().Contain(";BQ;5141;;");

        // The parameters are stored as settings and read back.
        var settings = (await service.GetSettingsAsync()).Value;
        (await service.SaveSettingsAsync(settings with { PriceTolerancePercent = 2m, KeyRatePercent = 2.5m })).IsSuccess.Should().BeTrue();
        (await service.GetSettingsAsync()).Value.Should().Match<PurchasingSettings>(s => s.PriceTolerancePercent == 2m && s.PenaltyRatePercent == 9.5m);
    }
}
