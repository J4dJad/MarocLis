using MarocLIS.Application.Catalog;
using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Orders;

/// <summary>What happened to an analysis or a profile the clerk chose.</summary>
public enum BasketAddOutcome
{
    /// <summary>It went into the request.</summary>
    Added = 0,

    /// <summary>A profile went in and took out the analyses chosen on their own that it holds.</summary>
    MergedIntoPanel,

    /// <summary>Refused: it is already in the request.</summary>
    AlreadyPresent,

    /// <summary>Refused: a profile of the request already holds this analysis.</summary>
    AlreadyInPanel
}

/// <summary>The answer to <see cref="OrderBasket.Add"/>.</summary>
/// <param name="CoveringPanel">The profile that already holds the analysis (<see cref="BasketAddOutcome.AlreadyInPanel"/>).</param>
/// <param name="Absorbed">The analyses the profile took out (<see cref="BasketAddOutcome.MergedIntoPanel"/>).</param>
public sealed record BasketAddResult(
    BasketAddOutcome Outcome,
    CatalogPickerItem? CoveringPanel = null,
    IReadOnlyList<CatalogPickerItem>? Absorbed = null)
{
    public bool WentIn => Outcome is BasketAddOutcome.Added or BasketAddOutcome.MergedIntoPanel;
}

/// <summary>Where a catalogue line stands against the request.</summary>
public enum CatalogItemState
{
    Free = 0,
    InRequest,
    InPanel
}

/// <summary>An analysis of the request the patient already had within the repeat window.</summary>
/// <param name="MemberCode">For a profile, the code of the member already done; null for an analysis.</param>
public sealed record BasketRepeat(CatalogPickerItem Item, RecentAnalysis Previous, string? MemberCode);

/// <summary>
/// The request the counter is composing: the analyses and profiles chosen, the repeats the clerk
/// chose to keep, and the patient's recent analyses they are checked against.
/// <para>
/// The rules of « Nouvelle demande » that hold the request — an analysis once, a profile taking out
/// its own members chosen alone, the shared analyses billed once, the reminder of a repeat — live
/// here so the workstation and the web compose a request the same way.
/// </para>
/// </summary>
public sealed class OrderBasket
{
    /// <summary>The catalogue filter showing every line.</summary>
    public const string FilterAll = "*";

    /// <summary>The catalogue filter showing the profiles only.</summary>
    public const string FilterPanels = "#panels";

    private readonly List<CatalogPickerItem> _items = [];
    private readonly HashSet<Guid> _kept = [];
    private IReadOnlyList<RecentAnalysis> _recent = [];

    public IReadOnlyList<CatalogPickerItem> Items => _items;

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>The analyses chosen on their own, as the registration takes them.</summary>
    public IReadOnlyList<Guid> AnalysisTestIds => [.. _items.Where(s => !s.IsPanel).Select(s => s.Id)];

    /// <summary>The profiles chosen, as the registration takes them.</summary>
    public IReadOnlyList<Guid> TestPanelIds => [.. _items.Where(s => s.IsPanel).Select(s => s.Id)];

    /// <summary>The longest fast any line asks for, 0 when none does.</summary>
    public int MaxFastingHours => _items.Select(s => s.FastingHours ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>Whether a line asks for the patient's consent.</summary>
    public bool RequiresConsent => _items.Any(s => s.ConsentRequirement != ConsentRequirement.None);

    /// <summary>The analyses a profile holds.</summary>
    public static HashSet<Guid> Members(CatalogPickerItem panel) =>
        [.. (panel.Components ?? []).Select(c => c.AnalysisTestId)];

    public bool Contains(CatalogPickerItem item) =>
        _items.Any(s => s.Id == item.Id && s.IsPanel == item.IsPanel);

    /// <summary>
    /// Adds a line. The order holds each analysis once, however many times it was chosen: an
    /// analysis already inside a chosen profile is not added again, and choosing a profile takes out
    /// the analyses chosen on their own that it contains — otherwise the list showed them twice and
    /// the counter total ran above the amount registered and invoiced.
    /// </summary>
    public BasketAddResult Add(CatalogPickerItem item)
    {
        if (Contains(item)) return new BasketAddResult(BasketAddOutcome.AlreadyPresent);

        if (!item.IsPanel && CoveringPanel(item) is { } covering)
            return new BasketAddResult(BasketAddOutcome.AlreadyInPanel, CoveringPanel: covering);

        List<CatalogPickerItem> absorbed = [];
        if (item.IsPanel)
        {
            var members = Members(item);
            absorbed = _items.Where(s => !s.IsPanel && members.Contains(s.Id)).ToList();
            foreach (var analysis in absorbed) _items.Remove(analysis);
        }

        _items.Add(item);

        return absorbed.Count > 0
            ? new BasketAddResult(BasketAddOutcome.MergedIntoPanel, Absorbed: absorbed)
            : new BasketAddResult(BasketAddOutcome.Added);
    }

    public void Remove(CatalogPickerItem item)
    {
        _items.RemoveAll(s => s.Id == item.Id && s.IsPanel == item.IsPanel);
        _kept.Remove(item.Id);
    }

    public void Clear()
    {
        _items.Clear();
        _kept.Clear();
    }

    /// <summary>The clerk keeps a repeated analysis: its reminder is no longer shown.</summary>
    public void Keep(CatalogPickerItem item) => _kept.Add(item.Id);

    public bool IsKept(CatalogPickerItem item) => _kept.Contains(item.Id);

    /// <summary>
    /// The patient's analyses of the repeat window (<see cref="OrderRegistrationRules.RepeatWindowDays"/>).
    /// A new patient has none yet, and the repeats kept for the previous one no longer apply.
    /// </summary>
    public void SetRecent(IReadOnlyList<RecentAnalysis> recent) => _recent = recent;

    /// <summary>Forgets the patient's analyses and the repeats kept: the patient was changed.</summary>
    public void ForgetPatient()
    {
        _recent = [];
        _kept.Clear();
    }

    /// <summary>The profile of the request that already holds this analysis, or null.</summary>
    public CatalogPickerItem? CoveringPanel(CatalogPickerItem analysis) =>
        analysis.IsPanel ? null : _items.FirstOrDefault(s => s.IsPanel && Members(s).Contains(analysis.Id));

    public CatalogItemState StateOf(CatalogPickerItem item)
    {
        if (Contains(item)) return CatalogItemState.InRequest;
        if (CoveringPanel(item) is not null) return CatalogItemState.InPanel;
        return CatalogItemState.Free;
    }

    /// <summary>For a profile not yet chosen, the codes of its members already chosen on their own.</summary>
    public IReadOnlyList<string> ChosenMembersOf(CatalogPickerItem panel) =>
        panel.IsPanel
            ? [.. _items.Where(s => !s.IsPanel && Members(panel).Contains(s.Id)).Select(s => s.Code)]
            : [];

    /// <summary>
    /// How many of a line's analyses an earlier line of the request already holds: two profiles can
    /// share members, and the shared ones are billed once, on the first.
    /// </summary>
    public int SharedCount(CatalogPickerItem item)
    {
        var counted = new HashSet<Guid>();
        foreach (var other in _items.TakeWhile(s => s != item))
            foreach (var id in other.IsPanel ? Members(other) : [other.Id]) counted.Add(id);
        return (item.IsPanel ? Members(item) : [item.Id]).Count(id => counted.Contains(id));
    }

    /// <summary>
    /// The reminder of a repeat on a line of the request, null when there is none or the clerk kept
    /// it. For a profile, the member already done is named by its code from <paramref name="catalogue"/>.
    /// </summary>
    public BasketRepeat? RepeatOf(CatalogPickerItem item, IEnumerable<CatalogPickerItem> catalogue)
    {
        if (_kept.Contains(item.Id)) return null;
        if (OrderRegistrationRules.RepeatOf(item.Id, item.IsPanel, Members(item), _recent) is not { } repeat)
            return null;

        var member = item.IsPanel
            ? catalogue.FirstOrDefault(i => !i.IsPanel && i.Id == repeat.AnalysisId)?.Code ?? item.Code
            : null;

        return new BasketRepeat(item, repeat.Previous, member);
    }

    /// <summary>The catalogue lines a filter chip shows: every line, the profiles, or one department's.</summary>
    public static IEnumerable<CatalogPickerItem> Filter(IEnumerable<CatalogPickerItem> items, string filter) => filter switch
    {
        FilterAll => items,
        FilterPanels => items.Where(i => i.IsPanel),
        _ => items.Where(i => i.DepartmentCode == filter)
    };

    /// <summary>
    /// What the bar says is still missing, as text keys: the patient, the analyses, and the
    /// prescriber when the origin requires one (<see cref="LabOrder.RequiresPrescriber"/>).
    /// </summary>
    public static IReadOnlyList<string> MissingKeys(bool hasPatient, bool hasAnalyses, bool prescriberMissing)
    {
        var missing = new List<string>();
        if (!hasPatient) missing.Add("OrderEntry.MissingPatient");
        if (!hasAnalyses) missing.Add("OrderEntry.MissingAnalyses");
        if (prescriberMissing) missing.Add("OrderEntry.MissingPrescriber");
        return missing;
    }

    /// <summary>
    /// Whether the request lacks the prescriber its origin requires: none chosen from the list and
    /// no name typed.
    /// </summary>
    public static bool PrescriberMissing(bool chosenFromList, string? typed, OrderOrigin origin) =>
        !chosenFromList && (typed?.Trim().Length ?? 0) == 0 && LabOrder.RequiresPrescriber(origin);

    /// <summary>
    /// The doctor of the list the clerk means: the one picked, or the one whose name was typed out
    /// in full (case ignored) rather than picked.
    /// </summary>
    public static T? MatchPrescriber<T>(IEnumerable<T> choices, Func<T, string> display, string? typed)
        where T : class
    {
        var text = typed?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;
        return choices.FirstOrDefault(c => string.Equals(display(c), text, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Whether leaving the entry loses something: a patient, a line, clinical information or a
    /// prescriber typed. Leaving asks first only then.
    /// </summary>
    public static bool HasEntries(bool hasPatient, int lines, string? clinicalInformation, string? prescriberText) =>
        hasPatient || lines > 0 || (clinicalInformation?.Length ?? 0) > 0 || (prescriberText?.Trim().Length ?? 0) > 0;

    /// <summary>The label copies of each tube, as the printer settings allow them: one to five.</summary>
    public static int LabelCopies(int configured) => Math.Clamp(configured, 1, 5);
}
