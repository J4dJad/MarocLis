using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarocLIS.Mobile.Services;

/// <summary>The three languages of MarocLIS, by the names the API gives them.</summary>
public enum UiLanguage
{
    French = 0,
    Arabic = 1,
    English = 2
}

/// <summary>
/// The phone's texts, read from the subset of the workstation's catalogues embedded at build time
/// (Build/MobileStrings.targets), with the workstation's rules: a missing key falls back to French,
/// <c>{0:plural:a|b}</c> agrees with its count, and a Latin or numeric value placed in an Arabic
/// sentence is isolated so it keeps its own direction.
/// </summary>
public sealed partial class Texts
{
    private const char Lri = '⁦';
    private const char Pdi = '⁩';

    private readonly Dictionary<UiLanguage, IReadOnlyDictionary<string, string>> _catalogues = [];

    public Texts() => Language = UiLanguage.French;

    public UiLanguage Language { get; private set; }

    public bool IsRightToLeft => Language == UiLanguage.Arabic;

    /// <summary>The value of the page's <c>dir</c> attribute.</summary>
    public string Direction => IsRightToLeft ? "rtl" : "ltr";

    /// <summary>The value of the page's <c>lang</c> attribute.</summary>
    public string LanguageTag => Tag(Language);

    /// <summary>Raised after <see cref="Use"/> changed the language, so the open screens re-render.</summary>
    public event Action? Changed;

    public void Use(UiLanguage language)
    {
        if (language == Language && _catalogues.ContainsKey(language)) return;
        Language = language;
        Changed?.Invoke();
    }

    /// <summary>The language the API names (« French », « Arabic », « English »); French when unknown.</summary>
    public static UiLanguage Parse(string? name) =>
        Enum.TryParse<UiLanguage>(name, ignoreCase: true, out var language) && Enum.IsDefined(language)
            ? language
            : UiLanguage.French;

    public static string Tag(UiLanguage language) => language switch
    {
        UiLanguage.Arabic => "ar",
        UiLanguage.English => "en",
        _ => "fr"
    };

    public bool Has(string key) => Catalogue(Language).ContainsKey(key) || Catalogue(UiLanguage.French).ContainsKey(key);

    /// <summary>The text of <paramref name="key"/>, formatted with <paramref name="args"/>.</summary>
    public string Get(string key, params object?[] args)
    {
        var template = Catalogue(Language).TryGetValue(key, out var text)
            ? text
            : Catalogue(UiLanguage.French).TryGetValue(key, out var french) ? french : key;

        if (args.Length == 0) return template;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, Plurals(template, args), Isolated(args));
        }
        catch (FormatException)
        {
            // A translation with a malformed placeholder must not take the screen down with it.
            return template;
        }
    }

    /// <summary>
    /// The same as <see cref="Get"/>, under the name the build scans for (T("Key")): a key named
    /// outside a component is kept and checked like the others.
    /// </summary>
    public string T(string key, params object?[] args) => Get(key, args);

    /// <summary>A Latin or numeric value, isolated when the page reads right to left.</summary>
    public string Isolate(string? value) =>
        value is null ? string.Empty : IsRightToLeft && NeedsIsolation(value) ? $"{Lri}{value}{Pdi}" : value;

    private object?[] Isolated(object?[] args)
    {
        if (!IsRightToLeft) return args;

        var isolated = (object?[])args.Clone();
        for (var i = 0; i < isolated.Length; i++)
        {
            var text = isolated[i] switch
            {
                null => null,
                string s => s,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var other => other.ToString()
            };
            if (NeedsIsolation(text)) isolated[i] = $"{Lri}{text}{Pdi}";
        }

        return isolated;
    }

    private static bool NeedsIsolation(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == Lri) return false;

        var latin = false;
        foreach (var c in value)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return false;
            if (char.IsAsciiLetter(c) || char.IsAsciiDigit(c)) latin = true;
        }

        return latin;
    }

    [GeneratedRegex(@"\{(\d+):plural:([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PluralToken();

    /// <summary>
    /// <c>{0:plural:dossier|dossiers}</c> by the count in argument 0: in French 0 and 1 take the
    /// singular, in English and Arabic only 1 — the catalogues' own rule (JsonLocalizer.Plurals).
    /// </summary>
    private string Plurals(string template, object?[] args)
    {
        if (!template.Contains(":plural:", StringComparison.Ordinal)) return template;

        return PluralToken().Replace(template, match =>
        {
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var forms = match.Groups[2].Value.Split('|');
            var singular = index < args.Length && args[index] is { } arg && IsSingular(arg);
            return (singular ? forms[0] : forms[^1]).Replace("{", "{{", StringComparison.Ordinal)
                                                     .Replace("}", "}}", StringComparison.Ordinal);
        });
    }

    private bool IsSingular(object value)
    {
        if (value is string or bool or Enum or not IConvertible) return false;

        decimal count;
        try
        {
            count = Math.Abs(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }

        return Language == UiLanguage.French ? count < 2m : count == 1m;
    }

    private IReadOnlyDictionary<string, string> Catalogue(UiLanguage language)
    {
        if (_catalogues.TryGetValue(language, out var catalogue)) return catalogue;

        var name = $"MarocLIS.Mobile.Strings.{Tag(language)}.json";
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream(name);

        catalogue = stream is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize(stream, StringsJsonContext.Default.DictionaryStringString)
              ?? new Dictionary<string, string>(StringComparer.Ordinal);

        _catalogues[language] = catalogue;
        return catalogue;
    }

    /// <summary>Text for tests: the catalogue as embedded, by language.</summary>
    internal IReadOnlyDictionary<string, string> CatalogueFor(UiLanguage language) => Catalogue(language);

    /// <summary>A short, fixed date: dd/MM, Latin digits in every language, as on the printed report.</summary>
    public static string ShortDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd/MM", CultureInfo.InvariantCulture);

    public static string ShortDate(DateOnly value) => value.ToString("dd/MM", CultureInfo.InvariantCulture);

    public static string Date(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Today's files by their time, older ones by their date: what a list needs to tell apart.</summary>
    public static string WhenShort(DateTimeOffset value) =>
        value.ToLocalTime().Date == DateTime.Today ? Time(value) : ShortDate(value);
}
