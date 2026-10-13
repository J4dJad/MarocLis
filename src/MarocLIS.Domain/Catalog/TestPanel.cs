using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Catalog;

/// <summary>
/// A named bundle of analyses ordered as one line (un profil / un bilan): bilan lipidique,
/// bilan prénatal, bilan préopératoire. Expanded into individual order items at registration so
/// that pricing, worklists and validation stay per-analysis.
/// </summary>
public sealed class TestPanel : AuditableAggregateRoot, ISoftDeletable
{
    private readonly List<TestPanelItem> _items = [];

    private TestPanel() { }

    public TestPanel(string code, MultilingualText name)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name;
    }

    public string Code { get; private set; } = string.Empty;
    public MultilingualText Name { get; set; } = new();
    public string? SearchAliases { get; set; }

    /// <summary>
    /// Fixed price for the bundle, when the laboratory sells it at a discount rather than as the
    /// sum of its parts. Null means "price the components individually".
    /// </summary>
    public decimal? PackagePrice { get; set; }

    /// <summary>
    /// Whether the components stay visible as separate lines on the invoice. AMO claims require
    /// each act to appear with its own code, so this is true for reimbursable panels.
    /// </summary>
    public bool ItemizeOnInvoice { get; set; } = true;

    /// <summary>Printed as a single grouped section on the report instead of per-discipline.</summary>
    public bool GroupOnReport { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public IReadOnlyCollection<TestPanelItem> Items => _items.AsReadOnly();

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }

    public void AddAnalysis(Guid analysisTestId, bool isOptional = false)
    {
        if (_items.Any(i => i.AnalysisTestId == analysisTestId)) return;
        _items.Add(new TestPanelItem(Id, analysisTestId)
        {
            IsOptional = isOptional,
            DisplayOrder = _items.Count == 0 ? 1 : _items.Max(i => i.DisplayOrder) + 1
        });
    }

    public void RemoveAnalysis(Guid analysisTestId)
    {
        var item = _items.FirstOrDefault(i => i.AnalysisTestId == analysisTestId);
        if (item is not null) _items.Remove(item);
    }

    /// <summary>The analyses added to an order when the panel is selected, optional ones excluded.</summary>
    public IReadOnlyList<Guid> MandatoryAnalysisIds =>
        [.. _items.Where(i => !i.IsOptional).OrderBy(i => i.DisplayOrder).Select(i => i.AnalysisTestId)];

    public Result Validate()
    {
        var errors = new List<Error>();
        if (string.IsNullOrWhiteSpace(Name.Fr))
            errors.Add(Error.Validation("Panel_NameFrenchRequired"));
        if (_items.Count == 0)
            errors.Add(Error.Validation("Panel_AtLeastOneAnalysisRequired"));
        if (PackagePrice is < 0m)
            errors.Add(Error.Validation("Panel_NegativePrice"));
        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }
}

public sealed class TestPanelItem : Entity
{
    private TestPanelItem() { }

    public TestPanelItem(Guid testPanelId, Guid analysisTestId)
    {
        TestPanelId = testPanelId;
        AnalysisTestId = analysisTestId;
    }

    public Guid TestPanelId { get; private set; }
    public Guid AnalysisTestId { get; private set; }
    public AnalysisTest? AnalysisTest { get; private set; }

    /// <summary>Proposed but not added automatically — the operator ticks it if wanted.</summary>
    public bool IsOptional { get; set; }

    public int DisplayOrder { get; set; }
}

/// <summary>
/// A rule that automatically adds a follow-up analysis when a result meets a condition
/// (analyse en cascade / reflex testing): a positive HBs antigen triggers a confirmation, a high
/// TSH triggers a free T4. Configuring this in the catalogue rather than in code is what keeps the
/// system extensible for a biologist without a developer.
/// </summary>
public sealed class ReflexRule : AuditableAggregateRoot
{
    private ReflexRule() { }

    public ReflexRule(Guid triggerParameterId, Guid analysisToAddId, MultilingualText description)
    {
        TriggerParameterId = triggerParameterId;
        AnalysisToAddId = analysisToAddId;
        Description = description;
    }

    public Guid TriggerParameterId { get; private set; }
    public Guid AnalysisToAddId { get; private set; }
    public MultilingualText Description { get; set; } = new();

    public ReflexCondition Condition { get; set; } = ReflexCondition.GreaterThan;

    /// <summary>Threshold for a numeric condition.</summary>
    public decimal? Threshold { get; set; }
    public decimal? ThresholdUpper { get; set; }

    /// <summary>Coded value that fires the rule, for non-numeric parameters.</summary>
    public string? TriggerCodedValue { get; set; }

    /// <summary>
    /// When true the follow-up is added silently; when false the biologist is asked to confirm.
    /// Reflex acts are billable, so silent addition needs an explicit policy decision.
    /// </summary>
    public bool AddAutomatically { get; set; }

    /// <summary>Whether the reflex act is charged to the patient or absorbed by the laboratory.</summary>
    public bool IsBillable { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public bool Fires(decimal? numericValue, string? codedValue) => Condition switch
    {
        ReflexCondition.GreaterThan => numericValue is { } v && Threshold is { } t && v > t,
        ReflexCondition.LessThan => numericValue is { } v && Threshold is { } t && v < t,
        ReflexCondition.Outside => numericValue is { } v && Threshold is { } low && ThresholdUpper is { } high
                                   && (v < low || v > high),
        ReflexCondition.Between => numericValue is { } v && Threshold is { } low && ThresholdUpper is { } high
                                   && v >= low && v <= high,
        ReflexCondition.EqualsCoded => codedValue is not null && TriggerCodedValue is not null
                                       && string.Equals(codedValue, TriggerCodedValue, StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}

public enum ReflexCondition
{
    GreaterThan = 0,
    LessThan = 1,
    Outside = 2,
    Between = 3,
    EqualsCoded = 4
}
