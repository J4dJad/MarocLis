using System.Globalization;

namespace MarocLIS.Application.Inventory;

/// <summary>
/// One CSV cell, as every purchasing and stock export writes it: quoted when it holds the separator,
/// a quote or a line break, and neutralised when a spreadsheet would read it as a formula.
/// <para>
/// These files open straight in Excel. A patient or supplier name typed as
/// <c>=HYPERLINK("http://…"&amp;B2;"DUPONT")</c> would otherwise become a live formula sending the
/// neighbouring cells away. A value starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a
/// carriage return gets a leading apostrophe, unless it is a plain number such as "-12,50".
/// </para>
/// </summary>
public static class CsvCell
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Of(string? value)
    {
        var text = value ?? string.Empty;
        if (StartsLikeFormula(text)) text = "'" + text;
        return text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    internal static bool StartsLikeFormula(string text) =>
        text.Length > 0
        && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
        && !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
        && !decimal.TryParse(text, NumberStyles.Number, Fr, out _);
}
