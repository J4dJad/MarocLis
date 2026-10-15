using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Equipment;

/// <summary>
/// A contract that places one or more analysers in the laboratory — mise à disposition (placement),
/// comodat, lease or purchase with a reagent commitment.
/// <para>
/// In a placement or a comodat the supplier does not sell the analyser: it lends it against a
/// commitment to buy its reagents (the captive reagents), expressed as a number of tests or as a
/// minimum purchase amount, per year or over the whole term. Falling short exposes the laboratory
/// to the penalty the contract sets. What the laboratory needs from the software is to see, early
/// enough to act, whether it is on course: committed against consumed, pro rata of the time elapsed,
/// projected to the end of the period.
/// </para>
/// </summary>
public sealed class InstrumentContract : AuditableAggregateRoot, ISiteScoped
{
    private readonly List<InstrumentContractInstrument> _instruments = [];
    private readonly List<InstrumentContractReagent> _reagents = [];

    private InstrumentContract() { }

    public InstrumentContract(string reference, Guid supplierId, Guid siteId, InstrumentContractKind kind,
                              DateOnly startDate, int durationMonths)
    {
        Reference = reference.Trim();
        SupplierId = supplierId;
        SiteId = siteId;
        Kind = kind;
        StartDate = startDate;
        DurationMonths = durationMonths;
    }

    /// <summary>The contract's reference, as written on it.</summary>
    public string Reference { get; set; } = string.Empty;

    public Guid SupplierId { get; set; }
    public Guid SiteId { get; set; }

    public InstrumentContractKind Kind { get; set; }

    public DateOnly StartDate { get; set; }
    public int DurationMonths { get; set; }

    /// <summary>Last day of the contract.</summary>
    public DateOnly EndDate => StartDate.AddMonths(DurationMonths).AddDays(-1);

    // ---- Commitment ---------------------------------------------------------------------------

    public CommitmentBasis CommitmentBasis { get; set; }

    /// <summary>Number of tests, or amount in dirhams excluding VAT, according to the basis.</summary>
    public decimal CommittedVolume { get; set; }

    /// <summary>
    /// Codes of the analyses the commitment counts, separated by commas or semicolons. Empty: every
    /// result produced on the contract's analysers counts.
    /// </summary>
    public string? CountedAnalysisCodesCsv { get; set; }

    // ---- Maintenance and other terms ------------------------------------------------------------

    /// <summary>Preventive maintenance visits per year the supplier owes.</summary>
    public int? PreventiveVisitsPerYear { get; set; }

    /// <summary>Longest delay, in hours, between a call and the technician's arrival.</summary>
    public int? MaxInterventionDelayHours { get; set; }

    public string? MaintenanceTerms { get; set; }
    public string? TrainingTerms { get; set; }
    public string? ExitConditions { get; set; }
    public string? PenaltyTerms { get; set; }
    public decimal? PenaltyAmountMad { get; set; }

    public string? Notes { get; set; }

    /// <summary>Cleared when the contract is terminated before its term.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The day the contract was terminated before its term (<see cref="IsActive"/> cleared).</summary>
    public DateOnly? TerminatedOn { get; set; }

    /// <summary>
    /// The last day the contract covered: its term, or the day it was terminated. A contract
    /// deactivated before the termination day was kept counts from its last modification.
    /// </summary>
    public DateOnly EffectiveEndDate
    {
        get
        {
            if (IsActive) return EndDate;
            var terminated = TerminatedOn ?? DateOnly.FromDateTime((ModifiedAt ?? CreatedAt).DateTime);
            return terminated < EndDate ? terminated : EndDate;
        }
    }

    public IReadOnlyCollection<InstrumentContractInstrument> Instruments => _instruments.AsReadOnly();
    public IReadOnlyCollection<InstrumentContractReagent> Reagents => _reagents.AsReadOnly();

    /// <summary>In force on a day: active, and the day within its term.</summary>
    public bool IsInForce(DateOnly day) => IsActive && day >= StartDate && day <= EndDate;

    /// <summary>Days left until the end, negative once it has passed.</summary>
    public int DaysToEnd(DateOnly today) => EndDate.DayNumber - today.DayNumber;

    /// <summary>The end is near enough to renegotiate: <see cref="EndingSoonDays"/> days or fewer.</summary>
    public bool IsEndingSoon(DateOnly today) =>
        IsActive && DaysToEnd(today) is >= 0 and <= EndingSoonDays;

    public const int EndingSoonDays = 90;

    public IReadOnlyList<string> CountedAnalysisCodes =>
        string.IsNullOrWhiteSpace(CountedAnalysisCodesCsv)
            ? []
            : [.. CountedAnalysisCodesCsv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .Select(c => c.ToUpperInvariant()).Distinct()];

    public Result Validate()
    {
        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(Reference)) errors.Add(Error.Validation("Contract_ReferenceRequired"));
        if (SupplierId == Guid.Empty) errors.Add(Error.Validation("Contract_SupplierRequired"));
        if (DurationMonths is <= 0 or > 240) errors.Add(Error.Validation("Contract_DurationOutOfRange"));
        if (CommittedVolume < 0m) errors.Add(Error.Validation("Contract_CommitmentNegative"));
        if (CommitmentBasis != CommitmentBasis.None && CommittedVolume == 0m)
            errors.Add(Error.Validation("Contract_CommitmentRequired"));
        if (MaxInterventionDelayHours is < 0 || PreventiveVisitsPerYear is < 0 || PenaltyAmountMad is < 0m)
            errors.Add(Error.Validation("Contract_NegativeTerm"));
        if (_reagents.Any(r => r.ContractPriceMad is < 0m || r.CostPerTestMad is < 0m))
            errors.Add(Error.Validation("Po_PriceMustNotBeNegative"));
        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    /// <summary>Replaces the analysers the contract covers.</summary>
    public void SetInstruments(IEnumerable<Guid> instrumentIds)
    {
        var wanted = instrumentIds.Distinct().ToHashSet();
        _instruments.RemoveAll(i => !wanted.Contains(i.InstrumentId));

        foreach (var id in wanted.Where(id => _instruments.All(i => i.InstrumentId != id)))
        {
            var link = new InstrumentContractInstrument(id);
            link.AttachTo(Id);
            _instruments.Add(link);
        }
    }

    /// <summary>
    /// Replaces the captive reagents and their contracted prices. A reagent kept from the previous
    /// version keeps its row, so its history in the audit trail reads as a price change.
    /// </summary>
    public void SetReagents(IEnumerable<InstrumentContractReagentInput> reagents)
    {
        var wanted = reagents.GroupBy(r => r.StockItemId).Select(g => g.Last()).ToList();
        _reagents.RemoveAll(r => wanted.All(w => w.StockItemId != r.StockItemId));

        foreach (var input in wanted)
        {
            var existing = _reagents.FirstOrDefault(r => r.StockItemId == input.StockItemId);
            if (existing is null)
            {
                existing = new InstrumentContractReagent(input.StockItemId);
                existing.AttachTo(Id);
                _reagents.Add(existing);
            }

            existing.ContractPriceMad = input.ContractPriceMad is { } price ? Money.Round(price) : null;
            existing.CostPerTestMad = input.CostPerTestMad is { } cost ? Money.Round(cost) : null;
        }
    }
}

public enum InstrumentContractKind
{
    /// <summary>Mise à disposition against a reagent commitment.</summary>
    Placement = 0,
    /// <summary>Prêt à usage (comodat), free of charge, usually tied to reagent purchases.</summary>
    Comodat = 1,
    /// <summary>Purchase of the analyser, with or without a reagent commitment.</summary>
    Purchase = 2,
    /// <summary>Location / leasing.</summary>
    Lease = 3
}

/// <summary>What the commitment is counted in, and over which period.</summary>
public enum CommitmentBasis
{
    None = 0,
    /// <summary>A number of tests each contract year.</summary>
    TestsPerYear = 1,
    /// <summary>A number of tests over the whole term.</summary>
    TestsTotal = 2,
    /// <summary>A minimum amount (MAD excluding VAT) of captive reagents bought each contract year.</summary>
    PurchaseAmountPerYear = 3,
    /// <summary>A minimum amount (MAD excluding VAT) of captive reagents bought over the whole term.</summary>
    PurchaseAmountTotal = 4
}

/// <summary>An analyser covered by a contract.</summary>
public sealed class InstrumentContractInstrument : Entity
{
    private InstrumentContractInstrument() { }

    internal InstrumentContractInstrument(Guid instrumentId) => InstrumentId = instrumentId;

    public Guid InstrumentContractId { get; private set; }
    internal void AttachTo(Guid contractId) => InstrumentContractId = contractId;

    public Guid InstrumentId { get; private set; }
}

/// <summary>A captive reagent: bought from the contract's supplier at the contracted price.</summary>
public sealed class InstrumentContractReagent : Entity
{
    private InstrumentContractReagent() { }

    internal InstrumentContractReagent(Guid stockItemId) => StockItemId = stockItemId;

    public Guid InstrumentContractId { get; private set; }
    internal void AttachTo(Guid contractId) => InstrumentContractId = contractId;

    public Guid StockItemId { get; private set; }

    /// <summary>Contracted price per stock unit, excluding VAT: what a purchase-order line starts from.</summary>
    public decimal? ContractPriceMad { get; set; }

    /// <summary>Contracted cost per test, where the contract states one.</summary>
    public decimal? CostPerTestMad { get; set; }
}

public sealed record InstrumentContractReagentInput(Guid StockItemId, decimal? ContractPriceMad,
                                                    decimal? CostPerTestMad);

/// <summary>Where a contract stands against its commitment on a given day.</summary>
/// <param name="ExpectedToDate">The commitment pro rata of the time elapsed in the period.</param>
/// <param name="Projected">Consumption extrapolated linearly to the end of the period.</param>
/// <param name="ProjectedRatio">Projected over committed; 1 means exactly on target.</param>
public sealed record CommitmentStatus(
    CommitmentBasis Basis,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Committed,
    decimal ExpectedToDate,
    decimal Consumed,
    decimal Projected,
    decimal ElapsedFraction,
    decimal? ProjectedRatio,
    bool AtRisk);

/// <summary>
/// Committed against consumed.
/// <para>
/// The alert threshold is <see cref="RiskThreshold"/> (90 %): a projection below nine tenths of the
/// commitment leaves too little room to catch up by the end of the period, and is the point at which
/// the laboratory should talk to the supplier rather than discover the penalty at the annual review.
/// The projection is linear — the volume of a laboratory is steady enough over a year for that to
/// be the honest estimate — and it is not raised in the first <see cref="MinimumElapsedDays"/> days of
/// a period, when a week's activity says nothing about a year's.
/// </para>
/// </summary>
public static class ContractCommitment
{
    public const decimal RiskThreshold = 0.90m;
    public const int MinimumElapsedDays = 30;

    /// <summary>
    /// The period the commitment is measured over on <paramref name="today"/>: the current contract
    /// year for a yearly commitment, the whole term otherwise. The last contract year is cut at the
    /// end of the contract.
    /// </summary>
    public static (DateOnly Start, DateOnly End) Period(InstrumentContract contract, DateOnly today)
    {
        if (contract.CommitmentBasis is not (CommitmentBasis.TestsPerYear or CommitmentBasis.PurchaseAmountPerYear))
            return (contract.StartDate, contract.EndDate);

        var day = today < contract.StartDate ? contract.StartDate
                : today > contract.EndDate ? contract.EndDate
                : today;

        var years = 0;
        while (contract.StartDate.AddYears(years + 1) <= day) years++;

        var start = contract.StartDate.AddYears(years);
        var end = start.AddYears(1).AddDays(-1);
        return (start, end > contract.EndDate ? contract.EndDate : end);
    }

    public static bool CountsTests(CommitmentBasis basis) =>
        basis is CommitmentBasis.TestsPerYear or CommitmentBasis.TestsTotal;

    public static bool CountsPurchases(CommitmentBasis basis) =>
        basis is CommitmentBasis.PurchaseAmountPerYear or CommitmentBasis.PurchaseAmountTotal;

    /// <param name="consumed">Tests produced or amount bought within <see cref="Period"/>, up to today.</param>
    public static CommitmentStatus Evaluate(InstrumentContract contract, DateOnly today, decimal consumed)
    {
        var (start, end) = Period(contract, today);

        var totalDays = end.DayNumber - start.DayNumber + 1;
        var elapsedDays = Math.Clamp(today.DayNumber - start.DayNumber + 1, 0, totalDays);
        var fraction = totalDays <= 0 ? 1m : (decimal)elapsedDays / totalDays;

        // A yearly commitment over a last contract year cut short is owed pro rata of that year.
        var committed = contract.CommittedVolume;
        if (contract.CommitmentBasis is CommitmentBasis.TestsPerYear or CommitmentBasis.PurchaseAmountPerYear)
        {
            var fullYear = start.AddYears(1).DayNumber - start.DayNumber;
            if (totalDays < fullYear) committed = Math.Round(committed * totalDays / fullYear, 2);
        }

        var expected = Math.Round(committed * fraction, 2);
        var projected = fraction > 0m ? Math.Round(consumed / fraction, 2) : 0m;
        decimal? ratio = committed > 0m && fraction > 0m ? Math.Round(projected / committed, 4) : null;

        var atRisk = contract.CommitmentBasis != CommitmentBasis.None
                     && contract.IsActive
                     && today >= start && today <= end
                     && elapsedDays >= Math.Min(MinimumElapsedDays, totalDays)
                     && ratio is { } r && r < RiskThreshold;

        return new CommitmentStatus(contract.CommitmentBasis, start, end, committed, expected, consumed,
                                    projected, Math.Round(fraction, 4), ratio, atRisk);
    }

    /// <summary>
    /// Hours between the call to the supplier and the technician's arrival, when both were recorded.
    /// </summary>
    public static decimal? InterventionDelayHours(MaintenanceRecord record) =>
        record.RequestedAt is { } requested && record.ArrivedAt is { } arrived && arrived >= requested
            ? Math.Round((decimal)(arrived - requested).TotalHours, 1)
            : null;

    /// <summary>Whether an intervention arrived later than the contract allows.</summary>
    public static bool IsDelayExceeded(InstrumentContract contract, MaintenanceRecord record) =>
        contract.MaxInterventionDelayHours is { } max
        && InterventionDelayHours(record) is { } delay
        && delay > max;
}
