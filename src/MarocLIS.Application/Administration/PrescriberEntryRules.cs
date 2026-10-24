using MarocLIS.Domain.Orders;

namespace MarocLIS.Application.Administration;

/// <summary>
/// The rules of the prescriber form that need no window, shared by the directory and the order
/// counter of the workstation and by the web's counter.
/// </summary>
public static class PrescriberEntryRules
{
    /// <summary>What the form refuses before it is sent, as a text key: a prescriber has a last name.</summary>
    public static string? Problem(string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? "Prescriber_LastNameRequired" : null;

    /// <summary>
    /// Whether the INPE warning is shown: only a physician files an AMO claim, so it is scoped to
    /// those kinds rather than nagging on a dentist or an institution.
    /// </summary>
    public static bool WarnsOfMissingInpe(PrescriberKind kind, string? inpe) =>
        kind is PrescriberKind.GeneralPractitioner or PrescriberKind.Specialist && string.IsNullOrWhiteSpace(inpe);

    /// <summary>
    /// The name typed in the order counter's list, split into the new prescriber's last and first
    /// names at its first space, so it is not typed twice.
    /// </summary>
    public static (string LastName, string? FirstName) SplitTypedName(string? typed)
    {
        var parts = (typed?.Trim() ?? string.Empty).Split(' ', 2);
        return (parts.ElementAtOrDefault(0) ?? string.Empty, parts.ElementAtOrDefault(1));
    }
}
