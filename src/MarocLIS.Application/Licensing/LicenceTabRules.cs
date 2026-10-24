using MarocLIS.Domain.Common;
using MarocLIS.Domain.Licensing;

namespace MarocLIS.Application.Licensing;

/// <summary>Where the licence in force is bound.</summary>
public enum LicenceBinding
{
    /// <summary>No licence read.</summary>
    None,

    /// <summary>A licence for any server.</summary>
    Unbound,
    BoundHere,

    /// <summary>For another server: the licence is not in force here.</summary>
    BoundElsewhere
}

/// <summary>
/// A sentence of the licence tab's warning: the refusal itself, or a key and its arguments, written
/// as a sentence of its own when it is the cause of the read-only mode.
/// </summary>
public sealed record LicenceNote(Error? Problem, string? Key, bool AsSentence, params object?[] Arguments);

/// <summary>
/// The rules of « Paramètres › Licence » that need no window: the state's pill and whether it says
/// « lecture seule », why the licence is not in force, what is left of it, its modules open and
/// closed, the workstations it allows, where it is bound, and the name of the request file. The
/// workstation's tab and the web's apply the same ones; the import and the request are
/// LicenceService's, which records each import and each refusal.
/// </summary>
public static class LicenceTabRules
{
    /// <summary>The state's pill: valid, expiring soon, or not in force.</summary>
    /// <remarks>The level of the days left too (<see cref="LicenceNavigation.DaysLevel"/>): one reading.</remarks>
    public static LicenceBannerLevel Tone(LicenceStatus status) => LicenceNavigation.DaysLevel(status);

    /// <summary>« Expirée · Lecture seule »: the mode said beside the state, but not before the licence has been read.</summary>
    public static bool SaysReadOnly(LicenceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.IsReadOnly && status.State != LicenceState.Pending;
    }

    /// <summary>
    /// Why the licence is not in force, in the words of the refusal; and a clock found behind; and,
    /// read-only, until when the bench may still be cleared.
    /// </summary>
    public static IReadOnlyList<LicenceNote> Notes(LicenceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var notes = new List<LicenceNote>();
        if (status.Problem is { } problem) notes.Add(new LicenceNote(problem, null, false));
        else if (status.State == LicenceState.Missing) notes.Add(new LicenceNote(null, "Licence.Cause.Missing", true));

        if (status.ClockRolledBack) notes.Add(new LicenceNote(null, "Licence.ClockRolledBack", false, status.LatestDateSeen));

        if (status.IsReadOnly && status.BenchClearingUntil is { } bench && status.State != LicenceState.Pending)
            notes.Add(status.InBenchClearing
                ? new LicenceNote(null, "Licence.BenchUntil", false, bench)
                : new LicenceNote(null, "Licence.BenchOver", false));

        return notes;
    }

    /// <summary>A cause written to follow « Lecture seule — », as a sentence of its own.</summary>
    public static string Sentence(string text, System.Globalization.CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0
            ? text
            : char.ToUpper(text[0], culture) + text[1..] + (char.IsPunctuation(text[^1]) ? "" : ".");
    }

    /// <summary>What is left: the days of a valid licence (none without an end), of the grace, the date it expired.</summary>
    public static (string? Key, object? Argument) Remaining(LicenceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            LicenceState.Valid when status.DaysRemaining is { } days => ("Licence.DaysRemaining", days),
            LicenceState.Valid => ("Licence.NoEnd", null),
            LicenceState.Grace => ("Licence.GraceRemaining", status.GraceDaysRemaining),
            LicenceState.Expired => ("Licence.ExpiredOn", status.Terms?.EndsOn),
            _ => (null, null)
        };
    }

    /// <summary>The workstations allowed, the evaluation's figure first.</summary>
    public static int? WorkstationLimit(LicenceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.WorkstationLimit ?? status.Terms?.Workstations;
    }

    /// <summary>The modules the licence opens, in the tab's order, and those it leaves closed.</summary>
    public static (IReadOnlyList<string> Open, IReadOnlyList<string> Closed) Modules(LicenceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var open = status.Terms is { } terms ? LicenceModules.All.Where(terms.HasModule).ToList() : [];
        return (open, [.. LicenceModules.All.Except(open)]);
    }

    public static LicenceBinding Binding(LicenceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.Terms is not { } terms) return LicenceBinding.None;
        if (terms.ServerId is null) return LicenceBinding.Unbound;
        return string.Equals(terms.ServerId, status.ServerId, StringComparison.Ordinal)
            ? LicenceBinding.BoundHere
            : LicenceBinding.BoundElsewhere;
    }

    /// <summary>« demande-licence-LABO-CENTRAL.demande »: the site in the name, so the vendor's inbox tells them apart.</summary>
    public static string RequestFileName(LicenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var site = new string([.. request.Site.Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        if (site.Length > 40) site = site[..40];
        return $"demande-licence{(site.Length == 0 ? "" : "-" + site)}{LicenceRequest.Extension}";
    }

    /// <summary>A licence is a few hundred bytes; a file far larger is not one, and is not read.</summary>
    public static bool IsReadableSize(long bytes) => bytes <= LicenceFile.MaxFileBytes;
}
