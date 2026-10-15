using FluentAssertions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Accounting;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Assets;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;
using MarocLIS.Domain.Sequencing;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MarocLIS.Tests.Domain.ReviewFixesHrTests;

namespace MarocLIS.Tests.Domain;

/// <summary>
/// Lot G, fixed assets: the depreciation plans (straight-line pro rata from the month put into
/// service, declining with the Moroccan coefficients and the switch to straight-line), disposals, the
/// register of a year, the depreciation entries handed to the accounting export, the accounts, and
/// the refusal to capitalise equipment a supplier provides.
/// </summary>
public sealed class FinanceGTests
{
    private static readonly DateOnly Jan2026 = new(2026, 1, 1);

    private static DepreciationTerms Straight(decimal gross, int years, DateOnly inService, DateOnly? acquired = null,
                                              DateOnly? disposal = null, decimal residual = 0m) =>
        new(gross, residual, DepreciationMethod.StraightLine, years, acquired ?? inService, inService, disposal);

    // ---- Straight-line --------------------------------------------------------------------------------

    [Fact]
    public void Straight_line_runs_pro_rata_by_month_from_the_month_put_into_service()
    {
        // Bought in January, put into service on 15 March: March counts, January and February do not.
        var schedule = Depreciation.Compute(Straight(120_000m, 5, new DateOnly(2026, 3, 15), new DateOnly(2026, 1, 20)));

        schedule.Months.Should().HaveCount(60);
        schedule.Months[0].Should().Be(new DepreciationMonth(2026, 3, new DateOnly(2026, 3, 31), 2_000m));
        schedule.Months[^1].Date.Should().Be(new DateOnly(2031, 2, 28));
        schedule.Years.Select(y => (y.Year, y.Charge)).Should().Equal(
            (2026, 20_000m), (2027, 24_000m), (2028, 24_000m), (2029, 24_000m), (2030, 24_000m), (2031, 4_000m));
        schedule.Years[0].Rate.Should().Be(0.2m);
        schedule.Years.Should().OnlyContain(y => y.StraightLine);
        schedule.Total.Should().Be(120_000m);
        schedule.NetBookValueAt(new DateOnly(2026, 12, 31)).Should().Be(100_000m);
    }

    [Fact]
    public void Straight_line_ends_exactly_on_the_base_to_the_centime()
    {
        var schedule = Depreciation.Compute(Straight(1_000m, 3, new DateOnly(2026, 2, 1)));

        schedule.Total.Should().Be(1_000m);
        schedule.Months.Should().OnlyContain(m => m.Amount >= 27.77m && m.Amount <= 27.78m);
    }

    [Fact]
    public void Residual_value_is_not_depreciated()
    {
        var schedule = Depreciation.Compute(Straight(60_000m, 5, Jan2026, residual: 6_000m));

        schedule.Total.Should().Be(54_000m);
        schedule.NetBookValueAt(new DateOnly(2031, 12, 31)).Should().Be(6_000m);
    }

    // ---- Declining balance -------------------------------------------------------------------------------

    [Theory]
    [InlineData(2, null)]
    [InlineData(3, 1.5)]
    [InlineData(4, 1.5)]
    [InlineData(5, 2.0)]
    [InlineData(6, 2.0)]
    [InlineData(7, 3.0)]
    [InlineData(10, 3.0)]
    public void Declining_coefficients_follow_the_useful_life(int years, double? coefficient) =>
        Depreciation.DecliningCoefficient(years).Should().Be(coefficient is { } c ? (decimal)c : null);

    [Fact]
    public void Declining_starts_from_the_acquisition_month_and_switches_to_straight_line()
    {
        // 100 000 MAD over five years: 20 % × 2 = 40 %. Acquired in July: six months the first year.
        var terms = new DepreciationTerms(100_000m, 0m, DepreciationMethod.Declining, 5,
                                          new DateOnly(2024, 7, 10), new DateOnly(2024, 9, 1));
        var schedule = Depreciation.Compute(terms);

        schedule.Years.Select(y => (y.Year, y.Charge, y.StraightLine)).Should().Equal(
            (2024, 20_000m, false),   // 100 000 × 40 % × 6/12
            (2025, 32_000m, false),   // 80 000 × 40 %
            (2026, 19_200m, false),   // 48 000 × 40 % (straight-line would give 16 000)
            (2027, 14_400m, true),    // 28 800 ÷ 2 > 28 800 × 40 %
            (2028, 14_400m, true));
        schedule.Years[0].Rate.Should().Be(0.4m);
        schedule.Years[0].Months.Should().Be(6);
        schedule.Months[0].Should().Be(new DepreciationMonth(2024, 7, new DateOnly(2024, 7, 31), 3_333.33m));
        schedule.Total.Should().Be(100_000m);
    }

    [Fact]
    public void A_life_under_three_years_cannot_be_declining()
    {
        var asset = NewAsset(cost: 10_000m);
        asset.Method = DepreciationMethod.Declining;
        asset.UsefulLifeYears = 2;

        asset.Validate().Errors.Should().Contain(e => e.ResourceKey == "Asset_DecliningNeedsThreeYears");
    }

    // ---- Asset and disposal -----------------------------------------------------------------------------

    private static FixedAsset NewAsset(decimal cost = 120_000m, decimal vat = 0m, bool recoverable = false,
                                       FixedAssetCategory category = FixedAssetCategory.LaboratoryEquipment) =>
        new("an-01", "Automate de biochimie", category, Guid.NewGuid())
        {
            AcquisitionDate = Jan2026, InServiceDate = Jan2026, CostExcludingVatMad = cost, VatAmountMad = vat,
            VatRecoverable = recoverable, Method = DepreciationMethod.StraightLine, UsefulLifeYears = 5
        };

    [Fact]
    public void Vat_not_recovered_is_part_of_the_depreciated_value()
    {
        NewAsset(cost: 100_000m, vat: 20_000m).GrossValueMad.Should().Be(120_000m);
        NewAsset(cost: 100_000m, vat: 20_000m, recoverable: true).GrossValueMad.Should().Be(100_000m);
        NewAsset().Code.Should().Be("AN-01");
    }

    [Fact]
    public void The_asset_validates_its_dates_amounts_and_life()
    {
        var asset = NewAsset();
        asset.InServiceDate = Jan2026.AddDays(-1);
        asset.UsefulLifeYears = 0;
        asset.ResidualValueMad = 200_000m;

        asset.Validate().Errors.Select(e => e.ResourceKey).Should().Contain(
            ["Asset_InServiceBeforeAcquisition", "Asset_UsefulLifeOutOfRange", "Asset_ResidualOutOfRange"]);
        NewAsset().Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_disposal_stops_depreciation_at_its_month_and_can_be_cancelled()
    {
        var asset = NewAsset();
        asset.RecordDisposal(new DateOnly(2027, 6, 10), 50_000m, "").Errors.Single().ResourceKey
             .Should().Be("Asset_DisposalReasonRequired");
        asset.RecordDisposal(new DateOnly(2027, 6, 10), 50_000m, "Cédé au laboratoire X").IsSuccess.Should().BeTrue();
        asset.RecordDisposal(new DateOnly(2027, 7, 1), 1m, "encore").Errors.Single().ResourceKey.Should().Be("Asset_AlreadyDisposed");

        var schedule = Depreciation.Compute(asset.Terms);
        schedule.Months[^1].Should().Be(new DepreciationMonth(2027, 6, new DateOnly(2027, 6, 10), 2_000m));
        schedule.Total.Should().Be(36_000m);

        asset.CancelDisposal().IsSuccess.Should().BeTrue();
        Depreciation.Compute(asset.Terms).Total.Should().Be(120_000m);
    }

    // ---- Supplier-provided equipment ------------------------------------------------------------------------

    [Fact]
    public void Placement_comodat_and_lease_block_capitalisation_while_they_run()
    {
        var on = new DateOnly(2026, 3, 1);
        ProvidingContract Contract(InstrumentContractKind kind, bool active = true, int endYear = 2030) =>
            new("CT-" + kind, kind, active, new DateOnly(2024, 1, 1), new DateOnly(endYear, 12, 31));

        SupplierProvidedEquipment.Blocking([Contract(InstrumentContractKind.Placement)], on)!.Reference.Should().Be("CT-Placement");
        SupplierProvidedEquipment.Blocking([Contract(InstrumentContractKind.Comodat)], on).Should().NotBeNull();
        SupplierProvidedEquipment.Blocking([Contract(InstrumentContractKind.Lease)], on).Should().NotBeNull();

        // Bought outright, terminated, or bought out after the contract ended: it can be capitalised.
        SupplierProvidedEquipment.Blocking([Contract(InstrumentContractKind.Purchase)], on).Should().BeNull();
        SupplierProvidedEquipment.Blocking([Contract(InstrumentContractKind.Placement, active: false)], on).Should().BeNull();
        SupplierProvidedEquipment.Blocking([Contract(InstrumentContractKind.Placement, endYear: 2025)], on).Should().BeNull();
    }

    [Fact]
    public async Task Saving_an_asset_on_a_placed_analyser_is_refused_with_the_contract()
    {
        var instrument = Guid.NewGuid();
        var queries = new FakeAssetQueries
        {
            Contracts = [new ProvidingContract("MAD-2024-07", InstrumentContractKind.Placement, true,
                                               new DateOnly(2024, 1, 1), new DateOnly(2028, 12, 31))]
        };
        var (service, repository) = Service(queries);

        var saved = await service.SaveAsync(Request() with { InstrumentId = instrument });

        saved.IsFailure.Should().BeTrue();
        saved.FirstError.ResourceKey.Should().Be("Asset_SupplierProvided");
        saved.FirstError.Args.Should().Contain("MAD-2024-07");
        repository.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_instrument_is_capitalised_once()
    {
        var queries = new FakeAssetQueries { CapitalisedAs = "AN-01" };
        var (service, _) = Service(queries);

        var saved = await service.SaveAsync(Request() with { InstrumentId = Guid.NewGuid() });

        saved.FirstError.ResourceKey.Should().Be("Asset_InstrumentAlreadyCapitalised");
    }

    [Fact]
    public async Task A_valid_asset_is_saved_with_its_rounded_amounts()
    {
        var (service, repository) = Service(new FakeAssetQueries());

        var saved = await service.SaveAsync(Request() with { CostExcludingVatMad = 85_000.004m, InstrumentId = Guid.NewGuid() });

        saved.IsSuccess.Should().BeTrue();
        var asset = repository.Items.Single();
        asset.Code.Should().Be("CENT-02");
        asset.CostExcludingVatMad.Should().Be(85_000m);
        asset.SiteId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Without_the_manage_right_nothing_is_saved()
    {
        var (service, repository) = Service(new FakeAssetQueries(), new OnlyGuard(Permissions.Assets.View));

        (await service.SaveAsync(Request())).FirstError.Code.Should().Be("forbidden");
        (await service.GetRegisterAsync(2026)).IsSuccess.Should().BeTrue();
        repository.Items.Should().BeEmpty();
    }

    [Fact]
    public void An_instrument_becomes_a_draft_asset_with_its_dates_and_supplier()
    {
        var supplier = Guid.NewGuid();
        var bought = new AssetInstrumentOption(Guid.NewGuid(), "CENT-02", new MultilingualText("Centrifugeuse"),
                                               new DateOnly(2025, 4, 2), new DateOnly(2025, 4, 20), supplier, null, null);
        var placed = bought with { Id = Guid.NewGuid(), Code = "AN-03", ProvidedBy = "MAD-1" };
        var noDate = bought with { Id = Guid.NewGuid(), Code = "PIP-1", PurchaseDate = null };
        var done = bought with { Id = Guid.NewGuid(), Code = "FRG-1", AssetCode = "FRG-1" };

        FixedAssetService.Candidates(new FixedAssetOptions([], [placed, noDate, done, bought]))
                         .Should().ContainSingle().Which.Code.Should().Be("CENT-02");

        var draft = FixedAssetService.DraftFromInstrument(bought);
        draft.Should().BeEquivalentTo(new
        {
            Code = "CENT-02", Label = "Centrifugeuse", Category = FixedAssetCategory.LaboratoryEquipment,
            AcquisitionDate = new DateOnly(2025, 4, 2), InServiceDate = new DateOnly(2025, 4, 20),
            SupplierId = (Guid?)supplier, InstrumentId = (Guid?)bought.Id, UsefulLifeYears = 5
        });
    }

    // ---- Register and report --------------------------------------------------------------------------------

    private static FixedAssetSource Source(string code, decimal gross, DateOnly acquired, int years = 5,
                                           FixedAssetCategory category = FixedAssetCategory.LaboratoryEquipment,
                                           DateOnly? disposal = null, decimal? price = null) =>
        new(Guid.NewGuid(), code, "Bien " + code, category, Guid.NewGuid(), acquired, acquired, gross, 0m, false,
            DepreciationMethod.StraightLine, years, 0m, disposal, price, disposal is null ? null : "Cession", "Fournisseur",
            "F-" + code, null, null);

    [Fact]
    public void The_register_of_a_year_gives_opening_charge_closing_and_disposal_result()
    {
        var sources = new[]
        {
            Source("A", 60_000m, new DateOnly(2025, 1, 1)),
            Source("B", 12_000m, new DateOnly(2026, 7, 1), category: FixedAssetCategory.ItEquipment),
            Source("C", 24_000m, new DateOnly(2024, 1, 1), disposal: new DateOnly(2026, 3, 15), price: 10_000m),
            Source("D", 9_000m, new DateOnly(2027, 1, 1)),
            Source("E", 9_000m, new DateOnly(2020, 1, 1), disposal: new DateOnly(2025, 6, 1), price: 0m)
        };
        var provided = new SupplierProvidedRow(Guid.NewGuid(), "AN-09", new MultilingualText("Automate placé"),
                                               "Fournisseur X", "MAD-9", InstrumentContractKind.Placement,
                                               new DateOnly(2025, 1, 1), new DateOnly(2029, 12, 31));

        var register = FixedAssetService.BuildRegister(2026, sources, [provided], FixedAssetSettings.Default);

        register.Assets.Select(r => r.Asset.Code).Should().Equal("A", "C", "B");
        var a = register.Assets.Single(r => r.Asset.Code == "A");
        (a.Opening, a.Charge, a.Closing, a.NetBookValue).Should().Be((12_000m, 12_000m, 24_000m, 36_000m));
        a.Accounts.Should().Be(new CategoryAccounts("2332", "28332", "6193"));

        var b = register.Assets.Single(r => r.Asset.Code == "B");
        (b.Opening, b.Charge).Should().Be((0m, 1_200m));
        b.Accounts.Asset.Should().Be("2355");

        // 24 000 over five years from January 2024: 9 600 by the end of 2025, three months of 2026.
        var c = register.Assets.Single(r => r.Asset.Code == "C");
        (c.Opening, c.Charge, c.NetBookValue, c.DisposalResult).Should().Be((9_600m, 1_200m, 13_200m, -3_200m));

        register.Charge.Should().Be(14_400m);

        var lines = FixedAssetRegisterReport.Lines(register, FixedAssetRegisterReport.FrenchCategory,
                                                   m => m.ToString(), p => p.InstrumentName.Fr,
                                                   "Sous-total", "Total", "Mis à disposition");
        lines.Should().HaveCount(3 + 2 + 1 + 1);
        lines[^1][2].Should().Be("Mis à disposition");
        lines[^1][6].Should().BeEmpty("equipment placed by a supplier has no value in the register");
        lines[^2][1].Should().Be("Total");
        lines[^2][11].Should().Be(FixedAssetRegisterReport.Amount(14_400m));

        var csv = System.Text.Encoding.UTF8.GetString(FixedAssetRegisterReport.ToCsv(register));
        csv.Should().StartWith("﻿Code;Désignation;Catégorie");
        csv.Should().Contain("Mis à disposition").And.Contain("MAD-9");
    }

    // ---- Depreciation entries for the accounting export ----------------------------------------------------

    [Fact]
    public void Depreciation_entries_are_monthly_on_the_category_accounts()
    {
        var settings = FixedAssetSettings.FromValues(new Dictionary<string, string?>
        {
            [FixedAssetSettings.DepreciationAccountKey(FixedAssetCategory.ItEquipment)] = "283551"
        });
        var sources = new[]
        {
            Source("A", 60_000m, new DateOnly(2025, 1, 1)),
            Source("B", 12_000m, new DateOnly(2026, 7, 1), category: FixedAssetCategory.ItEquipment),
            Source("C", 24_000m, new DateOnly(2024, 1, 1), disposal: new DateOnly(2026, 3, 15), price: 10_000m)
        };

        var entries = FixedAssetDepreciationSource.Entries(sources, settings, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

        entries.Should().HaveCount(6);
        entries.Should().OnlyContain(e => e.ChargeAccount == "6193");
        entries.Where(e => e.AssetCode == "A").Select(e => e.Date).Should().Equal(
            new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31));
        entries.Single(e => e.AssetCode == "C" && e.Date.Month == 3).Date.Should().Be(new DateOnly(2026, 3, 15));
        entries.Where(e => e.AssetCode == "A").Should().OnlyContain(e => e.Amount == 1_000m && e.DepreciationAccount == "28332");
        entries[0].Label.Should().StartWith("Dot. amort. 01/2026");

        var july = FixedAssetDepreciationSource.Entries(sources, settings, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        july.Single(e => e.AssetCode == "B").DepreciationAccount.Should().Be("283551");
        july.Should().NotContain(e => e.AssetCode == "C");

        // The entries of a year add up to the register's charge.
        FixedAssetDepreciationSource.Entries(sources, settings, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31))
            .Sum(e => e.Amount)
            .Should().Be(FixedAssetService.BuildRegister(2026, sources, [], settings).Charge);
    }

    [Fact]
    public async Task The_source_is_registered_for_the_accounting_export()
    {
        var services = new ServiceCollection();
        services.AddMarocLisApplicationForTests();
        services.AddScoped<IFixedAssetQueries>(_ => new FakeAssetQueries { Assets = [Source("A", 12_000m, Jan2026, 1)] });

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IDepreciationEntrySource>().ToList();

        sources.Should().ContainSingle().Which.Should().BeOfType<FixedAssetDepreciationSource>();
        (await sources[0].GetEntriesAsync(Guid.NewGuid(), Jan2026, new DateOnly(2026, 12, 31)))
            .Sum(e => e.Amount).Should().Be(12_000m);
    }

    // ---- Settings and permissions --------------------------------------------------------------------------

    [Fact]
    public void Accounts_default_to_the_cgnc_and_reject_wrong_classes()
    {
        var defaults = FixedAssetSettings.Default;
        defaults.For(FixedAssetCategory.Vehicles).Should().Be(new CategoryAccounts("2340", "28340", "6193"));
        defaults.For(FixedAssetCategory.Furniture).Asset.Should().Be("2351");
        defaults.IsValid().Should().BeTrue();

        var values = defaults.ToValues();
        FixedAssetSettings.FromValues(values.ToDictionary(v => v.Key, v => (string?)v.Value))
                          .Should().BeEquivalentTo(defaults);

        var wrong = defaults with
        {
            Accounts = defaults.Accounts.ToDictionary(a => a.Key, a => a.Key == FixedAssetCategory.Other
                                                                          ? a.Value with { Depreciation = "2380" }
                                                                          : a.Value)
        };
        wrong.IsValid().Should().BeFalse();
        (defaults with
        {
            Accounts = defaults.Accounts.ToDictionary(a => a.Key, a => a.Value with { Charge = "61A3" })
        }).IsValid().Should().BeFalse();
    }

    [Fact]
    public void Assets_rights_are_shipped_to_direction_accounting_and_quality()
    {
        var type = typeof(LisDbContext).Assembly.GetType("MarocLIS.Infrastructure.Persistence.RolePermissionSets")!;
        var roles = new Dictionary<string, string[]>();
        foreach (var definition in (System.Collections.IEnumerable)type
                     .GetMethod("Build", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                                         | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!)
        {
            var tuple = definition.GetType();
            roles[(string)tuple.GetField("Item1")!.GetValue(definition)!] = (string[])tuple.GetField("Item3")!.GetValue(definition)!;
        }

        roles[SystemRoles.Administrator].Should().Contain([Permissions.Assets.View, Permissions.Assets.Manage]);
        roles[SystemRoles.ResponsibleBiologist].Should().Contain([Permissions.Assets.View, Permissions.Assets.Manage]);
        roles[SystemRoles.Accountant].Should().Contain([Permissions.Assets.View, Permissions.Assets.Manage]);
        roles[SystemRoles.QualityManager].Should().Contain(Permissions.Assets.View)
                                         .And.NotContain(Permissions.Assets.Manage);
        roles[SystemRoles.Technician].Should().NotContain(Permissions.Assets.View);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static SaveFixedAssetRequest Request() => new()
    {
        Code = "cent-02", Label = "Centrifugeuse", Category = FixedAssetCategory.LaboratoryEquipment,
        AcquisitionDate = new DateOnly(2026, 2, 1), InServiceDate = new DateOnly(2026, 2, 10),
        CostExcludingVatMad = 85_000m, Method = DepreciationMethod.StraightLine, UsefulLifeYears = 5
    };

    private static (FixedAssetService, InMemoryRepository<FixedAsset>) Service(FakeAssetQueries queries,
                                                                             IPermissionGuard? guard = null)
    {
        var repository = new InMemoryRepository<FixedAsset>();
        return (new FixedAssetService(repository, queries, new NoSettings(), new FakeUnitOfWork(), guard ?? new AllowAll(),
                                      new FakeUser(Permissions.Assets.Manage), new FixedClock()), repository);
    }

    private sealed class OnlyGuard(params string[] granted) : IPermissionGuard
    {
        public Result Require(string permission) =>
            granted.Contains(permission) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden", permission));
        public Result RequireAny(params string[] permissions) =>
            permissions.Any(granted.Contains) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Result RequireAll(params string[] permissions) =>
            permissions.All(granted.Contains) ? Result.Success() : Result.Failure(Error.Forbidden("Forbidden"));
        public Task<Result> RequireBiologicalValidationAsync(string departmentCode, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class NoSettings : IConfigurationWriter
    {
        public Task<NumberSequence?> GetSequenceAsync(Guid sequenceId, CancellationToken ct = default) =>
            Task.FromResult<NumberSequence?>(null);
        public Task<AppSetting?> GetSettingAsync(string key, Guid? siteId, CancellationToken ct = default) =>
            Task.FromResult<AppSetting?>(null);
        public Task AddSettingAsync(AppSetting setting, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeAssetQueries : IFixedAssetQueries
    {
        public IReadOnlyList<FixedAssetSource> Assets { get; init; } = [];
        public IReadOnlyList<ProvidingContract> Contracts { get; init; } = [];
        public string? CapitalisedAs { get; init; }

        public Task<IReadOnlyList<FixedAssetSource>> GetAssetsAsync(Guid siteId, CancellationToken ct = default) =>
            Task.FromResult(Assets);
        public Task<IReadOnlyList<SupplierProvidedRow>> GetSupplierProvidedAsync(Guid siteId, DateOnly today,
                                                                                 CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SupplierProvidedRow>>([]);
        public Task<IReadOnlyList<ProvidingContract>> GetProvidingContractsAsync(Guid instrumentId,
                                                                                 CancellationToken ct = default) =>
            Task.FromResult(Contracts);
        public Task<bool> CodeExistsAsync(Guid siteId, string code, Guid? exceptAssetId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<string?> AssetCodeForInstrumentAsync(Guid instrumentId, Guid? exceptAssetId,
                                                         CancellationToken ct = default) =>
            Task.FromResult(CapitalisedAs);
        public Task<FixedAssetOptions> GetOptionsAsync(Guid siteId, Guid? assetId, DateOnly today,
                                                       CancellationToken ct = default) =>
            Task.FromResult(new FixedAssetOptions([], []));
        public Task<IReadOnlyList<AssetInvoiceOption>> GetSupplierInvoicesAsync(Guid siteId, Guid supplierId,
                                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssetInvoiceOption>>([]);
        public Task<AssetInstrumentSummary?> GetInstrumentSummaryAsync(Guid instrumentId, CancellationToken ct = default) =>
            Task.FromResult<AssetInstrumentSummary?>(null);
        public Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix,
                                                                                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
    }
}

internal static class FinanceGServiceCollection
{
    /// <summary>The application registrations, without the configuration sections the tests do not need.</summary>
    public static IServiceCollection AddMarocLisApplicationForTests(this IServiceCollection services) =>
        MarocLIS.Application.ApplicationServiceCollectionExtensions.AddMarocLisApplication(
            services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
}
