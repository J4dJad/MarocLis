using System.Reflection;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Inventory;
using MarocLIS.Application.Quality;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
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
/// A scratch database of its own for the owner's decisions on quality, stock, purchasing and
/// equipment. Foreign keys are switched off: the scenarios seed only what the services read.
/// </summary>
public sealed class RecoRcDatabase : IDisposable
{
    private readonly bool _created;

    public RecoRcDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_rc_" + Guid.NewGuid().ToString("N")[..8]
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
/// The owner's decisions on the quality, stock, purchasing and equipment screens, replayed through
/// the services the screens call on SQL Server and read back in a fresh context: automatic
/// non-conformities piloted by whoever raised them, a procedure scheduled for a later date, a
/// decommissioned instrument left alone, a failed post-check taking an analyser out of service, a
/// QC reviewed once, a zero SD refused, a technician who declares but does not manage a
/// non-conformity, a quotation awarded again after its order was cancelled, and a count not dated
/// in the future.
/// </summary>
public sealed class RecoRcQualityStockDatabaseTests(RecoRcDatabase database) : IClassFixture<RecoRcDatabase>
{
    private sealed class DayClock(DateOnly day) : IClock
    {
        public DateTimeOffset Now => new(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        public DateOnly Today => day;
    }

    private static readonly Guid Site = Guid.NewGuid();
    private static readonly IClock Clock = new DayClock(new DateOnly(2026, 10, 16));

    private static ICurrentUser User(Guid userId, IReadOnlyCollection<string>? rights = null) =>
        Stub<ICurrentUser>.Create(new()
        {
            ["get_IsAuthenticated"] = _ => true,
            ["get_UserId"] = _ => userId,
            ["get_UserName"] = _ => "rc",
            ["get_FullName"] = _ => "Reco rc",
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

    private static QualityControlService Qc(LisDbContext c, ICurrentUser user) =>
        new(new Repository<QcTarget>(c, Clock), new QcResultRepository(c, Clock), new Repository<QcMaterial>(c, Clock),
            new NonConformityRepository(c, Clock), new QualityQueries(c), new SequenceAllocator(c, Clock, user),
            new UnitOfWork(c), Allowed, user, Clock);

    private static NonConformityService NonConformities(LisDbContext c, ICurrentUser user, IPermissionGuard? guard = null) =>
        new(new NonConformityRepository(c, Clock), new QualityQueries(c),
            new SequenceAllocator(c, Clock, user), new UnitOfWork(c), guard ?? Allowed, user, Clock);

    private static TemperatureMonitoringService Temperatures(LisDbContext c, ICurrentUser user) =>
        new(new Repository<StorageUnit>(c, Clock), new Repository<TemperatureReading>(c, Clock),
            new NonConformityRepository(c, Clock), new TemperatureQueries(c), new SequenceAllocator(c, Clock, user),
            new UnitOfWork(c), Allowed, user, Clock);

    private static EquipmentService Equipment(LisDbContext c, ICurrentUser user) =>
        new(new InstrumentRepository(c, Clock), new InventoryQueries(c), new UnitOfWork(c), Allowed, user, Clock);

    private static DocumentControlService Documents(LisDbContext c, ICurrentUser user, IClock? clock = null) =>
        new(new ControlledDocumentRepository(c, clock ?? Clock), new DocumentControlQueries(c),
            new UnitOfWork(c), Allowed, user, clock ?? Clock);

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

    private static InventoryCountService Counts(LisDbContext c, ICurrentUser user) =>
        new(new Repository<InventoryCount>(c, Clock), new Repository<StockMovement>(c, Clock),
            new InventoryCountQueries(c), new InventoryQueries(c), new SequenceAllocator(c, Clock, user),
            new UnitOfWork(c), Allowed, user, Clock);

    private async Task<T> InFreshContextAsync<T>(ICurrentUser user, Func<LisDbContext, Task<T>> step)
    {
        await using var context = Context(user);
        return await step(context);
    }

    private static string Keys(Result result) => string.Join(", ", result.Errors.Select(e => e.ResourceKey));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<QcTarget> SeedQcTargetAsync(ICurrentUser user)
    {
        var material = new QcMaterial(Unique("QC"), MultilingualText.FromFrench("Contrôle normal"), QcLevel.Normal,
                                      Clock.Today.AddMonths(6));
        var target = new QcTarget(material.Id, Guid.NewGuid(), Guid.NewGuid()) { AssignedMean = 100m, AssignedSd = 2m };
        await using var seed = Context(user);
        seed.QcMaterials.Add(material);
        seed.QcTargets.Add(target);
        await seed.SaveChangesAsync();
        return target;
    }

    // ---- 1 and 5: the QC file is piloted by its reviewer, and a result is reviewed once ---------------

    [SqlServerFact]
    public async Task ARejectedControl_OpensAFilePilotedByItsReviewer_AndCannotBeReviewedASecondTime()
    {
        var technician = User(Guid.NewGuid());
        var manager = User(Guid.NewGuid());
        var other = User(Guid.NewGuid());
        var target = await SeedQcTargetAsync(technician);

        var run = await InFreshContextAsync(technician, c => Qc(c, technician).SubmitAsync([new QcEntry(target.Id, 112m)]));
        run.IsSuccess.Should().BeTrue(Keys(run));
        var resultId = run.Value.Results.Single().QcResultId;

        (await InFreshContextAsync(manager, c => Qc(c, manager).ReviewAsync(resultId, "Recalibration", openNonConformity: true)))
            .IsSuccess.Should().BeTrue();

        // The second review used to replace the reviewer and the corrective action on record.
        (await InFreshContextAsync(other, c => Qc(c, other).ReviewAsync(resultId, "Autre action", openNonConformity: true)))
            .FirstError.ResourceKey.Should().Be("Qc_AlreadyReviewed");

        await using var check = Context(manager);
        var reviewed = await check.QcResults.AsNoTracking().SingleAsync(r => r.Id == resultId);
        reviewed.ReviewedByUserId.Should().Be(manager.UserId);
        reviewed.CorrectiveActionTaken.Should().Be("Recalibration");
        var file = await check.NonConformities.AsNoTracking().SingleAsync(n => n.QcResultId == resultId);
        file.AssignedToUserId.Should().Be(manager.UserId);
    }

    [SqlServerFact]
    public async Task ATemperatureExcursion_OpensAFilePilotedByWhoeverReadIt()
    {
        var manager = User(Guid.NewGuid());
        var technician = User(Guid.NewGuid());

        var unitId = (await InFreshContextAsync(manager, c => Temperatures(c, manager).SaveUnitAsync(new StorageUnitRequest
        {
            Name = Unique("Frigo "), Kind = StorageUnitKind.Refrigerator, MinimumCelsius = 2m, MaximumCelsius = 8m
        }))).Value;

        var excursion = await InFreshContextAsync(technician, c => Temperatures(c, technician).RecordReadingAsync(
            new TemperatureReadingRequest { StorageUnitId = unitId, Celsius = 12m, Comment = "Porte ouverte" }));
        excursion.IsSuccess.Should().BeTrue(Keys(excursion));

        await using var check = Context(manager);
        var file = await check.NonConformities.AsNoTracking()
                              .SingleAsync(n => n.Reference == excursion.Value.NonConformityReference);
        file.AssignedToUserId.Should().Be(technician.UserId);
    }

    // ---- 10: limits are not established on a zero SD ------------------------------------------------

    [SqlServerFact]
    public async Task EstablishingLimits_OnPointsThatAllHaveTheSameValue_IsRefused()
    {
        var manager = User(Guid.NewGuid());
        var target = await SeedQcTargetAsync(manager);

        await using (var c = Context(manager))
        {
            for (var i = 0; i < QcTarget.MinimumPointsForLaboratoryStatistics; i++)
                (await Qc(c, manager).SubmitAsync([new QcEntry(target.Id, 100m)])).IsSuccess.Should().BeTrue();
        }

        (await InFreshContextAsync(manager, c => Qc(c, manager).RecomputeStatisticsAsync(target.Id, Clock.Today, Clock.Today)))
            .FirstError.ResourceKey.Should().Be("Qc_ZeroSdRefused");

        await using var check = Context(manager);
        (await check.QcTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).LaboratorySd.Should().BeNull();
    }

    [SqlServerFact]
    public async Task AControlWithNoManufacturerValues_GetsItsLimitsFromItsOwnPoints_AndIsJudgedFromThen()
    {
        var manager = User(Guid.NewGuid());
        var material = new QcMaterial(Unique("QC"), MultilingualText.FromFrench("Contrôle interne maison"), QcLevel.Normal,
                                      Clock.Today.AddMonths(6));
        var target = new QcTarget(material.Id, Guid.NewGuid(), Guid.NewGuid());
        await using (var seed = Context(manager))
        {
            seed.QcMaterials.Add(material);
            seed.QcTargets.Add(target);
            await seed.SaveChangesAsync();
        }

        await using (var c = Context(manager))
        {
            for (var i = 0; i < QcTarget.MinimumPointsForLaboratoryStatistics; i++)
                (await Qc(c, manager).SubmitAsync([new QcEntry(target.Id, 100m + i % 5)])).IsSuccess.Should().BeTrue();
        }

        var limits = await InFreshContextAsync(manager, c => Qc(c, manager).RecomputeStatisticsAsync(target.Id, Clock.Today, Clock.Today));
        limits.IsSuccess.Should().BeTrue(Keys(limits));
        limits.Value.Points.Should().Be(QcTarget.MinimumPointsForLaboratoryStatistics);
        limits.Value.Mean.Should().Be(102m);

        var next = await InFreshContextAsync(manager, c => Qc(c, manager).SubmitAsync([new QcEntry(target.Id, 102m)]));
        next.IsSuccess.Should().BeTrue(Keys(next));
        await using var check = Context(manager);
        (await check.QcResults.AsNoTracking().SingleAsync(r => r.Id == next.Value.Results.Single().QcResultId))
            .ZScore.Should().NotBeNull("the control is judged against the limits it now has");
    }

    // ---- 6: a technician declares a non-conformity but does not manage it ----------------------------

    [SqlServerFact]
    public async Task ATechnician_DeclaresANonConformity_ButTheFileStaysWithQuality()
    {
        var user = User(Guid.NewGuid(), ShippedRole(SystemRoles.Technician));

        async Task<T> AsTechnician<T>(Func<NonConformityService, Task<T>> step)
        {
            await using var c = Context(user);
            var guard = new PermissionGuard(user, c, Clock, Stub<ISecurityAuditor>.Create());
            return await step(NonConformities(c, user, guard));
        }

        var opened = await AsTechnician(s => s.OpenAsync(new CreateNonConformityRequest
        {
            Category = NonConformityCategory.PreAnalytical, Severity = NonConformitySeverity.Minor,
            Description = MultilingualText.FromFrench("Tube hémolysé non signalé")
        }));
        opened.IsSuccess.Should().BeTrue(Keys(opened));

        Guid fileId;
        await using (var c = Context(user))
        {
            var file = await c.NonConformities.AsNoTracking().SingleAsync(n => n.Reference == opened.Value);
            file.DetectedByUserId.Should().Be(user.UserId);
            fileId = file.Id;
        }

        (await AsTechnician(s => s.CloseAsync(fileId))).FirstError.ResourceKey.Should().Be("Message.PermissionDenied");

        // The roles that manage files declare them too; the receptionist does neither.
        ShippedRole(SystemRoles.QualityManager).Should().Contain(Permissions.Quality.ReportNonConformity);
        ShippedRole(SystemRoles.Biologist).Should().Contain(Permissions.Quality.ReportNonConformity);
        ShippedRole(SystemRoles.ResponsibleBiologist).Should().Contain(Permissions.Quality.ReportNonConformity);
        ShippedRole(SystemRoles.Technician).Should().NotContain(Permissions.Quality.ManageNonConformity);
        ShippedRole(SystemRoles.Receptionist).Should().NotContain(Permissions.Quality.ReportNonConformity);
    }

    // ---- 2: a version put in force with a future date is scheduled until then -------------------------

    [SqlServerFact]
    public async Task AVersionPutInForceWithAFutureDate_IsScheduled_AndTheCurrentOneStaysInForceUntilThatDay()
    {
        var author = User(Guid.NewGuid());
        var reviewer = User(Guid.NewGuid());
        var manager = User(Guid.NewGuid());
        var technician = User(Guid.NewGuid());
        var start = Clock.Today.AddDays(10);

        var id = (await InFreshContextAsync(author, c => Documents(c, author).CreateAsync(new CreateControlledDocumentRequest
        {
            Reference = Unique("PR-RC-"), Title = MultilingualText.FromFrench("Hygiène des mains"),
            Kind = DocumentKind.Procedure, RequiresAcknowledgement = true
        }))).Value;

        async Task Approve(int version)
        {
            (await InFreshContextAsync(author, c => Documents(c, author).AddDraftAsync(id, null, [37, 80, 68, 70], $"v{version}.pdf")))
                .IsSuccess.Should().BeTrue();
            (await InFreshContextAsync(author, c => Documents(c, author).SubmitForReviewAsync(id, version))).IsSuccess.Should().BeTrue();
            (await InFreshContextAsync(reviewer, c => Documents(c, reviewer).ReviewAsync(id, version))).IsSuccess.Should().BeTrue();
            (await InFreshContextAsync(manager, c => Documents(c, manager).ApproveAsync(id, version))).IsSuccess.Should().BeTrue();
        }

        await Approve(1);
        (await InFreshContextAsync(manager, c => Documents(c, manager).MakeEffectiveAsync(id, 1, Clock.Today))).IsSuccess.Should().BeTrue();
        await Approve(2);
        var scheduled = await InFreshContextAsync(manager, c => Documents(c, manager).MakeEffectiveAsync(id, 2, start));
        scheduled.IsSuccess.Should().BeTrue(Keys(scheduled));

        async Task<(ControlledDocumentRow Row, IReadOnlyList<DocumentVersionRow> Versions)> SeenOn(DateOnly day)
        {
            var clock = new DayClock(day);
            await using var c = Context(technician);
            var row = (await Documents(c, technician, clock).GetRegisterAsync()).Value.Single(d => d.Id == id);
            var versions = (await Documents(c, technician, clock).GetVersionsAsync(id)).Value;
            return (row, versions);
        }

        // Today: v1 is still the one in force, v2 is announced as scheduled.
        var today = await SeenOn(Clock.Today);
        today.Row.EffectiveVersion.Should().Be(1);
        today.Row.LatestStatus.Should().Be(DocumentVersionStatus.Scheduled);
        today.Versions.Select(v => (v.VersionNumber, v.Status)).Should().Equal(
            (1, DocumentVersionStatus.Effective), (2, DocumentVersionStatus.Scheduled));

        // Read today, it is v1 that is acknowledged.
        (await InFreshContextAsync(technician, c => Documents(c, technician).AcknowledgeAsync(id))).IsSuccess.Should().BeTrue();
        await using (var c = Context(technician))
            (await c.Set<DocumentAcknowledgement>().AsNoTracking().SingleAsync(a => a.ControlledDocumentId == id))
                .VersionNumber.Should().Be(1);

        // The day before, still v1; on the day, v2 is in force and v1 superseded.
        (await SeenOn(start.AddDays(-1))).Row.EffectiveVersion.Should().Be(1);
        var then = await SeenOn(start);
        then.Row.EffectiveVersion.Should().Be(2);
        then.Row.AcknowledgedByCurrentUser.Should().BeFalse();
        then.Versions.Select(v => (v.VersionNumber, v.Status)).Should().Equal(
            (1, DocumentVersionStatus.Superseded), (2, DocumentVersionStatus.Effective));
    }

    // ---- 3, 15, 16: equipment ------------------------------------------------------------------------

    private async Task<Instrument> SeedInstrumentAsync(ICurrentUser user)
    {
        var instrument = new Instrument(Unique("AUT"), MultilingualText.FromFrench("Automate"), EquipmentKind.Analyser, Site)
        {
            CalibrationIntervalDays = 30,
            LastCalibrationOn = Clock.Today.AddDays(-5)
        };
        await using var seed = Context(user);
        seed.Instruments.Add(instrument);
        await seed.SaveChangesAsync();
        return instrument;
    }

    [SqlServerFact]
    public async Task ADecommissionedInstrument_StaysDecommissioned_AndTakesNoMoreInterventions()
    {
        var engineer = User(Guid.NewGuid());
        var instrument = await SeedInstrumentAsync(engineer);

        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).ChangeStatusAsync(
            instrument.Id, EquipmentStatus.Decommissioned, "Remplacé par le nouvel automate"))).IsSuccess.Should().BeTrue();

        foreach (var status in new[] { EquipmentStatus.Operational, EquipmentStatus.OutOfService, EquipmentStatus.UnderMaintenance })
            (await InFreshContextAsync(engineer, c => Equipment(c, engineer).ChangeStatusAsync(instrument.Id, status, "Remise en route")))
                .FirstError.ResourceKey.Should().Be("Equipment_DecommissionedIsFinal");

        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordMaintenanceAsync(
            instrument.Id, MaintenanceKind.Corrective, MultilingualText.FromFrench("Réparation"))))
            .FirstError.ResourceKey.Should().Be("Equipment_DecommissionedNoIntervention");
        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordCalibrationAsync(
            instrument.Id, null, CalibrationOutcome.Passed, "CAL-1", null)))
            .FirstError.ResourceKey.Should().Be("Equipment_DecommissionedNoIntervention");

        await using var check = Context(engineer);
        var saved = await check.Instruments.Include(i => i.MaintenanceRecords).Include(i => i.Calibrations)
                               .AsNoTracking().SingleAsync(i => i.Id == instrument.Id);
        saved.Status.Should().Be(EquipmentStatus.Decommissioned);
        saved.MaintenanceRecords.Should().BeEmpty();
        saved.Calibrations.Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task ACalibrationPassedWithReservation_NeedsItsCalibratorLot()
    {
        var engineer = User(Guid.NewGuid());
        var instrument = await SeedInstrumentAsync(engineer);

        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordCalibrationAsync(
            instrument.Id, null, CalibrationOutcome.PassedWithReservation, "  ", null)))
            .FirstError.ResourceKey.Should().Be("Calibration_CalibratorLotRequired");

        var recorded = await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordCalibrationAsync(
            instrument.Id, null, CalibrationOutcome.PassedWithReservation, "CAL-2610", null, comment: "Pente limite"));
        recorded.IsSuccess.Should().BeTrue(Keys(recorded));

        // A failed calibration still needs no lot.
        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordCalibrationAsync(
            instrument.Id, null, CalibrationOutcome.Failed, null, null))).IsSuccess.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task AFailedPostCheck_OnAnOperationalInstrument_TakesItOutOfService()
    {
        var engineer = User(Guid.NewGuid());
        var operational = await SeedInstrumentAsync(engineer);
        var underMaintenance = await SeedInstrumentAsync(engineer);
        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).ChangeStatusAsync(
            underMaintenance.Id, EquipmentStatus.UnderMaintenance, "Panne de la pompe"))).IsSuccess.Should().BeTrue();

        foreach (var id in new[] { operational.Id, underMaintenance.Id })
            (await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordMaintenanceAsync(
                id, MaintenanceKind.Corrective, MultilingualText.FromFrench("Remplacement de la pompe"),
                postCheckPassed: false))).IsSuccess.Should().BeTrue();

        // A passing check changes nothing.
        var passing = await SeedInstrumentAsync(engineer);
        (await InFreshContextAsync(engineer, c => Equipment(c, engineer).RecordMaintenanceAsync(
            passing.Id, MaintenanceKind.Routine, MultilingualText.FromFrench("Nettoyage"), postCheckPassed: true)))
            .IsSuccess.Should().BeTrue();

        await using var check = Context(engineer);
        (await check.Instruments.AsNoTracking().SingleAsync(i => i.Id == operational.Id)).Status
            .Should().Be(EquipmentStatus.OutOfService);
        (await check.Instruments.AsNoTracking().SingleAsync(i => i.Id == underMaintenance.Id)).Status
            .Should().Be(EquipmentStatus.UnderMaintenance);
        (await check.Instruments.AsNoTracking().SingleAsync(i => i.Id == passing.Id)).Status
            .Should().Be(EquipmentStatus.Operational);
        (await check.AuditEntries.AsNoTracking().Select(a => a.Reason).ToListAsync())
            .Should().Contain(r => r != null && r.Contains(operational.Code) && r.Contains("mis hors service"));

        // The non-conformity the screen then offers is opened on the instrument.
        var opened = await InFreshContextAsync(engineer, c => NonConformities(c, engineer).OpenAsync(new CreateNonConformityRequest
        {
            Category = NonConformityCategory.Equipment, Severity = NonConformitySeverity.Major,
            Description = MultilingualText.FromFrench($"Contrôle après intervention non conforme — {operational.Code}"),
            InstrumentId = operational.Id
        }));
        opened.IsSuccess.Should().BeTrue(Keys(opened));
        (await check.NonConformities.AsNoTracking().SingleAsync(n => n.Reference == opened.Value)).InstrumentId
            .Should().Be(operational.Id);
    }

    // ---- 7, 8, 17: purchasing ------------------------------------------------------------------------

    [SqlServerFact]
    public async Task AQuoteWhoseOrderWasCancelled_IsAwardedAgain_AndTheCancelReasonLeavesTheNotesAlone()
    {
        var buyer = User(Guid.NewGuid());
        var supplier = new Supplier(Unique("F"), "Labo-Diffusion") { IsApproved = true, PaymentTermsDays = 60 };
        var item = new StockItem(Unique("TUB"), MultilingualText.FromFrench("Tubes EDTA"), StockItemKind.Consumable, "boîte", Site);
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

        Guid offerId;
        await using (var c = Context(buyer))
        {
            var itemId = (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value.Items.Single().Id;
            (await Quotes(c, buyer).RecordOfferAsync(quoteId, new RecordQuoteOfferRequest
            {
                SupplierId = supplier.Id, ReceivedOn = Clock.Today, DeliveryDelayDays = 10,
                Lines = [new SupplierQuoteLineInput(itemId, 100m, 0.20m, 0m)]
            })).IsSuccess.Should().BeTrue();
            offerId = (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value.Offers.Single().Id;
        }

        var first = await InFreshContextAsync(buyer, c => Quotes(c, buyer).AwardAsync(quoteId, offerId));
        first.IsSuccess.Should().BeTrue(Keys(first));

        // Awarded and its order alive: not awarded a second time.
        (await InFreshContextAsync(buyer, c => Quotes(c, buyer).AwardAsync(quoteId, offerId)))
            .FirstError.ResourceKey.Should().Be("Quote_NotOpen");
        (await InFreshContextAsync(buyer, c => Quotes(c, buyer).GetDetailAsync(quoteId))).Value.OrderCancelled.Should().BeFalse();

        Guid firstOrderId;
        await using (var c = Context(buyer))
            firstOrderId = (await c.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Number == first.Value)).Id;

        (await InFreshContextAsync(buyer, c => Orders(c, buyer).CancelAsync(firstOrderId, "Prix révisé par le fournisseur")))
            .IsSuccess.Should().BeTrue();

        await using (var c = Context(buyer))
        {
            var cancelled = await c.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == firstOrderId);
            cancelled.Notes.Should().Be($"Offre retenue sur la demande de devis {(await Quotes(c, buyer).GetDetailAsync(quoteId)).Value.Number}");
            cancelled.CancellationReason.Should().Be("Prix révisé par le fournisseur");
            (await Orders(c, buyer).GetOrdersAsync(onlyOpen: false)).Value.Single(o => o.Id == firstOrderId)
                .CancellationReason.Should().Be("Prix révisé par le fournisseur");
            (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value.OrderCancelled.Should().BeTrue();
        }

        var second = await InFreshContextAsync(buyer, c => Quotes(c, buyer).AwardAsync(quoteId, offerId));
        second.IsSuccess.Should().BeTrue(Keys(second));
        second.Value.Should().NotBe(first.Value);

        await using (var c = Context(buyer))
        {
            var quote = await c.QuoteRequests.AsNoTracking().SingleAsync(q => q.Id == quoteId);
            quote.Status.Should().Be(QuoteRequestStatus.Awarded);
            var order = await c.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Number == second.Value);
            quote.PurchaseOrderId.Should().Be(order.Id);
            order.Status.Should().Be(PurchaseOrderStatus.Draft);

            // The register row says who raised the order, so the screen greys « Approuver » for them.
            (await Orders(c, buyer).GetOrdersAsync(onlyOpen: true)).Value.Single(o => o.Id == order.Id)
                .RaisedByUserId.Should().Be(buyer.UserId);
            (await Quotes(c, buyer).GetDetailAsync(quoteId)).Value.OrderCancelled.Should().BeFalse();
        }

        (await InFreshContextAsync(buyer, c => Orders(c, buyer).ApproveAsync(
            c.PurchaseOrders.AsNoTracking().Single(o => o.Number == second.Value).Id)))
            .FirstError.ResourceKey.Should().Be("Po_ApproverMustDifferFromRequester");
    }

    // ---- 4: purchasing parameters above 100 % --------------------------------------------------------

    [SqlServerFact]
    public async Task PurchasingRatesAbove100Percent_AreRefusedAndNothingIsSaved()
    {
        var admin = User(Guid.NewGuid());
        var current = (await InFreshContextAsync(admin, c => Invoices(c, admin).GetSettingsAsync())).Value;

        foreach (var wrong in new[]
                 {
                     current with { KeyRatePercent = 150m }, current with { PenaltyMarginPoints = 100.5m },
                     current with { FineFirstMonthPercent = 101m }, current with { PriceTolerancePercent = 250m }
                 })
            (await InFreshContextAsync(admin, c => Invoices(c, admin).SaveSettingsAsync(wrong)))
                .FirstError.ResourceKey.Should().Be("Purchasing_SettingOutOfRange");

        (await InFreshContextAsync(admin, c => Invoices(c, admin).GetSettingsAsync())).Value.KeyRatePercent
            .Should().Be(current.KeyRatePercent);
    }

    // ---- 18: a count is not dated in the future ------------------------------------------------------

    [SqlServerFact]
    public async Task AnInventoryCount_DatedInTheFuture_IsRefused()
    {
        var manager = User(Guid.NewGuid());

        (await InFreshContextAsync(manager, c => Counts(c, manager).CreateAsync(
            new CreateInventoryCountRequest { CountDate = Clock.Today.AddDays(1) })))
            .FirstError.ResourceKey.Should().Be("InventoryCount_DateInFuture");

        var today = await InFreshContextAsync(manager, c => Counts(c, manager).CreateAsync(
            new CreateInventoryCountRequest { CountDate = Clock.Today }));
        today.IsSuccess.Should().BeTrue(Keys(today));
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
