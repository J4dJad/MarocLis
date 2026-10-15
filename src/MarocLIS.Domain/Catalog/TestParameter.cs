using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Catalog;

/// <summary>
/// A single measurand that carries a result (un paramètre). One analysis may have one parameter
/// (glycaemia) or many (an NFS, a urine sediment, an antibiogram panel).
/// </summary>
public sealed class TestParameter : Entity, IAuditable
{
    private readonly List<ReferenceRange> _referenceRanges = [];
    private readonly List<CodedResultOption> _codedOptions = [];

    private TestParameter() { }

    public TestParameter(string code, MultilingualText name, ResultValueType valueType)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
        ValueType = valueType;
    }

    public Guid AnalysisTestId { get; private set; }
    public AnalysisTest? AnalysisTest { get; private set; }

    internal void AttachTo(Guid analysisTestId) => AnalysisTestId = analysisTestId;

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();
    public MultilingualText? ShortName { get; set; }

    public ResultValueType ValueType { get; set; }

    /// <summary>LOINC code of the measurand itself, which is finer-grained than the order code.</summary>
    public string? LoincCode { get; set; }

    // ---- Numeric presentation ----------------------------------------------------------------

    public Guid? UnitId { get; set; }
    public MeasurementUnit? Unit { get; private set; }

    /// <summary>Decimal places used for display and for rounding the stored value.</summary>
    public int Decimals { get; set; } = 2;

    /// <summary>
    /// Analytical measuring range of the method. A value outside it must be reported as
    /// "&lt; low" / "&gt; high" or re-run on a dilution, never as a bare number.
    /// </summary>
    public decimal? MeasuringRangeLow { get; set; }
    public decimal? MeasuringRangeHigh { get; set; }

    /// <summary>
    /// Values a human can never plausibly produce — a typing slip, not a patient state.
    /// Entry is refused outright.
    /// </summary>
    public decimal? TechnicalLimitLow { get; set; }
    public decimal? TechnicalLimitHigh { get; set; }

    /// <summary>
    /// Panic values. Crossing one raises a critical-result alert that must be phoned to the
    /// prescriber and the call logged — an explicit ISO 15189 §5.9 requirement.
    /// </summary>
    public decimal? CriticalLow { get; set; }
    public decimal? CriticalHigh { get; set; }

    /// <summary>Coefficient of variation of the method (%), used for the measurement-uncertainty note.</summary>
    public decimal? AnalyticalCvPercent { get; set; }

    // ---- Delta check -------------------------------------------------------------------------

    /// <summary>
    /// Relative change against the patient's previous result, in percent, above which the value is
    /// flagged for review. Catches sample mix-ups better than any range check.
    /// </summary>
    public decimal? DeltaCheckPercent { get; set; }

    /// <summary>Window in days over which the previous result is still comparable.</summary>
    public int? DeltaCheckWindowDays { get; set; }

    // ---- Calculated parameters ---------------------------------------------------------------

    /// <summary>
    /// Expression evaluated by the calculation engine, referencing other parameter codes in square
    /// brackets, e.g. <c>[CHOL] - [HDL] - [TG]/5</c> for LDL by Friedewald. Null for measured
    /// parameters.
    /// </summary>
    public string? CalculationFormula { get; set; }

    public bool IsCalculated => !string.IsNullOrWhiteSpace(CalculationFormula);

    /// <summary>Hidden from the report but still stored — intermediate values, raw instrument reads.</summary>
    public bool IsInternalOnly { get; set; }

    /// <summary>Printed even when no result was produced, showing "non fait" — keeps panels readable.</summary>
    public bool AlwaysShowOnReport { get; set; } = true;

    public int DisplayOrder { get; set; }

    /// <summary>Indentation level on the report, so a differential sits under its parent count.</summary>
    public int IndentLevel { get; set; }

    public bool IsActive { get; set; } = true;

    public IReadOnlyCollection<ReferenceRange> ReferenceRanges => _referenceRanges.AsReadOnly();
    public IReadOnlyCollection<CodedResultOption> CodedOptions => _codedOptions.AsReadOnly();

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    // ---- Behaviour ---------------------------------------------------------------------------

    public void AddReferenceRange(ReferenceRange range)
    {
        range.AttachTo(Id);
        _referenceRanges.Add(range);
    }

    public void AddCodedOption(CodedResultOption option)
    {
        option.AttachTo(Id);
        if (option.DisplayOrder == 0)
            option.DisplayOrder = _codedOptions.Count == 0 ? 1 : _codedOptions.Max(o => o.DisplayOrder) + 1;
        _codedOptions.Add(option);
    }

    /// <summary>
    /// Picks the reference range that fits this patient. Ranges are scored by specificity so that a
    /// pregnancy-specific range beats a sex-specific one, which in turn beats an age-only range;
    /// the most specific match wins rather than the first one declared.
    /// <para>
    /// Only an interval in force on the context's reference date is considered. A revision closes
    /// the predecessor with an end date but leaves it active — results already interpreted against
    /// it still point at it — and the successor carries the same stratum, so without the date both
    /// matched with equal specificity and the superseded interval, declared first, kept winning.
    /// Between equally specific intervals the most recently started one wins.
    /// </para>
    /// </summary>
    public ReferenceRange? ResolveRange(ReferenceRangeContext context) =>
        _referenceRanges
            .Where(r => r.IsActive
                        && (context.ReferenceDate is not { } on || r.IsInForceOn(on))
                        && r.Matches(context))
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.ValidFrom)
            .FirstOrDefault();

    /// <summary>Rounds a raw instrument value to the parameter's declared precision.</summary>
    public decimal Round(decimal value) =>
        Math.Round(value, Math.Clamp(Decimals, 0, 6), MidpointRounding.AwayFromZero);

    public bool IsTechnicallyImpossible(decimal value) =>
        (TechnicalLimitLow is { } low && value < low) ||
        (TechnicalLimitHigh is { } high && value > high);

    public bool IsOutsideMeasuringRange(decimal value) =>
        (MeasuringRangeLow is { } low && value < low) ||
        (MeasuringRangeHigh is { } high && value > high);

    public bool IsCritical(decimal value) =>
        (CriticalLow is { } low && value <= low) ||
        (CriticalHigh is { } high && value >= high);

    /// <summary>
    /// Whether the change from a previous result exceeds the configured delta. Returns false when
    /// no delta check is configured, when there is no comparable previous value, or when the
    /// previous value is zero (a percentage change is undefined there).
    /// </summary>
    public bool FailsDeltaCheck(decimal current, decimal? previous, int? daysSincePrevious)
    {
        if (DeltaCheckPercent is not { } threshold || previous is not { } prior || prior == 0m)
            return false;
        if (DeltaCheckWindowDays is { } window && daysSincePrevious is { } days && days > window)
            return false;

        var change = Math.Abs((current - prior) / prior) * 100m;
        return change > threshold;
    }
}

/// <summary>How a result for a parameter is captured and stored.</summary>
public enum ResultValueType
{
    /// <summary>A decimal measurement with a unit.</summary>
    Numeric = 0,

    /// <summary>Free text — a microscopy description, a comment-only parameter.</summary>
    Text = 1,

    /// <summary>One value from a controlled list (Positif / Négatif, Présent / Absent).</summary>
    Coded = 2,

    /// <summary>An ordered grade: Négatif, Traces, +, ++, +++.</summary>
    SemiQuantitative = 3,

    /// <summary>A titre expressed as a ratio, e.g. 1/320.</summary>
    Titre = 4,

    /// <summary>A numeric value that may be censored: "&lt; 0,01".</summary>
    NumericWithOperator = 5,

    /// <summary>A section heading carrying no result, used to structure a long panel.</summary>
    Heading = 6
}

/// <summary>One allowed answer for a coded or semi-quantitative parameter.</summary>
public sealed class CodedResultOption : Entity
{
    private CodedResultOption() { }

    public CodedResultOption(string code, MultilingualText label)
    {
        Code = code;
        Label = label;
    }

    public Guid TestParameterId { get; private set; }
    internal void AttachTo(Guid parameterId) => TestParameterId = parameterId;

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Label { get; set; } = new();

    /// <summary>Rank for semi-quantitative scales; also the sort order in the picker.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Whether choosing this option means the result is abnormal.</summary>
    public bool IsAbnormal { get; set; }

    /// <summary>Whether choosing this option triggers the critical-result procedure.</summary>
    public bool IsCritical { get; set; }

    /// <summary>The expected answer in a healthy subject, shown as the reference on the report.</summary>
    public bool IsExpectedNormal { get; set; }
}
