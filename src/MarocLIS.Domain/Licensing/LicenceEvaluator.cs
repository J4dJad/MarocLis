using System.Security.Cryptography;
using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Licensing;

/// <summary>What the evaluation of a licence reads besides the licence itself.</summary>
public sealed record LicenceFacts
{
    /// <summary>The licence file's text as stored in the database; null when none was imported.</summary>
    public string? LicenceText { get; init; }

    /// <summary>This server's identifier (<see cref="ServerIdentity"/>).</summary>
    public required string ServerId { get; init; }

    /// <summary>The clock's date.</summary>
    public required DateOnly Today { get; init; }

    /// <summary>The latest date MarocLIS has seen, kept in the database.</summary>
    public DateOnly? LatestDateSeen { get; init; }

    /// <summary>The registration date of the most recent order.</summary>
    public DateOnly? LatestOrderOn { get; init; }

    public int OrderCount { get; init; }
    public bool HasPatients { get; init; }

    /// <summary>The release date of this version of MarocLIS, for a perpetual licence's updates.</summary>
    public required DateOnly ReleaseDate { get; init; }

    /// <summary>The day the read-only mode began, for a licence missing or invalid (not expired).</summary>
    public DateOnly? RestrictedSince { get; init; }

    /// <summary>Numbers revoked, compiled into this version: « LIC-2026-0042 » or « LIC-2026-0042/1 ».</summary>
    public IReadOnlySet<string> Revoked { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Turns a licence and the facts around it into a <see cref="LicenceStatus"/>. Pure: the clock,
/// the database and the key are given, so every transition — warning, grace, read-only, bench
/// clearing, a clock set back — is tested without either.
/// </summary>
public static class LicenceEvaluator
{
    /// <summary>A clock this many days behind the latest date seen is said to have gone back.</summary>
    private const int ClockTolerance = 1;

    public static LicenceStatus Evaluate(LicenceFacts facts, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(publicKey);

        // The clock cannot take MarocLIS back before a day it has already lived: the latest date it
        // has seen and the date of the latest order both hold it forward.
        var seen = Max(facts.LatestDateSeen, facts.LatestOrderOn);
        var today = seen is { } floor && floor > facts.Today ? floor : facts.Today;
        var rolledBack = seen is { } latest && facts.Today < latest.AddDays(-ClockTolerance);

        var basis = new LicenceStatus
        {
            Today = today,
            ClockRolledBack = rolledBack,
            LatestDateSeen = rolledBack ? seen : null,
            HasData = facts.HasPatients || facts.OrderCount > 0,
            ServerId = facts.ServerId
        };

        if (facts.LicenceText is null)
            return Restricted(basis with { State = LicenceState.Missing }, facts, terms: null);

        var parsed = LicenceFile.Parse(facts.LicenceText);
        if (parsed.IsFailure)
            return Restricted(basis with { State = LicenceState.Invalid, Problem = parsed.FirstError }, facts, null);

        var licence = parsed.Value;

        if (!LicenceFile.Verify(licence, publicKey))
            return Restricted(basis with
            {
                State = LicenceState.Invalid,
                Problem = Error.Forbidden("Licence_SignatureInvalid")
            }, facts, null);

        // From here the terms are the vendor's: shown, and their modules kept open for consultation
        // even when the licence cannot be used any more.
        var terms = licence.Terms;

        if (Check(terms, facts, today) is { } problem)
            return Restricted(basis with { State = LicenceState.Invalid, Problem = problem }, facts, terms);

        var full = basis with
        {
            Terms = terms,
            Mode = LicenceMode.Full,
            Modules = ModulesOf(terms),
            WorkstationLimit = terms.Workstations,
            IsMarked = terms.IsMarked
        };

        if (terms.EndsOn is not { } end) return full with { State = LicenceState.Valid };

        var remaining = end.DayNumber - today.DayNumber;
        full = full with { DaysRemaining = remaining };

        if (remaining >= 0)
            return full with { State = LicenceState.Valid, IsExpiringSoon = remaining <= terms.WarningDays };

        var graceEnd = end.AddDays(terms.GraceDays);
        if (today <= graceEnd)
            return full with
            {
                State = LicenceState.Grace,
                GraceDaysRemaining = graceEnd.DayNumber - today.DayNumber
            };

        // Read-only from the day after the grace, and the bench cleared for so many days more.
        return full with
        {
            State = LicenceState.Expired,
            Mode = LicenceMode.ReadOnly,
            WorkstationLimit = null,
            BenchClearingUntil = graceEnd.AddDays(terms.BenchClearingDays)
        };
    }

    /// <summary>
    /// What makes a genuine licence unusable here, in the order a laboratory most needs to be told:
    /// a licence for another server is the commonest, then a revoked one.
    /// </summary>
    private static Error? Check(LicenceTerms terms, LicenceFacts facts, DateOnly today)
    {
        if (terms.Problems() is [var first, ..]) return Error.Validation(first);

        if (terms.ServerId is { } bound
            && !string.Equals(bound, ServerIdentity.Normalise(facts.ServerId), StringComparison.Ordinal))
            return Error.Forbidden("Licence_WrongServer", bound, facts.ServerId);

        if (facts.Revoked.Contains(terms.Number) || facts.Revoked.Contains($"{terms.Number}/{terms.Version}"))
            return Error.Forbidden("Licence_Revoked", terms.Number);

        if (today < terms.StartsOn) return Error.Forbidden("Licence_NotYetValid", terms.StartsOn);

        if (terms.Kind == LicenceKind.Perpetual && terms.UpdatesUntil is { } until && facts.ReleaseDate > until)
            return Error.Forbidden("Licence_UpdatesExpired", until, facts.ReleaseDate);

        if (terms.Kind == LicenceKind.Training && facts.OrderCount > LicenceTerms.TrainingOrderLimit)
            return Error.Forbidden("Licence_TrainingTooManyOrders", LicenceTerms.TrainingOrderLimit, facts.OrderCount);

        return null;
    }

    private static LicenceStatus Restricted(LicenceStatus status, LicenceFacts facts, LicenceTerms? terms)
    {
        // The bench is cleared from the day the restriction was first seen; a database met for the
        // first time in that state starts counting today.
        var since = facts.RestrictedSince ?? status.Today;
        var days = terms?.BenchClearingDays ?? LicenceTerms.DefaultBenchClearingDays;

        return status with
        {
            Mode = LicenceMode.ReadOnly,
            Terms = terms,
            Modules = terms is null ? new HashSet<string>(StringComparer.Ordinal) : ModulesOf(terms),
            WorkstationLimit = null,
            BenchClearingUntil = since.AddDays(days)
        };
    }

    private static HashSet<string> ModulesOf(LicenceTerms terms) =>
        new(terms.Modules.Where(LicenceModules.IsKnown), StringComparer.Ordinal);

    private static DateOnly? Max(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
