using System.Runtime.CompilerServices;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Gives every button, tick box and field label of a screen its own Alt+letter.
/// <para>
/// Windows underlines the letter after an "&amp;" in a caption, shows the underline while Alt is
/// held, and Alt with that letter presses the button or moves to the field after the label. The
/// letters are chosen here rather than written into the translations: there are hundreds of
/// captions in three languages, and two of them on one screen sharing a letter is a mistake no
/// translator can see. Buttons are served first, preferring the first letter of a word, then
/// tick boxes, then the labels of input fields.
/// </para>
/// <para>
/// Applied from <see cref="LocalizedView"/> and <see cref="LocalizedForm"/> after the texts, so a
/// language change gets letters of its own. A caption the screen rewrites later (a status, a
/// count) is simply left without one.
/// </para>
/// </summary>
public static class Mnemonics
{
    // The caption each control had before a letter was marked, and the caption with the mark, so a
    // second pass over an unchanged control starts again from the original.
    private static readonly ConditionalWeakTable<Control, Marked> Applied = new();

    private sealed record Marked(string Original, string WithMark);

    /// <summary>The caption as displayed: marks removed, "&amp;&amp;" back to "&amp;".</summary>
    public static string Plain(string text) =>
        text.Replace("&&", "\0").Replace("&", string.Empty).Replace("\0", "&");

    public static void Apply(Control root)
    {
        var buttons = new List<Control>();
        var ticks = new List<Control>();
        var labels = new List<Control>();

        Collect(root, root, buttons, ticks, labels);

        var used = new HashSet<char>();
        foreach (var control in buttons.Concat(ticks).Concat(labels)) Mark(control, used);
    }

    private static void Collect(Control root, Control parent, List<Control> buttons,
                                List<Control> ticks, List<Control> labels)
    {
        var children = parent.Controls.Cast<Control>().OrderBy(c => c.TabIndex).ToList();

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            // A screen hosted inside another (the shell, a split) assigns its own letters.
            if (child is LocalizedView && child != root) continue;

            switch (child)
            {
                case Button { Tag: not UiTags.NoMnemonic }:
                    buttons.Add(child);
                    break;
                case CheckBox or RadioButton:
                    ticks.Add(child);
                    break;
                case Label label when label.UseMnemonic && LabelsAnInput(children, i):
                    labels.Add(child);
                    break;
            }

            Collect(root, child, buttons, ticks, labels);
        }
    }

    // A label moves the focus to the next control in tab order, so it only gets a letter when
    // that control is a field.
    private static bool LabelsAnInput(List<Control> siblings, int index) =>
        index + 1 < siblings.Count
        && siblings[index + 1] is TextBoxBase or ComboBox or DateTimePicker or NumericUpDown or ListBox
        && siblings[index + 1].TabStop;

    private static void Mark(Control control, HashSet<char> used)
    {
        var text = control.Text;
        if (Applied.TryGetValue(control, out var previous) && previous.WithMark == text)
            text = previous.Original;

        if (string.IsNullOrWhiteSpace(text)) return;

        // A caption that already carries a deliberate mark keeps it.
        var plain = Plain(text);
        if (plain != text.Replace("&&", "&"))
        {
            var given = text.Replace("&&", "\0").IndexOf('&');
            if (given >= 0 && given + 1 < text.Length) used.Add(char.ToUpperInvariant(text[given + 1]));
            return;
        }

        var escaped = text.Replace("&", "&&");
        var position = Pick(plain, used);
        if (position < 0) return;

        used.Add(char.ToUpperInvariant(plain[position]));

        // Position in the escaped caption: every "&" before it was doubled.
        var before = plain[..position].Count(c => c == '&');
        var at = position + before;
        var withMark = escaped[..at] + "&" + escaped[at..];

        control.Text = withMark;
        Applied.AddOrUpdate(control, new Marked(text, withMark));
    }

    private static int Pick(string text, HashSet<char> used)
    {
        // First letters of words, then any letter.
        for (var i = 0; i < text.Length; i++)
        {
            var startsWord = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            if (startsWord && Free(text[i], used)) return i;
        }

        for (var i = 0; i < text.Length; i++)
            if (Free(text[i], used)) return i;

        return -1;
    }

    private static bool Free(char c, HashSet<char> used) =>
        char.IsLetterOrDigit(c) && !used.Contains(char.ToUpperInvariant(c));
}
