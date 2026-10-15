using FluentAssertions;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// A scratch database of its own for purchasing lot C, created from the model and dropped at the end.
/// Foreign keys are switched off so a result can be written without its whole order.
/// </summary>
public sealed class AchatsCommandesScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_AchatsC_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public AchatsCommandesScratchDatabase()
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
/// The purchasing queries against SQL Server: the contract dashboard's counts (tests produced on the
/// contract's analysers, captive reagents bought), the captive price offered on a purchase-order line,
/// a request for quotation whose answer is replaced, and the purchase order as printed.
/// </summary>
public sealed class AchatsCommandesDatabaseTests(AchatsCommandesScratchDatabase database)
    : IClassFixture<AchatsCommandesScratchDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly TimeSpan Morocco = TimeSpan.FromHours(1);

    private sealed record Arranged(Guid Site, Supplier Supplier, StockItem Reagent, Instrument Analyser,
                                   Instrument Other, InstrumentContract Contract);

    private async Task<Arranged> ArrangeAsync(CommitmentBasis basis, decimal committed)
    {
        var site = Guid.NewGuid();
        await using var db = database.Context();

        var supplier = new Supplier("SUP" + Guid.NewGuid().ToString("N")[..6], "Bio Maroc")
        {
            IsApproved = true,
            Email = "commandes@biomaroc.ma",
            Ice = "002345678000012"
        };
        var reagent = new StockItem("R" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Réactif TSH", "", ""),
                                    StockItemKind.Reagent, "coffret", site)
        {
            SupplierCatalogueReference = "BM-TSH-100"
        };
        var analyser = new Instrument("A" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Cobas", "", ""),
                                      EquipmentKind.Analyser, site);
        var other = new Instrument("B" + Guid.NewGuid().ToString("N")[..6], new MultilingualText("Autre", "", ""),
                                   EquipmentKind.Analyser, site);

        analyser.AddMaintenance(new MaintenanceRecord(MaintenanceKind.Corrective, new DateOnly(2026, 6, 2),
                                                      new MultilingualText("Panne pipeteur", "", ""))
        {
            DowntimeHours = 6m,
            RequestedAt = new DateTimeOffset(2026, 6, 2, 8, 0, 0, Morocco),
            ArrivedAt = new DateTimeOffset(2026, 6, 2, 16, 0, 0, Morocco)
        });

        var contract = new InstrumentContract("PLAC-" + Guid.NewGuid().ToString("N")[..6], supplier.Id, site,
                                              InstrumentContractKind.Placement, new DateOnly(2025, 1, 1), 60)
        {
            CommitmentBasis = basis,
            CommittedVolume = committed,
            MaxInterventionDelayHours = 4
        };
        contract.SetInstruments([analyser.Id]);
        contract.SetReagents([new InstrumentContractReagentInput(reagent.Id, 980m, 9.8m)]);

        db.AddRange(supplier, reagent, analyser, other, contract);
        await db.SaveChangesAsync();

        return new Arranged(site, supplier, reagent, analyser, other, contract);
    }

    private static TestResult Result(Guid site, Guid orderItem, Guid instrument, DateTimeOffset at, bool entered = true)
    {
        var result = new TestResult(Guid.NewGuid(), orderItem, Guid.NewGuid(), site) { InstrumentId = instrument, MeasuredAt = at };
        if (entered) result.EnterText("0,8", at, Guid.NewGuid(), fromInstrument: true);
        return result;
    }

    [SqlServerFact]
    public async Task Dashboard_CountsOneTestPerOrderedAnalysis_OnTheContractsAnalysersOnly()
    {
        var a = await ArrangeAsync(CommitmentBasis.TestsPerYear, 1_000m);
        var at = new DateTimeOffset(2026, 9, 1, 10, 0, 0, Morocco);
        var panel = Guid.NewGuid();

        await using (var db = database.Context())
        {
            db.AddRange(
                // Two parameters of one ordered analysis: one test.
                Result(a.Site, panel, a.Analyser.Id, at),
                Result(a.Site, panel, a.Analyser.Id, at),
                Result(a.Site, Guid.NewGuid(), a.Analyser.Id, at.AddDays(1)),
                // Not counted: never measured, another analyser, before the contract year.
                Result(a.Site, Guid.NewGuid(), a.Analyser.Id, at, entered: false),
                Result(a.Site, Guid.NewGuid(), a.Other.Id, at),
                Result(a.Site, Guid.NewGuid(), a.Analyser.Id, new DateTimeOffset(2025, 12, 20, 10, 0, 0, Morocco)));
            await db.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new InstrumentContractQueries(read);

        var row = (await queries.GetContractsAsync(a.Site, Today, includeInactive: false)).Single();

        row.Commitment.PeriodStart.Should().Be(new DateOnly(2026, 1, 1));
        row.Commitment.Consumed.Should().Be(2m);
        row.Commitment.AtRisk.Should().BeTrue();
        row.Interventions.Should().Be(1);
        row.DowntimeHours.Should().Be(6m);
        row.DelaysExceeded.Should().Be(1);

        // Restricted to analyses the contract lists: none of these results' parameters match.
        (await queries.CountTestsAsync([a.Analyser.Id], ["TSH"], new DateOnly(2026, 1, 1), Today)).Should().Be(0);

        var intervention = (await queries.GetInterventionsAsync(a.Contract.Id)).Single();
        intervention.DelayHours.Should().Be(8m);
        intervention.DelayExceeded.Should().BeTrue();
    }

    [SqlServerFact]
    public async Task Dashboard_CountsTheCaptiveReagentsBought_OnApprovedOrdersOnly()
    {
        var a = await ArrangeAsync(CommitmentBasis.PurchaseAmountPerYear, 120_000m);

        await using (var db = database.Context())
        {
            var approved = new PurchaseOrder("BC/T/" + Guid.NewGuid().ToString("N")[..8], a.Supplier.Id, a.Site,
                                             new DateOnly(2026, 3, 10), Guid.NewGuid());
            approved.AddLine(new PurchaseOrderLine(a.Reagent.Id, 10m, 980m) { DiscountRate = 0.05m });
            approved.AddLine(new PurchaseOrderLine(Guid.NewGuid(), 1m, 5_000m));
            approved.Approve(DateTimeOffset.Now, Guid.NewGuid());

            var draft = new PurchaseOrder("BC/T/" + Guid.NewGuid().ToString("N")[..8], a.Supplier.Id, a.Site,
                                          new DateOnly(2026, 4, 10), Guid.NewGuid());
            draft.AddLine(new PurchaseOrderLine(a.Reagent.Id, 50m, 980m));

            db.AddRange(approved, draft);
            await db.SaveChangesAsync();
        }

        await using var read = database.Context();
        var row = (await new InstrumentContractQueries(read).GetContractsAsync(a.Site, Today, false)).Single();

        row.Commitment.Consumed.Should().Be(9_310m);
    }

    [SqlServerFact]
    public async Task CaptiveReagent_IsOfferedAtTheContractPrice_AndKeptOutOfQuotation()
    {
        var a = await ArrangeAsync(CommitmentBasis.None, 0m);

        await using var read = database.Context();

        var orderable = (await new PurchaseOrderQueries(read).GetOrderableItemsAsync(a.Site)).Single(i => i.Id == a.Reagent.Id);
        orderable.ContractPriceMad.Should().Be(980m);
        orderable.ContractSupplierId.Should().Be(a.Supplier.Id);

        var quotable = (await new QuoteQueries(read).GetQuotableItemsAsync(a.Site, Today)).Single(i => i.Id == a.Reagent.Id);
        quotable.ContractReference.Should().Be(a.Contract.Reference);
    }

    [SqlServerFact]
    public async Task QuoteRequest_WithAReplacedAnswer_ReadsBackOneOfferPerSupplier()
    {
        var a = await ArrangeAsync(CommitmentBasis.None, 0m);
        var second = new Supplier("SUP" + Guid.NewGuid().ToString("N")[..6], "Labo Distribution") { IsApproved = true };
        var quote = new QuoteRequest("DP/T/" + Guid.NewGuid().ToString("N")[..8], a.Site, Today, Guid.NewGuid());
        quote.AddItem(a.Reagent.Id, 5m, "Réactif TSH");
        quote.AddSupplier(a.Supplier.Id);
        quote.AddSupplier(second.Id);
        var item = quote.Items.Single().Id;

        await using (var db = database.Context())
        {
            quote.RecordOffer(a.Supplier.Id, Today, [new(item, 1_000m)], deliveryDelayDays: 10);
            db.AddRange(second, quote);
            await db.SaveChangesAsync();
        }

        await using (var db = database.Context())
        {
            var tracked = await db.QuoteRequests.SingleAsync(q => q.Id == quote.Id);
            tracked.RecordOffer(a.Supplier.Id, Today, [new(item, 950m)], deliveryDelayDays: 7).IsSuccess.Should().BeTrue();
            tracked.RecordOffer(second.Id, Today, [new(item, 990m)], deliveryDelayDays: 2).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        await using var read = database.Context();
        var detail = (await new QuoteQueries(read).GetDetailAsync(quote.Id, Today))!;

        detail.Offers.Should().HaveCount(2);
        detail.Offers.First().TotalExcludingVat.Should().Be(4_750m);
        var cells = detail.Comparison.Single().Cells;
        cells.Single(c => c.SupplierId == a.Supplier.Id).IsCheapest.Should().BeTrue();
        cells.Single(c => c.SupplierId == second.Id).IsFastest.Should().BeTrue();
        (await read.Set<SupplierQuoteLine>().CountAsync(l => detail.Offers.Select(o => o.Id).Contains(l.SupplierQuoteId)))
            .Should().Be(2);
    }

    [SqlServerFact]
    public async Task PurchaseOrderDocument_PrintsTheSuppliersReference_AndItsIdentity()
    {
        var a = await ArrangeAsync(CommitmentBasis.None, 0m);
        var order = new PurchaseOrder("BC/T/" + Guid.NewGuid().ToString("N")[..8], a.Supplier.Id, a.Site, Today,
                                      Guid.NewGuid());
        order.AddLine(new PurchaseOrderLine(a.Reagent.Id, 2m, 980m) { VatRate = 0.20m });
        order.Approve(DateTimeOffset.Now, Guid.NewGuid());

        await using (var db = database.Context())
        {
            db.Add(order);
            await db.SaveChangesAsync();
        }

        await using var read = database.Context();
        var document = (await new PurchaseOrderDocumentQueries(read).GetDocumentAsync(order.Id))!;

        document.Supplier.Ice.Should().Be("002345678000012");
        document.Supplier.Email.Should().Be("commandes@biomaroc.ma");
        document.Lines.Single().Reference.Should().Be("BM-TSH-100");
        document.Lines.Single().Designation.Should().Be("Réactif TSH");
        document.TotalIncludingVat.Should().Be(2_352m);
    }
}
