using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Orders;

/// <summary>A profile as the counter offers it in one click, and how often the site ordered it.</summary>
public sealed record FrequentPanel(Guid PanelId, string Code, MultilingualText Name, int OrderCount);

/// <summary>An analysis the patient already had, on which file and when.</summary>
public sealed record RecentAnalysis(Guid AnalysisTestId, DateTimeOffset RegisteredAt, string AccessionNumber);

/// <summary>The reads of the "Nouvelle demande" screen that no other screen makes.</summary>
public interface IOrderEntryQueries
{
    /// <summary>
    /// The profiles the site ordered most since <paramref name="since"/>, most ordered first,
    /// completed with the catalogue's own order when fewer were ordered than asked for.
    /// </summary>
    Task<IReadOnlyList<FrequentPanel>> GetFrequentPanelsAsync(Guid siteId, DateTimeOffset since,
                                                               int take = 5, CancellationToken ct = default);

    /// <summary>
    /// The analyses on the patient's files registered since <paramref name="since"/>, newest first,
    /// cancelled files and cancelled analyses left out.
    /// </summary>
    Task<IReadOnlyList<RecentAnalysis>> GetRecentAnalysesAsync(Guid patientId, DateTimeOffset since,
                                                                CancellationToken ct = default);
}
