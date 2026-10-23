using MarocLIS.Domain.Licensing;

namespace MarocLIS.Application.Licensing;

/// <summary>
/// What the licence changes in the shell: which menu entries a module holds, and which banner the
/// state of the licence calls for. Kept apart from the forms so the rules can be tested, and in the
/// application layer so every client opens and closes the same entries.
/// </summary>
public static class LicenceNavigation
{
    /// <summary>
    /// The menu entries of the optional modules, by the key of the entry. An entry not listed is
    /// part of the base, always there. A closed module's entries disappear as an entry without the
    /// permission does — its data stays in the database, and comes back with the module.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ModuleOfEntry =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["interfaces"] = LicenceModules.Instruments,

            ["claims"] = LicenceModules.ThirdPartyPayer,

            ["qc"] = LicenceModules.Quality,
            ["nonconformities"] = LicenceModules.Quality,
            ["eqa"] = LicenceModules.Quality,
            ["documents"] = LicenceModules.Quality,

            ["inventory"] = LicenceModules.StockAndPurchasing,
            ["purchaseorders"] = LicenceModules.StockAndPurchasing,
            ["quotes"] = LicenceModules.StockAndPurchasing,
            ["supplierinvoices"] = LicenceModules.StockAndPurchasing,
            ["supplierevaluation"] = LicenceModules.StockAndPurchasing,
            ["consumption"] = LicenceModules.StockAndPurchasing,
            ["inventorycounts"] = LicenceModules.StockAndPurchasing,
            ["temperatures"] = LicenceModules.StockAndPurchasing,

            ["equipment"] = LicenceModules.Equipment,
            ["contracts"] = LicenceModules.Equipment,

            ["personnel"] = LicenceModules.HumanResources,
            ["habilitations"] = LicenceModules.HumanResources,
            ["hrdeadlines"] = LicenceModules.HumanResources,
            ["leave"] = LicenceModules.HumanResources,
            ["planning"] = LicenceModules.HumanResources,
            ["accidents"] = LicenceModules.HumanResources,

            ["payroll"] = LicenceModules.Payroll,

            ["prescriberstats"] = LicenceModules.Relations,
            ["relations"] = LicenceModules.Relations,
            ["patientmessaging"] = LicenceModules.Relations,

            ["accountingexport"] = LicenceModules.Finance,
            ["accountingsettings"] = LicenceModules.Finance,
            ["fixedassets"] = LicenceModules.Finance,
            ["bankreconciliation"] = LicenceModules.Finance,
            ["cashforecast"] = LicenceModules.Finance,
            ["cashreview"] = LicenceModules.Finance,

            ["steering"] = LicenceModules.Steering,
            ["costmargin"] = LicenceModules.Steering
        };

    /// <summary>Whether the licence opens the menu entry <paramref name="key"/>.</summary>
    public static bool IsLicensed(string key, LicenceStatus status) =>
        !ModuleOfEntry.TryGetValue(key, out var module) || status.HasModule(module);

    /// <summary>
    /// The banner under the header, or null. The orange one, before the end, is for those who can
    /// do something about it — the administrators, who import the licence, and the biologists, who
    /// run the laboratory; the red ones, once the licence no longer runs normally, for everyone.
    /// </summary>
    public static LicenceBanner? BannerFor(LicenceStatus status, bool administrator, bool biologist)
    {
        ArgumentNullException.ThrowIfNull(status);

        switch (status.State)
        {
            case LicenceState.Valid when status.IsExpiringSoon && (administrator || biologist):
                // The licence runs through its last day: on it, it « expires today ».
                return status.DaysRemaining switch
                {
                    0 => new LicenceBanner(LicenceBannerLevel.Warning, "Licence.Banner.ExpiresToday", []),
                    1 => new LicenceBanner(LicenceBannerLevel.Warning, "Licence.Banner.ExpiresTomorrow", []),
                    _ => new LicenceBanner(LicenceBannerLevel.Warning, "Licence.Banner.ExpiresIn", [status.DaysRemaining])
                };

            case LicenceState.Grace:
                return new LicenceBanner(LicenceBannerLevel.Critical, "Licence.Banner.Grace",
                                         [status.Terms?.EndsOn, status.GraceDaysRemaining]);

            case LicenceState.Expired:
            case LicenceState.Invalid:
            case LicenceState.Missing when status.HasData:
                return new LicenceBanner(LicenceBannerLevel.Critical,
                                         status.InBenchClearing ? "Licence.Banner.ReadOnlyBench" : "Licence.Banner.ReadOnly",
                                         [status.BenchClearingUntil], CauseKey(status));

            default:
                return null;
        }
    }

    /// <summary>
    /// The resource that says why the licence is not in force, for the banner's first words and the
    /// message at sign-in; null when it is.
    /// </summary>
    public static string? CauseKey(LicenceStatus status) => status.State switch
    {
        LicenceState.Missing => "Licence.Cause.Missing",
        LicenceState.Expired => "Licence.Cause.Expired",
        LicenceState.Invalid => "Licence.Cause.Invalid",
        _ => null
    };

    /// <summary>
    /// The message shown once at sign-in: during the grace period and in the read-only mode, to
    /// everyone; null otherwise.
    /// </summary>
    public static string? SignInNoticeKey(LicenceStatus status) => status.State switch
    {
        LicenceState.Grace => "Licence.Login.Grace",
        LicenceState.Expired or LicenceState.Invalid => "Licence.Login.ReadOnly",
        LicenceState.Missing when status.HasData => "Licence.Login.ReadOnly",
        _ => null
    };

    /// <summary>The colour of the days remaining on the licence tab: green, orange under the warning, red after.</summary>
    public static LicenceBannerLevel DaysLevel(LicenceStatus status) =>
        status.State switch
        {
            LicenceState.Valid when status.IsExpiringSoon => LicenceBannerLevel.Warning,
            LicenceState.Valid => LicenceBannerLevel.Normal,
            _ => LicenceBannerLevel.Critical
        };
}

public enum LicenceBannerLevel
{
    Normal,
    Warning,
    Critical
}

/// <summary>
/// A banner's level, its resource and the arguments it is formatted with; a read-only banner also
/// names its cause, which the shell writes in as the last argument.
/// </summary>
public sealed record LicenceBanner(LicenceBannerLevel Level, string ResourceKey, object?[] Arguments,
                                     string? CauseKey = null);
