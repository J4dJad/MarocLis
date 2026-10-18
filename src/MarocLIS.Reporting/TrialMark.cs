using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarocLIS.Reporting;

/// <summary>
/// « VERSION D'ESSAI – NE PAS REMETTRE AU PATIENT », on every page of a report or an invoice
/// printed under a trial or a training licence.
/// <para>
/// In the page's foreground, over whatever the page holds, so it cannot be covered by a long
/// table or cropped by a printer's margins: once across the page and once in a band at the top. A
/// document printed while the laboratory was evaluating MarocLIS must never pass for one it can
/// hand over.
/// </para>
/// </summary>
internal static class TrialMark
{
    /// <summary>The resource of the mark, in the document's language.</summary>
    public const string ResourceKey = "Licence.TrialMark";

    public const string French = "VERSION D'ESSAI – NE PAS REMETTRE AU PATIENT";

    // Red, translucent enough to read the values under it, opaque enough to survive a photocopy.
    private const string Diagonal = "#59C62828";
    private const string Band = "#C62828";

    public static void Apply(PageDescriptor page, string text)
    {
        page.Foreground().Layers(layers =>
        {
            // QuestPDF turns the text about its top-left corner, which lifts the line's middle up and
            // to the left of the page's centre; it is moved back by about as much, from its length.
            var length = text.Length * 12.4f;
            layers.PrimaryLayer().AlignCenter().AlignMiddle()
                  .TranslateX(length * 0.065f).TranslateY(length * 0.23f)
                  .Rotate(-35)
                  .Text(text).FontSize(22).Bold().FontColor(Diagonal);

            layers.Layer().AlignTop()
                  .Border(1).BorderColor(Band).Background(Colors.White)
                  .PaddingVertical(2).AlignCenter()
                  .Text(text).FontSize(9).Bold().FontColor(Band);
        });
    }
}
