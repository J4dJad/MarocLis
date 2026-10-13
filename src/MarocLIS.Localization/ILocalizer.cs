using System.Globalization;
using MarocLIS.Domain.Common;

namespace MarocLIS.Localization;

/// <summary>
/// Resolves a resource key to text in the operator's current language.
/// <para>
/// Every user-visible string in the application — labels, messages, validation errors, report
/// headings — goes through here. Domain errors carry resource keys rather than sentences for the
/// same reason: the message a reception clerk reads must be in the language they chose, and the
/// same failure has to be printable in Arabic on a patient document.
/// </para>
/// </summary>
public interface ILocalizer
{
    /// <summary>The language currently in force for this thread's UI.</summary>
    AppLanguage CurrentLanguage { get; }

    /// <summary>
    /// Text for <paramref name="key"/> in the current language. Falls back to French, then to the
    /// key itself so a missing translation is visible in testing rather than rendering as blank.
    /// </summary>
    string this[string key] { get; }

    string Get(string key);

    string Get(string key, AppLanguage language);

    /// <summary>Formats a parameterised resource, e.g. <c>"Échéance dépassée de {0} jours"</c>.</summary>
    string Format(string key, params object?[] args);

    string Format(AppLanguage language, string key, params object?[] args);

    /// <summary>Renders a domain error, substituting its arguments into the resolved text.</summary>
    string Describe(Error error);

    string Describe(Error error, AppLanguage language);

    /// <summary>Joins several errors into one message, one per line — what a message box shows.</summary>
    string Describe(IEnumerable<Error> errors);

    /// <summary>True when <paramref name="key"/> has an entry in the requested language.</summary>
    bool Has(string key, AppLanguage? language = null);

    /// <summary>Keys with no entry in <paramref name="language"/> — drives the translation-gap report.</summary>
    IReadOnlyList<string> MissingKeys(AppLanguage language);

    /// <summary>Languages that have a catalogue loaded.</summary>
    IReadOnlyList<AppLanguage> AvailableLanguages { get; }

    /// <summary>Re-reads the catalogues from disk, so a corrected translation applies without a restart.</summary>
    void Reload();
}

/// <summary>
/// Sets the language for the running application. Kept separate from <see cref="ILocalizer"/>
/// because switching language is a command with side effects — it changes the thread cultures and
/// tells the UI to re-render, including mirroring the layout for Arabic.
/// </summary>
public interface ICultureManager
{
    AppLanguage CurrentLanguage { get; }

    CultureInfo CurrentCulture { get; }

    bool IsRightToLeft { get; }

    /// <summary>
    /// Applies a language to the current thread and to new threads. Raises
    /// <see cref="LanguageChanged"/> so open forms can re-localise themselves in place.
    /// </summary>
    void SetLanguage(AppLanguage language);

    event EventHandler<LanguageChangedEventArgs>? LanguageChanged;
}

public sealed class LanguageChangedEventArgs(AppLanguage language) : EventArgs
{
    public AppLanguage Language { get; } = language;
    public bool IsRightToLeft => Language.IsRightToLeft();
}
