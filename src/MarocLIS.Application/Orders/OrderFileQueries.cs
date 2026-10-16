namespace MarocLIS.Application.Orders;

/// <summary>What an entry of a file's history records.</summary>
public enum OrderFileEventKind
{
    /// <summary>A change saved with its reason: registration, addition, cancellation, collection…</summary>
    Change = 0,

    /// <summary>A version of the report released to the patient or the prescriber.</summary>
    ReportReleased = 1
}

/// <summary>
/// One dated line of a file's history: who did what to it, as the audit journal recorded it, or a
/// release of its report.
/// </summary>
/// <param name="Description">The reason saved with the change; null for a report release.</param>
/// <param name="ReportVersion">The version released; null for a change.</param>
public sealed record OrderFileEvent(
    DateTimeOffset OccurredAt,
    OrderFileEventKind Kind,
    string? UserName,
    string? Description,
    int? ReportVersion);

/// <summary>The reads of the "Consultation d'un dossier" screen that no other screen makes.</summary>
public interface IOrderFileQueries
{
    /// <summary>How many lines a file's history shows at most, the most recent kept.</summary>
    const int HistoryLimit = 300;

    /// <summary>
    /// The history of one file of the given site, oldest first: every change saved with a reason
    /// on the file, its analyses, its tubes or its results — one line per save, however many rows
    /// it touched — and every release of its report. Empty for a file of another site.
    /// </summary>
    Task<IReadOnlyList<OrderFileEvent>> GetHistoryAsync(Guid orderId, Guid siteId,
                                                         CancellationToken ct = default);
}
