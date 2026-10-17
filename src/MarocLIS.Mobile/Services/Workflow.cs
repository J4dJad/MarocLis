using MarocLIS.Api.Contracts;

namespace MarocLIS.Mobile.Services;

/// <summary>What one screen hands to the next, in memory only.</summary>
/// <param name="Outcome">What the server did with the decisions.</param>
/// <param name="Sent">The rows sent for release, to tell a refused one from one never ticked.</param>
/// <param name="At">When the validation was sent, for « Validé par … le … à … ».</param>
public sealed record ValidationRun(Guid OrderId, ValidationOutcomeResponse Outcome, IReadOnlySet<Guid> Sent, DateTimeOffset At);

/// <summary>
/// The thread of a validation round — the worklist that was open, the outcome of the last
/// validation, the report just issued — kept between screens of the same opening.
/// <para>
/// In the page's memory and nowhere else: nothing here reaches the browser's storage, and all of
/// it is gone when the session ends (<see cref="Clear"/>). A screen opened without its thread — a
/// reload on « Validée » — goes back to the file and reads it again from the API.
/// </para>
/// </summary>
public sealed class Workflow
{
    /// <summary>The chip last chosen on the worklist.</summary>
    public string Filter { get; set; } = ApiProtocol.Filters.All;

    /// <summary>The order of the files on the worklist last shown, for « Dossier suivant ».</summary>
    public IReadOnlyList<Guid> WorklistOrder { get; set; } = [];

    /// <summary>The file last opened, for the screens it leads to (history, outcome, issue).</summary>
    public OrderFile? File { get; set; }

    public ValidationRun? LastValidation { get; set; }

    public ReleasedReportResponse? LastReport { get; set; }

    /// <summary>The next file of the worklist after <paramref name="orderId"/>, if any.</summary>
    public Guid? NextAfter(Guid orderId)
    {
        var list = WorklistOrder;
        var index = list.ToList().IndexOf(orderId);
        if (index >= 0 && index + 1 < list.Count) return list[index + 1];
        return list.FirstOrDefault(id => id != orderId) is { } other && other != Guid.Empty ? other : null;
    }

    public void Clear()
    {
        Filter = ApiProtocol.Filters.All;
        WorklistOrder = [];
        File = null;
        LastValidation = null;
        LastReport = null;
    }
}
