using System.Globalization;
using System.Text;
using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Administration;
using MarocLIS.Application.Inventory;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Assets;

// ---- Read models -------------------------------------------------------------------------------

/// <summary>One asset as the register and the depreciation entries need it.</summary>
public sealed record FixedAssetSource(
    Guid Id,
    string Code,
    string Label,
    FixedAssetCategory Category,
    Guid SiteId,
    DateOnly AcquisitionDate,
    DateOnly InServiceDate,
    decimal CostExcludingVat,
    decimal Vat,
    bool VatRecoverable,
    DepreciationMethod Method,
    int UsefulLifeYears,
    decimal ResidualValue,
    DateOnly? DisposalDate,
    decimal? DisposalPrice,
    string? DisposalReason,
    string? SupplierName,
    string? InvoiceReference,
    Guid? InstrumentId,
    string? InstrumentCode)
{
    public decimal GrossValue => FixedAsset.GrossValue(CostExcludingVat, Vat, VatRecoverable);

    public DepreciationTerms Terms =>
        new(GrossValue, ResidualValue, Method, UsefulLifeYears, AcquisitionDate, InServiceDate, DisposalDate);
}

/// <summary>One asset's line in the register for a financial year.</summary>
/// <param name="Opening">Depreciation accumulated at 1 January.</param>
/// <param name="Charge">The year's depreciation (dotation de l'exercice).</param>
/// <param name="Closing">Depreciation accumulated at 31 December: zero once the asset has left the register.</param>
/// <param name="NetBookValue">At 31 December: zero once the asset has left the register.</param>
/// <param name="DisposalResult">Disposal price less the net book value at the disposal, when disposed of in the year.</param>
/// <param name="GrossOut">Gross value leaving the register in the year (sorties): the asset's, when disposed of in it.</param>
/// <param name="DepreciationOut">Depreciation accumulated at the disposal, leaving with the asset.</param>
public sealed record FixedAssetRegisterRow(
    FixedAssetSource Asset,
    CategoryAccounts Accounts,
    decimal Rate,
    decimal Opening,
    decimal Charge,
    decimal Closing,
    decimal NetBookValue,
    decimal? DisposalResult,
    decimal GrossOut = 0m,
    decimal DepreciationOut = 0m)
{
    /// <summary>Gross value at 31 December: nothing for an asset disposed of in the year.</summary>
    public decimal ClosingGross => Money.Round(Asset.GrossValue - GrossOut);
}

/// <summary>An analyser placed by its supplier: in the laboratory, not in the assets.</summary>
public sealed record SupplierProvidedRow(
    Guid InstrumentId,
    string InstrumentCode,
    MultilingualText InstrumentName,
    string SupplierName,
    string ContractReference,
    InstrumentContractKind Kind,
    DateOnly StartDate,
    DateOnly EndDate);

/// <summary>The register for one financial year: the assets, and the equipment placed by suppliers.</summary>
public sealed record FixedAssetRegister(
    int Year,
    IReadOnlyList<FixedAssetRegisterRow> Assets,
    IReadOnlyList<SupplierProvidedRow> SupplierProvided)
{
    /// <summary>Gross value at 31 December, without the assets disposed of in the year.</summary>
    public decimal GrossValue => Money.Round(Assets.Sum(a => a.ClosingGross));
    public decimal GrossOut => Money.Round(Assets.Sum(a => a.GrossOut));
    public decimal DepreciationOut => Money.Round(Assets.Sum(a => a.DepreciationOut));
    public decimal Opening => Money.Round(Assets.Sum(a => a.Opening));
    public decimal Charge => Money.Round(Assets.Sum(a => a.Charge));
    public decimal Closing => Money.Round(Assets.Sum(a => a.Closing));
    public decimal NetBookValue => Money.Round(Assets.Sum(a => a.NetBookValue));
}

/// <summary>A supplier, as the asset editor offers it.</summary>
public sealed record AssetLookup(Guid Id, string Code, string Name, bool IsActive = true);

/// <summary>
/// An instrument of the equipment module, as the asset editor offers it.
/// </summary>
/// <param name="ProvidedBy">Reference of the placement, comodat or lease contract that covers it today, if any.</param>
/// <param name="AssetCode">The asset already linked to it, if any.</param>
public sealed record AssetInstrumentOption(
    Guid Id,
    string Code,
    MultilingualText Name,
    DateOnly? PurchaseDate,
    DateOnly? CommissioningDate,
    Guid? SupplierId,
    string? ProvidedBy,
    string? AssetCode);

public sealed record FixedAssetOptions(
    IReadOnlyList<AssetLookup> Suppliers,
    IReadOnlyList<AssetInstrumentOption> Instruments);

/// <summary>A supplier invoice an asset can be linked to.</summary>
public sealed record AssetInvoiceOption(Guid Id, string InvoiceNumber, DateOnly InvoiceDate,
                                        decimal TotalExcludingVat, decimal TotalVat);

/// <summary>One intervention on the asset's instrument, read from the equipment module.</summary>
public sealed record AssetMaintenanceRow(
    DateOnly PerformedOn,
    MaintenanceKind Kind,
    MultilingualText Description,
    decimal? DowntimeHours,
    decimal? CostMad,
    string? Technician,
    bool CheckPassed);

/// <summary>The instrument behind an asset: its state, its upkeep and its breakdowns, read-only.</summary>
public sealed record AssetInstrumentSummary(
    Guid InstrumentId,
    string Code,
    MultilingualText Name,
    EquipmentStatus Status,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    DateOnly? NextPreventiveMaintenanceDue,
    DateOnly? NextCalibrationDue,
    DateOnly? MaintenanceContractValidUntil,
    IReadOnlyList<AssetMaintenanceRow> Interventions)
{
    /// <summary>Breakdowns: the corrective interventions.</summary>
    public int Breakdowns => Interventions.Count(i => i.Kind == MaintenanceKind.Corrective);

    public decimal DowntimeHours => Interventions.Sum(i => i.DowntimeHours ?? 0m);

    public decimal MaintenanceCost => Money.Round(Interventions.Sum(i => i.CostMad ?? 0m));
}

/// <summary>An asset as the editor shows it.</summary>
public sealed record FixedAssetDetail(
    SaveFixedAssetRequest Values,
    DateOnly? DisposalDate,
    decimal? DisposalPrice,
    string? DisposalReason);

public sealed record SaveFixedAssetRequest
{
    public Guid? Id { get; init; }
    public required string Code { get; init; }
    public required string Label { get; init; }
    public FixedAssetCategory Category { get; init; }
    public required DateOnly AcquisitionDate { get; init; }
    public required DateOnly InServiceDate { get; init; }
    public decimal CostExcludingVatMad { get; init; }
    public decimal VatAmountMad { get; init; }
    public bool VatRecoverable { get; init; }
    public Guid? SupplierId { get; init; }
    public string? InvoiceReference { get; init; }
    public Guid? SupplierInvoiceId { get; init; }
    public Guid? InstrumentId { get; init; }
    public DepreciationMethod Method { get; init; }
    public int UsefulLifeYears { get; init; }
    public decimal ResidualValueMad { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// The fixed-assets register: entering the assets (from scratch or from an instrument of the
/// equipment module), their depreciation schedule, their disposal, the annual register report
/// (tableau des immobilisations et des amortissements), and the accounts it is kept under.
/// <para>
/// Equipment a supplier provides under a placement, comodat or lease contract is the supplier's
/// property: it is refused as an asset and listed apart, without depreciation.
/// </para>
/// </summary>
public sealed class FixedAssetService(
    IRepository<FixedAsset> assets,
    IFixedAssetQueries queries,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    ICurrentUser currentUser,
    IClock clock,
    IRepository<FixedAssetTransfer>? transfers = null)
{
    // ---- Reading ---------------------------------------------------------------------------------

    public async Task<Result<FixedAssetRegister>> GetRegisterAsync(int year, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.View);
        if (permitted.IsFailure) return Result.Failure<FixedAssetRegister>(permitted.FirstError);
        if (year is < 1900 or > 9999) return Result.Failure<FixedAssetRegister>(Error.Validation("Asset_YearOutOfRange"));

        var settings = await GetSettingsCoreAsync(ct);
        var sources = await queries.GetAssetsAsync(currentUser.SiteId, ct);
        // The equipment placed during the year shown, not today's.
        var provided = await queries.GetSupplierProvidedAsync(currentUser.SiteId, new DateOnly(year, 1, 1),
                                                              new DateOnly(year, 12, 31), ct);

        return Result.Success(BuildRegister(year, sources, provided, settings));
    }

    /// <summary>
    /// The register of a year: the assets acquired by its end and not disposed of before it began,
    /// with the depreciation accumulated at the opening, the year's charge, the closing and the net
    /// book value; an asset disposed of during the year shows the result of the disposal.
    /// <para>
    /// An asset disposed of during the year is no longer the laboratory's at 31 December: its gross
    /// value and the depreciation accumulated at the disposal leave the register in the "sorties"
    /// columns, and its closing depreciation and net book value are zero, so that the totals are
    /// those of the balance sheet.
    /// </para>
    /// </summary>
    public static FixedAssetRegister BuildRegister(int year, IEnumerable<FixedAssetSource> sources,
                                                   IReadOnlyList<SupplierProvidedRow> provided,
                                                   FixedAssetSettings settings)
    {
        var first = new DateOnly(year, 1, 1);
        var last = new DateOnly(year, 12, 31);
        var rows = new List<FixedAssetRegisterRow>();

        foreach (var asset in sources.Where(a => a.AcquisitionDate <= last && (a.DisposalDate is null || a.DisposalDate >= first))
                                     .OrderBy(a => a.Category).ThenBy(a => a.Code, StringComparer.Ordinal))
        {
            var schedule = Depreciation.Compute(asset.Terms);
            var opening = schedule.AccumulatedAt(first.AddDays(-1));
            var charge = schedule.ChargeBetween(first, last);
            var closing = Money.Round(opening + charge);
            var netBookValue = Money.Round(asset.GrossValue - closing);

            var disposedInYear = asset.DisposalDate is { } disposed && disposed <= last;
            decimal? result = disposedInYear ? Money.Round((asset.DisposalPrice ?? 0m) - netBookValue) : null;
            var (grossOut, depreciationOut) = disposedInYear ? (asset.GrossValue, closing) : (0m, 0m);
            if (disposedInYear) (closing, netBookValue) = (0m, 0m);

            var rate = schedule.Years.FirstOrDefault(y => y.Year == year)?.Rate
                       ?? (asset.Method == DepreciationMethod.Declining
                           ? Depreciation.DecliningRate(asset.UsefulLifeYears) ?? 0m
                           : Depreciation.StraightLineRate(asset.UsefulLifeYears));

            rows.Add(new FixedAssetRegisterRow(asset, settings.For(asset.Category), rate, opening, charge, closing,
                                               netBookValue, result, grossOut, depreciationOut));
        }

        return new FixedAssetRegister(year, rows, provided);
    }

    public async Task<Result<FixedAssetDetail>> GetAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.View);
        if (permitted.IsFailure) return Result.Failure<FixedAssetDetail>(permitted.FirstError);

        var asset = await assets.GetByIdAsync(assetId, ct);
        if (asset is null) return Result.Failure<FixedAssetDetail>(Error.NotFound("Asset_NotFound"));

        return Result.Success(new FixedAssetDetail(
            new SaveFixedAssetRequest
            {
                Id = asset.Id, Code = asset.Code, Label = asset.Label, Category = asset.Category,
                AcquisitionDate = asset.AcquisitionDate, InServiceDate = asset.InServiceDate,
                CostExcludingVatMad = asset.CostExcludingVatMad, VatAmountMad = asset.VatAmountMad,
                VatRecoverable = asset.VatRecoverable, SupplierId = asset.SupplierId,
                InvoiceReference = asset.InvoiceReference, SupplierInvoiceId = asset.SupplierInvoiceId,
                InstrumentId = asset.InstrumentId, Method = asset.Method, UsefulLifeYears = asset.UsefulLifeYears,
                ResidualValueMad = asset.ResidualValueMad, Notes = asset.Notes
            },
            asset.DisposalDate, asset.DisposalPriceMad, asset.DisposalReason));
    }

    /// <summary>An asset's plan, by year and by month, from the stored asset.</summary>
    public async Task<Result<DepreciationSchedule>> GetScheduleAsync(Guid assetId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.View);
        if (permitted.IsFailure) return Result.Failure<DepreciationSchedule>(permitted.FirstError);

        var asset = await assets.GetByIdAsync(assetId, ct);
        return asset is null
            ? Result.Failure<DepreciationSchedule>(Error.NotFound("Asset_NotFound"))
            : Result.Success(Depreciation.Compute(asset.Terms));
    }

    /// <summary>What the editor offers: the suppliers, and the instruments with their state.</summary>
    public async Task<Result<FixedAssetOptions>> GetOptionsAsync(Guid? assetId = null, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.Manage);
        if (permitted.IsFailure) return Result.Failure<FixedAssetOptions>(permitted.FirstError);

        return Result.Success(await queries.GetOptionsAsync(currentUser.SiteId, assetId, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<AssetInvoiceOption>>> GetSupplierInvoicesAsync(Guid supplierId,
                                                                                         CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.Manage);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<AssetInvoiceOption>>(permitted.FirstError);

        return Result.Success(await queries.GetSupplierInvoicesAsync(currentUser.SiteId, supplierId, ct));
    }

    /// <summary>
    /// The instruments that can become assets: bought (a purchase date is recorded), not yet in the
    /// register, and not placed by a supplier.
    /// </summary>
    public static IReadOnlyList<AssetInstrumentOption> Candidates(FixedAssetOptions options) =>
    [
        .. options.Instruments.Where(i => i.PurchaseDate is not null && i.AssetCode is null && i.ProvidedBy is null)
                              .OrderBy(i => i.Code, StringComparer.Ordinal)
    ];

    /// <summary>
    /// A new asset filled in from an instrument: its code, name, supplier and dates, laboratory
    /// equipment depreciated straight-line over the category's default life. The cost is left for
    /// the operator, the instrument record does not hold it.
    /// </summary>
    public static SaveFixedAssetRequest DraftFromInstrument(AssetInstrumentOption instrument)
    {
        var acquired = instrument.PurchaseDate ?? instrument.CommissioningDate ?? default;
        var inService = instrument.CommissioningDate is { } commissioned && commissioned >= acquired
            ? commissioned
            : acquired;

        return new SaveFixedAssetRequest
        {
            Code = instrument.Code,
            Label = instrument.Name.Fr,
            Category = FixedAssetCategory.LaboratoryEquipment,
            AcquisitionDate = acquired,
            InServiceDate = inService,
            SupplierId = instrument.SupplierId,
            InstrumentId = instrument.Id,
            Method = DepreciationMethod.StraightLine,
            UsefulLifeYears = FixedAssetCategories.DefaultUsefulLifeYears(FixedAssetCategory.LaboratoryEquipment)
        };
    }

    /// <summary>The instrument behind an asset, read from the equipment module.</summary>
    public async Task<Result<AssetInstrumentSummary>> GetInstrumentSummaryAsync(Guid instrumentId,
                                                                               CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.View);
        if (permitted.IsFailure) return Result.Failure<AssetInstrumentSummary>(permitted.FirstError);

        var summary = await queries.GetInstrumentSummaryAsync(instrumentId, ct);
        return summary is null
            ? Result.Failure<AssetInstrumentSummary>(Error.NotFound("Equipment_NotFound"))
            : Result.Success(summary);
    }

    // ---- Writing ---------------------------------------------------------------------------------

    public async Task<Result<Guid>> SaveAsync(SaveFixedAssetRequest request, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var code = FixedAsset.Normalise(request.Code);
        if (code.Length > FixedAsset.CodeMaxLength)
            return Result.Failure<Guid>(Error.Validation("Asset_CodeTooLong", FixedAsset.CodeMaxLength));
        if (code.Length > 0 && await queries.CodeExistsAsync(currentUser.SiteId, code, request.Id, ct))
            return Result.Failure<Guid>(Error.Conflict("Asset_CodeExists", code));

        if (request.InstrumentId is { } instrumentId)
        {
            var blocked = await CheckInstrumentAsync(instrumentId, request.AcquisitionDate, request.Id, ct);
            if (blocked.IsFailure) return Result.Failure<Guid>(blocked.Errors);
        }

        FixedAsset asset;
        (FixedAssetCategory Category, decimal Gross, Guid? InvoiceId)? before = null;
        if (request.Id is { } id)
        {
            var existing = await assets.GetByIdAsync(id, ct);
            if (existing is null) return Result.Failure<Guid>(Error.NotFound("Asset_NotFound"));
            asset = existing;
            before = (asset.Category, asset.GrossValueMad, asset.SupplierInvoiceId);
            asset.ChangeCode(code);
        }
        else
        {
            asset = new FixedAsset(code, request.Label ?? string.Empty, request.Category, currentUser.SiteId);
        }

        asset.Label = request.Label?.Trim() ?? string.Empty;
        asset.Category = request.Category;
        asset.AcquisitionDate = request.AcquisitionDate;
        asset.InServiceDate = request.InServiceDate;
        asset.CostExcludingVatMad = Money.Round(request.CostExcludingVatMad);
        asset.VatAmountMad = Money.Round(request.VatAmountMad);
        asset.VatRecoverable = request.VatRecoverable;
        asset.SupplierId = request.SupplierId;
        asset.InvoiceReference = Blank(request.InvoiceReference);
        asset.LinkSupplierInvoice(request.SupplierInvoiceId, clock.Now);
        asset.InstrumentId = request.InstrumentId;
        asset.Method = request.Method;
        asset.UsefulLifeYears = request.UsefulLifeYears;
        asset.ResidualValueMad = Money.Round(request.ResidualValueMad);
        asset.Notes = Blank(request.Notes);

        var valid = asset.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        if (before is { } old && old.Category != asset.Category)
            await RecordTransferAsync(asset, old.Category, old.Gross, old.InvoiceId, ct);

        if (request.Id is null) await assets.AddAsync(asset, ct);
        else assets.Update(asset);

        unitOfWork.AuditReason = request.Id is null
            ? $"Immobilisation {asset.Code} inscrite au registre"
            : $"Immobilisation {asset.Code} modifiée";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(asset.Id);
    }

    /// <summary>
    /// A change of category of an asset already in the accounting software — depreciation months
    /// exported, or its supplier invoice exported — is booked as a transfer (I8): the exported
    /// depreciation from the old 28xxx to the new one, the gross value from the old asset account to
    /// the new one, dated today, in the next export. The months already exported keep their accounts.
    /// Whether the accountant prefers the transfer dated the start of the financial year is to confirm.
    /// </summary>
    private async Task RecordTransferAsync(FixedAsset asset, FixedAssetCategory oldCategory, decimal oldGross,
                                           Guid? oldInvoiceId, CancellationToken ct)
    {
        if (transfers is null) return;

        var months = (await queries.GetDepreciationPostingsAsync(asset.SiteId, ct)).Where(p => p.AssetId == asset.Id).ToList();
        var (posted, exported) = (months.Count > 0, Money.Round(months.Sum(p => p.Amount)));
        var invoiceExported = oldInvoiceId is { } invoiceId && await queries.IsSupplierInvoiceExportedAsync(invoiceId, ct);
        if (!posted && !invoiceExported) return;

        var settings = await GetSettingsCoreAsync(ct);
        var (from, to) = (settings.For(oldCategory), settings.For(asset.Category));
        if (from.Asset == to.Asset && from.Depreciation == to.Depreciation) return;

        await transfers.AddAsync(new FixedAssetTransfer(asset.Id, clock.Today, clock.Now, oldCategory, asset.Category,
                                                        from.Asset, from.Depreciation, from.Charge, to.Asset,
                                                        to.Depreciation, oldGross, exported), ct);
    }

    /// <summary>
    /// An instrument may be capitalised once, and never while a supplier provides it: placement,
    /// comodat or lease leave it the supplier's property.
    /// </summary>
    private async Task<Result> CheckInstrumentAsync(Guid instrumentId, DateOnly acquisitionDate, Guid? exceptAssetId,
                                                    CancellationToken ct)
    {
        var contracts = await queries.GetProvidingContractsAsync(instrumentId, ct);
        if (SupplierProvidedEquipment.Blocking(contracts, acquisitionDate) is { } contract)
            return Result.Failure(Error.Regulatory("Asset_SupplierProvided", contract.Reference));

        if (await queries.AssetCodeForInstrumentAsync(instrumentId, exceptAssetId, ct) is { } other)
            return Result.Failure(Error.Conflict("Asset_InstrumentAlreadyCapitalised", other));

        return Result.Success();
    }

    public async Task<Result> RecordDisposalAsync(Guid assetId, DateOnly on, decimal price, string? reason,
                                                  CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.Manage);
        if (permitted.IsFailure) return permitted;
        if (on > clock.Today) return Result.Failure(Error.Validation("Asset_DisposalInFuture"));

        var asset = await assets.GetByIdAsync(assetId, ct);
        if (asset is null) return Result.Failure(Error.NotFound("Asset_NotFound"));

        var disposed = asset.RecordDisposal(on, price, reason);
        if (disposed.IsFailure) return disposed;

        assets.Update(asset);
        unitOfWork.AuditReason = $"Sortie de l'immobilisation {asset.Code} le {on:dd/MM/yyyy} : {asset.DisposalReason}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> CancelDisposalAsync(Guid assetId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.Manage);
        if (permitted.IsFailure) return permitted;

        var asset = await assets.GetByIdAsync(assetId, ct);
        if (asset is null) return Result.Failure(Error.NotFound("Asset_NotFound"));

        var cancelled = asset.CancelDisposal();
        if (cancelled.IsFailure) return cancelled;

        assets.Update(asset);
        unitOfWork.AuditReason = $"Sortie de l'immobilisation {asset.Code} annulée";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---- Settings --------------------------------------------------------------------------------

    private async Task<FixedAssetSettings> GetSettingsCoreAsync(CancellationToken ct) =>
        FixedAssetSettings.FromValues(await queries.GetSettingValuesAsync(FixedAssetSettings.Prefix, ct));

    public async Task<Result<FixedAssetSettings>> GetSettingsAsync(CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.View);
        if (permitted.IsFailure) return Result.Failure<FixedAssetSettings>(permitted.FirstError);

        return Result.Success(await GetSettingsCoreAsync(ct));
    }

    public async Task<Result> SaveSettingsAsync(FixedAssetSettings settings, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Assets.Manage);
        if (permitted.IsFailure) return permitted;
        if (!settings.IsValid()) return Result.Failure(Error.Validation("Asset_AccountInvalid"));

        var changed = new List<string>();
        foreach (var (key, value) in settings.ToValues())
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                await settingsWriter.AddSettingAsync(new AppSetting(key, value)
                {
                    Description = "Immobilisations : comptes du registre et des dotations aux amortissements"
                }, ct);
                changed.Add(key);
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed.Add(key);
            }
        }

        if (changed.Count == 0) return Result.Success();

        unitOfWork.AuditReason = $"Comptes des immobilisations modifiés : {string.Join(", ", changed)}";
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The register report, "tableau des immobilisations et des amortissements", as the columns of the
/// screen, the PDF and the CSV file. One line per asset, the subtotals per category and the
/// total; the equipment placed by suppliers follows, marked "mis à disposition", without amounts.
/// </summary>
public static class FixedAssetRegisterReport
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Amount(decimal value) => value.ToString("N2", Fr);

    public static string Percent(decimal rate) => (rate * 100m).ToString("0.##", Fr) + " %";

    /// <summary>The French column headings, which the CSV file always uses.</summary>
    public static readonly IReadOnlyList<string> FrenchHeaders =
    [
        "Code", "Désignation", "Catégorie", "Compte", "Acquisition", "Mise en service", "Valeur brute",
        "Méthode", "Durée", "Taux", "Amort. cumulés début", "Dotation exercice", "Sorties valeur brute",
        "Sorties amort.", "Valeur brute fin", "Amort. cumulés fin", "VNA",
        "Sortie", "Prix de cession", "Résultat de cession", "Fournisseur", "Facture", "Équipement"
    ];

    /// <summary>
    /// The relative widths of the 20 columns of the register's PDF (up to the disposal result), in
    /// points on an A3 landscape page at <see cref="PdfFontSize"/>: an amount up to "99 999 999,99" and
    /// a date hold on one line, the duration and the rate stay narrow.
    /// </summary>
    public static readonly IReadOnlyList<float> PdfColumnWidths =
    [
        52, 110, 70, 40, 46, 46, 58, 52, 26, 32, 58, 58, 58, 58, 58, 58, 58, 46, 58, 58
    ];

    public const float PdfFontSize = 7f;

    /// <summary>A line's cells, the words (category, method, "mis à disposition") given by the caller.</summary>
    public static IReadOnlyList<string> Cells(FixedAssetRegisterRow row, string category, string method) =>
    [
        row.Asset.Code, row.Asset.Label, category, row.Accounts.Asset,
        row.Asset.AcquisitionDate.ToString("dd/MM/yyyy", Fr), row.Asset.InServiceDate.ToString("dd/MM/yyyy", Fr),
        Amount(row.Asset.GrossValue), method, row.Asset.UsefulLifeYears.ToString(Fr), Percent(row.Rate),
        Amount(row.Opening), Amount(row.Charge), Amount(row.GrossOut), Amount(row.DepreciationOut),
        Amount(row.ClosingGross), Amount(row.Closing), Amount(row.NetBookValue),
        row.Asset.DisposalDate?.ToString("dd/MM/yyyy", Fr) ?? string.Empty,
        row.Asset.DisposalPrice is { } price && row.Asset.DisposalDate is not null ? Amount(price) : string.Empty,
        row.DisposalResult is { } result ? Amount(result) : string.Empty,
        row.Asset.SupplierName ?? string.Empty, row.Asset.InvoiceReference ?? string.Empty,
        row.Asset.InstrumentCode ?? string.Empty
    ];

    /// <summary>A subtotal or total line: the label in the designation column, the amounts in theirs.</summary>
    public static IReadOnlyList<string> TotalCells(string label, IEnumerable<FixedAssetRegisterRow> rows)
    {
        var list = rows.ToList();
        return
        [
            string.Empty, label, string.Empty, string.Empty, string.Empty, string.Empty,
            Amount(list.Sum(r => r.Asset.GrossValue)), string.Empty, string.Empty, string.Empty,
            Amount(list.Sum(r => r.Opening)), Amount(list.Sum(r => r.Charge)), Amount(list.Sum(r => r.GrossOut)),
            Amount(list.Sum(r => r.DepreciationOut)), Amount(list.Sum(r => r.ClosingGross)),
            Amount(list.Sum(r => r.Closing)), Amount(list.Sum(r => r.NetBookValue)), string.Empty, string.Empty,
            list.Any(r => r.DisposalResult is not null) ? Amount(list.Sum(r => r.DisposalResult ?? 0m)) : string.Empty,
            string.Empty, string.Empty, string.Empty
        ];
    }

    /// <summary>A line for equipment placed by a supplier: no value, no depreciation.</summary>
    public static IReadOnlyList<string> ProvidedCells(SupplierProvidedRow row, string name, string status) =>
    [
        row.InstrumentCode, name, status, string.Empty,
        row.StartDate.ToString("dd/MM/yyyy", Fr), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty,
        row.SupplierName, row.ContractReference, row.InstrumentCode
    ];

    /// <summary>
    /// Every line of the report in order: per category its assets and a subtotal, the total, then
    /// the equipment placed by suppliers.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Lines(FixedAssetRegister register,
                                                             Func<FixedAssetCategory, string> category,
                                                             Func<DepreciationMethod, string> method,
                                                             Func<SupplierProvidedRow, string> providedName,
                                                             string subtotal, string total, string provided)
    {
        var lines = new List<IReadOnlyList<string>>();
        foreach (var group in register.Assets.GroupBy(r => r.Asset.Category).OrderBy(g => g.Key))
        {
            lines.AddRange(group.Select(r => Cells(r, category(r.Asset.Category), method(r.Asset.Method))));
            lines.Add(TotalCells($"{subtotal} {category(group.Key)}", group));
        }

        if (register.Assets.Count > 0) lines.Add(TotalCells(total, register.Assets));

        lines.AddRange(register.SupplierProvided.Select(p => ProvidedCells(p, providedName(p), provided)));
        return lines;
    }

    /// <summary>The report as CSV: semicolons, UTF-8 with its byte-order mark so Excel reads the accents.</summary>
    public static byte[] ToCsv(FixedAssetRegister register)
    {
        var lines = Lines(register, c => FrenchCategory(c), m => m == DepreciationMethod.Declining ? "Dégressif" : "Linéaire",
                          p => p.InstrumentName.Fr, "Sous-total", "Total", "Mis à disposition");

        var text = new StringBuilder();
        text.AppendLine(string.Join(';', FrenchHeaders.Select(CsvCell.Of)));
        foreach (var line in lines) text.AppendLine(string.Join(';', line.Select(CsvCell.Of)));

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>An asset's plan as CSV: one line per month with the accumulated depreciation.</summary>
    public static byte[] ScheduleCsv(string assetCode, DepreciationSchedule schedule)
    {
        var text = new StringBuilder();
        text.AppendLine("Immobilisation;Exercice;Mois;Date;Dotation;Amort. cumulés;VNA");

        var accumulated = 0m;
        foreach (var month in schedule.Months)
        {
            accumulated = Money.Round(accumulated + month.Amount);
            text.AppendLine(string.Join(';',
                CsvCell.Of(assetCode), month.Year.ToString(CultureInfo.InvariantCulture), month.Month.ToString("00", Fr),
                month.Date.ToString("dd/MM/yyyy", Fr), Amount(month.Amount), Amount(accumulated),
                Amount(Money.Round(schedule.GrossValue - accumulated))));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public static string FrenchCategory(FixedAssetCategory category) => category switch
    {
        FixedAssetCategory.LaboratoryEquipment => "Matériel de laboratoire",
        FixedAssetCategory.ItEquipment => "Matériel informatique",
        FixedAssetCategory.Furniture => "Mobilier",
        FixedAssetCategory.Vehicles => "Matériel de transport",
        FixedAssetCategory.Fittings => "Agencements et aménagements",
        _ => "Autres immobilisations"
    };
}

/// <summary>Read-side queries of the fixed-assets register.</summary>
public interface IFixedAssetQueries
{
    /// <summary>Every asset of the site, disposed of or not.</summary>
    Task<IReadOnlyList<FixedAssetSource>> GetAssetsAsync(Guid siteId, CancellationToken ct = default);

    /// <summary>
    /// The site's instruments covered at some point between <paramref name="from"/> and
    /// <paramref name="to"/> by a placement, comodat or lease contract, up to its termination when
    /// it was terminated early.
    /// </summary>
    Task<IReadOnlyList<SupplierProvidedRow>> GetSupplierProvidedAsync(Guid siteId, DateOnly from, DateOnly to,
                                                                      CancellationToken ct = default);

    /// <summary>Every contract that names the instrument, whatever its kind or state.</summary>
    Task<IReadOnlyList<ProvidingContract>> GetProvidingContractsAsync(Guid instrumentId, CancellationToken ct = default);

    Task<bool> CodeExistsAsync(Guid siteId, string code, Guid? exceptAssetId, CancellationToken ct = default);

    /// <summary>The code of another asset already linked to the instrument, if any.</summary>
    Task<string?> AssetCodeForInstrumentAsync(Guid instrumentId, Guid? exceptAssetId, CancellationToken ct = default);

    /// <summary>
    /// Active suppliers and the site's instruments (not decommissioned), plus the supplier and
    /// instrument <paramref name="assetId"/> already uses whatever their state.
    /// </summary>
    Task<FixedAssetOptions> GetOptionsAsync(Guid siteId, Guid? assetId, DateOnly today, CancellationToken ct = default);

    /// <summary>The supplier's invoices at the site, not cancelled, newest first.</summary>
    Task<IReadOnlyList<AssetInvoiceOption>> GetSupplierInvoicesAsync(Guid siteId, Guid supplierId,
                                                                     CancellationToken ct = default);

    Task<AssetInstrumentSummary?> GetInstrumentSummaryAsync(Guid instrumentId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);

    /// <summary>The depreciation months of the site's assets already exported to the accounting software.</summary>
    Task<IReadOnlyList<DepreciationPosting>> GetDepreciationPostingsAsync(Guid siteId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DepreciationPosting>>([]);

    /// <summary>The changes of category of the site's assets made after they were exported.</summary>
    Task<IReadOnlyList<DepreciationTransfer>> GetDepreciationTransfersAsync(Guid siteId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DepreciationTransfer>>([]);

    /// <summary>Whether the purchases entry of the supplier invoice went out in an accounting export.</summary>
    Task<bool> IsSupplierInvoiceExportedAsync(Guid supplierInvoiceId, CancellationToken ct = default) =>
        Task.FromResult(false);
}
