namespace MarocLIS.Application.Input;

/// <summary>
/// How long a stated reason must be before it is accepted. A reason is what ISO 15189 and loi 08-09
/// keep of an alteration — who did it and why — and a box that takes « . » keeps nothing; the
/// workstation and the web hold every reason to the same length.
/// </summary>
public static class ReasonRules
{
    /// <summary>Any reason asked for (a cancellation, a reprint, a correction): five characters.</summary>
    public const int DefaultMinimumLength = 5;

    /// <summary>Merging two files: the default length.</summary>
    public const int MergeMinimumLength = DefaultMinimumLength;

    /// <summary>
    /// Creating a file although the system found one for the same person, and anonymising a file:
    /// a real justification, not merely a non-empty box.
    /// </summary>
    public const int JustificationMinimumLength = 10;

    /// <summary>Whether <paramref name="reason"/>, trimmed, is long enough.</summary>
    public static bool IsSufficient(string? reason, int minimumLength = DefaultMinimumLength) =>
        (reason?.Trim().Length ?? 0) >= minimumLength;
}
