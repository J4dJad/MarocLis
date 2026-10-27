using System.Text.RegularExpressions;
using MarocLIS.Application.Abstractions;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Patients;

namespace MarocLIS.Application.Catalog;

// ---- Requests ----------------------------------------------------------------------------------

public sealed record AnalysisRequest
{
    /// <summary>Null creates. The code is set at creation and never changes afterwards.</summary>
    public Guid? Id { get; init; }

    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }
    public MultilingualText? ShortName { get; init; }
    public string? SearchAliases { get; init; }

    public required Guid DepartmentId { get; init; }
    public required Guid SpecimenTypeId { get; init; }
    public Guid? ContainerTypeId { get; init; }
    public decimal? RequiredVolumeMl { get; init; }

    public string? LoincCode { get; init; }
    public string? NationalActCode { get; init; }

    public MultilingualText? Method { get; init; }
    public string? ReagentReference { get; init; }
    public bool IsAccredited { get; init; }
    public bool IsSubcontracted { get; init; }
    public Guid? SubcontractorSiteId { get; init; }

    public int RoutineTurnaroundHours { get; init; } = 24;
    public int UrgentTurnaroundMinutes { get; init; } = 120;
    public bool IsAvailableUrgently { get; init; } = true;
    public string? ScheduledDaysCsv { get; init; }

    public int? FastingHoursOverride { get; init; }
    public int? StabilityAtRoomTemperatureHours { get; init; }
    public int? StabilityRefrigeratedHours { get; init; }
    public MultilingualText? RequiredClinicalInformation { get; init; }
    public ConsentRequirement ConsentRequirement { get; init; } = ConsentRequirement.None;
    public bool IsNotifiableDisease { get; init; }

    public bool IsActive { get; init; } = true;
    public int DisplayOrder { get; init; }
}

public sealed record ParameterRequest
{
    public required Guid AnalysisTestId { get; init; }

    /// <summary>Null creates. The code is set at creation and never changes afterwards.</summary>
    public Guid? Id { get; init; }

    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }
    public MultilingualText? ShortName { get; init; }
    public required ResultValueType ValueType { get; init; }
    public string? LoincCode { get; init; }

    public Guid? UnitId { get; init; }
    public int Decimals { get; init; } = 2;

    public decimal? MeasuringRangeLow { get; init; }
    public decimal? MeasuringRangeHigh { get; init; }
    public decimal? TechnicalLimitLow { get; init; }
    public decimal? TechnicalLimitHigh { get; init; }
    public decimal? CriticalLow { get; init; }
    public decimal? CriticalHigh { get; init; }
    public decimal? AnalyticalCvPercent { get; init; }

    public decimal? DeltaCheckPercent { get; init; }
    public int? DeltaCheckWindowDays { get; init; }

    public string? CalculationFormula { get; init; }
    public bool IsInternalOnly { get; init; }
    public bool AlwaysShowOnReport { get; init; } = true;
    public int DisplayOrder { get; init; }
    public int IndentLevel { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>The whole allowed-answer list for a coded or semi-quantitative parameter.</summary>
    public IReadOnlyList<CodedOptionRequest> CodedOptions { get; init; } = [];
}

public sealed record CodedOptionRequest(string Code, MultilingualText Label, int DisplayOrder,
                                        bool IsAbnormal, bool IsCritical, bool IsExpectedNormal);

public sealed record ReferenceRangeRequest
{
    public required Guid TestParameterId { get; init; }

    /// <summary>Null creates. A change to the limits of an interval in force opens a revision.</summary>
    public Guid? Id { get; init; }

    public Gender AppliesToGender { get; init; } = Gender.Unknown;
    public int? AgeFromDays { get; init; }
    public int? AgeToDays { get; init; }

    /// <summary>The unit the band was typed in, in days per unit; see <see cref="ReferenceRange.AgeUnitDays"/>.</summary>
    public int? AgeUnitDays { get; init; }

    public bool? AppliesToPregnancy { get; init; }
    public int? PregnancyTrimester { get; init; }
    public Guid? InstrumentId { get; init; }

    public decimal? LowerLimit { get; init; }
    public decimal? UpperLimit { get; init; }
    public MultilingualText? DisplayText { get; init; }
    public string? ExpectedCodedValue { get; init; }
    public bool IsDecisionThreshold { get; init; }

    public string? Source { get; init; }
    public DateOnly? LastReviewedOn { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>A tariff revision. There is no edit: a price is versioned, never overwritten.</summary>
public sealed record TariffRequest
{
    public required Guid AnalysisTestId { get; init; }

    /// <summary>Null is the laboratory's own price list, used for self-paying patients.</summary>
    public Guid? PayerId { get; init; }

    public required DateOnly ValidFrom { get; init; }
    public string LetterKey { get; init; } = "B";
    public decimal Coefficient { get; init; }
    public decimal LetterKeyValue { get; init; }
    public decimal Price { get; init; }
    public bool IsVatExempt { get; init; } = true;
    public decimal VatRate { get; init; }
}

public sealed record PanelRequest
{
    public Guid? Id { get; init; }

    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }
    public string? SearchAliases { get; init; }
    public decimal? PackagePrice { get; init; }
    public bool ItemizeOnInvoice { get; init; } = true;
    public bool GroupOnReport { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>The whole membership. Analyses absent from the list are removed from the panel.</summary>
    public IReadOnlyList<PanelMemberRequest> Members { get; init; } = [];
}

public sealed record PanelMemberRequest(Guid AnalysisTestId, bool IsOptional);

public sealed record ReflexRuleRequest
{
    public Guid? Id { get; init; }

    public required Guid TriggerParameterId { get; init; }
    public required Guid AnalysisToAddId { get; init; }
    public required MultilingualText Description { get; init; }

    public ReflexCondition Condition { get; init; } = ReflexCondition.GreaterThan;
    public decimal? Threshold { get; init; }
    public decimal? ThresholdUpper { get; init; }
    public string? TriggerCodedValue { get; init; }
    public bool AddAutomatically { get; init; }
    public bool IsBillable { get; init; } = true;
    public bool IsActive { get; init; } = true;
}

public sealed record NomenclatureRequest
{
    public Guid? Id { get; init; }
    public required string Code { get; init; }
    public required MultilingualText Name { get; init; }

    // Department
    public int DisplayOrder { get; init; }
    public bool IsInAccreditationScope { get; init; }
    public Guid? SupervisorUserId { get; init; }

    // SpecimenType
    public MultilingualText? CollectionInstructions { get; init; }
    public MultilingualText? PatientInstructions { get; init; }
    public int FastingHours { get; init; }

    // ContainerType
    public string CapColourHex { get; init; } = "#FFFFFF";
    public MultilingualText? Additive { get; init; }
    public decimal? NominalVolumeMl { get; init; }
    public decimal? MinimumVolumeMl { get; init; }
    public int DrawOrder { get; init; }
    public bool RequiresProtectionFromLight { get; init; }
    public bool RequiresIceTransport { get; init; }

    // MeasurementUnit
    public string DisplaySymbol { get; init; } = string.Empty;
    public string? DisplaySymbolArabic { get; init; }
    public string? UcumCode { get; init; }
    public decimal? FactorToSi { get; init; }
    public string? SiUnitCode { get; init; }

    public bool IsActive { get; init; } = true;
}

// ---- Rows --------------------------------------------------------------------------------------

public sealed record AnalysisRow(
    Guid Id, string Code, MultilingualText Name, string DepartmentCode,
    MultilingualText DepartmentName, Guid DepartmentId, Guid SpecimenTypeId,
    MultilingualText SpecimenTypeName, Guid? ContainerTypeId, string? ContainerName,
    int ParameterCount, int ActiveParameterCount, int RangeCount, int TariffCount,
    decimal? CurrentPrice, decimal? CurrentCoefficient, string? NationalActCode,
    int RoutineTurnaroundHours, bool IsAccredited, bool IsSubcontracted,
    ConsentRequirement ConsentRequirement, bool IsActive, int OrderedCount,
    int UnconfirmedAgeBandCount = 0)
{
    /// <summary>
    /// Orderable in practice. An analysis with no active parameter has nowhere to put a result, and
    /// one with no tariff prices at zero — both are catalogue entries that fail at the counter.
    /// </summary>
    public bool IsOrderable => IsActive && ActiveParameterCount > 0 && TariffCount > 0;

    public bool HasBeenOrdered => OrderedCount > 0;
}

public sealed record ParameterRow(
    Guid Id, Guid AnalysisTestId, string Code, MultilingualText Name, ResultValueType ValueType,
    Guid? UnitId, string? UnitSymbol, int Decimals,
    decimal? MeasuringRangeLow, decimal? MeasuringRangeHigh,
    decimal? TechnicalLimitLow, decimal? TechnicalLimitHigh,
    decimal? CriticalLow, decimal? CriticalHigh, decimal? AnalyticalCvPercent,
    decimal? DeltaCheckPercent, int? DeltaCheckWindowDays,
    string? CalculationFormula, bool IsInternalOnly, bool AlwaysShowOnReport,
    int DisplayOrder, int IndentLevel, bool IsActive,
    int RangeCount, int CodedOptionCount, int ResultCount, int UnconfirmedAgeBandCount = 0)
{
    public bool IsCalculated => !string.IsNullOrWhiteSpace(CalculationFormula);

    /// <summary>A result exists, so the value type is frozen and the row cannot be deleted.</summary>
    public bool HasResults => ResultCount > 0;

    /// <summary>
    /// A control that cannot fire. A coded parameter with no options has no answer an operator can
    /// pick; a numeric one with no interval produces a value nobody can interpret.
    /// </summary>
    public bool IsUnusable =>
        (ValueType is ResultValueType.Coded or ResultValueType.SemiQuantitative && CodedOptionCount == 0)
        || (ValueType is ResultValueType.Numeric or ResultValueType.NumericWithOperator
            && RangeCount == 0 && !IsInternalOnly);
}

public sealed record ReferenceRangeRow(
    Guid Id, Guid TestParameterId, Gender AppliesToGender,
    int? AgeFromDays, int? AgeToDays, bool? AppliesToPregnancy, int? PregnancyTrimester,
    Guid? InstrumentId, string? InstrumentName,
    decimal? LowerLimit, decimal? UpperLimit, MultilingualText? DisplayText,
    string? ExpectedCodedValue, bool IsDecisionThreshold, string? Source,
    DateOnly? LastReviewedOn, DateOnly ValidFrom, DateOnly? ValidUntil, bool IsActive,
    int Specificity, int? AgeUnitDays = null)
{
    public bool IsInForce(DateOnly on) =>
        IsActive && ValidFrom <= on && (ValidUntil is null || ValidUntil >= on);

    /// <summary>
    /// ISO 15189 §5.5.2 requires the laboratory to review its intervals periodically. Never
    /// reviewed, or reviewed more than three years ago, is an audit finding waiting to happen.
    /// </summary>
    public bool NeedsReview(DateOnly today) =>
        LastReviewedOn is null || LastReviewedOn < today.AddYears(-3);

    /// <summary>
    /// Saved before the age unit was kept, with bounds that read differently in days and in
    /// months; see <see cref="ReferenceRange.HasUnconfirmedAgeUnit"/>. Re-saving it in the unit it
    /// was written in clears it. A withdrawn interval is read by nothing and is not listed.
    /// </summary>
    public bool HasUnconfirmedAgeUnit =>
        IsActive && ReferenceRange.HasUnconfirmedAgeUnit(AgeFromDays, AgeToDays, AgeUnitDays);
}

public sealed record TariffRow(
    Guid Id, Guid AnalysisTestId, Guid? PayerId, string? PayerName,
    DateOnly ValidFrom, DateOnly? ValidUntil, string LetterKey, decimal Coefficient,
    decimal LetterKeyValue, decimal Price, bool IsVatExempt, decimal VatRate)
{
    public decimal NationalReferenceBase => Math.Round(Coefficient * LetterKeyValue, 2);

    /// <summary>What the patient bears above the reimbursement base — the figure they query.</summary>
    public decimal ExcessOverReference => Math.Max(0m, Price - NationalReferenceBase);

    public bool IsInForce(DateOnly on) =>
        ValidFrom <= on && (ValidUntil is null || ValidUntil >= on);
}

public sealed record PanelRow(
    Guid Id, string Code, MultilingualText Name, decimal? PackagePrice,
    bool ItemizeOnInvoice, bool GroupOnReport, int DisplayOrder, bool IsActive,
    int MemberCount, int OptionalCount, int InactiveMemberCount, decimal ComponentTotal,
    IReadOnlyList<PanelMemberRow> Members)
{
    /// <summary>
    /// A panel whose package price is above the sum of its parts: the operator is being asked to
    /// sell a bundle at a premium, which is almost always a data-entry slip rather than a policy.
    /// </summary>
    public bool PackageAboveComponents => PackagePrice is { } price && price > ComponentTotal;
}

public sealed record PanelMemberRow(Guid AnalysisTestId, string Code, MultilingualText Name,
                                    bool IsOptional, bool IsAnalysisActive, decimal Price);

public sealed record ReflexRuleRow(
    Guid Id, Guid TriggerParameterId, string TriggerParameterCode,
    MultilingualText TriggerParameterName, string TriggerAnalysisCode,
    Guid AnalysisToAddId, string AnalysisToAddCode, MultilingualText AnalysisToAddName,
    MultilingualText Description, ReflexCondition Condition,
    decimal? Threshold, decimal? ThresholdUpper, string? TriggerCodedValue,
    bool AddAutomatically, bool IsBillable, bool IsActive)
{
    /// <summary>
    /// A rule whose condition can never be met. It appears configured on this screen and does
    /// nothing at the bench, which is worse than not existing: somebody is relying on it.
    /// </summary>
    public bool CanNeverFire => Condition switch
    {
        ReflexCondition.GreaterThan or ReflexCondition.LessThan => Threshold is null,
        ReflexCondition.Between or ReflexCondition.Outside => Threshold is null || ThresholdUpper is null,
        ReflexCondition.EqualsCoded => string.IsNullOrWhiteSpace(TriggerCodedValue),
        _ => true
    };
}

public sealed record NomenclatureRow(
    Guid Id, string Code, MultilingualText Name, bool IsActive, int UsageCount,
    int DisplayOrder, bool IsInAccreditationScope, Guid? SupervisorUserId, string? SupervisorName,
    MultilingualText? CollectionInstructions, MultilingualText? PatientInstructions, int FastingHours,
    string CapColourHex, MultilingualText? Additive, decimal? NominalVolumeMl,
    decimal? MinimumVolumeMl, int DrawOrder, bool RequiresProtectionFromLight,
    bool RequiresIceTransport,
    string DisplaySymbol, string? DisplaySymbolArabic, string? UcumCode,
    decimal? FactorToSi, string? SiUnitCode);

/// <summary>Which nomenclature list a request or query concerns.</summary>
public enum NomenclatureKind
{
    Department = 0,
    SpecimenType = 1,
    ContainerType = 2,
    MeasurementUnit = 3
}

// ---- Service -----------------------------------------------------------------------------------

/// <summary>
/// Maintenance of the analysis catalogue: analyses, their parameters, reference intervals, tariffs,
/// panels, reflex rules and the nomenclature lists underneath them.
/// <para>
/// Until this existed, the catalogue was whatever the seeder wrote. A laboratory could not add an
/// analysis, correct a reference interval or enter a tariff revision without a developer editing
/// seed data and redeploying — and both of those are routine events: intervals must be reviewed
/// periodically under ISO 15189 §5.5.2, and the national reference tariff is revised by decree.
/// </para>
/// <para>
/// Four rules run through everything here, and they are all about not falsifying the past.
/// A <b>code</b> is never renamed: worklists, instrument mappings and historical results all key on
/// it. A <b>value type</b> is frozen once a result exists, because reading an old numeric column as
/// a coded one makes the history unreadable. A <b>tariff</b> is versioned, never overwritten, so an
/// invoice reprints with the price that was charged. A <b>reference interval</b> in force is closed
/// and superseded rather than edited, so a result keeps the interval it was interpreted against.
/// </para>
/// </summary>
public sealed class CatalogMaintenanceService(
    IRepository<AnalysisTest> analyses,
    IRepository<TestPanel> panels,
    IRepository<ReflexRule> reflexRules,
    IRepository<Department> departments,
    IRepository<SpecimenType> specimenTypes,
    IRepository<ContainerType> containerTypes,
    IRepository<MeasurementUnit> units,
    ICatalogMaintenanceQueries queries,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    IClock clock)
{
    /// <summary>Parameter codes a calculation formula refers to, e.g. <c>[CHOL]</c>.</summary>
    private static readonly Regex FormulaReferences =
        new(@"\[([A-Za-z0-9_]+)\]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // ---- Reading -------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<AnalysisRow>>> GetAnalysesAsync(
        string? search = null, Guid? departmentId = null, bool includeInactive = true,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<AnalysisRow>>(permitted.FirstError);

        return Result.Success(
            await queries.GetAnalysesAsync(search, departmentId, includeInactive, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<ParameterRow>>> GetParametersAsync(
        Guid analysisTestId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ParameterRow>>(permitted.FirstError);

        return Result.Success(await queries.GetParametersAsync(analysisTestId, ct));
    }

    public async Task<Result<IReadOnlyList<ReferenceRangeRow>>> GetRangesAsync(
        Guid parameterId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ReferenceRangeRow>>(permitted.FirstError);

        return Result.Success(await queries.GetRangesAsync(parameterId, ct));
    }

    public async Task<Result<IReadOnlyList<TariffRow>>> GetTariffsAsync(
        Guid analysisTestId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<TariffRow>>(permitted.FirstError);

        return Result.Success(await queries.GetTariffsAsync(analysisTestId, ct));
    }

    public async Task<Result<IReadOnlyList<PanelRow>>> GetPanelsAsync(
        bool includeInactive = true, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PanelRow>>(permitted.FirstError);

        return Result.Success(await queries.GetPanelsAsync(includeInactive, clock.Today, ct));
    }

    public async Task<Result<IReadOnlyList<ReflexRuleRow>>> GetReflexRulesAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<ReflexRuleRow>>(permitted.FirstError);

        return Result.Success(await queries.GetReflexRulesAsync(ct));
    }

    public async Task<Result<IReadOnlyList<NomenclatureRow>>> GetNomenclatureAsync(
        NomenclatureKind kind, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<NomenclatureRow>>(permitted.FirstError);

        return Result.Success(await queries.GetNomenclatureAsync(kind, ct));
    }

    /// <summary>
    /// The partner laboratories a subcontracted analysis can be sent to.
    /// <para>
    /// Offered so the subcontracting tick box has somewhere to point. An analysis marked
    /// subcontracted without a partner site is refused by <see cref="AnalysisTest.Validate"/>, so a
    /// tick box with no picker beside it would be a control that can only produce a refusal.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<PartnerSiteOption>>> GetPartnerSitesAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<PartnerSiteOption>>(permitted.FirstError);

        return Result.Success(await queries.GetPartnerSitesAsync(ct));
    }

    /// <summary>
    /// The payers a tariff can be entered for: every active one but self-pay, whose price is the
    /// laboratory's own list.
    /// </summary>
    public async Task<Result<IReadOnlyList<TariffPayerOption>>> GetPayerOptionsAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<TariffPayerOption>>(permitted.FirstError);

        return Result.Success(await queries.GetPayerOptionsAsync(ct));
    }

    /// <summary>
    /// The most recent value entered for a letter key on any analysis. The value is national (one B
    /// for the whole nomenclature), so a first tariff starts from it rather than from zero.
    /// </summary>
    public async Task<Result<decimal?>> GetLatestLetterKeyValueAsync(string letterKey,
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<decimal?>(permitted.FirstError);

        return Result.Success(await queries.GetLatestLetterKeyValueAsync(letterKey, ct));
    }

    /// <summary>
    /// The analysers a reference interval can be restricted to.
    /// <para>
    /// Method-dependent intervals are the norm for hormones and immunoassays: two analysers
    /// measuring the same TSH do not share a normal range, and reporting one against the other's
    /// interval mislabels healthy patients. <see cref="ReferenceRange.InstrumentId"/> exists for
    /// that, and without this list it would be a field nothing could set.
    /// </para>
    /// </summary>
    public async Task<Result<IReadOnlyList<InstrumentOption>>> GetInstrumentOptionsAsync(
        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<IReadOnlyList<InstrumentOption>>(permitted.FirstError);

        return Result.Success(await queries.GetInstrumentOptionsAsync(ct));
    }

    /// <summary>
    /// The stored analysis as the request that would save it unchanged.
    /// <para>
    /// The editor starts from this rather than from the grid row. The row is a summary, and an
    /// editor built from it opened with LOINC, stabilities, fasting, clinical information, the
    /// partner laboratory and the urgent turnaround blank or defaulted, then saved those over the
    /// record: correcting a turnaround erased a stability window. Starting from the whole request,
    /// a field the dialog does not show (the execution days) is carried through as stored.
    /// </para>
    /// </summary>
    public async Task<Result<AnalysisRequest>> GetAnalysisForEditAsync(Guid analysisTestId,
                                                                        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<AnalysisRequest>(permitted.FirstError);

        var analysis = await analyses.GetByIdAsync(analysisTestId, ct);
        if (analysis is null) return Result.Failure<AnalysisRequest>(Error.NotFound("Test_NotFound"));

        return Result.Success(new AnalysisRequest
        {
            Id = analysis.Id,
            Code = analysis.Code,
            Name = analysis.Name,
            ShortName = analysis.ShortName,
            SearchAliases = analysis.SearchAliases,
            DepartmentId = analysis.DepartmentId,
            SpecimenTypeId = analysis.SpecimenTypeId,
            ContainerTypeId = analysis.ContainerTypeId,
            RequiredVolumeMl = analysis.RequiredVolumeMl,
            LoincCode = analysis.LoincCode,
            NationalActCode = analysis.NationalActCode,
            Method = analysis.Method,
            ReagentReference = analysis.ReagentReference,
            IsAccredited = analysis.IsAccredited,
            IsSubcontracted = analysis.IsSubcontracted,
            SubcontractorSiteId = analysis.SubcontractorSiteId,
            RoutineTurnaroundHours = analysis.RoutineTurnaroundHours,
            UrgentTurnaroundMinutes = analysis.UrgentTurnaroundMinutes,
            IsAvailableUrgently = analysis.IsAvailableUrgently,
            ScheduledDaysCsv = analysis.ScheduledDaysCsv,
            FastingHoursOverride = analysis.FastingHoursOverride,
            StabilityAtRoomTemperatureHours = analysis.StabilityAtRoomTemperatureHours,
            StabilityRefrigeratedHours = analysis.StabilityRefrigeratedHours,
            RequiredClinicalInformation = analysis.RequiredClinicalInformation,
            ConsentRequirement = analysis.ConsentRequirement,
            IsNotifiableDisease = analysis.IsNotifiableDisease,
            IsActive = analysis.IsActive,
            DisplayOrder = analysis.DisplayOrder
        });
    }

    /// <summary>
    /// The stored parameter as the request that would save it unchanged, allowed answers included.
    /// <para>
    /// The answer list is replaced as a whole on save, so an editor that opened it empty forced the
    /// operator to retype every answer of a coded parameter or lose them. Answers retired because
    /// a result used them are left out: they are no longer offered, and re-saving keeps them so.
    /// </para>
    /// </summary>
    public async Task<Result<ParameterRequest>> GetParameterForEditAsync(
        Guid analysisTestId, Guid parameterId, CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.View);
        if (permitted.IsFailure) return Result.Failure<ParameterRequest>(permitted.FirstError);

        var analysis = await analyses.GetByIdWithDetailsAsync(analysisTestId, ct);
        if (analysis is null) return Result.Failure<ParameterRequest>(Error.NotFound("Test_NotFound"));

        var parameter = analysis.Parameters.FirstOrDefault(p => p.Id == parameterId);
        if (parameter is null)
            return Result.Failure<ParameterRequest>(Error.NotFound("Parameter_NotFound"));

        return Result.Success(new ParameterRequest
        {
            Id = parameter.Id,
            AnalysisTestId = analysis.Id,
            Code = parameter.Code,
            Name = parameter.Name,
            ShortName = parameter.ShortName,
            ValueType = parameter.ValueType,
            LoincCode = parameter.LoincCode,
            UnitId = parameter.UnitId,
            Decimals = parameter.Decimals,
            MeasuringRangeLow = parameter.MeasuringRangeLow,
            MeasuringRangeHigh = parameter.MeasuringRangeHigh,
            TechnicalLimitLow = parameter.TechnicalLimitLow,
            TechnicalLimitHigh = parameter.TechnicalLimitHigh,
            CriticalLow = parameter.CriticalLow,
            CriticalHigh = parameter.CriticalHigh,
            AnalyticalCvPercent = parameter.AnalyticalCvPercent,
            DeltaCheckPercent = parameter.DeltaCheckPercent,
            DeltaCheckWindowDays = parameter.DeltaCheckWindowDays,
            CalculationFormula = parameter.CalculationFormula,
            IsInternalOnly = parameter.IsInternalOnly,
            AlwaysShowOnReport = parameter.AlwaysShowOnReport,
            DisplayOrder = parameter.DisplayOrder,
            IndentLevel = parameter.IndentLevel,
            IsActive = parameter.IsActive,
            CodedOptions = [.. parameter.CodedOptions
                                        .Where(o => o.DisplayOrder != int.MaxValue)
                                        .OrderBy(o => o.DisplayOrder)
                                        .Select(o => new CodedOptionRequest(
                                            o.Code, o.Label, o.DisplayOrder,
                                            o.IsAbnormal, o.IsCritical, o.IsExpectedNormal))]
        });
    }

    // ---- Analyses ------------------------------------------------------------------------------

    /// <summary>
    /// Creates or updates an analysis.
    /// <para>
    /// The code is settable only at creation. It is the join between the catalogue, the instrument
    /// mappings, the worklists and every historical result, and renaming it would silently detach
    /// all of them — so the domain keeps the setter private and this method does not offer one.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> SaveAnalysisAsync(AnalysisRequest request,
                                                       CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code)) errors.Add(Error.Validation("Test_CodeRequired"));
        if (string.IsNullOrWhiteSpace(request.Name.Fr)) errors.Add(Error.Validation("Test_NameFrenchRequired"));
        if (request.RoutineTurnaroundHours <= 0) errors.Add(Error.Validation("Test_TurnaroundMustBePositive"));
        if (request.UrgentTurnaroundMinutes <= 0) errors.Add(Error.Validation("Test_TurnaroundMustBePositive"));
        if (request.IsSubcontracted && request.SubcontractorSiteId is null)
            errors.Add(Error.Validation("Test_SubcontractorRequired"));

        // An analysis offered urgently that promises a slower urgent turnaround than its routine
        // one is a promise the reception desk will repeat to a patient and the laboratory will
        // break. Caught here rather than discovered on a complaint.
        if (request.IsAvailableUrgently
            && request.UrgentTurnaroundMinutes > request.RoutineTurnaroundHours * 60)
            errors.Add(Error.Validation("Test_UrgentSlowerThanRoutine"));

        if (request.StabilityAtRoomTemperatureHours is { } room
            && request.StabilityRefrigeratedHours is { } fridge && room > fridge)
            errors.Add(Error.Validation("Test_RoomStabilityAboveRefrigerated"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var code = request.Code.Trim().ToUpperInvariant();

        if (request.Id is null)
        {
            if (await queries.AnalysisCodeExistsAsync(code, null, ct))
                return Result.Failure<Guid>(Error.Conflict("Test_CodeAlreadyUsed", code));

            var created = new AnalysisTest(code, request.Name, request.DepartmentId,
                                            request.SpecimenTypeId);

            Apply(created, request);

            await analyses.AddAsync(created, ct);

            unitOfWork.AuditReason = $"Création de l'analyse {code}";
            await unitOfWork.SaveChangesAsync(ct);

            return Result.Success(created.Id);
        }

        var analysis = await analyses.GetByIdWithDetailsAsync(request.Id.Value, ct);
        if (analysis is null) return Result.Failure<Guid>(Error.NotFound("Test_NotFound"));

        if (!string.Equals(analysis.Code, code, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Guid>(Error.Regulatory("Test_CodeImmutable", analysis.Code));

        // Deactivating an analysis that is mid-flight strands the work: the item stays on the
        // order, the result row exists, and nothing in the worklist will offer it again.
        if (analysis.IsActive && !request.IsActive)
        {
            var open = await queries.CountOpenItemsAsync(analysis.Id, ct);

            if (open > 0)
                return Result.Failure<Guid>(Error.Conflict("Test_HasOpenItemsCannotDeactivate", open));
        }

        Apply(analysis, request);

        unitOfWork.AuditReason = $"Modification de l'analyse {analysis.Code}";
        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(analysis.Id);
    }

    private static void Apply(AnalysisTest analysis, AnalysisRequest request)
    {
        analysis.Name = request.Name;
        analysis.ShortName = request.ShortName;
        analysis.SearchAliases = Trimmed(request.SearchAliases);
        analysis.DepartmentId = request.DepartmentId;
        analysis.SpecimenTypeId = request.SpecimenTypeId;
        analysis.ContainerTypeId = request.ContainerTypeId;
        analysis.RequiredVolumeMl = request.RequiredVolumeMl;
        analysis.LoincCode = Trimmed(request.LoincCode);
        analysis.NationalActCode = Trimmed(request.NationalActCode);
        analysis.Method = request.Method;
        analysis.ReagentReference = Trimmed(request.ReagentReference);
        analysis.IsAccredited = request.IsAccredited;
        analysis.IsSubcontracted = request.IsSubcontracted;
        analysis.SubcontractorSiteId = request.IsSubcontracted ? request.SubcontractorSiteId : null;
        analysis.RoutineTurnaroundHours = request.RoutineTurnaroundHours;
        analysis.UrgentTurnaroundMinutes = request.UrgentTurnaroundMinutes;
        analysis.IsAvailableUrgently = request.IsAvailableUrgently;
        analysis.ScheduledDaysCsv = Trimmed(request.ScheduledDaysCsv);
        analysis.FastingHoursOverride = request.FastingHoursOverride;
        analysis.StabilityAtRoomTemperatureHours = request.StabilityAtRoomTemperatureHours;
        analysis.StabilityRefrigeratedHours = request.StabilityRefrigeratedHours;
        analysis.RequiredClinicalInformation = request.RequiredClinicalInformation;
        analysis.ConsentRequirement = request.ConsentRequirement;
        analysis.IsNotifiableDisease = request.IsNotifiableDisease;
        analysis.IsActive = request.IsActive;
        analysis.DisplayOrder = request.DisplayOrder;
    }

    // ---- Parameters ----------------------------------------------------------------------------

    /// <summary>
    /// Creates or updates a parameter, together with its whole allowed-answer list.
    /// <para>
    /// The value type is frozen once a result exists. A numeric column reinterpreted as coded does
    /// not just break the next entry — it makes every stored value unreadable, because the reader
    /// no longer knows which column the answer is in.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> SaveParameterAsync(ParameterRequest request,
                                                        CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var analysis = await analyses.GetByIdWithDetailsAsync(request.AnalysisTestId, ct);
        if (analysis is null) return Result.Failure<Guid>(Error.NotFound("Test_NotFound"));

        var errors = Validate(request, analysis);
        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var code = request.Code.Trim().ToUpperInvariant();

        TestParameter parameter;

        if (request.Id is null)
        {
            if (analysis.Parameters.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
                return Result.Failure<Guid>(Error.Conflict("Parameter_CodeAlreadyUsed", code));

            parameter = analysis.AddParameter(new TestParameter(code, request.Name, request.ValueType));
        }
        else
        {
            var found = analysis.Parameters.FirstOrDefault(p => p.Id == request.Id.Value);

            if (found is null) return Result.Failure<Guid>(Error.NotFound("Parameter_NotFound"));

            parameter = found;

            if (!string.Equals(parameter.Code, code, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Guid>(Error.Regulatory("Parameter_CodeImmutable", parameter.Code));

            if (parameter.ValueType != request.ValueType)
            {
                var stored = await queries.CountResultsAsync(parameter.Id, ct);

                if (stored > 0)
                    return Result.Failure<Guid>(
                        Error.Regulatory("Parameter_ValueTypeFrozen", stored));

                parameter.ValueType = request.ValueType;
            }
        }

        parameter.Name = request.Name;
        parameter.ShortName = request.ShortName;
        parameter.LoincCode = Trimmed(request.LoincCode);
        parameter.UnitId = request.UnitId;
        parameter.Decimals = Math.Clamp(request.Decimals, 0, 6);
        parameter.MeasuringRangeLow = request.MeasuringRangeLow;
        parameter.MeasuringRangeHigh = request.MeasuringRangeHigh;
        parameter.TechnicalLimitLow = request.TechnicalLimitLow;
        parameter.TechnicalLimitHigh = request.TechnicalLimitHigh;
        parameter.CriticalLow = request.CriticalLow;
        parameter.CriticalHigh = request.CriticalHigh;
        parameter.AnalyticalCvPercent = request.AnalyticalCvPercent;
        parameter.DeltaCheckPercent = request.DeltaCheckPercent;
        parameter.DeltaCheckWindowDays = request.DeltaCheckWindowDays;
        parameter.CalculationFormula = Trimmed(request.CalculationFormula);
        parameter.IsInternalOnly = request.IsInternalOnly;
        parameter.AlwaysShowOnReport = request.AlwaysShowOnReport;
        parameter.IndentLevel = Math.Max(0, request.IndentLevel);
        parameter.IsActive = request.IsActive;

        if (request.DisplayOrder > 0) parameter.DisplayOrder = request.DisplayOrder;

        await ReplaceCodedOptionsAsync(parameter, request, ct);

        unitOfWork.AuditReason =
            $"{(request.Id is null ? "Création" : "Modification")} du paramètre "
            + $"{analysis.Code}/{parameter.Code}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(parameter.Id);
    }

    private static List<Error> Validate(ParameterRequest request, AnalysisTest analysis)
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code)) errors.Add(Error.Validation("Parameter_CodeRequired"));
        if (string.IsNullOrWhiteSpace(request.Name.Fr))
            errors.Add(Error.Validation("Parameter_NameFrenchRequired"));

        var numeric = request.ValueType is ResultValueType.Numeric
                                        or ResultValueType.NumericWithOperator;

        // The limits have to nest: technical outside measuring, and each pair the right way round.
        // A measuring range wider than the technical limits means entry is refused for values the
        // method is supposed to report, which looks like a broken analyser to a technician.
        if (request.MeasuringRangeLow is { } mLow && request.MeasuringRangeHigh is { } mHigh
            && mLow > mHigh)
            errors.Add(Error.Validation("Parameter_MeasuringRangeInverted"));

        if (request.TechnicalLimitLow is { } tLow && request.TechnicalLimitHigh is { } tHigh
            && tLow > tHigh)
            errors.Add(Error.Validation("Parameter_TechnicalLimitsInverted"));

        if (request.CriticalLow is { } cLow && request.CriticalHigh is { } cHigh && cLow > cHigh)
            errors.Add(Error.Validation("Parameter_CriticalLimitsInverted"));

        if (request.TechnicalLimitLow is { } techLow && request.MeasuringRangeLow is { } measLow
            && techLow > measLow)
            errors.Add(Error.Validation("Parameter_TechnicalNarrowerThanMeasuring"));

        if (request.TechnicalLimitHigh is { } techHigh && request.MeasuringRangeHigh is { } measHigh
            && techHigh < measHigh)
            errors.Add(Error.Validation("Parameter_TechnicalNarrowerThanMeasuring"));

        // A delta check with no window compares against a result of any age. A urea from two years
        // ago will differ by more than any threshold, so the flag fires on everything and the
        // technician learns to ignore it — which disables the one check that catches a tube swap.
        if (request.DeltaCheckPercent is { } delta)
        {
            if (delta <= 0m) errors.Add(Error.Validation("Parameter_DeltaMustBePositive"));
            if (request.DeltaCheckWindowDays is null or <= 0)
                errors.Add(Error.Validation("Parameter_DeltaNeedsWindow"));
        }

        if (!numeric && request.ValueType != ResultValueType.Heading
                     && request.DeltaCheckPercent is not null)
            errors.Add(Error.Validation("Parameter_DeltaNeedsNumeric"));

        // A picker with no options: the technician has no answer to choose and the parameter can
        // never be completed. The same class of defect as an analyser with no calibration interval.
        if (request.ValueType is ResultValueType.Coded or ResultValueType.SemiQuantitative
            && request.CodedOptions.Count == 0)
            errors.Add(Error.Validation("Parameter_CodedNeedsOptions"));

        if (request.CodedOptions.Count > 0)
        {
            if (request.CodedOptions.Select(o => o.Code.Trim().ToUpperInvariant()).Distinct().Count()
                != request.CodedOptions.Count)
                errors.Add(Error.Validation("Parameter_DuplicateOptionCode"));

            if (request.CodedOptions.Any(o => string.IsNullOrWhiteSpace(o.Code)
                                              || string.IsNullOrWhiteSpace(o.Label.Fr)))
                errors.Add(Error.Validation("Parameter_OptionNeedsCodeAndLabel"));
        }

        if (request.ValueType == ResultValueType.Heading)
        {
            if (!string.IsNullOrWhiteSpace(request.CalculationFormula))
                errors.Add(Error.Validation("Parameter_HeadingCannotCalculate"));
        }
        else if (!string.IsNullOrWhiteSpace(request.CalculationFormula))
        {
            if (!numeric) errors.Add(Error.Validation("Parameter_FormulaNeedsNumeric"));

            // Every code the formula refers to must exist on this analysis. An unresolvable
            // reference does not raise anything at the bench: the calculation simply produces
            // nothing, and the parameter sits empty on the report with no explanation.
            var known = analysis.Parameters
                .Where(p => request.Id is null || p.Id != request.Id.Value)
                .Select(p => p.Code)
                .Append(request.Code.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var referenced = FormulaReferences.Matches(request.CalculationFormula)
                                              .Select(m => m.Groups[1].Value)
                                              .Distinct(StringComparer.OrdinalIgnoreCase)
                                              .ToList();

            if (referenced.Count == 0)
                errors.Add(Error.Validation("Parameter_FormulaHasNoReference"));

            var unknown = referenced.Where(r => !known.Contains(r)).ToList();

            if (unknown.Count > 0)
                errors.Add(Error.Validation("Parameter_FormulaUnknownReference",
                                            string.Join(", ", unknown)));

            // A formula referring to itself never resolves: the engine needs the value it is
            // being asked to produce.
            if (referenced.Contains(request.Code.Trim(), StringComparer.OrdinalIgnoreCase))
                errors.Add(Error.Validation("Parameter_FormulaSelfReference"));
        }

        return errors;
    }

    /// <summary>
    /// Replaces the allowed-answer list. Options already used by a stored result are kept even when
    /// the operator drops them from the list: deleting one would leave historical results pointing
    /// at a code with no label, which is a result nobody can read.
    /// </summary>
    private async Task ReplaceCodedOptionsAsync(TestParameter parameter, ParameterRequest request,
                                                 CancellationToken ct)
    {
        if (request.CodedOptions.Count == 0 && parameter.CodedOptions.Count == 0) return;

        var used = await queries.GetUsedCodedValuesAsync(parameter.Id, ct);

        var wanted = request.CodedOptions
            .Select(o => o with { Code = o.Code.Trim().ToUpperInvariant() })
            .ToList();

        foreach (var option in parameter.CodedOptions.ToList())
        {
            var match = wanted.FirstOrDefault(
                o => string.Equals(o.Code, option.Code, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                option.Label = match.Label;
                option.DisplayOrder = match.DisplayOrder;
                option.IsAbnormal = match.IsAbnormal;
                option.IsCritical = match.IsCritical;
                option.IsExpectedNormal = match.IsExpectedNormal;
                continue;
            }

            // Not wanted any more. Retired from the picker if a result used it, dropped otherwise.
            if (used.Contains(option.Code, StringComparer.OrdinalIgnoreCase))
                option.DisplayOrder = int.MaxValue;
            else
                queries.RemoveCodedOption(option);
        }

        foreach (var option in wanted)
        {
            if (parameter.CodedOptions.Any(
                    o => string.Equals(o.Code, option.Code, StringComparison.OrdinalIgnoreCase)))
                continue;

            parameter.AddCodedOption(new CodedResultOption(option.Code, option.Label)
            {
                DisplayOrder = option.DisplayOrder,
                IsAbnormal = option.IsAbnormal,
                IsCritical = option.IsCritical,
                IsExpectedNormal = option.IsExpectedNormal
            });
        }
    }

    // ---- Reference intervals -------------------------------------------------------------------

    /// <summary>
    /// Creates a reference interval, or supersedes one.
    /// <para>
    /// An interval already in force is not edited in place. A stored result was interpreted — normal,
    /// low, high — against the interval as it stood; rewriting its limits silently reclassifies
    /// every one of those results, including any that were phoned to a prescriber as critical. So a
    /// change to the limits closes the old interval the day before the new one starts and opens a
    /// successor, which is also what ISO 15189 §5.5.2 means by reviewing an interval.
    /// </para>
    /// <para>
    /// The review stamp and the source are editable in place: recording that an interval was
    /// reviewed and left unchanged is the whole point of the review, and it changes no result.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> SaveRangeAsync(ReferenceRangeRequest request,
                                                    CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.ManageReferenceRanges);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var analysis = await queries.GetAnalysisForParameterAsync(request.TestParameterId, ct);
        if (analysis is null) return Result.Failure<Guid>(Error.NotFound("Parameter_NotFound"));

        var loaded = await analyses.GetByIdWithDetailsAsync(analysis.Value, ct);
        var parameter = loaded?.Parameters.FirstOrDefault(p => p.Id == request.TestParameterId);

        if (loaded is null || parameter is null)
            return Result.Failure<Guid>(Error.NotFound("Parameter_NotFound"));

        var candidate = new ReferenceRange(request.AppliesToGender)
        {
            AgeFromDays = request.AgeFromDays,
            AgeToDays = request.AgeToDays,
            AgeUnitDays = request.AgeFromDays is null && request.AgeToDays is null
                ? null : request.AgeUnitDays,
            AppliesToPregnancy = request.AppliesToPregnancy,
            PregnancyTrimester = request.PregnancyTrimester,
            InstrumentId = request.InstrumentId,
            LowerLimit = request.LowerLimit,
            UpperLimit = request.UpperLimit,
            DisplayText = request.DisplayText,
            ExpectedCodedValue = Trimmed(request.ExpectedCodedValue),
            IsDecisionThreshold = request.IsDecisionThreshold,
            Source = Trimmed(request.Source),
            LastReviewedOn = request.LastReviewedOn,
            ValidFrom = request.ValidFrom == default ? clock.Today : request.ValidFrom,
            ValidUntil = request.ValidUntil,
            IsActive = request.IsActive
        };

        var valid = candidate.Validate();
        if (valid.IsFailure) return Result.Failure<Guid>(valid.Errors);

        // A numeric interval outside the method's own measuring range describes a value the
        // analyser cannot report, so nothing can ever land inside it.
        if (parameter.MeasuringRangeLow is { } measLow && candidate.UpperLimit is { } upper
            && upper < measLow)
            return Result.Failure<Guid>(Error.Validation("Range_BelowMeasuringRange"));

        if (parameter.MeasuringRangeHigh is { } measHigh && candidate.LowerLimit is { } lower
            && lower > measHigh)
            return Result.Failure<Guid>(Error.Validation("Range_AboveMeasuringRange"));

        if (request.ValidUntil is { } until && until < candidate.ValidFrom)
            return Result.Failure<Guid>(Error.Validation("Range_ValidUntilBeforeValidFrom"));

        if (request.Id is null)
        {
            parameter.AddReferenceRange(candidate);

            unitOfWork.AuditReason =
                $"Nouvel intervalle de référence pour {loaded.Code}/{parameter.Code}";

            await unitOfWork.SaveChangesAsync(ct);

            return Result.Success(candidate.Id);
        }

        var existing = parameter.ReferenceRanges.FirstOrDefault(r => r.Id == request.Id.Value);
        if (existing is null) return Result.Failure<Guid>(Error.NotFound("Range_NotFound"));

        var limitsChanged = existing.LowerLimit != candidate.LowerLimit
                            || existing.UpperLimit != candidate.UpperLimit
                            || existing.ExpectedCodedValue != candidate.ExpectedCodedValue
                            || existing.AppliesToGender != candidate.AppliesToGender
                            || existing.AgeFromDays != candidate.AgeFromDays
                            || existing.AgeToDays != candidate.AgeToDays
                            || existing.EffectiveAgeUnitDays != candidate.EffectiveAgeUnitDays
                            || existing.AppliesToPregnancy != candidate.AppliesToPregnancy
                            || existing.PregnancyTrimester != candidate.PregnancyTrimester
                            || existing.InstrumentId != candidate.InstrumentId;

        var interpreted = limitsChanged
            ? await queries.CountResultsInterpretedAgainstAsync(parameter.Id, existing.ValidFrom, ct)
            : 0;

        if (limitsChanged && interpreted > 0)
        {
            // Supersede rather than rewrite. The successor starts today at the earliest: an
            // interval cannot come into force before the day somebody decided on it.
            var from = candidate.ValidFrom < clock.Today ? clock.Today : candidate.ValidFrom;

            if (existing.ValidFrom >= from)
                return Result.Failure<Guid>(
                    Error.Validation("Range_SuccessorMustStartAfterPredecessor",
                                     existing.ValidFrom));

            existing.ValidUntil = from.AddDays(-1);
            candidate.ValidFrom = from;

            parameter.AddReferenceRange(candidate);

            unitOfWork.AuditReason =
                $"Révision de l'intervalle de référence {loaded.Code}/{parameter.Code} "
                + PluralText.French("à compter du {0:dd/MM/yyyy} ({1} {1:plural:résultat déjà interprété|résultats déjà interprétés})", from, interpreted);

            await unitOfWork.SaveChangesAsync(ct);

            return Result.Success(candidate.Id);
        }

        existing.AppliesToGender = candidate.AppliesToGender;
        existing.AgeFromDays = candidate.AgeFromDays;
        existing.AgeToDays = candidate.AgeToDays;
        existing.AgeUnitDays = candidate.AgeUnitDays;
        existing.AppliesToPregnancy = candidate.AppliesToPregnancy;
        existing.PregnancyTrimester = candidate.PregnancyTrimester;
        existing.InstrumentId = candidate.InstrumentId;
        existing.LowerLimit = candidate.LowerLimit;
        existing.UpperLimit = candidate.UpperLimit;
        existing.DisplayText = candidate.DisplayText;
        existing.ExpectedCodedValue = candidate.ExpectedCodedValue;
        existing.IsDecisionThreshold = candidate.IsDecisionThreshold;
        existing.Source = candidate.Source;
        existing.LastReviewedOn = candidate.LastReviewedOn;
        existing.ValidFrom = candidate.ValidFrom;
        existing.ValidUntil = candidate.ValidUntil;
        existing.IsActive = candidate.IsActive;

        unitOfWork.AuditReason =
            $"Modification de l'intervalle de référence {loaded.Code}/{parameter.Code}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(existing.Id);
    }

    /// <summary>
    /// Records a periodic review that left the interval unchanged — an ISO 15189 §5.5.2 obligation
    /// in its own right, and the commonest outcome of a review.
    /// </summary>
    public async Task<Result> RecordRangeReviewAsync(Guid parameterId, Guid rangeId, string? source,
                                                      CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.ManageReferenceRanges);
        if (permitted.IsFailure) return permitted;

        var analysisId = await queries.GetAnalysisForParameterAsync(parameterId, ct);
        if (analysisId is null) return Result.Failure(Error.NotFound("Parameter_NotFound"));

        var loaded = await analyses.GetByIdWithDetailsAsync(analysisId.Value, ct);
        var parameter = loaded?.Parameters.FirstOrDefault(p => p.Id == parameterId);
        var range = parameter?.ReferenceRanges.FirstOrDefault(r => r.Id == rangeId);

        if (loaded is null || parameter is null || range is null)
            return Result.Failure(Error.NotFound("Range_NotFound"));

        range.LastReviewedOn = clock.Today;
        if (!string.IsNullOrWhiteSpace(source)) range.Source = source.Trim();

        unitOfWork.AuditReason =
            $"Revue périodique de l'intervalle de référence {loaded.Code}/{parameter.Code}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Tariffs -------------------------------------------------------------------------------

    /// <summary>
    /// Enters a tariff revision.
    /// <para>
    /// There is no edit. A price is versioned because an invoice issued last year must reprint with
    /// the price that was charged — the CGI requires an issued invoice to be immutable, and a
    /// catalogue that overwrote its prices would make every reprint a different document.
    /// <see cref="AnalysisTest.AddTariff"/> closes the open-ended tariff the day before the new one
    /// starts, so the price history stays contiguous with no gap and no overlap.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> AddTariffAsync(TariffRequest request,
                                                    CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var analysis = await analyses.GetByIdWithDetailsAsync(request.AnalysisTestId, ct);
        if (analysis is null) return Result.Failure<Guid>(Error.NotFound("Test_NotFound"));

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.LetterKey))
            errors.Add(Error.Validation("Tariff_LetterKeyRequired"));
        if (request.Price < 0m) errors.Add(Error.Validation("Tariff_NegativePrice"));
        if (request.Coefficient < 0m) errors.Add(Error.Validation("Tariff_NegativeCoefficient"));
        if (request.LetterKeyValue < 0m) errors.Add(Error.Validation("Tariff_NegativeLetterKeyValue"));
        if (!request.IsVatExempt && request.VatRate <= 0m)
            errors.Add(Error.Validation("Tariff_VatRateRequired"));

        // An act carrying a national code but no reimbursement base pays the patient's share out of
        // a base of zero, so the payer share computes to nothing and the patient is charged in full
        // on a reimbursable act. The claim is then rejected and nobody knows why.
        if (!string.IsNullOrWhiteSpace(analysis.NationalActCode)
            && (request.Coefficient <= 0m || request.LetterKeyValue <= 0m))
            errors.Add(Error.Regulatory("Tariff_CodedActNeedsBase", analysis.NationalActCode));

        var existing = analysis.Tariffs.Where(t => t.PayerId == request.PayerId).ToList();

        var current = existing.FirstOrDefault(t => t.ValidUntil is null);

        // Backdating behind the tariff in force would leave two prices claiming the same day, and
        // the one that wins is whichever the query happens to order first.
        if (current is not null && request.ValidFrom <= current.ValidFrom)
            errors.Add(Error.Validation("Tariff_NotAfterCurrent", current.ValidFrom));

        if (existing.Any(t => t.ValidFrom == request.ValidFrom))
            errors.Add(Error.Conflict("Tariff_AlreadyStartsOnDate", request.ValidFrom));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var tariff = new AnalysisTestTariff(analysis.Id, request.PayerId, request.ValidFrom)
        {
            LetterKey = request.LetterKey.Trim().ToUpperInvariant(),
            Coefficient = request.Coefficient,
            LetterKeyValue = request.LetterKeyValue,
            Price = request.Price,
            IsVatExempt = request.IsVatExempt,
            VatRate = request.IsVatExempt ? 0m : request.VatRate
        };

        analysis.AddTariff(tariff);

        unitOfWork.AuditReason =
            $"Tarif de {analysis.Code} à compter du {request.ValidFrom:dd/MM/yyyy} : "
            + $"{request.Price:0.00} MAD";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(tariff.Id);
    }

    /// <summary>
    /// Corrects a tariff in place: a slip in the price, the coefficient or the letter-key value.
    /// <para>
    /// The payer and the dates stay as they are; a new price from a later date is still a new
    /// tariff, so the history of what was charged when stays readable. Correcting is safe for
    /// what was already billed: each order item and each invoice line keeps the price it was
    /// charged at, and the audit trail keeps the values the correction replaced.
    /// </para>
    /// </summary>
    public async Task<Result> UpdateTariffAsync(Guid tariffId, TariffRequest request,
                                                CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Billing.ManageTariffs);
        if (permitted.IsFailure) return permitted;

        var analysis = await analyses.GetByIdWithDetailsAsync(request.AnalysisTestId, ct);
        if (analysis is null) return Result.Failure(Error.NotFound("Test_NotFound"));

        var tariff = analysis.Tariffs.FirstOrDefault(t => t.Id == tariffId);
        if (tariff is null) return Result.Failure(Error.NotFound("Tariff_NotFound"));

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.LetterKey))
            errors.Add(Error.Validation("Tariff_LetterKeyRequired"));
        if (request.Price < 0m) errors.Add(Error.Validation("Tariff_NegativePrice"));
        if (request.Coefficient < 0m) errors.Add(Error.Validation("Tariff_NegativeCoefficient"));
        if (request.LetterKeyValue < 0m) errors.Add(Error.Validation("Tariff_NegativeLetterKeyValue"));
        if (!request.IsVatExempt && request.VatRate <= 0m)
            errors.Add(Error.Validation("Tariff_VatRateRequired"));

        if (!string.IsNullOrWhiteSpace(analysis.NationalActCode)
            && (request.Coefficient <= 0m || request.LetterKeyValue <= 0m))
            errors.Add(Error.Regulatory("Tariff_CodedActNeedsBase", analysis.NationalActCode));

        if (errors.Count > 0) return Result.Failure(errors);

        var before = $"{tariff.Price:0.00} MAD ({tariff.LetterKey} {tariff.Coefficient:0.##} × {tariff.LetterKeyValue:0.####})";

        tariff.LetterKey = request.LetterKey.Trim().ToUpperInvariant();
        tariff.Coefficient = request.Coefficient;
        tariff.LetterKeyValue = request.LetterKeyValue;
        tariff.Price = request.Price;
        tariff.IsVatExempt = request.IsVatExempt;
        tariff.VatRate = request.IsVatExempt ? 0m : request.VatRate;

        unitOfWork.AuditReason =
            $"Tarif de {analysis.Code} du {tariff.ValidFrom:dd/MM/yyyy} corrigé : {before} → "
            + $"{tariff.Price:0.00} MAD ({tariff.LetterKey} {tariff.Coefficient:0.##} × {tariff.LetterKeyValue:0.####})";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // ---- Panels --------------------------------------------------------------------------------

    public async Task<Result<Guid>> SavePanelAsync(PanelRequest request,
                                                    CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code)) errors.Add(Error.Validation("Panel_CodeRequired"));
        if (string.IsNullOrWhiteSpace(request.Name.Fr))
            errors.Add(Error.Validation("Panel_NameFrenchRequired"));
        if (request.Members.Count == 0)
            errors.Add(Error.Validation("Panel_AtLeastOneAnalysisRequired"));
        if (request.PackagePrice is < 0m) errors.Add(Error.Validation("Panel_NegativePrice"));

        // Every mandatory member optional is a panel that adds nothing when it is selected: the
        // operator picks "bilan lipidique" and gets an empty order.
        if (request.Members.Count > 0 && request.Members.All(m => m.IsOptional))
            errors.Add(Error.Validation("Panel_AllMembersOptional"));

        if (request.Members.Select(m => m.AnalysisTestId).Distinct().Count() != request.Members.Count)
            errors.Add(Error.Validation("Panel_DuplicateMember"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        // A panel offering an inactive analysis fails at registration, on the analysis the operator
        // did not choose individually and cannot see is the problem.
        var inactive = await queries.GetInactiveAnalysisCodesAsync(
            [.. request.Members.Where(m => !m.IsOptional).Select(m => m.AnalysisTestId)], ct);

        if (inactive.Count > 0)
            return Result.Failure<Guid>(
                Error.Conflict("Panel_MemberInactive", string.Join(", ", inactive)));

        var code = request.Code.Trim().ToUpperInvariant();

        TestPanel panel;

        if (request.Id is null)
        {
            if (await queries.PanelCodeExistsAsync(code, null, ct))
                return Result.Failure<Guid>(Error.Conflict("Panel_CodeAlreadyUsed", code));

            panel = new TestPanel(code, request.Name);
            await panels.AddAsync(panel, ct);
        }
        else
        {
            var found = await panels.GetByIdWithDetailsAsync(request.Id.Value, ct);

            if (found is null) return Result.Failure<Guid>(Error.NotFound("Panel_NotFound"));

            panel = found;

            if (!string.Equals(panel.Code, code, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Guid>(Error.Regulatory("Panel_CodeImmutable", panel.Code));
        }

        panel.Name = request.Name;
        panel.SearchAliases = Trimmed(request.SearchAliases);
        panel.PackagePrice = request.PackagePrice;
        panel.ItemizeOnInvoice = request.ItemizeOnInvoice;
        panel.GroupOnReport = request.GroupOnReport;
        panel.DisplayOrder = request.DisplayOrder;
        panel.IsActive = request.IsActive;

        foreach (var existing in panel.Items.ToList())
        {
            if (request.Members.All(m => m.AnalysisTestId != existing.AnalysisTestId))
                panel.RemoveAnalysis(existing.AnalysisTestId);
        }

        foreach (var member in request.Members)
        {
            var existing = panel.Items.FirstOrDefault(i => i.AnalysisTestId == member.AnalysisTestId);

            if (existing is null) panel.AddAnalysis(member.AnalysisTestId, member.IsOptional);
            else existing.IsOptional = member.IsOptional;
        }

        var validated = panel.Validate();
        if (validated.IsFailure) return Result.Failure<Guid>(validated.Errors);

        unitOfWork.AuditReason =
            $"{(request.Id is null ? "Création" : "Modification")} du profil {panel.Code}";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(panel.Id);
    }

    // ---- Reflex rules --------------------------------------------------------------------------

    /// <summary>
    /// Saves a reflex rule (analyse en cascade).
    /// <para>
    /// The validation here is the point of the method. A rule whose condition can never be met —
    /// a "greater than" with no threshold, a coded match with no value — appears configured on the
    /// screen and does nothing at the bench. Somebody then relies on a confirmation that is never
    /// ordered, which is the failure mode a reflex rule exists to prevent.
    /// </para>
    /// </summary>
    public async Task<Result<Guid>> SaveReflexRuleAsync(ReflexRuleRequest request,
                                                         CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Description.Fr))
            errors.Add(Error.Validation("Reflex_DescriptionRequired"));

        switch (request.Condition)
        {
            case ReflexCondition.GreaterThan or ReflexCondition.LessThan:
                if (request.Threshold is null)
                    errors.Add(Error.Validation("Reflex_ThresholdRequired"));
                break;

            case ReflexCondition.Between or ReflexCondition.Outside:
                if (request.Threshold is null || request.ThresholdUpper is null)
                    errors.Add(Error.Validation("Reflex_TwoThresholdsRequired"));
                else if (request.Threshold >= request.ThresholdUpper)
                    errors.Add(Error.Validation("Reflex_ThresholdsInverted"));
                break;

            case ReflexCondition.EqualsCoded:
                if (string.IsNullOrWhiteSpace(request.TriggerCodedValue))
                    errors.Add(Error.Validation("Reflex_CodedValueRequired"));
                break;
        }

        var numericCondition = request.Condition != ReflexCondition.EqualsCoded;

        var trigger = await queries.GetParameterSummaryAsync(request.TriggerParameterId, ct);

        if (trigger is null) errors.Add(Error.NotFound("Parameter_NotFound"));
        else
        {
            var isNumeric = trigger.ValueType is ResultValueType.Numeric
                                              or ResultValueType.NumericWithOperator;

            // A numeric condition on a coded parameter never fires: the evaluator is handed a coded
            // answer and a threshold, and has nothing to compare.
            if (numericCondition && !isNumeric)
                errors.Add(Error.Validation("Reflex_NumericConditionNeedsNumericParameter",
                                            trigger.Code));

            if (!numericCondition && isNumeric)
                errors.Add(Error.Validation("Reflex_CodedConditionNeedsCodedParameter",
                                            trigger.Code));

            // The coded value has to be one the picker actually offers, or the comparison is
            // against a string nobody can enter.
            if (!numericCondition && request.TriggerCodedValue is { Length: > 0 } coded)
            {
                var options = await queries.GetCodedOptionCodesAsync(request.TriggerParameterId, ct);

                if (options.Count > 0
                    && !options.Contains(coded.Trim(), StringComparer.OrdinalIgnoreCase))
                    errors.Add(Error.Validation("Reflex_CodedValueNotAnOption", coded));
            }
        }

        var target = await queries.GetAnalysisSummaryAsync(request.AnalysisToAddId, ct);

        if (target is null) errors.Add(Error.NotFound("Test_NotFound"));
        else if (!target.IsActive)
            errors.Add(Error.Validation("Reflex_TargetInactive", target.Code));

        // A rule that adds the analysis its own trigger belongs to loops: the addition is refused
        // as already present, every time, and the rule is noise in the audit trail.
        if (trigger is not null && trigger.AnalysisTestId == request.AnalysisToAddId)
            errors.Add(Error.Validation("Reflex_TargetIsTriggerAnalysis"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        ReflexRule rule;

        if (request.Id is null)
        {
            rule = new ReflexRule(request.TriggerParameterId, request.AnalysisToAddId,
                                   request.Description);

            await reflexRules.AddAsync(rule, ct);
        }
        else
        {
            var found = await reflexRules.GetByIdAsync(request.Id.Value, ct);

            if (found is null) return Result.Failure<Guid>(Error.NotFound("Reflex_NotFound"));

            rule = found;
            rule.Description = request.Description;
        }

        rule.Condition = request.Condition;
        rule.Threshold = request.Threshold;
        rule.ThresholdUpper = request.ThresholdUpper;
        rule.TriggerCodedValue = Trimmed(request.TriggerCodedValue);
        rule.AddAutomatically = request.AddAutomatically;
        rule.IsBillable = request.IsBillable;
        rule.IsActive = request.IsActive;

        unitOfWork.AuditReason =
            $"{(request.Id is null ? "Création" : "Modification")} de la règle en cascade "
            + $"« {request.Description.Fr} »";

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(rule.Id);
    }

    // ---- Nomenclature --------------------------------------------------------------------------

    /// <summary>
    /// Saves one of the lists the catalogue is built on: disciplines, specimen types, containers,
    /// units. Codes are immutable here too, and a list entry in use cannot be deactivated.
    /// </summary>
    public async Task<Result<Guid>> SaveNomenclatureAsync(NomenclatureKind kind,
                                                           NomenclatureRequest request,
                                                           CancellationToken ct = default)
    {
        var permitted = guard.Require(Permissions.Catalog.Manage);
        if (permitted.IsFailure) return Result.Failure<Guid>(permitted.FirstError);

        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.Code)) errors.Add(Error.Validation("Nomenclature_CodeRequired"));

        if (kind == NomenclatureKind.MeasurementUnit)
        {
            if (string.IsNullOrWhiteSpace(request.DisplaySymbol))
                errors.Add(Error.Validation("Unit_SymbolRequired"));

            // A conversion factor with no target unit converts to nothing; a target with no factor
            // is a conversion the report cannot perform. Either both or neither.
            if (request.FactorToSi is not null && string.IsNullOrWhiteSpace(request.SiUnitCode))
                errors.Add(Error.Validation("Unit_FactorNeedsSiUnit"));
            if (request.FactorToSi is null && !string.IsNullOrWhiteSpace(request.SiUnitCode))
                errors.Add(Error.Validation("Unit_SiUnitNeedsFactor"));
            if (request.FactorToSi is <= 0m) errors.Add(Error.Validation("Unit_FactorMustBePositive"));
        }
        else if (string.IsNullOrWhiteSpace(request.Name.Fr))
        {
            errors.Add(Error.Validation("Nomenclature_NameFrenchRequired"));
        }

        if (kind == NomenclatureKind.ContainerType)
        {
            if (!IsHexColour(request.CapColourHex))
                errors.Add(Error.Validation("Container_InvalidCapColour"));

            if (request.MinimumVolumeMl is { } minimum && request.NominalVolumeMl is { } nominal
                && minimum > nominal)
                errors.Add(Error.Validation("Container_MinimumAboveNominal"));

            // Without a draw order the collection worklist cannot sequence a multi-tube draw, and
            // additive carry-over between tubes is a real pre-analytical error source.
            if (request.DrawOrder <= 0) errors.Add(Error.Validation("Container_DrawOrderRequired"));
        }

        if (kind == NomenclatureKind.SpecimenType && request.FastingHours < 0)
            errors.Add(Error.Validation("Specimen_NegativeFasting"));

        if (errors.Count > 0) return Result.Failure<Guid>(errors);

        var code = request.Code.Trim().ToUpperInvariant();

        if (request.Id is null && await queries.NomenclatureCodeExistsAsync(kind, code, null, ct))
            return Result.Failure<Guid>(Error.Conflict("Nomenclature_CodeAlreadyUsed", code));

        // Deactivating an entry still referenced leaves an analysis pointing at a specimen type the
        // collection screen will not offer, which is a draw nobody can record.
        if (request.Id is { } id && !request.IsActive)
        {
            var usage = await queries.CountNomenclatureUsageAsync(kind, id, ct);

            if (usage > 0)
                return Result.Failure<Guid>(Error.Conflict("Nomenclature_InUseCannotDeactivate", usage));
        }

        var savedId = kind switch
        {
            NomenclatureKind.Department => await SaveDepartmentAsync(code, request, ct),
            NomenclatureKind.SpecimenType => await SaveSpecimenTypeAsync(code, request, ct),
            NomenclatureKind.ContainerType => await SaveContainerTypeAsync(code, request, ct),
            NomenclatureKind.MeasurementUnit => await SaveUnitAsync(code, request, ct),
            _ => Result.Failure<Guid>(Error.Validation("Nomenclature_UnknownKind"))
        };

        if (savedId.IsFailure) return savedId;

        unitOfWork.AuditReason =
            $"{(request.Id is null ? "Création" : "Modification")} de {kind} {code}";

        await unitOfWork.SaveChangesAsync(ct);

        return savedId;
    }

    private async Task<Result<Guid>> SaveDepartmentAsync(string code, NomenclatureRequest request,
                                                          CancellationToken ct)
    {
        Department entity;

        if (request.Id is null)
        {
            entity = new Department(code, request.Name);
            await departments.AddAsync(entity, ct);
        }
        else
        {
            var found = await departments.GetByIdAsync(request.Id.Value, ct);
            if (found is null) return Result.Failure<Guid>(Error.NotFound("Nomenclature_NotFound"));
            if (!string.Equals(found.Code, code, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Guid>(Error.Regulatory("Nomenclature_CodeImmutable", found.Code));
            entity = found;
        }

        entity.Name = request.Name;
        entity.DisplayOrder = request.DisplayOrder;
        entity.IsInAccreditationScope = request.IsInAccreditationScope;
        entity.SupervisorUserId = request.SupervisorUserId;
        entity.IsActive = request.IsActive;

        return Result.Success(entity.Id);
    }

    private async Task<Result<Guid>> SaveSpecimenTypeAsync(string code, NomenclatureRequest request,
                                                            CancellationToken ct)
    {
        SpecimenType entity;

        if (request.Id is null)
        {
            entity = new SpecimenType(code, request.Name);
            await specimenTypes.AddAsync(entity, ct);
        }
        else
        {
            var found = await specimenTypes.GetByIdAsync(request.Id.Value, ct);
            if (found is null) return Result.Failure<Guid>(Error.NotFound("Nomenclature_NotFound"));
            if (!string.Equals(found.Code, code, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Guid>(Error.Regulatory("Nomenclature_CodeImmutable", found.Code));
            entity = found;
        }

        entity.Name = request.Name;
        entity.CollectionInstructions = request.CollectionInstructions;
        entity.PatientInstructions = request.PatientInstructions;
        entity.FastingHours = request.FastingHours;
        entity.IsActive = request.IsActive;

        return Result.Success(entity.Id);
    }

    private async Task<Result<Guid>> SaveContainerTypeAsync(string code, NomenclatureRequest request,
                                                             CancellationToken ct)
    {
        ContainerType entity;

        if (request.Id is null)
        {
            entity = new ContainerType(code, request.Name, request.CapColourHex);
            await containerTypes.AddAsync(entity, ct);
        }
        else
        {
            var found = await containerTypes.GetByIdAsync(request.Id.Value, ct);
            if (found is null) return Result.Failure<Guid>(Error.NotFound("Nomenclature_NotFound"));
            if (!string.Equals(found.Code, code, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Guid>(Error.Regulatory("Nomenclature_CodeImmutable", found.Code));
            entity = found;
        }

        entity.Name = request.Name;
        entity.CapColourHex = request.CapColourHex.Trim().ToUpperInvariant();
        entity.Additive = request.Additive;
        entity.NominalVolumeMl = request.NominalVolumeMl;
        entity.MinimumVolumeMl = request.MinimumVolumeMl;
        entity.DrawOrder = request.DrawOrder;
        entity.RequiresProtectionFromLight = request.RequiresProtectionFromLight;
        entity.RequiresIceTransport = request.RequiresIceTransport;
        entity.IsActive = request.IsActive;

        return Result.Success(entity.Id);
    }

    private async Task<Result<Guid>> SaveUnitAsync(string code, NomenclatureRequest request,
                                                    CancellationToken ct)
    {
        MeasurementUnit entity;

        if (request.Id is null)
        {
            entity = new MeasurementUnit(code, request.DisplaySymbol.Trim());
            await units.AddAsync(entity, ct);
        }
        else
        {
            var found = await units.GetByIdAsync(request.Id.Value, ct);
            if (found is null) return Result.Failure<Guid>(Error.NotFound("Nomenclature_NotFound"));
            if (!string.Equals(found.Code, code, StringComparison.Ordinal))
                return Result.Failure<Guid>(Error.Regulatory("Nomenclature_CodeImmutable", found.Code));
            entity = found;
        }

        entity.DisplaySymbol = request.DisplaySymbol.Trim();
        entity.DisplaySymbolArabic = Trimmed(request.DisplaySymbolArabic);
        entity.UcumCode = Trimmed(request.UcumCode);
        entity.FactorToSi = request.FactorToSi;
        entity.SiUnitCode = Trimmed(request.SiUnitCode);
        entity.IsActive = request.IsActive;

        return Result.Success(entity.Id);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsHexColour(string? value) =>
        value is { Length: 7 } colour && colour[0] == '#'
        && colour[1..].All(Uri.IsHexDigit);
}

/// <summary>A parameter, reduced to what a cross-check needs.</summary>
public sealed record ParameterSummary(Guid Id, Guid AnalysisTestId, string Code,
                                      ResultValueType ValueType);

/// <summary>An analysis, reduced to what a cross-check needs.</summary>
public sealed record AnalysisSummary(Guid Id, string Code, bool IsActive);

/// <summary>A laboratory a subcontracted analysis can be sent to.</summary>
public sealed record PartnerSiteOption(Guid Id, string Code, MultilingualText Name);

public sealed record TariffPayerOption(Guid Id, string Code, MultilingualText Name);

/// <summary>An analyser a method-dependent reference interval can be restricted to.</summary>
public sealed record InstrumentOption(Guid Id, string Code, MultilingualText Name);

public interface ICatalogMaintenanceQueries
{
    Task<IReadOnlyList<AnalysisRow>> GetAnalysesAsync(string? search, Guid? departmentId,
                                                       bool includeInactive, DateOnly today,
                                                       CancellationToken ct = default);

    Task<IReadOnlyList<ParameterRow>> GetParametersAsync(Guid analysisTestId,
                                                          CancellationToken ct = default);

    Task<IReadOnlyList<ReferenceRangeRow>> GetRangesAsync(Guid parameterId,
                                                           CancellationToken ct = default);

    Task<IReadOnlyList<TariffRow>> GetTariffsAsync(Guid analysisTestId,
                                                    CancellationToken ct = default);

    Task<IReadOnlyList<PanelRow>> GetPanelsAsync(bool includeInactive, DateOnly today,
                                                  CancellationToken ct = default);

    Task<IReadOnlyList<ReflexRuleRow>> GetReflexRulesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<NomenclatureRow>> GetNomenclatureAsync(NomenclatureKind kind,
                                                               CancellationToken ct = default);

    /// <summary>Active referral partners — the only sites a subcontracted analysis may name.</summary>
    Task<IReadOnlyList<PartnerSiteOption>> GetPartnerSitesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TariffPayerOption>> GetPayerOptionsAsync(CancellationToken ct = default);

    Task<decimal?> GetLatestLetterKeyValueAsync(string letterKey, CancellationToken ct = default);

    /// <summary>Analysers in service, for a method-dependent reference interval.</summary>
    Task<IReadOnlyList<InstrumentOption>> GetInstrumentOptionsAsync(CancellationToken ct = default);

    Task<bool> AnalysisCodeExistsAsync(string code, Guid? exceptId, CancellationToken ct = default);
    Task<bool> PanelCodeExistsAsync(string code, Guid? exceptId, CancellationToken ct = default);
    Task<bool> NomenclatureCodeExistsAsync(NomenclatureKind kind, string code, Guid? exceptId,
                                            CancellationToken ct = default);

    /// <summary>Order items for this analysis that are neither validated nor cancelled.</summary>
    Task<int> CountOpenItemsAsync(Guid analysisTestId, CancellationToken ct = default);

    /// <summary>Stored results for a parameter, whatever their state. Freezes the value type.</summary>
    Task<int> CountResultsAsync(Guid parameterId, CancellationToken ct = default);

    /// <summary>
    /// Results produced while the given interval was in force — the ones a change to its limits
    /// would silently reclassify.
    /// </summary>
    Task<int> CountResultsInterpretedAgainstAsync(Guid parameterId, DateOnly since,
                                                   CancellationToken ct = default);

    /// <summary>Coded values a stored result actually carries, so a used option is never deleted.</summary>
    Task<IReadOnlyList<string>> GetUsedCodedValuesAsync(Guid parameterId,
                                                         CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetCodedOptionCodesAsync(Guid parameterId,
                                                          CancellationToken ct = default);

    Task<Guid?> GetAnalysisForParameterAsync(Guid parameterId, CancellationToken ct = default);

    Task<ParameterSummary?> GetParameterSummaryAsync(Guid parameterId, CancellationToken ct = default);
    Task<AnalysisSummary?> GetAnalysisSummaryAsync(Guid analysisTestId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetInactiveAnalysisCodesAsync(IReadOnlyList<Guid> analysisTestIds,
                                                               CancellationToken ct = default);

    /// <summary>Entries still pointed at by an analysis or a parameter.</summary>
    Task<int> CountNomenclatureUsageAsync(NomenclatureKind kind, Guid id,
                                           CancellationToken ct = default);

    /// <summary>
    /// Detaches a coded option the operator dropped and no result ever used. On the read side
    /// because the option is not an aggregate root and the repository abstraction has no reach into
    /// a child collection.
    /// </summary>
    void RemoveCodedOption(CodedResultOption option);
}
