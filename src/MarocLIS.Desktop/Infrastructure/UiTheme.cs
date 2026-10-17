using MarocLIS.Domain.Catalog;
using MarocLIS.Domain.Orders;
using MarocLIS.Domain.Results;

namespace MarocLIS.Desktop.Infrastructure;

/// <summary>
/// The visual language of the application.
/// <para>
/// Restrained on purpose. A laboratory operator looks at this screen for eight hours and needs to
/// spot one abnormal value in a list of forty; a colourful interface makes that harder, not easier.
/// Colour is reserved for clinical meaning — abnormal, critical, overdue — and everything else is
/// greyscale so that when colour appears it means something.
/// </para>
/// <para>
/// Every clinical state also carries a <b>glyph</b>. Roughly one man in twelve has a colour-vision
/// deficiency, reports get printed in monochrome, and a cue that exists only as a hue is a cue that
/// some users never receive.
/// </para>
/// </summary>
public static partial class UiTheme
{
    // ---- Typography -----------------------------------------------------------------------------

    private const string UiFamily = "Segoe UI";

    /// <summary>
    /// Arabic UI font. Segoe UI covers Arabic on Windows, but a Naskh face is far more readable at
    /// small sizes for long Arabic strings, so it is preferred when installed.
    /// </summary>
    private const string ArabicFamily = "Segoe UI";

    // GDI+ quietly substitutes Microsoft Sans Serif for a family that is not installed, so each
    // preferred face is checked first: Cascadia Mono ships with Windows 11 and recent Terminal
    // installs only, and "Segoe UI Semibold" is missing from some older images.
    private static readonly HashSet<string> Installed = InstalledFamilies();

    private static HashSet<string> InstalledFamilies()
    {
        try
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            return fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsInstalled(string family) => Installed.Contains(family);

    /// <summary>Weight 600 of the type scale: Segoe UI Semibold, or bold where it is missing.</summary>
    private static Font Semibold(float size) =>
        IsInstalled("Segoe UI Semibold") ? new Font("Segoe UI Semibold", size) : new Font(UiFamily, size, FontStyle.Bold);

    /// <summary>body: 13 px.</summary>
    public static Font Body { get; } = new(UiFamily, 9.75f);
    public static Font BodyBold { get; } = Semibold(9.75f);

    /// <summary>
    /// The cells of a grid: 12 px rather than the body's 13. The grids' columns are sized for it,
    /// and at 13 px a 1366-pixel screen lost whole columns ("Volume" on the collection screen).
    /// </summary>
    public static Font GridBody { get; } = new(UiFamily, 9f);

    /// <summary>
    /// Field captions and metadata: 11 px. The type scale's label is 12 px, but the captions sit
    /// at fixed places above their fields, and at 12 px the longer ones ran into the next column
    /// ("N° d'autorisation du ministère de la Santé" over its neighbour's tick box).
    /// </summary>
    public static Font Small { get; } = new(UiFamily, 8.25f);
    public static Font SmallBold { get; } = Semibold(8.25f);

    /// <summary>overline: 11 px, the navigation's group titles, in capitals.</summary>
    public static Font Overline { get; } = Semibold(8.25f);

    /// <summary>heading: 15 px, titles of cards and panels.</summary>
    public static Font SectionTitle { get; } = Semibold(11.25f);

    /// <summary>screen-title: 22 px.</summary>
    public static Font PageTitle { get; } = Semibold(16.5f);

    /// <summary>kpi: 26 px, the figures of the indicators.</summary>
    public static Font Kpi { get; } = Semibold(19.5f);

    /// <summary>
    /// Fixed-pitch, for result values, barcodes and accession numbers.
    /// <para>
    /// Not decoration: a monospaced face keeps digits in vertical alignment down a column, which is
    /// how a technician scanning forty values spots the one with an extra digit. It also prevents
    /// the 1/l and 0/O confusion that a proportional face invites when a value is read aloud.
    /// </para>
    /// <para>
    /// Cascadia Mono when installed, at 9 pt, where its wider advance matches Consolas at 9.5 pt:
    /// columns sized for one fit the other.
    /// </para>
    /// </summary>
    public static Font Monospace { get; } = Mono(FontStyle.Regular);
    public static Font MonospaceBold { get; } = Mono(FontStyle.Bold);

    /// <summary>A monospaced font of the given size, Cascadia Mono or Consolas.</summary>
    public static Font Mono(float consolasSize, FontStyle style = FontStyle.Regular) =>
        IsInstalled("Cascadia Mono")
            ? new Font("Cascadia Mono", consolasSize * 9f / 9.5f, style)
            : new Font("Consolas", consolasSize, style);

    private static Font Mono(FontStyle style) => Mono(9.5f, style);

    public static Font ForLanguage(Domain.Common.AppLanguage language, float size = 9.75f,
                                   FontStyle style = FontStyle.Regular) =>
        new(language == Domain.Common.AppLanguage.Arabic ? ArabicFamily : UiFamily, size, style);

    // ---- Metrics --------------------------------------------------------------------------------

    public const int Gutter = 12;
    public const int GutterTight = 6;

    /// <summary>A single-line grid row: comfortable to scan and to click.</summary>
    public const int RowHeight = 32;
    public const int InputHeight = 26;
    public const int ButtonHeight = 30;
    public const int SidebarWidth = 240;

    /// <summary>radius-md, buttons and fields; radius-lg, cards.</summary>
    public const int RadiusControl = 6;
    public const int RadiusCard = 8;

    // ---- Clinical state → appearance ------------------------------------------------------------

    /// <summary>
    /// Appearance for a result, by interpretation. Returns a glyph alongside the colours so a
    /// caller cannot accidentally convey the state by colour alone.
    /// </summary>
    public static (Color Foreground, Color Background, string Glyph, bool Bold) ForInterpretation(
        ResultInterpretation interpretation) => interpretation switch
    {
        ResultInterpretation.CriticalLow => (Critical, CriticalBackground, "↓↓", true),
        ResultInterpretation.CriticalHigh => (Critical, CriticalBackground, "↑↑", true),
        ResultInterpretation.Low => (Info, InfoBackground, "↓", true),
        ResultInterpretation.High => (Abnormal, AbnormalBackground, "↑", true),
        ResultInterpretation.Abnormal => (Abnormal, AbnormalBackground, "*", true),
        ResultInterpretation.Normal => (TextPrimary, SurfaceRaised, string.Empty, false),
        _ => (TextMuted, SurfaceRaised, string.Empty, false)
    };

    /// <summary>
    /// A short glyph string for the flags carried by a result. Shown in its own narrow column, so
    /// a technician sees at a glance which values need attention and why. The glyphs themselves are
    /// the validation rules' (shared with the phone), so both screens print the same ones.
    /// </summary>
    public static string FlagGlyphs(ResultFlag flags) =>
        MarocLIS.Application.Results.ValidationScreenRules.FlagGlyphs(flags);

    public static Color ForPriority(OrderPriority priority) => priority switch
    {
        OrderPriority.VitalEmergency => Critical,
        OrderPriority.Urgent => Abnormal,
        _ => TextSecondary
    };

    public static Color ForOrderStatus(LabOrderStatus status) => status switch
    {
        LabOrderStatus.Cancelled => TextMuted,
        LabOrderStatus.Reported => Normal,
        LabOrderStatus.Validated => Normal,
        LabOrderStatus.PartiallyValidated => Info,
        _ => Pending
    };

    // ---- Control helpers ------------------------------------------------------------------------

    public static Button PrimaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Height = ButtonHeight,
            Cursor = Cursors.Hand,
            Padding = new Padding(10, 0, 10, 0)
        };

        StylePrimary(button);
        return button;
    }

    /// <summary>
    /// The primary look — accent fill, on-accent caption — for a button made elsewhere. Used by
    /// <see cref="PrimaryButton"/> and for a dialog's accept button.
    /// </summary>
    public static void StylePrimary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.ForeColor = TextOnAccent;
        button.Font = BodyBold;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentPressed;
        ShowPrimaryState(button);

        // Windows paints a disabled flat button's caption in its own dark grey and ignores
        // ForeColor, which on the accent fill left "Prélever" and "Revue du contrôle" unreadable
        // whenever there was nothing selected. A disabled primary button is shown on a pale fill
        // instead, where that grey caption reads and plainly looks unavailable.
        button.EnabledChanged -= OnPrimaryEnabledChanged;
        button.EnabledChanged += OnPrimaryEnabledChanged;
    }

    private static void OnPrimaryEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is Button button) ShowPrimaryState(button);
    }

    private static void ShowPrimaryState(Button button)
    {
        button.BackColor = button.Enabled ? Accent : DisabledFill;
        button.FlatAppearance.BorderColor = button.Enabled ? Accent : Border;
    }

    private static Color DisabledFill => SurfaceSunken;

    public static Button SecondaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Height = ButtonHeight,
            Cursor = Cursors.Hand,
            Padding = new Padding(10, 0, 10, 0)
        };

        StyleSecondary(button);
        return button;
    }

    /// <summary>The secondary look: white, line-strong border, ink caption.</summary>
    public static void StyleSecondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = SurfaceRaised;
        button.ForeColor = TextPrimary;
        button.Font = Body;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = BorderStrong;
        button.FlatAppearance.MouseOverBackColor = SurfaceSunken;
        button.FlatAppearance.MouseDownBackColor = Border;
    }

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Small,
        ForeColor = TextSecondary,
        Margin = new Padding(0, 4, 0, 1)
    };

    public static TextBox Input(int width = 200) => new TextBox
    {
        Width = width,
        Height = InputHeight,
        Font = Body,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SurfaceRaised,
        ForeColor = TextPrimary
    }.WithFieldBorder();

    /// <summary>
    /// A grid configured the way a laboratory list has to behave: read-only, full-row selection,
    /// no add-row, and sized to content. The defaults WinForms ships would let a technician edit a
    /// validated value straight in the grid.
    /// </summary>
    public static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill,
        BackgroundColor = SurfaceRaised,
        BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        EnableHeadersVisualStyles = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Font = GridBody,
        GridColor = Border,
        // Headers wrap onto a second line rather than ending in "…": "Lieu de conservation" cut
        // to "Lieu de c…" is a column nobody can identify, and the grid is the whole screen.
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceSunken,
            ForeColor = TextSecondary,
            Font = SmallBold,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            WrapMode = DataGridViewTriState.True,
            // Taller than its text, as in the design system's grids: the headers read as a band.
            Padding = new Padding(8, 4, 8, 4),
            SelectionBackColor = SurfaceSunken,
            SelectionForeColor = TextSecondary
        },
        DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceRaised,
            ForeColor = TextPrimary,
            SelectionBackColor = AccentSubtle,
            SelectionForeColor = TextPrimary,
            Padding = new Padding(8, 0, 8, 0)
        },
        RowTemplate = { Height = RowHeight }
    };
}
