using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Samples;

/// <summary>The rules of the « Rappels » list (tubes to draw again) that need no window.</summary>
public static class RecollectionRules
{
    /// <summary>How long a rejected tube may wait for its patient to be reached.</summary>
    public static readonly TimeSpan ContactDelay = TimeSpan.FromHours(24);

    /// <summary>A patient nobody has reached a day after the tube was rejected.</summary>
    public static bool IsOverdue(RecollectionRow row, DateTimeOffset now) =>
        row.ContactCount == 0 && now - row.RejectedAt > ContactDelay;

    /// <summary>The rows drawn in red: an urgent file, or a patient not reached within a day.</summary>
    public static bool IsPriority(RecollectionRow row, DateTimeOffset now) =>
        row.Priority != OrderPriority.Routine || IsOverdue(row, now);
}
