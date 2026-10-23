using MarocLIS.Domain.Common;

namespace MarocLIS.Domain.Licensing;

/// <summary>Where a licence stands, from the licence tab's point of view.</summary>
public enum LicenceState
{
    /// <summary>Not evaluated yet: treated as read-only until it is.</summary>
    Pending = 0,

    /// <summary>No licence imported.</summary>
    Missing = 1,

    /// <summary>Valid; <see cref="LicenceStatus.IsExpiringSoon"/> says whether the end is near.</summary>
    Valid = 2,

    /// <summary>Past its end, within the grace period: everything works, under a red banner.</summary>
    Grace = 3,

    /// <summary>Past the grace period: read-only.</summary>
    Expired = 4,

    /// <summary>Altered, for another server, revoked or otherwise unusable: read-only, with the cause.</summary>
    Invalid = 5
}

/// <summary>What the application may do under the licence.</summary>
public enum LicenceMode
{
    /// <summary>Everything the licence's modules cover.</summary>
    Full = 0,

    /// <summary>
    /// Consultation, reprinting and payments; the results of orders already registered for
    /// <see cref="LicenceStatus.BenchClearingUntil"/>; no new patient, order, analyser result or
    /// setting. Never a blocking screen: a laboratory must always be able to give a patient a result
    /// already analysed.
    /// </summary>
    ReadOnly = 1
}

/// <summary>
/// The licence as evaluated now: the one value every gate, banner and tab reads.
/// <para>
/// The gates are here rather than in each service so the rule of the read-only mode is written
/// once: what the design's table allows, these allow; what it blocks, they refuse with a message
/// that says why and what to do.
/// </para>
/// </summary>
public sealed record LicenceStatus
{
    public LicenceState State { get; init; }
    public LicenceMode Mode { get; init; }

    /// <summary>The terms, when the signature holds; null for a missing or altered licence.</summary>
    public LicenceTerms? Terms { get; init; }

    /// <summary>Why the licence is invalid, as a resource key and its arguments.</summary>
    public Error? Problem { get; init; }

    /// <summary>The date the evaluation took as today: the clock, or later if the clock went back.</summary>
    public DateOnly Today { get; init; }

    /// <summary>The clock read earlier than a date MarocLIS has already seen.</summary>
    public bool ClockRolledBack { get; init; }

    /// <summary>The latest date seen, when it is what <see cref="Today"/> was taken from.</summary>
    public DateOnly? LatestDateSeen { get; init; }

    /// <summary>Days until the end (negative once past it); null without an end date.</summary>
    public int? DaysRemaining { get; init; }

    public bool IsExpiringSoon { get; init; }

    /// <summary>Days of grace left, during <see cref="LicenceState.Grace"/>.</summary>
    public int? GraceDaysRemaining { get; init; }

    /// <summary>In read-only mode, the last day results of existing orders may still be entered and released.</summary>
    public DateOnly? BenchClearingUntil { get; init; }

    /// <summary>The optional modules open; the base is always.</summary>
    public IReadOnlySet<string> Modules { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Workstations allowed at once; null when none is counted (read-only, or no licence).</summary>
    public int? WorkstationLimit { get; init; }

    /// <summary>The trial mark on reports and invoices.</summary>
    public bool IsMarked { get; init; }

    /// <summary>The database holds patients: without a licence it is read-only, never an import screen.</summary>
    public bool HasData { get; init; }

    public string? ServerId { get; init; }

    /// <summary>Before anything was read: read-only, so nothing is written on an unchecked licence.</summary>
    public static LicenceStatus Pending { get; } = new() { State = LicenceState.Pending, Mode = LicenceMode.ReadOnly };

    public bool HasModule(string module) => Modules.Contains(module);

    /// <summary>No licence and nothing in the database yet: the import screen replaces the sign-in.</summary>
    public bool NeedsImportScreen => State == LicenceState.Missing && !HasData;

    public bool IsReadOnly => Mode == LicenceMode.ReadOnly;

    public bool InBenchClearing => IsReadOnly && BenchClearingUntil is { } until && Today <= until;

    // ---- Gates --------------------------------------------------------------------------------

    /// <summary>A new patient, a new order, analyses added to an order: refused in read-only mode.</summary>
    public Result RequireNewWork() =>
        IsReadOnly ? Result.Failure(Error.Forbidden("Licence_ReadOnlyNewWork")) : Result.Success();

    /// <summary>
    /// Entering, validating and releasing the results of orders already registered: allowed in
    /// read-only mode until <see cref="BenchClearingUntil"/>, so the bench can be cleared.
    /// </summary>
    public Result RequireBenchWork()
    {
        if (!IsReadOnly || InBenchClearing) return Result.Success();

        return Result.Failure(BenchClearingUntil is { } until
            ? Error.Forbidden("Licence_ReadOnlyBenchOver", until)
            : Error.Forbidden("Licence_ReadOnlyNewWork"));
    }

    /// <summary>
    /// Changing a setting, except importing a licence: refused in read-only mode.
    /// <para>
    /// The backups (Paramètres › Sauvegardes) are deliberately not behind this gate, nor behind any
    /// other: the server's scheduled backup, « Sauvegarder maintenant », the restore and the backup
    /// schedule itself all stay open in read-only mode. A laboratory whose licence lapsed still holds
    /// its patients' records and must be able to protect them, and to move them to a new server to
    /// obtain the licence for it; blocking the backups would put the data at risk to enforce a
    /// commercial rule. <c>BackupService</c> therefore takes no licence state, and a test composes it
    /// with a read-only licence to keep it so.
    /// </para>
    /// </summary>
    public Result RequireSettingsChange() =>
        IsReadOnly ? Result.Failure(Error.Forbidden("Licence_ReadOnlySettings")) : Result.Success();

    /// <summary>A function of an optional module.</summary>
    public Result RequireModule(string module) =>
        HasModule(module)
            ? Result.Success()
            : Result.Failure(Error.Forbidden("Licence_ModuleMissing",
                                             (object?)LicenceModules.AsEnum(module) ?? module));

    /// <summary>Results arriving from an analyser: the module, and never in read-only mode.</summary>
    public Result RequireInstrumentResults()
    {
        var module = RequireModule(LicenceModules.Instruments);
        if (module.IsFailure) return module;
        return IsReadOnly ? Result.Failure(Error.Forbidden("Licence_ReadOnlyInstruments")) : Result.Success();
    }

    /// <summary>
    /// Opening the web interface: a licence evaluated, imported, and holding the « Version Web »
    /// module. Unlike the phone, the read-only mode does not close it: in a browser as on a Windows
    /// workstation, a laboratory whose licence lapsed keeps consulting and reprinting its results,
    /// and the services refuse the rest as they do at the workstation.
    /// <para>
    /// No licence at all is said apart from a missing module: the web interface has no import
    /// screen, and the administrator is sent to the Windows application on the server for it.
    /// </para>
    /// </summary>
    public Result RequireWebAccess()
    {
        if (State == LicenceState.Pending) return Result.Failure(Error.Forbidden("Licence_WebPending"));
        if (State == LicenceState.Missing) return Result.Failure(Error.Forbidden("Licence_WebNoLicence"));

        // An altered or unreadable licence has no terms, so no module to read: its cause is what
        // the administrator needs, not the name of a module it may well hold.
        if (State == LicenceState.Invalid && Terms is null)
            return Result.Failure(Error.Forbidden("Licence_WebInvalid"));

        return RequireModule(LicenceModules.Web);
    }

    /// <summary>Validation from the phone: the module, and never in read-only mode (bench clearing included).</summary>
    public Result RequireMobileValidation()
    {
        var module = RequireModule(LicenceModules.MobileValidation);
        if (module.IsFailure) return module;
        return IsReadOnly ? Result.Failure(Error.Forbidden("Licence_ReadOnlyMobile")) : Result.Success();
    }
}
