using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
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

/// <summary>A scratch database of its own for the cycle 4 quality and stock scenarios.</summary>
public sealed class Cycle4dDatabase : IDisposable
{
    private readonly bool _created;

    public Cycle4dDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_c4d_" + Guid.NewGuid().ToString("N")[..8]
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
/// Cycle 4, lot d: the workflows of the quality and stock screens replayed through the services
/// the screens call, on SQL Server, each step read back in a fresh context — the quotation's
/// discount and VAT carried to the order and the invoice, an equipment's life between breakdown,
/// calibration and return to service, and a procedure brought into force and read by the staff.
/// </summary>
public sealed class Cycle4dQualityStockDatabaseTests(Cycle4dDatabase database) : IClassFixture<Cycle4dDatabase>
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
        ["get_UserName"] = _ => "c4d",
        ["get_FullName"] = _ => "Cycle 4d",
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

    private static EquipmentService Equipment(LisDbContext c, ICurrentUser user) =>
        new(new InstrumentRepository(c, Clock), new InventoryQueries(c), new UnitOfWork(c), Allowed, user, Clock);

    private static DocumentControlService Documents(LisDbContext c, ICurrentUser user) =>
        new(new ControlledDocumentRepository(c, Clock), new DocumentControlQueries(c),
            new UnitOfWork(c), Allowed, user, Clock);

    private async Task<T> InFreshContextAsync<T>(ICurrentUser user, Func<LisDbContext, Task<T>> step)
    {
        await using var context = Context(user);
        return await step(context);
    }

    private static string Keys(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [SqlServerFact]
    public async Task AnOfferWithDiscountAndVat_IsOrderedAtItsNetPrice_AndTheInvoiceAtThatPriceIsPayable()
    {
        var buyer = User(Guid.NewGuid());
        var manager = User(Guid.NewGuid());
        var clerk = User(Guid.NewGuid());

        var supplier = new Supplier(Unique("F"), "Labo-Diffusion") { IsApproved = true, PaymentTermsDays = 60 };
        var item = new StockItem(Unique("TUB"), MultilingualText.FromFrench("Tubes EDTA"), StockItemKind.Consumable,
                                 "boîte", Site);
        await using (var seed = Context(buyer))
        {
            seed.Suppliers.Add(supplier);
            seed.StockItems.Add(item);
            await seed.SaveChangesAsync();
        }

        var quoteId = (await InFreshContextAsync(buyer, c => Quotes(c, buyer).CreateAsync(new CreateQuoteRequest
        {
            Items = [new QuoteItemInput(item.Id, 6m)], SupplierIds = [supplier.Id]
        }))).Value;

        string orderNumber;
        await using (var c = Context(buyer))
        {
            var itemId = (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value.Items.Single().Id;

            // A delay of millions of days made choosing the offer throw on the date arithmetic.
            (await Quotes(c, buyer).RecordOfferAsync(quoteId, new RecordQuoteOfferRequest
            {
                SupplierId = supplier.Id, ReceivedOn = Clock.Today, DeliveryDelayDays = 2_000_000,
                Lines = [new SupplierQuoteLineInput(itemId, 100m, 0.07m, 0.10m)]
            })).FirstError.ResourceKey.Should().Be("Quote_DelayOutOfRange");

            var recorded = await Quotes(c, buyer).RecordOfferAsync(quoteId, new RecordQuoteOfferRequest
            {
                SupplierId = supplier.Id, ReceivedOn = Clock.Today, DeliveryDelayDays = 10,
                Lines = [new SupplierQuoteLineInput(itemId, 100m, 0.07m, 0.10m)]
            });
            recorded.IsSuccess.Should().BeTrue(Keys(recorded));
        }

        await using (var c = Context(buyer))
        {
            var detail = (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value;
            var offer = detail.Offers.Single();
            offer.TotalExcludingVat.Should().Be(540m);
            detail.Comparison.Single().Cells.Single().NetUnitPriceMad.Should().Be(90m);

            var awarded = await Quotes(c, buyer).AwardAsync(quoteId, offer.Id);
            awarded.IsSuccess.Should().BeTrue(Keys(awarded));
            orderNumber = awarded.Value;
        }

        Guid orderId, lineId;
        await using (var c = Context(buyer))
        {
            var order = await c.PurchaseOrders.Include(o => o.Lines).AsNoTracking().SingleAsync(o => o.Number == orderNumber);
            order.ExpectedDeliveryDate.Should().Be(Clock.Today.AddDays(10));
            var line = order.Lines.Single();
            (line.UnitPriceMad, line.DiscountRate, line.VatRate).Should().Be((100m, 0.10m, 0.07m));
            order.TotalExcludingVat.Should().Be(540m);
            order.TotalVat.Should().Be(37.80m);
            orderId = order.Id;
            lineId = line.Id;
        }

        await using (var c = Context(manager))
        {
            (await Orders(c, manager).ApproveAsync(orderId)).IsSuccess.Should().BeTrue();
            (await Orders(c, manager).SendAsync(orderId)).IsSuccess.Should().BeTrue();
        }

        await using (var c = Context(clerk))
        {
            var received = await Orders(c, clerk).ReceiveLineAsync(orderId, new ReceivePurchaseOrderLineRequest
            {
                LineId = lineId, Quantity = 6m, LotNumber = "E2610", ExpiryDate = Clock.Today.AddMonths(18)
            });
            received.IsSuccess.Should().BeTrue(Keys(received));

            // The lot is valued at what it cost, discount taken off.
            var lot = await c.ReagentLots.AsNoTracking().SingleAsync(l => l.StockItemId == item.Id);
            lot.UnitPriceMad.Should().Be(90m);
        }

        // The supplier bills the net price at 7 %: the order says the same, nothing to block.
        await using (var c = Context(clerk))
        {
            var entered = await Invoices(c, clerk).CreateAsync(new CreateSupplierInvoiceRequest
            {
                SupplierId = supplier.Id, InvoiceNumber = "FA-C4D-1", InvoiceDate = Clock.Today, ReceivedOn = Clock.Today,
                Lines = [new SupplierInvoiceLineRequest(lineId, 6m, 90m, 0.07m)]
            });
            entered.IsSuccess.Should().BeTrue(Keys(entered));
            entered.Value.Status.Should().Be(SupplierInvoiceStatus.ApprovedForPayment);

            var paid = await Invoices(c, manager).RecordPaymentAsync(entered.Value.InvoiceId,
                new RecordSupplierPaymentRequest(Clock.Today, 577.80m, SupplierPaymentMethod.Transfer, "VIR-C4D"));
            paid.IsSuccess.Should().BeTrue(Keys(paid));
        }

        await using (var c = Context(clerk))
        {
            var invoice = await c.SupplierInvoices.Include(i => i.Lines).AsNoTracking().SingleAsync(i => i.InvoiceNumber == "FA-C4D-1");
            invoice.TotalIncludingVat.Should().Be(577.80m);
            invoice.Status.Should().Be(SupplierInvoiceStatus.Paid);
        }
    }

    [SqlServerFact]
    public async Task AnInstrumentPastItsCalibration_ComesBackIntoServiceOnlyOnceCalibrated_AndEachStepIsTraced()
    {
        var engineer = User(Guid.NewGuid());
        var instrument = new Instrument(Unique("AUT"), MultilingualText.FromFrench("Automate de biochimie"),
                                        EquipmentKind.Analyser, Site)
        {
            CalibrationIntervalDays = 30,
            LastCalibrationOn = Clock.Today.AddDays(-45)
        };
        await using (var seed = Context(engineer))
        {
            seed.Instruments.Add(instrument);
            await seed.SaveChangesAsync();
        }

        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).ChangeStatusAsync(
            instrument.Id, EquipmentStatus.UnderMaintenance, "Panne de la pompe à réactifs"))).IsSuccess.Should().BeTrue();

        var maintained = await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordMaintenanceAsync(
            instrument.Id, MaintenanceKind.Corrective, MultilingualText.FromFrench("Remplacement de la pompe"),
            downtimeHours: 6.5m, partsReplaced: "Pompe P-12", postCheckPassed: true, externalTechnician: "Technicien fournisseur"));
        maintained.IsSuccess.Should().BeTrue(Keys(maintained));

        // Calibration lapsed: back into service is refused until it is calibrated again.
        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).ChangeStatusAsync(
            instrument.Id, EquipmentStatus.Operational, "Réparation terminée")))
            .FirstError.ResourceKey.Should().Be("Instrument_CalibrationRequiredBeforeReturn");

        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordCalibrationAsync(
            instrument.Id, null, CalibrationOutcome.Passed, null, Clock.Today.AddDays(30))))
            .FirstError.ResourceKey.Should().Be("Calibration_CalibratorLotRequired");

        var calibrated = await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordCalibrationAsync(
            instrument.Id, null, CalibrationOutcome.Passed, "CAL-2610", Clock.Today.AddDays(30), 1.02m, -0.004m, "Étalonnage après réparation"));
        calibrated.IsSuccess.Should().BeTrue(Keys(calibrated));

        var back = await InFreshContextAsync(engineer, c => Equipment(c, engineer).ChangeStatusAsync(
            instrument.Id, EquipmentStatus.Operational, "Réparation terminée"));
        back.IsSuccess.Should().BeTrue(Keys(back));

        await using (var c = Context(engineer))
        {
            var saved = await c.Instruments.Include(i => i.MaintenanceRecords).Include(i => i.Calibrations)
                               .AsNoTracking().SingleAsync(i => i.Id == instrument.Id);
            saved.Status.Should().Be(EquipmentStatus.Operational);
            saved.LastCalibrationOn.Should().Be(Clock.Today);
            saved.MaintenanceRecords.Single().DowntimeHours.Should().Be(6.5m);
            saved.Calibrations.Should().ContainSingle().Which.Slope.Should().Be(1.02m);

            var row = (await Equipment(c, engineer).GetEquipmentAsync()).Value.Single(r => r.InstrumentId == instrument.Id);
            row.CalibrationOverdue.Should().BeFalse();
            row.NextCalibrationDue.Should().Be(Clock.Today.AddDays(30));

            var reasons = await c.AuditEntries.AsNoTracking().Select(a => a.Reason).ToListAsync();
            reasons.Should().Contain(r => r != null && r.Contains(instrument.Code) && r.Contains("UnderMaintenance"));
            reasons.Should().Contain(r => r != null && r.Contains("Étalonnage Passed sur " + instrument.Code));
        }
    }

    [SqlServerFact]
    public async Task AProcedure_BroughtIntoForce_IsReadOnceByEach_AndItsNextVersionAsksToBeReadAgain()
    {
        var author = User(Guid.NewGuid());
        var reviewer = User(Guid.NewGuid());
        var manager = User(Guid.NewGuid());
        var technician = User(Guid.NewGuid());
        var reference = Unique("PR-C4D-");

        var created = await InFreshContextAsync(author, c => Documents(c, author).CreateAsync(new CreateControlledDocumentRequest
        {
            Reference = reference, Title = MultilingualText.FromFrench("Hygiène des mains"), Kind = DocumentKind.Procedure,
            RequiresAcknowledgement = true
        }));
        created.IsSuccess.Should().BeTrue(Keys(created));
        var id = created.Value;

        async Task BringIntoForce(int version, string file)
        {
            (await InFreshContextAsync(author, c => Documents(c, author).AddDraftAsync(id, null, [37, 80, 68, 70], file)))
                .IsSuccess.Should().BeTrue();
            (await InFreshContextAsync(author, c => Documents(c, author).SubmitForReviewAsync(id, version))).IsSuccess.Should().BeTrue();
            // Neither the review nor the approval is the author's to give.
            (await InFreshContextAsync(author, c => Documents(c, author).ReviewAsync(id, version)))
                .FirstError.ResourceKey.Should().Be("Document_ReviewerMustDifferFromAuthor");
            (await InFreshContextAsync(reviewer, c => Documents(c, reviewer).ReviewAsync(id, version))).IsSuccess.Should().BeTrue();
            (await InFreshContextAsync(author, c => Documents(c, author).ApproveAsync(id, version)))
                .FirstError.ResourceKey.Should().Be("Document_ApproverMustDifferFromAuthor");
            (await InFreshContextAsync(manager, c => Documents(c, manager).ApproveAsync(id, version))).IsSuccess.Should().BeTrue();
            var effective = await InFreshContextAsync(manager, c => Documents(c, manager).MakeEffectiveAsync(id, version, Clock.Today));
            effective.IsSuccess.Should().BeTrue(Keys(effective));
        }

        // Nothing in force yet: nothing to acknowledge.
        (await InFreshContextAsync(technician, c => Documents(c, technician).AcknowledgeAsync(id)))
            .FirstError.ResourceKey.Should().Be("Document_NoEffectiveVersion");

        await BringIntoForce(1, "hygiene-v1.pdf");

        // Read twice, recorded once.
        (await InFreshContextAsync(technician, c => Documents(c, technician).AcknowledgeAsync(id))).IsSuccess.Should().BeTrue();
        (await InFreshContextAsync(technician, c => Documents(c, technician).AcknowledgeAsync(id))).IsSuccess.Should().BeTrue();

        async Task<ControlledDocumentRow> RowFor(ICurrentUser user) =>
            (await InFreshContextAsync(user, c => Documents(c, user).GetRegisterAsync())).Value.Single(d => d.Id == id);

        (await RowFor(technician)).Should().Match<ControlledDocumentRow>(d => d.EffectiveVersion == 1 && d.AcknowledgedByCurrentUser);
        (await RowFor(reviewer)).AcknowledgedByCurrentUser.Should().BeFalse();

        await BringIntoForce(2, "hygiene-v2.pdf");

        // The new version replaces the old one, which stays on record, and must be read again.
        (await RowFor(technician)).Should().Match<ControlledDocumentRow>(d => d.EffectiveVersion == 2 && !d.AcknowledgedByCurrentUser);
        var versions = (await InFreshContextAsync(technician, c => Documents(c, technician).GetVersionsAsync(id))).Value;
        versions.Select(v => (v.VersionNumber, v.Status)).Should().Equal(
            (1, DocumentVersionStatus.Superseded), (2, DocumentVersionStatus.Effective));

        (await InFreshContextAsync(technician, c => Documents(c, technician).AcknowledgeAsync(id))).IsSuccess.Should().BeTrue();
        (await RowFor(technician)).AcknowledgedByCurrentUser.Should().BeTrue();

        await using (var c = Context(technician))
            (await c.Set<DocumentAcknowledgement>().AsNoTracking().CountAsync(a => a.ControlledDocumentId == id))
                .Should().Be(2);
    }
}
