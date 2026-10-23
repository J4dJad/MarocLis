using MarocLIS.Application.Abstractions;
using MarocLIS.Application.Licensing;
using MarocLIS.Domain.Common;
using MarocLIS.Domain.Identity;
using MarocLIS.Domain.Licensing;
using MarocLIS.Domain.Organization;

namespace MarocLIS.Application.Administration;

/// <summary>
/// The languages the laboratory works in, held as an installation setting (<c>AppSetting</c>,
/// installation-wide): French alone, French and English, French and Arabic, or all three.
/// <para>
/// French is always on whatever is stored: it is the working language of the laboratory and the
/// fallback of every catalogue, and a record in a language switched off is shown and printed in
/// it. A key that is absent or blank keeps the three languages, so an installation that never
/// opened the setting works as it always has.
/// </para>
/// </summary>
public sealed record LanguageSettings
{
    public const string Prefix = "Localization.";
    public const string ActiveLanguagesKey = Prefix + "ActiveLanguages";

    /// <summary>The three languages, in the order every list shows them.</summary>
    public static IReadOnlyList<AppLanguage> AllLanguages { get; } = Enum.GetValues<AppLanguage>();

    public static LanguageSettings Default { get; } = new();

    /// <summary>The languages offered and assigned, French first; never empty.</summary>
    public IReadOnlyList<AppLanguage> Active { get; private init; } = AllLanguages;

    /// <summary>The settings for <paramref name="languages"/>, French added and unknown values dropped.</summary>
    public static LanguageSettings Of(IEnumerable<AppLanguage> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);

        var chosen = languages.Where(Enum.IsDefined).Append(AppLanguage.French).ToHashSet();
        return new LanguageSettings { Active = [.. AllLanguages.Where(chosen.Contains)] };
    }

    public bool IsActive(AppLanguage language) => Active.Contains(language);

    /// <summary>
    /// Each language in its own name, whatever the interface's language: an Arabic speaker looks
    /// for « العربية », not for « Arabe ». Every client's language list shows these.
    /// </summary>
    public static string OwnName(AppLanguage language) => language switch
    {
        AppLanguage.Arabic => "العربية",
        AppLanguage.English => "English",
        _ => "Français"
    };

    /// <summary>The language itself when the laboratory works in it, French otherwise.</summary>
    public AppLanguage Coerce(AppLanguage language) => IsActive(language) ? language : AppLanguage.French;

    public static LanguageSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!values.TryGetValue(ActiveLanguagesKey, out var text) || string.IsNullOrWhiteSpace(text))
            return Default;

        // A name that is not a language (typed by hand in the database) is ignored; French stays.
        var named = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(name => Enum.TryParse<AppLanguage>(name, ignoreCase: true, out var language)
                                        && Enum.IsDefined(language)
                            ? language
                            : (AppLanguage?)null)
                        .OfType<AppLanguage>();

        return Of(named);
    }

    public IReadOnlyList<(string Key, string Value)> ToValues() =>
        [(ActiveLanguagesKey, string.Join(",", Active))];

    // By the languages, not by the list instance: two readings of the same setting are equal.
    public bool Equals(LanguageSettings? other) => other is not null && Active.SequenceEqual(other.Active);

    public override int GetHashCode() => Active.Aggregate(17, (hash, language) => hash * 31 + (int)language);
}

/// <summary>Reads the installation settings under a prefix.</summary>
public interface ILanguageSettingsQueries
{
    Task<IReadOnlyDictionary<string, string?>> GetSettingValuesAsync(string prefix, CancellationToken ct = default);
}

/// <summary>
/// The languages the laboratory works in, for the services that assign one and the screens that
/// offer one.
/// </summary>
public interface IActiveLanguages
{
    /// <summary>The languages as last read: what a picker offers, without a round trip.</summary>
    LanguageSettings Current { get; }

    /// <summary>The languages, read again from the settings when the last reading is not recent.</summary>
    Task<LanguageSettings> GetAsync(CancellationToken ct = default);

    /// <summary><paramref name="language"/> when it is active, French otherwise, on a recent reading.</summary>
    Task<AppLanguage> CoerceAsync(AppLanguage language, CancellationToken ct = default);
}

/// <summary>
/// The last reading of the language settings, shared by the whole process.
/// <para>
/// A singleton so a picker can be filled without a query, and so a change saved on this workstation
/// is seen at once by every screen opened afterwards. Another workstation, or the API, sees it once
/// its own reading is older than <see cref="MaxAge"/>.
/// </para>
/// </summary>
public sealed class ActiveLanguagesCache
{
    /// <summary>How long a reading is trusted before the next service call reads the setting again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private sealed record Reading(LanguageSettings Settings, DateTimeOffset ReadAt);

    private volatile Reading? _reading;

    /// <summary>The last reading; the three languages before anything was read.</summary>
    public LanguageSettings Current => _reading?.Settings ?? LanguageSettings.Default;

    public bool IsFresh(DateTimeOffset now) => _reading is { } reading && now - reading.ReadAt < MaxAge;

    public void Store(LanguageSettings settings, DateTimeOffset readAt) =>
        _reading = new Reading(settings, readAt);
}

/// <summary>Reads, caches and saves the languages the laboratory works in.</summary>
public sealed class ActiveLanguagesService(
    ILanguageSettingsQueries queries,
    ActiveLanguagesCache cache,
    IConfigurationWriter settingsWriter,
    IUnitOfWork unitOfWork,
    IPermissionGuard guard,
    IClock clock,
    ILicenceState? licence = null) : IActiveLanguages
{
    public LanguageSettings Current => cache.Current;

    // Not gated: every screen that offers a language and every service that assigns one reads it.
    public async Task<LanguageSettings> GetAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        if (cache.IsFresh(now)) return cache.Current;

        var settings = Licensed(LanguageSettings.FromValues(await queries.GetSettingValuesAsync(LanguageSettings.Prefix, ct)));
        cache.Store(settings, now);
        return settings;
    }

    public async Task<AppLanguage> CoerceAsync(AppLanguage language, CancellationToken ct = default) =>
        (await GetAsync(ct)).Coerce(language);

    /// <summary>
    /// Switches Arabic and English on or off: the right to manage settings, traced with the list.
    /// French is kept whatever is asked.
    /// </summary>
    public async Task<Result> SaveAsync(IEnumerable<AppLanguage> active, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(active);

        var permitted = guard.Require(Permissions.Administration.ManageSettings);
        if (permitted.IsFailure) return permitted;

        var licensed = licence.RequireSettingsChange();
        if (licensed.IsFailure) return licensed;

        var settings = LanguageSettings.Of(active);

        if (settings.Active.Contains(AppLanguage.Arabic))
        {
            var arabic = licence.RequireModule(LicenceModules.Arabic);
            if (arabic.IsFailure) return arabic;
        }

        var changed = false;
        foreach (var (key, value) in settings.ToValues())
        {
            var existing = await settingsWriter.GetSettingAsync(key, null, ct);
            if (existing is null)
            {
                await settingsWriter.AddSettingAsync(new AppSetting(key, value)
                {
                    Description = "Langues de travail du laboratoire (le français reste toujours actif)"
                }, ct);
                changed = true;
            }
            else if (existing.Value != value)
            {
                existing.Value = value;
                changed = true;
            }
        }

        if (changed)
        {
            unitOfWork.AuditReason = $"Langues actives modifiées : {string.Join(", ", settings.Active)}";
            await unitOfWork.SaveChangesAsync(ct);
        }

        // At once on this workstation: the next dialog opened offers the new list.
        cache.Store(settings, clock.Now);
        return Result.Success();
    }

    /// <summary>
    /// Arabic is a module of the licence: switched on in the settings but not licensed — a licence
    /// replaced by one without it — it is off, and what would have been in Arabic is in French. The
    /// setting is kept, so the module bought back brings the choice back as it was.
    /// </summary>
    private LanguageSettings Licensed(LanguageSettings settings) =>
        licence.HasModule(LicenceModules.Arabic) || !settings.Active.Contains(AppLanguage.Arabic)
            ? settings
            : LanguageSettings.Of(settings.Active.Where(l => l != AppLanguage.Arabic));
}
