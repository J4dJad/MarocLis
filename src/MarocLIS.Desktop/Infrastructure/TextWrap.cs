namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Breaks a caption into lines at its spaces only.
/// <para>
/// Left to Windows, the word-wrap of a label or a column header broke French words after an
/// accented letter on some workstations: « Prélèvement pé | diatrique », « N° Ordre des mé |
/// decins », « Secteurs autorisé | s », « Date limite de ré | ponse ». Lines computed here, each
/// measured to fit, leave Windows nothing to break: a word goes whole to the next line, and a word
/// longer than the whole width stays whole on its own line, to be cut with an ellipsis rather
/// than split.
/// </para>
/// <para>
/// WinForms-free (the measure is passed in) so it can be tested.
/// </para>
/// </summary>
public static class TextWrap
{
    /// <summary>
    /// The lines of <paramref name="text"/> within <paramref name="width"/>, as measured by
    /// <paramref name="measure"/>. Line breaks already in the text are kept.
    /// </summary>
    public static IReadOnlyList<string> Lines(string? text, Func<string, int> measure, int width)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(text)) return [];

        var lines = new List<string>();

        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = words[0];
            foreach (var word in words.Skip(1))
            {
                var candidate = current + " " + word;
                if (measure(candidate) <= width)
                {
                    current = candidate;
                    continue;
                }

                lines.Add(current);
                current = word;
            }

            lines.Add(current);
        }

        return lines;
    }

    /// <summary>The same lines joined with line breaks, for a label that sizes itself to its text.</summary>
    public static string Wrap(string? text, Func<string, int> measure, int width) =>
        string.Join("\n", Lines(text, measure, width));
}
