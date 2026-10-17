using System.Globalization;

namespace MarocLIS.Domain.Common;

/// <summary>
/// The three languages the application is required to serve. French is the working language of
/// Moroccan laboratories and the neutral fallback; Arabic is an official language and must appear
/// on patient-facing documents; English is used for exports and for LOINC/UCUM alignment.
/// </summary>
public enum AppLanguage
{
    French = 0,
    Arabic = 1,
    English = 2
}

public static class AppLanguageExtensions
{
    public static string ToCultureName(this AppLanguage language) => language switch
    {
        AppLanguage.French => "fr-MA",
        AppLanguage.Arabic => "ar-MA",
        AppLanguage.English => "en-US",
        _ => "fr-MA"
    };

    public static bool IsRightToLeft(this AppLanguage language) => language == AppLanguage.Arabic;

    /// <summary>
    /// The culture to format numbers and dates against for this language.
    /// <para>
    /// Every method that takes an <see cref="AppLanguage"/> and renders a number needs this. Before
    /// it existed those methods accepted a language and then formatted against
    /// <see cref="CultureInfo.CurrentCulture"/> — so a report requested in Arabic from a
    /// French-culture thread got French decimal separators, and the language argument was decoration.
    /// </para>
    /// </summary>
    public static CultureInfo ToCulture(this AppLanguage language) =>
        CultureInfo.GetCultureInfo(language.ToCultureName());

    public static AppLanguage FromCulture(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName switch
        {
            "ar" => AppLanguage.Arabic,
            "en" => AppLanguage.English,
            _ => AppLanguage.French
        };
}

/// <summary>
/// Owned value object holding the same piece of business data in all three languages. Used for
/// everything an operator or a patient can read: test names, units, departments, comments,
/// document titles. Stored as three <c>nvarchar</c> columns on the owner table, so it is
/// queryable and indexable — unlike a JSON blob.
/// </summary>
public sealed record MultilingualText
{
    public MultilingualText() { }

    public MultilingualText(string french, string? arabic = null, string? english = null)
    {
        Fr = french;
        Ar = arabic;
        En = english;
    }

    /// <summary>French — mandatory, acts as the fallback for the other two.</summary>
    public string Fr { get; init; } = string.Empty;

    public string? Ar { get; init; }
    public string? En { get; init; }

    public string For(AppLanguage language) => language switch
    {
        AppLanguage.Arabic => Coalesce(Ar, Fr),
        AppLanguage.English => Coalesce(En, Fr),
        _ => Fr
    };

    public string ForCulture(CultureInfo culture) => For(AppLanguageExtensions.FromCulture(culture));

    public string Current => ForCulture(CultureInfo.CurrentUICulture);

    /// <summary>True when a translator still has work to do — drives the "missing translations" report.</summary>
    public bool IsFullyTranslated =>
        !string.IsNullOrWhiteSpace(Fr) &&
        !string.IsNullOrWhiteSpace(Ar) &&
        !string.IsNullOrWhiteSpace(En);

    public MultilingualText With(AppLanguage language, string value) => language switch
    {
        AppLanguage.Arabic => this with { Ar = value },
        AppLanguage.English => this with { En = value },
        _ => this with { Fr = value }
    };

    private static string Coalesce(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    public override string ToString() => Current;

    public static MultilingualText FromFrench(string french) => new(french);

    /// <summary>
    /// An optional text typed in the languages the operator chose, or null when nothing was typed.
    /// A text written only in Arabic (or English) is kept, and also stands as the French fallback:
    /// it used to be dropped without a word because the French box was empty.
    /// </summary>
    public static MultilingualText? FromOptional(string? french, string? arabic, string? english = null)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var (fr, ar, en) = (Clean(french), Clean(arabic), Clean(english));
        var fallback = fr ?? ar ?? en;
        return fallback is null ? null : new MultilingualText(fallback, ar, en);
    }
}
