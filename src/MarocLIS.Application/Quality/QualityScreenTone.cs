namespace MarocLIS.Application.Quality;

/// <summary>
/// The tone of a pill on the quality screens — « Non-conformités », « EEQ », « Documents qualité »:
/// the workstation's QhTone, by meaning rather than by colour, so the web draws the same pills.
/// </summary>
public enum QualityTone
{
    /// <summary>Waiting for someone: the neutral pill (an open file, a planned action, a draft).</summary>
    Waiting,

    /// <summary>Under way: the information pill (in progress, reviewed, approved).</summary>
    Info,

    /// <summary>Done and sound: the normal pill (closed, satisfactory, in force).</summary>
    Ok,

    /// <summary>Needs attention: the warning pill (major, questionable, awaiting verification).</summary>
    Warning,

    /// <summary>The alarm: critical, unsatisfactory, overdue.</summary>
    Critical,

    /// <summary>Out of the way: dismissed, cancelled, excluded, superseded.</summary>
    Muted
}

/// <summary>How a value of a quality register reads beside the others: the workstation's cell inks.</summary>
public enum QualityInk
{
    Plain,

    /// <summary>Kept for reference: the muted ink (a closed file, a retired document).</summary>
    Muted,

    /// <summary>Done: the normal ink (a completed action).</summary>
    Normal,

    /// <summary>A warning: the abnormal ink, in bold (a deadline passed, a review overdue).</summary>
    Abnormal,

    /// <summary>The alarm: the critical ink, in bold (an overdue action, an unsatisfactory score).</summary>
    Critical
}
