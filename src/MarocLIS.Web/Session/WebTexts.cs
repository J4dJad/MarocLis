using System.Globalization;
using MarocLIS.Domain.Common;
using MarocLIS.Localization;

namespace MarocLIS.Web.Session;

/// <summary>
/// The catalogues, in the language of this browser's session.
/// <para>
/// The workstation switches the language of its whole process (<see cref="ICultureManager"/>); a
/// server cannot, since each browser reads its own. So every text of a page is asked for in the
/// session's language explicitly, through the same <see cref="ILocalizer"/> and the same three
/// catalogues as the workstation — the same words, the same plurals, the same isolation of Latin
/// values in an Arabic sentence.
/// </para>
/// </summary>
public sealed class WebTexts(ILocalizer localizer, SessionContext session)
{
    public AppLanguage Language => session.Language;

    public bool IsRightToLeft => Language.IsRightToLeft();

    /// <summary>The value of the page's <c>dir</c> attribute.</summary>
    public string Direction => IsRightToLeft ? "rtl" : "ltr";

    /// <summary>The value of the page's <c>lang</c> attribute.</summary>
    public string LanguageTag => Language.ToCultureName()[..2];

    /// <summary>The culture dates and numbers are written in: the workstation's, built by the same rule.</summary>
    public CultureInfo Culture => CultureManager.Build(Language);

    public string this[string key] => localizer.Get(key, Language);

    public string T(string key, params object?[] args) =>
        args.Length == 0 ? localizer.Get(key, Language) : localizer.Format(Language, key, args);

    public string Describe(Error error) => localizer.Describe(error, Language);

    /// <summary>The errors, each said once, one per line — what a message box shows at the workstation.</summary>
    public string Describe(IEnumerable<Error> errors) =>
        string.Join(Environment.NewLine,
                    errors.Select(Describe).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));
}
