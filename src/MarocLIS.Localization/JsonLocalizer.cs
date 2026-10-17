using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using MarocLIS.Domain.Common;
using Microsoft.Extensions.Logging;

namespace MarocLIS.Localization;

public sealed class LocalizationOptions
{
    /// <summary>
    /// Folder holding the catalogues. Defaults to <c>Resources</c> beside the executable. A
    /// laboratory can point this at a shared folder on the server so a corrected wording reaches
    /// every workstation at once.
    /// </summary>
    public string ResourceDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary>Language used when the requested one has no entry for a key.</summary>
    public AppLanguage FallbackLanguage { get; set; } = AppLanguage.French;

    /// <summary>
    /// Whether a missing key surfaces as <c>[[key]]</c>. On in development so gaps are obvious;
    /// off in production, where the key itself is returned.
    /// </summary>
    public bool MarkMissingKeys { get; set; }

    /// <summary>Records every key that could not be resolved, for the translation-gap report.</summary>
    public bool TrackMissingKeys { get; set; } = true;
}

/// <summary>
/// Reads flat JSON catalogues (<c>fr.json</c>, <c>ar.json</c>, <c>en.json</c>) and resolves keys
/// against them with a French fallback.
/// <para>
/// Catalogues are looked up on disk first and fall back to the copies embedded in this assembly, so
/// the application always starts with a complete French catalogue even if the deployed
/// <c>Resources</c> folder is missing — a blank UI is a far worse failure than a stale translation.
/// </para>
/// </summary>
public sealed class JsonLocalizer : ILocalizer
{
    private readonly LocalizationOptions _options;
    private readonly ILogger<JsonLocalizer>? _logger;
    private readonly ICultureManager? _cultureManager;

    private readonly ConcurrentDictionary<AppLanguage, IReadOnlyDictionary<string, string>> _catalogues = new();
    private readonly ConcurrentDictionary<string, byte> _missingKeys = new(StringComparer.Ordinal);

    public JsonLocalizer(LocalizationOptions options,
                         ICultureManager? cultureManager = null,
                         ILogger<JsonLocalizer>? logger = null)
    {
        _options = options;
        _cultureManager = cultureManager;
        _logger = logger;
        LoadAll();
    }

    public AppLanguage CurrentLanguage =>
        _cultureManager?.CurrentLanguage ?? AppLanguageExtensions.FromCulture(CultureInfo.CurrentUICulture);

    public IReadOnlyList<AppLanguage> AvailableLanguages => [.. _catalogues.Keys.Order()];

    public string this[string key] => Get(key);

    public string Get(string key) => Get(key, CurrentLanguage);

    public string Get(string key, AppLanguage language)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        if (_catalogues.TryGetValue(language, out var catalogue) &&
            catalogue.TryGetValue(key, out var text))
            return text;

        if (language != _options.FallbackLanguage &&
            _catalogues.TryGetValue(_options.FallbackLanguage, out var fallback) &&
            fallback.TryGetValue(key, out var fallbackText))
        {
            RecordMissing(key, language);
            return fallbackText;
        }

        RecordMissing(key, language);
        return _options.MarkMissingKeys ? $"[[{key}]]" : key;
    }

    public string Format(string key, params object?[] args) => Format(CurrentLanguage, key, args);

    public string Format(AppLanguage language, string key, params object?[] args)
    {
        var template = Get(key, language);
        if (args.Length == 0) return template;

        var culture = CultureInfo.GetCultureInfo(language.ToCultureName());

        try
        {
            return string.Format(culture, Plurals(template, args, language), Isolate(Translated(args, language), language));
        }
        catch (FormatException)
        {
            // A translation with a malformed placeholder must not take down the screen that shows it.
            _logger?.LogWarning("Resource '{Key}' for {Language} has a malformed format placeholder.",
                                key, language);
            return template;
        }
    }

    private static readonly Regex PluralToken = new(@"\{(\d+):plural:([^{}]*)\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves the plural tokens of a template: <c>{0:plural:commande|commandes}</c> becomes
    /// <c>commande</c> or <c>commandes</c> by the count in argument 0 and the language's rule — in
    /// French 0 and 1 take the singular (« 1 commande », « 0 rejet »), in English only 1. The count
    /// itself is written by its own placeholder: <c>{0} {0:plural:commande|commandes}</c>, and an
    /// adjective further on agrees the same way (« {1} en retard ou {1:plural:manquant|manquants} »).
    /// <para>
    /// The counts in the page headers and messages were written in the plural only, and read
    /// « 1 rejets », « 1 commandes » whenever there was one. A template without a token is left as
    /// it is; an argument that is not a number takes the last form.
    /// </para>
    /// </summary>
    private static string Plurals(string template, object?[] args, AppLanguage language)
    {
        if (!template.Contains(":plural:", StringComparison.Ordinal)) return template;

        return PluralToken.Replace(template, match =>
        {
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var forms = match.Groups[2].Value.Split('|');
            var singular = index < args.Length && args[index] is { } arg && IsSingular(arg, language);
            return (singular ? forms[0] : forms[^1]).Replace("{", "{{", StringComparison.Ordinal)
                                                     .Replace("}", "}}", StringComparison.Ordinal);
        });
    }

    private static bool IsSingular(object value, AppLanguage language)
    {
        decimal count;
        try
        {
            if (value is string or bool or Enum or not IConvertible) return false;
            count = Math.Abs(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }

        return language == AppLanguage.French ? count < 2m : count == 1m;
    }

    /// <summary>
    /// Replaces each enumeration argument by its label, read from the section named after its type
    /// ("SampleStatus.Received"; the order line's <c>LabOrderItemStatus</c> lives under
    /// "OrderItemStatus", as on the screens).
    /// <para>
    /// The domain's refusals carry the status itself ("Validation impossible depuis le statut
    /// « {0} »"), and substituted as is it showed the code's English name — « Validated » in a
    /// French or Arabic sentence. Done here, once, so every such message is translated, those of
    /// the domain and of the services alike. A value with no label keeps its name.
    /// </para>
    /// </summary>
    private object?[] Translated(object?[] args, AppLanguage language)
    {
        object?[]? translated = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not Enum value || EnumLabel(value, language) is not { } label) continue;
            translated ??= (object?[])args.Clone();
            translated[i] = label;
        }

        return translated ?? args;
    }

    private string? EnumLabel(Enum value, AppLanguage language)
    {
        var type = value.GetType().Name;
        string[] sections = type.StartsWith("Lab", StringComparison.Ordinal) ? [type, type[3..]] : [type];
        foreach (var section in sections)
        {
            var key = $"{section}.{value}";
            if (Has(key, language) || Has(key, _options.FallbackLanguage)) return Get(key, language);
        }

        return null;
    }

    /// <summary>
    /// Wraps each Latin or numeric argument in a bidirectional isolate before it is substituted
    /// into an Arabic sentence.
    /// <para>
    /// Done here because this is the one boundary every composed message passes through. A value
    /// interpolated into a right-to-left template is otherwise laid out relative to that template:
    /// "Conserver {0} jours" with "5" is harmless, but "Tube {0}" with "ABC-123/4" and
    /// "Volume {0}" with "5 mL" both come out re-ordered, and the second is a clinical quantity.
    /// </para>
    /// <para>
    /// Only the arguments are isolated, never the template: the template is the translated text and
    /// its own direction is correct by construction. And only for a right-to-left language, so the
    /// French and English strings are byte-identical to what they were.
    /// </para>
    /// </summary>
    private static object?[] Isolate(object?[] args, AppLanguage language)
    {
        if (!language.IsRightToLeft() || args.Length == 0) return args;

        object?[]? isolated = null;

        for (var i = 0; i < args.Length; i++)
        {
            // Formatted through the culture rather than ToString(): a decimal or a date has to
            // reach its final text before it can be examined for direction.
            var text = args[i] switch
            {
                null => null,
                string s => s,
                IFormattable formattable =>
                    formattable.ToString(null, CultureInfo.GetCultureInfo(language.ToCultureName())),
                var other => other.ToString()
            };

            if (!BidiText.NeedsIsolation(text)) continue;

            isolated ??= (object?[])args.Clone();
            isolated[i] = BidiText.Isolate(text!);
        }

        return isolated ?? args;
    }

    public string Describe(Error error) => Describe(error, CurrentLanguage);

    public string Describe(Error error, AppLanguage language) =>
        error == Error.None || string.IsNullOrEmpty(error.ResourceKey)
            ? string.Empty
            : Format(language, error.ResourceKey, error.Args);

    // The same refusal for several tubes or lines is said once, not once per item.
    public string Describe(IEnumerable<Error> errors) =>
        string.Join(Environment.NewLine,
                    errors.Select(e => Describe(e)).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));

    public bool Has(string key, AppLanguage? language = null) =>
        _catalogues.TryGetValue(language ?? CurrentLanguage, out var catalogue) &&
        catalogue.ContainsKey(key);

    public IReadOnlyList<string> MissingKeys(AppLanguage language)
    {
        if (!_catalogues.TryGetValue(_options.FallbackLanguage, out var reference)) return [];

        var target = _catalogues.TryGetValue(language, out var catalogue)
            ? catalogue
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return [.. reference.Keys.Where(k => !target.ContainsKey(k)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Keys requested at runtime that no catalogue could resolve.</summary>
    public IReadOnlyList<string> UnresolvedKeysSeen => [.. _missingKeys.Keys.Order(StringComparer.Ordinal)];

    public void Reload()
    {
        _catalogues.Clear();
        _missingKeys.Clear();
        LoadAll();
    }

    private void LoadAll()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var loaded = LoadFromDisk(language) ?? LoadFromEmbedded(language);
            if (loaded is not null) _catalogues[language] = loaded;
        }

        if (!_catalogues.ContainsKey(_options.FallbackLanguage))
        {
            _logger?.LogError(
                "No catalogue could be loaded for the fallback language {Language}; the interface will show raw keys.",
                _options.FallbackLanguage);
        }
    }

    private static string FileNameFor(AppLanguage language) => language switch
    {
        AppLanguage.Arabic => "ar.json",
        AppLanguage.English => "en.json",
        _ => "fr.json"
    };

    private Dictionary<string, string>? LoadFromDisk(AppLanguage language)
    {
        var path = Path.Combine(_options.ResourceDirectory, FileNameFor(language));
        if (!File.Exists(path)) return null;

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "Could not read the {Language} catalogue at {Path}; using the embedded copy.",
                              language, path);
            return null;
        }
    }

    private Dictionary<string, string>? LoadFromEmbedded(AppLanguage language)
    {
        var assembly = typeof(JsonLocalizer).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Resources.{FileNameFor(language)}", StringComparison.Ordinal));

        if (name is null) return null;

        try
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "The embedded {Language} catalogue is not valid JSON.", language);
            return null;
        }
    }

    /// <summary>
    /// Parses a catalogue. Nested objects are flattened to dotted keys, so the file can be grouped
    /// by screen for a translator while lookups stay flat and allocation-free.
    /// </summary>
    private static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        Flatten(document.RootElement, prefix: null, result);
        return result;
    }

    private static void Flatten(JsonElement element, string? prefix, Dictionary<string, string> into)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var property in element.EnumerateObject())
        {
            // "$comment" entries let a translator leave notes in the file without becoming keys.
            if (property.NameEquals("$comment")) continue;

            var key = prefix is null ? property.Name : $"{prefix}.{property.Name}";

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key, into);
                    break;
                case JsonValueKind.String:
                    into[key] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    into[key] = property.Value.ToString();
                    break;
            }
        }
    }

    private void RecordMissing(string key, AppLanguage language)
    {
        if (!_options.TrackMissingKeys) return;
        _missingKeys.TryAdd($"{language}:{key}", 0);
    }
}
