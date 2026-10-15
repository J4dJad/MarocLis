using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Assets;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Finance review, cycle 2, fixed assets: months missed by exports already made caught up (A8),
/// the reclassification of an asset linked to an invoice already exported (I7/A9), a change of
/// category after export booked as a transfer (I8/A11), exported months kept when the dates move
/// (I9), the VAT statement (I10/A12), 4481 for capitalised purchases (I11/A13), contracts terminated
/// early (I12) and the register's PDF widths (S9).
/// </summary>
public sealed class ReviewFixesFinanceAssets2Tests
{
    private static readonly DateOnly Jan2026 = new(2026, 1, 1);
    private static readonly DateOnly Oct31 = new(2026, 10, 31);

    private static FixedAssetSource Asset(decimal gross, DateOnly inService, Guid? id = null, string code = "AN-1",
                                          FixedAssetCategory category = FixedAssetCategory.LaboratoryEquipment) =>
        new(id ?? Guid.NewGuid(), code, "Automate", category, Guid.NewGuid(), inService, inService, gross, 0m, false,
            DepreciationMethod.StraightLine, 5, 0m, null, null, null, null, null, null, null);

    private static List<DepreciationPosting> Export(FixedAssetSource asset, IEnumerable<DepreciationPosting> already,
                                                    DateOnly from, DateOnly to, IEnumerable<DepreciationPosting>? site = null,
                                                    string charge = "6193", string depreciation = "28332") =>
    [
        .. FixedAssetDepreciationSource.Months(asset, [.. already], from, to,
                                               FixedAssetDepreciationSource.SiteMonths([.. already, .. site ?? []]))
            .Where(m => !m.Posted)
            .Select(m => new DepreciationPosting(asset.Id, m.Year, m.Month, m.Date, m.Amount, charge, depreciation))
    ];

    // ---- A8 ------------------------------------------------------------------------------------------------

    [Fact]
    public void A8_an_asset_registered_after_its_first_months_were_exported_catches_them_up_in_the_next_export()
    {
        // January to October exported for the site, with another asset.
        var other = Asset(60_000m, Jan2026, code: "AN-0");
        var site = Export(other, [], Jan2026, Oct31);

        // Registered on 10/11, in service since 01/07: 2 000 a month.
        var late = Asset(120_000m, new DateOnly(2026, 7, 1));
        var entries = FixedAssetDepreciationSource.Entries([late], FixedAssetSettings.Default, new DateOnly(2026, 11, 1),
                                                           new DateOnly(2026, 12, 31), site);

        entries.Select(e => e.Amount).Should().Equal(10_000m, 2_000m);
        entries[0].Label.Should().Contain("régul.");

        // Exporting January to October again gives nothing for it: the months went out without it.
        FixedAssetDepreciationSource.Entries([late], FixedAssetSettings.Default, Jan2026, Oct31, site)
            .Should().BeEmpty();
    }

    [Fact]
    public void A8_dates_moved_earlier_after_export_catch_up_the_months_the_site_already_exported()
    {
        var id = Guid.NewGuid();
        var posted = Export(Asset(120_000m, new DateOnly(2026, 7, 1), id), [], Jan2026, Oct31);
        posted.Sum(p => p.Amount).Should().Be(8_000m);

        var other = Export(Asset(60_000m, Jan2026, code: "AN-0"), [], Jan2026, Oct31);
        var earlier = Asset(120_000m, new DateOnly(2026, 3, 1), id);
        var november = FixedAssetDepreciationSource.Months(earlier, posted, new DateOnly(2026, 11, 1),
                                                           new DateOnly(2026, 11, 30),
                                                           FixedAssetDepreciationSource.SiteMonths([.. posted, .. other]));

        november.Should().ContainSingle().Which.Amount.Should().Be(10_000m, "March to June (8 000) and November");
    }

    // ---- I9 ------------------------------------------------------------------------------------------------

    [Fact]
    public void I9_dates_moved_to_a_later_year_keep_the_exported_months_and_reverse_them_in_the_next_open_month()
    {
        var id = Guid.NewGuid();
        var posted = Export(Asset(120_000m, Jan2026, id), [], Jan2026, Oct31);
        posted.Sum(p => p.Amount).Should().Be(20_000m);

        var moved = Asset(120_000m, new DateOnly(2027, 1, 1), id);
        var settings = FixedAssetSettings.Default;

        FixedAssetDepreciationSource.Entries([moved], settings, Jan2026, Oct31, posted)
            .Sum(e => e.Amount).Should().Be(20_000m, "the months exported are given again as they were");

        var reversal = FixedAssetDepreciationSource.Entries([moved], settings, new DateOnly(2026, 11, 1),
                                                            new DateOnly(2026, 12, 31), posted)
            .Should().ContainSingle().Subject;
        (reversal.Date, reversal.Amount, reversal.ChargeAccount, reversal.DepreciationAccount)
            .Should().Be((new DateOnly(2026, 11, 30), 20_000m, "28332", "6193"));
    }

    // ---- I8 ------------------------------------------------------------------------------------------------

    [Fact]
    public void I8_months_exported_before_a_change_of_category_keep_their_accounts_and_the_change_is_transferred()
    {
        var id = Guid.NewGuid();
        var posted = Export(Asset(120_000m, Jan2026, id), [], Jan2026, Oct31);
        var it = Asset(120_000m, Jan2026, id, category: FixedAssetCategory.ItEquipment);
        var settings = FixedAssetSettings.Default;
        var transfer = new DepreciationTransfer(id, new DateOnly(2026, 11, 10), new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero),
                                                settings.For(FixedAssetCategory.LaboratoryEquipment),
                                                settings.For(FixedAssetCategory.ItEquipment), 120_000m, 20_000m);

        FixedAssetDepreciationSource.Entries([it], settings, Jan2026, Oct31, posted, [transfer])
            .Should().OnlyContain(e => e.DepreciationAccount == "28332");

        var later = FixedAssetDepreciationSource.Entries([it], settings, new DateOnly(2026, 11, 1),
                                                         new DateOnly(2026, 12, 31), posted, [transfer]);
        later.Select(e => (e.Date, e.ChargeAccount, e.DepreciationAccount, e.Amount)).Should().BeEquivalentTo(
        [
            (new DateOnly(2026, 11, 10), "28332", "28355", 20_000m),
            (new DateOnly(2026, 11, 10), "2355", "2332", 120_000m),
            (new DateOnly(2026, 11, 30), "6193", "28355", 2_000m),
            (new DateOnly(2026, 12, 31), "6193", "28355", 2_000m)
        ]);
    }

    [Fact]
    public void I8_a_posting_recorded_before_the_accounts_were_kept_takes_the_category_it_was_exported_under()
    {
        var settings = FixedAssetSettings.Default;
        var transfer = new DepreciationTransfer(Guid.NewGuid(), new DateOnly(2026, 11, 10), new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero),
                                                settings.For(FixedAssetCategory.LaboratoryEquipment),
                                                settings.For(FixedAssetCategory.ItEquipment), 1m, 1m);
        var current = settings.For(FixedAssetCategory.ItEquipment);

        FixedAssetDepreciationSource.AccountsAt([transfer], new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), current)
            .Depreciation.Should().Be("28332");
        FixedAssetDepreciationSource.AccountsAt([transfer], new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero), current)
            .Depreciation.Should().Be("28355");
        FixedAssetDepreciationSource.AccountsAt([transfer], null, current).Should().Be(current);
    }

    [Fact]
    public async Task I8_changing_the_category_of_an_asset_with_exported_months_records_a_transfer()
    {
        var site = Guid.NewGuid();
        var asset = new FixedAsset("AN-1", "Automate", FixedAssetCategory.LaboratoryEquipment, site)
        {
            AcquisitionDate = Jan2026, InServiceDate = Jan2026, CostExcludingVatMad = 120_000m, UsefulLifeYears = 5
        };
        var added = new List<FixedAssetTransfer>();
        var service = Service(asset, added, [new DepreciationPosting(asset.Id, 2026, 1, new DateOnly(2026, 1, 31), 2_000m)]);

        (await service.SaveAsync(Request(asset, FixedAssetCategory.ItEquipment))).IsSuccess.Should().BeTrue();

        var transfer = added.Should().ContainSingle().Subject;
        (transfer.Date, transfer.FromAssetAccount, transfer.ToAssetAccount, transfer.FromDepreciationAccount,
         transfer.ToDepreciationAccount, transfer.GrossMad, transfer.DepreciationMad)
            .Should().Be((new DateOnly(2026, 11, 10), "2332", "2355", "28332", "28355", 120_000m, 2_000m));

        // Nothing exported yet: nothing to transfer.
        var fresh = new List<FixedAssetTransfer>();
        (await Service(asset, fresh, []).SaveAsync(Request(asset, FixedAssetCategory.Furniture))).IsSuccess.Should().BeTrue();
        fresh.Should().BeEmpty();
    }

    private static SaveFixedAssetRequest Request(FixedAsset asset, FixedAssetCategory category) => new()
    {
        Id = asset.Id, Code = asset.Code, Label = asset.Label, Category = category,
        AcquisitionDate = asset.AcquisitionDate, InServiceDate = asset.InServiceDate,
        CostExcludingVatMad = asset.CostExcludingVatMad, UsefulLifeYears = asset.UsefulLifeYears
    };

    private static FixedAssetService Service(FixedAsset asset, List<FixedAssetTransfer> added,
                                             IReadOnlyList<DepreciationPosting> posted) =>
        new(ReviewFixesBilling2Tests.Stub<IRepository<FixedAsset>>.With(
                ("GetByIdAsync", _ => Task.FromResult<FixedAsset?>(asset))),
            ReviewFixesBilling2Tests.Stub<IFixedAssetQueries>.With(
                ("GetSettingValuesAsync", _ => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                    new Dictionary<string, string?>())),
                ("GetDepreciationPostingsAsync", _ => Task.FromResult(posted))),
            ReviewFixesBilling2Tests.Stub<IConfigurationWriter>.Create(),
            ReviewFixesBilling2Tests.Stub<IUnitOfWork>.Create(),
            ReviewFixesBilling2Tests.Stub<IPermissionGuard>.Create(),
            ReviewFixesBilling2Tests.Stub<ICurrentUser>.With(("get_SiteId", _ => asset.SiteId)),
            ReviewFixesBilling2Tests.Stub<IClock>.With(
                ("get_Today", _ => new DateOnly(2026, 11, 10)),
                ("get_Now", _ => new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero))),
            ReviewFixesBilling2Tests.Stub<IRepository<FixedAssetTransfer>>.With(
                ("AddAsync", args => { added.Add((FixedAssetTransfer)args![0]!); return Task.CompletedTask; })));

    // ---- I7 ------------------------------------------------------------------------------------------------

    private static AccountingInvoiceSource Invoice(params AccountingAssetShare[] assets) =>
        new("FRN", "Biomed", null, "FA-123", new DateOnly(2026, 3, 10),
            [new AccountingInvoiceLine(StockItemKind.SparePart, 300_000m, 60_000m)], assets, Guid.NewGuid(), true);

    [Fact]
    public void I7_an_asset_linked_after_the_invoice_was_exported_is_reclassified_on_the_day_of_the_link()
    {
        var invoice = Invoice(new AccountingAssetShare("2332", 300_000m, 60_000m, false, new DateOnly(2026, 4, 15)));
        var settings = PurchasingSettings.Default;

        // The invoice's own entry stays as exported: a charge, the VAT deducted, the supplier on 4411.
        var own = SupplierAccountingExport.BuildEntries([invoice], [], settings);
        own.Where(e => e.Debit > 0m).Select(e => e.Account).Should().Equal("6122", "34552");
        own.Single(e => e.Credit > 0m).Account.Should().Be("4411");

        var reclass = SupplierAccountingExport.Reclassifications([invoice], settings, new DateOnly(2026, 4, 1),
                                                                 new DateOnly(2026, 4, 30));
        reclass.Select(e => (e.Date, e.Account, e.Debit, e.Credit)).Should().Equal(
            (new DateOnly(2026, 4, 15), "2332", 360_000m, 0m),
            (new DateOnly(2026, 4, 15), "6122", 0m, 300_000m),
            (new DateOnly(2026, 4, 15), "34552", 0m, 60_000m));
        reclass.Should().OnlyContain(e => e.Journal == "ACH" && e.Piece == "FA-123");

        SupplierAccountingExport.Reclassifications([invoice], settings, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31))
            .Should().BeEmpty("the reclassification belongs to the period of the link");
    }

    [Fact]
    public void I7_a_recoverable_asset_vat_moves_from_34552_to_34551()
    {
        var invoice = Invoice(new AccountingAssetShare("2332", 300_000m, 60_000m, true, new DateOnly(2026, 4, 15)));
        SupplierAccountingExport.Reclassifications([invoice], PurchasingSettings.Default, Jan2026, new DateOnly(2026, 12, 31))
            .Select(e => (e.Account, e.Debit, e.Credit)).Should().Equal(
                ("2332", 300_000m, 0m), ("34551", 60_000m, 0m), ("6122", 0m, 300_000m), ("34552", 0m, 60_000m));
    }

    [Fact]
    public async Task I7_the_invoices_of_a_purchases_export_are_recorded_when_the_file_is_produced()
    {
        var site = Guid.NewGuid();
        var invoice = Invoice() with { Assets = null, Exported = false };
        var recorded = new List<SupplierInvoiceExport>();
        var reclassAsked = 0;
        var service = new AccountingExportService(
            ReviewFixesBilling2Tests.Stub<IAccountingQueries>.With(
                ("GetSitesAsync", _ => Task.FromResult<IReadOnlyList<AccountingSiteOption>>([new(site, "A", "Site A")])),
                ("GetSettingValuesAsync", _ => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                    new Dictionary<string, string?>()))),
            ReviewFixesBilling2Tests.Stub<ISupplierInvoiceQueries>.With(
                ("GetSettingValuesAsync", _ => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                    new Dictionary<string, string?>())),
                ("GetAccountingInvoicesAsync", _ => Task.FromResult<IReadOnlyList<AccountingInvoiceSource>>([invoice])),
                ("GetAccountingPaymentsAsync", _ => Task.FromResult<IReadOnlyList<AccountingPaymentSource>>([])),
                ("GetAccountingReclassificationsAsync", _ =>
                {
                    reclassAsked++;
                    return Task.FromResult<IReadOnlyList<AccountingInvoiceSource>>([]);
                })),
            [], null!, ReviewFixesBilling2Tests.Stub<IUnitOfWork>.Create(),
            ReviewFixesBilling2Tests.Stub<IPermissionGuard>.Create(),
            ReviewFixesBilling2Tests.Stub<ICurrentUser>.With(
                ("get_SiteId", _ => site), ("get_AccessibleSiteIds", _ => (IReadOnlyList<Guid>)[site])),
            ReviewFixesBilling2Tests.Stub<IDataAccessAuditor>.Create(),
            ReviewFixesBilling2Tests.Stub<IRepository<SupplierInvoiceExport>>.With(
                ("AddAsync", args => { recorded.Add((SupplierInvoiceExport)args![0]!); return Task.CompletedTask; })));

        var request = new AccountingExportRequest(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), [site],
                                                  AccountingJournals.Purchases, AccountingExportLayout.Sage);

        (await service.PrepareAsync(request)).IsSuccess.Should().BeTrue();
        recorded.Should().BeEmpty("the check records nothing");
        reclassAsked.Should().Be(1, "the reclassifications of the period are part of the purchases journal");

        (await service.ExportAsync(request)).IsSuccess.Should().BeTrue();
        recorded.Should().ContainSingle().Which.SupplierInvoiceId.Should().Be(invoice.Id);
    }

    // ---- I11 -----------------------------------------------------------------------------------------------

    [Fact]
    public void I11_a_capitalised_invoice_and_its_payments_go_through_4481()
    {
        var invoice = Invoice(new AccountingAssetShare("2332", 300_000m, 60_000m, false));
        var payment = new AccountingPaymentSource("FRN", "Biomed", null, "FA-123", new DateOnly(2026, 5, 2), 360_000m,
                                                  SupplierPaymentMethod.Transfer, "VIR-1", AssetPurchase: true);
        var entries = SupplierAccountingExport.BuildEntries([invoice], [payment], PurchasingSettings.Default);

        entries.Single(e => e.Journal == "ACH" && e.Credit > 0m).Account.Should().Be("4481");
        entries.Single(e => e.Journal == "BQ" && e.Debit > 0m).Account.Should().Be("4481");

        PurchasingSettings.FromValues(new Dictionary<string, string?> { [PurchasingSettings.AssetSupplierAccountKey] = "44810" })
            .AssetSupplierAccount.Should().Be("44810");
        AccountingExportService.Sheet(new AccountingSettings(), PurchasingSettings.Default, [], [])
            .Should().Contain(r => r.Key == PurchasingSettings.AssetSupplierAccountKey && r.Value == "4481");
    }

    // ---- I10 -----------------------------------------------------------------------------------------------

    [Fact]
    public void I10_the_vat_statement_leaves_out_asset_vat_not_recoverable_and_shows_asset_vat_apart()
    {
        var notRecoverable = Invoice(new AccountingAssetShare("2332", 300_000m, 60_000m, false));
        var statement = VatStatement.Build(Jan2026, new DateOnly(2026, 12, 31), [], [notRecoverable]);
        statement.Invoiced.Should().Be(0m);
        statement.Lines.Where(l => l.Kind == VatStatementLineKind.Purchases).Sum(l => l.Base).Should().Be(0m);

        var recoverable = Invoice(new AccountingAssetShare("2332", 200_000m, 40_000m, true));
        var mixed = VatStatement.Build(Jan2026, new DateOnly(2026, 12, 31), [], [recoverable]);
        mixed.Lines.Single(l => l.Kind == VatStatementLineKind.AssetPurchases)
             .Should().Be(new VatStatementLine(VatStatementLineKind.AssetPurchases, 20m, 200_000m, 40_000m));
        mixed.Lines.Single(l => l.Kind == VatStatementLineKind.Purchases)
             .Should().Be(new VatStatementLine(VatStatementLineKind.Purchases, 20m, 100_000m, 20_000m));
        mixed.Invoiced.Should().Be(60_000m);
        VatStatement.LabelOf(mixed.Lines.Single(l => l.Kind == VatStatementLineKind.AssetPurchases))
            .Should().Contain("TVA sur immobilisations");
    }

    // ---- I12 -----------------------------------------------------------------------------------------------

    [Fact]
    public void I12_a_contract_terminated_early_covers_up_to_its_termination()
    {
        var contract = new InstrumentContract("MAD-1", Guid.NewGuid(), Guid.NewGuid(), InstrumentContractKind.Placement,
                                              new DateOnly(2023, 1, 1), 60);
        contract.EffectiveEndDate.Should().Be(new DateOnly(2027, 12, 31));

        contract.IsActive = false;
        contract.TerminatedOn = new DateOnly(2026, 3, 15);
        contract.EffectiveEndDate.Should().Be(new DateOnly(2026, 3, 15));

        contract.TerminatedOn = null;
        contract.ModifiedAt = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);
        contract.EffectiveEndDate.Should().Be(new DateOnly(2026, 4, 1), "a contract deactivated before the date was kept");
    }

    // ---- S9 ------------------------------------------------------------------------------------------------

    [Fact]
    public void S9_the_register_pdf_gives_every_printed_column_its_width()
    {
        FixedAssetRegisterReport.PdfColumnWidths.Should().HaveCount(20).And.OnlyContain(w => w > 0f);
        var headers = FixedAssetRegisterReport.FrenchHeaders;
        var widths = FixedAssetRegisterReport.PdfColumnWidths;
        widths[headers.ToList().IndexOf("Valeur brute")].Should().BeGreaterThan(widths[headers.ToList().IndexOf("Durée")]);
    }
}
