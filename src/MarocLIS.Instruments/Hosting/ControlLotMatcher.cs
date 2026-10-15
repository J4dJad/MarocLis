using System.Text.RegularExpressions;
using MarocLIS.Domain.Quality;

namespace MarocLIS.Instruments.Hosting;

/// <summary>
/// Reads a control material's lot and level out of what an analyser sends for a control specimen:
/// a sample identifier such as "QC-LOT-882", an ASTM specimen descriptor such as "CONTROL-NORMAL",
/// or an HL7 specimen source. Only POCT1-A sends the bare lot number.
/// </summary>
public static class ControlLotMatcher
{
    /// <summary>
    /// True when <paramref name="lotNumber"/> stands in <paramref name="sent"/> as a whole token,
    /// ignoring case: "882" in "QC-LOT-882" or "LOT882", not in "8821". A digit next to a digit,
    /// or a letter next to a letter, continues the token.
    /// </summary>
    public static bool Names(string sent, string lotNumber)
    {
        var lot = lotNumber.Trim();
        if (lot.Length == 0 || string.IsNullOrWhiteSpace(sent)) return false;

        for (var at = sent.IndexOf(lot, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = sent.IndexOf(lot, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + lot.Length;
            if ((at == 0 || Separates(sent[at - 1], lot[0]))
                && (end == sent.Length || Separates(lot[^1], sent[end])))
                return true;
        }

        return false;
    }

    /// <summary>The control level a word in <paramref name="sent"/> names (LOW, NORMAL, HIGH), if any.</summary>
    public static QcLevel? LevelNamed(string sent)
    {
        foreach (var word in Regex.Split(sent.ToUpperInvariant(), @"[^\p{L}\p{Nd}]+"))
        {
            switch (word)
            {
                case "LOW" or "BAS" or "BASSE": return QcLevel.Low;
                case "NORMAL" or "NORMALE" or "NORM": return QcLevel.Normal;
                case "HIGH" or "HAUT" or "HAUTE" or "ELEVE": return QcLevel.High;
            }
        }

        return null;
    }

    private static bool Separates(char left, char right) =>
        !(char.IsDigit(left) && char.IsDigit(right)) && !(char.IsLetter(left) && char.IsLetter(right));
}
