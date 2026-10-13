using System.Globalization;
using MarocLIS.Domain.Common;

namespace MarocLIS.Localization;

/// <summary>
/// Applies a language to the running application.
/// <para>
/// Two decisions here are deliberate and worth stating. First, dates are forced to
/// <c>dd/MM/yyyy</c> and the Gregorian calendar in all three languages: Morocco uses the Gregorian
/// calendar administratively, and a clinical document whose date could be read two ways is a
/// safety problem. Second, digits are forced to Western Arabic numerals (0–9) even in the Arabic
/// UI. A laboratory result is dictated over the phone, compared against an analyser screen and
/// transcribed into other systems; rendering "١٤٫٢" instead of "14,2" invites a transcription
/// error for no clinical benefit.
/// </para>
/// </summary>
public sealed class CultureManager : ICultureManager
{
    private readonly object _gate = new();

    public CultureManager(AppLanguage initialLanguage = AppLanguage.French)
        => SetLanguage(initialLanguage);

    public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.French;

    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.InvariantCulture;

    public bool IsRightToLeft => CurrentLanguage.IsRightToLeft();

    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    public void SetLanguage(AppLanguage language)
    {
        CultureInfo culture;

        lock (_gate)
        {
            culture = Build(language);

            CurrentLanguage = language;
            CurrentCulture = culture;

            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        LanguageChanged?.Invoke(this, new LanguageChangedEventArgs(language));
    }

    /// <summary>
    /// Builds the culture actually used, starting from the language's own culture and overriding
    /// the conventions that must not vary between languages.
    /// </summary>
    public static CultureInfo Build(AppLanguage language)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(language.ToCultureName()).Clone();

        // Gregorian, unambiguous day-first dates, 24-hour clock.
        culture.DateTimeFormat.Calendar = new GregorianCalendar();
        culture.DateTimeFormat.ShortDatePattern = "dd/MM/yyyy";
        culture.DateTimeFormat.LongDatePattern = language == AppLanguage.Arabic
            ? "dddd d MMMM yyyy"
            : "dddd d MMMM yyyy";
        culture.DateTimeFormat.ShortTimePattern = "HH:mm";
        culture.DateTimeFormat.LongTimePattern = "HH:mm:ss";
        culture.DateTimeFormat.DateSeparator = "/";
        culture.DateTimeFormat.TimeSeparator = ":";

        // Western digits everywhere, including the Arabic UI.
        culture.NumberFormat.DigitSubstitution = DigitShapes.None;
        culture.NumberFormat.NativeDigits = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

        // Comma decimal separator with a narrow space for thousands, which is what Moroccan
        // laboratories and clinicians read in both French and Arabic documents.
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = " ";
        culture.NumberFormat.NumberDecimalDigits = 2;

        culture.NumberFormat.CurrencyDecimalSeparator = ",";
        culture.NumberFormat.CurrencyGroupSeparator = " ";
        culture.NumberFormat.CurrencySymbol = language == AppLanguage.Arabic
            ? Money.SymbolAr
            : language == AppLanguage.English ? Money.CurrencyCode : Money.SymbolFr;
        culture.NumberFormat.CurrencyDecimalDigits = 2;
        culture.NumberFormat.CurrencyPositivePattern = 3;   // "n $"
        culture.NumberFormat.CurrencyNegativePattern = 8;   // "-n $"

        culture.NumberFormat.PercentDecimalSeparator = ",";
        culture.NumberFormat.PercentGroupSeparator = " ";

        return CultureInfo.ReadOnly(culture);
    }

    /// <summary>
    /// Wraps a string in Unicode bidirectional isolates so a Latin token — a test code, a unit, a
    /// barcode — keeps its own direction when embedded in Arabic text. Without this, "mg/dL" next
    /// to Arabic renders with the slash on the wrong side.
    /// </summary>
    public static string IsolateLtr(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $"⁦{value}⁩";

    /// <summary>Isolates a value only when the surrounding text is right-to-left.</summary>
    public static string IsolateIfRtl(string? value, AppLanguage language) =>
        language.IsRightToLeft() ? IsolateLtr(value) : value ?? string.Empty;
}
