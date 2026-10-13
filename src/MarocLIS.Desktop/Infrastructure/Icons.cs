using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    // The glyphs are drawn once, at build time, from the Lucide font into one sheet of 64-pixel
    // white masks (Resources/Icons/icons.png), in ascending code-point order, 16 to a row. Loading
    // the font itself through GDI+ worked under test but drew nothing on a real Windows desktop:
    // a memory font is only as reliable as the collection that owns it, and the sheet removes the
    // font machinery from the running application altogether.
    private const int Cell = 64;
    private const int Columns = 16;
    private const int Margin = 3;

    private static readonly Lazy<Bitmap?> Sheet = new(LoadSheet);
    private static readonly Lazy<Dictionary<Glyph, int>> Index = new(() =>
        Enum.GetValues<Glyph>().Distinct().OrderBy(g => (int)g)
            .Select((glyph, i) => (glyph, i)).ToDictionary(p => p.glyph, p => p.i));

    private static readonly ConcurrentDictionary<(Glyph, int, int), Bitmap> Cache = new();
    private static readonly Lock Gate = new();

    /// <summary>The glyph as a bitmap of <paramref name="pixels"/> square, or null if the sheet is unavailable.</summary>
    public static Bitmap? Render(Glyph glyph, Color color, int pixels)
    {
        if (Sheet.Value is not { } sheet || !Index.Value.TryGetValue(glyph, out var index)) return null;

        return Cache.GetOrAdd((glyph, color.ToArgb(), pixels), key =>
        {
            var source = new Rectangle(index % Columns * Cell, index / Columns * Cell, Cell, Cell);

            using var tinted = new Bitmap(Cell, Cell, PixelFormat.Format32bppArgb);

            lock (Gate)
            {
                using var graphics = Graphics.FromImage(tinted);

                // The mask is white; scaling its colour channels to the wanted colour tints it, and
                // its alpha is kept, scaled by the colour's own (a greyed icon is a lighter one).
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix(
                [
                    [color.R / 255f, 0, 0, 0, 0],
                    [0, color.G / 255f, 0, 0, 0],
                    [0, 0, color.B / 255f, 0, 0],
                    [0, 0, 0, color.A / 255f, 0],
                    [0, 0, 0, 0, 1]
                ]));

                graphics.DrawImage(sheet, new Rectangle(0, 0, Cell, Cell),
                                   source.X, source.Y, Cell, Cell, GraphicsUnit.Pixel, attributes);
            }

            // Transparent margin on both sides: a button puts its picture flush against the caption,
            // and the margin is the gap between them whichever side the caption is on (Arabic).
            var bitmap = new Bitmap(key.Item3 + 2 * Margin, key.Item3, PixelFormat.Format32bppArgb);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.DrawImage(tinted, new Rectangle(Margin, 0, key.Item3, key.Item3));
            }

            return bitmap;
        });
    }

    private static Bitmap? LoadSheet()
    {
        try
        {
            using var stream = typeof(Icons).Assembly
                .GetManifestResourceStream("MarocLIS.Desktop.Resources.Icons.icons.png");
            if (stream is null) return null;

            // Copied off the stream: a GDI+ bitmap keeps reading the stream it was created from.
            using var decoded = new Bitmap(stream);
            return new Bitmap(decoded);
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
                    state.Current = byText.TryGetValue(Mnemonics.Plain(button.Text).Trim(), out var found)
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
