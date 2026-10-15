using System.Globalization;
using FluentAssertions;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Queries;
using MarocLIS.Tests.Persistence;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Third review, desktop UI (cycle 1 of the fixes): the contract editor keeps what the contract
/// already uses (U2), and item names follow the operator's language on the stock screens (U8).
/// </summary>
public sealed class ReviewFixesCycle1UiTests(AchatsBScratchDatabase database) : IClassFixture<AchatsBScratchDatabase>
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static readonly MultilingualText Glucose = new("Réactif glucose", "كاشف الغلوكوز", "Glucose reagent");

    // ---- U8: consumption cells ------------------------------------------------------------------------

    private static ConsumptionRow Row() =>
        new(Guid.NewGuid(), "GLU", Glucose, StockItemKind.Reagent, "coffret", ConsumptionBreakdown.None,
            new StockReconciliation(2m, 10m, 3m, 9m, 10m), 3m, null, []);

    [Fact]
    public void U8_consumption_cells_carry_the_item_name_in_the_screens_language()
    {
        StockCsv.ConsumptionCells(Row(), AppLanguage.Arabic)[1].Should().Be("كاشف الغلوكوز");
        StockCsv.ConsumptionCells(Row(), AppLanguage.English)[1].Should().Be("Glucose reagent");
    }

    [Fact]
    public void U8_consumption_cells_stay_french_by_default_and_fall_back_to_french()
    {
        StockCsv.ConsumptionCells(Row())[1].Should().Be("Réactif glucose");

        var frenchOnly = Row() with { Name = new MultilingualText("Urée") };
        StockCsv.ConsumptionCells(frenchOnly, AppLanguage.Arabic)[1].Should().Be("Urée");
    }

    // ---- U2: contract options -------------------------------------------------------------------------

    [SqlServerFact]
    public async Task U2_editing_a_contract_offers_its_inactive_supplier_decommissioned_analyser_and_inactive_reagent()
    {
        var siteId = Guid.NewGuid();

        var retired = new Supplier(Unique("S"), "Siemens") { IsActive = false };
        var active = new Supplier(Unique("A"), "Abbott");
        var analyser = new Instrument(Unique("I"), new MultilingualText("Atellica"), EquipmentKind.Analyser, siteId);
        analyser.Decommission(Today.AddDays(-30), "Fin de contrat");
        var reagent = new StockItem(Unique("R"), Glucose, StockItemKind.Reagent, "coffret", siteId) { IsActive = false };

        var contract = new InstrumentContract(Unique("CT-"), retired.Id, siteId, InstrumentContractKind.Placement,
                                              Today.AddYears(-5), 60);
        contract.SetInstruments([analyser.Id]);
        contract.SetReagents([new InstrumentContractReagentInput(reagent.Id, 120m, null)]);

        await using (var context = database.Context())
        {
            context.Suppliers.AddRange(retired, active);
            context.Instruments.Add(analyser);
            context.StockItems.Add(reagent);
            context.InstrumentContracts.Add(contract);
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new InstrumentContractQueries(read);

        // A new contract is offered only what is in service.
        var fresh = await queries.GetOptionsAsync(siteId, null);
        fresh.Suppliers.Select(s => s.Id).Should().Contain(active.Id).And.NotContain(retired.Id);
        fresh.Instruments.Should().BeEmpty();
        fresh.StockItems.Should().BeEmpty();

        // The edited contract keeps what it uses, flagged inactive, so saving it changes nothing.
        var editing = await queries.GetOptionsAsync(siteId, contract.Id);
        editing.Suppliers.Should().ContainSingle(s => s.Id == retired.Id).Which.IsActive.Should().BeFalse();
        editing.Suppliers.Single(s => s.Id == active.Id).IsActive.Should().BeTrue();
        editing.Instruments.Should().ContainSingle(i => i.Id == analyser.Id).Which.IsActive.Should().BeFalse();
        var item = editing.StockItems.Should().ContainSingle(i => i.Id == reagent.Id).Which;
        item.IsActive.Should().BeFalse();
        item.Name.For(AppLanguage.Arabic).Should().Be("كاشف الغلوكوز");
    }

    // ---- U8: count lines ------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task U8_count_lines_name_the_item_in_the_operators_language()
    {
        var siteId = Guid.NewGuid();
        var item = new StockItem(Unique("R"), Glucose, StockItemKind.Reagent, "coffret", siteId);
        var lot = item.AddLot(new ReagentLot("L1", Today.AddDays(90), 4m, Today.AddDays(-10)));

        var count = new InventoryCount(Unique("INV/"), siteId, Today, InventoryCountScope.All, Guid.NewGuid());
        count.StartCounting([new InventoryCountLineInput(item.Id, lot.Id, item.Code, "L1", lot.ExpiryDate, null, 4m)],
                            DateTimeOffset.Now).IsSuccess.Should().BeTrue();

        await using (var context = database.Context())
        {
            context.StockItems.Add(item);
            context.InventoryCounts.Add(count);
            await context.SaveChangesAsync();
        }

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-MA");
            await using var read = database.Context();
            var detail = await new InventoryCountQueries(read).GetDetailAsync(count.Id);
            detail!.Lines.Single().ItemName.Should().Be("كاشف الغلوكوز");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
