using System.Globalization;
using System.Text.RegularExpressions;
using MarocLIS.Api.Contracts;

namespace MarocLIS.Mobile.Services;

/// <summary>How the screens show what the API sends: the indicator, the colour of a value, the patient line.</summary>
public static partial class Display
{
    /// <summary>
    /// The arrows of the workstation's indicator column (↑ ↓ ↑↑ ↓↓), drawn as ▲ ▼ beside the value
    /// as on the approved screens. The other glyphs of that column (CIQ, Δ, R…) are said in words by
    /// the row's pills instead.
    /// </summary>
    public static string Arrows(string? indicator)
    {
        if (string.IsNullOrEmpty(indicator)) return string.Empty;

        var up = indicator.Count(c => c == '↑');
        var down = indicator.Count(c => c == '↓');
        return up > 0 ? new string('▲', Math.Min(up, 2)) : down > 0 ? new string('▼', Math.Min(down, 2)) : string.Empty;
    }

    /// <summary>The colour class of a value, by its interpretation; a value not yet releasable is muted.</summary>
    public static string ValueClass(ResultRow row)
    {
        if (row.Status == "Entered") return "v-muted";

        return row.Interpretation switch
        {
            "CriticalHigh" or "CriticalLow" => "v-critical",
            "High" or "Abnormal" => "v-high",
            "Low" => "v-low",
            _ => row.IsCritical ? "v-critical" : row.IsAbnormal ? "v-high" : "v-normal"
        };
    }

    public static bool IsUncalledCritical(ResultRow row) => row.IsCritical && !row.CriticalCommunicated;

    /// <summary>
    /// Whether the row's box can be ticked: what the server allows, less a critical value nobody has
    /// been told about yet. The server would refuse that one anyway (Result_CriticalMustBeCommunicated);
    /// greying it out says why before « Valider » rather than after, as the banner above asks.
    /// </summary>
    public static bool CanTick(ResultRow row) => !row.IsHeading && row.CanTick && !IsUncalledCritical(row);

    public static bool HasFlag(ResultRow row, string flag) => row.Flags.Contains(flag, StringComparer.Ordinal);

    /// <summary>Waiting for the technician: entered, not technically validated.</summary>
    public static bool AwaitsTechnicalValidation(ResultRow row) => row.Status == "Entered";

    /// <summary>« F, 64 ans »: the sex letter of the workstation, then the age.</summary>
    public static string SexAndAge(Texts texts, string gender, int? ageYears)
    {
        var sex = gender switch
        {
            "Female" => texts.T("GenderShort.Female"),
            "Male" => texts.T("GenderShort.Male"),
            _ => string.Empty
        };
        var age = ageYears is { } years ? texts.T("OrderFile.AgeYears", years) : string.Empty;
        // The Arabic comma in Arabic: a Latin one reads as a stray mark there.
        return string.Join(texts.IsRightToLeft ? "، " : ", ", new[] { sex, age }.Where(s => s.Length > 0));
    }

    /// <summary>« KB » for « Dr Karim Bennani »: the title dropped, first and last names' initials.</summary>
    public static string Initials(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "?";

        var words = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.TrimEnd('.').Equals("Dr", StringComparison.OrdinalIgnoreCase)
                        && !w.TrimEnd('.').Equals("Pr", StringComparison.OrdinalIgnoreCase)
                        && !w.StartsWith('('))
            .ToList();

        if (words.Count == 0) return "?";
        var first = char.ToUpperInvariant(words[0][0]);
        return words.Count == 1 ? first.ToString() : $"{first}{char.ToUpperInvariant(words[^1][0])}";
    }

    /// <summary>The priority as a badge: only what is not routine is worth one.</summary>
    public static bool IsUrgent(string priority) => priority is "Urgent" or "VitalEmergency";

    /// <summary>The catalogue key of a priority (Priority.Urgent…).</summary>
    public static string PriorityKey(string priority) => priority switch
    {
        "VitalEmergency" => "Priority.VitalEmergency",
        "Urgent" => "Priority.Urgent",
        _ => "Priority.Routine"
    };

    /// <summary>The tone class of a pill from the API (ApiProtocol.Tones).</summary>
    public static string Tone(string tone) => tone switch
    {
        ApiProtocol.Tones.Critical => "critical",
        ApiProtocol.Tones.Warning => "warning",
        ApiProtocol.Tones.Ok => "ok",
        ApiProtocol.Tones.Waiting => "waiting",
        _ => "info"
    };

    [GeneratedRegex(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    /// <summary>
    /// The limits of a reference range as the server writes it (« 3,5 – 5,0 », « &lt; 6 », « &gt; 40 »),
    /// for the band of the antériorité chart; null where the text says nothing usable.
    /// </summary>
    public static (decimal? Low, decimal? High) ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return (null, null);

        var numbers = NumberPattern().Matches(range)
            .Select(m => decimal.TryParse(m.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToList();

        var text = range.TrimStart();
        if (text.StartsWith('<') || text.StartsWith('≤')) return (null, numbers.FirstOrDefault());
        if (text.StartsWith('>') || text.StartsWith('≥')) return (numbers.FirstOrDefault(), null);
        return numbers.Count >= 2 ? (numbers[0], numbers[1]) : (null, null);
    }

    /// <summary>A number as the server writes values: decimal comma, no trailing zeros beyond the value's own.</summary>
    public static string Number(decimal value, char separator = ',') =>
        value.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', separator);

    /// <summary>The decimal separator the server used in a value (« 6,9 » or « 6.9 »).</summary>
    public static char SeparatorOf(string? sample) => sample is not null && sample.Contains('.') && !sample.Contains(',') ? '.' : ',';

    /// <summary>The value of a row as a number, when it is one.</summary>
    public static decimal? Numeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = NumberPattern().Match(value);
        return match.Success && decimal.TryParse(match.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }
}
