using FluentAssertions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database for the purchasing-quality queries, created from the model and dropped.</summary>
public sealed class AchatsAScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_achatsA_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public AchatsAScratchDatabase()
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
/// The purchasing-quality queries against SQL Server: the supplier evaluation's inputs (A7), the QC
/// evidence of a lot (A4), the recall list and the lot in use for a result (A6), the GTIN lookup (A2).
/// </summary>
public sealed class AchatsPurchasingQualityDatabaseTests(AchatsAScratchDatabase database)
    : IClassFixture<AchatsAScratchDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(1));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(AnalysisTest Analysis, TestParameter Parameter)> CatalogueAsync()
    {
        var department = new Department(Unique("D"), new MultilingualText("Biochimie"));
        var specimen = new SpecimenType(Unique("S"), new MultilingualText("Sérum"));
        var analysis = new AnalysisTest(Unique("GLU"), new MultilingualText("Glycémie"), department.Id, specimen.Id);
        var parameter = new TestParameter(Unique("G"), new MultilingualText("Glucose"), ResultValueType.Numeric);
        analysis.AddParameter(parameter);

        await using var context = database.Context();
        context.Departments.Add(department);
        context.SpecimenTypes.Add(specimen);
        context.AnalysisTests.Add(analysis);
        await context.SaveChangesAsync();

        return (analysis, parameter);
    }

    private static ReagentLot Released(StockItem item, string number, int receivedDaysAgo, bool open)
    {
        var lot = item.AddLot(new ReagentLot(number, Today.AddDays(200), 3m, Today.AddDays(-receivedDaysAgo)));
        lot.Release(Today.AddDays(-receivedDaysAgo), Guid.NewGuid(), "CQ conforme").IsSuccess.Should().BeTrue();
        if (open) lot.OpenedOn = Today.AddDays(-1);
        return lot;
    }

    [SqlServerFact]
    public async Task TheSupplierEvaluation_CountsDeliveriesPerOrderAndDate_RefusalsAndOpenNonConformities()
    {
        var siteId = Guid.NewGuid();
        var supplier = new Supplier(Unique("F"), "Biodis") { LeadTimeDays = 5, RegulatoryRegistrationNumber = "DMP-9" };
        var item = new StockItem(Unique("R"), new MultilingualText("Réactif"), StockItemKind.Reagent, "coffret", siteId);

        // Ordered 1 March, expected 6 March: two lots received together on the 5th count once, on time.
        var onTime = new PurchaseOrder(Unique("BC"), supplier.Id, siteId, new DateOnly(2026, 3, 1), Guid.NewGuid())
        {
            ExpectedDeliveryDate = new DateOnly(2026, 3, 6)
        };
        // Ordered 1 May with no expected date: due 6 May by the lead time, received the 10th, 4 days late.
        var late = new PurchaseOrder(Unique("BC"), supplier.Id, siteId, new DateOnly(2026, 5, 1), Guid.NewGuid());

        void Lot(string number, DateOnly on, Guid? order, string? note = null) =>
            item.AddLot(new ReagentLot(number, Today.AddYears(1), 1m, on)
            {
                SupplierId = supplier.Id, PurchaseOrderId = order, DeliveryNoteReference = note
            });

        Lot("A1", new DateOnly(2026, 3, 5), onTime.Id);
        Lot("A2", new DateOnly(2026, 3, 5), onTime.Id);
        Lot("B1", new DateOnly(2026, 5, 10), late.Id);
        Lot("C1", new DateOnly(2026, 6, 1), null, "BL-1");      // without an order: counted, not dated
        Lot("Z1", new DateOnly(2025, 12, 20), onTime.Id);       // another year

        var refusal = new RefusedDelivery(siteId, supplier.Id, null, new DateOnly(2026, 7, 1), Now, Guid.NewGuid(),
                                          "Colis chaud");
        var open = new NonConformity(Unique("NC"), siteId, NonConformityCategory.ExternalProvider,
                                     NonConformitySeverity.Major, new MultilingualText("Chaîne du froid"),
                                     new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero), Guid.NewGuid())
        { SupplierId = supplier.Id };
        var dismissed = new NonConformity(Unique("NC"), siteId, NonConformityCategory.ExternalProvider,
                                          NonConformitySeverity.Minor, new MultilingualText("Erreur de saisie"),
                                          new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero), Guid.NewGuid())
        { SupplierId = supplier.Id };
        dismissed.Dismiss(Now, Guid.NewGuid(), "Pas une non-conformité").IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.Suppliers.Add(supplier);
            context.StockItems.Add(item);
            context.PurchaseOrders.AddRange(onTime, late);
            context.Set<RefusedDelivery>().Add(refusal);
            context.NonConformities.AddRange(open, dismissed);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var input = (await new PurchasingQualityQueries(read).GetSupplierEvaluationInputsAsync(2026))
            .Single(i => i.SupplierId == supplier.Id);

        input.Deliveries.Should().HaveCount(3);
        input.Deliveries.Should().ContainEquivalentOf(new SupplierDelivery(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 6)));
        input.Deliveries.Should().ContainEquivalentOf(new SupplierDelivery(new DateOnly(2026, 5, 10), new DateOnly(2026, 5, 6)));
        input.Deliveries.Should().ContainEquivalentOf(new SupplierDelivery(new DateOnly(2026, 6, 1), null));
        input.RefusedDeliveries.Should().Be(1);
        input.NonConformities.Should().Be(1);
        input.RegistrationNumber.Should().Be("DMP-9");

        var kpis = SupplierEvaluation.Compute(input.Deliveries, input.RefusedDeliveries, input.NonConformities);
        kpis.OnTimeRatePercent.Should().Be(50m);
        kpis.AverageDelayDays.Should().Be(2m);
        // 50 × 0.5 + 30 × (1 − 1/4) + 15 = 62.5 → 63
        kpis.ProposedScore.Should().Be(63);
    }

    [SqlServerFact]
    public async Task TheLotQcEvidence_HasTheLevelsOfTheAnalysis_AndThePointsOnTheLot()
    {
        var (analysis, parameter) = await CatalogueAsync();
        var siteId = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId)
        {
            UsedByAnalysisCodesCsv = analysis.Code
        };
        var lot = item.AddLot(new ReagentLot("GLU-NEW", Today.AddDays(300), 4m, Today));
        var material = new QcMaterial(Unique("CTRL"), new MultilingualText("Contrôle bas"), QcLevel.Low, Today.AddDays(90));
        var instrument = Guid.NewGuid();
        var target = new QcTarget(material.Id, parameter.Id, instrument);
        var other = new QcResult(target.Id, parameter.Id, instrument, siteId, 5.1m, Now.AddHours(-2), Guid.NewGuid());
        var onLot = new QcResult(target.Id, parameter.Id, instrument, siteId, 5.2m, Now.AddHours(-1), Guid.NewGuid())
        {
            ReagentLotId = lot.Id
        };
        onLot.ApplyEvaluation(new WestgardEvaluation(WestgardRule.None, QcOutcome.Accepted, []));

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.QcMaterials.Add(material);
            context.QcTargets.Add(target);
            context.QcResults.AddRange(other, onLot);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var evidence = await new PurchasingQualityQueries(read).GetLotQcEvidenceAsync(lot.Id, Today);

        evidence!.AnalysisCodes.Should().Equal(analysis.Code);
        evidence.RequiredLevels.Single().QcTargetId.Should().Be(target.Id);
        evidence.RequiredLevels.Single().Label.Should().Contain("niveau bas");
        evidence.Points.Single().QcResultId.Should().Be(onLot.Id);

        LotQcGate.Evaluate(true, evidence.RequiredLevels, evidence.Points).AllowsRelease.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task TheLotInUse_IsFoundForAResult_AndTheRecallListFindsThePatient()
    {
        var (analysis, parameter) = await CatalogueAsync();
        var siteId = Guid.NewGuid();
        var item = new StockItem(Unique("R"), new MultilingualText("Glucose R1"), StockItemKind.Reagent, "coffret", siteId)
        {
            UsedByAnalysisCodesCsv = $"{analysis.Code.ToLowerInvariant()}; UREE",
            Gtin = "04006381333931"
        };
        var inUse = Released(item, "GLU-OPEN", 30, open: true);
        Released(item, "GLU-SPARE", 10, open: false);

        var patient = new Patient(Unique("P"), "ALAOUI", "Salma", new DateOnly(1980, 1, 1), Gender.Female, siteId);
        var order = new LabOrder(Unique("A"), patient.Id, siteId, Now.AddHours(-3), Guid.NewGuid());
        var orderItem = new LabOrderItem(analysis.Id, OrderPriority.Routine);
        order.AddItem(orderItem);
        var result = new TestResult(order.Id, orderItem.Id, parameter.Id, siteId) { ReagentLotId = inUse.Id };

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.Patients.Add(patient);
            context.LabOrders.Add(order);
            context.TestResults.Add(result);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();

        var lots = await new ResultQueries(read).GetReagentLotsInUseAsync([parameter.Id], siteId, Today);
        lots[parameter.Id].Should().Be(inUse.Id);

        // Another site's stock says nothing about this one.
        (await new ResultQueries(read).GetReagentLotsInUseAsync([parameter.Id], Guid.NewGuid(), Today))
            .Should().BeEmpty();

        var queries = new PurchasingQualityQueries(read);
        var recall = await queries.GetResultsProducedWithLotAsync(inUse.Id);
        recall.Single().PatientName.Should().Be("ALAOUI Salma");
        recall.Single().AccessionNumber.Should().Be(order.AccessionNumber);
        recall.Single().IsValidated.Should().BeFalse();

        (await queries.FindItemsByGtinAsync("04006381333931", siteId)).Single().StockItemId.Should().Be(item.Id);
        (await queries.GetStorageConditionAsync(item.Id)).Should().Be(StorageCondition.RoomTemperature);
    }
}
