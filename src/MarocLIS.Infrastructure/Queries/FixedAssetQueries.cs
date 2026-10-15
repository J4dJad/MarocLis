using MarocLIS.Application.Assets;
using MarocLIS.Domain.Assets;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Inventory;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class FixedAssetQueries(LisDbContext db) : IFixedAssetQueries
{
    private static readonly InstrumentContractKind[] ProvidedKinds =
        [InstrumentContractKind.Placement, InstrumentContractKind.Comodat, InstrumentContractKind.Lease];

    public async Task<IReadOnlyList<FixedAssetSource>> GetAssetsAsync(Guid siteId, CancellationToken ct = default)
    {
        var assets = await db.FixedAssets.AsNoTracking()
            .Where(a => a.SiteId == siteId)
            .OrderBy(a => a.Code)
            .ToListAsync(ct);

        // A supplier or an instrument deleted since still names the asset's origin.
        var supplierIds = assets.Where(a => a.SupplierId != null).Select(a => a.SupplierId!.Value).Distinct().ToList();
        var suppliers = await db.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var instrumentIds = assets.Where(a => a.InstrumentId != null).Select(a => a.InstrumentId!.Value).Distinct().ToList();
        var instruments = await db.Instruments.IgnoreQueryFilters().AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Code, ct);

        return
        [
            .. assets.Select(a => new FixedAssetSource(
                a.Id, a.Code, a.Label, a.Category, a.SiteId, a.AcquisitionDate, a.InServiceDate,
                a.CostExcludingVatMad, a.VatAmountMad, a.VatRecoverable, a.Method, a.UsefulLifeYears,
                a.ResidualValueMad, a.DisposalDate, a.DisposalPriceMad, a.DisposalReason,
                a.SupplierId is { } s ? suppliers.GetValueOrDefault(s) : null,
                a.InvoiceReference,
                a.InstrumentId,
                a.InstrumentId is { } i ? instruments.GetValueOrDefault(i) : null))
        ];
    }

    public async Task<IReadOnlyList<SupplierProvidedRow>> GetSupplierProvidedAsync(Guid siteId, DateOnly today,
                                                                                   CancellationToken ct = default)
    {
        var contracts = (await db.InstrumentContracts.AsNoTracking()
                .Where(c => c.SiteId == siteId && c.IsActive && ProvidedKinds.Contains(c.Kind))
                .ToListAsync(ct))
            .Where(c => c.EndDate >= today)
            .ToList();

        var instrumentIds = contracts.SelectMany(c => c.Instruments.Select(i => i.InstrumentId)).Distinct().ToList();
        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, i.Name })
            .ToDictionaryAsync(i => i.Id, ct);

        var supplierIds = contracts.Select(c => c.SupplierId).Distinct().ToList();
        var suppliers = await db.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        // An instrument under two contracts (a renewal entered before the old one was closed) is
        // listed once, under the latest.
        return
        [
            .. contracts.OrderByDescending(c => c.StartDate)
                .SelectMany(c => c.Instruments.Select(i => (Contract: c, i.InstrumentId)))
                .Where(x => instruments.ContainsKey(x.InstrumentId))
                .DistinctBy(x => x.InstrumentId)
                .Select(x => new SupplierProvidedRow(
                    x.InstrumentId, instruments[x.InstrumentId].Code, instruments[x.InstrumentId].Name,
                    suppliers.GetValueOrDefault(x.Contract.SupplierId) ?? string.Empty,
                    x.Contract.Reference, x.Contract.Kind, x.Contract.StartDate, x.Contract.EndDate))
                .OrderBy(r => r.InstrumentCode, StringComparer.Ordinal)
        ];
    }

    public async Task<IReadOnlyList<ProvidingContract>> GetProvidingContractsAsync(Guid instrumentId,
                                                                                   CancellationToken ct = default)
    {
        var contracts = await db.InstrumentContracts.AsNoTracking()
            .Where(c => c.Instruments.Any(i => i.InstrumentId == instrumentId))
            .ToListAsync(ct);

        return [.. contracts.Select(c => new ProvidingContract(c.Reference, c.Kind, c.IsActive, c.StartDate, c.EndDate))];
    }

    public Task<bool> CodeExistsAsync(Guid siteId, string code, Guid? exceptAssetId, CancellationToken ct = default) =>
        db.FixedAssets.AsNoTracking()
          .AnyAsync(a => a.SiteId == siteId && a.Code == code && (exceptAssetId == null || a.Id != exceptAssetId), ct);

    public Task<string?> AssetCodeForInstrumentAsync(Guid instrumentId, Guid? exceptAssetId,
                                                     CancellationToken ct = default) =>
        db.FixedAssets.IgnoreQueryFilters().AsNoTracking()
          .Where(a => a.InstrumentId == instrumentId && (exceptAssetId == null || a.Id != exceptAssetId))
          .Select(a => (string?)a.Code)
          .FirstOrDefaultAsync(ct);

    public async Task<FixedAssetOptions> GetOptionsAsync(Guid siteId, Guid? assetId, DateOnly today,
                                                         CancellationToken ct = default)
    {
        var current = assetId is { } id
            ? await db.FixedAssets.AsNoTracking()
                .Where(a => a.Id == id)
                .Select(a => new { a.SupplierId, a.InstrumentId })
                .FirstOrDefaultAsync(ct)
            : null;

        var supplierId = current?.SupplierId ?? Guid.Empty;
        var instrumentId = current?.InstrumentId ?? Guid.Empty;

        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(s => s.IsActive || s.Id == supplierId)
            .OrderBy(s => s.Name)
            .Select(s => new AssetLookup(s.Id, s.Code, s.Name, s.IsActive))
            .ToListAsync(ct);

        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => i.SiteId == siteId
                        && (i.Status != EquipmentStatus.Decommissioned || i.Id == instrumentId))
            .OrderBy(i => i.Code)
            .Select(i => new { i.Id, i.Code, i.Name, i.PurchaseDate, i.CommissioningDate, i.SupplierId })
            .ToListAsync(ct);

        var ids = instruments.Select(i => i.Id).ToList();

        var linked = await db.FixedAssets.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.InstrumentId != null && ids.Contains(a.InstrumentId.Value))
            .Select(a => new { InstrumentId = a.InstrumentId!.Value, a.Code })
            .ToListAsync(ct);
        var assetCodes = linked.GroupBy(a => a.InstrumentId).ToDictionary(g => g.Key, g => g.First().Code);

        var contracts = await db.InstrumentContracts.AsNoTracking()
            .Where(c => c.IsActive && ProvidedKinds.Contains(c.Kind)
                        && c.Instruments.Any(i => ids.Contains(i.InstrumentId)))
            .ToListAsync(ct);
        var provided = contracts
            .SelectMany(c => c.Instruments.Select(i => (i.InstrumentId,
                                                        Contract: new ProvidingContract(c.Reference, c.Kind, c.IsActive,
                                                                                        c.StartDate, c.EndDate))))
            .GroupBy(x => x.InstrumentId)
            .ToDictionary(g => g.Key, g => SupplierProvidedEquipment.Blocking(g.Select(x => x.Contract), today)?.Reference);

        return new FixedAssetOptions(
            suppliers,
            [
                .. instruments.Select(i => new AssetInstrumentOption(
                    i.Id, i.Code, i.Name, i.PurchaseDate, i.CommissioningDate, i.SupplierId,
                    provided.GetValueOrDefault(i.Id),
                    assetCodes.TryGetValue(i.Id, out var code) && i.Id != instrumentId ? code : null))
            ]);
    }

    public async Task<IReadOnlyList<AssetInvoiceOption>> GetSupplierInvoicesAsync(Guid siteId, Guid supplierId,
                                                                                  CancellationToken ct = default)
    {
        var invoices = await db.SupplierInvoices.AsNoTracking()
            .Where(i => i.SiteId == siteId && i.SupplierId == supplierId
                        && i.Status != SupplierInvoiceStatus.Cancelled)
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new { i.Id, i.InvoiceNumber, i.InvoiceDate })
            .Take(200)
            .ToListAsync(ct);

        var ids = invoices.Select(i => i.Id).ToList();
        var lines = await db.SupplierInvoiceLines.AsNoTracking()
            .Where(l => ids.Contains(l.SupplierInvoiceId))
            .Select(l => new { l.SupplierInvoiceId, l.Quantity, l.UnitPriceMad, l.VatRate })
            .ToListAsync(ct);

        var totals = lines.GroupBy(l => l.SupplierInvoiceId).ToDictionary(g => g.Key, g =>
        {
            var excluding = g.Sum(l => Domain.Common.Money.Round(l.Quantity * l.UnitPriceMad));
            var vat = g.Sum(l => Domain.Common.Money.Round(Domain.Common.Money.Round(l.Quantity * l.UnitPriceMad) * l.VatRate));
            return (Excluding: Domain.Common.Money.Round(excluding), Vat: Domain.Common.Money.Round(vat));
        });

        return
        [
            .. invoices.Select(i => new AssetInvoiceOption(
                i.Id, i.InvoiceNumber, i.InvoiceDate,
                totals.GetValueOrDefault(i.Id).Excluding, totals.GetValueOrDefault(i.Id).Vat))
        ];
    }

    public async Task<AssetInstrumentSummary?> GetInstrumentSummaryAsync(Guid instrumentId,
                                                                        CancellationToken ct = default)
    {
        var instrument = await db.Instruments.AsNoTracking()
            .Where(i => i.Id == instrumentId)
            .Select(i => new
            {
                i.Id, i.Code, i.Name, i.Status, i.Manufacturer, i.Model, i.SerialNumber, i.Location,
                i.LastPreventiveMaintenanceOn, i.PreventiveMaintenanceIntervalDays,
                i.LastCalibrationOn, i.CalibrationIntervalDays, i.MaintenanceContractValidUntil
            })
            .FirstOrDefaultAsync(ct);
        if (instrument is null) return null;

        var interventions = await db.MaintenanceRecords.AsNoTracking()
            .Where(m => m.InstrumentId == instrumentId)
            .OrderByDescending(m => m.PerformedOn)
            .Select(m => new AssetMaintenanceRow(m.PerformedOn, m.Kind, m.Description, m.DowntimeHours, m.CostMad,
                                                 m.ExternalTechnicianName, m.PostInterventionCheckPassed))
            .ToListAsync(ct);

        return new AssetInstrumentSummary(
            instrument.Id, instrument.Code, instrument.Name, instrument.Status, instrument.Manufacturer,
            instrument.Model, instrument.SerialNumber, instrument.Location,
            instrument.LastPreventiveMaintenanceOn is { } maintained && instrument.PreventiveMaintenanceIntervalDays is { } every
                ? maintained.AddDays(every) : null,
            instrument.LastCalibrationOn is { } calibrated && instrument.CalibrationIntervalDays is { } interval
                ? calibrated.AddDays(interval) : null,
            instrument.MaintenanceContractValidUntil,
            interventions);
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix,
                                                                                  CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.SiteId == null && s.Key.StartsWith(prefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }
}
