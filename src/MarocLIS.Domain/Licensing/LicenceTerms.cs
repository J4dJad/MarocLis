namespace MarocLIS.Domain.Licensing;

/// <summary>The kinds of licence, by the word written in the file.</summary>
public enum LicenceKind
{
    /// <summary>« Essai »: a limited period, usually not bound to the server, marked on every document.</summary>
    Trial = 0,

    /// <summary>« Annuelle »: an end date.</summary>
    Annual = 1,

    /// <summary>« Perpetuelle »: no end date; updates are covered until a given date.</summary>
    Perpetual = 2,

    /// <summary>
    /// « Formation »: free, one workstation, no end date, marked like a trial, and refused once the
    /// database holds more than <see cref="LicenceTerms.TrainingOrderLimit"/> orders — it is for
    /// demonstrations and for training staff, not for running a laboratory.
    /// </summary>
    Training = 3
}

/// <summary>
/// What a licence grants: the signed part of the licence file.
/// <para>
/// The values the licensing design left to decide — the length of a trial, the warning before the
/// end, the grace after it, the days of bench work left in read-only mode, whether the licence is
/// bound to the server — are carried by each licence rather than fixed in MarocLIS. The generator
/// fills them with the design's proposals and they can be changed per laboratory, without a new
/// version of the application.
/// </para>
/// </summary>
public sealed record LicenceTerms
{
    /// <summary>The version of this file's layout. A newer layout is refused rather than half read.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Orders beyond which a training licence is refused.</summary>
    public const int TrainingOrderLimit = 200;

    public const int DefaultTrialDays = 30;
    public const int DefaultWarningDays = 30;
    public const int DefaultGraceDays = 15;
    public const int DefaultBenchClearingDays = 7;

    public int Format { get; init; } = CurrentFormat;

    /// <summary>« LIC-2026-0042 »: the same number across the versions that replace one another.</summary>
    public required string Number { get; init; }

    /// <summary>Version 2 replaces version 1 of the same number; an older version is refused.</summary>
    public int Version { get; init; } = 1;

    public required string Laboratory { get; init; }

    /// <summary>The laboratory's health-ministry authorisation number, as printed on its reports.</summary>
    public string? Authorisation { get; init; }

    /// <summary>The site named in the licence request; shown, not checked (the server ID is).</summary>
    public string? Site { get; init; }

    public LicenceKind Kind { get; init; }

    public DateOnly IssuedOn { get; init; }
    public DateOnly StartsOn { get; init; }

    /// <summary>Null for a perpetual or a training licence.</summary>
    public DateOnly? EndsOn { get; init; }

    /// <summary>
    /// Perpetual licence: the last release date of MarocLIS the licence covers. A version published
    /// after it is refused; the version the laboratory already had keeps working.
    /// </summary>
    public DateOnly? UpdatesUntil { get; init; }

    /// <summary>Workstations (computers, not users) that may be signed in at once.</summary>
    public int Workstations { get; init; }

    /// <summary>The optional modules, each named; the base is never listed.</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];

    /// <summary>The server identifier the licence is bound to; null for a licence valid anywhere.</summary>
    public string? ServerId { get; init; }

    public int WarningDays { get; init; } = DefaultWarningDays;
    public int GraceDays { get; init; } = DefaultGraceDays;
    public int BenchClearingDays { get; init; } = DefaultBenchClearingDays;

    /// <summary>Whether reports and invoices printed under this licence carry the trial mark.</summary>
    public bool IsMarked => Kind is LicenceKind.Trial or LicenceKind.Training;

    public bool HasModule(string module) => Modules.Contains(module, StringComparer.Ordinal);

    /// <summary>The word the file uses for <paramref name="kind"/>.</summary>
    public static string KindWord(LicenceKind kind) => kind switch
    {
        LicenceKind.Trial => "Essai",
        LicenceKind.Annual => "Annuelle",
        LicenceKind.Perpetual => "Perpetuelle",
        LicenceKind.Training => "Formation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static LicenceKind? KindOf(string? word) => word switch
    {
        "Essai" => LicenceKind.Trial,
        "Annuelle" => LicenceKind.Annual,
        "Perpetuelle" => LicenceKind.Perpetual,
        "Formation" => LicenceKind.Training,
        _ => null
    };

    /// <summary>
    /// What makes terms impossible, whoever signed them: checked by the generator before signing
    /// and by MarocLIS after the signature, so a slip in the generator cannot become a licence
    /// MarocLIS has to guess about. Resource keys.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Format != CurrentFormat) problems.Add("Licence_FormatUnknown");
        if (string.IsNullOrWhiteSpace(Number)) problems.Add("Licence_NumberRequired");
        if (Version < 1) problems.Add("Licence_VersionInvalid");
        if (string.IsNullOrWhiteSpace(Laboratory)) problems.Add("Licence_LaboratoryRequired");
        if (Workstations < 1) problems.Add("Licence_WorkstationsInvalid");

        if (Kind is LicenceKind.Trial or LicenceKind.Annual)
        {
            if (EndsOn is null) problems.Add("Licence_EndRequired");
            else if (EndsOn < StartsOn) problems.Add("Licence_EndBeforeStart");
        }

        if (Kind == LicenceKind.Perpetual && UpdatesUntil is null) problems.Add("Licence_UpdatesUntilRequired");
        if (Kind == LicenceKind.Training && Workstations != 1) problems.Add("Licence_TrainingOneWorkstation");

        if (WarningDays is < 0 or > 365 || GraceDays is < 0 or > 365 || BenchClearingDays is < 0 or > 365)
            problems.Add("Licence_PeriodsInvalid");

        // A module this version does not know is not a problem: a later generator may name one
        // added since, and this version has nothing to open or close for it.
        return problems;
    }
}
