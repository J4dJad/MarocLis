using MarocLIS.Domain.Common;
using MarocLIS.Domain.Patients;
using MarocLIS.Domain.Results;

namespace MarocLIS.Domain.Catalog;

/// <summary>
/// The facts about a patient that decide which reference interval applies. Passed as a single
/// value object so the selection logic stays testable and identical between result entry,
/// report rendering and the re-interpretation that follows a demographic correction.
/// <para>
/// <see cref="ReferenceDate"/> is the day the result is interpreted for. A revised interval does
/// not rewrite the past: the predecessor stays in the catalogue with its end date, and only the
/// interval in force on that day may apply. Null leaves the validity window unchecked.
/// </para>
/// <para>
/// <see cref="DateOfBirth"/> lets an age band written in months be compared on the calendar, as
/// one written in years is through <see cref="AgeInYears"/>; see <see cref="ReferenceRange.Matches"/>.
/// </para>
/// </summary>
public readonly record struct ReferenceRangeContext(
    Gender Gender,
    int? AgeInDays,
    int? AgeInYears,
    bool IsPregnant,
    int? PregnancyTrimester,
    Guid? InstrumentId,
    DateOnly? ReferenceDate = null,
    DateOnly? DateOfBirth = null)
{
    public static ReferenceRangeContext For(Patient patient, DateOnly on, Guid? instrumentId = null) =>
        new(patient.Gender,
            patient.AgeInDays(on),
            patient.AgeInYears(on),
            patient.IsPregnant,
            TrimesterFrom(patient.LastMenstrualPeriod, on),
            instrumentId,
            on,
            patient.DateOfBirth);

    /// <summary>
    /// The context a stored result is interpreted in: the patient as they are now recorded, aged on
    /// the day the result was measured (the day it was entered when no measurement time is known,
    /// <paramref name="today"/> for a result with neither), and the analyser that produced it.
    /// <para>
    /// The same for every caller — entry, the analyser interface and the re-interpretation after a
    /// demographic correction — so a re-run reaches the interval the first run did. Built without
    /// the instrument, a method-specific TSH interval was silently replaced by the generic one.
    /// </para>
    /// </summary>
    public static ReferenceRangeContext ForResult(Patient patient, TestResult result, DateOnly today) =>
        For(patient, ReferenceDateOf(result, today), result.InstrumentId);

    /// <summary>The day a stored result is interpreted for; see <see cref="ForResult"/>.</summary>
    public static DateOnly ReferenceDateOf(TestResult result, DateOnly today) =>
        (result.MeasuredAt ?? result.EnteredAt) is { } at ? DateOnly.FromDateTime(at.Date) : today;

    /// <summary>
    /// Gestational trimester from the last menstrual period: 1 up to 13 completed weeks,
    /// 2 up to 27, 3 thereafter. Null when the LMP is unknown or not plausible.
    /// </summary>
    public static int? TrimesterFrom(DateOnly? lastMenstrualPeriod, DateOnly on)
    {
        if (lastMenstrualPeriod is not { } lmp || lmp > on) return null;
        var weeks = (on.DayNumber - lmp.DayNumber) / 7;
        return weeks switch
        {
            < 0 => null,
            <= 13 => 1,
            <= 27 => 2,
            <= 45 => 3,
            _ => null
        };
    }
}

/// <summary>
/// A reference interval (valeurs usuelles) for one parameter and one population stratum.
/// ISO 15189 §5.5.2 requires the laboratory to define its intervals, review them periodically and
/// state their source — hence <see cref="Source"/> is part of the record, not a comment.
/// </summary>
public sealed class ReferenceRange : Entity, IAuditable
{
    private ReferenceRange() { }

    public ReferenceRange(Gender appliesToGender = Gender.Unknown)
        => AppliesToGender = appliesToGender;

    public Guid TestParameterId { get; private set; }
    internal void AttachTo(Guid parameterId) => TestParameterId = parameterId;

    /// <summary><see cref="Gender.Unknown"/> means the range applies to any sex.</summary>
    public Gender AppliesToGender { get; set; } = Gender.Unknown;

    /// <summary>
    /// Inclusive lower bound of the age band, in days. Null means "from birth". A band whose bounds
    /// are whole multiples of 365 means years and of 30 means months — the factors the catalogue
    /// editor stores them with — and is compared on the calendar; see <see cref="AgeBandUnitDays"/>.
    /// </summary>
    public int? AgeFromDays { get; set; }

    /// <summary>Exclusive upper bound of the age band, in days. Null means "no upper limit".</summary>
    public int? AgeToDays { get; set; }

    /// <summary>Days per year in a stored age bound: "50 years" is stored as 18 250.</summary>
    public const int DaysPerYear = 365;

    /// <summary>Days per month in a stored age bound: "6 months" is stored as 180.</summary>
    public const int DaysPerMonth = 30;

    /// <summary>
    /// The unit an age band is written in: years (365) when both bounds are whole years, otherwise
    /// months (30) when both are whole months, otherwise days (1). The catalogue editor reopens a
    /// band in this unit, and matching reads the bounds in it, so what the biologist typed is what
    /// is compared.
    /// </summary>
    public static int AgeBandUnitDays(int? fromDays, int? toDays)
    {
        var from = fromDays ?? 0;
        var to = toDays ?? 0;

        if (from % DaysPerYear == 0 && to % DaysPerYear == 0) return DaysPerYear;
        if (from % DaysPerMonth == 0 && to % DaysPerMonth == 0) return DaysPerMonth;

        return 1;
    }

    /// <summary>When set, the range applies only to pregnant (true) or non-pregnant (false) patients.</summary>
    public bool? AppliesToPregnancy { get; set; }

    /// <summary>Restricts the range to one gestational trimester.</summary>
    public int? PregnancyTrimester { get; set; }

    /// <summary>
    /// Restricts the range to results produced on one instrument. Method-dependent intervals are
    /// the norm for hormones and immunoassays.
    /// </summary>
    public Guid? InstrumentId { get; set; }

    // ---- The interval itself ------------------------------------------------------------------

    public decimal? LowerLimit { get; set; }
    public decimal? UpperLimit { get; set; }

    /// <summary>
    /// Printed interval, when the numeric bounds do not tell the whole story —
    /// "&lt; 5,7 % (normal) / 5,7–6,4 % (prédiabète)". Falls back to a generated string.
    /// </summary>
    public MultilingualText? DisplayText { get; set; }

    /// <summary>The expected answer for a non-numeric parameter, e.g. "Négatif".</summary>
    public string? ExpectedCodedValue { get; set; }

    /// <summary>
    /// Clinical decision threshold as opposed to a population interval — an LDL target, an HbA1c
    /// diagnostic cut-off. Reported as a target rather than as "normal/abnormal".
    /// </summary>
    public bool IsDecisionThreshold { get; set; }

    /// <summary>Literature or manufacturer reference the interval was taken from.</summary>
    public string? Source { get; set; }

    /// <summary>Date of the last periodic review, per ISO 15189.</summary>
    public DateOnly? LastReviewedOn { get; set; }

    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidUntil { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    // ---- Matching -----------------------------------------------------------------------------

    /// <summary>Whether the interval is in force on a given day; both ends are inclusive.</summary>
    public bool IsInForceOn(DateOnly on) =>
        ValidFrom <= on && (ValidUntil is null || ValidUntil >= on);

    public bool Matches(ReferenceRangeContext context)
    {
        if (AppliesToGender != Gender.Unknown && AppliesToGender != context.Gender)
            return false;

        if (AgeFromDays is not null || AgeToDays is not null)
        {
            if (context.AgeInDays is null && context.AgeInYears is null) return false;

            var unit = AgeBandUnitDays(AgeFromDays, AgeToDays);

            if (AgeFromDays is { } from && !HasReached(from, unit, context)) return false;
            if (AgeToDays is { } to && HasReached(to, unit, context)) return false;
        }

        if (AppliesToPregnancy is { } pregnancy && pregnancy != context.IsPregnant)
            return false;

        if (PregnancyTrimester is { } trimester && trimester != context.PregnancyTrimester)
            return false;

        if (InstrumentId is { } instrument && instrument != context.InstrumentId)
            return false;

        return true;
    }

    /// <summary>
    /// Whether the patient has reached an age bound, read in the band's unit.
    /// <para>
    /// A bound in years is compared in completed years and one in months on the calendar from the
    /// date of birth. Counting days against 365 × n moved a patient into the next band before the
    /// birthday — a day early per leap year lived, twelve days early at fifty — and 30 × n drifts
    /// the same way against real months. A bound in days is compared in days, which is what a
    /// neonatal band means. Without a date of birth the declared age in whole years is all there
    /// is, as before.
    /// </para>
    /// </summary>
    private static bool HasReached(int bound, int unit, ReferenceRangeContext context)
    {
        if (unit == DaysPerYear && context.AgeInYears is { } years)
            return years >= bound / DaysPerYear;

        if (unit == DaysPerMonth && context.DateOfBirth is { } dob && context.ReferenceDate is { } on)
            return dob.AddMonths(bound / DaysPerMonth) <= on;

        if (context.AgeInDays is { } days) return days >= bound;

        return context.AgeInYears is { } declared && declared * DaysPerYear >= bound;
    }

    /// <summary>
    /// How narrowly this range is targeted. Used to break ties when several ranges match, so the
    /// gestational interval wins over the generic female one.
    /// <para>
    /// The weights are powers of two, each worth more than every weight below it combined, so the
    /// order is a strict precedence and not an accident of arithmetic: a trimester-specific range
    /// outranks any pregnancy range, which outranks any age band, which outranks a
    /// method-specific range, which outranks a sex-specific one. A range carrying two of these
    /// still beats one carrying only the lower of the two.
    /// </para>
    /// <para>
    /// Age above sex, which is the way round it was not. A three-day-old's haemoglobin interval is
    /// 14–22 g/dL and an adult male's is 13–17; with sex scoring higher, a newborn boy was resolved
    /// against the adult interval and his physiologically normal 20 g/dL reported as High — and at
    /// 25 as critical, with a telephone call to the paediatrician about a healthy baby. Sex-linked
    /// haemoglobin differences do not appear until puberty, so at three days the age band is not
    /// merely the narrower predicate, it is the only one that means anything.
    /// </para>
    /// </summary>
    public int Specificity =>
        (PregnancyTrimester is not null ? 16 : 0) +
        (AppliesToPregnancy is not null ? 8 : 0) +
        (AgeFromDays is not null || AgeToDays is not null ? 4 : 0) +
        (InstrumentId is not null ? 2 : 0) +
        (AppliesToGender != Gender.Unknown ? 1 : 0);

    /// <summary>
    /// Classifies a numeric value against this interval. A one-sided interval only flags on the
    /// side it defines, which is what "&lt; 200 mg/dL" means.
    /// </summary>
    public ResultInterpretation Interpret(decimal value)
    {
        if (LowerLimit is { } low && value < low) return ResultInterpretation.Low;
        if (UpperLimit is { } high && value > high) return ResultInterpretation.High;
        if (LowerLimit is null && UpperLimit is null) return ResultInterpretation.NotInterpretable;
        return ResultInterpretation.Normal;
    }

    /// <summary>The interval as printed on the report, in the requested language.</summary>
    public string Format(AppLanguage language, int decimals)
    {
        if (DisplayText is not null && !string.IsNullOrWhiteSpace(DisplayText.For(language)))
            return DisplayText.For(language);

        if (!string.IsNullOrWhiteSpace(ExpectedCodedValue))
            return ExpectedCodedValue;

        var format = "N" + decimals;

        // Formatted against the requested language's culture, not the thread's. The interval is
        // printed next to the result on the report, and the two have to agree on the decimal
        // separator — "0,70 - 1,10" beside a result of "1.05" reads as a typesetting fault.
        var culture = language.ToCulture();

        return (LowerLimit, UpperLimit) switch
        {
            ({ } low, { } high) => $"{low.ToString(format, culture)} - {high.ToString(format, culture)}",
            (null, { } high) => $"< {high.ToString(format, culture)}",
            ({ } low, null) => $"> {low.ToString(format, culture)}",
            _ => "-"
        };
    }

    public Result Validate()
    {
        var errors = new List<Error>();

        if (LowerLimit is { } low && UpperLimit is { } high && low > high)
            errors.Add(Error.Validation("Range_LowerAboveUpper"));
        if (AgeFromDays is { } from && AgeToDays is { } to && from >= to)
            errors.Add(Error.Validation("Range_InvalidAgeBand"));
        if (PregnancyTrimester is { } trimester and (< 1 or > 3))
            errors.Add(Error.Validation("Range_InvalidTrimester"));
        if (PregnancyTrimester is not null && AppliesToPregnancy is false)
            errors.Add(Error.Validation("Range_TrimesterContradictsPregnancy"));
        if (AppliesToPregnancy is true && AppliesToGender == Gender.Male)
            errors.Add(Error.Validation("Range_PregnancyRequiresFemale"));
        if (LowerLimit is null && UpperLimit is null &&
            string.IsNullOrWhiteSpace(ExpectedCodedValue) && DisplayText is null)
            errors.Add(Error.Validation("Range_EmptyInterval"));

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    // ---- Convenience builders used heavily by the catalogue seeder -----------------------------

    public static ReferenceRange Numeric(decimal? low, decimal? high,
                                         Gender gender = Gender.Unknown,
                                         int? ageFromDays = null, int? ageToDays = null,
                                         string? source = null) => new(gender)
    {
        LowerLimit = low,
        UpperLimit = high,
        AgeFromDays = ageFromDays,
        AgeToDays = ageToDays,
        Source = source
    };

    public static ReferenceRange Coded(string expected, Gender gender = Gender.Unknown) => new(gender)
    {
        ExpectedCodedValue = expected
    };
}

/// <summary>Where a result sits relative to its reference interval.</summary>
public enum ResultInterpretation
{
    NotInterpretable = 0,
    Normal = 1,
    Low = 2,
    High = 3,
    /// <summary>Below the critical low threshold — panic value.</summary>
    CriticalLow = 4,
    /// <summary>Above the critical high threshold — panic value.</summary>
    CriticalHigh = 5,
    /// <summary>Non-numeric result whose coded value is flagged abnormal.</summary>
    Abnormal = 6
}
