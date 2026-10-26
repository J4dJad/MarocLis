namespace MarocLIS.Application.Dashboard;

/// <summary>
/// A single dashboard figure, with the navigation target it stands for: the key of the menu entry
/// whose screen lists what the figure counts.
/// </summary>
public sealed record DashboardMetric(
    string LabelKey,
    int Value,
    DashboardSeverity Severity,
    string? NavigationKey = null,
    string? DetailKey = null);

public enum DashboardSeverity
{
    /// <summary>Informational: a count of normal activity.</summary>
    Neutral = 0,

    /// <summary>Needs attention today.</summary>
    Attention = 1,

    /// <summary>Needs attention now — a breached obligation or an uncommunicated critical result.</summary>
    Urgent = 2
}

public sealed record DashboardSnapshot(
    IReadOnlyList<DashboardMetric> Activity,
    IReadOnlyList<DashboardMetric> Alerts,
    DateTimeOffset GeneratedAt);

public interface IDashboardQueries
{
    Task<DashboardSnapshot> GetSnapshotAsync(Guid siteId, DateTimeOffset now,
                                              CancellationToken ct = default);
}
