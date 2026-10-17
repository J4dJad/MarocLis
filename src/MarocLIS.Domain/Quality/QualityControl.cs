using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Quality;

/// <summary>
/// A lot of control material (matériau de contrôle interne). Lot and expiry are tracked because a
/// control result is only interpretable against the target values of the lot actually used, and
/// because a lot change is the most common cause of an apparent shift in a Levey-Jennings chart.
/// </summary>
public sealed class QcMaterial : AuditableAggregateRoot, ISoftDeletable
{
    private QcMaterial() { }

    public QcMaterial(string lotNumber, MultilingualText name, QcLevel level, DateOnly expiryDate)
    {
        LotNumber = lotNumber;
        Name = name;
        Level = level;
        ExpiryDate = expiryDate;
    }

    public string LotNumber { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();
    public QcLevel Level { get; private set; }

    public string? Manufacturer { get; set; }
    public string? CatalogueReference { get; set; }

    public DateOnly ExpiryDate { get; private set; }

    /// <summary>Date the vial was opened; on-board stability runs from here, not from receipt.</summary>
    public DateOnly? OpenedOn { get; set; }
    public int? StabilityAfterOpeningDays { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public bool IsExpired(DateOnly today) => ExpiryDate < today;

    public bool IsBeyondOpenStability(DateOnly today) =>
        OpenedOn is { } opened && StabilityAfterOpeningDays is { } days &&
        today > opened.AddDays(days);

    public bool IsUsable(DateOnly today) =>
        IsActive && !IsExpired(today) && !IsBeyondOpenStability(today);
}

public enum QcLevel
{
    Low = 0,
    Normal = 1,
    High = 2,
    /// <summary>A fourth level used by some assays.</summary>
    Extra = 3
}

/// <summary>
/// The target mean and dispersion for one control material, one parameter and one instrument.
/// ISO 15189 §5.6 expects the laboratory to establish its own mean and SD from its own data rather
/// than relying only on the manufacturer's insert, so both are held and the laboratory's values
/// take precedence once enough points exist.
/// </summary>
public sealed class QcTarget : AuditableAggregateRoot
{
    private QcTarget() { }

    public QcTarget(Guid qcMaterialId, Guid testParameterId, Guid instrumentId)
    {
        QcMaterialId = qcMaterialId;
        TestParameterId = testParameterId;
        InstrumentId = instrumentId;
    }

    public Guid QcMaterialId { get; private set; }
    public Guid TestParameterId { get; private set; }
    public Guid InstrumentId { get; private set; }

    /// <summary>Mean stated on the manufacturer's insert.</summary>
    public decimal? AssignedMean { get; set; }
    public decimal? AssignedSd { get; set; }

    /// <summary>Mean computed from the laboratory's own accumulated points.</summary>
    public decimal? LaboratoryMean { get; set; }
    public decimal? LaboratorySd { get; set; }
    public int LaboratoryPointCount { get; set; }

    /// <summary>Period the laboratory statistics were computed over.</summary>
    public DateOnly? StatisticsFrom { get; set; }
    public DateOnly? StatisticsTo { get; set; }

    /// <summary>
    /// Minimum number of points before the laboratory's own statistics are trusted. Twenty is the
    /// conventional threshold for establishing a control range.
    /// </summary>
    public const int MinimumPointsForLaboratoryStatistics = 20;

    /// <summary>Mean actually used to compute Z-scores.</summary>
    public decimal? EffectiveMean =>
        LaboratoryPointCount >= MinimumPointsForLaboratoryStatistics ? LaboratoryMean ?? AssignedMean
                                                                     : AssignedMean ?? LaboratoryMean;

    /// <summary>SD actually used to compute Z-scores.</summary>
    public decimal? EffectiveSd =>
        LaboratoryPointCount >= MinimumPointsForLaboratoryStatistics ? LaboratorySd ?? AssignedSd
                                                                     : AssignedSd ?? LaboratorySd;

    /// <summary>Coefficient of variation in percent, the headline precision indicator.</summary>
    public decimal? CvPercent =>
        EffectiveMean is { } mean and not 0m && EffectiveSd is { } sd
            ? Math.Round(sd / Math.Abs(mean) * 100m, 2)
            : null;

    /// <summary>Maximum acceptable CV, from the laboratory's analytical performance specifications.</summary>
    public decimal? MaxAcceptableCvPercent { get; set; }

    public bool IsCvAcceptable =>
        MaxAcceptableCvPercent is not { } maximum || CvPercent is not { } cv || cv <= maximum;

    /// <summary>Westgard rules applied to this target, as a flags value.</summary>
    public WestgardRule ActiveRules { get; set; } =
        WestgardRule.OneThreeS | WestgardRule.TwoTwoS | WestgardRule.R4S |
        WestgardRule.FourOneS | WestgardRule.TenX | WestgardRule.OneTwoS;

    public bool IsActive { get; set; } = true;

    /// <summary>Z-score of a measurement against this target, or null when no target is established.</summary>
    public decimal? ZScore(decimal value) =>
        EffectiveMean is { } mean && EffectiveSd is { } sd && sd != 0m
            ? Math.Round((value - mean) / sd, 4)
            : null;

    /// <summary>
    /// Folds a new accepted point into the running mean and SD using Welford's method, so the
    /// laboratory statistics stay current without re-reading the whole history.
    /// </summary>
    public void AccumulatePoint(decimal value)
    {
        var count = LaboratoryPointCount;
        var mean = LaboratoryMean ?? value;
        var variance = LaboratorySd is { } sd && count > 1 ? sd * sd : 0m;

        var newCount = count + 1;
        var delta = value - mean;
        var newMean = mean + delta / newCount;

        // Sample variance, updated incrementally; needs at least two points to be defined.
        var newVariance = newCount > 1
            ? ((count - 1 > 0 ? variance * (count - 1) : 0m) + delta * (value - newMean)) / (newCount - 1)
            : 0m;

        LaboratoryPointCount = newCount;
        LaboratoryMean = Math.Round(newMean, 6);
        LaboratorySd = newVariance > 0m
            ? Math.Round((decimal)Math.Sqrt((double)newVariance), 6)
            : LaboratorySd;
    }
}

/// <summary>
/// One internal quality control measurement. Results are kept even when rejected — a rejected
/// point is evidence that the laboratory detected a problem, and deleting it would make the
/// Levey-Jennings chart a work of fiction.
/// </summary>
public sealed class QcResult : AuditableAggregateRoot, ISiteScoped
{
    private QcResult() { }

    public QcResult(Guid qcTargetId, Guid testParameterId, Guid instrumentId, Guid siteId,
                    decimal value, DateTimeOffset measuredAt, Guid enteredByUserId)
    {
        QcTargetId = qcTargetId;
        TestParameterId = testParameterId;
        InstrumentId = instrumentId;
        SiteId = siteId;
        Value = value;
        MeasuredAt = measuredAt;
        EnteredByUserId = enteredByUserId;
    }

    public Guid QcTargetId { get; private set; }
    public Guid TestParameterId { get; private set; }
    public Guid InstrumentId { get; private set; }
    public Guid SiteId { get; set; }

    /// <summary>Groups the levels measured together, so multi-level rules can be evaluated per run.</summary>
    public Guid? QcRunId { get; set; }

    public decimal Value { get; private set; }
    public DateTimeOffset MeasuredAt { get; private set; }
    public Guid EnteredByUserId { get; private set; }

    /// <summary>True when the point came from the analyser rather than a keyboard.</summary>
    public bool IsAutoTransmitted { get; set; }

    /// <summary>Target statistics frozen at measurement time, so the chart never shifts retroactively.</summary>
    public decimal? TargetMean { get; set; }
    public decimal? TargetSd { get; set; }

    public decimal? ZScore { get; set; }

    public QcOutcome Outcome { get; private set; } = QcOutcome.Pending;

    /// <summary>Rules the point violated.</summary>
    public WestgardRule ViolatedRules { get; private set; } = WestgardRule.None;

    /// <summary>Reagent and calibration in use, so a rejection points at a cause.</summary>
    public Guid? ReagentLotId { get; set; }

    /// <summary>
    /// Measured on a reagent lot still awaiting release, for its acceptance. Such a point is
    /// evidence about the new lot only: it stays out of the routine Westgard series, out of the
    /// laboratory's own mean and SD, and never flags the patient results, which were all produced
    /// with the lot in use.
    /// </summary>
    public bool IsLotEvaluation { get; set; }
    public Guid? CalibrationRecordId { get; set; }

    // ---- Review -------------------------------------------------------------------------------

    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }

    /// <summary>What the laboratory did about a rejection — required before patient results flow.</summary>
    public string? CorrectiveActionTaken { get; private set; }

    /// <summary>Non-conformity opened as a consequence, when the rejection warranted one.</summary>
    public Guid? NonConformityId { get; set; }

    /// <summary>
    /// Set when the biologist released patient results despite an out-of-control point, with a
    /// documented justification. The exception is recorded, not hidden.
    /// </summary>
    public bool WasOverridden { get; private set; }
    public string? OverrideJustification { get; private set; }

    public string? Comment { get; set; }

    public bool IsAccepted => Outcome == QcOutcome.Accepted;
    public bool IsRejected => Outcome == QcOutcome.Rejected;

    /// <summary>Applies an evaluation to the point.</summary>
    public void ApplyEvaluation(WestgardEvaluation evaluation)
    {
        ViolatedRules = evaluation.ViolatedRules;
        Outcome = evaluation.Outcome;
    }

    public Result Review(DateTimeOffset at, Guid byUserId, string? correctiveAction)
    {
        // A second review replaced the first reviewer and the corrective action on record: the
        // review is a signature, given once.
        if (ReviewedAt is not null)
            return Result.Failure(Error.Conflict("Qc_AlreadyReviewed"));

        if (Outcome == QcOutcome.Rejected && string.IsNullOrWhiteSpace(correctiveAction))
            return Result.Failure(Error.Regulatory("Qc_RejectionRequiresCorrectiveAction"));

        ReviewedAt = at;
        ReviewedByUserId = byUserId;
        CorrectiveActionTaken = correctiveAction;
        return Result.Success();
    }

    public Result Override(string justification)
    {
        if (Outcome != QcOutcome.Rejected)
            return Result.Failure(Error.Conflict("Qc_NotRejected"));
        if (string.IsNullOrWhiteSpace(justification))
            return Result.Failure(Error.Regulatory("Qc_OverrideJustificationRequired"));

        WasOverridden = true;
        OverrideJustification = justification;
        return Result.Success();
    }
}

public enum QcOutcome
{
    /// <summary>Recorded but not yet evaluated.</summary>
    Pending = 0,

    /// <summary>In control.</summary>
    Accepted = 1,

    /// <summary>A warning rule fired; the run is usable but the series needs watching.</summary>
    Warning = 2,

    /// <summary>Out of control. Patient results covered by this run may not be released.</summary>
    Rejected = 3
}
