using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Samples;

/// <summary>The chips above the tubes awaiting reception.</summary>
public enum AwaitingReceptionFilter
{
    All = 0,
    Urgent = 1,
    LongDelay = 2
}

/// <summary>The rules of the « Réception des échantillons » screen that need no window.</summary>
public static class SampleReceptionRules
{
    /// <summary>
    /// Whether a tube belongs under a chip: « Urgents » for a file whose priority is not routine,
    /// « Délai long » for a tube past its stability before reception.
    /// </summary>
    public static bool Matches(SampleReceptionCandidate candidate, AwaitingReceptionFilter filter) => filter switch
    {
        AwaitingReceptionFilter.Urgent => candidate.Priority != OrderPriority.Routine,
        AwaitingReceptionFilter.LongDelay => candidate.StabilityExceeded,
        _ => true
    };
}
