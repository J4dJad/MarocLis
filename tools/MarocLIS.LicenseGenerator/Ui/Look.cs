using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MarocLIS.Desktop.Infrastructure;

namespace MarocLIS.LicenseGenerator.Ui;

/// <summary>
/// The generator's look: MarocLIS's colours (the palette is shared source) and its icons (the same
/// sheet of Lucide glyphs), with the few control builders the screens need.
/// </summary>
internal static class Look
{
    public const int ButtonHeight = 32;

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Bold = new("Segoe UI", 9.75f, FontStyle.Bold);
    public static readonly Font Heading = new("Segoe UI", 13f, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Mono = new("Consolas", 10.5f);

    public static Button Primary(string text, Glyph glyph) => Styled(text, glyph, primary: true);
    public static Button Secondary(string text, Glyph glyph) => Styled(text, glyph, primary: false);

    private static Button Styled(string text, Glyph glyph, bool primary)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Height = ButtonHeight,
            MinimumSize = new Size(0, ButtonHeight),
            Padding = new Padding(8, 0, 10, 0),
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Font = primary ? Bold : Body,
            BackColor = primary ? UiTheme.Accent : UiTheme.SurfaceRaised,
            ForeColor = primary ? UiTheme.TextOnAccent : UiTheme.TextPrimary,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleLeft
        };
        button.FlatAppearance.BorderColor = primary ? UiTheme.Accent : UiTheme.BorderStrong;
        button.FlatAppearance.MouseOverBackColor = primary ? UiTheme.AccentHover : UiTheme.SurfaceSunken;

        button.Image = Glyphs.Render(glyph, button.ForeColor, 16);
        button.EnabledChanged += (_, _) =>
            button.Image = Glyphs.Render(glyph, button.Enabled ? button.ForeColor : UiTheme.TextMuted, 16);
        return button;
    }

    public static FlowLayoutPanel Bar(params Control[] controls)
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 10),
            BackColor = Color.Transparent
        };
        bar.Controls.AddRange(controls);
        return bar;
    }

    public static Label Text(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? UiTheme.TextPrimary,
        Margin = new Padding(0, 0, 0, 6)
    };

    public static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = UiTheme.SurfaceRaised,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = UiTheme.Border,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 32,
            Font = Body
        };
        grid.RowTemplate.Height = 28;
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceSunken;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = Bold;
        grid.DefaultCellStyle.SelectionBackColor = UiTheme.AccentSubtle;
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.AccentInk;
        return grid;
    }

    public static void AddColumn(DataGridView grid, string name, string header, float weight, bool right = false)
    {
        var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight };
        if (right) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.Columns.Add(column);
    }
}

/// <summary>MarocLIS's icon sheet, read the way the application reads it.</summary>
internal static class Glyphs
{
    // Same layout as MarocLIS's Icons: 64-pixel white masks, in ascending code-point order, 16 a row.
    private const int Cell = 64;
    private const int Columns = 16;

    private static readonly Lazy<Bitmap?> Sheet = new(() =>
    {
        using var stream = typeof(Glyphs).Assembly.GetManifestResourceStream("MarocLIS.LicenseGenerator.icons.png");
        if (stream is null) return null;
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    });

    private static readonly Lazy<Dictionary<Glyph, int>> Index = new(() =>
        Enum.GetValues<Glyph>().Distinct().OrderBy(g => (int)g)
            .Select((glyph, i) => (glyph, i)).ToDictionary(p => p.glyph, p => p.i));

    public static Bitmap? Render(Glyph glyph, Color color, int pixels)
    {
        if (Sheet.Value is not { } sheet || !Index.Value.TryGetValue(glyph, out var index)) return null;

        var bitmap = new Bitmap(pixels + 4, pixels, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // The mask is white: scaling its channels to the colour tints it.
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(
        [
            [color.R / 255f, 0, 0, 0, 0],
            [0, color.G / 255f, 0, 0, 0],
            [0, 0, color.B / 255f, 0, 0],
            [0, 0, 0, color.A / 255f, 0],
            [0, 0, 0, 0, 1]
        ]));
        graphics.DrawImage(sheet, new Rectangle(0, 0, pixels, pixels),
                           index % Columns * Cell, index / Columns * Cell, Cell, Cell, GraphicsUnit.Pixel, attributes);
        return bitmap;
    }
}
