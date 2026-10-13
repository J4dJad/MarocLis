using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarocLIS.Localization;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// Draws the bundled icon font into small bitmaps for buttons and the navigation column.
/// <para>
/// A font rather than a folder of pictures: one glyph renders sharp at any size and in any colour,
/// so the same icon serves a white-on-blue primary button, a dark secondary one, a greyed disabled
/// one and a 150 % display without a set of PNGs per combination.
/// </para>
/// </summary>
public static class Icons
{
    private static readonly Lazy<FontFamily?> Family = new(LoadFamily);
    private static readonly ConcurrentDictionary<(Glyph, int, int), Bitmap> Cache = new();

    // Kept for the life of the process: GDI+ reads the font from this memory, not from a copy.
    private static IntPtr _fontMemory;

    private const int Margin = 3;

    /// <summary>The glyph as a bitmap of <paramref name="pixels"/> square, or null if the font is unavailable.</summary>
    public static Bitmap? Render(Glyph glyph, Color color, int pixels)
    {
        if (Family.Value is not { } family) return null;

        return Cache.GetOrAdd((glyph, color.ToArgb(), pixels), key =>
        {
            // Transparent margin on both sides: a button puts its picture flush against the caption,
            // and the margin is the gap between them whichever side the caption is on (Arabic).
            var bitmap = new Bitmap(key.Item3 + 2 * Margin, key.Item3);

            using var graphics = Graphics.FromImage(bitmap);
            using var font = new Font(family, key.Item3 * 0.92f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(key.Item2));
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            graphics.DrawString(((char)(int)key.Item1).ToString(), font, brush,
                                new RectangleF(Margin, 0, key.Item3, key.Item3), format);

            return bitmap;
        });
    }

    private static FontFamily? LoadFamily()
    {
        try
        {
            using var stream = typeof(Icons).Assembly
                .GetManifestResourceStream("MarocLIS.Desktop.Resources.Icons.lucide.ttf");
            if (stream is null) return null;

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);

            _fontMemory = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, _fontMemory, bytes.Length);

            var collection = new PrivateFontCollection();
            collection.AddMemoryFont(_fontMemory, bytes.Length);

            return collection.Families.FirstOrDefault();
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException)
        {
            // Without icons the buttons still carry their captions; nothing depends on the picture.
            return null;
        }
    }
}

/// <summary>
/// Puts the icon matching each button's caption on the button.
/// <para>
/// The caption is matched against the localised text of the keys in <see cref="IconCatalog"/>, in
/// the current language, so this runs after the texts are applied and again when the language or a
/// caption changes. A button that already carries a picture of its own is left alone.
/// </para>
/// </summary>
public static class ButtonIcons
{
    private sealed class State
    {
        public Glyph? Explicit;
        public Glyph? Current;
        public bool Always;
    }

    private static readonly ConditionalWeakTable<Button, State> Decorated = new();

    public static int Size(Control control) => (int)Math.Round(16 * control.DeviceDpi / 96.0);

    public static void Apply(Control root, ILocalizer localizer)
    {
        var byText = new Dictionary<string, Glyph>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var (key, glyph) in IconCatalog.Actions)
        {
            var text = localizer[key];
            if (!string.IsNullOrWhiteSpace(text) && text != key) byText.TryAdd(text.Trim(), glyph);
        }

        Walk(root, byText);
    }

    /// <summary>Sets a button's icon explicitly, for captions that are not a plain resource text.</summary>
    public static void Set(Button button, Glyph glyph, bool always = false)
    {
        var state = Decorated.GetValue(button, Track);
        state.Explicit = state.Current = glyph;
        state.Always = always;
        Paint(button, glyph);
    }

    private static void Walk(Control parent, Dictionary<string, Glyph> byText)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button button)
            {
                if (Decorated.TryGetValue(button, out _) || button.Image is null)
                {
                    var state = Decorated.GetValue(button, Track);

                    // An explicitly set icon stands for a caption the catalogue does not know.
                    state.Current = byText.TryGetValue(button.Text.Trim(), out var found)
                        ? found
                        : state.Explicit;

                    Paint(button, state.Current);
                }
            }

            Walk(child, byText);
        }
    }

    private static State Track(Button button)
    {
        button.EnabledChanged += (_, _) => Repaint(button);
        button.BackColorChanged += (_, _) => Repaint(button);
        button.ForeColorChanged += (_, _) => Repaint(button);
        return new State();
    }

    private static void Repaint(Button button)
    {
        if (Decorated.TryGetValue(button, out var state)) Paint(button, state.Current);
    }

    private static void Paint(Button button, Glyph? glyph)
    {
        if (glyph is null)
        {
            if (button.Image is not null && Decorated.TryGetValue(button, out _)) button.Image = null;
            return;
        }

        // A button sized for its caption alone keeps its caption: a cut-off word is worse than a
        // missing picture. Strips that reflow widen their buttons to fit both.
        var size = Size(button);
        var needed = TextRenderer.MeasureText(button.Text, button.Font).Width
                     + size + 6 + button.Padding.Horizontal + 8;

        if (!button.AutoSize && needed > button.Width && !BarReflow.Manages(button)
            && !(Decorated.TryGetValue(button, out var state) && state.Always))
        {
            if (button.Image is not null) button.Image = null;
            return;
        }

        var color = !button.Enabled ? UiTheme.TextMuted : button.ForeColor;

        button.Image = Icons.Render(glyph.Value, color, size);
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.ImageAlign = button.TextAlign;
    }
}
