using MarocLIS.Domain.Common;

namespace MarocLIS.Localization;

/// <summary>
/// Keeps a Latin or numeric value readable inside Arabic text.
/// <para>
/// The Unicode bidirectional algorithm lays out a run of left-to-right and neutral characters
/// relative to the direction of the paragraph around it. In a right-to-left paragraph that means
/// <c>5 mL</c> renders as <c>mL 5</c>, <c>120/80</c> as <c>80/120</c>, and a value's trailing
/// punctuation jumps to its front. Two of those three are wrong in a way that matters clinically: a
/// reversed blood pressure is a different blood pressure, and a reversed barcode is a different
/// tube.
/// </para>
/// <para>
/// The remedy is a first-strong isolate — U+2066 LRI … U+2069 PDI — which asks the algorithm to lay
/// the enclosed run out independently and to treat it as one neutral object in the surrounding
/// paragraph. An isolate rather than the older LRM mark or an embedding (U+202A): a mark has to be
/// placed at both ends and reasoned about per string, and an embedding has undefined behaviour when
/// left unterminated, which is what happens the first time a value is truncated for display.
/// </para>
/// <para>
/// It lives in this project, not in the desktop client, because it is a text-direction concern of
/// the same kind as the culture and the catalogues — and because the one place worth applying it is
/// <see cref="ILocalizer.Format"/>, which is the boundary where a Latin value enters a translated
/// sentence.
/// </para>
/// </summary>
public static class BidiText
{
    /// <summary>LEFT-TO-RIGHT ISOLATE.</summary>
    public const char Lri = '⁦';

    /// <summary>POP DIRECTIONAL ISOLATE.</summary>
    public const char Pdi = '⁩';

    /// <summary>
    /// Whether a string would be re-ordered by a right-to-left paragraph.
    /// <para>
    /// True when it carries at least one strongly left-to-right character — a Latin letter or a
    /// digit — and no right-to-left character of its own. A string that already contains Arabic is
    /// left alone: its own strong characters set the run's direction, and isolating it would fight
    /// the layout the reader expects.
    /// </para>
    /// </summary>
    public static bool NeedsIsolation(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        // Already isolated, by this method or by a caller that did it deliberately.
        if (value[0] == Lri) return false;

        var hasLatin = false;

        foreach (var character in value)
        {
            if (IsRightToLeft(character)) return false;

            if (!hasLatin && (char.IsAsciiLetter(character) || char.IsAsciiDigit(character)))
                hasLatin = true;
        }

        // A string of pure punctuation or whitespace has no direction to protect.
        return hasLatin;
    }

    public static string Isolate(string value) => $"{Lri}{value}{Pdi}";

    /// <summary>Isolates when it is needed and the language is right-to-left; otherwise passes through.</summary>
    public static string ForLanguage(string? value, AppLanguage language) =>
        value is null
            ? string.Empty
            : language.IsRightToLeft() && NeedsIsolation(value) ? Isolate(value) : value;

    /// <summary>
    /// The Unicode ranges carrying a strong right-to-left direction that this application can
    /// actually encounter: Arabic and Hebrew with their presentation blocks. Checked by range rather
    /// than through <see cref="System.Globalization.CharUnicodeInfo"/> because the question is asked
    /// once per character of every visible string.
    /// </summary>
    private static bool IsRightToLeft(char character) =>
        character is >= '֐' and <= 'ࣿ'      // Hebrew, Arabic, Syriac, Thaana, N'Ko…
                  or >= 'יִ' and <= '﷿'      // Hebrew and Arabic presentation forms A
                  or >= 'ﹰ' and <= '﻿';     // Arabic presentation forms B
}
