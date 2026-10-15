using System.Globalization;
using MarocLIS.Domain.Catalog;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The text conversions behind the editor dialogs' number and age boxes, kept free of WinForms so
/// the test project can compile this file and check them.
/// </summary>
internal static class EditorValues
{
    /// <summary>
    /// Reads a number typed, pasted or pre-filled in any of the cultures the application runs in.
    /// <para>
    /// The cultures write "150 000,5" with a space (or a no-break space, which the framework uses
    /// for French) between thousands. Read with the invariant culture that was null, and a dialog
    /// saving it erased the reference limit it had just displayed. Spaces are dropped; a comma or a
    /// point is the decimal separator; when both appear, the last one is, and the other groups
    /// thousands; a separator that repeats ("1.500.000") can only be grouping.
    /// </para>
    /// </summary>
    public static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // The direction marks go with the spaces: the Arabic culture writes a negative number as
        // "\u200E-2,5" (a left-to-right mark before the sign), and a box pre-filled with it read
        // back as null, so saving an editor erased every negative bound it had displayed.
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not ' '
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

        return decimal.TryParse(compact.Replace(',', '.'), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Writes a value for an editable box: at least <paramref name="decimals"/> decimals, any
    /// further significant ones kept so opening and saving does not round the stored value, and no
    /// thousands separator.
    /// </summary>
    public static string FormatForEditing(decimal value, int decimals, IFormatProvider? provider = null)
    {
        var fixedPart = new string('0', Math.Max(0, decimals));
        var format = "0." + fixedPart + "##########";

        // Without the direction mark the Arabic culture puts before the minus sign: the boxes are
        // laid out left to right, and the text must read back as the value it shows.
        return WithoutDirectionMarks(value.ToString(format, provider ?? CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// The invisible Unicode characters that only steer the display direction — LRM, RLM, the
    /// Arabic letter mark, the embeddings and overrides, and the isolates — which a culture's
    /// number format or a copy from an Arabic text can carry into a number box.
    /// </summary>
    public static bool IsDirectionMark(char c) =>
        c is '\u200E' or '\u200F' or '\u061C'
            or (>= '\u202A' and <= '\u202E')
            or (>= '\u2066' and <= '\u2069');

    /// <summary>The text with every <see cref="IsDirectionMark"/> character removed.</summary>
    public static string WithoutDirectionMarks(string text) =>
        text.Any(IsDirectionMark) ? new string(text.Where(c => !IsDirectionMark(c)).ToArray()) : text;

    /// <summary>
    /// The unit, in days, to show a stored age band in: the largest of years (365 days) and months
    /// (30 days), the factors the editor multiplies by when saving, that holds both bounds exactly,
    /// otherwise days. "1 to 3 years" is 365 to 1095 days and must reopen as 1 to 3 years, not as
    /// 12 to 36 months that save back as 360 to 1080 days.
    /// <para>
    /// The rule is the domain's own: interval matching reads the bounds in the same unit — whole
    /// years and months on the calendar — so the band the editor shows is the band that applies.
    /// </para>
    /// </summary>
    public static int AgeUnitDays(int? fromDays, int? toDays) =>
        ReferenceRange.AgeBandUnitDays(fromDays, toDays);
}
