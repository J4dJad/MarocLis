using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Samples;

namespace MarocLIS.Application.Samples;

/// <summary>The chips above the tubes awaiting reception.</summary>
public enum AwaitingReceptionFilter
{
    All = 0,
    Urgent = 1,
    LongDelay = 2
}

/// <summary>The status pill of a tube awaiting reception, most telling first.</summary>
public enum ReceptionVerdict
{
    /// <summary>The tube on the bench, just scanned.</summary>
    Scanned = 0,
    ExcessiveDelay = 1,
    Defects = 2,
    InTransit = 3,
    /// <summary>Its status, in the waiting colours.</summary>
    Waiting = 4
}

/// <summary>What the reception card offers for the tube on the bench.</summary>
public sealed record ReceptionActions(bool Accept, bool AcceptWithReservation, bool Reject)
{
    public static readonly ReceptionActions None = new(false, false, false);
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

    /// <summary>
    /// The pill of a waiting tube: the one on the bench, an exceeded stability window, defects
    /// recorded upstream, then its status.
    /// </summary>
    public static ReceptionVerdict Verdict(SampleReceptionCandidate candidate, Guid? onBench) =>
        onBench == candidate.SampleId ? ReceptionVerdict.Scanned
        : candidate.StabilityExceeded ? ReceptionVerdict.ExcessiveDelay
        : candidate.ExistingDefects != SampleDefect.None ? ReceptionVerdict.Defects
        : candidate.Status == SampleStatus.InTransit ? ReceptionVerdict.InTransit
        : ReceptionVerdict.Waiting;

    /// <summary>
    /// The non-conformities ticked when a tube is scanned, so reception cannot unknowingly accept a
    /// tube already flagged: the stability exceeded, a volume drawn under the minimum, and the
    /// defects recorded upstream. The operator still decides.
    /// </summary>
    public static SampleDefect PresetDefects(SampleReceptionCandidate candidate)
    {
        var defects = candidate.ExistingDefects;

        if (candidate.StabilityExceeded) defects |= SampleDefect.ExcessiveDelay;

        if (candidate.VolumeMl is { } drawn && candidate.MinimumVolumeMl is { } minimum && drawn < minimum)
            defects |= SampleDefect.InsufficientVolume;

        return defects;
    }

    /// <summary>
    /// Keeps the two acceptance buttons honest: a tube with a defect ticked is not accepted by the
    /// plain button, a conforming one is not offered the reservation path. A tube already on the
    /// bench can still be refused (a clot found after centrifugation) but not received again; a
    /// refused or discarded tube admits neither.
    /// </summary>
    public static ReceptionActions Actions(SampleReceptionCandidate? candidate, SampleDefect ticked)
    {
        if (candidate is null) return ReceptionActions.None;

        var defective = ticked != SampleDefect.None;

        return candidate.Status switch
        {
            SampleStatus.Rejected or SampleStatus.Discarded => ReceptionActions.None,
            SampleStatus.Received or SampleStatus.InAnalysis or SampleStatus.Stored => new(false, false, true),
            _ => new(!defective, defective, true)
        };
    }

    /// <summary><see cref="Actions(SampleReceptionCandidate?, SampleDefect)"/>, each with its right.</summary>
    public static ReceptionActions Actions(SampleReceptionCandidate? candidate, SampleDefect ticked, ICurrentUser user)
    {
        var offered = Actions(candidate, ticked);
        var receive = user.Has(Permissions.Samples.Receive);

        return new ReceptionActions(offered.Accept && receive, offered.AcceptWithReservation && receive,
                                    offered.Reject && user.Has(Permissions.Samples.Reject));
    }

    /// <summary>
    /// Whether the tube scanned has been decided already — received, refused — so the screen says
    /// where it stands rather than letting it look like a tube to receive.
    /// </summary>
    public static bool IsAlreadyDecided(SampleReceptionCandidate candidate) =>
        candidate.Status is not (SampleStatus.Collected or SampleStatus.InTransit or SampleStatus.AwaitingCollection);

    /// <summary>
    /// Whether a typed reception temperature is refused: text that is no number, or a figure no
    /// tube reaches the bench at. It used to be dropped, and the tube received with no temperature.
    /// </summary>
    public static bool IsTemperatureRefused(string? typed)
    {
        var temperature = TypedNumber.Parse(typed);
        return (!string.IsNullOrWhiteSpace(typed) && temperature is null)
               || temperature is < Sample.MinimumReceptionTemperature or > Sample.MaximumReceptionTemperature;
    }

    /// <summary>
    /// The reception recorded: the defects ticked, the reservation, the temperature, the cold chain
    /// only for a tube that needs one, and the shelf chosen.
    /// </summary>
    public static ReceiveSampleRequest Request(SampleReceptionCandidate candidate, SampleDefect defects, bool withReservation,
                                               string? typedTemperature, bool coldChainRespected, string? storageLocation) => new()
    {
        SampleId = candidate.SampleId,
        ObservedDefects = defects,
        AcceptWithReservation = withReservation,
        TemperatureCelsius = TypedNumber.Parse(typedTemperature),
        ColdChainRespected = candidate.RequiresColdChain ? coldChainRespected : null,
        StorageLocation = storageLocation
    };
}
