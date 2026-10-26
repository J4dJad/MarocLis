using MarocLIS.Application.Catalog;
using MarocLIS.Application.Input;

namespace MarocLIS.Application.Orders;

/// <summary>
/// The rules of the "Nouvelle demande" screen that need no window. They live in the application
/// layer so every client that registers an order — the workstation today, the web client next —
/// ranks the catalogue and points out a repeat the same way.
/// </summary>
public static class OrderRegistrationRules
{
    /// <summary>
    /// How far back an analysis already done for the patient is pointed out. No setting per analysis
    /// exists in the catalogue; thirty days is the usual window for a redundant request.
    /// </summary>
    public const int RepeatWindowDays = 30;

    /// <summary>
    /// Whether a save prints the tube labels: « Enregistrer » (F12, Ctrl+S) does, as it always has;
    /// « Enregistrer sans étiquettes » (F8) does not.
    /// </summary>
    public static bool PrintsLabels(bool withoutLabels) => !withoutLabels;

    /// <summary>
    /// The message of a registered order in the action bar: its number, its samples and, when its
    /// labels are not printed by this save, where they will be. Short enough for the two lines the bar
    /// gives it at 1366 px: the reason the browser cannot print them is in the summary that opens
    /// with it, not repeated here.
    /// </summary>
    /// <param name="labelsFromWorkstation">The save asked for the labels, which this client cannot
    /// print (the browser): they are printed from the Windows workstation.</param>
    public static string RegisteredStatus(ScreenText text, string accessionNumber, int samples,
                                          bool printLabels, bool labelsFromWorkstation)
    {
        var message = $"{text("Order.AccessionNumber")} {accessionNumber} — " + text("OrderEntry.SampleCount", samples);
        if (samples == 0) return message;
        if (!printLabels) return message + " — " + text("OrderEntry.LabelsNotPrinted");
        return labelsFromWorkstation ? message + " — " + text("OrderEntry.LabelsAtWorkstation") : message;
    }

    /// <summary>
    /// What Enter adds: an exact code wins ("NFS" is the blood count, not every analysis whose name
    /// holds "nfs"), then a code that starts with what was typed, then the first match by name.
    /// </summary>
    public static CatalogPickerItem? BestMatch(IReadOnlyList<CatalogPickerItem> items, string term) =>
        Ranked(items, term).FirstOrDefault();

    /// <summary>The matches in the order of <see cref="BestMatch"/>, for the suggestion list.</summary>
    public static IEnumerable<CatalogPickerItem> Ranked(IEnumerable<CatalogPickerItem> items, string term) =>
        items.Select((item, index) => (item, index))
             .OrderBy(x => string.Equals(x.item.Code, term, StringComparison.OrdinalIgnoreCase) ? 0
                           : x.item.Code.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 1
                           : 2)
             .ThenBy(x => x.index)
             .Select(x => x.item);

    /// <summary>
    /// The latest time the patient had this analysis — or, for a profile, any of its members —
    /// among <paramref name="recent"/>, or null.
    /// </summary>
    public static (RecentAnalysis Previous, Guid AnalysisId)? RepeatOf(
        Guid itemId, bool isPanel, IReadOnlyCollection<Guid> members, IReadOnlyList<RecentAnalysis> recent)
    {
        IReadOnlyCollection<Guid> ids = isPanel ? members : [itemId];
        var hit = recent.Where(r => ids.Contains(r.AnalysisTestId))
                        .OrderByDescending(r => r.RegisteredAt)
                        .FirstOrDefault();
        return hit is null ? null : (hit, hit.AnalysisTestId);
    }

    /// <summary>"ALAOUI Mohammed" → "AM": the first letter of the first two words.</summary>
    public static string Initials(string fullName) =>
        string.Concat(fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                              .Take(2)
                              .Select(w => char.ToUpperInvariant(w[0])));
}
