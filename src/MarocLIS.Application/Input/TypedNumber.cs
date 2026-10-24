using System.Globalization;

namespace MarocLIS.Application.Input;

/// <summary>
/// Reads a number as an operator types it, in any of the cultures the application runs in. The
/// workstation's editors and the web's forms read a box the same way, so a value that one client
/// accepts the other does not refuse, and neither saves a typing slip as "no value".
/// </summary>
public static class TypedNumber
{
    /// <summary>
    /// The first magnitude no number box takes: a decimal(18,6) column holds twelve figures before
    /// the comma. The length caps alone let it through in the "1e17" notation, or in a box that
    /// takes fifteen characters, and the save then failed with an unexpected error.
    /// </summary>
    public const decimal Limit = 1_000_000_000_000m;

    /// <summary>
    /// Reads a number typed, pasted or pre-filled in any of the cultures the application runs in.
    /// <para>
    /// The cultures write "150 000,5" with a space (or a no-break space, which the framework uses
    /// for French) between thousands. Read with the invariant culture that was null, and a dialog
    /// saving it erased the reference limit it had just displayed. Spaces are dropped; a comma or a
    /// point is the decimal separator; when both appear, the last one is, and the other groups
    /// thousands; a separator that repeats ("1.500.000") can only be grouping.
    /// </para>
    /// <para>A number of <see cref="Limit"/> or more reads as null, like any text that is no number.</para>
    /// <para>
    /// So does the exponent notation ("1e5", "2E-3"): nobody at the lab writes an amount or a
    /// result that way, and read as a number it turned a slip in a box ("1e5" for "105") into a
    /// hundred thousand without a word.
    /// </para>
    /// </summary>
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // The direction marks go with the spaces: the Arabic culture writes a negative number as
        // "‎-2,5" (a left-to-right mark before the sign), and a box pre-filled with it read
        // back as null, so saving an editor erased every negative bound it had displayed.
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not ' '
                                                 && !IsDirectionMark(c)).ToArray());

        var lastComma = compact.LastIndexOf(',');
        var lastPoint = compact.LastIndexOf('.');

        if (lastComma >= 0 && lastPoint >= 0)
        {
            var group = lastComma > lastPoint ? "." : ",";
            compact = compact.Replace(group, string.Empty, StringComparison.Ordinal);
        }

        foreach (var separator in new[] { ",", "." })
        {
            if (compact.IndexOf(separator, StringComparison.Ordinal)
                != compact.LastIndexOf(separator, StringComparison.Ordinal))
                compact = compact.Replace(separator, string.Empty, StringComparison.Ordinal);
        }

        return decimal.TryParse(compact.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture, out var value)
               && Math.Abs(value) < Limit
            ? value
            : null;
    }

    /// <summary>
    /// True when a box holds text that <see cref="Parse"/> cannot read — a typing slip, a unit typed
    /// with the figure ("10 %"), or a number too large for any column. Such a box is refused on its
    /// field rather than saved as an empty value.
    /// </summary>
    public static bool IsUnreadable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Parse(text) is null;

    /// <summary>
    /// The invisible Unicode characters that only steer the display direction — LRM, RLM, the
    /// Arabic letter mark, the embeddings and overrides, and the isolates — which a culture's
    /// number format or a copy from an Arabic text can carry into a number box.
    /// </summary>
    public static bool IsDirectionMark(char c) =>
        c is '‎' or '‏' or '؜'
            or (>= '‪' and <= '‮')
            or (>= '⁦' and <= '⁩');

    /// <summary>The text with every <see cref="IsDirectionMark"/> character removed.</summary>
    public static string WithoutDirectionMarks(string text) =>
        text.Any(IsDirectionMark) ? new string(text.Where(c => !IsDirectionMark(c)).ToArray()) : text;
}
