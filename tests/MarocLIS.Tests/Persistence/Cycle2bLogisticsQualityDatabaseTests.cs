using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Infrastructure.Security;
using MarocLIS.Infrastructure.Sequencing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesSecurityTests;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own for the cycle 2 logistics and quality scenarios. Foreign keys are
/// switched off: the scenarios seed only what the services read (a QC target without its analyte
/// and analyser rows), not the whole catalogue.
/// </summary>
public sealed class Cycle2bDatabase : IDisposable
{
    private readonly bool _created;

    public Cycle2bDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c2b_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = new LisDbContext(new DbContextOptionsBuilder<LisDbContext>()
                                                 .UseSqlServer(ConnectionString).Options);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'");
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
/// Cycle 2, lot b: the logistics and quality workflows end to end on SQL Server, through the
/// services the screens call, each step read back in a fresh context as the next screen sees it.
/// A delivery invoiced before it was complete, matched once the rest arrived and part-paid; an
/// issue larger than the shelf, refused without touching it; a count with a variance closed; a
/// control out of limits worked from its non-conformity to closure; a temperature excursion; and
/// the shipped roles of a quality manager, a technician and an accountant against those steps.
/// </summary>
public sealed class Cycle2bLogisticsQualityDatabaseTests(Cycle2bDatabase database) : IClassFixture<Cycle2bDatabase>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 16);
    }

    private static readonly Guid Site = Guid.NewGuid();
    private static readonly FixedClock Clock = new();

    private static ICurrentUser User(Guid userId, IReadOnlyCollection<string>? rights = null) =>
        Stub<ICurrentUser>.Create(new()
        {
            ["get_IsAuthenticated"] = _ => true,
            ["get_UserId"] = _ => userId,
            ["get_UserName"] = _ => "c2b",
            ["get_FullName"] = _ => "Cycle 2b",
            ["get_SiteId"] = _ => Site,
            ["get_SiteCode"] = _ => "PRINCIPAL",
            ["get_AccessibleSiteIds"] = _ => (IReadOnlyList<Guid>)[Site],
            ["Has"] = a => rights is null || rights.Contains((string)a[0]!)
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

    private static PurchaseOrderService Orders(LisDbContext c, ICurrentUser user, IPermissionGuard? guard = null) =>
        new(new PurchaseOrderRepository(c, Clock), new StockItemRepository(c, Clock),
            new Repository<StockMovement>(c, Clock), new PurchaseOrderQueries(c),
            new SequenceAllocator(c, Clock, user), new UnitOfWork(c), guard ?? Allowed, user, Clock);

    private static SupplierInvoiceService Invoices(LisDbContext c, ICurrentUser user, IPermissionGuard? guard = null) =>
        new(new SupplierInvoiceRepository(c, Clock), new Repository<SupplierInvoiceFile>(c, Clock),
            new Repository<Supplier>(c, Clock), new Repository<LatePaymentDeclaration>(c, Clock),
            new SupplierInvoiceQueries(c), new ConfigurationWriter(c), new UnitOfWork(c), guard ?? Allowed, user, Clock);

    private static InventoryService Inventory(LisDbContext c, ICurrentUser user, IPermissionGuard? guard = null) =>
        new(new StockItemRepository(c, Clock), new Repository<StockMovement>(c, Clock),
            new Repository<Supplier>(c, Clock), new InventoryQueries(c), new UnitOfWork(c),
            guard ?? Allowed, user, Clock);

    private static InventoryCountService Counts(LisDbContext c, ICurrentUser user, IPermissionGuard? guard = null) =>
        new(new Repository<InventoryCount>(c, Clock), new Repository<StockMovement>(c, Clock),
            new InventoryCountQueries(c), new InventoryQueries(c), new SequenceAllocator(c, Clock, user),
            new UnitOfWork(c), guard ?? Allowed, user, Clock);

    private static QualityControlService Qc(LisDbContext c, ICurrentUser user) =>
        new(new Repository<QcTarget>(c, Clock), new QcResultRepository(c, Clock), new Repository<QcMaterial>(c, Clock),
            new NonConformityRepository(c, Clock), new QualityQueries(c), new SequenceAllocator(c, Clock, user),
            new UnitOfWork(c), Allowed, user, Clock);

    private static NonConformityService NonConformities(LisDbContext c, ICurrentUser user) =>
        new(new NonConformityRepository(c, Clock), new QualityQueries(c),
            new SequenceAllocator(c, Clock, user), new UnitOfWork(c), Allowed, user, Clock);

    private static TemperatureMonitoringService Temperatures(LisDbContext c, ICurrentUser user) =>
        new(new Repository<StorageUnit>(c, Clock), new Repository<TemperatureReading>(c, Clock),
            new NonConformityRepository(c, Clock), new TemperatureQueries(c), new SequenceAllocator(c, Clock, user),
            new UnitOfWork(c), Allowed, user, Clock);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string Keys(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private async Task<(Supplier Supplier, StockItem Item)> SeedSupplierAndItemAsync(ICurrentUser user)
    {
        var supplier = new Supplier(Unique("F"), "Bio-Distribution") { IsApproved = true, PaymentTermsDays = 60 };
        var item = new StockItem(Unique("CRP"), MultilingualText.FromFrench("CRP latex"), StockItemKind.Reagent,
                                 "coffret", Site);
        await using var seed = Context(user);
        seed.Suppliers.Add(supplier);
        seed.StockItems.Add(item);
        await seed.SaveChangesAsync();
        return (supplier, item);
    }

    [SqlServerFact]
    public async Task AnInvoiceBilledBeforeTheBackorder_IsBlocked_ThenMatchedWhenItArrives_AndPartPaid()
    {
        var buyer = User(Guid.NewGuid());
        var manager = User(Guid.NewGuid());
        var clerk = User(Guid.NewGuid());
        var (supplier, item) = await SeedSupplierAndItemAsync(buyer);

        // ---- An order of ten, approved by another hand and sent ------------------------------------
        string number;
        await using (var c = Context(buyer))
        {
            var created = await Orders(c, buyer).CreateAsync(new CreatePurchaseOrderRequest { SupplierId = supplier.Id });
            created.IsSuccess.Should().BeTrue(Keys(created));
            number = created.Value;
        }

        Guid orderId, lineId;
        await using (var c = Context(buyer))
        {
            orderId = (await c.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Number == number)).Id;
            var added = await Orders(c, buyer).AddLineAsync(orderId, new AddPurchaseOrderLineRequest
            {
                StockItemId = item.Id, Quantity = 10m, UnitPriceMad = 50m, VatRate = 0.20m
            });
            added.IsSuccess.Should().BeTrue(Keys(added));
            lineId = (await c.PurchaseOrders.Include(o => o.Lines).AsNoTracking().SingleAsync(o => o.Id == orderId))
                     .Lines.Single().Id;
        }

        await using (var c = Context(manager))
        {
            (await Orders(c, manager).ApproveAsync(orderId)).IsSuccess.Should().BeTrue();
            (await Orders(c, manager).SendAsync(orderId)).IsSuccess.Should().BeTrue();
        }

        ReceivePurchaseOrderLineRequest Delivery(decimal quantity) => new()
        {
            LineId = lineId, Quantity = quantity, LotNumber = "C2611", ExpiryDate = Clock.Today.AddMonths(8),
            DeliveryNoteReference = "BL-" + quantity
        };

        // ---- Four delivered; the supplier bills the ten --------------------------------------------
        await using (var c = Context(clerk))
            (await Orders(c, clerk).ReceiveLineAsync(orderId, Delivery(4m))).IsSuccess.Should().BeTrue();

        Guid invoiceId;
        await using (var c = Context(clerk))
        {
            var entered = await Invoices(c, clerk).CreateAsync(new CreateSupplierInvoiceRequest
            {
                SupplierId = supplier.Id, InvoiceNumber = "FA-C2B-1", InvoiceDate = Clock.Today, ReceivedOn = Clock.Today,
                Lines = [new SupplierInvoiceLineRequest(lineId, 10m, 50m, 0.20m)]
            });
            entered.IsSuccess.Should().BeTrue(Keys(entered));
            entered.Value.Status.Should().Be(SupplierInvoiceStatus.Blocked);
            invoiceId = entered.Value.InvoiceId;
        }

        await using (var c = Context(manager))
        {
            var detail = (await Invoices(c, manager).GetDetailAsync(invoiceId)).Value;
            detail.Variances.Should().ContainSingle()
                  .Which.Should().Be(new SupplierInvoiceVarianceRow(1, VarianceKind.QuantityExceedsReceived, 4m, 10m));
            detail.Header.TotalIncludingVat.Should().Be(600m);

            // Blocked: nothing can be paid on it yet.
            (await Invoices(c, manager).RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 100m, SupplierPaymentMethod.Transfer, "VIR-1"))).FirstError.ResourceKey
                .Should().Be("SupplierInvoice_NotApprovedForPayment");
        }

        // ---- The backorder arrives (same lot): matched again, approved without anyone releasing it --
        await using (var c = Context(clerk))
            (await Orders(c, clerk).ReceiveLineAsync(orderId, Delivery(6m))).IsSuccess.Should().BeTrue();

        await using (var c = Context(clerk))
        {
            var rematched = await Invoices(c, clerk).RematchAsync(invoiceId);
            rematched.IsSuccess.Should().BeTrue(Keys(rematched));
            rematched.Value.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);
            rematched.Value.VarianceCount.Should().Be(0);

            // Approved, it is not matched again: the decision to pay stands.
            (await Invoices(c, clerk).RematchAsync(invoiceId)).FirstError.ResourceKey
                .Should().Be("SupplierInvoice_AlreadyMatched");
        }

        // ---- Part-paid; a payment dated tomorrow or above the balance is refused -------------------
        await using (var c = Context(manager))
        {
            var invoices = Invoices(c, manager);
            (await invoices.RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today.AddDays(1), 100m, SupplierPaymentMethod.Transfer, "VIR-1"))).FirstError.ResourceKey
                .Should().Be("SupplierPayment_DateInFuture");
            (await invoices.RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 100m, SupplierPaymentMethod.Cheque, " "))).FirstError.ResourceKey
                .Should().Be("SupplierPayment_ReferenceRequired");
            (await invoices.RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 250m, SupplierPaymentMethod.Transfer, "VIR-1"))).IsSuccess.Should().BeTrue();
            (await invoices.RecordPaymentAsync(invoiceId, new RecordSupplierPaymentRequest(
                Clock.Today, 350.01m, SupplierPaymentMethod.Transfer, "VIR-2"))).FirstError.ResourceKey
                .Should().Be("SupplierPayment_ExceedsOutstanding");
        }

        await using (var c = Context(clerk))
        {
            var detail = (await Invoices(c, clerk).GetDetailAsync(invoiceId)).Value;
            detail.Header.Status.Should().Be(SupplierInvoiceStatus.PartiallyPaid);
            detail.Header.AmountOutstanding.Should().Be(350m);

            // Paid in part, it can no longer be cancelled; the ten received are all billed.
            (await Invoices(c, clerk).CancelAsync(invoiceId, "Saisie en double")).FirstError.ResourceKey
                .Should().Be("SupplierInvoice_HasPayments");
            (await Invoices(c, clerk).GetInvoiceableLinesAsync(supplier.Id)).Value.Should().BeEmpty();

            // A second invoice on the same line bills goods nobody received.
            var again = await Invoices(c, clerk).CreateAsync(new CreateSupplierInvoiceRequest
            {
                SupplierId = supplier.Id, InvoiceNumber = "FA-C2B-2", InvoiceDate = Clock.Today, ReceivedOn = Clock.Today,
                Lines = [new SupplierInvoiceLineRequest(lineId, 1m, 50m, 0.20m)]
            });
            again.Value.Status.Should().Be(SupplierInvoiceStatus.Blocked);

            // Cancelled (no payment), it frees nothing it had not billed and leaves the first alone.
            (await Invoices(c, clerk).CancelAsync(again.Value.InvoiceId, "Doublon du fournisseur")).IsSuccess.Should().BeTrue();
        }

        await using (var c = Context(clerk))
        {
            var lot = await c.ReagentLots.AsNoTracking().SingleAsync(l => l.StockItemId == item.Id);
            lot.QuantityReceived.Should().Be(10m);
            (await c.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status
                .Should().Be(PurchaseOrderStatus.Received);
            var reasons = await c.AuditEntries.AsNoTracking().Where(a => a.EntityId == invoiceId && a.Reason != null)
                                 .Select(a => a.Reason!).ToListAsync();
            reasons.Should().Contain(r => r.StartsWith("Nouveau rapprochement de la facture fournisseur FA-C2B-1"));
        }
    }

    [SqlServerFact]
    public async Task AnIssueLargerThanTheShelf_IsRefused_WithoutDrawingDownTheLotsForTheNextSave()
    {
        var technician = User(Guid.NewGuid());
        var item = new StockItem(Unique("GLU"), MultilingualText.FromFrench("Glucose HK"), StockItemKind.Reagent,
                                 "coffret", Site);
        var first = item.AddLot(new ReagentLot("G-OLD", Clock.Today.AddDays(40), 3m, Clock.Today.AddDays(-20)));
        var second = item.AddLot(new ReagentLot("G-NEW", Clock.Today.AddDays(200), 2m, Clock.Today.AddDays(-5)));
        first.Release(Clock.Today.AddDays(-20), technician.UserId, "CQ conforme").IsSuccess.Should().BeTrue();
        second.Release(Clock.Today.AddDays(-5), technician.UserId, "CQ conforme").IsSuccess.Should().BeTrue();
        await using (var seed = Context(technician))
        {
            seed.StockItems.Add(item);
            await seed.SaveChangesAsync();
        }

        // One screen, one context: the refused issue and then a correct one, as the operator retries.
        await using (var screen = Context(technician))
        {
            var inventory = Inventory(screen, technician);
            var refused = await inventory.IssueAsync(item.Id, 6m);
            refused.FirstError.ResourceKey.Should().Be("Stock_InsufficientUsableStock");

            var issued = await inventory.IssueAsync(item.Id, 1m);
            issued.IsSuccess.Should().BeTrue(Keys(issued));
            issued.Value.Should().StartWith("G-OLD");
        }

        await using (var c = Context(technician))
        {
            var lots = await c.ReagentLots.AsNoTracking().Where(l => l.StockItemId == item.Id)
                              .ToDictionaryAsync(l => l.LotNumber, l => l.QuantityRemaining);
            lots["G-OLD"].Should().Be(2m, "only the issue that went through is drawn, first expiry first");
            lots["G-NEW"].Should().Be(2m);
            (await c.StockMovements.AsNoTracking().CountAsync(m => m.StockItemId == item.Id)).Should().Be(1);
        }
    }

    [SqlServerFact]
    public async Task ACountWithAVariance_PostsOneAdjustment_AndIsThenClosedForGood()
    {
        var manager = User(Guid.NewGuid());
        var technician = User(Guid.NewGuid());
        var location = Unique("FRIGO-");
        var item = new StockItem(Unique("TSH"), MultilingualText.FromFrench("TSH"), StockItemKind.Reagent,
                                 "coffret", Site) { DefaultStorageLocation = location };
        var lot = item.AddLot(new ReagentLot("T-1", Clock.Today.AddDays(90), 10m, Clock.Today.AddDays(-10))
        {
            StorageLocation = location
        });
        lot.Release(Clock.Today.AddDays(-10), manager.UserId, "CQ conforme").IsSuccess.Should().BeTrue();
        await using (var seed = Context(manager))
        {
            seed.StockItems.Add(item);
            await seed.SaveChangesAsync();
        }

        string number;
        await using (var c = Context(manager))
        {
            var created = await Counts(c, manager).CreateAsync(new CreateInventoryCountRequest
            {
                CountDate = Clock.Today, Scope = InventoryCountScope.StorageLocation, StorageLocation = location
            });
            created.IsSuccess.Should().BeTrue(Keys(created));
            number = created.Value;
        }

        Guid countId;
        await using (var c = Context(manager))
        {
            countId = (await c.InventoryCounts.AsNoTracking().SingleAsync(x => x.Number == number)).Id;
            var started = await Counts(c, manager).StartCountingAsync(countId);
            started.IsSuccess.Should().BeTrue(Keys(started));
            started.Value.Should().Be(1);
        }

        await using (var c = Context(technician))
        {
            var counts = Counts(c, technician);
            var line = (await counts.GetDetailAsync(countId)).Value.Lines.Single();
            (await counts.RecordCountAsync(countId, line.LineId, -1m, null)).FirstError.ResourceKey
                .Should().Be("InventoryCount_NegativeQuantity");
            (await counts.RecordCountAsync(countId, line.LineId, 7m, "Deux coffrets cassés, un introuvable"))
                .IsSuccess.Should().BeTrue();
        }

        await using (var c = Context(manager))
        {
            var closed = await Counts(c, manager).CloseAsync(countId);
            closed.IsSuccess.Should().BeTrue(Keys(closed));
            closed.Value.Should().Be(1);
        }

        await using (var c = Context(manager))
        {
            var counts = Counts(c, manager);
            var detail = (await counts.GetDetailAsync(countId)).Value;
            detail.Count.Status.Should().Be(InventoryCountStatus.Closed);
            detail.Lines.Single().Variance.Should().Be(-3m);
            detail.Lines.Single().Comment.Should().Be("Deux coffrets cassés, un introuvable");

            (await c.ReagentLots.AsNoTracking().SingleAsync(l => l.Id == lot.Id)).QuantityRemaining.Should().Be(7m);
            var adjustment = await c.StockMovements.AsNoTracking().SingleAsync(m => m.ReagentLotId == lot.Id);
            adjustment.Kind.Should().Be(StockMovementKind.NegativeAdjustment);
            adjustment.Quantity.Should().Be(3m);
            adjustment.Reference.Should().Be(number);

            // Closed: no more figures, no second closing, no cancelling.
            (await counts.RecordCountAsync(countId, detail.Lines.Single().LineId, 8m, null)).FirstError.ResourceKey
                .Should().Be("InventoryCount_NotCounting");
            (await counts.CloseAsync(countId)).FirstError.ResourceKey.Should().Be("InventoryCount_NotCounting");
            (await counts.CancelAsync(countId, "Erreur de périmètre")).FirstError.ResourceKey
                .Should().Be("InventoryCount_Closed");
        }
    }

    [SqlServerFact]
    public async Task AControlOutOfLimits_OpensANonConformity_WorkedToClosure()
    {
        var technician = User(Guid.NewGuid());
        var manager = User(Guid.NewGuid());

        var material = new QcMaterial(Unique("QC"), MultilingualText.FromFrench("Contrôle normal"), QcLevel.Normal,
                                      Clock.Today.AddMonths(6));
        var target = new QcTarget(material.Id, Guid.NewGuid(), Guid.NewGuid()) { AssignedMean = 100m, AssignedSd = 2m };
        await using (var seed = Context(technician))
        {
            seed.QcMaterials.Add(material);
            seed.QcTargets.Add(target);
            await seed.SaveChangesAsync();
        }

        // A value no analyser can produce (a key held down) is refused before anything is stored.
        await using (var c = Context(technician))
        {
            (await Qc(c, technician).SubmitAsync([new QcEntry(target.Id, 1_000_000_000_000_000m)])).FirstError.ResourceKey
                .Should().Be("Result_TechnicallyImpossible");
            (await Qc(c, technician).SubmitAsync([new QcEntry(target.Id, 4_000_000m)])).FirstError.ResourceKey
                .Should().Be("Result_TechnicallyImpossible");
        }

        await using (var c = Context(technician))
            (await c.QcResults.AsNoTracking().CountAsync(r => r.QcTargetId == target.Id)).Should().Be(0);

        // 112 against 100 ± 2: six SD out, rejected on 1-3s.
        Guid resultId;
        await using (var c = Context(technician))
        {
            var run = await Qc(c, technician).SubmitAsync([new QcEntry(target.Id, 112m)]);
            run.IsSuccess.Should().BeTrue(Keys(run));
            run.Value.AnyRejected.Should().BeTrue();
            resultId = run.Value.Results.Single().QcResultId;
        }

        await using (var c = Context(manager))
        {
            (await Qc(c, manager).ReviewAsync(resultId, "  ", openNonConformity: true)).FirstError.ResourceKey
                .Should().Be("Qc_RejectionRequiresCorrectiveAction");
            (await Qc(c, manager).ReviewAsync(resultId, "Recalibration, contrôle repassé conforme", openNonConformity: true))
                .IsSuccess.Should().BeTrue();
        }

        Guid fileId;
        await using (var c = Context(manager))
        {
            var reviewed = await c.QcResults.AsNoTracking().SingleAsync(r => r.Id == resultId);
            reviewed.NonConformityId.Should().NotBeNull();
            var file = await c.NonConformities.AsNoTracking().SingleAsync(n => n.Id == reviewed.NonConformityId);
            file.QcResultId.Should().Be(resultId);
            file.DetectionSource.Should().Be(DetectionSource.InternalQualityControl);
            file.ImmediateAction!.Fr.Should().Be("Recalibration, contrôle repassé conforme");
            fileId = file.Id;
        }

        // Corrective action, its evidence, the cause and the effectiveness check, then closure.
        await using (var c = Context(manager))
            (await NonConformities(c, manager).AddActionRequestAsync(fileId)).IsSuccess.Should().BeTrue();

        Guid actionId;
        await using (var c = Context(manager))
            actionId = (await c.NonConformities.Include(n => n.Actions).AsNoTracking().SingleAsync(n => n.Id == fileId))
                       .Actions.Single().Id;

        await using (var c = Context(manager))
        {
            var service = NonConformities(c, manager);
            (await service.CloseAsync(fileId)).Errors.Select(e => e.ResourceKey)
                .Should().Contain("Nc_AllActionsMustBeFinished");
            (await service.CompleteActionAsync(fileId, actionId, MultilingualText.FromFrench("Fiche de calibration")))
                .IsSuccess.Should().BeTrue();
        }

        await using (var c = Context(manager))
        {
            var service = NonConformities(c, manager);
            (await service.RecordAnalysisAsync(fileId, RootCauseCategory.EquipmentFailure,
                MultilingualText.FromFrench("Dérive de la calibration"),
                effectivenessVerification: MultilingualText.FromFrench("CIQ conformes sur 10 séries"))).IsSuccess.Should().BeTrue();
            var closed = await service.CloseAsync(fileId);
            closed.IsSuccess.Should().BeTrue(Keys(closed));
        }

        await using (var c = Context(manager))
        {
            (await c.NonConformities.AsNoTracking().SingleAsync(n => n.Id == fileId)).Status
                .Should().Be(NonConformityStatus.Closed);

            // A second review of the same control opens no second file.
            (await Qc(c, manager).ReviewAsync(resultId, "Revue complémentaire du biologiste", openNonConformity: true))
                .IsSuccess.Should().BeTrue();
            (await c.NonConformities.AsNoTracking().CountAsync(n => n.QcResultId == resultId)).Should().Be(1);
        }
    }

    [SqlServerFact]
    public async Task ATemperatureExcursion_OpensANonConformity_AndImplausibleFiguresAreRefused()
    {
        var manager = User(Guid.NewGuid());
        var technician = User(Guid.NewGuid());

        Guid unitId;
        await using (var c = Context(manager))
        {
            var service = Temperatures(c, manager);

            // A range no reading can meet is a typing error.
            (await service.SaveUnitAsync(new StorageUnitRequest
            {
                Name = Unique("Frigo "), Kind = StorageUnitKind.Refrigerator, MinimumCelsius = 2m,
                MaximumCelsius = 8_000_000_000_000m
            })).FirstError.ResourceKey.Should().Be("Temperature_ValueImplausible");

            var saved = await service.SaveUnitAsync(new StorageUnitRequest
            {
                Name = Unique("Frigo réactifs "), Kind = StorageUnitKind.Refrigerator, MinimumCelsius = 2m, MaximumCelsius = 8m
            });
            saved.IsSuccess.Should().BeTrue(Keys(saved));
            unitId = saved.Value;
        }

        await using (var c = Context(technician))
        {
            var service = Temperatures(c, technician);

            // The period's extremes are held to the same bounds as the reading.
            (await service.RecordReadingAsync(new TemperatureReadingRequest
            {
                StorageUnitId = unitId, Celsius = 5m, PeriodMinimumCelsius = 4m, PeriodMaximumCelsius = 1_000_000_000_000m,
                Comment = "Relevé"
            })).FirstError.ResourceKey.Should().Be("Temperature_ValueImplausible");
            (await service.RecordReadingAsync(new TemperatureReadingRequest
            {
                StorageUnitId = unitId, Celsius = 5m, PeriodMinimumCelsius = -5000m, Comment = "Relevé"
            })).FirstError.ResourceKey.Should().Be("Temperature_ValueImplausible");

            // In range: recorded, no file.
            var normal = await service.RecordReadingAsync(new TemperatureReadingRequest { StorageUnitId = unitId, Celsius = 5m });
            normal.IsSuccess.Should().BeTrue(Keys(normal));
            normal.Value.OutOfRange.Should().BeFalse();

            // Back in range but warm overnight: an excursion, which needs a comment and opens a file.
            (await service.RecordReadingAsync(new TemperatureReadingRequest
            {
                StorageUnitId = unitId, Celsius = 6m, PeriodMaximumCelsius = 11m
            })).FirstError.ResourceKey.Should().Be("Temperature_CommentRequired");
            var excursion = await service.RecordReadingAsync(new TemperatureReadingRequest
            {
                StorageUnitId = unitId, Celsius = 6m, PeriodMaximumCelsius = 11m,
                Comment = "Porte restée entrouverte, réactifs transférés"
            });
            excursion.IsSuccess.Should().BeTrue(Keys(excursion));
            excursion.Value.OutOfRange.Should().BeTrue();
            excursion.Value.NonConformityReference.Should().NotBeNull();
        }

        await using (var c = Context(manager))
        {
            var file = await c.NonConformities.AsNoTracking()
                              .SingleAsync(n => n.Reference == c.TemperatureReadings
                                                                .Where(r => r.StorageUnitId == unitId && r.NonConformityId != null)
                                                                .Select(r => c.NonConformities.First(x => x.Id == r.NonConformityId).Reference)
                                                                .First());
            file.Category.Should().Be(NonConformityCategory.Equipment);
            file.ImmediateAction!.Fr.Should().Be("Porte restée entrouverte, réactifs transférés");
            (await c.TemperatureReadings.AsNoTracking().CountAsync(r => r.StorageUnitId == unitId)).Should().Be(2);
        }
    }

    [SqlServerFact]
    public async Task TheShippedRoles_OpenOnlyTheirOwnStepsOfTheChain()
    {
        var owner = User(Guid.NewGuid());
        var (supplier, item) = await SeedSupplierAndItemAsync(owner);
        var lot = new ReagentLot("R-1", Clock.Today.AddDays(120), 5m, Clock.Today);
        await using (var c = Context(owner))
        {
            var tracked = await c.StockItems.Include(i => i.Lots).SingleAsync(i => i.Id == item.Id);
            tracked.AddLot(lot);
            await c.SaveChangesAsync();
        }

        async Task<Result> As(string role, Func<LisDbContext, ICurrentUser, IPermissionGuard, Task<Result>> step)
        {
            var user = User(Guid.NewGuid(), ShippedRole(role));
            await using var c = Context(user);
            var guard = new PermissionGuard(user, c, Clock, Stub<ISecurityAuditor>.Create());
            return await step(c, user, guard);
        }

        static string Denied(Result r) => r.IsFailure ? r.FirstError.ResourceKey : "allowed";

        // The technician takes deliveries in and counts, but neither releases a lot, adjusts the
        // stock, opens or closes a count, nor approves an order or a payment.
        Denied(await As(SystemRoles.Technician, (c, u, g) => Inventory(c, u, g).ReleaseLotAsync(lot.Id, "CQ conforme"))).Should().Be("Message.PermissionDenied");
        Denied(await As(SystemRoles.Technician, (c, u, g) => Inventory(c, u, g).AdjustAsync(lot.Id, 1m, StockMovementKind.DamageWriteOff, "Casse"))).Should().Be("Message.PermissionDenied");
        Denied(await As(SystemRoles.Technician, async (c, u, g) => (Result)await Counts(c, u, g).CreateAsync(
            new CreateInventoryCountRequest { CountDate = Clock.Today }))).Should().Be("Message.PermissionDenied");
        Denied(await As(SystemRoles.Technician, (c, u, g) => Orders(c, u, g).ApproveAsync(Guid.NewGuid()))).Should().Be("Message.PermissionDenied");
        Denied(await As(SystemRoles.Technician, async (c, u, g) => (Result)await Invoices(c, u, g).GetInvoicesAsync())).Should().Be("Message.PermissionDenied");

        // The quality manager releases and approves orders, but keys no invoice and pays nothing.
        var released = await As(SystemRoles.QualityManager, (c, u, g) => Inventory(c, u, g).ReleaseLotAsync(lot.Id, "CQ conforme sur 2 niveaux"));
        released.IsSuccess.Should().BeTrue(Keys(released));
        Denied(await As(SystemRoles.QualityManager, (c, u, g) => Invoices(c, u, g).UnblockAsync(Guid.NewGuid(), "Hausse tarifaire"))).Should().Be("Message.PermissionDenied");
        Denied(await As(SystemRoles.QualityManager, async (c, u, g) => (Result)await Orders(c, u, g).CreateAsync(
            new CreatePurchaseOrderRequest { SupplierId = supplier.Id }))).Should().Be("Message.PermissionDenied");

        // Once released the technician issues from it, which is their work.
        var issued = await As(SystemRoles.Technician, async (c, u, g) => (Result)await Inventory(c, u, g).IssueAsync(item.Id, 1m));
        issued.IsSuccess.Should().BeTrue(Keys(issued));

        // The accountant raises orders and keys invoices, but neither receives goods nor pays.
        (await As(SystemRoles.Accountant, async (c, u, g) => (Result)await Orders(c, u, g).CreateAsync(
            new CreatePurchaseOrderRequest { SupplierId = supplier.Id }))).IsSuccess.Should().BeTrue();
        Denied(await As(SystemRoles.Accountant, (c, u, g) => Orders(c, u, g).ReceiveLineAsync(Guid.NewGuid(),
            new ReceivePurchaseOrderLineRequest { LineId = Guid.NewGuid(), Quantity = 1m, LotNumber = "X", ExpiryDate = Clock.Today.AddDays(90) }))).Should().Be("Message.PermissionDenied");
        Denied(await As(SystemRoles.Accountant, (c, u, g) => Invoices(c, u, g).RecordPaymentAsync(Guid.NewGuid(),
            new RecordSupplierPaymentRequest(Clock.Today, 1m, SupplierPaymentMethod.Cash, null)))).Should().Be("Message.PermissionDenied");
    }

    private static string[] ShippedRole(string roleCode)
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var definitions = (System.Collections.IEnumerable)type
            .GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        foreach (var definition in definitions)
        {
            var tuple = definition.GetType();
            if ((string)tuple.GetField("Item1")!.GetValue(definition)! == roleCode)
                return (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        return [];
    }
}

internal static class Cycle2bNonConformityExtensions
{
    private static readonly IClock Clock = new FixedDay();

    private sealed class FixedDay : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 16);
    }

    /// <summary>The corrective action the QC scenario plans on its file.</summary>
    public static Task<Result> AddActionRequestAsync(this NonConformityService service, Guid fileId) =>
        service.AddActionAsync(new AddActionRequest(fileId, ActionKind.CorrectiveAction,
                                                    MultilingualText.FromFrench("Recalibrer et repasser les patients"),
                                                    Guid.Empty, Clock.Today.AddDays(3)));
}
