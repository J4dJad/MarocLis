using Bunit;
using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Quality;
using MarocLIS.Reporting;
using MarocLIS.Web.Components.Screens.Stock;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Rules = MarocLIS.Tests.Desktop.StockScreensRulesTests;

namespace MarocLIS.Tests.Web;

/// <summary>
/// « Consommation », « Inventaires » and « Températures » in the browser: the consumption with its
/// flagged lines, its breakdown and its export, a period that ends before it starts refused; a
/// count's sheet typed line by line, a figure that is no number refused on its line, a count opened
/// with a place required; the units with an excursion in the alarm's colours, a reading out of range
/// that asks for its comment before it is sent and then opens its non-conformity — each kept by the
/// right the workstation asks, through the workstation's services.
/// </summary>
public sealed class WebStockScreensTests : WebScreenContext
{
    private readonly Fake<IUnitOfWork> _work = Fake<IUnitOfWork>.Create();
    private readonly Fake<ISequenceAllocator> _sequences = Fake<ISequenceAllocator>.Create();

    public WebStockScreensTests()
    {
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(_sequences.Object);
        Services.AddSingleton<IListPdfRenderer>(new PdfStub());
    }

    private string? AuditReason => _work.Calls.LastOrDefault(c => c.Method == "set_AuditReason").Arguments is [string reason] ? reason : null;

    private sealed class PdfStub : IListPdfRenderer
    {
        public ListDocument? Last { get; private set; }

        public byte[] Render(ListDocument document)
        {
            Last = document;
            return "%PDF-1.7"u8.ToArray();
        }
    }

    // ---- Consumption -------------------------------------------------------------------------------

    private Fake<IStockPlanningQueries> Planning()
    {
        var queries = Fake<IStockPlanningQueries>.Create();
        Services.AddSingleton(queries.Object);
        Services.AddSingleton(Fake<IRepository<StockItem>>.Create().Object);
        Services.AddSingleton(Fake<IConfigurationWriter>.Create().Object);
        Services.AddScoped<StockPlanningService>();

        var glucose = new ItemConsumptionData(Guid.NewGuid(), "BIO-GLU", new MultilingualText("Glucose GOD-PAP 4×100 mL", "غلوكوز"),
                                              StockItemKind.Reagent, "coffret", 100, new StockConsumptionPolicy(), ["GLU"],
                                              [new AnalysisActivity("GLU", 380, 40, 10, 12)], 8m, 6m, 4m, 2m, "INV-2026-0003",
                                              7, 2m, 5m, 2m);
        var gloves = new ItemConsumptionData(Guid.NewGuid(), "GANT-M", new MultilingualText("Gants nitrile M"), StockItemKind.Consumable,
                                             "boîte", null, new StockConsumptionPolicy(), [], [], 3m, 5m, 2m, 4m, null, 5, 1m, 2m, 4m);
        queries.On(nameof(IStockPlanningQueries.GetConsumptionDataAsync), _ => new[] { glucose, gloves });
        return queries;
    }

    [Fact]
    public void TheConsumption_FlagsALineBeyondTheThreshold_AndOpensItsBreakdown()
    {
        var queries = Planning();
        SignIn(AppLanguage.French, Permissions.Inventory.View);

        var page = Render<ConsumptionScreen>();

        var args = queries.Calls.Single(c => c.Method == nameof(IStockPlanningQueries.GetConsumptionDataAsync)).Arguments;
        args[1].Should().Be(new DateOnly(2026, 9, 1), "the month before this one is the one reviewed");
        page.Find("[data-row='BIO-GLU']").ClassName.Should().Contain("stock-alarm");
        page.Find("[data-row='GANT-M']").ClassName.Should().Contain("is-tone-muted", "the activity cannot explain it");
        page.Find("[data-state=notice]").TextContent.Should().Contain("dépassent le seuil");
        page.FindAll("[data-action='Stock.Settings']").Should().BeEmpty("the thresholds are the administrator's");

        page.Find("[data-row='BIO-GLU']").DoubleClick();
        page.Find("[data-modal=consumption-detail]").TextContent.Should().Contain("Résultats patients").And.Contain("GLU");

        page.Find("[data-modal=consumption-detail] [data-action='Action.Close']").Click();
        page.Find("[data-action='Consumption.Csv']").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                    && (string)i.Arguments[0]! == "consommation-20260901-20260930.csv");
    }

    [Fact]
    public void APeriodEndingBeforeItStarts_IsRefused_AndNothingComputed()
    {
        var queries = Planning();
        SignIn(AppLanguage.French, Permissions.Inventory.View);

        var page = Render<ConsumptionScreen>();
        page.Find("[data-field=period-to]").Change("2026-08-15");

        page.Find("[data-refusal=period-to]").TextContent.Should().Contain("date de fin");
        queries.CallsTo(nameof(IStockPlanningQueries.GetConsumptionDataAsync)).Should().Be(1);
    }

    [Fact]
    public void TheStockSettings_AreRefusedOnTheBoxOutOfRange()
    {
        Planning();
        SignIn(AppLanguage.French, Permissions.Inventory.View, Permissions.Administration.ManageSettings);

        var page = Render<ConsumptionScreen>();
        page.Find("[data-action='Stock.Settings']").Click();
        page.Find($"[data-field={ConsumptionScreenRules.WindowField}]").Change("3");
        page.Find("[data-modal=stock-settings] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={ConsumptionScreenRules.WindowField}]").Should().NotBeNull();
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    // ---- Inventory counts --------------------------------------------------------------------------

    private (InventoryCount Count, Fake<IInventoryCountQueries> Queries) Counting()
    {
        var queries = Fake<IInventoryCountQueries>.Create();
        var repository = Fake<IRepository<InventoryCount>>.Create();
        Services.AddSingleton(queries.Object);
        Services.AddSingleton(repository.Object);
        Services.AddSingleton(Fake<IRepository<StockMovement>>.Create().Object);
        Services.AddSingleton(Fake<IInventoryQueries>.Create().Object);
        Services.AddScoped<InventoryCountService>();

        var count = new InventoryCount("INV-2026-0004", Guid.NewGuid(), TestClock.Today, InventoryCountScope.All, Guid.NewGuid());
        count.StartCounting(
        [
            new InventoryCountLineInput(Guid.NewGuid(), Guid.NewGuid(), "BIO-GLU", "GLU-2611", TestClock.Today.AddDays(140), "Réfrigérateur 1", 4m),
            new InventoryCountLineInput(Guid.NewGuid(), Guid.NewGuid(), "TUB-EDTA", "ED-5521", TestClock.Today.AddDays(400), "Réserve", 10m)
        ], TestClock.Now.AddHours(-1));

        IReadOnlyList<InventoryCountLineRow> Lines() =>
        [
            .. count.Lines.Select(l => new InventoryCountLineRow(l.Id, l.LineNumber, l.StockItemId, l.ItemCode, l.ItemCode, "coffret",
                                                                 l.ReagentLotId, l.LotNumber, l.ExpiryDate, l.StorageLocation, l.ExpectedQuantity,
                                                                 l.CountedQuantity, l.CountedQuantity is { } c ? c - l.ExpectedQuantity : null,
                                                                 l.Comment))
        ];
        InventoryCountRow Row() => new(count.Id, count.Number, count.CountDate, count.Scope, null, null, count.Status,
                                       count.Lines.Count, count.Lines.Count(l => l.CountedQuantity is not null), 0, null);

        queries.On(nameof(IInventoryCountQueries.GetCountsAsync), _ => new[] { Row(), Rules.Count("INV-2026-0003", InventoryCountStatus.Closed) });
        queries.On(nameof(IInventoryCountQueries.GetDetailAsync), _ => new InventoryCountDetail(Row(), Lines()));
        repository.On(nameof(IRepository<InventoryCount>.GetByIdAsync), _ => count);
        return (count, queries);
    }

    [Fact]
    public void ASheet_IsCountedLineByLine_AndAFigureThatIsNoNumberRefusedOnItsLine()
    {
        var (count, _) = Counting();
        SignIn(AppLanguage.French, Permissions.Inventory.Receive);

        var page = Render<InventoryCounts>();

        page.Find("[data-row='INV-2026-0004']").ClassName.Should().Contain("is-selected");
        page.FindAll("[data-grid=count-lines] tbody tr").Should().HaveCount(2);
        page.FindAll("[data-action='InventoryCount.New']").Should().BeEmpty("opening a count is the manager's");
        page.FindAll("[data-action='InventoryCount.Close']").Should().BeEmpty();

        var first = count.Lines.Single(l => l.LotNumber == "GLU-2611");
        var field = InventoryCountScreenRules.CountedField(first.Id);
        page.Find($"[data-field={field}]").Change("trois");
        page.Find($"[data-refusal={field}]").TextContent.Should().Contain("trois");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);

        page.Find($"[data-field={field}]").Change("3");
        first.CountedQuantity.Should().Be(3m);
        AuditReason.Should().Be("Comptage INV-2026-0004 : lot GLU-2611 (BIO-GLU) = 3");
        page.FindAll($"[data-refusal={field}]").Should().BeEmpty();
        page.Find("[data-grid=count-lines] [data-row='1']").ClassName.Should().Contain("is-tone-critical", "3 counted against 4 expected");
    }

    [Fact]
    public void ACountOnAPlace_TakesThePlace_AndIsRefusedOnItsBoxWithout()
    {
        var (_, queries) = Counting();
        SignIn(AppLanguage.French, Permissions.Inventory.Receive, Permissions.Inventory.CloseCounts);
        queries.On(nameof(IInventoryCountQueries.GetStorageLocationsAsync), _ => new[] { "Réfrigérateur 1", "Réserve" });

        var page = Render<InventoryCounts>();
        page.Find("[data-action='InventoryCount.New']").Click();
        page.Find($"[data-field={InventoryCountScreenRules.LocationField}]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-field=count-scope]").Change(nameof(InventoryCountScope.StorageLocation));
        page.Find("[data-modal=count-editor] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={InventoryCountScreenRules.LocationField}]").TextContent.Should().Contain("emplacement");
        page.FindAll("#count-locations option").Should().HaveCount(2);
        _sequences.CallsTo(nameof(ISequenceAllocator.NextAsync)).Should().Be(0);
    }

    // ---- Temperatures ------------------------------------------------------------------------------

    private (StorageUnit Unit, Fake<IRepository<NonConformity>> NonConformities) Units()
    {
        var queries = Fake<ITemperatureQueries>.Create();
        var units = Fake<IRepository<StorageUnit>>.Create();
        var nonConformities = Fake<IRepository<NonConformity>>.Create();
        Services.AddSingleton(queries.Object);
        Services.AddSingleton(units.Object);
        Services.AddSingleton(Fake<IRepository<TemperatureReading>>.Create().Object);
        Services.AddSingleton(nonConformities.Object);
        Services.AddScoped<TemperatureMonitoringService>();

        var fridge = new StorageUnit("Réfrigérateur réactifs", StorageUnitKind.Refrigerator, Guid.NewGuid());
        var row = Rules.Unit(today: 0) with { StorageUnitId = fridge.Id };
        queries.On(nameof(ITemperatureQueries.GetUnitsAsync), _ => new[] { row, Rules.Unit("Congélateur sérothèque", outOfRange: true, last: -12m) });
        queries.On(nameof(ITemperatureQueries.GetReadingsAsync), _ => new[] { Rules.Reading(), Rules.Reading(9.4m, true, "NC-2026-0031", "Porte restée ouverte") });
        units.On(nameof(IRepository<StorageUnit>.GetByIdAsync), _ => fridge);
        _sequences.On(nameof(ISequenceAllocator.NextAsync), _ => Result.Success("NC-2026-0042"));
        return (fridge, nonConformities);
    }

    [Fact]
    public void TheUnits_SayAnExcursion_AndTheReadingsOfTheMonth()
    {
        Units();
        SignIn(AppLanguage.French, Permissions.Inventory.RecordTemperatures);

        var page = Render<Temperatures>();

        page.Find("[data-row='Congélateur sérothèque']").ClassName.Should().Contain("stock-alarm");
        page.Find("[data-state=missing]").TextContent.Should().Contain("sans relevé");
        page.FindAll("[data-grid=readings] tbody tr").Should().HaveCount(2);
        page.Find("[data-grid=readings] tbody tr.stock-alarm").TextContent.Should().Contain("NC-2026-0031");
        page.FindAll("[data-action='Temperatures.NewUnit']").Should().BeEmpty("the units are managed by who manages them");

        page.Find("[data-action='Temperatures.MonthCsv']").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "marocLis.download"
                                                    && ((string)i.Arguments[0]!).StartsWith("temperatures-R", StringComparison.Ordinal));
    }

    [Fact]
    public void AReadingOutOfRange_AsksForItsComment_ThenOpensItsNonConformity()
    {
        var (_, nonConformities) = Units();
        SignIn(AppLanguage.French, Permissions.Inventory.RecordTemperatures, Permissions.Quality.View);

        var page = Render<Temperatures>();
        page.Find("[data-action='Temperatures.Record']").Click();
        page.Find($"[data-field={TemperatureScreenRules.ValueField}]").Input("9,6");
        page.Find("[data-state=excursion]").TextContent.Should().Contain("hors limites", "said while it is typed");
        page.Find("[data-modal=temperature-reading] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={TemperatureScreenRules.CommentField}]").Should().NotBeNull();
        nonConformities.CallsTo(nameof(IRepository<NonConformity>.AddAsync)).Should().Be(0);

        page.Find($"[data-field={TemperatureScreenRules.CommentField}]").Input("Produits transférés dans le réfrigérateur de secours");
        page.Find("[data-modal=temperature-reading] [data-action='Action.Save']").Click();

        nonConformities.CallsTo(nameof(IRepository<NonConformity>.AddAsync)).Should().Be(1);
        page.Find("[data-state=excursion-recorded]").TextContent.Should().Contain("NC-2026-0042");
        page.Find("[data-action=open-nonconformities]").GetAttribute("href").Should().Be("ecran/nonconformities");
    }

    [Fact]
    public void AUnitWithoutAName_IsRefusedOnItsBox()
    {
        Units();
        SignIn(AppLanguage.French, Permissions.Inventory.RecordTemperatures, Permissions.Inventory.ManageStorageUnits);

        var page = Render<Temperatures>();
        page.Find("[data-action='Temperatures.NewUnit']").Click();
        page.Find("[data-field=unit-kind]").Change(nameof(StorageUnitKind.Freezer20));
        page.Find($"[data-field={TemperatureScreenRules.RangeField}]").GetAttribute("value").Should().Be("-25");
        page.Find("[data-modal=storage-unit] [data-action='Action.Save']").Click();

        page.Find($"[data-refusal={TemperatureScreenRules.NameField}]").TextContent.Should().Contain("nom");
        _work.CallsTo(nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(0);
    }

    [Fact]
    public void TheTemperatures_AreInArabic_ForAnArabicSession()
    {
        Units();
        SignIn(AppLanguage.Arabic, Permissions.Inventory.RecordTemperatures, Permissions.Inventory.ManageStorageUnits);

        var page = Render<Temperatures>();

        page.Markup.Should().NotContain("[[", "every text of the screen has its Arabic");
        page.Find("[data-action='Temperatures.Record']").TextContent.Should().NotContain("Saisir");
    }
}
