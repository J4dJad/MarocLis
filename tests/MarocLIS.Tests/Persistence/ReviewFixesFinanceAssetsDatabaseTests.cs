using FluentAssertions;
using MarocLIS.Application.Assets;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Persistence;
using MarocLIS.Infrastructure.Queries;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>A scratch database for the fixed-assets review fixes, created from the model and dropped.</summary>
public sealed class ReviewFixesFinanceAssetsDatabase : IDisposable
{
    private readonly bool _created;

    public ReviewFixesFinanceAssetsDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured)) return;

        ConnectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "MarocLIS_T_f6ast_" + Guid.NewGuid().ToString("N")[..8]
        }.ConnectionString;

        using var context = Context();
        context.Database.EnsureCreated();
        _created = true;

        // An invoice line without its purchase order: only the export's figures are under test.
        context.Database.ExecuteSqlRaw("ALTER TABLE inv.SupplierInvoiceLines NOCHECK CONSTRAINT ALL");
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

/// <summary>
/// The fixed-assets fixes against SQL Server: the purchases journal capitalising an invoice linked
/// to an asset (I1), the depreciation months recorded at export and read back (I2), and the
/// supplier-provided equipment of the year shown (I4).
/// </summary>
public sealed class ReviewFixesFinanceAssetsDatabaseTests(ReviewFixesFinanceAssetsDatabase database)
    : IClassFixture<ReviewFixesFinanceAssetsDatabase>
{
    private static readonly DateOnly Jan2026 = new(2026, 1, 1);

    [SqlServerFact]
    public async Task I1_the_purchases_query_hands_the_linked_asset_with_its_category_account()
    {
        var site = new LabSite("F6A", new MultilingualText("Site I1"), LabSiteKind.MainLaboratory);
        var supplier = new Supplier("FRN-F6A", "Biomed");
        var item = new StockItem("PIECE-F6", new MultilingualText("Automate"), StockItemKind.SparePart, "U", site.Id);
        var invoice = new SupplierInvoice(supplier.Id, site.Id, "FA-123", new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 2),
                                          Guid.NewGuid());
        invoice.AddLine(new SupplierInvoiceLine(Guid.NewGuid(), item.Id, "Automate", 1m, 300_000m, 0.20m))
               .IsSuccess.Should().BeTrue();
        var asset = new FixedAsset("AN-I1", "Automate", FixedAssetCategory.LaboratoryEquipment, site.Id)
        {
            AcquisitionDate = new DateOnly(2026, 3, 2), InServiceDate = new DateOnly(2026, 3, 2),
            CostExcludingVatMad = 300_000m, VatAmountMad = 60_000m, VatRecoverable = false, UsefulLifeYears = 5,
            SupplierInvoiceId = invoice.Id
        };

        await using (var write = database.Context())
        {
            write.AddRange(site, supplier, item, invoice, asset);
            write.Add(new AppSetting(FixedAssetSettings.AssetAccountKey(FixedAssetCategory.LaboratoryEquipment), "23321"));
            await write.SaveChangesAsync();
        }

        await using var read = database.Context();
        var source = (await new SupplierInvoiceQueries(read)
            .GetAccountingInvoicesAsync(site.Id, Jan2026, new DateOnly(2026, 12, 31))).Single();

        source.Assets.Should().ContainSingle().Which
              .Should().Be(new Application.Inventory.AccountingAssetShare("23321", 300_000m, 60_000m, false));

        var entries = Application.Inventory.SupplierAccountingExport.BuildEntries(
            [source], [], Application.Inventory.PurchasingSettings.Default);
        entries.Where(e => e.Debit > 0m).Select(e => (e.Account, e.Debit)).Should().Equal(("23321", 360_000m));
    }

    [SqlServerFact]
    public async Task I2_the_months_of_an_export_are_recorded_once_and_read_back()
    {
        var site = new LabSite("F6B", new MultilingualText("Site I2"), LabSiteKind.MainLaboratory);
        var asset = new FixedAsset("AN-I2", "Automate", FixedAssetCategory.LaboratoryEquipment, site.Id)
        {
            AcquisitionDate = Jan2026, InServiceDate = Jan2026, CostExcludingVatMad = 60_000m, UsefulLifeYears = 5
        };
        await using (var write = database.Context())
        {
            write.AddRange(site, asset);
            await write.SaveChangesAsync();
        }

        async Task Record(DateOnly from, DateOnly to)
        {
            await using var context = database.Context();
            var clock = new Clock();
            await new FixedAssetDepreciationSource(new FixedAssetQueries(context),
                                                   new Repository<FixedAssetDepreciationPosting>(context, clock),
                                                   new UnitOfWork(context), clock)
                .RecordExportedAsync(site.Id, from, to);
        }

        await Record(Jan2026, new DateOnly(2026, 3, 31));
        await Record(Jan2026, new DateOnly(2026, 3, 31));

        await using var read = database.Context();
        var queries = new FixedAssetQueries(read);
        var posted = await queries.GetDepreciationPostingsAsync(site.Id);
        posted.Should().HaveCount(3, "exporting the same months again records nothing more")
              .And.OnlyContain(p => p.AssetId == asset.Id && p.Amount == 1_000m);

        // The cost corrected afterwards: April carries the difference on January to March.
        await using (var write = database.Context())
        {
            var stored = await write.FixedAssets.SingleAsync(a => a.Id == asset.Id);
            stored.CostExcludingVatMad = 72_000m;
            await write.SaveChangesAsync();
        }

        await using var after = database.Context();
        var entries = await new FixedAssetDepreciationSource(new FixedAssetQueries(after))
            .GetEntriesAsync(site.Id, Jan2026, new DateOnly(2026, 4, 30));
        entries.Select(e => e.Amount).Should().Equal(1_000m, 1_000m, 1_000m, 1_800m);
    }

    [SqlServerFact]
    public async Task I4_the_register_of_a_year_lists_the_placements_of_that_year()
    {
        var site = new LabSite("F6C", new MultilingualText("Site I4"), LabSiteKind.MainLaboratory);
        var supplier = new Supplier("FRN-F6C", "Placeur");
        var ended = new Instrument("AN-OLD", new MultilingualText("Ancien automate"), EquipmentKind.Analyser, site.Id);
        var coming = new Instrument("AN-NEW", new MultilingualText("Nouvel automate"), EquipmentKind.Analyser, site.Id);

        // Ended on 31 December 2025; starting in March 2026.
        var past = new InstrumentContract("MAD-2025", supplier.Id, site.Id, InstrumentContractKind.Placement,
                                          new DateOnly(2023, 1, 1), 36);
        past.SetInstruments([ended.Id]);
        var next = new InstrumentContract("MAD-2026", supplier.Id, site.Id, InstrumentContractKind.Placement,
                                          new DateOnly(2026, 3, 1), 60);
        next.SetInstruments([coming.Id]);

        await using (var write = database.Context())
        {
            write.AddRange(site, supplier, ended, coming, past, next);
            await write.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new FixedAssetQueries(read);
        (await queries.GetSupplierProvidedAsync(site.Id, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)))
            .Select(r => r.ContractReference).Should().Equal("MAD-2025");
        (await queries.GetSupplierProvidedAsync(site.Id, Jan2026, new DateOnly(2026, 12, 31)))
            .Select(r => r.ContractReference).Should().Equal("MAD-2026");
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now => new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 10, 15);
    }
}
