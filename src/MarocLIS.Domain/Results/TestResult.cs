using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Results;

/// <summary>
/// The result of one parameter for one order item. Values are never overwritten in place: every
/// change after the first entry produces a <see cref="ResultRevision"/> holding the previous value
/// and the reason, which is what makes a laboratory defensible in an audit or a dispute.
/// </summary>
public sealed class TestResult : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<ResultRevision> _revisions = [];

    private TestResult() { }

    public TestResult(Guid labOrderId, Guid labOrderItemId, Guid testParameterId, Guid siteId)
    {
        LabOrderId = labOrderId;
        LabOrderItemId = labOrderItemId;
        TestParameterId = testParameterId;
        SiteId = siteId;
        Status = TestResultStatus.Awaited;
    }

    public Guid LabOrderId { get; private set; }
    public Guid LabOrderItemId { get; private set; }
    public Guid TestParameterId { get; private set; }
    public TestParameter? TestParameter { get; private set; }
    public Guid SiteId { get; set; }

    /// <summary>Denormalised for worklist queries and for the report, which must not need the catalogue.</summary>
    public Guid DepartmentId { get; set; }

    public TestResultStatus Status { get; private set; }

    // ---- The value, in whichever shape the parameter declares -------------------------------

    /// <summary>Numeric value, rounded to the parameter's declared precision.</summary>
    public decimal? NumericValue { get; private set; }

    /// <summary>
    /// Censoring operator for a value outside the measuring range: "&lt;", "&gt;", "≤", "≥".
    /// Reported together with the numeric bound, never silently dropped.
    /// </summary>
    public string? ValueOperator { get; private set; }

    /// <summary>Free text, microscopy description, or antibiogram commentary.</summary>
    public string? TextValue { get; private set; }

    /// <summary>Selected code for a coded or semi-quantitative parameter.</summary>
    public string? CodedValue { get; private set; }

    /// <summary>Denominator of a titre expressed as 1/N.</summary>
    public int? TitreDenominator { get; private set; }

    /// <summary>
    /// Raw value exactly as the instrument sent it, before any conversion or rounding. Kept so a
    /// disputed result can be traced back to the analyser trace.
    /// </summary>
    public string? RawInstrumentValue { get; set; }

    /// <summary>Dilution factor applied and already accounted for in <see cref="NumericValue"/>.</summary>
    public decimal? DilutionFactor { get; set; }

    // ---- Interpretation ----------------------------------------------------------------------

    public ResultInterpretation Interpretation { get; private set; } = ResultInterpretation.NotInterpretable;

    public ResultFlag Flags { get; private set; } = ResultFlag.None;

    /// <summary>
    /// Reference interval that was applied, frozen as text. If the catalogue interval is revised
    /// next year, a reprint of this report still shows what the clinician was actually told.
    /// </summary>
    public string? AppliedReferenceRangeText { get; set; }
    public Guid? AppliedReferenceRangeId { get; set; }
    public decimal? AppliedLowerLimit { get; set; }
    public decimal? AppliedUpperLimit { get; set; }
    public string? AppliedUnitSymbol { get; set; }
    public int AppliedDecimals { get; set; } = 2;

    /// <summary>Previous comparable value and its date, printed as an antériorité column.</summary>
    public decimal? PreviousNumericValue { get; set; }
    public DateOnly? PreviousResultDate { get; set; }

    // ---- Production context (traceability, ISO 15189 §5.5 / §5.8) ----------------------------

    /// <summary>The analyser that produced the value, null for a manual bench result.</summary>
    public Guid? InstrumentId { get; set; }

    /// <summary>Reagent lot actually used — the link between a suspect result and a recalled lot.</summary>
    public Guid? ReagentLotId { get; set; }

    /// <summary>Calibration in force at the time of the run.</summary>
    public Guid? CalibrationRecordId { get; set; }

    /// <summary>QC run that covers this result; a failed QC blocks validation.</summary>
    public Guid? CoveringQcRunId { get; set; }

    public DateTimeOffset? MeasuredAt { get; set; }

    // ---- Who did what ------------------------------------------------------------------------

    public DateTimeOffset? EnteredAt { get; private set; }
    public Guid? EnteredByUserId { get; private set; }

    /// <summary>True when the value arrived from an instrument rather than a keyboard.</summary>
    public bool IsAutoTransmitted { get; private set; }

    public DateTimeOffset? TechnicallyValidatedAt { get; private set; }
    public Guid? TechnicallyValidatedByUserId { get; private set; }

    public DateTimeOffset? ValidatedAt { get; private set; }
    public Guid? ValidatedByUserId { get; private set; }

    // ---- Commentary --------------------------------------------------------------------------

    /// <summary>Technical note from the bench — not printed unless flagged for the report.</summary>
    public string? TechnicalComment { get; set; }

    /// <summary>
    /// The analyser's own flags and comments on this value (linearity exceeded, reagent alarm…), as
    /// sent. Shown at validation: a value the analyser itself doubted must not be signed unseen.
    /// </summary>
    public string? InstrumentFlags { get; set; }

    /// <summary>The biologist's interpretive comment, printed under the result in three languages.</summary>
    public MultilingualText? BiologistComment { get; set; }

    /// <summary>Whether the technical comment is disclosed on the report (e.g. "sur sérum hémolysé").</summary>
    public bool PrintTechnicalComment { get; set; }

    public IReadOnlyCollection<ResultRevision> Revisions => _revisions.AsReadOnly();

    public bool IsValidated => Status is TestResultStatus.Validated or TestResultStatus.Reported;

    // ---- Entry -------------------------------------------------------------------------------

    /// <summary>
    /// Records a value the analyser itself reported as censored — "&lt; 0,01", "&gt; 1000".
    /// <para>
    /// Distinct from <see cref="EnterNumeric"/> because the censoring is the analyser's statement
    /// about its own detection limit, not a conclusion drawn from the catalogue's measuring range.
    /// Dropping the operator and storing 0,01 would assert a precision the method does not have,
    /// and a clinician reading a bare 0,01 cannot tell it was below the limit of detection — which
    /// for a tumour marker or a drug level changes what they do next.
    /// </para>
    /// </summary>
    public Result EnterCensoredNumeric(TestParameter parameter, decimal bound, string valueOperator,
                                        DateTimeOffset at, Guid byUserId,
                                        bool fromInstrument = false, string? rawValue = null)
    {
        if (valueOperator is not ("<" or ">" or "<=" or ">="))
            return Result.Failure(Error.Validation("Result_InvalidValueOperator", valueOperator));

        var entered = EnterNumeric(parameter, bound, at, byUserId, fromInstrument, rawValue);
        if (entered.IsFailure) return entered;

        // Set after entry: EnterNumeric clears the operator, and its own censoring — applied when
        // the value falls outside the measuring range — must not override what the analyser said.
        ValueOperator = valueOperator;

        return Result.Success();
    }

    /// <summary>
    /// Records a numeric value. Refuses technically impossible values outright and, when the value
    /// falls outside the measuring range, forces it to be reported as censored rather than as a
    /// bare number the method cannot actually support.
    /// </summary>
    /// <summary>A stored figure is below this in absolute value (its column: 12 digits, 6 decimals).</summary>
    public const decimal MaximumMagnitude = 1_000_000_000_000m;

    /// <summary>The longest text result (its column).</summary>
    public const int TextValueMaxLength = 4000;

    /// <summary>The longest raw value kept as received (its column).</summary>
    public const int RawValueMaxLength = 200;

    /// <summary>
    /// The raw value as kept: cut to its column, since it is a trace of what was received, not
    /// the value. An analyser's long text answer failed the save of its whole transmission.
    /// </summary>
    public static string? KeepRaw(string? raw) =>
        raw is { Length: > RawValueMaxLength } ? raw[..RawValueMaxLength] : raw;

    public Result EnterNumeric(TestParameter parameter, decimal value, DateTimeOffset at,
                               Guid byUserId, bool fromInstrument = false, string? rawValue = null)
    {
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_ValidatedRequiresRevision"));
        // Whatever the parameter's limits, a figure of thirteen digits is no measurement — a barcode
        // scanned into the value cell, an analyser's garbled field — and the column cannot hold it:
        // the save failed for the whole file, or the whole transmission, instead of this one value.
        if (parameter.IsTechnicallyImpossible(value) || Math.Abs(value) >= MaximumMagnitude)
            return Result.Failure(Error.Validation("Result_TechnicallyImpossible", value));

        var rounded = parameter.Round(value);

        NumericValue = rounded;
        ValueOperator = null;

        if (parameter.MeasuringRangeLow is { } low && rounded < low)
        {
            NumericValue = low;
            ValueOperator = "<";
        }
        else if (parameter.MeasuringRangeHigh is { } high && rounded > high)
        {
            NumericValue = high;
            ValueOperator = ">";
        }

        TextValue = null;
        CodedValue = null;
        TitreDenominator = null;
        RawInstrumentValue = KeepRaw(rawValue) ?? RawInstrumentValue;
        AppliedDecimals = parameter.Decimals;

        StampEntry(at, byUserId, fromInstrument);
        return Result.Success();
    }

    public Result EnterText(string text, DateTimeOffset at, Guid byUserId, bool fromInstrument = false)
    {
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_ValidatedRequiresRevision"));
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure(Error.Validation("Result_TextRequired"));
        if (text.Trim().Length > TextValueMaxLength)
            return Result.Failure(Error.Validation("Result_TextTooLong", TextValueMaxLength));

        TextValue = text.Trim();
        NumericValue = null;
        ValueOperator = null;
        CodedValue = null;
        TitreDenominator = null;
        StampEntry(at, byUserId, fromInstrument);
        return Result.Success();
    }

    public Result EnterCoded(TestParameter parameter, string code, DateTimeOffset at,
                             Guid byUserId, bool fromInstrument = false)
    {
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_ValidatedRequiresRevision"));

        var option = parameter.CodedOptions.FirstOrDefault(
            o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
        if (option is null)
            return Result.Failure(Error.Validation("Result_CodedValueNotAllowed", code));

        CodedValue = option.Code;
        NumericValue = null;
        ValueOperator = null;
        TextValue = null;
        TitreDenominator = null;
        StampEntry(at, byUserId, fromInstrument);
        return Result.Success();
    }

    public Result EnterTitre(int denominator, DateTimeOffset at, Guid byUserId)
    {
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_ValidatedRequiresRevision"));
        if (denominator <= 0)
            return Result.Failure(Error.Validation("Result_TitreMustBePositive"));

        TitreDenominator = denominator;
        NumericValue = null;
        TextValue = null;
        CodedValue = null;
        StampEntry(at, byUserId, false);
        return Result.Success();
    }

    private void StampEntry(DateTimeOffset at, Guid byUserId, bool fromInstrument)
    {
        EnteredAt = at;
        EnteredByUserId = byUserId;
        IsAutoTransmitted = fromInstrument;
        MeasuredAt ??= at;
        Status = TestResultStatus.Entered;
        // A value replaces an earlier "not done": the flag would otherwise keep the report
        // printing "Non fait" over the figure now entered.
        Flags &= ~ResultFlag.NotDone;
        // A new value invalidates any previous technical check.
        TechnicallyValidatedAt = null;
        TechnicallyValidatedByUserId = null;
    }

    /// <summary>
    /// Withdraws the value of a result that no longer has one — a calculated parameter one of
    /// whose inputs was removed. The result goes back to awaited, with its interpretation and the
    /// flags that described the old value, so a stale LDL does not outlive the cholesterol it was
    /// computed from. A released result is changed only through <see cref="Revise"/>.
    /// </summary>
    public Result ClearValue()
    {
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_ValidatedRequiresRevision"));

        NumericValue = null;
        ValueOperator = null;
        TextValue = null;
        CodedValue = null;
        TitreDenominator = null;
        Interpretation = ResultInterpretation.NotInterpretable;
        Flags &= ~(InterpretationFlags | ResultFlag.DeltaCheckFailed | ResultFlag.NotDone);
        PreviousNumericValue = null;
        PreviousResultDate = null;
        Status = TestResultStatus.Awaited;
        TechnicallyValidatedAt = null;
        TechnicallyValidatedByUserId = null;
        return Result.Success();
    }

    /// <summary>The flags <see cref="ApplyInterpretation"/> derives from the value and the interval.</summary>
    private const ResultFlag InterpretationFlags =
        ResultFlag.Low | ResultFlag.High | ResultFlag.CriticalLow | ResultFlag.CriticalHigh
        | ResultFlag.Abnormal | ResultFlag.OutsideMeasuringRange;

    /// <summary>
    /// Replaces an already-validated value. Produces a revision holding the old value and the
    /// stated reason, and returns the result to the bench so it is validated again before it can
    /// be reported. This is the only lawful way to change a released figure.
    /// <para>
    /// The operator and the titre are part of the value and are replaced with it, not cleared
    /// behind the caller's back. The entry is re-stamped: the result now holds a figure nobody has
    /// communicated yet. To withdraw a release without changing the value, use
    /// <see cref="WithdrawValidation"/>.
    /// </para>
    /// </summary>
    public Result Revise(decimal? newNumeric, string? newText, string? newCoded,
                         DateTimeOffset at, Guid byUserId, string reason,
                         string? newValueOperator = null, int? newTitreDenominator = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Result_RevisionReasonRequired"));

        _revisions.Add(new ResultRevision(Id, _revisions.Count + 1, at, byUserId, reason)
        {
            PreviousNumericValue = NumericValue,
            PreviousValueOperator = ValueOperator,
            PreviousTextValue = TextValue,
            PreviousCodedValue = CodedValue,
            PreviousTitreDenominator = TitreDenominator,
            PreviousInterpretation = Interpretation,
            WasValidated = IsValidated,
            PreviousValidatedByUserId = ValidatedByUserId,
            PreviousValidatedAt = ValidatedAt
        });

        NumericValue = newNumeric;
        TextValue = newText;
        CodedValue = newCoded;
        ValueOperator = newNumeric is null ? null : newValueOperator;
        TitreDenominator = newTitreDenominator;
        EnteredAt = at;
        EnteredByUserId = byUserId;
        // The figure now standing is the reviser's, not the analyser's transmission.
        IsAutoTransmitted = false;

        ValidatedAt = null;
        ValidatedByUserId = null;
        TechnicallyValidatedAt = null;
        TechnicallyValidatedByUserId = null;
        Status = TestResultStatus.Entered;
        Flags |= ResultFlag.Corrected;
        return Result.Success();
    }

    // ---- Interpretation and flagging ---------------------------------------------------------

    /// <summary>
    /// Applies the reference interval and the parameter's thresholds, and freezes the interval text
    /// onto the result. Called at entry and again whenever the patient's demographics change,
    /// because a corrected sex or date of birth changes which interval applies.
    /// <para>
    /// The delta-check flag is cleared with the others: it described the value that was there
    /// before, and a corrected value must be checked afresh — the caller runs the delta check
    /// after interpreting, so a value that still fails it is flagged again.
    /// </para>
    /// </summary>
    public void ApplyInterpretation(TestParameter parameter, ReferenceRange? range,
                                    AppLanguage language, string? unitSymbol)
    {
        AppliedUnitSymbol = unitSymbol;
        AppliedDecimals = parameter.Decimals;
        AppliedReferenceRangeId = range?.Id;
        AppliedReferenceRangeText = range?.Format(language, parameter.Decimals);
        AppliedLowerLimit = range?.LowerLimit;
        AppliedUpperLimit = range?.UpperLimit;

        Flags &= ~(InterpretationFlags | ResultFlag.DeltaCheckFailed);

        if (NumericValue is { } value)
        {
            Interpretation = range?.Interpret(value) ?? ResultInterpretation.NotInterpretable;

            if (Interpretation == ResultInterpretation.Low) Flags |= ResultFlag.Low;
            if (Interpretation == ResultInterpretation.High) Flags |= ResultFlag.High;

            if (parameter.CriticalLow is { } criticalLow && value <= criticalLow)
            {
                Interpretation = ResultInterpretation.CriticalLow;
                Flags |= ResultFlag.CriticalLow;
            }
            else if (parameter.CriticalHigh is { } criticalHigh && value >= criticalHigh)
            {
                Interpretation = ResultInterpretation.CriticalHigh;
                Flags |= ResultFlag.CriticalHigh;
            }

            if (ValueOperator is not null) Flags |= ResultFlag.OutsideMeasuringRange;
        }
        else if (CodedValue is not null)
        {
            var option = parameter.CodedOptions.FirstOrDefault(
                o => string.Equals(o.Code, CodedValue, StringComparison.OrdinalIgnoreCase));

            if (option?.IsCritical == true)
            {
                Interpretation = ResultInterpretation.CriticalHigh;
                Flags |= ResultFlag.CriticalHigh;
            }
            else if (option?.IsAbnormal == true)
            {
                Interpretation = ResultInterpretation.Abnormal;
                Flags |= ResultFlag.Abnormal;
            }
            else
            {
                Interpretation = ResultInterpretation.Normal;
            }

            AppliedReferenceRangeText ??= parameter.CodedOptions
                .FirstOrDefault(o => o.IsExpectedNormal)?.Label.For(language);
        }
        else
        {
            Interpretation = ResultInterpretation.NotInterpretable;
        }
    }

    public void FlagDeltaCheckFailure() => Flags |= ResultFlag.DeltaCheckFailed;

    public void FlagQcFailure() => Flags |= ResultFlag.QcOutOfControl;

    public void FlagSampleDefect() => Flags |= ResultFlag.SampleDefect;

    public void FlagSubcontracted() => Flags |= ResultFlag.Subcontracted;

    /// <summary>Records that the value was obtained on a dilution, which the report discloses.</summary>
    public void FlagDiluted(decimal factor)
    {
        DilutionFactor = factor;
        Flags |= ResultFlag.Diluted;
    }

    /// <summary>
    /// Records a censoring operator the operator entered deliberately — "&lt; 0,01" from a
    /// qualitative read, where the method genuinely cannot say more than "below this".
    /// <para>
    /// Kept separate from <see cref="EnterNumeric"/>, which sets the operator only when the value
    /// falls outside the declared measuring range. Both are legitimate and neither should silently
    /// overwrite the other, so the deliberate one is applied explicitly and only when the automatic
    /// one did not already fire.
    /// </para>
    /// </summary>
    public Result SetCensoringOperator(string? valueOperator)
    {
        if (string.IsNullOrWhiteSpace(valueOperator))
        {
            ValueOperator = null;
            return Result.Success();
        }

        var normalised = valueOperator.Trim() switch
        {
            "<" => "<",
            ">" => ">",
            "<=" or "≤" => "≤",
            ">=" or "≥" => "≥",
            _ => null
        };

        if (normalised is null)
            return Result.Failure(Error.Validation("Result_InvalidOperator", valueOperator));

        ValueOperator = normalised;
        Flags |= ResultFlag.OutsideMeasuringRange;
        return Result.Success();
    }

    public void ClearFlag(ResultFlag flag) => Flags &= ~flag;

    public bool IsCritical => Flags.HasFlag(ResultFlag.CriticalLow) || Flags.HasFlag(ResultFlag.CriticalHigh);

    public bool IsAbnormal => Interpretation is not (ResultInterpretation.Normal
                                                  or ResultInterpretation.NotInterpretable);

    public bool HasValue => NumericValue is not null || !string.IsNullOrWhiteSpace(TextValue)
                            || CodedValue is not null || TitreDenominator is not null;

    // ---- Validation --------------------------------------------------------------------------

    public Result TechnicallyValidate(DateTimeOffset at, Guid byUserId)
    {
        if (!HasValue)
            return Result.Failure(Error.Conflict("Result_NoValueToValidate"));
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_AlreadyValidated"));

        TechnicallyValidatedAt = at;
        TechnicallyValidatedByUserId = byUserId;
        Status = TestResultStatus.TechnicallyValidated;
        return Result.Success();
    }

    /// <summary>
    /// Withdraws the technical validation of a value whose analysis is sent back to the bench. The
    /// value stays, as the record of the first run, until the new run replaces it — but nobody
    /// stands behind it any more, so it is no longer on the biologist's worklist as releasable.
    /// A released value is not reopened this way: its release is withdrawn first, by somebody
    /// entitled to withdraw it.
    /// </summary>
    public Result ReopenForRepeat()
    {
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_ValidatedRequiresRevision"));

        if (Status == TestResultStatus.TechnicallyValidated)
        {
            Status = TestResultStatus.Entered;
            TechnicallyValidatedAt = null;
            TechnicallyValidatedByUserId = null;
        }

        return Result.Success();
    }

    /// <summary>
    /// Biological validation of the individual value. The caller must already have established the
    /// user's right to validate; this guard enforces the structural preconditions — a value must
    /// exist, an out-of-control QC must have been explicitly overridden, and a critical value must
    /// have been communicated before the result leaves the laboratory.
    /// </summary>
    public Result BiologicallyValidate(DateTimeOffset at, Guid byBiologistUserId,
                                       bool qcOverrideJustified, bool criticalCommunicated)
    {
        var allowed = CanBiologicallyValidate(qcOverrideJustified, criticalCommunicated);
        if (allowed.IsFailure) return allowed;

        ValidatedAt = at;
        ValidatedByUserId = byBiologistUserId;
        Status = TestResultStatus.Validated;
        return Result.Success();
    }

    /// <summary>
    /// The preconditions of <see cref="BiologicallyValidate"/>, checked without changing anything,
    /// so a batch can be vetted before any of it is applied.
    /// <para>
    /// Technical validation comes first, as it does for the analysis
    /// (<see cref="Orders.LabOrderItem.MarkBiologicallyValidated"/>). A value that was only entered
    /// has not been checked for plausibility, QC coverage or delta by anybody; releasing it straight
    /// from entry left the analysis behind at "result entered" while its figure was already final.
    /// </para>
    /// </summary>
    public Result CanBiologicallyValidate(bool qcOverrideJustified, bool criticalCommunicated)
    {
        if (!HasValue)
            return Result.Failure(Error.Conflict("Result_NoValueToValidate"));
        if (IsValidated)
            return Result.Failure(Error.Conflict("Result_AlreadyValidated"));
        if (Status != TestResultStatus.TechnicallyValidated)
            return Result.Failure(Error.Regulatory("Item_TechnicalValidationRequiredFirst"));
        if (Flags.HasFlag(ResultFlag.QcOutOfControl) && !qcOverrideJustified)
            return Result.Failure(Error.Regulatory("Result_QcOutOfControlBlocksValidation"));
        if (IsCritical && !criticalCommunicated)
            return Result.Failure(Error.Regulatory("Result_CriticalMustBeCommunicated"));

        return Result.Success();
    }

    /// <summary>
    /// Withdraws a biological validation without touching the value. Records a revision holding the
    /// value and the reason, exactly as <see cref="Revise"/> does, so the amendment trail shows the
    /// release and its withdrawal; the result goes back to technically validated, where its analysis
    /// returns too (<see cref="Orders.LabOrderItem.Unvalidate"/>).
    /// <para>
    /// Not a <see cref="Revise"/> with the same figures: that path takes only a numeric, text and
    /// coded value and clears the operator and the titre, so "&gt; 1000" came back as a bare 1000
    /// and a 1/320 titre as nothing. The entry stamps are kept too — the value was not re-entered,
    /// and a critical call made about it still covers it.
    /// </para>
    /// </summary>
    public Result WithdrawValidation(DateTimeOffset at, Guid byUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Result_RevisionReasonRequired"));
        if (Status != TestResultStatus.Validated)
            return Result.Failure(Error.Conflict("Item_NotValidated"));

        _revisions.Add(new ResultRevision(Id, _revisions.Count + 1, at, byUserId, reason)
        {
            PreviousNumericValue = NumericValue,
            PreviousValueOperator = ValueOperator,
            PreviousTextValue = TextValue,
            PreviousCodedValue = CodedValue,
            PreviousTitreDenominator = TitreDenominator,
            PreviousInterpretation = Interpretation,
            WasValidated = true,
            PreviousValidatedByUserId = ValidatedByUserId,
            PreviousValidatedAt = ValidatedAt
        });

        ValidatedAt = null;
        ValidatedByUserId = null;
        Status = TechnicallyValidatedAt is null ? TestResultStatus.Entered
                                                : TestResultStatus.TechnicallyValidated;
        Flags |= ResultFlag.Corrected;
        return Result.Success();
    }

    public void MarkReported() => Status = TestResultStatus.Reported;

    /// <summary>Records that the analysis was not carried out, with the reason printed on the report.</summary>
    public Result MarkNotDone(string reason, DateTimeOffset at, Guid byUserId)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation("Result_NotDoneReasonRequired"));

        TextValue = null;
        NumericValue = null;
        CodedValue = null;
        TechnicalComment = reason;
        PrintTechnicalComment = true;
        Flags |= ResultFlag.NotDone;
        Status = TestResultStatus.NotDone;
        EnteredAt = at;
        EnteredByUserId = byUserId;
        return Result.Success();
    }

    /// <summary>The value as it appears on the report, in the requested language.</summary>
    public string FormatValue(AppLanguage language)
    {
        if (Flags.HasFlag(ResultFlag.NotDone))
            return language switch
            {
                AppLanguage.Arabic => "لم يُنجز",
                AppLanguage.English => "Not done",
                _ => "Non fait"
            };

        if (NumericValue is { } value)
        {
            var text = value.ToString("N" + Math.Clamp(AppliedDecimals, 0, 6), language.ToCulture());
            return ValueOperator is null ? text : $"{ValueOperator} {text}";
        }

        if (TitreDenominator is { } denominator) return $"1/{denominator}";
        if (!string.IsNullOrWhiteSpace(CodedValue)) return CodedValue;
        return TextValue ?? "-";
    }
}

public enum TestResultStatus
{
    /// <summary>Row created by the order; no value yet.</summary>
    Awaited = 0,
    Entered = 1,
    TechnicallyValidated = 2,
    Validated = 3,
    Reported = 4,
    /// <summary>Explicitly not carried out, with a reason.</summary>
    NotDone = 8
}

/// <summary>
/// Flags carried alongside a result and printed as annotations. A flags enum because a single
/// value is commonly both high and delta-check failed on a defective sample.
/// </summary>
[Flags]
public enum ResultFlag
{
    None = 0,
    Low = 1 << 0,
    High = 1 << 1,
    CriticalLow = 1 << 2,
    CriticalHigh = 1 << 3,
    /// <summary>Abnormal coded or semi-quantitative answer.</summary>
    Abnormal = 1 << 4,
    /// <summary>Censored because the value fell outside the analytical measuring range.</summary>
    OutsideMeasuringRange = 1 << 5,
    /// <summary>Changed too much against the previous result for the same patient.</summary>
    DeltaCheckFailed = 1 << 6,
    /// <summary>The covering internal quality control was out of control.</summary>
    QcOutOfControl = 1 << 7,
    /// <summary>Produced on a sample accepted with a documented defect.</summary>
    SampleDefect = 1 << 8,
    /// <summary>Value obtained after a dilution.</summary>
    Diluted = 1 << 9,
    /// <summary>Corrected after having been validated — the report must be issued as amended.</summary>
    Corrected = 1 << 10,
    /// <summary>Produced by a partner laboratory.</summary>
    Subcontracted = 1 << 11,
    /// <summary>Not carried out.</summary>
    NotDone = 1 << 12,
    /// <summary>Repeated before release.</summary>
    Repeated = 1 << 13
}

/// <summary>
/// An immutable record of what a result used to say. Append-only; there is no code path that
/// deletes or edits a revision.
/// </summary>
public sealed class ResultRevision : Entity
{
    private ResultRevision() { }

    public ResultRevision(Guid testResultId, int revisionNumber, DateTimeOffset at,
                          Guid byUserId, string reason)
    {
        TestResultId = testResultId;
        RevisionNumber = revisionNumber;
        RevisedAt = at;
        RevisedByUserId = byUserId;
        Reason = reason;
    }

    public Guid TestResultId { get; private set; }
    public int RevisionNumber { get; private set; }
    public DateTimeOffset RevisedAt { get; private set; }
    public Guid RevisedByUserId { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    public decimal? PreviousNumericValue { get; init; }
    public string? PreviousValueOperator { get; init; }
    public string? PreviousTextValue { get; init; }
    public string? PreviousCodedValue { get; init; }
    public int? PreviousTitreDenominator { get; init; }
    public ResultInterpretation PreviousInterpretation { get; init; }

    /// <summary>Whether the superseded value had already been biologically validated.</summary>
    public bool WasValidated { get; init; }
    public Guid? PreviousValidatedByUserId { get; init; }
    public DateTimeOffset? PreviousValidatedAt { get; init; }
}
