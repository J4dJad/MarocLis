using FluentAssertions;
using MarocLIS.Application.Assets;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Domain.Organization;
using MarocLIS.Infrastructure.Queries;
using Xunit;

namespace MarocLIS.Tests.Persistence;

/// <summary>
/// The cycle-2 fixed-assets fixes against SQL Server: an asset linked after its invoice was exported
/// handed as a reclassification (I7), the payments of a capitalised invoice flagged for 4481 (I11),
/// the posting accounts and category transfers read back (I8), and a placement terminated early
/// still listed for the years it covered (I12).
/// </summary>
public sealed class ReviewFixesFinanceAssets2DatabaseTests(ReviewFixesFinanceAssetsDatabase database)
    : IClassFixture<ReviewFixesFinanceAssetsDatabase>
{
    private static readonly DateTimeOffset Exported = new(2026, 4, 5, 9, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task I7_I11_an_asset_linked_after_the_export_is_a_reclassification_and_its_payment_stays_on_4411()
    {
        var site = new LabSite("F7A", new MultilingualText("Site I7"), LabSiteKind.MainLaboratory);
        var supplier = new Supplier("FRN-F7A", "Biomed");
        var item = new StockItem("PIECE-F7", new MultilingualText("Automate"), StockItemKind.SparePart, "U", site.Id);

        SupplierInvoice NewInvoice(string number)
        {
            var invoice = new SupplierInvoice(supplier.Id, site.Id, number, new DateOnly(2026, 3, 10),
                                              new DateOnly(2026, 3, 10), Guid.NewGuid());
            invoice.AddLine(new SupplierInvoiceLine(Guid.NewGuid(), item.Id, "Automate", 1m, 300_000m, 0.20m))
                   .IsSuccess.Should().BeTrue();
            invoice.ApplyMatch([]).IsSuccess.Should().BeTrue();
            invoice.RecordPayment(new SupplierPayment(new DateOnly(2026, 5, 2), 360_000m, SupplierPaymentMethod.Transfer,
                                                      number, Exported, Guid.NewGuid())).IsSuccess.Should().BeTrue();
            return invoice;
        }

        var late = NewInvoice("FA-LATE");
        var onTime = NewInvoice("FA-ONTIME");

        FixedAsset NewAsset(string code, SupplierInvoice invoice, DateTimeOffset linkedAt)
        {
            var asset = new FixedAsset(code, "Automate", FixedAssetCategory.LaboratoryEquipment, site.Id)
            {
                AcquisitionDate = new DateOnly(2026, 3, 10), InServiceDate = new DateOnly(2026, 3, 10),
                CostExcludingVatMad = 300_000m, VatAmountMad = 60_000m, UsefulLifeYears = 5
            };
            asset.LinkSupplierInvoice(invoice.Id, linkedAt);
            return asset;
        }

        await using (var write = database.Context())
        {
            write.AddRange(site, supplier, item, late, onTime,
                           NewAsset("AN-LATE", late, new DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero)),
                           NewAsset("AN-ONTIME", onTime, new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero)));
            write.AddRange(new SupplierInvoiceExport(late.Id, Exported), new SupplierInvoiceExport(onTime.Id, Exported));
            await write.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new SupplierInvoiceQueries(read);

        var march = await queries.GetAccountingInvoicesAsync(site.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        march.Should().OnlyContain(i => i.Exported);
        march.Single(i => i.InvoiceNumber == "FA-LATE").Assets!.Single().ReclassifiedOn.Should().Be(new DateOnly(2026, 4, 15));
        march.Single(i => i.InvoiceNumber == "FA-ONTIME").Assets!.Single().ReclassifiedOn.Should().BeNull();

        (await queries.GetAccountingReclassificationsAsync(site.Id, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)))
            .Select(i => i.InvoiceNumber).Should().Equal("FA-LATE");
        (await queries.GetAccountingReclassificationsAsync(site.Id, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31)))
            .Should().BeEmpty();

        var payments = await queries.GetAccountingPaymentsAsync(site.Id, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));
        payments.Single(p => p.InvoiceNumber == "FA-ONTIME").AssetPurchase.Should().BeTrue();
        payments.Single(p => p.InvoiceNumber == "FA-LATE").AssetPurchase.Should().BeFalse();

        (await new FixedAssetQueries(read).IsSupplierInvoiceExportedAsync(late.Id)).Should().BeTrue();
    }

    [SqlServerFact]
    public async Task I8_the_accounts_of_a_posting_and_the_transfers_are_read_back()
    {
        var site = new LabSite("F7B", new MultilingualText("Site I8"), LabSiteKind.MainLaboratory);
        var asset = new FixedAsset("AN-I8", "Automate", FixedAssetCategory.ItEquipment, site.Id)
        {
            AcquisitionDate = new DateOnly(2026, 1, 1), InServiceDate = new DateOnly(2026, 1, 1),
            CostExcludingVatMad = 120_000m, UsefulLifeYears = 5
        };

        await using (var write = database.Context())
        {
            write.AddRange(site, asset);
            write.Add(new FixedAssetDepreciationPosting(asset.Id, 2026, 1, new DateOnly(2026, 1, 31), 2_000m, Exported)
                .WithAccounts("6193", "28332"));
            write.Add(new FixedAssetTransfer(asset.Id, new DateOnly(2026, 11, 10), Exported.AddMonths(7),
                                             FixedAssetCategory.LaboratoryEquipment, FixedAssetCategory.ItEquipment,
                                             "2332", "28332", "6193", "2355", "28355", 120_000m, 2_000m));
            await write.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new FixedAssetQueries(read);
        var posting = (await queries.GetDepreciationPostingsAsync(site.Id)).Should().ContainSingle().Subject;
        (posting.ChargeAccount, posting.DepreciationAccount).Should().Be(("6193", "28332"));

        var transfer = (await queries.GetDepreciationTransfersAsync(site.Id)).Should().ContainSingle().Subject;
        (transfer.From.Asset, transfer.To.Asset, transfer.From.Depreciation, transfer.To.Depreciation,
         transfer.Gross, transfer.Depreciation)
            .Should().Be(("2332", "2355", "28332", "28355", 120_000m, 2_000m));
    }

    [SqlServerFact]
    public async Task I12_a_placement_terminated_later_is_still_listed_for_the_years_it_covered()
    {
        var site = new LabSite("F7C", new MultilingualText("Site I12"), LabSiteKind.MainLaboratory);
        var supplier = new Supplier("FRN-F7C", "Placeur");
        var analyser = new Instrument("AN-MAD", new MultilingualText("Automate placé"), EquipmentKind.Analyser, site.Id);
        var contract = new InstrumentContract("MAD-2027", supplier.Id, site.Id, InstrumentContractKind.Placement,
                                              new DateOnly(2023, 1, 1), 60)
        {
            IsActive = false, TerminatedOn = new DateOnly(2026, 3, 15)
        };
        contract.SetInstruments([analyser.Id]);

        await using (var write = database.Context())
        {
            write.AddRange(site, supplier, analyser, contract);
            await write.SaveChangesAsync();
        }

        await using var read = database.Context();
        var queries = new FixedAssetQueries(read);
        var row = (await queries.GetSupplierProvidedAsync(site.Id, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)))
            .Should().ContainSingle().Subject;
        (row.ContractReference, row.EndDate).Should().Be(("MAD-2027", new DateOnly(2026, 3, 15)));
        (await queries.GetSupplierProvidedAsync(site.Id, new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31)))
            .Should().BeEmpty();
    }
}
