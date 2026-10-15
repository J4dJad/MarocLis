using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Assets;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using Xunit;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Finance review, fixed assets: an invoice linked to an asset capitalised rather than expensed
/// (I1), corrections after depreciation was exported booked as a difference (I2), the year's totals
/// without the assets disposed of in it (I3), and the text lengths of the dialogs (I5).
/// </summary>
public sealed class ReviewFixesFinanceAssetsTests
{
    private static readonly DateOnly Jan2026 = new(2026, 1, 1);

    private static FixedAssetSource Asset(decimal gross, DateOnly inService, DateOnly? disposal = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "AN-1", "Automate", FixedAssetCategory.LaboratoryEquipment, Guid.NewGuid(), inService,
            inService, gross, 0m, false, DepreciationMethod.StraightLine, 5, 0m, disposal, disposal is null ? null : 0m,
            disposal is null ? null : "Cession", null, null, null, null);

    /// <summary>What an export of the period would record for the asset.</summary>
    private static List<DepreciationPosting> Export(FixedAssetSource asset, IEnumerable<DepreciationPosting> already,
                                                    DateOnly from, DateOnly to) =>
    [
        .. FixedAssetDepreciationSource.Months(asset, [.. already], from, to)
            .Where(m => !m.Posted)
            .Select(m => new DepreciationPosting(asset.Id, m.Year, m.Month, m.Date, m.Amount))
    ];

    // ---- I1 ------------------------------------------------------------------------------------------------

    [Fact]
    public void I1_an_invoice_linked_to_an_asset_debits_the_asset_account_with_the_vat_not_deducted()
    {
        var invoice = new AccountingInvoiceSource("BIOMED", "Biomed", null, "FA-123", new DateOnly(2026, 3, 2),
            [new AccountingInvoiceLine(StockItemKind.SparePart, 300_000m, 60_000m)],
            [new AccountingAssetShare("2332", 300_000m, 60_000m, VatRecoverable: false)]);

        var entries = SupplierAccountingExport.BuildEntries([invoice], [], PurchasingSettings.Default);

        entries.Should().HaveCount(2);
        entries.Single(e => e.Account == "2332").Debit.Should().Be(360_000m);
        entries.Single(e => e.Account == "4411").Credit.Should().Be(360_000m);
        entries.Should().NotContain(e => e.Account.StartsWith('6') || e.Account.StartsWith("3455", StringComparison.Ordinal));
    }

    [Fact]
    public void I1_the_deducted_vat_of_an_asset_goes_to_34551_and_the_rest_of_the_invoice_stays_a_charge()
    {
        var invoice = new AccountingInvoiceSource("BIOMED", "Biomed", null, "FA-124", new DateOnly(2026, 3, 2),
            [
                new AccountingInvoiceLine(StockItemKind.Reagent, 1_000m, 200m),
                new AccountingInvoiceLine(StockItemKind.SparePart, 50_000m, 10_000m)
            ],
            [new AccountingAssetShare("2332", 50_000m, 10_000m, VatRecoverable: true)]);

        var entries = SupplierAccountingExport.BuildEntries([invoice], [], PurchasingSettings.Default);

        entries.Sum(e => e.Debit).Should().Be(entries.Sum(e => e.Credit)).And.Be(61_200m);
        entries.Single(e => e.Account == "2332").Debit.Should().Be(50_000m);
        entries.Single(e => e.Account == "34551").Debit.Should().Be(10_000m);
        entries.Single(e => e.Account == "6122").Debit.Should().Be(1_000m);
        entries.Single(e => e.Account == "34552").Debit.Should().Be(200m);
    }

    [Fact]
    public void I1_an_asset_costing_more_than_its_invoice_takes_no_more_than_the_invoice_holds()
    {
        // The asset's cost includes transport invoiced by someone else.
        var invoice = new AccountingInvoiceSource("BIOMED", "Biomed", null, "FA-125", new DateOnly(2026, 3, 2),
            [new AccountingInvoiceLine(StockItemKind.SparePart, 10_000m, 2_000m)],
            [new AccountingAssetShare("2355", 11_500m, 2_300m, VatRecoverable: true)]);

        var entries = SupplierAccountingExport.BuildEntries([invoice], [], PurchasingSettings.Default);

        entries.Single(e => e.Account == "2355").Debit.Should().Be(10_000m);
        entries.Single(e => e.Account == "34551").Debit.Should().Be(2_000m);
        entries.Single(e => e.Account == "4411").Credit.Should().Be(12_000m);
        entries.Should().HaveCount(3);
    }

    [Fact]
    public void I1_the_vat_account_on_assets_is_a_setting_with_34551_by_default()
    {
        PurchasingSettings.Default.AssetVatAccount.Should().Be("34551");
        var settings = PurchasingSettings.FromValues(new Dictionary<string, string?> { [PurchasingSettings.AssetVatAccountKey] = "345510" });
        settings.AssetVatAccount.Should().Be("345510");
        settings.ToValues()[PurchasingSettings.AssetVatAccountKey].Should().Be("345510");
    }

    // ---- I2 ------------------------------------------------------------------------------------------------

    [Fact]
    public void I2_a_cost_corrected_after_export_is_caught_up_in_the_first_month_not_exported()
    {
        var id = Guid.NewGuid();
        var posted = Export(Asset(100_000m, Jan2026, id: id), [], Jan2026, new DateOnly(2026, 10, 31));
        posted.Sum(p => p.Amount).Should().Be(16_666.67m);

        var corrected = Asset(120_000m, Jan2026, id: id);
        var settings = FixedAssetSettings.Default;

        // January to October again: as exported, not restated.
        FixedAssetDepreciationSource.Entries([corrected], settings, Jan2026, new DateOnly(2026, 10, 31), posted)
            .Sum(e => e.Amount).Should().Be(16_666.67m);

        // November carries the 3 333.33 missing on January to October, December is the plan's month.
        var later = FixedAssetDepreciationSource.Entries([corrected], settings, new DateOnly(2026, 11, 1),
                                                         new DateOnly(2026, 12, 31), posted);
        later.Select(e => e.Amount).Should().Equal(5_333.33m, 2_000m);
        later[0].Label.Should().Contain("régul.");
        (posted.Sum(p => p.Amount) + later.Sum(e => e.Amount)).Should().Be(24_000m, "two years of 120 000 over five");
    }

    [Fact]
    public void I2_a_disposal_entered_after_its_months_were_exported_is_reversed_in_the_next_export()
    {
        var id = Guid.NewGuid();
        var posted = Export(Asset(100_000m, Jan2026, id: id), [], Jan2026, new DateOnly(2026, 9, 30));
        posted.Sum(p => p.Amount).Should().Be(15_000m);

        // Sold on 30 June, entered in October: July to September were booked for nothing.
        var disposed = Asset(100_000m, Jan2026, disposal: new DateOnly(2026, 6, 30), id: id);
        var entries = FixedAssetDepreciationSource.Entries([disposed], FixedAssetSettings.Default,
                                                           new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), posted);

        var reversal = entries.Should().ContainSingle().Subject;
        (reversal.Date, reversal.Amount, reversal.ChargeAccount, reversal.DepreciationAccount)
            .Should().Be((new DateOnly(2026, 10, 31), 5_000m, "28332", "6193"),
                         "a reversal debits the depreciation and credits the charge");
        reversal.Label.Should().StartWith("Reprise amort.");
    }

    [Fact]
    public void I2_a_cancelled_disposal_books_the_months_never_exported_and_does_not_book_them_twice()
    {
        var id = Guid.NewGuid();
        var disposed = Asset(100_000m, Jan2026, disposal: new DateOnly(2026, 6, 30), id: id);
        var posted = Export(disposed, [], Jan2026, new DateOnly(2026, 9, 30));
        posted.Sum(p => p.Amount).Should().Be(10_000m);

        var kept = Asset(100_000m, Jan2026, id: id);
        var october = Export(kept, posted, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        october.Should().ContainSingle().Which.Amount.Should().Be(6_666.67m, "July to October");

        // July to September exported afterwards: already taken up by October.
        Export(kept, [.. posted, .. october], new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)).Should().BeEmpty();
    }

    [Fact]
    public void I2_an_asset_never_exported_follows_its_plan_as_before()
    {
        var asset = Asset(60_000m, Jan2026);
        FixedAssetDepreciationSource.Entries([asset], FixedAssetSettings.Default, new DateOnly(2026, 5, 1),
                                             new DateOnly(2026, 6, 30))
            .Select(e => e.Amount).Should().Equal(1_000m, 1_000m);
    }

    [Fact]
    public async Task I2_the_months_are_recorded_when_the_file_is_produced_not_when_it_is_checked()
    {
        var site = Guid.NewGuid();
        var recorder = new RecordingSource();
        var service = new AccountingExportService(
            ReviewFixesBilling2Tests.Stub<IAccountingQueries>.With(
                ("GetSitesAsync", _ => Task.FromResult<IReadOnlyList<AccountingSiteOption>>([new(site, "A", "Site A")])),
                ("GetSettingValuesAsync", _ => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                    new Dictionary<string, string?>()))),
            null!, [recorder], null!, null!,
            ReviewFixesBilling2Tests.Stub<IPermissionGuard>.Create(),
            ReviewFixesBilling2Tests.Stub<ICurrentUser>.With(
                ("get_SiteId", _ => site), ("get_AccessibleSiteIds", _ => (IReadOnlyList<Guid>)[site])));

        var request = new AccountingExportRequest(Jan2026, new DateOnly(2026, 1, 31), [site],
                                                  AccountingJournals.Depreciation, AccountingExportLayout.Sage);

        (await service.PrepareAsync(request)).Value.Entries.Should().ContainSingle();
        recorder.Recorded.Should().BeEmpty("the check records nothing");

        (await service.ExportAsync(request)).IsSuccess.Should().BeTrue();
        recorder.Recorded.Should().Equal((site, Jan2026, new DateOnly(2026, 1, 31)));
    }

    private sealed class RecordingSource : IDepreciationEntrySource, IDepreciationExportRecorder
    {
        public List<(Guid, DateOnly, DateOnly)> Recorded { get; } = [];

        public Task<IReadOnlyList<DepreciationEntry>> GetEntriesAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                     CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DepreciationEntry>>(
                [new DepreciationEntry(new DateOnly(2026, 1, 31), "AN-1", "Dot. amort.", "6193", "28332", 100m)]);

        public Task RecordExportedAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            Recorded.Add((siteId, from, to));
            return Task.CompletedTask;
        }
    }

    // ---- I3 ------------------------------------------------------------------------------------------------

    [Fact]
    public void I3_the_year_end_totals_leave_out_the_assets_disposed_of_in_the_year()
    {
        var a = Asset(100_000m, new DateOnly(2024, 1, 1)) with { Code = "A" };
        var b = Asset(50_000m, new DateOnly(2024, 1, 1), disposal: new DateOnly(2026, 6, 30)) with
        {
            Code = "B", DisposalPrice = 25_000m
        };

        var register = FixedAssetService.BuildRegister(2026, [a, b], [], FixedAssetSettings.Default);

        (register.GrossValue, register.Closing, register.NetBookValue).Should().Be((100_000m, 60_000m, 40_000m));
        (register.GrossOut, register.DepreciationOut).Should().Be((50_000m, 25_000m));

        var sold = register.Assets.Single(r => r.Asset.Code == "B");
        (sold.ClosingGross, sold.Closing, sold.NetBookValue, sold.DisposalResult).Should().Be((0m, 0m, 0m, 0m));

        var headers = FixedAssetRegisterReport.FrenchHeaders;
        var total = FixedAssetRegisterReport.TotalCells("Total", register.Assets);
        total.Should().HaveCount(headers.Count);
        total[headers.ToList().IndexOf("Sorties valeur brute")].Should().Be(FixedAssetRegisterReport.Amount(50_000m));
        total[headers.ToList().IndexOf("Valeur brute fin")].Should().Be(FixedAssetRegisterReport.Amount(100_000m));
        total[headers.ToList().IndexOf("VNA")].Should().Be(FixedAssetRegisterReport.Amount(40_000m));
        FixedAssetRegisterReport.Cells(sold, "c", "m").Should().HaveCount(headers.Count);
    }

    [Fact]
    public void I3_the_lines_for_equipment_placed_by_a_supplier_keep_the_columns()
    {
        var row = new SupplierProvidedRow(Guid.NewGuid(), "AN-9", new("Automate"), "X", "MAD-9",
                                          InstrumentContractKind.Placement, Jan2026, new DateOnly(2029, 12, 31));
        FixedAssetRegisterReport.ProvidedCells(row, "Automate", "Mis à disposition")
            .Should().HaveCount(FixedAssetRegisterReport.FrenchHeaders.Count);
    }

    // ---- I5 ------------------------------------------------------------------------------------------------

    [Fact]
    public void I5_texts_longer_than_their_columns_are_refused_with_a_message()
    {
        FixedAsset Valid() => new("AN-1", "Automate", FixedAssetCategory.LaboratoryEquipment, Guid.NewGuid())
        {
            AcquisitionDate = Jan2026, InServiceDate = Jan2026, CostExcludingVatMad = 1_000m, UsefulLifeYears = 5
        };

        Valid().Validate().IsSuccess.Should().BeTrue();

        var label = Valid();
        label.Label = new string('x', 201);
        label.Validate().Errors.Should().Contain(e => e.ResourceKey == "Asset_LabelTooLong");

        var notes = Valid();
        notes.Notes = new string('x', 1_001);
        notes.Validate().Errors.Should().Contain(e => e.ResourceKey == "Asset_NotesTooLong");

        var reference = Valid();
        reference.InvoiceReference = new string('x', 61);
        reference.Validate().Errors.Should().Contain(e => e.ResourceKey == "Asset_InvoiceReferenceTooLong");

        Valid().RecordDisposal(Jan2026, 0m, new string('x', 501)).Errors
            .Should().Contain(e => e.ResourceKey == "Asset_DisposalReasonTooLong");
        Valid().RecordDisposal(Jan2026, 0m, new string('x', 500)).IsSuccess.Should().BeTrue();
    }
}
