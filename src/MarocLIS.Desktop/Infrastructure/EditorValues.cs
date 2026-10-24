using System.Globalization;
using MarocLIS.Application.Billing;
using MarocLIS.Application.Input;
using MarocLIS.Domain.Catalog;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The text conversions behind the editor dialogs' number and age boxes, kept free of WinForms so
/// the test project can compile this file and check them.
/// </summary>
internal static class EditorValues
{
    /// <summary>
    /// The longest amount a money box takes: "999 999 999 999" and its decimals still fit the
    /// decimal(18,2) columns, where a longer figure was refused by the database with an unexpected
    /// error and the form lost with it.
    /// </summary>
    public const int AmountMaxLength = 15;

    /// <summary>The same for the decimal(18,6) columns of the catalogue (limits, volumes, coefficients): twelve figures.</summary>
    public const int MeasureMaxLength = 12;

    /// <summary>
    /// The first magnitude no number box takes: a decimal(18,6) column holds twelve figures before
    /// the comma. The length caps alone let it through in the "1e17" notation, or in a box that
    /// takes fifteen characters, and the save then failed with an unexpected error.
    /// </summary>
    public const decimal Limit = TypedNumber.Limit;

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
    /// <summary>
    /// Reads the discount box of an invoice: an amount in dirhams ("50", "74,70") or a percentage of
    /// the gross ("10%", "10 %", or with the Arabic sign "10٪"). A percentage becomes the amount it
    /// stands for, rounded to the centime, since the invoice records the reduction itself. Empty
    /// means no discount (zero); null is text that is neither, or a percentage outside 0–100. The
    /// reading is the application's (<see cref="DiscountEntry.Parse"/>), shared with the web.
    /// </summary>
    public static decimal? ParseDiscount(string? text, decimal gross) => DiscountEntry.Parse(text, gross);

    /// <summary>True when the discount box holds a percentage rather than an amount (<see cref="DiscountEntry.IsPercent"/>).</summary>
    public static bool IsPercentDiscount(string? text) => DiscountEntry.IsPercent(text);

    /// <summary>The application's reading of a typed number (<see cref="TypedNumber.Parse"/>), shared with the web.</summary>
    public static decimal? ParseDecimal(string? text) => TypedNumber.Parse(text);

    /// <summary>
    /// <see cref="ParseDecimal"/> for the boxes where an empty box means zero (the payroll
    /// variables, the settlement amounts): false only for text that is no number.
    /// </summary>
    public static bool TryParseOrZero(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (ParseDecimal(text) is not { } read) return false;
        value = read;
        return true;
    }

    /// <summary>
    /// True when a box holds text that <see cref="ParseDecimal"/> cannot read — a typing slip, a
    /// unit typed with the figure ("10 %"), or a number too large for any column. The editors read
    /// such a box as "no value"; they check this first so it is refused on its box, not saved as
    /// an empty limit or a zero stock level.
    /// </summary>
    public static bool IsUnreadable(string? text) => TypedNumber.IsUnreadable(text);

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
    public static bool IsDirectionMark(char c) => TypedNumber.IsDirectionMark(c);

    /// <summary>The text with every <see cref="IsDirectionMark"/> character removed.</summary>
    public static string WithoutDirectionMarks(string text) => TypedNumber.WithoutDirectionMarks(text);

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
