using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Orders;

// The progress line of the "Consultation d'un dossier" screen, worked out without a window. It
// lives in the application layer so every client that shows a file reads its progress the same way.

/// <summary>The six stages a file goes through, in order, as its progress line shows them.</summary>
public enum FileStage { Registered, Collected, Received, Results, Validated, Reported }

/// <summary>Where a stage stands: reached, the one the file is waiting on, or still ahead.</summary>
public enum FileStageState { Done, Current, Ahead }

/// <summary>
/// One stage of the progress line: its state, when it was reached when that is known, and how
/// far along it is (tubes collected or received, values entered, analyses validated).
/// </summary>
public sealed record FileStep(FileStage Stage, FileStageState State, DateTimeOffset? At, int Done, int Total);

/// <summary>
/// Works out the progress line from the file as loaded: its tubes, its analyses and their values,
/// and when its report was last released. Nothing is stored for it; it reads what the file says.
/// </summary>
public static class FileProgress
{
    public static IReadOnlyList<FileStep> Compute(OrderDetail detail, DateTimeOffset? reportReleasedAt)
    {
        var items = detail.Items.Where(i => !i.IsCancelled).ToList();
        var tubes = detail.Samples
            .Where(s => s.Status is not (SampleStatus.Rejected or SampleStatus.Discarded))
            .ToList();

        var collected = tubes.Count(s => s.CollectedAt is not null);
        var received = tubes.Count(s => s.ReceivedAt is not null);
        var values = items.Sum(i => i.ResultCount);
        var entered = items.Sum(i => Math.Min(i.EnteredResultCount, i.ResultCount));
        var validated = items.Count(i => i.Status is LabOrderItemStatus.Validated or LabOrderItemStatus.Reported);

        // An analysis past entry counts as entered even when it has no value of its own to count
        // (a profile heading, a sub-contracted analysis returned on paper).
        var resultsComplete = items.Count > 0
                              && items.All(i => i.Status >= LabOrderItemStatus.ResultEntered
                                                || (i.ResultCount > 0 && i.EnteredResultCount >= i.ResultCount));
        var validatedComplete = items.Count > 0 && validated == items.Count;

        var raw = new (FileStage Stage, bool Complete, DateTimeOffset? At, int Done, int Total)[]
        {
            (FileStage.Registered, true, detail.RegisteredAt, 0, 0),
            (FileStage.Collected, collected == tubes.Count, collected == tubes.Count ? Latest(tubes.Select(s => s.CollectedAt)) : null,
             collected, tubes.Count),
            (FileStage.Received, received == tubes.Count, received == tubes.Count ? Latest(tubes.Select(s => s.ReceivedAt)) : null,
             received, tubes.Count),
            (FileStage.Results, resultsComplete, null, entered, values),
            (FileStage.Validated, validatedComplete, validatedComplete ? Latest(items.Select(i => i.ValidatedAt)) : null,
             validated, items.Count),
            (FileStage.Reported, detail.Status == LabOrderStatus.Reported || (validatedComplete && reportReleasedAt is not null),
             reportReleasedAt, 0, 0)
        };

        var steps = new List<FileStep>(raw.Length);
        var waiting = false;

        foreach (var (stage, complete, at, done, total) in raw)
        {
            // The first stage not reached is the one the file waits on; those after it are ahead,
            // whatever they say — a file is not "validated" while a tube is still to be drawn. A
            // cancelled file waits on nothing.
            var state = waiting ? FileStageState.Ahead
                      : complete ? FileStageState.Done
                      : detail.IsCancelled ? FileStageState.Ahead
                      : FileStageState.Current;

            if (state != FileStageState.Done) waiting = true;

            steps.Add(new FileStep(stage, state, at, done, total));
        }

        return steps;
    }

    private static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> moments) =>
        moments.Where(m => m is not null).DefaultIfEmpty(null).Max();
}

/// <summary>The colour family of a state on the file screen, drawn from the design system by the client.</summary>
public enum OrderStatusTone
{
    Ok = 0,
    Waiting = 1,
    Info = 2,
    Warning = 3,
    Critical = 4
}

/// <summary>The tone of each state the file screen shows in a pill: the file, an analysis, a tube.</summary>
public static class OrderFileTones
{
    /// <summary>A file's state: waiting in grey, under way in blue, released in green, cancelled in red.</summary>
    public static OrderStatusTone ForOrder(LabOrderStatus status) => status switch
    {
        LabOrderStatus.Draft or LabOrderStatus.Registered or LabOrderStatus.AwaitingCollection => OrderStatusTone.Waiting,
        LabOrderStatus.Validated or LabOrderStatus.Reported => OrderStatusTone.Ok,
        LabOrderStatus.Cancelled => OrderStatusTone.Critical,
        _ => OrderStatusTone.Info
    };

    /// <summary>An analysis's state, on the same scale.</summary>
    public static OrderStatusTone ForItem(LabOrderItemStatus status) => status switch
    {
        LabOrderItemStatus.Pending => OrderStatusTone.Waiting,
        LabOrderItemStatus.Validated or LabOrderItemStatus.Reported => OrderStatusTone.Ok,
        LabOrderItemStatus.Cancelled => OrderStatusTone.Critical,
        _ => OrderStatusTone.Info
    };

    /// <summary>A tube's state: to draw in amber, refused or discarded in red, the rest under way.</summary>
    public static OrderStatusTone ForSample(SampleStatus status) => status switch
    {
        SampleStatus.AwaitingCollection => OrderStatusTone.Warning,
        SampleStatus.Rejected or SampleStatus.Discarded => OrderStatusTone.Critical,
        SampleStatus.Stored => OrderStatusTone.Ok,
        _ => OrderStatusTone.Info
    };
}
