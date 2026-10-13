using MarocLIS.Application.Catalog;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;
using MarocLIS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarocLIS.Infrastructure.Queries;

/// <summary>
/// Read side of catalogue maintenance, plus the cross-checks the service needs before it writes.
/// <para>
/// The counts matter as much as the rows. "How many results exist for this parameter?" is what
/// freezes its value type; "how many order items are still open?" is what refuses a deactivation.
/// Each of them answers a question the screen would otherwise have to guess at, and a guess there
/// means either a refusal the operator cannot understand or a change that quietly breaks history.
/// </para>
/// </summary>
public sealed class CatalogMaintenanceQueries(LisDbContext db) : ICatalogMaintenanceQueries
{
    public async Task<IReadOnlyList<AnalysisRow>> GetAnalysesAsync(
        string? search, Guid? departmentId, bool includeInactive, DateOnly today,
        CancellationToken ct = default)
    {
        var query = db.AnalysisTests.AsNoTracking();

        if (!includeInactive) query = query.Where(a => a.IsActive);
        if (departmentId is { } department) query = query.Where(a => a.DepartmentId == department);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";

            query = query.Where(a => EF.Functions.Like(a.Code, pattern)
                                     || EF.Functions.Like(a.Name.Fr, pattern)
                                     || (a.Name.Ar != null && EF.Functions.Like(a.Name.Ar, pattern))
                                     || (a.Name.En != null && EF.Functions.Like(a.Name.En, pattern))
                                     || (a.SearchAliases != null
                                         && EF.Functions.Like(a.SearchAliases, pattern)));
        }

        var rows = await query
            .OrderBy(a => a.DisplayOrder).ThenBy(a => a.Code)
            .Select(a => new
            {
                a.Id,
                a.Code,
                a.Name,
                a.DepartmentId,
                a.SpecimenTypeId,
                a.ContainerTypeId,
                a.NationalActCode,
                a.RoutineTurnaroundHours,
                a.IsAccredited,
                a.IsSubcontracted,
                a.ConsentRequirement,
                a.IsActive,
                Department = db.Departments.Where(d => d.Id == a.DepartmentId)
                                            .Select(d => new { d.Code, d.Name }).FirstOrDefault(),
                SpecimenTypeName = db.SpecimenTypes.Where(s => s.Id == a.SpecimenTypeId)
                                                    .Select(s => s.Name).FirstOrDefault(),
                ContainerName = db.ContainerTypes.Where(c => c.Id == a.ContainerTypeId)
                                                  .Select(c => c.Name.Fr).FirstOrDefault(),
                ParameterCount = db.TestParameters.Count(p => p.AnalysisTestId == a.Id),
                ActiveParameterCount = db.TestParameters.Count(
                    p => p.AnalysisTestId == a.Id && p.IsActive),
                RangeCount = db.ReferenceRanges.Count(
                    r => db.TestParameters.Any(p => p.Id == r.TestParameterId
                                                    && p.AnalysisTestId == a.Id)),
                TariffCount = db.Tariffs.Count(t => t.AnalysisTestId == a.Id),
                Current = db.Tariffs
                    .Where(t => t.AnalysisTestId == a.Id && t.PayerId == null
                                && t.ValidFrom <= today
                                && (t.ValidUntil == null || t.ValidUntil >= today))
                    .OrderByDescending(t => t.ValidFrom)
                    .Select(t => new { t.Price, t.Coefficient })
                    .FirstOrDefault(),
                OrderedCount = db.LabOrderItems.Count(i => i.AnalysisTestId == a.Id)
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(a => new AnalysisRow(
                a.Id,
                a.Code,
                a.Name,
                a.Department?.Code ?? string.Empty,
                a.Department?.Name ?? new MultilingualText(string.Empty),
                a.DepartmentId,
                a.SpecimenTypeId,
                a.SpecimenTypeName ?? new MultilingualText(string.Empty),
                a.ContainerTypeId,
                a.ContainerName,
                a.ParameterCount,
                a.ActiveParameterCount,
                a.RangeCount,
                a.TariffCount,
                a.Current?.Price,
                a.Current?.Coefficient,
                a.NationalActCode,
                a.RoutineTurnaroundHours,
                a.IsAccredited,
                a.IsSubcontracted,
                a.ConsentRequirement,
                a.IsActive,
                a.OrderedCount))
        ];
    }

    public async Task<IReadOnlyList<ParameterRow>> GetParametersAsync(
        Guid analysisTestId, CancellationToken ct = default)
    {
        var rows = await db.TestParameters.AsNoTracking()
            .Where(p => p.AnalysisTestId == analysisTestId)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Code)
            .Select(p => new
            {
                p.Id,
                p.AnalysisTestId,
                p.Code,
                p.Name,
                p.ValueType,
                p.UnitId,
                p.Decimals,
                p.MeasuringRangeLow,
                p.MeasuringRangeHigh,
                p.TechnicalLimitLow,
                p.TechnicalLimitHigh,
                p.CriticalLow,
                p.CriticalHigh,
                p.AnalyticalCvPercent,
                p.DeltaCheckPercent,
                p.DeltaCheckWindowDays,
                p.CalculationFormula,
                p.IsInternalOnly,
                p.AlwaysShowOnReport,
                p.DisplayOrder,
                p.IndentLevel,
                p.IsActive,
                UnitSymbol = db.Units.Where(u => u.Id == p.UnitId)
                                      .Select(u => u.DisplaySymbol).FirstOrDefault(),
                RangeCount = db.ReferenceRanges.Count(r => r.TestParameterId == p.Id && r.IsActive),
                CodedOptionCount = db.CodedResultOptions.Count(o => o.TestParameterId == p.Id),
                ResultCount = db.TestResults.Count(r => r.TestParameterId == p.Id)
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(p => new ParameterRow(
                p.Id, p.AnalysisTestId, p.Code, p.Name, p.ValueType, p.UnitId, p.UnitSymbol,
                p.Decimals, p.MeasuringRangeLow, p.MeasuringRangeHigh, p.TechnicalLimitLow,
                p.TechnicalLimitHigh, p.CriticalLow, p.CriticalHigh, p.AnalyticalCvPercent,
                p.DeltaCheckPercent, p.DeltaCheckWindowDays, p.CalculationFormula,
                p.IsInternalOnly, p.AlwaysShowOnReport, p.DisplayOrder, p.IndentLevel,
                p.IsActive, p.RangeCount, p.CodedOptionCount, p.ResultCount))
        ];
    }

    public async Task<IReadOnlyList<ReferenceRangeRow>> GetRangesAsync(
        Guid parameterId, CancellationToken ct = default)
    {
        var rows = await db.ReferenceRanges.AsNoTracking()
            .Where(r => r.TestParameterId == parameterId)
            // Newest in force first, then the superseded ones: the current interval is what the
            // operator came to look at, and its predecessors are why it says what it says.
            .OrderByDescending(r => r.ValidFrom)
            .Select(r => new
            {
                r.Id,
                r.TestParameterId,
                r.AppliesToGender,
                r.AgeFromDays,
                r.AgeToDays,
                r.AppliesToPregnancy,
                r.PregnancyTrimester,
                r.InstrumentId,
                r.LowerLimit,
                r.UpperLimit,
                r.DisplayText,
                r.ExpectedCodedValue,
                r.IsDecisionThreshold,
                r.Source,
                r.LastReviewedOn,
                r.ValidFrom,
                r.ValidUntil,
                r.IsActive,
                InstrumentName = db.Instruments.Where(i => i.Id == r.InstrumentId)
                                                .Select(i => i.Name.Fr).FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new ReferenceRangeRow(
                r.Id, r.TestParameterId, r.AppliesToGender, r.AgeFromDays, r.AgeToDays,
                r.AppliesToPregnancy, r.PregnancyTrimester, r.InstrumentId, r.InstrumentName,
                r.LowerLimit, r.UpperLimit, r.DisplayText, r.ExpectedCodedValue,
                r.IsDecisionThreshold, r.Source, r.LastReviewedOn, r.ValidFrom, r.ValidUntil,
                r.IsActive,
                // Recomputed here rather than read: it is the same expression the resolver uses to
                // break a tie between matching intervals, and the screen has to show the operator
                // which of two overlapping intervals will actually win.
                (r.PregnancyTrimester is not null ? 8 : 0)
                + (r.AppliesToPregnancy is not null ? 4 : 0)
                + (r.InstrumentId is not null ? 3 : 0)
                + (r.AppliesToGender != Domain.Patients.Gender.Unknown ? 2 : 0)
                + (r.AgeFromDays is not null || r.AgeToDays is not null ? 1 : 0)))
        ];
    }

    public async Task<IReadOnlyList<TariffRow>> GetTariffsAsync(
        Guid analysisTestId, CancellationToken ct = default)
    {
        var rows = await db.Tariffs.AsNoTracking()
            .Where(t => t.AnalysisTestId == analysisTestId)
            .OrderBy(t => t.PayerId == null ? 0 : 1)
            .ThenByDescending(t => t.ValidFrom)
            .Select(t => new
            {
                t.Id,
                t.AnalysisTestId,
                t.PayerId,
                t.ValidFrom,
                t.ValidUntil,
                t.LetterKey,
                t.Coefficient,
                t.LetterKeyValue,
                t.Price,
                t.IsVatExempt,
                t.VatRate,
                PayerName = db.Payers.Where(p => p.Id == t.PayerId)
                                      .Select(p => p.Name.Fr).FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(t => new TariffRow(
                t.Id, t.AnalysisTestId, t.PayerId, t.PayerName, t.ValidFrom, t.ValidUntil,
                t.LetterKey, t.Coefficient, t.LetterKeyValue, t.Price, t.IsVatExempt, t.VatRate))
        ];
    }

    public async Task<IReadOnlyList<PanelRow>> GetPanelsAsync(
        bool includeInactive, DateOnly today, CancellationToken ct = default)
    {
        var query = db.TestPanels.AsNoTracking();

        if (!includeInactive) query = query.Where(p => p.IsActive);

        var panels = await query
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Code)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.PackagePrice, p.ItemizeOnInvoice, p.GroupOnReport,
                p.DisplayOrder, p.IsActive
            })
            .ToListAsync(ct);

        var ids = panels.Select(p => p.Id).ToList();

        var members = await db.TestPanelItems.AsNoTracking()
            .Where(i => ids.Contains(i.TestPanelId))
            .OrderBy(i => i.DisplayOrder)
            .Select(i => new
            {
                i.TestPanelId,
                i.AnalysisTestId,
                i.IsOptional,
                Analysis = db.AnalysisTests.Where(a => a.Id == i.AnalysisTestId)
                    .Select(a => new { a.Code, a.Name, a.IsActive }).FirstOrDefault(),
                Price = db.Tariffs
                    .Where(t => t.AnalysisTestId == i.AnalysisTestId && t.PayerId == null
                                && t.ValidFrom <= today
                                && (t.ValidUntil == null || t.ValidUntil >= today))
                    .OrderByDescending(t => t.ValidFrom)
                    .Select(t => (decimal?)t.Price)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. panels.Select(p =>
            {
                var own = members.Where(m => m.TestPanelId == p.Id).ToList();

                return new PanelRow(
                    p.Id, p.Code, p.Name, p.PackagePrice, p.ItemizeOnInvoice, p.GroupOnReport,
                    p.DisplayOrder, p.IsActive,
                    own.Count,
                    own.Count(m => m.IsOptional),
                    own.Count(m => m.Analysis is null || !m.Analysis.IsActive),
                    own.Where(m => !m.IsOptional).Sum(m => m.Price ?? 0m),
                    [
                        .. own.Select(m => new PanelMemberRow(
                            m.AnalysisTestId,
                            m.Analysis?.Code ?? string.Empty,
                            m.Analysis?.Name ?? new MultilingualText(string.Empty),
                            m.IsOptional,
                            m.Analysis?.IsActive ?? false,
                            m.Price ?? 0m))
                    ]);
            })
        ];
    }

    public async Task<IReadOnlyList<ReflexRuleRow>> GetReflexRulesAsync(CancellationToken ct = default)
    {
        var rows = await db.ReflexRules.AsNoTracking()
            .Select(r => new
            {
                r.Id,
                r.TriggerParameterId,
                r.AnalysisToAddId,
                r.Description,
                r.Condition,
                r.Threshold,
                r.ThresholdUpper,
                r.TriggerCodedValue,
                r.AddAutomatically,
                r.IsBillable,
                r.IsActive,
                Trigger = db.TestParameters.Where(p => p.Id == r.TriggerParameterId)
                    .Select(p => new
                    {
                        p.Code,
                        p.Name,
                        AnalysisCode = db.AnalysisTests.Where(a => a.Id == p.AnalysisTestId)
                                                        .Select(a => a.Code).FirstOrDefault()
                    })
                    .FirstOrDefault(),
                Target = db.AnalysisTests.Where(a => a.Id == r.AnalysisToAddId)
                    .Select(a => new { a.Code, a.Name }).FirstOrDefault()
            })
            .ToListAsync(ct);

        return
        [
            .. rows
                // Inactive last, then by the analysis the trigger belongs to, so the rules that
                // concern one analysis read together.
                .OrderBy(r => r.IsActive ? 0 : 1)
                .ThenBy(r => r.Trigger == null ? string.Empty : r.Trigger.AnalysisCode)
                .ThenBy(r => r.Trigger == null ? string.Empty : r.Trigger.Code)
                .Select(r => new ReflexRuleRow(
                    r.Id,
                    r.TriggerParameterId,
                    r.Trigger?.Code ?? string.Empty,
                    r.Trigger?.Name ?? new MultilingualText(string.Empty),
                    r.Trigger?.AnalysisCode ?? string.Empty,
                    r.AnalysisToAddId,
                    r.Target?.Code ?? string.Empty,
                    r.Target?.Name ?? new MultilingualText(string.Empty),
                    r.Description,
                    r.Condition,
                    r.Threshold,
                    r.ThresholdUpper,
                    r.TriggerCodedValue,
                    r.AddAutomatically,
                    r.IsBillable,
                    r.IsActive))
        ];
    }

    public async Task<IReadOnlyList<NomenclatureRow>> GetNomenclatureAsync(
        NomenclatureKind kind, CancellationToken ct = default)
    {
        switch (kind)
        {
            case NomenclatureKind.Department:
            {
                var rows = await db.Departments.AsNoTracking()
                    .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Code)
                    .Select(d => new
                    {
                        d.Id, d.Code, d.Name, d.IsActive, d.DisplayOrder,
                        d.IsInAccreditationScope, d.SupervisorUserId,
                        SupervisorName = db.Users.Where(u => u.Id == d.SupervisorUserId)
                                                  .Select(u => u.FullName).FirstOrDefault(),
                        UsageCount = db.AnalysisTests.Count(a => a.DepartmentId == d.Id)
                    })
                    .ToListAsync(ct);

                return
                [
                    .. rows.Select(d => Empty(d.Id, d.Code, d.Name, d.IsActive, d.UsageCount) with
                    {
                        DisplayOrder = d.DisplayOrder,
                        IsInAccreditationScope = d.IsInAccreditationScope,
                        SupervisorUserId = d.SupervisorUserId,
                        SupervisorName = d.SupervisorName
                    })
                ];
            }

            case NomenclatureKind.SpecimenType:
            {
                var rows = await db.SpecimenTypes.AsNoTracking()
                    .OrderBy(s => s.Code)
                    .Select(s => new
                    {
                        s.Id, s.Code, s.Name, s.IsActive, s.CollectionInstructions,
                        s.PatientInstructions, s.FastingHours,
                        UsageCount = db.AnalysisTests.Count(a => a.SpecimenTypeId == s.Id)
                    })
                    .ToListAsync(ct);

                return
                [
                    .. rows.Select(s => Empty(s.Id, s.Code, s.Name, s.IsActive, s.UsageCount) with
                    {
                        CollectionInstructions = s.CollectionInstructions,
                        PatientInstructions = s.PatientInstructions,
                        FastingHours = s.FastingHours
                    })
                ];
            }

            case NomenclatureKind.ContainerType:
            {
                var rows = await db.ContainerTypes.AsNoTracking()
                    .OrderBy(c => c.DrawOrder).ThenBy(c => c.Code)
                    .Select(c => new
                    {
                        c.Id, c.Code, c.Name, c.IsActive, c.CapColourHex, c.Additive,
                        c.NominalVolumeMl, c.MinimumVolumeMl, c.DrawOrder,
                        c.RequiresProtectionFromLight, c.RequiresIceTransport,
                        UsageCount = db.AnalysisTests.Count(a => a.ContainerTypeId == c.Id)
                    })
                    .ToListAsync(ct);

                return
                [
                    .. rows.Select(c => Empty(c.Id, c.Code, c.Name, c.IsActive, c.UsageCount) with
                    {
                        CapColourHex = c.CapColourHex,
                        Additive = c.Additive,
                        NominalVolumeMl = c.NominalVolumeMl,
                        MinimumVolumeMl = c.MinimumVolumeMl,
                        DrawOrder = c.DrawOrder,
                        RequiresProtectionFromLight = c.RequiresProtectionFromLight,
                        RequiresIceTransport = c.RequiresIceTransport
                    })
                ];
            }

            default:
            {
                var rows = await db.Units.AsNoTracking()
                    .OrderBy(u => u.Code)
                    .Select(u => new
                    {
                        u.Id, u.Code, u.IsActive, u.DisplaySymbol, u.DisplaySymbolArabic,
                        u.UcumCode, u.FactorToSi, u.SiUnitCode,
                        UsageCount = db.TestParameters.Count(p => p.UnitId == u.Id)
                    })
                    .ToListAsync(ct);

                return
                [
                    // A unit has no multilingual name: its symbol is the name, and "mg/dL" is not
                    // translated. The symbol is copied into the name so one grid renders all four
                    // lists without a special case.
                    .. rows.Select(u => Empty(u.Id, u.Code,
                                              new MultilingualText(u.DisplaySymbol,
                                                                   u.DisplaySymbolArabic),
                                              u.IsActive, u.UsageCount) with
                    {
                        DisplaySymbol = u.DisplaySymbol,
                        DisplaySymbolArabic = u.DisplaySymbolArabic,
                        UcumCode = u.UcumCode,
                        FactorToSi = u.FactorToSi,
                        SiUnitCode = u.SiUnitCode
                    })
                ];
            }
        }
    }

    /// <summary>
    /// One row shape serves four lists, so each branch fills only the fields its own list has. The
    /// alternative — four row types and four grids — would be four screens to keep in step for no
    /// gain: the operator is doing the same thing in each.
    /// </summary>
    private static NomenclatureRow Empty(Guid id, string code, MultilingualText name, bool isActive,
                                          int usage) =>
        new(id, code, name, isActive, usage,
            0, false, null, null,
            null, null, 0,
            "#FFFFFF", null, null, null, 0, false, false,
            string.Empty, null, null, null, null);

    // ---- Cross-checks --------------------------------------------------------------------------

    public Task<bool> AnalysisCodeExistsAsync(string code, Guid? exceptId,
                                               CancellationToken ct = default) =>
        db.AnalysisTests.AsNoTracking()
          .AnyAsync(a => a.Code == code && (exceptId == null || a.Id != exceptId), ct);

    public Task<bool> PanelCodeExistsAsync(string code, Guid? exceptId,
                                            CancellationToken ct = default) =>
        db.TestPanels.AsNoTracking()
          .AnyAsync(p => p.Code == code && (exceptId == null || p.Id != exceptId), ct);

    public Task<bool> NomenclatureCodeExistsAsync(NomenclatureKind kind, string code,
                                                   Guid? exceptId, CancellationToken ct = default) =>
        kind switch
        {
            NomenclatureKind.Department => db.Departments.AsNoTracking()
                .AnyAsync(d => d.Code == code && (exceptId == null || d.Id != exceptId), ct),
            NomenclatureKind.SpecimenType => db.SpecimenTypes.AsNoTracking()
                .AnyAsync(s => s.Code == code && (exceptId == null || s.Id != exceptId), ct),
            NomenclatureKind.ContainerType => db.ContainerTypes.AsNoTracking()
                .AnyAsync(c => c.Code == code && (exceptId == null || c.Id != exceptId), ct),
            _ => db.Units.AsNoTracking()
                .AnyAsync(u => u.Code == code && (exceptId == null || u.Id != exceptId), ct)
        };

    /// <summary>
    /// Items neither validated, reported nor cancelled — work that is still expected to produce a
    /// result. Deactivating the analysis under them strands it.
    /// </summary>
    public Task<int> CountOpenItemsAsync(Guid analysisTestId, CancellationToken ct = default) =>
        db.LabOrderItems.AsNoTracking()
          .CountAsync(i => i.AnalysisTestId == analysisTestId
                           && i.Status != LabOrderItemStatus.Validated
                           && i.Status != LabOrderItemStatus.Reported
                           && i.Status != LabOrderItemStatus.Cancelled, ct);

    public Task<int> CountResultsAsync(Guid parameterId, CancellationToken ct = default) =>
        db.TestResults.AsNoTracking()
          .CountAsync(r => r.TestParameterId == parameterId
                           && r.Status != TestResultStatus.Awaited, ct);

    /// <summary>
    /// Results this parameter produced while the interval was in force.
    /// <para>
    /// Dated on <c>EnteredAt</c>, not on the row's creation. The awaited row is created at
    /// registration, so creation time would count every result of every order registered since the
    /// interval opened — including the ones still empty, which have never been interpreted against
    /// anything and which a revision cannot reclassify.
    /// </para>
    /// </summary>
    public Task<int> CountResultsInterpretedAgainstAsync(Guid parameterId, DateOnly since,
                                                          CancellationToken ct = default)
    {
        var from = new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return db.TestResults.AsNoTracking()
                 .CountAsync(r => r.TestParameterId == parameterId
                                  && r.EnteredAt != null
                                  && r.EnteredAt >= from, ct);
    }

    public async Task<IReadOnlyList<string>> GetUsedCodedValuesAsync(
        Guid parameterId, CancellationToken ct = default) =>
        await db.TestResults.AsNoTracking()
                .Where(r => r.TestParameterId == parameterId && r.CodedValue != null)
                .Select(r => r.CodedValue!)
                .Distinct()
                .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetCodedOptionCodesAsync(
        Guid parameterId, CancellationToken ct = default) =>
        await db.CodedResultOptions.AsNoTracking()
                .Where(o => o.TestParameterId == parameterId)
                .Select(o => o.Code)
                .ToListAsync(ct);

    public Task<Guid?> GetAnalysisForParameterAsync(Guid parameterId,
                                                     CancellationToken ct = default) =>
        db.TestParameters.AsNoTracking()
          .Where(p => p.Id == parameterId)
          .Select(p => (Guid?)p.AnalysisTestId)
          .FirstOrDefaultAsync(ct);

    public Task<ParameterSummary?> GetParameterSummaryAsync(Guid parameterId,
                                                             CancellationToken ct = default) =>
        db.TestParameters.AsNoTracking()
          .Where(p => p.Id == parameterId)
          .Select(p => new ParameterSummary(p.Id, p.AnalysisTestId, p.Code, p.ValueType))
          .FirstOrDefaultAsync(ct);

    public Task<AnalysisSummary?> GetAnalysisSummaryAsync(Guid analysisTestId,
                                                           CancellationToken ct = default) =>
        db.AnalysisTests.AsNoTracking()
          .Where(a => a.Id == analysisTestId)
          .Select(a => new AnalysisSummary(a.Id, a.Code, a.IsActive))
          .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<string>> GetInactiveAnalysisCodesAsync(
        IReadOnlyList<Guid> analysisTestIds, CancellationToken ct = default)
    {
        if (analysisTestIds.Count == 0) return [];

        var ids = analysisTestIds.ToList();

        return await db.AnalysisTests.AsNoTracking()
                       .Where(a => ids.Contains(a.Id) && !a.IsActive)
                       .Select(a => a.Code)
                       .ToListAsync(ct);
    }

    public Task<int> CountNomenclatureUsageAsync(NomenclatureKind kind, Guid id,
                                                   CancellationToken ct = default) =>
        kind switch
        {
            NomenclatureKind.Department => db.AnalysisTests.AsNoTracking()
                .CountAsync(a => a.DepartmentId == id && a.IsActive, ct),
            NomenclatureKind.SpecimenType => db.AnalysisTests.AsNoTracking()
                .CountAsync(a => a.SpecimenTypeId == id && a.IsActive, ct),
            NomenclatureKind.ContainerType => db.AnalysisTests.AsNoTracking()
                .CountAsync(a => a.ContainerTypeId == id && a.IsActive, ct),
            _ => db.TestParameters.AsNoTracking().CountAsync(p => p.UnitId == id && p.IsActive, ct)
        };

    public async Task<IReadOnlyList<PartnerSiteOption>> GetPartnerSitesAsync(
        CancellationToken ct = default) =>
        await db.Sites.AsNoTracking()
                .Where(s => s.IsActive
                            && s.Kind == Domain.Organization.LabSiteKind.ReferralPartner)
                .OrderBy(s => s.Code)
                .Select(s => new PartnerSiteOption(s.Id, s.Code, s.Name))
                .ToListAsync(ct);

    /// <summary>
    /// Analysers only, and decommissioned ones excluded. A reference interval tied to an analyser
    /// the laboratory no longer owns can never match a result, so offering one would be offering a
    /// way to make an interval silently unreachable.
    /// </summary>
    public async Task<IReadOnlyList<InstrumentOption>> GetInstrumentOptionsAsync(
        CancellationToken ct = default) =>
        await db.Instruments.AsNoTracking()
                .Where(i => i.Kind == Domain.Equipment.EquipmentKind.Analyser
                            && i.Status != Domain.Equipment.EquipmentStatus.Decommissioned)
                .OrderBy(i => i.Code)
                .Select(i => new InstrumentOption(i.Id, i.Code, i.Name))
                .ToListAsync(ct);

    public void RemoveCodedOption(CodedResultOption option) =>
        db.CodedResultOptions.Remove(option);
}
