using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Samples;

/// <summary>The chips above the patients to draw.</summary>
public enum CollectionFilter
{
    All = 0,
    Urgent = 1,
    Fasting = 2
}

/// <summary>
/// The answer given in the fasting band for the patient at the chair: yes, no, « Ne sait pas »
/// (asked, not known), or nothing pressed yet — the draw then asks, unless the patient declared it.
/// </summary>
public enum FastingAnswer
{
    NotAsked = 0,
    Yes = 1,
    No = 2,
    Unknown = 3
}

/// <summary>Why « Prélever » or « Expédier » records nothing: no tube ticked, or every ticked one already drawn.</summary>
public enum CollectionRefusal
{
    None = 0,
    NothingSelected = 1,
    AlreadyCollected = 2
}

/// <summary>What the action bar of « Prélèvements » offers, by the patient's tubes and the operator's rights.</summary>
public sealed record CollectionActions(bool Record, bool Dispatch, bool Reprint, bool OpenFile)
{
    public static readonly CollectionActions None = new(false, false, false, false);
}

/// <summary>
/// The rules of the « Prélèvements » screen that need no window: which patients a chip keeps and in
/// what order, the tubes of one patient in draw order and which come ticked, what the fasting band
/// answers and records, which typed volume is refused, and what is drawn and sent. The workstation
/// and the web client apply the same ones; the service remains the authority.
/// </summary>
public static class CollectionRules
{
    /// <summary>
    /// The patients' orders, one group per order, under a chip: « Urgents » any priority above
    /// routine (the server's rule), « À jeun requis » an order with a tube that needs fasting.
    /// The most urgent first, then by arrival.
    /// </summary>
    public static IReadOnlyList<IGrouping<Guid, CollectionWorklistEntry>> Orders(
        IEnumerable<CollectionWorklistEntry> entries, CollectionFilter filter) =>
    [
        .. entries
            .GroupBy(e => e.LabOrderId)
            .Where(g => filter switch
            {
                CollectionFilter.Urgent => g.First().Priority != OrderPriority.Routine,
                CollectionFilter.Fasting => g.Max(e => e.RequiredFastingHours) > 0,
                _ => true
            })
            .OrderByDescending(g => g.First().Priority)
            .ThenBy(g => g.First().RegisteredAt)
    ];

    /// <summary>
    /// One patient's tubes, drawn together: selecting any of them selects the set, sorted by the
    /// order of draw rather than alphabetically — additive carry-over between containers drawn out
    /// of order is a real pre-analytical error.
    /// </summary>
    public static IReadOnlyList<CollectionWorklistEntry> DrawSet(IEnumerable<CollectionWorklistEntry> entries, Guid orderId) =>
        [.. entries.Where(e => e.LabOrderId == orderId).OrderBy(e => e.DrawOrder)];

    /// <summary>
    /// Whether a tube comes ticked: the tubes still to draw while any is left, then — once all are
    /// drawn — every tube, ready for « Expédier ».
    /// </summary>
    public static bool IsTickedByDefault(CollectionWorklistEntry entry, IReadOnlyCollection<CollectionWorklistEntry> set) =>
        !set.Any(e => !e.IsCollected) || !entry.IsCollected;

    /// <summary>The tubes of the set still to draw.</summary>
    public static int ToDraw(IEnumerable<CollectionWorklistEntry> set) => set.Count(e => !e.IsCollected);

    /// <summary>The tubes still to draw that are not ticked: « 2 tubes restent à cocher ».</summary>
    public static int RemainingToTick(IEnumerable<CollectionWorklistEntry> set, IReadOnlySet<Guid> ticked) =>
        set.Count(e => !e.IsCollected && !ticked.Contains(e.SampleId));

    /// <summary>The longest fasting any tube of the set requires, in hours; 0 when none does.</summary>
    public static int RequiredFasting(IReadOnlyCollection<CollectionWorklistEntry> set) =>
        set.Count == 0 ? 0 : set.Max(e => e.RequiredFastingHours);

    /// <summary>The analyses that require the fasting, named in the band.</summary>
    public static IReadOnlyList<string> FastingAnalyses(IEnumerable<CollectionWorklistEntry> set) =>
        [.. set.Where(e => e.RequiredFastingHours > 0).SelectMany(e => e.AnalysisCodes).Distinct()];

    /// <summary>
    /// What the band shows as answered: the answer pressed, or the patient's earlier declaration
    /// while nothing is pressed. « Ne sait pas » shows no answer.
    /// </summary>
    public static bool? ShownFasting(FastingAnswer answer, bool? declared) => answer switch
    {
        FastingAnswer.Yes => true,
        FastingAnswer.No => false,
        FastingAnswer.Unknown => null,
        _ => declared
    };

    /// <summary>
    /// Whether the fasting must be asked at the draw: it matters, nothing was pressed in the band
    /// and the patient declared nothing before. Asked before the draw is recorded, because
    /// afterwards the answer cannot honestly be obtained.
    /// </summary>
    public static bool MustAskFasting(FastingAnswer answer, bool? declared, int requiredFasting, int toDraw) =>
        toDraw > 0 && requiredFasting > 0 && answer == FastingAnswer.NotAsked && declared is null;

    /// <summary>
    /// The fasting recorded with the draw: the band's answer, or the one given to the question at
    /// the draw (<paramref name="asked"/>). « Ne sait pas », or a declaration made earlier, records
    /// nothing new.
    /// </summary>
    public static bool? FastingToRecord(FastingAnswer answer, bool? asked, int requiredFasting, int toDraw)
    {
        if (toDraw == 0 || requiredFasting <= 0) return null;

        return answer switch
        {
            FastingAnswer.Yes => true,
            FastingAnswer.No => false,
            FastingAnswer.NotAsked => asked,
            _ => null
        };
    }

    /// <summary>
    /// Whether a typed volume is refused: text that is no number, or a number the tube cannot hold
    /// (nothing, or 5 000 000 typed for 5 mL) — spaces alone included. An empty box records no volume. It used to be
    /// dropped without a word and the tube recorded with no volume.
    /// </summary>
    public static bool IsVolumeRefused(string? typed)
    {
        var volume = TypedNumber.Parse(typed);
        return (!string.IsNullOrEmpty(typed) && volume is null)
               || volume is <= 0m or >= Sample.MaximumVolumeMl;
    }

    /// <summary>The volume a typed box records, once <see cref="IsVolumeRefused"/> let it through.</summary>
    public static decimal? Volume(string? typed) => TypedNumber.Parse(typed);

    /// <summary>
    /// The ticked tubes the action draws first — those not drawn yet — or why nothing is done:
    /// nothing ticked, or « Prélever » on tubes all drawn already (« Expédier » sends them).
    /// </summary>
    public static (IReadOnlyList<Guid> ToDraw, CollectionRefusal Refusal) Plan(
        IReadOnlyCollection<CollectionWorklistEntry> set, IReadOnlyCollection<Guid> ticked, bool dispatch)
    {
        if (ticked.Count == 0) return ([], CollectionRefusal.NothingSelected);

        var collected = set.Where(e => e.IsCollected).Select(e => e.SampleId).ToHashSet();
        var toDraw = ticked.Where(id => !collected.Contains(id)).ToList();

        return !dispatch && toDraw.Count == 0
            ? ([], CollectionRefusal.AlreadyCollected)
            : (toDraw, CollectionRefusal.None);
    }

    /// <summary>
    /// The draws recorded: each tube with its volume, the fasting answered and, when the patient
    /// fasted, the hours required. The place is sent only once the operator chose one — until then
    /// each tube keeps the one its order implied — and an empty comment is no comment.
    /// </summary>
    public static IReadOnlyList<RecordCollectionRequest> Requests(
        IEnumerable<(Guid SampleId, decimal? VolumeMl)> volumes, bool? fasted, int requiredFasting,
        CollectionLocation? chosenLocation, string? comment) =>
    [
        .. volumes.Select(v => new RecordCollectionRequest
        {
            SampleId = v.SampleId,
            VolumeMl = v.VolumeMl,
            PatientFasted = fasted,
            DeclaredFastingHours = fasted == true ? requiredFasting : null,
            Location = chosenLocation,
            Note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()
        })
    ];

    /// <summary>
    /// The action bar: « Prélever » while a tube is left to draw, « Expédier », the labels and the
    /// file once a patient is open — each with its right.
    /// </summary>
    public static CollectionActions Actions(IReadOnlyCollection<CollectionWorklistEntry> set, ICurrentUser user)
    {
        if (set.Count == 0) return CollectionActions.None;

        return new CollectionActions(
            Record: set.Any(e => !e.IsCollected) && user.Has(Permissions.Samples.Collect),
            Dispatch: user.Has(Permissions.Samples.Dispatch),
            Reprint: user.Has(Permissions.Samples.Reprint),
            OpenFile: user.Has(Permissions.Orders.View));
    }

    /// <summary>
    /// The barcode a scan names on the list — equality, not a partial match: a near barcode is
    /// another tube. A tube of another patient brings that patient up first.
    /// </summary>
    public static CollectionWorklistEntry? Scanned(IEnumerable<CollectionWorklistEntry> entries, string? scanned)
    {
        var barcode = scanned?.Trim();
        return string.IsNullOrEmpty(barcode) ? null : entries.FirstOrDefault(e => e.Barcode == barcode);
    }
}
