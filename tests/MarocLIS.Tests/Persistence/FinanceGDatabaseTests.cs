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

/// <summary>A scratch database of its own for the fixed-assets register, created from the model.</summary>
public sealed class FinanceGScratchDatabase : IDisposable
{
    public string Name { get; } = "MarocLIS_T_finG_" + Guid.NewGuid().ToString("N")[..8];

    public string ConnectionString { get; }

    private readonly bool _created;

    public FinanceGScratchDatabase()
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
/// The register's queries against SQL Server: the assets with their supplier and instrument, the
/// analysers placed by suppliers, the instruments the editor offers with their state, the instrument
/// summary read from the equipment module, and the depreciation entries built from what is stored.
/// </summary>
public sealed class FinanceGDatabaseTests(FinanceGScratchDatabase database) : IClassFixture<FinanceGScratchDatabase>
{
    [SqlServerFact]
    public async Task The_register_reads_assets_placed_analysers_and_the_equipment_file()
    {
        var site = new LabSite("FG", new MultilingualText("Site immobilisations"), LabSiteKind.MainLaboratory);
        var supplier = new Supplier("FRN-G", "Biomédical Maroc");

        var bought = new Instrument("CENT-G", new MultilingualText("Centrifugeuse"), EquipmentKind.Centrifuge, site.Id)
        {
            PurchaseDate = new DateOnly(2025, 3, 1), CommissioningDate = new DateOnly(2025, 3, 10), SupplierId = supplier.Id
        };
        bought.AddMaintenance(new MaintenanceRecord(MaintenanceKind.Corrective, new DateOnly(2026, 5, 4),
                                                    new MultilingualText("Moteur remplacé"))
        {
            DowntimeHours = 6m, CostMad = 1_500m, PostInterventionCheckPassed = true
        });
        bought.AddMaintenance(new MaintenanceRecord(MaintenanceKind.Preventive, new DateOnly(2026, 2, 1),
                                                    new MultilingualText("Visite annuelle")));

        var placed = new Instrument("AN-G", new MultilingualText("Automate placé"), EquipmentKind.Analyser, site.Id)
        {
            PurchaseDate = new DateOnly(2024, 1, 1)
        };
        var contract = new InstrumentContract("MAD-G-1", supplier.Id, site.Id, InstrumentContractKind.Placement,
                                              new DateOnly(2024, 1, 1), 84);
        contract.SetInstruments([placed.Id]);

        var asset = new FixedAsset("CENT-G", "Centrifugeuse", FixedAssetCategory.LaboratoryEquipment, site.Id)
        {
            AcquisitionDate = new DateOnly(2025, 3, 1), InServiceDate = new DateOnly(2025, 3, 10),
            CostExcludingVatMad = 36_000m, VatAmountMad = 7_200m, VatRecoverable = false,
            Method = DepreciationMethod.StraightLine, UsefulLifeYears = 5, SupplierId = supplier.Id,
            InvoiceReference = "FA-2025-77", InstrumentId = bought.Id
        };

        await using (var context = database.Context())
        {
            context.AddRange(site, supplier, bought, placed, contract, asset,
                             new AppSetting(FixedAssetSettings.ChargeAccountKey(FixedAssetCategory.LaboratoryEquipment), "61933"));
            await context.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new FixedAssetQueries(read);
        var today = new DateOnly(2026, 10, 15);

        var source = (await queries.GetAssetsAsync(site.Id)).Single();
        source.Should().BeEquivalentTo(new
        {
            Code = "CENT-G", SupplierName = "Biomédical Maroc", InstrumentCode = "CENT-G", GrossValue = 43_200m,
            InvoiceReference = "FA-2025-77"
        });

        var provided = (await queries.GetSupplierProvidedAsync(site.Id, today)).Single();
        (provided.InstrumentCode, provided.ContractReference, provided.Kind)
            .Should().Be(("AN-G", "MAD-G-1", InstrumentContractKind.Placement));

        (await queries.GetProvidingContractsAsync(placed.Id)).Single().EndDate.Should().Be(new DateOnly(2030, 12, 31));

        var options = await queries.GetOptionsAsync(site.Id, null, today);
        options.Instruments.Single(i => i.Code == "AN-G").ProvidedBy.Should().Be("MAD-G-1");
        options.Instruments.Single(i => i.Code == "CENT-G").AssetCode.Should().Be("CENT-G");
        (await queries.GetOptionsAsync(site.Id, asset.Id, today))
            .Instruments.Single(i => i.Code == "CENT-G").AssetCode.Should().BeNull("the asset being edited is its own link");
        FixedAssetService.Candidates(options).Should().BeEmpty();

        (await queries.CodeExistsAsync(site.Id, "CENT-G", null)).Should().BeTrue();
        (await queries.CodeExistsAsync(site.Id, "CENT-G", asset.Id)).Should().BeFalse();
        (await queries.AssetCodeForInstrumentAsync(bought.Id, null)).Should().Be("CENT-G");

        var summary = (await queries.GetInstrumentSummaryAsync(bought.Id))!;
        summary.Interventions.Should().HaveCount(2);
        summary.Interventions[0].Kind.Should().Be(MaintenanceKind.Corrective);
        (summary.Breakdowns, summary.DowntimeHours, summary.MaintenanceCost).Should().Be((1, 6m, 1_500m));

        // 43 200 over five years from March 2025: 720 a month, with the charge account set in the parameters.
        var entries = await new FixedAssetDepreciationSource(queries)
            .GetEntriesAsync(site.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        entries.Should().HaveCount(12).And.OnlyContain(e => e.Amount == 720m && e.ChargeAccount == "61933"
                                                            && e.DepreciationAccount == "28332");

        // An instrument is capitalised once, in the database too.
        await using var write = database.Context();
        write.Add(new FixedAsset("CENT-G2", "Doublon", FixedAssetCategory.LaboratoryEquipment, site.Id)
        {
            AcquisitionDate = new DateOnly(2025, 3, 1), InServiceDate = new DateOnly(2025, 3, 1),
            CostExcludingVatMad = 1m, UsefulLifeYears = 5, InstrumentId = bought.Id
        });
        await write.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }
}
