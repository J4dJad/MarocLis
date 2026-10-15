using MarocLIS.Application.Administration;
using MarocLIS.Domain.Billing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Equipment;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Quality;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

public sealed class MasterDataQueries(LisDbContext db) : IMasterDataQueries
{
    // ---- Instruments ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<InstrumentMasterRow>> GetInstrumentsAsync(
        Guid siteId, bool includeInactive, CancellationToken ct = default)
    {
        var query = db.Instruments.AsNoTracking().Where(i => i.SiteId == siteId);

        if (!includeInactive)
            query = query.Where(i => i.Status != EquipmentStatus.Decommissioned);

        return await query
            .OrderBy(i => i.Code)
            .Select(i => new InstrumentMasterRow(
                i.Id, i.Code, i.Name, i.Kind, i.Status,
                i.Manufacturer, i.Model, i.SerialNumber, i.Location,
                i.PreventiveMaintenanceIntervalDays, i.CalibrationIntervalDays,
                i.VerificationIntervalDays, i.LastCalibrationOn,
                i.MaintenanceContractValidUntil,
                db.InstrumentInterfaces.Any(f => f.InstrumentId == i.Id),
                db.QcTargets.Count(t => t.InstrumentId == i.Id && t.IsActive),
                // Flagged because a missing interval silently disarms the overdue indicators —
                // and for an analyser, the calibration guard that stops it producing results.
                i.CalibrationIntervalDays == null || i.PreventiveMaintenanceIntervalDays == null))
            .ToListAsync(ct);
    }

    public Task<bool> InstrumentCodeExistsAsync(string code, Guid? excludingId,
                                                 CancellationToken ct = default) =>
        db.Instruments.AsNoTracking()
          .AnyAsync(i => i.Code == code && (excludingId == null || i.Id != excludingId), ct);

    // ---- QC materials --------------------------------------------------------------------------

    public async Task<IReadOnlyList<QcMaterialRow>> GetQcMaterialsAsync(
        bool includeInactive, DateOnly today, CancellationToken ct = default)
    {
        var query = db.QcMaterials.AsNoTracking();

        if (!includeInactive) query = query.Where(m => m.IsActive);

        var rows = await query
            .Select(m => new
            {
                m.Id, m.LotNumber, m.Name, m.Level, m.ExpiryDate, m.Manufacturer,
                m.CatalogueReference, m.OpenedOn, m.StabilityAfterOpeningDays, m.IsActive,
                TargetCount = db.QcTargets.Count(t => t.QcMaterialId == m.Id && t.IsActive)
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                .Select(m =>
                {
                    var beyondOpen = m.OpenedOn is { } opened
                                     && m.StabilityAfterOpeningDays is { } days
                                     && today > opened.AddDays(days);

                    return new QcMaterialRow(
                        m.Id, m.LotNumber, m.Name, m.Level, m.ExpiryDate,
                        m.ExpiryDate.DayNumber - today.DayNumber,
                        m.Manufacturer, m.CatalogueReference, m.OpenedOn,
                        m.StabilityAfterOpeningDays, m.IsActive,
                        m.IsActive && m.ExpiryDate >= today && !beyondOpen,
                        m.TargetCount);
                })
                // Expired and unusable first: a control that cannot be run is the row that needs
                // replacing, and a technician looking here is usually looking for exactly that.
                .OrderBy(m => m.IsUsable)
                .ThenBy(m => m.ExpiryDate)
                .ThenBy(m => m.LotNumber)
        ];
    }

    public Task<bool> QcMaterialExistsAsync(string lotNumber, QcLevel level,
                                             CancellationToken ct = default) =>
        db.QcMaterials.AsNoTracking()
          .AnyAsync(m => m.LotNumber == lotNumber && m.Level == level, ct);

    // ---- QC targets ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<QcTargetRow>> GetQcTargetsAsync(
        Guid siteId, Guid? qcMaterialId, CancellationToken ct = default)
    {
        var query = db.QcTargets.AsNoTracking();

        if (qcMaterialId is { } material) query = query.Where(t => t.QcMaterialId == material);

        return await query
            .Join(db.Instruments.AsNoTracking().Where(i => i.SiteId == siteId),
                  t => t.InstrumentId, i => i.Id, (t, i) => new { Target = t, Instrument = i })
            .Join(db.QcMaterials.AsNoTracking(), x => x.Target.QcMaterialId, m => m.Id,
                  (x, m) => new { x.Target, x.Instrument, Material = m })
            .Join(db.TestParameters.AsNoTracking(), x => x.Target.TestParameterId, p => p.Id,
                  (x, p) => new { x.Target, x.Instrument, x.Material, Parameter = p })
            .OrderBy(x => x.Material.LotNumber)
            .ThenBy(x => x.Parameter.Code)
            .Select(x => new QcTargetRow(
                x.Target.Id, x.Material.Id, x.Material.LotNumber, x.Material.Level,
                x.Parameter.Id, x.Parameter.Code, x.Parameter.Name,
                x.Instrument.Id, x.Instrument.Code,
                x.Target.AssignedMean, x.Target.AssignedSd,
                x.Target.LaboratoryMean, x.Target.LaboratorySd, x.Target.LaboratoryPointCount,
                x.Target.MaxAcceptableCvPercent, x.Target.ActiveRules, x.Target.IsActive,
                // A target with neither an assigned nor a laboratory SD can accept values but can
                // never compute a Z-score, so no rule can ever evaluate against it.
                (x.Target.AssignedMean != null && x.Target.AssignedSd != null)
                || (x.Target.LaboratoryMean != null && x.Target.LaboratorySd != null)))
            .ToListAsync(ct);
    }

    public Task<bool> QcTargetExistsAsync(Guid qcMaterialId, Guid testParameterId,
                                           Guid instrumentId, CancellationToken ct = default) =>
        db.QcTargets.AsNoTracking()
          .AnyAsync(t => t.QcMaterialId == qcMaterialId
                         && t.TestParameterId == testParameterId
                         && t.InstrumentId == instrumentId, ct);

    public async Task<IReadOnlyList<ParameterOption>> GetParametersAsync(
        CancellationToken ct = default) =>
        await db.TestParameters.AsNoTracking()
            .Join(db.AnalysisTests.AsNoTracking(), p => p.AnalysisTestId, a => a.Id,
                  (p, a) => new { Parameter = p, Analysis = a })
            .Where(x => x.Analysis.IsActive)
            .OrderBy(x => x.Analysis.Code)
            .ThenBy(x => x.Parameter.Code)
            .Select(x => new ParameterOption(
                x.Parameter.Id, x.Analysis.Code, x.Parameter.Code, x.Parameter.Name,
                x.Parameter.UnitId == null
                    ? null
                    : db.Units.Where(u => u.Id == x.Parameter.UnitId)
                              .Select(u => u.DisplaySymbol).FirstOrDefault(),
                x.Parameter.Decimals))
            .ToListAsync(ct);

    // ---- Stock items ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<StockItemMasterRow>> GetStockItemsAsync(
        Guid siteId, bool includeInactive, CancellationToken ct = default)
    {
        var query = db.StockItems.AsNoTracking().Where(i => i.SiteId == siteId);

        if (!includeInactive) query = query.Where(i => i.IsActive);

        return await query
            .OrderBy(i => i.Code)
            .Select(i => new StockItemMasterRow(
                i.Id, i.Code, i.Name, i.Kind, i.UnitOfMeasure,
                i.ReorderPoint, i.MinimumStock, i.IsCritical, i.StorageCondition,
                i.PreferredSupplierId == null
                    ? null
                    : db.Suppliers.Where(s => s.Id == i.PreferredSupplierId)
                                  .Select(s => s.Name).FirstOrDefault(),
                i.IsHazardous, i.IsActive,
                i.Lots.Count(l => l.QuantityRemaining > 0m),
                i.Gtin, i.IsCeMarked, i.PreferredSupplierId, i.Manufacturer,
                i.SupplierCatalogueReference, i.UsedByAnalysisCodesCsv, i.DefaultStorageLocation,
                i.TestsPerPack, i.ReorderQuantity, i.MaximumStock, i.HazardClass))
            .ToListAsync(ct);
    }

    public Task<bool> StockItemCodeExistsAsync(string code, Guid siteId, Guid? excludingId,
                                                CancellationToken ct = default) =>
        db.StockItems.AsNoTracking()
          .AnyAsync(i => i.SiteId == siteId && i.Code == code
                         && (excludingId == null || i.Id != excludingId), ct);

    // ---- Suppliers -----------------------------------------------------------------------------

    public async Task<IReadOnlyList<SupplierRow>> GetSuppliersAsync(
        bool includeInactive, DateOnly today, CancellationToken ct = default)
    {
        var query = db.Suppliers.AsNoTracking();

        if (!includeInactive) query = query.Where(s => s.IsActive);

        var rows = await query
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Code, s.Name, s.Ice, s.ContactName, s.Phone, s.Email, s.City,
                s.LeadTimeDays, s.IsApproved, s.LastEvaluationDate, s.EvaluationScore, s.IsActive,
                StockItemCount = db.StockItems.Count(i => i.PreferredSupplierId == s.Id),
                s.RegulatoryRegistrationNumber, s.RegulatoryRegistrationValidUntil,
                s.FiscalId, s.CommercialRegisterNumber, s.AddressLine, s.Region, s.PaymentTermsDays, s.Notes
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(s => new SupplierRow(
                s.Id, s.Code, s.Name, s.Ice, s.ContactName, s.Phone, s.Email, s.City,
                s.LeadTimeDays, s.IsApproved, s.LastEvaluationDate, s.EvaluationScore,
                // ISO 15189 §4.6: an approved supplier of critical items is re-evaluated
                // periodically. Twelve months is the common laboratory cycle.
                s.IsApproved && (s.LastEvaluationDate is null
                                 || s.LastEvaluationDate.Value.AddMonths(12) < today),
                s.IsActive, s.StockItemCount,
                s.RegulatoryRegistrationNumber, s.RegulatoryRegistrationValidUntil,
                s.FiscalId, s.CommercialRegisterNumber, s.AddressLine, s.Region, s.PaymentTermsDays,
                s.Notes))
        ];
    }

    public Task<bool> SupplierCodeExistsAsync(string code, Guid? excludingId,
                                               CancellationToken ct = default) =>
        db.Suppliers.AsNoTracking()
          .AnyAsync(s => s.Code == code && (excludingId == null || s.Id != excludingId), ct);

    // ---- Payers --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PayerRow>> GetPayersAsync(
        bool includeInactive, DateOnly today, CancellationToken ct = default)
    {
        var query = db.Payers.AsNoTracking();

        if (!includeInactive) query = query.Where(p => p.IsActive);

        var rows = await query
            // Self-pay first, then the compulsory schemes, then everything else: the order an
            // administrator reads the list in, not alphabetical by code.
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Code)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.Kind, p.DefaultReimbursementRate,
                p.UsesNationalReferenceTariff, p.ThirdPartyPaymentAgreement, p.AgreementNumber,
                p.AgreementValidUntil, p.PaymentTermsDays, p.RequiresPrescription,
                p.RequiresInpeOnClaim, p.Ice, p.ContactName, p.Phone, p.Email, p.AddressLine,
                p.City, p.IsActive,

                // Counted rather than loaded: both are shown as numbers and a payer with a long
                // history would otherwise pull thousands of rows across the LAN to display one.
                CoveredPatientCount = db.PatientCoverages.Count(c => c.PayerId == p.Id),
                OpenInvoiceCount = db.Invoices.Count(
                    i => i.PayerId == p.Id
                         && i.Status != InvoiceStatus.Paid
                         && i.Status != InvoiceStatus.WrittenOff
                         && i.Status != InvoiceStatus.Cancelled)
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(p => new PayerRow(
                p.Id, p.Code, p.Name, p.Kind, p.DefaultReimbursementRate,
                p.UsesNationalReferenceTariff, p.ThirdPartyPaymentAgreement, p.AgreementNumber,
                p.AgreementValidUntil,
                // Expired only if there is an agreement to expire. A payer with no convention is
                // not "expired", it simply settles differently.
                p.ThirdPartyPaymentAgreement
                    && p.AgreementValidUntil is not null
                    && p.AgreementValidUntil < today,
                p.PaymentTermsDays, p.RequiresPrescription, p.RequiresInpeOnClaim, p.Ice,
                p.ContactName, p.Phone, p.Email, p.AddressLine, p.City, p.IsActive,
                p.CoveredPatientCount, p.OpenInvoiceCount))
        ];
    }

    /// <summary>
    /// Ignores the soft-delete filter on purpose: the code column is uniquely indexed in the
    /// database, so a deleted payer still occupies its code and reusing it would fail at the
    /// constraint with a message nobody can act on.
    /// </summary>
    public Task<bool> PayerCodeExistsAsync(string code, Guid? excludingId,
                                            CancellationToken ct = default) =>
        db.Payers.AsNoTracking().IgnoreQueryFilters()
          .AnyAsync(p => p.Code == code && (excludingId == null || p.Id != excludingId), ct);

    // ---- Prescribers ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<PrescriberRow>> GetPrescribersAsync(
        string? search, bool includeInactive, CancellationToken ct = default)
    {
        var query = db.Prescribers.AsNoTracking();

        if (!includeInactive) query = query.Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalised = PatientSearchKey.Normalise(search);
            var upper = search.Trim().ToUpperInvariant();

            query = query.Where(p => p.SearchKey.Contains(normalised)
                                     || (p.Inpe != null && p.Inpe.Contains(upper))
                                     || (p.InstitutionName != null
                                         && EF.Functions.Like(p.InstitutionName, $"%{search.Trim()}%")));
        }

        return await query
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Take(500)
            .Select(p => new PrescriberRow(
                p.Id, p.LastName + " " + p.FirstName, p.FullNameArabic, p.Kind, p.Speciality,
                p.ProfessionalOrderNumber, p.Inpe, p.InstitutionName,
                p.MobilePhone ?? p.Phone, p.Email, p.City,
                // Projected, not defaulted: the editor shows this as a checkbox, so a row that did
                // not carry it reopened every prescriber with partial reporting switched off and
                // saved that back over whatever was configured.
                p.DeliveryPreference, p.SendPartialReports, p.DiscountPercent, p.IsActive,
                db.LabOrders.Count(o => o.PrescriberId == p.Id)))
            .ToListAsync(ct);
    }
}
